using System.Text;
using TKCode.Ai.Messages;
using TKCode.Ai.Models;

namespace TKCode.Agent.Compaction;

/// <summary>上下文占用级别（docs/design/compaction.md §4）。</summary>
public enum ContextLevel
{
    Ok,
    Warn,
    Compact,
    Blocked,
}

/// <summary>压缩阈值（token）。</summary>
public sealed record ContextThresholds(long AutoCompactAt, long WarnAt, long BlockAt)
{
    public ContextLevel LevelOf(long tokens) =>
        tokens >= BlockAt ? ContextLevel.Blocked
        : tokens >= AutoCompactAt ? ContextLevel.Compact
        : tokens >= WarnAt ? ContextLevel.Warn
        : ContextLevel.Ok;
}

/// <summary>
/// 上下文计量（docs/design/compaction.md §3、§4）：最后一次真实 usage + 之后新增内容的估算（约 4 字符 / token，图片按 2000）。
/// </summary>
public static class ContextAccounting
{
    /// <summary>为输出和摘要回复预留的上限；也是摘要请求的 max_tokens。</summary>
    public const int SummaryMaxTokens = 20_000;
    public const int AutoCompactBuffer = 13_000;
    public const int WarnBuffer = 20_000;
    public const int BlockBuffer = 3_000;
    public const int ImageTokens = 2_000;

    /// <param name="budget">用户选择的上下文预算（≤ 模型窗口）；0 表示用模型窗口。</param>
    public static ContextThresholds Thresholds(int budget, ModelInfo model, int? maxTokens = null)
    {
        var window = model.ContextWindow;
        budget = budget <= 0 ? window : Math.Min(budget, window);
        long reserve = Math.Min(maxTokens ?? model.MaxTokens, SummaryMaxTokens);
        var effective = budget - reserve;
        // 小窗口模型直接相减会得到过低甚至为负的阈值：至少用有效窗口的 60%（TKCode 补充，§4）
        var autoCompactAt = Math.Max(effective - AutoCompactBuffer, (long)(effective * 0.6));
        var blockAt = window - reserve - BlockBuffer;
        return new ContextThresholds(autoCompactAt, Math.Max(0, autoCompactAt - WarnBuffer), Math.Max(autoCompactAt, blockAt));
    }

    /// <summary>当前上下文的 token 数：最后一次成功请求的 usage，加上它之后的消息估算；没有 usage 时整段估算。</summary>
    public static long ContextTokens(IReadOnlyList<Message> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is AssistantMessage { StopReason: not (StopReason.Error or StopReason.Aborted) } assistant && UsageTokens(assistant.Usage) > 0)
            {
                var tokens = UsageTokens(assistant.Usage);
                for (var j = i + 1; j < messages.Count; j++)
                {
                    tokens += Estimate(messages[j]);
                }
                return tokens;
            }
        }
        return Estimate(messages);
    }

    public static long UsageTokens(Usage usage) => usage.Input + usage.CacheRead + usage.CacheWrite + usage.Output;

    public static long Estimate(IEnumerable<Message> messages) => messages.Sum(Estimate);

    public static long Estimate(Message message) => message switch
    {
        SystemMessage s => EstimateText(s.Text),
        UserMessage u => Estimate(u.Content),
        AssistantMessage a => Estimate(a.Content),
        ToolResultMessage r => Estimate(r.Content) + EstimateText(r.ToolName),
        _ => 0,
    };

    public static long Estimate(IEnumerable<ContentBlock> blocks) => blocks.Sum(block => block switch
    {
        TextContent t => EstimateText(t.Text),
        ThinkingContent t => EstimateText(t.Thinking),
        ToolCallContent c => EstimateText(c.Name) + EstimateText(c.ArgumentsJson),
        ImageContent => ImageTokens,
        _ => 0L,
    });

    public static long EstimateText(string? text) => string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;

    /// <summary>估算文本截断到约 <paramref name="tokens"/> 个 token 的长度（字符）。</summary>
    public static int CharsFor(long tokens) => (int)Math.Min(int.MaxValue, tokens * 4);

    /// <summary>纯文本内容（用于序列化回退摘要、统计等）。</summary>
    public static string TextOf(IEnumerable<ContentBlock> blocks)
    {
        var sb = new StringBuilder();
        foreach (var block in blocks)
        {
            if (block is TextContent t)
            {
                sb.Append(t.Text);
            }
            else if (block is ImageContent)
            {
                sb.Append("[image]");
            }
        }
        return sb.ToString();
    }
}
