using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using TKCode.Terminal.Core;
using TKCode.Terminal.Pty;

namespace TKCode.Terminal.Pty.Tests;

internal static class PtyTestSupport
{
    public static readonly TerminalSize Size = new(80, 24, 8, 16);

    public static IReadOnlyList<KeyValuePair<string, string>> Environment(params (string Key, string Value)[] extra)
    {
        var env = System.Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "");
        env["TERM"] = "xterm-256color";
        env.Remove("TKCODE_AGENT");
        foreach (var (key, value) in extra)
        {
            env[key] = value;
        }
        return env.ToList();
    }

    public static PtyLaunch Shell(string script, string? cwd = null) => OperatingSystem.IsWindows()
        ? new PtyLaunch("cmd.exe", ["/d", "/c", script], null, cwd, Environment(), Size)
        : new PtyLaunch("/bin/sh", ["-c", script], "sh", cwd, Environment(), Size);

    /// <summary>读取全部输出直到 EOF（或超时）。</summary>
    public static async Task<string> ReadAllAsync(PipeReader reader, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var sb = new StringBuilder();
        try
        {
            while (true)
            {
                var result = await reader.ReadAsync(cts.Token);
                foreach (var segment in result.Buffer)
                {
                    sb.Append(Encoding.UTF8.GetString(segment.Span));
                }
                reader.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        return sb.ToString();
    }

    /// <summary>
    /// 把 PTY 输出喂给一个终端核心（相当于镜像核心）：查询应答经 TryWriteReply 写回 PTY。
    /// 返回一个持续泵送的任务；predicate 在每批数据之后检查屏幕。
    /// </summary>
    public static async Task PumpUntilAsync(ITerminalConnection connection, TerminalCore core, Func<TerminalCore, bool> predicate, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var reader = connection.Output;
        while (!predicate(core))
        {
            ReadResult result;
            try
            {
                result = await reader.ReadAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"Screen never matched. Screen:\n{core.FormatPlainText()}");
            }
            foreach (var segment in result.Buffer)
            {
                core.Write(segment.Span);
            }
            reader.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted)
            {
                if (!predicate(core))
                {
                    throw new InvalidOperationException($"Output ended before the screen matched. Screen:\n{core.FormatPlainText()}");
                }
                return;
            }
        }
    }

    /// <summary>镜像核心的副作用：应答写回 PTY。</summary>
    public sealed class ReplyToConnection(ITerminalConnection connection) : ITerminalEffects
    {
        public void WriteToPty(ReadOnlySpan<byte> reply) => connection.TryWriteReply(reply);
        public void Bell() { }
        public void TitleChanged(string title) { }
    }
}
