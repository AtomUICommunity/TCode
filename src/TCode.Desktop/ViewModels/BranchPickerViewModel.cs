using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCode.Desktop.Localization;

namespace TCode.Desktop.ViewModels;

public sealed partial class BranchItemViewModel(string name, bool isCurrent) : ObservableObject
{
    public string Name { get; } = name;
    public bool IsCurrent { get; } = isCurrent;
}

/// <summary>
/// 分支弹层：搜索本地分支，标出当前分支；选择其它分支时先在弹层内确认，再执行 git switch。
/// Agent 在该项目中运行时不允许切换。
/// </summary>
public sealed partial class BranchPickerViewModel : ObservableObject
{
    private readonly Func<string, Task<string?>> _switch;
    private readonly Func<bool> _isBusy;
    private IReadOnlyList<string> _all = [];

    public BranchPickerViewModel(Func<string, Task<string?>> switchBranch, Func<bool> isBusy)
    {
        _switch = switchBranch;
        _isBusy = isBusy;
        Strings.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(ConfirmText));
        };
    }

    public ObservableCollection<BranchItemViewModel> Items { get; } = [];

    public event Action? CloseRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    public partial string? Current { get; private set; }

    /// <summary>触发按钮上的文字：当前分支，不是 Git 仓库时为“无分支”。</summary>
    public string DisplayName => Current ?? Strings.Get(TCodeLangResourceKind.NoBranch);

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    /// <summary>等待确认的目标分支。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfirmText), nameof(IsConfirming))]
    public partial string? Pending { get; private set; }

    public bool IsConfirming => Pending is not null;
    public string ConfirmText => Pending is null ? "" : Strings.Format(TCodeLangResourceKind.SwitchBranchConfirmFormat, Pending);

    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial bool IsSwitching { get; private set; }

    partial void OnSearchChanged(string value) => Refresh();

    public void Load(string? current, IReadOnlyList<string> branches)
    {
        Current = current;
        _all = branches.Count > 0 || current is null ? branches : [current];
        Refresh();
    }

    [RelayCommand]
    private void Select(BranchItemViewModel item)
    {
        Error = null;
        if (item.IsCurrent)
        {
            CloseRequested?.Invoke();
            return;
        }
        if (_isBusy())
        {
            Error = Strings.Get(TCodeLangResourceKind.BranchBusy);
            return;
        }
        Pending = item.Name;
    }

    [RelayCommand]
    private void Cancel()
    {
        Pending = null;
        Error = null;
    }

    private bool CanConfirm() => !IsSwitching;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        if (Pending is not { } target)
        {
            return;
        }
        IsSwitching = true;
        try
        {
            var error = await _switch(target);
            if (error is null)
            {
                Pending = null;
                CloseRequested?.Invoke();
            }
            else
            {
                Error = Strings.Format(TCodeLangResourceKind.SwitchBranchFailedFormat, error);
            }
        }
        finally
        {
            IsSwitching = false;
        }
    }

    public void OnClosed()
    {
        Search = "";
        Pending = null;
        Error = null;
    }

    private void Refresh()
    {
        Items.Clear();
        foreach (var name in _all.Where(b => Search.Length == 0 || b.Contains(Search, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(b => b == Current))
        {
            Items.Add(new BranchItemViewModel(name, name == Current));
        }
    }
}
