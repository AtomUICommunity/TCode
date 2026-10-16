using TCode.Agent.Coding.Processes;

namespace TCode.Agent.Coding.Tools.Shell;

/// <summary>
/// 选择执行命令的 shell。Unix：$SHELL（限 bash / zsh）→ /bin/bash → /bin/sh；
/// Windows：Git Bash → PowerShell。结果在进程内缓存。
/// </summary>
public sealed record ShellInfo(string Executable, IReadOnlyList<string> PrefixArguments, string Name)
{
    public bool IsPowerShell => Name is "powershell" or "pwsh";
}

public static class ShellResolver
{
    private static readonly Lazy<ShellInfo> Cached = new(Resolve);

    public static ShellInfo Current => Cached.Value;

    private static ShellInfo Resolve()
    {
        if (OperatingSystem.IsWindows())
        {
            string[] gitBash =
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "bin", "bash.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "bin", "bash.exe"),
            ];
            if (gitBash.FirstOrDefault(File.Exists) is { } bash)
            {
                return new ShellInfo(bash, ["-c"], "bash");
            }
            if (ProcessRunner.FindExecutable("pwsh") is { } pwsh)
            {
                return new ShellInfo(pwsh, ["-NoProfile", "-NonInteractive", "-Command"], "pwsh");
            }
            return new ShellInfo("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command"], "powershell");
        }

        var preferred = Environment.GetEnvironmentVariable("SHELL");
        if (preferred is { Length: > 0 } && Path.GetFileName(preferred) is "bash" or "zsh" && File.Exists(preferred))
        {
            return new ShellInfo(preferred, ["-c"], Path.GetFileName(preferred));
        }
        if (File.Exists("/bin/bash"))
        {
            return new ShellInfo("/bin/bash", ["-c"], "bash");
        }
        return new ShellInfo("/bin/sh", ["-c"], "sh");
    }
}
