using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace TKCode.Desktop.Controls;

/// <summary>
/// 彩色点阵加载动画（3×3 圆点，紫 / 粉 / 蓝依次闪烁）：会话中的“琢磨中…”与侧栏运行中的会话。
/// 仅在有效可见（自身与所有祖先都可见）且挂到可视树时计时：祖先被隐藏时（例如退场后的启动页）停止，
/// 否则隐藏的转圈会每 110ms 让界面重绘一次，空闲时也持续占用 CPU。
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

    // 已挂到可视树的转圈：任意元素的 IsVisible 变化后统一重新检查（有效可见性的变化事件不公开）
    private static readonly HashSet<DotSpinner> s_attached = [];
    private static bool s_recheckPending;

    static DotSpinner()
    {
        IsVisibleProperty.Changed.AddClassHandler<Visual>((_, _) =>
        {
            if (s_attached.Count == 0 || s_recheckPending)
            {
                return;
            }
            s_recheckPending = true;
            Dispatcher.UIThread.Post(() =>
            {
                s_recheckPending = false;
                foreach (var spinner in s_attached.ToList())
                {
                    spinner.UpdateTimer();
                }
            }, DispatcherPriority.Background);
        });
    }

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(110) };
    private int _frame;
    private bool _attached;

    public DotSpinner()
    {
        _timer.Tick += (_, _) =>
        {
            if (!IsEffectivelyVisible)
            {
                UpdateTimer();
                return;
            }
            _frame = (_frame + 1) % (Ring.Length * Palette.Length);
            InvalidateVisual();
        };
    }

    /// <summary>计时器是否在运行（测试用）。</summary>
    internal bool IsAnimating => _timer.IsEnabled;

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
        s_attached.Add(this);
        UpdateTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        _timer.Stop();
        s_attached.Remove(this);
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
        if (IsVisible && _attached && IsEffectivelyVisible)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }
}
