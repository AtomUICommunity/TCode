using TCode.Agent;
using TCode.Agent.Coding;
using TCode.Agent.Coding.Sessions;
using TCode.Ai;
using TCode.Ai.Fake;
using TCode.Ai.Messages;
using TCode.Ai.Models;
using Xunit;

namespace TCode.Agent.Tests;

public sealed class AgentLoopTests
{
    private static WireApiRegistry Apis() => new([new FakeWireApi(TimeSpan.Zero)]);

    [Fact]
    public async Task ToolRoundTrip_AppendsAssistantToolResultAndFinalAnswer()
    {
        var root = Directory.CreateTempSubdirectory("tcode-test-").FullName;
        await using var session = AgentSession.Create(Apis(), FakeWireApi.Models[0], root, Path.Combine(root, "sessions"));
        var events = new List<AgentEvent>();
        session.Agent.Subscribe((e, _) => { lock (events) { events.Add(e); } return ValueTask.CompletedTask; });

        session.Prompt("看看目录里有哪些文件");
        await session.Agent.WaitForIdleAsync();

        var messages = session.Agent.Messages;
        Assert.Collection(messages,
            m => Assert.IsType<UserMessage>(m),
            m => Assert.Equal(StopReason.ToolUse, Assert.IsType<AssistantMessage>(m).StopReason),
            m => Assert.Equal("ls", Assert.IsType<ToolResultMessage>(m).ToolName),
            m => Assert.Equal(StopReason.Stop, Assert.IsType<AssistantMessage>(m).StopReason));
        Assert.Contains(events, e => e is ToolExecutionEndEvent { Result.IsError: false });
        Assert.IsType<AgentEndEvent>(events[^1]);

        // 会话文件可读回同样的分支
        var (_, restored) = SessionStore.Open(session.Store.FilePath);
        Assert.Equal(messages.Count, restored.Count);
        Assert.Equal(messages.Select(m => m.GetType()), restored.Select(m => m.GetType()));
    }

    [Fact]
    public async Task PendingModelAndOptions_AreWrittenWithFirstMessage_AndReadBack()
    {
        var root = Directory.CreateTempSubdirectory("tcode-test-").FullName;
        await using var session = AgentSession.Create(Apis(), FakeWireApi.Models[0], root, Path.Combine(root, "sessions"));
        await session.Store.AppendModelChangeAsync("fake", "fake-sonnet");
        await session.Store.AppendOptionsChangeAsync(ThinkingLevel.Low, true, 128_000);
        // 没有消息前不落盘
        Assert.False(File.Exists(session.Store.FilePath));

        session.Prompt("hello");
        await session.Agent.WaitForIdleAsync();
        await session.Store.AppendOptionsChangeAsync(ThinkingLevel.High, null, null);

        var snapshot = SessionStore.Read(session.Store.FilePath);
        Assert.Equal("fake-sonnet", snapshot.ModelId);
        Assert.Equal(ThinkingLevel.High, snapshot.ThinkingLevel);
        Assert.Equal(true, snapshot.FastMode);
        Assert.Equal(128_000, snapshot.ContextWindow);
        Assert.Equal(session.Agent.Messages.Count, snapshot.Messages.Count);
    }

    [Fact]
    public async Task Abort_EndsRunWithAbortedMessage()
    {
        var root = Directory.CreateTempSubdirectory("tcode-test-").FullName;
        var agent = new Agent(new WireApiRegistry([new FakeWireApi(TimeSpan.FromMilliseconds(50))]), FakeWireApi.Models[0], "sys", []);
        var run = agent.PromptAsync(UserMessage.FromText("hello"));
        await Task.Delay(150);
        agent.Abort();
        await run;

        var last = Assert.IsType<AssistantMessage>(agent.Messages[^1]);
        Assert.Equal(StopReason.Aborted, last.StopReason);
        Assert.False(agent.IsRunning);
    }
}
