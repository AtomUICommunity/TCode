using TCode.Agent;
using TCode.Agent.Coding;
using TCode.Agent.Coding.Sessions;
using TCode.Ai.Messages;
using TCode.Ai.Models;
using Xunit;

namespace TCode.Runtimes.Tests;

public sealed class ExternalAgentTests
{
    internal static readonly ModelInfo Model = new() { Id = "fake-1", Name = "Fake 1", Provider = "fake-cli", Api = "fake-cli" };

    [Fact]
    public async Task Turn_ProducesOrderedTranscript_WithToolPairsAndUsage()
    {
        var runtime = new ScriptedRuntime(async (msg, writer, host, ct) =>
        {
            var thinking = await writer.BeginBlockAsync(BlockKind.Thinking);
            await writer.AppendAsync(thinking, "plan");
            var text = await writer.BeginBlockAsync(BlockKind.Text);
            await writer.AppendAsync(text, "Run");
            await writer.EndBlockAsync(text, "Running ls");   // 权威全文：补发剩余部分
            var decision = await host.RequestApprovalAsync(new RuntimeApprovalRequest("call-1", "shell", """{"cmd":"ls"}""", ApprovalKind.Command), ct);
            Assert.True(decision.IsAllowed);
            await writer.ToolCallAsync("call-1", "shell", """{"cmd":"ls"}""");
            await writer.ToolStartedAsync("call-1", "shell", """{"cmd":"ls"}""");
            await writer.ToolCompletedAsync("call-1", "shell", "a.txt", isError: false);
            // 未事先声明的工具：自动补一条助手消息
            await writer.ToolCompletedAsync("call-2", "read", "content", isError: true, """{"path":"a.txt"}""");
            await writer.TextAsync("Done.");
            writer.SetUsage(new Usage(10, 5, 2, 0));
        });
        var agent = new ExternalAgent(runtime, Model, "/tmp") { ApprovalHandler = (_, _) => ValueTask.FromResult(ApprovalDecision.AllowOnce) };
        var events = Record(agent);

        await agent.PromptAsync(UserMessage.FromText("hi"));

        var messages = agent.Messages;
        Assert.Collection(messages,
            m => Assert.IsType<UserMessage>(m),
            m =>
            {
                var a = Assert.IsType<AssistantMessage>(m);
                Assert.Equal(StopReason.ToolUse, a.StopReason);
                Assert.Equal("Running ls", Assert.IsType<TextContent>(a.Content[1]).Text);
                Assert.Equal("call-1", Assert.IsType<ToolCallContent>(a.Content[2]).Id);
                Assert.Equal(Usage.Empty, a.Usage);
            },
            m => Assert.Equal("call-1", Assert.IsType<ToolResultMessage>(m).ToolCallId),
            m => Assert.Equal("call-2", Assert.IsType<ToolCallContent>(Assert.Single(Assert.IsType<AssistantMessage>(m).Content)).Id),
            m => Assert.True(Assert.IsType<ToolResultMessage>(m).IsError),
            m =>
            {
                var a = Assert.IsType<AssistantMessage>(m);
                Assert.Equal(StopReason.Stop, a.StopReason);
                Assert.Equal(new Usage(10, 5, 2, 0), a.Usage);
                Assert.Equal("fake-cli", a.Provider);
            });
        Assert.IsType<AgentStartEvent>(events[0]);
        Assert.IsType<AgentEndEvent>(events[^1]);
        Assert.Equal(1, events.Count(e => e is ToolExecutionStartEvent { ToolCallId: "call-1" }));
        Assert.Equal(1, events.Count(e => e is ToolExecutionEndEvent { ToolCallId: "call-2" }));
        // 文本增量：先 "Run"，结束时补发 " ls"
        Assert.Equal("Running ls", string.Concat(events.OfType<MessageUpdateEvent>().Select(u => u.Event)
            .OfType<TCode.Ai.Streaming.TextDeltaEvent>().Where(d => d.ContentIndex == 1).Select(d => d.Delta)));
        Assert.False(agent.IsRunning);
    }

    [Fact]
    public async Task ApprovalWithoutClient_IsDenied_AndFailureBecomesErrorMessage()
    {
        ApprovalDecision? seen = null;
        var runtime = new ScriptedRuntime(async (msg, writer, host, ct) =>
        {
            seen = await host.RequestApprovalAsync(new RuntimeApprovalRequest("c", "shell", "{}", ApprovalKind.Command), ct);
            throw new InvalidOperationException("cli crashed");
        });
        var agent = new ExternalAgent(runtime, Model, "/tmp");
        await agent.PromptAsync(UserMessage.FromText("hi"));

        Assert.Equal(ApprovalOutcome.Deny, seen!.Outcome);
        var error = Assert.IsType<AssistantMessage>(agent.Messages[^1]);
        Assert.Equal(StopReason.Error, error.StopReason);
        Assert.Equal("cli crashed", error.ErrorMessage);
        // 出错后会话被丢弃，下一次提问重新启动并恢复
        await agent.PromptAsync(UserMessage.FromText("again"));
        Assert.Equal(2, runtime.Starts);
    }

    [Theory]
    [InlineData(TCode.Agent.PermissionMode.Ask, ApprovalOutcome.Deny)]
    [InlineData(TCode.Agent.PermissionMode.AutoApprove, ApprovalOutcome.AllowOnce)]
    [InlineData(TCode.Agent.PermissionMode.FullAccess, ApprovalOutcome.AllowOnce)]
    public async Task PermissionMode_DecidesWhetherApprovalsAreAsked(TCode.Agent.PermissionMode mode, ApprovalOutcome expected)
    {
        ApprovalDecision? seen = null;
        var runtime = new ScriptedRuntime(async (msg, writer, host, ct) =>
            seen = await host.RequestApprovalAsync(new RuntimeApprovalRequest("c", "shell", "{}", ApprovalKind.Command), ct));
        var agent = new ExternalAgent(runtime, Model, "/tmp")
        {
            PermissionMode = mode,
            ApprovalHandler = (_, _) => ValueTask.FromResult(ApprovalDecision.Deny()),
        };
        await agent.PromptAsync(UserMessage.FromText("hi"));
        Assert.Equal(expected, seen!.Outcome);
        Assert.Equal(mode, runtime.LastRequest!.Options.Permission);
    }

    [Fact]
    public async Task Abort_CancelsTurn_AndRecordsAbortedMessage()
    {
        var started = new TaskCompletionSource();
        var runtime = new ScriptedRuntime(async (msg, writer, host, ct) =>
        {
            await writer.TextAsync("partial");
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        });
        var agent = new ExternalAgent(runtime, Model, "/tmp");
        var run = agent.PromptAsync(UserMessage.FromText("hi"));
        await started.Task;
        agent.Abort();
        await run;

        var last = Assert.IsType<AssistantMessage>(agent.Messages[^1]);
        Assert.Equal(StopReason.Aborted, last.StopReason);
        Assert.Equal("partial", Assert.IsType<TextContent>(Assert.Single(last.Content)).Text);
    }

    [Fact]
    public async Task FollowUps_RunAsSeparateTurns_AndNativeIdIsPersisted()
    {
        var gate = new TaskCompletionSource();
        var runtime = new ScriptedRuntime(async (msg, writer, host, ct) =>
        {
            host.OnNativeSessionId("native-42");
            if (TextOf(msg) == "first")
            {
                await gate.Task;
            }
            await writer.TextAsync("re:" + TextOf(msg));
        });
        var root = Directory.CreateTempSubdirectory("tcode-ext-").FullName;
        await using var session = AgentSession.Create(root, _ => new ExternalAgent(runtime, Model, root), Path.Combine(root, "sessions"));

        Assert.Equal(PromptDisposition.Started, session.Prompt("first"));
        // 不支持 steering 的运行时：steer 退化为排队
        Assert.Equal(PromptDisposition.Queued, session.Prompt("second", StreamingBehavior.Steer));
        gate.SetResult();
        await WaitIdleAsync(session.Agent);

        Assert.Equal(["first", "re:first", "second", "re:second"],
            session.Agent.Messages.Select(m => m switch { UserMessage u => TextOf(u), AssistantMessage a => TextOf(a.Content), _ => "?" }));
        var snapshot = SessionStore.Read(session.Store.FilePath);
        Assert.Equal("fake-cli", snapshot.Runtime);
        Assert.Equal("native-42", snapshot.NativeSessionId);
        Assert.Equal(4, snapshot.Messages.Count);
    }

    [Fact]
    public async Task ResumedSession_PassesNativeIdToRuntime()
    {
        var runtime = new ScriptedRuntime((_, writer, _, _) => writer.TextAsync("ok").AsTask());
        var agent = new ExternalAgent(runtime, Model, "/tmp", resumeSessionId: "native-7");
        var changed = 0;
        agent.NativeSessionIdChanged += _ => changed++;
        await agent.PromptAsync(UserMessage.FromText("hi"));
        Assert.Equal("native-7", runtime.LastRequest!.ResumeSessionId);
        Assert.Equal(0, changed);
    }

    private static async Task WaitIdleAsync(IAgentLoop agent)
    {
        for (var i = 0; i < 200 && agent.IsRunning; i++)
        {
            await agent.WaitForIdleAsync();
        }
    }

    private static List<AgentEvent> Record(IAgentLoop agent)
    {
        var events = new List<AgentEvent>();
        agent.Subscribe((e, _) =>
        {
            lock (events)
            {
                events.Add(e);
            }
            return ValueTask.CompletedTask;
        });
        return events;
    }

    internal static string TextOf(UserMessage m) => TextOf(m.Content);
    internal static string TextOf(IReadOnlyList<ContentBlock> content) => string.Concat(content.OfType<TextContent>().Select(t => t.Text));
}

/// <summary>按脚本运行每一轮的假运行时。</summary>
internal sealed class ScriptedRuntime(Func<UserMessage, TurnWriter, IRuntimeHost, CancellationToken, Task> turn) : IAgentRuntime
{
    public int Starts;
    public RuntimeSessionRequest? LastRequest;

    public string Id => "fake-cli";
    public string DisplayName => "Fake";
    public RuntimeDescriptor Descriptor { get; } = new("fake-cli", "Fake", RuntimeStatus.Ready);
    public event Action? DescriptorChanged { add { } remove { } }

    public Task<RuntimeDescriptor> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(Descriptor);

    public Task<IRuntimeSession> StartSessionAsync(RuntimeSessionRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Starts);
        LastRequest = request;
        return Task.FromResult<IRuntimeSession>(new Session(this, request.Host, request.ResumeSessionId));
    }

    private sealed class Session(ScriptedRuntime owner, IRuntimeHost host, string? nativeId) : IRuntimeSession
    {
        public string? NativeSessionId => nativeId;

        public Task RunTurnAsync(UserMessage message, TurnOptions options, TurnWriter writer, CancellationToken cancellationToken) =>
            owner._turn(message, writer, host, cancellationToken);

        public ValueTask<bool> SteerAsync(UserMessage message, CancellationToken cancellationToken) => ValueTask.FromResult(false);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private readonly Func<UserMessage, TurnWriter, IRuntimeHost, CancellationToken, Task> _turn = turn;
}
