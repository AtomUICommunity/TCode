using System.Text.Json;
using TCode.Terminal.Core.Tests.Replay;
using Xunit;

namespace TCode.Terminal.Core.Tests.Fuzz;

/// <summary>
/// fuzz（11 §1.1.5）：①上游 AFL++ 语料（PR 子集）经过 C# 门面回放；②结构化随机生成器，PR 固定 20 个种子 × 1 MiB，
/// TCODE_FUZZ_MINUTES=N 时用随机种子跑 N 分钟（每晚），失败时输出种子。每个输入之后检查 <see cref="Invariants"/>。
/// <para>原生崩溃会带走测试进程：目前在进程内运行（CI 中崩溃表现为测试运行中止，日志里有最后一个用例名）；子进程隔离留到需要时再做。</para>
/// </summary>
[Trait("Category", "Fuzz")]
public sealed class FuzzTests
{
    public static TheoryData<int> CorpusShards => new(Enumerable.Range(0, 8));

    private static readonly Lazy<List<(string Kind, string Name, byte[] Data)>> Corpus = new(() =>
    {
        var list = new List<(string, string, byte[])>();
        foreach (var line in File.ReadLines(Path.Combine(FixtureCatalog.OutputRoot, "fuzz", "corpus.jsonl")).Skip(1))
        {
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            list.Add((e.GetProperty("kind").GetString()!, e.GetProperty("name").GetString()!, Convert.FromBase64String(e.GetProperty("data").GetString()!)));
        }
        return list;
    });

    /// <summary>上游语料按 8 片并行回放。stream 语料的第一个字节是上游驱动的路径选择位，丢弃；osc 语料包上 OSC 头与终止符。</summary>
    [Theory]
    [MemberData(nameof(CorpusShards))]
    public void UpstreamCorpusKeepsInvariants(int shard)
    {
        var inputs = Corpus.Value.Where((_, i) => i % 8 == shard).ToList();
        Assert.NotEmpty(inputs);
        foreach (var (kind, name, data) in inputs)
        {
            var bytes = kind switch
            {
                "stream" => data.Length > 0 ? data[1..] : data,
                "osc" when data.Length > 0 => [.. "\x1b]"u8, .. data[1..], .. (data[0] % 3) switch { 0 => "\a"u8.ToArray(), 1 => "\x1b\\"u8.ToArray(), _ => [] }],
                _ => data,
            };
            foreach (var chunk in new[] { bytes.Length, 1 })
            {
                using var core = TerminalCore.Create(new TerminalCoreOptions(new TerminalSize(80, 24, 8, 16)));
                for (var i = 0; i < bytes.Length; i += Math.Max(1, chunk))
                {
                    core.Write(bytes.AsSpan(i, Math.Min(Math.Max(1, chunk), bytes.Length - i)));
                }
                Invariants.Check(core, $"{name} (chunk {chunk})");
            }
        }
    }

    public static TheoryData<int> Seeds => new(Enumerable.Range(1, 20));

    [Theory]
    [MemberData(nameof(Seeds))]
    public void GeneratedStreamsKeepInvariants(int seed) => RunGenerated(seed, totalBytes: 1 << 20);

    [Fact]
    [Trait("Category", "FuzzLong")]
    public void GeneratedStreamsLongRun()
    {
        var minutes = int.TryParse(Environment.GetEnvironmentVariable("TCODE_FUZZ_MINUTES"), out var m) ? m : 0;
        Assert.SkipWhen(minutes == 0, "set TCODE_FUZZ_MINUTES to run the long fuzz");
        var deadline = DateTime.UtcNow.AddMinutes(minutes);
        while (DateTime.UtcNow < deadline)
        {
            var seed = Random.Shared.Next();
            TestContext.Current.TestOutputHelper?.WriteLine($"seed {seed}");
            RunGenerated(seed, totalBytes: 4 << 20);
        }
    }

    /// <summary>一个种子：分批写入，穿插 resize、reset、视口滚动、选区、搜索；每批之后检查不变量。</summary>
    private static void RunGenerated(int seed, int totalBytes)
    {
        var generator = new VtGenerator(seed);
        var random = new Random(seed ^ 0x5eed);
        using var core = TerminalCore.Create(new TerminalCoreOptions(new TerminalSize(80, 24, 8, 16)) { ScrollbackMaxLines = 500 });
        var written = 0;
        var batch = 0;
        while (written < totalBytes)
        {
            var bytes = generator.Next(random.Next(256, 16 * 1024));
            core.Write(bytes);
            written += bytes.Length;
            switch (random.Next(12))
            {
                case 0:
                    core.Resize(new TerminalSize(random.Next(2, 200), random.Next(1, 80), 8, 16));
                    break;
                case 1:
                    core.ScrollViewport(ViewportScroll.Lines(random.Next(-50, 50)));
                    break;
                case 2:
                    var size = core.Size;
                    core.Select(SelectionRequest.Range(new ViewportPoint(random.Next(size.Columns), random.Next(size.Rows)),
                        new ViewportPoint(random.Next(size.Columns), random.Next(size.Rows)), random.Next(2) == 0));
                    _ = core.GetSelectionText();
                    break;
                case 3:
                    var search = core.CreateSearch();
                    search.Needle = "ab";
                    search.Update();
                    _ = core.CaptureFrame(force: true);
                    core.EndSearch();
                    break;
                case 4 when random.Next(4) == 0:
                    core.Reset();
                    break;
            }
            // 快照往返较慢：每 8 批做一次
            Invariants.Check(core, $"seed {seed} batch {batch}", snapshotRoundTrip: batch % 8 == 0);
            batch++;
        }
    }
}
