using System.Buffers;
using System.Text;
using System.Text.Json;

namespace TKCode.Agent.Questions;

/// <summary>
/// Agent 向用户提出的问题（docs/design/ask-user.md）。内置 ask_user 与外部运行时（Claude AskUserQuestion、
/// Codex requestUserInput）共用这一模型，经 Host 的 input/request 交给同一个问答面板。
/// </summary>
/// <param name="Id">回答以它为键：内置与 Claude 为问题原文，Codex 为问题 id。</param>
/// <param name="Options">为空时只有文本框。</param>
/// <param name="AllowOther">提供“其他”自由输入。</param>
/// <param name="IsSecret">文本框掩码显示（Codex isSecret）。</param>
public sealed record UserQuestion(
    string Id,
    string Header,
    string Question,
    IReadOnlyList<UserQuestionOption> Options,
    bool MultiSelect = false,
    bool AllowOther = true,
    bool IsSecret = false);

public sealed record UserQuestionOption(string Label, string? Description = null);

/// <param name="Selected">选中的选项标签。</param>
/// <param name="Text">用户自己输入的答案；null 表示没有。</param>
public sealed record UserAnswer(string QuestionId, IReadOnlyList<string> Selected, string? Text = null)
{
    public bool IsEmpty => Selected.Count == 0 && string.IsNullOrWhiteSpace(Text);
    public bool HasText => !string.IsNullOrWhiteSpace(Text);
}

public enum UserInputOutcome
{
    Answered,
    /// <summary>用户拒绝回答（Esc）。</summary>
    Declined,
    /// <summary>轮次被中止，请求被撤回。</summary>
    Aborted,
}

public sealed record UserInputResult(UserInputOutcome Outcome, IReadOnlyList<UserAnswer> Answers)
{
    public static UserInputResult Declined { get; } = new(UserInputOutcome.Declined, []);
    public static UserInputResult Aborted { get; } = new(UserInputOutcome.Aborted, []);

    public UserAnswer? AnswerFor(UserQuestion question) => Answers.FirstOrDefault(a => a.QuestionId == question.Id);
}

/// <summary>没有可提问的用户（没有 UI 连接或请求发送失败）。</summary>
public sealed class UserInputUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>回答的文本形式与转录卡片的 Details（kind = question）。</summary>
public static class UserAnswers
{
    /// <summary>用户拒绝回答（Esc）时回给模型的文本；内置 ask_user 与外部运行时共用（docs/design/ask-user.md §5.4）。</summary>
    public const string DeclinedMessage =
        "The user declined to answer these questions. Do not proceed on assumptions about them: end your turn now and wait for the user's next message.";

    /// <summary>
    /// 一个问题的回答写成一个字符串：选项标签按 ", " 连接，用户输入的文本放在最后。
    /// 标签本身含 ", " 或引号时加引号，避免与分隔符混淆（与 Claude CLI 的编码一致）。
    /// </summary>
    public static string ValueOf(UserAnswer answer)
    {
        var parts = answer.Selected.Select(label => answer.Selected.Count > 1 || answer.HasText ? QuoteIfNeeded(label) : label).ToList();
        if (answer.HasText)
        {
            parts.Add(parts.Count > 0 ? QuoteIfNeeded(answer.Text!.Trim()) : answer.Text!.Trim());
        }
        return string.Join(", ", parts);
    }

    private static string QuoteIfNeeded(string value) =>
        value.Contains(", ", StringComparison.Ordinal) || value.Contains('"') ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;

    /// <summary>
    /// 转录问答卡片的数据：<c>{"kind":"question","status":"pending|answered|declined|aborted","questions":[…]}</c>，
    /// 每个问题带 selected / text（已回答时）。手写 JSON，保持 NativeAOT 安全且不依赖具体的 ToolDetails 上下文。
    /// </summary>
    public static string DetailsJson(IReadOnlyList<UserQuestion> questions, UserInputResult? result)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("kind", "question");
            w.WriteString("status", result?.Outcome switch
            {
                null => "pending",
                UserInputOutcome.Answered => "answered",
                UserInputOutcome.Declined => "declined",
                _ => "aborted",
            });
            w.WriteStartArray("questions");
            foreach (var q in questions)
            {
                w.WriteStartObject();
                w.WriteString("id", q.Id);
                w.WriteString("header", q.Header);
                w.WriteString("question", q.Question);
                if (q.MultiSelect)
                {
                    w.WriteBoolean("multiSelect", true);
                }
                if (q.IsSecret)
                {
                    w.WriteBoolean("isSecret", true);
                }
                w.WriteStartArray("options");
                foreach (var o in q.Options)
                {
                    w.WriteStartObject();
                    w.WriteString("label", o.Label);
                    if (o.Description is { } d)
                    {
                        w.WriteString("description", d);
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                if (result?.AnswerFor(q) is { } answer)
                {
                    w.WriteStartArray("selected");
                    foreach (var s in answer.Selected)
                    {
                        w.WriteStringValue(s);
                    }
                    w.WriteEndArray();
                    if (answer.HasText)
                    {
                        // 掩码问题不把内容写进转录
                        w.WriteString("text", q.IsSecret ? "••••••" : answer.Text!.Trim());
                    }
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
