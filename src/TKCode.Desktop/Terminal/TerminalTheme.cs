using TKCode.Terminal.Core;
using TKCode.Terminal.Protocol;

namespace TKCode.Desktop.Terminal;

/// <summary>终端的默认颜色（随应用的明暗主题切换）。256 色中 16–255 按 xterm 的公式生成。</summary>
public static class TerminalTheme
{
    private static readonly int[] Base16Dark =
    [
        0x1E1E1E, 0xCD3131, 0x0DBC79, 0xE5E510, 0x2472C8, 0xBC3FBC, 0x11A8CD, 0xE5E5E5,
        0x666666, 0xF14C4C, 0x23D18B, 0xF5F543, 0x3B8EEA, 0xD670D6, 0x29B8DB, 0xFFFFFF,
    ];

    // 浅色背景上的 16 色（VS Code Light+）：黄、白、亮色都压暗，保证在白底上可读
    private static readonly int[] Base16Light =
    [
        0x000000, 0xCD3131, 0x00BC00, 0x949800, 0x0451A5, 0xBC05BC, 0x0598BC, 0x555555,
        0x666666, 0xCD3131, 0x14CE14, 0xB5BA00, 0x0451A5, 0xBC05BC, 0x0598BC, 0xA5A5A5,
    ];

    public static PaletteDto Dark { get; } = Build(Base16Dark, foreground: 0xD4D4D4, background: 0x1E1E1E, cursor: 0xD4D4D4, scheme: "dark");

    public static PaletteDto Light { get; } = Build(Base16Light, foreground: 0x1F2328, background: 0xFFFFFF, cursor: 0x1F2328, scheme: "light");

    public static PaletteDto For(bool dark) => dark ? Dark : Light;

    private static PaletteDto Build(int[] base16, int foreground, int background, int cursor, string scheme)
    {
        var colors = new int[256];
        Array.Copy(base16, colors, 16);
        int[] levels = [0, 95, 135, 175, 215, 255];
        for (var i = 0; i < 216; i++)
        {
            colors[16 + i] = (levels[i / 36] << 16) | (levels[i / 6 % 6] << 8) | levels[i % 6];
        }
        for (var i = 0; i < 24; i++)
        {
            var v = 8 + i * 10;
            colors[232 + i] = (v << 16) | (v << 8) | v;
        }
        return new PaletteDto(foreground, background, cursor, colors, scheme);
    }

    public static TerminalRgb ToRgb(int v) => new((byte)(v >> 16), (byte)(v >> 8), (byte)v);

    /// <summary>协议里的调色板（RGB 整数）转为核心使用的调色板。</summary>
    public static TerminalPalette ToCorePalette(PaletteDto p)
    {
        var colors = new TerminalRgb[256];
        for (var i = 0; i < 256; i++)
        {
            colors[i] = ToRgb(i < p.Palette.Length ? p.Palette[i] : 0);
        }
        return new TerminalPalette(ToRgb(p.Foreground), ToRgb(p.Background), p.Cursor is { } c ? ToRgb(c) : null, colors);
    }
}
