using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace TKCode.Desktop.Controls;

/// <summary>上下文占用环：灰色轨道 + 按 <see cref="Value"/>（0–1）绘制的前景弧。</summary>
public sealed class ContextRing : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<ContextRing, double>(nameof(Value));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<ContextRing, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<ContextRing, IBrush?>(nameof(Foreground));

    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<ContextRing, double>(nameof(Thickness), 1.75);

    static ContextRing()
    {
        AffectsRender<ContextRing>(ValueProperty, TrackBrushProperty, ForegroundProperty, ThicknessProperty);
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

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double Thickness
    {
        get => GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= Thickness)
        {
            return;
        }
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = (size - Thickness) / 2;
        context.DrawEllipse(null, new Pen(TrackBrush, Thickness), center, radius, radius);

        var value = Math.Clamp(Value, 0, 1);
        if (value <= 0)
        {
            return;
        }
        var pen = new Pen(Foreground, Thickness, lineCap: PenLineCap.Round);
        if (value >= 0.999)
        {
            context.DrawEllipse(null, pen, center, radius, radius);
            return;
        }
        // 从 12 点方向顺时针绘制。
        var angle = value * 2 * Math.PI;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, false);
            ctx.ArcTo(end, new Size(radius, radius), 0, value > 0.5, SweepDirection.Clockwise);
            ctx.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }
}
