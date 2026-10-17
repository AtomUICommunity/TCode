using System.Text.Json.Serialization;

namespace TCode.Ai.Models;

[JsonConverter(typeof(JsonStringEnumConverter<ThinkingLevel>))]
public enum ThinkingLevel
{
    Off,
    Minimal,
    Low,
    Medium,
    High,
    XHigh,
    Max,
}

/// <summary>模型描述：纯数据，可序列化。Api 决定使用哪个线协议适配器，Compat 描述厂商差异。</summary>
public sealed record ModelInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Provider { get; init; }
    public required string Api { get; init; }
    public string? BaseUrl { get; init; }
    /// <summary>模型选择器里的一句话说明。</summary>
    public string? Description { get; init; }
    public bool Reasoning { get; init; }
    public bool SupportsImages { get; init; }
    public int ContextWindow { get; init; } = 200_000;
    public int MaxTokens { get; init; } = 32_000;
    /// <summary>新会话默认的上下文预算（模型选择器的较小一档）；null 时用全局默认值，见 AgentHost 的 SessionOptions。</summary>
    public int? DefaultContextWindow { get; init; }
    /// <summary>
    /// 是否支持 fast mode：Anthropic 为 speed:"fast"（Opus 5.5 / Opus 5 / Opus 4.8，仅 Claude API），
    /// OpenAI 为 service_tier:"fast"。
    /// </summary>
    public bool SupportsFastMode { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public ModelCompat? Compat { get; init; }
}
