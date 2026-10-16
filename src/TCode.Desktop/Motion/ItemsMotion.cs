using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;

namespace TCode.Desktop.Motion;

/// <summary>
/// 列表新增项入场：motion:ItemsMotion.Enter="SlideUp"。
/// 只对“已经加载之后”新增的项播放（初次加载、换数据源、分页追加整页时不播），多项同时新增时逐项错开。
/// </summary>
public static class ItemsMotion
{
    public static readonly AttachedProperty<PresenceEffect?> EnterProperty =
        AvaloniaProperty.RegisterAttached<ItemsControl, PresenceEffect?>("Enter", typeof(ItemsMotion));

    /// <summary>一轮最多播放入场的项数，超出的直接出现。</summary>
    public static readonly AttachedProperty<int> MaxBatchProperty =
        AvaloniaProperty.RegisterAttached<ItemsControl, int>("MaxBatch", typeof(ItemsMotion), 24);

    private static readonly AttachedProperty<Tracker?> TrackerProperty =
        AvaloniaProperty.RegisterAttached<ItemsControl, Tracker?>("Tracker", typeof(ItemsMotion));

    static ItemsMotion()
    {
        EnterProperty.Changed.AddClassHandler<ItemsControl>((items, e) =>
        {
            items.GetValue(TrackerProperty)?.Dispose();
            items.SetValue(TrackerProperty, e.GetNewValue<PresenceEffect?>() is { } effect ? new Tracker(items, effect) : null);
        });
    }

    public static PresenceEffect? GetEnter(ItemsControl items) => items.GetValue(EnterProperty);
    public static void SetEnter(ItemsControl items, PresenceEffect? value) => items.SetValue(EnterProperty, value);
    public static int GetMaxBatch(ItemsControl items) => items.GetValue(MaxBatchProperty);
    public static void SetMaxBatch(ItemsControl items, int value) => items.SetValue(MaxBatchProperty, value);

    private sealed class Tracker : IDisposable
    {
        private readonly ItemsControl _items;
        private readonly PresenceEffect _effect;
        private readonly Dictionary<object, int> _fresh = new(ReferenceEqualityComparer.Instance);
        private int _batch;
        private bool _rebuilding;
        private bool _idleScheduled;

        public Tracker(ItemsControl items, PresenceEffect effect)
        {
            _items = items;
            _effect = effect;
            items.Items.CollectionChanged += OnItemsChanged;
            items.ContainerPrepared += OnContainerPrepared;
        }

        public void Dispose()
        {
            _items.Items.CollectionChanged -= OnItemsChanged;
            _items.ContainerPrepared -= OnContainerPrepared;
        }

        private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                // Clear 之后紧跟着逐项 Add 的是整体重建（搜索、换根目录），这一轮都不播
                _fresh.Clear();
                _rebuilding = true;
                ScheduleIdle();
                return;
            }
            if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is not { } added || _rebuilding ||
                !_items.IsLoaded || !MotionSettings.IsEnabled)
            {
                return;
            }
            if (!_idleScheduled)
            {
                _batch = 0;
            }
            foreach (var item in added)
            {
                // 一轮新增太多（批量加载）时超出的部分不播
                if (item is not null && _batch < GetMaxBatch(_items))
                {
                    _fresh[item] = _batch++;
                }
            }
            ScheduleIdle();
        }

        private void ScheduleIdle()
        {
            if (_idleScheduled)
            {
                return;
            }
            _idleScheduled = true;
            // 这一轮布局结束后：没实现出来的项（虚拟化在视口外）以后滚动进来时不再播
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _idleScheduled = false;
                _rebuilding = false;
                _fresh.Clear();
            }, Avalonia.Threading.DispatcherPriority.Background);
        }

        private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
        {
            var item = _items.ItemFromContainer(e.Container);
            if (item is null || !_fresh.Remove(item, out var order))
            {
                return;
            }
            var delay = TimeSpan.FromTicks(MotionTokens.Stagger.Ticks * Math.Min(order, MotionTokens.StaggerLimit));
            Presence.PlayEnter(e.Container, _effect, delay);
        }
    }
}
