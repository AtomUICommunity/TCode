using System.Net;
using System.Text.Json.Nodes;
using TKCode.Ai.Apis;
using TKCode.Ai.Messages;
using TKCode.Ai.Models;
using TKCode.Ai.Streaming;
using Xunit;

namespace TKCode.Ai.Tests;

public sealed class AnthropicMessagesApiTests
{
    private static readonly ModelInfo Opus = new()
    {
        Id = "claude-opus-5-5", Name = "Claude Opus 5.5", Provider = "anthropic", Api = KnownApis.AnthropicMessages, Reasoning = true,
    };

    // 规格 §6.7 的示例流：thinking + signature + tool_use（参数分两段）
    private static readonly string ThinkingToolStream = Sse.Of(
        ("message_start", """{"type":"message_start","message":{"id":"msg_01","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":25,"cache_creation_input_tokens":10,"cache_read_input_tokens":48000,"output_tokens":1}}}"""),
        ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}"""),
        ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"需要先读取文件。"}}"""),
        ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"EqQBCkgI"}}"""),
        ("content_block_stop", """{"type":"content_block_stop","index":0}"""),
        ("content_block_start", """{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_01","name":"read_file","input":{}}}"""),
        ("content_block_delta", """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"path\": \"REA"}}"""),
        ("content_block_delta", """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"DME.md\"}"}}"""),
        ("content_block_stop", """{"type":"content_block_stop","index":1}"""),
        ("ping", """{"type":"ping"}"""),
        ("message_delta", """{"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":89}}"""),
        ("message_stop", """{"type":"message_stop"}"""));

    [Fact]
    public async Task Stream_ParsesThinkingSignatureToolUseAndCumulativeUsage()
    {
        var handler = new FakeHandler((HttpStatusCode.OK, ThinkingToolStream));
        var api = new AnthropicMessagesApi(new HttpClient(handler));

        var (events, message) = await Run.StreamAsync(api, Opus, Run.Context(UserMessage.FromText("读 README")));

        Assert.Equal(StopReason.ToolUse, message.StopReason);
        var thinking = Assert.IsType<ThinkingContent>(message.Content[0]);
        Assert.Equal("需要先读取文件。", thinking.Thinking);
        Assert.Equal("EqQBCkgI", thinking.Signature);
        var call = Assert.IsType<ToolCallContent>(message.Content[1]);
        Assert.Equal("toolu_01", call.Id);
        Assert.Equal("{\"path\": \"README.md\"}", call.ArgumentsJson);
        Assert.Null(call.ArgumentsError);
        // input_tokens 不含缓存；message_delta 覆盖 output_tokens
        Assert.Equal(new Usage(25, 89, 48000, 10), message.Usage);
        Assert.Contains(events, e => e is ToolCallDeltaEvent);
        Assert.IsType<DoneEvent>(events[^1]);
    }

    [Fact]
    public async Task Request_ReplaysThinkingVerbatim_MergesToolResultsFirst_AndUsesAdaptiveEffort()
    {
        var handler = new FakeHandler((HttpStatusCode.OK, ThinkingToolStream), (HttpStatusCode.OK, ThinkingToolStream));
        var api = new AnthropicMessagesApi(new HttpClient(handler));
        var (_, first) = await Run.StreamAsync(api, Opus, Run.Context(UserMessage.FromText("读 README")));

        // 工具结果之后紧跟一条 steer 进来的用户消息：必须合并进同一个 user turn，且 tool_result 在前。
        var context = Run.Context(
            UserMessage.FromText("读 README"),
            first,
            new ToolResultMessage("toolu_01", "read_file", [new TextContent("# Title")]),
            UserMessage.FromText("顺便看下 LICENSE"));
        await Run.StreamAsync(api, Opus, context, new StreamOptions { ApiKey = "k", ThinkingLevel = ThinkingLevel.High });

        var body = handler.Bodies[1];
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.Equal("adaptive", body["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal("high", body["output_config"]!["effort"]!.GetValue<string>());
        Assert.Equal("auto", body["tool_choice"]!["type"]!.GetValue<string>());
        Assert.True(body["tools"]![0]!["eager_input_streaming"]!.GetValue<bool>());
        Assert.NotNull(body["system"]![0]!["cache_control"]);

        var messages = body["messages"]!.AsArray();
        Assert.Equal(["user", "assistant", "user"], messages.Select(m => m!["role"]!.GetValue<string>()));
        var assistant = messages[1]!["content"]!.AsArray();
        Assert.Equal("thinking", assistant[0]!["type"]!.GetValue<string>());
        Assert.Equal("EqQBCkgI", assistant[0]!["signature"]!.GetValue<string>());
        Assert.Equal("README.md", assistant[1]!["input"]!["path"]!.GetValue<string>());
        var user = messages[2]!["content"]!.AsArray();
        Assert.Equal("tool_result", user[0]!["type"]!.GetValue<string>());
        Assert.Equal("text", user[1]!["type"]!.GetValue<string>());

        var request = handler.Requests[1];
        Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());
        Assert.Equal("k", request.Headers.GetValues("x-api-key").Single());
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public async Task FastMode_SendsSpeedAndBeta_OnlyWhenModelSupportsIt()
    {
        var handler = new FakeHandler((HttpStatusCode.OK, ThinkingToolStream));
        var api = new AnthropicMessagesApi(new HttpClient(handler));
        var options = new StreamOptions { ApiKey = "k", FastMode = true };
        await Run.StreamAsync(api, Opus with { SupportsFastMode = true }, Run.Context(UserMessage.FromText("hi")), options);
        await Run.StreamAsync(api, Opus, Run.Context(UserMessage.FromText("hi")), options);

        Assert.Equal("fast", handler.Bodies[0]["speed"]!.GetValue<string>());
        Assert.Contains("fast-mode-2026-02-01", string.Join(",", handler.Requests[0].Headers.GetValues("anthropic-beta")));
        Assert.Null(handler.Bodies[1]["speed"]);
        Assert.False(handler.Requests[1].Headers.Contains("anthropic-beta"));
    }

    [Fact]
    public async Task ThinkingOff_OnAlwaysThinkingModel_SendsLowEffortInsteadOfDisabled()
    {
        var handler = new FakeHandler((HttpStatusCode.OK, ThinkingToolStream));
        await Run.StreamAsync(new AnthropicMessagesApi(new HttpClient(handler)), Opus,
            Run.Context(UserMessage.FromText("hi")), new StreamOptions { ApiKey = "k", ThinkingLevel = ThinkingLevel.Off });

        var body = handler.Bodies[0];
        Assert.Equal("adaptive", body["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal("low", body["output_config"]!["effort"]!.GetValue<string>());
    }

    [Fact]
    public async Task ThirdPartyEndpoint_UsesBearerAndOmitsAnthropicOnlyFields()
    {
        var kimi = Opus with { Id = "kimi-k3", Provider = "kimi", BaseUrl = "https://api.moonshot.ai/anthropic" };
        var handler = new FakeHandler((HttpStatusCode.OK, ThinkingToolStream));
        await Run.StreamAsync(new AnthropicMessagesApi(new HttpClient(handler)), kimi, Run.Context(UserMessage.FromText("hi")));

        var body = handler.Bodies[0];
        // Kimi 只认顶层自动缓存（规格 §12）
        Assert.Equal("ephemeral", body["cache_control"]!["type"]!.GetValue<string>());
        Assert.Null(body["tools"]![0]!["eager_input_streaming"]);
        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization!.Scheme);
        Assert.Equal("https://api.moonshot.ai/anthropic/v1/messages", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task Refusal_EndsWithError()
    {
        var stream = Sse.Of(
            ("message_start", """{"type":"message_start","message":{"id":"m","usage":{"input_tokens":5,"output_tokens":0}}}"""),
            ("message_delta", """{"type":"message_delta","delta":{"stop_reason":"refusal"},"usage":{"output_tokens":0}}"""),
            ("message_stop", """{"type":"message_stop"}"""));
        var (events, message) = await Run.StreamAsync(new AnthropicMessagesApi(new HttpClient(new FakeHandler((HttpStatusCode.OK, stream)))),
            Opus, Run.Context(UserMessage.FromText("hi")));

        Assert.Equal(StopReason.Error, message.StopReason);
        Assert.Contains("refusal", message.ErrorMessage);
        Assert.IsType<ErrorEvent>(events[^1]);
    }

    [Fact]
    public async Task MidStreamErrorEvent_EndsWithError_KeepingPartialText()
    {
        var stream = Sse.Of(
            ("message_start", """{"type":"message_start","message":{"id":"m","usage":{"input_tokens":5,"output_tokens":1}}}"""),
            ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hel"}}"""),
            ("error", """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}"""));
        var (_, message) = await Run.StreamAsync(new AnthropicMessagesApi(new HttpClient(new FakeHandler((HttpStatusCode.OK, stream)))),
            Opus, Run.Context(UserMessage.FromText("hi")));

        Assert.Equal(StopReason.Error, message.StopReason);
        Assert.Contains("Overloaded", message.ErrorMessage);
        Assert.Equal("Hel", Assert.IsType<TextContent>(message.Content[0]).Text);
    }

    [Fact]
    public async Task HttpError_IsEncodedInMessage_NotThrown()
    {
        var handler = new FakeHandler((HttpStatusCode.BadRequest,
            """{"type":"error","error":{"type":"invalid_request_error","message":"tool_choice: type \"tool\" and \"any\" are not supported for this model."}}"""));
        var (events, message) = await Run.StreamAsync(new AnthropicMessagesApi(new HttpClient(handler)), Opus, Run.Context(UserMessage.FromText("hi")));

        Assert.Single(events);
        Assert.Equal(StopReason.Error, message.StopReason);
        Assert.StartsWith("400 tool_choice", message.ErrorMessage);
        Assert.Single(handler.Requests); // 400 不重试
    }
}
