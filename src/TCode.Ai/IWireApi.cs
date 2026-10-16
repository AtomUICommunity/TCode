using TCode.Ai.Messages;
using TCode.Ai.Models;
using TCode.Ai.Streaming;

namespace TCode.Ai;

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
    /// <summary>用作 prompt_cache_key / 会话亲和头。</summary>
    public string? SessionId { get; init; }
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
