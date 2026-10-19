using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace TKCode.Desktop.Controls;

/// <summary>
/// 卡片里的一行统计：可选的色点（图例）、标签、右对齐的数值。文字用文字色，颜色只在色点上。
/// 色点可直接给 <see cref="Marker"/>，也可给资源键 <see cref="MarkerKey"/>（随主题切换）。
/// </summary>
public class CardStatRow : TemplatedControl
{
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<CardStatRow, string?>(nameof(Label));

    public static readonly StyledProperty<string?> ValueProperty =
        AvaloniaProperty.Register<CardStatRow, string?>(nameof(Value));

    public static readonly StyledProperty<IBrush?> MarkerProperty =
        AvaloniaProperty.Register<CardStatRow, IBrush?>(nameof(Marker));

    public static readonly StyledProperty<string?> MarkerKeyProperty =
        AvaloniaProperty.Register<CardStatRow, string?>(nameof(MarkerKey));

    private IDisposable? _markerBinding;

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public IBrush? Marker
    {
        get => GetValue(MarkerProperty);
        set => SetValue(MarkerProperty, value);
    }

    public string? MarkerKey
    {
        get => GetValue(MarkerKeyProperty);
        set => SetValue(MarkerKeyProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkerKeyProperty)
        {
            _markerBinding?.Dispose();
            _markerBinding = change.GetNewValue<string?>() is { } key
                ? this.Bind(MarkerProperty, this.GetResourceObservable(key, v => v as IBrush))
                : null;
        }
    }
}
