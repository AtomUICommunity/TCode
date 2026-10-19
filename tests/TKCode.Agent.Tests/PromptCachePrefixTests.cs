using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TKCode.Agent.Coding;
using TKCode.Ai;
using TKCode.Ai.Apis;
using TKCode.Ai.Models;
using Xunit;

namespace TKCode.Agent.Tests;

/// <summary>
/// 缓存命中的前提：真实 Agent 循环 + 真实适配器跑多轮，上一次请求（去掉移动的 cache_control 之后）必须逐字是下一次的前缀，
/// 工具与系统提示不变，缓存键稳定，全局 TTL 传到请求体。
/// </summary>
public sealed class PromptCachePrefixTests
{
    private sealed class Handler(Func<int, string> respond) : HttpMessageHandler
    {
        public List<JsonNode> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respond(Bodies.Count - 1), Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    private static string Sse(params (string? Event, string Data)[] events) =>
        string.Concat(events.Select(e => (e.Event is null ? "" : $"event: {e.Event}\n") + $"data: {e.Data}\n\n"));

    // 第一次回复调用 ls，之后都是文本回复
    private static string Messages(int i) => i == 0
        ? Sse(
            ("message_start", """{"type":"message_start","message":{"id":"m1","usage":{"input_tokens":5,"output_tokens":1}}}"""),
            ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"look"}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"SIG"}}"""),
            ("content_block_stop", """{"type":"content_block_stop","index":0}"""),
            ("content_block_start", """{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"ls","input":{}}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"path\": \".\"}"}}"""),
            ("content_block_stop", """{"type":"content_block_stop","index":1}"""),
            ("message_delta", """{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":9}}"""),
            ("message_stop", """{"type":"message_stop"}"""))
        : Sse(
            ("message_start", """{"type":"message_start","message":{"id":"m2","usage":{"input_tokens":5,"output_tokens":1}}}"""),
            ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"done"}}"""),
            ("content_block_stop", """{"type":"content_block_stop","index":0}"""),
            ("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":9}}"""),
            ("message_stop", """{"type":"message_stop"}"""));

    private static string Chat(int i) => i == 0
        ? Sse(
            (null, """{"id":"c1","choices":[{"index":0,"delta":{"role":"assistant","content":"look"},"finish_reason":null}]}"""),
            (null, """{"id":"c1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"ls","arguments":"{\"path\":\".\"}"}}]}}]}"""),
            (null, """{"id":"c1","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}"""),
            (null, "[DONE]"))
        : Sse(
            (null, """{"id":"c2","choices":[{"index":0,"delta":{"role":"assistant","content":"done"},"finish_reason":null}]}"""),
            (null, """{"id":"c2","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}"""),
            (null, "[DONE]"));

    private static string Responses(int i) => i == 0
        ? Sse(
            ("response.output_item.added", """{"type":"response.output_item.added","output_index":0,"item":{"type":"reasoning","id":"rs_1","summary":[]}}"""),
            ("response.output_item.done", """{"type":"response.output_item.done","output_index":0,"item":{"type":"reasoning","id":"rs_1","summary":[],"encrypted_content":"ENC","status":"completed"}}"""),
            ("response.output_item.added", """{"type":"response.output_item.added","output_index":1,"item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"ls","arguments":""}}"""),
            ("response.function_call_arguments.delta", """{"type":"response.function_call_arguments.delta","item_id":"fc_1","output_index":1,"delta":"{\"path\":\".\"}"}"""),
            ("response.output_item.done", """{"type":"response.output_item.done","output_index":1,"item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"ls","arguments":"{\"path\":\".\"}","status":"completed"}}"""),
            ("response.completed", """{"type":"response.completed","response":{"id":"r1","status":"completed","usage":{"input_tokens":10,"output_tokens":5}}}"""))
        : Sse(
            ("response.output_item.added", """{"type":"response.output_item.added","output_index":0,"item":{"type":"message","id":"msg_2","role":"assistant","content":[]}}"""),
            ("response.output_text.delta", """{"type":"response.output_text.delta","item_id":"msg_2","output_index":0,"content_index":0,"delta":"done"}"""),
            ("response.output_item.done", """{"type":"response.output_item.done","output_index":0,"item":{"type":"message","id":"msg_2","role":"assistant","status":"completed","content":[{"type":"output_text","text":"done","annotations":[]}]}}"""),
            ("response.completed", """{"type":"response.completed","response":{"id":"r2","status":"completed","usage":{"input_tokens":10,"output_tokens":5}}}"""));

    /// <summary>去掉 cache_control：移动的断点不是失效原因（被标过的位置仍可命中）。</summary>
    private static JsonNode Strip(JsonNode node)
    {
        var clone = node.DeepClone();
        void Walk(JsonNode? n)
        {
            switch (n)
            {
                case JsonObject o:
                    o.Remove("cache_control");
                    foreach (var (_, value) in o)
                    {
                        Walk(value);
                    }
                    break;
                case JsonArray a:
                    foreach (var item in a)
                    {
                        Walk(item);
                    }
                    break;
            }
        }
        Walk(clone);
        return clone;
    }

    /// <summary>两次提问：第一次带一次工具调用（2 个请求），第二次直接回复（1 个请求）。</summary>
    private static async Task RunTwoPromptsAsync(IWireApi api, ModelInfo model)
    {
        var root = Directory.CreateTempSubdirectory("tkcode-cache-").FullName;
        await using var session = AgentSession.Create(new WireApiRegistry([api]), model, root, Path.Combine(root, "sessions"), _ => "k");
        session.Prompt("first");
        await session.Agent.WaitForIdleAsync();
        session.Prompt("second");
        await session.Agent.WaitForIdleAsync();
    }

    public static TheoryData<string> Protocols => new() { "messages", "chat", "responses" };

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task PreviousRequest_IsBytePrefixOfTheNext(string protocol)
    {
        var handler = new Handler(protocol switch { "messages" => Messages, "chat" => Chat, _ => Responses });
        var http = new HttpClient(handler);
        IWireApi api = protocol switch
        {
            "messages" => new AnthropicMessagesApi(http),
            "chat" => new OpenAIChatCompletionsApi(http),
            _ => new OpenAIResponsesApi(http),
        };
        // 第三方 Base URL：中转站上也要能命中
        var model = new ModelInfo
        {
            Id = protocol == "messages" ? "claude-opus-5-5" : "gpt-5.6-sol", Name = "m", Provider = "relay", Api = api.Api, Reasoning = true,
            BaseUrl = protocol == "messages" ? "https://relay.example.com" : "https://relay.example.com/v1",
        };
        await RunTwoPromptsAsync(api, model);

        var bodies = handler.Bodies;
        Assert.Equal(3, bodies.Count);
        var list = protocol == "responses" ? "input" : "messages";
        for (var i = 1; i < bodies.Count; i++)
        {
            var prev = Strip(bodies[i - 1]);
            var cur = Strip(bodies[i]);
            Assert.Equal(prev["tools"]!.ToJsonString(), cur["tools"]!.ToJsonString());
            Assert.Equal(prev["system"]?.ToJsonString(), cur["system"]?.ToJsonString());
            Assert.Equal(prev["prompt_cache_key"]?.ToJsonString(), cur["prompt_cache_key"]?.ToJsonString());
            Assert.Equal(prev["metadata"]?.ToJsonString(), cur["metadata"]?.ToJsonString());
            var before = prev[list]!.AsArray();
            var after = cur[list]!.AsArray();
            Assert.True(after.Count > before.Count);
            for (var j = 0; j < before.Count; j++)
            {
                Assert.Equal(before[j]!.ToJsonString(), after[j]!.ToJsonString());
            }
        }
        if (protocol == "messages")
        {
            Assert.All(bodies, b => Assert.NotNull(b["system"]![0]!["cache_control"]));
            Assert.All(bodies, b => Assert.NotNull(b["metadata"]!["user_id"]));
        }
        else
        {
            Assert.All(bodies, b => Assert.StartsWith("tkcode:", b["prompt_cache_key"]!.GetValue<string>()));
        }
    }

    [Fact]
    public async Task GlobalCacheTtl_IsReadOnEveryRequest()
    {
        var handler = new Handler(Messages);
        var ttl = PromptCacheTtl.FiveMinutes;
        var model = new ModelInfo
        {
            Id = "claude-opus-5-5", Name = "m", Provider = "anthropic", Api = KnownApis.AnthropicMessages, Reasoning = true,
        };
        var root = Directory.CreateTempSubdirectory("tkcode-cache-").FullName;
        await using var session = AgentSession.Create(new WireApiRegistry([new AnthropicMessagesApi(new HttpClient(handler))]), model, root,
            Path.Combine(root, "sessions"), _ => "k", new BuiltinOptions { CacheTtl = () => ttl });
        session.Prompt("first");
        await session.Agent.WaitForIdleAsync();
        // 设置页改成 1 小时：进行中的会话下一次请求就生效
        ttl = PromptCacheTtl.OneHour;
        session.Prompt("second");
        await session.Agent.WaitForIdleAsync();

        Assert.Null(handler.Bodies[0]["system"]![0]!["cache_control"]!["ttl"]);
        Assert.Equal("1h", handler.Bodies[^1]["system"]![0]!["cache_control"]!["ttl"]!.GetValue<string>());
    }
}
