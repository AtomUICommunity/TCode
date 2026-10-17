using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Serialization.Metadata;

namespace TCode.Terminal.Protocol;

/// <summary>连接参数。<see cref="TokenId"/> 只在使用受限令牌（auth/mintToken）时填写。</summary>
public sealed record PtyHostEndpoint(string Address, byte[] Token, string? InstanceId = null, string? TokenId = null);

public sealed record PtyHostClientOptions
{
    public ClientKind Kind { get; init; } = ClientKind.Ui;
    public string? ClientVersion { get; init; }
    public string? GhosttyBuildId { get; init; }
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>断开原因：<see cref="Goodbye"/> 不为空表示 host 正常退出（host/goodbye），否则是连接丢失。</summary>
public sealed record PtyHostDisconnect(string? Goodbye, Exception? Error);

/// <summary>
/// 一个已 attach 的会话的接收端。在 IPC 读线程上调用：不能阻塞，不能同步调用 <see cref="PtyHostClient"/> 的方法；
/// span 只在调用期间有效，需要的话复制后再交给视图核心 actor（04 §13.1）。
/// </summary>
public interface IPtySessionSink
{
    /// <summary>attach 成功：在读线程上、早于该订阅的任何数据帧调用（attach 结果里有核心选项与起始位置）。</summary>
    void OnAttached(PtySessionHandle handle);

    void OnSnapshotChunk(uint attachId, SnapshotFormat format, ulong offset, ReadOnlySpan<byte> chunk, bool last);

    void OnOutput(ulong position, ReadOnlySpan<byte> data);

    void OnRecord(ulong position, in LogRecord record);

    /// <summary>会话事件（ExitedEventDto、TitleEventDto 等，带 seq：在核心处理到 seq 之后再呈现）。</summary>
    void OnEvent(object e);

    void OnResync(string reason);

    void OnDetached(string reason);
}

/// <summary>Desktop / AgentHost 连接 pty host 的客户端（04 §13.1）。线程安全。</summary>
public sealed class PtyHostClient : IAsyncDisposable, IPtyConnectionHandler
{
    private static PtyHostJsonContext J => PtyHostJsonContext.Default;

    private readonly PtyConnection _connection;
    private readonly ConcurrentDictionary<uint, PtySessionHandle> _sessions = new();
    private readonly ConcurrentDictionary<uint, (uint SessionId, IPtySessionSink Sink)> _pendingAttaches = new();
    private readonly TaskCompletionSource<PtyHostDisconnect> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Timer _heartbeat;
    private string? _goodbye;
    private bool _responsive = true;
    private int _heartbeatStage;

    private PtyHostClient(Stream stream)
    {
        _connection = new PtyConnection(stream, this, "ptyhost-client");
        _heartbeat = new Timer(_ => CheckHeartbeat(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public HelloResult Host { get; private set; } = null!;
    public string Scope { get; private set; } = "none";
    public uint ConnectionId { get; private set; }

    /// <summary>心跳状态（§1.6）：6 s + 5 s 收不到任何帧时为 false，之后收到任何帧就恢复。</summary>
    public bool IsResponsive => _responsive;

    public Task<PtyHostDisconnect> Completion => _completion.Task;

    public event Action<bool>? ResponsivenessChanged;
    public event Action<SessionInfoDto>? SessionAdded;
    public event Action<SessionRemovedDto>? SessionRemoved;

    /// <summary>OSC 52 读取请求（只发给主视图所在的连接）；不设置时拒绝。</summary>
    public Func<ClipboardReadRequest, CancellationToken, ValueTask<ClipboardReadResponse>>? ClipboardReadHandler { get; set; }

    /// <summary>连接并完成 hello / auth（§2.4）：校验 serverProof 与 instanceId，令牌不上线。</summary>
    public static async Task<PtyHostClient> ConnectAsync(PtyHostEndpoint endpoint, PtyHostClientOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new PtyHostClientOptions();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.ConnectTimeout);
        var stream = await PtyTransport.ConnectAsync(endpoint.Address, timeout.Token).ConfigureAwait(false);
        var client = new PtyHostClient(stream);
        client._connection.Start();
        try
        {
            var clientNonce = Handshake.NewNonce();
            var hello = await client._connection.RequestAsync(PtyMethods.Hello, new HelloParams
            {
                Protocol = ProtocolVersion.Current,
                ClientKind = options.Kind,
                ClientVersion = options.ClientVersion,
                GhosttyBuildId = options.GhosttyBuildId,
                ClientNonce = clientNonce,
                Pid = Environment.ProcessId,
                TokenId = endpoint.TokenId,
            }, J.HelloParams, J.HelloResult, cancellationToken: timeout.Token).ConfigureAwait(false);
            if (!Handshake.Verify(Handshake.ServerProof(endpoint.Token, clientNonce, hello.ServerNonce), hello.ServerProof))
            {
                throw new PtyHostException(PtyErrors.Unauthenticated, "the host failed to prove it holds the token");
            }
            if (endpoint.InstanceId is { } instance && instance != hello.InstanceId)
            {
                throw new PtyHostException(PtyErrors.Unauthenticated, $"instance mismatch: expected {instance}, got {hello.InstanceId}");
            }
            var auth = await client._connection.RequestAsync(PtyMethods.Auth, new AuthParams(Handshake.ClientProof(endpoint.Token, hello.ServerNonce, clientNonce)),
                J.AuthParams, J.AuthResult, cancellationToken: timeout.Token).ConfigureAwait(false);
            client.Host = hello;
            client.Scope = auth.Scope;
            client.ConnectionId = auth.ConnectionId;
            client._connection.Authenticated = true;
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // ---- host ----

    public Task<TResult> RequestAsync<TParams, TResult>(string method, TParams value, JsonTypeInfo<TParams> paramsType, JsonTypeInfo<TResult> resultType,
        CancellationToken cancellationToken = default) =>
        _connection.RequestAsync(method, value, paramsType, resultType, cancellationToken: cancellationToken);

    public Task ConfigureAsync(HostSettingsDto settings, CancellationToken ct = default) =>
        RequestAsync(PtyMethods.HostConfigure, settings, J.HostSettingsDto, J.AppliedResult, ct);

    public Task<HostInfoResult> InfoAsync(CancellationToken ct = default) => RequestAsync(PtyMethods.HostInfo, new EmptyDto(), J.EmptyDto, J.HostInfoResult, ct);

    public Task<QuitCheckResult> QuitCheckAsync(bool includeAgent = false, CancellationToken ct = default) =>
        RequestAsync(PtyMethods.HostQuitCheck, new QuitCheckParams(includeAgent), J.QuitCheckParams, J.QuitCheckResult, ct);

    public Task<QuitResult> QuitAsync(QuitParams p, CancellationToken ct = default) => RequestAsync(PtyMethods.HostQuit, p, J.QuitParams, J.QuitResult, ct);

    public Task<MintedToken> MintAgentTokenAsync(string label, CancellationToken ct = default) =>
        RequestAsync(PtyMethods.AuthMintToken, new MintTokenParams("agent", label), J.MintTokenParams, J.MintedToken, ct);

    public Task RevokeAsync(string tokenId, CancellationToken ct = default) => RequestAsync(PtyMethods.AuthRevoke, new RevokeParams(tokenId), J.RevokeParams, J.EmptyDto, ct);

    // ---- 会话 ----

    /// <summary>创建会话。<paramref name="sink"/> 不为空时同时 attach（<see cref="SessionCreateParams.Attach"/> 为空则用默认的 view 模式）。</summary>
    public async Task<(SessionCreatedDto Created, PtySessionHandle? Handle)> CreateSessionAsync(SessionCreateParams p, IPtySessionSink? sink, CancellationToken ct = default)
    {
        if (sink is null)
        {
            return (await RequestAsync(PtyMethods.SessionCreate, p with { Attach = null }, J.SessionCreateParams, J.SessionCreatedDto, ct).ConfigureAwait(false), null);
        }
        // 会话 id 在应答之前未知；host 保证应答先于该会话的任何数据帧（§8.2），所以在读线程收到应答时同步登记（OnResponse）
        var id = _connection.ReserveRequestId();
        _pendingAttaches[id] = (0, sink);
        try
        {
            var created = await _connection.RequestAsync(PtyMethods.SessionCreate, p with { Attach = p.Attach ?? new AttachParams() }, J.SessionCreateParams,
                J.SessionCreatedDto, cancellationToken: ct, requestId: id).ConfigureAwait(false);
            return (created, _sessions[created.SessionId]);
        }
        finally
        {
            _pendingAttaches.TryRemove(id, out _);
        }
    }

    public async Task<SessionInfoDto[]> ListSessionsAsync(CancellationToken ct = default) =>
        await RequestAsync(PtyMethods.SessionList, new EmptyDto(), J.EmptyDto, J.SessionInfoDtoArray, ct).ConfigureAwait(false);

    public async Task<PtySessionHandle> AttachAsync(AttachParams p, IPtySessionSink sink, CancellationToken ct = default)
    {
        // 应答在读线程上同步登记 handle（OnResponse），之后的数据帧才能找到 sink
        var id = _connection.ReserveRequestId();
        _pendingAttaches[id] = (p.SessionId, sink);
        try
        {
            await _connection.RequestAsync(PtyMethods.SessionAttach, p, J.AttachParams, J.AttachResultDto, p.SessionId, ct, requestId: id).ConfigureAwait(false);
            return _sessions[p.SessionId];
        }
        finally
        {
            _pendingAttaches.TryRemove(id, out _);
        }
    }

    public Task<ForegroundDto> ForegroundAsync(uint sessionId, CancellationToken ct = default) =>
        _connection.RequestAsync(PtyMethods.SessionForeground, new SessionIdParams(sessionId), J.SessionIdParams, J.ForegroundDto, sessionId, ct);

    public Task<CloseResult> CloseSessionAsync(uint sessionId, bool force, CancellationToken ct = default) =>
        _connection.RequestAsync(PtyMethods.SessionClose, new CloseParams(sessionId, force), J.CloseParams, J.CloseResult, sessionId, ct);

    public Task<ReadResult> ReadAsync(ReadParams p, CancellationToken ct = default) =>
        _connection.RequestAsync(PtyMethods.SessionRead, p, J.ReadParams, J.ReadResult, p.SessionId, ct);

    public Task InputAsync(uint sessionId, string text, CancellationToken ct = default) =>
        _connection.RequestAsync(PtyMethods.SessionInput, new SessionInputParams(sessionId, text), J.SessionInputParams, J.EmptyDto, sessionId, ct);

    public Task<KillResult> KillAsync(uint sessionId, CancellationToken ct = default) =>
        _connection.RequestAsync(PtyMethods.SessionKill, new SessionIdParams(sessionId), J.SessionIdParams, J.KillResult, sessionId, ct);

    // ---- 持久化 ----

    public async Task<ReviveEntryDto[]> ListReviveAsync(CancellationToken ct = default) =>
        await RequestAsync(PtyMethods.PersistenceList, new EmptyDto(), J.EmptyDto, J.ReviveEntryDtoArray, ct).ConfigureAwait(false);

    public Task<PersistenceSaveResult> SaveAsync(PersistenceSaveParams p, CancellationToken ct = default) =>
        RequestAsync(PtyMethods.PersistenceSave, p, J.PersistenceSaveParams, J.PersistenceSaveResult, ct);

    public Task<DeletedResult> DeleteReviveAsync(string[] persistentIds, CancellationToken ct = default) =>
        RequestAsync(PtyMethods.PersistenceDelete, new PersistenceDeleteParams(persistentIds), J.PersistenceDeleteParams, J.DeletedResult, ct);

    // ---- 内部：handle 使用 ----

    internal PtyConnection Connection => _connection;

    internal void Forget(uint sessionId) => _sessions.TryRemove(sessionId, out _);

    // ---- 读线程回调 ----

    void IPtyConnectionHandler.OnResponse(PtyConnection connection, uint id, Envelope response)
    {
        if (response.Error is not null || !_pendingAttaches.TryRemove(id, out var pending))
        {
            return;
        }
        uint sessionId;
        AttachResultDto? attach;
        if (pending.SessionId == 0)
        {
            var created = response.Read(J.SessionCreatedDto);
            (sessionId, attach) = (created.SessionId, created.Attach);
        }
        else
        {
            (sessionId, attach) = (pending.SessionId, response.Read(J.AttachResultDto));
        }
        if (attach is not null)
        {
            var handle = new PtySessionHandle(this, sessionId, attach, pending.Sink);
            _sessions[sessionId] = handle;
            pending.Sink.OnAttached(handle);
        }
    }

    void IPtyConnectionHandler.OnRequest(PtyConnection connection, uint sessionId, Envelope request)
    {
        var id = request.Id!.Value;
        if (request.Method == PtyMethods.UiClipboardRead && ClipboardReadHandler is { } handler)
        {
            var p = request.Read(J.ClipboardReadRequest);
            _ = Task.Run(async () =>
            {
                try
                {
                    connection.Respond(id, await handler(p, CancellationToken.None).ConfigureAwait(false), J.ClipboardReadResponse, sessionId);
                }
                catch (Exception e)
                {
                    connection.RespondError(id, PtyErrors.InternalError, e.Message, sessionId);
                }
            });
            return;
        }
        if (request.Method == PtyMethods.UiClipboardRead)
        {
            connection.Respond(id, new ClipboardReadResponse(false), J.ClipboardReadResponse, sessionId);
            return;
        }
        connection.RespondError(id, PtyErrors.MethodNotFound, $"unknown method {request.Method}", sessionId);
    }

    void IPtyConnectionHandler.OnNotification(PtyConnection connection, uint sessionId, Envelope n)
    {
        switch (n.Method)
        {
            case PtyMethods.HostSessionAdded:
                SessionAdded?.Invoke(n.Read(J.SessionInfoDto));
                return;
            case PtyMethods.HostSessionRemoved:
                var removed = n.Read(J.SessionRemovedDto);
                if (_sessions.TryRemove(removed.SessionId, out var gone))
                {
                    gone.Sink.OnDetached(removed.Reason);
                }
                SessionRemoved?.Invoke(removed);
                return;
            case PtyMethods.HostGoodbye:
                _goodbye = n.Read(J.GoodbyeDto).Reason;
                return;
        }
        if (Sink(sessionId) is not { } sink)
        {
            return;
        }
        object? e = n.Method switch
        {
            PtyMethods.EventExited => n.Read(J.ExitedEventDto),
            PtyMethods.EventTitle => n.Read(J.TitleEventDto),
            PtyMethods.EventCwd => n.Read(J.CwdEventDto),
            PtyMethods.EventBell => new BellEvent(n.Read(J.SeqEventDto).Seq),
            PtyMethods.EventNotification => n.Read(J.NotificationEventDto),
            PtyMethods.EventProgress => n.Read(J.ProgressEventDto),
            PtyMethods.EventCommandStarted => n.Read(J.CommandStartedEventDto),
            PtyMethods.EventCommandFinished => n.Read(J.CommandFinishedEventDto),
            PtyMethods.EventClipboardWrite => n.Read(J.ClipboardWriteEventDto),
            PtyMethods.EventInputOverflow => n.Read(J.InputOverflowEventDto),
            _ => null,
        };
        if (n.Method == PtyMethods.EventResync)
        {
            sink.OnResync(n.Read(J.ResyncEventDto).Reason);
        }
        else if (e is not null)
        {
            sink.OnEvent(e);
        }
    }

    void IPtyConnectionHandler.OnData(PtyConnection connection, in FrameHeader header, ReadOnlySpan<byte> payload)
    {
        if (Sink(header.SessionId) is not { } sink)
        {
            return;
        }
        switch (header.Type)
        {
            case FrameType.Output:
                sink.OnOutput(DataFrames.ReadSeq(payload), payload[DataFrames.OutputHeader..]);
                break;
            case FrameType.Record:
                if (payload.Length < DataFrames.RecordHeader)
                {
                    throw new ProtocolException("record frame too short");
                }
                var record = LogRecord.Read((RecordKind)payload[8], payload[DataFrames.RecordHeader..]);
                sink.OnRecord(DataFrames.ReadSeq(payload), record);
                break;
            case FrameType.SnapshotChunk:
                var (attachId, last, format, offset) = DataFrames.ReadSnapshotHeader(payload);
                sink.OnSnapshotChunk(attachId, format, offset, payload[DataFrames.SnapshotHeader..], last);
                break;
        }
    }

    private IPtySessionSink? Sink(uint sessionId) => _sessions.TryGetValue(sessionId, out var h) ? h.Sink : null;

    void IPtyConnectionHandler.OnClosed(PtyConnection connection, Exception? error)
    {
        _heartbeat.Dispose();
        foreach (var (id, handle) in _sessions)
        {
            if (_sessions.TryRemove(id, out _))
            {
                handle.Sink.OnDetached(_goodbye is null ? "connectionLost" : "hostExited");
            }
        }
        _completion.TrySetResult(new PtyHostDisconnect(_goodbye, error));
    }

    /// <summary>两段计时（§1.6）：6 s 记警告，再 5 s 判定无响应；收到任何帧就恢复。</summary>
    private void CheckHeartbeat()
    {
        var silent = _connection.SinceLastReceive;
        var stage = silent >= TimeSpan.FromSeconds(11) ? 2 : silent >= TimeSpan.FromSeconds(6) ? 1 : 0;
        var previous = Interlocked.Exchange(ref _heartbeatStage, stage);
        if (stage == previous)
        {
            return;
        }
        var responsive = stage < 2;
        if (responsive != _responsive)
        {
            _responsive = responsive;
            ResponsivenessChanged?.Invoke(responsive);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
        await _heartbeat.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed record BellEvent(ulong Seq);

/// <summary>一个已 attach 的订阅（04 §13.1）。所有方法线程安全；SendInput 从不阻塞。</summary>
public sealed class PtySessionHandle : IAsyncDisposable
{
    private static PtyHostJsonContext J => PtyHostJsonContext.Default;

    private readonly PtyHostClient _client;

    internal PtySessionHandle(PtyHostClient client, uint sessionId, AttachResultDto attach, IPtySessionSink sink)
    {
        _client = client;
        SessionId = sessionId;
        Attach = attach;
        Sink = sink;
    }

    public uint SessionId { get; }
    public AttachResultDto Attach { get; }
    internal IPtySessionSink Sink { get; }

    /// <summary>按 64 KiB 切成 Input 帧。与控制请求同一个先进先出队列，保证顺序。</summary>
    public void SendInput(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            var n = Math.Min(bytes.Length, FrameLimits.MaxDataChunk);
            _client.Connection.SendFrame(FrameType.Input, SessionId, bytes[..n]);
            bytes = bytes[n..];
        }
    }

    /// <summary>已处理完所有位置 &lt; <paramref name="position"/> 的记录（§7.2 第 4 条：每 32 KiB 一次，快照处理完后一次）。插到队首。</summary>
    public void Ack(ulong position)
    {
        Span<byte> payload = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, position);
        _client.Connection.SendFrame(FrameType.Ack, SessionId, payload, urgent: true);
    }

    public Task<ResizeResult> ResizeAsync(TerminalSizeDto size, CancellationToken ct = default) =>
        Request(PtyMethods.SessionResize, new SessionResizeParams(SessionId, size.Cols, size.Rows, size.CellWidth, size.CellHeight), J.SessionResizeParams, J.ResizeResult, ct);

    public Task<FocusResult> FocusAsync(CancellationToken ct = default) =>
        Request(PtyMethods.SessionFocus, new SessionIdParams(SessionId), J.SessionIdParams, J.FocusResult, ct);

    public async Task<ulong> SetColorsAsync(PaletteDto palette, CancellationToken ct = default) =>
        (await Request(PtyMethods.SessionSetColors, new SetColorsParams(SessionId, palette), J.SetColorsParams, J.SeqResult, ct).ConfigureAwait(false)).Seq;

    public async Task<ulong> ResetAsync(bool hard, CancellationToken ct = default) =>
        (await Request(PtyMethods.SessionReset, new ResetParams(SessionId, hard), J.ResetParams, J.SeqResult, ct).ConfigureAwait(false)).Seq;

    public async Task<ulong> ClearAsync(bool screen, bool scrollback, bool redrawPrompt = false, CancellationToken ct = default) =>
        (await Request(PtyMethods.SessionClear, new ClearParams(SessionId, scrollback, screen, redrawPrompt), J.ClearParams, J.SeqResult, ct).ConfigureAwait(false)).Seq;

    public async Task<bool> SignalAsync(string signal, string target = "foreground", CancellationToken ct = default) =>
        (await Request(PtyMethods.SessionSignal, new SignalParams(SessionId, signal, target), J.SignalParams, J.DeliveredResult, ct).ConfigureAwait(false)).Delivered;

    public async Task DetachAsync(CancellationToken ct = default)
    {
        _client.Forget(SessionId);
        await Request(PtyMethods.SessionDetach, new SessionIdParams(SessionId), J.SessionIdParams, J.EmptyDto, ct).ConfigureAwait(false);
    }

    private Task<TResult> Request<TParams, TResult>(string method, TParams p, JsonTypeInfo<TParams> pt, JsonTypeInfo<TResult> rt, CancellationToken ct) =>
        _client.Connection.RequestAsync(method, p, pt, rt, SessionId, ct);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await DetachAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or PtyHostException)
        {
        }
    }
}
