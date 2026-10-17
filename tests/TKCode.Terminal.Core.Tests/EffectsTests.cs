using System.Text;
using TKCode.Terminal.Core;
using Xunit;

namespace TKCode.Terminal.Core.Tests;

/// <summary>副作用回调与由核心作答的查询（02 §6.3、§7.2，10 §2–§3）。</summary>
public sealed class EffectsTests
{
    private sealed class Recorder : ITerminalEffects
    {
        public readonly StringBuilder Output = new();
        public readonly List<string> Events = [];
        public ClipboardReadDecision ReadDecision = ClipboardReadDecision.Denied;
        public PendingClipboardRead? LastRead;

        public void WriteToPty(ReadOnlySpan<byte> reply) => Output.Append(Encoding.UTF8.GetString(reply));
        public void Bell() => Events.Add("bell");
        public void TitleChanged(string title) => Events.Add($"title:{title}");
        public void WorkingDirectoryChanged(string raw) => Events.Add($"pwd:{raw}");
        public ClipboardWriteResult ClipboardWrite(ClipboardTarget target, string mime, ReadOnlySpan<byte> data)
        {
            Events.Add($"clip-write:{target}:{Encoding.UTF8.GetString(data)}");
            return ClipboardWriteResult.Success;
        }
        public ClipboardReadDecision ClipboardRead(in PendingClipboardRead request)
        {
            LastRead = request;
            Events.Add($"clip-read:{request.Target}");
            return ReadDecision;
        }
        public void Notification(string title, string body) => Events.Add($"notify:{title}|{body}");
        public void Progress(ProgressState state, int? percent) => Events.Add($"progress:{state}:{percent}");
        public void SemanticPrompt(in SemanticPromptEvent e) => Events.Add($"prompt:{e.Kind}");
        public void RenderHold(bool hold) => Events.Add($"hold:{hold}");
        public void FullReset() => Events.Add("reset");
        public void UnknownOsc(ReadOnlySpan<byte> payload, bool truncated) => Events.Add($"osc:{Encoding.UTF8.GetString(payload)}");
    }

    private static (TerminalCore Core, Recorder Effects) Create(TerminalCoreOptions? options = null)
    {
        var effects = new Recorder();
        return (TerminalCore.Create(options ?? new TerminalCoreOptions(new TerminalSize(80, 24, 9, 18)) { DeferClipboardRead = true }, effects), effects);
    }

    private static string Reply(TerminalCore core, Recorder r, string query)
    {
        r.Output.Clear();
        core.Write(Encoding.UTF8.GetBytes(query));
        return r.Output.ToString();
    }

    [Fact]
    public void AnswersDeviceQueries()
    {
        var (core, r) = Create();
        using (core)
        {
            Assert.Equal("\x1b[?62;22;52c", Reply(core, r, "\x1b[c"));
            Assert.Equal("\x1b[>1;10;0c", Reply(core, r, "\x1b[>c"));
            Assert.Equal("\x1bP>|TKCode\x1b\\", Reply(core, r, "\x1b[>q"));
            Assert.Equal("\x1b[8;24;80t", Reply(core, r, "\x1b[18t"));
            Assert.Equal("\x1b[6;18;9t", Reply(core, r, "\x1b[16t"));
            Assert.Equal("\x1b[4;432;720t", Reply(core, r, "\x1b[14t"));
            Assert.Equal("\x1b[?997;1n", Reply(core, r, "\x1b[?996n"));
            Assert.Equal("", Reply(core, r, "\x1b[21t"));  // 标题回报关闭
        }
    }

    [Fact]
    public void Da1OmitsOsc52WhenNotAdvertised()
    {
        var (core, r) = Create(new TerminalCoreOptions(new TerminalSize(80, 24, 9, 18)) { Replies = TerminalReplyProfile.Default with { AdvertiseOsc52 = false } });
        using (core)
        {
            Assert.Equal("\x1b[?62;22c", Reply(core, r, "\x1b[c"));
        }
    }

    [Fact]
    public void DefaultColorsAndSchemeAnswerQueries()
    {
        var (core, r) = Create();
        using (core)
        {
            var colors = Enumerable.Range(0, 256).Select(i => new TerminalRgb((byte)i, 0, 0)).ToArray();
            core.SetDefaultColors(new TerminalPalette(new TerminalRgb(0x11, 0x22, 0x33), new TerminalRgb(0xf0, 0xf0, 0xf0), null, colors), ColorScheme.Light);
            Assert.Equal("\x1b]10;rgb:1111/2222/3333\x07", Reply(core, r, "\x1b]10;?\x07"));
            Assert.Equal("\x1b]4;5;rgb:0505/0000/0000\x1b\\", Reply(core, r, "\x1b]4;5;?\x1b\\"));
            Assert.Equal("\x1b[?997;2n", Reply(core, r, "\x1b[?996n"));
        }
    }

    [Fact]
    public void TitlePwdBellNotificationProgress()
    {
        var (core, r) = Create();
        using (core)
        {
            core.Write("\x1b]2;build\x07\x07\x1b]7;file://host/tmp/x\x07\x1b]9;done\x07\x1b]777;notify;T;B\x07\x1b]9;4;1;40\x07"u8);
            Assert.Equal(["title:build", "bell", "pwd:file://host/tmp/x", "notify:|done", "notify:T|B", "progress:Set:40"], r.Events);
            Assert.Equal("file://host/tmp/x", core.WorkingDirectoryRaw);
        }
    }

    [Fact]
    public void ClipboardWriteAndDeferredRead()
    {
        var (core, r) = Create();
        using (core)
        {
            core.Write("\x1b]52;c;aGVsbG8=\x07"u8);
            Assert.Contains("clip-write:Standard:hello", r.Events);

            r.ReadDecision = ClipboardReadDecision.Deferred;
            Assert.Equal("", Reply(core, r, "\x1b]52;c;?\x07"));   // O3：回调返回时不应答
            Assert.True(r.LastRead!.Value.BelTerminator);
            r.Output.Clear();
            core.CompleteClipboardRead(r.LastRead.Value, "hi"u8);
            Assert.Equal("\x1b]52;c;aGk=\x07", r.Output.ToString());

            r.ReadDecision = ClipboardReadDecision.Denied;
            Assert.Equal("\x1b]52;c;\x1b\\", Reply(core, r, "\x1b]52;c;?\x1b\\"));  // 拒绝：库回空剪贴板
        }
    }

    [Fact]
    public void TracksCommandsAndReadsOutput()
    {
        var (core, r) = Create();
        using (core)
        {
            core.Write("\x1b]133;A\x07$ \x1b]133;B\x07ls\r\n\x1b]133;C;cmdline=ls\afile1\r\nfile2\r\n\x1b]133;D;3\x07"u8);
            var record = Assert.Single(core.Commands.Records);
            Assert.Equal(CommandState.Finished, record.State);
            Assert.Equal(3, record.ExitCode);
            Assert.Equal("ls", record.CommandLine);
            Assert.Equal("file1\nfile2", core.Commands.ReadOutput(record)!.Trim());
            Assert.Contains("prompt:CommandEnd", r.Events);

            core.Write("\x1b]133;A\x07$ "u8);
            Assert.True(core.CursorAtPrompt);
            Assert.Equal(2, core.Commands.Records.Count);
        }
    }

    [Fact]
    public void Osc633RequiresMatchingNonce()
    {
        var (core, _) = Create();
        using (core)
        {
            core.Commands.Osc633Nonce = "n0nce";
            core.Write("\x1b]633;A\x07$ \x1b]633;B\x07\x1b]633;E;echo a\\x3bb;wrong\x07\x1b]633;C\x07"u8);
            Assert.Null(core.Commands.Current!.CommandLine);
            core.Write("\x1b]633;E;echo a\\x3bb;n0nce\x07out\r\n\x1b]633;D;0\x07"u8);
            Assert.Equal("echo a;b", core.Commands.Current!.CommandLine);
            Assert.Equal((CommandState.Finished, 0), (core.Commands.Current.State, core.Commands.Current.ExitCode));
        }
    }

    [Fact]
    public void RisClearsCommandsAndReportsReset()
    {
        var (core, r) = Create();
        using (core)
        {
            core.Write("\x1b]133;A\x07$ \x1b" + "c"u8.ToArray().Aggregate("", (s, b) => s + (char)b) is var seq ? Encoding.UTF8.GetBytes(seq) : []);
            Assert.Empty(core.Commands.Records);
            Assert.Contains("reset", r.Events);
        }
    }

    [Fact]
    public void ClearInjectsAtGround()
    {
        var (core, _) = Create();
        using (core)
        {
            core.Write("line1\r\nline2"u8);
            core.Clear(ClearTarget.All);
            Assert.Equal("", core.FormatPlainText());
            core.Write("\x1b[3"u8);
            Assert.Throws<InvalidOperationException>(() => core.Clear(ClearTarget.Screen));
        }
    }

    [Fact]
    public void GraphemeWidthPolicySetsMode2027Default()
    {
        var (core, _) = Create(new TerminalCoreOptions(new TerminalSize(20, 4, 8, 16)) { Width = WidthPolicy.Grapheme });
        using (core)
        {
            Assert.True(core.GetMode(2027));
            core.Write("\x1b[?2027l\x1b" + "c" is var s ? Encoding.UTF8.GetBytes(s) : []);
            Assert.True(core.GetMode(2027));  // RIS 恢复为默认值
        }
    }
}
