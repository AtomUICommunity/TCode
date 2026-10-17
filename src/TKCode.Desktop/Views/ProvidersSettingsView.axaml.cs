using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using TKCode.Desktop.ViewModels;

namespace TKCode.Desktop.Views;

public partial class ProvidersSettingsView : UserControl
{
    public ProvidersSettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm)
            {
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(SettingsViewModel.IsPickingTemplate) && vm.IsPickingTemplate)
                    {
                        Dispatcher.UIThread.Post(() => TemplateSearchBox.Focus(), DispatcherPriority.Input);
                    }
                };
            }
        };
        // 选择框打开时 Esc 关闭
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && DataContext is SettingsViewModel { IsPickingTemplate: true } vm)
            {
                vm.CloseTemplatePickerCommand.Execute(null);
                e.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void OnTemplateBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.CloseTemplatePickerCommand.Execute(null);
        }
    }
}
