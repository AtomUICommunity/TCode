using Xunit;
using System.Text;
using TKCode.Terminal.Core;

namespace TKCode.Terminal.Core.Tests;

/// <summary>字节流回放 → 屏幕内容断言（11-testing-benchmarks.md 的 L1 黄金样本雏形）。</summary>
public sealed class ReplayTests
{
    private static TerminalCore Create(int cols = 20, int rows = 5) => TerminalCore.Create(new TerminalCoreOptions(new TerminalSize(cols, rows, 8, 16)));

    private static void Write(TerminalCore core, string text) => core.Write(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void EmptyScreenFormatsAsEmptyString()
    {
        using var core = Create();
        Assert.Equal(string.Empty, core.FormatPlainText());
    }

    [Fact]
    public void PrintsTextAndTracksCursor()
    {
        using var core = Create();
        Write(core, "hello\r\nworld");
        Assert.Equal("hello\nworld", core.FormatPlainText());
        Assert.Equal((5, 1), core.Cursor);
    }

    [Fact]
    public void CursorPositioningAndErase()
    {
        using var core = Create();
        Write(core, "aaaaa\r\nbbbbb\x1b[1;3H\x1b[K\x1b[2;2HX");
        Assert.Equal("aa\nbXbbb", core.FormatPlainText());
    }

    [Fact]
    public void WideCharactersOccupyTwoCells()
    {
        using var core = Create();
        Write(core, "中文");
        Assert.Equal((4, 0), core.Cursor);
    }

    [Fact]
    public void AlternateScreenRestoresPrimary()
    {
        using var core = Create();
        Write(core, "shell$ ");
        Write(core, "\x1b[?1049h\x1b[2J\x1b[Hvim");
        Assert.Equal("vim", core.FormatPlainText());
        Write(core, "\x1b[?1049l");
        Assert.Equal("shell$", core.FormatPlainText());
    }

    [Fact]
    public void ResizeReflowsSoftWrappedLines()
    {
        using var core = Create(cols: 10);
        Write(core, "0123456789abcdef");
        core.Resize(new TerminalSize(20, 5, 8, 16));
        Assert.Equal(new TerminalSize(20, 5, 8, 16), core.Size);
        Assert.Equal("0123456789abcdef", core.FormatPlainText());
    }

    [Fact]
    public void TitleChangeRaisesEffect()
    {
        var effects = new RecordingEffects();
        using var core = TerminalCore.Create(new TerminalCoreOptions(new TerminalSize(20, 5, 8, 16)), effects);
        Write(core, "\x1b]2;my title\x07\x07");
        Assert.Equal("my title", core.Title);
        Assert.Equal(["my title"], effects.Titles);
        Assert.Equal(1, effects.Bells);
    }

    [Fact]
    public void QueryRepliesGoToWriteToPty()
    {
        var effects = new RecordingEffects();
        using var core = TerminalCore.Create(new TerminalCoreOptions(new TerminalSize(20, 5, 8, 16)), effects);
        Write(core, "ab\x1b[6n");
        Assert.Equal("\x1b[1;3R", effects.Output);
    }

    [Fact]
    public void ParserGroundTracksIncompleteSequences()
    {
        using var core = Create();
        Write(core, "\x1b[3");
        Assert.False(core.IsParserGround);
        Write(core, "1m");
        Assert.True(core.IsParserGround);
    }

    internal sealed class RecordingEffects : ITerminalEffects
    {
        private readonly StringBuilder _output = new();
        public List<string> Titles { get; } = [];
        public int Bells { get; private set; }
        public string Output => _output.ToString();

        public void WriteToPty(ReadOnlySpan<byte> reply) => _output.Append(Encoding.UTF8.GetString(reply));
        public void Bell() => Bells++;
        public void TitleChanged(string title) => Titles.Add(title);
    }
}
