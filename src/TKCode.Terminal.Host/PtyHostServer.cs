using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization.Metadata;
using TKCode.Terminal.Core;
using TKCode.Terminal.Host.Sessions;
using TKCode.Terminal.Protocol;
using TKCode.Terminal.Pty;

namespace TKCode.Terminal.Host;

public sealed record PtyHostServerOptions
{
    public required string Endpoint { get; init; }

    /// <summary>完整权限令牌（32 字节）。</summary>
    public required byte[] Token { get; init; }

    /// <summary>TKCODE_HOME：revive 数据在 <c>terminal/revive/</c> 下。为 null 时不持久化。</summary>
    public string? Home { get; init; }

    public string InstanceId { get; init; } = Ulid.New();
    public string Build { get; init; } = "dev";
    public string? GhosttyBuildId { get; init; }
    public string HostVersion { get; init; } = typeof(PtyHostServer).Assembly.GetName().Version?.ToString(3) ?? "0";
    public bool JobBound { get; init; }
    public int MaxSessions { get; init; } = 256;
    public int MaxConnections { get; init; } = 32;

    /// <summary>认证超时（04 §2.4）。</summary>
    public TimeSpan AuthTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>多久收不到客户端的帧就断开它（§1.6）。</summary>
    public TimeSpan ClientDeadTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// pty host 的服务端（04 §5.1）：监听、握手与认证、按方法分派、会话表。请求在连接的读线程上解析，
/// 会话类请求直接投递到会话 actor（同一连接上按会话串行，§4.8），其余在线程池上执行。
/// </summary>
public sealed partial class PtyHostServer : IAsyncDisposable
{
    private readonly PtyHostServerOptions _options;
    private readonly PtyListener _listener;
    private readonly ConcurrentDictionary<uint, ClientConnection> _clients = new();
    private readonly ConcurrentDictionary<uint, Session> _sessions = new();
    private readonly ConcurrentDictionary<string, MintedTokenEntry> _tokens = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource<string> _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HostContext _context;
    private readonly Timer _tick;
    private readonly long _startedAt = Stopwatch.GetTimestamp();
    private uint _nextConnection;
    private uint _nextSession;
    private uint _nextAttach;

    public PtyHostServer(PtyHostServerOptions options, HostLog log)
    {
        _options = options;
        Log = log;
        _context = new HostContext(log, OnSessionExited);
        _listener = PtyTransport.Listen(options.Endpoint);
        _tick = new Timer(_ => OnTick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        InitializePersistence();
    }

    public HostLog Log { get; }
    public string Endpoint => _listener.Endpoint;
    public string InstanceId => _options.InstanceId;
    public int SessionCount => _sessions.Count;

    /// <summary>host 结束（quit、宽限期到期、信号）：结果为原因。</summary>
    public Task<string> Stopped => _stopped.Task;

    internal IReadOnlyDictionary<uint, Session> Sessions => _sessions;
    internal HostContext Context => _context;

    /// <summary>UI 连接数变化（宽限期，§1.7）。</summary>
    internal event Action? ConnectionsChanged;

    public int UiConnections => _clients.Values.Count(c => c.IsFull && c.Kind == ClientKind.Ui);

    public int ConnectionCount => _clients.Count;

    /// <summary>host/quit 的模式（宽限期：keepAlive 不计时）。</summary>
    internal event Action<string>? QuitRequested;

    internal void RaiseQuitRequested(string mode) => QuitRequested?.Invoke(mode);

    /// <summary>宽限期到期（§1.7）：关闭没有前台程序的会话，有程序在运行的保留。</summary>
    internal async Task CloseIdleSessionsAsync(string reason)
    {
        foreach (var s in _sessions.Values)
        {
            var fg = await ForegroundAsync(s).ConfigureAwait(false);
            if (!fg.Running)
            {
                await SaveForQuitAsync(s, reason).ConfigureAwait(false);
                await RemoveSessionAsync(s, "closed").ConfigureAwait(false);
            }
        }
    }

    // ---- 接受连接 ----

    public async Task RunAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            Stream stream;
            try
            {
                stream = await _listener.AcceptAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
            {
                if (_stopping.IsCancellationRequested)
                {
                    break;
                }
                Log.Warn($"accept failed: {e.Message}");
                continue;
            }
            if (_clients.Count >= _options.MaxConnections)
            {
                Log.Warn("connection limit reached; rejecting");
                await stream.DisposeAsync().ConfigureAwait(false);
                continue;
            }
            var client = new ClientConnection(this, Interlocked.Increment(ref _nextConnection), stream);
            _clients[client.Id] = client;
            client.Connection.Start();
            _ = EnforceAuthTimeoutAsync(client);
        }
    }

    private async Task EnforceAuthTimeoutAsync(ClientConnection client)
    {
        await Task.Delay(_options.AuthTimeout).ConfigureAwait(false);
        if (client.Scope == "none" && !client.Connection.IsClosed)
        {
            Log.Warn($"conn{client.Id}: authentication timed out");
            client.Connection.Close(new TimeoutException("authentication timeout"));
        }
    }

    internal void OnClientClosed(ClientConnection client, Exception? error)
    {
        _clients.TryRemove(client.Id, out _);
        foreach (var s in client.Subscriptions)
        {
            client.RemoveSubscription(s.Session.Id);
            try
            {
                s.Session.Post(() => s.Session.Detach(s));
            }
            catch (PtyHostException)
            {
            }
        }
        if (error is not null)
        {
            Log.Info($"conn{client.Id} ({client.Kind}) closed: {error.Message}");
        }
        else
        {
            Log.Info($"conn{client.Id} ({client.Kind}) closed");
        }
        ConnectionsChanged?.Invoke();
    }

    private void OnTick()
    {
        foreach (var s in _sessions.Values)
        {
            s.Tick();
        }
        foreach (var c in _clients.Values)
        {
            if (c.Connection.SinceLastReceive > _options.ClientDeadTimeout)
            {
                Log.Warn($"conn{c.Id}: no frames for {_options.ClientDeadTimeout.TotalSeconds:F0} s; closing");
                c.Connection.Close(new TimeoutException("client heartbeat lost"));
            }
        }
        Ticked?.Invoke();
    }

    /// <summary>每秒一次（宽限期、定时保存等挂在这里）。</summary>
    internal event Action? Ticked;

    // ---- 数据帧 ----

    internal void OnData(ClientConnection client, in FrameHeader header, ReadOnlySpan<byte> payload)
    {
        if (client.Scope == "none")
        {
            throw new ProtocolException("data frame before authentication");
        }
        switch (header.Type)
        {
            case FrameType.Input:
                if (_sessions.TryGetValue(header.SessionId, out var session) && CanWrite(client, session) && session.Exit is null)
                {
                    _ = session.Pty.WriteAsync(payload.ToArray());
                }
                break;
            case FrameType.Ack:
                client.GetSubscription(header.SessionId)?.OnAck(DataFrames.ReadSeq(payload));
                break;
            default:
                throw new ProtocolException($"unexpected {header.Type} frame from client");
        }
    }

    // ---- 会话退出 ----

    private void OnSessionExited(Session session) => Log.Info($"session {session.Id} exited ({session.Exit?.ExitCode?.ToString() ?? session.Exit?.Signal})");

    internal async Task RemoveSessionAsync(Session session, string reason)
    {
        if (!_sessions.TryRemove(session.Id, out _))
        {
            return;
        }
        foreach (var s in session.Subscribers)
        {
            s.Client.RemoveSubscription(session.Id);
        }
        BroadcastFull(PtyMethods.HostSessionRemoved, new SessionRemovedDto(session.Id, session.PersistentId, reason), PtyHostJsonContext.Default.SessionRemovedDto);
        await session.DisposeAsync().ConfigureAwait(false);
    }

    private void BroadcastFull<T>(string method, T value, JsonTypeInfo<T> type, ClientConnection? except = null)
    {
        foreach (var c in _clients.Values)
        {
            if (c.IsFull && c != except)
            {
                c.Connection.Notify(method, value, type);
            }
        }
    }

    // ---- 停止 ----

    /// <summary>结束 host：发 goodbye、关闭全部会话与连接。</summary>
    internal async Task ShutdownAsync(string reason)
    {
        if (_stopping.IsCancellationRequested)
        {
            await _stopped.Task.ConfigureAwait(false);
            return;
        }
        _stopping.Cancel();
        Log.Info($"shutting down: {reason}");
        BroadcastFull(PtyMethods.HostGoodbye, new GoodbyeDto(reason), PtyHostJsonContext.Default.GoodbyeDto);
        await Task.WhenAll(_clients.Values.Select(c => c.Connection.DrainAsync(TimeSpan.FromSeconds(1)))).ConfigureAwait(false);
        await Task.WhenAll(_sessions.Values.Select(s => RemoveSessionAsync(s, "closed"))).ConfigureAwait(false);
        await Task.WhenAll(_clients.Values.Select(c => c.Connection.DisposeAsync().AsTask())).ConfigureAwait(false);
        _listener.Dispose();
        _tick.Dispose();
        _stopped.TrySetResult(reason);
    }

    public async ValueTask DisposeAsync() => await ShutdownAsync("disposed").ConfigureAwait(false);

    private sealed record MintedTokenEntry(byte[] Token, string Scope, string? Label, DateTimeOffset? Expires);
}
