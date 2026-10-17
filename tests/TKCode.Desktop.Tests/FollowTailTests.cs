using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using TKCode.Desktop.Controls;
using Xunit;

namespace TKCode.Desktop.Tests;

public sealed class FollowTailTests
{
    [Fact]
    public async Task FixedHeight_FollowsGrowingText_UntilUserScrollsUp()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            var text = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Text = "line" };
            var viewer = new ScrollViewer { MaxHeight = 240, Content = text, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            FollowTail.SetIsEnabled(viewer, true);
            var window = new Window { Width = 400, Height = 600, Content = new StackPanel { Children = { viewer } } };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            for (var i = 0; i < 60; i++)
            {
                text.Text += "\nstreamed thinking line " + i;
                Dispatcher.UIThread.RunJobs();
            }
            Assert.True(viewer.Bounds.Height <= 240.5, $"height {viewer.Bounds.Height}");
            Assert.True(viewer.Extent.Height > 240);
            Assert.Equal(viewer.Extent.Height - viewer.Viewport.Height, viewer.Offset.Y, 1);

            // 用户向上滚动后不再跟随
            viewer.Offset = new Avalonia.Vector(0, 0);
            Dispatcher.UIThread.RunJobs();
            text.Text += "\nmore";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, viewer.Offset.Y);
            window.Close();
        }, TestContext.Current.CancellationToken);
    }
}
