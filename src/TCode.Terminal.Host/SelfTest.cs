using System.IO.Pipelines;
using System.Text;
using TCode.Terminal.Core;
using TCode.Terminal.Pty;
using TCode.Terminal.Pty.Windows;

namespace TCode.Terminal.Host;

/// <summary>
/// 发布产物的自检：每一步都打印 PASS / FAIL，任一失败则退出码为 1。
/// 不依赖测试框架，所以能在 AOT 二进制和 CI 的 aot-smoke 作业里直接运行。
/// </summary>
internal static class SelfTest
{
    private static readonly TerminalSize Size = new(80, 24, 8, 16);

    public static async Task<int> RunAsync(TextWriter log)
    {
        var failures = 0;
        void Check(bool ok, string what)
        {
            log.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}");
            if (!ok)
            {
                failures++;
            }
        }

        log.WriteLine($"libghostty-vt {GhosttyRuntime.Version} (simd: {GhosttyRuntime.SimdEnabled})");

        // 1. 核心：解析、应答、快照往返（含 overlay O1）
        using (var core = TerminalCore.Create(new TerminalCoreOptions(Size)))
        {
            core.Write("hello \u001b[1mworld\u001b[0m\u001b[?9001h"u8);
            Check(core.FormatPlainText() == "hello world", "core parses text");
            Check(core.GetMode(9001), "overlay O1 tracks win32-input-mode");
            using var restored = TerminalCore.Restore(core.EncodeSnapshot(), new TerminalCoreOptions(Size));
            Check(restored.FormatPlainText() == "hello world" && restored.GetMode(9001), "snapshot round trip");

            core.Write("\r\n\u001b]8;;https://tcode.dev\u001b\\link\u001b]8;;\u001b\\"u8);
            core.Select(SelectionRequest.Word(new ViewportPoint(7, 0)));
            var search = core.CreateSearch();
            search.Needle = "link";
            search.Update();
            var frame = core.CaptureFrame(force: true)!;
            Check(core.GetSelectionText() == "world" && frame.Selection.Length == 1, "selection");
            Check(search.TotalMatches == 1 && frame.Search?.Matches.Length == 1, "search");
            Check(frame.LinkOf(frame.Rows[1].Styles[TCell.StyleIndex(frame.Rows[1].Cells[0])]) == "https://tcode.dev", "hyperlinks");
            core.EndSearch();
        }

        // 2. PTY：启动 shell，读输出，退出码
        try
        {
            var shell = OperatingSystem.IsWindows()
                ? new PtyLaunch("cmd.exe", ["/d", "/c", "echo self-test & exit 7"], null, null, Environment(), Size)
                : new PtyLaunch("/bin/sh", ["-c", "echo self-test; exit 7"], "sh", null, Environment(), Size);
            await using var pty = LocalPty.Start(shell);
            using var mirror = TerminalCore.Create(new TerminalCoreOptions(Size), new Replies(pty));
            await PumpAsync(pty, mirror, TimeSpan.FromSeconds(10));
            var exit = await pty.Exited.WaitAsync(TimeSpan.FromSeconds(10));
            Check(mirror.FormatPlainText().Contains("self-test"), "pty output reaches the core");
            Check(exit.ExitCode == 7, $"pty exit code (got {exit.ExitCode?.ToString() ?? exit.Signal ?? "unknown"})");
            if (OperatingSystem.IsWindows())
            {
                // 找不到随包 conpty.dll / OpenConsole.exe 时会静默退回系统 conhost（12 §9 故障排查）
                Check(LocalPty.ConPtyImplementation == ConPtyImplementation.Bundled,
                    $"bundled ConPTY (got {LocalPty.ConPtyImplementation}: {LocalPty.ConPtyDegradedReason})");
            }
        }
        catch (Exception ex)
        {
            Check(false, $"pty round trip: {ex.Message}");
        }

        // 3. 全屏程序：vim 进入备用屏、应答查询、:q! 退出（M0 验收；没有 vim 时跳过）
        var vim = OperatingSystem.IsWindows() ? null : new[] { "/usr/bin/vim", "/bin/vim", "/usr/local/bin/vim" }.FirstOrDefault(File.Exists);
        if (vim is null)
        {
            log.WriteLine("SKIP vim not installed");
        }
        else
        {
            try
            {
                await using var pty = LocalPty.Start(new PtyLaunch(vim, ["-u", "NONE", "-N", "-c", "set noswapfile", "-c", "call setline(1, 'tcode')"],
                    "vim", null, Environment(), Size));
                using var mirror = TerminalCore.Create(new TerminalCoreOptions(Size), new Replies(pty));
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var quitSent = false;
                var reader = pty.Output;
                while (true)
                {
                    var result = await reader.ReadAsync(cts.Token);
                    foreach (var segment in result.Buffer)
                    {
                        mirror.Write(segment.Span);
                    }
                    reader.AdvanceTo(result.Buffer.End);
                    if (!quitSent && mirror.GetMode(1049) && mirror.FormatPlainText().Contains("tcode"))
                    {
                        Check(true, "vim draws on the alternate screen");
                        await pty.WriteAsync("\u001b:q!\r"u8.ToArray());
                        quitSent = true;
                    }
                    if (result.IsCompleted)
                    {
                        break;
                    }
                }
                var exit = await pty.Exited.WaitAsync(TimeSpan.FromSeconds(10));
                Check(quitSent && exit.ExitCode == 0 && !mirror.GetMode(1049), "vim quits cleanly");
            }
            catch (Exception ex)
            {
                Check(false, $"vim: {ex.Message}");
            }
        }

        log.WriteLine(failures == 0 ? "SELF-TEST PASSED" : $"SELF-TEST FAILED ({failures})");
        return failures == 0 ? 0 : 1;
    }

    private static IReadOnlyList<KeyValuePair<string, string>> Environment()
    {
        var env = new List<KeyValuePair<string, string>>();
        foreach (System.Collections.DictionaryEntry e in System.Environment.GetEnvironmentVariables())
        {
            env.Add(new((string)e.Key, (string?)e.Value ?? string.Empty));
        }
        env.RemoveAll(p => p.Key == "TERM");
        env.Add(new("TERM", "xterm-256color"));
        return env;
    }

    private static async Task PumpAsync(ITerminalConnection connection, TerminalCore core, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var reader = connection.Output;
        while (true)
        {
            var result = await reader.ReadAsync(cts.Token);
            foreach (var segment in result.Buffer)
            {
                core.Write(segment.Span);
            }
            reader.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted)
            {
                return;
            }
        }
    }

    private sealed class Replies(ITerminalConnection connection) : ITerminalEffects
    {
        public void WriteToPty(ReadOnlySpan<byte> reply) => connection.TryWriteReply(reply);
        public void Bell() { }
        public void TitleChanged(string title) { }
    }
}
