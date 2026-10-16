using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TCode.Agent;
using TCode.Agent.Coding;
using TCode.Agent.Coding.Sessions;
using TCode.Agent.Coding.Tools;
using TCode.Agent.Coding.Tools.Web;
using TCode.Agent.Coding.Workspace;
using TCode.AgentHost.Peers;
using TCode.Config;
using TCode.Ai;
using TCode.Ai.Messages;
using TCode.Ai.Models;
using TCode.Ai.Streaming;
using TCode.Rpc;
using TCode.Runtimes;

namespace TCode.AgentHost;

/// <summary>
/// 管理 Host 内的多个会话，把 Agent 事件翻译成只含增量的 RPC 事件推送给 UI，
/// 并为非只读工具向 UI 请求审批。
/// </summary>
public sealed class SessionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Entry> _sessions = new();
    private readonly WireApiRegistry apis;
    private readonly ModelCatalog catalog;
    private readonly RuntimeRegistry runtimes;
    private readonly ILogger<SessionManager> logger;
    private readonly WebFetcher _webFetcher = new();
    private (string? Signature, IWebSearchProvider? Provider) _webSearch;
    private IAgentHostClient? _client;

    public SessionManager(WireApiRegistry apis, ModelCatalog catalog, RuntimeRegistry runtimes, PeerDirectory peers, ILogger<SessionManager> logger)
    {
        this.apis = apis;
        this.catalog = catalog;
        this.runtimes = runtimes;
        this.logger = logger;
        Coordinator = new AgentCoordinator(this, peers, logger);
    }

    /// <summary>子 Agent 与跨会话消息。</summary>
    public AgentCoordinator Coordinator { get; }

    public WireApiRegistry Apis => apis;

    public string? GetApiKey(string provider) => catalog.GetApiKey(provider);

    /// <summary>子 Agent 可用的模型：只接受内置协议的模型（外部运行时的模型不能作为子 Agent）。</summary>
    public ModelInfo? FindBuiltinModel(string reference) =>
        catalog.Find(reference) is { } model && runtimes.Find(model.Provider) is null ? model : null;

    /// <summary>内置工具使用的宿主能力：网页抓取、按 settings 配置的网页搜索、子 Agent 协调。</summary>
    public ToolServices ServicesFor(string cwd) => new()
    {
        WebFetcher = _webFetcher,
        WebSearch = CurrentWebSearch(),
        Agents = Coordinator,
    };

    private IWebSearchProvider? CurrentWebSearch()
    {
        var settings = SettingsFile.Load().Settings?.WebSearch;
        if (settings?.Provider is not { Length: > 0 } provider)
        {
            return null;
        }
        var key = Secrets.Resolve(settings.ApiKey).Value;
        var signature = $"{provider}|{key}|{settings.BaseUrl}";
        lock (_sessions)
        {
            if (_webSearch.Signature == signature)
            {
                return _webSearch.Provider;
            }
            IWebSearchProvider? created = null;
            try
            {
                created = WebSearchProviders.Create(provider, key, settings.BaseUrl);
            }
            catch (WebSearchException ex)
            {
                logger.LogWarning("web_search is disabled: {Reason}", ex.Message);
            }
            _webSearch = (signature, created);
            return created;
        }
    }

    private BuiltinOptions OptionsFor(string cwd) => new()
    {
        Workspace = new WorkspaceRoots(cwd, SettingsFile.Load().Settings?.Permissions?.AdditionalDirectories),
        Services = ServicesFor(cwd),
    };

    /// <summary>会话在 send_message / list_agents 中的名字：项目目录名加会话 id 的末 4 位。</summary>
    public string NameOf(AgentSession session) =>
        $"{AgentCoordinator.Slug(Path.GetFileName(session.Cwd.TrimEnd(Path.DirectorySeparatorChar)))}-{session.Id[^4..]}";

    /// <summary>打开的顶层会话（不含子 Agent）。</summary>
    public IReadOnlyList<AgentSession> TopLevel() => _sessions.Values.Select(e => e.Session).ToArray();

    public AgentSession? TryGet(string sessionId) => _sessions.TryGetValue(sessionId, out var entry) ? entry.Session : null;

    public void AttachClient(IAgentHostClient client)
    {
        _client = client;
        catalog.Changed += OnCatalogChanged;
        runtimes.Changed += NotifyModelsChanged;
    }

    /// <summary>
    /// settings.json 变化后：已打开会话中仍存在的模型换成新配置（Key / Base URL / 元数据），然后通知 UI。
    /// 已不存在的模型保持原样，直到用户切换模型。
    /// </summary>
    private void OnCatalogChanged()
    {
        foreach (var entry in _sessions.Values)
        {
            var current = entry.Session.Agent.Model;
            if (catalog.Find(current.Provider, current.Id) is { } updated && !ReferenceEquals(updated, current))
            {
                entry.Session.Agent.Model = updated;
            }
        }
        NotifyModelsChanged();
    }

    /// <summary>模型列表变化（settings.json、目录同步、外部运行时探测完成）后推送给 UI。</summary>
    private void NotifyModelsChanged()
    {
        try
        {
            _client?.OnModelsChanged(new ModelsChangedNotification(AgentHostServer.AllModelDtos(catalog, runtimes), catalog.Errors));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to notify models changed");
        }
    }

    public AgentSession Create(string cwd, ModelInfo model) =>
        Register(AgentSession.Create(apis, model, cwd, getApiKey: catalog.GetApiKey, options: OptionsFor(cwd)));

    public AgentSession Open(string filePath, ModelInfo model) =>
        Register(AgentSession.Open(apis, model, filePath, catalog.GetApiKey, OptionsFor(SessionStore.Read(filePath).Cwd)));

    /// <summary>由外部运行时承载的新会话；CLI 进程在第一次提问时启动。</summary>
    public AgentSession CreateExternal(string cwd, IAgentRuntime runtime, ModelInfo model) =>
        Register(AgentSession.Create(cwd, _ => new ExternalAgent(runtime, model, cwd)));

    /// <summary>打开外部运行时的会话：第一次提问时用记录的原生会话 id 恢复。</summary>
    public AgentSession OpenExternal(string filePath, IAgentRuntime runtime, ModelInfo model) =>
        Register(AgentSession.Open(filePath, (store, snapshot) => new ExternalAgent(runtime, model, store.Cwd, snapshot.NativeSessionId)));

    /// <summary>
    /// 换承载会话的运行时（<paramref name="runtime"/> 为 null 表示内置 Agent）。只允许尚无消息、未在运行的会话：
    /// 已有对话时上下文在原运行时里，无法迁移。会话 id 与文件保持不变。
    /// </summary>
    public async Task<AgentSession> SwitchRuntimeAsync(string sessionId, IAgentRuntime? runtime, ModelInfo model)
    {
        var old = Get(sessionId);
        if (old.Agent.IsRunning || old.Agent.Messages.Count > 0)
        {
            throw new InvalidOperationException("This session already has a conversation; start a new session to use a different agent.");
        }
        if (_sessions.TryRemove(sessionId, out var entry))
        {
            entry.Subscription?.Dispose();
            await entry.Session.DisposeAsync();
        }
        logger.LogInformation("Session {SessionId} switched to {Runtime}", sessionId, runtime?.Id ?? "builtin");
        return Register(runtime is null
            ? AgentSession.CreateBuiltin(apis, model, old.Store, catalog.GetApiKey, OptionsFor(old.Cwd))
            : AgentSession.Rebind(old.Store, new ExternalAgent(runtime, model, old.Cwd)));
    }

    /// <summary>按会话文件查找已打开的会话（同一文件只允许一个 Agent 写入）。</summary>
    public AgentSession? FindByFile(string filePath) =>
        _sessions.Values.Select(e => e.Session)
            .FirstOrDefault(s => string.Equals(Path.GetFullPath(s.Store.FilePath), Path.GetFullPath(filePath), StringComparison.Ordinal));

    public bool IsRunning(string sessionId) => _sessions.TryGetValue(sessionId, out var entry) && entry.Session.Agent.IsRunning;

    private AgentSession Register(AgentSession session)
    {
        var entry = new Entry(session);
        entry.Subscription = session.Agent.Subscribe((e, ct) => ForwardAsync(entry, e));
        if (session.Agent is Agent.Agent builtin)
        {
            builtin.BeforeToolCall = (ctx, ct) => ApproveAsync(session.Id, session, ctx, ct);
        }
        else if (session.Agent is ExternalAgent external)
        {
            external.ApprovalHandler = (request, ct) => ApproveExternalAsync(session.Id, request, ct);
        }
        _sessions[session.Id] = entry;
        logger.LogInformation("Session {SessionId} registered in {Cwd}", session.Id, session.Cwd);
        return session;
    }

    public AgentSession Get(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var entry)
            ? entry.Session
            : throw new KeyNotFoundException($"Session '{sessionId}' not found.");

    public async Task CloseAsync(string sessionId)
    {
        await Coordinator.CloseChildrenAsync(sessionId);
        if (_sessions.TryRemove(sessionId, out var entry))
        {
            entry.Subscription?.Dispose();
            await entry.Session.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _sessions.Keys.ToArray())
        {
            await CloseAsync(id);
        }
    }

    /// <summary>
    /// 内置 Agent 的审批：按 <see cref="PermissionPolicy"/> 判定，需要询问时转给 UI；没有 UI 连接时拒绝。
    /// 子 Agent 以父会话（<paramref name="sessionId"/>）的名义请求，权限档位跟随父会话，标题前缀为子 Agent 名。
    /// </summary>
    public async ValueTask<ToolCallDecision> ApproveAsync(string sessionId, AgentSession session, BeforeToolCallContext ctx, CancellationToken ct,
        string? agentName = null)
    {
        var mode = TryGet(sessionId)?.Agent.PermissionMode ?? session.Agent.PermissionMode;
        var verdict = PermissionPolicy.Evaluate(ctx.Permission, mode, session.Workspace, session.Approvals);
        if (verdict.Allowed)
        {
            return ToolCallDecision.Allow;
        }
        if (_client is null)
        {
            return ToolCallDecision.Deny("No client is connected to approve this tool call.");
        }
        var title = agentName is null ? verdict.Title : $"[{agentName}] {verdict.Title}";
        var response = await _client.RequestToolApprovalAsync(new ToolApprovalRequest(sessionId, ctx.ToolCall.Id, ctx.ToolCall.Name,
            ctx.ToolCall.ArgumentsJson, verdict.Kind, title, null, verdict.SessionKey is not null), ct);
        if (!response.Approved)
        {
            return ToolCallDecision.Deny(response.Reason is { Length: > 0 } reason
                ? $"The user denied this tool call: {reason}"
                : "The user denied this tool call.");
        }
        if (response.ForSession && verdict.SessionKey is { } key)
        {
            session.Approvals.Add(key);
        }
        return ToolCallDecision.Allow;
    }

    /// <summary>外部运行时的审批：没有 UI 连接时拒绝（ExternalAgent 也会把异常与断连转成拒绝）。</summary>
    private async ValueTask<ApprovalDecision> ApproveExternalAsync(string sessionId, RuntimeApprovalRequest request, CancellationToken ct)
    {
        if (_client is null)
        {
            return ApprovalDecision.Deny("No client is connected.");
        }
        var kind = request.Kind switch
        {
            ApprovalKind.Command => "command",
            ApprovalKind.FileChange => "fileChange",
            _ => "tool",
        };
        var response = await _client.RequestToolApprovalAsync(new ToolApprovalRequest(sessionId, request.ToolCallId, request.ToolName,
            request.ArgumentsJson, kind, request.Title, request.Reason, request.CanAllowForSession), ct);
        if (!response.Approved)
        {
            return ApprovalDecision.Deny(response.Reason);
        }
        return response.ForSession && request.CanAllowForSession
            ? new ApprovalDecision(ApprovalOutcome.AllowForSession)
            : ApprovalDecision.AllowOnce;
    }

    private ValueTask ForwardAsync(Entry entry, AgentEvent e)
    {
        var mapped = Map(e);
        if (mapped is null || _client is null)
        {
            return ValueTask.CompletedTask;
        }
        try
        {
            // 通知按发送顺序写入连接；UI 端按 Seq 处理。
            _client.OnSessionEvent(new SessionEventNotification(entry.Session.Id, Interlocked.Increment(ref entry.Seq), mapped));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to forward session event");
        }
        return ValueTask.CompletedTask;
    }

    private static SessionEvent? Map(AgentEvent e) => e switch
    {
        AgentStartEvent => new AgentStarted(),
        AgentEndEvent => new AgentEnded(),
        MessageStartEvent { Message: UserMessage user } s => new MessageStarted(s.MessageId, "user", TextOf(user.Content),
            user.Content.OfType<ImageContent>().Select(i => i.Path).OfType<string>().ToArray() is { Length: > 0 } images ? images : null,
            user.Source),
        MessageStartEvent { Message: ToolResultMessage or SystemMessage } => null,
        MessageStartEvent s => new MessageStarted(s.MessageId, "assistant", null),
        MessageUpdateEvent { Event: TextDeltaEvent d } u => new TextDelta(u.MessageId, d.ContentIndex, d.Delta),
        MessageUpdateEvent { Event: ThinkingDeltaEvent d } u => new ThinkingDelta(u.MessageId, d.ContentIndex, d.Delta),
        MessageUpdateEvent { Event: ToolCallStartEvent d } u => new ToolCallStarted(u.MessageId, d.ContentIndex, d.Id, d.Name),
        MessageUpdateEvent { Event: ToolCallDeltaEvent d } u => new ToolCallDelta(u.MessageId, d.ContentIndex, d.JsonFragment),
        MessageUpdateEvent { Event: ToolCallEndEvent d } u => new ToolCallEnded(u.MessageId, d.ContentIndex, d.ToolCall.Id, d.ToolCall.ArgumentsJson),
        MessageEndEvent { Message: AssistantMessage a } m => new MessageEnded(m.MessageId, "assistant", a.StopReason.ToString(), a.ErrorMessage,
            a.Usage is { } usage ? new UsageDto(usage.Input, usage.Output, usage.CacheRead, usage.CacheWrite) : null),
        MessageEndEvent { Message: UserMessage } m => new MessageEnded(m.MessageId, "user", "Stop", null, null),
        ToolExecutionStartEvent t => new ToolExecutionStarted(t.ToolCallId, t.ToolName, t.ArgumentsJson),
        ToolExecutionUpdateEvent t => new ToolExecutionUpdated(t.ToolCallId, t.ToolName, HistoryPaging.Truncate(TextOf(t.Partial.Content)), t.Partial.Details),
        ToolExecutionEndEvent t => new ToolExecutionEnded(t.ToolCallId, t.ToolName, t.Result.IsError, HistoryPaging.Truncate(TextOf(t.Result.Content)),
            t.Duration.TotalMilliseconds, t.Result.Details),
        _ => null,
    };

    private static string TextOf(IReadOnlyList<ContentBlock> content) => string.Concat(content.OfType<TextContent>().Select(t => t.Text));

    private sealed class Entry(AgentSession session)
    {
        public AgentSession Session { get; } = session;
        public IDisposable? Subscription { get; set; }
        public long Seq;
    }
}
