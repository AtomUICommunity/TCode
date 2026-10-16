using Avalonia.Controls;
using Avalonia.Threading;
using TCode.Desktop.Motion;
using TCode.Desktop.Views;
using Xunit;

namespace TCode.Desktop.Tests;

public sealed class SplashTests
{
    [Theory]
    [InlineData("程序员修 bug——越修越多", "程序员修 bug", "——", "越修越多")]
    [InlineData("Shipping on a Friday evening — brave, very brave", "Shipping on a Friday evening", "—", "brave, very brave")]
    [InlineData("no separator", "no separator", "", "")]
    public void Split_SeparatesSetupAndPunchline(string text, string setup, string dash, string punch) =>
        Assert.Equal((setup, dash, punch), SplashView.Split(text));

    [Fact]
    public async Task Splash_Entrance_ThenDismiss()
    {
        await HeadlessApp.Session.Dispatch(() => HeadlessApp.WithMotion(() =>
        {
            var splash = new SplashView();
            var window = new AtomUI.Desktop.Controls.Window { Width = 900, Height = 600, Content = splash };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            HeadlessApp.Step(window, 16);
            HeadlessApp.Save(window, "splash-enter");
            // 歇后语：前半句先出现（入场 320ms 后），后半句再过 700ms 才揭晓
            var setup = splash.FindControl<TextBlock>("QuipSetup")!;
            var punch = splash.FindControl<TextBlock>("QuipPunch")!;
            HeadlessApp.Step(window, 600);
            Assert.True(setup.Opacity > 0.5);
            Assert.True(punch.Opacity < 0.05);
            // 后台很快就绪：仍要等到最短展示时间（1.8s）才退场，让第一句歇后语讲完
            splash.Dismiss();
            HeadlessApp.Step(window, 500);
            Assert.True(punch.Opacity > 0.05);
            HeadlessApp.Step(window, 400);
            Assert.True(splash.IsHitTestVisible);
            HeadlessApp.Save(window, "splash");
            Assert.NotEqual("", punch.Text);
            for (var t = MotionTicker.Now; MotionTicker.Now - t < TimeSpan.FromMilliseconds(400); )
            {
                HeadlessApp.Step(window, 16);
            }
            Assert.False(splash.IsHitTestVisible);
            Assert.True(splash.IsVisible);
            Assert.True(splash.Opacity is > 0.01 and < 0.99, $"opacity {splash.Opacity}");
            HeadlessApp.Save(window, "splash-exit");
            MotionTicker.FinishAll();
            Assert.False(splash.IsVisible);
            window.Close();
        }), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Splash_MotionDisabled_DismissesImmediately()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            var splash = new SplashView();
            var window = new AtomUI.Desktop.Controls.Window { Width = 600, Height = 400, Content = splash };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            splash.Dismiss();
            Assert.False(splash.IsVisible);
            window.Close();
        }, TestContext.Current.CancellationToken);
    }
}
