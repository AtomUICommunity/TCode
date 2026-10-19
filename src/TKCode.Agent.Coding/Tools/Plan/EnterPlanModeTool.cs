using System.Buffers;
using System.Text;
using System.Text.Json;
using TKCode.Agent.Questions;

namespace TKCode.Agent.Coding.Tools.Plan;

/// <summary>
/// enter_plan_mode：Agent 模式下申请先做计划（docs/design/plan-mode.md §7.2）。确认复用问答面板，任何权限档位下都要用户同意；
/// 同意后宿主切到 Plan，工具请求刷新上下文，同一轮继续调研。提示词见 §12.4。
/// </summary>
public sealed class EnterPlanModeTool(ToolContext context) : IAgentTool
{
    public const string ToolName = "enter_plan_mode";

    /// <summary>确认问题的选项标签（界面按 input/request 的 source 本地化显示，回答仍是这些标签）。</summary>
    public const string PlanOption = "Plan first";
    public const string ProceedOption = "Proceed";

    public const string QuestionId = "enter_plan_mode";

    public string Name => ToolName;
    public string Label => "Tool.EnterPlanMode";

    public string Description => """
        Ask the user to switch this session to Plan mode before you start a change, and wait for their decision. In Plan mode you investigate and write a plan for the user to approve, without changing anything.

        Use it before starting a change when the right approach is unclear and a wrong start would waste significant work, for example:
        - a new feature, or a change to existing behavior that touches several files;
        - several reasonable designs or technologies to choose between;
        - requirements you must explore before you can say what to change.

        Do not use it for small or clear changes (a typo, an obvious fix, a single function with a clear spec), when the user has already given detailed instructions or a plan, or for questions and research.
        """;

    public string ParametersSchemaJson => """
        {"type":"object","properties":{
          "reason":{"type":"string","description":"One sentence shown to the user: why planning first helps with this task."}},
         "additionalProperties":false}
        """;

    public string PromptSnippet => "Ask the user to switch to Plan mode before a large or unclear change";

    public ToolConcurrency Concurrency => ToolConcurrency.Exclusive;

    public ToolPermission DescribePermission(JsonElement arguments) => ToolPermission.NoAccess;

    public const string AcceptedMessage =
        "The user switched the session to Plan mode. Follow your Plan mode instructions: investigate, write the plan to the plan file, " +
        "and ask for approval with exit_plan_mode. Do not change anything yet.";

    public const string DeclinedMessage =
        "The user declined to switch to Plan mode. Continue with the task in Agent mode, and do not call enter_plan_mode again for it.";

    public const string UnavailableMessage =
        "enter_plan_mode is unavailable: no user is attached to this session. Continue in Agent mode.";

    /// <summary>确认问题：正文是模型给的理由。</summary>
    public static UserQuestion Question(string? reason) => new(QuestionId, "Plan mode",
        string.IsNullOrWhiteSpace(reason) ? "Plan before making changes?" : reason.Trim(),
        [
            new UserQuestionOption(PlanOption, "Investigate and write a plan; nothing changes until you approve it."),
            new UserQuestionOption(ProceedOption, "Carry on without a plan."),
        ],
        AllowOther: false);

    public async Task<AgentToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        if (context.PlanFile is null || context.Services.PlanMode is not { } service)
        {
            return AgentToolResult.Error(UnavailableMessage);
        }
        var reason = invocation.Arguments.ValueKind == JsonValueKind.Object ? invocation.Arguments.String("reason")?.Trim() : null;
        invocation.Progress?.Report(AgentToolResult.Text("") with { Details = DetailsJson(reason, null) });
        UserInputResult result;
        try
        {
            result = await service.ConfirmEnterAsync(context.SessionId, invocation.ToolCallId, Question(reason), cancellationToken);
        }
        catch (UserInputUnavailableException)
        {
            return AgentToolResult.Error(UnavailableMessage);
        }
        if (result.Outcome == UserInputOutcome.Aborted)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        var accepted = Accepted(result);
        if (accepted)
        {
            context.RequestContextRefresh();
        }
        return AgentToolResult.Text(accepted ? AcceptedMessage : DeclinedMessage) with { Details = DetailsJson(reason, accepted) };
    }

    public static bool Accepted(UserInputResult result) =>
        result.Outcome == UserInputOutcome.Answered &&
        result.Answers.FirstOrDefault(a => a.QuestionId == QuestionId)?.Selected.Contains(PlanOption) == true;

    /// <summary>转录卡片：<c>{"kind":"enterPlanMode","status":"pending|accepted|declined","reason":…}</c>。</summary>
    public static string DetailsJson(string? reason, bool? accepted)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("kind", "enterPlanMode");
            w.WriteString("status", accepted switch { null => "pending", true => "accepted", false => "declined" });
            if (!string.IsNullOrWhiteSpace(reason))
            {
                w.WriteString("reason", reason);
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
