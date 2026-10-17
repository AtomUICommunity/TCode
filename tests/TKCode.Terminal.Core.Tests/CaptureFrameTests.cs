using System.Text;
using TKCode.Terminal.Core;
using Xunit;

namespace TKCode.Terminal.Core.Tests;

public sealed class CaptureFrameTests
{
    private static TerminalCore Create(int cols = 10, int rows = 3) => TerminalCore.Create(new TerminalCoreOptions(new TerminalSize(cols, rows, 8, 16)));

    [Fact]
    public void CapturesCellsStylesAndCursor()
    {
        using var core = Create();
        core.Write(Encoding.UTF8.GetBytes("a中\x1b[1;31mR\x1b[0m\r\n\x1b[44m \x1b[0m\x1b[4:3;38;2;1;2;3mu"));
        var frame = core.CaptureFrame()!;

        Assert.Equal(10, frame.Columns);
        var row = frame.Rows[0];
        Assert.Equal('a', (char)TCell.Codepoint(row.Cells[0]));
        Assert.Equal(("中", CellWidth.Wide), (row.TextAt(1), TCell.Width(row.Cells[1])));
        Assert.Equal(CellWidth.SpacerTail, TCell.Width(row.Cells[2]));
        var red = row.Styles[TCell.StyleIndex(row.Cells[3])];
        Assert.Equal(TerminalColor.Palette(1), red.Foreground);
        Assert.True(red.Flags.HasFlag(StyleFlags.Bold));
        Assert.Equal(0, TCell.StyleIndex(row.Cells[0]));

        var second = frame.Rows[1];
        Assert.Equal(TerminalColor.Palette(4), second.Styles[TCell.StyleIndex(second.Cells[0])].Background);
        var curly = second.Styles[TCell.StyleIndex(second.Cells[1])];
        Assert.Equal((UnderlineStyle.Curly, TerminalColor.Rgb(1, 2, 3)), (curly.Underline, curly.Foreground));
        Assert.Equal((2, 1, true), (frame.Cursor.Column, frame.Cursor.Row, frame.Cursor.Visible));
        Assert.Equal(new TerminalRgb(255, 255, 255) == frame.Palette.Foreground || frame.Palette.Foreground != default, true);
    }

    [Fact]
    public void GraphemeClustersAreCaptured()
    {
        using var core = Create();
        core.Write(Encoding.UTF8.GetBytes("\x1b[?2027héx"));  // e + 组合重音
        var row = core.CaptureFrame()!.Rows[0];
        Assert.True(TCell.HasGrapheme(row.Cells[0]));
        Assert.Equal("é", row.TextAt(0));
        Assert.Equal("x", row.TextAt(1));
    }

    [Fact]
    public void UnchangedRowsAreSharedAcrossFrames()
    {
        using var core = Create(rows: 4);
        core.Write("one\r\ntwo\r\nthree"u8);
        var first = core.CaptureFrame()!;
        Assert.Null(core.CaptureFrame());  // 没有变化

        core.Write("!"u8);
        var second = core.CaptureFrame()!;
        Assert.Same(first.Rows[0], second.Rows[0]);
        Assert.Same(first.Rows[1], second.Rows[1]);
        Assert.NotSame(first.Rows[2], second.Rows[2]);
        Assert.Equal(first.Rows[2].RowId, second.Rows[2].RowId);
        Assert.Equal(first.Rows[2].Version + 1, second.Rows[2].Version);
        Assert.Same(first.Palette, second.Palette);
    }

    [Fact]
    public void RowIdsFollowRowsWhenScrolling()
    {
        using var core = Create(rows: 3);
        core.Write("a\r\nb\r\nc"u8);
        var before = core.CaptureFrame()!;
        core.Write("\r\nd"u8);
        var after = core.CaptureFrame()!;
        Assert.Equal(before.Rows[1].RowId, after.Rows[0].RowId);
        Assert.Equal(before.Rows[2].RowId, after.Rows[1].RowId);
        Assert.Equal(2, after.Scrollbar.Offset - before.Scrollbar.Offset + 1);
    }
}
