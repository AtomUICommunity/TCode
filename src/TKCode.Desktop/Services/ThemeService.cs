using AtomUI;
using AtomUI.Theme;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TKCode.Desktop.Services;

/// <summary>
/// 在 TKCodeDark / TKCodeLight 两套 AtomUI 主题之间切换，支持跟随系统。
/// 主题定义在 Assets/Themes/*.theme.xml；应用专属颜色在 Styling/Palette.axaml 的 ThemeDictionaries 中随之切换。
/// </summary>
public sealed partial class ThemeService : ObservableObject
{
    private readonly UserSettingsService _settings;
    private ThemeMode _applied;

    public ThemeService(UserSettingsService settings)
    {
        _settings = settings;
        _applied = settings.Theme;
        // settings.json 被外部修改主题时立即应用
        settings.Changed += async () =>
        {
            if (_settings.Theme != _applied)
            {
                _applied = _settings.Theme;
                OnPropertyChanged(nameof(Mode));
                await ApplyAsync();
            }
        };
    }

    public const string DarkThemeId = "TKCodeDark";
    public const string LightThemeId = "TKCodeLight";

    private Application? _app;

    public ThemeMode Mode => _settings.Theme;

    [ObservableProperty]
    public partial bool IsDark { get; private set; } = true;

    public string ResolveInitialThemeId()
    {
        var id = Resolve(Mode, Application.Current?.PlatformSettings);
        IsDark = id == DarkThemeId;
        return id;
    }

    public void Attach(Application app)
    {
        _app = app;
        if (app.PlatformSettings is { } platform)
        {
            platform.ColorValuesChanged += async (_, _) =>
            {
                if (Mode == ThemeMode.System)
                {
                    await ApplyAsync();
                }
            };
        }
    }

    public async Task SetModeAsync(ThemeMode mode)
    {
        _applied = mode;
        _settings.SetTheme(mode);
        OnPropertyChanged(nameof(Mode));
        await ApplyAsync();
    }

    public Task ToggleAsync() => SetModeAsync(IsDark ? ThemeMode.Light : ThemeMode.Dark);

    private async Task ApplyAsync()
    {
        if (_app?.GetThemeManager() is not { } manager)
        {
            return;
        }
        var id = Resolve(Mode, _app.PlatformSettings);
        var result = await manager.ApplyThemeAsync(new ThemeRequest(id, null, ThemeTransitionReason.UserRequest));
        if (result.Status != ThemeTransitionStatus.Failed)
        {
            IsDark = id == DarkThemeId;
        }
    }

    private static string Resolve(ThemeMode mode, IPlatformSettings? platform) => mode switch
    {
        ThemeMode.Dark => DarkThemeId,
        ThemeMode.Light => LightThemeId,
        _ => platform?.GetColorValues().ThemeVariant == PlatformThemeVariant.Light ? LightThemeId : DarkThemeId,
    };
}
