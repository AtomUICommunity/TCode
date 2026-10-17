using System.Text;
using TCode.Terminal.Core;
using TCode.Terminal.Pty.Windows;
using Xunit;
using static TCode.Terminal.Pty.Tests.PtyTestSupport;

namespace TCode.Terminal.Pty.Tests;

/// <summary>Windows ConPTY 专项（03-pty.md §11、§8.2）。</summary>
public sealed class WindowsPtyTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task UsesBundledConPty()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        await using var pty = LocalPty.Start(Shell("exit /b 0"));
        await pty.Exited.WaitAsync(Timeout);
        Assert.True(LocalPty.ConPtyImplementation == ConPtyImplementation.Bundled, LocalPty.ConPtyDegradedReason);
    }

    [Fact]
    public async Task ForegroundTracksRunningProgram()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        await using var pty = LocalPty.Start(new PtyLaunch("cmd.exe", ["/d", "/k", "prompt $G"], null, null, Environment(), Size));
        using var core = TerminalCore.Create(new TerminalCoreOptions(Size), new ReplyToConnection(pty));
        await PumpUntilAsync(pty, core, c => c.FormatPlainText().Contains('>'), Timeout);
        Assert.True(pty.Capabilities.HasFlag(ConnectionCapabilities.ForegroundProcess));
        Assert.True((await pty.GetForegroundAsync())!.IsShell);

        await pty.WriteAsync(Encoding.UTF8.GetBytes("ping -n 30 127.0.0.1\r"));
        ForegroundInfo? busy = null;
        for (var i = 0; i < 100 && busy?.IsShell != false; i++)
        {
            await Task.Delay(50);
            busy = await pty.GetForegroundAsync();
        }
        Assert.False(busy!.IsShell);
        Assert.Equal("ping", busy.Name, ignoreCase: true);
    }
}
