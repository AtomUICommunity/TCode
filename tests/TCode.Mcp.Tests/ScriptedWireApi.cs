using TCode.Ai;
using TCode.Ai.Messages;
using TCode.Ai.Models;
using TCode.Ai.Streaming;

namespace TCode.Mcp.Tests;

/// <summary>按脚本逐轮回复的模拟协议：每轮要么是一组工具调用，要么是一段文本；记录每次收到的上下文。</summary>
internal sealed class ScriptedWireApi : IWireApi
{
    private readonly Queue<Turn> _turns;

    public ScriptedWireApi(params Turn[] turns) => _turns = new Queue<Turn>(turns);

    public static ModelInfo Model { get; } = new() { Id = "scripted", Name = "Scripted", Provider = "fake", Api = "scripted" };

    public string Api => "scripted";

    public List<LlmContext> Contexts { get; } = [];

    public AssistantMessageStream Stream(ModelInfo model, LlmContext context, StreamOptions options, CancellationToken cancellationToken)
    {
        lock (Contexts)
        {
            Contexts.Add(context);
        }
        var turn = _turns.Count > 0 ? _turns.Dequeue() : Turn.Reply("done");
        var stream = new AssistantMessageStream();
        var builder = new AssistantMessageBuilder(model, stream);
        builder.Start();
        if (turn.Calls is { } calls)
        {
            foreach (var (name, args) in calls)
            {
                var index = builder.BeginToolCall($"call_{Guid.NewGuid():N}"[..16], name);
                builder.AppendDelta(index, args);
                builder.EndBlock(index);
            }
        }
        else
        {
            var index = builder.BeginText();
            builder.AppendDelta(index, turn.Text ?? "");
            builder.EndBlock(index);
        }
        builder.Finish();
        return stream;
    }

    public sealed record Turn(string? Text, IReadOnlyList<(string Name, string Args)>? Calls)
    {
        public static Turn Reply(string text) => new(text, null);
        public static Turn Tools(params (string Name, string Args)[] calls) => new(null, calls);
    }
}
