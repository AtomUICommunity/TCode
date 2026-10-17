using System.Text.RegularExpressions;

namespace TKCode.Ai.Messages;

/// <summary>
/// 识别“上下文超窗”错误（docs/design/compaction.md §9）。各厂商没有统一的错误码，按错误文案匹配；
/// 能解析出“实际 / 上限”时给出超出的 token 数，供压缩重试决定丢弃多少历史。
/// </summary>
public static partial class ContextOverflow
{
    [GeneratedRegex(
        @"prompt is too long|input is too long|exceeds? the (model's )?context window|context window exceeds limit|" +
        @"maximum context length is \d+|context[_ ]length[_ ]exceeded|model_context_window_exceeded|reduce the length of the messages|" +
        @"input token count.*exceeds the maximum|maximum prompt length is \d+|exceeds the available context size|" +
        @"greater than the context length|exceeded model token limit|input length and `?max_tokens`? exceed context limit|" +
        @"too many (input )?tokens|token limit exceeded",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OverflowPattern();

    /// <summary>Anthropic：“215000 tokens > 200000 maximum”。</summary>
    [GeneratedRegex(@"(\d+)\s*tokens?\s*>\s*(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex AnthropicCounts();

    /// <summary>OpenAI 兼容：“maximum context length is 128000 tokens. However, your messages resulted in 130512 tokens”。</summary>
    [GeneratedRegex(@"maximum context length is (\d+).*?resulted in (\d+)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex OpenAICounts();

    public static bool IsOverflowMessage(string? message) => message is not null && OverflowPattern().IsMatch(message);

    /// <summary>这条助手消息是否因上下文超窗而失败（或被截断）。</summary>
    public static bool Is(AssistantMessage message) =>
        message.ContextOverflow || (message.StopReason == StopReason.Error && IsOverflowMessage(message.ErrorMessage));

    /// <summary>从错误文案中解析超出的 token 数；解析不到时为 null。</summary>
    public static long? Excess(string? message)
    {
        if (message is null)
        {
            return null;
        }
        if (AnthropicCounts().Match(message) is { Success: true } a &&
            long.TryParse(a.Groups[1].Value, out var actual) && long.TryParse(a.Groups[2].Value, out var limit) && actual > limit)
        {
            return actual - limit;
        }
        if (OpenAICounts().Match(message) is { Success: true } o &&
            long.TryParse(o.Groups[1].Value, out var max) && long.TryParse(o.Groups[2].Value, out var used) && used > max)
        {
            return used - max;
        }
        return null;
    }
}
