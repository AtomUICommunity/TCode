using System.Text.Json;
using TKCode.AgentHost.Catalog;
using Xunit;

namespace TKCode.AgentHost.Tests;

/// <summary>
/// 可选的一致性检查：设置 TKCODE_MODELSDEV_FILE 指向 tools/gen_catalog.py 使用的同一份 api.json 时，
/// C# 裁剪结果应与嵌入的 catalog.json 完全一致（两处规则必须同步修改）。
/// </summary>
public sealed class TrimmerParityTests
{
    [Fact]
    public void CSharpTrimmer_MatchesPythonGenerator()
    {
        if (Environment.GetEnvironmentVariable("TKCODE_MODELSDEV_FILE") is not { Length: > 0 } path || !File.Exists(path))
        {
            return;
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var csharp = ModelsDevTrimmer.Trim(doc);
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/TKCode.AgentHost/Catalog/catalog.json"));
        using var embedded = JsonDocument.Parse(File.ReadAllText(root));
        var python = embedded.RootElement.GetProperty("providers");
        Assert.Equal(python.EnumerateObject().Select(p => p.Name).Order(), csharp.Keys.Order());
        foreach (var p in python.EnumerateObject())
        {
            var c = csharp[p.Name];
            Assert.Equal(p.Value.GetProperty("baseUrl").GetString(), c.BaseUrl);
            Assert.Equal(p.Value.GetProperty("api").GetString(), c.Api);
            var models = p.Value.GetProperty("models");
            Assert.Equal(models.EnumerateObject().Select(m => m.Name).Order(), c.Models.Keys.Order());
            foreach (var m in models.EnumerateObject())
            {
                var cm = c.Models[m.Name];
                Assert.Equal(m.Value.TryGetProperty("contextWindow", out var ctx) ? ctx.GetInt32() : null, cm.ContextWindow);
                Assert.Equal(m.Value.TryGetProperty("reasoningContent", out _), cm.ReasoningContent);
                Assert.Equal(m.Value.TryGetProperty("canDisableThinking", out _), cm.CanDisableThinking);
                Assert.Equal(m.Value.TryGetProperty("images", out _), cm.Images);
            }
        }
    }
}
