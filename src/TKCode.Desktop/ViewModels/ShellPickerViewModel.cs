using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKCode.Desktop.Localization;
using TKCode.Shell;

namespace TKCode.Desktop.ViewModels;

/// <summary>
/// 设置页里选择 shell 的一行（Agent 的 bash 工具、新建终端各一个）：自动、本机探测到的 shell，或自定义路径 / 程序名。
/// 探测在后台线程做（Windows 上要读文件版本），结果回到界面线程后再刷新列表。
/// </summary>
public sealed partial class ShellPickerViewModel(ShellUse use, TKCodeLangResourceKind title, TKCodeLangResourceKind hint,
    Func<string?> current, Action<string?> save, ShellCatalog? catalog = null) : ObservableObject
{
    private const string Auto = "auto";

    private readonly ShellCatalog _catalog = catalog ?? ShellCatalog.Default;
    private int _generation;

    /// <summary>选定或应用自定义路径后，请视图关闭弹层。</summary>
    public event Action? CloseRequested;

    public string Title => Strings.Get(title);
    public string Hint => Strings.Get(hint);

    public ObservableCollection<SettingsChoiceViewModel> Choices { get; } = [];

    /// <summary>弹层按钮上的文字：自动时带上实际会用的 shell。</summary>
    [ObservableProperty]
    public partial string Label { get; set; } = "";

    /// <summary>实际使用的 shell 路径；指定的 shell 不可用时说明改用了哪个。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    public partial string Detail { get; set; } = "";

    [ObservableProperty]
    public partial string CustomPath { get; set; } = "";

    public bool HasDetail => Detail.Length > 0;

    /// <summary>按当前设置与语言重新生成选项（设置变化、语言切换、打开设置页时调用）。</summary>
    public void Refresh() => _ = RefreshAsync();

    /// <summary>同 <see cref="Refresh"/>，完成后返回（测试用）。</summary>
    public async Task RefreshAsync()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Hint));
        var preference = Normalize(current());
        var generation = Interlocked.Increment(ref _generation);
        var (detected, selection) = await Task.Run(() => (_catalog.Detect(), _catalog.Resolve(preference, use)));
        if (generation == Volatile.Read(ref _generation))
        {
            Apply(preference, detected, selection);
        }
    }

    private void Apply(string? preference, IReadOnlyList<DetectedShell> detected, ShellSelection selection)
    {
        var items = new List<(string Value, string Label)> { (Auto, Strings.Get(TKCodeLangResourceKind.ShellAuto)) };
        // cmd 不能执行 Agent 的命令（引号规则与逐个传参不兼容），只在终端里提供
        items.AddRange(detected.Where(s => use == ShellUse.Terminal || s.Kind != ShellKind.Cmd).Select(s => (s.Id, s.Label)));

        string selected;
        var custom = false;
        if (preference is null)
        {
            selected = Auto;
        }
        else if (items.FindIndex(i => i.Value.Equals(preference, StringComparison.OrdinalIgnoreCase)) is >= 0 and var index)
        {
            selected = items[index].Value;
        }
        else
        {
            // 自定义路径、PATH 里的程序名，或本机探测不到的 id
            selected = preference;
            custom = true;
            items.Add((preference, selection.Warning is null ? preference : Strings.Format(TKCodeLangResourceKind.ShellUnavailable, preference)));
        }

        Choices.Clear();
        foreach (var (value, label) in items)
        {
            Choices.Add(new SettingsChoiceViewModel(value, label) { IsSelected = value == selected });
        }
        Label = selected == Auto
            ? $"{items[0].Label} · {selection.Shell.Label}"
            : items.First(i => i.Value == selected).Label;
        Detail = selection.Warning is null
            ? Strings.Format(TKCodeLangResourceKind.ShellCurrent, selection.Shell.Executable)
            : Strings.Format(TKCodeLangResourceKind.ShellFallback, preference ?? "", selection.Shell.Label);
        CustomPath = custom ? selected : "";
    }

    [RelayCommand]
    private void Choose(SettingsChoiceViewModel choice)
    {
        save(Normalize(choice.Value));
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void ApplyCustomPath()
    {
        if (Normalize(CustomPath) is { } path)
        {
            save(path);
            CloseRequested?.Invoke();
        }
    }

    /// <summary>去掉首尾空白与引号；空值和 auto 都表示默认（写回设置时删除该字段）。</summary>
    private static string? Normalize(string? value)
    {
        var text = value?.Trim().Trim('"').Trim();
        return string.IsNullOrEmpty(text) || text.Equals(Auto, StringComparison.OrdinalIgnoreCase) ? null : text;
    }
}
