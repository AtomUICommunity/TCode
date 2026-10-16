using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TCode.Desktop.Localization;
using TCode.Desktop.ViewModels;

namespace TCode.Desktop.Views;

public partial class ProjectPickerView : UserControl
{
    private ProjectPickerViewModel? _vm;

    public ProjectPickerView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.CloseRequested -= Close;
            _vm.NewProjectRequested -= OnNewProjectRequested;
        }
        _vm = DataContext as ProjectPickerViewModel;
        if (_vm is not null)
        {
            _vm.CloseRequested += Close;
            _vm.NewProjectRequested += OnNewProjectRequested;
        }
    }

    public void Open() => Popup.IsOpen = true;

    private void Close() => Popup.IsOpen = false;

    private void OnTriggerClick(object? sender, RoutedEventArgs e) => Popup.IsOpen = !Popup.IsOpen;

    private void OnPopupOpened(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => SearchBox.Focus(), DispatcherPriority.Input);

    private void OnPopupClosed(object? sender, EventArgs e) => _vm?.OnClosed();

    private async void OnNewProjectRequested()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.Get(TCodeLangResourceKind.SelectFolderTitle),
            AllowMultiple = false,
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
        {
            _vm?.AddProject(path);
        }
    }
}
