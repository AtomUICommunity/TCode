namespace TKCode.Desktop.Services;

/// <summary>
/// 监视仓库的 HEAD、packed-refs 与 refs/heads：在终端 / Agent / 外部工具里切换、新建或删除分支后触发回调，
/// 让底部分支选择器保持最新。worktree 的 HEAD 与分支分处两个目录，两处都监视。回调在线程池线程上、去抖之后调用。
/// </summary>
public sealed class GitBranchWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);

    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Timer _timer;

    public GitBranchWatcher(string repository, Action changed)
    {
        _timer = new Timer(_ => changed(), null, Timeout.Infinite, Timeout.Infinite);
        if (GitLayout.Find(repository) is not { } layout)
        {
            return;
        }
        // git 目录只关心 HEAD（git switch 先写 HEAD.lock 再改名）与 packed-refs（git gc / pack-refs 之后）
        if (layout.CommonDir == layout.GitDir)
        {
            Watch(layout.GitDir, includeSubdirectories: false, name => name is "HEAD" or "packed-refs");
        }
        else
        {
            Watch(layout.GitDir, includeSubdirectories: false, name => name is "HEAD");
            Watch(layout.CommonDir, includeSubdirectories: false, name => name is "packed-refs");
        }
        var heads = Path.Combine(layout.CommonDir, "refs", "heads");
        if (Directory.Exists(heads))
        {
            Watch(heads, includeSubdirectories: true, _ => true);
        }
    }

    private void Watch(string directory, bool includeSubdirectories, Func<string?, bool> relevant)
    {
        try
        {
            var watcher = new FileSystemWatcher(directory)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = includeSubdirectories,
            };
            watcher.Changed += (_, e) => Schedule(e.Name, relevant);
            watcher.Created += (_, e) => Schedule(e.Name, relevant);
            watcher.Deleted += (_, e) => Schedule(e.Name, relevant);
            watcher.Renamed += (_, e) => Schedule(e.Name, relevant);
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            // 监视失败（权限 / inotify 上限）不影响功能：打开分支弹层时仍会重新读取
        }
    }

    private void Schedule(string? name, Func<string?, bool> relevant)
    {
        if (relevant(name))
        {
            try
            {
                _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }
        _watchers.Clear();
        _timer.Dispose();
    }
}
