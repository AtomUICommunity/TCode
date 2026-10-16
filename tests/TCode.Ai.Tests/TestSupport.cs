using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TCode.Ai;
using TCode.Ai.Messages;
using TCode.Ai.Models;
using TCode.Ai.Streaming;

namespace TCode.Ai.Tests;

/// <summary>返回预置 SSE 文本的 HttpMessageHandler，并记录请求体与请求头。</summary>
internal sealed class FakeHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
{
    private int _next;
    public List<JsonNode> Bodies { get; } = [];
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!);
        var (status, body) = responses[Math.Min(_next++, responses.Length - 1)];
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, status == HttpStatusCode.OK ? "text/event-stream" : "application/json"),
        };
    }
}

internal static class Sse
{
    /// <summary>把 (event, json) 列表拼成 SSE 文本。</summary>
    public static string Of(params (string? Event, string Data)[] events)
    {
        var sb = new StringBuilder();
        foreach (var (name, data) in events)
        {
            if (name is not null)
            {
                sb.Append("event: ").Append(name).Append('\n');
            }
            sb.Append("data: ").Append(data).Append("\n\n");
        }
        return sb.ToString();
    }
}

internal static class Run
{
    public static async Task<(List<LlmEvent> Events, AssistantMessage Message)> StreamAsync(
        IWireApi api, ModelInfo model, LlmContext context, StreamOptions? options = null, CancellationToken ct = default)
    {
        var stream = api.Stream(model, context, options ?? new StreamOptions { ApiKey = "test-key" }, ct);
        var events = new List<LlmEvent>();
        await foreach (var e in stream)
        {
            events.Add(e);
        }
        return (events, await stream.Result);
    }

    public static LlmContext Context(params Message[] messages) =>
        new("You are a test.", messages, [new ToolDefinition("read_file", "Read a file", """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""")]);
}
