using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TKCode.Rpc;

namespace TKCode.AgentHost.Catalog;

/// <summary>
/// 模型目录的手动更新（从不自动应用）：
/// <list type="number">
/// <item>CheckAsync：下载 models.dev/api.json（带 If-None-Match）→ 按 <see cref="ModelsDevTrimmer"/> 裁剪 → 与当前目录比较，暂存结果；</item>
/// <item>Apply：用户确认后写入缓存、替换目录并重载模型列表；</item>
/// <item>Discard：放弃暂存结果。</item>
/// </list>
/// </summary>
public sealed class CatalogSync(HttpClient http, BuiltInCatalog builtIn, ModelCatalog catalog, ILogger<CatalogSync> logger)
{
    public const string SourceUrl = "https://models.dev/api.json";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Pending? _pending;

    private sealed record Pending(Dictionary<string, CatalogProvider> Providers, string? ETag, DateTimeOffset FetchedAt);

    public bool IsChecking { get; private set; }
    public DateTimeOffset? LastChecked { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>可替换下载地址（测试用）。</summary>
    public string Url { get; init; } = Environment.GetEnvironmentVariable("TKCODE_CATALOG_URL") is { Length: > 0 } url ? url : SourceUrl;

    public CatalogStatusDto Status() => new(
        builtIn.IsSynced ? "synced" : "embedded",
        builtIn.Generated,
        builtIn.SyncedAt,
        builtIn.Providers.Count,
        builtIn.ModelCount,
        IsChecking,
        LastError,
        LastChecked);

    public async Task<CatalogUpdateDto> CheckAsync(IReadOnlySet<string> configured, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        IsChecking = true;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("TKCode/0.1");
            if (builtIn.IsSynced && builtIn.ETag is { } etag)
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            }
            var watch = Stopwatch.StartNew();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            LastChecked = DateTimeOffset.UtcNow;
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                _pending = null;
                LastError = null;
                return Empty(upToDate: true);
            }
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            var providers = ModelsDevTrimmer.Trim(doc);
            if (providers.Count == 0)
            {
                throw new InvalidDataException("models.dev returned no usable providers.");
            }
            LastError = null;
            var diff = CatalogDiff.Compute(builtIn.RawProviders, providers, configured);
            logger.LogInformation("Catalog check: {Providers} providers changed ({Ms} ms)", diff.Count, watch.ElapsedMilliseconds);
            if (diff.Count == 0)
            {
                _pending = null;
                return Empty(upToDate: true);
            }
            _pending = new Pending(providers, response.Headers.ETag?.ToString(), DateTimeOffset.UtcNow);
            var models = diff.SelectMany(p => p.Models).ToList();
            return new CatalogUpdateDto(Status(), false, true, DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                models.Count(m => m.Kind == CatalogChangeKind.Added),
                models.Count(m => m.Kind == CatalogChangeKind.Removed),
                models.Count(m => m.Kind == CatalogChangeKind.Changed),
                diff);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException or IOException)
        {
            LastError = ex is TaskCanceledException ? "timed out" : ex.Message;
            logger.LogWarning(ex, "Catalog check failed");
            return Empty(upToDate: false);
        }
        finally
        {
            IsChecking = false;
            _gate.Release();
        }
    }

    /// <summary>应用暂存的更新。没有暂存结果时不做任何事。</summary>
    public CatalogStatusDto Apply()
    {
        if (Interlocked.Exchange(ref _pending, null) is { } pending)
        {
            builtIn.Replace(pending.Providers, pending.FetchedAt, pending.ETag);
            logger.LogInformation("Catalog update applied: {Providers} providers, {Models} models", builtIn.Providers.Count, builtIn.ModelCount);
            catalog.Reload();
        }
        return Status();
    }

    public void Discard() => _pending = null;

    private CatalogUpdateDto Empty(bool upToDate) => new(Status(), upToDate, false, null, 0, 0, 0, []);
}

/// <summary>比较两份（未叠加补充的）目录，列出新增 / 移除 / 变更的厂商与模型及字段变化。已配置的厂商排在前面。</summary>
public static class CatalogDiff
{
    public static IReadOnlyList<CatalogProviderChangeDto> Compute(IReadOnlyDictionary<string, CatalogProvider> current,
        IReadOnlyDictionary<string, CatalogProvider> next, IReadOnlySet<string> configured)
    {
        var result = new List<CatalogProviderChangeDto>();
        foreach (var id in current.Keys.Union(next.Keys).Order(StringComparer.Ordinal))
        {
            var before = current.GetValueOrDefault(id);
            var after = next.GetValueOrDefault(id);
            if (before is null)
            {
                result.Add(new(id, after!.Name, CatalogChangeKind.Added, configured.Contains(id), [],
                    after.Models.Select(m => new CatalogModelChangeDto(m.Key, m.Value.Name ?? m.Key, CatalogChangeKind.Added, [])).ToArray()));
                continue;
            }
            if (after is null)
            {
                result.Add(new(id, before.Name, CatalogChangeKind.Removed, configured.Contains(id), [],
                    before.Models.Select(m => new CatalogModelChangeDto(m.Key, m.Value.Name ?? m.Key, CatalogChangeKind.Removed, [])).ToArray()));
                continue;
            }
            var providerChanges = new List<CatalogFieldChangeDto>();
            Compare(providerChanges, "baseUrl", before.BaseUrl, after.BaseUrl);
            Compare(providerChanges, "api", before.Api, after.Api);
            var models = new List<CatalogModelChangeDto>();
            foreach (var modelId in before.Models.Keys.Union(after.Models.Keys).Order(StringComparer.Ordinal))
            {
                var m0 = before.Models.GetValueOrDefault(modelId);
                var m1 = after.Models.GetValueOrDefault(modelId);
                if (m0 is null)
                {
                    models.Add(new(modelId, m1!.Name ?? modelId, CatalogChangeKind.Added, []));
                }
                else if (m1 is null)
                {
                    models.Add(new(modelId, m0.Name ?? modelId, CatalogChangeKind.Removed, []));
                }
                else if (ModelChanges(m0, m1) is { Count: > 0 } changes)
                {
                    models.Add(new(modelId, m1.Name ?? modelId, CatalogChangeKind.Changed, changes));
                }
            }
            if (providerChanges.Count > 0 || models.Count > 0)
            {
                // 变更的模型最值得关注，排在新增 / 移除之前
                var ordered = models.OrderBy(m => m.Kind switch { CatalogChangeKind.Changed => 0, CatalogChangeKind.Added => 1, _ => 2 }).ToArray();
                result.Add(new(id, after.Name, CatalogChangeKind.Changed, configured.Contains(id), providerChanges, ordered));
            }
        }
        return result.OrderByDescending(p => p.IsConfigured).ToArray();
    }

    private static List<CatalogFieldChangeDto> ModelChanges(CatalogModel a, CatalogModel b)
    {
        var changes = new List<CatalogFieldChangeDto>();
        Compare(changes, "name", a.Name, b.Name);
        Compare(changes, "contextWindow", Num(a.ContextWindow), Num(b.ContextWindow));
        Compare(changes, "maxTokens", Num(a.MaxTokens), Num(b.MaxTokens));
        Compare(changes, "reasoning", Bool(a.Reasoning), Bool(b.Reasoning));
        Compare(changes, "images", Bool(a.Images), Bool(b.Images));
        Compare(changes, "efforts", a.Efforts is null ? null : string.Join(",", a.Efforts), b.Efforts is null ? null : string.Join(",", b.Efforts));
        Compare(changes, "thinkingToggle", Bool(a.CanDisableThinking), Bool(b.CanDisableThinking));
        Compare(changes, "inputPrice", Price(a.Cost, 0), Price(b.Cost, 0));
        Compare(changes, "outputPrice", Price(a.Cost, 1), Price(b.Cost, 1));
        return changes;
    }

    private static void Compare(List<CatalogFieldChangeDto> changes, string field, string? a, string? b)
    {
        if (!string.Equals(a, b, StringComparison.Ordinal))
        {
            changes.Add(new CatalogFieldChangeDto(field, a, b));
        }
    }

    private static string? Num(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string Bool(bool value) => value ? "true" : "false";

    private static string? Price(List<double?>? cost, int index) =>
        cost is { Count: > 0 } && cost.Count > index && cost[index] is { } v ? v.ToString("0.####", CultureInfo.InvariantCulture) : null;
}
