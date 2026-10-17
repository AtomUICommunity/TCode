using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Authentication;
using TCode.Config;

namespace TCode.Mcp;

/// <summary>一个服务器保存的 OAuth 状态：令牌（含 client_id 与签发它的授权服务器）与上次使用的回调端口。</summary>
public sealed class McpAuthEntry
{
    public TokenContainer? Tokens { get; set; }
    public int? CallbackPort { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Dictionary<string, McpAuthEntry>))]
internal sealed partial class McpAuthJsonContext : JsonSerializerContext;

/// <summary>
/// MCP 服务器的 OAuth 令牌（docs/design/mcp.md §4.3、§9）：~/.tcode/mcp/auth.json，0600，按服务器的规范 URL 分组。
/// client_id / secret 与签发它的授权服务器（TokenContainer.AuthorizationServer）一起保存，SDK 负责按 issuer 校验与重新注册。
/// </summary>
public sealed class McpAuthStore(string? path = null)
{
    private readonly string _path = path ?? Path.Combine(ConfigPaths.Home, "mcp", "auth.json");
    private readonly Lock _gate = new();

    /// <summary>规范 URI：小写 scheme 与 host，去掉 fragment 与末尾斜杠（规格 §8）。</summary>
    public static string Canonical(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }
        var builder = new UriBuilder(uri) { Fragment = "", Scheme = uri.Scheme.ToLowerInvariant(), Host = uri.Host.ToLowerInvariant() };
        var text = builder.Uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped);
        return text.TrimEnd('/');
    }

    public bool HasTokens(string url)
    {
        lock (_gate)
        {
            return Read().TryGetValue(Canonical(url), out var entry) && entry.Tokens is not null;
        }
    }

    public McpAuthEntry? Get(string url)
    {
        lock (_gate)
        {
            return Read().GetValueOrDefault(Canonical(url));
        }
    }

    public void Update(string url, Action<McpAuthEntry> mutate)
    {
        lock (_gate)
        {
            var all = Read();
            var key = Canonical(url);
            if (!all.TryGetValue(key, out var entry))
            {
                entry = new McpAuthEntry();
                all[key] = entry;
            }
            mutate(entry);
            Write(all);
        }
    }

    public void Remove(string url)
    {
        lock (_gate)
        {
            var all = Read();
            if (all.Remove(Canonical(url)))
            {
                Write(all);
            }
        }
    }

    /// <summary>供 SDK 使用的令牌缓存（单个服务器）。</summary>
    public ITokenCache CacheFor(string url) => new Cache(this, url);

    private Dictionary<string, McpAuthEntry> Read()
    {
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize(File.ReadAllText(_path), McpAuthJsonContext.Default.DictionaryStringMcpAuthEntry) is { } all)
            {
                return all;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
        }
        return new Dictionary<string, McpAuthEntry>(StringComparer.Ordinal);
    }

    private void Write(Dictionary<string, McpAuthEntry> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        // 先创建为 0600 再写入，避免令牌短暂地以默认权限落盘
        using (var stream = OperatingSystem.IsWindows()
                   ? new FileStream(temp, FileMode.Create, FileAccess.Write)
                   : new FileStream(temp, new FileStreamOptions
                   {
                       Mode = FileMode.Create,
                       Access = FileAccess.Write,
                       UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                   }))
        {
            JsonSerializer.Serialize(stream, all, McpAuthJsonContext.Default.DictionaryStringMcpAuthEntry);
        }
        File.Move(temp, _path, overwrite: true);
    }

    private sealed class Cache(McpAuthStore store, string url) : ITokenCache
    {
        public ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
        {
            store.Update(url, e => e.Tokens = tokens);
            return ValueTask.CompletedTask;
        }

        public ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(store.Get(url)?.Tokens);
    }
}

/// <summary>需要用户登录（非交互连接时没有可用令牌）。</summary>
public sealed class McpAuthRequiredException() : Exception("Authentication required.");

/// <summary>
/// OAuth 的交互部分（docs/design/mcp.md §9）：回环监听回调，交给宿主打开系统浏览器。
/// 协议细节（PRM / AS 元数据发现、DCR、PKCE、resource、iss 与 state 校验、刷新、step-up）由 SDK 完成。
/// </summary>
public sealed class McpOAuth(McpAuthStore store, Func<string, string, CancellationToken, Task<bool>> openBrowser)
{
    public static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(5);

    public McpAuthStore Store { get; } = store;

    /// <summary>
    /// 为连接生成 OAuth 选项。没有保存的令牌且不是用户发起的登录时返回 null：直接连接，服务器返回 401 时进入 NeedsAuth，
    /// 不会在后台做动态注册或打开浏览器。
    /// </summary>
    public ClientOAuthOptions? OptionsFor(McpConnection connection)
    {
        var url = connection.Expansion.Url;
        if (url is null || (!connection.InteractiveAuthAllowed && !Store.HasTokens(url)))
        {
            return null;
        }
        var config = connection.Config;
        // 回调端口：配置中固定的端口 > 上次使用且仍空闲的端口（动态注册的 redirect URI 与端口绑定）> 新的随机端口
        int port;
        if (config.OAuth?.CallbackPort is { } fixedPort)
        {
            port = fixedPort;
        }
        else if (Store.Get(url)?.CallbackPort is { } saved && IsFree(saved))
        {
            port = saved;
        }
        else
        {
            port = FreePort();
        }
        Store.Update(url, e => e.CallbackPort = port);
        return new ClientOAuthOptions
        {
            RedirectUri = new Uri($"http://127.0.0.1:{port}/callback"),
            ClientId = config.OAuth?.ClientId is { Length: > 0 } id ? new McpVariables(connection.Workspace).Expand(id) : null,
            ClientSecret = config.OAuth?.ClientSecret is { Length: > 0 } secret ? new McpVariables(connection.Workspace).Expand(secret) : null,
            Scopes = config.OAuth?.Scopes?.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            DynamicClientRegistration = new DynamicClientRegistrationOptions { ClientName = "TCode" },
            TokenCache = Store.CacheFor(url),
            AuthorizationCallbackHandler = (context, ct) => AuthorizeAsync(connection, context, ct),
        };
    }

    private async Task<AuthorizationResult?> AuthorizeAsync(McpConnection connection, AuthorizationCallbackContext context, CancellationToken ct)
    {
        if (!connection.InteractiveAuthAllowed)
        {
            throw new McpAuthRequiredException();
        }
        using var listener = new HttpListener();
        var prefix = context.RedirectUri.GetLeftPart(UriPartial.Path);
        listener.Prefixes.Add(prefix.EndsWith('/') ? prefix : prefix + "/");
        listener.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(LoginTimeout);
        var pending = listener.GetContextAsync();
        connection.AppendLog($"Opening the browser to log in: {context.AuthorizationUri.GetLeftPart(UriPartial.Authority)}");
        if (!await openBrowser(connection.Key, context.AuthorizationUri.ToString(), timeout.Token))
        {
            throw new McpAuthRequiredException();
        }
        HttpListenerContext callback;
        try
        {
            callback = await pending.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            connection.AppendLog("Login was not completed in time.");
            throw new McpAuthRequiredException();
        }
        var query = callback.Request.QueryString;
        var error = query["error"];
        var body = Encoding.UTF8.GetBytes(error is null
            ? "<!doctype html><meta charset=utf-8><title>TCode</title><p style='font:15px system-ui;margin:40px'>Login complete. You can close this tab and return to TCode.</p>"
            : "<!doctype html><meta charset=utf-8><title>TCode</title><p style='font:15px system-ui;margin:40px'>Login failed. Return to TCode for details.</p>");
        callback.Response.ContentType = "text/html; charset=utf-8";
        await callback.Response.OutputStream.WriteAsync(body, ct);
        callback.Response.Close();
        if (error is not null)
        {
            // iss 校验之前不能显示 error 内容（规格 §8 第 8 步），这里只记录错误码
            connection.AppendLog($"Authorization failed ({error}).");
            throw new McpAuthRequiredException();
        }
        return new AuthorizationResult { Code = query["code"], State = query["state"], Iss = query["iss"] };
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool IsFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>没有 UI 时的兜底：用系统默认浏览器打开。</summary>
    public static bool OpenWithSystemBrowser(string url)
    {
        try
        {
            var start = OperatingSystem.IsMacOS() ? new ProcessStartInfo("open", [url])
                : OperatingSystem.IsWindows() ? new ProcessStartInfo(url) { UseShellExecute = true }
                : new ProcessStartInfo("xdg-open", [url]);
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
