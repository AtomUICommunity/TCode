using System.Buffers;
using TCode.Terminal.Protocol;

namespace TCode.Terminal.Host.Sessions;

/// <summary>
/// 会话的输出日志（04 §6）：带位置序号的环。Output 记录按字节推进位置，其他记录各占 1。
/// actor 追加，各连接的写线程读取；全部操作在 <see cref="Sync"/> 锁内。
/// </summary>
internal sealed class OutputLog
{
    private readonly List<Entry> _entries = [];
    private int _first;
    private long _bytes;

    public object Sync { get; } = new();

    /// <summary>下一条记录的位置。</summary>
    public ulong Head { get; private set; }

    /// <summary>最早保留的位置。</summary>
    public ulong Start => _first < _entries.Count ? _entries[_first].Seq : Head;

    public long RetainedBytes => _bytes;

    /// <summary>追加输出，按 64 KiB 切成记录（一帧一条）。</summary>
    public void AppendOutput(ReadOnlySpan<byte> data)
    {
        lock (Sync)
        {
            while (!data.IsEmpty)
            {
                var n = Math.Min(data.Length, FrameLimits.MaxDataChunk);
                var buffer = ArrayPool<byte>.Shared.Rent(n);
                data[..n].CopyTo(buffer);
                _entries.Add(new Entry(Head, 0, buffer, n));
                Head += (ulong)n;
                _bytes += n;
                data = data[n..];
            }
        }
    }

    public ulong AppendRecord(in LogRecord record)
    {
        lock (Sync)
        {
            var n = record.PayloadLength;
            var buffer = ArrayPool<byte>.Shared.Rent(n);
            record.WritePayload(buffer);
            var seq = Head;
            _entries.Add(new Entry(seq, (byte)record.Kind, buffer, n));
            Head++;
            _bytes += n;
            return seq;
        }
    }

    /// <summary>
    /// 回收：环超过 <paramref name="ringBytes"/> 时，从最早的记录开始丢弃，但不丢弃位置 ≥ <paramref name="minAcked"/> 的记录
    /// （还有活跃订阅者没确认，§6.3）。
    /// </summary>
    public void Trim(long ringBytes, ulong minAcked)
    {
        lock (Sync)
        {
            while (_first < _entries.Count && _bytes > ringBytes)
            {
                var e = _entries[_first];
                if (e.End > minAcked)
                {
                    break;
                }
                ArrayPool<byte>.Shared.Return(e.Data);
                _bytes -= e.Length;
                _entries[_first] = default;
                _first++;
            }
            if (_first > 256 && _first * 2 > _entries.Count)
            {
                _entries.RemoveRange(0, _first);
                _first = 0;
            }
        }
    }

    /// <summary>
    /// 把位置 <paramref name="cursor"/> 处的一帧写入 <paramref name="frame"/>（帧头 + 载荷），返回帧长度与下一个游标。
    /// cursor 已落到环外时返回 -1；没有数据时返回 0。调用方持有 <see cref="Sync"/>。
    /// </summary>
    public int WriteFrame(ulong cursor, uint sessionId, Span<byte> frame, out ulong next)
    {
        next = cursor;
        if (cursor >= Head)
        {
            return 0;
        }
        if (cursor < Start)
        {
            return -1;
        }
        var e = _entries[Find(cursor)];
        if (e.Kind == 0)
        {
            var offset = (int)(cursor - e.Seq);
            var n = e.Length - offset;
            new FrameHeader(DataFrames.OutputHeader + n, FrameType.Output, FrameFlags.None, sessionId).Write(frame);
            DataFrames.WriteOutputHeader(frame[FrameLimits.HeaderSize..], cursor);
            e.Data.AsSpan(offset, n).CopyTo(frame[(FrameLimits.HeaderSize + DataFrames.OutputHeader)..]);
            next = cursor + (ulong)n;
            return FrameLimits.HeaderSize + DataFrames.OutputHeader + n;
        }
        new FrameHeader(DataFrames.RecordHeader + e.Length, FrameType.Record, FrameFlags.None, sessionId).Write(frame);
        DataFrames.WriteRecordHeader(frame[FrameLimits.HeaderSize..], cursor, (RecordKind)e.Kind);
        e.Data.AsSpan(0, e.Length).CopyTo(frame[(FrameLimits.HeaderSize + DataFrames.RecordHeader)..]);
        next = cursor + 1;
        return FrameLimits.HeaderSize + DataFrames.RecordHeader + e.Length;
    }

    /// <summary>包含位置 <paramref name="seq"/> 的记录下标（二分查找）。</summary>
    private int Find(ulong seq)
    {
        int lo = _first, hi = _entries.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (_entries[mid].Seq <= seq)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return lo;
    }

    public void Clear()
    {
        lock (Sync)
        {
            for (var i = _first; i < _entries.Count; i++)
            {
                ArrayPool<byte>.Shared.Return(_entries[i].Data);
            }
            _entries.Clear();
            _first = 0;
            _bytes = 0;
        }
    }

    private readonly record struct Entry(ulong Seq, byte Kind, byte[] Data, int Length)
    {
        public ulong End => Kind == 0 ? Seq + (ulong)Length : Seq + 1;
    }
}
