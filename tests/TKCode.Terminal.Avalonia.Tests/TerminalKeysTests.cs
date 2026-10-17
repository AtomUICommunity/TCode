using Avalonia.Input;
using TKCode.Terminal.Core;
using Xunit;

namespace TKCode.Terminal.Avalonia.Tests;

/// <summary>按键映射（06-input.md §3）：物理键与修饰键的映射，以及哪些键交给 TextInput 产生字符。</summary>
public sealed class TerminalKeysTests
{
    [Theory]
    [InlineData(Key.A, TerminalKey.A)]
    [InlineData(Key.Z, TerminalKey.Z)]
    [InlineData(Key.D0, TerminalKey.Digit0)]
    [InlineData(Key.D9, TerminalKey.Digit9)]
    [InlineData(Key.NumPad5, TerminalKey.Numpad5)]
    [InlineData(Key.F1, TerminalKey.F1)]
    [InlineData(Key.F24, TerminalKey.F24)]
    [InlineData(Key.Return, TerminalKey.Enter)]
    [InlineData(Key.Back, TerminalKey.Backspace)]
    [InlineData(Key.Left, TerminalKey.ArrowLeft)]
    [InlineData(Key.PageUp, TerminalKey.PageUp)]
    [InlineData(Key.OemQuestion, TerminalKey.Slash)]
    [InlineData(Key.OemTilde, TerminalKey.Backquote)]
    [InlineData(Key.LeftCtrl, TerminalKey.ControlLeft)]
    public void MapsPhysicalKeys(Key key, TerminalKey expected)
    {
        Assert.True(TerminalKeys.TryMap(key, out var mapped));
        Assert.Equal(expected, mapped);
    }

    [Fact]
    public void UnmappedKeysAreRejected()
    {
        Assert.False(TerminalKeys.TryMap(Key.None, out _));
        Assert.False(TerminalKeys.TryMap(Key.ImeConvert, out _));
    }

    [Fact]
    public void ModifiersMapToTerminalMods()
    {
        Assert.Equal(KeyMods.Ctrl | KeyMods.Shift, TerminalKeys.Mods(KeyModifiers.Control | KeyModifiers.Shift));
        Assert.Equal(KeyMods.Super, TerminalKeys.Mods(KeyModifiers.Meta));
        Assert.Equal(KeyMods.None, TerminalKeys.Mods(KeyModifiers.None));
    }

    [Fact]
    public void PrintableKeysProduceTextThroughTextInput()
    {
        Assert.True(TerminalKeys.ProducesText(Key.A, KeyModifiers.None));
        Assert.True(TerminalKeys.ProducesText(Key.A, KeyModifiers.Shift));  // Shift+a 是 "A"，由 TextInput 给出
        Assert.True(TerminalKeys.ProducesText(Key.Space, KeyModifiers.None));
        Assert.True(TerminalKeys.ProducesText(Key.OemComma, KeyModifiers.None));
    }

    [Theory]
    [InlineData(Key.A, KeyModifiers.Control)]       // Ctrl+A 经编码器输出控制字符
    [InlineData(Key.A, KeyModifiers.Alt)]           // Alt+a 需要 ESC 前缀，不能只靠 TextInput
    [InlineData(Key.A, KeyModifiers.Meta)]          // Command+a 不产生字符
    [InlineData(Key.Return, KeyModifiers.None)]     // 功能键
    [InlineData(Key.Left, KeyModifiers.None)]
    public void NonPrintableOrModifiedKeysAreEncodedDirectly(Key key, KeyModifiers modifiers) =>
        Assert.False(TerminalKeys.ProducesText(key, modifiers));
}
