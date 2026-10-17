using System.Buffers;
using System.Text;
using TKCode.Terminal.Core;
using Xunit;

namespace TKCode.Terminal.Core.Tests;

/// <summary>InputModes、按键 / 鼠标 / 焦点编码与粘贴（02 §13，research/03 §4–§5）。</summary>
public sealed class InputTests
{
    private static string Key(InputEncoder encoder, KeyInput key)
    {
        Span<byte> buffer = stackalloc byte[64];
        return Encoding.UTF8.GetString(buffer[..encoder.EncodeKey(key, buffer)]);
    }

    private static InputModes ModesAfter(string sequence)
    {
        using var core = TerminalCore.Create(new TerminalCoreOptions(new TerminalSize(80, 24, 8, 16)));
        core.Write(Encoding.UTF8.GetBytes(sequence));
        return core.ReadInputModes();
    }

    [Fact]
    public void ReadsInputModes()
    {
        var m = ModesAfter("\x1b[?1h\x1b[?1000h\x1b[?1006h\x1b[?2004h\x1b[?1004h\x1b[?1049h\x1b[>4;2m\x1b[>5u");
        Assert.True(m.CursorKeyApplication);
        Assert.Equal((MouseTracking.Normal, MouseFormat.Sgr), (m.MouseTracking, m.MouseFormat));
        Assert.True(m.BracketedPaste && m.FocusEvents && m.AlternateScreen && m.ModifyOtherKeys2);
        Assert.Equal(5, m.KittyFlags);
        var defaults = ModesAfter("");
        Assert.Equal(InputModes.Defaults with { CursorAtPrompt = defaults.CursorAtPrompt }, defaults);
    }

    [Fact]
    public void LegacyKeyEncoding()
    {
        using var encoder = new InputEncoder();
        Assert.Equal("a", Key(encoder, new KeyInput(TerminalKey.A, KeyAction.Press, KeyMods.None, "a")));
        Assert.Equal("A", Key(encoder, new KeyInput(TerminalKey.A, KeyAction.Press, KeyMods.Shift, "A", KeyMods.Shift, 'a')));
        Assert.Equal("\x03", Key(encoder, new KeyInput(TerminalKey.C, KeyAction.Press, KeyMods.Ctrl, null, UnshiftedCodepoint: 'c')));
        // macOS：Option 默认是组合键（产生文本），作为 Alt 需要用户设置；其他平台该选项无效
        encoder.OptionAsAlt = OptionAsAlt.Both;
        Assert.Equal("\x1bx", Key(encoder, new KeyInput(TerminalKey.X, KeyAction.Press, KeyMods.Alt, "x", UnshiftedCodepoint: 'x')));
        encoder.OptionAsAlt = OptionAsAlt.None;
        Assert.Equal("\x1b[A", Key(encoder, new KeyInput(TerminalKey.ArrowUp, KeyAction.Press, KeyMods.None)));
        Assert.Equal("\x7f", Key(encoder, new KeyInput(TerminalKey.Backspace, KeyAction.Press, KeyMods.None)));
        Assert.Equal("\r", Key(encoder, new KeyInput(TerminalKey.Enter, KeyAction.Press, KeyMods.None)));
        Assert.Equal("\x1b[1;5C", Key(encoder, new KeyInput(TerminalKey.ArrowRight, KeyAction.Press, KeyMods.Ctrl)));
        Assert.Equal("", Key(encoder, new KeyInput(TerminalKey.ShiftLeft, KeyAction.Press, KeyMods.Shift)));
        Assert.Equal("", Key(encoder, new KeyInput(TerminalKey.A, KeyAction.Release, KeyMods.None, "a")));  // legacy 不上报抬起

        encoder.Apply(InputModes.Defaults with { CursorKeyApplication = true });
        Assert.Equal("\x1bOA", Key(encoder, new KeyInput(TerminalKey.ArrowUp, KeyAction.Press, KeyMods.None)));
    }

    [Fact]
    public void KittyKeyboardProtocol()
    {
        using var encoder = new InputEncoder();
        encoder.Apply(InputModes.Defaults with { KittyFlags = 1 });  // disambiguate
        Assert.Equal("\x1b[99;5u", Key(encoder, new KeyInput(TerminalKey.C, KeyAction.Press, KeyMods.Ctrl, null, UnshiftedCodepoint: 'c')));
        Assert.Equal("\x1b[27u", Key(encoder, new KeyInput(TerminalKey.Escape, KeyAction.Press, KeyMods.None)));
        Assert.Equal("a", Key(encoder, new KeyInput(TerminalKey.A, KeyAction.Press, KeyMods.None, "a", UnshiftedCodepoint: 'a')));
    }

    [Fact]
    public void MouseAndFocusEncoding()
    {
        using var encoder = new InputEncoder();
        var geometry = new MouseGeometry(800, 480, 10, 20, 0, 0, 0, 0);
        Span<byte> buffer = stackalloc byte[64];
        Assert.Equal(0, encoder.EncodeMouse(new MouseInput(MouseAction.Press, MouseButton.Left, KeyMods.None, 25, 65), geometry, false, buffer));

        encoder.Apply(InputModes.Defaults with { MouseTracking = MouseTracking.Normal, MouseFormat = MouseFormat.Sgr });
        var n = encoder.EncodeMouse(new MouseInput(MouseAction.Press, MouseButton.Left, KeyMods.None, 25, 65), geometry, false, buffer);
        Assert.Equal("\x1b[<0;3;4M", Encoding.ASCII.GetString(buffer[..n]));
        n = encoder.EncodeMouse(new MouseInput(MouseAction.Press, MouseButton.WheelUp, KeyMods.None, 25, 65), geometry, false, buffer);
        Assert.Equal("\x1b[<64;3;4M", Encoding.ASCII.GetString(buffer[..n]));

        n = InputEncoder.EncodeFocus(true, buffer);
        Assert.Equal("\x1b[I", Encoding.ASCII.GetString(buffer[..n]));
    }

    [Fact]
    public void PasteIsBracketedOrRequiresConfirmation()
    {
        using var core = TerminalCore.Create(new TerminalCoreOptions(new TerminalSize(80, 24, 8, 16)));
        var output = new ArrayBufferWriter<byte>();
        Assert.Equal(PasteOutcome.Written, core.EncodePaste("hello", false, output));
        Assert.Equal("hello", Encoding.UTF8.GetString(output.WrittenSpan));

        output.Clear();
        Assert.Equal(PasteOutcome.RequiresConfirmation, core.EncodePaste("rm -rf x\n", false, output));
        Assert.Equal(0, output.WrittenCount);
        Assert.Equal(PasteOutcome.Written, core.EncodePaste("rm -rf x\n", true, output));
        Assert.Equal("rm -rf x\r", Encoding.UTF8.GetString(output.WrittenSpan));

        core.Write("\x1b[?2004h"u8);
        output.Clear();
        Assert.Equal(PasteOutcome.Written, core.EncodePaste("a\nb", false, output));
        Assert.Equal("\x1b[200~a\nb\x1b[201~", Encoding.UTF8.GetString(output.WrittenSpan));

        Assert.False(PasteEncoder.IsSafe("x\x1b[201~y"u8));
        Assert.Equal("\x1b[200~ab\x1b[201~", Encoding.UTF8.GetString(PasteEncoder.Encode("ab"u8, bracketed: true)));
    }
}
