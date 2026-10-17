using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using TKCode.Terminal.Core;
using TKCode.Terminal.Pty.Unix;
using TKCode.Terminal.Pty.Windows;
using Xunit;
using static TKCode.Terminal.Pty.Tests.PtyTestSupport;

namespace TKCode.Terminal.Pty.Tests;

/// <summary>
/// PTY 压力测试（03-pty.md §14 的 T10–T14）。默认按 CI 可接受的规模运行；设置 TKCODE_PTY_STRESS=1 时按文档规模
/// （1 GB、500 次快速开关、1000 次 resize），用于每晚任务。
/// </summary>
[Collection(nameof(PtyStressCollection))]
public sealed class PtyStressTests
{
    private static readonly bool Full = System.Environment.GetEnvironmentVariable("TKCODE_PTY_STRESS") == "1";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(Full ? 300 : 60);

    /// <summary>Pipe 的暂停阈值（03 §9：256 KiB）加一次读取块（64 KiB）。</summary>
    private const long MaxBacklog = 256 * 1024 + 64 * 1024;

    private static void Log(string message) => TestContext.Current.TestOutputHelper?.WriteLine(message);

    [Fact]
    public async Task T10_SlowConsumerAppliesBackpressure()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix shell syntax");
        var megabytes = Full ? 100 : 16;
        await using var pty = LocalPty.Start(Shell($"yes | head -c {megabytes * 1024L * 1024}"));
        await Task.Delay(2000, TestContext.Current.CancellationToken);  // 消费方暂停

        var first = await pty.Output.ReadAsync(TestContext.Current.CancellationToken);
        var backlog = first.Buffer.Length;
        pty.Output.AdvanceTo(first.Buffer.Start);  // 不消费：交给下面的 DrainAsync 一并检查
        Log($"backlog after 2 s pause: {backlog / 1024} KiB");
        Assert.InRange(backlog, 1, MaxBacklog);

        var tail = new List<byte>();
        var anomalies = new List<string>();
        duplicatedCr = 0;
        var total = await DrainAsync(pty.Output, tail, anomalies);
        // ONLCR：每个 "y\n" 变成 "y\r\n"。macOS 内核在输出队列满、写端阻塞时会重复输出 '\r'（"y\r\r\n"），
        // 不用本库的纯 Python 慢读者同样复现；显示上无差别，这里单独计数，其余任何偏差都算失败。
        Log($"duplicated CR (kernel ONLCR under backpressure): {duplicatedCr}");
        Assert.Empty(anomalies);
        Assert.True(megabytes * 1024L * 1024 / 2 * 3 == total - duplicatedCr, $"total {total}, anomalies: {string.Join("; ", anomalies)}; tail: {Encoding.ASCII.GetString(tail.ToArray()).Replace("\r", "\\r").Replace("\n", "\\n")}");
        Assert.Equal(0, (await pty.Exited.WaitAsync(Timeout)).ExitCode);
    }

    [Fact]
    public async Task T11_BulkOutputThroughput()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix shell syntax");
        var megabytes = Full ? 1024 : 64;
        GC.Collect();
        var rssBefore = Process.GetCurrentProcess().WorkingSet64;
        var watch = Stopwatch.StartNew();
        await using var pty = LocalPty.Start(Shell($"head -c {megabytes * 1024L * 1024} /dev/zero | tr '\\0' a"));
        var total = await DrainAsync(pty.Output);
        watch.Stop();
        GC.Collect();
        var rssDelta = Process.GetCurrentProcess().WorkingSet64 - rssBefore;
        Log($"{megabytes} MiB in {watch.Elapsed.TotalSeconds:F2} s = {megabytes / watch.Elapsed.TotalSeconds:F0} MiB/s; RSS delta {rssDelta / 1024 / 1024} MiB");
        Assert.Equal(megabytes * 1024L * 1024, total);
        Assert.True(rssDelta < 50L * 1024 * 1024, $"RSS grew by {rssDelta / 1024 / 1024} MiB");
    }

    [Fact]
    public async Task T12_ConcurrentSessions()
    {
        // 第一批用于预热：50 个并发会话会让线程池扩容，新线程自带 OS 句柄（Windows 上尤其明显），不算泄漏。
        // 基线取在第一批之后；真正的泄漏会随第二批继续增长
        var pids = await RunConcurrentBatchAsync();
        await WarmUpAsync();
        var fdsBefore = OpenDescriptors();
        var threadsBefore = ThreadCount();
        pids.AddRange(await RunConcurrentBatchAsync());
        Log($"threads: before {threadsBefore}, after {ThreadCount()}");
        await AssertNoLeaksAsync(fdsBefore, pids);
    }

    private static async Task<List<int>> RunConcurrentBatchAsync()
    {
        const int count = 50;
        var sessions = Enumerable.Range(0, count).Select(i => LocalPty.Start(Shell($"echo session-{i}"))).ToList();
        var outputs = await Task.WhenAll(sessions.Select(s => ReadAllAsync(s.Output, Timeout)));
        var exits = await Task.WhenAll(sessions.Select(s => s.Exited.WaitAsync(Timeout)));
        var pids = sessions.Select(Pid).ToList();
        foreach (var s in sessions)
        {
            await s.DisposeAsync();
        }
        for (var i = 0; i < count; i++)
        {
            Assert.Contains($"session-{i}", outputs[i]);
            Assert.Equal(0, exits[i].ExitCode);
        }
        return pids;
    }

    private static int ThreadCount()
    {
        using var self = Process.GetCurrentProcess();
        return self.Threads.Count;
    }

    [Fact]
    public async Task T13_RapidStartAndClose()
    {
        var iterations = Full ? 500 : 50;
        await WarmUpAsync();
        var fdsBefore = OpenDescriptors();
        var pids = new List<int>();
        for (var i = 0; i < iterations; i++)
        {
            var pty = LocalPty.Start(Shell(OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1 >nul" : "sleep 30"));
            pids.Add(Pid(pty));
            await pty.CloseAsync(TimeSpan.FromSeconds(2));
            await pty.Exited.WaitAsync(Timeout);
            await pty.DisposeAsync();
        }
        await AssertNoLeaksAsync(fdsBefore, pids);
    }

    [Fact]
    public async Task T14_ResizeStorm()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix only (SIGWINCH)");
        var resizes = Full ? 1000 : 200;
        await using var pty = LocalPty.Start(Shell("trap 'stty size' WINCH; echo ready; while :; do sleep 0.02; done"));
        var output = new StringBuilder();
        var reading = Task.Run(async () =>
        {
            while (true)
            {
                var r = await pty.Output.ReadAsync();
                foreach (var segment in r.Buffer)
                {
                    lock (output)
                    {
                        output.Append(Encoding.UTF8.GetString(segment.Span));
                    }
                }
                pty.Output.AdvanceTo(r.Buffer.End);
                if (r.IsCompleted)
                {
                    return;
                }
            }
        });
        await WaitForAsync(() => Snapshot(output).Contains("ready"));

        var random = new Random(14);
        for (var i = 0; i < resizes; i++)
        {
            await pty.ResizeAsync(new TerminalSize(random.Next(20, 200), random.Next(5, 60), 8, 16));
        }
        await pty.ResizeAsync(new TerminalSize(123, 45, 8, 16));
        await WaitForAsync(() => LastSize(Snapshot(output)) == "45 123");

        await pty.CloseAsync(TimeSpan.FromSeconds(2));
        await pty.Exited.WaitAsync(Timeout);
        await reading.WaitAsync(Timeout);
    }

    private static int Pid(ITerminalConnection c) => c switch
    {
        UnixPtyConnection u => u.ProcessId,
        WindowsPtyConnection w => w.ProcessId,
        _ => throw new NotSupportedException(c.Kind),
    };

    private static string Snapshot(StringBuilder sb)
    {
        lock (sb)
        {
            return sb.ToString();
        }
    }

    private static string? LastSize(string output) =>
        output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault(l => l.Length > 2 && char.IsDigit(l[0]) && l.Contains(' '));

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition not met");
            }
            await Task.Delay(20);
        }
    }

    private static long duplicatedCr;

    private static async Task<long> DrainAsync(PipeReader reader, List<byte>? tail = null, List<string>? anomalies = null, long offset = 0)
    {
        var phase = 0;
        using var cts = new CancellationTokenSource(Timeout);
        long total = 0;
        while (true)
        {
            var r = await reader.ReadAsync(cts.Token);
            total += r.Buffer.Length;
            if (anomalies is not null)
            {
                foreach (var segment in r.Buffer)
                {
                    foreach (var c in segment.Span)
                    {
                        var expected = "y\r\n"[phase];
                        if (c == expected)
                        {
                            phase = (phase + 1) % 3;
                        }
                        else if (c == '\r' && expected == '\n')
                        {
                            duplicatedCr++;
                        }
                        else if (anomalies.Count < 20)
                        {
                            anomalies.Add($"@{offset} got {c:x2} expected {(byte)expected:x2}");
                        }
                        offset++;
                    }
                }
            }
            if (tail is not null)
            {
                // 输出末尾的最多 64 字节（断言失败时显示）
                tail.AddRange(r.Buffer.Slice(r.Buffer.Length - Math.Min(64, r.Buffer.Length)).ToArray());
                tail.RemoveRange(0, Math.Max(0, tail.Count - 64));
            }
            reader.AdvanceTo(r.Buffer.End);
            if (r.IsCompleted)
            {
                return total;
            }
        }
    }

    /// <summary>当前进程打开的 fd 数（Unix）；Windows 用句柄数。</summary>
    private static int OpenDescriptors()
    {
        if (OperatingSystem.IsWindows())
        {
            // 托管 Thread 在终结前一直持有 OS 线程句柄：先回收，避免把已结束的读 / 写 / 等待线程算成泄漏
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            using var self = Process.GetCurrentProcess();
            return self.HandleCount;
        }
        return Directory.GetFileSystemEntries(OperatingSystem.IsLinux() ? "/proc/self/fd" : "/dev/fd").Length;
    }

    /// <summary>首次使用时创建的进程级单例（退出监视器、线程池线程等）不算泄漏：先跑一个会话再取基线。</summary>
    private static async Task WarmUpAsync()
    {
        await using var pty = LocalPty.Start(Shell("exit 0"));
        await ReadAllAsync(pty.Output, Timeout);
        await pty.Exited.WaitAsync(Timeout);
    }

    private static async Task AssertNoLeaksAsync(int fdsBefore, IReadOnlyList<int> pids)
    {
        // 退出监视线程与读线程在 Exited 之后才收尾：给一点时间
        var fds = 0;
        for (var i = 0; i < 50; i++)
        {
            fds = OpenDescriptors();
            if (fds <= fdsBefore + 4)
            {
                break;
            }
            await Task.Delay(100);
        }
        Log($"descriptors: before {fdsBefore}, after {fds}");
        Assert.True(fds <= fdsBefore + 4, $"descriptor leak: {fdsBefore} → {fds}");
        if (!OperatingSystem.IsWindows())
        {
            var zombies = Zombies(pids);
            Assert.True(zombies.Count == 0, $"zombie processes: {string.Join(", ", zombies)}");
        }
    }

    /// <summary>仍处于 Z 状态的进程（ps 查不到的已被回收）。</summary>
    private static List<int> Zombies(IReadOnlyList<int> pids)
    {
        var psi = new ProcessStartInfo("ps", ["-o", "pid=,stat=", "-p", string.Join(',', pids)]) { RedirectStandardOutput = true };
        using var ps = Process.Start(psi)!;
        var text = ps.StandardOutput.ReadToEnd();
        ps.WaitForExit();
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length == 2 && p[1].StartsWith('Z'))
            .Select(p => int.Parse(p[0]))
            .ToList();
    }
}

/// <summary>压力测试独占运行：fd / 句柄计数与吞吐不受其他并行测试干扰。</summary>
[CollectionDefinition(nameof(PtyStressCollection), DisableParallelization = true)]
public sealed class PtyStressCollection;
