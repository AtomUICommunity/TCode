using TCode.Ai.Messages;
using TCode.Ai.Streaming;

namespace TCode.Agent;

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
