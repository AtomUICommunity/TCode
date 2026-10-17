using TKCode.Ai.Models;
using Xunit;

namespace TKCode.Ai.Tests;

public sealed class ThinkingLevelsTests
{
    private static ModelInfo Model(string id, string api, string? baseUrl = null, ModelCompat? compat = null) =>
        new() { Id = id, Name = id, Provider = "p", Api = api, BaseUrl = baseUrl, Reasoning = true, Compat = compat };

    [Fact]
    public void AlwaysThinkingClaude_OffersEffortsWithoutOff()
    {
        var levels = ThinkingLevels.Available(Model("claude-opus-5-5", KnownApis.AnthropicMessages));
        Assert.Equal([ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High, ThinkingLevel.XHigh, ThinkingLevel.Max], levels);
        Assert.Equal(ThinkingLevel.High, ThinkingLevels.Default(Model("claude-opus-5-5", KnownApis.AnthropicMessages)));
    }

    [Fact]
    public void ClaudeThatCanDisable_OffersOff()
    {
        var levels = ThinkingLevels.Available(Model("claude-haiku-5-5", KnownApis.AnthropicMessages));
        Assert.Equal(ThinkingLevel.Off, levels[0]);
    }

    [Fact]
    public void DeepSeek_MapsNoneToOffAndKeepsDeclaredEfforts()
    {
        var levels = ThinkingLevels.Available(Model("deepseek-v4-pro", KnownApis.OpenAIChatCompletions, "https://api.deepseek.com"));
        Assert.Equal([ThinkingLevel.Off, ThinkingLevel.Low, ThinkingLevel.High, ThinkingLevel.Max], levels);
    }

    [Fact]
    public void Responses_UsesCompatEfforts()
    {
        var model = Model("gpt-6.1-sol", KnownApis.OpenAIResponses,
            compat: new ModelCompat { ReasoningEfforts = ["low", "medium", "high", "xhigh", "max"] });
        Assert.DoesNotContain(ThinkingLevel.Off, ThinkingLevels.Available(model));
        Assert.Equal(ThinkingLevel.High, ThinkingLevels.Default(model));
    }

    [Fact]
    public void NonReasoningModel_HasNoLevels()
    {
        var model = Model("x", KnownApis.OpenAIResponses) with { Reasoning = false };
        Assert.Empty(ThinkingLevels.Available(model));
        Assert.Equal(ThinkingLevel.Off, ThinkingLevels.Default(model));
    }
}
