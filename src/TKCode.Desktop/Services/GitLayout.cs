namespace TKCode.Desktop.Services;

/// <summary>
/// 仓库的 git 目录布局。普通仓库的 ".git" 是目录；worktree / submodule / <c>--separate-git-dir</c> 的 ".git" 是一个文件，
/// 内容为 "gitdir: &lt;path&gt;"。linked worktree 的 HEAD 在各自的 git 目录里，而分支（refs/heads、packed-refs）在
/// 它的 "commondir" 指向的主仓库 git 目录里，所以要分开记。
/// </summary>
/// <param name="GitDir">存放 HEAD 的目录（每个 worktree 各自一份）。</param>
/// <param name="CommonDir">存放 refs/heads 与 packed-refs 的目录；普通仓库、submodule 与 <paramref name="GitDir"/> 相同。</param>
public sealed record GitLayout(string GitDir, string CommonDir)
{
    /// <summary>读取 <paramref name="repository"/> 根目录下的 ".git"；不是 Git 仓库或读不出时返回 null。</summary>
    public static GitLayout? Find(string repository)
    {
        try
        {
            var dotGit = Path.Combine(repository, ".git");
            string? gitDir;
            if (Directory.Exists(dotGit))
            {
                gitDir = dotGit;
            }
            else if (File.Exists(dotGit))
            {
                gitDir = ReadGitDirFile(dotGit, repository);
            }
            else
            {
                return null;
            }
            return gitDir is not null && Directory.Exists(gitDir) ? new GitLayout(gitDir, ReadCommonDir(gitDir)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? ReadGitDirFile(string file, string baseDir)
    {
        var line = File.ReadLines(file).FirstOrDefault();
        return line is not null && line.StartsWith("gitdir:", StringComparison.Ordinal)
            ? Path.GetFullPath(line["gitdir:".Length..].Trim(), baseDir)
            : null;
    }

    /// <summary>linked worktree 的 git 目录里有 "commondir"（相对该目录或绝对路径）；没有则自己就是公共目录。</summary>
    private static string ReadCommonDir(string gitDir)
    {
        var file = Path.Combine(gitDir, "commondir");
        if (!File.Exists(file))
        {
            return gitDir;
        }
        var line = File.ReadLines(file).FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(line))
        {
            return gitDir;
        }
        var common = Path.GetFullPath(line, gitDir);
        return Directory.Exists(common) ? common : gitDir;
    }
}
