using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform;
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
    }

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
        if (Bounds.Size is { Width: > 0, Height: > 0 } size)
        {
            Apply(size);
        }
    }

    private void OnKeyPressed(object? sender, TerminalKeyEventArgs e)
    {
        if (_attached is { } vm)
        {
            e.Handled = vm.HandleKey(e);
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
