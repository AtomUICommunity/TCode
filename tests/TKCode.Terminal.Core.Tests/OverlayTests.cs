using Xunit;
using System.Text;
using TKCode.Terminal.Core;

namespace TKCode.Terminal.Core.Tests;

/// <summary>native/ghostty/overlays 的 O1–O4（02-core-ghostty.md §2.4）。</summary>
public sealed class OverlayTests
{
    private static (TerminalCore Core, ReplayTests.RecordingEffects Effects) Create(bool deferClipboard = false)
    {
        var effects = new ReplayTests.RecordingEffects();
        var core = TerminalCore.Create(new TerminalCoreOptions(new TerminalSize(80, 24, 8, 16)) { DeferClipboardRead = deferClipboard }, effects);
        return (core, effects);
    }

    private static void Write(TerminalCore core, string text) => core.Write(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void O1_Win32InputModeIsTracked()
    {
        var (core, effects) = Create();
        using (core)
        {
            Assert.False(core.GetMode(9001));
            Write(core, "\x1b[?9001h");
            Assert.True(core.GetMode(9001));
            Write(core, "\x1b[?9001$p");
            Assert.Equal("\x1b[?9001;1$y", effects.Output);
            Write(core, "\x1b" + "c");
            Assert.False(core.GetMode(9001));
        }
    }

    [Fact]
    public void O2_KittyPasteEventsAreNotRecognized()
    {
        var (core, effects) = Create();
        using (core)
        {
            Write(core, "\x1b[?5522h\x1b[?5522$p");
            Assert.False(core.GetMode(5522));
            Assert.Equal("\x1b[?5522;0$y", effects.Output);
        }
    }

    [Fact]
    public void O4_ModifyOtherKeys2()
    {
        var (core, _) = Create();
        using (core)
        {
            Assert.False(core.ModifyOtherKeys2);
            Write(core, "\x1b[>4;2m");
            Assert.True(core.ModifyOtherKeys2);
            Write(core, "\x1b[>4;0m");
            Assert.False(core.ModifyOtherKeys2);
        }
    }

    /// <summary>02 S13：O1 让 ModePacked 变为 44 位后，快照往返仍保留 9001。</summary>
    [Fact]
    public void O1_SurvivesSnapshotRoundTrip()
    {
        var options = new TerminalCoreOptions(new TerminalSize(30, 4, 8, 16));
        using var original = TerminalCore.Create(options);
        Write(original, "line one\r\nline two\x1b[?9001h\x1b[?2004h");
        var snapshot = original.EncodeSnapshot();

        using var restored = TerminalCore.Restore(snapshot, options);
        Assert.True(restored.GetMode(9001));
        Assert.True(restored.GetMode(2004));
        Assert.Equal(original.FormatPlainText(), restored.FormatPlainText());
        Assert.Equal(original.Cursor, restored.Cursor);
    }

    /// <summary>快照包含解析到一半的输入：在序列中间截断，恢复后继续写入剩余字节，结果与不截断相同。</summary>
    [Fact]
    public void SnapshotResumesMidSequence()
    {
        var options = new TerminalCoreOptions(new TerminalSize(30, 4, 8, 16));
        var bytes = Encoding.UTF8.GetBytes("abc\x1b[31mred\x1b[0m done");
        var split = Array.IndexOf(bytes, (byte)'3');

        using var reference = TerminalCore.Create(options);
        reference.Write(bytes);

        using var first = TerminalCore.Create(options);
        first.Write(bytes.AsSpan(0, split));
        Assert.False(first.IsParserGround);
        using var resumed = TerminalCore.Restore(first.EncodeSnapshot(), options);
        resumed.Write(bytes.AsSpan(split));
        Assert.Equal(reference.FormatPlainText(), resumed.FormatPlainText());
    }
}
