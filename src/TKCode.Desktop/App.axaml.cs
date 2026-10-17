using AtomUI;
using AtomUI.Desktop.Controls;
using AtomUI.Localization;
using AtomUI.Theme.Definitions;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using TKCode.Desktop.Controls;
using TKCode.Desktop.Localization;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;

namespace TKCode.Desktop;

public partial class App : Application
{
    /// <summary>由 Program 在 Avalonia 启动前设置；XAML 预览器下为 null。</summary>
    public static IServiceProvider? Services { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        var themes = Services?.GetRequiredService<ThemeService>();
        this.UseAtomUI(builder =>
        {
            builder.WithApplicationId("TKCode");
            var settings = Services?.GetRequiredService<UserSettingsService>();
            var language = LanguageTag.TryParse(settings?.Current.Language, out var tag) && (tag == LanguageTags.EnUS || tag == LanguageTags.ZhCN)
                ? tag
                : LanguageTags.ZhCN;
            builder.UseLanguages(language, [LanguageTags.EnUS, LanguageTags.ZhCN]);
            builder.AddThemeDefinitionResolver(new AvaloniaAssetThemeDefinitionResolver(
                "TKCode.Themes",
                [
                    new Uri("avares://TKCode/Assets/Themes/TKCodeDark.theme.xml"),
                    new Uri("avares://TKCode/Assets/Themes/TKCodeLight.theme.xml"),
                ],
                typeof(App).Assembly.GetName().Version?.ToString() ?? "1"));
            builder.WithInitialTheme(themes?.ResolveInitialThemeId() ?? ThemeService.DarkThemeId);
            builder.WithDefaultFontFamily("$Default");
            builder.UseDesktopControls();
        });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Strings.Attach(this);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && Services is not null)
        {
            Services.GetRequiredService<ThemeService>().Attach(this);
            // settings.json 被外部修改语言时立即切换
            var userSettings = Services.GetRequiredService<UserSettingsService>();
            TKCode.Desktop.Motion.MotionSettings.UserReduceMotion = userSettings.Current.ReduceMotion == true;
            MarkdownSetup.OriginalImages = userSettings.Current.OriginalImages == true;
            userSettings.Changed += () =>
            {
                TKCode.Desktop.Motion.MotionSettings.UserReduceMotion = userSettings.Current.ReduceMotion == true;
                MarkdownSetup.OriginalImages = userSettings.Current.OriginalImages == true;
                if (this.GetLanguageManager() is { } manager && LanguageTag.TryParse(userSettings.Current.Language, out var wanted) &&
                    (wanted == LanguageTags.EnUS || wanted == LanguageTags.ZhCN) && manager.Current.CurrentLanguage != wanted)
                {
                    manager.ChangeLanguage(wanted);
                }
            };
            var mainViewModel = Services.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = mainViewModel };
            desktop.MainWindow = window;
            // 系统托盘；默认关闭窗口只隐藏，进程与会话继续运行
            // 托盘图标、窗口与更新的事件持有它，不需要另存引用
            _ = new AppTray(this, desktop, window, userSettings, Services.GetRequiredService<UpdatesViewModel>(), mainViewModel);
            desktop.ShutdownRequested += (_, _) => Services.GetRequiredService<AgentHostConnection>().Dispose();
            // 应用内更新：安装程序启动后退出；启动 60 秒后开始检查（docs/design/auto-update.md）
            Services.GetRequiredService<UpdatesViewModel>().Shutdown = () => desktop.Shutdown();
            Services.GetRequiredService<TKCode.Desktop.Updates.UpdateService>().Start();
        }
        base.OnFrameworkInitializationCompleted();
        TKCode.Desktop.Services.MacDockIcon.Apply();
    }
}
