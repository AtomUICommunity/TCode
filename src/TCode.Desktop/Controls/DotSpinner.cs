using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace TCode.Desktop.Controls;

/// <summary>
/// 彩色点阵加载动画（3×3 圆点，紫 / 粉 / 蓝依次闪烁）：会话中的“琢磨中…”与侧栏运行中的会话。
/// 仅在可见且挂到可视树时计时。
/// </summary>
public sealed class DotSpinner : Control
{
    private static readonly IBrush[] Palette =
    [
        new SolidColorBrush(Color.Parse("#8B7CF6")),
        new SolidColorBrush(Color.Parse("#E879A6")),
        new SolidColorBrush(Color.Parse("#60A5FA")),
    ];

    // 点亮顺序：沿外圈顺时针，中心点常亮偏暗
    private static readonly int[] Ring = [0, 1, 2, 5, 8, 7, 6, 3];

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(110) };
    private int _frame;
    private bool _attached;

    public DotSpinner()
    {
        _timer.Tick += (_, _) =>
        {
            _frame = (_frame + 1) % (Ring.Length * Palette.Length);
            InvalidateVisual();
        };
    }

    protected override Size MeasureOverride(Size availableSize) => new(12, 12);

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
        {
            return;
        }
        var step = size / 3;
        var radius = step * 0.32;
        var head = _frame % Ring.Length;
        var color = Palette[_frame / Ring.Length % Palette.Length];
        for (var i = 0; i < 9; i++)
        {
            var position = Array.IndexOf(Ring, i);
            // 头部最亮，尾部依次变暗；中心点保持低亮度
            var distance = position < 0 ? Ring.Length : (head - position + Ring.Length) % Ring.Length;
            var opacity = position < 0 ? 0.25 : Math.Max(0.15, 1 - distance * 0.22);
            var brush = distance <= 1 ? color : Palette[(Array.IndexOf(Palette, color) + 1 + i) % Palette.Length];
            using (context.PushOpacity(opacity))
            {
                var center = new Point(step * (i % 3) + step / 2, step * (i / 3) + step / 2);
                context.DrawEllipse(brush, null, center, radius, radius);
            }
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        UpdateTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        _timer.Stop();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            UpdateTimer();
        }
    }

    private void UpdateTimer()
    {
        if (IsVisible && _attached)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }
}
