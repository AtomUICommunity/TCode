using System.Buffers.Binary;

namespace TCode.Terminal.Protocol;

/// <summary>
/// 日志中改变终端状态的非输出记录（04 §6.2）。两个核心在同一位置执行相同的操作（01 §3.2 规则 1、5）。
/// 二进制载荷接在 <c>[u64 seq][u8 kind][3 B 保留]</c> 之后。
/// </summary>
public readonly record struct LogRecord
{
    public RecordKind Kind { get; init; }

    // Resize
    public ushort Cols { get; init; }
    public ushort Rows { get; init; }
    public ushort CellWidth { get; init; }
    public ushort CellHeight { get; init; }

    // Reset
    public bool Hard { get; init; }

    // Clear：bit0 screen、bit1 scrollback
    public byte ClearFlags { get; init; }

    // SetColors
    public PaletteDto? Palette { get; init; }

    public static LogRecord Resize(int cols, int rows, int cellWidth, int cellHeight) => new()
    {
        Kind = RecordKind.Resize, Cols = (ushort)cols, Rows = (ushort)rows, CellWidth = (ushort)cellWidth, CellHeight = (ushort)cellHeight,
    };

    public static LogRecord Reset(bool hard) => new() { Kind = RecordKind.Reset, Hard = hard };

    public static LogRecord Clear(bool screen, bool scrollback) => new()
    {
        Kind = RecordKind.Clear, ClearFlags = (byte)((screen ? 1 : 0) | (scrollback ? 2 : 0)),
    };

    public static LogRecord SetColors(PaletteDto palette) => new() { Kind = RecordKind.SetColors, Palette = palette };

    /// <summary>载荷长度（不含 12 字节的记录小头）。</summary>
    public int PayloadLength => Kind switch
    {
        RecordKind.Resize => 8,
        RecordKind.SetColors => 4 + 3 * 4 + 256 * 4 + 1,
        _ => 1,
    };

    public void WritePayload(Span<byte> d)
    {
        switch (Kind)
        {
            case RecordKind.Resize:
                BinaryPrimitives.WriteUInt16LittleEndian(d, Cols);
                BinaryPrimitives.WriteUInt16LittleEndian(d[2..], Rows);
                BinaryPrimitives.WriteUInt16LittleEndian(d[4..], CellWidth);
                BinaryPrimitives.WriteUInt16LittleEndian(d[6..], CellHeight);
                break;
            case RecordKind.Reset:
                d[0] = (byte)(Hard ? 1 : 0);
                break;
            case RecordKind.Clear:
                d[0] = ClearFlags;
                break;
            case RecordKind.SetColors:
                var p = Palette!;
                // mask：bit0 fg、bit1 bg、bit2 cursor、bit3 palette；末尾 1 字节为明暗（0 dark、1 light）
                BinaryPrimitives.WriteUInt32LittleEndian(d, p.Cursor is null ? 0b1011u : 0b1111u);
                Rgbx(d[4..], p.Foreground);
                Rgbx(d[8..], p.Background);
                Rgbx(d[12..], p.Cursor ?? 0);
                for (var i = 0; i < 256; i++)
                {
                    Rgbx(d[(16 + i * 4)..], i < p.Palette.Length ? p.Palette[i] : 0);
                }
                d[16 + 256 * 4] = (byte)(p.Scheme == "light" ? 1 : 0);
                break;
        }
    }

    public static LogRecord Read(RecordKind kind, ReadOnlySpan<byte> d)
    {
        switch (kind)
        {
            case RecordKind.Resize when d.Length >= 8:
                return Resize(BinaryPrimitives.ReadUInt16LittleEndian(d), BinaryPrimitives.ReadUInt16LittleEndian(d[2..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(d[4..]), BinaryPrimitives.ReadUInt16LittleEndian(d[6..]));
            case RecordKind.Reset when d.Length >= 1:
                return Reset(d[0] != 0);
            case RecordKind.Clear when d.Length >= 1:
                return new LogRecord { Kind = RecordKind.Clear, ClearFlags = d[0] };
            case RecordKind.SetColors when d.Length >= 16 + 256 * 4 + 1:
                var mask = BinaryPrimitives.ReadUInt32LittleEndian(d);
                var palette = new int[256];
                for (var i = 0; i < 256; i++)
                {
                    palette[i] = FromRgbx(d[(16 + i * 4)..]);
                }
                return SetColors(new PaletteDto(FromRgbx(d[4..]), FromRgbx(d[8..]), (mask & 4) != 0 ? FromRgbx(d[12..]) : null, palette,
                    d[16 + 256 * 4] == 1 ? "light" : "dark"));
            default:
                throw new ProtocolException($"malformed {kind} record");
        }
    }

    private static void Rgbx(Span<byte> d, int rgb)
    {
        d[0] = (byte)(rgb >> 16);
        d[1] = (byte)(rgb >> 8);
        d[2] = (byte)rgb;
        d[3] = 0;
    }

    private static int FromRgbx(ReadOnlySpan<byte> d) => (d[0] << 16) | (d[1] << 8) | d[2];
}
