using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TCode.Desktop.Updates;

/// <summary>GitHub REST 的 Release（只取用到的字段）。</summary>
public sealed record GitHubRelease
{
    public string TagName { get; init; } = "";
    public bool Draft { get; init; }
    public bool Prerelease { get; init; }
    public string? Body { get; init; }
    public string? HtmlUrl { get; init; }
    public List<GitHubAsset> Assets { get; init; } = [];
}

public sealed record GitHubAsset
{
    public string Name { get; init; } = "";
    public string BrowserDownloadUrl { get; init; } = "";
    public long Size { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<GitHubRelease>))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;

public enum UpdateChannel
{
    Stable,
    Preview,
}

/// <summary>检查得到的可用更新：清单已验签，本平台的安装程序已确定。</summary>
public sealed record UpdateCandidate(
    SemVersion Version,
    bool IsPreview,
    UpdateManifest Manifest,
    byte[] ManifestBytes,
    byte[] SignatureBytes,
    UpdateAsset Asset,
    string AssetUrl,
    string? Notes,
    string? NotesUrl);

/// <summary>检查结果：<see cref="Candidate"/> 为 null 表示已是最新。</summary>
public sealed record UpdateCheckResult(UpdateCandidate? Candidate);

/// <summary>检查时缓存的 ETag 与响应体（304 时复用，不计入未认证配额）。</summary>
public sealed class ReleaseListCache
{
    public string? ETag { get; set; }
    public string? Body { get; set; }
}

/// <summary>更新源出错（网络、限流后的回退也失败、清单验签失败等）。</summary>
public sealed class UpdateFeedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// 从 GitHub Releases 查找新版本（auto-update.md §5）：
/// 列出 Release（带 If-None-Match）→ 按通道和版本过滤 → 取最高版本 → 下载 manifest.json 与 .sig 验签 → 选出本平台的安装程序。
/// API 被限流时回退到 releases/latest/download/manifest.json（只能拿到稳定版）。
/// <para>TCODE_UPDATE_FEED 可指向另一个 HTTP 根地址（测试用）：其下的 releases.json 与 GitHub API 的格式相同，此时不做限流回退。</para>
/// </summary>
public sealed class GitHubReleaseFeed
{
    public const string Repository = "AtomUICommunity/TCode";
    private const string ManifestName = "manifest.json";
    private const string SignatureName = "manifest.json.sig";

    private readonly HttpClient _http;
    private readonly IReadOnlyList<string> _keys;
    private readonly string _releasesUrl;
    private readonly string? _fallbackBase;

    public GitHubReleaseFeed(HttpClient http, IReadOnlyList<string> publicKeys, string? feedOverride = null)
    {
        _http = http;
        _keys = publicKeys;
        if (feedOverride is { Length: > 0 })
        {
            _releasesUrl = feedOverride.TrimEnd('/') + "/releases.json";
            _fallbackBase = null;
        }
        else
        {
            _releasesUrl = $"https://api.github.com/repos/{Repository}/releases?per_page=20";
            _fallbackBase = $"https://github.com/{Repository}/releases";
        }
    }

    public async Task<UpdateCheckResult> CheckAsync(SemVersion current, UpdateChannel channel, string rid, Version osVersion, ReleaseListCache cache,
        CancellationToken cancellationToken)
    {
        List<GitHubRelease> releases;
        try
        {
            releases = await ListReleasesAsync(cache, cancellationToken).ConfigureAwait(false);
        }
        catch (RateLimitedException) when (_fallbackBase is not null)
        {
            return await CheckLatestStableAsync(current, rid, osVersion, cancellationToken).ConfigureAwait(false);
        }

        var best = releases
            .Where(r => !r.Draft && (channel == UpdateChannel.Preview || !r.Prerelease))
            .Where(r => r.Assets.Any(a => a.Name == ManifestName) && r.Assets.Any(a => a.Name == SignatureName))
            .Select(r => (Release: r, Ok: SemVersion.TryParse(r.TagName, out var v), Version: v))
            .Where(x => x.Ok && x.Version > current)
            .OrderByDescending(x => x.Version)
            .FirstOrDefault();
        if (best.Release is null)
        {
            return new UpdateCheckResult(null);
        }
        var release = best.Release;
        var manifestBytes = await GetBytesAsync(release.Assets.First(a => a.Name == ManifestName).BrowserDownloadUrl, cancellationToken).ConfigureAwait(false);
        var signature = await GetBytesAsync(release.Assets.First(a => a.Name == SignatureName).BrowserDownloadUrl, cancellationToken).ConfigureAwait(false);
        var manifest = Validate(manifestBytes, signature, best.Version, rid, osVersion);
        if (manifest is null)
        {
            return new UpdateCheckResult(null);
        }
        var asset = manifest.Assets[rid];
        var url = release.Assets.FirstOrDefault(a => a.Name == asset.Name)?.BrowserDownloadUrl
                  ?? throw new UpdateFeedException($"Release {release.TagName} is missing {asset.Name}.");
        return new UpdateCheckResult(new UpdateCandidate(best.Version, release.Prerelease, manifest, manifestBytes, signature, asset, url,
            release.Body, release.HtmlUrl ?? manifest.NotesUrl));
    }

    /// <summary>限流回退：最新稳定版的清单（不经过 API，没有更新说明正文）。</summary>
    private async Task<UpdateCheckResult> CheckLatestStableAsync(SemVersion current, string rid, Version osVersion, CancellationToken cancellationToken)
    {
        var manifestBytes = await GetBytesAsync($"{_fallbackBase}/latest/download/{ManifestName}", cancellationToken).ConfigureAwait(false);
        var signature = await GetBytesAsync($"{_fallbackBase}/latest/download/{SignatureName}", cancellationToken).ConfigureAwait(false);
        if (ManifestVerifier.VerifyAndParse(manifestBytes, signature, _keys) is not { } peek || !SemVersion.TryParse(peek.Version, out var version) ||
            version <= current)
        {
            return new UpdateCheckResult(null);
        }
        var manifest = Validate(manifestBytes, signature, version, rid, osVersion);
        if (manifest is null)
        {
            return new UpdateCheckResult(null);
        }
        var asset = manifest.Assets[rid];
        var url = $"{_fallbackBase}/download/v{version}/{Uri.EscapeDataString(asset.Name)}";
        return new UpdateCheckResult(new UpdateCandidate(version, false, manifest, manifestBytes, signature, asset, url, null, manifest.NotesUrl));
    }

    /// <summary>
    /// 验签并检查清单与 Release 一致。签名无效或版本不符抛出异常（可能被篡改）；本平台没有安装程序或系统版本过低时返回 null（当作没有更新）。
    /// </summary>
    private UpdateManifest? Validate(byte[] manifestBytes, byte[] signature, SemVersion version, string rid, Version osVersion)
    {
        var manifest = ManifestVerifier.VerifyAndParse(manifestBytes, signature, _keys)
                       ?? throw new UpdateFeedException($"The update manifest for {version} has an invalid signature.");
        if (!SemVersion.TryParse(manifest.Version, out var declared) || declared != version)
        {
            throw new UpdateFeedException($"The update manifest declares {manifest.Version}, expected {version}.");
        }
        if (!manifest.Assets.TryGetValue(rid, out var asset) || asset.Name.Length == 0 || asset.Sha256.Length != 64)
        {
            return null;
        }
        if (asset.MinOs is { } minOs && Version.TryParse(minOs.Contains('.') ? minOs : minOs + ".0", out var min) &&
            new Version(osVersion.Major, Math.Max(osVersion.Minor, 0)) < min)
        {
            return null;
        }
        return manifest;
    }

    private async Task<List<GitHubRelease>> ListReleasesAsync(ReleaseListCache cache, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _releasesUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (cache.ETag is { } etag && cache.Body is not null && EntityTagHeaderValue.TryParse(etag, out var tag))
        {
            request.Headers.IfNoneMatch.Add(tag);
        }
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body;
        if (response.StatusCode == HttpStatusCode.NotModified && cache.Body is not null)
        {
            body = cache.Body;
        }
        else if (response.StatusCode is HttpStatusCode.TooManyRequests ||
                 (response.StatusCode == HttpStatusCode.Forbidden && response.Headers.TryGetValues("X-RateLimit-Remaining", out var left) && left.FirstOrDefault() == "0"))
        {
            throw new RateLimitedException();
        }
        else
        {
            EnsureSuccess(response);
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            cache.ETag = response.Headers.ETag?.ToString();
            cache.Body = cache.ETag is null ? null : body;
        }
        try
        {
            return JsonSerializer.Deserialize(body, GitHubJsonContext.Default.ListGitHubRelease) ?? [];
        }
        catch (JsonException ex)
        {
            cache.ETag = null;
            cache.Body = null;
            throw new UpdateFeedException("The release list is not valid JSON.", ex);
        }
    }

    private async Task<byte[]> GetBytesAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateFeedException(ex.Message, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateFeedException("The request timed out.", ex);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateFeedException($"{response.RequestMessage?.RequestUri} returned {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
    }

    private sealed class RateLimitedException : Exception;
}
