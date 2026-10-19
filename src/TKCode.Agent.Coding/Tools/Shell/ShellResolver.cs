using TKCode.Shell;

namespace TKCode.Agent.Coding.Tools.Shell;

/// <summary>
/// 执行命令所用的 shell 及其调用方式。选择规则在 <see cref="ShellCatalog"/>：
/// Windows 默认 PowerShell 7 最新稳定版 → Git Bash → Windows PowerShell，Unix 是 $SHELL（限 bash / zsh）→ /bin/bash → /bin/sh；
/// settings.json 的 <c>agent.shell.profile</c> 可以改成别的 shell。
/// </summary>
/// <param name="CommandPrefix">拼在命令前面的脚本；Windows 上的 PowerShell 用它把输出编码设成 UTF-8。</param>
public sealed record ShellInfo(string Executable, IReadOnlyList<string> PrefixArguments, string Name, string? CommandPrefix = null)
{
    public bool IsPowerShell => Name is "powershell" or "pwsh";
}

public static class ShellResolver
{
    /// <summary>
    /// 控制台输出编码默认是系统的 OEM 代码页（中文系统为 GBK），而 BashTool 按 UTF-8 解码输出，不设置会出现乱码。
    /// </summary>
    internal const string Utf8OutputPrefix = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8;";

    /// <summary>未指定 shell 时的默认选择。</summary>
    public static ShellInfo Current => Resolve(null);

    /// <param name="preference">settings.json 的 agent.shell.profile；null 或 auto 为默认选择。</param>
    public static ShellInfo Resolve(string? preference) => ToInfo(ShellCatalog.Default.Resolve(preference, ShellUse.Agent).Shell);

    internal static ShellInfo ToInfo(DetectedShell shell) => shell.Kind switch
    {
        ShellKind.Pwsh => new ShellInfo(shell.Executable, ["-NoProfile", "-NonInteractive", "-Command"], shell.Name, OperatingSystem.IsWindows() ? Utf8OutputPrefix : null),
        ShellKind.WindowsPowerShell => new ShellInfo(shell.Executable, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command"], shell.Name, Utf8OutputPrefix),
        _ => new ShellInfo(shell.Executable, ["-c"], shell.Name),
    };
}
