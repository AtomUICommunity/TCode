using System.Buffers;
using System.Text;
using System.Text.Json;

namespace TKCode.Agent.Plans;

/// <summary>
/// 计划审批（docs/design/plan-mode.md）。内置 exit_plan_mode 与 Claude 运行时的 ExitPlanMode 共用这一模型，
/// 经 Host 的 plan/review 交给同一个审批面板。
/// </summary>
public enum PlanReviewChoice
{
    /// <summary>清空上下文，切到 Agent 并自动执行。</summary>
    Clear,
    /// <summary>切到 Agent，权限设为自动批准（完全授权时保持）。</summary>
    Auto,
    /// <summary>切到 Agent，权限设为询问。</summary>
    Ask,
    /// <summary>切到 ACP，权限不变。</summary>
    Acp,
}

public enum PlanReviewOutcome
{
    Approved,
    /// <summary>继续完善（可能附反馈）。</summary>
    Revise,
    /// <summary>关闭面板（Esc），暂不批准。</summary>
    Dismissed,
    /// <summary>轮次被中止，请求被撤回。</summary>
    Aborted,
}

/// <param name="Choice">批准时的选项。</param>
/// <param name="Feedback">继续完善时的反馈；空表示没有。</param>
/// <param name="EditedPlan">用户在面板里编辑过计划时的新版本；null 表示没改。</param>
public sealed record PlanReviewResult(PlanReviewOutcome Outcome, PlanReviewChoice? Choice = null, string? Feedback = null, string? EditedPlan = null)
{
    public static PlanReviewResult Dismissed { get; } = new(PlanReviewOutcome.Dismissed);
    public static PlanReviewResult Aborted { get; } = new(PlanReviewOutcome.Aborted);

    public bool HasFeedback => !string.IsNullOrWhiteSpace(Feedback);
}

/// <summary>选项的线格式与转录卡片的 Details（kind = planReview）。</summary>
public static class PlanReviews
{
    public const string DetailsKind = "planReview";

    public static string IdOf(PlanReviewChoice choice) => choice switch
    {
        PlanReviewChoice.Clear => "clear",
        PlanReviewChoice.Auto => "auto",
        PlanReviewChoice.Ask => "ask",
        _ => "acp",
    };

    public static PlanReviewChoice? Parse(string? id) => id switch
    {
        "clear" => PlanReviewChoice.Clear,
        "auto" => PlanReviewChoice.Auto,
        "ask" => PlanReviewChoice.Ask,
        "acp" => PlanReviewChoice.Acp,
        _ => null,
    };

    /// <summary>
    /// 转录计划卡片的数据：<c>{"kind":"planReview","status":"pending|approved|revise|dismissed|aborted","path":…,"plan":…,
    /// "edited":bool,"choice":…,"feedback":…}</c>。plan 是当时展示或批准的全文快照。手写 JSON，保持 NativeAOT 安全。
    /// </summary>
    public static string DetailsJson(string? path, string plan, PlanReviewResult? result)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("kind", DetailsKind);
            w.WriteString("status", result?.Outcome switch
            {
                null => "pending",
                PlanReviewOutcome.Approved => "approved",
                PlanReviewOutcome.Revise => "revise",
                PlanReviewOutcome.Dismissed => "dismissed",
                _ => "aborted",
            });
            if (path is not null)
            {
                w.WriteString("path", path);
            }
            w.WriteString("plan", result?.EditedPlan ?? plan);
            if (result?.EditedPlan is not null)
            {
                w.WriteBoolean("edited", true);
            }
            if (result is { Outcome: PlanReviewOutcome.Approved, Choice: { } choice })
            {
                w.WriteString("choice", IdOf(choice));
            }
            if (result is { HasFeedback: true })
            {
                w.WriteString("feedback", result.Feedback!.Trim());
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
