using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TKCode.Desktop.ViewModels;

namespace TKCode.Desktop.Views;

public partial class PermissionPickerView : UserControl
{
    private PermissionPickerViewModel? _vm;

    public PermissionPickerView()
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
        _vm = DataContext as PermissionPickerViewModel;
        if (_vm is not null)
        {
            _vm.CloseRequested += Close;
        }
    }

    public void Toggle() => Popup.IsOpen = !Popup.IsOpen;

    private void Close() => Popup.IsOpen = false;

    private void OnChipClick(object? sender, RoutedEventArgs e) => Toggle();

    private void OnPopupClosed(object? sender, EventArgs e) => _vm?.OnClosed();

    /// <summary>Esc：确认卡片先回到三档列表，再关闭弹层。</summary>
    private void OnPopupKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _vm is null)
        {
            return;
        }
        if (_vm.IsConfirmingFullAccess)
        {
            _vm.CancelConfirmCommand.Execute(null);
        }
        else
        {
            Close();
        }
        e.Handled = true;
    }
}
