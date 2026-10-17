using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace TKCode.Agent.Coding.Tools.Web;

/// <summary>
/// 把 HTML 元素树转换为 GFM：标题、段落、列表（可嵌套）、引用、代码块（保留语言）、表格、链接、图片、强调。
/// 相对链接按页面地址解析为绝对地址。只依赖 AngleSharp 的 DOM，不使用反射。
/// </summary>
public sealed partial class HtmlToMarkdown(Uri? baseUri)
{
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "template", "svg", "canvas", "iframe", "object", "embed", "head", "meta", "link",
        "button", "input", "select", "textarea", "option", "form", "dialog",
    };

    private static readonly HashSet<string> Inline = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "abbr", "b", "bdi", "bdo", "br", "cite", "code", "data", "del", "dfn", "em", "i", "img", "ins", "kbd", "label", "mark",
        "q", "s", "samp", "small", "span", "strike", "strong", "sub", "sup", "time", "tt", "u", "var", "wbr", "font",
    };

    public string Convert(IElement root)
    {
        var blocks = new List<string>();
        RenderBlocks(root, blocks);
        var text = string.Join("\n\n", blocks.Where(b => b.Trim().Length > 0));
        return ExtraBlankLines().Replace(text, "\n\n").Trim() + "\n";
    }

    private void RenderBlocks(INode parent, List<string> blocks)
    {
        var inline = new StringBuilder();
        void Flush()
        {
            var text = CollapseLines(inline.ToString());
            if (text.Length > 0)
            {
                blocks.Add(text);
            }
            inline.Clear();
        }

        foreach (var child in parent.ChildNodes)
        {
            switch (child)
            {
                case IText text:
                    inline.Append(Collapse(text.Data));
                    break;
                case IElement element when Skipped.Contains(element.LocalName) || IsHidden(element):
                    break;
                case IElement element when Inline.Contains(element.LocalName):
                    inline.Append(RenderInline(element));
                    break;
                case IElement element:
                    Flush();
                    RenderBlock(element, blocks);
                    break;
            }
        }
        Flush();
    }

    private void RenderBlock(IElement element, List<string> blocks)
    {
        switch (element.LocalName)
        {
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                var heading = InlineText(element);
                if (heading.Length > 0)
                {
                    blocks.Add(new string('#', element.LocalName[1] - '0') + " " + heading.Replace('\n', ' '));
                }
                break;
            case "p":
                blocks.Add(InlineText(element));
                break;
            case "hr":
                blocks.Add("---");
                break;
            case "pre":
                blocks.Add(CodeBlock(element));
                break;
            case "ul" or "ol" or "menu":
                blocks.Add(List(element));
                break;
            case "blockquote":
                var inner = new List<string>();
                RenderBlocks(element, inner);
                var quoted = string.Join("\n\n", inner);
                if (quoted.Trim().Length > 0)
                {
                    blocks.Add(string.Join('\n', quoted.Split('\n').Select(l => l.Length == 0 ? ">" : "> " + l)));
                }
                break;
            case "table":
                blocks.Add(Table(element));
                break;
            case "dl":
                foreach (var item in element.Children)
                {
                    if (item.LocalName == "dt")
                    {
                        blocks.Add("**" + InlineText(item) + "**");
                    }
                    else if (item.LocalName == "dd")
                    {
                        var dd = new List<string>();
                        RenderBlocks(item, dd);
                        blocks.Add(string.Join("\n\n", dd));
                    }
                }
                break;
            case "figure":
                RenderBlocks(element, blocks);
                break;
            case "figcaption":
                blocks.Add("*" + InlineText(element) + "*");
                break;
            default:
                RenderBlocks(element, blocks);
                break;
        }
    }

    private string RenderInline(IElement element)
    {
        switch (element.LocalName)
        {
            case "br":
                return "\n";
            case "wbr":
                return "";
            case "img":
                var alt = Collapse(element.GetAttribute("alt") ?? "").Trim();
                var src = Resolve(element.GetAttribute("src") ?? element.GetAttribute("data-src"));
                return src is null ? alt : $"![{EscapeBrackets(alt)}]({src})";
            case "a":
                var text = InlineText(element);
                var href = element.GetAttribute("href");
                if (text.Length == 0)
                {
                    return "";
                }
                if (href is null || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || href.StartsWith('#'))
                {
                    return text;
                }
                return Resolve(href) is { } url ? $"[{EscapeBrackets(text.Replace('\n', ' '))}]({url})" : text;
            case "code" or "kbd" or "samp" or "tt":
                var code = element.TextContent;
                if (code.Length == 0)
                {
                    return "";
                }
                var fence = code.Contains('`') ? "`` " : "`";
                return fence + code.Replace('\n', ' ') + (fence.Length > 1 ? " ``" : "`");
            case "strong" or "b":
                return Wrap(InlineText(element), "**");
            case "em" or "i" or "cite" or "dfn":
                return Wrap(InlineText(element), "*");
            case "del" or "s" or "strike":
                return Wrap(InlineText(element), "~~");
            case "q":
                return "“" + InlineText(element) + "”";
            default:
                return InlineChildren(element);
        }
    }

    /// <summary>元素内的行内内容（块级子元素也按行内处理）。</summary>
    private string InlineText(IElement element) => CollapseLines(InlineChildren(element));

    private string InlineChildren(IElement element)
    {
        var sb = new StringBuilder();
        foreach (var child in element.ChildNodes)
        {
            switch (child)
            {
                case IText text:
                    sb.Append(Collapse(text.Data));
                    break;
                case IElement e when Skipped.Contains(e.LocalName) || IsHidden(e):
                    break;
                case IElement e when Inline.Contains(e.LocalName):
                    sb.Append(RenderInline(e));
                    break;
                case IElement e:
                    sb.Append(' ').Append(InlineChildren(e)).Append(' ');
                    break;
            }
        }
        return sb.ToString();
    }

    private static string CodeBlock(IElement pre)
    {
        var code = pre.QuerySelector("code") ?? pre;
        var language = LanguageOf(code) ?? LanguageOf(pre) ?? "";
        var text = code.TextContent.Replace("\r\n", "\n").TrimEnd('\n');
        var fence = text.Contains("```", StringComparison.Ordinal) ? "````" : "```";
        return $"{fence}{language}\n{text}\n{fence}";
    }

    private static string? LanguageOf(IElement element)
    {
        foreach (var cls in (element.GetAttribute("class") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var prefix in (string[])["language-", "lang-", "highlight-source-", "highlight-"])
            {
                if (cls.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && cls.Length > prefix.Length)
                {
                    return cls[prefix.Length..];
                }
            }
        }
        return element.GetAttribute("data-lang") ?? element.GetAttribute("data-language");
    }

    private string List(IElement list, int depth = 0)
    {
        var ordered = list.LocalName == "ol";
        var number = int.TryParse(list.GetAttribute("start"), out var start) ? start : 1;
        var lines = new List<string>();
        foreach (var item in list.Children)
        {
            if (item.LocalName != "li")
            {
                continue;
            }
            var marker = ordered ? $"{number++}. " : "- ";
            var indent = new string(' ', marker.Length);
            var parts = new List<string>();
            var inline = new StringBuilder();
            void Flush()
            {
                var text = CollapseLines(inline.ToString());
                if (text.Length > 0)
                {
                    parts.Add(text);
                }
                inline.Clear();
            }
            foreach (var child in item.ChildNodes)
            {
                switch (child)
                {
                    case IText text:
                        inline.Append(Collapse(text.Data));
                        break;
                    case IElement e when Skipped.Contains(e.LocalName) || IsHidden(e):
                        break;
                    case IElement e when e.LocalName is "ul" or "ol":
                        Flush();
                        parts.Add(List(e, depth + 1));
                        break;
                    case IElement e when Inline.Contains(e.LocalName):
                        inline.Append(RenderInline(e));
                        break;
                    case IElement e:
                        Flush();
                        var nested = new List<string>();
                        RenderBlock(e, nested);
                        parts.AddRange(nested.Where(n => n.Trim().Length > 0));
                        break;
                }
            }
            Flush();
            if (parts.Count == 0)
            {
                continue;
            }
            var body = string.Join('\n', parts);
            var bodyLines = body.Split('\n');
            lines.Add(marker + bodyLines[0]);
            lines.AddRange(bodyLines.Skip(1).Select(l => l.Length == 0 ? "" : indent + l));
        }
        return string.Join('\n', lines);
    }

    private string Table(IElement table)
    {
        var rows = table.QuerySelectorAll("tr")
            .Where(tr => tr.Closest("table") == table)
            .Select(tr => tr.Children.Where(c => c.LocalName is "td" or "th")
                .Select(c => InlineText(c).Replace('\n', ' ').Replace("|", "\\|", StringComparison.Ordinal))
                .ToList())
            .Where(r => r.Count > 0)
            .ToList();
        if (rows.Count == 0)
        {
            return "";
        }
        var columns = rows.Max(r => r.Count);
        foreach (var row in rows)
        {
            while (row.Count < columns)
            {
                row.Add("");
            }
        }
        var sb = new StringBuilder();
        sb.Append("| ").Append(string.Join(" | ", rows[0])).Append(" |\n");
        sb.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", columns))).Append('\n');
        foreach (var row in rows.Skip(1))
        {
            sb.Append("| ").Append(string.Join(" | ", row)).Append(" |\n");
        }
        return sb.ToString().TrimEnd();
    }

    private string? Resolve(string? href)
    {
        if (string.IsNullOrWhiteSpace(href) || href.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (Uri.TryCreate(href.Trim(), UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https" or "mailto")
        {
            return absolute.ToString();
        }
        return baseUri is not null && Uri.TryCreate(baseUri, href.Trim(), out var resolved) ? resolved.ToString() : href;
    }

    public static bool IsHidden(IElement element) =>
        element.HasAttribute("hidden")
        || string.Equals(element.GetAttribute("aria-hidden"), "true", StringComparison.OrdinalIgnoreCase)
        || (element.GetAttribute("style") is { } style && HiddenStyle().IsMatch(style));

    private static string Wrap(string text, string marker)
    {
        var trimmed = text.Trim();
        return trimmed.Length == 0 ? text : $"{marker}{trimmed}{marker}" + (text.EndsWith(' ') ? " " : "");
    }

    private static string EscapeBrackets(string text) => text.Replace("[", "\\[", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal);

    private static string Collapse(string text) => Whitespace().Replace(text, " ");

    /// <summary>行内文本整理：每行去掉首尾空白，去掉空行。</summary>
    private static string CollapseLines(string text) =>
        string.Join('\n', text.Split('\n').Select(l => SpaceRun().Replace(l, " ").Trim()).Where(l => l.Length > 0));

    [GeneratedRegex(@"[ \t\r\n\f ]+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@" {2,}")]
    private static partial Regex SpaceRun();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExtraBlankLines();

    [GeneratedRegex(@"display\s*:\s*none|visibility\s*:\s*hidden", RegexOptions.IgnoreCase)]
    private static partial Regex HiddenStyle();
}
