namespace TKCode.Agent.Coding.Workspace;

/// <summary>
/// 会话的工作区：cwd 加上额外加入的目录。只读工具在工作区内免审批；
/// TKCode 自己的配置（含 API Key）即使位于工作区内也视为敏感文件。
/// </summary>
public sealed class WorkspaceRoots
{
    private readonly Lock _gate = new();
    private readonly List<string> _additional = [];
    private string[] _realRoots;

    public WorkspaceRoots(string cwd, IEnumerable<string>? additional = null)
    {
        Cwd = Path.GetFullPath(cwd);
        _realRoots = [PathUtil.RealPath(Cwd)];
        foreach (var dir in additional ?? [])
        {
            Add(dir);
        }
    }

    public string Cwd { get; }

    public IReadOnlyList<string> Additional
    {
        get
        {
            lock (_gate)
            {
                return _additional.ToArray();
            }
        }
    }

    public void Add(string directory)
    {
        var full = Path.GetFullPath(directory, Cwd);
        lock (_gate)
        {
            if (_additional.Contains(full, PathUtil.Comparer) || PathUtil.SamePath(full, Cwd))
            {
                return;
            }
            _additional.Add(full);
            _realRoots = [.. _realRoots, PathUtil.RealPath(full)];
        }
    }

    /// <summary>
    /// 项目记忆目录（docs/design/memory.md §5）：读取视同工作区内，其中的 .md 文件写入在任何档位都免审批。null 表示未启用。
    /// </summary>
    public string? MemoryDirectory
    {
        get
        {
            lock (_gate)
            {
                return _memoryDirectory;
            }
        }
        set
        {
            var full = value is null ? null : Path.GetFullPath(value);
            var real = full is null ? null : PathUtil.RealPath(full);
            lock (_gate)
            {
                _memoryDirectory = full;
                _realMemoryDirectory = real;
            }
        }
    }

    private string? _memoryDirectory;
    private string? _realMemoryDirectory;

    /// <summary>路径（解析符号链接后）位于记忆目录之内。</summary>
    public bool InMemoryDirectory(string path)
    {
        string? root;
        lock (_gate)
        {
            root = _realMemoryDirectory;
        }
        return root is not null && PathUtil.IsUnder(PathUtil.RealPath(path), root);
    }

    /// <summary>记忆目录内的 .md 文件（按真实路径判断，指向目录外的符号链接不算）。</summary>
    public bool IsMemoryFile(string path)
    {
        string? root;
        lock (_gate)
        {
            root = _realMemoryDirectory;
        }
        if (root is null)
        {
            return false;
        }
        var real = PathUtil.RealPath(path);
        return PathUtil.IsUnder(real, root) && !PathUtil.SamePath(real, root) && real.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
    }

    public string Resolve(string? path) => PathUtil.Resolve(Cwd, path);

    /// <summary>路径（解析符号链接后）位于某个工作区根目录之内。</summary>
    public bool Contains(string path)
    {
        var real = PathUtil.RealPath(path);
        string[] roots;
        lock (_gate)
        {
            roots = _realRoots;
        }
        return roots.Any(root => PathUtil.IsUnder(real, root));
    }

    public static bool IsSensitive(string path)
    {
        var real = PathUtil.RealPath(path);
        var home = PathUtil.RealPath(TKCodePaths.Home);
        return SensitiveFiles.Any(name => PathUtil.SamePath(real, Path.Combine(home, name)));
    }

    private static readonly string[] SensitiveFiles = ["settings.json", "state.json"];
}
