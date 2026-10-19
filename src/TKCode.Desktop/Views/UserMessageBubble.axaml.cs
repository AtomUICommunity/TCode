using Avalonia;
using Avalonia.Controls;
using TKCode.Desktop.Controls.Composer;
using TKCode.Desktop.ViewModels;

namespace TKCode.Desktop.Views;

/// <summary>
/// 用户消息气泡：超过 <see cref="UserMessageItemViewModel.CollapsedLineLimit"/> 行时默认收起，
/// 点击展开显示全部。行数按实际排版计算（自动换行也算），短消息不显示按钮。
/// </summary>
public partial class UserMessageBubble : UserControl
{
    /// <summary>内容超出折叠行数时才显示展开 / 收起按钮。</summary>
    public static readonly StyledProperty<bool> ShowToggleProperty =
        AvaloniaProperty.Register<UserMessageBubble, bool>(nameof(ShowToggle));

    private UserMessageItemViewModel? _message;

    public UserMessageBubble()
    {
        InitializeComponent();
        PlainText.GetObservable(TextBlock.TextProperty).Subscribe(_ => Update());
        RichText.GetObservable(ComposerEditor.DocumentProperty).Subscribe(_ => Update());
    }

    public bool ShowToggle
    {
        get => GetValue(ShowToggleProperty);
        private set => SetValue(ShowToggleProperty, value);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_message is not null)
        {
            _message.PropertyChanged -= OnMessagePropertyChanged;
        }
        _message = DataContext as UserMessageItemViewModel;
        if (_message is not null)
        {
            _message.PropertyChanged += OnMessagePropertyChanged;
        }
        Update();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (e.WidthChanged)
        {
            Update();
        }
    }

    private void OnMessagePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UserMessageItemViewModel.IsExpanded))
        {
            Update();
        }
    }

    /// <summary>按当前宽度排版，决定折叠状态与按钮显隐。</summary>
    private void Update()
    {
        if (_message is null)
        {
            return;
        }
        var limit = UserMessageItemViewModel.CollapsedLineLimit;
        var lines = _message.HasSegments ? RichText.LayoutLineCount(Math.Max(1, RichText.Bounds.Width)) : PlainText.TextLayout.TextLines.Count;
        ShowToggle = lines > limit;
        var expanded = _message.IsExpanded || !ShowToggle;
        PlainText.MaxLines = expanded ? 0 : limit;
        RichText.MaxLines = expanded ? 0 : limit;
    }
}
