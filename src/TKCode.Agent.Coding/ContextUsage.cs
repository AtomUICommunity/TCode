using TKCode.Agent.Coding.Assets;
using TKCode.Agent.Coding.Prompt;
using TKCode.Agent.Compaction;
using TKCode.Ai.Messages;

namespace TKCode.Agent.Coding;

/// <summary>上下文按来源分类的 token 数（docs/design/context-usage.md）。</summary>
public sealed record ContextBreakdown(long SystemPrompt, long SystemTools, long McpTools, long Skills, long Messages, long Other)
{
    public long Total => SystemPrompt + SystemTools + McpTools + Skills + Messages + Other;
}

/// <param name="Tokens">当前上下文的 token 数（最近一次真实用量加之后新增内容的估算）。</param>
/// <param name="Breakdown">内置 Agent 的分类估算，各类之和等于 <paramref name="Tokens"/>；外部运行时为 null。</param>
/// <param name="Cache">整个会话（含已压缩掉的消息）的提示缓存累计。</param>
public sealed record ContextUsageReport(long Tokens, ContextBreakdown? Breakdown, CacheTotals Cache);

/// <summary>提示缓存读取、写入与全部输入（未缓存 + 读取 + 写入）的累计，命中率按 token 加权。</summary>
public readonly record struct CacheTotals(long Read, long Written, long Input)
{
    /// <summary>没有任何缓存读写（厂商不报告或没有缓存）时为 null。</summary>
    public double? HitRate => Input > 0 && Read + Written > 0 ? (double)Read / Input : null;

    public static CacheTotals Of(IEnumerable<Message> messages)
    {
        long read = 0, written = 0, input = 0;
        foreach (var message in messages)
        {
            if (message is AssistantMessage { Usage: var usage })
            {
                read += usage.CacheRead;
                written += usage.CacheWrite;
                input += usage.Input + usage.CacheRead + usage.CacheWrite;
            }
        }
        return new CacheTotals(read, written, input);
    }

    public static CacheTotals operator +(CacheTotals a, CacheTotals b) => new(a.Read + b.Read, a.Written + b.Written, a.Input + b.Input);
}

/// <summary>
/// 内置 Agent 的上下文分类估算（docs/design/context-usage.md）：系统提示按节、工具定义按来源、消息逐条，约 4 字符 / token；
/// 再按最近一次真实用量归一——估算偏少时差额计入“其他”（工具协议开销、格式、分词差异），偏多时按比例缩小。
/// </summary>
public static class ContextUsage
{
    // 与 TKCode.Mcp 的 McpManager（"mcp" 节）和 McpToolNames.Prefix 一致；这里不能引用 TKCode.Mcp
    private const string McpSection = "mcp";
    private const string McpToolPrefix = "mcp__";

    private const string SkillBlockStart = "<skill name=\"";
    private const string SkillBlockEnd = "</skill>";

    /// <summary>估算并按最近一次真实用量归一；还没有真实用量时返回估算值。</summary>
    public static ContextBreakdown Measure(IReadOnlyList<PromptSection> prompt, IReadOnlyList<IAgentTool> tools, IReadOnlyList<Message> messages)
    {
        var estimate = Estimate(prompt, tools, messages);
        // 真实用量覆盖整个请求（系统提示、工具定义与消息）；没有时 ContextTokens 只估算了消息，不能用来归一
        var measured = messages.Any(m => m is AssistantMessage { StopReason: not (StopReason.Error or StopReason.Aborted) } a &&
                                         ContextAccounting.UsageTokens(a.Usage) > 0);
        return measured ? Normalize(estimate, ContextAccounting.ContextTokens(messages)) : estimate;
    }

    /// <param name="prompt">发给模型的开头系统提示的各节。</param>
    /// <param name="tools">当前工具集。</param>
    /// <param name="messages">当前上下文中的消息（压缩后的部分）。</param>
    public static ContextBreakdown Estimate(IReadOnlyList<PromptSection> prompt, IReadOnlyList<IAgentTool> tools, IReadOnlyList<Message> messages)
    {
        long systemPrompt = 0, systemTools = 0, mcpTools = 0, skills = 0, conversation = 0;

        void AddSection(string? name, long tokens)
        {
            switch (name)
            {
                case SkillToolSource.SectionName:
                    skills += tokens;
                    break;
                case McpSection:
                    mcpTools += tokens;
                    break;
                default:
                    systemPrompt += tokens;
                    break;
            }
        }

        foreach (var section in prompt)
        {
            AddSection(section.Name, ContextAccounting.EstimateText(SystemPromptBuilder.RenderSection(section)));
        }
        foreach (var tool in tools)
        {
            var tokens = ContextAccounting.EstimateText(tool.Name) + ContextAccounting.EstimateText(tool.Description) +
                         ContextAccounting.EstimateText(tool.ParametersSchemaJson);
            if (tool is SkillTool)
            {
                skills += tokens;
            }
            else if (tool.Name.StartsWith(McpToolPrefix, StringComparison.Ordinal))
            {
                mcpTools += tokens;
            }
            else
            {
                systemTools += tokens;
            }
        }
        foreach (var message in messages)
        {
            switch (message)
            {
                // 会话中途的系统提示变化（补丁）按节归回各类
                case SystemMessage system:
                    foreach (var (name, text) in SystemPromptBuilder.PatchParts(system.Text))
                    {
                        AddSection(name, ContextAccounting.EstimateText(text));
                    }
                    break;
                // 载入的 skill 正文：skill 工具的结果，或用户显式调用时附在消息里的 <skill> 块
                case ToolResultMessage { ToolName: SkillTool.ToolName } result:
                    skills += ContextAccounting.Estimate(result);
                    break;
                case UserMessage user:
                    var (skillTokens, rest) = SplitSkillBlocks(user);
                    skills += skillTokens;
                    conversation += rest;
                    break;
                default:
                    conversation += ContextAccounting.Estimate(message);
                    break;
            }
        }
        return new ContextBreakdown(systemPrompt, systemTools, mcpTools, skills, conversation, 0);
    }

    /// <summary>
    /// 按真实用量 <paramref name="actual"/> 归一：估算少于真实值时差额计入 Other，多于真实值时各类按比例缩小。
    /// <paramref name="actual"/> 不大于 0（还没有真实用量）时原样返回。
    /// </summary>
    public static ContextBreakdown Normalize(ContextBreakdown estimate, long actual)
    {
        var total = estimate.Total;
        if (actual <= 0 || total <= 0)
        {
            return estimate;
        }
        if (actual >= total)
        {
            return estimate with { Other = estimate.Other + actual - total };
        }
        var scale = (double)actual / total;
        long Scale(long value) => (long)Math.Round(value * scale);
        return new ContextBreakdown(Scale(estimate.SystemPrompt), Scale(estimate.SystemTools), Scale(estimate.McpTools),
            Scale(estimate.Skills), Scale(estimate.Messages), Scale(estimate.Other));
    }

    private static (long Skills, long Other) SplitSkillBlocks(UserMessage message)
    {
        long skills = 0, rest = 0;
        foreach (var block in message.Content)
        {
            if (block is not TextContent { Text: var text } || !text.Contains(SkillBlockStart, StringComparison.Ordinal))
            {
                rest += ContextAccounting.Estimate([block]);
                continue;
            }
            var skillChars = 0;
            var index = 0;
            while ((index = text.IndexOf(SkillBlockStart, index, StringComparison.Ordinal)) >= 0)
            {
                var end = text.IndexOf(SkillBlockEnd, index, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + SkillBlockEnd.Length;
                skillChars += end - index;
                index = end;
            }
            skills += (skillChars + 3) / 4;
            rest += (text.Length - skillChars + 3) / 4;
        }
        return (skills, rest);
    }
}
