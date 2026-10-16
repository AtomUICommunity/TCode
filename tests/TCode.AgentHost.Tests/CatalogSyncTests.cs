using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TCode.AgentHost.Catalog;
using TCode.Config;
using Xunit;

namespace TCode.AgentHost.Tests;

public sealed class CatalogSyncTests : IDisposable
{
    /// <summary>models.dev 格式的最小样例：一个 Anthropic 协议厂商（baseURL 带 /v1）、一个兼容厂商、一个不支持的厂商。</summary>
    private const string Fixture = """
    {
      "minimax": { "id": "minimax", "name": "MiniMax", "npm": "@ai-sdk/anthropic", "api": "https://api.minimax.io/anthropic/v1",
        "env": ["MINIMAX_API_KEY"],
        "models": { "MiniMax-M3": { "id": "MiniMax-M3", "name": "MiniMax M3", "tool_call": true, "reasoning": true,
          "reasoning_options": [ { "type": "effort", "values": ["low", "high"] } ],
          "modalities": { "input": ["text", "image"], "output": ["text"] }, "limit": { "context": 1048576, "output": 131072 },
          "cost": { "input": 0.3, "output": 1.2 }, "release_date": "2026-09-01" } } },
      "acme": { "id": "acme", "name": "Acme", "npm": "@ai-sdk/openai-compatible", "api": "https://api.acme.dev/v1", "env": ["ACME_KEY"],
        "models": {
          "acme-coder": { "id": "acme-coder", "name": "Acme Coder", "tool_call": true, "limit": { "context": 64000, "output": 8000 },
            "interleaved": { "field": "reasoning_content" }, "reasoning": true, "reasoning_options": [ { "type": "toggle" } ] },
          "acme-old": { "id": "acme-old", "tool_call": true, "status": "deprecated" },
          "acme-embed": { "id": "acme-embed", "tool_call": false } } },
      "gemini": { "id": "google", "name": "Google", "npm": "@ai-sdk/google", "models": { "g": { "id": "g", "tool_call": true } } }
    }
    """;

    private readonly string? _savedHome = Environment.GetEnvironmentVariable(ConfigPaths.HomeVariable);
    private readonly string _home = Directory.CreateTempSubdirectory("tcode-sync-").FullName;

    public CatalogSyncTests() => Environment.SetEnvironmentVariable(ConfigPaths.HomeVariable, _home);

    public void Dispose() => Environment.SetEnvironmentVariable(ConfigPaths.HomeVariable, _savedHome);

    [Fact]
    public void Trimmer_MapsProtocols_FiltersModels_AndStripsAnthropicV1()
    {
        using var doc = JsonDocument.Parse(Fixture);
        var providers = ModelsDevTrimmer.Trim(doc);
        Assert.Equal(["acme", "minimax"], providers.Keys.Order());
        Assert.Equal("messages", providers["minimax"].Api);
        Assert.Equal("https://api.minimax.io/anthropic", providers["minimax"].BaseUrl);
        var m3 = providers["minimax"].Models["MiniMax-M3"];
        Assert.Equal(1_048_576, m3.ContextWindow);
        Assert.Equal(["low", "high"], m3.Efforts!);
        Assert.False(m3.CanDisableThinking);
        Assert.True(m3.Images);
        Assert.Equal([0.3, 1.2, null, null], m3.Cost!);
        var coder = Assert.Single(providers["acme"].Models).Value;
        Assert.True(coder.ReasoningContent);
        Assert.True(coder.CanDisableThinking);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public Queue<HttpResponseMessage> Responses { get; } = new();
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Responses.Dequeue());
        }
    }

    [Fact]
    public async Task Check_DoesNotApply_ApplyReplaces_UsesETag_AndKeepsCatalogOnFailure()
    {
        var handler = new Handler();
        var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Fixture) };
        ok.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
        handler.Responses.Enqueue(ok);
        handler.Responses.Enqueue(new HttpResponseMessage(HttpStatusCode.NotModified));
        handler.Responses.Enqueue(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var builtIn = new BuiltInCatalog();
        Assert.False(builtIn.IsSynced);
        var catalog = new ModelCatalog(NullLogger<ModelCatalog>.Instance, builtIn, new ModelDiscovery(new HttpClient(handler), NullLogger<ModelDiscovery>.Instance));
        var changed = 0;
        catalog.Changed += () => changed++;
        var sync = new CatalogSync(new HttpClient(handler), builtIn, catalog, NullLogger<CatalogSync>.Instance);

        // 检查：只比较，不应用
        var update = await sync.CheckAsync(new HashSet<string> { "anthropic" }, CancellationToken.None);
        Assert.True(update.HasPending);
        Assert.Equal(0, changed);
        Assert.Equal("embedded", update.Status.Source);
        Assert.NotNull(builtIn.Find("deepseek"));
        Assert.False(File.Exists(BuiltInCatalog.CachePath));
        // 已配置的 anthropic 排在最前，它在新目录中不存在 → 移除
        Assert.Equal("anthropic", update.Providers[0].ProviderId);
        Assert.Equal(TCode.Rpc.CatalogChangeKind.Removed, update.Providers[0].Kind);
        Assert.Contains(update.Providers, p => p.ProviderId == "acme" && p.Kind == TCode.Rpc.CatalogChangeKind.Added); // minimax 内置快照已有，属于变更

        // 应用
        var status = sync.Apply();
        Assert.Equal("synced", status.Source);
        Assert.Equal(3, status.ProviderCount); // acme、minimax + TCode 补充的本地 Ollama
        Assert.NotNull(builtIn.Find("minimax"));
        Assert.Null(builtIn.Find("deepseek"));
        Assert.Equal(1, changed);
        Assert.True(File.Exists(BuiltInCatalog.CachePath));

        // 再次检查：带 If-None-Match，304 → 已是最新
        update = await sync.CheckAsync(new HashSet<string>(), CancellationToken.None);
        Assert.Equal("\"v1\"", handler.Requests[^1].Headers.IfNoneMatch.Single().ToString());
        Assert.True(update.UpToDate);
        Assert.False(update.HasPending);

        // 失败：保留当前目录并报告错误
        update = await sync.CheckAsync(new HashSet<string>(), CancellationToken.None);
        Assert.NotNull(update.Status.LastError);
        Assert.NotNull(builtIn.Find("minimax"));

        // 重启后从缓存加载
        var reloaded = new BuiltInCatalog();
        Assert.True(reloaded.IsSynced);
        Assert.Equal("https://api.minimax.io/anthropic", reloaded.Find("minimax")!.BaseUrl);
    }
}

public sealed class CatalogDiffTests
{
    [Fact]
    public void Diff_ReportsFieldChanges_AndOrdersConfiguredFirst()
    {
        var before = new Dictionary<string, CatalogProvider>
        {
            ["a"] = new() { Name = "A", Api = "chat", BaseUrl = "https://a", Models = new() { ["m"] = new() { Name = "M", ContextWindow = 200_000, Cost = [3, 15, null, null] } } },
            ["b"] = new() { Name = "B", Api = "chat", BaseUrl = "https://b", Models = new() { ["x"] = new() { Name = "X" } } },
        };
        var after = new Dictionary<string, CatalogProvider>
        {
            ["a"] = new() { Name = "A", Api = "chat", BaseUrl = "https://a", Models = new() { ["m"] = new() { Name = "M", ContextWindow = 1_000_000, Cost = [2, 15, null, null] }, ["n"] = new() { Name = "N" } } },
            ["b"] = new() { Name = "B", Api = "chat", BaseUrl = "https://b2", Models = new() { ["x"] = new() { Name = "X" } } },
        };
        var diff = CatalogDiff.Compute(before, after, new HashSet<string> { "b" });
        Assert.Equal(["b", "a"], diff.Select(p => p.ProviderId));
        Assert.Equal(("https://b", "https://b2"), (diff[0].Changes.Single().OldValue, diff[0].Changes.Single().NewValue));
        var m = diff[1].Models.Single(x => x.ModelId == "m");
        Assert.Equal(["contextWindow", "inputPrice"], m.Changes.Select(c => c.Field));
        Assert.Equal("1000000", m.Changes[0].NewValue);
        Assert.Contains(diff[1].Models, x => x.ModelId == "n" && x.Kind == TCode.Rpc.CatalogChangeKind.Added);
        Assert.Empty(CatalogDiff.Compute(before, before, new HashSet<string>()));
    }
}
