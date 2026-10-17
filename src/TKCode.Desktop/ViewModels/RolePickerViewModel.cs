using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKCode.Desktop.Icons;
using TKCode.Desktop.Localization;
using TKCode.Rpc;

namespace TKCode.Desktop.ViewModels;

/// <summary>role 选择器中的一项。</summary>
/// <param name="Model">选中时同时切换到的模型（自定义 role 的 frontmatter）。</param>
public sealed partial class RoleOptionViewModel(string name, string title, string? description, LucideKind icon, string? source, string? model)
    : ObservableObject
{
    public string Name { get; } = name;
    public string Title { get; } = title;
    public string? Description { get; } = description;
    public LucideKind Icon { get; } = icon;
    public string? Source { get; } = source;
    public string? Model { get; } = model;
    public bool HasDescription => !string.IsNullOrEmpty(Description);

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>
/// role 选择器（docs/design/composer.md §10.3，类似 Cursor 的模式）：内置 Agent / Plan / ACP，加上当前项目的自定义 role。
/// 芯片显示当前 role；Shift+Tab 依次切换。取值与 Host 一致：agent / plan / acp / 自定义 role 名。
/// </summary>
public sealed partial class RolePickerViewModel : ObservableObject
{
    public const string Agent = "agent";
    public const string Plan = "plan";
    public const string Acp = "acp";

    private IReadOnlyList<AgentAssetDto> _custom = [];

    public RolePickerViewModel() => Load([]);

    /// <summary>当前模型的运行时能派生子 Agent；不能时不提供 ACP（docs/design/composer.md §11）。</summary>
    public bool AcpAvailable
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                Load(_custom);
            }
        }
    } = true;

    public ObservableCollection<RoleOptionViewModel> Roles { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Current), nameof(IsAgent))]
    public partial string Role { get; private set; } = Agent;

    /// <summary>当前 role；自定义 role 已被删除时显示为 Agent。</summary>
    public RoleOptionViewModel Current => Roles.FirstOrDefault(r => r.Name.Equals(Role, StringComparison.OrdinalIgnoreCase)) ?? Roles[0];

    public bool IsAgent => Current.Name == Agent;

    /// <summary>用户选定了新的 role。</summary>
    public event Action<RoleOptionViewModel>? RoleChosen;

    public event Action? CloseRequested;

    /// <summary>刷新可选的 role：内置三种在前，然后是项目与全局的自定义 role。</summary>
    public void Load(IReadOnlyList<AgentAssetDto> custom)
    {
        _custom = custom;
        Roles.Clear();
        Roles.Add(new RoleOptionViewModel(Agent, Strings.Get(TKCodeLangResourceKind.RoleAgent), Strings.Get(TKCodeLangResourceKind.RoleAgentDesc), LucideKind.Bot, null, null));
        Roles.Add(new RoleOptionViewModel(Plan, Strings.Get(TKCodeLangResourceKind.RolePlan), Strings.Get(TKCodeLangResourceKind.RolePlanDesc), LucideKind.ListChecks, null, null));
        if (AcpAvailable)
        {
            Roles.Add(new RoleOptionViewModel(Acp, Strings.Get(TKCodeLangResourceKind.RoleAcp), Strings.Get(TKCodeLangResourceKind.RoleAcpDesc), LucideKind.LayoutGrid, null, null));
        }
        foreach (var role in custom)
        {
            if (Roles.Any(r => r.Name.Equals(role.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            var icon = role.Icon is { } name && Enum.TryParse<LucideKind>(name.Replace("-", ""), ignoreCase: true, out var kind) ? kind : LucideKind.Sparkles;
            Roles.Add(new RoleOptionViewModel(role.Name, role.DisplayName ?? role.Name, role.Description, icon,
                Completion.AssetCatalogCache.SourceLabel(role.Source), role.Model));
        }
        UpdateSelection();
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(IsAgent));
    }

    /// <summary>显示会话的当前 role（不触发 <see cref="RoleChosen"/>）。</summary>
    public void Show(string? role)
    {
        Role = string.IsNullOrEmpty(role) ? Agent : role;
        UpdateSelection();
    }

    [RelayCommand]
    private void Choose(RoleOptionViewModel role)
    {
        CloseRequested?.Invoke();
        if (role.Name == Role)
        {
            return;
        }
        Role = role.Name;
        UpdateSelection();
        RoleChosen?.Invoke(role);
    }

    /// <summary>按名称选择（/ 补全中选中 role）。</summary>
    public void Choose(string name)
    {
        if (Roles.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } role)
        {
            Choose(role);
        }
    }

    /// <summary>Shift+Tab：切换到下一个 role（循环）。</summary>
    public void Cycle()
    {
        var index = Roles.IndexOf(Current);
        Choose(Roles[(index + 1) % Roles.Count]);
    }

    private void UpdateSelection()
    {
        foreach (var role in Roles)
        {
            role.IsSelected = role == Current;
        }
    }
}
