using System.Text.Json;
using System.Text.Json.Serialization;

namespace TKCode.Ai.Messages;

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

    /// <summary>思考耗时（毫秒）：从思考块开始到最后一段思考增量；只用于界面显示，不发给模型。</summary>
    public long? DurationMs { get; init; }
}

/// <summary>
/// 图片（base64）。带 <see cref="Path"/> 的图片数据不常驻内存：会话记录与历史只保存路径，
/// 每次发给模型前用 <see cref="WithData"/> 从文件读回；读不到（文件已删除）的图片不会发给模型。
/// </summary>
public sealed record ImageContent(string Data, string MimeType) : ContentBlock
{
    /// <summary>图片文件的绝对路径：用户附件，或已转存到附件目录的工具图片。</summary>
    public string? Path { get; init; }

    public bool HasData => Data.Length > 0;

    /// <summary>需要数据时从 <see cref="Path"/> 读回；已有数据、没有路径或文件读不到时返回自身。</summary>
    public ImageContent WithData()
    {
        if (HasData || Path is null)
        {
            return this;
        }
        try
        {
            return File.Exists(Path) ? this with { Data = Convert.ToBase64String(File.ReadAllBytes(Path)) } : this;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return this;
        }
    }
}

public sealed record ToolCallContent(string Id, string Name, string ArgumentsJson) : ContentBlock
{
    /// <summary>最终参数不是合法 JSON 对象时的错误描述（此时不执行工具）。</summary>
    public string? ArgumentsError { get; init; }

    public JsonElement? ProviderState { get; init; }
}
