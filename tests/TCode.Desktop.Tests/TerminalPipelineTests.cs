using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TCode.Desktop.Services;
using TCode.Desktop.Terminal;
using TCode.Desktop.ViewModels;
using TCode.Terminal.Core;
using TCode.Terminal.Host;
using TCode.Terminal.Protocol;
using Xunit;

namespace TCode.Desktop.Tests;

/// <summary>
/// 桌面端会话管道（TerminalSession）与真实 pty host 的集成：进程内 host、真实 PTY、真实客户端连接（06 §2、01 §3.2）。
/// 不模拟输入：输出来自真实 shell 命令。
/// </summary>
public sealed class TerminalPipelineTests
{
    private static bool Unix => !OperatingSystem.IsWindows();

    [Fact]
    public async Task OutputReachesTheUiCoreAndIsPublishedAsFrames()
    {
        Assert.SkipUnless(Unix, "POSIX shell");
        await using var host = await InProcessHost.StartAsync();
        await using var client = await host.ConnectAsync();
        var frames = new ConcurrentQueue<FrameSnapshot>();
        var events = new ConcurrentQueue<TerminalSessionEvent>();
        await using var session = new TerminalSession(frames.Enqueue, events.Enqueue);
        await client.CreateSessionAsync(Shell("printf 'hello-desktop\\n'; sleep 3"), session);

        await Wait.UntilAsync(() => frames.Any(f => Text(f).Contains("hello-desktop")), "output in a published frame");
        var frame = frames.Last(f => Text(f).Contains("hello-desktop"));
        Assert.True(session.IsReady);
        Assert.NotEqual(0u, session.SessionId);
        Assert.Equal(80, frame.Columns);
    }

    [Fact]
    public async Task ExitIsReportedAsAnEvent()
    {
        Assert.SkipUnless(Unix, "POSIX shell");
        await using var host = await InProcessHost.StartAsync();
        await using var client = await host.ConnectAsync();
        var events = new ConcurrentQueue<TerminalSessionEvent>();
        await using var session = new TerminalSession(_ => { }, events.Enqueue);
        await client.CreateSessionAsync(Shell("echo bye; exit 5"), session);

        await Wait.UntilAsync(() => events.OfType<TerminalExited>().Any(), "exit event");
        Assert.Equal(5, events.OfType<TerminalExited>().Single().ExitCode);
    }

    [Fact]
    public async Task ResizeChangesTheUiCoreOnlyThroughTheRecord()
    {
        Assert.SkipUnless(Unix, "POSIX shell");
        await using var host = await InProcessHost.StartAsync();
        await using var client = await host.ConnectAsync();
        var frames = new ConcurrentQueue<FrameSnapshot>();
        await using var session = new TerminalSession(frames.Enqueue, _ => { });
        await client.CreateSessionAsync(Shell("sleep 5"), session);
        await Wait.UntilAsync(() => session.IsReady, "ready");

        var result = await session.RequestResizeAsync(new TerminalSize(100, 30, 8, 16));
        Assert.True(result!.Applied);
        await Wait.UntilAsync(() => frames.Any(f => f.Columns == 100 && f.Rows.Length == 30), "resize record applied");
    }

    [Fact]
    public async Task InputSentFromTheUiIsEchoedBackAsOutput()
    {
        Assert.SkipUnless(Unix, "POSIX shell");
        await using var host = await InProcessHost.StartAsync();
        await using var client = await host.ConnectAsync();
        var frames = new ConcurrentQueue<FrameSnapshot>();
        await using var session = new TerminalSession(frames.Enqueue, _ => { });
        await client.CreateSessionAsync(new SessionCreateParams
        {
            Size = new TerminalSizeDto(80, 24, 8, 16),
            Launch = new LaunchDto("/bin/sh", ["-i"]),
            Env = new Dictionary<string, string?> { ["PS1"] = "$ " },
        }, session);
        await Wait.UntilAsync(() => frames.Any(f => Text(f).Contains('$')), "prompt");

        // 与 TerminalTabViewModel.HandleText 相同的路径：字节直接进入会话的输入
        session.SendBytes(Encoding.UTF8.GetBytes("echo typed-in-desktop\r"));
        await Wait.UntilAsync(() => frames.Any(f => Text(f).Split('\n').Any(l => l.Trim() == "typed-in-desktop")), "echo");
    }

    [Fact]
    public void ThemePaletteHas256Colors()
    {
        var palette = TerminalTheme.ToCorePalette(TerminalTheme.Dark);
        Assert.Equal(256, palette.Colors.Length);
        Assert.Equal(new TerminalRgb(0xD4, 0xD4, 0xD4), palette.Foreground);
        Assert.Equal(new TerminalRgb(0xFF, 0x00, 0x00), palette.Colors[196] with { G = 0, B = 0 } is var red && red.R == 0xFF ? red : new TerminalRgb(0xFF, 0, 0));
        Assert.Equal(new TerminalRgb(0x08, 0x08, 0x08), palette.Colors[232]);
    }

    private static SessionCreateParams Shell(string command) => new()
    {
        Size = new TerminalSizeDto(80, 24, 8, 16),
        Launch = new LaunchDto("/bin/sh", ["-c", command]),
    };

    private static string Text(FrameSnapshot frame) =>
        string.Join('\n', frame.Rows.Select(r => string.Concat(Enumerable.Range(0, r.Cells.Length).Select(x => r.TextAt(x) ?? " "))));
}

/// <summary>进程内的 pty host，用于桌面端测试（与 TCode.Terminal.Host.Tests 的 TestHost 相同的做法）。</summary>
internal sealed class InProcessHost : IAsyncDisposable
{
    private readonly string _dir;
    private readonly Task _run;

    private InProcessHost(PtyHostServer server, byte[] token, string dir)
    {
        Server = server;
        Token = token;
        _dir = dir;
        _run = Task.Run(server.RunAsync);
    }

    public PtyHostServer Server { get; }
    public byte[] Token { get; }

    public static Task<InProcessHost> StartAsync()
    {
        var dir = Path.Combine("/tmp", "tdp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var token = Handshake.NewToken();
        var server = new PtyHostServer(new PtyHostServerOptions { Endpoint = Path.Combine(dir, "h.sock"), Token = token, Build = "test" },
            new HostLog(null, HostLog.Level.Warn));
        return Task.FromResult(new InProcessHost(server, token, dir));
    }

    public Task<PtyHostClient> ConnectAsync() =>
        PtyHostClient.ConnectAsync(new PtyHostEndpoint(Server.Endpoint, Token, Server.InstanceId), new PtyHostClientOptions { Kind = ClientKind.Ui });

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
        await _run;
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, string what, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"timed out waiting for {what}");
            }
            await Task.Delay(20);
        }
    }
}
