using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCode.Desktop.Icons;

namespace TCode.Desktop.ViewModels;

/// <summary>文件树中的一行（扁平化显示，按 Depth 缩进）。</summary>
public sealed partial class FileNodeViewModel(string fullPath, string name, bool isDirectory, int depth) : ObservableObject
{
    public string FullPath { get; } = fullPath;
    public string Name { get; } = name;
    public bool IsDirectory { get; } = isDirectory;
    public int Depth { get; } = depth;
    public double Indent => 8 + Depth * 14;
    public LucideKind Icon { get; } = FileIcons.For(name, isDirectory).Icon;
    public string IconColor { get; } = FileIcons.For(name, isDirectory).Color;

    /// <summary>搜索结果中显示的相对目录。</summary>
    public string? Hint { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chevron))]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public LucideKind Chevron => IsExpanded ? LucideKind.ChevronDown : LucideKind.ChevronRight;
}

/// <summary>
/// 右侧文件树：当前会话（或选中项目）的工作目录。目录懒加载；搜索按文件名递归匹配；
/// 默认隐藏 . 开头的项与 bin / obj / node_modules 等构建目录。点击文件在编辑区打开。
/// </summary>
public sealed partial class FileExplorerViewModel : ObservableObject
{
    private const int MaxSearchResults = 300;
    private static readonly HashSet<string> BuildDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "dist", "build", "out", "target", "__pycache__", ".git",
    };

    private readonly WorkbenchViewModel _workbench;
    private CancellationTokenSource? _searchCts;

    public FileExplorerViewModel(WorkbenchViewModel workbench)
    {
        _workbench = workbench;
        workbench.ActiveFileChanged += path => SetSelected(path);
    }

    public ObservableCollection<FileNodeViewModel> VisibleNodes { get; } = [];

    [ObservableProperty]
    public partial string? Root { get; private set; }

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    public partial bool ShowHidden { get; set; }

    public bool HasRoot => Root is not null;
    public bool IsEmpty => VisibleNodes.Count == 0;

    partial void OnSearchChanged(string value) => _ = RefreshAsync();

    partial void OnShowHiddenChanged(bool value) => _ = RefreshAsync();

    partial void OnRootChanged(string? value)
    {
        OnPropertyChanged(nameof(HasRoot));
        _workbench.Root = value;
        Search = "";
        _ = RefreshAsync();
    }

    public void SetRoot(string? root)
    {
        if (root != Root)
        {
            Root = root is not null && Directory.Exists(root) ? root : null;
        }
    }

    [RelayCommand]
    private void ToggleHidden() => ShowHidden = !ShowHidden;

    [RelayCommand]
    private void Activate(FileNodeViewModel node)
    {
        if (node.IsDirectory)
        {
            if (Search.Length > 0)
            {
                // 搜索结果中的目录：清空搜索并在树中展开到该目录
                Search = "";
                Reveal(node.FullPath);
                return;
            }
            Toggle(node);
            return;
        }
        _workbench.OpenFile(node.FullPath);
    }

    /// <summary>展开到指定文件并选中（编辑区“在文件树中定位”）。</summary>
    public void Reveal(string path)
    {
        if (Root is null || !path.StartsWith(Root, StringComparison.Ordinal))
        {
            return;
        }
        if (Search.Length > 0)
        {
            Search = "";
        }
        var relative = Path.GetRelativePath(Root, path).Split(Path.DirectorySeparatorChar);
        var current = Root;
        for (var i = 0; i < relative.Length - 1; i++)
        {
            current = Path.Combine(current, relative[i]);
            if (VisibleNodes.FirstOrDefault(n => n.FullPath == current) is { IsDirectory: true, IsExpanded: false } dir)
            {
                Toggle(dir);
            }
        }
        SetSelected(path);
    }

    private void SetSelected(string? path)
    {
        foreach (var node in VisibleNodes)
        {
            node.IsSelected = node.FullPath == path;
        }
    }

    private void Toggle(FileNodeViewModel node)
    {
        var index = VisibleNodes.IndexOf(node);
        if (node.IsExpanded)
        {
            node.IsExpanded = false;
            while (index + 1 < VisibleNodes.Count && VisibleNodes[index + 1].Depth > node.Depth)
            {
                VisibleNodes.RemoveAt(index + 1);
            }
            return;
        }
        node.IsExpanded = true;
        var children = ReadChildren(node.FullPath, node.Depth + 1);
        for (var i = 0; i < children.Count; i++)
        {
            VisibleNodes.Insert(index + 1 + i, children[i]);
        }
        SetSelected(_workbench.ActiveFilePath);
    }

    private async Task RefreshAsync()
    {
        _searchCts?.Cancel();
        VisibleNodes.Clear();
        if (Root is not { } root)
        {
            OnPropertyChanged(nameof(IsEmpty));
            return;
        }
        if (Search.Trim().Length == 0)
        {
            foreach (var node in ReadChildren(root, 0))
            {
                VisibleNodes.Add(node);
            }
            // 重建树后展开到当前文件，保持高亮
            if (_workbench.ActiveFilePath is { } active)
            {
                Reveal(active);
            }
        }
        else
        {
            var cts = _searchCts = new CancellationTokenSource();
            var query = Search.Trim();
            var showHidden = ShowHidden;
            var results = await Task.Run(() => SearchFiles(root, query, showHidden, cts.Token), cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
            {
                return;
            }
            foreach (var node in results)
            {
                VisibleNodes.Add(node);
            }
        }
        SetSelected(_workbench.ActiveFilePath);
        OnPropertyChanged(nameof(IsEmpty));
    }

    private bool IsHidden(string name, bool isDirectory) =>
        !ShowHidden && (name.StartsWith('.') || (isDirectory && BuildDirectories.Contains(name)) || name == ".DS_Store");

    private List<FileNodeViewModel> ReadChildren(string dir, int depth)
    {
        try
        {
            var info = new DirectoryInfo(dir);
            return info.EnumerateDirectories().Where(d => !IsHidden(d.Name, true)).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(d => new FileNodeViewModel(d.FullName, d.Name, true, depth))
                .Concat(info.EnumerateFiles().Where(f => !IsHidden(f.Name, false)).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(f => new FileNodeViewModel(f.FullName, f.Name, false, depth)))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>按文件名（不区分大小写）递归搜索，跳过构建目录（显示隐藏项时除 .git 外都搜索）。</summary>
    private static List<FileNodeViewModel> SearchFiles(string root, string query, bool showHidden, CancellationToken ct)
    {
        var results = new List<FileNodeViewModel>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0 && results.Count < MaxSearchResults && !ct.IsCancellationRequested)
        {
            var dir = pending.Pop();
            try
            {
                foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                {
                    var isDir = entry is DirectoryInfo;
                    var hidden = entry.Name.StartsWith('.') || (isDir && BuildDirectories.Contains(entry.Name));
                    if (entry.Name == ".git" || (!showHidden && hidden))
                    {
                        continue;
                    }
                    if (entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        var parent = Path.GetRelativePath(root, Path.GetDirectoryName(entry.FullName)!);
                        results.Add(new FileNodeViewModel(entry.FullName, entry.Name, isDir, 0) { Hint = parent == "." ? null : parent });
                    }
                    if (isDir)
                    {
                        pending.Push(entry.FullName);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return results.OrderBy(r => r.IsDirectory ? 1 : 0).ThenBy(r => r.Name.Length).ToList();
    }
}
