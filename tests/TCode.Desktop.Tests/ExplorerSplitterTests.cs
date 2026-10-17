using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TCode.Desktop.Controls;
using TCode.Desktop.Services;
using TCode.Desktop.ViewModels;
using TCode.Desktop.Views;
using Xunit;

namespace TCode.Desktop.Tests;

/// <summary>
/// 文件树分割条：编辑区关闭（宽 0 的列夹在中间）时仍能拖动，宽度与对话栏互换。
/// </summary>
public sealed class ExplorerSplitterTests
{
    [Fact]
    public async Task ExplorerSplitter_ResizesAgainstConversation_WhenWorkbenchClosed()
    {
        var home = Directory.CreateTempSubdirectory("tcode-ui-home-").FullName;
        Environment.SetEnvironmentVariable("TCODE_HOME", home);
        Environment.SetEnvironmentVariable("TCODE_AGENTHOST_DIR", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/TCode.AgentHost/bin/Debug/net10.0")));
        Environment.SetEnvironmentVariable("TCODE_FAKE_PROVIDER", "1");
        Environment.SetEnvironmentVariable("TCODE_EXTERNAL_RUNTIMES", "0");

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        Program.ConfigureServices(services);
        await using var provider = services.BuildServiceProvider();

        await HeadlessApp.Session.Dispatch(async () =>
        {
            var vm = provider.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 800 };
            window.Show();
            vm.IsWorkbenchOpen = false;
            vm.IsExplorerOpen = true;
            await WaitUntil(() => window.FindControl<DockPanel>("ExplorerColumn")!.Bounds.Width >= 199, "explorer open");

            var splitter = window.FindControl<PanelSplitter>("ExplorerSplitter")!;
            var explorer = window.FindControl<DockPanel>("ExplorerColumn")!;
            var before = explorer.Bounds.Width;

            // 向左拖 80：文件树变宽
            Drag(splitter, -80);
            await Settle();
            Assert.Equal(before + 80, explorer.Bounds.Width, 1);

            // 向右拖 40：文件树变窄
            Drag(splitter, 40);
            await Settle();
            Assert.Equal(before + 40, explorer.Bounds.Width, 1);

            // 拖到超过 1/4 上限（420）后，开合编辑区不会把文件树缩回去
            Drag(splitter, -(before + 200 - explorer.Bounds.Width));
            await Settle();
            var dragged = explorer.Bounds.Width;
            Assert.True(dragged > 420, $"dragged width {dragged}");
            vm.IsWorkbenchOpen = true;
            await WaitUntil(() => window.FindControl<DockPanel>("EditorColumn")!.Bounds.Width >= 319, "workbench open");
            vm.IsWorkbenchOpen = false;
            await WaitUntil(() => !window.FindControl<DockPanel>("EditorColumn")!.IsVisible, "workbench closed");
            Assert.Equal(dragged, explorer.Bounds.Width, 1);
            vm.IsExplorerOpen = false;
            await WaitUntil(() => !explorer.IsVisible, "explorer closed");
            vm.IsExplorerOpen = true;
            await WaitUntil(() => explorer.IsVisible && Math.Abs(explorer.Bounds.Width - dragged) < 1, "explorer reopened at dragged width");

            // 不低于最小宽度
            Drag(splitter, 1000);
            await Settle();
            Assert.Equal(200, explorer.Bounds.Width, 1);
            HeadlessApp.Save(window, "explorer-splitter");
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private static void Drag(Thumb thumb, double dx)
    {
        thumb.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragStartedEvent, Vector = default });
        thumb.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragDeltaEvent, Vector = new Avalonia.Vector(dx, 0) });
        thumb.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragCompletedEvent, Vector = new Avalonia.Vector(dx, 0) });
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 20_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }
        await Settle();
    }

    private static async Task Settle()
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
        }
    }
}
