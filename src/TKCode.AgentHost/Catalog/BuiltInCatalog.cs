using System.Text.Json;
using System.Text.Json.Serialization;
using TKCode.Ai;

namespace TKCode.AgentHost.Catalog;

public sealed record CatalogModel
{
    public string? Name { get; init; }
    public string? Description { get; init; }
    public int? ContextWindow { get; init; }
    public int? MaxTokens { get; init; }
    /// <summary>新会话默认的上下文预算（TKCode 补充，models.dev 没有）。</summary>
    public int? DefaultContextWindow { get; init; }
    public bool Reasoning { get; init; }
    public List<string>? Efforts { get; init; }
    public bool CanDisableThinking { get; init; }
    public bool Images { get; init; }
    /// <summary>带工具时历史助手消息必须回传 reasoning_content（DeepSeek、Kimi 等）。</summary>
    public bool ReasoningContent { get; init; }
    public bool FastMode { get; init; }
    public string? Released { get; init; }
    /// <summary>[输入, 输出, 缓存读, 缓存写]，美元 / 百万 tokens。</summary>
    public List<double?>? Cost { get; init; }
}

public sealed record CatalogProvider
{
    public required string Name { get; init; }
    /// <summary>chat | responses | messages。</summary>
    public required string Api { get; init; }
    public required string BaseUrl { get; init; }
    public List<string> Env { get; init; } = [];
    public Dictionary<string, CatalogModel> Models { get; init; } = new(StringComparer.Ordinal);
    /// <summary>未配置时默认启用的模型；为空时按发布时间自动挑选。</summary>
    public List<string>? Featured { get; init; }
    /// <summary>本地服务，不需要 API Key。</summary>
    public bool KeyOptional { get; init; }
}

internal sealed record CatalogFile
{
    public string? Source { get; init; }
    /// <summary>快照日期（yyyy-MM-dd）。</summary>
    public string? Generated { get; init; }
    /// <summary>在线同步的时间与 ETag（内置快照中为空）。</summary>
    public DateTimeOffset? Synced { get; init; }
    public string? ETag { get; init; }
    public Dictionary<string, CatalogProvider> Providers { get; init; } = new(StringComparer.Ordinal);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CatalogFile))]
internal sealed partial class CatalogJsonContext : JsonSerializerContext;

/// <summary>
/// 内置模型目录：嵌入的 models.dev 快照（tools/gen_catalog.py 生成）+ TKCode 补充（推荐模型、说明、fast mode、本地服务）。
/// 目录只描述“可以用什么”，是否启用由 settings.json 决定（见 docs/settings.md）。
/// </summary>
public sealed class BuiltInCatalog
{
    /// <summary>未配置 models 时自动挑选的推荐模型数量。</summary>
    private const int AutoFeaturedCount = 8;

    /// <summary>设置页“推荐厂商”：即使没有配置也会列出，方便填写 Key。</summary>
    public static readonly string[] Recommended = ["anthropic", "openai", "deepseek", "moonshotai-cn", "zhipuai", "alibaba-cn", "openrouter"];

    /// <summary>在线同步的缓存（~/.tkcode/cache/catalog.json）。</summary>
    public static string CachePath => Path.Combine(TKCode.Config.ConfigPaths.Home, "cache", "catalog.json");

    private volatile IReadOnlyDictionary<string, CatalogProvider> _providers;

    /// <summary>优先加载在线同步的缓存；缓存不存在或损坏时使用嵌入的快照。</summary>
    public BuiltInCatalog()
    {
        var file = LoadCache() ?? LoadEmbedded();
        IsSynced = file.Synced is not null;
        Generated = file.Generated;
        SyncedAt = file.Synced;
        ETag = file.ETag;
        _raw = file.Providers;
        _providers = Overlay(file.Providers);
    }

    private volatile Dictionary<string, CatalogProvider> _raw;

    /// <summary>未叠加 TKCode 补充的目录（与 models.dev 裁剪结果同一形态，用于比较更新）。</summary>
    public IReadOnlyDictionary<string, CatalogProvider> RawProviders => _raw;

    /// <summary>当前目录来自在线同步（否则为嵌入快照）。</summary>
    public bool IsSynced { get; private set; }
    public string? Generated { get; private set; }
    public DateTimeOffset? SyncedAt { get; private set; }
    public string? ETag { get; private set; }
    public IReadOnlyDictionary<string, CatalogProvider> Providers => _providers;
    public int ModelCount => _providers.Values.Sum(p => p.Models.Count);

    /// <summary>用在线同步的结果替换目录，并写入缓存。</summary>
    public void Replace(Dictionary<string, CatalogProvider> providers, DateTimeOffset syncedAt, string? etag)
    {
        var file = new CatalogFile
        {
            Source = "https://models.dev/api.json",
            Generated = syncedAt.UtcDateTime.ToString("yyyy-MM-dd"),
            Synced = syncedAt,
            ETag = etag,
            Providers = providers,
        };
        WriteCache(file);
        lock (_index)
        {
            _raw = providers;
            _providers = Overlay(providers);
            _index.Clear();
        }
        IsSynced = true;
        Generated = file.Generated;
        SyncedAt = syncedAt;
        ETag = etag;
    }

    /// <summary>远端未变化（304）：只更新检查时间。</summary>
    public void Touch(DateTimeOffset checkedAt)
    {
        if (LoadCache() is { } cached)
        {
            WriteCache(cached with { Synced = checkedAt });
            SyncedAt = checkedAt;
        }
    }

    private static CatalogFile LoadEmbedded()
    {
        using var stream = typeof(BuiltInCatalog).Assembly.GetManifestResourceStream("TKCode.AgentHost.catalog.json")
                           ?? throw new InvalidOperationException("Embedded catalog.json is missing.");
        return JsonSerializer.Deserialize(stream, CatalogJsonContext.Default.CatalogFile) ?? new CatalogFile();
    }

    private static CatalogFile? LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                return null;
            }
            using var stream = File.OpenRead(CachePath);
            var file = JsonSerializer.Deserialize(stream, CatalogJsonContext.Default.CatalogFile);
            return file is { Providers.Count: > 0 } ? file : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteCache(CatalogFile file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        var temp = CachePath + ".tmp";
        using (var stream = File.Create(temp))
        {
            JsonSerializer.Serialize(stream, file, CatalogJsonContext.Default.CatalogFile);
        }
        File.Move(temp, CachePath, overwrite: true);
    }

    public CatalogProvider? Find(string providerId) => Providers.GetValueOrDefault(providerId);

    /// <summary>
    /// 按模型 id 在整个目录中查找元数据（同协议的厂商优先，其次是模型原厂）。
    /// 用于代理与本地服务：它们转发的通常是目录中已有的模型。
    /// </summary>
    public CatalogModel? FindModel(string modelId, string? api)
    {
        lock (_index)
        {
            if (_index.Count == 0)
            {
                // 优先：推荐厂商（多为模型原厂）→ 有推荐模型列表的厂商 → 其余（聚合平台等）
                foreach (var (_, provider) in Providers
                             .OrderBy(p => Array.IndexOf(Recommended, p.Key) is var i && i >= 0 ? i : int.MaxValue)
                             .ThenByDescending(p => p.Value.Featured is not null))
                {
                    foreach (var (id, model) in provider.Models)
                    {
                        _index.TryAdd((id, provider.Api), model);
                        _index.TryAdd((id, null), model);
                    }
                }
            }
            return _index.GetValueOrDefault((modelId, api)) ?? _index.GetValueOrDefault((modelId, null));
        }
    }

    private readonly Dictionary<(string Id, string? Api), CatalogModel> _index = new();

    /// <summary>未配置 models 时启用的模型 id。</summary>
    public IReadOnlyList<string> FeaturedModels(CatalogProvider provider)
    {
        if (provider.Featured is { Count: > 0 } featured)
        {
            return featured.Where(provider.Models.ContainsKey).ToArray();
        }
        return provider.Models
            .OrderByDescending(m => m.Value.Released ?? "")
            .Take(AutoFeaturedCount)
            .Select(m => m.Key)
            .ToArray();
    }

    /// <summary>TKCode 对目录的补充。只写 models.dev 没有、或需要 TKCode 自己决定的信息。</summary>
    private static Dictionary<string, CatalogProvider> Overlay(Dictionary<string, CatalogProvider> providers)
    {
        var result = new Dictionary<string, CatalogProvider>(providers, StringComparer.Ordinal);

        void Patch(string id, Func<CatalogProvider, CatalogProvider> patch)
        {
            if (result.TryGetValue(id, out var provider))
            {
                result[id] = patch(provider);
            }
        }

        static Dictionary<string, CatalogModel> Models(CatalogProvider p, params (string Id, Func<CatalogModel, CatalogModel> Patch)[] patches)
        {
            var models = new Dictionary<string, CatalogModel>(p.Models, StringComparer.Ordinal);
            foreach (var (id, patch) in patches)
            {
                if (models.TryGetValue(id, out var model))
                {
                    models[id] = patch(model);
                }
            }
            return models;
        }

        Patch("anthropic", p => p with
        {
            Featured = ["claude-opus-5-5", "claude-fable-5-1", "claude-sonnet-5-5", "claude-haiku-5-5"],
            Models = Models(p,
                ("claude-opus-5-5", m => m with { Description = "Best for everyday, complex tasks", FastMode = true }),
                ("claude-fable-5-1", m => m with { Description = "Most capable model for the hardest problems" }),
                ("claude-sonnet-5-5", m => m with { Description = "Fast and capable for everyday coding" }),
                ("claude-haiku-5-5", m => m with { Description = "Fastest for quick, simple tasks" })),
        });
        Patch("openai", p => p with { Featured = ["gpt-6.1-sol", "gpt-6-astra", "gpt-6-luna", "gpt-5.5"] });
        Patch("deepseek", p => p with { Featured = ["deepseek-v4-pro", "deepseek-flash", "deepseek-v4-flash"] });
        Patch("lmstudio", p => p with { KeyOptional = true });
        ApplyDefaultContextWindows(result);
        // models.dev 只收录了 Ollama Cloud；本地 Ollama 的模型由用户在 settings.json 中列出
        result.TryAdd("ollama", new CatalogProvider
        {
            Name = "Ollama",
            Api = KnownApisShort.Chat,
            BaseUrl = "http://localhost:11434/v1",
            KeyOptional = true,
        });
        return result;
    }

    /// <summary>
    /// 单独设置默认上下文预算的模型（其余模型用全局默认值）。按模型 id 匹配所有厂商，
    /// 代理与聚合平台的 “openai/gpt-6.1-sol”、“gpt-6.1-sol@eu” 也算同一模型。
    /// </summary>
    private static readonly Dictionary<string, int> DefaultContextWindows = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-6.1-sol"] = 500_000,
    };

    private static void ApplyDefaultContextWindows(Dictionary<string, CatalogProvider> providers)
    {
        foreach (var (id, provider) in providers.ToArray())
        {
            Dictionary<string, CatalogModel>? models = null;
            foreach (var (modelId, model) in provider.Models)
            {
                if (model.DefaultContextWindow is null && DefaultContextWindows.TryGetValue(BaseModelId(modelId), out var window))
                {
                    models ??= new Dictionary<string, CatalogModel>(provider.Models, StringComparer.Ordinal);
                    models[modelId] = model with { DefaultContextWindow = window };
                }
            }
            if (models is not null)
            {
                providers[id] = provider with { Models = models };
            }
        }
    }

    /// <summary>去掉路由前缀（“openai/”）和区域、变体后缀（“@eu”、“:free”）。</summary>
    internal static string BaseModelId(string modelId)
    {
        var id = modelId[(modelId.LastIndexOf('/') + 1)..];
        var end = id.IndexOfAny(['@', ':']);
        return end > 0 ? id[..end] : id;
    }
}

/// <summary>settings.json 中的协议名 ↔ TKCode.Ai 的适配器名。</summary>
public static class KnownApisShort
{
    public const string Chat = "chat";
    public const string Responses = "responses";
    public const string Messages = "messages";

    public static string? ToWireApi(string? api) => api switch
    {
        Chat => KnownApis.OpenAIChatCompletions,
        Responses => KnownApis.OpenAIResponses,
        Messages => KnownApis.AnthropicMessages,
        // 兼容旧 models.json 的写法
        KnownApis.OpenAIChatCompletions or KnownApis.OpenAIResponses or KnownApis.AnthropicMessages => api,
        _ => null,
    };
}
