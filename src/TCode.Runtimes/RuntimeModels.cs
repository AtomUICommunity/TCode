using TCode.Ai.Models;

namespace TCode.Runtimes;

/// <summary>外部运行时的模型约定：Provider 与 Api 都是运行时 id。</summary>
public static class RuntimeModels
{
    public static ModelInfo Create(IAgentRuntime runtime, string id, string? name = null, string? description = null,
        bool reasoning = true, int contextWindow = 200_000, bool supportsImages = false) => new()
    {
        Id = id,
        Name = string.IsNullOrWhiteSpace(name) ? id : name,
        Provider = runtime.Id,
        Api = runtime.Id,
        Description = description,
        Reasoning = reasoning,
        ContextWindow = contextWindow,
        SupportsImages = supportsImages,
    };

    /// <summary>运行时尚未探测完成或已卸载时，打开历史会话用的占位模型。</summary>
    public static ModelInfo Placeholder(IAgentRuntime runtime, string? modelId) =>
        Create(runtime, modelId ?? "default", reasoning: false);
}
