using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace TKCode.Ai;

/// <summary>缓存断点（cache_control）的有效期，全局设置 settings.json 的 promptCacheTtl。</summary>
public enum PromptCacheTtl
{
    /// <summary>5 分钟（默认，不发送 ttl 字段）：每次读取自动续期，写入按 1.25 倍计价。</summary>
    FiveMinutes,

    /// <summary>1 小时：写入按 2 倍计价，适合两次请求间隔 5–60 分钟的用法。</summary>
    OneHour,
}

/// <summary>各协议共用的缓存标识：断点、缓存键与会话亲和 id。</summary>
public static class PromptCache
{
    /// <summary>新建一个断点对象（JsonNode 只能挂在一个父节点下，每个位置各建一个）。</summary>
    public static JsonObject Control(PromptCacheTtl ttl)
    {
        var node = new JsonObject { ["type"] = "ephemeral" };
        if (ttl == PromptCacheTtl.OneHour)
        {
            node["ttl"] = "1h";
        }
        return node;
    }

    /// <summary>OpenAI 的 prompt_cache_key 与各厂商的会话亲和头：同一会话的请求路由到同一缓存。</summary>
    public static string Key(string sessionId) => "tkcode:" + sessionId;

    /// <summary>
    /// Anthropic metadata.user_id，沿用 Claude Code 的形状 <c>user_&lt;hash&gt;_account__session_&lt;uuid&gt;</c>：
    /// 中转站据其中的 session 把同一会话粘在同一上游账号上（缓存按组织隔离，换账号必然不命中）。只含哈希与会话 id，不含个人信息。
    /// </summary>
    public static string AnthropicUserId(string sessionId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("tkcode:" + sessionId));
        var session = Guid.TryParseExact(sessionId, "N", out var id) || Guid.TryParse(sessionId, out id) ? id : new Guid(hash.AsSpan(0, 16));
        return $"user_{Convert.ToHexStringLower(hash)}_account__session_{session:D}";
    }
}
