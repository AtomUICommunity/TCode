using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TCode.Desktop.Views;

public partial class TaskOrbView : UserControl
{
    public TaskOrbView()
    {
        InitializeComponent();
    }

    /// <summary>请求打开变更面板（由主窗口接到工作区）。</summary>
    public event Action? OpenChangesRequested;

    private void OnOpenChangesClick(object? sender, RoutedEventArgs e)
    {
        OrbToggle.IsChecked = false;
        OpenChangesRequested?.Invoke();
    }
}
