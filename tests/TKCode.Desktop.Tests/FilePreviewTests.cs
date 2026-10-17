using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TKCode.Desktop.Controls;
using TKCode.Desktop.Previews;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>
/// 文件预览：按文件探测选出查看方式（图片 / Markdown / 源码），多种方式时可切换；
/// 图片显示棋盘格与信息栏并可缩放，动图解码出全部帧；源码按语言着色；二进制与过大文件显示提示。
/// </summary>
public sealed class FilePreviewTests
{
    [Fact]
    public void Registry_MatchesProvidersByContent()
    {
        var dir = Directory.CreateTempSubdirectory("tkcode-preview-").FullName;
        Assert.Equal(["Image"], Match(dir, "a.png", PngBytes()));
        Assert.Equal(["Image"], Match(dir, "anim.gif", AnimatedGif()));
        // 扩展名不对也能按文件头识别
        Assert.Equal(["Image"], Match(dir, "noext", PngBytes()));
        Assert.Equal(["Image", "Code"], Match(dir, "logo.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"24\" height=\"24\"/>"u8.ToArray()));
        Assert.Equal(["Markdown", "Code"], Match(dir, "README.md", "# hi"u8.ToArray()));
        Assert.Equal(["Code"], Match(dir, "data.json", "{}"u8.ToArray()));
        Assert.Empty(Match(dir, "blob.bin", [1, 0, 2, 3]));

        Assert.Equal("cs", CodePreviewProvider.LanguageFor("App.cs"));
        Assert.Equal("dockerfile", CodePreviewProvider.LanguageFor("Dockerfile"));
        Assert.Equal("shell", CodePreviewProvider.LanguageFor(".env.local"));
        Assert.Equal("json", CodePreviewProvider.LanguageFor("global.json"));
        Assert.Equal("xml", CodePreviewProvider.LanguageFor("TKCode.slnx"));
    }

    private static string[] Match(string dir, string name, byte[] bytes)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        var probe = FileProbe.ReadAsync(path, CancellationToken.None).GetAwaiter().GetResult();
        return FilePreviewRegistry.Default.Match(probe).Select(p => p.GetType().Name.Replace("PreviewProvider", "")).ToArray();
    }

    [Fact]
    public async Task FileTab_RendersEachPreviewKind()
    {
        var root = Directory.CreateTempSubdirectory("tkcode-preview-ui-").FullName;
        File.WriteAllBytes(Path.Combine(root, "logo.png"), PngBytes());
        File.WriteAllBytes(Path.Combine(root, "spinner.gif"), AnimatedGif());
        File.WriteAllText(Path.Combine(root, "logo.svg"),
            """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24" fill="none" stroke="#4F8CFF" stroke-width="2" stroke-linecap="round"><path d="M4.5 6.5h15"/><path d="M12 6.5v9"/><rect x="8.5" y="18" width="7" height="2.5" fill="#8B7CF6" stroke="none"/></svg>""");
        File.WriteAllText(Path.Combine(root, "global.json"), """
            {
              "sdk": {
                "version": "10.0.100",
                "rollForward": "latestFeature"
              },
              "msbuild-sdks": { "Avalonia": true, "count": 3, "nothing": null }
            }
            """);
        File.WriteAllText(Path.Combine(root, "App.cs"), "namespace Demo;\n\npublic static class App\n{\n\t// tab indented comment\n    public static int Main() => \"中文字符串\".Length;\n}\n");
        File.WriteAllBytes(Path.Combine(root, "blob.bin"), [1, 0, 2, 3]);
        File.WriteAllText(Path.Combine(root, "huge.txt"), new string('x', (int)CodePreviewProvider.MaxBytes + 10));

        await HeadlessApp.Session.Dispatch(async () =>
        {
            var host = new ContentControl();
            var window = new Window { Width = 760, Height = 480, Content = host };
            window[!TemplatedControl.BackgroundProperty] = window.GetResourceObservable("TKCode.WindowBg").ToBinding();
            window.Show();

            async Task<FileTabViewModel> Open(string name)
            {
                var tab = new FileTabViewModel(Path.Combine(root, name), root);
                host.Content = new FileTabView { DataContext = tab };
                await WaitUntil(() => tab.Preview is not null || tab.HasNotice, name);
                await Settle();
                return tab;
            }

            // PNG：棋盘格 + 信息栏；适应窗口时位图不放大
            var png = await Open("logo.png");
            var image = Assert.IsType<ImagePreviewViewModel>(png.Preview);
            Assert.False(png.HasViewSwitch);
            Assert.Equal(new PixelSize(64, 48), image.PixelSize);
            Assert.True(image.IsFit);
            Assert.Equal(1, image.Zoom);
            Assert.StartsWith("64 × 48 · ", image.Info);
            HeadlessApp.Save(window, "file-image-png");
            image.ZoomInCommand.Execute(null);
            image.ZoomInCommand.Execute(null);
            image.ZoomInCommand.Execute(null);
            await Settle();
            Assert.False(image.IsFit);
            Assert.Equal(3, image.Zoom);
            HeadlessApp.Save(window, "file-image-zoomed");
            image.FitCommand.Execute(null);
            Assert.Equal(1, image.Zoom);

            // 动图：解码全部帧
            var gif = await Open("spinner.gif");
            var animation = Assert.IsType<ImagePreviewViewModel>(gif.Preview);
            Assert.True(animation.IsAnimated);
            Assert.Equal(2, animation.Frames.Count);
            Assert.Contains("2", animation.Info);

            // SVG：矢量图适应窗口时放大；可切换到源码
            var svg = await Open("logo.svg");
            Assert.True(svg.HasViewSwitch);
            var vector = Assert.IsType<ImagePreviewViewModel>(svg.Preview);
            Assert.True(vector.IsVector);
            Assert.True(vector.Zoom > 1);
            HeadlessApp.Save(window, "file-image-svg");
            await svg.SelectViewCommand.ExecuteAsync(svg.Views[1]);
            Assert.IsType<CodePreviewViewModel>(svg.Preview);
            await WaitHighlighted(window);
            HeadlessApp.Save(window, "file-svg-source");

            // 源码：JSON / C# 着色
            var json = await Open("global.json");
            Assert.Equal("json", Assert.IsType<CodePreviewViewModel>(json.Preview).Language);
            await WaitHighlighted(window);
            HeadlessApp.Save(window, "file-code-json");
            await Open("App.cs");
            await WaitHighlighted(window);
            var code = window.GetVisualDescendants().OfType<CodeView>().Single();
            Assert.Equal(7, code.LineCount);
            code.SelectAll();
            Assert.StartsWith("namespace Demo;", code.SelectedText);
            await Settle();
            HeadlessApp.Save(window, "file-code-csharp");

            // 提示：二进制 / 过大
            var blob = await Open("blob.bin");
            Assert.True(blob.HasNotice);
            Assert.Empty(blob.Views);
            var huge = await Open("huge.txt");
            Assert.True(huge.HasNotice);
            Assert.Contains("MB", huge.Notice);

            png.Dispose();
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private static async Task WaitHighlighted(Window window)
    {
        await WaitUntil(() => window.GetVisualDescendants().OfType<CodeView>().SingleOrDefault() is { IsHighlighted: true }, "highlighted");
        await Settle();
    }

    /// <summary>64×48 的半透明渐变（检查棋盘格）。</summary>
    private static byte[] PngBytes()
    {
        using var bitmap = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(64, 48, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Unpremul));
        for (var y = 0; y < 48; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                // 从左到右由透明到不透明
                bitmap.SetPixel(x, y, new SkiaSharp.SKColor((byte)(0x4F + x * 2), (byte)(0x7C + y), 0xF6, (byte)(x * 4)));
            }
        }
        using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>两帧 2×2 的 GIF89a 动图（每个像素前插入清除码，LZW 码长始终为 3 位）。</summary>
    private static byte[] AnimatedGif()
    {
        var gif = new List<byte>();
        gif.AddRange("GIF89a"u8.ToArray());
        gif.AddRange([2, 0, 2, 0, 0b1000_0001, 0, 0]); // 2×2，全局调色板 4 色
        gif.AddRange([0, 0, 0, 0xFF, 0, 0, 0, 0xFF, 0, 0, 0, 0xFF]);
        gif.AddRange([0x21, 0xFF, 11, .. "NETSCAPE2.0"u8.ToArray(), 3, 1, 0, 0, 0]); // 循环播放
        foreach (var pixels in new[] { new byte[] { 1, 2, 3, 1 }, [3, 1, 2, 3] })
        {
            gif.AddRange([0x21, 0xF9, 4, 0, 10, 0, 0, 0]); // 帧间隔 100ms
            gif.AddRange([0x2C, 0, 0, 0, 0, 2, 0, 2, 0, 0]);
            gif.Add(2); // LZW 最小码长
            var codes = pixels.SelectMany(p => new[] { 4, (int)p }).Append(5).ToArray();
            var data = new List<byte>();
            int acc = 0, bits = 0;
            foreach (var c in codes)
            {
                acc |= c << bits;
                bits += 3;
                while (bits >= 8)
                {
                    data.Add((byte)acc);
                    acc >>= 8;
                    bits -= 8;
                }
            }
            if (bits > 0)
            {
                data.Add((byte)acc);
            }
            gif.Add((byte)data.Count);
            gif.AddRange(data);
            gif.Add(0);
        }
        gif.Add(0x3B);
        return gif.ToArray();
    }

    private static async Task Settle()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 20_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {what}");
            }
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }
}
