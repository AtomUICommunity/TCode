using Avalonia.Controls;
using Avalonia.Interactivity;
using TKCode.Desktop.ViewModels;

namespace TKCode.Desktop.Views;

public partial class ShellPickerView : UserControl
{
    private ShellPickerViewModel? _vm;

    public ShellPickerView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.CloseRequested -= Close;
        }
        _vm = DataContext as ShellPickerViewModel;
        if (_vm is not null)
        {
            _vm.CloseRequested += Close;
        }
    }

    private void Close() => Popup.IsOpen = false;

    private void OnChipClick(object? sender, RoutedEventArgs e) => Popup.IsOpen = !Popup.IsOpen;
}
