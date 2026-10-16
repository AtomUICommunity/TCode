using System.Text;
using System.Text.RegularExpressions;

namespace TCode.Agent.Coding.Tools.Search;

/// <summary>
/// glob → 正则。支持 <c>**</c>、<c>*</c>、<c>?</c>、<c>[...]</c>、<c>{a,b}</c>。
/// 不含 <c>/</c> 的模式匹配任意深度的文件名（与 rg -g 一致），否则匹配相对搜索根的完整路径。
/// </summary>
public sealed class GlobPattern
{
    private readonly Regex _regex;

    public GlobPattern(string pattern, bool ignoreCase)
    {
        Pattern = pattern.Replace('\\', '/');
        var anchored = Pattern.TrimEnd('/').Contains('/');
        var body = Pattern.TrimStart('/');
        if (body.StartsWith("./", StringComparison.Ordinal))
        {
            body = body[2..];
        }
        var regex = anchored ? "^" + Translate(body) + "$" : "(^|/)" + Translate(body) + "$";
        _regex = new Regex(regex, RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None));
    }

    public string Pattern { get; }

    /// <summary><paramref name="relativePath"/> 使用 / 分隔，相对搜索根。</summary>
    public bool IsMatch(string relativePath) => _regex.IsMatch(relativePath);

    public static string Translate(string glob)
    {
        var sb = new StringBuilder();
        var braceDepth = 0;
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                    i++;
                    if (i + 1 < glob.Length && glob[i + 1] == '/')
                    {
                        i++;
                        sb.Append("(?:.*/)?");
                    }
                    else
                    {
                        sb.Append(".*");
                    }
                    break;
                case '*':
                    sb.Append("[^/]*");
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                case '[':
                    var close = glob.IndexOf(']', i + 1);
                    if (close < 0)
                    {
                        sb.Append("\\[");
                        break;
                    }
                    var set = glob[(i + 1)..close];
                    sb.Append('[').Append(set.StartsWith('!') ? "^" + Regex.Escape(set[1..]).Replace("\\-", "-") : Regex.Escape(set).Replace("\\-", "-")).Append(']');
                    i = close;
                    break;
                case '{':
                    braceDepth++;
                    sb.Append("(?:");
                    break;
                case '}' when braceDepth > 0:
                    braceDepth--;
                    sb.Append(')');
                    break;
                case ',' when braceDepth > 0:
                    sb.Append('|');
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        return sb.ToString();
    }
}
