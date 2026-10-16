using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCode.Desktop.Localization;
using TCode.Desktop.Services;

namespace TCode.Desktop.ViewModels;

public sealed partial class ProjectItemViewModel(WorkspaceInfo? info, string title, string? meta) : ObservableObject
{
    /// <summary>null 表示“全部项目”（侧栏筛选）或“不使用项目”（新会话）。</summary>
    public WorkspaceInfo? Info { get; } = info;
    public string Title { get; } = title;
    public string? Meta { get; } = meta;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

public enum ProjectPickerMode
{
    /// <summary>侧栏：筛选会话列表，首项为“全部项目”。</summary>
    Filter,
    /// <summary>空状态：选择新会话的项目，末尾有“不使用项目”。</summary>
    Workspace,
}

/// <summary>可搜索的项目弹层：搜索框 + 项目列表 + “新建项目…”（+“不使用项目”）。</summary>
public sealed partial class ProjectPickerViewModel : ObservableObject
{
    private readonly ObservableCollection<WorkspaceItemViewModel> _projects;
    private readonly Action<WorkspaceInfo?> _onSelect;
    private readonly Action<string> _onAddProject;
    private WorkspaceInfo? _selected;

    public ProjectPickerViewModel(ProjectPickerMode mode, ObservableCollection<WorkspaceItemViewModel> projects,
        Action<WorkspaceInfo?> onSelect, Action<string> onAddProject)
    {
        Mode = mode;
        _projects = projects;
        _onSelect = onSelect;
        _onAddProject = onAddProject;
        _projects.CollectionChanged += (_, _) => Refresh();
        Strings.LanguageChanged += (_, _) => Refresh();
        Refresh();
    }

    public ProjectPickerMode Mode { get; }
    public bool ShowNoProject => Mode == ProjectPickerMode.Workspace;
    public ObservableCollection<ProjectItemViewModel> Items { get; } = [];

    /// <summary>请求关闭弹层（打开状态由视图持有）。</summary>
    public event Action? CloseRequested;

    /// <summary>“新建项目…”：由视图弹出文件夹选择框。</summary>
    public event Action? NewProjectRequested;

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    /// <summary>触发按钮上的文字：当前选择的项目名，或“全部项目” / “无项目”。</summary>
    [ObservableProperty]
    public partial string Title { get; private set; } = "";

    partial void OnSearchChanged(string value) => Refresh();

    public void SetSelected(WorkspaceInfo? selected)
    {
        _selected = selected;
        Title = selected?.Name ?? Strings.Get(Mode == ProjectPickerMode.Filter ? TCodeLangResourceKind.AllProjects : TCodeLangResourceKind.NoProjectName);
        foreach (var item in Items)
        {
            item.IsSelected = item.Info?.Path == selected?.Path;
        }
    }

    [RelayCommand]
    private void Select(ProjectItemViewModel item)
    {
        CloseRequested?.Invoke();
        _onSelect(item.Info);
    }

    [RelayCommand]
    private void NewProject()
    {
        CloseRequested?.Invoke();
        NewProjectRequested?.Invoke();
    }

    [RelayCommand]
    private void ChooseNoProject()
    {
        CloseRequested?.Invoke();
        _onSelect(null);
    }

    public void OnClosed() => Search = "";

    /// <summary>视图在文件夹选择框返回后调用。</summary>
    public void AddProject(string path) => _onAddProject(path);

    private void Refresh()
    {
        Items.Clear();
        if (Mode == ProjectPickerMode.Filter && Search.Length == 0)
        {
            Items.Add(new ProjectItemViewModel(null, Strings.Get(TCodeLangResourceKind.AllProjects), null));
        }
        var meta = "@ " + DeviceInfo.Name;
        foreach (var project in _projects.Where(p => Search.Length == 0 || p.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)))
        {
            Items.Add(new ProjectItemViewModel(project.Info, project.Name, meta));
        }
        SetSelected(_selected);
    }
}
