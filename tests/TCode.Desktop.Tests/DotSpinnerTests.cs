using Avalonia.Controls;
using Avalonia.Threading;
using TCode.Desktop.Controls;
using Xunit;

namespace TCode.Desktop.Tests;

public sealed class DotSpinnerTests
{
    // 回归：退场后的启动页只隐藏了自己，里面的转圈仍在每 110ms 重绘，空闲时持续占用 CPU
    [Fact]
    public async Task Spinner_StopsWhileAncestorHidden_AndResumesWhenShown()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            var spinner = new DotSpinner { Width = 12, Height = 12 };
            var host = new Border { Child = new Panel { Children = { spinner } } };
            var window = new Window { Width = 200, Height = 200, Content = host };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.True(spinner.IsAnimating);

            host.IsVisible = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(spinner.IsAnimating);

            host.IsVisible = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(spinner.IsAnimating);

            spinner.IsVisible = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(spinner.IsAnimating);
            window.Close();
        }, TestContext.Current.CancellationToken);
    }
}
