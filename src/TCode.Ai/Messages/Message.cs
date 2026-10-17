using System.Text.Json.Serialization;

namespace TCode.Ai.Messages;

/// <summary>
/// 与协议无关的规范消息模型。会话记录（append-only）中保存的就是这些消息。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "role")]
[JsonDerivedType(typeof(SystemMessage), "system")]
[JsonDerivedType(typeof(UserMessage), "user")]
[JsonDerivedType(typeof(AssistantMessage), "assistant")]
[JsonDerivedType(typeof(ToolResultMessage), "toolResult")]
public abstract record Message
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 系统消息。首条为会话的系统提示；之后追加的系统消息表示提示或工具集的增量变化，
/// 以保持历史只追加（Anthropic preserved thinking 与提示缓存都依赖这一点）。
/// </summary>
public sealed record SystemMessage(string Text) : Message;

public sealed record UserMessage(IReadOnlyList<ContentBlock> Content) : Message
{
    public static UserMessage FromText(string text) => new([new TextContent(text)]);

    /// <summary>
    /// 非用户输入的来源：<c>task-notification</c>（后台任务完成）、<c>session-message</c>（其他会话 / 子 Agent 的消息）。
    /// 对模型而言仍是 user 角色的消息；UI 据此用特殊样式显示。null 表示用户输入。
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    /// 用户在输入框里看到的内容（文字与 tag，docs/design/composer.md §12）。<see cref="Content"/> 是实际发给模型的版本
    /// （command 已展开）；没有 tag 时为 null，直接显示 Content。
    /// </summary>
    public IReadOnlyList<MessageSegment>? Display { get; init; }

    /// <summary>
    /// 发给模型的版本：模型不支持图片时去掉全部图片，否则从文件读回图片数据，并去掉读不到的图片
    /// （提示词里已有图片路径，Agent 仍可自行读取）。没有图片时返回自身。
    /// </summary>
    public UserMessage ForModel(bool supportsImages) =>
        Content.Any(c => c is ImageContent) ? this with { Content = ImageContents.ForModel(Content, supportsImages) } : this;
}

/// <summary>用户消息显示用的一段：文字（<see cref="Text"/>）或 tag（<see cref="Kind"/> 为 file / folder / command / skill / mcpResource / mcpPrompt / image）。</summary>
public sealed record MessageSegment(string Kind, string? Text = null, string? Value = null, string? Label = null);

public sealed record AssistantMessage(IReadOnlyList<ContentBlock> Content) : Message
{
    public required string Api { get; init; }
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public Usage Usage { get; init; } = Usage.Empty;
    public StopReason StopReason { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ResponseId { get; init; }

    /// <summary>因上下文超窗而失败或被截断（适配器识别，见 <see cref="Messages.ContextOverflow"/>）；Agent 据此压缩后重试。</summary>
    public bool ContextOverflow { get; init; }
}

public sealed record ToolResultMessage(string ToolCallId, string ToolName, IReadOnlyList<ContentBlock> Content) : Message
{
    public bool IsError { get; init; }

    /// <summary>工具给 UI 的结构化数据（JSON 文本，例如 diff）；随会话持久化，适配器不会发给模型。</summary>
    public string? Details { get; init; }

    /// <summary>发给模型的版本：从文件读回图片数据，去掉读不到的图片。没有图片时返回自身。</summary>
    public ToolResultMessage ForModel() =>
        Content.Any(c => c is ImageContent) ? this with { Content = ImageContents.ForModel(Content, true) } : this;
}

internal static class ImageContents
{
    public static ContentBlock[] ForModel(IReadOnlyList<ContentBlock> content, bool supportsImages) => content
        .Select(c => c is ImageContent image ? supportsImages ? image.WithData() : null : c)
        .Where(c => c is not null && c is not ImageContent { HasData: false })
        .ToArray()!;
}

[JsonConverter(typeof(JsonStringEnumConverter<StopReason>))]
public enum StopReason
{
    Pending,
    Stop,
    Length,
    ToolUse,
    Error,
    Aborted,
}

/// <summary>
/// 归一化的用量：Input 不含缓存命中/写入（OpenAI 的 prompt_tokens 需要扣除缓存部分）。
/// </summary>
public sealed record Usage(long Input, long Output, long CacheRead, long CacheWrite, long? Reasoning = null)
{
    public static Usage Empty { get; } = new(0, 0, 0, 0);
    public long Total => Input + Output + CacheRead + CacheWrite;
}
