using System.Text;
using TCode.Ai.Messages;
using TCode.Ai.Models;

namespace TCode.Ai.Streaming;

/// <summary>
/// 适配器内部使用的可变构建器：累积各内容块并同步推送事件。仅由单个生产者访问。
/// </summary>
public sealed class AssistantMessageBuilder
{
    private readonly ModelInfo _model;
    private readonly AssistantMessageStream _stream;
    private readonly List<BlockState> _blocks = [];

    public AssistantMessageBuilder(ModelInfo model, AssistantMessageStream stream)
    {
        _model = model;
        _stream = stream;
    }

    public Usage Usage { get; set; } = Usage.Empty;
    public StopReason StopReason { get; set; } = StopReason.Pending;
    public string? ResponseId { get; set; }
    /// <summary>上下文超窗（例如 Anthropic 的 model_context_window_exceeded）；失败时还会按错误文案识别。</summary>
    public bool ContextOverflow { get; set; }

    public void Start() => _stream.Push(new StartEvent());

    public int BeginText()
    {
        var index = Add(new BlockState(BlockKind.Text));
        _stream.Push(new TextStartEvent(index));
        return index;
    }

    public int BeginThinking()
    {
        var index = Add(new BlockState(BlockKind.Thinking));
        _stream.Push(new ThinkingStartEvent(index));
        return index;
    }

    public int BeginToolCall(string id, string name)
    {
        var index = Add(new BlockState(BlockKind.ToolCall) { ToolCallId = id, ToolName = name });
        _stream.Push(new ToolCallStartEvent(index, id, name));
        return index;
    }

    public void AppendDelta(int index, string delta)
    {
        var block = _blocks[index];
        block.Buffer.Append(delta);
        block.LastDeltaAt = System.Diagnostics.Stopwatch.GetTimestamp();
        _stream.Push(block.Kind switch
        {
            BlockKind.Text => new TextDeltaEvent(index, delta),
            BlockKind.Thinking => new ThinkingDeltaEvent(index, delta),
            _ => new ToolCallDeltaEvent(index, delta),
        });
    }

    public void SetSignature(int index, string signature) => _blocks[index].Signature = signature;

    /// <summary>Anthropic 的 signature 以多个 signature_delta 到达，逐段累积。</summary>
    public void AppendSignature(int index, string fragment) =>
        _blocks[index].Signature = (_blocks[index].Signature ?? "") + fragment;

    /// <summary>标记为 redacted 思考：数据保存在 Signature 中，界面只显示占位文本。</summary>
    public void SetRedacted(int index, string data)
    {
        var block = _blocks[index];
        block.Redacted = true;
        block.Signature = data;
        block.Buffer.Clear().Append("[Reasoning redacted]");
    }

    /// <summary>厂商原生状态（Responses 的消息 id/phase、function_call item id 等），原样保存以便回放。</summary>
    public void SetProviderState(int index, System.Text.Json.JsonElement state) => _blocks[index].ProviderState = state;

    /// <summary>当前缓冲的原始文本（例如尚未结束的工具参数）。</summary>
    public string GetBuffer(int index) => _blocks[index].Buffer.ToString();

    public int BlockCount => _blocks.Count;

    public bool HasToolCalls => _blocks.Any(b => b.Kind == BlockKind.ToolCall);

    public void EndBlock(int index)
    {
        var block = _blocks[index];
        if (block.Ended)
        {
            return;
        }
        block.Ended = true;
        var content = block.ToContent();
        _stream.Push(content switch
        {
            TextContent text => new TextEndEvent(index, text),
            ThinkingContent thinking => new ThinkingEndEvent(index, thinking),
            ToolCallContent toolCall => new ToolCallEndEvent(index, toolCall),
            _ => throw new InvalidOperationException(),
        });
    }

    public void Finish()
    {
        for (var i = 0; i < _blocks.Count; i++)
        {
            EndBlock(i);
        }
        if (StopReason == StopReason.Pending)
        {
            StopReason = _blocks.Any(b => b.Kind == BlockKind.ToolCall) ? StopReason.ToolUse : StopReason.Stop;
        }
        _stream.Push(new DoneEvent(Build(null)));
    }

    public void Fail(Exception exception, bool aborted)
    {
        StopReason = aborted ? StopReason.Aborted : StopReason.Error;
        ContextOverflow |= !aborted && Messages.ContextOverflow.IsOverflowMessage(exception.Message);
        _stream.Push(new ErrorEvent(Build(aborted ? "Request aborted" : exception.Message)));
    }

    private AssistantMessage Build(string? error) => new(_blocks.Select(b => b.ToContent()).ToArray())
    {
        Api = _model.Api,
        Provider = _model.Provider,
        Model = _model.Id,
        Usage = Usage,
        StopReason = StopReason,
        ErrorMessage = error,
        ResponseId = ResponseId,
        ContextOverflow = ContextOverflow,
    };

    private int Add(BlockState block)
    {
        _blocks.Add(block);
        return _blocks.Count - 1;
    }

    private enum BlockKind
    {
        Text,
        Thinking,
        ToolCall,
    }

    private sealed class BlockState(BlockKind kind)
    {
        public BlockKind Kind { get; } = kind;
        public StringBuilder Buffer { get; } = new();
        public string? Signature { get; set; }
        public bool Redacted { get; set; }
        public System.Text.Json.JsonElement? ProviderState { get; set; }
        public string? ToolCallId { get; init; }
        public string? ToolName { get; init; }
        public bool Ended { get; set; }
        public long StartedAt { get; } = System.Diagnostics.Stopwatch.GetTimestamp();
        public long LastDeltaAt { get; set; }

        /// <summary>思考耗时：开始到最后一段增量（没有增量时为空）。</summary>
        public long? DurationMs => LastDeltaAt == 0 ? null : (long)System.Diagnostics.Stopwatch.GetElapsedTime(StartedAt, LastDeltaAt).TotalMilliseconds;

        public ContentBlock ToContent() => Kind switch
        {
            BlockKind.Text => new TextContent(Buffer.ToString()) { ProviderState = ProviderState },
            BlockKind.Thinking => new ThinkingContent(Buffer.ToString()) { Signature = Signature, Redacted = Redacted, DurationMs = DurationMs },
            _ => ToToolCall(),
        };

        private ToolCallContent ToToolCall()
        {
            var json = Buffer.Length == 0 ? "{}" : Buffer.ToString();
            string? error = null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                {
                    error = "Tool arguments must be a JSON object.";
                }
            }
            catch (System.Text.Json.JsonException ex)
            {
                error = ex.Message;
            }
            return new ToolCallContent(ToolCallId!, ToolName!, error is null ? json : "{}")
            {
                ArgumentsError = error,
                ProviderState = ProviderState,
            };
        }
    }
}
