using System.Text;
using TCode.Terminal.Core;
using TCode.Terminal.Pty;
using TCode.Terminal.Pty.Unix;
using Xunit;
using static TCode.Terminal.Pty.Tests.PtyTestSupport;

namespace TCode.Terminal.Pty.Tests;

/// <summary>真实 PTY 往返（03-pty.md §14 的 T1–T19 子集）。</summary>
public sealed class LocalPtyTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ReadsOutputAndExitCode()
    {
        await using var pty = LocalPty.Start(Shell(OperatingSystem.IsWindows() ? "echo hello& exit /b 3" : "echo hello; exit 3"));
        var output = await ReadAllAsync(pty.Output, Timeout);
        var exit = await pty.Exited.WaitAsync(Timeout);
        Assert.Contains("hello", output);
        Assert.Equal(3, exit.ExitCode);
    }

    [Fact]
    public async Task OutputPrecedesExit()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix shell syntax");
        await using var pty = LocalPty.Start(Shell("i=0; while [ $i -lt 200 ]; do echo line$i; i=$((i+1)); done"));
        var exit = await pty.Exited.WaitAsync(Timeout);
        Assert.Equal(0, exit.ExitCode);
        var output = await ReadAllAsync(pty.Output, Timeout);
        Assert.Contains("line199", output);
    }

    [Fact]
    public async Task ChildHasControllingTerminalAndInitialSize()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix only");
        await using var pty = LocalPty.Start(Shell("tty >/dev/null && echo has-tty; stty size"));
        var output = await ReadAllAsync(pty.Output, Timeout);
        Assert.Contains("has-tty", output);
        Assert.Contains("24 80", output);
    }

    [Fact]
    public async Task WorkingDirectoryIsApplied()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix only");
        var dir = Directory.CreateTempSubdirectory("tcode-pty-").FullName;
        await using var pty = LocalPty.Start(Shell("pwd -P", dir));
        var output = await ReadAllAsync(pty.Output, Timeout);
        Assert.Contains(Path.GetFileName(dir), output);
    }

    [Fact]
    public async Task ResizeUpdatesKernelWinsizeWithPixels()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix only");
        await using var pty = (UnixPtyConnection)LocalPty.Start(Shell("sleep 5"));
        await pty.ResizeAsync(new TerminalSize(120, 40, 9, 18));
        Assert.Equal((120, 40, 1080, 720), pty.KernelWinsize());
    }

    [Fact]
    public async Task InputIsEchoedBack()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix shell syntax");
        await using var pty = LocalPty.Start(Shell("read line; echo got:$line"));
        await pty.WriteAsync(Encoding.UTF8.GetBytes("abc\r"));
        var output = await ReadAllAsync(pty.Output, Timeout);
        Assert.Contains("got:abc", output);
    }

    /// <summary>research/02 U3：子进程不能继承 .NET 的 SIGPIPE=SIG_IGN。</summary>
    [Fact]
    public async Task SigpipeIsDefault()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix only");
        await using var pty = LocalPty.Start(Shell("trap -p PIPE; echo end"));
        var output = await ReadAllAsync(pty.Output, Timeout);
        Assert.DoesNotContain("PIPE", output);
        Assert.Contains("end", output);
    }

    [Fact]
    public async Task ExitBySignalIsReported()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix only");
        await using var pty = LocalPty.Start(Shell("kill -TERM $$"));
        var exit = await pty.Exited.WaitAsync(Timeout);
        Assert.Null(exit.ExitCode);
        Assert.Equal("SIGTERM", exit.Signal);
    }

    [Fact]
    public async Task ForegroundTracksRunningProgram()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix only");
        // 交互式 shell 才会为前台作业建立进程组
        await using var pty = LocalPty.Start(new PtyLaunch("/bin/sh", ["-i"], "sh", null, Environment(("PS1", "$ ")), Size));
        using var core = TerminalCore.Create(new TerminalCoreOptions(Size), new ReplyToConnection(pty));
        await PumpUntilAsync(pty, core, c => c.FormatPlainText().Contains('$'), Timeout);
        var idle = await pty.GetForegroundAsync();
        Assert.True(idle!.IsShell);

        await pty.WriteAsync(Encoding.UTF8.GetBytes("sleep 30\r"));
        ForegroundInfo? busy = null;
        for (var i = 0; i < 50 && busy?.IsShell != false; i++)
        {
            await Task.Delay(50);
            busy = await pty.GetForegroundAsync();
        }
        Assert.False(busy!.IsShell);
        Assert.Equal("sleep", busy.Name);
    }

    [Fact]
    public async Task CloseHangsUpTheSession()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix only");
        var pty = LocalPty.Start(Shell("sleep 60"));
        await pty.CloseAsync(TimeSpan.FromSeconds(2));
        var exit = await pty.Exited.WaitAsync(Timeout);
        Assert.Equal("SIGHUP", exit.Signal);
    }

    /// <summary>
    /// M0 验收：终端核心 + PTY 驱动 vim（备用屏、查询应答、全屏刷新），:q 后正常退出。
    /// </summary>
    [Fact]
    public async Task RunsVimAndQuits()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix only");
        var vim = new[] { "/usr/bin/vim", "/bin/vim", "/usr/local/bin/vim" }.FirstOrDefault(File.Exists);
        Assert.SkipWhen(vim is null, "vim not installed");
        await using var pty = LocalPty.Start(new PtyLaunch(vim!, ["-u", "NONE", "-N", "-c", "set noswapfile", "-c", "call setline(1, 'hello from vim')"],
            "vim", null, Environment(), Size));
        using var core = TerminalCore.Create(new TerminalCoreOptions(Size), new ReplyToConnection(pty));
        await PumpUntilAsync(pty, core, c => c.GetMode(1049) && c.FormatPlainText().Contains("hello from vim"), Timeout);

        await pty.WriteAsync(Encoding.UTF8.GetBytes("\u001b:q!\r"));
        await PumpUntilAsync(pty, core, c => !c.GetMode(1049), Timeout);
        var exit = await pty.Exited.WaitAsync(Timeout);
        Assert.Equal(0, exit.ExitCode);
    }
}
