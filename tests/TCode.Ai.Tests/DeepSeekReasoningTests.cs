using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using TCode.Ai.Apis;
using TCode.Ai.Messages;
using TCode.Ai.Models;
using Xunit;

namespace TCode.Ai.Tests;

/// <summary>
/// DeepSeek Chat 协议的思考字段（reasoning_content）规则，依据 https://api-docs.deepseek.com/guides/thinking_mode：
/// 带 tools 时，之前所有轮次（包括没有调用工具的轮次）的 reasoning_content 都必须在后续请求中回传，缺失返回 400。
/// </summary>
public sealed class DeepSeekReasoningTests
{
    private static readonly ModelInfo Pro = new()
    {
        Id = "deepseek-v4-pro", Name = "DeepSeek V4 Pro", Provider = "deepseek", Api = KnownApis.OpenAIChatCompletions,
        BaseUrl = "https://api.deepseek.com", Reasoning = true,
    };

    private static readonly ModelInfo Flash = Pro with { Id = "deepseek-flash", Name = "DeepSeek Flash" };

    private static string Chunks(params string[] json) => string.Concat(json.Select(j => $"data: {j}\n\n"));

    // DeepSeek 流：reasoning_content 先于 content，再是工具调用；usage 附在最后一个内容 chunk 上。
    private static string ToolTurn(string reasoning, string callId) => Chunks(
        $$$"""{"id":"r1","choices":[{"index":0,"delta":{"role":"assistant","content":null,"reasoning_content":"{{{reasoning}}}"}}]}""",
        $$$"""{"id":"r1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"{{{callId}}}","type":"function","function":{"name":"read_file","arguments":"{\"path\":\"a\"}"}}]}}]}""",
        """{"id":"r1","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":10,"completion_tokens":5,"prompt_cache_hit_tokens":4,"prompt_cache_miss_tokens":6}}""",
        "[DONE]");

    private static string AnswerTurn(string reasoning, string answer) => Chunks(
        $$$"""{"id":"r2","choices":[{"index":0,"delta":{"reasoning_content":"{{{reasoning}}}"}}]}""",
        $$$"""{"id":"r2","choices":[{"index":0,"delta":{"content":"{{{answer}}}"}}]}""",
        """{"id":"r2","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
        "[DONE]");

    [Fact]
    public async Task ToolLoop_EveryAssistantTurnEchoesItsReasoningContent_IncludingTurnsWithoutToolCalls()
    {
        var handler = new FakeHandler(
            (HttpStatusCode.OK, ToolTurn("先读文件", "call_00_a")),
            (HttpStatusCode.OK, AnswerTurn("读完了，可以回答", "答案一")),
            (HttpStatusCode.OK, AnswerTurn("x", "y")));
        var api = new OpenAIChatCompletionsApi(new HttpClient(handler));

        var history = new List<Message> { UserMessage.FromText("问题一") };
        var (_, turn1) = await Run.StreamAsync(api, Pro, Run.Context([.. history]));
        history.Add(turn1);
        history.Add(new ToolResultMessage("call_00_a", "read_file", [new TextContent("文件内容")]));
        var (_, turn2) = await Run.StreamAsync(api, Pro, Run.Context([.. history]));
        history.Add(turn2);
        history.Add(UserMessage.FromText("问题二"));
        await Run.StreamAsync(api, Pro, Run.Context([.. history]));

        var assistants = Assistants(handler.Bodies[2]);
        Assert.Equal(2, assistants.Count);
        Assert.Equal("先读文件", assistants[0]["reasoning_content"]!.GetValue<string>());
        Assert.NotNull(assistants[0]["tool_calls"]);
        // 没有调用工具的那一轮也必须回传
        Assert.Equal("读完了，可以回答", assistants[1]["reasoning_content"]!.GetValue<string>());
        Assert.Equal("答案一", assistants[1]["content"]!.GetValue<string>());
        // 思考内容不能混进 content
        Assert.DoesNotContain("先读文件", assistants[0]["content"]?.ToJsonString() ?? "");
    }

    [Fact]
    public async Task SwitchingBetweenDeepSeekModels_KeepsReasoningContent()
    {
        var handler = new FakeHandler((HttpStatusCode.OK, ToolTurn("flash 的推理", "call_1")), (HttpStatusCode.OK, AnswerTurn("a", "b")));
        var api = new OpenAIChatCompletionsApi(new HttpClient(handler));
        var (_, fromFlash) = await Run.StreamAsync(api, Flash, Run.Context(UserMessage.FromText("q")));
        Assert.Equal("deepseek-flash", fromFlash.Model);

        await Run.StreamAsync(api, Pro, Run.Context(UserMessage.FromText("q"), fromFlash,
            new ToolResultMessage("call_1", "read_file", [new TextContent("x")])));

        var assistant = Assistants(handler.Bodies[1]).Single();
        Assert.Equal("flash 的推理", assistant["reasoning_content"]!.GetValue<string>());
        Assert.Equal("call_1", assistant["tool_calls"]![0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task ReasoningSurvivesSessionSerialization()
    {
        var handler = new FakeHandler((HttpStatusCode.OK, ToolTurn("持久化的推理", "call_1")), (HttpStatusCode.OK, AnswerTurn("a", "b")));
        var api = new OpenAIChatCompletionsApi(new HttpClient(handler));
        var (_, turn) = await Run.StreamAsync(api, Pro, Run.Context(UserMessage.FromText("q")));

        // 与会话 JSONL 相同的序列化路径
        var json = JsonSerializer.Serialize<Message>(turn, AiJsonContext.Default.Message);
        var restored = (AssistantMessage)JsonSerializer.Deserialize(json, AiJsonContext.Default.Message)!;

        await Run.StreamAsync(api, Pro, Run.Context(UserMessage.FromText("q"), restored,
            new ToolResultMessage("call_1", "read_file", [new TextContent("x")])));
        Assert.Equal("持久化的推理", Assistants(handler.Bodies[1]).Single()["reasoning_content"]!.GetValue<string>());
    }

    [Fact]
    public async Task AssistantTurnsFromOtherProviders_GetEmptyReasoningContentWhenToolsArePresent()
    {
        var handler = new FakeHandler((HttpStatusCode.OK, AnswerTurn("a", "b")));
        var claude = new AssistantMessage([new ThinkingContent("claude 的思考") { Signature = "sig" }, new TextContent("claude 的回答")])
        {
            Api = KnownApis.AnthropicMessages, Provider = "anthropic", Model = "claude-opus-5-5", StopReason = StopReason.Stop,
        };
        await Run.StreamAsync(new OpenAIChatCompletionsApi(new HttpClient(handler)), Pro,
            Run.Context(UserMessage.FromText("q"), claude, UserMessage.FromText("q2")));

        var assistant = Assistants(handler.Bodies[0]).Single();
        // 其它厂商的思考不作为 reasoning_content 发送（转成正文），但字段必须存在
        Assert.Equal("", assistant["reasoning_content"]!.GetValue<string>());
        Assert.Contains("claude 的回答", assistant["content"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(ThinkingLevel.Off, "disabled", null)]
    [InlineData(ThinkingLevel.Low, "enabled", "low")]
    [InlineData(ThinkingLevel.Medium, "enabled", "high")]
    [InlineData(ThinkingLevel.XHigh, "enabled", "max")]
    [InlineData(ThinkingLevel.Max, "enabled", "max")]
    public async Task ThinkingSwitchAndEffort(ThinkingLevel level, string type, string? effort)
    {
        var handler = new FakeHandler((HttpStatusCode.OK, AnswerTurn("a", "b")));
        await Run.StreamAsync(new OpenAIChatCompletionsApi(new HttpClient(handler)), Pro, Run.Context(UserMessage.FromText("q")),
            new StreamOptions { ApiKey = "k", ThinkingLevel = level });

        var body = handler.Bodies[0];
        Assert.Equal(type, body["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal(effort, body["reasoning_effort"]?.GetValue<string>());
        Assert.NotNull(body["max_tokens"]);
        Assert.Null(body["max_completion_tokens"]);
    }

    private static List<JsonObject> Assistants(JsonNode body) =>
        body["messages"]!.AsArray().Select(m => m!.AsObject()).Where(m => m["role"]!.GetValue<string>() == "assistant").ToList();
}
