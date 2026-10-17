using Avalonia.Controls;
using Avalonia.Interactivity;
using TKCode.Desktop.ViewModels;

namespace TKCode.Desktop.Views;

public partial class RolePickerView : UserControl
{
    private RolePickerViewModel? _vm;

    public RolePickerView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.CloseRequested -= Close;
        }
        _vm = DataContext as RolePickerViewModel;
        if (_vm is not null)
        {
            _vm.CloseRequested += Close;
        }
    }

    public void Toggle() => Popup.IsOpen = !Popup.IsOpen;

    private void Close() => Popup.IsOpen = false;

    private void OnChipClick(object? sender, RoutedEventArgs e) => Toggle();
}
