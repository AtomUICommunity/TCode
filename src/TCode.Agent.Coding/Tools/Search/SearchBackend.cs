using TCode.Agent.Coding.Processes;

namespace TCode.Agent.Coding.Tools.Search;

/// <summary>搜索后端的可用性：优先 rg，其次 git grep（仅 git 仓库），最后用内置实现。结果在进程内缓存。</summary>
public static class SearchBackend
{
    private static readonly Lazy<string?> Rg = new(() => Environment.GetEnvironmentVariable("TCODE_DISABLE_RG") == "1"
        ? null
        : ProcessRunner.FindExecutable("rg"));

    private static readonly Lazy<string?> Git = new(() => Environment.GetEnvironmentVariable("TCODE_DISABLE_GIT_GREP") == "1"
        ? null
        : ProcessRunner.FindExecutable("git"));

    /// <summary>强制使用某个后端（测试与诊断用）；Auto 按可用性依次回退。</summary>
    public static SearchBackendKind Preferred { get; set; } = SearchBackendKind.Auto;

    public static string? RipgrepPath => Preferred is SearchBackendKind.Auto or SearchBackendKind.Ripgrep ? Rg.Value : null;

    public static string? GitPath => Preferred is SearchBackendKind.Auto or SearchBackendKind.GitGrep ? Git.Value : null;

    /// <summary>相对路径统一用 /，去掉开头的 ./。</summary>
    public static string NormalizeRelative(string path)
    {
        var p = path.Replace('\\', '/');
        return p.StartsWith("./", StringComparison.Ordinal) ? p[2..] : p;
    }
}

public enum SearchBackendKind
{
    Auto,
    Ripgrep,
    GitGrep,
    Managed,
}
