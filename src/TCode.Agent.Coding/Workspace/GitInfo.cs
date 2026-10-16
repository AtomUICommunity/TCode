namespace TCode.Agent.Coding.Workspace;

/// <summary>工作目录所在的 git 仓库：根目录（含 .git 的目录）与当前分支。直接读文件，不启动 git 进程。</summary>
public sealed record GitInfo(string Root, string? Branch)
{
    public static GitInfo? Find(string cwd)
    {
        for (var dir = new DirectoryInfo(cwd); dir is not null; dir = dir.Parent)
        {
            var dotGit = Path.Combine(dir.FullName, ".git");
            string? gitDir = null;
            if (Directory.Exists(dotGit))
            {
                gitDir = dotGit;
            }
            else if (File.Exists(dotGit))
            {
                // worktree / submodule：".git" 文件内容为 "gitdir: <path>"
                gitDir = ReadGitDirFile(dotGit, dir.FullName);
            }
            else
            {
                continue;
            }
            return new GitInfo(dir.FullName, gitDir is null ? null : ReadBranch(gitDir));
        }
        return null;
    }

    private static string? ReadGitDirFile(string file, string baseDir)
    {
        try
        {
            var line = File.ReadLines(file).FirstOrDefault();
            return line is not null && line.StartsWith("gitdir:", StringComparison.Ordinal)
                ? Path.GetFullPath(line["gitdir:".Length..].Trim(), baseDir)
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string? ReadBranch(string gitDir)
    {
        try
        {
            var head = File.ReadLines(Path.Combine(gitDir, "HEAD")).FirstOrDefault()?.Trim();
            if (head is null)
            {
                return null;
            }
            const string prefix = "ref: refs/heads/";
            if (head.StartsWith(prefix, StringComparison.Ordinal))
            {
                return head[prefix.Length..];
            }
            return head.Length >= 7 ? $"detached at {head[..7]}" : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
