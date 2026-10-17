using System.Diagnostics;
using TCode.Terminal.Protocol;
using Xunit;
using static TCode.Terminal.Host.Tests.Shells;

namespace TCode.Terminal.Host.Tests;

/// <summary>发现、启动、Desktop 被杀后重连、陈旧文件（04 §1.3–§1.4、§16 的"发现""Desktop 被杀"）。用真实的 host 进程。</summary>
public sealed class DiscoveryTests : IAsyncLifetime
{
    private readonly string _home = Path.Combine(OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp", "tcd-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _build = HostMain.CurrentBuild();

    private PtyHostLaunchOptions Options(string? build = null) => new()
    {
        Home = _home,
        // TCODE_PTYHOST_DIR 指向 AOT 发布的 host 时，同一组用例验证 AOT 二进制（04 §15 验收）
        HostDirectory = Environment.GetEnvironmentVariable("TCODE_PTYHOST_DIR") ?? AppContext.BaseDirectory,
        Build = build ?? _build,
        ReadyTimeout = TimeSpan.FromSeconds(30),
        LogLevel = "debug",
    };

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        // 收尾：关掉测试启动的 host
        try
        {
            if (await PtyHostLocator.TryConnectAsync(_home, _build) is { } c)
            {
                await using (c)
                {
                    await c.QuitAsync(new QuitParams { Mode = "close" });
                    await c.Completion.WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
        }
        catch (Exception)
        {
        }
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task StartsOnceAndConcurrentStartersShareTheHost()
    {
        var clients = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => PtyHostLocator.ConnectOrStartAsync(Options())));
        try
        {
            Assert.Single(clients.Select(c => c.Host.InstanceId).Distinct());
            Assert.All(clients, c => Assert.Equal("full", c.Scope));
            var registration = PtyHostLocator.ReadRegistration(PtyHostPaths.Registration(_home, _build))!;
            Assert.Equal(clients[0].Host.InstanceId, registration.InstanceId);
            Assert.NotEqual(Environment.ProcessId, registration.Pid);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(PtyHostPaths.Registration(_home, _build)));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(PtyHostPaths.RunDirectory(_home)));
            }
        }
        finally
        {
            foreach (var c in clients)
            {
                await c.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task SessionsSurviveTheClientAndReattachConsistently()
    {
        uint sessionId;
        await using (var desktop = await PtyHostLocator.ConnectOrStartAsync(Options()))
        {
            using var sink = new ReplicaSink();
            var (created, handle) = await desktop.CreateSessionAsync(Interactive(), sink);
            sessionId = created.SessionId;
            await Wait.UntilAsync(() => sink.Text().Contains(OperatingSystem.IsWindows() ? ">" : "$"), "prompt");
            handle!.SendInput(Bytes("echo before-crash\r"));
            await Wait.UntilAsync(() => sink.Text().Contains("before-crash"), "echo");
            // 不发 host/quit 直接断开：等同于 Desktop 崩溃
        }

        await using var again = await PtyHostLocator.ConnectOrStartAsync(Options());
        var session = Assert.Single(await again.ListSessionsAsync());
        Assert.Equal((sessionId, "running"), (session.SessionId, session.State));
        using var replica = new ReplicaSink();
        var reattached = await again.AttachAsync(new AttachParams { SessionId = sessionId }, replica);
        await Wait.UntilAsync(() => replica.Core is not null, "snapshot");
        Assert.Contains("before-crash", replica.Text());
        reattached.SendInput(Bytes("echo after-reattach\r"));
        await Wait.UntilAsync(() => replica.Text().Contains("after-reattach"), "shell still alive");
    }

    [Fact]
    public async Task StaleRegistrationIsCleanedAfterTheHostExits()
    {
        var client = await PtyHostLocator.ConnectOrStartAsync(Options());
        var pid = PtyHostLocator.ReadRegistration(PtyHostPaths.Registration(_home, _build))!.Pid;
        await client.QuitAsync(new QuitParams { Mode = "close" });
        var disconnect = await client.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("quit", disconnect.Goodbye);
        await client.DisposeAsync();
        await Wait.UntilAsync(() => !IsRunning(pid), "host process exit");

        // 进程已死：锁拿得到 → 陈旧文件被清理，TryConnect 返回 null
        Assert.Null(await PtyHostLocator.TryConnectAsync(_home, _build));
        Assert.False(File.Exists(PtyHostPaths.Registration(_home, _build)));
    }

    [Fact]
    public async Task KilledHostIsReportedAsLost()
    {
        var client = await PtyHostLocator.ConnectOrStartAsync(Options());
        var pid = PtyHostLocator.ReadRegistration(PtyHostPaths.Registration(_home, _build))!.Pid;
        Process.GetProcessById(pid).Kill();
        var disconnect = await client.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(disconnect.Goodbye);  // 没有 goodbye：崩溃
        await client.DisposeAsync();
        await Wait.UntilAsync(() => !IsRunning(pid), "host process exit");
        // 下一次需要终端时重新启动一个新的 host
        await using var fresh = await PtyHostLocator.ConnectOrStartAsync(Options());
        Assert.NotEqual(pid, PtyHostLocator.ReadRegistration(PtyHostPaths.Registration(_home, _build))!.Pid);
    }

    [Fact]
    public async Task BuildMismatchFailsToStart()
    {
        var e = await Assert.ThrowsAsync<PtyHostStartException>(() => PtyHostLocator.ConnectOrStartAsync(Options(build: "000000000000")));
        Assert.Equal(PtyHostLocator.ExitBuildMismatch, e.ExitCode);
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
