using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Reflection;
using Microsoft.Extensions.Logging;
using TKCode.AgentHost;
using TKCode.Ai;
using TKCode.Ai.Apis;
using TKCode.Ai.Fake;

if (args.Contains("--self-test"))
{
    return await TKCode.AgentHost.SelfTest.RunAsync();
}
// 外部运行时启动的 MCP 桥接进程（docs/design/mcp.md §10）
if (args.Contains("--mcp-bridge"))
{
    return await TKCode.AgentHost.McpBridgeClient.RunAsync();
}

var logs = Path.Combine(TKCode.Agent.Coding.TKCodePaths.Home, "logs");
TKCode.Config.CrashLog.Install(logs, "host",
    typeof(Program).Assembly.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?");

// stdout 专用于 JSON-RPC 帧，所有日志必须写到 stderr。
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.AddProvider(new TKCode.Config.FileLoggerProvider(logs, "host"));

// 所有 HTTP 适配器共享一个 HttpClient；流式请求由适配器自己的空闲超时控制，这里不设总超时。
builder.Services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
{
    Timeout = Timeout.InfiniteTimeSpan,
});
builder.Services.AddSingleton<IWireApi>(_ => new FakeWireApi());
builder.Services.AddSingleton<IWireApi>(sp => new OpenAIChatCompletionsApi(sp.GetRequiredService<HttpClient>()));
builder.Services.AddSingleton<IWireApi>(sp => new OpenAIResponsesApi(sp.GetRequiredService<HttpClient>()));
builder.Services.AddSingleton<IWireApi>(sp => new AnthropicMessagesApi(sp.GetRequiredService<HttpClient>()));
builder.Services.AddSingleton<WireApiRegistry>();
builder.Services.AddSingleton<TKCode.AgentHost.Catalog.BuiltInCatalog>();
builder.Services.AddSingleton<ModelDiscovery>();
builder.Services.AddSingleton<ModelCatalog>();
builder.Services.AddSingleton<ProviderTester>();
builder.Services.AddSingleton<TKCode.AgentHost.Catalog.CatalogSync>();
builder.Services.AddHostedService<SettingsWatcher>();
// 外部 Agent 运行时：自动发现用户安装的 CLI，后台探测，免配置。
builder.Services.AddSingleton(_ => new TKCode.Runtimes.RuntimeCache(Path.Combine(TKCode.Agent.Coding.TKCodePaths.Home, "cache", "runtimes")));
builder.Services.AddSingleton<TKCode.Runtimes.IAgentRuntime>(_ => new TKCode.Runtimes.Claude.ClaudeRuntime());
builder.Services.AddSingleton<TKCode.Runtimes.IAgentRuntime>(_ => new TKCode.Runtimes.Codex.CodexRuntime());
foreach (var acp in TKCode.Runtimes.Acp.AcpAgents.Known)
{
    builder.Services.AddSingleton<TKCode.Runtimes.IAgentRuntime>(sp =>
        new TKCode.Runtimes.Acp.AcpRuntime(acp, sp.GetRequiredService<TKCode.Runtimes.RuntimeCache>()));
}
builder.Services.AddSingleton<TKCode.Runtimes.RuntimeRegistry>();
builder.Services.AddHostedService<RuntimeProbeService>();
builder.Services.AddSingleton<SessionIndex>();
builder.Services.AddSingleton<TKCode.AgentHost.Peers.PeerDirectory>();
// MCP：Host 内共享的连接池（docs/design/mcp.md §6）
builder.Services.AddSingleton(sp => new TKCode.Mcp.McpManager(new TKCode.Mcp.McpHost
{
    LoggerFactory = sp.GetRequiredService<ILoggerFactory>(),
    Logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Mcp"),
}));
builder.Services.AddSingleton<McpService>();
builder.Services.AddSingleton<FileIndexService>();
builder.Services.AddSingleton(_ => new TKCode.Agent.Coding.Assets.AgentAssetStore());
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<AgentHostServer>();
builder.Services.AddHostedService<RpcHostService>();
builder.Services.AddHostedService<PeerHostService>();

await builder.Build().RunAsync();
return 0;
