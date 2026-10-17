using AtomUI;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TKCode.Desktop.Services;
using TKCode.Desktop.Updates;
using TKCode.Desktop.ViewModels;

namespace TKCode.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--smoke"))
        {
            return SmokeMode.Run(args);
        }
        if (args.Contains("--version"))
        {
            // 发布 CI 的冒烟检查与安装后的版本确认
            Console.WriteLine(AppVersionInfo.Display);
            return 0;
        }
        // Windows 安装程序（Packaging/windows/TKCode.iss 的 AppMutex）据此等待 TKCode 退出后再替换文件
        using var instance = OperatingSystem.IsWindows() ? new Mutex(false, "TKCodeDesktop") : null;
        // Avalonia 必须占用主线程（macOS 要求），因此 Host 只 Start，不 Run。
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();
        builder.Logging.AddConsole();
        ConfigureServices(builder.Services);

        using var host = builder.Build();
        App.Services = host.Services;
        host.StartAsync().GetAwaiter().GetResult();
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
    }

    // XAML 预览器也会调用此方法（不经过 Main）。
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
                  .UseAtomUIPlatformDetect()
                  .WithAtomUIDefaultOptions()
                  .LogToTrace();

    internal static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<StateService>();
        services.AddSingleton<UserSettingsService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<AgentHostConnection>();
        services.AddSingleton<WorkspaceService>();
        services.AddSingleton<PtyHostService>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<UpdatesViewModel>();

        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<SidebarViewModel>();
        services.AddSingleton<ConversationViewModel>();
        services.AddSingleton<WorkbenchViewModel>();
    }
}
