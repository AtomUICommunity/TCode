using TKCode.Terminal.Protocol;
using Xunit;
using static TKCode.Terminal.Host.Tests.Shells;

namespace TKCode.Terminal.Host.Tests;

/// <summary>持久化与 revive（04 §10、§16 的"持久化"）。</summary>
public sealed class PersistenceTests : IDisposable
{
    private readonly string _home = Path.Combine(OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp", "tcp-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static HostSettingsDto Settings(int lines = 1000, int interval = 30) => new()
    {
        Persistence = new PersistenceSettingsDto { ScrollbackLines = lines, SaveIntervalSec = interval },
    };

    private string Revive => Path.Combine(_home, "terminal", "revive");

    /// <summary>在第一个 host 里产生内容并以 revive 模式退出，返回 persistentId。</summary>
    private async Task<string> ProduceAndQuitAsync(int lines, int scrollbackLines = 1000)
    {
        await using var host = TestHost.Start(_home);
        await using var client = await host.ConnectAsync();
        await client.ConfigureAsync(Settings(scrollbackLines));
        using var sink = new ReplicaSink();
        var (created, _) = await client.CreateSessionAsync(
            Script($"i=0; while [ $i -lt {lines} ]; do echo history-$i; i=$((i+1)); done; echo LAST-MARKER; sleep 30",
                $"for /L %i in (0,1,{lines - 1}) do @echo history-%i\r\n& echo LAST-MARKER& ping -n 30 127.0.0.1 >nul"), sink);
        await Wait.UntilAsync(() => sink.Text().Contains("LAST-MARKER"), "output", 30);
        var result = await client.QuitAsync(new QuitParams { Mode = "revive" });
        Assert.Equal(1, result.Saved);
        Assert.Equal("quit", (await client.Completion.WaitAsync(TimeSpan.FromSeconds(10))).Goodbye);
        return created.PersistentId;
    }

    [Fact]
    public async Task ReviveRestoresContentIntoScrollbackAndStartsAFreshShell()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix shell syntax");
        var id = await ProduceAndQuitAsync(200);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.Combine(Revive, id)));
            Assert.All(Directory.GetFiles(Path.Combine(Revive, id)), f => Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(f)));
        }

        await using var host = TestHost.Start(_home);
        await using var client = await host.ConnectAsync();
        var entry = Assert.Single(await client.ListReviveAsync());
        Assert.Equal((id, true), (entry.PersistentId, entry.Compatible));

        using var sink = new ReplicaSink();
        var (created, _) = await client.CreateSessionAsync(Interactive() with
        {
            ReviveFrom = id, PersistentId = id, ReviveBanner = "── restored ──",
        }, sink);
        Assert.Equal(id, created.PersistentId);
        Assert.False(created.Revived!.UsedFallback);
        await Wait.UntilAsync(() => sink.Text().Contains('$'), "new shell prompt");
        var text = sink.Text();
        Assert.Contains("LAST-MARKER", text);
        Assert.Contains("history-0", text);
        Assert.Contains("── restored ──", text);
        // 恢复的内容与分隔文字在回滚里，新 shell 从干净的屏幕左上角开始
        Assert.True(text.IndexOf("── restored ──", StringComparison.Ordinal) > text.IndexOf("LAST-MARKER", StringComparison.Ordinal));
        Assert.Equal(0, sink.Core!.Cursor.Row);
        await Wait.UntilAsync(async () => await host.MirrorGridAsync(created.SessionId) == sink.Grid(), "grids converge");
    }

    [Fact]
    public async Task SavedScrollbackIsTrimmed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix shell syntax");
        var id = await ProduceAndQuitAsync(5000, scrollbackLines: 100);
        await using var host = TestHost.Start(_home);
        await using var client = await host.ConnectAsync();
        var entry = Assert.Single(await client.ListReviveAsync());
        Assert.InRange(entry.Lines, 100, 1000);  // libghostty 按页裁剪：≥ N
        using var sink = new ReplicaSink();
        await client.CreateSessionAsync(Interactive() with { ReviveFrom = id }, sink);
        await Wait.UntilAsync(() => sink.Text().Contains("LAST-MARKER"), "restored");
        Assert.DoesNotContain("history-0\n", sink.Text());
        Assert.Contains("history-4999", sink.Text());
    }

    [Fact]
    public async Task CorruptSnapshotFallsBackToVtText()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix shell syntax");
        var id = await ProduceAndQuitAsync(50);
        await File.WriteAllBytesAsync(Path.Combine(Revive, id, "snapshot.bin"), [1, 2, 3]);
        await using var host = TestHost.Start(_home);
        await using var client = await host.ConnectAsync();
        using var sink = new ReplicaSink();
        var (created, _) = await client.CreateSessionAsync(Interactive() with { ReviveFrom = id }, sink);
        Assert.True(created.Revived!.UsedFallback);
        await Wait.UntilAsync(() => sink.Text().Contains("LAST-MARKER"), "restored from tail.vt");
    }

    [Fact]
    public async Task PeriodicSaveOnlyWritesChangedSessions()
    {
        await using var host = TestHost.Start(_home);
        await using var client = await host.ConnectAsync();
        await client.ConfigureAsync(Settings(interval: 1));
        using var sink = new ReplicaSink();
        var (created, handle) = await client.CreateSessionAsync(Interactive(), sink);
        var meta = Path.Combine(Revive, created.PersistentId, "meta.json");
        await Wait.UntilAsync(() => File.Exists(meta), "first periodic save", 15);
        await Wait.UntilAsync(() => sink.Text().Contains(OperatingSystem.IsWindows() ? ">" : "$"), "prompt");
        await Task.Delay(1500);  // 提示符的输出也已保存
        var first = File.GetLastWriteTimeUtc(meta);
        await Task.Delay(2500);
        Assert.Equal(first, File.GetLastWriteTimeUtc(meta));  // 没有新输出：不重写
        handle!.SendInput(Bytes("echo changed\r"));
        await Wait.UntilAsync(() => File.GetLastWriteTimeUtc(meta) > first, "save after new output", 10);
    }

    [Fact]
    public async Task ClosingDeletesEntriesAndCloseModeClearsAll()
    {
        await using var host = TestHost.Start(_home);
        await using var client = await host.ConnectAsync();
        await client.ConfigureAsync(Settings());
        var (a, _) = await client.CreateSessionAsync(Interactive(), null);
        var (b, _) = await client.CreateSessionAsync(Interactive(), null);
        Assert.Equal(2, (await client.SaveAsync(new PersistenceSaveParams())).Saved);
        Assert.Equal(2, (await client.ListReviveAsync()).Length);

        // 用户明确关闭的会话不恢复
        Assert.True((await client.CloseSessionAsync(a.SessionId, force: true)).Closed);
        Assert.Equal(b.PersistentId, Assert.Single(await client.ListReviveAsync()).PersistentId);

        await client.QuitAsync(new QuitParams { Mode = "close" });
        await client.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(Directory.GetDirectories(Revive));
    }

    [Fact]
    public async Task AgentSessionsAreNotPersistedByDefault()
    {
        await using var host = TestHost.Start(_home);
        await using var client = await host.ConnectAsync();
        await client.ConfigureAsync(Settings());
        await client.CreateSessionAsync(Interactive() with { Kind = "agent", AgentSessionId = "a1" }, null);
        Assert.Equal(0, (await client.SaveAsync(new PersistenceSaveParams())).Saved);
    }
}
