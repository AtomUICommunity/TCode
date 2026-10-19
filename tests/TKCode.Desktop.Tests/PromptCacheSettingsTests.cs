using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using TKCode.Desktop.Services;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>提示缓存：设置页写回 promptCacheTtl（命中率显示在上下文容量卡片里，见 ContextUsageCardTests）。</summary>
public sealed class PromptCacheSettingsTests
{
    [Fact]
    public void PromptCacheTtl_OneHourIsWritten_FiveMinutesRemovesTheField()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("tkcode-cache-ttl-").FullName, "settings.json");
        using var settings = new UserSettingsService(NullLogger<UserSettingsService>.Instance, path);

        settings.SetPromptCacheTtl("1h");
        Assert.Equal("1h", settings.Current.PromptCacheTtl);
        Assert.Equal("1h", JsonNode.Parse(File.ReadAllText(path))!["promptCacheTtl"]!.GetValue<string>());

        // 默认值不落盘，保持 settings.json 干净
        settings.SetPromptCacheTtl("5m");
        Assert.Null(settings.Current.PromptCacheTtl);
        Assert.Null(JsonNode.Parse(File.ReadAllText(path))!["promptCacheTtl"]);
    }
}
