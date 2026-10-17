using System.Text;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using TCode.Desktop.Services;
using TCode.Desktop.Terminal;
using TCode.Desktop.ViewModels;
using TCode.Desktop.Views;
using TCode.Terminal.Core;
using Xunit;

namespace TCode.Desktop.Tests;

/// <summary>
/// 终端标签的界面（06 §2、05 §13）：视图模型显示一帧（来自真实的核心解析，不经输入模拟），渲染终端标签并截图。
/// 状态提示（启动中、退出）覆盖在终端之上。
/// </summary>
public sealed class TerminalTabSnapshotTests
{
    private static FrameSnapshot Frame(string text, int cols = 60, int rows = 10)
    {
        using var core = TerminalCore.Create(new TerminalCoreOptions(new TerminalSize(cols, rows, 8, 16)));
        core.SetDefaultColors(TerminalTheme.ToCorePalette(TerminalTheme.Dark), ColorScheme.Dark);
        core.Write(Encoding.UTF8.GetBytes(text));
        return core.CaptureFrame(force: true)!;
    }

    [Fact]
    public async Task RendersAShellFrameWithStatusOverlay()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            var hosts = new PtyHostService(NullLogger<PtyHostService>.Instance);
            var tab = new TerminalTabViewModel(hosts, cwd: null);
            var view = new TerminalTabView { DataContext = tab };
            var window = new Window { Width = 640, Height = 260, Content = view, Background = Brushes.Black };
            window.Show();

            tab.Display(Frame("\x1b[1;32mtcode\x1b[0m ~/code $ ls\r\n\x1b[34msrc\x1b[0m  tests  README.md\r\n中文宽字符 ✓ \x1b[7m inverse \x1b[0m\r\n$ "));
            window.UpdateLayout();
            Assert.Equal(60, tab.Frame!.Columns);
            HeadlessApp.Save(window, "terminal-tab-shell");

            tab.ShowStatus("正在启动终端…");
            window.UpdateLayout();
            HeadlessApp.Save(window, "terminal-tab-status");
            Assert.Equal("正在启动终端…", tab.Status);
            window.Close();
        }, CancellationToken.None);
    }
}
