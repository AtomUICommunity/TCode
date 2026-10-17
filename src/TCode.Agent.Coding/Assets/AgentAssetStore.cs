using TCode.Config;

namespace TCode.Agent.Coding.Assets;

/// <summary>
/// 按项目根缓存 <see cref="AgentAssetCatalog"/>（docs/design/composer.md §5）。
/// 监听全局目录与各项目的 .agents：变化后 300ms 防抖，使缓存失效并触发 <see cref="Changed"/>（参数为项目根；全局变化时为 null）。
/// </summary>
public sealed class AgentAssetStore : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);
    private static readonly string[] Kinds = ["commands", "skills", "roles"];

    private readonly string _home;
    private readonly Dictionary<string, Entry> _projects = new(PathComparer);
    private readonly FileSystemWatcher? _globalWatcher;
    private readonly Timer _globalTimer;
    private AgentAssetCatalog? _globalOnly;

    private static StringComparer PathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <param name="home">全局目录（默认 ~/.tcode，受 TCODE_HOME 控制）。</param>
    public AgentAssetStore(string? home = null)
    {
        _home = home ?? ConfigPaths.Home;
        _globalTimer = new Timer(_ => OnGlobalChanged(), null, Timeout.Infinite, Timeout.Infinite);
        try
        {
            Directory.CreateDirectory(_home);
            // 数据目录里还有会话记录等频繁写入的文件：只关心三个资产目录下的变化
            _globalWatcher = new FileSystemWatcher(_home)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            FileSystemEventHandler handler = (_, e) =>
            {
                if (IsAssetPath(_home, e.FullPath) || e is RenamedEventArgs r && IsAssetPath(_home, r.OldFullPath))
                {
                    _globalTimer.Change(Debounce, Timeout.InfiniteTimeSpan);
                }
            };
            _globalWatcher.Created += handler;
            _globalWatcher.Deleted += handler;
            _globalWatcher.Changed += handler;
            _globalWatcher.Renamed += (s, e) => handler(s, e);
            _globalWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            _globalWatcher = null;
        }
    }

    /// <summary>资产变化（参数为项目根；全局目录变化时为 null，表示所有项目都受影响）。</summary>
    public event Action<string?>? Changed;

    /// <summary>cwd 所在项目生效的 command / skill / role；cwd 为空时只有全局项。</summary>
    public AgentAssetCatalog Get(string? cwd)
    {
        if (cwd is null || !Directory.Exists(cwd))
        {
            lock (_projects)
            {
                return _globalOnly ??= AgentAssetLoader.Load(_home, null);
            }
        }
        var root = AgentAssetLoader.ProjectRoot(cwd);
        lock (_projects)
        {
            if (!_projects.TryGetValue(root, out var entry))
            {
                entry = new Entry(this, root);
                _projects[root] = entry;
            }
            return entry.Catalog ??= AgentAssetLoader.Load(_home, root);
        }
    }

    /// <summary>读取定义文件的正文（去掉 frontmatter）；读取失败时为 null。</summary>
    public static string? ReadBody(string path)
    {
        try
        {
            return Frontmatter.Parse(File.ReadAllText(path)).Body;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsAssetPath(string baseDir, string path)
    {
        var relative = Path.GetRelativePath(baseDir, path).Replace('\\', '/');
        var first = relative.Split('/')[0];
        return Kinds.Contains(first, StringComparer.OrdinalIgnoreCase);
    }

    private void OnGlobalChanged()
    {
        lock (_projects)
        {
            _globalOnly = null;
            foreach (var entry in _projects.Values)
            {
                entry.Catalog = null;
            }
        }
        Changed?.Invoke(null);
    }

    private void OnProjectChanged(Entry entry)
    {
        lock (_projects)
        {
            entry.Catalog = null;
        }
        Changed?.Invoke(entry.Root);
    }

    public void Dispose()
    {
        _globalWatcher?.Dispose();
        _globalTimer.Dispose();
        lock (_projects)
        {
            foreach (var entry in _projects.Values)
            {
                entry.Dispose();
            }
            _projects.Clear();
        }
    }

    /// <summary>
    /// 一个项目：监听项目根（只看 .agents 的创建 / 删除 / 改名）与 .agents 内部（递归）。
    /// .agents 不存在时只有根目录的监听，创建后再挂上内部的监听。
    /// </summary>
    private sealed class Entry : IDisposable
    {
        private readonly Timer _timer;
        private FileSystemWatcher? _rootWatcher;
        private FileSystemWatcher? _agentsWatcher;

        public Entry(AgentAssetStore owner, string root)
        {
            Root = root;
            _timer = new Timer(_ =>
            {
                WatchAgents();
                owner.OnProjectChanged(this);
            }, null, Timeout.Infinite, Timeout.Infinite);
            try
            {
                _rootWatcher = new FileSystemWatcher(root, AgentAssetLoader.ProjectDirectory)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.DirectoryName,
                };
                _rootWatcher.Created += (_, _) => Schedule();
                _rootWatcher.Deleted += (_, _) => Schedule();
                _rootWatcher.Renamed += (_, _) => Schedule();
                _rootWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
            {
                _rootWatcher = null;
            }
            WatchAgents();
        }

        public string Root { get; }

        public AgentAssetCatalog? Catalog { get; set; }

        private void Schedule() => _timer.Change(Debounce, Timeout.InfiniteTimeSpan);

        private void WatchAgents()
        {
            var dir = Path.Combine(Root, AgentAssetLoader.ProjectDirectory);
            if (_agentsWatcher is not null && Directory.Exists(dir))
            {
                return;
            }
            _agentsWatcher?.Dispose();
            _agentsWatcher = null;
            if (!Directory.Exists(dir))
            {
                return;
            }
            try
            {
                _agentsWatcher = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                _agentsWatcher.Created += (_, _) => Schedule();
                _agentsWatcher.Deleted += (_, _) => Schedule();
                _agentsWatcher.Changed += (_, _) => Schedule();
                _agentsWatcher.Renamed += (_, _) => Schedule();
                _agentsWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
            {
                _agentsWatcher = null;
            }
        }

        public void Dispose()
        {
            _rootWatcher?.Dispose();
            _agentsWatcher?.Dispose();
            _timer.Dispose();
        }
    }
}
