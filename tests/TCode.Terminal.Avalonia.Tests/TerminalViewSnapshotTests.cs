using System.Text;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using TCode.Terminal.Core;
using Xunit;

namespace TCode.Terminal.Avalonia.Tests;

public sealed class TerminalViewSnapshotTests
{
    /// <summary>M0：静态网格——ANSI 颜色、宽字符（CJK）、box drawing、光标。</summary>
    [Fact]
    public async Task RendersStaticGrid()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            using var core = TerminalCore.Create(new TerminalCoreOptions(new TerminalSize(40, 8, 8, 16)));
            core.Write(Encoding.UTF8.GetBytes(
                "\x1b[1;32m~/code\x1b[0m $ ls\r\n" +
                "\x1b[34msrc\x1b[0m  \x1b[34mtests\x1b[0m  README.md  终端.md\r\n" +
                "┌──────┬──────┐\r\n" +
                "│ 名称 │ 状态 │\r\n" +
                "└──────┴──────┘\r\n" +
                "\x1b[30;43m warn \x1b[0m \x1b[38;2;255;100;0mtruecolor\x1b[0m\r\n$ "));
            var frame = core.CaptureFrame();

            var view = new TerminalView { Snapshot = frame, FontFamily = new FontFamily("Menlo, Consolas, DejaVu Sans Mono"), FontSize = 13 };
            var window = new Window { Content = view, SizeToContent = SizeToContent.WidthAndHeight, Background = Brushes.Black };
            window.Show();
            window.UpdateLayout();

            var m = view.Metrics;
            Assert.True(m.CellWidth > 0 && m.CellHeight > m.CellWidth, $"metrics {m}");
            Assert.Equal(40 * m.CellWidth + 2 * 6, (int)Math.Round(view.Bounds.Width * m.Scaling));

            var path = HeadlessApp.Save(window, "terminal-static-grid");
            using var bitmap = new Bitmap(path);
            Assert.True(bitmap.PixelSize.Width > 0);
            window.Close();
        }, CancellationToken.None);
    }
}
