using TCode.Terminal.Host.Sessions;
using TCode.Terminal.Protocol;

namespace TCode.Terminal.Host;

/// <summary>host 侧的一条客户端连接（04 §5.1）：握手状态、权限、订阅。</summary>
internal sealed class ClientConnection : IPtyConnectionHandler
{
    private readonly PtyHostServer _server;
    private readonly Dictionary<uint, Subscription> _subscriptions = [];
    private readonly Lock _lock = new();

    public ClientConnection(PtyHostServer server, uint id, Stream stream)
    {
        _server = server;
        Id = id;
        Connection = new PtyConnection(stream, this, $"ptyhost-conn{id}");
    }

    public uint Id { get; }
    public PtyConnection Connection { get; }
    public ClientKind Kind { get; set; } = ClientKind.Test;
    public int Pid { get; set; }

    /// <summary>none（未认证）| full | agent。</summary>
    public string Scope { get; set; } = "none";

    public string? TokenId { get; set; }

    /// <summary>hello 中声明的受限令牌 id：认证只用这个令牌验证。</summary>
    public string? ClaimedTokenId { get; set; }
    public byte[]? ClientNonce { get; set; }
    public byte[]? ServerNonce { get; set; }
    public bool Hello => ServerNonce is not null;
    public bool IsFull => Scope == "full";
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;

    public Subscription? GetSubscription(uint sessionId)
    {
        lock (_lock)
        {
            return _subscriptions.GetValueOrDefault(sessionId);
        }
    }

    public bool TryAddSubscription(Subscription subscription)
    {
        lock (_lock)
        {
            return _subscriptions.TryAdd(subscription.Session.Id, subscription);
        }
    }

    public Subscription? RemoveSubscription(uint sessionId)
    {
        lock (_lock)
        {
            return _subscriptions.Remove(sessionId, out var s) ? s : null;
        }
    }

    public Subscription[] Subscriptions
    {
        get
        {
            lock (_lock)
            {
                return [.. _subscriptions.Values];
            }
        }
    }

    public void OnRequest(PtyConnection connection, uint sessionId, Envelope request) => _server.Dispatch(this, sessionId, request);

    public void OnNotification(PtyConnection connection, uint sessionId, Envelope notification)
    {
        // 客户端 → host 的通知目前只有 $/cancel；请求都很短，忽略
    }

    public void OnData(PtyConnection connection, in FrameHeader header, ReadOnlySpan<byte> payload) => _server.OnData(this, header, payload);

    public void OnClosed(PtyConnection connection, Exception? error) => _server.OnClientClosed(this, error);
}
