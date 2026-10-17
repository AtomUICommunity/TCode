using Avalonia;
using Avalonia.Data.Converters;
using TKCode.Desktop.Icons;

namespace TKCode.Desktop.Views;

public static class Converters
{
    /// <summary>暗色时显示太阳（切到亮色），亮色时显示月亮。</summary>
    public static readonly IValueConverter ThemeIcon =
        new FuncValueConverter<bool, LucideKind>(isDark => isDark ? LucideKind.Sun : LucideKind.Moon);

    /// <summary>被忽略的文件（bin、obj、.git 等）在文件树中变暗。</summary>
    public static readonly IValueConverter DimOpacity =
        new FuncValueConverter<bool, double>(dimmed => dimmed ? 0.45 : 1.0);

    public static readonly IValueConverter LeftIndent =
        new FuncValueConverter<double, Thickness>(indent => new Thickness(indent - 8, 0, 0, 0));

    /// <summary>绑定值等于 ConverterParameter（分段选择的选中态）。</summary>
    public static readonly IValueConverter EqualsParameter =
        new FuncValueConverter<object?, object?, bool>((value, parameter) => Equals(value?.ToString(), parameter?.ToString()));

    /// <summary>Key 输入框：显示明文时不使用掩码字符。</summary>
    public static readonly IValueConverter PasswordChar =
        new FuncValueConverter<bool, char>(revealed => revealed ? '\0' : '•');

    public static readonly IValueConverter RevealIcon =
        new FuncValueConverter<bool, LucideKind>(revealed => revealed ? LucideKind.EyeOff : LucideKind.Eye);
}
