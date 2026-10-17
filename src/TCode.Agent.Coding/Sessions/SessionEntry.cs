using System.Text.Json.Serialization;
using TCode.Ai.Messages;
using TCode.Agent.Coding.Prompt;
using TCode.Ai.Models;

namespace TCode.Agent.Coding.Sessions;

/// <summary>
/// 会话文件中的一行（JSONL）。除首行 header 外，每条记录都有 id / parentId，
/// 形成一棵只追加的树：分支 = 从某个旧节点继续追加，压缩 = 追加一条摘要记录。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SessionHeader), "session")]
[JsonDerivedType(typeof(MessageEntry), "message")]
[JsonDerivedType(typeof(ModelChangeEntry), "modelChange")]
[JsonDerivedType(typeof(OptionsChangeEntry), "optionsChange")]
[JsonDerivedType(typeof(RuntimeSessionEntry), "runtimeSession")]
[JsonDerivedType(typeof(SessionMetaEntry), "meta")]
[JsonDerivedType(typeof(SystemPromptEntry), "systemPrompt")]
[JsonDerivedType(typeof(TasksEntry), "tasks")]
public abstract record SessionEntry
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record SessionHeader(string SessionId, string Cwd, int Version) : SessionEntry;

/// <summary>
/// 会话元数据（不属于对话树，最后一条生效）：用户重命名的标题、是否置顶。字段为 null 表示不变；
/// 标题为空串表示清除自定义标题（回到第一条用户消息）。
/// </summary>
public sealed record SessionMetaEntry(string? Title, bool? Pinned) : SessionEntry;

public abstract record TreeEntry(string Id, string? ParentId) : SessionEntry;

public sealed record MessageEntry(string Id, string? ParentId, Message Message) : TreeEntry(Id, ParentId);

public sealed record ModelChangeEntry(string Id, string? ParentId, string Provider, string ModelId) : TreeEntry(Id, ParentId);

/// <summary>会话参数变化；为 null 的字段保持不变。</summary>
public sealed record OptionsChangeEntry(string Id, string? ParentId, ThinkingLevel? ThinkingLevel, bool? FastMode, int? ContextWindow,
    TCode.Agent.PermissionMode? PermissionMode = null, string? Role = null)
    : TreeEntry(Id, ParentId);

/// <summary>外部运行时的原生会话 id（Claude session_id / Codex threadId / ACP sessionId），用于恢复。</summary>
public sealed record RuntimeSessionEntry(string Id, string? ParentId, string Runtime, string NativeSessionId) : TreeEntry(Id, ParentId);

/// <summary>
/// 内置 Agent 的系统提示快照（按节）。分支上第一条是发给模型的开头系统提示，
/// 之后每条对应一次中途变化（同时追加一条 SystemMessage 补丁）；恢复会话时以此为准，不从磁盘重建。
/// </summary>
public sealed record SystemPromptEntry(string Id, string? ParentId, IReadOnlyList<PromptSection> Sections) : TreeEntry(Id, ParentId);

/// <summary>任务清单的全量快照（最后一条生效）。</summary>
public sealed record TasksEntry(string Id, string? ParentId, IReadOnlyList<TCode.Agent.Coding.Tools.TaskItem> Tasks) : TreeEntry(Id, ParentId);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(SessionEntry))]
internal sealed partial class SessionJsonContext : JsonSerializerContext;
