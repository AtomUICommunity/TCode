using System.Text.RegularExpressions;

namespace TKCode.Agent.Coding.Tools.Search;

/// <summary>
/// 没有 rg 时的目录遍历：遵守各级 .gitignore（含否定规则、目录规则、锚定规则），跳过 .git，包含点文件。
/// 只实现 gitignore 的常用子集，足够用于搜索兜底。
/// </summary>
public static class FileWalker
{
    public readonly record struct Entry(string FullPath, string RelativePath);

    public static IEnumerable<Entry> EnumerateFiles(string root, CancellationToken cancellationToken)
    {
        var stack = new Stack<(string Dir, string Relative, IReadOnlyList<IgnoreRule> Rules)>();
        stack.Push((root, "", LoadRules(root, "", [])));
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (dir, relative, rules) = stack.Pop();
            IEnumerable<FileSystemInfo> children;
            try
            {
                children = new DirectoryInfo(dir).EnumerateFileSystemInfos().OrderBy(c => c.Name, StringComparer.Ordinal).ToArray();
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }
            var subdirs = new List<(string, string)>();
            foreach (var child in children)
            {
                var childRelative = relative.Length == 0 ? child.Name : relative + "/" + child.Name;
                var isDir = child is DirectoryInfo;
                if (isDir && child.Name == ".git")
                {
                    continue;
                }
                if (IsIgnored(rules, childRelative, isDir))
                {
                    continue;
                }
                if (isDir)
                {
                    // 不跟随目录符号链接，避免循环。
                    if (child.LinkTarget is null)
                    {
                        subdirs.Add((child.FullName, childRelative));
                    }
                }
                else
                {
                    yield return new Entry(child.FullName, childRelative);
                }
            }
            for (var i = subdirs.Count - 1; i >= 0; i--)
            {
                var (full, rel) = subdirs[i];
                stack.Push((full, rel, LoadRules(full, rel, rules)));
            }
        }
    }

    private sealed record IgnoreRule(Regex Regex, bool Negate, bool DirectoryOnly);

    private static IReadOnlyList<IgnoreRule> LoadRules(string dir, string relative, IReadOnlyList<IgnoreRule> inherited)
    {
        var file = Path.Combine(dir, ".gitignore");
        if (!File.Exists(file))
        {
            return inherited;
        }
        var rules = new List<IgnoreRule>(inherited);
        string[] lines;
        try
        {
            lines = File.ReadAllLines(file);
        }
        catch (IOException)
        {
            return inherited;
        }
        var prefix = relative.Length == 0 ? "" : Regex.Escape(relative) + "/";
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            var negate = line.StartsWith('!');
            if (negate)
            {
                line = line[1..];
            }
            if (line.StartsWith("\\#", StringComparison.Ordinal) || line.StartsWith("\\!", StringComparison.Ordinal))
            {
                line = line[1..];
            }
            var directoryOnly = line.EndsWith('/');
            line = line.TrimEnd('/');
            if (line.Length == 0)
            {
                continue;
            }
            var anchored = line.Contains('/');
            var body = GlobPattern.Translate(line.TrimStart('/'));
            var pattern = anchored ? $"^{prefix}{body}$" : $"^{prefix}(?:.*/)?{body}$";
            try
            {
                rules.Add(new IgnoreRule(new Regex(pattern, RegexOptions.CultureInvariant), negate, directoryOnly));
            }
            catch (ArgumentException)
            {
            }
        }
        return rules;
    }

    private static bool IsIgnored(IReadOnlyList<IgnoreRule> rules, string relative, bool isDirectory)
    {
        var ignored = false;
        foreach (var rule in rules)
        {
            if (rule.DirectoryOnly && !isDirectory)
            {
                continue;
            }
            if (rule.Regex.IsMatch(relative))
            {
                ignored = !rule.Negate;
            }
        }
        return ignored;
    }
}
