using System.Runtime.InteropServices;
using System.Text;
using TKCode.Agent.Coding.Workspace;
using TKCode.Ai.Models;

namespace TKCode.Agent.Coding.Prompt;

/// <summary>系统提示的一节。identity 节不加标签，其余渲染为 &lt;name&gt;…&lt;/name&gt;。</summary>
public sealed record PromptSection(string Name, string Text);

/// <summary>
/// 按节构建内置 Agent 的系统提示（docs/design/builtin-agent-tools.md §3）：
/// identity → tools → rules → environment → project_context → cwd。
/// </summary>
public static class SystemPromptBuilder
{
    /// <summary>每个目录只取第一个命中的上下文文件。</summary>
    public static readonly string[] ContextFileNames = ["AGENTS.override.md", "AGENTS.md"];

    /// <summary>project_context 节的总预算（UTF-8 字节）。</summary>
    public const int ContextBudgetBytes = 32 * 1024;

    private const string Identity =
        "You are TKCode, an expert coding assistant running inside the TKCode desktop app on the user's computer. " +
        "You help users by reading files, running commands, editing code and writing new files.";

    private static readonly string[] GeneralRules =
    [
        "Be concise in your responses.",
        "Show file paths clearly when working with files.",
        "Never revert changes you did not make unless the user explicitly asks; the workspace may contain edits by the user or other agents.",
        "Do not run destructive commands (for example `git reset --hard`, `git checkout --`, `git clean -f`, `git push --force`, `rm -rf`) unless the user explicitly asks.",
        "In user messages, @path refers to a file or directory in the workspace, relative to the working directory. Read it with your tools when you need its contents; the message only carries the path.",
    ];

    public static IReadOnlyList<PromptSection> Build(WorkspaceRoots workspace, IReadOnlyList<IAgentTool> tools, ModelInfo model,
        DateOnly? today = null, IReadOnlyList<PromptSection>? extra = null)
    {
        var sections = new List<PromptSection> { new("identity", Identity) };

        var snippets = tools.Where(t => t.PromptSnippet is not null).Select(t => $"- {t.Name}: {t.PromptSnippet}").ToList();
        if (snippets.Count > 0)
        {
            sections.Add(new("tools", "Available tools:\n" + string.Join('\n', snippets)));
        }

        var rules = GeneralRules.Concat(tools.SelectMany(t => t.PromptGuidelines)).Distinct(StringComparer.Ordinal).Select(r => "- " + r);
        sections.Add(new("rules", string.Join('\n', rules)));

        sections.Add(new("environment", BuildEnvironment(workspace.Cwd, model, today ?? DateOnly.FromDateTime(DateTime.Now))));

        var context = LoadContextFiles(workspace.Cwd);
        if (context.Count > 0)
        {
            var sb = new StringBuilder("Project-specific instructions and guidelines:\n");
            foreach (var file in context)
            {
                sb.Append('\n').Append($"<project_instructions path=\"{file.Path}\">\n").Append(file.Content.Trim()).Append("\n</project_instructions>\n");
            }
            sections.Add(new("project_context", sb.ToString().TrimEnd()));
        }

        if (extra is not null)
        {
            sections.AddRange(extra);
        }

        var cwd = new StringBuilder(Slashes(workspace.Cwd));
        foreach (var directory in workspace.Additional)
        {
            cwd.Append("\nAdditional workspace directory: ").Append(Slashes(directory));
        }
        sections.Add(new("cwd", cwd.ToString()));
        return sections;
    }

    public static string Render(IReadOnlyList<PromptSection> sections) =>
        string.Join("\n\n", sections.Select(RenderSection));

    public static string RenderSection(PromptSection section) =>
        section.Name == "identity" ? section.Text : $"<{section.Name}>\n{section.Text}\n</{section.Name}>";

    /// <summary>
    /// 新旧两组节的差异，渲染为一条追加的系统消息正文；无变化时返回 null。
    /// identity 节固定不变，不参与比较。
    /// </summary>
    public static string? Diff(IReadOnlyList<PromptSection> previous, IReadOnlyList<PromptSection> current)
    {
        var parts = new List<string>();
        var old = previous.ToDictionary(s => s.Name, s => s.Text);
        foreach (var section in current)
        {
            if (section.Name != "identity" && (!old.TryGetValue(section.Name, out var text) || text != section.Text))
            {
                parts.Add($"Updated system prompt section \"{section.Name}\":\n\n{RenderSection(section)}");
            }
        }
        var names = current.Select(s => s.Name).ToHashSet();
        foreach (var section in previous)
        {
            if (section.Name != "identity" && !names.Contains(section.Name))
            {
                parts.Add($"Removed system prompt section \"{section.Name}\".");
            }
        }
        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    private static string BuildEnvironment(string cwd, ModelInfo model, DateOnly today)
    {
        var sb = new StringBuilder();
        sb.Append("Platform: ").Append(PlatformName()).Append('\n');
        sb.Append("OS version: ").Append(RuntimeInformation.OSDescription).Append('\n');
        sb.Append("Shell: ").Append(ShellName()).Append('\n');
        if (GitInfo.Find(cwd) is { } git)
        {
            sb.Append("Git repository: yes").Append(git.Branch is { } branch ? $" (branch {branch})" : "").Append('\n');
        }
        else
        {
            sb.Append("Git repository: no\n");
        }
        sb.Append("Today's date: ").Append(today.ToString("yyyy-MM-dd")).Append('\n');
        sb.Append("Model: ").Append(model.Name).Append(" (").Append(model.Id).Append(')');
        return sb.ToString();
    }

    private static string PlatformName() =>
        OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "unknown";

    private static string ShellName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "powershell";
        }
        return Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } shell ? Path.GetFileName(shell) : "sh";
    }

    /// <summary>
    /// 全局 ~/.tkcode/AGENTS.md，加上从项目根（含 .git 的目录，找不到时为 cwd）到 cwd 每一级的上下文文件。
    /// 按真实路径去重；总量超出预算时截断后续内容。
    /// </summary>
    public static IReadOnlyList<(string Path, string Content)> LoadContextFiles(string cwd)
    {
        var candidates = new List<string>();
        var global = Path.Combine(TKCodePaths.Home, "AGENTS.md");
        if (File.Exists(global))
        {
            candidates.Add(global);
        }

        var root = GitInfo.Find(cwd)?.Root ?? cwd;
        var dirs = new List<string>();
        for (var dir = new DirectoryInfo(cwd); dir is not null; dir = dir.Parent)
        {
            dirs.Add(dir.FullName);
            if (PathUtil.SamePath(dir.FullName, root))
            {
                break;
            }
        }
        dirs.Reverse();
        foreach (var dir in dirs)
        {
            if (ContextFileNames.Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists) is { } file)
            {
                candidates.Add(file);
            }
        }

        var result = new List<(string, string)>();
        var seen = new HashSet<string>(PathUtil.Comparer);
        var remaining = ContextBudgetBytes;
        foreach (var file in candidates)
        {
            if (!seen.Add(PathUtil.RealPath(file)) || remaining <= 0)
            {
                continue;
            }
            string content;
            try
            {
                content = File.ReadAllText(file);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            var bytes = Encoding.UTF8.GetByteCount(content);
            if (bytes > remaining)
            {
                content = TruncateUtf8(content, remaining) + "\n[truncated]";
                bytes = remaining;
            }
            remaining -= bytes;
            result.Add((file, content));
        }
        return result;
    }

    private static string TruncateUtf8(string text, int maxBytes)
    {
        var length = 0;
        var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maxBytes)
            {
                break;
            }
            bytes += rune.Utf8SequenceLength;
            length += rune.Utf16SequenceLength;
        }
        return text[..length];
    }

    private static string Slashes(string path) => path.Replace('\\', '/');
}
