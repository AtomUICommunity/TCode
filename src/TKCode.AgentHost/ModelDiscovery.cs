using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TKCode.AgentHost.Catalog;
using TKCode.Ai.Http;

namespace TKCode.AgentHost;

/// <summary>一次 GET /models 的结果。</summary>
public sealed record DiscoveryResult(IReadOnlyList<string>? Ids, int? StatusCode, double LatencyMs, string? Error)
{
    public bool Success => Ids is not null;
}

/// <summary>
/// 自定义 / 本地厂商的模型发现：GET {baseUrl}/models（OpenAI 兼容）或 GET {baseUrl}/v1/models（Anthropic）。
/// 结果按厂商缓存，直到显式刷新；失败同样缓存，避免每次重载都重试。
/// </summary>
public sealed class ModelDiscovery(HttpClient http, ILogger<ModelDiscovery> logger)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly ConcurrentDictionary<string, DiscoveryResult> _cache = new(StringComparer.Ordinal);

    public DiscoveryResult? Cached(string providerId) => _cache.GetValueOrDefault(providerId);

    public void Store(string providerId, DiscoveryResult result) => _cache[providerId] = result;

    public void Forget(string providerId) => _cache.TryRemove(providerId, out _);

    public async Task<DiscoveryResult> FetchAsync(string api, string baseUrl, string? apiKey, IReadOnlyDictionary<string, string>? headers,
        bool bearerForMessages, CancellationToken ct)
    {
        // Messages 协议拼 /v1/models；Base URL 已以 /v1 结尾时不重复
        var root = baseUrl.TrimEnd('/');
        if (api == KnownApisShort.Messages && root.EndsWith("/v1", StringComparison.Ordinal))
        {
            root = root[..^3];
        }
        var url = root + (api == KnownApisShort.Messages ? "/v1/models" : "/models");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(apiKey))
        {
            if (api == KnownApisShort.Messages && !bearerForMessages)
            {
                request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            }
            else
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }
        }
        if (api == KnownApisShort.Messages)
        {
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        var watch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            using var response = await http.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            var ms = watch.Elapsed.TotalMilliseconds;
            if (!response.IsSuccessStatusCode)
            {
                var message = HttpWireApi.ExtractErrorMessage(body) ?? response.ReasonPhrase ?? "request failed";
                return new DiscoveryResult(null, (int)response.StatusCode, ms, $"HTTP {(int)response.StatusCode}: {message}");
            }
            return new DiscoveryResult(ParseIds(body), (int)response.StatusCode, ms, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogDebug(ex, "GET {Url} failed", url);
            var message = ex is TaskCanceledException ? $"timed out after {Timeout.TotalSeconds:0}s" : ex.Message;
            return new DiscoveryResult(null, null, watch.Elapsed.TotalMilliseconds, message);
        }
    }

    /// <summary>OpenAI 与 Anthropic 都返回 { "data": [ { "id": … } ] }；Ollama 的 /api/tags 不在此列。</summary>
    private static IReadOnlyList<string> ParseIds(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var data = root.ValueKind == JsonValueKind.Array ? root
            : root.TryGetProperty("data", out var d) ? d
            : root.TryGetProperty("models", out var m) ? m
            : default;
        if (data.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Response has no \"data\" array.");
        }
        var ids = new List<string>();
        foreach (var item in data.EnumerateArray())
        {
            var id = item.ValueKind == JsonValueKind.String ? item.GetString()
                : item.TryGetProperty("id", out var idProp) ? idProp.GetString()
                : item.TryGetProperty("name", out var nameProp) ? nameProp.GetString()
                : null;
            if (!string.IsNullOrEmpty(id))
            {
                ids.Add(id);
            }
        }
        return ids.Distinct(StringComparer.Ordinal).ToArray();
    }
}
