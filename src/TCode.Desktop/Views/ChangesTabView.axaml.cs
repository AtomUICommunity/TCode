using Avalonia.Controls;
using Avalonia.Threading;
using TCode.Desktop.ViewModels;

namespace TCode.Desktop.Views;

public partial class ChangesTabView : UserControl
{
    private ChangesTabViewModel? _vm;

    public ChangesTabView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.RevealRequested -= Reveal;
        }
        _vm = DataContext as ChangesTabViewModel;
        if (_vm is not null)
        {
            _vm.RevealRequested += Reveal;
        }
    }

    /// <summary>“审查”进入时把该文件的行滚到顶部附近（等展开后的布局完成再滚）。</summary>
    private void Reveal(FileChangeViewModel file)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (FileList.ContainerFromItem(file) is { } container)
            {
                container.BringIntoView();
            }
        }, DispatcherPriority.Loaded);
    }
}
