using System.Text.Json.Serialization;

namespace TKCode.Ai.Models;

/// <summary>Chat Completions 兼容厂商控制推理的方式。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChatThinkingFormat>))]
public enum ChatThinkingFormat
{
    /// <summary>不发送任何推理参数。</summary>
    None,
    /// <summary>OpenAI：reasoning_effort。</summary>
    OpenAIEffort,
    /// <summary>只有开关：thinking: { type: enabled | disabled }（Kimi K2.x）。</summary>
    ThinkingType,
    /// <summary>开关 + 强度：thinking.type + reasoning_effort（DeepSeek V4、智谱 GLM-5.2+）。</summary>
    ThinkingTypeAndEffort,
    /// <summary>通义 DashScope：enable_thinking。</summary>
    EnableThinking,
    /// <summary>OpenRouter：reasoning: { effort }。</summary>
    OpenRouter,
}

[JsonConverter(typeof(JsonStringEnumConverter<AnthropicThinkingMode>))]
public enum AnthropicThinkingMode
{
    /// <summary>thinking: {type: adaptive} + output_config.effort（Claude 4.6 及以后）。</summary>
    Adaptive,
    /// <summary>thinking: {type: enabled, budget_tokens}（旧模型与部分兼容端点）。</summary>
    Budget,
    /// <summary>不发送 thinking 参数。</summary>
    None,
}

/// <summary>
/// 厂商差异的数据化描述。所有字段可空：null 表示使用按 BaseUrl 自动探测的默认值。
/// 构建顺序：默认值 → 按主机探测 → 模型/用户覆盖（<see cref="CompatResolver"/>）。
/// </summary>
public sealed record ModelCompat
{
    /// <summary>
    /// 模型接受的 effort 取值（none / minimal / low / medium / high / xhigh / max 的子集）。
    /// null 表示全部可用。用户选择的档位不在集合中时就近取值；"关闭"在不支持 none 时取最低档。
    /// </summary>
    public IReadOnlyList<string>? ReasoningEfforts { get; init; }

    // ---- Chat Completions ----
    /// <summary>推理模型的系统提示使用 developer 角色（许多兼容厂商不支持）。</summary>
    public bool? SupportsDeveloperRole { get; init; }
    /// <summary>"max_tokens" 或 "max_completion_tokens"。</summary>
    public string? MaxTokensField { get; init; }
    public bool? SupportsStore { get; init; }
    public bool? SupportsUsageInStreaming { get; init; }
    public ChatThinkingFormat? ThinkingFormat { get; init; }
    /// <summary>
    /// 明文推理（reasoning_content）可在同一 Provider 的不同模型之间回放（DeepSeek、Kimi、智谱、通义）。
    /// 关闭时只在完全同一模型内回放；带签名 / 加密的推理（OpenAI、OpenRouter）不应开启。
    /// </summary>
    public bool? ReplayReasoningAcrossProviderModels { get; init; }
    /// <summary>带工具时，所有历史助手消息都必须带 reasoning_content（DeepSeek V4、Kimi）。</summary>
    public bool? RequiresReasoningContentOnAssistant { get; init; }
    /// <summary>工具结果消息需要带 name 字段（Mistral 等）。</summary>
    public bool? RequiresToolResultName { get; init; }
    /// <summary>历史有工具调用而本次无工具时发送 tools: []（LiteLLM 等代理需要）。</summary>
    public bool? SendEmptyToolsWithHistory { get; init; }
    /// <summary>发送 OpenAI 的 prompt_cache_key 与 stream_options.include_obfuscation:false。</summary>
    public bool? SupportsPromptCacheKey { get; init; }

    /// <summary>对话中途的 system 消息是否可用；不可用时改写为 &lt;system-reminder&gt; 用户文本。</summary>
    public bool? SupportsMidConversationSystem { get; init; }

    // ---- Anthropic Messages ----
    public AnthropicThinkingMode? AnthropicThinking { get; init; }
    /// <summary>
    /// ThinkingLevel.Off 时能否发送 thinking:{type:disabled}。
    /// Anthropic Opus 5.5 / Fable / Sonnet 5.5 与智谱 GLM-5.3 等强制思考的模型会 400。
    /// </summary>
    public bool? SupportsDisabledThinking { get; init; }
    /// <summary>是否接受 output_config.effort（Sonnet 4.5 / Haiku 4.5 会 400）。</summary>
    public bool? SupportsEffort { get; init; }
    /// <summary>认证方式："x-api-key"（Anthropic、DeepSeek、智谱）或 "bearer"（Kimi、MiniMax）。</summary>
    public string? AuthHeader { get; init; }
    public bool? SupportsEagerToolInputStreaming { get; init; }
    public bool? SupportsCacheControl { get; init; }
    /// <summary>附加的 anthropic-beta 头。</summary>
    public IReadOnlyList<string>? AnthropicBetas { get; init; }
}
