using System.Text;
using TCode.Terminal.Core;
using Xunit;

namespace TCode.Terminal.Core.Tests;

/// <summary>选区、搜索、视口滚动与超链接（M1 阶段 D）。</summary>
public sealed class SelectionTests
{
    private static TerminalCore Create(int cols = 20, int rows = 4) => TerminalCore.Create(new TerminalCoreOptions(new TerminalSize(cols, rows, 8, 16)));

    private static void Write(TerminalCore core, string text) => core.Write(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void RangeSelectionProducesTextAndSpans()
    {
        using var core = Create();
        Write(core, "hello world\r\nsecond line");
        Assert.False(core.HasSelection);
        Assert.True(core.Select(SelectionRequest.Range(new ViewportPoint(6, 0), new ViewportPoint(5, 1))));
        Assert.True(core.HasSelection);
        Assert.Equal("world\nsecond", core.GetSelectionText());

        var frame = core.CaptureFrame(force: true)!;
        Assert.Equal([new CellSpan(0, 6, 19), new CellSpan(1, 0, 5)], frame.Selection);

        core.ClearSelection();
        Assert.Null(core.GetSelectionText());
        Assert.Empty(core.CaptureFrame()!.Selection);
    }

    [Fact]
    public void SelectionChangeAloneProducesNewFrame()
    {
        using var core = Create();
        Write(core, "abc def");
        core.CaptureFrame(force: true);
        Assert.Null(core.CaptureFrame());
        core.Select(SelectionRequest.Word(new ViewportPoint(5, 0)));
        var frame = core.CaptureFrame();
        Assert.NotNull(frame);
        Assert.Equal([new CellSpan(0, 4, 6)], frame.Selection);
        Assert.Equal("def", core.GetSelectionText());
    }

    [Fact]
    public void RectangleLineAndAllSelections()
    {
        using var core = Create();
        Write(core, "abcdef\r\nghijkl\r\nmnopqr");
        core.Select(SelectionRequest.Range(new ViewportPoint(1, 0), new ViewportPoint(3, 2), rectangle: true));
        Assert.Equal("bcd\nhij\nnop", core.GetSelectionText());
        Assert.Equal([new CellSpan(0, 1, 3), new CellSpan(1, 1, 3), new CellSpan(2, 1, 3)], core.CaptureFrame(force: true)!.Selection);

        core.Select(SelectionRequest.Line(new ViewportPoint(2, 1)));
        Assert.Equal("ghijkl", core.GetSelectionText());

        core.Select(SelectionRequest.All());
        Assert.Equal("abcdef\nghijkl\nmnopqr", core.GetSelectionText());
    }

    [Fact]
    public void CommandOutputSelectionUsesSemanticMarks()
    {
        using var core = Create(30, 6);
        Write(core, "\x1b]133;A\a$ \x1b]133;B\als\r\n\x1b]133;C\afile1\r\nfile2\r\n\x1b]133;D;0\a\x1b]133;A\a$ ");
        Assert.True(core.Select(SelectionRequest.CommandOutput(new ViewportPoint(0, 1))));
        Assert.Equal("file1\nfile2", core.GetSelectionText()?.TrimEnd('\n'));
    }

    [Fact]
    public void ScrollViewportMovesThroughScrollback()
    {
        using var core = Create(10, 3);
        for (var i = 0; i < 10; i++)
        {
            Write(core, $"line{i}\r\n");
        }
        var bottom = core.CaptureFrame(force: true)!;
        Assert.Equal(bottom.Scrollbar.Total - bottom.Scrollbar.Length, bottom.Scrollbar.Offset);

        core.ScrollViewport(ViewportScroll.Top);
        var top = core.CaptureFrame()!;
        Assert.Equal(0, top.Scrollbar.Offset);
        Assert.Equal("line0", RowText(top.Rows[0]));

        core.ScrollViewport(ViewportScroll.Lines(2));
        Assert.Equal("line2", RowText(core.CaptureFrame()!.Rows[0]));

        core.ScrollViewport(ViewportScroll.ToRow(5));
        Assert.Equal("line5", RowText(core.CaptureFrame()!.Rows[0]));

        core.ScrollViewport(ViewportScroll.Bottom);
        Assert.Equal(bottom.Scrollbar.Offset, core.CaptureFrame()!.Scrollbar.Offset);
    }

    [Fact]
    public void SelectionSpansFollowViewportScroll()
    {
        using var core = Create(10, 3);
        for (var i = 0; i < 10; i++)
        {
            Write(core, $"line{i}\r\n");
        }
        core.ScrollViewport(ViewportScroll.Top);
        core.Select(SelectionRequest.Line(new ViewportPoint(0, 1)));
        Assert.Equal("line1", core.GetSelectionText());
        Assert.Equal([new CellSpan(1, 0, 4)], core.CaptureFrame()!.Selection);  // 行选区止于最后一个非空格
        core.ScrollViewport(ViewportScroll.Bottom);
        Assert.Empty(core.CaptureFrame()!.Selection);  // 选区在视口之外
        Assert.Equal("line1", core.GetSelectionText());
    }

    [Fact]
    public void SearchFindsMatchesAndHighlightsViewport()
    {
        using var core = Create(20, 3);
        for (var i = 0; i < 6; i++)
        {
            Write(core, $"item {i} foo\r\n");
        }
        var search = core.CreateSearch();
        search.Needle = "foo";
        search.Update();
        Assert.Equal(6, search.TotalMatches);

        var frame = core.CaptureFrame(force: true)!;
        Assert.NotNull(frame.Search);
        Assert.Equal(6, frame.Search.TotalMatches);
        Assert.All(frame.Search.Matches, m => Assert.Equal((7, 9), (m.StartColumn, m.EndColumn)));
        Assert.NotEmpty(frame.Search.Matches);

        search.Next();
        Assert.Equal(0, search.SelectedIndex);
        search.Next();
        Assert.Equal(1, search.SelectedIndex);
        var selected = core.CaptureFrame()!;
        Assert.Single(selected.Search!.Selected);

        search.Needle = "";
        Assert.Null(core.CaptureFrame()!.Search);
        core.EndSearch();
    }

    [Fact]
    public void HyperlinksAreResolvedIntoLinkTable()
    {
        using var core = Create();
        Write(core, "a\x1b]8;;https://example.com/x\x1b\\link\x1b]8;;\x1b\\ \x1b]8;;https://tcode.dev\x1b\\t\x1b]8;;\x1b\\");
        var frame = core.CaptureFrame(force: true)!;
        var row = frame.Rows[0];
        Assert.True(row.Flags.HasFlag(RowFlags.HasHyperlink));
        Assert.Null(frame.LinkOf(row.Styles[TCell.StyleIndex(row.Cells[0])]));
        for (var x = 1; x <= 4; x++)
        {
            Assert.True(TCell.Hyperlink(row.Cells[x]));
            Assert.Equal("https://example.com/x", frame.LinkOf(row.Styles[TCell.StyleIndex(row.Cells[x])]));
        }
        Assert.Equal("https://tcode.dev", frame.LinkOf(row.Styles[TCell.StyleIndex(row.Cells[6])]));
        Assert.Equal(["https://example.com/x", "https://tcode.dev"], frame.Links);
        Assert.Equal("https://tcode.dev", core.HyperlinkAt(new ViewportPoint(6, 0)));
        Assert.Null(core.HyperlinkAt(new ViewportPoint(5, 0)));
    }

    private static string RowText(RowSnapshot row) => string.Concat(Enumerable.Range(0, row.Cells.Length).Select(x => row.TextAt(x) ?? " ")).TrimEnd();
}
