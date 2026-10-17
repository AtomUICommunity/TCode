using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using AngleSharp.Html.Parser;

namespace TKCode.Agent.Coding.Tools.Web;

/// <summary>
/// 抓取网页并转换为 Markdown（docs/design/builtin-agent-tools.md §8.1）：
/// 只允许 http/https（http 升级为 https），拒绝本机与私网地址（连接时按实际 IP 再校验一次），
/// 跨主机重定向交还模型，响应体上限 5MB，结果缓存 15 分钟。
/// </summary>
public sealed class WebFetcher : IDisposable
{
    public const int MaxBodyBytes = 5 * 1024 * 1024;
    private const int MaxRedirects = 5;
    private const int CacheCapacity = 64;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly bool _allowPrivateNetworks;
    private readonly ConcurrentDictionary<string, (DateTime At, FetchResult Result)> _cache = new(StringComparer.Ordinal);

    static WebFetcher()
    {
        // GBK、Shift_JIS 等非 Unicode 编码的网页。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <param name="handler">测试用；默认使用带私网校验的 SocketsHttpHandler。</param>
    /// <param name="allowPrivateNetworks">测试用：允许访问本机与私网地址。</param>
    public WebFetcher(HttpMessageHandler? handler = null, bool allowPrivateNetworks = false)
    {
        _allowPrivateNetworks = allowPrivateNetworks;
        _http = new HttpClient(handler ?? CreateHandler(allowPrivateNetworks), disposeHandler: true) { Timeout = Timeout };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; TKCode-User/1.0; +https://tkcode.dev)");
        _http.DefaultRequestHeaders.Accept.ParseAdd("text/markdown, text/html;q=0.9, application/xhtml+xml;q=0.9, text/plain;q=0.8, */*;q=0.5");
    }

    /// <param name="Markdown">正文（Markdown 或纯文本）；重定向到其他主机时为 null。</param>
    /// <param name="RedirectTo">跨主机重定向的目标地址。</param>
    public sealed record FetchResult(Uri Url, int StatusCode, string ContentType, string? Title, string? Markdown, Uri? RedirectTo = null);

    public sealed class FetchException(string message) : Exception(message);

    /// <summary>校验并规范化 URL：http 升级为 https，拒绝本机与内网主机名。</summary>
    public Uri Normalize(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new FetchException("URL must be an absolute http(s) URL.");
        }
        if (uri.Scheme == "http" && !_allowPrivateNetworks)
        {
            uri = new UriBuilder(uri) { Scheme = "https", Port = uri.IsDefaultPort ? -1 : uri.Port }.Uri;
        }
        if (!_allowPrivateNetworks)
        {
            var host = uri.IdnHost;
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
            {
                throw new FetchException($"Refusing to fetch {host}: local and internal hosts are not allowed.");
            }
            if (IPAddress.TryParse(host.Trim('[', ']'), out var ip))
            {
                if (IsPrivate(ip))
                {
                    throw new FetchException($"Refusing to fetch {host}: private, loopback and link-local addresses are not allowed.");
                }
            }
            else if (!host.Contains('.'))
            {
                throw new FetchException($"Refusing to fetch {host}: host names without a domain are not allowed.");
            }
        }
        return uri;
    }

    public async Task<FetchResult> FetchAsync(string url, CancellationToken cancellationToken)
    {
        var uri = Normalize(url);
        var key = uri.ToString();
        if (_cache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < CacheTtl)
        {
            return cached.Result;
        }

        var current = uri;
        for (var redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                var target = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (redirects >= MaxRedirects)
                {
                    throw new FetchException($"Too many redirects (last: {target}).");
                }
                if (!SameSite(current, target))
                {
                    return new FetchResult(current, status, "", null, null, target);
                }
                current = Normalize(target.ToString());
                continue;
            }

            var bytes = await ReadLimitedAsync(response.Content, cancellationToken);
            var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? SniffMediaType(bytes);
            var result = Convert(current, status, mediaType, response.Content.Headers.ContentType, bytes);
            if (status is >= 200 and < 300)
            {
                Remember(key, result);
            }
            return result;
        }
    }

    private static FetchResult Convert(Uri url, int status, string mediaType, MediaTypeHeaderValue? contentType, byte[] bytes)
    {
        if (mediaType is "text/html" or "application/xhtml+xml")
        {
            var parser = new HtmlParser();
            using var stream = new MemoryStream(bytes);
            var document = parser.ParseDocument(stream);
            var extracted = ReadabilityExtractor.Extract(document);
            var markdown = new HtmlToMarkdown(url).Convert(extracted.Content);
            return new FetchResult(url, status, mediaType, extracted.Title, markdown);
        }
        if (mediaType.StartsWith("text/", StringComparison.Ordinal) || mediaType is "application/json" or "application/xml" or "application/javascript"
            || mediaType.EndsWith("+json", StringComparison.Ordinal) || mediaType.EndsWith("+xml", StringComparison.Ordinal))
        {
            return new FetchResult(url, status, mediaType, null, Decode(bytes, contentType?.CharSet));
        }
        throw new FetchException($"Unsupported content type '{mediaType}' ({bytes.Length} bytes). web_fetch only returns text, HTML, Markdown, JSON and XML.");
    }

    private static string Decode(byte[] bytes, string? charset)
    {
        Encoding encoding = Encoding.UTF8;
        if (charset is { Length: > 0 })
        {
            try
            {
                encoding = Encoding.GetEncoding(charset.Trim('"'));
            }
            catch (ArgumentException)
            {
            }
        }
        return encoding.GetString(bytes);
    }

    private static string SniffMediaType(byte[] bytes)
    {
        var head = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 512)).TrimStart();
        return head.StartsWith('<') ? "text/html" : "text/plain";
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength is > MaxBodyBytes)
        {
            throw new FetchException($"Response is too large ({content.Headers.ContentLength / 1024 / 1024}MB, limit {MaxBodyBytes / 1024 / 1024}MB).");
        }
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                throw new FetchException($"Response is larger than {MaxBodyBytes / 1024 / 1024}MB.");
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>同一主机（忽略 www. 前缀）视为站内重定向，自动跟随。</summary>
    private static bool SameSite(Uri a, Uri b)
    {
        static string Strip(string host) => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        return string.Equals(Strip(a.IdnHost), Strip(b.IdnHost), StringComparison.OrdinalIgnoreCase);
    }

    private void Remember(string key, FetchResult result)
    {
        if (_cache.Count >= CacheCapacity)
        {
            foreach (var old in _cache.OrderBy(kv => kv.Value.At).Take(_cache.Count - CacheCapacity + 1).ToArray())
            {
                _cache.TryRemove(old.Key, out _);
            }
        }
        _cache[key] = (DateTime.UtcNow, result);
    }

    private static SocketsHttpHandler CreateHandler(bool allowPrivate) => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = async (context, ct) =>
        {
            // 按实际解析出的地址校验，防止 DNS 指向内网（SSRF）。
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            var allowed = allowPrivate ? addresses : addresses.Where(a => !IsPrivate(a)).ToArray();
            if (allowed.Length == 0)
            {
                throw new FetchException($"Refusing to connect to {context.DnsEndPoint.Host}: it resolves to a private or local address.");
            }
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None))
        {
            return true;
        }
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] switch
            {
                10 or 127 or 0 => true,
                172 => b[1] is >= 16 and <= 31,
                192 => b[1] == 168 || (b[1] == 0 && b[2] == 0),
                169 => b[1] == 254,
                100 => b[1] is >= 64 and <= 127,
                >= 224 => true,
                _ => false,
            };
        }
        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal;
    }

    public void Dispose() => _http.Dispose();
}
