using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using TKCode.Agent;
using TKCode.Agent.Coding.Sessions;
using TKCode.Agent.Coding.Tools.Agents;
using TKCode.AgentHost.Catalog;
using TKCode.AgentHost.Peers;
using TKCode.Ai;
using TKCode.Ai.Messages;
using TKCode.Ai.Models;
using TKCode.Ai.Streaming;
using TKCode.Config;
using TKCode.Rpc;
using TKCode.Runtimes;
using Xunit;

namespace TKCode.AgentHost.Tests;

public sealed class ModelRoutingTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("tkcode-model-routing-").FullName;
    private readonly Dictionary<string, string?> _saved = [];

    public ModelRoutingTests()
    {
        foreach (var key in new[] { ConfigPaths.HomeVariable, "TKCODE_EXTERNAL_RUNTIMES", "COWORK_API_KEY" })
        {
            _saved[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, null);
        }
        Environment.SetEnvironmentVariable(ConfigPaths.HomeVariable, _home);
    }

    public void Dispose()
    {
        foreach (var (key, value) in _saved)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    private (SessionManager Sessions, ModelCatalog Catalog) Create(RecordingApi api, string? fastModel)
    {
        File.WriteAllText(ConfigPaths.SettingsFile, """
            {
              "model": "primary/main",
              "providers": {
                "primary": { "api": "chat", "baseUrl": "https://primary.invalid/v1", "apiKey": "primary-key",
                  "models": { "main": { "reasoning": true, "contextWindow": 64000 } } },
                "quick": { "api": "chat", "baseUrl": "https://quick.invalid/v1", "apiKey": "quick-key",
                  "models": { "small": { "contextWindow": 8192 } } }
              }
            }
            """);
        SettingsFile.Update(root => SettingsFile.Set(root, "fastModel", fastModel is null ? null : System.Text.Json.Nodes.JsonValue.Create(fastModel)));
        var catalog = new ModelCatalog(NullLogger<ModelCatalog>.Instance, new BuiltInCatalog(),
            new ModelDiscovery(new HttpClient(), NullLogger<ModelDiscovery>.Instance));
        var sessions = new SessionManager(new WireApiRegistry([api]), catalog, new RuntimeRegistry([]),
            new PeerDirectory(NullLogger<PeerDirectory>.Instance),
            new TKCode.Mcp.McpManager(new TKCode.Mcp.McpHost(), null, () => []), NullLogger<SessionManager>.Instance);
        return (sessions, catalog);
    }

    [Theory]
    [InlineData("quick/small", null, "quick/small", "quick-key")]
    [InlineData(null, null, "primary/main", "primary-key")]
    [InlineData("missing/model", null, "primary/main", "primary-key")]
    [InlineData("quick/small", "primary/main", "primary/main", "primary-key")]
    public async Task Subagent_PrefersExplicitThenFastThenSession(string? fastModel, string? explicitModel, string expected, string key)
    {
        var api = new RecordingApi();
        var (sessions, catalog) = Create(api, fastModel);
        await using var cleanup = sessions;
        var parent = sessions.Create(_home, catalog.Get("primary/main"));
        parent.ContextWindow = 64_000;
        parent.Agent.ThinkingLevel = ThinkingLevel.Max;

        var handle = await sessions.Coordinator.SpawnAsync(new SubagentRequest(parent.Id, "greet", "Say hello.",
            SubagentTypes.Explore, explicitModel, null), TestContext.Current.CancellationToken);
        Assert.Equal("completed", (await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        var request = Assert.Single(api.Requests);
        Assert.Equal(expected, ModelCatalog.RefOf(request.Model));
        Assert.Equal(key, request.Options.ApiKey);
        if (expected == "quick/small")
        {
            Assert.Equal(ThinkingLevel.Off, request.Options.ThinkingLevel);
        }

        // 已打开的父会话派生下一位子 Agent 时使用刚刚重载的配置。
        SettingsFile.Update(root => root["fastModel"] = "quick/small");
        Assert.True(catalog.Reload());
        var next = await sessions.Coordinator.SpawnAsync(new SubagentRequest(parent.Id, "again", "Say hello.",
            SubagentTypes.Explore, null, null), TestContext.Current.CancellationToken);
        await next.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("quick/small", ModelCatalog.RefOf(api.Requests.Last().Model));
        Assert.Equal("primary/main", ModelCatalog.RefOf(parent.Agent.Model));
    }

    [Theory]
    [InlineData("quick/small", "quick/small", "quick-key")]
    [InlineData(null, "primary/main", "primary-key")]
    [InlineData("missing/model", "primary/main", "primary-key")]
    public async Task Title_UsesFastOrSessionModel_PersistsAndPublishes_WithoutChangingConversation(string? fastModel, string expected, string key)
    {
        var api = new RecordingApi();
        var (sessions, catalog) = Create(api, fastModel);
        await using var cleanup = sessions;
        var client = new TitleClient();
        sessions.AttachClient(client);
        var session = sessions.Create(_home, catalog.Get("primary/main"));
        session.Prompt(UserMessage.FromText("Implement model configuration."));
        await session.Agent.WaitForIdleAsync();

        Assert.Equal("模型配置", (await client.Title.Task.WaitAsync(TimeSpan.FromSeconds(5))).Title);
        var request = Assert.Single(api.Requests, r => r.Context.SystemPrompt == SessionTitleGenerator.Instructions);
        Assert.Equal(expected, ModelCatalog.RefOf(request.Model));
        Assert.Equal(key, request.Options.ApiKey);
        Assert.Empty(request.Context.Tools);
        Assert.Equal(ThinkingLevel.Off, request.Options.ThinkingLevel);
        Assert.True(request.Options.MaxTokens <= 512);
        Assert.Equal("模型配置", SessionStore.ReadSummary(session.Store.FilePath).CustomTitle);
        // 用户消息与回复；会话开头的记忆索引（docs/design/memory.md §4.2）不计
        Assert.Equal(2, session.Agent.Messages.Count(m => !TKCode.Agent.Coding.Memory.ProjectMemory.IsIndexMessage(m)));
        Assert.Equal("primary/main", ModelCatalog.RefOf(session.Agent.Model));
        var sequences = client.Sequences.ToArray();
        Assert.Equal(sequences.Order(), sequences);

        session.Prompt("Follow up.");
        await session.Agent.WaitForIdleAsync();
        Assert.Single(api.Requests, r => r.Context.SystemPrompt == SessionTitleGenerator.Instructions);
    }

    [Theory]
    [InlineData("My own title")]
    [InlineData("")]
    public async Task GeneratedTitle_DoesNotOverwriteManualRenameOrReset(string manualTitle)
    {
        var store = SessionStore.Create(_home);
        await store.AppendMessageAsync(UserMessage.FromText("Original title"));
        await store.AppendMetaAsync(manualTitle, null);

        Assert.False(await store.TryAppendGeneratedTitleAsync("Generated title"));
        Assert.Equal(manualTitle.Length == 0 ? null : manualTitle, SessionStore.ReadSummary(store.FilePath).CustomTitle);
    }

    [Fact]
    public async Task Close_CancelsPendingTitle_AndKeepsFirstMessageFallback()
    {
        var api = new RecordingApi { HoldTitle = true };
        var (sessions, catalog) = Create(api, "quick/small");
        await using var cleanup = sessions;
        var session = sessions.Create(_home, catalog.Get("primary/main"));
        session.Prompt("Original title");
        await api.TitleStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await sessions.CloseAsync(session.Id);
        await api.TitleCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var summary = SessionStore.ReadSummary(session.Store.FilePath);
        Assert.Null(summary.CustomTitle);
        Assert.Equal("Original title", SessionIndex.TitleOf(summary.Messages));
    }

    [Fact]
    public async Task FailedOrEmptyTitle_KeepsFallback_AndDisplayTextIsUsedInsteadOfExpandedCommands()
    {
        var api = new RecordingApi { TitleResponse = "" };
        var (sessions, catalog) = Create(api, null);
        await using var cleanup = sessions;
        var generator = new SessionTitleGenerator(new WireApiRegistry([api]), new RuntimeRegistry([]), catalog.GetApiKey, () => PromptCacheTtl.FiveMinutes);
        var user = UserMessage.FromText("Very long expanded skill body") with
        {
            Display = [new MessageSegment("command", Value: "review"), new MessageSegment("text", " Original question")],
        };
        Assert.Null(await generator.GenerateAsync(catalog.Get("primary/main"), _home, "s", user, TestContext.Current.CancellationToken));
        Assert.Equal("/review Original question", Assert.IsType<TextContent>(Assert.IsType<UserMessage>(Assert.Single(api.Requests).Context.Messages[0]).Content[0]).Text);

        api.TitleResponse = "Ignored partial title";
        api.FailTitle = true;
        Assert.Null(await generator.GenerateAsync(catalog.Get("primary/main"), _home, "s", user, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExternalSession_TitleCanFollowItsRuntime_InAnIsolatedReadOnlyContext()
    {
        var runtime = new TitleRuntime();
        var model = RuntimeModels.Create(runtime, "session-model");
        var generator = new SessionTitleGenerator(new WireApiRegistry([]), new RuntimeRegistry([runtime]), _ => null, () => PromptCacheTtl.FiveMinutes);
        Assert.Equal("External title", await generator.GenerateAsync(model, _home, "s", UserMessage.FromText("Original question"), TestContext.Current.CancellationToken));
        Assert.Equal("session-model", runtime.Request!.Options.Model.Id);
        Assert.True(runtime.Request.Options.Mode.ReadOnly);
        Assert.Null(runtime.Request.ResumeSessionId);
        Assert.Null(runtime.Request.McpBridge);
        Assert.Equal(ApprovalOutcome.Deny, runtime.Approval!.Outcome);
        Assert.Contains("Original question", runtime.Prompt);
    }

    private sealed class TitleRuntime : IAgentRuntime
    {
        public string Id => "title-cli";
        public string DisplayName => "Title CLI";
        public RuntimeDescriptor Descriptor => new(Id, DisplayName, RuntimeStatus.Ready);
        public event Action? DescriptorChanged { add { } remove { } }
        public RuntimeSessionRequest? Request { get; private set; }
        public ApprovalDecision? Approval { get; private set; }
        public string? Prompt { get; private set; }
        public Task<RuntimeDescriptor> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(Descriptor);
        public Task<IRuntimeSession> StartSessionAsync(RuntimeSessionRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult<IRuntimeSession>(new Session(this, request.Host));
        }

        private sealed class Session(TitleRuntime owner, IRuntimeHost host) : IRuntimeSession
        {
            public string? NativeSessionId => null;
            public async Task RunTurnAsync(UserMessage message, TurnOptions options, TurnWriter writer, CancellationToken cancellationToken)
            {
                owner.Prompt = string.Concat(message.Content.OfType<TextContent>().Select(t => t.Text));
                owner.Approval = await host.RequestApprovalAsync(new RuntimeApprovalRequest("tool", "shell", "{}", ApprovalKind.Command), cancellationToken);
                await writer.TextAsync("External title");
                await writer.CompleteAsync(StopReason.Stop);
            }
            public ValueTask<bool> SteerAsync(UserMessage message, CancellationToken cancellationToken) => ValueTask.FromResult(false);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed record Request(ModelInfo Model, LlmContext Context, StreamOptions Options);

    private sealed class RecordingApi : IWireApi
    {
        public string Api => KnownApis.OpenAIChatCompletions;
        public ConcurrentQueue<Request> Requests { get; } = new();
        public bool HoldTitle { get; init; }
        public bool FailTitle { get; set; }
        public string TitleResponse { get; set; } = "“模型配置”\nUnwanted explanation";
        public TaskCompletionSource TitleStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TitleCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AssistantMessageStream Stream(ModelInfo model, LlmContext context, StreamOptions options, CancellationToken cancellationToken)
        {
            Requests.Enqueue(new Request(model, context, options));
            var stream = new AssistantMessageStream();
            _ = ProduceAsync();
            return stream;

            async Task ProduceAsync()
            {
                var builder = new AssistantMessageBuilder(model, stream);
                try
                {
                    var isTitle = context.SystemPrompt == SessionTitleGenerator.Instructions;
                    if (isTitle)
                    {
                        TitleStarted.TrySetResult();
                        if (HoldTitle)
                        {
                            await Task.Delay(Timeout.Infinite, cancellationToken);
                        }
                    }
                    builder.Start();
                    var block = builder.BeginText();
                    builder.AppendDelta(block, isTitle ? TitleResponse : "Hello.");
                    if (isTitle && FailTitle)
                    {
                        builder.Fail(new IOException("Provider unavailable"), aborted: false);
                    }
                    else
                    {
                        builder.Finish();
                    }
                }
                catch (OperationCanceledException ex)
                {
                    builder.Fail(ex, aborted: true);
                    TitleCancelled.TrySetResult();
                }
            }
        }
    }

    private sealed class TitleClient : IAgentHostClient
    {
        public TaskCompletionSource<SessionTitleChanged> Title { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<long> Sequences { get; } = new();
        public void OnSessionEvent(SessionEventNotification notification)
        {
            Sequences.Enqueue(notification.Seq);
            if (notification.Event is SessionTitleChanged title)
            {
                Title.TrySetResult(title);
            }
        }
        public void OnModelsChanged(ModelsChangedNotification notification) { }
        public void OnMcpChanged(McpChangedNotification notification) { }
        public void OnAssetsChanged(AssetsChangedNotification notification) { }
        public Task<ToolApprovalResponse> RequestToolApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new ToolApprovalResponse(false));
        public Task<McpElicitResponse> RequestMcpElicitationAsync(McpElicitRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new McpElicitResponse("cancel"));
        public Task<McpOpenUrlResponse> OpenMcpUrlAsync(McpOpenUrlRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new McpOpenUrlResponse(false));
        public Task<PlanReviewResponse> ReviewPlanAsync(PlanReviewRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(PlanReviewResponse.Dismiss);

        public Task<UserInputResponse> RequestUserInputAsync(UserInputRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new UserInputResponse("declined", []));
    }
}
