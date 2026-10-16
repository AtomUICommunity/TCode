namespace TCode.Terminal.Core;

/// <summary>
/// 一帧屏幕的只读快照，渲染层只依赖它，不依赖 libghostty 的任何类型（01-architecture.md §6.1）。
/// M0 版本：每格保存文本与已解析的颜色。M1 换成 8 字节 cell + 行内样式表（02 §6.5、§8），接口形状不变。
/// </summary>
public sealed class FrameSnapshot
{
    public required long Generation { get; init; }
    public required int Columns { get; init; }
    public required RowSnapshot[] Rows { get; init; }
    public required TerminalCursor Cursor { get; init; }
    public required TerminalRgb Foreground { get; init; }
    public required TerminalRgb Background { get; init; }
}

public sealed class RowSnapshot(TerminalCell[] cells)
{
    public TerminalCell[] Cells { get; } = cells;
}

/// <summary>一格。宽字符占两格：第一格 <see cref="Width"/> 为 2，第二格是 <see cref="CellWidth.SpacerTail"/>。</summary>
public readonly record struct TerminalCell(string? Text, CellWidth Width, TerminalRgb? Foreground, TerminalRgb? Background);

public enum CellWidth : byte
{
    Narrow = 0,
    Wide = 1,
    SpacerTail = 2,
    SpacerHead = 3,
}

public readonly record struct TerminalRgb(byte R, byte G, byte B);

public enum CursorStyle
{
    Bar = 0,
    Block = 1,
    Underline = 2,
    BlockHollow = 3,
}

/// <summary>光标。<see cref="Visible"/> 为 false 时（DECTCEM 关闭或不在视口内）不绘制。</summary>
public readonly record struct TerminalCursor(int Column, int Row, bool Visible, CursorStyle Style);
