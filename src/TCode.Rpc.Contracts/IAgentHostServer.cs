namespace TCode.Rpc;

/// <summary>
/// Agent Host 进程对外暴露的方法（UI → Host）。JSON-RPC 2.0，经 stdio 传输。
/// 方法名见 <see cref="RpcMethods"/>；线路实现见 <see cref="AgentHostServerProxy"/> / <see cref="AgentHostServerDispatcher"/>。
/// 新增方法时：接口 + RpcMethods + RpcJsonContext（DTO）+ 代理与分发器各一行。
/// </summary>
public interface IAgentHostServer
{
    Task<HostInfo> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModelDto>> ListModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>设置页：已配置 / 检测到 Key / 推荐的厂商，以及它们的模型与 Key 状态。</summary>
    Task<ProvidersSnapshotDto> ListProvidersAsync(CancellationToken cancellationToken = default);

    /// <summary>内置目录中的全部厂商（“添加厂商”）。</summary>
    Task<IReadOnlyList<CatalogProviderDto>> ListCatalogProvidersAsync(CancellationToken cancellationToken = default);

    /// <summary>立即重新读取 settings.json（文件监听之外的兜底，例如界面刚写完文件）。</summary>
    Task ReloadSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>测试厂商连接（优先 GET /models，不支持时发一次最小请求）。</summary>
    Task<ProviderTestResult> TestProviderAsync(ProviderTestRequest request, CancellationToken cancellationToken = default);

    /// <summary>重新通过 GET /models 获取自定义 / 本地厂商的模型列表，完成后推送 models/changed。</summary>
    Task RefreshProviderModelsAsync(string providerId, CancellationToken cancellationToken = default);

    /// <summary>模型目录的来源与同步状态。</summary>
    Task<CatalogStatusDto> GetCatalogStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>从 models.dev 下载最新目录并与当前目录比较；结果暂存，不会自动应用。</summary>
    Task<CatalogUpdateDto> CheckCatalogAsync(CancellationToken cancellationToken = default);

    /// <summary>应用最近一次检查得到的更新，完成后推送 models/changed。</summary>
    Task<CatalogStatusDto> ApplyCatalogUpdateAsync(CancellationToken cancellationToken = default);

    /// <summary>放弃暂存的更新。</summary>
    Task DiscardCatalogUpdateAsync(CancellationToken cancellationToken = default);

    /// <summary>外部 Agent 运行时（Claude Agent / Codex / ACP Agent）的发现与探测结果。</summary>
    Task<IReadOnlyList<RuntimeDto>> ListRuntimesAsync(CancellationToken cancellationToken = default);

    Task<SessionDto> CreateSessionAsync(CreateSessionRequest request, CancellationToken cancellationToken = default);

    /// <summary>按最近修改时间倒序列出会话；<paramref name="cwd"/> 为 null 时列出全部项目。</summary>
    Task<IReadOnlyList<SessionSummaryDto>> ListSessionsAsync(string? cwd, CancellationToken cancellationToken = default);

    /// <summary>恢复会话文件；已在 Host 中打开（例如仍在运行）时直接返回该会话，不会重复加载。</summary>
    Task<OpenSessionResult> OpenSessionAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>打开会话，只返回最近 <paramref name="turnLimit"/> 轮历史（更早的用 <see cref="GetHistoryAsync"/> 分页获取）。</summary>
    Task<OpenSessionResult> OpenSessionAsync(string filePath, int? turnLimit, CancellationToken cancellationToken = default);

    /// <summary>已打开会话中 <paramref name="before"/> 之前的若干轮历史。</summary>
    Task<HistoryPageDto> GetHistoryAsync(string sessionId, int before, int turnLimit, CancellationToken cancellationToken = default);

    /// <summary>分页列出会话（基于会话索引，只重新解析修改过的文件）。</summary>
    Task<SessionPageDto> QuerySessionsAsync(string? cwd, int offset, int limit, CancellationToken cancellationToken = default);

    /// <summary>重命名；空串恢复为第一条用户消息。</summary>
    Task RenameSessionAsync(string filePath, string title, CancellationToken cancellationToken = default);

    Task PinSessionAsync(string filePath, bool pinned, CancellationToken cancellationToken = default);

    /// <summary>永久删除会话文件；会话正在 Host 中打开时先中止并关闭。</summary>
    Task DeleteSessionAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>在新会话中继续：复制当前分支的历史与参数到一个新会话并打开（仅内置 Agent）。</summary>
    Task<OpenSessionResult> ForkSessionAsync(string filePath, CancellationToken cancellationToken = default);

    Task<PromptResult> PromptAsync(PromptRequest request, CancellationToken cancellationToken = default);

    Task AbortAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>切换模型，返回按新模型修正后的会话参数。</summary>
    Task<SessionOptionsDto> SetModelAsync(string sessionId, string modelId, CancellationToken cancellationToken = default);

    /// <summary>修改思考档位 / fast mode / 上下文预算，返回生效后的完整参数（不支持的取值会被就近修正）。</summary>
    Task<SessionOptionsDto> SetOptionsAsync(string sessionId, SessionOptionsDto options, CancellationToken cancellationToken = default);

    Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default);
}

/// <summary>
/// UI 进程实现、供 Host 调用的方法（Host → UI）：会话事件推送与工具审批。
/// </summary>
public interface IAgentHostClient
{
    /// <summary>会话事件通知（JSON-RPC notification，无返回）。</summary>
    void OnSessionEvent(SessionEventNotification notification);

    /// <summary>settings.json 变化后推送最新模型列表（notification）。</summary>
    void OnModelsChanged(ModelsChangedNotification notification);

    /// <summary>需要审批的工具调用（按权限档位与工作区边界判定）执行前向用户请求审批。</summary>
    Task<ToolApprovalResponse> RequestToolApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default);
}

public static class RpcProtocol
{
    public const int Version = 13;
}
