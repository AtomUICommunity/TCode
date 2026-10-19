using TKCode.Agent.Plans;
using TKCode.Agent.Questions;

namespace TKCode.Agent.Coding.Tools.Plan;

/// <summary>宿主提供的 Plan 模式能力（docs/design/plan-mode.md §7）：计划审批与进入 Plan 的确认。</summary>
public interface IPlanModeService
{
    /// <summary>
    /// 把计划交给 UI 的审批面板并等待决定。批准时宿主已按选项切换模式与权限（编辑后的计划由工具写回计划文件）。
    /// 取消令牌触发（中止）时抛出 <see cref="OperationCanceledException"/>；没有 UI 或发送失败时抛出 <see cref="UserInputUnavailableException"/>。
    /// </summary>
    Task<PlanReviewResult> ReviewAsync(string sessionId, string toolCallId, string planPath, string plan, CancellationToken cancellationToken);

    /// <summary>
    /// 问用户是否切到 Plan 模式（复用问答面板）；用户选了 <see cref="EnterPlanModeTool.PlanOption"/> 时宿主已切到 Plan。
    /// 异常约定同 <see cref="ReviewAsync"/>。
    /// </summary>
    Task<UserInputResult> ConfirmEnterAsync(string sessionId, string toolCallId, UserQuestion question, CancellationToken cancellationToken);
}

/// <summary>计划文件的位置（docs/design/plan-mode.md §6）。</summary>
public static class PlanFiles
{
    /// <summary>~/.tkcode/plans/&lt;会话文件名去掉扩展名&gt;.md，与会话 JSONL 一一对应。</summary>
    public static string PathFor(string sessionFile) =>
        Path.Combine(TKCodePaths.Home, "plans", Path.GetFileNameWithoutExtension(sessionFile) + ".md");

    /// <summary>读取计划；文件不存在、读不了或为空时返回 null。</summary>
    public static string? Read(string path)
    {
        try
        {
            return File.Exists(path) && File.ReadAllText(path) is { } text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>fork 出新会话时复制计划文件（存在时）。</summary>
    public static void CopyFor(string fromSessionFile, string toSessionFile)
    {
        var from = PathFor(fromSessionFile);
        if (!File.Exists(from))
        {
            return;
        }
        try
        {
            File.Copy(from, PathFor(toSessionFile), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
