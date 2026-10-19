using Avalonia;
using Avalonia.Controls;

namespace TKCode.Desktop.Controls;

/// <summary>
/// 信息卡片的外壳（悬停卡片、弹层里的说明卡）：标题与右侧摘要一行、正文、分隔线下的底栏；
/// 背景、描边、圆角、投影与内边距统一在 Controls.axaml 的主题里。
/// </summary>
public class InfoCard : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<InfoCard, string?>(nameof(Title));

    /// <summary>标题右侧的摘要（例如“2.6万/100万 (2.6%)”）。</summary>
    public static readonly StyledProperty<object?> ExtraProperty =
        AvaloniaProperty.Register<InfoCard, object?>(nameof(Extra));

    /// <summary>分隔线下方的底栏；为 null 时不显示分隔线。</summary>
    public static readonly StyledProperty<object?> FooterProperty =
        AvaloniaProperty.Register<InfoCard, object?>(nameof(Footer));

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public object? Extra
    {
        get => GetValue(ExtraProperty);
        set => SetValue(ExtraProperty, value);
    }

    public object? Footer
    {
        get => GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }
}
