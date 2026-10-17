using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TKCode.Agent.Coding.Tools.Web;

/// <summary>web_search 的后端。实现只负责调用搜索 API 并归一化结果，域名过滤由工具统一处理。</summary>
public interface IWebSearchProvider
{
    /// <summary>brave / tavily / exa / searxng。</summary>
    string Name { get; }

    Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int limit, IReadOnlyList<string>? allowedDomains, CancellationToken cancellationToken);
}

public sealed class WebSearchException(string message) : Exception(message);

/// <summary>按配置创建搜索后端。</summary>
public static class WebSearchProviders
{
    /// <param name="provider">brave / tavily / exa / searxng。</param>
    public static IWebSearchProvider Create(string provider, string? apiKey, string? baseUrl, HttpMessageHandler? handler = null)
    {
        var http = new HttpClient(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TKCode/1.0");
        return provider.ToLowerInvariant() switch
        {
            "brave" => new BraveSearch(http, Require(apiKey, provider), baseUrl),
            "tavily" => new TavilySearch(http, Require(apiKey, provider), baseUrl),
            "exa" => new ExaSearch(http, Require(apiKey, provider), baseUrl),
            "searxng" => new SearxngSearch(http, baseUrl ?? throw new WebSearchException("webSearch.baseUrl is required for searxng.")),
            _ => throw new WebSearchException($"Unknown web search provider '{provider}'."),
        };
    }

    private static string Require(string? key, string provider) =>
        string.IsNullOrEmpty(key) ? throw new WebSearchException($"webSearch.apiKey is required for {provider}.") : key;

    internal static async Task<JsonNode> SendAsync(HttpClient http, HttpRequestMessage request, string provider, CancellationToken ct)
    {
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new WebSearchException($"{provider} search failed with HTTP {(int)response.StatusCode}: {Truncate(body, 300)}");
        }
        try
        {
            return JsonNode.Parse(body) ?? throw new WebSearchException($"{provider} returned an empty response.");
        }
        catch (JsonException)
        {
            throw new WebSearchException($"{provider} returned invalid JSON: {Truncate(body, 200)}");
        }
    }

    internal static StringContent Json(JsonObject body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    internal static string? Str(this JsonNode? node, string name) =>
        node is JsonObject obj && obj[name] is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}

internal sealed class BraveSearch(HttpClient http, string apiKey, string? baseUrl) : IWebSearchProvider
{
    public string Name => "brave";

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int limit, IReadOnlyList<string>? allowedDomains, CancellationToken ct)
    {
        var q = allowedDomains is { Count: > 0 } ? $"{query} ({string.Join(" OR ", allowedDomains.Select(d => "site:" + d))})" : query;
        var url = $"{(baseUrl ?? "https://api.search.brave.com/res/v1/web/search").TrimEnd('/')}?q={Uri.EscapeDataString(q)}&count={Math.Min(limit, 20)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Subscription-Token", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var root = await WebSearchProviders.SendAsync(http, request, Name, ct);
        return (root["web"]?["results"] as JsonArray ?? [])
            .Select(r => new WebSearchResult(r.Str("title") ?? "", r.Str("url") ?? "", r.Str("description")))
            .Where(r => r.Url.Length > 0)
            .ToArray();
    }
}

internal sealed class TavilySearch(HttpClient http, string apiKey, string? baseUrl) : IWebSearchProvider
{
    public string Name => "tavily";

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int limit, IReadOnlyList<string>? allowedDomains, CancellationToken ct)
    {
        var body = new JsonObject { ["query"] = query, ["max_results"] = Math.Min(limit, 20), ["search_depth"] = "basic" };
        if (allowedDomains is { Count: > 0 })
        {
            var domains = new JsonArray();
            foreach (var domain in allowedDomains)
            {
                domains.Add((JsonNode)domain);
            }
            body["include_domains"] = domains;
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, (baseUrl ?? "https://api.tavily.com").TrimEnd('/') + "/search")
        {
            Content = WebSearchProviders.Json(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var root = await WebSearchProviders.SendAsync(http, request, Name, ct);
        return (root["results"] as JsonArray ?? [])
            .Select(r => new WebSearchResult(r.Str("title") ?? "", r.Str("url") ?? "", r.Str("content")))
            .Where(r => r.Url.Length > 0)
            .ToArray();
    }
}

internal sealed class ExaSearch(HttpClient http, string apiKey, string? baseUrl) : IWebSearchProvider
{
    public string Name => "exa";

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int limit, IReadOnlyList<string>? allowedDomains, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["query"] = query,
            ["numResults"] = Math.Min(limit, 20),
            ["contents"] = new JsonObject { ["highlights"] = new JsonObject { ["numSentences"] = 3 } },
        };
        if (allowedDomains is { Count: > 0 })
        {
            var domains = new JsonArray();
            foreach (var domain in allowedDomains)
            {
                domains.Add((JsonNode)domain);
            }
            body["includeDomains"] = domains;
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, (baseUrl ?? "https://api.exa.ai").TrimEnd('/') + "/search")
        {
            Content = WebSearchProviders.Json(body),
        };
        request.Headers.Add("x-api-key", apiKey);
        var root = await WebSearchProviders.SendAsync(http, request, Name, ct);
        return (root["results"] as JsonArray ?? [])
            .Select(r => new WebSearchResult(r.Str("title") ?? "", r.Str("url") ?? "",
                r?["highlights"] is JsonArray h ? string.Join(" … ", h.Select(x => x?.GetValue<string>())) : r.Str("text")))
            .Where(r => r.Url.Length > 0)
            .ToArray();
    }
}

internal sealed class SearxngSearch(HttpClient http, string baseUrl) : IWebSearchProvider
{
    public string Name => "searxng";

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int limit, IReadOnlyList<string>? allowedDomains, CancellationToken ct)
    {
        var q = allowedDomains is { Count: > 0 } ? $"{query} {string.Join(' ', allowedDomains.Select(d => "site:" + d))}" : query;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/search?q={Uri.EscapeDataString(q)}&format=json");
        JsonNode root;
        try
        {
            root = await WebSearchProviders.SendAsync(http, request, Name, ct);
        }
        catch (WebSearchException ex) when (ex.Message.Contains("HTTP 403", StringComparison.Ordinal))
        {
            throw new WebSearchException("SearXNG refused the JSON API (HTTP 403). Enable `json` in search.formats of the instance's settings.yml.");
        }
        return (root["results"] as JsonArray ?? [])
            .Select(r => new WebSearchResult(r.Str("title") ?? "", r.Str("url") ?? "", r.Str("content")))
            .Where(r => r.Url.Length > 0)
            .Take(limit)
            .ToArray();
    }
}
