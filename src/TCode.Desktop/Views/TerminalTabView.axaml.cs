using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Styling;
using TCode.Desktop.ViewModels;
using TCode.Terminal.Avalonia;

namespace TCode.Desktop.Views;

public partial class TerminalTabView : UserControl
{
    private TerminalTabViewModel? _attached;

    public TerminalTabView()
    {
        InitializeComponent();
        Terminal.KeyPressed += OnKeyPressed;
        Terminal.TextEntered += (_, text) => _attached?.HandleText(text);
        Terminal.PointerChanged += (_, e) => _attached?.HandlePointer(e);
        Terminal.SizeChanged += OnSizeChanged;
        Loaded += (_, _) => Terminal.Focus();
        DataContextChanged += OnDataContextChanged;
        ActualThemeVariantChanged += (_, _) => _attached?.SetDarkTheme(IsDark);
        SearchBox.KeyDown += OnSearchKeyDown;
    }

    /// <summary>搜索框内：Esc 关闭，Enter / Shift+Enter 切换；其余按键（输入搜索词）照常进入搜索框。</summary>
    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (_attached is not { IsSearchOpen: true } vm)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Escape:
                vm.CloseSearch();
                Terminal.Focus();
                e.Handled = true;
                break;
            case Key.Enter:
                vm.StepSearch(next: !e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                e.Handled = true;
                break;
        }
    }

    /// <summary>打开搜索栏时把焦点交给搜索框；关闭后交还终端。</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private bool IsDark => ActualThemeVariant == ThemeVariant.Dark || ActualThemeVariant?.InheritVariant == ThemeVariant.Dark;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_attached is { } previous)
        {
            previous.SetClipboardText = null;
            previous.GetClipboardText = null;
            previous.OpenLink = null;
        }
        _attached = DataContext as TerminalTabViewModel;
        if (_attached is null)
        {
            return;
        }
        _attached.SetClipboardText = text => TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard ? clipboard.SetTextAsync(text) : Task.CompletedTask;
        _attached.GetClipboardText = async () =>
        {
            // Avalonia 12 的剪贴板是数据传输模型：先取数据，再取文本
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            {
                return null;
            }
            var data = await clipboard.TryGetDataAsync();
            return data is null ? null : await data.TryGetTextAsync();
        };
        _attached.OpenLink = uri => TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(uri) ?? Task.CompletedTask;
        _attached.SetDarkTheme(IsDark);  // 会话创建（首次布局）之前确定调色板
        if (Bounds.Size is { Width: > 0, Height: > 0 } size)
        {
            Apply(size);
        }
    }

    private void OnKeyPressed(object? sender, TerminalKeyEventArgs e)
    {
        if (_attached is not { } vm)
        {
            return;
        }
        var wasOpen = vm.IsSearchOpen;
        e.Handled = vm.HandleKey(e);
        // ⌘F 打开搜索栏：焦点立即交给搜索框（之后的字符进入搜索词）
        if (!wasOpen && vm.IsSearchOpen)
        {
            Dispatcher.UIThread.Post(FocusSearch);
        }
        else if (wasOpen && !vm.IsSearchOpen)
        {
            Terminal.Focus();
        }
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e) => Apply(e.NewSize);

    private void Apply(Size size)
    {
        if (_attached is not { } vm || size.Width <= 0 || size.Height <= 0)
        {
            return;
        }
        var grid = Terminal.GridSizeFor(size);
        vm.EnsureStarted(grid);
        vm.Resize(grid);
    }
}
