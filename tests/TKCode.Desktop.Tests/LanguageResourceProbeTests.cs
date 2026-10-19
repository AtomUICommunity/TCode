using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using TKCode.Desktop.Localization;
using Xunit;

namespace TKCode.Desktop.Tests;

public sealed class LanguageResourceProbeTests
{
    // 回归：AtomUI 6.2.6 在 App.Initialize 之前提交 LanguageResourceProvider。
    // App.axaml 替换 Application.Resources 后，{loc:} 为空，而 Strings.Get 仍有文案。
    [Fact]
    public async Task MarkupLocalizationResolvesAfterXamlResourcesLoad()
    {
        string? viaStrings = null;
        string? viaMarkup = null;
        var paletteFound = false;
        await HeadlessApp.Session.Dispatch(() =>
        {
            var app = Application.Current!;
            viaStrings = Strings.Get(TKCodeLangResourceKind.SettingsGeneral);
            var block = new TextBlock();
            block.Bind(TextBlock.TextProperty, new DynamicResourceExtension(TKCodeLangResourceKind.SettingsGeneral));
            var window = new Window { Width = 240, Height = 80, Content = block };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            viaMarkup = block.Text;
            paletteFound = app.TryGetResource("TKCode.WindowBg", app.ActualThemeVariant, out var brush) && brush is IBrush;
            window.Close();
        }, TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrEmpty(viaStrings));
        Assert.Equal(viaStrings, viaMarkup);
        Assert.True(paletteFound);
    }
}
