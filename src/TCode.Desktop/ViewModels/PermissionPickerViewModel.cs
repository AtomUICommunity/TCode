using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TCode.Desktop.ViewModels;

/// <summary>
/// 权限选择器（Composer 底栏）：询问 / 自动放行 / 完全授权。
/// 选择“完全授权”时先在弹层内二次确认，确认后才生效；取消则保持原档位。
/// 取值与 Host 一致：ask / autoApprove / fullAccess。
/// </summary>
public sealed partial class PermissionPickerViewModel : ObservableObject
{
    public const string Ask = "ask";
    public const string AutoApprove = "autoApprove";
    public const string FullAccess = "fullAccess";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAsk), nameof(IsAutoApprove), nameof(IsFullAccess))]
    public partial string Mode { get; private set; } = Ask;

    /// <summary>弹层内正在确认“完全授权”。</summary>
    [ObservableProperty]
    public partial bool IsConfirmingFullAccess { get; private set; }

    public bool IsAsk => Mode == Ask;
    public bool IsAutoApprove => Mode == AutoApprove;
    public bool IsFullAccess => Mode == FullAccess;

    /// <summary>用户选定（并确认）了新的档位。</summary>
    public event Action<string>? ModeChosen;

    public event Action? CloseRequested;

    /// <summary>显示会话或项目的当前档位（不触发 <see cref="ModeChosen"/>）。</summary>
    public void Show(string? mode)
    {
        Mode = Normalize(mode);
        IsConfirmingFullAccess = false;
    }

    public static string Normalize(string? mode) => mode switch
    {
        AutoApprove => AutoApprove,
        FullAccess => FullAccess,
        _ => Ask,
    };

    [RelayCommand]
    private void Choose(string mode)
    {
        if (mode == FullAccess && Mode != FullAccess)
        {
            IsConfirmingFullAccess = true;
            return;
        }
        Apply(mode);
    }

    [RelayCommand]
    private void ConfirmFullAccess() => Apply(FullAccess);

    [RelayCommand]
    private void CancelConfirm() => IsConfirmingFullAccess = false;

    public void OnClosed() => IsConfirmingFullAccess = false;

    private void Apply(string mode)
    {
        IsConfirmingFullAccess = false;
        CloseRequested?.Invoke();
        mode = Normalize(mode);
        if (mode == Mode)
        {
            return;
        }
        Mode = mode;
        ModeChosen?.Invoke(mode);
    }
}
