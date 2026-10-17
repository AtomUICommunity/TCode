using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TCode.Config;

/// <summary>
/// settings.json 的读取与写回。
/// 读取：JSONC、字段名不区分大小写；未知字段给出警告而不报错。
/// 写回：在 JSON 节点上只改动涉及的字段，保留其余字段与键的顺序（注释会丢失）；原子替换文件并设为 0600。
/// </summary>
public static class SettingsFile
{
    public const string SchemaReference = "./settings.schema.json";

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly HashSet<string> TopLevelKeys = new(StringComparer.OrdinalIgnoreCase) { "$schema", "model", "language", "theme", "reduceMotion", "providers", "webSearch", "permissions", "mcpServers" };
    private static readonly HashSet<string> WebSearchKeys = new(StringComparer.OrdinalIgnoreCase) { "provider", "apiKey", "baseUrl" };
    private static readonly HashSet<string> WebSearchProviders = new(StringComparer.OrdinalIgnoreCase) { "brave", "tavily", "exa", "searxng" };
    private static readonly HashSet<string> PermissionKeys = new(StringComparer.OrdinalIgnoreCase) { "additionalDirectories" };
    private static readonly HashSet<string> ProviderKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "api", "baseUrl", "apiKey", "enabled", "headers", "models", "compat",
    };
    private static readonly HashSet<string> ModelKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "api", "baseUrl", "name", "description", "contextWindow", "maxTokens", "reasoning", "efforts", "images", "fastMode", "compat",
    };
    private static readonly HashSet<string> Apis = new(StringComparer.Ordinal) { "chat", "responses", "messages" };

    public static SettingsLoadResult Load(string? path = null)
    {
        path ??= ConfigPaths.SettingsFile;
        if (!File.Exists(path))
        {
            return new SettingsLoadResult(new TCodeSettings(), [], []);
        }
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            return new SettingsLoadResult(null, [ex.Message], []);
        }
        return Parse(text);
    }

    public static SettingsLoadResult Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new SettingsLoadResult(new TCodeSettings(), [], []);
        }
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text, documentOptions: DocumentOptions);
        }
        catch (JsonException ex)
        {
            return new SettingsLoadResult(null, [Describe(ex)], []);
        }
        if (root is not JsonObject obj)
        {
            return new SettingsLoadResult(null, ["settings.json must contain a JSON object."], []);
        }

        var warnings = new List<string>();
        var errors = new List<string>();
        Check(obj, TopLevelKeys, "", warnings);
        if (obj["providers"] is JsonObject providers)
        {
            foreach (var (id, node) in providers)
            {
                if (node is not JsonObject provider)
                {
                    errors.Add($"providers.{id}: must be an object.");
                    continue;
                }
                Check(provider, ProviderKeys, $"providers.{id}.", warnings);
                if (provider["api"] is JsonValue api && !Apis.Contains(api.ToString()))
                {
                    errors.Add($"providers.{id}.api: expected chat, responses or messages, got \"{api}\".");
                }
                if (provider["models"] is JsonObject models)
                {
                    foreach (var (modelId, model) in models)
                    {
                        if (model is JsonObject m)
                        {
                            Check(m, ModelKeys, $"providers.{id}.models.{modelId}.", warnings);
                            if (m["api"] is JsonValue modelApi && !Apis.Contains(modelApi.ToString()))
                            {
                                errors.Add($"providers.{id}.models.{modelId}.api: expected chat, responses or messages, got \"{modelApi}\".");
                            }
                        }
                        else if (model is not null)
                        {
                            errors.Add($"providers.{id}.models.{modelId}: must be an object (use {{}} for defaults).");
                        }
                    }
                }
            }
        }
        if (obj["webSearch"] is JsonObject webSearch)
        {
            Check(webSearch, WebSearchKeys, "webSearch.", warnings);
            if (webSearch["provider"] is JsonValue provider && !WebSearchProviders.Contains(provider.ToString()))
            {
                errors.Add($"webSearch.provider: expected brave, tavily, exa or searxng, got \"{provider}\".");
            }
        }
        if (obj["permissions"] is JsonObject permissions)
        {
            Check(permissions, PermissionKeys, "permissions.", warnings);
        }
        if (obj["mcpServers"] is { } mcp && mcp is not JsonObject)
        {
            errors.Add("mcpServers: must be an object.");
        }
        else if (obj["mcpServers"] is JsonObject mcpServers)
        {
            // 单个服务器的错误只停用该服务器（在设置页显示），不让整个文件失效
            _ = McpServerConfig.ParseServers(mcpServers, McpConfigSource.Global, warnings);
        }
        if (errors.Count > 0)
        {
            return new SettingsLoadResult(null, errors, warnings);
        }
        try
        {
            var settings = obj.Deserialize(SettingsJsonContext.Default.TCodeSettings) ?? new TCodeSettings();
            // 源生成的反序列化会把 JSON 中缺失的 init 属性设为 null（属性初始值不生效），这里补回空集合
            settings = settings with { Providers = settings.Providers ?? new Dictionary<string, ProviderSettings>(StringComparer.Ordinal) };
            return new SettingsLoadResult(settings, [], warnings);
        }
        catch (JsonException ex)
        {
            return new SettingsLoadResult(null, [Describe(ex)], warnings);
        }
    }

    /// <summary>读取 → 修改节点 → 写回。文件不存在时创建（带 $schema）。</summary>
    public static void Update(Action<JsonObject> mutate, string? path = null)
    {
        path ??= ConfigPaths.SettingsFile;
        JsonObject root;
        if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path), documentOptions: DocumentOptions) is JsonObject existing)
        {
            root = existing;
        }
        else
        {
            root = new JsonObject { ["$schema"] = SchemaReference };
        }
        mutate(root);
        Write(path, root);
    }

    /// <summary>取得（必要时创建）providers.&lt;id&gt; 节点。</summary>
    public static JsonObject Provider(JsonObject root, string id)
    {
        if (root["providers"] is not JsonObject providers)
        {
            providers = new JsonObject();
            root["providers"] = providers;
        }
        if (providers[id] is not JsonObject provider)
        {
            provider = new JsonObject();
            providers[id] = provider;
        }
        return provider;
    }

    /// <summary>设置或删除（null）mcpServers.&lt;key&gt;。</summary>
    public static void SetMcpServer(JsonObject root, string key, JsonObject? server)
    {
        if (root["mcpServers"] is not JsonObject servers)
        {
            if (server is null)
            {
                return;
            }
            servers = new JsonObject();
            root["mcpServers"] = servers;
        }
        if (server is null)
        {
            servers.Remove(key);
        }
        else
        {
            servers[key] = server;
        }
    }

    public static void RemoveProvider(JsonObject root, string id)
    {
        if (root["providers"] is JsonObject providers)
        {
            providers.Remove(id);
        }
    }

    /// <summary>设置或删除（value 为 null）一个字段。</summary>
    public static void Set(JsonObject obj, string key, JsonNode? value)
    {
        if (value is null)
        {
            obj.Remove(key);
        }
        else
        {
            obj[key] = value;
        }
    }

    private static void Write(string path, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        var temp = path + ".tmp";
        File.WriteAllText(temp, json + "\n", new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            // 文件里可能有明文 API Key
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        File.Move(temp, path, overwrite: true);
    }

    private static void Check(JsonObject obj, HashSet<string> known, string prefix, List<string> warnings)
    {
        foreach (var (key, _) in obj)
        {
            if (!known.Contains(key))
            {
                warnings.Add($"Unknown field \"{prefix}{key}\" is ignored.");
            }
        }
    }

    private static string Describe(JsonException ex) =>
        ex.LineNumber is { } line ? $"Line {line + 1}, column {(ex.BytePositionInLine ?? 0) + 1}: {ex.Message}" : ex.Message;
}
