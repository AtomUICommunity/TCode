using System.Text;
using TCode.Terminal.Core;
using TCode.Terminal.Pty;

// esctest2 无头驱动（11 §1.1.4，参照 Ghostty test/esctest/src/main.zig）：
// 在真实 PTY 中运行 esctest.py，输出全部写入 TCode 核心（一致性测试模式：DECRQCRA 校验和、VT520 DA、CSI 8 t），
// 核心的应答写回 PTY；结束后解析 esctest 日志，与 known-failures 比较：新增失败 → 退出码 1；已知失败通过了 → 警告。
//
// 用法：esctest-runner --esctest <esctest 目录> [--known <known-failures.txt>] [--log <日志路径>] [-- esctest 参数...]

var esctestDir = Arg("--esctest") ?? throw new ArgumentException("--esctest <dir> is required");
var knownPath = Arg("--known");
var logPath = Path.GetFullPath(Arg("--log") ?? Path.Combine(Path.GetTempPath(), $"esctest-tcode-{Environment.ProcessId}.log"));
var passthrough = args.SkipWhile(a => a != "--").Skip(1).ToArray();

var (tally, failing) = await RunAsync(passthrough, logPath);
if (tally.StartsWith("*** 0 tests passed", StringComparison.Ordinal) && failing.Count > 10)
{
    // 偶发：esctest 启动时第一个查询的应答丢失，之后全部超时。整轮重跑一次
    Console.Error.WriteLine("every test failed; retrying the whole run once");
    (tally, failing) = await RunAsync(passthrough, logPath);
}
Console.WriteLine(tally);

var known = knownPath is null ? [] : new SortedSet<string>(File.ReadAllLines(knownPath).Select(l => l.Split('#')[0].Trim()).Where(l => l.Length > 0), StringComparer.Ordinal);
var unexpected = new List<string>();
foreach (var name in knownPath is null ? [] : failing.Except(known))  // 没有 known 列表时只报告
{
    // 新增失败先单独重跑一次：单独能通过的记为不稳定（警告），不阻断
    var (_, again) = await RunAsync([$"--include=^{System.Text.RegularExpressions.Regex.Escape(name)}$"], logPath + ".retry");
    if (again.Contains(name))
    {
        unexpected.Add(name);
    }
    else
    {
        Console.WriteLine($"flaky (passes when run alone): {name}");
    }
}
var fixedNow = knownPath is null ? [] : known.Except(failing).ToList();
foreach (var name in unexpected)
{
    Console.WriteLine($"NEW FAILURE: {name}");
}
foreach (var name in fixedNow)
{
    Console.WriteLine($"now passing (remove from known failures): {name}");
}

if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
{
    var sb = new StringBuilder();
    sb.AppendLine("### esctest2").AppendLine().AppendLine($"`{tally}`").AppendLine();
    sb.AppendLine($"- failing: {failing.Count} (known: {failing.Count(known.Contains)}, new: {unexpected.Count})");
    sb.AppendLine($"- known failures now passing: {fixedNow.Count}");
    foreach (var name in unexpected)
    {
        sb.AppendLine($"  - NEW: `{name}`");
    }
    File.AppendAllText(summary, sb.ToString());
}
if (Arg("--write-known") is { } writeKnown)
{
    File.WriteAllLines(writeKnown, failing);
    Console.WriteLine($"wrote {failing.Count} failing tests to {writeKnown}");
}
return unexpected.Count > 0 ? 1 : 0;

async Task<(string Tally, SortedSet<string> Failing)> RunAsync(string[] extra, string log)
{
    const int Columns = 80, Rows = 25;
    var size = new TerminalSize(Columns, Rows, 10, 20);
    File.Delete(log);
    string[] esctestArgs =
    [
        "esctest.py", "--expected-terminal=xterm", "--xterm-checksum=411", "--xterm-reverse-wrap=411", "--timeout=0.2",
        $"--logfile={log}", .. extra,
    ];
    var env = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
        .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? string.Empty);
    env["TERM"] = "xterm";

    await using var pty = LocalPty.Start(new PtyLaunch("python3", esctestArgs, null, Path.GetFullPath(esctestDir), env.ToList(), size));
    var effects = new Effects(pty);
    using var core = TerminalCore.Create(new TerminalCoreOptions(size) { Conformance = new ConformanceMode(), ScrollbackMaxLines = 0 }, effects);

    var started = DateTime.UtcNow;
    while (true)
    {
        var result = await pty.Output.ReadAsync();
        foreach (var segment in result.Buffer)
        {
            core.Write(segment.Span);
            // CSI 8 t：回调里不能改动终端，写完这一块再执行（与 xterm 一样，0 表示保持该维度）
            if (effects.TakeResize() is { } r)
            {
                var current = core.Size;
                var next = current with { Columns = r.Columns == 0 ? current.Columns : r.Columns, Rows = r.Rows == 0 ? current.Rows : r.Rows };
                core.Resize(next);
                await pty.ResizeAsync(next);
            }
        }
        pty.Output.AdvanceTo(result.Buffer.End);
        if (result.IsCompleted)
        {
            break;
        }
    }
    var exit = await pty.Exited;
    Console.Error.WriteLine($"esctest exited ({exit.ExitCode?.ToString() ?? exit.Signal}) after {(DateTime.UtcNow - started).TotalSeconds:F0} s; log: {log}");
    if (!File.Exists(log))
    {
        throw new InvalidOperationException("esctest wrote no log (python3 missing, or esctest crashed)");
    }

    var lines = File.ReadAllLines(log);
    var tally = (lines.LastOrDefault(l => l.Contains(" passed, ", StringComparison.Ordinal)) ?? "(no tally)").Trim();
    var failing = new SortedSet<string>(StringComparer.Ordinal);
    var inList = false;
    foreach (var line in lines)
    {
        if (line.StartsWith("Failing tests:", StringComparison.Ordinal))
        {
            inList = true;
            continue;
        }
        if (inList)
        {
            var name = line.Trim();
            if (name.Length == 0 || !name.Contains('.'))
            {
                inList = false;
                continue;
            }
            failing.Add(name);
        }
    }
    return (tally, failing);
}

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && Array.IndexOf(args, "--") is var dd && (dd < 0 || i < dd) ? args[i + 1] : null;
}

/// <summary>应答写回 PTY；CSI 8 t 记下来，回到主循环再执行。</summary>
sealed class Effects(ITerminalConnection pty) : ITerminalEffects
{
    private (int Columns, int Rows)? _resize;

    public void WriteToPty(ReadOnlySpan<byte> reply) => pty.TryWriteReply(reply);

    public void ResizeWindowRequested(int columns, int rows) => _resize = (columns, rows);

    public (int Columns, int Rows)? TakeResize()
    {
        var r = _resize;
        _resize = null;
        return r;
    }
}
