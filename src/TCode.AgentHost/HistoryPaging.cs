using TCode.Ai.Messages;

namespace TCode.AgentHost;

/// <summary>
/// 发给界面的历史：按轮次分页（每页以用户消息开头，工具调用与结果不会被拆开），
/// 超长的工具输出与思考内容截断传输（保留开头与结尾）。Agent 自身仍持有完整历史。
/// </summary>
public static class HistoryPaging
{
    /// <summary>单段文本的传输上限（字符）；超过时保留开头 24K 与结尾 8K。</summary>
    public const int MaxTextLength = 32 * 1024;
    private const int HeadLength = 24 * 1024;
    private const int TailLength = 8 * 1024;

    public static string Truncate(string text)
    {
        if (text.Length <= MaxTextLength)
        {
            return text;
        }
        var omitted = text.Length - HeadLength - TailLength;
        return string.Concat(text.AsSpan(0, HeadLength), $"\n\n… [{omitted:N0} characters omitted] …\n\n", text.AsSpan(text.Length - TailLength));
    }

    /// <summary>
    /// <paramref name="before"/> 之前最近 <paramref name="turnLimit"/> 轮的起始位置：
    /// 从第 N 个（倒数）用户消息开始；用户消息不足时从 0 开始。
    /// </summary>
    public static int StartOfRecentTurns(IReadOnlyList<Message> messages, int before, int turnLimit)
    {
        before = Math.Clamp(before, 0, messages.Count);
        var turns = 0;
        for (var i = before - 1; i >= 0; i--)
        {
            if (messages[i] is UserMessage && ++turns == turnLimit)
            {
                return i;
            }
        }
        return 0;
    }
}
