using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKCode.Desktop.Localization;
using TKCode.Desktop.Services;

namespace TKCode.Desktop.ViewModels;

public sealed partial class WorkspaceItemViewModel(WorkspaceInfo info) : ObservableObject
{
    public WorkspaceInfo Info { get; } = info;
    public string Name => Info.Name;
    public string Path => Info.Path;
}

/// <summary>
/// 侧栏：项目筛选（全部项目 / 单个项目）+ 会话列表。
/// 项目即本机上的工作目录；会话来自 Host 的 session/list 与当前打开的会话。
/// </summary>
public sealed partial class SidebarViewModel : ObservableObject
{
    private readonly WorkspaceService _workspaces;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };

    public SidebarViewModel(WorkspaceService workspaces, ConversationViewModel conversation, StateService settings)
    {
        _workspaces = workspaces;
        Conversation = conversation;
        Projects = new(workspaces.Load().Select(w => new WorkspaceItemViewModel(w)));
        FilterPicker = new ProjectPickerViewModel(ProjectPickerMode.Filter, Projects, SetFilter, AddProject);
        WorkspacePicker = new ProjectPickerViewModel(ProjectPickerMode.Workspace, Projects, conversation.SetWorkspace, AddProject);

        FilterPicker.SetSelected(null);
        conversation.Sessions.CollectionChanged += (_, _) => RefreshVisibleSessions();
        conversation.WorkspaceChanged += () => WorkspacePicker.SetSelected(conversation.Workspace);
        Strings.LanguageChanged += (_, _) => OnPropertyChanged(nameof(FilterTitle));
        _clock.Tick += (_, _) =>
        {
            foreach (var item in Conversation.Sessions)
            {
                item.Refresh();
            }
        };
        _clock.Start();

        var last = Projects.FirstOrDefault(w => w.Path == settings.Current.LastWorkspace) ?? Projects.FirstOrDefault();
        conversation.SetWorkspace(last?.Info);
    }

    public ConversationViewModel Conversation { get; }
    public ObservableCollection<WorkspaceItemViewModel> Projects { get; }
    public ProjectPickerViewModel FilterPicker { get; }
    public ProjectPickerViewModel WorkspacePicker { get; }

    /// <summary>当前筛选下的会话。</summary>
    public ObservableCollection<SessionItemViewModel> VisibleSessions { get; } = [];

    public bool HasNoSessions => VisibleSessions.Count == 0;

    public string UserName { get; } = Environment.UserName;
    public string UserInitial => string.IsNullOrEmpty(UserName) ? "?" : UserName[..1].ToUpperInvariant();

    /// <summary>侧栏筛选的项目；null 表示全部项目。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterTitle))]
    public partial WorkspaceInfo? Filter { get; private set; }

    public string FilterTitle => Filter?.Name ?? Strings.Get(TKCodeLangResourceKind.AllProjects);

    [ObservableProperty]
    public partial bool IsSessionsExpanded { get; set; } = true;

    [RelayCommand]
    private void ToggleSessions() => IsSessionsExpanded = !IsSessionsExpanded;

    [RelayCommand]
    private void NewSession()
    {
        if (Filter is not null && Filter.Path != Conversation.Workspace?.Path)
        {
            Conversation.SetWorkspace(Filter);
        }
        Conversation.NewChat();
    }

    /// <summary>添加项目文件夹，并把它设为新会话的项目。</summary>
    public void AddProject(string path)
    {
        var existing = Projects.FirstOrDefault(w => w.Path == path);
        if (existing is null)
        {
            existing = new WorkspaceItemViewModel(_workspaces.Add(path));
            Projects.Insert(0, existing);
        }
        Conversation.SetWorkspace(existing.Info);
    }

    private void SetFilter(WorkspaceInfo? project)
    {
        Filter = project;
        FilterPicker.SetSelected(project);
        // 选中某个项目时，新会话也默认在该项目中创建。
        if (project is not null)
        {
            Conversation.SetWorkspace(project);
        }
        // 按项目在 Host 端分页查询（只加载该项目的会话）
        _ = Conversation.SetSessionFilterAsync(project?.Path);
    }

    private void RefreshVisibleSessions()
    {
        VisibleSessions.Clear();
        foreach (var item in Conversation.Sessions.Where(s => Filter is null || SamePath(s.Cwd, Filter.Path)))
        {
            VisibleSessions.Add(item);
        }
        OnPropertyChanged(nameof(HasNoSessions));
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
