using System.Buffers.Binary;

namespace TKCode.Terminal.Protocol;

/// <summary>帧类型（04 §3.2）。0x01–0x03 属于引导子集，跨主版本冻结。</summary>
public enum FrameType : byte
{
    Request = 0x01,
    Response = 0x02,
    Notification = 0x03,
    Output = 0x10,
    Record = 0x11,
    SnapshotChunk = 0x12,
    Input = 0x20,
    Ack = 0x21,
    Ping = 0x30,
    Pong = 0x31,
}

[Flags]
public enum FrameFlags : byte
{
    None = 0,

    /// <summary>接收方不认识该类型时可以丢弃。</summary>
    Ignorable = 1,
}

public static class FrameLimits
{
    public const int HeaderSize = 12;

    /// <summary>控制帧载荷上限。</summary>
    public const int MaxControlPayload = 1 << 20;

    /// <summary>数据帧（0x10–0x2F）载荷上限：64 KiB 数据 + 类型自己的小头。</summary>
    public const int MaxDataPayload = MaxDataChunk + 16;

    /// <summary>认证完成前的载荷上限。</summary>
    public const int MaxPreAuthPayload = 16 * 1024;

    /// <summary>一个数据帧最多携带的原始字节。</summary>
    public const int MaxDataChunk = 64 * 1024;

    public static bool IsData(FrameType type) => (byte)type is >= 0x10 and <= 0x2F && type != FrameType.Ack;

    public static bool IsKnown(FrameType type) => type is FrameType.Request or FrameType.Response or FrameType.Notification or FrameType.Output
        or FrameType.Record or FrameType.SnapshotChunk or FrameType.Input or FrameType.Ack or FrameType.Ping or FrameType.Pong;
}

/// <summary>
/// 12 字节帧头，全部小端（04 §3.1）：<c>[u32 length][u8 type][u8 flags][u16 reserved][u32 sessionId]</c>，
/// length = 8 + 载荷长度。
/// </summary>
public readonly record struct FrameHeader(int PayloadLength, FrameType Type, FrameFlags Flags, uint SessionId)
{
    public void Write(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)(PayloadLength + 8));
        destination[4] = (byte)Type;
        destination[5] = (byte)Flags;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], SessionId);
    }

    /// <summary>解析帧头并校验长度、保留位、类型。不合法时抛 <see cref="ProtocolException"/>（调用方以 ProtocolError 关闭连接，不尝试恢复同步）。</summary>
    public static FrameHeader Read(ReadOnlySpan<byte> source, bool authenticated)
    {
        var length = BinaryPrimitives.ReadUInt32LittleEndian(source);
        if (length < 8)
        {
            throw new ProtocolException($"frame length {length} < 8");
        }
        var type = (FrameType)source[4];
        var flags = (FrameFlags)source[5];
        if (((byte)flags & ~(byte)FrameFlags.Ignorable) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(source[6..]) != 0)
        {
            throw new ProtocolException("reserved frame bits set");
        }
        var payload = length - 8;
        var limit = !authenticated ? FrameLimits.MaxPreAuthPayload : FrameLimits.IsData(type) ? FrameLimits.MaxDataPayload : FrameLimits.MaxControlPayload;
        if (payload > (uint)limit)
        {
            throw new ProtocolException($"frame payload {payload} exceeds the limit {limit} for {type}");
        }
        return new FrameHeader((int)payload, type, flags, BinaryPrimitives.ReadUInt32LittleEndian(source[8..]));
    }
}

/// <summary>帧或消息不合法：以 ProtocolError 关闭连接。</summary>
public sealed class ProtocolException(string message) : Exception(message);

/// <summary>数据帧的小头（04 §3.2、§6.2）。</summary>
public static class DataFrames
{
    public const int OutputHeader = 8;
    public const int RecordHeader = 12;
    public const int SnapshotHeader = 16;

    public static void WriteOutputHeader(Span<byte> d, ulong seq) => BinaryPrimitives.WriteUInt64LittleEndian(d, seq);

    public static ulong ReadSeq(ReadOnlySpan<byte> payload) =>
        payload.Length >= 8 ? BinaryPrimitives.ReadUInt64LittleEndian(payload) : throw new ProtocolException("data frame too short");

    public static void WriteRecordHeader(Span<byte> d, ulong seq, RecordKind kind)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(d, seq);
        d[8] = (byte)kind;
        d[9] = d[10] = d[11] = 0;
    }

    public static void WriteSnapshotHeader(Span<byte> d, uint attachId, bool last, SnapshotFormat format, ulong offset)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(d, attachId);
        d[4] = (byte)(last ? 1 : 0);
        d[5] = (byte)format;
        d[6] = d[7] = 0;
        BinaryPrimitives.WriteUInt64LittleEndian(d[8..], offset);
    }

    public static (uint AttachId, bool Last, SnapshotFormat Format, ulong Offset) ReadSnapshotHeader(ReadOnlySpan<byte> p) =>
        p.Length >= SnapshotHeader
            ? (BinaryPrimitives.ReadUInt32LittleEndian(p), (p[4] & 1) != 0, (SnapshotFormat)p[5], BinaryPrimitives.ReadUInt64LittleEndian(p[8..]))
            : throw new ProtocolException("snapshot chunk too short");
}

/// <summary>日志记录种类（04 §6.2）；Output 用单独的帧类型。</summary>
public enum RecordKind : byte
{
    Resize = 1,
    SetColors = 2,
    Reset = 3,
    Clear = 4,
}

public enum SnapshotFormat : byte
{
    Ghostty = 1,
    Vt = 2,
}
