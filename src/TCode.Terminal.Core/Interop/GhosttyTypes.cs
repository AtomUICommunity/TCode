using System.Runtime.InteropServices;

namespace TCode.Terminal.Core.Interop;

// libghostty-vt 的枚举都以 int 为底层类型（types.h 的 GHOSTTY_ENUM_TYPED）。
// 结构体布局在测试中与 ghostty_type_json() 的清单逐字段比对（GhosttyLayoutTests）。

internal enum GhosttyResult
{
    Success = 0,
    OutOfMemory = -1,
    InvalidValue = -2,
    OutOfSpace = -3,
    NoValue = -4,
    IoError = -5,
    LimitExceeded = -6,
    Rejected = -7,
}

internal enum GhosttyTerminalOption
{
    Userdata = 0,
    WritePty = 1,
    Bell = 2,
    TitleChanged = 5,
    GlyphProtocol = 24,
    PwdChanged = 25,
    ScrollbackMaxBytes = 27,
    ScrollbackMaxLines = 28,
    ContinuationMaxBytes = 31,
    ClipboardRead = 38,
}

internal enum GhosttyTerminalData
{
    Cols = 1,
    Rows = 2,
    CursorX = 3,
    CursorY = 4,
    ActiveScreen = 6,
    CursorVisible = 7,
    Title = 12,
    Pwd = 13,
    TotalRows = 14,
    ScrollbackRows = 15,
    Mode = 37,
    VtGround = 38,
}

internal enum GhosttySnapshotDecoderOption
{
    MaxContinuationBytes = 0,
    RetainContinuation = 1,
}

internal enum GhosttyRenderStateData
{
    Cols = 1,
    Rows = 2,
    Dirty = 3,
    RowIterator = 4,
    ColorBackground = 5,
    ColorForeground = 6,
    CursorVisualStyle = 10,
    CursorVisible = 11,
    CursorViewportHasValue = 14,
    CursorViewportX = 15,
    CursorViewportY = 16,
}

internal enum GhosttyRenderStateRowData
{
    Cells = 3,
    ViewportY = 6,
}

internal enum GhosttyRenderStateRowCellsData
{
    Raw = 1,
    BgColor = 5,
    FgColor = 6,
    GraphemesUtf8 = 9,
}

internal enum GhosttyCellData
{
    Wide = 3,
}

/// <summary>C: GhosttyColorRgb。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyColorRgb
{
    public byte R, G, B;
}

/// <summary>C: GhosttyBuffer（调用方提供的输出缓冲区）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct GhosttyBuffer
{
    public byte* Ptr;
    public nuint Cap;
    public nuint Len;
}

internal enum GhosttyFormatterFormat
{
    Plain = 0,
    Vt = 1,
    Html = 2,
}

internal enum GhosttyBuildInfo
{
    Simd = 1,
    KittyGraphics = 2,
    Optimize = 4,
    VersionString = 5,
}

/// <summary>C: GhosttyString（借用的字节串）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct GhosttyString
{
    public byte* Ptr;
    public nuint Len;
}

/// <summary>C: GhosttyTerminalModeConfig（frozen layout）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyTerminalModeConfig
{
    public ushort Mode;
    public bool Value;
}

/// <summary>C: GhosttyFormatterScreenExtra（sized struct）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyFormatterScreenExtra
{
    public nuint Size;
    public bool Cursor;
    public bool Style;
    public bool Hyperlink;
    public bool Protection;
    public bool KittyKeyboard;
    public bool Charsets;
}

/// <summary>C: GhosttyFormatterTerminalExtra（sized struct）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyFormatterTerminalExtra
{
    public nuint Size;
    public bool Palette;
    public bool Modes;
    public bool ScrollingRegion;
    public bool Tabstops;
    public bool Pwd;
    public bool Keyboard;
    public GhosttyFormatterScreenExtra Screen;
}

/// <summary>C: GhosttyFormatterTerminalOptions（sized struct，按值传给 ghostty_formatter_terminal_new）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct GhosttyFormatterTerminalOptions
{
    public nuint Size;
    public GhosttyFormatterFormat Emit;
    public bool Unwrap;
    public bool Trim;
    public GhosttyFormatterTerminalExtra Extra;
    public void* Selection;

    public static GhosttyFormatterTerminalOptions Create(GhosttyFormatterFormat emit) => new()
    {
        Size = (nuint)sizeof(GhosttyFormatterTerminalOptions),
        Emit = emit,
        Extra = new GhosttyFormatterTerminalExtra
        {
            Size = (nuint)sizeof(GhosttyFormatterTerminalExtra),
            Screen = new GhosttyFormatterScreenExtra { Size = (nuint)sizeof(GhosttyFormatterScreenExtra) },
        },
    };
}
