using System.Text;
using TKCode.Terminal.Core.Testing;
using Xunit;

namespace TKCode.Terminal.Core.Tests.Replay;

/// <summary>
/// 回放 → 网格（11 §1.1.1）。三类断言：
/// ① 与上游参照文本（xterm / Alacritty 的实际输出）一致，已知差异登记在 known-differences.txt；
/// ② 与 TKCode 自己的 .grid 黄金样本逐字节一致（回归锁定；TKCODE_UPDATE_GOLDEN=1 时写回）；
/// ③ 分块不变性：按 1、7、4096 字节与随机块回放，网格与整块回放相同。
/// </summary>
[Trait("Category", "Golden")]
public sealed class GoldenTests
{
    public static TheoryData<string> Fixtures => FixtureCatalog.Enumerate("xtermjs", "alacritty");

    private static readonly IReadOnlyDictionary<string, string> Known = FixtureCatalog.KnownDifferences("known-differences.txt");

    private static (TerminalCore Core, List<CastEvent> Events) Load(string id)
    {
        var (header, events) = Cast.Parse(FixtureCatalog.Read(id, ".cast"));
        return (Cast.CreateCore(header), events);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void MatchesReferenceText(string id)
    {
        var (core, events) = Load(id);
        using var _ = core;
        Cast.Replay(core, events);
        var actual = ViewportText(core);
        var expected = FixtureCatalog.Read(id, ".ref.txt");
        if (Known.TryGetValue(id, out var reason))
        {
            // 已知差异：仍然回放（不崩溃），差异消失时提示从列表删除
            if (actual == expected)
            {
                TestContext.Current.TestOutputHelper?.WriteLine($"{id} now matches the reference; remove it from known-differences.txt");
            }
            Assert.Skip($"known difference: {reason}");
        }
        if (actual != expected)
        {
            var path = FixtureCatalog.WriteActual(id, ".ref.txt", actual);
            Assert.Fail($"{id} differs from the upstream reference (actual written to {path}):\n{Diff(expected, actual)}");
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void MatchesGoldenGrid(string id)
    {
        var (core, events) = Load(id);
        using var _ = core;
        Cast.Replay(core, events);
        var actual = GridSnapshot.Capture(core);
        if (FixtureCatalog.UpdateGolden)
        {
            File.WriteAllText(Path.Combine(FixtureCatalog.SourceRoot(), id + ".grid"), actual);
            return;
        }
        Assert.True(FixtureCatalog.Exists(id, ".grid"), $"{id}.grid missing; run TKCODE_UPDATE_GOLDEN=1 dotnet test --filter Category=Golden");
        var expected = FixtureCatalog.Read(id, ".grid");
        if (actual != expected)
        {
            var path = FixtureCatalog.WriteActual(id, ".grid", actual);
            Assert.Fail($"{id}.grid changed (actual written to {path}):\n{Diff(expected, actual)}");
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void ChunkingDoesNotChangeTheResult(string id)
    {
        string Run(int? chunk)
        {
            var (core, events) = Load(id);
            using var _ = core;
            Cast.Replay(core, events, chunk, new Random(id.GetHashCode(StringComparison.Ordinal) & 0xFFFF));
            return GridSnapshot.Capture(core);
        }

        var whole = Run(null);
        foreach (var chunk in new[] { 1, 7, 4096, 0 })
        {
            var chunked = Run(chunk);
            Assert.True(whole == chunked, $"{id}: chunk size {(chunk == 0 ? "random" : chunk)} differs:\n{Diff(whole, chunked)}");
        }
    }

    /// <summary>视口各行文本（去掉行尾空白；宽字符第二列不输出；字形簇完整输出），与 .ref.txt 同格式。</summary>
    private static string ViewportText(TerminalCore core)
    {
        var frame = core.CaptureFresh();
        var sb = new StringBuilder();
        foreach (var row in frame.Rows)
        {
            var line = new StringBuilder();
            for (var x = 0; x < row.Cells.Length; x++)
            {
                var width = TCell.Width(row.Cells[x]);
                if (width == CellWidth.SpacerTail)
                {
                    continue;
                }
                line.Append(width == CellWidth.SpacerHead ? " " : row.TextAt(x) ?? " ");
            }
            sb.Append(line.ToString().TrimEnd()).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>逐行差异（只列出不同的行，最多 20 行）。</summary>
    private static string Diff(string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        var sb = new StringBuilder();
        var shown = 0;
        for (var i = 0; i < Math.Max(e.Length, a.Length) && shown < 20; i++)
        {
            var el = i < e.Length ? e[i] : "<none>";
            var al = i < a.Length ? a[i] : "<none>";
            if (el != al)
            {
                sb.Append($"  line {i + 1}\n    - {el}\n    + {al}\n");
                shown++;
            }
        }
        return sb.ToString();
    }
}
