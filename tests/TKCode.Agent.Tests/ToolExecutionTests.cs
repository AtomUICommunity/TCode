using System.Text.Json;
using TKCode.Ai;
using TKCode.Ai.Messages;
using Xunit;

namespace TKCode.Agent.Tests;

public sealed class ToolExecutionTests
{
    /// <summary>记录并发度的测试工具：执行期间计数，结束时递减。</summary>
    private sealed class ProbeTool(string name, ToolConcurrency concurrency, Probe probe) : IAgentTool
    {
        public string Name => name;
        public string Label => name;
        public string Description => name;
        public string ParametersSchemaJson => """{"type":"object"}""";
        public ToolConcurrency Concurrency => concurrency;
        public ToolPermission DescribePermission(JsonElement arguments) => ToolPermission.NoAccess;

        public async Task<AgentToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref probe.Running);
            lock (probe)
            {
                probe.MaxRunning = Math.Max(probe.MaxRunning, now);
            }
            for (var i = 0; i < 5; i++)
            {
                invocation.Progress?.Report(AgentToolResult.Text($"{name}:{i}"));
            }
            await Task.Delay(80, cancellationToken);
            Interlocked.Decrement(ref probe.Running);
            return AgentToolResult.Text("ok") with { Details = """{"kind":"probe"}""" };
        }
    }

    private sealed class Probe
    {
        public int Running;
        public int MaxRunning;
    }

    private static async Task<(Agent Agent, List<AgentEvent> Events)> RunAsync(params IAgentTool[] tools)
    {
        var api = new ScriptedWireApi(ScriptedWireApi.Turn.Tools(tools.Select(t => (t.Name, "{}")).ToArray()));
        var agent = new Agent(new WireApiRegistry([api]), ScriptedWireApi.Model, "sys", tools);
        var events = new List<AgentEvent>();
        agent.Subscribe((e, _) =>
        {
            lock (events)
            {
                events.Add(e);
            }
            return ValueTask.CompletedTask;
        });
        await agent.PromptAsync(UserMessage.FromText("go"));
        return (agent, events);
    }

    [Fact]
    public async Task ParallelTools_RunConcurrently()
    {
        var probe = new Probe();
        await RunAsync(new ProbeTool("a", ToolConcurrency.Parallel, probe), new ProbeTool("b", ToolConcurrency.Parallel, probe));
        Assert.Equal(2, probe.MaxRunning);
    }

    [Fact]
    public async Task ExclusiveTool_SerializesWholeBatch()
    {
        var probe = new Probe();
        var (agent, _) = await RunAsync(new ProbeTool("a", ToolConcurrency.Parallel, probe), new ProbeTool("b", ToolConcurrency.Exclusive, probe));
        Assert.Equal(1, probe.MaxRunning);
        // 结果仍按源顺序追加
        Assert.Equal(["a", "b"], agent.Messages.OfType<ToolResultMessage>().Select(m => m.ToolName));
    }

    [Fact]
    public async Task Progress_IsDeliveredInOrderBeforeEnd_AndDetailsArePersistedOnResult()
    {
        var probe = new Probe();
        var (agent, events) = await RunAsync(new ProbeTool("a", ToolConcurrency.Parallel, probe));

        var forTool = events.Where(e => e is ToolExecutionUpdateEvent or ToolExecutionEndEvent).ToList();
        Assert.IsType<ToolExecutionEndEvent>(forTool[^1]);
        var updates = forTool.OfType<ToolExecutionUpdateEvent>()
            .Select(u => ((TextContent)u.Partial.Content[0]).Text).ToList();
        Assert.Equal(["a:0", "a:1", "a:2", "a:3", "a:4"], updates);

        var result = Assert.Single(agent.Messages.OfType<ToolResultMessage>());
        Assert.Equal("""{"kind":"probe"}""", result.Details);
    }

    [Fact]
    public async Task ContextUpdates_AreAppendedBeforeEachPromptBatch()
    {
        var api = new ScriptedWireApi(ScriptedWireApi.Turn.Reply("one"), ScriptedWireApi.Turn.Reply("two"));
        var agent = new Agent(new WireApiRegistry([api]), ScriptedWireApi.Model, "sys", []);
        var calls = 0;
        agent.ContextUpdates = _ => ValueTask.FromResult<IReadOnlyList<Message>>(++calls == 1 ? [] : [new SystemMessage("patch")]);

        await agent.PromptAsync(UserMessage.FromText("first"));
        await agent.PromptAsync(UserMessage.FromText("second"));

        Assert.Collection(agent.Messages,
            m => Assert.IsType<UserMessage>(m),
            m => Assert.IsType<AssistantMessage>(m),
            m => Assert.Equal("patch", Assert.IsType<SystemMessage>(m).Text),
            m => Assert.IsType<UserMessage>(m),
            m => Assert.IsType<AssistantMessage>(m));
        Assert.Equal("sys", api.Contexts[^1].SystemPrompt);
    }
}
