using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace TKCode.Desktop.Motion;

/// <summary>
/// 会话内容入场：数据源（TranscriptAdapter）在条目“刚追加”时 <see cref="Mark"/>，
/// 条目模板的根元素写 motion:Entrance.Effect="SlideUp"，加载时如果它的数据被标记过就播一次入场。
/// 初次挂载会话、加载更早的历史、虚拟化滚动再次实现都不会被标记，所以不播。
/// </summary>
public static class Entrance
{
    public static readonly AttachedProperty<PresenceEffect?> EffectProperty =
        AvaloniaProperty.RegisterAttached<Control, PresenceEffect?>("Effect", typeof(Entrance));

    public static readonly AttachedProperty<double> DistanceProperty =
        AvaloniaProperty.RegisterAttached<Control, double>("Distance", typeof(Entrance), 10);

    private static readonly HashSet<object> Fresh = new(ReferenceEqualityComparer.Instance);
    private static bool _clearScheduled;

    static Entrance()
    {
        EffectProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            control.Loaded -= OnLoaded;
            if (e.GetNewValue<PresenceEffect?>() is not null)
            {
                control.Loaded += OnLoaded;
            }
        });
    }

    public static PresenceEffect? GetEffect(Control control) => control.GetValue(EffectProperty);
    public static void SetEffect(Control control, PresenceEffect? value) => control.SetValue(EffectProperty, value);
    public static double GetDistance(Control control) => control.GetValue(DistanceProperty);
    public static void SetDistance(Control control, double value) => control.SetValue(DistanceProperty, value);

    /// <summary>标记刚追加的数据；这一轮布局里没被实现出来的标记随后清除（以后滚动进来时不播）。</summary>
    public static void Mark(object item)
    {
        if (!MotionSettings.IsEnabled)
        {
            return;
        }
        Fresh.Add(item);
        if (_clearScheduled)
        {
            return;
        }
        _clearScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _clearScheduled = false;
            Fresh.Clear();
        }, DispatcherPriority.Background);
    }

    private static void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control { DataContext: { } item } control && GetEffect(control) is { } effect && Fresh.Remove(item))
        {
            Presence.SetDistance(control, GetDistance(control));
            Presence.PlayEnter(control, effect);
        }
    }
}
