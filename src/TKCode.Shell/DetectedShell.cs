namespace TKCode.Shell;

public enum ShellKind
{
    Pwsh,
    WindowsPowerShell,
    Cmd,
    GitBash,
    Bash,
    Zsh,
    Fish,
    Sh,
    Nu,
    Other,
}

/// <summary>shell 的用途：Agent 的 bash 工具（<c>shell -c 命令</c>，一次一个进程）与交互终端的默认选择不同。</summary>
public enum ShellUse
{
    Agent,
    Terminal,
}

/// <summary>
/// 探测到（或由用户指定路径）的一个 shell。<see cref="Id"/> 同时是设置里 <c>agent.shell.profile</c> /
/// <c>terminal.defaultProfile</c> 可以填的值：pwsh、pwsh-preview、powershell、git-bash、cmd、bash、zsh……
/// </summary>
public sealed record DetectedShell(string Id, string Label, string Executable, ShellKind Kind)
{
    public bool IsPowerShell => Kind is ShellKind.Pwsh or ShellKind.WindowsPowerShell;

    /// <summary>写进系统提示的名字：pwsh、powershell、bash、zsh……</summary>
    public string Name => Kind switch
    {
        ShellKind.Pwsh => "pwsh",
        ShellKind.WindowsPowerShell => "powershell",
        ShellKind.Cmd => "cmd",
        ShellKind.GitBash or ShellKind.Bash => "bash",
        ShellKind.Zsh => "zsh",
        ShellKind.Fish => "fish",
        ShellKind.Sh => "sh",
        ShellKind.Nu => "nu",
        _ => Path.GetFileNameWithoutExtension(Executable),
    };

    public static ShellKind KindOf(string executable) => Path.GetFileNameWithoutExtension(executable).ToLowerInvariant() switch
    {
        "pwsh" or "pwsh-preview" => ShellKind.Pwsh,
        "powershell" => ShellKind.WindowsPowerShell,
        "cmd" => ShellKind.Cmd,
        "bash" => ShellKind.Bash,
        "zsh" => ShellKind.Zsh,
        "fish" => ShellKind.Fish,
        "sh" => ShellKind.Sh,
        "nu" => ShellKind.Nu,
        _ => ShellKind.Other,
    };
}

/// <summary>解析设置的结果。<see cref="Warning"/> 非空表示用户指定的 shell 不可用，已退回默认选择。</summary>
public sealed record ShellSelection(DetectedShell Shell, string? Warning);
