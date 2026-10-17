using TCode.Terminal.Core.Testing;
using Xunit;

namespace TCode.Terminal.Core.Tests.Fuzz;

/// <summary>每个 fuzz 输入之后检查的不变量（11 §1.1.5）。</summary>
internal static class Invariants
{
    public static void Check(TerminalCore core, string where, bool snapshotRoundTrip = true)
    {
        var size = core.Size;
        var (col, row) = core.Cursor;
        Assert.True(col >= 0 && col < size.Columns && row >= 0 && row < size.Rows, $"{where}: cursor ({col},{row}) outside {size.Columns}x{size.Rows}");

        var incremental = core.CaptureFrame(force: true)!;
        var fresh = core.CaptureFresh();
        DifferentialTests.AssertSameScreen(fresh, incremental, where);
        foreach (var r in fresh.Rows)
        {
            for (var x = 0; x < r.Cells.Length; x++)
            {
                var w = TCell.Width(r.Cells[x]);
                if (w == CellWidth.Wide)
                {
                    Assert.True(x + 1 < r.Cells.Length && TCell.Width(r.Cells[x + 1]) == CellWidth.SpacerTail, $"{where}: wide cell at {x} without spacer tail");
                }
                else if (w == CellWidth.SpacerTail)
                {
                    Assert.True(x > 0 && TCell.Width(r.Cells[x - 1]) == CellWidth.Wide, $"{where}: spacer tail at {x} without wide head");
                }
            }
        }

        if (snapshotRoundTrip)
        {
            // 视口位置不属于终端状态（不进快照），比较前都回到底部
            core.ScrollViewport(ViewportScroll.Bottom);
            using var restored = TerminalCore.Restore(core.EncodeSnapshot(), new TerminalCoreOptions(size));
            var expected = SnapshotNormalize(GridSnapshot.Capture(core));
            var actual = SnapshotNormalize(GridSnapshot.Capture(restored));
            Assert.True(expected == actual, $"{where}: snapshot round trip changed the grid\n{LineDiff(expected, actual)}");
        }
    }

    /// <summary>
    /// libghostty 快照（b3dc3d7）已知不保存的状态，比较时去掉：mode 2026（同步输出，临时状态，恢复后立即绘制即可）、
    /// 直接以 <c>CSI ? 12 h/l</c> 设置的光标闪烁（经 DECSCUSR 设置的会保存）。见 13 §5 的 overlay 候选。
    /// </summary>
    private static string SnapshotNormalize(string grid) =>
        System.Text.RegularExpressions.Regex.Replace(grid, @" (2026|blink)=[01]", "");

    private static string LineDiff(string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        var lines = new List<string>();
        for (var i = 0; i < Math.Max(e.Length, a.Length) && lines.Count < 24; i++)
        {
            var el = i < e.Length ? e[i] : "<none>";
            var al = i < a.Length ? a[i] : "<none>";
            if (el != al)
            {
                lines.Add($"- {el}\n+ {al}");
            }
        }
        return string.Join('\n', lines);
    }
}
