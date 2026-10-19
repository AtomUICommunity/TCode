using TKCode.Ai.Messages;
using TKCode.Ai.Models;
using TKCode.Ai.Streaming;

namespace TKCode.Ai;

/// <summary>工具定义：Parameters 为 JSON Schema（type: object）。</summary>
public sealed record ToolDefinition(string Name, string Description, string ParametersSchemaJson);

/// <summary>一次请求的上下文：系统提示、完整（只追加的）历史、工具集。</summary>
public sealed record LlmContext(string? SystemPrompt, IReadOnlyList<Message> Messages, IReadOnlyList<ToolDefinition> Tools);

public sealed record StreamOptions
{
    public string? ApiKey { get; init; }
    public ThinkingLevel ThinkingLevel { get; init; } = ThinkingLevel.Medium;
    public int? MaxTokens { get; init; }
    /// <summary>请求 fast mode；模型不支持（<see cref="ModelInfo.SupportsFastMode"/> 为 false）时忽略。</summary>
    public bool FastMode { get; init; }
    /// <summary>用作 prompt_cache_key、会话亲和头与 Anthropic metadata.user_id（<see cref="PromptCache"/>）。</summary>
    public string? SessionId { get; init; }
    /// <summary>cache_control 断点的有效期（Anthropic Messages，以及经 Chat 调用的 Claude 模型）。</summary>
    public PromptCacheTtl CacheTtl { get; init; }
    /// <summary>
    /// 一次性的分叉请求（例如压缩摘要）：消息断点放在最后一条消息之前（共享前缀的末尾），
    /// 只读取主循环已写入的缓存，不为本次独有的尾部写入新条目（同 Claude Code 的 skipCacheWrite）。
    /// </summary>
    public bool SkipCacheWrite { get; init; }
}

/// <summary>
/// 线协议适配器：一种协议一个实现（Chat Completions / Responses / Messages / Fake）。
/// 约定：返回的流永不抛异常，失败与中止都编码为 <see cref="ErrorEvent"/>。
/// </summary>
public interface IWireApi
{
    string Api { get; }

    AssistantMessageStream Stream(ModelInfo model, LlmContext context, StreamOptions options, CancellationToken cancellationToken);
}

/// <summary>按 <see cref="ModelInfo.Api"/> 路由到对应适配器。</summary>
public sealed class WireApiRegistry
{
    private readonly Dictionary<string, IWireApi> _apis = new(StringComparer.Ordinal);

    public WireApiRegistry(IEnumerable<IWireApi> apis)
    {
        foreach (var api in apis)
        {
            _apis[api.Api] = api;
        }
    }

    public AssistantMessageStream Stream(ModelInfo model, LlmContext context, StreamOptions options, CancellationToken cancellationToken)
    {
        if (_apis.TryGetValue(model.Api, out var api))
        {
            return api.Stream(model, context, options, cancellationToken);
        }
        var stream = new AssistantMessageStream();
        new AssistantMessageBuilder(model, stream).Fail(new NotSupportedException($"No adapter registered for api '{model.Api}'."), aborted: false);
        return stream;
    }
}
