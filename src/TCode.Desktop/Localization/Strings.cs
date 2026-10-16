using System.Globalization;
using AtomUI;
using AtomUI.Localization;
using Avalonia;

namespace TCode.Desktop.Localization;

/// <summary>
/// C# 侧读取界面文案。返回的是当前语言下的快照；需要随语言切换刷新的文案应在 AXAML 中用
/// {loc:TCodeLangResource Key} 绑定，或订阅 <see cref="LanguageChanged"/> 后重新读取。
/// </summary>
public static class Strings
{
    public static string Get(TCodeLangResourceKind key) =>
        Application.Current?.GetLocalizer()?.Get(key) ?? key.ToString();

    public static string Format(TCodeLangResourceKind key, params object?[] args) =>
        Application.Current?.GetLocalizer()?.Format(key, args) ?? key.ToString();

    public static event EventHandler? LanguageChanged;

    internal static void Attach(Application app)
    {
        if (app.GetLanguageManager() is { } manager)
        {
            ApplyCulture(manager.Current.CurrentLanguage);
            manager.LanguageChanged += (_, _) =>
            {
                ApplyCulture(manager.Current.CurrentLanguage);
                LanguageChanged?.Invoke(null, EventArgs.Empty);
            };
        }
    }

    /// <summary>AtomUI 不修改 CultureInfo；日期等格式化跟随界面语言需要自己设置。</summary>
    private static void ApplyCulture(LanguageTag language)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(language.Value);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
        catch (CultureNotFoundException)
        {
        }
    }
}
