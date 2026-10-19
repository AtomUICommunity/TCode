using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TKCode.Desktop.Icons;
using TKCode.Desktop.Services;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>
/// 输入框的发送 / 停止按钮（TKCode.SendButton）：强调色实心圆，没有描边和投影；
/// 不可用时只是整体变淡，不能像 AtomUI 的 Solid 按钮那样换成描边圆（暗色下像套了一层气泡，亮色下是粗黑圈）。
/// 放大 4 倍截图供人工查看。
/// </summary>
public sealed class SendButtonSnapshotTests
{
    [Fact]
    public async Task SendButton_IsSolidDisk_DisabledOnlyFades_InBothThemes()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-ui-home-").FullName;
        Environment.SetEnvironmentVariable("TKCODE_HOME", home);
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        Program.ConfigureServices(services);
        await using var provider = services.BuildServiceProvider();

        await HeadlessApp.Session.Dispatch(async () =>
        {
            var disabled = Make(LucideKind.ArrowUp, 15, enabled: false);
            var send = Make(LucideKind.ArrowUp, 15, enabled: true);
            var stop = Make(LucideKind.StopFilled, 12, enabled: true);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(8) };
            row.Children.AddRange([disabled, send, stop]);
            var frame = new Border { Child = row, Padding = new Thickness(8, 4), CornerRadius = new CornerRadius(22), BorderThickness = new Thickness(1) };
            frame.Bind(Border.BackgroundProperty, frame.GetResourceObservable("TKCode.ComposerBg"));
            frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("TKCode.ComposerBorder"));
            var window = new Window
            {
                Width = 560, Height = 200,
                Content = new LayoutTransformControl { LayoutTransform = new ScaleTransform(4, 4), Child = frame },
            };
            window.Bind(Window.BackgroundProperty, window.GetResourceObservable("TKCode.WindowBg"));
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // 不可用：圆仍是实心的强调色，只降低透明度；没有描边
            Assert.Equal(new Size(26, 26), send.Bounds.Size);
            Assert.Equal(0.3, Disk(disabled).Opacity, 3);
            Assert.Equal(1, Disk(send).Opacity, 3);
            Assert.Equal(default, Disk(disabled).BorderThickness);
            HeadlessApp.Save(window, "send-button-dark");

            var themes = provider.GetRequiredService<ThemeService>();
            themes.Attach(Application.Current!);
            await themes.SetModeAsync(ThemeMode.Light);
            Dispatcher.UIThread.RunJobs();
            HeadlessApp.Save(window, "send-button-light");
            await themes.SetModeAsync(ThemeMode.Dark);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private static Button Make(LucideKind kind, double size, bool enabled)
    {
        var button = new Button { IsEnabled = enabled };
        button.Theme = (Avalonia.Styling.ControlTheme)Application.Current!.FindResource("TKCode.SendButton")!;
        var icon = new LucideIcon(kind) { Width = size, Height = size };
        icon.Bind(LucideIcon.StrokeBrushProperty, button.GetObservable(Button.ForegroundProperty));
        button.Content = icon;
        return button;
    }

    private static Border Disk(Button button) =>
        button.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Disk");
}
