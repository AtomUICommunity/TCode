using Microsoft.Extensions.Logging.Abstractions;
using TCode.AgentHost;
using TCode.AgentHost.Catalog;
using TCode.Ai;
using TCode.Ai.Fake;
using TCode.Ai.Messages;
using TCode.Config;
using TCode.Runtimes;
using Xunit;

namespace TCode.AgentHost.Tests;

/// <summary>换运行时：尚无消息的会话可以在内置 Agent 与外部运行时之间切换，已有对话的会话不行。</summary>
public sealed class SessionRuntimeSwitchTests : IDisposable
{
    private readonly string? _savedHome = Environment.GetEnvironmentVariable(ConfigPaths.HomeVariable);
    private readonly string _home = Directory.CreateTempSubdirectory("tcode-switch-").FullName;

    public SessionRuntimeSwitchTests() => Environment.SetEnvironmentVariable(ConfigPaths.HomeVariable, _home);

    public void Dispose() => Environment.SetEnvironmentVariable(ConfigPaths.HomeVariable, _savedHome);

    [Fact]
    public async Task EmptySession_SwitchesRuntime_KeepingSessionId()
    {
        var (sessions, runtime, builtinModel) = Create();
        var cwd = Directory.CreateTempSubdirectory("tcode-switch-cwd-").FullName;
        var session = sessions.Create(cwd, builtinModel);

        var switched = await sessions.SwitchRuntimeAsync(session.Id, runtime, RuntimeModels.Create(runtime, "m1"));
        Assert.Equal(session.Id, switched.Id);
        Assert.Equal("fake-cli", switched.External?.RuntimeId);
        Assert.Same(switched, sessions.Get(session.Id));

        var back = await sessions.SwitchRuntimeAsync(session.Id, null, builtinModel);
        Assert.Null(back.External);
        Assert.Equal(session.Store.FilePath, back.Store.FilePath);
        await sessions.DisposeAsync();
    }

    [Fact]
    public async Task SessionWithHistory_CannotSwitchRuntime()
    {
        var (sessions, runtime, builtinModel) = Create();
        var cwd = Directory.CreateTempSubdirectory("tcode-switch-cwd-").FullName;
        var session = sessions.Create(cwd, builtinModel);
        session.Agent.LoadHistory([UserMessage.FromText("hi")]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sessions.SwitchRuntimeAsync(session.Id, runtime, RuntimeModels.Create(runtime, "m1")));
        Assert.Same(session, sessions.Get(session.Id));
        await sessions.DisposeAsync();
    }

    [Fact]
    public async Task ExternalSession_RefusesAcpWithoutSubagents_AndAcceptsPlan()
    {
        var (sessions, runtime, builtinModel) = Create();
        var cwd = Directory.CreateTempSubdirectory("tcode-switch-cwd-").FullName;
        var session = await sessions.SwitchRuntimeAsync(sessions.Create(cwd, builtinModel).Id, runtime, RuntimeModels.Create(runtime, "m1"));
        Assert.False(session.External!.SupportsSubagents);

        var options = SessionOptions.Apply(session, new TCode.Rpc.SessionOptionsDto(Role: "acp"));
        Assert.Equal("agent", options.Role);
        options = SessionOptions.Apply(session, new TCode.Rpc.SessionOptionsDto(Role: "plan"));
        Assert.Equal("plan", options.Role);
        Assert.True(session.Role.IsReadOnly);
        await sessions.DisposeAsync();
    }

    private static (SessionManager Sessions, IAgentRuntime Runtime, TCode.Ai.Models.ModelInfo Model) Create()
    {
        var catalog = new ModelCatalog(NullLogger<ModelCatalog>.Instance, new BuiltInCatalog(),
            new ModelDiscovery(new HttpClient(), NullLogger<ModelDiscovery>.Instance));
        var runtime = new FakeRuntime();
        var sessions = new SessionManager(new WireApiRegistry([new FakeWireApi()]), catalog, new RuntimeRegistry([runtime]),
            new TCode.AgentHost.Peers.PeerDirectory(NullLogger<TCode.AgentHost.Peers.PeerDirectory>.Instance), new TCode.Mcp.McpManager(new TCode.Mcp.McpHost(), null, () => []), NullLogger<SessionManager>.Instance);
        return (sessions, runtime, FakeWireApi.Models[0]);
    }

    private sealed class FakeRuntime : IAgentRuntime
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
