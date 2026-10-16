using System.Text.Json.Serialization;

namespace TCode.Rpc;

/// <summary>Host 推送的会话事件。Seq 单调递增，便于检测丢失与去重。</summary>
public sealed record SessionEventNotification(string SessionId, long Seq, SessionEvent Event);

/// <summary>
/// 跨进程的会话事件：只携带增量（不带"截至目前的完整消息"），保持流量与消息长度线性相关。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(AgentStarted), "agentStarted")]
[JsonDerivedType(typeof(AgentEnded), "agentEnded")]
[JsonDerivedType(typeof(MessageStarted), "messageStarted")]
[JsonDerivedType(typeof(TextDelta), "textDelta")]
[JsonDerivedType(typeof(ThinkingDelta), "thinkingDelta")]
[JsonDerivedType(typeof(ToolCallStarted), "toolCallStarted")]
[JsonDerivedType(typeof(ToolCallDelta), "toolCallDelta")]
[JsonDerivedType(typeof(ToolCallEnded), "toolCallEnded")]
[JsonDerivedType(typeof(MessageEnded), "messageEnded")]
[JsonDerivedType(typeof(ToolExecutionStarted), "toolExecutionStarted")]
[JsonDerivedType(typeof(ToolExecutionUpdated), "toolExecutionUpdated")]
[JsonDerivedType(typeof(ToolExecutionEnded), "toolExecutionEnded")]
public abstract record SessionEvent;

public sealed record AgentStarted : SessionEvent;

public sealed record AgentEnded : SessionEvent;

/// <summary>Role：user / assistant / toolResult。用户消息附带文本与附图路径；助手消息内容随后以增量到达。</summary>
/// <param name="Source">非用户输入的来源：task-notification / session-message；null 表示用户输入。</param>
public sealed record MessageStarted(string MessageId, string Role, string? Text, IReadOnlyList<string>? Images = null, string? Source = null) : SessionEvent;

public sealed record TextDelta(string MessageId, int ContentIndex, string Delta) : SessionEvent;

public sealed record ThinkingDelta(string MessageId, int ContentIndex, string Delta) : SessionEvent;

public sealed record ToolCallStarted(string MessageId, int ContentIndex, string ToolCallId, string ToolName) : SessionEvent;

public sealed record ToolCallDelta(string MessageId, int ContentIndex, string JsonFragment) : SessionEvent;

public sealed record ToolCallEnded(string MessageId, int ContentIndex, string ToolCallId, string ArgumentsJson) : SessionEvent;

public sealed record MessageEnded(string MessageId, string Role, string StopReason, string? ErrorMessage, UsageDto? Usage) : SessionEvent;

public sealed record ToolExecutionStarted(string ToolCallId, string ToolName, string ArgumentsJson) : SessionEvent;

/// <summary>工具执行中的进度（例如命令输出、子 Agent 动态）。Text 为截至目前的输出；DetailsJson 为工具的结构化数据。</summary>
public sealed record ToolExecutionUpdated(string ToolCallId, string ToolName, string Text, string? DetailsJson = null) : SessionEvent;

/// <param name="DetailsJson">工具给 UI 的结构化数据（例如 diff），格式由工具约定。</param>
public sealed record ToolExecutionEnded(string ToolCallId, string ToolName, bool IsError, string ResultText, double DurationMs,
    string? DetailsJson = null) : SessionEvent;
