using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using TKCode.Desktop.Controls;
using TKCode.Desktop.ViewModels;

namespace TKCode.Desktop.Views;

/// <summary>
/// 审批面板的键盘与焦点（docs/design/plan-mode.md §14.1）：
/// ↑↓ 移动；1–N 直接选（选到“继续完善”时进入反馈框）；Enter 确认；E 编辑；在反馈框里打字自动选中“继续完善”；
/// Cmd/Ctrl+Enter 在任何位置确认；Esc 先退出编辑框或反馈框，否则暂不批准。
/// 计划区最高约窗口的 55%；窗口重新激活（从外部编辑器回来）时重读计划文件。
/// </summary>
public partial class PlanReviewPanelView : UserControl
{
    /// <summary>计划区占窗口高度的比例上限。</summary>
    private const double PlanHeightRatio = 0.55;

    /// <summary>编辑框的最小高度（计划很短时也留出编辑空间）。</summary>
    private const double MinEditorHeight = 200;

    private PlanReviewViewModel? _review;
    private TopLevel? _topLevel;

    public PlanReviewPanelView()
    {
        InitializeComponent();
        // 隧道阶段处理：文本框拿到 Enter / Esc / ↑↓ 之前先由面板决定
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        FeedbackInput.GotFocus += (_, _) =>
        {
            if (_review is { IsReviseHighlighted: false } review)
            {
                review.Highlight = review.ReviseOption.Index;
            }
        };
        ReviseRow.PointerPressed += (_, _) => _review?.Select(_review.ReviseOption);
    }

    private PlanReviewViewModel? Review => DataContext as PlanReviewViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_review is not null)
        {
            _review.PropertyChanged -= OnReviewPropertyChanged;
        }
        _review = Review;
        if (_review is not null)
        {
            _review.PropertyChanged += OnReviewPropertyChanged;
        }
        FocusPanel();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null)
        {
            _topLevel.PropertyChanged += OnTopLevelPropertyChanged;
        }
        if (_topLevel is Window window)
        {
            window.Activated += OnWindowActivated;
        }
        UiZoom.Changed += UpdatePlanHeight;
        UpdatePlanHeight();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_topLevel is not null)
        {
            _topLevel.PropertyChanged -= OnTopLevelPropertyChanged;
        }
        if (_topLevel is Window window)
        {
            window.Activated -= OnWindowActivated;
        }
        _topLevel = null;
        UiZoom.Changed -= UpdatePlanHeight;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible)
        {
            UpdatePlanHeight();
            FocusPanel();
        }
    }

    private void OnTopLevelPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TopLevel.ClientSizeProperty)
        {
            UpdatePlanHeight();
        }
    }

    /// <summary>计划区最高约窗口的 55%（窗口尺寸换算到缩放后的排版坐标）。</summary>
    private void UpdatePlanHeight()
    {
        if (_topLevel is not { } top)
        {
            return;
        }
        var height = top.ClientSize.Height / Math.Max(UiZoom.Scale, 0.1) * PlanHeightRatio;
        if (height > 0)
        {
            PlanBox.MaxHeight = Math.Max(120, height);
            PlanEditor.MaxHeight = Math.Max(110, height - 2);
        }
    }

    /// <summary>从外部编辑器回来：重读计划文件，有变化就刷新并按“已编辑”处理。</summary>
    private void OnWindowActivated(object? sender, EventArgs e)
    {
        if (IsEffectivelyVisible)
        {
            Review?.RefreshFromFile();
        }
    }

    private void OnReviewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // 选到“继续完善”：进入反馈框；离开时焦点回到面板
            case nameof(PlanReviewViewModel.IsReviseHighlighted) when _review is { } review:
                if (review.IsReviseHighlighted)
                {
                    Dispatcher.UIThread.Post(() => FeedbackInput.Focus(), DispatcherPriority.Input);
                }
                else if (FeedbackInput.IsKeyboardFocusWithin)
                {
                    Focus();
                }
                break;
            case nameof(PlanReviewViewModel.IsEditing) when _review is { } review:
                if (review.IsEditing)
                {
                    // 编辑框与渲染视图一样高（至少能看到十来行），切换时面板不跳动；超出部分在框内滚动
                    var max = double.IsFinite(PlanBox.MaxHeight) ? PlanBox.MaxHeight - 2 : double.PositiveInfinity;
                    PlanEditor.Height = Math.Min(Math.Max(PlanScroll.Bounds.Height, MinEditorHeight), max);
                    Dispatcher.UIThread.Post(() => PlanEditor.Focus(), DispatcherPriority.Input);
                }
                else
                {
                    FocusPanel();
                }
                break;
        }
    }

    /// <summary>面板出现或换请求时把键盘焦点放到面板上（编辑中放进编辑框，“继续完善”选中时放进反馈框）。</summary>
    public void FocusPanel()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsEffectivelyVisible)
            {
                return;
            }
            if (_review is { IsEditing: true })
            {
                PlanEditor.Focus();
            }
            else if (_review is { IsReviseHighlighted: true })
            {
                FeedbackInput.Focus();
            }
            else
            {
                Focus();
            }
        }, DispatcherPriority.Input);
    }

    /// <summary>双击一种批准方式：选中并确认。</summary>
    private void OnOptionDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: PlanReviewOptionViewModel option } && Review is { IsDecided: false } review)
        {
            review.Select(option);
            review.Confirm();
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (Review is { IsDecided: false } review &&
            HandleKey(review, e.Key, e.KeyModifiers, inEditor: PlanEditor.IsKeyboardFocusWithin, inFeedback: FeedbackInput.IsKeyboardFocusWithin))
        {
            e.Handled = true;
        }
    }

    /// <summary>按键 → 面板操作；返回 true 表示已处理。</summary>
    /// <param name="inEditor">焦点在计划的源码编辑框里（Enter、方向键、字母都交给编辑框）。</param>
    /// <param name="inFeedback">焦点在“继续完善”的反馈框里。</param>
    internal static bool HandleKey(PlanReviewViewModel review, Key key, KeyModifiers modifiers, bool inEditor, bool inFeedback)
    {
        var command = modifiers.HasFlag(KeyModifiers.Meta) || modifiers.HasFlag(KeyModifiers.Control);
        var inText = inEditor || inFeedback;
        switch (key)
        {
            case Key.Enter when command:
                review.Confirm();
                return true;
            case Key.Escape:
                if (review.IsEditing)
                {
                    review.ToggleEdit(); // 退出编辑，回到渲染视图
                    return true;
                }
                if (inFeedback)
                {
                    review.Highlight = 0; // 退出反馈框，回到选项
                    return true;
                }
                review.Dismiss();
                return true;
            case Key.Up when !inEditor:
                review.MoveHighlight(-1);
                return true;
            case Key.Down when !inEditor:
                review.MoveHighlight(1);
                return true;
            case Key.Enter when !inEditor:
                review.Confirm();
                return true;
            case Key.E when !inText && modifiers == KeyModifiers.None:
                review.ToggleEdit();
                return true;
            case >= Key.D1 and <= Key.D9 when !inText && !command:
            case >= Key.NumPad1 and <= Key.NumPad9 when !inText && !command:
            {
                var index = key >= Key.NumPad1 ? key - Key.NumPad1 : key - Key.D1;
                if (index < review.Options.Count)
                {
                    review.Select(review.Options[index]);
                }
                return true;
            }
            default:
                return false;
        }
    }
}
