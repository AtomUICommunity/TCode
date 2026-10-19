using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using TKCode.Desktop.Motion;

namespace TKCode.Desktop.Controls;

/// <summary>
/// 悬停卡片：指针在触发内容（<see cref="ContentControl.Content"/>）上停留 <see cref="OpenDelay"/> 后弹出 <see cref="Card"/>，
/// 离开后 <see cref="CloseDelay"/> 内移进卡片就保持打开；按下触发内容立即打开。
/// 弹层（motion:Popover + ZoomHost）与阴影留白由模板负责，卡片外观交给内容（通常是 <see cref="InfoCard"/>）。
/// </summary>
public class HoverCard : ContentControl
{
    /// <summary>卡片四周给阴影留的空间（与 TKCode.MenuShadow 的 0 8 24 相配）；弹层偏移按它换算，卡片本体贴着触发内容对齐。</summary>
    public static readonly Thickness ShadowMargin = new(20, 12, 20, 28);

    public static readonly StyledProperty<object?> CardProperty =
        AvaloniaProperty.Register<HoverCard, object?>(nameof(Card));

    public static readonly StyledProperty<PlacementMode> PlacementProperty =
        AvaloniaProperty.Register<HoverCard, PlacementMode>(nameof(Placement), PlacementMode.BottomEdgeAlignedLeft);

    /// <summary>卡片本体与触发内容之间的距离。</summary>
    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<HoverCard, double>(nameof(Gap), 6);

    public static readonly StyledProperty<TimeSpan> OpenDelayProperty =
        AvaloniaProperty.Register<HoverCard, TimeSpan>(nameof(OpenDelay), TimeSpan.FromMilliseconds(300));

    public static readonly StyledProperty<TimeSpan> CloseDelayProperty =
        AvaloniaProperty.Register<HoverCard, TimeSpan>(nameof(CloseDelay), TimeSpan.FromMilliseconds(150));

    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<HoverCard, bool>(nameof(IsOpen), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    private Popover? _popup;
    private ContentPresenter? _cardHost;
    private IDisposable? _pending;
    private bool _pendingOpen;
    /// <summary>卡片打开期间监听指针移动的窗口。</summary>
    private TopLevel? _root;

    static HoverCard()
    {
        // 触发内容的空白处也要能悬停
        BackgroundProperty.OverrideDefaultValue<HoverCard>(Avalonia.Media.Brushes.Transparent);
    }

    public object? Card
    {
        get => GetValue(CardProperty);
        set => SetValue(CardProperty, value);
    }

    public PlacementMode Placement
    {
        get => GetValue(PlacementProperty);
        set => SetValue(PlacementProperty, value);
    }

    public double Gap
    {
        get => GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    public TimeSpan OpenDelay
    {
        get => GetValue(OpenDelayProperty);
        set => SetValue(OpenDelayProperty, value);
    }

    public TimeSpan CloseDelay
    {
        get => GetValue(CloseDelayProperty);
        set => SetValue(CloseDelayProperty, value);
    }

    /// <summary>卡片是否打开（双向绑定：视图模型可据此在打开时刷新数据）。</summary>
    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    /// <summary>模板里的弹层（测试把它放到窗口浮层上截图）。</summary>
    public Popover? Popup => _popup;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (_cardHost is not null)
        {
            _cardHost.PointerEntered -= OnCardEntered;
            _cardHost.PointerExited -= OnCardExited;
        }
        if (_popup is not null)
        {
            _popup.Closed -= OnPopupClosed;
        }
        _popup = e.NameScope.Find<Popover>("PART_Popup");
        if (_popup is not null)
        {
            _popup.PlacementTarget = this;
            _popup.Placement = Placement;
            _popup.IsLightDismissEnabled = false;
            _popup.Closed += OnPopupClosed;
            _popup.IsOpen = IsOpen;
        }
        _cardHost = e.NameScope.Find<ContentPresenter>("PART_CardHost");
        if (_cardHost is not null)
        {
            // 阴影留白也算卡片：弹层窗口任何位置都能接住指针（留白会盖住一部分触发内容）
            _cardHost.Padding = ShadowMargin;
            _cardHost.Background = Avalonia.Media.Brushes.Transparent;
            _cardHost.PointerEntered += OnCardEntered;
            _cardHost.PointerExited += OnCardExited;
        }
        UpdateOffsets();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PlacementProperty || change.Property == GapProperty)
        {
            UpdateOffsets();
        }
        else if (change.Property == IsOpenProperty)
        {
            var open = change.GetNewValue<bool>();
            if (_popup is not null)
            {
                _popup.IsOpen = open;
            }
            WatchPointer(open);
        }
        else if (change.Property == IsVisibleProperty && !IsVisible)
        {
            Close();
        }
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        Schedule(true, OpenDelay);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        // 弹层窗口（含阴影留白）出现时盖住了触发内容，主窗口会报告“离开”，但指针并没有动：仍在触发内容里就不算离开，
        // 否则卡片关闭 → 指针回到触发内容 → 再打开，反复闪烁。真正移走由 OnRootPointerMoved 与卡片的离开事件处理
        if (IsOpen && Contains(this, e))
        {
            return;
        }
        Schedule(false, CloseDelay);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Schedule(true, TimeSpan.Zero);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Close();
    }

    /// <summary>弹层自己关闭（例如所在窗口关闭）时同步状态。</summary>
    private void OnPopupClosed(object? sender, EventArgs e) => IsOpen = false;

    private void OnCardEntered(object? sender, PointerEventArgs e) => Schedule(true, TimeSpan.Zero);

    private void OnCardExited(object? sender, PointerEventArgs e) => Schedule(false, CloseDelay);

    /// <summary>卡片打开期间，指针在窗口里移动：回到触发内容或卡片上就保持，移到别处就关闭。</summary>
    private void OnRootPointerMoved(object? sender, PointerEventArgs e)
    {
        // 卡片在同一窗口的浮层上时（ShouldUseOverlayLayer），移到卡片上也会经过这里
        var overCard = _cardHost is not null && TopLevel.GetTopLevel(_cardHost) == _root && Contains(_cardHost, e);
        Schedule(Contains(this, e) || overCard, CloseDelay);
    }

    private void WatchPointer(bool on)
    {
        _root?.RemoveHandler(PointerMovedEvent, OnRootPointerMoved);
        _root = on ? TopLevel.GetTopLevel(this) : null;
        _root?.AddHandler(PointerMovedEvent, OnRootPointerMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private static bool Contains(Visual visual, PointerEventArgs e) => new Rect(visual.Bounds.Size).Contains(e.GetPosition(visual));

    /// <summary>
    /// 在 <paramref name="delay"/> 后切到 <paramref name="open"/>（取消反方向尚未生效的切换）。
    /// 已在等待同一方向时不重新计时：指针连续移动不会把关闭一直往后推。
    /// </summary>
    private void Schedule(bool open, TimeSpan delay)
    {
        if (delay > TimeSpan.Zero && _pending is not null && _pendingOpen == open)
        {
            return;
        }
        _pending?.Dispose();
        _pending = null;
        if (IsOpen == open)
        {
            return;
        }
        if (delay <= TimeSpan.Zero)
        {
            IsOpen = open;
            return;
        }
        _pendingOpen = open;
        _pending = DispatcherTimer.RunOnce(() =>
        {
            _pending = null;
            IsOpen = open;
        }, delay);
    }

    private void Close()
    {
        _pending?.Dispose();
        _pending = null;
        IsOpen = false;
    }

    /// <summary>
    /// 弹层定位的是含阴影留白的整个内容；按放置方向把留白抵消掉，再留出 <see cref="Gap"/>，
    /// 让可见的卡片边缘与触发内容对齐。
    /// </summary>
    private void UpdateOffsets()
    {
        var m = ShadowMargin;
        var name = Placement.ToString();
        double h = 0, v = 0;
        if (name.StartsWith("Top", StringComparison.Ordinal))
        {
            v = m.Bottom - Gap;
        }
        else if (name.StartsWith("Bottom", StringComparison.Ordinal))
        {
            v = Gap - m.Top;
        }
        else if (name.StartsWith("Left", StringComparison.Ordinal))
        {
            h = m.Right - Gap;
        }
        else if (name.StartsWith("Right", StringComparison.Ordinal))
        {
            h = Gap - m.Left;
        }
        if (name.EndsWith("AlignedLeft", StringComparison.Ordinal))
        {
            h = -m.Left;
        }
        else if (name.EndsWith("AlignedRight", StringComparison.Ordinal))
        {
            h = m.Right;
        }
        else if (name.EndsWith("AlignedTop", StringComparison.Ordinal))
        {
            v = -m.Top;
        }
        else if (name.EndsWith("AlignedBottom", StringComparison.Ordinal))
        {
            v = m.Bottom;
        }
        if (_popup is not null)
        {
            _popup.Placement = Placement;
            _popup.HorizontalOffset = h;
            _popup.VerticalOffset = v;
        }
    }
}
