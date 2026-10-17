using TKCode.Ai.Messages;
using TKCode.Ai.Models;
using TKCode.Ai.Transform;
using Xunit;

namespace TKCode.Ai.Tests;

public sealed class MessageTransformerTests
{
    private static readonly ModelInfo Claude = new() { Id = "claude-opus-5-5", Name = "c", Provider = "anthropic", Api = KnownApis.AnthropicMessages };

    private static AssistantMessage FromOpenAI(params ContentBlock[] blocks) => new(blocks)
    {
        Api = KnownApis.OpenAIResponses, Provider = "openai", Model = "gpt-5.6-sol", StopReason = StopReason.ToolUse,
    };

    [Fact]
    public void ForeignThinkingBecomesText_SignaturesDropped_AndToolIdsRemapped()
    {
        var messages = new Message[]
        {
            UserMessage.FromText("q"),
            FromOpenAI(new ThinkingContent("plan") { Signature = "{\"type\":\"reasoning\"}" },
                new ThinkingContent("") { Signature = "enc" },
                new ToolCallContent("call|weird id", "read_file", "{}")),
            new ToolResultMessage("call|weird id", "read_file", [new TextContent("ok")]),
        };

        var result = MessageTransformer.Prepare(messages, Claude, MessageTransformer.AnthropicToolCallId);

        var assistant = Assert.IsType<AssistantMessage>(result[1]);
        Assert.Equal("plan", Assert.IsType<TextContent>(assistant.Content[0]).Text);
        var call = Assert.IsType<ToolCallContent>(assistant.Content[1]);
        Assert.Equal("call_weird_id", call.Id);
        Assert.Equal("call_weird_id", Assert.IsType<ToolResultMessage>(result[2]).ToolCallId);
    }

    [Fact]
    public void OrphanToolCallGetsSyntheticError_AndAbortedTurnsAreSkipped()
    {
        var messages = new Message[]
        {
            UserMessage.FromText("q"),
            FromOpenAI(new ToolCallContent("c1", "read_file", "{}")),
            UserMessage.FromText("never mind"),
            new AssistantMessage([new TextContent("half")]) { Api = "x", Provider = "x", Model = "x", StopReason = StopReason.Aborted },
        };

        var result = MessageTransformer.Prepare(messages, Claude, MessageTransformer.AnthropicToolCallId);

        Assert.Equal(4, result.Count);
        var synthetic = Assert.IsType<ToolResultMessage>(result[2]);
        Assert.True(synthetic.IsError);
        Assert.IsType<UserMessage>(result[3]);
    }

    [Fact]
    public void ToolResultsWithoutMatchingCall_AreDropped()
    {
        var aborted = new AssistantMessage([new ToolCallContent("c9", "read_file", "{}")])
        {
            Api = "x", Provider = "x", Model = "x", StopReason = StopReason.Aborted,
        };
        var messages = new Message[]
        {
            UserMessage.FromText("q"),
            aborted,
            new ToolResultMessage("c9", "read_file", [new TextContent("late")]),
            UserMessage.FromText("again"),
        };

        var result = MessageTransformer.Prepare(messages, Claude, MessageTransformer.AnthropicToolCallId);

        Assert.Equal(2, result.Count);
        Assert.All(result, m => Assert.IsType<UserMessage>(m));
    }
}
