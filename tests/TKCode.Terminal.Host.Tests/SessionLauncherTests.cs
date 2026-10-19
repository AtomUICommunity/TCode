using TKCode.Shell;
using TKCode.Terminal.Host.Sessions;
using TKCode.Terminal.Protocol;
using Xunit;

namespace TKCode.Terminal.Host.Tests;

/// <summary>新建终端默认 shell 的选择（terminal.defaultProfile 经 host/configure 的 DefaultShell 下发）。</summary>
public sealed class SessionLauncherTests
{
    private static SessionCreateParams Create(LaunchDto? launch = null) => new() { Size = new TerminalSizeDto(80, 24), Launch = launch, Cwd = Path.GetTempPath() };

    private static HostSettingsDto Settings(string? defaultShell, string? shellVariable = "/bin/sh") => new()
    {
        DefaultShell = defaultShell,
        BaseEnvironment = shellVariable is null ? new() : new() { ["SHELL"] = shellVariable, ["PATH"] = "/bin:/usr/bin" },
    };

    [Fact]
    public void Auto_OnUnix_IsTheLoginShell_ExactlyAsBefore()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var launch = SessionLauncher.Resolve(Create(), Settings(null, "/bin/sh"), out var actual);

        Assert.Equal("/bin/sh", launch.File);
        Assert.Empty(actual.Args);
        // macOS 的登录 shell 用 argv0 = "-sh"，不经 /usr/bin/login；Linux 不变
        Assert.Equal(OperatingSystem.IsMacOS() ? "-sh" : null, actual.Argv0);
        Assert.Equal(OperatingSystem.IsMacOS(), actual.Login);
    }

    [Fact]
    public void ConfiguredShell_ByPath_OverridesTheLoginShell()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var launch = SessionLauncher.Resolve(Create(), Settings("/bin/bash", "/bin/sh"), out var actual);

        Assert.Equal("/bin/bash", launch.File);
        Assert.Equal("/bin/bash", actual.File);
    }

    [Fact]
    public void ConfiguredShell_ThatDoesNotExist_FallsBackToTheDefault()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var launch = SessionLauncher.Resolve(Create(), Settings("/no/such/shell", "/bin/sh"), out _);

        Assert.Equal("/bin/sh", launch.File);
    }

    [Fact]
    public void ExplicitLaunch_IgnoresTheDefaultShell()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var launch = SessionLauncher.Resolve(Create(new LaunchDto("/bin/echo", ["hi"])), Settings("/bin/bash"), out var actual);

        Assert.Equal("/bin/echo", launch.File);
        Assert.Equal(["hi"], actual.Args);
    }

    [Fact]
    public void InteractiveArguments_DependOnTheShellKind()
    {
        Assert.Equal(["-NoLogo"], SessionLauncher.InteractiveArguments(new DetectedShell("pwsh", "PowerShell 7", "pwsh.exe", ShellKind.Pwsh)));
        Assert.Equal(["-NoLogo"], SessionLauncher.InteractiveArguments(new DetectedShell("powershell", "Windows PowerShell", "powershell.exe", ShellKind.WindowsPowerShell)));
        Assert.Equal(["--login", "-i"], SessionLauncher.InteractiveArguments(new DetectedShell("git-bash", "Git Bash", "bash.exe", ShellKind.GitBash)));
        Assert.Empty(SessionLauncher.InteractiveArguments(new DetectedShell("cmd", "Command Prompt", "cmd.exe", ShellKind.Cmd)));
        Assert.Empty(SessionLauncher.InteractiveArguments(new DetectedShell("zsh", "Zsh", "/bin/zsh", ShellKind.Zsh)));
    }
}
