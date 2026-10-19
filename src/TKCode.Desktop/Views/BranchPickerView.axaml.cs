using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using TKCode.Desktop.ViewModels;

namespace TKCode.Desktop.Views;

public partial class BranchPickerView : UserControl
{
    private BranchPickerViewModel? _vm;

    public BranchPickerView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.CloseRequested -= Close;
        }
        _vm = DataContext as BranchPickerViewModel;
        if (_vm is not null)
        {
            _vm.CloseRequested += Close;
        }
    }

    private void Close() => Popup.IsOpen = false;

    private void OnTriggerClick(object? sender, RoutedEventArgs e) => Popup.IsOpen = !Popup.IsOpen;

    private void OnPopupOpened(object? sender, EventArgs e)
    {
        _vm?.OnOpened();
        Dispatcher.UIThread.Post(() => SearchBox.Focus(), DispatcherPriority.Input);
    }

    private void OnPopupClosed(object? sender, EventArgs e) => _vm?.OnClosed();
}
