using System.Text.Json;
using TKCode.Agent.Coding;
using TKCode.Agent.Coding.Assets;
using TKCode.Agent.Coding.Prompt;
using TKCode.Agent.Coding.Tools;
using TKCode.Agent.Coding.Workspace;
using TKCode.Agent.Compaction;
using TKCode.Ai.Messages;
using Xunit;

namespace TKCode.Agent.Tests;

/// <summary>
/// 上下文占用卡片的分类估算（docs/design/context-usage.md）：系统提示按节、工具按来源、会话中途的提示补丁按节归回，
/// 载入的 skill 正文计入技能；再按真实用量归一（差额计入其他）；缓存命中率按 token 加权覆盖整个会话。
/// </summary>
public sealed class ContextUsageTests
{
    private sealed class StubTool(string name, string description) : IAgentTool
    {
        public string Name => name;
        public string Label => name;
        public string Description => description;
        public string ParametersSchemaJson => """{"type":"object"}""";
        public ToolPermission DescribePermission(JsonElement arguments) => ToolPermission.NoAccess;
        public Task<AgentToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static long Tokens(string text) => ContextAccounting.EstimateText(text);
    private static long Tool(IAgentTool tool) => Tokens(tool.Name) + Tokens(tool.Description) + Tokens(tool.ParametersSchemaJson);

    private static AssistantMessage Reply(string text, Usage usage) =>
        new([new TextContent(text)]) { Api = "test", Provider = "test", Model = "test", Usage = usage, StopReason = StopReason.Stop };

    [Fact]
    public void Estimate_ClassifiesPromptSections_Tools_AndMessages()
    {
        var project = Directory.CreateTempSubdirectory("tkcode-context-usage-").FullName;
        using var store = new AgentAssetStore(Directory.CreateTempSubdirectory("tkcode-context-home-").FullName);
        var context = new ToolContext(new WorkspaceRoots(project), "context-usage");

        PromptSection identity = new("identity", new string('i', 400)), rules = new("rules", new string('r', 200)),
            agents = new("project_context", new string('p', 800)), skills = new(SkillToolSource.SectionName, new string('s', 120)),
            mcp = new("mcp", new string('m', 80));
        var read = new StubTool("read", new string('d', 600));
        var github = new StubTool("mcp__github__issues", new string('g', 300));
        var skill = new SkillTool(store, context);

        // 会话中途：skills 与 mcp 节变化、mode 节加入
        var patch = SystemPromptBuilder.Diff([identity, skills, mcp],
            [identity, skills with { Text = new string('S', 160) }, mcp with { Text = new string('M', 40) }, new("mode", new string('o', 60))])!;
        var skillBlock = "<skill name=\"pdf\" directory=\"/skills/pdf\">\n" + new string('k', 400) + "\n</skill>";
        var user = UserMessage.FromText("look at this " + skillBlock);
        var skillResult = new ToolResultMessage("call_1", SkillTool.ToolName, [new TextContent(new string('K', 200))]);
        var reply = Reply(new string('a', 100), new Usage(10, 5, 0, 0));
        var notes = UserMessage.FromText(new string('u', 40));

        var estimate = ContextUsage.Estimate([identity, rules, agents, skills, mcp], [read, github, skill],
            [user, skillResult, reply, new SystemMessage(patch), notes]);

        var parts = SystemPromptBuilder.PatchParts(patch).ToList();
        long Part(string name) => parts.Where(p => p.Name == name).Sum(p => Tokens(p.Text));
        Assert.Equal(["skills", "mcp", "mode"], parts.Select(p => p.Name));

        Assert.Equal(Tool(read), estimate.SystemTools);
        Assert.Equal(Tool(github) + Tokens(SystemPromptBuilder.RenderSection(mcp)) + Part("mcp"), estimate.McpTools);
        Assert.Equal(Tool(skill) + Tokens(SystemPromptBuilder.RenderSection(skills)) + Part("skills") + (skillBlock.Length + 3) / 4 +
                     ContextAccounting.Estimate(skillResult), estimate.Skills);
        Assert.Equal(new[] { identity, rules, agents }.Sum(s => Tokens(SystemPromptBuilder.RenderSection(s))) + Part("mode"), estimate.SystemPrompt);
        Assert.Equal(("look at this ".Length + 3) / 4 + ContextAccounting.Estimate(reply) + ContextAccounting.Estimate(notes), estimate.Messages);
        Assert.Equal(0, estimate.Other);
    }

    [Fact]
    public void Measure_NormalizesToTheLastRealUsage()
    {
        PromptSection prompt = new("identity", new string('i', 4000)); // 1000
        var messages = new List<Message> { UserMessage.FromText(new string('u', 2000)) }; // 500

        // 还没有真实用量：原样返回估算
        var unmeasured = ContextUsage.Measure([prompt], [], messages);
        Assert.Equal((1000, 500, 0), (unmeasured.SystemPrompt, unmeasured.Messages, unmeasured.Other));

        // 真实用量比估算多：差额计入其他，总数等于真实用量
        messages.Add(Reply(new string('a', 400), new Usage(1900, 100, 1000, 0))); // 估算 100，真实 3000
        var more = ContextUsage.Measure([prompt], [], messages);
        Assert.Equal((1000, 600, 1400), (more.SystemPrompt, more.Messages, more.Other));
        Assert.Equal(3000, more.Total);

        // 真实用量比估算少：按比例缩小，不出现其他
        messages[^1] = Reply(new string('a', 400), new Usage(700, 100, 0, 0)); // 真实 800，估算 1600
        var less = ContextUsage.Measure([prompt], [], messages);
        Assert.Equal((500, 300, 0), (less.SystemPrompt, less.Messages, less.Other));

        // 出错的回复不算真实用量
        messages[^1] = Reply("", new Usage(700, 100, 0, 0)) with { StopReason = StopReason.Error };
        Assert.Equal(0, ContextUsage.Measure([prompt], [], messages).Other);
        Assert.Equal(1500, ContextUsage.Measure([prompt], [], messages).Total);
    }

    [Fact]
    public void CacheTotals_WeightByTokens_AndAreNullWithoutAnyCacheActivity()
    {
        var first = CacheTotals.Of([Reply("a", new Usage(100, 10, 0, 900))]); // 写入缓存，读取 0
        var later = CacheTotals.Of([Reply("b", new Usage(50, 10, 950, 0)), Reply("c", new Usage(100, 10, 900, 0))]);
        var all = first + later;
        Assert.Equal(1850.0 / 3000, all.HitRate!.Value, 6);
        Assert.Equal(0, first.HitRate);
        Assert.Null(CacheTotals.Of([Reply("d", new Usage(100, 10, 0, 0)), UserMessage.FromText("x")]).HitRate);
        Assert.Null(CacheTotals.Of([]).HitRate);
    }

    [Fact]
    public void PatchParts_SplitsADiffBackIntoSections_AndKeepsPlainTextUnnamed()
    {
        var patch = SystemPromptBuilder.Diff([new("identity", "x"), new("cwd", "/a"), new("mcp", "one")],
            [new("identity", "x"), new("cwd", "/b"), new("skills", "list")])!;
        var parts = SystemPromptBuilder.PatchParts(patch).ToList();
        Assert.Equal(["cwd", "skills", "mcp"], parts.Select(p => p.Name));
        Assert.Equal(patch, string.Concat(parts.Select(p => p.Text)));
        Assert.Contains("<skills>\nlist\n</skills>", parts[1].Text);

        Assert.Equal([(null, "just text")], SystemPromptBuilder.PatchParts("just text").Select(p => (p.Name, p.Text)));
    }
}
