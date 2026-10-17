using Microsoft.Extensions.Logging.Abstractions;
using TKCode.Desktop.Services;
using TKCode.Desktop.Terminal;
using TKCode.Desktop.ViewModels;
using TKCode.Terminal.Core;
using TKCode.Terminal.Protocol;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>终端跟随应用的明暗主题：已经在运行的会话切换主题后，调色板经 host 更新到新的帧。真实 host 与真实会话。</summary>
public sealed class TerminalThemeTests
{
    [Fact]
    public async Task SwitchingThemeRecolorsARunningSession()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX shell");
        await using var host = await InProcessHost.StartAsync();
        await using var client = await host.ConnectAsync();
        FrameSnapshot? latest = null;
        await using var session = new TerminalSession(f => Volatile.Write(ref latest, f), _ => { });
        await client.CreateSessionAsync(new SessionCreateParams
        {
            Size = new TerminalSizeDto(80, 24, 8, 16),
            Palette = TerminalTheme.Dark,
            Launch = new LaunchDto("/bin/sh", ["-c", "while true; do echo tick; sleep 0.1; done"]),
        }, session);
        await Wait.UntilAsync(() => Task.FromResult(Volatile.Read(ref latest)?.Palette.Background == TerminalTheme.ToRgb(TerminalTheme.Dark.Background)), "dark frame");

        var tab = new TerminalTabViewModel(new PtyHostService(NullLogger<PtyHostService>.Instance), cwd: null);
        tab.AttachForTesting(client, session);
        tab.SetDarkTheme(false);
        await Wait.UntilAsync(() => Task.FromResult(Volatile.Read(ref latest)?.Palette.Background == TerminalTheme.ToRgb(TerminalTheme.Light.Background)), "light frame");
        Assert.Equal(TerminalTheme.ToRgb(TerminalTheme.Light.Foreground), latest!.Palette.Foreground);

        tab.SetDarkTheme(true);
        await Wait.UntilAsync(() => Task.FromResult(Volatile.Read(ref latest)?.Palette.Background == TerminalTheme.ToRgb(TerminalTheme.Dark.Background)), "dark again");
    }
}
