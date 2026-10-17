using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCode.Desktop.Icons;
using TCode.Desktop.Localization;
using TCode.Rpc;

namespace TCode.Desktop.ViewModels;

/// <summary>
/// 一条等待用户决定的工具审批（Host 的 tool/approve 反向请求）。
/// Kind 为 command / fileChange / read / network / tool；带 Title、Reason，并可能允许“本会话内允许”。
/// </summary>
public sealed partial class ApprovalRequestViewModel(ToolApprovalRequest request) : ObservableObject
{
    private readonly TaskCompletionSource<ToolApprovalResponse> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ToolApprovalRequest Request { get; } = request;
    public Task<ToolApprovalResponse> Result => _result.Task;

    /// <summary>做出决定后从队列中移除。</summary>
    public event Action<ApprovalRequestViewModel>? Decided;

    public LucideKind Icon => Request.Kind switch
    {
        "command" => LucideKind.SquareTerminal,
        "fileChange" => LucideKind.FileCode,
        "read" => LucideKind.Eye,
        "network" => LucideKind.Globe,
        "mcp" => LucideKind.Plug,
        _ => LucideKind.Wrench,
    };

    public string KindText => Strings.Get(Request.Kind switch
    {
        "command" => TCodeLangResourceKind.ApprovalCommand,
        "fileChange" => TCodeLangResourceKind.ApprovalFileChange,
        "read" => TCodeLangResourceKind.ApprovalRead,
        "network" => TCodeLangResourceKind.ApprovalNetwork,
        "mcp" => TCodeLangResourceKind.ApprovalMcp,
        _ => TCodeLangResourceKind.ApprovalTool,
    });

    /// <summary>命令行 / 文件路径；内置 Agent 没有 Title 时显示工具名。</summary>
    public string Title => string.IsNullOrWhiteSpace(Request.Title) ? Request.ToolName : Request.Title;

    /// <summary>内置 Agent 的参数 JSON（外部运行时一般已在 Title 中说明）。</summary>
    /// <remarks>MCP 工具总是显示参数：调用第三方服务器之前让用户看到要发送的内容（规格 §4.2）。</remarks>
    public string? Details => (string.IsNullOrWhiteSpace(Request.Title) || Request.Kind == "mcp") && Request.ArgumentsJson is { Length: > 2 } args
        ? args
        : null;
    public bool HasDetails => Details is not null;
    public string? Reason => Request.Reason;
    public bool HasReason => !string.IsNullOrWhiteSpace(Request.Reason);
    public bool CanAllowForSession => Request.CanAllowForSession;

    [RelayCommand]
    private void AllowOnce() => Decide(new ToolApprovalResponse(true));

    [RelayCommand]
    private void AllowForSession() => Decide(new ToolApprovalResponse(true, ForSession: true));

    [RelayCommand]
    private void Deny() => Decide(new ToolApprovalResponse(false, "User denied the action."));

    /// <summary>会话关闭或连接断开时拒绝未决请求，避免 Host 一直等待。</summary>
    public void Cancel() => Decide(new ToolApprovalResponse(false, "The request was cancelled."));

    private void Decide(ToolApprovalResponse response)
    {
        if (_result.TrySetResult(response))
        {
            Decided?.Invoke(this);
        }
    }
}
