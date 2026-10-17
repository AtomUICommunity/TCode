using Microsoft.Extensions.Logging.Abstractions;
using TKCode.Agent;
using TKCode.AgentHost.Catalog;
using TKCode.AgentHost.Peers;
using TKCode.Ai;
using TKCode.Ai.Fake;
using TKCode.Ai.Messages;
using TKCode.Config;
using TKCode.Runtimes;
using Xunit;

namespace TKCode.AgentHost.Tests;

/// <summary>空闲会话释放后，再次访问时从会话文件透明地重新打开，并保留不落盘的权限档位与会话内授权。</summary>
public sealed class SessionIdleReleaseTests : IDisposable
{
    private readonly string? _savedHome = Environment.GetEnvironmentVariable(ConfigPaths.HomeVariable);
    private readonly string _home = Directory.CreateTempSubdirectory("tkcode-idle-").FullName;

    public SessionIdleReleaseTests() => Environment.SetEnvironmentVariable(ConfigPaths.HomeVariable, _home);

    public void Dispose() => Environment.SetEnvironmentVariable(ConfigPaths.HomeVariable, _savedHome);

    private static SessionManager CreateManager(TimeSpan? chunkDelay = null)
    {
        var catalog = new ModelCatalog(NullLogger<ModelCatalog>.Instance, new BuiltInCatalog(),
            new ModelDiscovery(new HttpClient(), NullLogger<ModelDiscovery>.Instance));
        return new SessionManager(new WireApiRegistry([new FakeWireApi(chunkDelay ?? TimeSpan.Zero)]), catalog, new RuntimeRegistry([]),
            new PeerDirectory(NullLogger<PeerDirectory>.Instance), new TKCode.Mcp.McpManager(new TKCode.Mcp.McpHost(), null, () => []),
            NullLogger<SessionManager>.Instance) { IdleTimeout = TimeSpan.Zero };
    }

    [Fact]
    public async Task IdleSession_IsReleased_AndReopenedOnAccess_WithPermissionAndApprovals()
    {
        await using var sessions = CreateManager();
        var session = sessions.Create(Directory.CreateTempSubdirectory("tkcode-idle-cwd-").FullName, FakeWireApi.Models[1]);
        session.Prompt("hello");
        await session.Agent.WaitForIdleAsync();
        session.Agent.PermissionMode = PermissionMode.FullAccess;
        session.Approvals.Add("command:git status");
        var messages = session.Agent.Messages.Count;

        await sessions.SweepAsync();

        Assert.Null(sessions.TryGet(session.Id));
        Assert.Empty(sessions.TopLevel());
        var reopened = sessions.Get(session.Id);
        Assert.NotSame(session, reopened);
        Assert.Equal(session.Store.FilePath, reopened.Store.FilePath);
        Assert.Equal(messages, reopened.Agent.Messages.Count);
        Assert.Equal(PermissionMode.FullAccess, reopened.Agent.PermissionMode);
        Assert.True(reopened.Approvals.Contains("command:git status"));
        Assert.Same(reopened, sessions.OpenFile(session.Store.FilePath));
    }

    [Fact]
    public async Task Sweep_KeepsRunningSessions_AndSessionsWithoutFile()
    {
        await using var sessions = CreateManager(TimeSpan.FromSeconds(1));
        var cwd = Directory.CreateTempSubdirectory("tkcode-idle-cwd-").FullName;
        var fresh = sessions.Create(cwd, FakeWireApi.Models[1]);
        var running = sessions.Create(cwd, FakeWireApi.Models[1]);
        running.Prompt("hello");
        while (!File.Exists(running.Store.FilePath))
        {
            await Task.Delay(20);
        }

        await sessions.SweepAsync();

        Assert.Same(fresh, sessions.TryGet(fresh.Id));
        Assert.Same(running, sessions.TryGet(running.Id));
        running.Agent.Abort();
        await running.Agent.WaitForIdleAsync();
    }

    [Fact]
    public async Task Sweep_KeepsSessionsWithRunningBackgroundTasks()
    {
        await using var sessions = CreateManager();
        var session = sessions.Create(Directory.CreateTempSubdirectory("tkcode-idle-cwd-").FullName, FakeWireApi.Models[1]);
        session.Prompt("hello");
        await session.Agent.WaitForIdleAsync();
        var task = session.Tools!.BackgroundTasks.Start("bash", "npm run dev", null, () => Task.CompletedTask, session.Id);

        await sessions.SweepAsync();
        Assert.Same(session, sessions.TryGet(session.Id));

        // 完成通知会作为新一轮投递给会话，等这一轮结束
        session.Tools.BackgroundTasks.Complete(task, "completed", null);
        while (!session.Agent.Messages.OfType<UserMessage>().Any(m => m.Source == "task-notification") || session.Agent.IsRunning)
        {
            await Task.Delay(20);
        }
        await sessions.SweepAsync();
        Assert.Null(sessions.TryGet(session.Id));
    }

    [Fact]
    public async Task ClosedSession_IsNotReopened()
    {
        await using var sessions = CreateManager();
        var session = sessions.Create(Directory.CreateTempSubdirectory("tkcode-idle-cwd-").FullName, FakeWireApi.Models[1]);
        session.Prompt("hello");
        await session.Agent.WaitForIdleAsync();

        await sessions.SweepAsync();
        await sessions.CloseAsync(session.Id);

        Assert.Throws<KeyNotFoundException>(() => sessions.Get(session.Id));
    }
}
