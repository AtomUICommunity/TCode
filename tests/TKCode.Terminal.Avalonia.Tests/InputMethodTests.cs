using System.Text;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using TKCode.Terminal.Core;
using Xunit;

namespace TKCode.Terminal.Avalonia.Tests;

/// <summary>
/// 输入法（06 §5）：光标矩形与组合文字（preedit）。组合文字由平台经 TextInputMethodClient 推送，这里直接调用同一个入口，
/// 不模拟系统输入法。
/// </summary>
public sealed class InputMethodTests
{
    [Fact]
    public async Task PreeditIsDrawnAtTheCursorAndCleared()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            using var core = TerminalCore.Create(new TerminalCoreOptions(new TerminalSize(40, 4, 8, 16)));
            core.Write(Encoding.UTF8.GetBytes("\x1b[1;32m~/code\x1b[0m $ "));
            var view = new TerminalView { Snapshot = core.CaptureFrame()!, FontFamily = new FontFamily("Menlo, Consolas, DejaVu Sans Mono"), FontSize = 13 };
            var window = new Window { Content = view, SizeToContent = SizeToContent.WidthAndHeight, Background = Brushes.Black };
            window.Show();
            window.UpdateLayout();

            // 光标矩形与帧的光标格对齐（内边距 6 DIP，之后每列一个 cell）
            var cursor = view.CursorRectangle;
            var m = view.Metrics;
            var column = view.Snapshot!.Cursor.Column;
            Assert.Equal(9, column);
            var pad = Math.Round(6 * m.Scaling) / m.Scaling;
            Assert.Equal(pad + column * m.CellWidth / m.Scaling, cursor.X, 0.6);
            Assert.True(cursor.Height > 0);

            var client = new TerminalInputMethodClient(view);
            client.SetPreeditText("nihao");
            Assert.Equal("nihao", view.Preedit);
            view.UpdateLayout();
            HeadlessApp.Save(window, "terminal-ime-preedit");

            // 提交（或取消）时组合文字清除
            client.SetPreeditText(null);
            Assert.Null(view.Preedit);
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task WideCharacterPreeditKeepsCellAlignment()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            using var core = TerminalCore.Create(new TerminalCoreOptions(new TerminalSize(30, 3, 8, 16)));
            var view = new TerminalView { Snapshot = core.CaptureFrame()!, FontFamily = new FontFamily("Menlo, Consolas, DejaVu Sans Mono"), FontSize = 13 };
            var window = new Window { Content = view, SizeToContent = SizeToContent.WidthAndHeight, Background = Brushes.Black };
            window.Show();
            window.UpdateLayout();
            new TerminalInputMethodClient(view).SetPreeditText("中文ab");
            view.UpdateLayout();
            HeadlessApp.Save(window, "terminal-ime-preedit-cjk");
            Assert.Equal("中文ab", view.Preedit);
            window.Close();
            return Task.CompletedTask;
        }, CancellationToken.None);
    }
}
