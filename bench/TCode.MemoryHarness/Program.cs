using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TCode.Desktop;
using TCode.Desktop.Services;
using TCode.Desktop.ViewModels;

namespace TCode.MemoryHarness;

/// <summary>
/// 内存与空闲 CPU 基准：隔离的 TCODE_HOME + 模拟模型，在真实窗口中跑一个场景，等待稳定后记录
/// footprint（与“活动监视器”的“内存”列一致）、历史峰值、GC 堆，以及场景结束后一段安静时间内的 CPU。
/// 环境变量：MH_SCENARIO=idle|markdown|chat，MH_LABEL=结果标签，MH_HOME=复用的数据目录（测磁盘缓存命中），
/// MH_OUT=结果追加到的文件，MH_QUIET_SECONDS=安静窗口秒数（默认 10）。
/// </summary>
internal static class Program
{
    private static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

    [STAThread]
    public static int Main(string[] args)
    {
        var home = Environment.GetEnvironmentVariable("MH_HOME") is { Length: > 0 } fixedHome
            ? Directory.CreateDirectory(fixedHome).FullName
            : Directory.CreateTempSubdirectory("tcode-mem-home-").FullName;
        var workspace = Directory.CreateDirectory(Path.Combine(home, "workspace")).FullName;
        Environment.SetEnvironmentVariable("TCODE_HOME", home);
        Environment.SetEnvironmentVariable("TCODE_FAKE_PROVIDER", "1");
        Environment.SetEnvironmentVariable("TCODE_EXTERNAL_RUNTIMES", "0");
        var state = new StateService(Path.Combine(home, "state.json"));
        if (!state.Current.Workspaces.Contains(workspace))
        {
            state.Current.Workspaces.Add(workspace);
            state.Save();
        }

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        TCode.Desktop.Program.ConfigureServices(services);
        using var provider = services.BuildServiceProvider();
        App.Services = provider;
        return TCode.Desktop.Program.BuildAvaloniaApp()
            .AfterSetup(b => { Dispatcher.UIThread.Post(() => { _ = RunAsync(provider, workspace); }); })
            .StartWithClassicDesktopLifetime(args);
    }

    private static async Task RunAsync(IServiceProvider provider, string workspace)
    {
        var scenario = Env("MH_SCENARIO", "idle");
        var vm = provider.GetRequiredService<MainWindowViewModel>();
        await Task.Delay(TimeSpan.FromSeconds(4));
        var baseline = Footprint.Current;
        try
        {
            switch (scenario)
            {
                case "markdown":
                    var file = Samples.WriteMarkdown(workspace);
                    vm.Workbench.OpenFile(file);
                    await Task.Delay(TimeSpan.FromSeconds(8));
                    break;
                case "chat":
                    for (var i = 0; i < 60 && vm.Conversation.Picker.Selected is null; i++)
                    {
                        await Task.Delay(100);
                    }
                    vm.Conversation.ComposerText = Samples.ChatPrompt;
                    vm.Conversation.SendCommand.Execute(null);
                    for (var i = 0; i < 300 && vm.Conversation.Active?.IsRunning != false; i++)
                    {
                        await Task.Delay(100);
                    }
                    await Task.Delay(TimeSpan.FromSeconds(3));
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"scenario {scenario} failed: {ex}");
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await Task.Delay(TimeSpan.FromSeconds(1));
        var settled = Footprint.Current;
        var gcHeap = GC.GetGCMemoryInfo();

        // 场景结束后的安静窗口：界面不应再持续渲染
        var process = Process.GetCurrentProcess();
        var quiet = TimeSpan.FromSeconds(int.Parse(Env("MH_QUIET_SECONDS", "10")));
        process.Refresh();
        var cpu0 = process.TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        await Task.Delay(quiet);
        process.Refresh();
        var idleCpu = (process.TotalProcessorTime - cpu0).TotalSeconds / wall.Elapsed.TotalSeconds * 100;

        var line = $"{Env("MH_LABEL", scenario),-18} footprint={Mb(settled),7:F1}MB  delta={Mb(settled - baseline),6:F1}MB  peak={Mb(Footprint.Peak),7:F1}MB  " +
                   $"gcHeap={Mb(gcHeap.HeapSizeBytes),6:F1}MB  gcCommitted={Mb(gcHeap.TotalCommittedBytes),6:F1}MB  quietCpu={idleCpu,5:F1}%";
        Console.WriteLine(line);
        if (Environment.GetEnvironmentVariable("MH_OUT") is { Length: > 0 } output)
        {
            File.AppendAllText(output, line + Environment.NewLine);
        }
        (Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)!.Shutdown();
    }

    private static double Mb(long bytes) => bytes / 1048576.0;
}

/// <summary>场景素材：覆盖已知的内存热点（回退字体的粗体 / 斜体、Emoji、中文、大尺寸图片）。</summary>
internal static class Samples
{
    public const string ChatPrompt = "### 🤝 贡献\n\n**粗体 😀 中文** 与 *斜体 😀 中文*，***粗斜体 🎉***。";

    public static string WriteMarkdown(string workspace)
    {
        var images = Directory.CreateDirectory(Path.Combine(workspace, "images")).FullName;
        // 与 AtomUI README 中的截图同量级：宽度远大于显示宽度
        WritePng(Path.Combine(images, "gallery.png"), 3840, 1463);
        WritePng(Path.Combine(images, "banner.png"), 3312, 1104);
        WritePng(Path.Combine(images, "photo.png"), 1741, 1092);
        var path = Path.Combine(workspace, "README.md");
        File.WriteAllText(path, """
            # 内存基准 😀

            ## 介绍

            普通文字、**粗体中文**、*斜体中文*、***粗斜体 🎉***，以及 `代码`。

            ![gallery](images/gallery.png)

            ### 🤝 贡献

            - **列表里的粗体 😀**
            - *列表里的斜体 🚀*

            ![banner](images/banner.png)

            ![photo](images/photo.png)
            """);
        return path;
    }

    private static unsafe void WritePng(string path, int width, int height)
    {
        if (File.Exists(path))
        {
            return;
        }
        using var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var buffer = bitmap.Lock())
        {
            // 渐变 + 网格：接近界面截图的可压缩性
            for (var y = 0; y < height; y++)
            {
                var row = (uint*)((byte*)buffer.Address + y * buffer.RowBytes);
                for (var x = 0; x < width; x++)
                {
                    var grid = x % 64 == 0 || y % 64 == 0 ? 60u : 0u;
                    var r = (uint)(x * 255 / width) ^ grid;
                    var g = (uint)(y * 255 / height) ^ grid;
                    row[x] = 0xFF000000u | (r << 16) | (g << 8) | 0x80u;
                }
            }
        }
        bitmap.Save(path, new PngBitmapEncoderOptions());
    }
}

internal static class Footprint
{
    [DllImport("libSystem.dylib")]
    private static extern int proc_pid_rusage(int pid, int flavor, ulong[] buffer);

    private static ulong[] Info()
    {
        var buffer = new ulong[64];
        proc_pid_rusage(Environment.ProcessId, 4, buffer); // RUSAGE_INFO_V4
        return buffer;
    }

    // rusage_info_v4：uuid 占 2 个 ulong；ri_phys_footprint 是其后第 8 个字段，ri_lifetime_max_phys_footprint 是第 29 个。
    // 其他平台没有 footprint，用工作集近似
    public static long Current => OperatingSystem.IsMacOS() ? (long)Info()[2 + 7] : Process.GetCurrentProcess().WorkingSet64;

    public static long Peak => OperatingSystem.IsMacOS() ? (long)Info()[2 + 28] : Process.GetCurrentProcess().PeakWorkingSet64;
}
