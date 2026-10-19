using System.Text.Json;
using TKCode.Agent.Plans;
using TKCode.Agent.Questions;

namespace TKCode.Agent.Coding.Tools.Plan;

/// <summary>
/// exit_plan_mode：把计划文件交给用户审批并等待决定（docs/design/plan-mode.md §7.1）。只在 Plan 模式下出现，不经审批，
/// 从不自动通过；批准时宿主切换模式与权限，工具请求刷新上下文，同一轮继续。提示词见 §12.2、§12.3。
/// </summary>
public sealed class ExitPlanModeTool(ToolContext context) : IAgentTool
{
    public const string ToolName = "exit_plan_mode";

    public string Name => ToolName;
    public string Label => "Tool.ExitPlanMode";

    public string Description => """
        Ask the user to approve your plan, and wait for their decision. Available only in Plan mode.

        - First write the complete plan to the plan file named in your Plan mode instructions. This tool reads the plan from that file and shows it to the user, who may edit it before approving.
        - Call it only when the plan is finished and the open questions are settled. Do not call it for questions or research that do not lead to a change.
        - This is the only way to ask whether the plan is acceptable: do not ask in text or with ask_user.
        - If the user approves, Plan mode ends and you continue in the same turn under the mode they chose. If they ask for changes, update the plan file and call this tool again.
        """;

    public string ParametersSchemaJson => """{"type":"object","properties":{},"additionalProperties":false}""";

    public string PromptSnippet => "Ask the user to approve the plan in the plan file (Plan mode only)";

    /// <summary>同一条回复里先写计划文件再调用时，串行执行保证读到的是新内容。</summary>
    public ToolConcurrency Concurrency => ToolConcurrency.Exclusive;

    public ToolPermission DescribePermission(JsonElement arguments) => ToolPermission.NoAccess;

    public static string NoPlanMessage(string path) =>
        $"No plan to review: write the complete plan to {path} first, then call exit_plan_mode again.";

    public static string UnavailableMessage(string? path) =>
        path is null
            ? "exit_plan_mode is unavailable in this session."
            : $"exit_plan_mode is unavailable: no user is attached to this session. End your turn; the plan is saved at {path}.";

    public const string NotApprovedMessage =
        "The user did not approve the plan yet and wants to keep planning. Stay in Plan mode: end your turn now and wait for the user's next message.";

    public const string ClearedMessage =
        "The user approved the plan. The conversation is being cleared; you will continue from the plan alone.";

    private const string EditedNote = " The user edited the plan before approving it; follow the version below.";

    public async Task<AgentToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        if (context.PlanFile is not { } path || context.Services.PlanMode is not { } service)
        {
            return AgentToolResult.Error(UnavailableMessage(context.PlanFile));
        }
        if (PlanFiles.Read(path) is not { } plan)
        {
            return AgentToolResult.Error(NoPlanMessage(path));
        }
        // 等待期间转录卡片显示“等待批准”
        invocation.Progress?.Report(AgentToolResult.Text("") with { Details = PlanReviews.DetailsJson(path, plan, null) });
        PlanReviewResult result;
        try
        {
            result = await service.ReviewAsync(context.SessionId, invocation.ToolCallId, path, plan, cancellationToken);
        }
        catch (UserInputUnavailableException)
        {
            return AgentToolResult.Error(UnavailableMessage(path));
        }
        if (result.Outcome == PlanReviewOutcome.Aborted)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        var details = PlanReviews.DetailsJson(path, plan, result);
        if (result.Outcome != PlanReviewOutcome.Approved)
        {
            var text = result is { Outcome: PlanReviewOutcome.Revise, HasFeedback: true } ? FeedbackText(result.Feedback!) : NotApprovedMessage;
            return AgentToolResult.Text(text) with { Details = details };
        }

        var approved = plan;
        if (result.EditedPlan is { } edited && !string.IsNullOrWhiteSpace(edited))
        {
            approved = edited.Trim();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, approved + "\n", CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 写回失败不影响执行：批准的全文在工具结果里
            }
        }
        context.RequestContextRefresh();
        if (result.Choice == PlanReviewChoice.Clear)
        {
            context.RequestHandoff(HandoffMessage(approved, path, context.TranscriptPath));
            return AgentToolResult.Text(ClearedMessage) with { Details = details };
        }
        return AgentToolResult.Text(ApprovedText(approved, path, result.EditedPlan is not null, result.Choice == PlanReviewChoice.Acp))
            with { Details = details };
    }

    /// <summary>批准后的结果文本（§12.3 A / B）。</summary>
    public static string ApprovedText(string plan, string path, bool edited, bool acp)
    {
        var note = edited ? EditedNote : "";
        return acp
            ? $"""
              The user approved the plan.{note} The session is now in ACP mode: carry the plan out by delegating it to subagents, as your ACP mode instructions describe. Do not implement it yourself.

              The plan is saved at {path}; give each subagent the parts of it that it needs.

              ## Approved plan

              {plan}
              """
            : $"""
              The user approved the plan.{note} Plan mode is over: you can now edit files and run commands to carry it out.

              The plan is saved at {path}; refer back to it as you work.

              ## Approved plan

              {plan}

              Start implementing now. If the work has several steps, track them with task_create first.
              """;
    }

    /// <summary>继续完善且附了反馈（§12.3 C）。</summary>
    public static string FeedbackText(string feedback) => $"""
        The user did not approve the plan yet. Stay in Plan mode. Their feedback:

        {feedback.Trim()}

        Update the plan file to address it (ask with ask_user if the feedback is unclear), then call exit_plan_mode again.
        """;

    /// <summary>清空上下文后的交接消息（§12.6）。</summary>
    public static string HandoffMessage(string plan, string path, string? transcriptPath)
    {
        var transcript = transcriptPath is null
            ? ""
            : $" If you need specific details from the planning conversation (exact code snippets, error messages or findings), read the full transcript at: {transcriptPath}";
        return $"""
            The user approved the plan below in Plan mode and cleared the earlier conversation. Implement it now.

            {plan}

            The plan is saved at {path}.{transcript}
            """;
    }
}
