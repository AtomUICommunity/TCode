using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace TCode.Desktop.Motion;

public enum SmoothAxes
{
    Vertical,
    Horizontal,
    Both,
}

/// <summary>
/// 内容尺寸变化时平滑过渡占用尺寸（输入框逐行增高、弹层内切换子状态、列表结果数量变化）。
/// 内容始终按完整尺寸排版，过渡期间按 VerticalAnchor 对齐并裁剪。
/// </summary>
public class SmoothSize : Decorator, IMotionGated
{
    public static readonly StyledProperty<SmoothAxes> AxesProperty =
        AvaloniaProperty.Register<SmoothSize, SmoothAxes>(nameof(Axes));

    /// <summary>过渡期间内容贴哪一边：Bottom 适合停靠在底部、向上长高的输入框。</summary>
    public static readonly StyledProperty<VerticalAlignment> AnchorProperty =
        AvaloniaProperty.Register<SmoothSize, VerticalAlignment>(nameof(Anchor), VerticalAlignment.Top);

    private readonly MotionGate _gate;
    private readonly MotionValue _width;
    private readonly MotionValue _height;
    private bool _measured;
    private bool _inMeasure;

    public SmoothSize()
    {
        ClipToBounds = true;
        _gate = MotionGate.For(this);
        _width = new MotionValue(this, 0, _ => Invalidate());
        _height = new MotionValue(this, 0, _ => Invalidate());
    }

    public SmoothAxes Axes
    {
        get => GetValue(AxesProperty);
        set => SetValue(AxesProperty, value);
    }

    public VerticalAlignment Anchor
    {
        get => GetValue(AnchorProperty);
        set => SetValue(AnchorProperty, value);
    }

    public new bool IsAnimating => _width.IsAnimating || _height.IsAnimating;

    private void Invalidate()
    {
        if (!_inMeasure)
        {
            InvalidateMeasure();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _measured = false;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child)
        {
            return default;
        }
        child.Measure(availableSize);
        var desired = child.DesiredSize;
        _inMeasure = true;
        try
        {
            var animate = _measured && _gate.ShouldAnimate();
            Track(_width, desired.Width, animate && Axes != SmoothAxes.Vertical);
            Track(_height, desired.Height, animate && Axes != SmoothAxes.Horizontal);
        }
        finally
        {
            _inMeasure = false;
        }
        _measured = true;
        return new Size(Math.Max(0, _width.Value), Math.Max(0, _height.Value));
    }

    private static void Track(MotionValue value, double target, bool animate)
    {
        if (Math.Abs(value.Target - target) < 0.01)
        {
            return;
        }
        if (animate)
        {
            value.AnimateTo(target, MotionTokens.Glide);
        }
        else
        {
            value.Snap(target);
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is not { } child)
        {
            return finalSize;
        }
        var desired = child.DesiredSize;
        // 过渡期间按内容自己的尺寸排版（不被拉伸或压扁），静止后照常填满
        var width = _width.IsAnimating ? desired.Width : finalSize.Width;
        var height = _height.IsAnimating ? desired.Height : finalSize.Height;
        var y = Anchor == VerticalAlignment.Bottom ? finalSize.Height - height : 0;
        child.Arrange(new Rect(0, y, width, height));
        return finalSize;
    }
}
