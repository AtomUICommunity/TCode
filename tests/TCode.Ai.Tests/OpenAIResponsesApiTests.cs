using System.Net;
using System.Text.Json.Nodes;
using TCode.Ai.Apis;
using TCode.Ai.Messages;
using TCode.Ai.Models;
using TCode.Ai.Streaming;
using Xunit;

namespace TCode.Ai.Tests;

public sealed class OpenAIResponsesApiTests
{
    private static readonly ModelInfo Model = new()
    {
        Id = "gpt-5.6-sol", Name = "GPT-5.6 Sol", Provider = "openai", Api = KnownApis.OpenAIResponses, Reasoning = true,
    };

    private const string ReasoningDone = """{"type":"reasoning","id":"rs_1","summary":[{"type":"summary_text","text":"Plan"}],"encrypted_content":"gAAAA-enc","status":"completed"}""";
    private const string MessageDone = """{"type":"message","id":"msg_1","role":"assistant","status":"completed","phase":"commentary","content":[{"type":"output_text","text":"Let me look.","annotations":[]}]}""";
    private const string CallDone = """{"type":"function_call","id":"fc_1","call_id":"call_1","name":"read_file","arguments":"{\"path\":\"a.cs\"}","status":"completed"}""";

    // 规格 §5.5 的典型事件顺序
    private static readonly string ToolTurn = Sse.Of(
        ("response.created", """{"type":"response.created","sequence_number":0,"response":{"id":"resp_1","status":"in_progress","output":[],"usage":null}}"""),
        ("response.output_item.added", """{"type":"response.output_item.added","sequence_number":1,"output_index":0,"item":{"type":"reasoning","id":"rs_1","summary":[]}}"""),
        ("response.reasoning_summary_part.added", """{"type":"response.reasoning_summary_part.added","sequence_number":2,"item_id":"rs_1","output_index":0,"summary_index":0,"part":{"type":"summary_text","text":""}}"""),
        ("response.reasoning_summary_text.delta", """{"type":"response.reasoning_summary_text.delta","sequence_number":3,"item_id":"rs_1","output_index":0,"summary_index":0,"delta":"Plan"}"""),
        ("response.output_item.done", $$"""{"type":"response.output_item.done","sequence_number":4,"output_index":0,"item":{{ReasoningDone}}}"""),
        ("response.output_item.added", """{"type":"response.output_item.added","sequence_number":5,"output_index":1,"item":{"type":"message","id":"msg_1","role":"assistant","status":"in_progress","content":[],"phase":"commentary"}}"""),
        ("response.output_text.delta", """{"type":"response.output_text.delta","sequence_number":6,"item_id":"msg_1","output_index":1,"content_index":0,"delta":"Let me look.","obfuscation":"xx"}"""),
        ("response.output_item.done", $$"""{"type":"response.output_item.done","sequence_number":7,"output_index":1,"item":{{MessageDone}}}"""),
        ("response.output_item.added", """{"type":"response.output_item.added","sequence_number":8,"output_index":2,"item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"read_file","arguments":""}}"""),
        ("response.function_call_arguments.delta", """{"type":"response.function_call_arguments.delta","sequence_number":9,"item_id":"fc_1","output_index":2,"delta":"{\"path\":"}"""),
        ("response.function_call_arguments.delta", """{"type":"response.function_call_arguments.delta","sequence_number":10,"item_id":"fc_1","output_index":2,"delta":"\"a.cs\"}"}"""),
        ("response.output_item.done", $$"""{"type":"response.output_item.done","sequence_number":11,"output_index":2,"item":{{CallDone}}}"""),
        ("response.some_future_event", """{"type":"response.some_future_event","sequence_number":12}"""),
        ("response.completed", """{"type":"response.completed","sequence_number":13,"response":{"id":"resp_1","status":"completed","usage":{"input_tokens":1000,"input_tokens_details":{"cached_tokens":600,"cache_write_tokens":100},"output_tokens":50,"output_tokens_details":{"reasoning_tokens":20},"total_tokens":1050}}}"""));

    [Fact]
    public async Task Stream_MapsItemsToBlocks_AndKeepsDoneItemsForReplay()
    {
        var handler = new FakeHandler((HttpStatusCode.OK, ToolTurn));
        var (_, message) = await Run.StreamAsync(new OpenAIResponsesApi(new HttpClient(handler)), Model, Run.Context(UserMessage.FromText("go")));

        Assert.Equal(StopReason.ToolUse, message.StopReason);
        Assert.Equal("resp_1", message.ResponseId);
        var thinking = Assert.IsType<ThinkingContent>(message.Content[0]);
        Assert.Equal("Plan", thinking.Thinking);
        Assert.Contains("gAAAA-enc", thinking.Signature);
        Assert.Equal("Let me look.", Assert.IsType<TextContent>(message.Content[1]).Text);
        var call = Assert.IsType<ToolCallContent>(message.Content[2]);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("{\"path\":\"a.cs\"}", call.ArgumentsJson);
        // input_tokens 已含缓存读写，归一化后扣除
        Assert.Equal(new Usage(300, 50, 600, 100, 20), message.Usage);
    }

    [Fact]
    public async Task Request_IsStatelessAndReplaysDoneItemsVerbatim()
    {
        var handler = new FakeHandler((HttpStatusCode.OK, ToolTurn), (HttpStatusCode.OK, ToolTurn));
        var api = new OpenAIResponsesApi(new HttpClient(handler));
        var (_, first) = await Run.StreamAsync(api, Model, Run.Context(UserMessage.FromText("go")));

        var context = Run.Context(UserMessage.FromText("go"), first,
            new ToolResultMessage("call_1", "read_file", [new TextContent("class A {}")]));
        await Run.StreamAsync(api, Model, context, new StreamOptions { ApiKey = "k", SessionId = "s1" });

        var body = handler.Bodies[1];
        Assert.False(body["store"]!.GetValue<bool>());
        Assert.Null(body["previous_response_id"]);
        Assert.Equal("reasoning.encrypted_content", body["include"]![0]!.GetValue<string>());
        Assert.Equal("tcode:s1", body["prompt_cache_key"]!.GetValue<string>());
        var tool = body["tools"]![0]!;
        Assert.Equal("read_file", tool["name"]!.GetValue<string>()); // 扁平结构
        Assert.Null(tool["function"]);
        Assert.False(tool["strict"]!.GetValue<bool>());

        var input = body["input"]!.AsArray();
        Assert.Equal("developer", input[0]!["role"]!.GetValue<string>());
        Assert.Equal("user", input[1]!["role"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(ReasoningDone), input[2]));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(MessageDone), input[3]));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(CallDone), input[4]));
        Assert.Equal("function_call_output", input[5]!["type"]!.GetValue<string>());
        Assert.Equal("call_1", input[5]!["call_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task TrailingReasoningWithoutFollowingItem_IsNotReplayed()
    {
        var assistant = new AssistantMessage([new TextContent("hi"), new ThinkingContent("x") { Signature = ReasoningDone }])
        {
            Api = Model.Api, Provider = Model.Provider, Model = Model.Id, StopReason = StopReason.Length,
        };
        var handler = new FakeHandler((HttpStatusCode.OK, ToolTurn));
        await Run.StreamAsync(new OpenAIResponsesApi(new HttpClient(handler)), Model,
            Run.Context(UserMessage.FromText("a"), assistant, UserMessage.FromText("b")));

        var input = handler.Bodies[0]["input"]!.AsArray();
        Assert.DoesNotContain(input, i => i!["type"]?.GetValue<string>() == "reasoning");
    }

    [Fact]
    public async Task StreamWithoutTerminalEvent_IsError()
    {
        var truncated = Sse.Of(
            ("response.created", """{"type":"response.created","response":{"id":"r"}}"""),
            ("response.output_item.added", """{"type":"response.output_item.added","output_index":0,"item":{"type":"message","id":"m","content":[]}}"""),
            ("response.output_text.delta", """{"type":"response.output_text.delta","output_index":0,"content_index":0,"delta":"par"}"""));
        var (_, message) = await Run.StreamAsync(new OpenAIResponsesApi(new HttpClient(new FakeHandler((HttpStatusCode.OK, truncated)))),
            Model, Run.Context(UserMessage.FromText("a")));

        Assert.Equal(StopReason.Error, message.StopReason);
        Assert.Contains("terminal", message.ErrorMessage);
    }

    [Fact]
    public async Task Incomplete_MaxOutputTokens_MapsToLength()
    {
        var stream = Sse.Of(("response.incomplete",
            """{"type":"response.incomplete","response":{"id":"r","status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"usage":null}}"""));
        var (_, message) = await Run.StreamAsync(new OpenAIResponsesApi(new HttpClient(new FakeHandler((HttpStatusCode.OK, stream)))),
            Model, Run.Context(UserMessage.FromText("a")));
        Assert.Equal(StopReason.Length, message.StopReason);
    }

    [Theory]
    [InlineData("""{"type":"error","code":"server_error","message":"boom","param":null}""")]
    [InlineData("""{"error":{"message":"boom","type":"server_error"}}""")]
    public async Task ErrorEvent_BothShapes_EndWithError(string errorEvent)
    {
        var stream = Sse.Of(("error", errorEvent));
        var (_, message) = await Run.StreamAsync(new OpenAIResponsesApi(new HttpClient(new FakeHandler((HttpStatusCode.OK, stream)))),
            Model, Run.Context(UserMessage.FromText("a")));
        Assert.Equal(StopReason.Error, message.StopReason);
        Assert.Contains("boom", message.ErrorMessage);
    }
}
