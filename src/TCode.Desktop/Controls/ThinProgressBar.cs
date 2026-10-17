using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace TCode.Desktop.Controls;

/// <summary>细进度条（更新下载卡片）：轨道 + 按 <see cref="Value"/>（0–1）填充的圆角条。</summary>
public sealed class ThinProgressBar : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<ThinProgressBar, double>(nameof(Value));
    public static readonly StyledProperty<IBrush?> TrackBrushProperty = AvaloniaProperty.Register<ThinProgressBar, IBrush?>(nameof(TrackBrush));
    public static readonly StyledProperty<IBrush?> FillBrushProperty = AvaloniaProperty.Register<ThinProgressBar, IBrush?>(nameof(FillBrush));

    static ThinProgressBar()
    {
        AffectsRender<ThinProgressBar>(ValueProperty, TrackBrushProperty, FillBrushProperty);
        HeightProperty.OverrideDefaultValue<ThinProgressBar>(4);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? FillBrush
    {
        get => GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        var radius = bounds.Height / 2;
        if (TrackBrush is { } track)
        {
            context.DrawRectangle(track, null, bounds, radius, radius);
        }
        var fraction = Math.Clamp(double.IsFinite(Value) ? Value : 0, 0, 1);
        if (FillBrush is { } fill && fraction > 0)
        {
            context.DrawRectangle(fill, null, new Rect(0, 0, Math.Max(bounds.Height, bounds.Width * fraction), bounds.Height), radius, radius);
        }
    }
}
