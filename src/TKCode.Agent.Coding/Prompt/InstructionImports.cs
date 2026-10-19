using System.Text;
using System.Text.RegularExpressions;
using TKCode.Agent.Coding.Tools.Files;
using TKCode.Agent.Coding.Workspace;

namespace TKCode.Agent.Coding.Prompt;

/// <summary>
/// 上下文文件（AGENTS.md）中的 @import（docs/design/memory.md §7）：行首或空白之后的 <c>@路径</c>，
/// 围栏代码块与行内代码里的不算；<c>\ </c> 表示路径中的空格。
/// </summary>
public static partial class InstructionImports
{
    /// <summary>从被发现的文件算起最多导入 4 跳。</summary>
    public const int MaxDepth = 4;

    public const long MaxFileBytes = 4 * 1024 * 1024;

    /// <summary>文本中依次出现的导入路径（原样，未解析）。</summary>
    public static IEnumerable<string> Find(string content)
    {
        string? fence = null;
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.TrimStart();
            if (fence is not null)
            {
                if (trimmed.StartsWith(fence, StringComparison.Ordinal) && trimmed.Trim().Trim(fence[0]).Length == 0)
                {
                    fence = null;
                }
                continue;
            }
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                fence = new string(trimmed[0], trimmed.TakeWhile(c => c == trimmed[0]).Count());
                continue;
            }
            foreach (Match match in ImportToken().Matches(StripCodeSpans(line)))
            {
                yield return match.Groups[1].Value.Replace("\\ ", " ", StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// 把 <paramref name="importingFile"/> 中的导入路径解析为存在、不超过 4 MiB 的文件：<c>~/</c> 为用户主目录，
    /// 相对路径以导入它的文件所在目录为基准。原样找不到且末尾是标点时去掉标点再试一次。
    /// </summary>
    public static string? Resolve(string importingFile, string raw)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(importingFile)) ?? "";
        if (Candidate(dir, raw) is { } path)
        {
            return path;
        }
        var trimmed = raw.TrimEnd('.', ',', ';', ':', '!', '?', ')');
        return trimmed.Length > 0 && trimmed.Length < raw.Length ? Candidate(dir, trimmed) : null;
    }

    /// <summary>
    /// 深度优先展开 <paramref name="file"/> 的导入（不含它自己），按出现顺序返回（路径, 内容）。
    /// <paramref name="scope"/> 不为 null 时只导入真实路径位于其下的文件；<paramref name="visit"/> 按真实路径去重，第一次见到时返回 true。
    /// </summary>
    public static IEnumerable<(string Path, string Content)> Expand(string file, string content, string? scope, Func<string, bool> visit, int depth = 0)
    {
        if (depth >= MaxDepth)
        {
            yield break;
        }
        foreach (var raw in Find(content))
        {
            if (Resolve(file, raw) is not { } target)
            {
                continue;
            }
            var real = PathUtil.RealPath(target);
            if ((scope is not null && !PathUtil.IsUnder(real, scope)) || !visit(real) || ReadText(target) is not { } text)
            {
                continue;
            }
            yield return (target, text);
            foreach (var nested in Expand(target, text, scope, visit, depth + 1))
            {
                yield return nested;
            }
        }
    }

    /// <summary>读取被导入的文本文件；不存在、过大、像二进制或读不出时为 null。</summary>
    public static string? ReadText(string path)
    {
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes)
            {
                return null;
            }
            var bytes = File.ReadAllBytes(path);
            return TextFile.LooksBinary(bytes) ? null : TextFile.Decode(bytes).Text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Candidate(string dir, string raw)
    {
        try
        {
            var path = PathUtil.Resolve(dir, raw);
            return File.Exists(path) && new FileInfo(path).Length <= MaxFileBytes ? path : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>把行内代码（成对的反引号串）换成非空白的占位符：紧跟在行内代码后的 @ 不算导入；没有配对的反引号原样保留。</summary>
    private static string StripCodeSpans(string line)
    {
        if (!line.Contains('`'))
        {
            return line;
        }
        var sb = new StringBuilder(line.Length);
        var i = 0;
        while (i < line.Length)
        {
            if (line[i] != '`')
            {
                sb.Append(line[i++]);
                continue;
            }
            var run = Run(line, i);
            var close = FindRun(line, i + run, run);
            if (close < 0)
            {
                sb.Append(line, i, run);
                i += run;
                continue;
            }
            sb.Append('\0', close + run - i);
            i = close + run;
        }
        return sb.ToString();
    }

    private static int Run(string line, int start)
    {
        var end = start;
        while (end < line.Length && line[end] == '`')
        {
            end++;
        }
        return end - start;
    }

    /// <summary>从 <paramref name="start"/> 起长度恰好为 <paramref name="length"/> 的反引号串的位置；没有时为 -1。</summary>
    private static int FindRun(string line, int start, int length)
    {
        var i = start;
        while (i < line.Length)
        {
            if (line[i] != '`')
            {
                i++;
                continue;
            }
            var run = Run(line, i);
            if (run == length)
            {
                return i;
            }
            i += run;
        }
        return -1;
    }

    [GeneratedRegex(@"(?:^|(?<=\s))@((?:\\ |\S)+)")]
    private static partial Regex ImportToken();
}
