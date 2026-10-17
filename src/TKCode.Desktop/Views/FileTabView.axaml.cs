using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using TKCode.Desktop.ViewModels;

namespace TKCode.Desktop.Views;

public partial class FileTabView : UserControl
{
    public FileTabView()
    {
        InitializeComponent();
    }

    private void OnRevealInExplorerClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is FileTabViewModel file && TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel vm)
        {
            vm.IsExplorerOpen = true;
            vm.Explorer.Reveal(file.FilePath);
        }
    }

    private async void OnCopyPathClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is FileTabViewModel file && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(file.FilePath);
        }
    }
}
