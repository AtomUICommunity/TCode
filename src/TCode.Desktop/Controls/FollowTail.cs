using Avalonia;
using Avalonia.Controls;

namespace TCode.Desktop.Controls;

/// <summary>
/// ScrollViewer 附加行为：内容增长时，如果之前停在底部就继续贴底（流式思考内容），
/// 用户向上滚动后不再跟随，滚回底部后恢复。
/// </summary>
public static class FollowTail
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>("IsEnabled", typeof(FollowTail));

    private const double Tolerance = 2;

    static FollowTail()
    {
        IsEnabledProperty.Changed.AddClassHandler<ScrollViewer>((viewer, e) =>
        {
            if (e.NewValue is true)
            {
                viewer.ScrollChanged += OnScrollChanged;
            }
            else
            {
                viewer.ScrollChanged -= OnScrollChanged;
            }
        });
    }

    public static bool GetIsEnabled(ScrollViewer viewer) => viewer.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(ScrollViewer viewer, bool value) => viewer.SetValue(IsEnabledProperty, value);

    private static void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer viewer || e.ExtentDelta.Y <= 0)
        {
            return;
        }
        // 内容变高之前是否贴底：用变化前的范围判断
        var previousExtent = viewer.Extent.Height - e.ExtentDelta.Y;
        var wasAtBottom = viewer.Offset.Y + viewer.Viewport.Height >= previousExtent - Tolerance;
        if (wasAtBottom)
        {
            viewer.ScrollToEnd();
        }
    }
}
