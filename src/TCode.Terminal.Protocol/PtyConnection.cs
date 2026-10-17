using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Serialization.Metadata;

namespace TCode.Terminal.Protocol;

/// <summary>数据帧的生产者（04 §3.4 的拉取式发送）：写线程轮转调用，每次最多生产一帧。</summary>
public interface IFrameProducer
{
    /// <summary>
    /// 有可发送的数据时把一帧（帧头 + 载荷）写入 <paramref name="frame"/> 并返回长度；没有时返回 0。
    /// <paramref name="frame"/> 的长度 ≥ 帧头 + <see cref="FrameLimits.MaxDataPayload"/>。在写线程上调用。
    /// </summary>
    int TryProduce(Span<byte> frame);
}

/// <summary>收到帧时的回调，在读线程上同步调用：不能阻塞；span 只在调用期间有效。</summary>
public interface IPtyConnectionHandler
{
    void OnRequest(PtyConnection connection, uint sessionId, Envelope request);

    void OnNotification(PtyConnection connection, uint sessionId, Envelope notification);

    void OnData(PtyConnection connection, in FrameHeader header, ReadOnlySpan<byte> payload);

    void OnClosed(PtyConnection connection, Exception? error);

    /// <summary>响应到达、在完成等待它的任务之前（读线程上同步调用）。用于必须先于后续数据帧完成的登记。</summary>
    void OnResponse(PtyConnection connection, uint id, Envelope response)
    {
    }
}

/// <summary>
/// 一条 IPC 连接（两端共用，04 §3）：读线程按帧头分派；写线程先发控制队列，再轮转数据生产者；
/// 请求 / 响应按 id 关联；空闲 5 s 发 Ping（§1.6）。
/// </summary>
public sealed class PtyConnection : IAsyncDisposable
{
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(5);

    private readonly Stream _stream;
    private readonly IPtyConnectionHandler _handler;
    private readonly ConcurrentQueue<byte[]> _urgent = new();
    private readonly ConcurrentQueue<byte[]> _control = new();
    private readonly List<IFrameProducer> _producers = [];
    private readonly Lock _producersLock = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<Envelope>> _pending = new();
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly CancellationTokenSource _closing = new();
    private readonly TaskCompletionSource<Exception?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _reader;
    private readonly Thread _writer;
    private uint _nextId;
    private int _producerCursor;
    private int _closed;
    private long _lastReceive = Stopwatch.GetTimestamp();
    private long _lastSend = Stopwatch.GetTimestamp();

    public PtyConnection(Stream stream, IPtyConnectionHandler handler, string name)
    {
        _stream = stream;
        _handler = handler;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = $"{name}-read" };
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = $"{name}-write" };
    }

    /// <summary>认证完成后放宽帧长度上限（认证前 16 KiB）。</summary>
    public bool Authenticated { get; set; }

    /// <summary>距离上次收到任何帧的时间。</summary>
    public TimeSpan SinceLastReceive => Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastReceive));

    /// <summary>连接结束：null 表示正常关闭。</summary>
    public Task<Exception?> Completion => _completion.Task;

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>附加数据（例如服务端的连接状态）。</summary>
    public object? Tag { get; set; }

    public void Start()
    {
        _reader.Start();
        _writer.Start();
    }

    // ---- 发送 ----

    public void SendFrame(FrameType type, uint sessionId, ReadOnlySpan<byte> payload, bool urgent = false)
    {
        var frame = new byte[FrameLimits.HeaderSize + payload.Length];
        new FrameHeader(payload.Length, type, FrameFlags.None, sessionId).Write(frame);
        payload.CopyTo(frame.AsSpan(FrameLimits.HeaderSize));
        (urgent ? _urgent : _control).Enqueue(frame);
        _wake.Release();
    }

    /// <summary>预留一个请求 id（配合 <see cref="IPtyConnectionHandler.OnResponse"/> 识别某个请求的响应）。</summary>
    public uint ReserveRequestId() => Interlocked.Increment(ref _nextId);

    public async Task<TResult> RequestAsync<TParams, TResult>(string method, TParams value, JsonTypeInfo<TParams> paramsType, JsonTypeInfo<TResult> resultType,
        uint sessionId = 0, CancellationToken cancellationToken = default, uint? requestId = null)
    {
        var id = requestId ?? Interlocked.Increment(ref _nextId);
        var envelope = await RequestRawAsync(id, method, Envelope.Request(id, method, value, paramsType), sessionId, cancellationToken)
            .ConfigureAwait(false);
        return envelope.Read(resultType);
    }

    private async Task<Envelope> RequestRawAsync(uint id, string method, byte[] json, uint sessionId, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        if (IsClosed)
        {
            _pending.TryRemove(id, out _);
            throw new IOException("connection closed");
        }
        SendFrame(FrameType.Request, sessionId, json);
        await using var registration = cancellationToken.Register(() =>
        {
            if (_pending.TryRemove(id, out var t) && t.TrySetCanceled(cancellationToken))
            {
                Notify(PtyMethods.Cancel, new SessionIdParams(id), PtyHostJsonContext.Default.SessionIdParams);
            }
        }).ConfigureAwait(false);
        var response = await tcs.Task.ConfigureAwait(false);
        if (response.Error is { } error)
        {
            throw new PtyHostException(error.Code, $"{method}: {error.Message}");
        }
        return response;
    }

    public void Notify<T>(string method, T value, JsonTypeInfo<T> type, uint sessionId = 0) =>
        SendFrame(FrameType.Notification, sessionId, Envelope.Notification(method, value, type));

    public void Respond<T>(uint id, T value, JsonTypeInfo<T> type, uint sessionId = 0) =>
        SendFrame(FrameType.Response, sessionId, Envelope.Response(id, value, type));

    public void RespondError(uint id, int code, string message, uint sessionId = 0) =>
        SendFrame(FrameType.Response, sessionId, Envelope.ErrorResponse(id, code, message));

    public void AddProducer(IFrameProducer producer)
    {
        lock (_producersLock)
        {
            _producers.Add(producer);
        }
        Wake();
    }

    public void RemoveProducer(IFrameProducer producer)
    {
        lock (_producersLock)
        {
            _producers.Remove(producer);
        }
    }

    /// <summary>生产者有新数据（或信用额度恢复）时调用。</summary>
    public void Wake() => _wake.Release();

    // ---- 线程 ----

    private void ReadLoop()
    {
        Exception? error = null;
        var header = new byte[FrameLimits.HeaderSize];
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024 + 64);
        try
        {
            while (true)
            {
                if (!ReadExactly(header))
                {
                    break;
                }
                var h = FrameHeader.Read(header, Authenticated);
                if (h.PayloadLength > buffer.Length)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = ArrayPool<byte>.Shared.Rent(h.PayloadLength);
                }
                if (!ReadExactly(buffer.AsSpan(0, h.PayloadLength)))
                {
                    throw new ProtocolException("connection closed mid-frame");
                }
                Interlocked.Exchange(ref _lastReceive, Stopwatch.GetTimestamp());
                Dispatch(h, buffer.AsSpan(0, h.PayloadLength));
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException && IsClosed)
        {
        }
        catch (Exception e)
        {
            error = e;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            Close(error);
        }
    }

    private bool ReadExactly(Span<byte> destination)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var n = _stream.Read(destination[read..]);
            if (n == 0)
            {
                if (read == 0 && destination.Length == FrameLimits.HeaderSize)
                {
                    return false;  // 帧边界上的 EOF：正常关闭
                }
                throw new ProtocolException("connection closed mid-frame");
            }
            read += n;
        }
        return true;
    }

    private void Dispatch(in FrameHeader h, ReadOnlySpan<byte> payload)
    {
        switch (h.Type)
        {
            case FrameType.Ping:
                SendFrame(FrameType.Pong, 0, payload, urgent: true);
                return;
            case FrameType.Pong:
                return;
            case FrameType.Request or FrameType.Response or FrameType.Notification:
                var envelope = Envelope.Parse(payload.ToArray());
                if (h.Type == FrameType.Response)
                {
                    if (envelope.Id is { } id)
                    {
                        _handler.OnResponse(this, id, envelope);
                        if (_pending.TryRemove(id, out var tcs))
                        {
                            tcs.TrySetResult(envelope);
                        }
                    }
                }
                else if (h.Type == FrameType.Request)
                {
                    if (envelope.Id is null || envelope.Method is null)
                    {
                        throw new ProtocolException("request without id or method");
                    }
                    _handler.OnRequest(this, h.SessionId, envelope);
                }
                else
                {
                    _handler.OnNotification(this, h.SessionId, envelope);
                }
                return;
            default:
                if (!FrameLimits.IsKnown(h.Type))
                {
                    if (h.Flags.HasFlag(FrameFlags.Ignorable))
                    {
                        return;
                    }
                    throw new ProtocolException($"unknown frame type 0x{(byte)h.Type:x2}");
                }
                _handler.OnData(this, h, payload);
                return;
        }
    }

    private void WriteLoop()
    {
        var scratch = new byte[FrameLimits.HeaderSize + FrameLimits.MaxDataPayload];
        var ping = new byte[16];
        try
        {
            while (!IsClosed)
            {
                var wrote = false;
                while (_urgent.TryDequeue(out var u) || _control.TryDequeue(out u))
                {
                    Send(u);
                    wrote = true;
                }
                // 数据：轮转，每个生产者一帧，之后回到控制队列
                IFrameProducer[] producers;
                lock (_producersLock)
                {
                    producers = _producers.Count == 0 ? [] : [.. _producers];
                }
                for (var i = 0; i < producers.Length; i++)
                {
                    var p = producers[(_producerCursor + i) % producers.Length];
                    var n = p.TryProduce(scratch);
                    if (n > 0)
                    {
                        Send(scratch.AsSpan(0, n));
                        wrote = true;
                        _producerCursor = (_producerCursor + i + 1) % producers.Length;
                        break;
                    }
                }
                if (wrote)
                {
                    continue;
                }
                _stream.Flush();
                if (!_wake.Wait(PingInterval, _closing.Token) && Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastSend)) >= PingInterval)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(ping, (ulong)Environment.TickCount64);
                    BinaryPrimitives.WriteInt64LittleEndian(ping[8..], Stopwatch.GetTimestamp());
                    SendFrame(FrameType.Ping, 0, ping, urgent: true);
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            Close(IsClosed ? null : e);
        }
        catch (Exception e)
        {
            Close(e);
        }
    }

    private void Send(ReadOnlySpan<byte> frame)
    {
        _stream.Write(frame);
        Interlocked.Exchange(ref _lastSend, Stopwatch.GetTimestamp());
    }

    // ---- 关闭 ----

    public void Close(Exception? error = null)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }
        _closing.Cancel();
        try
        {
            _stream.Dispose();
        }
        catch
        {
        }
        foreach (var (id, tcs) in _pending)
        {
            if (_pending.TryRemove(id, out _))
            {
                tcs.TrySetException(new IOException("connection closed", error));
            }
        }
        try
        {
            _handler.OnClosed(this, error);
        }
        finally
        {
            _completion.TrySetResult(error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // 先让写线程把已排队的帧发出去（最多 1 s），再关闭
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        while (!IsClosed && (!_control.IsEmpty || !_urgent.IsEmpty) && Stopwatch.GetTimestamp() < deadline)
        {
            await Task.Delay(5).ConfigureAwait(false);
        }
        Close();
        await _completion.Task.ConfigureAwait(false);
    }
}
