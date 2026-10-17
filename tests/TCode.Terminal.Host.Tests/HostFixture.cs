using System.Collections.Concurrent;
using System.Text;
using TCode.Terminal.Core;
using TCode.Terminal.Core.Testing;
using TCode.Terminal.Host;
using TCode.Terminal.Protocol;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace TCode.Terminal.Host.Tests;

/// <summary>进程内的 host：每个测试一个（独立的 endpoint 与令牌）。</summary>
internal sealed class TestHost : IAsyncDisposable
{
    private readonly string _dir;

    private TestHost(PtyHostServer server, byte[] token, string dir)
    {
        Server = server;
        Token = token;
        _dir = dir;
        Run = Task.Run(server.RunAsync);
    }

    public PtyHostServer Server { get; }
    public byte[] Token { get; }
    public Task Run { get; }
    public List<string> LogLines { get; } = [];

    public static TestHost Start()
    {
        // sun_path 上限约 104 字节：放在短路径下
        var dir = Path.Combine(OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp", "tch-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var endpoint = OperatingSystem.IsWindows() ? $@"\\.\pipe\tcode-test-{Guid.NewGuid():N}" : Path.Combine(dir, "h.sock");
        var token = Handshake.NewToken();
        var lines = new List<string>();
        var server = new PtyHostServer(new PtyHostServerOptions { Endpoint = endpoint, Token = token, Build = "test" },
            new HostLog(null, HostLog.Level.Debug, line => { lock (lines) { lines.Add(line); } }));
        var host = new TestHost(server, token, dir);
        host.LogLines.AddRange(lines);
        return host;
    }

    public PtyHostEndpoint Endpoint(byte[]? token = null, string? tokenId = null) => new(Server.Endpoint, token ?? Token, Server.InstanceId, tokenId);

    public Task<PtyHostClient> ConnectAsync(ClientKind kind = ClientKind.Ui, byte[]? token = null, string? tokenId = null) =>
        PtyHostClient.ConnectAsync(Endpoint(token, tokenId), new PtyHostClientOptions { Kind = kind });

    /// <summary>在会话 actor 上取镜像核心的 .grid。</summary>
    public Task<string> MirrorGridAsync(uint sessionId) =>
        Server.Sessions[sessionId].InvokeAsync(() => GridSnapshot.Capture(Server.Sessions[sessionId].Core));

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
        await Run;
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// 模拟 UI 侧核心（01 §3.2）：快照 → Restore，之后按序 Write / 应用记录，每 32 KiB Ack 一次（§7.2 第 4 条）。
/// 回调在读线程上，直接操作核心（测试里足够快）。<see cref="AutoAck"/> 为 false 时不 Ack（模拟慢订阅者）。
/// </summary>
internal sealed class ReplicaSink : IPtySessionSink, IDisposable
{
    private readonly MemoryStream _snapshot = new();
    private readonly Lock _lock = new();
    private ulong _lastAck;

    public PtySessionHandle? Handle { get; set; }
    public TerminalCore? Core { get; private set; }
    public CoreOptionsDto? Options { get; set; }
    public ulong Position { get; private set; }
    public bool AutoAck { get; set; } = true;
    public ConcurrentQueue<object> Events { get; } = new();
    public ConcurrentQueue<string> Resyncs { get; } = new();
    public TaskCompletionSource<string> Detached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public long OutputBytes;

    public void OnAttached(PtySessionHandle handle)
    {
        var attach = handle.Attach;
        lock (_lock)
        {
            Handle = handle;
            Options = attach.CoreOptions;
            Position = attach.SnapshotSeq;
            if (attach.SnapshotFormat is "none" or "resume")
            {
                Core ??= Create();
            }
        }
    }

    private TerminalCore Create() => TerminalCore.Create(CoreOptions());

    private TerminalCoreOptions CoreOptions() =>
        new(new TerminalSize(Options!.Cols, Options.Rows, Options.CellWidth, Options.CellHeight)) { ScrollbackMaxLines = Options.ScrollbackMaxLines };

    public void OnSnapshotChunk(uint attachId, SnapshotFormat format, ulong offset, ReadOnlySpan<byte> chunk, bool last)
    {
        lock (_lock)
        {
            Assert.Equal((long)offset, _snapshot.Length);
            _snapshot.Write(chunk);
            if (last)
            {
                Core?.Dispose();
                if (format == SnapshotFormat.Ghostty)
                {
                    Core = TerminalCore.Restore(_snapshot.ToArray(), CoreOptions());
                }
                else
                {
                    Core = Create();
                    Core.Write(_snapshot.ToArray());
                }
                _snapshot.SetLength(0);
                Ack();
            }
        }
    }

    public void OnOutput(ulong position, ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            Assert.True(position == Position, $"output at {position}, expected {Position}");
            Core!.Write(data);
            Position += (ulong)data.Length;
            Interlocked.Add(ref OutputBytes, data.Length);
            if (Position - _lastAck >= 32 * 1024)
            {
                Ack();
            }
        }
    }

    public void OnRecord(ulong position, in LogRecord record)
    {
        lock (_lock)
        {
            Assert.True(position == Position, $"record at {position}, expected {Position}");
            switch (record.Kind)
            {
                case RecordKind.Resize:
                    Core!.Resize(new TerminalSize(record.Cols, record.Rows, record.CellWidth, record.CellHeight));
                    break;
                case RecordKind.Reset:
                    Core!.Reset();
                    break;
                case RecordKind.Clear:
                    Core!.Clear(record.ClearFlags switch { 1 => ClearTarget.Screen, 2 => ClearTarget.Scrollback, _ => ClearTarget.All });
                    break;
            }
            Position++;
            Ack();
        }
    }

    private void Ack()
    {
        if (AutoAck && Handle is { } h)
        {
            h.Ack(Position);
            _lastAck = Position;
        }
    }

    /// <summary>手动 Ack 到当前位置（AutoAck 关闭时用）。</summary>
    public void AckNow()
    {
        lock (_lock)
        {
            Handle?.Ack(Position);
            _lastAck = Position;
        }
    }

    public string Grid()
    {
        lock (_lock)
        {
            return GridSnapshot.Capture(Core!);
        }
    }

    public string Text()
    {
        lock (_lock)
        {
            return Core?.FormatPlainText() ?? string.Empty;
        }
    }

    public void OnEvent(object e) => Events.Enqueue(e);

    public void OnResync(string reason) => Resyncs.Enqueue(reason);

    public void OnDetached(string reason) => Detached.TrySetResult(reason);

    public void Dispose() => Core?.Dispose();
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

    public static async Task UntilAsync(Func<Task<bool>> condition, string what, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"timed out waiting for {what}");
            }
            await Task.Delay(20);
        }
    }
}

internal static class Shells
{
    public static SessionCreateParams Script(string unix, string windows, int cols = 80, int rows = 24) => new()
    {
        Size = new TerminalSizeDto(cols, rows, 8, 16),
        Launch = OperatingSystem.IsWindows() ? new LaunchDto("cmd.exe", ["/d", "/c", windows]) : new LaunchDto("/bin/sh", ["-c", unix]),
        Env = new Dictionary<string, string?> { ["PS1"] = "$ " },
    };

    public static SessionCreateParams Interactive(int cols = 80, int rows = 24) => new()
    {
        Size = new TerminalSizeDto(cols, rows, 8, 16),
        Launch = OperatingSystem.IsWindows() ? new LaunchDto("cmd.exe", ["/d", "/k", "prompt $G"]) : new LaunchDto("/bin/sh", ["-i"]),
        Env = new Dictionary<string, string?> { ["PS1"] = "$ " },
    };

    public static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
}
