using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace TKCode.Desktop.Controls;

/// <summary>分段条的一段：占整条轨道的比例（0–1）与颜色的资源键。</summary>
public sealed record BarSegment(double Value, string BrushKey);

/// <summary>
/// 分段占用条（上下文容量）：各段从左到右依次排列，段与段之间留 <see cref="Gap"/> 的空隙（透出卡片底色，不画描边），
/// 剩余部分是轨道。填充太窄放不下各段时，整段画成一个圆点 / 短条，用占比最大的那段的颜色。
/// 颜色按资源键在渲染时取，随主题切换。
/// </summary>
public sealed class SegmentedBar : Control
{
    public static readonly StyledProperty<IReadOnlyList<BarSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<SegmentedBar, IReadOnlyList<BarSegment>?>(nameof(Segments));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<SegmentedBar, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<SegmentedBar, double>(nameof(Gap), 2);

    /// <summary>每段至少的宽度，太窄的段从最大的段里借。</summary>
    private const double MinSegment = 3;

    static SegmentedBar()
    {
        AffectsRender<SegmentedBar>(SegmentsProperty, TrackBrushProperty, GapProperty);
        HeightProperty.OverrideDefaultValue<SegmentedBar>(6);
    }

    public IReadOnlyList<BarSegment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public double Gap
    {
        get => GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    public SegmentedBar()
    {
        // 段的颜色在渲染时按资源键取：换主题后重画
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        var radius = height / 2;
        var segments = (Segments ?? []).Where(s => double.IsFinite(s.Value) && s.Value > 0).ToList();
        var total = Math.Min(1, segments.Sum(s => s.Value));
        if (total <= 0)
        {
            DrawTrack(context, 0, width, height);
            return;
        }
        var fill = Math.Min(width, Math.Max(height, width * total));
        var widths = Layout(segments, fill, height);
        if (widths is null)
        {
            // 放不下各段：画成一段，用最大的那段的颜色
            var largest = segments.MaxBy(s => s.Value)!;
            context.DrawRectangle(Brush(largest.BrushKey), null, new Rect(0, 0, fill, height), radius, radius);
        }
        else
        {
            using (context.PushClip(new RoundedRect(new Rect(0, 0, fill, height), radius)))
            {
                var x = 0.0;
                for (var i = 0; i < segments.Count; i++)
                {
                    context.DrawRectangle(Brush(segments[i].BrushKey), null, new Rect(x, 0, widths[i], height));
                    x += widths[i] + Gap;
                }
            }
        }
        if (fill + Gap < width)
        {
            DrawTrack(context, fill + Gap, width - fill - Gap, height);
        }
    }

    /// <summary>各段的宽度（总宽 <paramref name="fill"/> 扣掉段间空隙）；每段至少 <see cref="MinSegment"/>，放不下时返回 null。</summary>
    private double[]? Layout(List<BarSegment> segments, double fill, double height)
    {
        var available = fill - Gap * (segments.Count - 1);
        if (segments.Count > 1 && available < segments.Count * Math.Max(MinSegment, height))
        {
            return null;
        }
        var sum = segments.Sum(s => s.Value);
        var widths = segments.Select(s => available * s.Value / sum).ToArray();
        var largest = Array.IndexOf(widths, widths.Max());
        for (var i = 0; i < widths.Length; i++)
        {
            if (widths[i] < MinSegment)
            {
                widths[largest] -= MinSegment - widths[i];
                widths[i] = MinSegment;
            }
        }
        return widths;
    }

    private void DrawTrack(DrawingContext context, double x, double width, double height)
    {
        if (TrackBrush is { } track && width > 0)
        {
            var radius = height / 2;
            context.DrawRectangle(track, null, new Rect(x, 0, Math.Max(width, height), height), radius, radius);
        }
    }

    private IBrush? Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;
}
