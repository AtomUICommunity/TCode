using System.Text.Json.Nodes;

namespace TKCode.Config;

/// <summary>
/// settings.json / .mcp.json 中一个 MCP 服务器的原始写法（docs/design/mcp.md §4.1）。
/// 只做反序列化；类型推断、校验与变量展开见 <see cref="McpServerConfig"/>。
/// </summary>
public sealed record McpServerSettings
{
    /// <summary>stdio | http | sse；省略时按 command / url 推断。</summary>
    public string? Type { get; init; }
    public string? Command { get; init; }
    public List<string>? Args { get; init; }
    public Dictionary<string, string>? Env { get; init; }
    public string? Cwd { get; init; }
    /// <summary>stdio：额外加载的 dotenv 文件。</summary>
    public string? EnvFile { get; init; }
    public string? Url { get; init; }
    public Dictionary<string, string>? Headers { get; init; }
    public McpOAuthSettings? OAuth { get; init; }
    public bool? Enabled { get; init; }
    /// <summary>不暴露给模型的工具（MCP 原名）。</summary>
    public List<string>? DisabledTools { get; init; }
    /// <summary>Ask 档免审批的工具；"*" 表示整个服务器。</summary>
    public List<string>? AlwaysAllow { get; init; }
    /// <summary>连接（启动 + 握手）超时，秒。</summary>
    public int? StartupTimeout { get; init; }
    /// <summary>单次工具调用超时，秒。</summary>
    public int? ToolTimeout { get; init; }
    /// <summary>显示名。</summary>
    public string? Name { get; init; }
}

public sealed record McpOAuthSettings
{
    /// <summary>预注册的 client_id；省略时用动态注册。</summary>
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    /// <summary>固定的回调端口（预注册了 redirect URI 时）；省略时随机。</summary>
    public int? CallbackPort { get; init; }
    /// <summary>空格分隔的 scope；省略时由服务器决定。</summary>
    public string? Scopes { get; init; }
}

public enum McpTransportKind
{
    Stdio,
    /// <summary>Streamable HTTP（不通时自动回退到旧版 HTTP+SSE）。</summary>
    Http,
    /// <summary>强制使用旧版 HTTP+SSE。</summary>
    Sse,
}

/// <summary>配置的来源。</summary>
public enum McpConfigSource
{
    /// <summary>~/.tkcode/settings.json。</summary>
    Global,
    /// <summary>项目根目录的 .mcp.json。</summary>
    Project,
}

/// <summary>
/// 规整后的 MCP 服务器配置（字符串尚未展开变量）。<see cref="Error"/> 非空时该服务器不可用，但不影响其他服务器。
/// </summary>
public sealed record McpServerConfig
{
    public required string Key { get; init; }
    public McpConfigSource Source { get; init; }
    public McpTransportKind Transport { get; init; }
    public string? Command { get; init; }
    public IReadOnlyList<string> Args { get; init; } = [];
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();
    public string? Cwd { get; init; }
    public string? EnvFile { get; init; }
    public string? Url { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public McpOAuthSettings? OAuth { get; init; }
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<string> DisabledTools { get; init; } = [];
    public IReadOnlyList<string> AlwaysAllow { get; init; } = [];
    public TimeSpan StartupTimeout { get; init; } = DefaultStartupTimeout;
    public TimeSpan ToolTimeout { get; init; } = DefaultToolTimeout;
    public string? DisplayName { get; init; }
    public string? Error { get; init; }

    public static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultToolTimeout = TimeSpan.FromMinutes(10);

    /// <summary>服务器键（同时是工具名前缀）的最大长度。</summary>
    public const int MaxKeyLength = 32;

    public string Title => string.IsNullOrWhiteSpace(DisplayName) ? Key : DisplayName;

    public bool IsRemote => Transport != McpTransportKind.Stdio;

    /// <summary>远程服务器配置了 Authorization 头时不走 OAuth。</summary>
    public bool HasStaticAuthorization => Headers.Keys.Any(h => h.Equals("Authorization", StringComparison.OrdinalIgnoreCase));

    public bool AllowsAlways(string tool) => AlwaysAllow.Contains("*") || AlwaysAllow.Contains(tool);

    /// <summary>服务器键是否合法：[A-Za-z0-9_-]{1,32}。</summary>
    public static bool IsValidKey(string key) =>
        key.Length is > 0 and <= MaxKeyLength && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    /// <summary>把任意名字规整成合法的服务器键（导入时使用）。</summary>
    public static string SanitizeKey(string name)
    {
        var chars = name.Trim().Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '-').ToArray();
        var key = new string(chars).Trim('-');
        while (key.Contains("--", StringComparison.Ordinal))
        {
            key = key.Replace("--", "-", StringComparison.Ordinal);
        }
        if (key.Length > MaxKeyLength)
        {
            key = key[..MaxKeyLength].TrimEnd('-');
        }
        return key.Length == 0 ? "server" : key;
    }

    /// <summary>
    /// 从原始写法规整：推断传输类型、校验必填字段。<paramref name="settings"/> 为 null 表示该项无法解析（<paramref name="parseError"/>）。
    /// </summary>
    public static McpServerConfig From(string key, McpServerSettings? settings, McpConfigSource source, string? parseError = null)
    {
        if (settings is null)
        {
            return new McpServerConfig { Key = key, Source = source, Error = parseError ?? "Invalid server entry." };
        }
        string? error = null;
        if (!IsValidKey(key))
        {
            error = $"Invalid server name \"{key}\": use 1–{MaxKeyLength} letters, digits, '_' or '-'.";
        }
        McpTransportKind transport;
        switch (settings.Type?.Trim().ToLowerInvariant())
        {
            case null or "":
                transport = !string.IsNullOrWhiteSpace(settings.Command) ? McpTransportKind.Stdio
                    : !string.IsNullOrWhiteSpace(settings.Url) ? McpTransportKind.Http
                    : McpTransportKind.Stdio;
                break;
            case "stdio" or "local":
                transport = McpTransportKind.Stdio;
                break;
            case "http" or "streamable-http" or "streamablehttp" or "remote":
                transport = McpTransportKind.Http;
                break;
            case "sse":
                transport = McpTransportKind.Sse;
                break;
            default:
                transport = McpTransportKind.Stdio;
                error ??= $"Unsupported type \"{settings.Type}\": expected stdio, http or sse.";
                break;
        }
        if (error is null)
        {
            if (transport == McpTransportKind.Stdio && string.IsNullOrWhiteSpace(settings.Command))
            {
                error = "Missing \"command\" (or \"url\" for a remote server).";
            }
            else if (transport != McpTransportKind.Stdio && string.IsNullOrWhiteSpace(settings.Url))
            {
                error = "Missing \"url\".";
            }
        }
        return new McpServerConfig
        {
            Key = key,
            Source = source,
            Transport = transport,
            Command = settings.Command?.Trim(),
            Args = settings.Args ?? [],
            Env = settings.Env ?? new Dictionary<string, string>(),
            Cwd = settings.Cwd,
            EnvFile = settings.EnvFile,
            Url = settings.Url?.Trim(),
            Headers = settings.Headers ?? new Dictionary<string, string>(),
            OAuth = settings.OAuth,
            Enabled = settings.Enabled ?? true,
            DisabledTools = settings.DisabledTools ?? [],
            AlwaysAllow = settings.AlwaysAllow ?? [],
            StartupTimeout = settings.StartupTimeout is > 0 and var s ? TimeSpan.FromSeconds(s) : DefaultStartupTimeout,
            ToolTimeout = settings.ToolTimeout is > 0 and var t ? TimeSpan.FromSeconds(t) : DefaultToolTimeout,
            DisplayName = settings.Name,
            Error = error,
        };
    }

    /// <summary>解析一个 mcpServers 对象（settings.json 或 .mcp.json）：每项独立解析，一项出错不影响其他项。</summary>
    public static IReadOnlyList<McpServerConfig> ParseServers(JsonObject? servers, McpConfigSource source, List<string>? warnings = null)
    {
        if (servers is null)
        {
            return [];
        }
        var list = new List<McpServerConfig>();
        foreach (var (key, node) in servers)
        {
            if (node is not JsonObject obj)
            {
                list.Add(From(key, null, source, "Must be an object."));
                continue;
            }
            if (warnings is not null)
            {
                foreach (var (field, _) in obj)
                {
                    if (!KnownFields.Contains(field))
                    {
                        warnings.Add($"Unknown field \"mcpServers.{key}.{field}\" is ignored.");
                    }
                }
            }
            McpServerSettings? settings;
            string? parseError = null;
            try
            {
                settings = System.Text.Json.JsonSerializer.Deserialize(obj, SettingsJsonContext.Default.McpServerSettings);
            }
            catch (System.Text.Json.JsonException ex)
            {
                settings = null;
                parseError = ex.Message;
            }
            list.Add(From(key, settings, source, parseError));
        }
        return list;
    }

    /// <summary>settings.json / .mcp.json 中服务器条目的已知字段（其他客户端的专有字段只给出警告）。</summary>
    public static readonly HashSet<string> KnownFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "type", "command", "args", "env", "cwd", "envFile", "url", "headers", "oauth", "enabled", "disabledTools", "alwaysAllow",
        "startupTimeout", "toolTimeout", "name",
    };

    /// <summary>写回用：按 TKCode 写法序列化（省略默认值）。</summary>
    public JsonObject ToJson()
    {
        var obj = new JsonObject();
        if (Transport == McpTransportKind.Stdio)
        {
            obj["command"] = Command;
            if (Args.Count > 0)
            {
                obj["args"] = new JsonArray(Args.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());
            }
            if (Env.Count > 0)
            {
                obj["env"] = ToObject(Env);
            }
            if (Cwd is { Length: > 0 })
            {
                obj["cwd"] = Cwd;
            }
            if (EnvFile is { Length: > 0 })
            {
                obj["envFile"] = EnvFile;
            }
        }
        else
        {
            if (Transport == McpTransportKind.Sse)
            {
                obj["type"] = "sse";
            }
            obj["url"] = Url;
            if (Headers.Count > 0)
            {
                obj["headers"] = ToObject(Headers);
            }
            if (OAuth is { } oauth)
            {
                var o = new JsonObject();
                if (oauth.ClientId is { } id)
                {
                    o["clientId"] = id;
                }
                if (oauth.ClientSecret is { } secret)
                {
                    o["clientSecret"] = secret;
                }
                if (oauth.CallbackPort is { } port)
                {
                    o["callbackPort"] = port;
                }
                if (oauth.Scopes is { } scopes)
                {
                    o["scopes"] = scopes;
                }
                if (o.Count > 0)
                {
                    obj["oauth"] = o;
                }
            }
        }
        if (!Enabled)
        {
            obj["enabled"] = false;
        }
        if (DisabledTools.Count > 0)
        {
            obj["disabledTools"] = new JsonArray(DisabledTools.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
        }
        if (AlwaysAllow.Count > 0)
        {
            obj["alwaysAllow"] = new JsonArray(AlwaysAllow.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
        }
        if (StartupTimeout != DefaultStartupTimeout)
        {
            obj["startupTimeout"] = (int)StartupTimeout.TotalSeconds;
        }
        if (ToolTimeout != DefaultToolTimeout)
        {
            obj["toolTimeout"] = (int)ToolTimeout.TotalSeconds;
        }
        if (DisplayName is { Length: > 0 })
        {
            obj["name"] = DisplayName;
        }
        return obj;
    }

    private static JsonObject ToObject(IReadOnlyDictionary<string, string> map)
    {
        var obj = new JsonObject();
        foreach (var (k, v) in map)
        {
            obj[k] = v;
        }
        return obj;
    }
}
