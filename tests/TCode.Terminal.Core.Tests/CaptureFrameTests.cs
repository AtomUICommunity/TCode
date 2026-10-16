using System.Text;
using TCode.Terminal.Core;
using Xunit;

namespace TCode.Terminal.Core.Tests;

public sealed class CaptureFrameTests
{
    [Fact]
    public void CapturesTextWidthColorsAndCursor()
    {
        using var core = TerminalCore.Create(new TerminalCoreOptions(new TerminalSize(10, 3, 8, 16)));
        core.Write(Encoding.UTF8.GetBytes("a中\x1b[31mR\x1b[0m\r\n\x1b[44m \x1b[0m"));
        var frame = core.CaptureFrame();

        Assert.Equal(10, frame.Columns);
        Assert.Equal(3, frame.Rows.Length);
        var row = frame.Rows[0].Cells;
        Assert.Equal("a", row[0].Text);
        Assert.Equal(("中", CellWidth.Wide), (row[1].Text, row[1].Width));
        Assert.Equal(CellWidth.SpacerTail, row[2].Width);
        Assert.Equal("R", row[3].Text);
        Assert.NotNull(row[3].Foreground);
        Assert.Null(row[0].Foreground);
        Assert.NotNull(frame.Rows[1].Cells[0].Background);
        Assert.Equal(new TerminalCursor(1, 1, true, frame.Cursor.Style), frame.Cursor);
        Assert.Equal(1, frame.Generation);
        Assert.Equal(2, core.CaptureFrame().Generation);
    }
}
