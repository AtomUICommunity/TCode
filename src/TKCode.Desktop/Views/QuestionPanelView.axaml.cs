using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using TKCode.Desktop.ViewModels;

namespace TKCode.Desktop.Views;

/// <summary>
/// 问答面板的键盘操作（docs/design/ask-user.md §7.2）：
/// ↑↓ 移动；1–9 直接选（单选并前进 / 多选切换）；Space 多选切换；Enter 选中并前进或提交；
/// ←→ / Tab 切换问题；Cmd/Ctrl+Enter 直接提交；Esc 先退出“其他”文本框，否则拒绝回答。
/// 高亮移到“其他”行时焦点进入文本框，在文本框里 ↑↓ 回到选项。
/// </summary>
public partial class QuestionPanelView : UserControl
{
    private QuestionRequestViewModel? _request;
    private QuestionPageViewModel? _page;

    public QuestionPanelView()
    {
        InitializeComponent();
        // 隧道阶段处理：文本框拿到 Enter / Esc / ↑↓ 之前先由面板决定
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        OtherInput.GotFocus += (_, _) => _page?.SelectOther();
    }

    private QuestionRequestViewModel? Request => DataContext as QuestionRequestViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_request is not null)
        {
            _request.PropertyChanged -= OnRequestPropertyChanged;
        }
        _request = Request;
        if (_request is not null)
        {
            _request.PropertyChanged += OnRequestPropertyChanged;
        }
        AttachPage();
        FocusPanel();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible)
        {
            FocusPanel();
        }
    }

    private void OnRequestPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QuestionRequestViewModel.Current))
        {
            AttachPage();
            FocusPanel();
        }
    }

    private void AttachPage()
    {
        if (_page is not null)
        {
            _page.PropertyChanged -= OnPagePropertyChanged;
        }
        _page = Request?.Current;
        if (_page is not null)
        {
            _page.PropertyChanged += OnPagePropertyChanged;
            OtherInput.PasswordChar = _page.IsSecret ? '•' : default;
        }
    }

    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 高亮移到“其他”行：进入文本框；离开时焦点回到面板
        if (e.PropertyName == nameof(QuestionPageViewModel.IsOtherHighlighted) && _page is { } page)
        {
            if (page.IsOtherHighlighted)
            {
                Dispatcher.UIThread.Post(() => OtherInput.Focus(), DispatcherPriority.Input);
            }
            else if (OtherInput.IsFocused || OtherInput.IsKeyboardFocusWithin)
            {
                Focus();
            }
        }
    }

    /// <summary>面板出现、换题或换请求时把键盘焦点放到面板上（“其他”行高亮时放进文本框）。</summary>
    public void FocusPanel()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsEffectivelyVisible)
            {
                return;
            }
            if (_page is { IsOtherHighlighted: true })
            {
                OtherInput.Focus();
            }
            else
            {
                Focus();
            }
        }, DispatcherPriority.Input);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (Request is { IsDecided: false } request && HandleKey(request, e.Key, e.KeyModifiers, OtherInput.IsKeyboardFocusWithin))
        {
            e.Handled = true;
        }
    }

    /// <summary>按键 → 面板操作；返回 true 表示已处理。<paramref name="inText"/> 为焦点在“其他”文本框里。</summary>
    internal static bool HandleKey(QuestionRequestViewModel request, Key key, KeyModifiers modifiers, bool inText)
    {
        var command = modifiers.HasFlag(KeyModifiers.Meta) || modifiers.HasFlag(KeyModifiers.Control);
        var page = request.Current;
        switch (key)
        {
            case Key.Enter when command:
                request.Submit();
                return true;
            case Key.Escape:
                if (inText && page is not null && page.Options.Count > 0)
                {
                    page.Highlight = 0; // 退出文本框，回到选项
                    return true;
                }
                request.Decline();
                return true;
            case Key.Up when page is not null:
                page.MoveHighlight(-1);
                return true;
            case Key.Down when page is not null:
                page.MoveHighlight(1);
                return true;
            case Key.Tab:
                request.MoveTab(modifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                return true;
            case Key.Left or Key.Right when !inText:
                request.MoveTab(key == Key.Left ? -1 : 1);
                return true;
            case Key.Enter:
                if (page is null)
                {
                    request.Submit(); // 提交页
                }
                else if (page.IsOtherHighlighted || page.MultiSelect)
                {
                    if (page.IsOtherHighlighted && !page.MultiSelect && !string.IsNullOrWhiteSpace(page.OtherText))
                    {
                        page.SelectOther();
                    }
                    request.Advance();
                }
                else if (page.Options.ElementAtOrDefault(page.Highlight) is { } option)
                {
                    request.ChooseCommand.Execute(option);
                }
                return true;
            case Key.Space when !inText && page is not null:
                if (page.Options.ElementAtOrDefault(page.Highlight) is { } highlighted)
                {
                    if (page.MultiSelect)
                    {
                        page.Toggle(highlighted);
                    }
                    else
                    {
                        request.ChooseCommand.Execute(highlighted);
                    }
                }
                return true;
            case >= Key.D1 and <= Key.D9 when !inText && !command && page is not null:
            case >= Key.NumPad1 and <= Key.NumPad9 when !inText && !command && page is not null:
            {
                var index = key >= Key.NumPad1 ? key - Key.NumPad1 : key - Key.D1;
                if (index < page.Options.Count)
                {
                    var option = page.Options[index];
                    if (page.MultiSelect)
                    {
                        page.Toggle(option);
                    }
                    else
                    {
                        request.ChooseCommand.Execute(option);
                    }
                }
                else if (index == page.Options.Count && page.AllowOther)
                {
                    if (page.MultiSelect)
                    {
                        page.ToggleOther();
                    }
                    else
                    {
                        page.SelectOther();
                    }
                }
                return true;
            }
            default:
                return false;
        }
    }
}
