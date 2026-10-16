using TCode.Agent;
using TCode.Ai.Messages;
using TCode.Ai.Models;

namespace TCode.Runtimes;

/// <summary>
/// 由外部运行时驱动的 Agent 循环（<see cref="IExternalAgentLoop"/>）。
/// 进程在第一次提问时才启动并在轮次之间保留；进程异常退出后，下一次提问用原生会话 id 恢复。
/// 事件语义与内置 <see cref="TCode.Agent.Agent"/> 相同：AgentStart → (用户消息 → 助手 / 工具消息…)* → AgentEnd。
/// </summary>
public sealed class ExternalAgent : IExternalAgentLoop, IRuntimeHost
{
    private readonly IAgentRuntime _runtime;
    private readonly string _cwd;
    private readonly List<Message> _messages = [];
    private readonly List<Func<AgentEvent, CancellationToken, ValueTask>> _listeners = [];
    private readonly Queue<UserMessage> _followUps = new();
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private IRuntimeSession? _session;
    private TurnWriter? _writer;
    private CancellationTokenSource? _runCts;
    private Task _running = Task.CompletedTask;
    private string? _nativeSessionId;

    public ExternalAgent(IAgentRuntime runtime, ModelInfo model, string cwd, string? resumeSessionId = null)
    {
        _runtime = runtime;
        _cwd = cwd;
        Model = model;
        _nativeSessionId = resumeSessionId;
    }

    public string RuntimeId => _runtime.Id;
    public string? NativeSessionId => _nativeSessionId;
    public event Action<string>? NativeSessionIdChanged;

    public ModelInfo Model { get; set; }
    public ThinkingLevel ThinkingLevel { get; set; } = ThinkingLevel.Medium;
    public bool FastMode { get; set; }

    /// <summary>权限档位：非 Ask 时审批请求直接允许（仅本次）；沙箱范围由各运行时按档位设置。</summary>
    public PermissionMode PermissionMode { get; set; }

    /// <summary>审批交给 UI；未设置、异常或断连时一律拒绝。</summary>
    public Func<RuntimeApprovalRequest, CancellationToken, ValueTask<ApprovalDecision>>? ApprovalHandler { get; set; }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _runCts is not null;
            }
        }
    }

    public IReadOnlyList<Message> Messages
    {
        get
        {
            lock (_gate)
            {
                return _messages.ToArray();
            }
        }
    }

    public void LoadHistory(IEnumerable<Message> messages)
    {
        lock (_gate)
        {
            if (_runCts is not null)
            {
                throw new InvalidOperationException("Cannot load history while running.");
            }
            _messages.Clear();
            _messages.AddRange(messages);
        }
    }

    public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> listener)
    {
        lock (_gate)
        {
            _listeners.Add(listener);
        }
        return new Unsubscriber(() =>
        {
            lock (_gate)
            {
                _listeners.Remove(listener);
            }
        });
    }

    public Task PromptAsync(UserMessage message)
    {
        lock (_gate)
        {
            if (_runCts is not null)
            {
                throw new InvalidOperationException("Agent is already running; use Steer() or FollowUp().");
            }
            var cts = _runCts = new CancellationTokenSource();
            _running = Task.Run(() => RunAsync(message, cts.Token));
            return _running;
        }
    }

    /// <summary>运行时支持时立即插入当前轮；否则（或插入失败）按 follow-up 排队。</summary>
    public void Steer(UserMessage message)
    {
        IRuntimeSession? session;
        TurnWriter? writer;
        lock (_gate)
        {
            session = _session;
            writer = _writer;
        }
        if (session is null || writer is null || writer.IsCompleted || !_runtime.Descriptor.Capabilities.SupportsSteering)
        {
            FollowUp(message);
            return;
        }
        _ = SteerAsync(session, writer, message);
    }

    private async Task SteerAsync(IRuntimeSession session, TurnWriter writer, UserMessage message)
    {
        try
        {
            if (await session.SteerAsync(message.ForModel(Model.SupportsImages), CancellationToken.None).ConfigureAwait(false))
            {
                await writer.UserMessageAsync(message).ConfigureAwait(false);
                return;
            }
        }
        catch (Exception)
        {
            // 插入失败时退回排队
        }
        FollowUp(message);
    }

    public void FollowUp(UserMessage message)
    {
        lock (_gate)
        {
            _followUps.Enqueue(message);
        }
    }

    public void Abort()
    {
        lock (_gate)
        {
            _runCts?.Cancel();
        }
    }

    public Task WaitForIdleAsync()
    {
        lock (_gate)
        {
            return _running;
        }
    }

    private async Task RunAsync(UserMessage first, CancellationToken ct)
    {
        try
        {
            await EmitAsync(new AgentStartEvent()).ConfigureAwait(false);
            var next = first;
            while (next is not null && !ct.IsCancellationRequested)
            {
                if (!await RunTurnAsync(next, ct).ConfigureAwait(false))
                {
                    break;
                }
                lock (_gate)
                {
                    next = _followUps.Count > 0 ? _followUps.Dequeue() : null;
                }
            }
        }
        finally
        {
            await EmitAsync(new AgentEndEvent([])).ConfigureAwait(false);
            lock (_gate)
            {
                _writer = null;
                _runCts?.Dispose();
                _runCts = null;
            }
        }
    }

    /// <summary>运行一轮；返回 false 表示出错或中止（排队的 follow-up 保留到下次运行）。</summary>
    private async Task<bool> RunTurnAsync(UserMessage message, CancellationToken ct)
    {
        await AppendAsync(message).ConfigureAwait(false);
        var options = new TurnOptions(Model, ThinkingLevel, FastMode, PermissionMode);
        var writer = new TurnWriter(Model, e => EmitAsync(e), m =>
        {
            lock (_gate)
            {
                _messages.Add(m);
            }
        });
        try
        {
            var session = await EnsureSessionAsync(options, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _writer = writer;
            }
            await session.RunTurnAsync(message.ForModel(Model.SupportsImages), options, writer, ct).ConfigureAwait(false);
            await writer.CompleteAsync(ct.IsCancellationRequested ? StopReason.Aborted : StopReason.Stop).ConfigureAwait(false);
            return !ct.IsCancellationRequested;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await writer.CompleteAsync(StopReason.Aborted, "Request aborted").ConfigureAwait(false);
            return false;
        }
        catch (Exception ex)
        {
            await writer.CompleteAsync(StopReason.Error, ex.Message).ConfigureAwait(false);
            // 会话可能已损坏（进程退出、协议错误）：丢弃，下一次提问时用原生会话 id 重启并恢复。
            await ResetSessionAsync().ConfigureAwait(false);
            return false;
        }
    }

    private async Task<IRuntimeSession> EnsureSessionAsync(TurnOptions options, CancellationToken ct)
    {
        await _sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session is { } existing)
            {
                return existing;
            }
            var session = await _runtime.StartSessionAsync(new RuntimeSessionRequest(_cwd, options, _nativeSessionId, this), ct)
                .ConfigureAwait(false);
            lock (_gate)
            {
                _session = session;
            }
            if (session.NativeSessionId is { } id)
            {
                OnNativeSessionId(id);
            }
            return session;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task ResetSessionAsync()
    {
        IRuntimeSession? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }
        if (session is not null)
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 进程可能已经退出
            }
        }
    }

    // ---------------- IRuntimeHost ----------------

    async ValueTask<ApprovalDecision> IRuntimeHost.RequestApprovalAsync(RuntimeApprovalRequest request, CancellationToken cancellationToken)
    {
        if (PermissionMode != PermissionMode.Ask)
        {
            return ApprovalDecision.AllowOnce;
        }
        if (ApprovalHandler is not { } handler)
        {
            return ApprovalDecision.Deny("No client is available to approve this action.");
        }
        CancellationToken runToken;
        lock (_gate)
        {
            runToken = _runCts?.Token ?? CancellationToken.None;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runToken);
        try
        {
            return await handler(request, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ApprovalDecision.Cancel;
        }
        catch (Exception ex)
        {
            // 安全规则：UI 无响应、断连或出错时一律拒绝，绝不默认放行。
            return ApprovalDecision.Deny($"Approval failed: {ex.Message}");
        }
    }

    /// <summary>原生会话 id 与已记录的不同（首次确定、或恢复失败后新建）时通知会话持久化。</summary>
    public void OnNativeSessionId(string nativeSessionId)
    {
        lock (_gate)
        {
            if (string.Equals(_nativeSessionId, nativeSessionId, StringComparison.Ordinal))
            {
                return;
            }
            _nativeSessionId = nativeSessionId;
        }
        NativeSessionIdChanged?.Invoke(nativeSessionId);
    }

    // ---------------- 事件 ----------------

    private async Task AppendAsync(Message message)
    {
        var id = Guid.CreateVersion7().ToString("N");
        await EmitAsync(new MessageStartEvent(id, message)).ConfigureAwait(false);
        lock (_gate)
        {
            _messages.Add(message);
        }
        await EmitAsync(new MessageEndEvent(id, message)).ConfigureAwait(false);
    }

    private async ValueTask EmitAsync(AgentEvent e)
    {
        Func<AgentEvent, CancellationToken, ValueTask>[] listeners;
        lock (_gate)
        {
            listeners = _listeners.ToArray();
        }
        foreach (var listener in listeners)
        {
            await listener(e, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Abort();
        await WaitForIdleAsync().ConfigureAwait(false);
        await ResetSessionAsync().ConfigureAwait(false);
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
