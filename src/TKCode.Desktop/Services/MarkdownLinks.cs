using StreamMark.Avalonia.Plugins;

namespace TKCode.Desktop.Services;

/// <summary>
/// Markdown 文件链接：解析到实际存在的本地文件，行号与锚点不作为文件名的一部分。
/// 行内代码整段是文件路径时也按同一规则解析（<see cref="ResolveCodeSpan"/>）。
/// </summary>
internal static class MarkdownLinks
{
    public static string? ResolveFile(string url, string? directory)
    {
        url = url.Trim();
        if (url.Length == 0 || url.StartsWith('#'))
        {
            return null;
        }
        try
        {
            var end = url.IndexOfAny(['?', '#']);
            var target = end < 0 ? url : url[..end];
            string path;
            if (target.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || !uri.IsFile)
                {
                    return null;
                }
                path = uri.LocalPath;
            }
            else
            {
                if (!Path.IsPathRooted(target) && Uri.TryCreate(target, UriKind.Absolute, out _))
                {
                    return null;
                }
                path = Uri.UnescapeDataString(target);
            }
            if (path.StartsWith("~/", StringComparison.Ordinal))
            {
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
            }
            if (!Path.IsPathFullyQualified(path))
            {
                if (string.IsNullOrEmpty(directory))
                {
                    return null;
                }
                path = Path.GetFullPath(path, directory);
            }
            else
            {
                path = Path.GetFullPath(path);
            }
            if (File.Exists(path))
            {
                return path;
            }
            // 编码代理常用 path:line 或 path:line:column；优先保留本身就包含冒号的真实文件名。
            for (var i = 0; i < 2; i++)
            {
                var colon = path.LastIndexOf(':');
                if (colon < 0 || !int.TryParse(path.AsSpan(colon + 1), out var line) || line <= 0)
                {
                    break;
                }
                path = path[..colon];
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            // 无效路径和不可访问的文件不打开标签页。
        }
        return null;
    }

    /// <summary>
    /// 行内代码的整段文本如果是一个已存在的文件路径，返回绝对路径。
    /// 代码片段、命令和标识符不是路径。<code>markdown:docs/a.md</code> 这种语言前缀会先去掉。
    /// </summary>
    public static string? ResolveCodeSpan(string code, string? directory)
    {
        code = code.Trim();
        if (ResolveShaped(code, directory) is { } direct)
        {
            return direct;
        }
        // ```markdown:docs/a.md 或 12:40:src/App.cs：第一个路径分隔符之前的冒号是语言或行号前缀
        var slash = code.IndexOfAny(['/', '\\']);
        if (slash <= 0)
        {
            return null;
        }
        var start = code.LastIndexOf(':', slash - 1);
        if (start <= 0 || (start == 1 && char.IsAsciiLetter(code[0])))
        {
            return null;
        }
        return ResolveShaped(code[(start + 1)..].Trim(), directory);
    }

    /// <summary>
    /// 代码围栏信息串里的文件（<code>```cs src/App.cs#L10</code>、<code>```docs/a.md</code>）。
    /// 只判断形状，不检查文件是否存在。
    /// </summary>
    public static string? FenceTarget(string? info)
    {
        var fence = CodeFenceInfo.Parse(info);
        if (fence.Path is { } path && IsFileReference(path))
        {
            return path;
        }
        if (string.IsNullOrEmpty(fence.Path) && fence.Language is { } language && IsFileReference(language))
        {
            return language;
        }
        return null;
    }

    /// <summary>整段文本看起来像文件路径（含分隔符、以 ~/ 开头，或带扩展名的文件名），而不是代码或网址。</summary>
    public static bool IsFileReference(string text)
    {
        text = text.Trim();
        if (text.Length is 0 or > 1024 || text.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }
        foreach (var c in text)
        {
            if (c < ' ' || c is '<' or '>' or '"' or '|' or '*' or '`' or '?')
            {
                return false;
            }
        }
        if (text.Contains('/') || text.Contains('\\') || text.StartsWith("~/", StringComparison.Ordinal))
        {
            return true;
        }
        return HasFileExtension(text);
    }

    private static string? ResolveShaped(string code, string? directory) =>
        IsFileReference(code) ? ResolveFile(code, directory) : null;

    private static bool HasFileExtension(string text)
    {
        var end = text.IndexOfAny(['?', '#']);
        var target = end < 0 ? text : text[..end];
        var slash = target.LastIndexOfAny(['/', '\\']);
        var name = slash >= 0 ? target[(slash + 1)..] : target;
        for (var i = 0; i < 2; i++)
        {
            var colon = name.LastIndexOf(':');
            if (colon <= 0 || !IsDigits(name.AsSpan(colon + 1)))
            {
                break;
            }
            name = name[..colon];
        }
        var dot = name.LastIndexOf('.');
        if (dot <= 0 || dot >= name.Length - 1)
        {
            return false;
        }
        var ext = name.AsSpan(dot + 1);
        if (ext.Length > 20)
        {
            return false;
        }
        foreach (var c in ext)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsDigits(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return false;
        }
        foreach (var c in text)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }
        return true;
    }
}
