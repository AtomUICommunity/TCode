using System.Diagnostics;
using TCode.Terminal.Protocol;

namespace TCode.Terminal.Host.Sessions;

internal enum SubscriptionMode
{
    /// <summary>接收快照与 Output / Record，参与流控。</summary>
    View,

    /// <summary>只接收事件，不参与流控（04 §7.3）。</summary>
    Agent,
}

internal enum SubscriptionState
{
    Live,

    /// <summary>被降级（慢订阅者 10 s，或游标落到环外），等客户端重新 attach（04 §7.4）。</summary>
    Resync,

    Detached,
}

/// <summary>一个连接对一个会话的订阅（04 §6.4）。作为数据生产者挂在连接的写线程上。</summary>
internal sealed class Subscription(Session session, ClientConnection client, SubscriptionMode mode, uint attachId) : IFrameProducer
{
    private long _acked;
    private byte[]? _snapshot;
    private int _snapshotOffset;
    private SnapshotFormat _snapshotFormat;
    private volatile SubscriptionState _state = SubscriptionState.Live;

    public Session Session { get; } = session;
    public ClientConnection Client { get; } = client;
    public SubscriptionMode Mode { get; } = mode;
    public uint AttachId { get; } = attachId;

    /// <summary>下一个要发送的位置（在日志锁内读写）。</summary>
    public ulong SendCursor { get; set; }

    public ulong Acked => (ulong)Interlocked.Read(ref _acked);

    public SubscriptionState State => _state;

    /// <summary>backlog 开始持续 ≥ 高水位的时刻（Stopwatch 时间戳，0 表示没有）。</summary>
    public long LaggingSince { get; set; }

    /// <summary>最近一次 attach 或 focus 的顺序号：尺寸所有者按"最近获得焦点者"选择（04 §9.2）。</summary>
    public long FocusOrder { get; set; }

    public bool IsLiveView => Mode == SubscriptionMode.View && _state == SubscriptionState.Live;

    public void Start(ulong cursor, byte[]? snapshot, SnapshotFormat format)
    {
        SendCursor = cursor;
        Interlocked.Exchange(ref _acked, (long)cursor);
        _snapshot = snapshot;
        _snapshotFormat = format;
    }

    public void OnAck(ulong seq)
    {
        long current;
        do
        {
            current = Interlocked.Read(ref _acked);
            if ((long)seq <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _acked, (long)seq, current) != current);
        Session.OnAcked();
        Client.Connection.Wake();
    }

    public void MarkResync() => _state = SubscriptionState.Resync;

    public void MarkDetached() => _state = SubscriptionState.Detached;

    public int TryProduce(Span<byte> frame)
    {
        if (_state != SubscriptionState.Live)
        {
            return 0;
        }
        if (_snapshot is { } snapshot)
        {
            // 快照字节不计入 backlog（§7.2 第 5 条），在 Output 之前按序发完
            var n = Math.Min(FrameLimits.MaxDataChunk, snapshot.Length - _snapshotOffset);
            var last = _snapshotOffset + n == snapshot.Length;
            new FrameHeader(DataFrames.SnapshotHeader + n, FrameType.SnapshotChunk, FrameFlags.None, Session.Id).Write(frame);
            DataFrames.WriteSnapshotHeader(frame[FrameLimits.HeaderSize..], AttachId, last, _snapshotFormat, (ulong)_snapshotOffset);
            snapshot.AsSpan(_snapshotOffset, n).CopyTo(frame[(FrameLimits.HeaderSize + DataFrames.SnapshotHeader)..]);
            _snapshotOffset += n;
            if (last)
            {
                _snapshot = null;
            }
            return FrameLimits.HeaderSize + DataFrames.SnapshotHeader + n;
        }
        if (Mode != SubscriptionMode.View)
        {
            return 0;
        }
        var log = Session.Log;
        lock (log.Sync)
        {
            if (SendCursor - Acked >= FlowControl.HighWater)
            {
                return 0;
            }
            var n = log.WriteFrame(SendCursor, Session.Id, frame, out var next);
            if (n < 0)
            {
                Session.Resync(this, "ringOverflow");
                return 0;
            }
            SendCursor = next;
            return n;
        }
    }
}

/// <summary>流控参数（04 §7.1）。</summary>
internal static class FlowControl
{
    public const ulong HighWater = 256 * 1024;
    public const ulong LowWater = 64 * 1024;
    /// <summary>慢订阅者降级前的等待（测试可缩短）。</summary>
    public static TimeSpan LaggingTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public static bool Elapsed(long since, TimeSpan span) => since != 0 && Stopwatch.GetElapsedTime(since) >= span;
}

/// <summary>可以 await 的开关：打开时 WaitAsync 立即完成。</summary>
internal sealed class AsyncGate
{
    private TaskCompletionSource _tcs = NewOpen();

    public bool IsOpen => _tcs.Task.IsCompleted;

    public Task WaitAsync() => Volatile.Read(ref _tcs).Task;

    public void Open() => Volatile.Read(ref _tcs).TrySetResult();

    public void Close()
    {
        if (IsOpen)
        {
            Volatile.Write(ref _tcs, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }
    }

    private static TaskCompletionSource NewOpen()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }
}
