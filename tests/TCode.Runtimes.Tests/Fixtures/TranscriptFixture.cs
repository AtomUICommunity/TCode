using System.IO.Pipelines;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace TCode.Runtimes.Tests.Fixtures;

/// <summary>
/// 规格附录中的转录夹具：每行 <c>&gt; </c>（TCode 必须写出）或 <c>&lt; </c>（注入给 TCode）。
/// 直接从 docs/protocols/*.md 中提取，保证测试与规格同步。
/// </summary>
internal sealed record TranscriptLine(bool FromClient, string Json);

internal static class TranscriptFixture
{
    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TCode.slnx")))
            {
                dir = dir.Parent;
            }
            return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
    }

    /// <summary>取标题行包含 <paramref name="heading"/> 的小节中第一个 ```text 代码块。</summary>
    public static IReadOnlyList<TranscriptLine> Load(string specFile, string heading, string clientPrefix = "> ", string serverPrefix = "< ")
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot, "docs", "protocols", specFile));
        var start = text.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Fixture '{heading}' not found in {specFile}");
        var block = Regex.Match(text[start..], "```[a-z]*\\n(.*?)```", RegexOptions.Singleline).Groups[1].Value;
        return block.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.StartsWith(clientPrefix, StringComparison.Ordinal) ? new TranscriptLine(true, l[clientPrefix.Length..])
                : l.StartsWith(serverPrefix, StringComparison.Ordinal) ? new TranscriptLine(false, l[serverPrefix.Length..])
                : null)
            .OfType<TranscriptLine>()
            .ToArray();
    }
}

/// <summary>
/// 假的 Agent 进程：按夹具顺序执行——遇到 “&gt;” 行就读取 TCode 写出的下一行并按 JSON 语义比较，遇到 “&lt;” 行就写给 TCode。
/// </summary>
internal sealed class ScriptedPeer
{
    private readonly Pipe _toClient = new();
    private readonly Pipe _fromClient = new();
    private readonly StreamReader _reader;
    private readonly Stream _writer;

    public ScriptedPeer()
    {
        _reader = new StreamReader(_fromClient.Reader.AsStream(), Encoding.UTF8);
        _writer = _toClient.Writer.AsStream();
    }

    /// <summary>TCode 读取的流（Agent 的 stdout）。</summary>
    public Stream ClientInput => _toClient.Reader.AsStream();

    /// <summary>TCode 写入的流（Agent 的 stdin）。</summary>
    public Stream ClientOutput => _fromClient.Writer.AsStream();

    /// <summary>TCode 写出的全部行（用于失败时诊断）。</summary>
    public List<string> Written { get; } = [];

    public async Task RunAsync(IEnumerable<TranscriptLine> lines, Func<string, string>? adjustExpected = null, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        var index = 0;
        foreach (var line in lines)
        {
            index++;
            if (line.FromClient)
            {
                var actual = await _reader.ReadLineAsync(cts.Token)
                             ?? throw new InvalidOperationException($"Line {index}: TCode closed its output, expected {line.Json}");
                Written.Add(actual);
                var expected = adjustExpected?.Invoke(line.Json) ?? line.Json;
                Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)),
                    $"Line {index} differs.\nExpected: {expected}\nActual:   {actual}");
            }
            else
            {
                await _writer.WriteAsync(Encoding.UTF8.GetBytes(line.Json + "\n"), cts.Token);
                await _writer.FlushAsync(cts.Token);
            }
        }
    }

    /// <summary>读取 TCode 之后写出的一行（夹具之外的额外断言）。</summary>
    public async Task<string?> ReadExtraAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return await _reader.ReadLineAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public void CloseFromAgent() => _toClient.Writer.Complete();
}
