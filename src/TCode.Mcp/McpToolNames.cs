using System.Security.Cryptography;
using System.Text;

namespace TCode.Mcp;

/// <summary>
/// 模型看到的 MCP 工具名（docs/design/mcp.md §5.1）：<c>mcp__&lt;服务器&gt;__&lt;工具&gt;</c>，
/// 只含 <c>[A-Za-z0-9_-]</c>，总长 ≤ 64（OpenAI 的限制）；过长或规整后冲突时加 6 位 hash。
/// </summary>
public static class McpToolNames
{
    public const string Prefix = "mcp__";
    public const int MaxLength = 64;

    /// <summary>为一个服务器的全部工具取名；返回 原名 → 模型名，保证互不冲突。</summary>
    public static IReadOnlyDictionary<string, string> Assign(string serverKey, IEnumerable<string> toolNames, string prefix = Prefix, int maxLength = MaxLength)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in toolNames)
        {
            var candidate = Make(serverKey, name, prefix, withHash: false, maxLength);
            if (!used.Add(candidate))
            {
                candidate = Make(serverKey, name, prefix, withHash: true, maxLength);
                used.Add(candidate);
            }
            result[name] = candidate;
        }
        return result;
    }

    public static string Make(string serverKey, string toolName, string prefix = Prefix, bool withHash = false, int maxLength = MaxLength)
    {
        var head = prefix + serverKey + "__";
        var tool = Sanitize(toolName);
        var full = head + tool;
        if (!withHash && full.Length <= maxLength && tool.Length > 0)
        {
            return full;
        }
        var hash = "_" + Hash(toolName);
        var room = Math.Max(1, maxLength - head.Length - hash.Length);
        return head + (tool.Length > room ? tool[..room] : tool) + hash;
    }

    /// <summary>非 [A-Za-z0-9_-] 的字符换成 _。</summary>
    public static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        }
        return sb.ToString();
    }

    /// <summary>从模型名中取出服务器键（不是 MCP 工具时为 null）。</summary>
    public static string? ServerOf(string modelName, string prefix = Prefix)
    {
        if (!modelName.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }
        var rest = modelName[prefix.Length..];
        var sep = rest.IndexOf("__", StringComparison.Ordinal);
        return sep > 0 ? rest[..sep] : null;
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(bytes)[..6];
    }
}
