using Microsoft.Extensions.Logging.Abstractions;
using TCode.Agent.Coding.Tools.Agents;
using TCode.AgentHost.Catalog;
using TCode.AgentHost.Peers;
using TCode.Ai;
using TCode.Ai.Fake;
using TCode.Ai.Messages;
using TCode.Runtimes;
using Xunit;

namespace TCode.AgentHost.Tests;

public sealed class AgentCoordinatorTests
{
    private static SessionManager CreateManager(PeerDirectory? peers = null)
    {
        var catalog = new ModelCatalog(NullLogger<ModelCatalog>.Instance, new BuiltInCatalog(),
            new ModelDiscovery(new HttpClient(), NullLogger<ModelDiscovery>.Instance));
        return new SessionManager(new WireApiRegistry([new FakeWireApi(TimeSpan.Zero)]), catalog, new RuntimeRegistry([]),
            peers ?? new PeerDirectory(NullLogger<PeerDirectory>.Instance), new TCode.Mcp.McpManager(new TCode.Mcp.McpHost(), null, () => []), NullLogger<SessionManager>.Instance);
    }

    private static string TempDir() => Directory.CreateTempSubdirectory("tcode-coord-").FullName;

    [Fact]
    public async Task Subagent_RunsWithSharedState_ReturnsReport_AndCanBeMessagedAfterwards()
    {
        await using var sessions = CreateManager();
        var parent = sessions.Create(TempDir(), FakeWireApi.Models[1]);
        var handle = await sessions.Coordinator.SpawnAsync(
            new SubagentRequest(parent.Id, "say hi", "Reply with a greeting.", SubagentTypes.Explore, null, "Greeter"), CancellationToken.None);

        Assert.Equal("greeter", handle.Name);
        var outcome = await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("completed", outcome.Status);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Report));

        // 子 Agent 不能再派生子 Agent
        await Assert.ThrowsAsync<InvalidOperationException>(() => sessions.Coordinator.SpawnAsync(
            new SubagentRequest(handle.AgentId, "nested", "x", SubagentTypes.GeneralPurpose, null, null), CancellationToken.None));

        var peers = await sessions.Coordinator.ListAsync(parent.Id, CancellationToken.None);
        Assert.Contains(peers, p => p.Name == "greeter" && p.Kind.StartsWith("subagent", StringComparison.Ordinal));
        var fromChild = await sessions.Coordinator.ListAsync(handle.AgentId, CancellationToken.None);
        Assert.Contains(fromChild, p => p.Name == "main");

        // 追问已结束的子 Agent：它的回复以通知的形式回到父会话
        var delivery = await sessions.Coordinator.SendAsync(parent.Id, "greeter", "One more time please.", null, CancellationToken.None);
        Assert.Contains("delivered to greeter", delivery);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!parent.Agent.Messages.OfType<UserMessage>().Any(m => m.Source == "task-notification") && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
        var notification = Assert.Single(parent.Agent.Messages.OfType<UserMessage>(), m => m.Source == "task-notification");
        Assert.Contains("<agent>greeter</agent>", ((TextContent)notification.Content[0]).Text);
    }

    [Fact]
    public async Task SendMessage_BetweenTopLevelSessions_WrapsAsSessionMessage()
    {
        await using var sessions = CreateManager();
        var a = sessions.Create(TempDir(), FakeWireApi.Models[1]);
        var b = sessions.Create(TempDir(), FakeWireApi.Models[1]);
        var result = await sessions.Coordinator.SendAsync(a.Id, sessions.NameOf(b), "Please check the build.", null, CancellationToken.None);
        Assert.Contains("started working", result);
        await b.Agent.WaitForIdleAsync();
        var message = Assert.Single(b.Agent.Messages.OfType<UserMessage>());
        Assert.Equal("session-message", message.Source);
        Assert.Contains($"from=\"{sessions.NameOf(a)}\"", ((TextContent)message.Content[0]).Text);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => sessions.Coordinator.SendAsync(a.Id, "nobody", "x", null, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sessions.Coordinator.SendAsync(a.Id, sessions.NameOf(a), "x", null, CancellationToken.None));
    }

    [Fact]
    public async Task Peers_ListAndDeliverAcrossHosts()
    {
        await using var server = new PeerDirectory(NullLogger<PeerDirectory>.Instance);
        await using var host = CreateManager(server);
        var target = host.Create(TempDir(), FakeWireApi.Models[1]);
        server.Start(host.Coordinator);
        Assert.True(server.IsListening);

        var client = new PeerDirectory(NullLogger<PeerDirectory>.Instance) { IncludeSelf = true };
        var listed = await client.ListPeersAsync(CancellationToken.None);
        var peer = Assert.Single(listed, p => p.Session.Ref == AgentCoordinator.RefOf(target.Id));
        Assert.Equal(host.NameOf(target), peer.Session.Name);

        var result = await client.SendAsync(peer, new PeerSendRequest(peer.Session.Ref, "remote-1234", "abcd1234", "hello from afar"), CancellationToken.None);
        Assert.Contains("delivered", result);
        await target.Agent.WaitForIdleAsync();
        Assert.Contains("hello from afar", ((TextContent)target.Agent.Messages.OfType<UserMessage>().Single().Content[0]).Text);
    }

    [Fact]
    public async Task Subagent_SurvivesParentTurnAbort_ButIsStoppedWithParentSession()
    {
        await using var sessions = CreateManager();
        var parent = sessions.Create(TempDir(), FakeWireApi.Models[1]);
        var handle = await sessions.Coordinator.SpawnAsync(
            new SubagentRequest(parent.Id, "say hi", "Reply with a greeting.", SubagentTypes.Explore, null, null), CancellationToken.None);

        // 中止父会话当前这一轮不应影响子 Agent
        parent.Agent.Abort();
        var outcome = await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("completed", outcome.Status);

        // 关闭父会话：尚在运行的子 Agent 以 stopped 收尾
        var second = await sessions.Coordinator.SpawnAsync(
            new SubagentRequest(parent.Id, "again", "Reply with a greeting.", SubagentTypes.Explore, null, "again"), CancellationToken.None);
        await sessions.CloseAsync(parent.Id);
        var closed = await second.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(closed.Status, new[] { "stopped", "completed" });
    }
}
