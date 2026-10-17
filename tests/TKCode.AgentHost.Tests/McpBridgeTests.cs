using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using TKCode.AgentHost.Catalog;
using TKCode.Ai;
using TKCode.Ai.Fake;
using TKCode.Config;
using TKCode.Mcp;
using TKCode.Runtimes;
using Xunit;

namespace TKCode.AgentHost.Tests;

/// <summary>
/// 外部运行时的 MCP 桥接（docs/design/mcp.md §10）：外部会话得到 stdio 桥接的启动参数；
/// 用 SDK 客户端扮演外部 CLI 启动 `TKCode.AgentHost --mcp-bridge`，列出并调用 TKCode 配置的 MCP 工具，审批按 TKCode 的档位。
/// </summary>
public sealed class McpBridgeTests : IDisposable
{
    private readonly string? _savedHome = Environment.GetEnvironmentVariable(ConfigPaths.HomeVariable);
    private readonly string _home = Directory.CreateTempSubdirectory("tkcode-bridge-").FullName;

    public McpBridgeTests() => Environment.SetEnvironmentVariable(ConfigPaths.HomeVariable, _home);

    public void Dispose() => Environment.SetEnvironmentVariable(ConfigPaths.HomeVariable, _savedHome);

    [Fact]
    public async Task ExternalSession_ReachesMcpToolsThroughBridge()
    {
        var serverDll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../TKCode.Mcp.TestServer/bin/Debug/net10.0/TKCode.Mcp.TestServer.dll"));
        var config = McpServerConfig.From("test", new McpServerSettings { Command = "dotnet", Args = [serverDll], StartupTimeout = 20 },
            McpConfigSource.Global);
        var catalog = new ModelCatalog(NullLogger<ModelCatalog>.Instance, new BuiltInCatalog(),
            new ModelDiscovery(new HttpClient(), NullLogger<ModelDiscovery>.Instance));
        var runtime = new CapturingRuntime();
        await using var mcp = new McpManager(new McpHost(), new McpTrustStore(Path.Combine(_home, "trust.json")), () => [config]);
        var sessions = new SessionManager(new WireApiRegistry([new FakeWireApi()]), catalog, new RuntimeRegistry([runtime]),
            new TKCode.AgentHost.Peers.PeerDirectory(NullLogger<TKCode.AgentHost.Peers.PeerDirectory>.Instance), mcp, NullLogger<SessionManager>.Instance);
        // TKCODE_TEST_HOST_EXE 指向 AOT 发布的 Host 时，桥接进程用 AOT 二进制
        sessions.Bridge.SelfCommand = () => Environment.GetEnvironmentVariable("TKCODE_TEST_HOST_EXE") is { Length: > 0 } aot
            ? (aot, ["--mcp-bridge"])
            : ("dotnet", [Path.Combine(AppContext.BaseDirectory, "TKCode.AgentHost.dll"), "--mcp-bridge"]);

        var cwd = Directory.CreateTempSubdirectory("tkcode-bridge-cwd-").FullName;
        var session = sessions.CreateExternal(cwd, runtime, RuntimeModels.Create(runtime, "m1"));
        var external = Assert.IsType<ExternalAgent>(session.Agent);
        var launch = external.McpBridge?.Invoke();
        Assert.NotNull(launch);
        Assert.Contains("--mcp-bridge", launch!.Args);
        Assert.True(launch.Env.ContainsKey(McpBridgeServer.TokenVariable));
        Assert.DoesNotContain(launch.Args, a => a.Contains(launch.Env[McpBridgeServer.TokenVariable], StringComparison.Ordinal));

        // 扮演外部 CLI：启动桥接进程
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = launch.Command,
            Arguments = launch.Args.ToList(),
            EnvironmentVariables = launch.Env.ToDictionary(e => e.Key, e => (string?)e.Value),
        }));
        Assert.Equal("tkcode", client.ServerInfo.Name);
        var tools = await client.ListToolsAsync();
        Assert.Contains(tools, t => t.Name == "test__echo");
        Assert.All(tools, t => Assert.True(t.Name.Length <= 52));

        // Ask 档、没有 UI：TKCode 侧拒绝
        var denied = await client.CallToolAsync("test__echo", new Dictionary<string, object?> { ["text"] = "hi" });
        Assert.True(denied.IsError);
        // 自动放行：经共享连接调用成功
        session.Agent.PermissionMode = TKCode.Agent.PermissionMode.AutoApprove;
        var result = await client.CallToolAsync("test__echo", new Dictionary<string, object?> { ["text"] = "via bridge" });
        Assert.NotEqual(true, result.IsError);
        Assert.Equal("echo: via bridge", Assert.IsType<TextContentBlock>(result.Content.Single()).Text);
        var image = await client.CallToolAsync("test__image");
        Assert.Contains(image.Content, c => c is ImageContentBlock);

        // 未知令牌被拒绝
        await using (var bad = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = launch.Command,
            Arguments = launch.Args.ToList(),
            EnvironmentVariables = new Dictionary<string, string?>
            {
                [McpBridgeServer.SocketVariable] = launch.Env[McpBridgeServer.SocketVariable],
                [McpBridgeServer.TokenVariable] = "forged",
            },
        })))
        {
            await Assert.ThrowsAnyAsync<Exception>(async () => await bad.ListToolsAsync());
        }

        // 会话关闭：令牌作废
        await sessions.CloseAsync(session.Id);
        await Assert.ThrowsAnyAsync<Exception>(async () => await client.ListToolsAsync());
        await sessions.DisposeAsync();
    }

    [Fact]
    public void NoServers_NoBridge()
    {
        var catalog = new ModelCatalog(NullLogger<ModelCatalog>.Instance, new BuiltInCatalog(),
            new ModelDiscovery(new HttpClient(), NullLogger<ModelDiscovery>.Instance));
        var runtime = new CapturingRuntime();
        var sessions = new SessionManager(new WireApiRegistry([new FakeWireApi()]), catalog, new RuntimeRegistry([runtime]),
            new TKCode.AgentHost.Peers.PeerDirectory(NullLogger<TKCode.AgentHost.Peers.PeerDirectory>.Instance),
            new McpManager(new McpHost(), null, () => []), NullLogger<SessionManager>.Instance);
        var session = sessions.CreateExternal(Path.GetTempPath(), runtime, RuntimeModels.Create(runtime, "m1"));
        Assert.Null(((ExternalAgent)session.Agent).McpBridge?.Invoke());
    }

    private sealed class CapturingRuntime : IAgentRuntime
    {
        public string Id => "fake-cli";
        public string DisplayName => "Fake";
        public RuntimeDescriptor Descriptor { get; } = new("fake-cli", "Fake", RuntimeStatus.Ready);
        public event Action? DescriptorChanged { add { } remove { } }
        public Task<RuntimeDescriptor> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(Descriptor);
        public Task<IRuntimeSession> StartSessionAsync(RuntimeSessionRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
