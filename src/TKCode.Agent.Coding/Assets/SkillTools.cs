using System.Text;
using System.Text.Json;
using TKCode.Agent.Coding.Prompt;
using TKCode.Agent.Coding.Tools;

namespace TKCode.Agent.Coding.Assets;

/// <summary>
/// skills 的自动发现（docs/design/composer.md §10.1）：每批用户输入前取当前项目生效的 skill，
/// 有可发现的 skill（带描述）时提供 <see cref="SkillTool"/> 与系统提示的 skills 节；变化时由系统提示补丁告知模型。
/// </summary>
public sealed class SkillToolSource(AgentAssetStore store, ToolContext context, Func<bool>? enabled = null) : IDynamicToolSource
{
    /// <summary>系统提示里最多列出的 skill 数。</summary>
    public const int MaxListed = 100;

    public const string SectionName = "skills";

    public ValueTask<DynamicToolSet> RefreshAsync(bool firstTurn, CancellationToken cancellationToken)
    {
        if (enabled?.Invoke() == false)
        {
            return ValueTask.FromResult(DynamicToolSet.Empty);
        }
        var skills = store.Get(context.Cwd).Skills.Where(s => s.Description is not null).ToList();
        if (skills.Count == 0)
        {
            return ValueTask.FromResult(DynamicToolSet.Empty);
        }
        return ValueTask.FromResult(new DynamicToolSet([new SkillTool(store, context)], [Section(skills)]));
    }

    /// <summary>系统提示的 skills 节（docs/design/composer.md §18.6）。</summary>
    public static PromptSection Section(IReadOnlyList<AgentAsset> skills)
    {
        var sb = new StringBuilder("""
            Skills are packaged instructions for specific kinds of tasks, written by the user or for this project. Each entry below is a skill name followed by when to use it.

            - Before starting a task that clearly matches a skill's description, call the skill tool with its name and follow the instructions it returns. Do not load skills speculatively.
            - Paths in a skill are relative to the skill directory that the tool returns. Scripts there run with the bash tool and need the usual approvals.
            - If the user's request conflicts with a skill's instructions, follow the user.
            - When a message already contains a <skill> block, that skill is loaded; do not call the tool for it again.

            Available skills:
            """);
        foreach (var skill in skills.Take(MaxListed))
        {
            sb.Append("\n- ").Append(skill.Name).Append(": ").Append(OneLine(skill.Description!));
        }
        if (skills.Count > MaxListed)
        {
            sb.Append($"\n\n[{skills.Count - MaxListed} more skills are not listed.]");
        }
        return new PromptSection(SectionName, sb.ToString());
    }

    private static string OneLine(string text) => string.Join(' ', text.Split((char[])['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>读取一个 skill 的说明（SKILL.md 正文）与目录位置。只读、不涉及工作区外的写入，不需要审批。</summary>
public sealed class SkillTool(AgentAssetStore store, ToolContext context) : IAgentTool
{
    public const string ToolName = "skill";

    public string Name => ToolName;
    public string Label => "Tool.Skill";

    public string Description =>
        "Load a skill's instructions. Returns the content of its SKILL.md and the absolute path of its directory. " +
        "Call it before starting a task that matches a skill listed in the skills section, or when the user names a skill.";

    public string ParametersSchemaJson => """
        {"type":"object","properties":{
          "name":{"type":"string","description":"Skill name, as listed in the skills section."}},
         "required":["name"],"additionalProperties":false}
        """;

    public string PromptSnippet => "Load a skill's instructions";

    public ToolPermission DescribePermission(JsonElement arguments) => new(ToolAccess.None, Title: $"skill {arguments.String("name")}");

    public Task<AgentToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var name = invocation.Arguments.String("name")?.Trim().TrimStart('/') ?? "";
        var catalog = store.Get(context.Cwd);
        if (catalog.FindSkill(name) is not { } skill)
        {
            var known = string.Join(", ", catalog.Skills.Select(s => s.Name));
            return Task.FromResult(AgentToolResult.Error($"Unknown skill '{name}'. Available skills: {(known.Length == 0 ? "(none)" : known)}."));
        }
        if (AgentAssetStore.ReadBody(skill.Path) is not { } body)
        {
            return Task.FromResult(AgentToolResult.Error($"Cannot read {skill.Path}."));
        }
        return Task.FromResult(AgentToolResult.Text(SkillBlock.Render(skill, body)));
    }
}

/// <summary>skill 交给模型时的格式（工具结果与用户显式调用共用）。</summary>
public static class SkillBlock
{
    public static string Render(AgentAsset skill, string body) =>
        $"<skill name=\"{skill.Name}\" directory=\"{Path.GetDirectoryName(skill.Path)}\">\n{body.Trim()}\n</skill>";
}

/// <summary>多个动态工具来源合并为一个（MCP 与 skills）：各自刷新，工具与系统提示节按顺序拼接；一个出错不影响其他。</summary>
public sealed class CompositeToolSource(IReadOnlyList<IDynamicToolSource> sources) : IDynamicToolSource
{
    public async ValueTask<DynamicToolSet> RefreshAsync(bool firstTurn, CancellationToken cancellationToken)
    {
        var tools = new List<IAgentTool>();
        var sections = new List<PromptSection>();
        foreach (var source in sources)
        {
            DynamicToolSet set;
            try
            {
                set = await source.RefreshAsync(firstTurn, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                continue;
            }
            tools.AddRange(set.Tools);
            sections.AddRange(set.Sections);
        }
        return new DynamicToolSet(tools, sections);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var source in sources)
        {
            await source.DisposeAsync();
        }
    }
}
