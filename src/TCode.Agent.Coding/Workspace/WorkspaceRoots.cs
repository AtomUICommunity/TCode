namespace TCode.Agent.Coding.Workspace;

/// <summary>
/// 会话的工作区：cwd 加上额外加入的目录。只读工具在工作区内免审批；
/// TCode 自己的配置（含 API Key）即使位于工作区内也视为敏感文件。
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
        var home = PathUtil.RealPath(TCodePaths.Home);
        return SensitiveFiles.Any(name => PathUtil.SamePath(real, Path.Combine(home, name)));
    }

    private static readonly string[] SensitiveFiles = ["settings.json", "state.json"];
}
