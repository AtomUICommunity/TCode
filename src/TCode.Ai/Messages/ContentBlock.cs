using System.Text.Json;
using System.Text.Json.Serialization;

namespace TCode.Ai.Messages;

/// <summary>
/// 消息内容块。厂商相关的不透明数据（思考签名、加密推理、消息 phase 等）
/// 原样保存在 Signature / ProviderState 中，只在同一 (provider, api, model) 下回放。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextContent), "text")]
[JsonDerivedType(typeof(ThinkingContent), "thinking")]
[JsonDerivedType(typeof(ImageContent), "image")]
[JsonDerivedType(typeof(ToolCallContent), "toolCall")]
public abstract record ContentBlock;

public sealed record TextContent(string Text) : ContentBlock
{
    /// <summary>例如 OpenAI Responses 的消息 item id 与 phase。</summary>
    public JsonElement? ProviderState { get; init; }
}

public sealed record ThinkingContent(string Thinking) : ContentBlock
{
    /// <summary>Anthropic 的 signature；Responses 为整个 reasoning item 的 JSON。</summary>
    public string? Signature { get; init; }

    /// <summary>Anthropic redacted_thinking：Thinking 为占位文本，数据在 Signature 中。</summary>
    public bool Redacted { get; init; }
}

/// <summary>
/// 图片（base64）。用户附件带 <see cref="Path"/>：会话记录只保存路径，打开会话时再从文件读回数据；
/// 数据为空（文件已删除，或尚未读回）的图片不会发给模型。
/// </summary>
public sealed record ImageContent(string Data, string MimeType) : ContentBlock
{
    /// <summary>附件文件的绝对路径（用户粘贴或选择的图片）；工具产生的图片为 null。</summary>
    public string? Path { get; init; }

    public bool HasData => Data.Length > 0;
}

public sealed record ToolCallContent(string Id, string Name, string ArgumentsJson) : ContentBlock
{
    /// <summary>最终参数不是合法 JSON 对象时的错误描述（此时不执行工具）。</summary>
    public string? ArgumentsError { get; init; }

    public JsonElement? ProviderState { get; init; }
}
