using TKCode.Agent.Coding.Assets;
using TKCode.Ai.Models;
using Xunit;

namespace TKCode.Agent.Tests;

public sealed class BuiltinPromptCommandTests
{
    private static readonly ModelInfo Model = new() { Id = "claude-sonnet-5-5", Name = "Sonnet 5.5", Provider = "p", Api = "x", ContextWindow = 1, MaxTokens = 1 };

    [Theory]
    [InlineData("init")]
    [InlineData("INIT")]
    [InlineData("commit")]
    public void BuiltinPromptsAreFoundCaseInsensitively(string name)
    {
        Assert.True(BuiltinPromptCommands.Contains(name));
        Assert.False(string.IsNullOrWhiteSpace(BuiltinPromptCommands.Render(name, Model)));
    }

    [Fact]
    public void CommitTrailerCarriesTheCurrentModel()
    {
        var prompt = BuiltinPromptCommands.Render("commit", Model)!;

        Assert.Contains("Co-Authored-By: Sonnet 5.5 (claude-sonnet-5-5) <token@routin.ai>", prompt);
        Assert.DoesNotContain("{{model_id}}", prompt);
        Assert.DoesNotContain("{{model_name}}", prompt);
    }

    [Fact]
    public void UnknownNameIsNotBuiltin()
    {
        Assert.False(BuiltinPromptCommands.Contains("review"));
        Assert.Null(BuiltinPromptCommands.Render("review", Model));
    }
}
