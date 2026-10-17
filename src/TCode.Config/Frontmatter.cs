using System.Text;

namespace TCode.Config;

/// <summary>frontmatter 的一个值：字符串或字符串列表。</summary>
public sealed record FrontmatterValue(string? Text, IReadOnlyList<string>? List)
{
    public override string ToString() => Text ?? string.Join(", ", List ?? []);
}

/// <param name="Fields">字段（键不区分大小写）；嵌套的映射不展开，只记下键名（值为 null）。</param>
/// <param name="Body">frontmatter 之后的正文。</param>
/// <param name="Warnings">无法解析的行（不影响其他字段）。</param>
public sealed record Frontmatter(IReadOnlyDictionary<string, FrontmatterValue?> Fields, string Body, IReadOnlyList<string> Warnings)
{
    public string? GetString(string key) => Fields.TryGetValue(key, out var value) ? value?.Text ?? (value?.List is { } l ? string.Join(", ", l) : null) : null;

    public IReadOnlyList<string>? GetList(string key) => Fields.TryGetValue(key, out var value) && value is not null
        ? value.List ?? (value.Text is { Length: > 0 } t ? t.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : [])
        : null;

    /// <summary>
    /// 解析 Markdown 开头的 YAML frontmatter（docs/design/composer.md §3）。只支持命令、skill、role 用到的子集：
    /// <c>key: value</c>（可加引号，行尾 # 注释）、<c>key: [a, b]</c>、缩进的 <c>- item</c> 列表、<c>|</c> / <c>&gt;</c> 多行文本；
    /// 嵌套映射只记下键名。不依赖反射，NativeAOT 安全。没有 frontmatter 时整段都是正文。
    /// </summary>
    public static Frontmatter Parse(string text)
    {
        var fields = new Dictionary<string, FrontmatterValue?>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            return new Frontmatter(fields, text, warnings);
        }
        var end = Array.FindIndex(lines, 1, l => l.Trim() is "---" or "...");
        if (end < 0)
        {
            warnings.Add("frontmatter is not closed with ---");
            return new Frontmatter(fields, text, warnings);
        }
        var i = 1;
        while (i < end)
        {
            var line = lines[i];
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#'))
            {
                i++;
                continue;
            }
            if (char.IsWhiteSpace(line[0]))
            {
                // 属于上一个嵌套映射的缩进行
                i++;
                continue;
            }
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                warnings.Add($"line {i + 1}: expected 'key: value'");
                i++;
                continue;
            }
            var key = line[..colon].Trim().Trim('"', '\'');
            var raw = StripComment(line[(colon + 1)..]).Trim();
            i++;
            if (raw is "|" or ">" or "|-" or ">-" or "|+" or ">+")
            {
                var block = new List<string>();
                while (i < end && (lines[i].Length == 0 || char.IsWhiteSpace(lines[i][0])))
                {
                    block.Add(lines[i]);
                    i++;
                }
                var indent = block.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
                var content = block.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart()).ToList();
                var joined = raw[0] == '|' ? string.Join('\n', content) : Fold(content);
                fields[key] = new FrontmatterValue(raw.EndsWith('+') ? joined + "\n" : joined.TrimEnd('\n'), null);
                continue;
            }
            if (raw.Length == 0)
            {
                // 下面的缩进行是列表（- item）或嵌套映射
                var items = new List<string>();
                var nested = false;
                while (i < end && (lines[i].Trim().Length == 0 || char.IsWhiteSpace(lines[i][0]) || lines[i].StartsWith('-')))
                {
                    var trimmed = lines[i].Trim();
                    if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed == "-")
                    {
                        items.Add(Scalar(StripComment(trimmed[1..]).Trim()));
                    }
                    else if (trimmed.Length > 0)
                    {
                        nested = true;
                    }
                    i++;
                }
                fields[key] = items.Count > 0 ? new FrontmatterValue(null, items) : nested ? null : new FrontmatterValue("", null);
                continue;
            }
            if (raw.StartsWith('[') && raw.EndsWith(']'))
            {
                fields[key] = new FrontmatterValue(null, SplitFlow(raw[1..^1]).Select(Scalar).Where(s => s.Length > 0).ToList());
                continue;
            }
            fields[key] = new FrontmatterValue(Scalar(raw), null);
        }
        var body = string.Join('\n', lines.Skip(end + 1));
        return new Frontmatter(fields, body.TrimStart('\n'), warnings);
    }

    /// <summary>去掉引号并处理双引号转义；未加引号时原样返回。</summary>
    private static string Scalar(string raw)
    {
        if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'')
        {
            return raw[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            var sb = new StringBuilder();
            for (var i = 1; i < raw.Length - 1; i++)
            {
                if (raw[i] == '\\' && i + 1 < raw.Length - 1)
                {
                    i++;
                    sb.Append(raw[i] switch { 'n' => '\n', 't' => '\t', var c => c });
                }
                else
                {
                    sb.Append(raw[i]);
                }
            }
            return sb.ToString();
        }
        return raw;
    }

    /// <summary>去掉行尾注释（引号外、前面是空白的 #）。</summary>
    private static string StripComment(string value)
    {
        char? quote = null;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (quote is null && c is '"' or '\'')
            {
                quote = c;
            }
            else if (quote == c)
            {
                quote = null;
            }
            else if (quote is null && c == '#' && (i == 0 || char.IsWhiteSpace(value[i - 1])))
            {
                return value[..i];
            }
        }
        return value;
    }

    private static IEnumerable<string> SplitFlow(string inner)
    {
        var start = 0;
        char? quote = null;
        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (quote is null && c is '"' or '\'')
            {
                quote = c;
            }
            else if (quote == c)
            {
                quote = null;
            }
            else if (quote is null && c == ',')
            {
                yield return inner[start..i].Trim();
                start = i + 1;
            }
        }
        yield return inner[start..].Trim();
    }

    /// <summary>折叠多行文本（&gt;）：相邻行以空格连接，空行变成换行。</summary>
    private static string Fold(List<string> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (line.Trim().Length == 0)
            {
                sb.Append('\n');
            }
            else
            {
                if (sb.Length > 0 && sb[^1] != '\n')
                {
                    sb.Append(' ');
                }
                sb.Append(line.Trim());
            }
        }
        return sb.ToString();
    }
}
