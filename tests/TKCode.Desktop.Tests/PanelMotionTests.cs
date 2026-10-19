using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TKCode.Desktop.Motion;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>
/// 面板开合动画（真实 MainWindow，不启动 Agent Host）：列宽连续变化、动画期间会话视图固定宽度（降载），结束后与无动效时一致。
/// </summary>
public sealed class PanelMotionTests
{
    [Fact]
    public async Task Workbench_AndSidebar_AnimateWidths()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-panel-home-").FullName;
        Environment.SetEnvironmentVariable("TKCODE_HOME", home);
        Environment.SetEnvironmentVariable("TKCODE_EXTERNAL_RUNTIMES", "0");
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical));
        Program.ConfigureServices(services);
        await using var provider = services.BuildServiceProvider();

        await HeadlessApp.Session.Dispatch(() => HeadlessApp.WithMotion(() =>
        {
            var vm = provider.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 760 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var editor = window.FindControl<DockPanel>("EditorColumn")!;
            var grid = window.FindControl<Grid>("MainGrid")!;
            var transcript = window.FindControl<Control>("Transcript")!;

            // 打开编辑区：列宽从 0 逐帧增长，编辑区内容与会话视图在动画期间固定宽度
            vm.IsWorkbenchOpen = true;
            HeadlessApp.Step(window, 50);
            var mid = grid.ColumnDefinitions[2].ActualWidth;
            Assert.InRange(mid, 1, 500);
            Assert.True(editor.IsSet(Control.WidthProperty));
            Assert.True(transcript.IsSet(Control.WidthProperty));
            HeadlessApp.Save(window, "motion-workbench-mid");
            MotionTicker.FinishAll();
            window.UpdateLayout();
            Assert.Equal(520, grid.ColumnDefinitions[2].ActualWidth, 1);
            Assert.False(editor.IsSet(Control.WidthProperty));
            Assert.False(transcript.IsSet(Control.WidthProperty));

            // 中途反向：从当前宽度收回
            vm.IsWorkbenchOpen = false;
            HeadlessApp.Step(window, 40);
            Assert.InRange(grid.ColumnDefinitions[2].ActualWidth, 200, 519);
            vm.IsWorkbenchOpen = true;
            MotionTicker.FinishAll();
            window.UpdateLayout();
            Assert.Equal(520, grid.ColumnDefinitions[2].ActualWidth, 1);

            // 侧栏：宽度 256 → 0 逐帧收起
            var sidebar = window.FindControl<Reveal>("SidebarReveal")!;
            Assert.Equal(256, sidebar.Bounds.Width, 1);
            vm.IsSidebarOpen = false;
            HeadlessApp.Step(window, 50);
            Assert.InRange(sidebar.Bounds.Width, 1, 255);
            Assert.True(transcript.IsSet(Control.WidthProperty));
            HeadlessApp.Save(window, "motion-sidebar-mid");
            MotionTicker.FinishAll();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal(0, sidebar.Bounds.Width, 1);
            Assert.False(transcript.IsSet(Control.WidthProperty));
            window.Close();
        }), TestContext.Current.CancellationToken);
    }
}
