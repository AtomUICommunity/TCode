using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using TCode.Desktop.ViewModels;

namespace TCode.Desktop.Views;

public partial class ModelPickerView : UserControl
{
    private ModelPickerViewModel? _vm;

    public ModelPickerView()
    {
        InitializeComponent();
        Popup.AddHandler(KeyDownEvent, OnPopupKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.CloseRequested -= Close;
        }
        _vm = DataContext as ModelPickerViewModel;
        if (_vm is not null)
        {
            _vm.CloseRequested += Close;
        }
    }

    public void Toggle() => Popup.IsOpen = !Popup.IsOpen;

    private void Close() => Popup.IsOpen = false;

    private void OnChipClick(object? sender, RoutedEventArgs e) => Toggle();

    private void OnPopupOpened(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => SearchBox.Focus(), DispatcherPriority.Input);

    private void OnPopupClosed(object? sender, EventArgs e) => _vm?.OnClosed();

    /// <summary>⌘/Ctrl + 1…9 选择列表中的模型；Esc 先收起子菜单，再关闭弹层。</summary>
    private void OnPopupKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }
        var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (e.KeyModifiers == command && e.Key is >= Key.D1 and <= Key.D9)
        {
            e.Handled = _vm.SelectByShortcut(e.Key - Key.D0);
        }
        else if (e.Key == Key.Escape)
        {
            if (_vm.ExpandedParameter is not null)
            {
                _vm.CollapseParameter();
            }
            else
            {
                Close();
            }
            e.Handled = true;
        }
    }
}
