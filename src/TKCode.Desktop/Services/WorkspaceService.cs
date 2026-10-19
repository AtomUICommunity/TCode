namespace TKCode.Desktop.Services;

public sealed record WorkspaceInfo(string Path, string Name)
{
    /// <summary>读取 .git/HEAD 得到当前分支；不是 Git 仓库时返回 null。</summary>
    public string? ReadBranch()
    {
        try
        {
            var head = System.IO.Path.Combine(Path, ".git", "HEAD");
            if (!File.Exists(head))
            {
                return null;
            }
            var text = File.ReadAllText(head).Trim();
            const string prefix = "ref: refs/heads/";
            return text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : text[..Math.Min(7, text.Length)];
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>本地分支（refs/heads 与 packed-refs），按名称排序；不是 Git 仓库时为空。</summary>
    public IReadOnlyList<string> ReadBranches()
    {
        var git = System.IO.Path.Combine(Path, ".git");
        if (!Directory.Exists(git))
        {
            return [];
        }
        var names = new SortedSet<string>(StringComparer.Ordinal);
        try
        {
            var heads = System.IO.Path.Combine(git, "refs", "heads");
            if (Directory.Exists(heads))
            {
                foreach (var file in Directory.EnumerateFiles(heads, "*", SearchOption.AllDirectories))
                {
                    names.Add(System.IO.Path.GetRelativePath(heads, file).Replace(System.IO.Path.DirectorySeparatorChar, '/'));
                }
            }
            var packed = System.IO.Path.Combine(git, "packed-refs");
            if (File.Exists(packed))
            {
                const string prefix = "refs/heads/";
                foreach (var line in File.ReadLines(packed))
                {
                    var space = line.IndexOf(' ');
                    if (space > 0 && line.AsSpan(space + 1).StartsWith(prefix, StringComparison.Ordinal))
                    {
                        names.Add(line[(space + 1 + prefix.Length)..]);
                    }
                }
            }
        }
        catch (IOException)
        {
        }
        return names.ToArray();
    }
}

/// <summary>工作区（侧栏中的 Repositories）。首次启动时从 ~/code 等常见目录发现一些仓库。</summary>
public sealed class WorkspaceService(StateService settings)
{
    public IReadOnlyList<WorkspaceInfo> Load()
    {
        if (settings.Current.Workspaces.Count == 0)
        {
            settings.Current.Workspaces.AddRange(Discover().DistinctBy(p => Path.GetFullPath(p).ToLowerInvariant()));
            settings.Save();
        }
        return settings.Current.Workspaces
            .Where(Directory.Exists)
            .DistinctBy(p => p.ToLowerInvariant())
            .Select(p => new WorkspaceInfo(p, System.IO.Path.GetFileName(p.TrimEnd(System.IO.Path.DirectorySeparatorChar))))
            .ToList();
    }

    /// <summary>
    /// 托盘 / Dock / 跳转列表的项目菜单：最近使用的在前；用过的不足 <paramref name="count"/> 个时按侧栏顺序补齐，
    /// 这样从没切换过项目时菜单也不是空的。
    /// </summary>
    public IReadOnlyList<WorkspaceInfo> Recent(int count)
    {
        var all = Load();
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var ordered = new List<WorkspaceInfo>();
        foreach (var path in settings.Current.RecentWorkspaces)
        {
            if (all.FirstOrDefault(w => string.Equals(w.Path, path, comparison)) is { } known && !ordered.Contains(known))
            {
                ordered.Add(known);
            }
        }
        foreach (var workspace in all)
        {
            if (!ordered.Contains(workspace))
            {
                ordered.Add(workspace);
            }
        }
        return ordered.Take(count).ToList();
    }

    public WorkspaceInfo Add(string path)
    {
        if (!settings.Current.Workspaces.Contains(path))
        {
            settings.Current.Workspaces.Insert(0, path);
            settings.Save();
        }
        return new WorkspaceInfo(path, System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar)));
    }

    private static IEnumerable<string> Discover()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { "code", "Code", "src", "projects", "Projects", "source/repos" })
        {
            var dir = System.IO.Path.Combine(home, root);
            if (!Directory.Exists(dir) || !seen.Add(new DirectoryInfo(dir).FullName))
            {
                continue;
            }
            foreach (var repo in Directory.EnumerateDirectories(dir)
                         .Where(d => Directory.Exists(System.IO.Path.Combine(d, ".git")) || File.Exists(System.IO.Path.Combine(d, ".git")))
                         .OrderByDescending(Directory.GetLastWriteTimeUtc)
                         .Take(12))
            {
                yield return repo;
            }
        }
    }
}
