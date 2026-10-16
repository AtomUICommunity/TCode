using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TCode.AgentHost;
using TCode.Ai;
using TCode.Ai.Apis;
using TCode.Ai.Fake;

if (args.Contains("--self-test"))
{
    return await TCode.AgentHost.SelfTest.RunAsync();
}

// stdout 专用于 JSON-RPC 帧，所有日志必须写到 stderr。
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

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
builder.Services.AddSingleton<TCode.AgentHost.Catalog.BuiltInCatalog>();
builder.Services.AddSingleton<ModelDiscovery>();
builder.Services.AddSingleton<ModelCatalog>();
builder.Services.AddSingleton<ProviderTester>();
builder.Services.AddSingleton<TCode.AgentHost.Catalog.CatalogSync>();
builder.Services.AddHostedService<SettingsWatcher>();
// 外部 Agent 运行时：自动发现用户安装的 CLI，后台探测，免配置。
builder.Services.AddSingleton(_ => new TCode.Runtimes.RuntimeCache(Path.Combine(TCode.Agent.Coding.TCodePaths.Home, "cache", "runtimes")));
builder.Services.AddSingleton<TCode.Runtimes.IAgentRuntime>(_ => new TCode.Runtimes.Claude.ClaudeRuntime());
builder.Services.AddSingleton<TCode.Runtimes.IAgentRuntime>(_ => new TCode.Runtimes.Codex.CodexRuntime());
foreach (var acp in TCode.Runtimes.Acp.AcpAgents.Known)
{
    builder.Services.AddSingleton<TCode.Runtimes.IAgentRuntime>(sp =>
        new TCode.Runtimes.Acp.AcpRuntime(acp, sp.GetRequiredService<TCode.Runtimes.RuntimeCache>()));
}
builder.Services.AddSingleton<TCode.Runtimes.RuntimeRegistry>();
builder.Services.AddHostedService<RuntimeProbeService>();
builder.Services.AddSingleton<SessionIndex>();
builder.Services.AddSingleton<TCode.AgentHost.Peers.PeerDirectory>();
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<AgentHostServer>();
builder.Services.AddHostedService<RpcHostService>();
builder.Services.AddHostedService<PeerHostService>();

await builder.Build().RunAsync();
return 0;
