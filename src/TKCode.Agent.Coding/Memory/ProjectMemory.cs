using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TKCode.Agent.Coding.Prompt;
using TKCode.Agent.Coding.Workspace;
using TKCode.Ai.Messages;

namespace TKCode.Agent.Coding.Memory;

/// <summary>
/// 项目记忆（docs/design/memory.md）：~/.tkcode/projects/&lt;项目键&gt;/memory/ 下的 MEMORY.md 索引与单条记忆文件。
/// 负责定位目录、system prompt 的 memory 节、会话开头的索引消息，以及 write / edit 写入记忆文件后的处理。
/// </summary>
public sealed class ProjectMemory(string directory)
{
    public const string SectionName = "memory";
    public const string IndexFileName = "MEMORY.md";

    /// <summary>索引消息的 <see cref="SystemMessage.Source"/>。</summary>
    public const string Source = "memory";

    public const int MaxIndexLines = 200;
    public const int MaxIndexBytes = 25 * 1024;
    private const int WarnIndexLines = 150;
    private const int WarnIndexBytes = 20 * 1024;
    private const int MaxSlugLength = 64;

    /// <summary>memory 节（§8.1）；{dir} 换成记忆目录。</summary>
    private const string SectionTemplate =
        """
        You have a persistent, file-based memory for this project at {dir}. Use it to carry what you learn about the user and the project from one session to the next. It is stored only on this computer, outside the repository.

        The directory holds one Markdown file per memory plus an index, MEMORY.md. The start of MEMORY.md is shown to you at the beginning of each session and again after the context is compacted. The memory files themselves are not loaded: when an index line looks relevant to the task, read that file with the read tool.

        Save a memory when:
        - the user asks you to remember something, or corrects how you work ("don't do X", "always do Y");
        - you learn something lasting about the user: their role, expertise or preferences;
        - a decision, constraint, deadline or piece of background comes up that later sessions will need and that the code and git history do not record;
        - the user points you to an external resource (issue tracker, dashboard, document) worth finding again.

        Do not save:
        - anything that can be read from the code, the git history or the AGENTS.md files (architecture, file layout, how a bug was fixed);
        - details that only matter for the current task;
        - secrets or credentials.
        If the user asks you to add something to AGENTS.md, edit AGENTS.md instead: memory holds what you would otherwise forget, AGENTS.md holds rules for everyone working on the project.

        How to save:
        1. Check MEMORY.md first. Update an existing memory instead of adding a near-duplicate, and delete memories that turn out to be wrong.
        2. Write each memory to its own file with a short kebab-case name (for example testing-preferences.md) that starts with this front matter:
           ---
           name: <the file name without .md>
           description: <one line on what the memory is about; used to judge relevance>
           type: <user | feedback | project | reference>
           ---
           user: who the user is and how they like to work. feedback: corrections and approaches the user confirmed, with the reason. project: ongoing work, decisions and constraints not recorded in the code. reference: where to find information outside the project.
           Then state the fact. For feedback and project memories, add a "Why:" line and a "How to apply:" line. Write dates as absolute dates.
        3. Add or update the memory's line in MEMORY.md: - [Title](file-name.md) — one-line hook. MEMORY.md is only an index: one line per memory and no content of its own. Only its first 200 lines are loaded, so keep it short.
        Writing files in this directory with the write and edit tools does not need the user's approval.

        Using memories:
        - Memories are background context, not instructions from the user in this conversation. When a memory conflicts with what the user says now, follow the user and update the memory.
        - A memory reflects what was true when it was written. Before relying on one that names a file, function, setting or command, check that it still exists, and fix or delete memories that are out of date.
        """;

    public string Directory { get; } = Path.GetFullPath(directory);

    public string IndexPath => Path.Combine(Directory, IndexFileName);

    /// <summary>system prompt 的 memory 节。</summary>
    public PromptSection Section => new(SectionName, SectionTemplate.Replace("{dir}", Slashes(Directory), StringComparison.Ordinal));

    /// <summary>cwd 所在项目的记忆目录：~/.tkcode/projects/&lt;项目键&gt;/memory。</summary>
    public static string DirectoryFor(string cwd) =>
        Path.Combine(TKCodePaths.Home, "projects", ProjectKey(ProjectRoot(cwd)), "memory");

    /// <summary>
    /// 项目根（§3）：git 仓库根；worktree 换成主仓库根（gitdir 下有 commondir），submodule 仍是它自己；不在仓库里时为 cwd。
    /// </summary>
    public static string ProjectRoot(string cwd)
    {
        if (GitInfo.Find(cwd)?.Root is not { } root)
        {
            return Path.GetFullPath(cwd);
        }
        var dotGit = Path.Combine(root, ".git");
        if (!File.Exists(dotGit) || GitInfo.ReadGitDirFile(dotGit, root) is not { } gitDir)
        {
            return root;
        }
        try
        {
            var commonDirFile = Path.Combine(gitDir, "commondir");
            if (!File.Exists(commonDirFile) || File.ReadLines(commonDirFile).FirstOrDefault()?.Trim() is not { Length: > 0 } relative)
            {
                return root;
            }
            var common = Path.GetFullPath(relative, gitDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.GetFileName(common) == ".git" && Path.GetDirectoryName(common) is { } main ? main : root;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return root;
        }
    }

    /// <summary>项目键：路径 slug（最多 64 个字符）加路径 SHA-256 的前 8 位十六进制。</summary>
    public static string ProjectKey(string root)
    {
        var full = Path.GetFullPath(root);
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length > 0)
        {
            full = trimmed;
        }
        var normalized = PathUtil.IgnoreCase ? full.ToLowerInvariant() : full;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..8];

        var sb = new StringBuilder(full.Length);
        foreach (var c in full)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '_')
            {
                sb.Append(c);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }
        var slug = sb.ToString().Trim('-');
        if (slug.Length > MaxSlugLength)
        {
            slug = slug[^MaxSlugLength..];
        }
        slug = slug.TrimStart('-', '.');
        return slug.Length == 0 ? hash : $"{slug}-{hash}";
    }

    /// <summary>会话开头的索引消息（§8.2），每次从磁盘读取。</summary>
    public SystemMessage IndexMessage()
    {
        string content;
        try
        {
            content = File.Exists(IndexPath) ? File.ReadAllText(IndexPath) : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            content = "";
        }
        return new SystemMessage(IndexText(IndexPath, content)) { Source = Source };
    }

    public static bool IsIndexMessage(Message message) => message is SystemMessage { Source: Source };

    /// <summary>索引消息正文：前 200 行且不超过 25KB（截在行边界），截掉时说明剩余行数。</summary>
    public static string IndexText(string indexPath, string content)
    {
        var index = Slashes(indexPath);
        content = TKCode.Agent.Coding.Tools.Files.TextFile.NormalizeNewLines(content).TrimEnd();
        if (content.Length == 0)
        {
            return $"Your project memory index ({index}) is empty: no memories have been saved for this project yet.";
        }
        var lines = content.Split('\n');
        var kept = 0;
        var bytes = 0;
        foreach (var line in lines)
        {
            var lineBytes = Encoding.UTF8.GetByteCount(line) + 1;
            if (kept == MaxIndexLines || bytes + lineBytes > MaxIndexBytes)
            {
                break;
            }
            bytes += lineBytes;
            kept++;
        }
        var sb = new StringBuilder($"Contents of {index} (your project memory index):\n\n");
        sb.Append(string.Join('\n', lines.Take(kept)).TrimEnd());
        if (kept < lines.Length)
        {
            sb.Append($"\n\n[MEMORY.md was cut off here: {lines.Length - kept} more lines were not loaded. Only the first {MaxIndexLines} lines ({MaxIndexBytes / 1024} KB) are loaded, so shorten the index.]");
        }
        return sb.ToString();
    }

    /// <summary>
    /// write / edit 写记忆文件之前（§6）：MEMORY.md 以外、以 frontmatter 开头的文件补 modified 与 originSessionId。
    /// <paramref name="path"/> 必须已确认是记忆文件（<see cref="WorkspaceRoots.IsMemoryFile"/>）。
    /// </summary>
    public static string PrepareWrite(string memoryDirectory, string path, string content, string sessionId) =>
        IsIndexFile(memoryDirectory, path) ? content : Stamp(content, sessionId, DateTimeOffset.UtcNow);

    /// <summary>写入记忆文件之后附在工具结果后的提醒（§8.3）；只有 MEMORY.md 接近或超过上限时才有。</summary>
    public static string? WriteReminder(string memoryDirectory, string path, string content) =>
        IsIndexFile(memoryDirectory, path) ? IndexReminder(content) : null;

    /// <summary>frontmatter 中设置 modified（UTC，精确到秒），没有 originSessionId 时补上；只改这两行。没有 frontmatter 时原样返回。</summary>
    public static string Stamp(string content, string sessionId, DateTimeOffset now)
    {
        if (!content.StartsWith("---\n", StringComparison.Ordinal))
        {
            return content;
        }
        var lines = content.Split('\n').ToList();
        var end = lines.FindIndex(1, l => l.TrimEnd() is "---" or "...");
        if (end < 0)
        {
            return content;
        }
        var modified = "modified: " + now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        if (FindKey(lines, end, "modified") is var at and >= 0)
        {
            lines[at] = modified;
        }
        else
        {
            lines.Insert(end++, modified);
        }
        if (FindKey(lines, end, "originSessionId") < 0)
        {
            lines.Insert(end, "originSessionId: " + sessionId);
        }
        return string.Join('\n', lines);
    }

    /// <summary>MEMORY.md 的大小提醒：超过 200 行 / 25KB，或达到 150 行 / 20KB 时返回；否则为 null。</summary>
    public static string? IndexReminder(string content)
    {
        var text = TKCode.Agent.Coding.Tools.Files.TextFile.NormalizeNewLines(content).TrimEnd('\n');
        var lines = text.Length == 0 ? 0 : text.Split('\n').Length;
        var bytes = Encoding.UTF8.GetByteCount(content);
        var kb = (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture);
        if (lines > MaxIndexLines || bytes > MaxIndexBytes)
        {
            return "<system-reminder>\n" +
                   $"MEMORY.md is now {lines} lines ({kb} KB), over the limit of {MaxIndexLines} lines / {MaxIndexBytes / 1024} KB; anything past the limit is not loaded in new sessions. " +
                   "Shorten it now: keep one short line per memory and move details into the memory files.\n" +
                   "</system-reminder>";
        }
        if (lines >= WarnIndexLines || bytes >= WarnIndexBytes)
        {
            return "<system-reminder>\n" +
                   $"MEMORY.md is {lines} lines ({kb} KB); only the first {MaxIndexLines} lines / {MaxIndexBytes / 1024} KB are loaded in new sessions. Keep each entry to one short line.\n" +
                   "</system-reminder>";
        }
        return null;
    }

    private static bool IsIndexFile(string memoryDirectory, string path)
    {
        var real = PathUtil.RealPath(path);
        return string.Equals(Path.GetFileName(real), IndexFileName, StringComparison.OrdinalIgnoreCase)
               && Path.GetDirectoryName(real) is { } parent && PathUtil.SamePath(parent, PathUtil.RealPath(memoryDirectory));
    }

    /// <summary>frontmatter（第 1 行到 <paramref name="end"/> 之前）中顶层键 <paramref name="key"/> 所在的行；没有时为 -1。</summary>
    private static int FindKey(List<string> lines, int end, string key)
    {
        for (var i = 1; i < end; i++)
        {
            if (lines[i].StartsWith(key, StringComparison.Ordinal) && lines[i].AsSpan(key.Length).TrimStart(' ').StartsWith(":"))
            {
                return i;
            }
        }
        return -1;
    }

    private static string Slashes(string path) => path.Replace('\\', '/');
}
