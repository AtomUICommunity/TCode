using System.Text;
using System.Text.Json;
using TCode.Agent.Questions;

namespace TCode.Agent.Coding.Tools.Questions;

/// <summary>宿主提供的提问能力：把问题交给 UI 的问答面板并等待回答（docs/design/ask-user.md §4）。</summary>
public interface IUserInputService
{
    /// <summary>
    /// 等待用户回答。取消令牌触发（中止）时抛出 <see cref="OperationCanceledException"/>；
    /// 没有 UI 或发送失败时抛出 <see cref="UserInputUnavailableException"/>。
    /// </summary>
    Task<UserInputResult> AskAsync(string sessionId, string toolCallId, IReadOnlyList<UserQuestion> questions, CancellationToken cancellationToken);
}

/// <summary>
/// ask_user：向用户提出 1–4 个选择题并等待回答。不经审批，任何权限档位下都可用，也从不自动回答；
/// 子 Agent 没有这个工具（宿主不提供 <see cref="ToolServices.UserInput"/>）。提示词见 docs/design/ask-user.md §5。
/// </summary>
public sealed class AskUserTool(ToolContext context) : IAgentTool
{
    public const string ToolName = "ask_user";
    public const int MaxQuestions = 4;
    public const int MinOptions = 2;
    public const int MaxOptions = 4;

    public string Name => ToolName;
    public string Label => "Tool.AskUser";

    public string Description => """
        Ask the user one to four multiple-choice questions and wait for the answers. Use it only when you are blocked on a decision that is genuinely the user's to make: one you cannot resolve from the request, the code, or sensible defaults.

        - Investigate first. Do not ask what the workspace, the conversation or a quick check can answer.
        - Give each question 2-4 distinct options. If you recommend one, put it first and add " (Recommended)" to its label.
        - Do not add an "Other" option: the user can always type their own answer.
        - Set multiSelect when the choices are not mutually exclusive, and phrase the question to match.
        - Do not use it to ask for approval of a plan or for permission to proceed, and do not ask again what the user has already answered.
        """;

    public string ParametersSchemaJson => """
        {"type":"object","properties":{
          "questions":{"type":"array","minItems":1,"maxItems":4,"description":"The questions to ask, 1-4.","items":{
            "type":"object","properties":{
              "question":{"type":"string","description":"The complete question, clear and specific, ending with a question mark."},
              "header":{"type":"string","description":"A very short label shown as the question's tab, at most 12 characters, e.g. 'Database'."},
              "options":{"type":"array","minItems":2,"maxItems":4,"description":"2-4 distinct choices.","items":{
                "type":"object","properties":{
                  "label":{"type":"string","description":"The choice itself, 1-5 words."},
                  "description":{"type":"string","description":"What choosing it means, or its trade-off."}},
                "required":["label","description"],"additionalProperties":false}},
              "multiSelect":{"type":"boolean","description":"Let the user pick several options. Default false."}},
            "required":["question","header","options"],"additionalProperties":false}}},
         "required":["questions"],"additionalProperties":false}
        """;

    public string PromptSnippet => "Ask the user multiple-choice questions when a decision is genuinely theirs";

    public IReadOnlyList<string> PromptGuidelines =>
        ["When you need the user to choose between approaches, ask with ask_user instead of ending your turn with questions in text."];

    /// <summary>同一批里有提问时整批串行，问答面板不与审批卡片同时出现。</summary>
    public ToolConcurrency Concurrency => ToolConcurrency.Exclusive;

    public ToolPermission DescribePermission(JsonElement arguments) => ToolPermission.NoAccess;

    public const string TooFewOptionsMessage =
        "The questions were rejected and the user never saw them: every question needs at least two real options. " +
        "If there is only one reasonable path, state it as the approach you are taking and continue. Do not invent a filler second option.";

    public const string UnavailableMessage =
        "ask_user is unavailable: no user is attached to this session. Continue with your best judgment and state your assumptions.";

    public const string DeclinedMessage = UserAnswers.DeclinedMessage;

    public async Task<AgentToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        if (Parse(invocation.Arguments) is not { } questions)
        {
            return AgentToolResult.Error(TooFewOptionsMessage);
        }
        if (context.Services.UserInput is not { } service)
        {
            return AgentToolResult.Error(UnavailableMessage);
        }
        // 等待期间转录卡片显示“等待回答”
        invocation.Progress?.Report(AgentToolResult.Text("") with { Details = UserAnswers.DetailsJson(questions, null) });
        UserInputResult result;
        try
        {
            result = await service.AskAsync(context.SessionId, invocation.ToolCallId, questions, cancellationToken);
        }
        catch (UserInputUnavailableException)
        {
            return AgentToolResult.Error(UnavailableMessage);
        }
        if (result.Outcome == UserInputOutcome.Aborted)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        return AgentToolResult.Text(ResultText(questions, result)) with { Details = UserAnswers.DetailsJson(questions, result) };
    }

    /// <summary>解析并校验参数；选项不足时返回 null（其他格式问题抛出 <see cref="ToolArgumentException"/>）。</summary>
    internal static IReadOnlyList<UserQuestion>? Parse(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("questions", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            throw new ToolArgumentException("Missing required array parameter 'questions'.");
        }
        var questions = new List<UserQuestion>();
        foreach (var item in list.EnumerateArray())
        {
            var text = item.Required("question").Trim();
            var header = item.String("header")?.Trim() ?? "";
            var options = new List<UserQuestionOption>();
            if (item.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
            {
                foreach (var o in opts.EnumerateArray())
                {
                    var label = o.Required("label").Trim();
                    if (label.Length > 0)
                    {
                        options.Add(new UserQuestionOption(label, o.String("description")?.Trim() is { Length: > 0 } d ? d : null));
                    }
                }
            }
            if (options.Count < MinOptions)
            {
                return null;
            }
            if (options.Count > MaxOptions)
            {
                throw new ToolArgumentException($"Question \"{text}\" has {options.Count} options; give at most {MaxOptions}.");
            }
            if (options.Select(o => o.Label).Distinct(StringComparer.Ordinal).Count() != options.Count)
            {
                throw new ToolArgumentException($"Option labels must be unique within question \"{text}\".");
            }
            questions.Add(new UserQuestion(text, header.Length > 0 ? header : text, text, options, item.Bool("multiSelect") == true));
        }
        if (questions.Count is 0 or > MaxQuestions)
        {
            throw new ToolArgumentException($"Ask between 1 and {MaxQuestions} questions.");
        }
        if (questions.Select(q => q.Question).Distinct(StringComparer.Ordinal).Count() != questions.Count)
        {
            throw new ToolArgumentException("Question texts must be unique.");
        }
        return questions;
    }

    /// <summary>回给模型的文本（docs/design/ask-user.md §5.4）。</summary>
    internal static string ResultText(IReadOnlyList<UserQuestion> questions, UserInputResult result)
    {
        if (result.Outcome == UserInputOutcome.Declined)
        {
            return DeclinedMessage;
        }
        var list = new StringBuilder();
        var typed = false;
        foreach (var q in questions)
        {
            if (list.Length > 0)
            {
                list.Append(", ");
            }
            list.Append('"').Append(q.Question).Append("\"=");
            if (result.AnswerFor(q) is { IsEmpty: false } answer)
            {
                typed |= answer.HasText;
                list.Append('"').Append(UserAnswers.ValueOf(answer)).Append('"');
            }
            else
            {
                list.Append("(no answer)");
            }
        }
        return typed
            ? $"The user answered: {list}. Some answers are in the user's own words; read them carefully and follow what they actually say, even if it asks for clarification, a change of course, or that you not proceed."
            : $"The user answered your questions: {list}. Continue with these answers in mind.";
    }
}
