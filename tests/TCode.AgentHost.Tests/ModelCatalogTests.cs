using Microsoft.Extensions.Logging.Abstractions;
using TCode.AgentHost;
using TCode.AgentHost.Catalog;
using TCode.Ai;
using TCode.Config;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace TCode.AgentHost.Tests;

/// <summary>settings.json 语义（docs/settings.md）。每个测试使用独立的 TCODE_HOME，并清掉常见厂商的环境变量。</summary>
public sealed class ModelCatalogTests : IDisposable
{
    private static readonly BuiltInCatalog Catalog = new();
    private static readonly string[] KeyVariables =
        ["ANTHROPIC_API_KEY", "OPENAI_API_KEY", "DEEPSEEK_API_KEY", "OPENROUTER_API_KEY", "MOONSHOT_API_KEY", "ZHIPU_API_KEY", "DASHSCOPE_API_KEY", "TCODE_FAKE_PROVIDER", "TCODE_TEST_KEY"];

    private readonly Dictionary<string, string?> _saved = [];
    private readonly string _home = Directory.CreateTempSubdirectory("tcode-catalog-").FullName;

    public ModelCatalogTests()
    {
        foreach (var name in KeyVariables.Append(ConfigPaths.HomeVariable))
        {
            _saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
        Environment.SetEnvironmentVariable(ConfigPaths.HomeVariable, _home);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    /// <summary>GET /models 的假响应：按 URL 返回预置内容。</summary>
    private sealed class FakeModelsHandler : HttpMessageHandler
    {
        public Dictionary<string, (System.Net.HttpStatusCode Status, string Body)> Responses { get; } = [];
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var (status, body) = Responses.GetValueOrDefault(request.RequestUri!.ToString(), (System.Net.HttpStatusCode.NotFound, "{}"));
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private readonly FakeModelsHandler _http = new();

    private ModelDiscovery Discovery() => new(new HttpClient(_http), NullLogger<ModelDiscovery>.Instance);

    private ModelCatalog Load(string json)
    {
        File.WriteAllText(Path.Combine(_home, "settings.json"), json);
        return new ModelCatalog(NullLogger<ModelCatalog>.Instance, Catalog, Discovery());
    }

    [Fact]
    public void NoSettings_OnlyFakeModels_AndSchemaWritten()
    {
        var catalog = new ModelCatalog(NullLogger<ModelCatalog>.Instance, Catalog, Discovery());
        Assert.All(catalog.Models, m => Assert.Equal("fake", m.Provider));
        Assert.True(File.Exists(Path.Combine(_home, "settings.schema.json")));
        // 推荐厂商列出但未启用（缺少 Key）
        Assert.Contains(catalog.Providers, p => p.Id == "anthropic" && !p.IsActive);
    }

    [Fact]
    public void ContextWindows_DefaultBudget_PerModelOverride_AndUserSetting()
    {
        var catalog = Load("""
            { "providers": {
                "anthropic": { "apiKey": "sk-ant-test" },
                "openai": { "apiKey": "sk-test", "models": { "gpt-6.1-sol": {}, "gpt-6-luna": { "defaultContextWindow": 400000 } } },
                "openrouter": { "apiKey": "sk-or", "models": { "openai/gpt-6.1-sol": {} } } } }
            """);
        int[] Windows(string provider, string id) => SessionOptions.ContextWindows(catalog.Models.Single(m => m.Provider == provider && m.Id == id)).ToArray();

        Assert.Equal([272_000, 1_000_000], Windows("anthropic", "claude-opus-5-5"));
        // 单独指定的模型：按去掉路由前缀的 id 匹配所有厂商
        Assert.Equal([500_000, 1_050_000], Windows("openai", "gpt-6.1-sol"));
        Assert.Equal([500_000, 1_050_000], Windows("openrouter", "openai/gpt-6.1-sol"));
        // settings.json 覆盖
        Assert.Equal([400_000, 1_050_000], Windows("openai", "gpt-6-luna"));
        // 模型窗口不超过默认预算：只有一档
        Assert.Equal([128_000], SessionOptions.ContextWindows(new TCode.Ai.Models.ModelInfo { Id = "m", Name = "m", Provider = "p", Api = "chat", ContextWindow = 128_000 }));
    }

    [Theory]
    [InlineData("gpt-6.1-sol", "gpt-6.1-sol")]
    [InlineData("openai/gpt-6.1-sol", "gpt-6.1-sol")]
    [InlineData("gpt-6.1-sol@eu", "gpt-6.1-sol")]
    [InlineData("auto/openai/gpt-6.1-sol:free", "gpt-6.1-sol")]
    public void BaseModelId_StripsRoutingPrefixAndSuffix(string id, string expected) => Assert.Equal(expected, BuiltInCatalog.BaseModelId(id));

    [Fact]
    public void SettingsWithoutProviders_Loads()
    {
        // 迁移 desktop.json 后的典型文件：只有语言与主题
        var catalog = Load("""{ "$schema": "./settings.schema.json", "language": "zh-CN", "theme": "dark" }""");
        Assert.Empty(catalog.Errors);
        Assert.All(catalog.Models, m => Assert.Equal("fake", m.Provider));
    }

    [Fact]
    public void BuiltInProvider_KeyOnly_EnablesFeaturedModelsWithCatalogMetadata()
    {
        var catalog = Load("""{ "providers": { "anthropic": { "apiKey": "sk-ant-test" } } }""");
        var models = catalog.Models.Where(m => m.Provider == "anthropic").ToList();
        Assert.Equal(["claude-opus-5-5", "claude-fable-5-1", "claude-sonnet-5-5", "claude-haiku-5-5"], models.Select(m => m.Id));
        var opus = models[0];
        Assert.Equal(KnownApis.AnthropicMessages, opus.Api);
        Assert.Equal(1_000_000, opus.ContextWindow);
        Assert.True(opus.SupportsFastMode);
        // 目录给了 effort 但没有开关：思考不能关闭；Haiku 有开关
        Assert.False(opus.Compat!.SupportsDisabledThinking);
        Assert.True(models.Single(m => m.Id == "claude-haiku-5-5").Compat!.SupportsDisabledThinking);
        Assert.Equal("sk-ant-test", catalog.GetApiKey("anthropic"));
        Assert.DoesNotContain(catalog.Models, m => m.Provider == "fake");
    }

    [Fact]
    public void EnvReference_And_CatalogEnvFallback()
    {
        Environment.SetEnvironmentVariable("TCODE_TEST_KEY", "from-env");
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "ds-env");
        var catalog = Load("""{ "providers": { "openai": { "apiKey": "{env:TCODE_TEST_KEY}" } } }""");
        Assert.Equal("from-env", catalog.GetApiKey("openai"));
        // 未配置的 deepseek：检测到目录登记的环境变量即启用
        var deepseek = catalog.Providers.Single(p => p.Id == "deepseek");
        Assert.True(deepseek.IsActive);
        Assert.Equal(KeySource.Environment, deepseek.Key.Source);
        Assert.Equal("DEEPSEEK_API_KEY", deepseek.Key.Variable);
        // DeepSeek 目录元数据推导出 reasoning_content 回传
        Assert.True(catalog.Models.First(m => m.Provider == "deepseek").Compat!.RequiresReasoningContentOnAssistant);
    }

    [Fact]
    public void ModelsMap_OnlyListedModels_WithOverrides()
    {
        var catalog = Load("""
        {
          // 注释与末尾逗号
          "providers": {
            "anthropic": { "apiKey": "k", "models": { "claude-haiku-5-5": { "contextWindow": 200000 }, }, },
          },
        }
        """);
        var model = Assert.Single(catalog.Models, m => m.Provider == "anthropic");
        Assert.Equal("claude-haiku-5-5", model.Id);
        Assert.Equal(200_000, model.ContextWindow);
        // 其它目录模型仍列在设置页中，但未启用
        Assert.Contains(catalog.Providers.Single(p => p.Id == "anthropic").Models, m => m.Model.Id == "claude-opus-5-5" && !m.Enabled);
    }

    [Fact]
    public void CustomProvider_MinimalModels_UseDefaults()
    {
        var catalog = Load("""
        { "providers": { "gw": { "api": "chat", "baseUrl": "https://gw.example.com/v1", "apiKey": "x",
            "models": { "qwen3-coder": { "reasoning": true }, "my-private-model": {} } } } }
        """);
        var models = catalog.Models.Where(m => m.Provider == "gw").ToList();
        Assert.Equal(["qwen3-coder", "my-private-model"], models.Select(m => m.Id));
        Assert.Equal(KnownApis.OpenAIChatCompletions, models[0].Api);
        Assert.True(models[0].Reasoning);
        Assert.Equal(128_000, models[1].ContextWindow);
        Assert.Equal(16_384, models[1].MaxTokens);
        Assert.NotNull(catalog.Find("gw/my-private-model"));
    }

    [Fact]
    public void ModelProtocol_OverridesProvider_AndNormalizesV1ForMessages()
    {
        var catalog = Load("""
        { "providers": { "gw": { "api": "chat", "baseUrl": "http://127.0.0.1:17321/v1", "apiKey": "x",
            "models": { "glm-5.3-flash": {}, "claude-opus-5-5": { "api": "messages" }, "gpt-6.1-sol": { "api": "responses", "baseUrl": "https://other/v1" } } } } }
        """);
        var chat = catalog.Find("gw/glm-5.3-flash")!;
        Assert.Equal(KnownApis.OpenAIChatCompletions, chat.Api);
        Assert.Equal("http://127.0.0.1:17321/v1", chat.BaseUrl);
        var messages = catalog.Find("gw/claude-opus-5-5")!;
        Assert.Equal(KnownApis.AnthropicMessages, messages.Api);
        Assert.Equal("http://127.0.0.1:17321", messages.BaseUrl); // 适配器自行拼 /v1/messages
        var responses = catalog.Find("gw/gpt-6.1-sol")!;
        Assert.Equal(KnownApis.OpenAIResponses, responses.Api);
        Assert.Equal("https://other/v1", responses.BaseUrl);
        var state = catalog.Providers.Single(p => p.Id == "gw").Models.Single(m => m.Model.Id == "claude-opus-5-5");
        Assert.True(state.ApiOverridden);
        Assert.Equal("messages", state.Api);
    }

    [Fact]
    public void ModelProtocol_InvalidValue_IsFileError()
    {
        var catalog = Load("""{ "providers": { "gw": { "api": "chat", "baseUrl": "https://g", "models": { "m": { "api": "grpc" } } } } }""");
        Assert.Contains(catalog.Errors, e => e.Contains("models.m.api"));
    }

    [Fact]
    public void CustomProvider_MissingApi_ReportsError_AndIsInactive()
    {
        var catalog = Load("""{ "providers": { "gw": { "baseUrl": "https://gw", "apiKey": "x", "models": { "m": {} } } } }""");
        // 单个厂商的问题不算文件错误：只停用该厂商
        Assert.Empty(catalog.Errors);
        var gw = catalog.Providers.Single(p => p.Id == "gw");
        Assert.Contains(gw.Errors, e => e.Contains("\"api\" is required"));
        Assert.False(gw.IsActive);
    }

    [Fact]
    public async Task CustomProvider_WithoutModels_DiscoversThem_AndBorrowsCatalogMetadata()
    {
        _http.Responses["https://gw.example.com/v1/models"] = (System.Net.HttpStatusCode.OK,
            """{ "object": "list", "data": [ { "id": "deepseek-v4-pro" }, { "id": "my-private-model" } ] }""");
        var catalog = Load("""{ "providers": { "gw": { "api": "chat", "baseUrl": "https://gw.example.com/v1", "apiKey": "x" } } }""");
        await catalog.RefreshModelsAsync("gw");

        var provider = catalog.Providers.Single(p => p.Id == "gw");
        Assert.True(provider.SupportsDiscovery);
        Assert.Empty(provider.Errors);
        Assert.True(provider.IsActive);
        Assert.Equal(["deepseek-v4-pro", "my-private-model"], catalog.Models.Where(m => m.Provider == "gw").Select(m => m.Id));
        // 目录中有同名模型：沿用其元数据
        var known = catalog.Find("gw/deepseek-v4-pro")!;
        Assert.Equal(1_000_000, known.ContextWindow);
        Assert.True(known.Compat!.RequiresReasoningContentOnAssistant);
        Assert.Equal(128_000, catalog.Find("gw/my-private-model")!.ContextWindow);
        Assert.Equal("Bearer", _http.Requests[^1].Headers.Authorization?.Scheme);
    }

    [Fact]
    public async Task CustomProvider_DiscoveryFails_WithoutModels_ReportsProviderError()
    {
        _http.Responses["https://gw.example.com/v1/models"] = (System.Net.HttpStatusCode.Unauthorized, """{ "error": { "message": "bad key" } }""");
        var catalog = Load("""{ "providers": { "gw": { "api": "chat", "baseUrl": "https://gw.example.com/v1", "apiKey": "x" } } }""");
        await catalog.RefreshModelsAsync("gw");
        var provider = catalog.Providers.Single(p => p.Id == "gw");
        Assert.Contains(provider.Errors, e => e.Contains("bad key"));
        Assert.Empty(catalog.Errors);
    }

    [Fact]
    public async Task Tester_UsesUnsavedKey_AndReportsUnauthorized()
    {
        _http.Responses["https://api.deepseek.com/models"] = (System.Net.HttpStatusCode.Unauthorized, """{ "error": { "message": "invalid api key" } }""");
        var discovery = Discovery();
        File.WriteAllText(Path.Combine(_home, "settings.json"), "{}");
        var catalog = new ModelCatalog(NullLogger<ModelCatalog>.Instance, Catalog, discovery);
        var tester = new ProviderTester(catalog, discovery, new WireApiRegistry([]));
        var result = await tester.TestAsync(new TCode.Rpc.ProviderTestRequest("deepseek", ApiKey: "sk-unsaved"), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(401, result.StatusCode);
        Assert.Contains("invalid api key", result.Message);
        Assert.Equal("sk-unsaved", _http.Requests[^1].Headers.Authorization?.Parameter);
    }

    [Fact]
    public void DisabledProvider_And_LocalProviderWithoutKey()
    {
        var catalog = Load("""
        { "providers": {
            "anthropic": { "apiKey": "k", "enabled": false },
            "ollama": { "models": { "qwen3:32b": {} } }
        } }
        """);
        Assert.DoesNotContain(catalog.Models, m => m.Provider == "anthropic");
        var local = Assert.Single(catalog.Models, m => m.Provider == "ollama");
        Assert.Equal("http://localhost:11434/v1", local.BaseUrl);
        Assert.Equal(KeySource.NotRequired, catalog.Providers.Single(p => p.Id == "ollama").Key.Source);
    }

    [Fact]
    public void InvalidFile_KeepsPreviousConfiguration()
    {
        var catalog = Load("""{ "providers": { "anthropic": { "apiKey": "k" } } }""");
        var before = catalog.Models.Count;
        File.WriteAllText(Path.Combine(_home, "settings.json"), """{ "providers": { "anthropic": """);
        Assert.False(catalog.Reload());
        Assert.NotEmpty(catalog.Errors);
        Assert.Equal(before, catalog.Models.Count);
    }

    [Fact]
    public void UnknownFields_AreWarnings_NotErrors()
    {
        var catalog = Load("""{ "modle": "x", "providers": { "anthropic": { "apiKey": "k", "baseURL2": "y" } } }""");
        Assert.Empty(catalog.Errors);
        Assert.Equal(2, catalog.Warnings.Count);
    }

    [Fact]
    public void LegacyModelsJson_IsMigrated()
    {
        File.WriteAllText(Path.Combine(_home, "models.json"), """
        { "providers": { "kimi": { "api": "openai-completions", "baseUrl": "https://api.moonshot.cn/v1", "apiKey": "k",
            "models": [ { "id": "kimi-k3", "supportsImages": true } ] } } }
        """);
        var catalog = new ModelCatalog(NullLogger<ModelCatalog>.Instance, Catalog, Discovery());
        var model = Assert.Single(catalog.Models, m => m.Provider == "kimi");
        Assert.True(model.SupportsImages);
        Assert.Equal(KnownApis.OpenAIChatCompletions, model.Api);
        Assert.True(File.Exists(Path.Combine(_home, "models.json.bak")));
        Assert.Contains("\"api\": \"chat\"", File.ReadAllText(Path.Combine(_home, "settings.json")));
    }

    [Fact]
    public void SettingsFile_Update_PreservesOtherFields_AndRestrictsPermissions()
    {
        var path = Path.Combine(_home, "settings.json");
        File.WriteAllText(path, """{ "theme": "dark", "providers": { "openai": { "enabled": false } } }""");
        SettingsFile.Update(root => SettingsFile.Provider(root, "anthropic")["apiKey"] = "sk-new", path);
        var text = File.ReadAllText(path);
        Assert.Contains("\"theme\": \"dark\"", text);
        Assert.Contains("\"enabled\": false", text);
        Assert.Contains("\"apiKey\": \"sk-new\"", text);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }
}
