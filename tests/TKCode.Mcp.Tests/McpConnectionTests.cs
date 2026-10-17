using ModelContextProtocol.Protocol;
using TKCode.Agent;
using TKCode.Agent.Coding.Workspace;
using TKCode.Ai.Messages;
using TKCode.Config;
using Xunit;

namespace TKCode.Mcp.Tests;

/// <summary>对测试用 MCP 服务器（tests/TKCode.Mcp.TestServer，stdio）的端到端测试。</summary>
public class McpConnectionTests
{
    private static McpManager Manager(params McpServerConfig[] configs) => new(new McpHost(), new McpTrustStore(Path.GetTempFileName()), () => configs);

    private static async Task<(McpManager Manager, IReadOnlyDictionary<string, IAgentTool> Tools, TKCode.Agent.Coding.Tools.IDynamicToolSource Source,
        TKCode.Agent.Coding.Tools.DynamicToolSet Set)> ConnectAsync(McpServerConfig config, TKCode.Agent.Coding.Tools.ToolContext? context = null)
    {
        var manager = Manager(config);
        var source = manager.CreateSource(context ?? TestSupport.Context());
        var set = await source.RefreshAsync(firstTurn: true, CancellationToken.None);
        return (manager, set.Tools.ToDictionary(t => t.Name), source, set);
    }

    [Fact]
    public async Task ListsToolsWithPrefixedNamesAndPromptSection()
    {
        var (manager, tools, source, set) = await ConnectAsync(TestSupport.StdioServer(disabled: ["fail"]));
        await using var _ = manager;
        Assert.Contains("mcp__test__echo", tools.Keys);
        Assert.DoesNotContain("mcp__test__fail", tools.Keys);
        var weird = Assert.Single(tools.Keys, k => k.StartsWith("mcp__test__weird", StringComparison.Ordinal));
        Assert.Equal("mcp__test__weird_name_with_spaces", weird);
        Assert.DoesNotContain("$schema", tools[weird].ParametersSchemaJson);
        Assert.Contains("\"properties\"", tools[weird].ParametersSchemaJson);
        Assert.StartsWith("Echo — ", tools["mcp__test__echo"].Description);
        var section = Assert.Single(set.Sections);
        Assert.Equal("mcp", section.Name);
        Assert.Contains("test (tkcode-test-server): 9 tools", section.Text);
        Assert.Contains("Use echo to repeat text.", section.Text);
        await source.DisposeAsync();
    }

    [Fact]
    public async Task CallsToolsAndConvertsResults()
    {
        var context = TestSupport.Context();
        var (manager, tools, source, _) = await ConnectAsync(TestSupport.StdioServer(env: new() { ["MCP_TEST_VAR"] = "{env:HOME}" }), context);
        await using var _m = manager;

        var echo = await tools["mcp__test__echo"].ExecuteAsync(new ToolInvocation("c1", TestSupport.Json("""{"text":"hi"}""")), CancellationToken.None);
        Assert.Equal("echo: hi", TestSupport.Text(echo));
        Assert.False(echo.IsError);
        Assert.Contains("\"server\":\"test\"", echo.Details);

        var env = await tools["mcp__test__env"].ExecuteAsync(new ToolInvocation("c2", TestSupport.Json("{}")), CancellationToken.None);
        Assert.Contains($"MCP_TEST_VAR={Environment.GetEnvironmentVariable("HOME")}", TestSupport.Text(env));
        // 没写 cwd 时以会话目录为工作目录
        Assert.Contains(Path.GetFileName(context.Cwd), TestSupport.Text(env));

        var image = await tools["mcp__test__image"].ExecuteAsync(new ToolInvocation("c3", TestSupport.Json("{}")), CancellationToken.None);
        Assert.Equal("image/png", Assert.Single(image.Content.OfType<ImageContent>()).MimeType);
        context.ModelSupportsImages = () => false;
        var noImage = await tools["mcp__test__image"].ExecuteAsync(new ToolInvocation("c4", TestSupport.Json("{}")), CancellationToken.None);
        Assert.Empty(noImage.Content.OfType<ImageContent>());
        Assert.Contains("[image: image/png", TestSupport.Text(noImage));

        var fail = await tools["mcp__test__fail"].ExecuteAsync(new ToolInvocation("c5", TestSupport.Json("{}")), CancellationToken.None);
        Assert.True(fail.IsError);
        Assert.Equal("bad input: try again", TestSupport.Text(fail));

        var structured = await tools["mcp__test__structured"].ExecuteAsync(new ToolInvocation("c6", TestSupport.Json("{}")), CancellationToken.None);
        Assert.Contains("22.5", TestSupport.Text(structured));

        var big = await tools["mcp__test__big"].ExecuteAsync(new ToolInvocation("c7", TestSupport.Json("""{"chars":150000}""")), CancellationToken.None);
        var text = TestSupport.Text(big);
        Assert.True(text.Length < 101_000);
        Assert.Contains("Full output saved to", text);
        var spill = Directory.GetFiles(context.TempDirectory, "mcp-c7.txt").Single();
        Assert.Equal(150_000, File.ReadAllText(spill).Length);
        await source.DisposeAsync();
    }

    [Fact]
    public async Task ReportsProgressAndHonoursCancellation()
    {
        var (manager, tools, source, _) = await ConnectAsync(TestSupport.StdioServer());
        await using var _m = manager;
        var updates = new List<string>();
        var progress = new SyncProgress(r => updates.Add(TestSupport.Text(r)));
        var result = await tools["mcp__test__slow"].ExecuteAsync(new ToolInvocation("p1", TestSupport.Json("""{"steps":3}"""), progress), CancellationToken.None);
        Assert.Equal("slow done after 3 steps", TestSupport.Text(result));
        Assert.Equal(["step 1 (1/3)", "step 2 (2/3)", "step 3 (3/3)"], updates);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            tools["mcp__test__slow"].ExecuteAsync(new ToolInvocation("p2", TestSupport.Json("""{"steps":50}""")), cts.Token));
        // 取消后连接仍可用
        var echo = await tools["mcp__test__echo"].ExecuteAsync(new ToolInvocation("p3", TestSupport.Json("""{"text":"still"}""")), CancellationToken.None);
        Assert.Equal("echo: still", TestSupport.Text(echo));
        await source.DisposeAsync();
    }

    [Fact]
    public async Task PicksUpListChangedAtNextRefresh()
    {
        var (manager, tools, source, _) = await ConnectAsync(TestSupport.StdioServer());
        await using var _m = manager;
        Assert.DoesNotContain("mcp__test__extra", tools.Keys);
        await tools["mcp__test__add_tool"].ExecuteAsync(new ToolInvocation("l1", TestSupport.Json("{}")), CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        IReadOnlyList<IAgentTool> next = [];
        while (DateTime.UtcNow < deadline)
        {
            next = (await source.RefreshAsync(false, CancellationToken.None)).Tools;
            if (next.Any(t => t.Name == "mcp__test__extra"))
            {
                break;
            }
            await Task.Delay(100);
        }
        Assert.Contains(next, t => t.Name == "mcp__test__extra");
        // 未变化的工具复用同一实例
        Assert.Same(tools["mcp__test__echo"], next.Single(t => t.Name == "mcp__test__echo"));
        await source.DisposeAsync();
    }

    [Fact]
    public async Task ForwardsElicitationWithSession()
    {
        var context = TestSupport.Context();
        var config = TestSupport.StdioServer();
        var host = new McpHost();
        McpElicitation? seen = null;
        host.Elicit = (request, _) =>
        {
            seen = request;
            return ValueTask.FromResult(new ElicitResult { Action = "accept", Content = new Dictionary<string, System.Text.Json.JsonElement> { ["name"] = TestSupport.Json("\"Ada\"") } });
        };
        await using var manager = new McpManager(host, new McpTrustStore(Path.GetTempFileName()), () => [config]);
        var source = manager.CreateSource(context);
        var tools = (await source.RefreshAsync(true, CancellationToken.None)).Tools.ToDictionary(t => t.Name);
        var result = await tools["mcp__test__ask"].ExecuteAsync(new ToolInvocation("e1", TestSupport.Json("{}")), CancellationToken.None);
        Assert.Equal("hello Ada", TestSupport.Text(result));
        Assert.NotNull(seen);
        Assert.Equal("test", seen!.ServerKey);
        Assert.Equal("What is your name?", seen.Request.Message);
        await source.DisposeAsync();
    }

    [Fact]
    public async Task SharesConnectionsAcrossSessionsAndReleasesLeases()
    {
        var config = TestSupport.StdioServer();
        var cwd = Directory.CreateTempSubdirectory("tkcode-mcp-shared-").FullName;
        await using var manager = Manager(config);
        var a = manager.CreateSource(TestSupport.Context(cwd));
        var b = manager.CreateSource(TestSupport.Context(cwd));
        await a.RefreshAsync(true, CancellationToken.None);
        await b.RefreshAsync(true, CancellationToken.None);
        var connection = Assert.Single(manager.Connections);
        Assert.Equal(2, connection.Leases);
        await a.DisposeAsync();
        await b.DisposeAsync();
        Assert.Equal(0, connection.Leases);

        // 另一个项目目录：stdio 连接按 cwd 区分
        var other = manager.CreateSource(TestSupport.Context());
        await other.RefreshAsync(true, CancellationToken.None);
        Assert.Equal(2, manager.Connections.Count);
        await other.DisposeAsync();
    }

    [Fact]
    public async Task AlwaysAllowAndPermissionPolicy()
    {
        var (manager, tools, source, _) = await ConnectAsync(TestSupport.StdioServer(alwaysAllow: ["echo"]));
        await using var _m = manager;
        Assert.Equal(ToolAccess.None, tools["mcp__test__echo"].DescribePermission(TestSupport.Json("{}")).Access);
        var permission = tools["mcp__test__env"].DescribePermission(TestSupport.Json("{}"));
        Assert.Equal(ToolAccess.External, permission.Access);

        var workspace = new WorkspaceRoots(Path.GetTempPath());
        var approvals = new SessionApprovals();
        var ask = PermissionPolicy.Evaluate(permission, PermissionMode.Ask, workspace, approvals);
        Assert.False(ask.Allowed);
        Assert.Equal("mcp", ask.Kind);
        Assert.Equal("mcp:test/env", ask.SessionKey);
        approvals.Add(ask.SessionKey!);
        Assert.True(PermissionPolicy.Evaluate(permission, PermissionMode.Ask, workspace, approvals).Allowed);
        Assert.True(PermissionPolicy.Evaluate(permission, PermissionMode.AutoApprove, workspace, new SessionApprovals()).Allowed);
        await source.DisposeAsync();
    }

    [Fact]
    public async Task FailingServerEndsInFailedStateWithLog()
    {
        var config = TestSupport.StdioServer(env: new() { ["MCP_TEST_CRASH"] = "1" }) with { StartupTimeout = TimeSpan.FromSeconds(10) };
        await using var manager = Manager(config);
        var connection = manager.Acquire(config, Path.GetTempPath());
        Assert.False(await connection.WaitReadyAsync(TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.Contains(connection.Log, l => l.Contains("crashing on purpose", StringComparison.Ordinal));
        manager.Release(connection);
    }

    [Fact]
    public async Task ProjectServersRequireTrust()
    {
        var root = Directory.CreateTempSubdirectory("tkcode-mcp-trust-").FullName;
        File.WriteAllText(Path.Combine(root, ".mcp.json"),
            $$"""{ "mcpServers": { "proj": { "command": "dotnet", "args": [{{System.Text.Json.JsonSerializer.Serialize(TestSupport.ServerDll)}}] } } }""");
        var trust = new McpTrustStore(Path.Combine(root, "trust.json"));
        await using var manager = new McpManager(new McpHost(), trust, () => []);
        var entry = Assert.Single(manager.Resolve(root));
        Assert.Equal(McpServerAvailability.Untrusted, entry.Availability);
        var source = manager.CreateSource(TestSupport.Context(root));
        Assert.Empty((await source.RefreshAsync(true, CancellationToken.None)).Tools);

        trust.Set(McpProjectConfig.ProjectRoot(root), entry.Config, true);
        Assert.Equal(McpServerAvailability.Ready, manager.Resolve(root).Single().Availability);
        Assert.Contains((await source.RefreshAsync(false, CancellationToken.None)).Tools, t => t.Name == "mcp__proj__echo");
        await source.DisposeAsync();
    }

    private sealed class SyncProgress(Action<AgentToolResult> report) : IProgress<AgentToolResult>
    {
        public void Report(AgentToolResult value) => report(value);
    }
}
