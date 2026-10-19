using System.Text.RegularExpressions;

namespace TKCode.Desktop.Controls.Composer;

/// <summary>
/// 非图片附件（日志、办公文档，以及除此之外的任意文件）。
/// 文件内容不复制、不 base64、不进会话；输入框里是文件名 tag，发给 Agent 的文字是包着绝对路径的提示。
/// </summary>
public static partial class AttachedFiles
{
    /// <summary>有附件文件时追加在提示词末尾，告诉 Agent 路径是本地绝对路径、内容没有内联。</summary>
    public const string Instruction =
        "Attached files above are absolute local paths. They were not uploaded and their contents are not in this message. " +
        "Read them from those paths with your tools. Binary files (for example .xls, .xlsx, .doc, .docx, .ppt, .pptx) " +
        "cannot be read as text; open the absolute path another way.";

    /// <summary>发给模型的引用。路径原样放入，不读取文件。</summary>
    public static string Reference(string absolutePath) => $"[Attached file: {absolutePath}]";

    public static TagSegment Tag(string path)
    {
        var full = Path.GetFullPath(path);
        var name = Path.GetFileName(full);
        return new TagSegment(TagKind.AttachedFile, full, name.Length == 0 ? full : name);
    }

    /// <summary>
    /// 拖入、粘贴或选择的本地路径。目录仍是文件夹 tag（工作区内为相对路径，之外为绝对路径文字）；
    /// 文件一律是附件 tag，值为绝对路径。
    /// </summary>
    public static Segment SegmentForLocalPath(string path, string? cwd)
    {
        var full = Path.GetFullPath(path);
        if (Directory.Exists(full))
        {
            if (WorkspaceRelative(full, cwd) is { } relative)
            {
                return new TagSegment(TagKind.Folder, relative, relative);
            }
            return new TextSegment(Quote(full));
        }
        if (File.Exists(full))
        {
            return Tag(full);
        }
        return new TextSegment(Quote(full));
    }

    public static string AppendInstruction(string text)
    {
        if (text.Contains(Instruction, StringComparison.Ordinal))
        {
            return text.TrimEnd();
        }
        var body = text.TrimEnd();
        return body.Length == 0 ? Instruction : body + "\n" + Instruction;
    }

    /// <summary>显示用：引用折叠成文件名，并去掉读取说明。</summary>
    public static string Collapse(string text)
    {
        if (text.Contains("[Attached file:", StringComparison.Ordinal))
        {
            text = ExpandedPattern().Replace(text, match =>
            {
                var name = Path.GetFileName(match.Groups[1].Value.Trim());
                return name.Length == 0 ? match.Value : "[" + name + "]";
            });
        }
        if (text.Contains(Instruction, StringComparison.Ordinal))
        {
            text = text.Replace("\n" + Instruction, "").Replace(Instruction, "").TrimEnd();
        }
        return text;
    }

    [GeneratedRegex(@"\[Attached file: ([^\]\n]+)\]")]
    private static partial Regex ExpandedPattern();

    private static string? WorkspaceRelative(string full, string? cwd)
    {
        if (cwd is null)
        {
            return null;
        }
        var relative = Path.GetRelativePath(cwd, full).Replace('\\', '/');
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return null;
        }
        return relative;
    }

    private static string Quote(string path) => path.Any(char.IsWhiteSpace) ? $"\"{path}\"" : path;
}
