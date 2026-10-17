using AtomUI;
using AtomUI.Desktop.Controls;
using AtomUI.Localization;
using AtomUI.Theme.Definitions;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using TCode.Desktop.Controls;
using TCode.Desktop.Localization;
using TCode.Desktop.Services;
using TCode.Desktop.ViewModels;
using TCode.Desktop.Views;

namespace TCode.Desktop;

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
            builder.WithApplicationId("TCode");
            var settings = Services?.GetRequiredService<UserSettingsService>();
            var language = LanguageTag.TryParse(settings?.Current.Language, out var tag) && (tag == LanguageTags.EnUS || tag == LanguageTags.ZhCN)
                ? tag
                : LanguageTags.ZhCN;
            builder.UseLanguages(language, [LanguageTags.EnUS, LanguageTags.ZhCN]);
            builder.AddThemeDefinitionResolver(new AvaloniaAssetThemeDefinitionResolver(
                "TCode.Themes",
                [
                    new Uri("avares://TCode/Assets/Themes/TCodeDark.theme.xml"),
                    new Uri("avares://TCode/Assets/Themes/TCodeLight.theme.xml"),
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
            TCode.Desktop.Motion.MotionSettings.UserReduceMotion = userSettings.Current.ReduceMotion == true;
            MarkdownSetup.OriginalImages = userSettings.Current.OriginalImages == true;
            userSettings.Changed += () =>
            {
                TCode.Desktop.Motion.MotionSettings.UserReduceMotion = userSettings.Current.ReduceMotion == true;
                MarkdownSetup.OriginalImages = userSettings.Current.OriginalImages == true;
                if (this.GetLanguageManager() is { } manager && LanguageTag.TryParse(userSettings.Current.Language, out var wanted) &&
                    (wanted == LanguageTags.EnUS || wanted == LanguageTags.ZhCN) && manager.Current.CurrentLanguage != wanted)
                {
                    manager.ChangeLanguage(wanted);
                }
            };
            desktop.MainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainWindowViewModel>(),
            };
            desktop.ShutdownRequested += (_, _) => Services.GetRequiredService<AgentHostConnection>().Dispose();
        }
        base.OnFrameworkInitializationCompleted();
        TCode.Desktop.Services.MacDockIcon.Apply();
    }
}
