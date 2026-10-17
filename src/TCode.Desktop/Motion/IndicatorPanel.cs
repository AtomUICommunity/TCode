using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace TCode.Desktop.Motion;

public enum IndicatorMode
{
    /// <summary>圆角底板（分段选择、命令面板高亮）。</summary>
    Fill,
    /// <summary>底部下划线（页签）。</summary>
    Underline,
}

/// <summary>
/// 共享滑动指示条：子孙元素写 motion:IndicatorPanel.IsCurrent="{Binding IsSelected}"，
/// 面板在当前项底下画一块底板（或一条下划线），当前项变化时底板弹簧滑过去并改变尺寸，而不是各项各自瞬间换色。
/// 底板画在面板自身（子元素之下），所以当前项自己的选中背景应在样式里设为透明。
/// </summary>
public class IndicatorPanel : Decorator, IMotionGated
{
    public static readonly AttachedProperty<bool> IsCurrentProperty =
        AvaloniaProperty.RegisterAttached<IndicatorPanel, Control, bool>("IsCurrent");

    public static readonly StyledProperty<IBrush?> IndicatorBrushProperty =
        AvaloniaProperty.Register<IndicatorPanel, IBrush?>(nameof(IndicatorBrush));

    public static readonly StyledProperty<IBrush?> IndicatorBorderBrushProperty =
        AvaloniaProperty.Register<IndicatorPanel, IBrush?>(nameof(IndicatorBorderBrush));

    public static readonly StyledProperty<double> IndicatorRadiusProperty =
        AvaloniaProperty.Register<IndicatorPanel, double>(nameof(IndicatorRadius), 6);

    public static readonly StyledProperty<IndicatorMode> ModeProperty =
        AvaloniaProperty.Register<IndicatorPanel, IndicatorMode>(nameof(Mode));

    /// <summary>下划线粗细与左右内缩。</summary>
    public static readonly StyledProperty<double> UnderlineThicknessProperty =
        AvaloniaProperty.Register<IndicatorPanel, double>(nameof(UnderlineThickness), 2);

    public static readonly StyledProperty<double> UnderlineInsetProperty =
        AvaloniaProperty.Register<IndicatorPanel, double>(nameof(UnderlineInset), 7);

    private readonly MotionGate _gate;
    private readonly MotionValue _x;
    private readonly MotionValue _y;
    private readonly MotionValue _w;
    private readonly MotionValue _h;
    private readonly MotionValue _opacity;
    private Control? _current;
    private Rect? _target;

    static IndicatorPanel()
    {
        AffectsRender<IndicatorPanel>(IndicatorBrushProperty, IndicatorBorderBrushProperty, IndicatorRadiusProperty, ModeProperty);
        IsCurrentProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (control.FindAncestorOfType<IndicatorPanel>() is not { } panel)
            {
                // 还没挂到树上：挂上后再报告
                if (e.GetNewValue<bool>())
                {
                    control.AttachedToVisualTree += ReportWhenAttached;
                }
                return;
            }
            panel.Report(control, e.GetNewValue<bool>());
        });
    }

    private static void ReportWhenAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control control)
        {
            control.AttachedToVisualTree -= ReportWhenAttached;
            if (GetIsCurrent(control) && control.FindAncestorOfType<IndicatorPanel>() is { } panel)
            {
                panel.Report(control, true);
            }
        }
    }

    public static bool GetIsCurrent(Control control) => control.GetValue(IsCurrentProperty);
    public static void SetIsCurrent(Control control, bool value) => control.SetValue(IsCurrentProperty, value);

    public IndicatorPanel()
    {
        _gate = MotionGate.For(this);
        _x = new MotionValue(this, 0, _ => InvalidateVisual());
        _y = new MotionValue(this, 0, _ => InvalidateVisual());
        _w = new MotionValue(this, 0, _ => InvalidateVisual());
        _h = new MotionValue(this, 0, _ => InvalidateVisual());
        _opacity = new MotionValue(this, 0, _ => InvalidateVisual());
        LayoutUpdated += (_, _) => Track();
    }

    public IBrush? IndicatorBrush
    {
        get => GetValue(IndicatorBrushProperty);
        set => SetValue(IndicatorBrushProperty, value);
    }

    public IBrush? IndicatorBorderBrush
    {
        get => GetValue(IndicatorBorderBrushProperty);
        set => SetValue(IndicatorBorderBrushProperty, value);
    }

    public double IndicatorRadius
    {
        get => GetValue(IndicatorRadiusProperty);
        set => SetValue(IndicatorRadiusProperty, value);
    }

    public IndicatorMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public double UnderlineThickness
    {
        get => GetValue(UnderlineThicknessProperty);
        set => SetValue(UnderlineThicknessProperty, value);
    }

    public double UnderlineInset
    {
        get => GetValue(UnderlineInsetProperty);
        set => SetValue(UnderlineInsetProperty, value);
    }

    /// <summary>指示条当前所在的矩形（测试用）。</summary>
    public Rect IndicatorRect => new(_x.Value, _y.Value, Math.Max(0, _w.Value), Math.Max(0, _h.Value));

    public Control? Current => _current;

    private void Report(Control control, bool current)
    {
        if (current)
        {
            _current = control;
            Track();
            return;
        }
        if (!ReferenceEquals(_current, control))
        {
            return;
        }
        // 另一项可能紧接着变成当前项（顺序不定）：稍后仍没有当前项时才隐藏
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_current, control) && !GetIsCurrent(control))
            {
                _current = null;
                _target = null;
                _opacity.AnimateTo(0, MotionTokens.FadeOut);
            }
        });
    }

    private void Track()
    {
        if (_current is not { } current || !current.IsEffectivelyVisible ||
            current.TranslatePoint(default, this) is not { } origin)
        {
            return;
        }
        var rect = new Rect(origin, current.Bounds.Size);
        if (_target == rect)
        {
            return;
        }
        var first = _target is null || _opacity.Target == 0;
        _target = rect;
        if (first || !_gate.ShouldAnimate())
        {
            _x.Snap(rect.X);
            _y.Snap(rect.Y);
            _w.Snap(rect.Width);
            _h.Snap(rect.Height);
            if (_gate.ShouldAnimate())
            {
                _opacity.AnimateTo(1, MotionTokens.Bouncy);
            }
            else
            {
                _opacity.Snap(1);
            }
            return;
        }
        _x.AnimateTo(rect.X, MotionTokens.Glide);
        _y.AnimateTo(rect.Y, MotionTokens.Glide);
        _w.AnimateTo(rect.Width, MotionTokens.Glide);
        _h.AnimateTo(rect.Height, MotionTokens.Glide);
        _opacity.AnimateTo(1, MotionTokens.Bouncy);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var opacity = Math.Clamp(_opacity.Value, 0, 1);
        if (opacity <= 0 || IndicatorBrush is not { } brush)
        {
            return;
        }
        var r = IndicatorRect;
        using (context.PushOpacity(opacity))
        {
            if (Mode == IndicatorMode.Underline)
            {
                var line = new Rect(r.X + UnderlineInset, r.Bottom - UnderlineThickness, Math.Max(0, r.Width - UnderlineInset * 2), UnderlineThickness);
                context.DrawRectangle(brush, null, line, UnderlineThickness / 2, UnderlineThickness / 2);
            }
            else
            {
                var pen = IndicatorBorderBrush is { } border ? new Pen(border, 1) : null;
                context.DrawRectangle(brush, pen, pen is null ? r : r.Deflate(0.5), IndicatorRadius, IndicatorRadius);
            }
        }
    }
}
