using System.Text.Json;

namespace TKCode.AgentHost.Catalog;

/// <summary>
/// 把 models.dev 的 api.json 裁剪成 TKCode 目录格式。规则与 tools/gen_catalog.py 保持一致（两处修改需同步）：
/// 只保留能映射到 chat / responses / messages 的厂商，以及支持工具调用、未弃用、输出文本的模型。
/// 用 JsonDocument 遍历，不依赖反射（NativeAOT）。
/// </summary>
public static class ModelsDevTrimmer
{
    /// <summary>npm 包 → (协议, 缺省 Base URL)。</summary>
    private static readonly Dictionary<string, (string Api, string? BaseUrl)> Npm = new(StringComparer.Ordinal)
    {
        ["@ai-sdk/openai-compatible"] = ("chat", null),
        ["@openrouter/ai-sdk-provider"] = ("chat", "https://openrouter.ai/api/v1"),
        ["@ai-sdk/xai"] = ("chat", "https://api.x.ai/v1"),
        ["@ai-sdk/groq"] = ("chat", "https://api.groq.com/openai/v1"),
        ["@ai-sdk/cerebras"] = ("chat", "https://api.cerebras.ai/v1"),
        ["@ai-sdk/deepinfra"] = ("chat", "https://api.deepinfra.com/v1/openai"),
        ["@ai-sdk/openai"] = ("responses", "https://api.openai.com/v1"),
        ["@ai-sdk/anthropic"] = ("messages", "https://api.anthropic.com"),
    };

    public static Dictionary<string, CatalogProvider> Trim(JsonDocument source)
    {
        var providers = new SortedDictionary<string, CatalogProvider>(StringComparer.Ordinal);
        foreach (var p in source.RootElement.EnumerateObject())
        {
            var provider = p.Value;
            if (provider.ValueKind != JsonValueKind.Object || Str(provider, "npm") is not { } npm || !Npm.TryGetValue(npm, out var mapping))
            {
                continue;
            }
            var baseUrl = Str(provider, "api") ?? mapping.BaseUrl;
            if (string.IsNullOrEmpty(baseUrl))
            {
                continue;
            }
            // AI SDK 的 Anthropic baseURL 含 /v1（再拼 /messages），TKCode 的 Messages 适配器自己拼 /v1/messages
            if (mapping.Api == "messages" && baseUrl.TrimEnd('/').EndsWith("/v1", StringComparison.Ordinal))
            {
                baseUrl = baseUrl.TrimEnd('/')[..^3];
            }
            var models = new Dictionary<string, CatalogModel>(StringComparer.Ordinal);
            if (provider.TryGetProperty("models", out var modelsNode) && modelsNode.ValueKind == JsonValueKind.Object)
            {
                foreach (var m in modelsNode.EnumerateObject())
                {
                    // 单个模型改用其它 SDK 的跳过
                    if (m.Value.TryGetProperty("provider", out var mp) && mp.ValueKind == JsonValueKind.Object &&
                        Str(mp, "npm") is { } modelNpm && modelNpm != npm)
                    {
                        continue;
                    }
                    if (Model(m.Value, m.Name) is { } model)
                    {
                        models[m.Name] = model;
                    }
                }
            }
            if (models.Count == 0)
            {
                continue;
            }
            var env = provider.TryGetProperty("env", out var envNode) && envNode.ValueKind == JsonValueKind.Array
                ? envNode.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
                : [];
            providers[p.Name] = new CatalogProvider
            {
                Name = Str(provider, "name") ?? p.Name,
                Api = mapping.Api,
                BaseUrl = baseUrl,
                Env = env,
                Models = models,
            };
        }
        return new Dictionary<string, CatalogProvider>(providers, StringComparer.Ordinal);
    }

    private static CatalogModel? Model(JsonElement m, string id)
    {
        if (m.ValueKind != JsonValueKind.Object || !Bool(m, "tool_call") || Str(m, "status") == "deprecated" ||
            Str(m, "type") is "embedding" or "reranking")
        {
            return null;
        }
        var modalities = m.TryGetProperty("modalities", out var mod) && mod.ValueKind == JsonValueKind.Object ? mod : default;
        var output = Strings(modalities, "output");
        if (output.Count > 0 && !output.Contains("text"))
        {
            return null;
        }
        var limit = m.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Object ? l : default;
        List<string>? efforts = null;
        var canDisable = false;
        var reasoning = Bool(m, "reasoning");
        if (reasoning && m.TryGetProperty("reasoning_options", out var options) && options.ValueKind == JsonValueKind.Array)
        {
            foreach (var option in options.EnumerateArray())
            {
                switch (Str(option, "type"))
                {
                    case "effort" when Strings(option, "values") is { Count: > 0 } values:
                        efforts = values;
                        break;
                    case "toggle":
                        canDisable = true;
                        break;
                }
            }
        }
        List<double?>? cost = null;
        if (m.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Object && Num(c, "input") is { } input && Num(c, "output") is { } outputPrice)
        {
            cost = [input, outputPrice, Num(c, "cache_read"), Num(c, "cache_write")];
        }
        var interleaved = m.TryGetProperty("interleaved", out var il) && il.ValueKind == JsonValueKind.Object && Str(il, "field") == "reasoning_content";
        return new CatalogModel
        {
            Name = Str(m, "name") ?? id,
            ContextWindow = Int(limit, "context"),
            MaxTokens = Int(limit, "output"),
            Reasoning = reasoning,
            Efforts = efforts,
            CanDisableThinking = canDisable,
            Images = Strings(modalities, "input").Contains("image"),
            ReasoningContent = interleaved,
            Released = Str(m, "release_date"),
            Cost = cost,
        };
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n > 0
            ? (int)Math.Min(n, int.MaxValue)
            : null;

    private static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static List<string> Strings(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];
}
