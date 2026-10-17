using TKCode.Ai.Messages;
using TKCode.Ai.Streaming;

namespace TKCode.Agent;

/// <summary>
/// Agent 生命周期事件。监听器按订阅顺序依次 await，因此 MessageEnd 是一个屏障：
/// 持久化完成之后才会进入工具执行。
/// </summary>
public abstract record AgentEvent;

public sealed record AgentStartEvent : AgentEvent;
public sealed record AgentEndEvent(IReadOnlyList<Message> NewMessages) : AgentEvent;
public sealed record TurnStartEvent : AgentEvent;
public sealed record TurnEndEvent(AssistantMessage Message, IReadOnlyList<ToolResultMessage> ToolResults) : AgentEvent;

/// <summary>消息开始：用户/工具结果消息带完整内容；助手消息在流式过程中 Message 为 null。</summary>
public sealed record MessageStartEvent(string MessageId, Message? Message) : AgentEvent;
public sealed record MessageUpdateEvent(string MessageId, LlmEvent Event) : AgentEvent;
public sealed record MessageEndEvent(string MessageId, Message Message) : AgentEvent;

public sealed record ToolExecutionStartEvent(string ToolCallId, string ToolName, string ArgumentsJson) : AgentEvent;
public sealed record ToolExecutionUpdateEvent(string ToolCallId, string ToolName, AgentToolResult Partial) : AgentEvent;
public sealed record ToolExecutionEndEvent(string ToolCallId, string ToolName, AgentToolResult Result, TimeSpan Duration) : AgentEvent;

/// <summary>开始压缩上下文（docs/design/compaction.md §5）。</summary>
public sealed record CompactionStartEvent(Compaction.CompactionTrigger Trigger, long PreTokens) : AgentEvent;

/// <summary>
/// 压缩结束。成功时 <see cref="Replaced"/> 为被替换掉的旧历史（会话据此保留显示用的完整记录），随后追加 Result.Messages；
/// 失败时 Error 为原因，历史不变。
/// </summary>
public sealed record CompactionEndEvent(
    Compaction.CompactionTrigger Trigger,
    long PreTokens,
    Compaction.CompactionResult? Result,
    string? Error,
    IReadOnlyList<Message> Replaced) : AgentEvent;

/// <summary>本会话停用了自动压缩（连续失败、反复触发等，docs/design/compaction.md §10）。</summary>
public sealed record CompactionSuspendedEvent(string Reason) : AgentEvent;
