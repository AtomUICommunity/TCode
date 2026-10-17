using TKCode.Terminal.Pty.Windows;
using Xunit;
using Entry = TKCode.Terminal.Pty.Windows.ProcessTree.Entry;

namespace TKCode.Terminal.Pty.Tests;

/// <summary>Windows 前台进程启发式的纯函数部分（03-pty.md §8.2），各平台都跑。</summary>
public sealed class ProcessTreeTests
{
    [Fact]
    public void IdleShellIsForeground()
    {
        var fg = ProcessTree.Pick(10, [new Entry(1, 0, "explorer.exe", 1), new Entry(10, 1, "pwsh.exe", 100), new Entry(11, 10, "conhost.exe", 101)]);
        Assert.Equal(new ForegroundInfo(10, "pwsh", true), fg);
    }

    [Fact]
    public void NewestDescendantWins()
    {
        var fg = ProcessTree.Pick(10,
        [
            new Entry(10, 1, "cmd.exe", 100),
            new Entry(20, 10, "node.exe", 200),
            new Entry(30, 20, "git.exe", 300),
            new Entry(40, 10, "OpenConsole.exe", 400),
        ]);
        Assert.Equal(new ForegroundInfo(30, "git", false), fg);
    }

    [Fact]
    public void ReusedPidIsNotADescendant()
    {
        // pid 5 的父进程记录是 10，但它比 shell 创建得早：父 pid 被复用，不是 shell 的子进程
        var fg = ProcessTree.Pick(10, [new Entry(10, 1, "cmd.exe", 100), new Entry(5, 10, "svchost.exe", 50)]);
        Assert.True(fg!.IsShell);
    }

    [Fact]
    public void ExitedShellReturnsNull() => Assert.Null(ProcessTree.Pick(10, [new Entry(11, 1, "x.exe", 1)]));

    [Fact]
    public void CyclesTerminate()
    {
        var fg = ProcessTree.Pick(10, [new Entry(10, 11, "cmd.exe", 0), new Entry(11, 10, "a.exe", 0)]);
        Assert.Equal(11, fg!.Pid);
    }
}
