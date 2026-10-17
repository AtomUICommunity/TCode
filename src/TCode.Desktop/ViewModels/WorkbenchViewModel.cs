using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCode.Desktop.Icons;
using TCode.Desktop.Services;
using TCode.Desktop.Terminal;
using TCode.Desktop.Localization;
using TCode.Desktop.Previews;

namespace TCode.Desktop.ViewModels;

public enum WorkbenchTabKind
{
    File,
    Browser,
    Terminal,
    Diffs,
    History,
}

internal static class WorkbenchTexts
{
    public static string Title(WorkbenchTabKind kind) => Strings.Get(kind switch
    {
        WorkbenchTabKind.Diffs => TCodeLangResourceKind.TabChanges,
        WorkbenchTabKind.Browser => TCodeLangResourceKind.TabBrowser,
        WorkbenchTabKind.Terminal => TCodeLangResourceKind.TabTerminal,
        WorkbenchTabKind.History => TCodeLangResourceKind.TabHistory,
        _ => TCodeLangResourceKind.TabFiles,
    });

    public static string Description(WorkbenchTabKind kind) => Strings.Get(kind switch
    {
        WorkbenchTabKind.Browser => TCodeLangResourceKind.BrowserDescription,
        WorkbenchTabKind.Terminal => TCodeLangResourceKind.TerminalDescription,
        WorkbenchTabKind.History => TCodeLangResourceKind.HistoryDescription,
        _ => TCodeLangResourceKind.ChangesDescription,
    });

    public static LucideKind Icon(WorkbenchTabKind kind) => kind switch
    {
        WorkbenchTabKind.Browser => LucideKind.Globe,
        WorkbenchTabKind.Terminal => LucideKind.SquareTerminal,
        WorkbenchTabKind.Diffs => LucideKind.GitCompare,
        WorkbenchTabKind.History => LucideKind.History,
        _ => LucideKind.File,
    };
}

/// <summary>编辑区的一个标签页：文件，或尚未实现的工具面板（浏览器 / 终端 / 变更 / 历史）。</summary>
public abstract partial class WorkbenchTabViewModel(WorkbenchTabKind kind) : ObservableObject
{
    public WorkbenchTabKind Kind { get; } = kind;
    public abstract string Title { get; }
    public virtual LucideKind Icon => WorkbenchTexts.Icon(Kind);
    public virtual string IconColor => "TCode.TextSecondary";

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    public virtual void RefreshLanguage() => OnPropertyChanged(nameof(Title));
}

public sealed class PlaceholderTabViewModel(WorkbenchTabKind kind) : WorkbenchTabViewModel(kind)
{
    public override string Title => WorkbenchTexts.Title(Kind);
    public string Description => WorkbenchTexts.Description(Kind);

    public override void RefreshLanguage()
    {
        base.RefreshLanguage();
        OnPropertyChanged(nameof(Description));
    }
}

/// <summary>面包屑的一段：项目内相对路径的目录或文件名。</summary>
public sealed record BreadcrumbItem(string Text, bool ShowSeparator);

/// <summary>“+”菜单中的一个工具面板。</summary>
public sealed record WorkbenchToolViewModel(WorkbenchTabKind Kind)
{
    public string Title => WorkbenchTexts.Title(Kind);
    public LucideKind Icon => WorkbenchTexts.Icon(Kind);
}

/// <summary>文件标签工具栏中的一种查看方式（预览 / 源码……），对应一个 <see cref="IFilePreviewProvider"/>。</summary>
public sealed partial class FileViewOption(IFilePreviewProvider provider) : ObservableObject
{
    public IFilePreviewProvider Provider { get; } = provider;
    public LucideKind Icon => Provider.Icon;
    public string Title => Strings.Get(Provider.Title);

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public void RefreshLanguage() => OnPropertyChanged(nameof(Title));
}

/// <summary>
/// 只读文件查看。打开时探测文件（大小、文件头），由 <see cref="FilePreviewRegistry"/> 选出可用的查看方式：
/// 第一个为默认视图，多于一个时在工具栏切换（如 Markdown 的预览 / 源码，SVG 的图片 / 源码）。
/// 没有任何预览器能处理时显示提示（过大 / 二进制 / 读取失败）。
/// </summary>
public sealed partial class FileTabViewModel : WorkbenchTabViewModel, IDisposable
{
    private readonly FilePreviewRegistry _registry;
    private readonly Dictionary<IFilePreviewProvider, FilePreviewViewModel> _previews = [];
    private FileProbe? _probe;
    private CancellationTokenSource _cts = new();

    public FileTabViewModel(string path, string? root, FilePreviewRegistry? registry = null) : base(WorkbenchTabKind.File)
    {
        _registry = registry ?? FilePreviewRegistry.Default;
        FilePath = path;
        Name = System.IO.Path.GetFileName(path);
        var relative = root is not null && path.StartsWith(root, StringComparison.Ordinal) ? System.IO.Path.GetRelativePath(root, path) : Name;
        Breadcrumbs = relative.Split(System.IO.Path.DirectorySeparatorChar).Select((part, i) => new BreadcrumbItem(part, i > 0)).ToArray();
        (FileIcon, FileColor) = FileIcons.For(Name, false);
        _ = LoadAsync();
    }

    public string FilePath { get; }
    public string Name { get; }
    public override string Title => Name;
    public LucideKind FileIcon { get; }
    public string FileColor { get; }
    public override LucideKind Icon => FileIcon;
    public override string IconColor => FileColor;
    public IReadOnlyList<BreadcrumbItem> Breadcrumbs { get; }

    /// <summary>可用的查看方式（按优先级）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasViewSwitch))]
    public partial IReadOnlyList<FileViewOption> Views { get; private set; } = [];

    public bool HasViewSwitch => Views.Count > 1;

    /// <summary>当前视图的内容（具体类型决定使用哪个 DataTemplate）。</summary>
    [ObservableProperty]
    public partial FilePreviewViewModel? Preview { get; private set; }

    /// <summary>无法显示内容时的说明（过大 / 二进制 / 读取或解码失败）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string? Notice { get; private set; }

    public bool HasNotice => Notice is not null;

    [RelayCommand]
    private async Task SelectView(FileViewOption option)
    {
        if (_probe is null || option.IsSelected)
        {
            return;
        }
        foreach (var view in Views)
        {
            view.IsSelected = view == option;
        }
        await ShowAsync(_probe, option.Provider, _cts.Token);
    }

    public override void RefreshLanguage()
    {
        base.RefreshLanguage();
        foreach (var view in Views)
        {
            view.RefreshLanguage();
        }
    }

    /// <summary>重新读取文件（保留当前查看方式）。</summary>
    public Task ReloadAsync()
    {
        var current = Views.FirstOrDefault(v => v.IsSelected)?.Provider;
        return LoadAsync(current);
    }

    private async Task LoadAsync(IFilePreviewProvider? preferred = null)
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        ClearPreviews();
        try
        {
            var probe = await FileProbe.ReadAsync(FilePath, token);
            _probe = probe;
            var views = _registry.Match(probe).Select(p => new FileViewOption(p)).ToArray();
            Views = views;
            if (views.Length == 0)
            {
                Notice = probe.IsBinary || probe.Length <= CodePreviewProvider.MaxBytes
                    ? Strings.Get(TCodeLangResourceKind.BinaryFile)
                    : Strings.Format(TCodeLangResourceKind.FileTooLargeFormat, $"{probe.Length / 1024.0 / 1024.0:0.0} MB");
                return;
            }
            var selected = views.FirstOrDefault(v => v.Provider == preferred) ?? views[0];
            selected.IsSelected = true;
            await ShowAsync(probe, selected.Provider, token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notice = ex.Message;
        }
    }

    private async Task ShowAsync(FileProbe probe, IFilePreviewProvider provider, CancellationToken token)
    {
        try
        {
            if (!_previews.TryGetValue(provider, out var preview))
            {
                preview = await provider.CreateAsync(probe, token);
                if (token.IsCancellationRequested)
                {
                    preview.Dispose();
                    return;
                }
                _previews[provider] = preview;
            }
            if (Views.FirstOrDefault(v => v.IsSelected)?.Provider == provider)
            {
                Preview = preview;
                Notice = null;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Preview = null;
            Notice = ex.Message;
        }
    }

    private void ClearPreviews()
    {
        Preview = null;
        foreach (var preview in _previews.Values)
        {
            preview.Dispose();
        }
        _previews.Clear();
    }

    public void Dispose()
    {
        _cts.Cancel();
        ClearPreviews();
    }
}

/// <summary>
/// 编辑区：文件与工具面板的标签页。“+”菜单添加浏览器 / 终端 / 变更 / 历史；可最大化（隐藏对话栏）。
/// 文件从右侧文件树打开；同一文件只打开一个标签。
/// </summary>
public sealed partial class WorkbenchViewModel : ObservableObject
{
    private readonly PtyHostService _hosts;

    public WorkbenchViewModel(PtyHostService hosts)
    {
        _hosts = hosts;
        Tools = [new(WorkbenchTabKind.Browser), new(WorkbenchTabKind.Terminal), new(WorkbenchTabKind.Diffs), new(WorkbenchTabKind.History)];
        Tabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTabs));
        Strings.LanguageChanged += (_, _) =>
        {
            foreach (var tab in Tabs)
            {
                tab.RefreshLanguage();
            }
        };
    }

    public IReadOnlyList<WorkbenchToolViewModel> Tools { get; }
    public ObservableCollection<WorkbenchTabViewModel> Tabs { get; } = [];
    public bool HasTabs => Tabs.Count > 0;

    /// <summary>文件树的根目录（用于面包屑的相对路径）。</summary>
    public string? Root { get; set; }

    /// <summary>当前文件变化时通知文件树高亮。</summary>
    public event Action<string?>? ActiveFileChanged;

    /// <summary>需要显示编辑区（打开了文件或面板）。</summary>
    public event Action? OpenRequested;

    /// <summary>最后一个标签被关闭，编辑区应收起。</summary>
    public event Action? Emptied;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveFile))]
    public partial WorkbenchTabViewModel? ActiveTab { get; private set; }

    public FileTabViewModel? ActiveFile => ActiveTab as FileTabViewModel;
    public string? ActiveFilePath => ActiveFile?.FilePath;

    [ObservableProperty]
    public partial bool IsMaximized { get; set; }

    partial void OnActiveTabChanged(WorkbenchTabViewModel? value) => ActiveFileChanged?.Invoke(ActiveFilePath);

    public void OpenFile(string path)
    {
        var tab = Tabs.OfType<FileTabViewModel>().FirstOrDefault(t => t.FilePath == path);
        if (tab is null)
        {
            tab = new FileTabViewModel(path, Root);
            Tabs.Add(tab);
        }
        Activate(tab);
        OpenRequested?.Invoke();
    }

    [RelayCommand]
    private void AddTool(WorkbenchToolViewModel tool)
    {
        if (tool.Kind == WorkbenchTabKind.Terminal)
        {
            // 每次都新建一个终端（01 §7），在项目目录中启动
            var terminal = new TerminalTabViewModel(_hosts, Root);
            terminal.Closed += () => RemoveTab(terminal);
            Tabs.Add(terminal);
            Activate(terminal);
            OpenRequested?.Invoke();
            return;
        }
        var existing = Tabs.FirstOrDefault(t => t.Kind == tool.Kind);
        if (existing is null)
        {
            existing = new PlaceholderTabViewModel(tool.Kind);
            Tabs.Add(existing);
        }
        Activate(existing);
        OpenRequested?.Invoke();
    }

    [RelayCommand]
    private void Activate(WorkbenchTabViewModel tab)
    {
        foreach (var t in Tabs)
        {
            t.IsActive = t == tab;
        }
        ActiveTab = tab;
    }

    [RelayCommand]
    private void Close(WorkbenchTabViewModel tab)
    {
        if (tab is TerminalTabViewModel terminal)
        {
            // 终端的关闭需要 host 确认：有程序在运行时不关闭，标签保留并显示确认栏；关闭成功时由 Closed 移除标签
            _ = terminal.CloseAsync();
            return;
        }
        RemoveTab(tab);
    }

    private void RemoveTab(WorkbenchTabViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        if (ActiveTab == tab)
        {
            ActiveTab = null;
            if (Tabs.Count > 0)
            {
                Activate(Tabs[Math.Clamp(index, 0, Tabs.Count - 1)]);
            }
        }
        if (Tabs.Count == 0)
        {
            IsMaximized = false;
            Emptied?.Invoke();
        }
        // 释放预览资源（解码后的图片帧等）
        (tab as IDisposable)?.Dispose();
    }

    [RelayCommand]
    private void ToggleMaximize() => IsMaximized = !IsMaximized;
}
