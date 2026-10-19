using System.Text;
using TKCode.Terminal.Core;
using Xunit;

namespace TKCode.Terminal.Core.Tests;

/// <summary>
/// 增量捕获 vs 全量重建（02 §15）：同一字节流分块写入两个核心，一个持续增量捕获，另一个每步用新的
/// render state 全量捕获，逐格比较。专门捕获 libghostty 的 dirty 漏标（RoyalTerminal 修过的那类问题）。
/// </summary>
public sealed class DifferentialTests
{
    private static readonly string[] Fragments =
    [
        "hello", "世界", "\x1b[31m", "\x1b[1;4;38;2;10;200;30m", "\x1b[0m", "\r\n", "\x1b[2J", "\x1b[H", "\x1b[5;3H", "\x1b[K", "\x1b[1K",
        "\x1b[2K", "\x1b[3L", "\x1b[2M", "\x1b[4@", "\x1b[2P", "\x1b[3X", "\x1b[2;5r", "\x1b[r", "\x1bM", "\u001bD", "\x1b[?1049h", "\x1b[?1049l",
        "\x1b[44m   \x1b[49m", "😀", "é", "\x1b[?2027h", "\x1b[?2027l", "\x1b[3S", "\x1b[2T", "\t", "\b", "\x1b[?7l", "\x1b[?7h",
        "abcdefghijklmnopqrstuvwxyz0123456789", "\x1b]8;;https://example.com\x1b\\link\x1b]8;;\x1b\\", "\x1b]8;id=2;https://tkcode.dev/a\x1b\\b\x1b]8;;\x1b\\", "\x1b[7m inv \x1b[27m", "\x1b[?25l", "\x1b[?25h",
        "\x1b]133;A\x07$ ", "\x1b]133;C\x07", "\x1b]133;D;0\x07", "\x1b[3 q", "\x1b#8", "\x1b[10;20H*", "\u001bc",
    ];

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void IncrementalCaptureMatchesFullRebuild(int seed) => Run(seed, steps: 400);

    /// <summary>
    /// overlay O6：mode 2027 关闭时，零宽码点（U+200B、组合符）追加到前一个 cell。上游这条路径不标脏，
    /// 增量帧会沿用只有 "x" 的旧行。先捕获一帧让行进入增量缓存，再追加。
    /// </summary>
    [Theory]
    [InlineData("​")]
    [InlineData("́")]
    public void ZeroWidthCodepointAppendedToPreviousCellMarksRowDirty(string zeroWidth)
    {
        var options = new TerminalCoreOptions(new TerminalSize(20, 4, 8, 16));
        using var incremental = TerminalCore.Create(options);
        using var reference = TerminalCore.Create(options);
        foreach (var core in new[] { incremental, reference })
        {
            core.Write("\x1b[?2027l\x1b[2Jx"u8);
        }
        _ = incremental.CaptureFrame(force: true);

        var append = Encoding.UTF8.GetBytes(zeroWidth);
        incremental.Write(append);
        reference.Write(append);

        var actual = incremental.CaptureFrame(force: true)!;
        AssertSameScreen(reference.CaptureFresh(), actual, "zero-width append");
        Assert.Equal("x" + zeroWidth, actual.Rows[0].TextAt(0));
    }

    /// <summary>大规模扫描：TKCODE_DIFF_SEEDS=N 时跑 N 个种子 × 3000 步（每晚 / 升级 libghostty 时）。</summary>
    [Fact]
    public void IncrementalCaptureMatchesFullRebuildSweep()
    {
        var seeds = int.TryParse(Environment.GetEnvironmentVariable("TKCODE_DIFF_SEEDS"), out var n) ? n : 0;
        Assert.SkipWhen(seeds == 0, "set TKCODE_DIFF_SEEDS to run the sweep");
        for (var seed = 100; seed < 100 + seeds; seed++)
        {
            Run(seed, steps: 3000);
        }
    }

    private static void Run(int seed, int steps)
    {
        var random = new Random(seed);
        var options = new TerminalCoreOptions(new TerminalSize(30, 8, 8, 16));
        using var incremental = TerminalCore.Create(options);
        using var reference = TerminalCore.Create(options);
        for (var step = 0; step < steps; step++)
        {
            var bytes = Encoding.UTF8.GetBytes(Fragments[random.Next(Fragments.Length)]);
            incremental.Write(bytes);
            reference.Write(bytes);
            if (random.Next(40) == 0)
            {
                var size = new TerminalSize(random.Next(10, 40), random.Next(3, 12), 8, 16);
                incremental.Resize(size);
                reference.Resize(size);
            }
            if (random.Next(3) == 0)
            {
                continue;  // 多次写入后再捕获，覆盖累积的脏状态
            }
            var actual = incremental.CaptureFrame(force: true)!;
            var expected = reference.CaptureFresh();
            AssertSameScreen(expected, actual, $"seed {seed} step {step}");
        }
    }

    internal static void AssertSameScreen(FrameSnapshot expected, FrameSnapshot actual, string where)
    {
        Assert.Equal(expected.Columns, actual.Columns);
        Assert.Equal(expected.Rows.Length, actual.Rows.Length);
        Assert.True(expected.Cursor == actual.Cursor, $"{where}: cursor {expected.Cursor} vs {actual.Cursor}");
        for (var y = 0; y < expected.Rows.Length; y++)
        {
            var e = expected.Rows[y];
            var a = actual.Rows[y];
            Assert.True(e.Flags == a.Flags, $"{where}: row {y} flags {e.Flags} vs {a.Flags}");
            for (var x = 0; x < expected.Columns; x++)
            {
                var ec = e.Cells[x];
                var ac = a.Cells[x];
                var same = TCell.Codepoint(ec) == TCell.Codepoint(ac) && TCell.Width(ec) == TCell.Width(ac) &&
                           TCell.Semantic(ec) == TCell.Semantic(ac) && TCell.Hyperlink(ec) == TCell.Hyperlink(ac) &&
                           SameStyle(expected, e.Styles[TCell.StyleIndex(ec)], actual, a.Styles[TCell.StyleIndex(ac)]) && e.TextAt(x) == a.TextAt(x);
                Assert.True(same, $"{where}: cell ({x},{y}) expected {Describe(e, x)}, got {Describe(a, x)}\n" +
                                  $"expected row: {RowText(e)}\nactual row:   {RowText(a)}");
            }
        }
    }

    /// <summary>超链接 id 是各自捕获器的内部编号，按地址比较。</summary>
    private static bool SameStyle(FrameSnapshot ef, ResolvedStyle es, FrameSnapshot af, ResolvedStyle @as) =>
        es with { HyperlinkId = 0 } == @as with { HyperlinkId = 0 } && ef.LinkOf(es) == af.LinkOf(@as);

    private static string Describe(RowSnapshot row, int x)
    {
        var c = row.Cells[x];
        return $"'{row.TextAt(x)}' width={TCell.Width(c)} semantic={TCell.Semantic(c)} link={TCell.Hyperlink(c)} style={row.Styles[TCell.StyleIndex(c)]}";
    }

    private static string RowText(RowSnapshot row) => string.Concat(Enumerable.Range(0, row.Cells.Length).Select(x => row.TextAt(x) ?? " "));
}
