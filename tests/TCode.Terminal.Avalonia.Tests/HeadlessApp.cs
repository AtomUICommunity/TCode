using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;

[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace TCode.Terminal.Avalonia.Tests;

/// <summary>无头 Avalonia + Skia（真实字体光栅化），用于终端视图的截图测试（11-testing-benchmarks.md L4）。</summary>
public sealed class TestApp : Application;

public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    private static readonly Lazy<HeadlessUnitTestSession> Shared = new(() => HeadlessUnitTestSession.StartNew(typeof(HeadlessApp)));

    public static HeadlessUnitTestSession Session => Shared.Value;

    /// <summary>截图目录：环境变量 TCODE_SNAPSHOT_DIR，默认测试输出目录下的 snapshots/。</summary>
    public static string Save(TopLevel topLevel, string name)
    {
        var dir = Environment.GetEnvironmentVariable("TCODE_SNAPSHOT_DIR") ?? Path.Combine(AppContext.BaseDirectory, "snapshots");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".png");
        using var frame = topLevel.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame rendered.");
        using var file = File.Create(path);
        frame.Save(file, new global::Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        return path;
    }
}
