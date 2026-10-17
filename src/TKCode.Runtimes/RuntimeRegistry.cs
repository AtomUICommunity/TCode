using TKCode.Ai.Models;

namespace TKCode.Runtimes;

/// <summary>
/// 所有外部运行时。启动后在后台探测（不阻塞 Host 启动）；探测完成或刷新后触发 <see cref="Changed"/>，
/// Host 据此推送 models/changed。环境变量 <c>TKCODE_EXTERNAL_RUNTIMES=0</c> 关闭全部外部运行时（测试用）。
/// </summary>
public sealed class RuntimeRegistry
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    public RuntimeRegistry(IEnumerable<IAgentRuntime> runtimes)
    {
        All = Environment.GetEnvironmentVariable("TKCODE_EXTERNAL_RUNTIMES") == "0" ? [] : runtimes.ToArray();
        foreach (var runtime in All)
        {
            runtime.DescriptorChanged += () => Changed?.Invoke();
        }
    }

    public IReadOnlyList<IAgentRuntime> All { get; }

    public event Action? Changed;

    public IAgentRuntime? Find(string? id) =>
        id is null ? null : All.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));

    /// <summary>可用运行时的全部模型（未就绪的运行时不提供模型）。</summary>
    public IReadOnlyList<ModelInfo> Models =>
        All.Where(r => r.Descriptor.Status == RuntimeStatus.Ready).SelectMany(r => r.Descriptor.Models).ToArray();

    /// <summary>按引用 <c>&lt;runtime&gt;/&lt;model&gt;</c> 查找；运行时 id 本身可能含 '/' 以外的字符（如 <c>acp:cursor</c>）。</summary>
    public (IAgentRuntime Runtime, ModelInfo Model)? FindModel(string? reference)
    {
        if (reference is null)
        {
            return null;
        }
        var slash = reference.IndexOf('/');
        return slash <= 0 ? null : FindModel(reference[..slash], reference[(slash + 1)..]);
    }

    public (IAgentRuntime Runtime, ModelInfo Model)? FindModel(string? runtimeId, string? modelId)
    {
        if (Find(runtimeId) is not { } runtime || modelId is null)
        {
            return null;
        }
        var model = runtime.Descriptor.Models.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.Ordinal));
        return model is null ? null : (runtime, model);
    }

    /// <summary>并行探测全部运行时；单个运行时失败不影响其他运行时。</summary>
    public async Task ProbeAllAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(All.Select(async runtime =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(ProbeTimeout);
            try
            {
                await runtime.ProbeAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // 运行时自己把错误写进 Descriptor
            }
        })).ConfigureAwait(false);
        Changed?.Invoke();
    }
}
