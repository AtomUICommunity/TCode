using TKCode.Shell;
using TKCode.Terminal.Core;
using TKCode.Terminal.Protocol;
using TKCode.Terminal.Pty;

namespace TKCode.Terminal.Host.Sessions;

/// <summary>
/// session/create 的启动参数解析（04 §4.3）：命令、cwd、环境。profile 探测与 shell 集成注入属于 TKCode.Terminal.Shell（M4），
/// 这里只处理"显式命令"和"系统默认 shell"两种情况。
/// </summary>
internal static class SessionLauncher
{
    /// <summary>永远不进入 shell 环境的内部变量（04 §13.2 第 5 条）。</summary>
    private static readonly string[] InternalPrefixes = ["TKCODE_PTYHOST_"];

    public static PtyLaunch Resolve(SessionCreateParams p, HostSettingsDto settings, out ActualLaunchDto actual)
    {
        var env = BuildEnvironment(p, settings);
        var cwd = p.Cwd;
        if (cwd is not null && !Directory.Exists(cwd))
        {
            throw new PtyHostException(PtyErrors.CwdNotFound, $"working directory does not exist: {cwd}");
        }
        cwd ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var size = new TerminalSize(Math.Max(1, p.Size.Cols), Math.Max(1, p.Size.Rows), p.Size.CellWidth, p.Size.CellHeight);
        var width = p.WidthMode == "grapheme" ? PtyWidthMode.Grapheme : PtyWidthMode.Compat;

        string file;
        string[] args;
        string? argv0 = null;
        var login = false;
        if (p.Launch is { } launch)
        {
            file = ResolveCommand(launch.Command, env) ?? throw new PtyHostException(PtyErrors.SpawnFailed, $"command not found: {launch.Command}");
            args = launch.Args;
        }
        else if (p.ProfileId is { } profile && profile != "default")
        {
            throw new PtyHostException(PtyErrors.ProfileNotFound, $"profile not found: {profile}");
        }
        else
        {
            // 默认 shell：settings 的 terminal.defaultProfile，auto 时 Windows 为 PowerShell 7 → Windows PowerShell → cmd，Unix 为登录 shell。
            // 按 Desktop 传来的基础环境（而不是 host 自己的）解析 PATH 与 $SHELL，指定的 shell 不可用时退回 auto。
            var shell = new ShellCatalog(new RealShellSystem(name => env.GetValueOrDefault(name))).Resolve(settings.DefaultShell, ShellUse.Terminal).Shell;
            file = shell.Executable;
            args = InteractiveArguments(shell);
            // macOS：不经 login 包装，argv0 写成 "-zsh" 形式作为登录 shell（03 §8.3，2026-10-17 决定）
            if (OperatingSystem.IsMacOS())
            {
                argv0 = "-" + Path.GetFileName(file);
                login = true;
            }
        }
        actual = new ActualLaunchDto(file, args, argv0, login);
        return new PtyLaunch(file, args, argv0, cwd, env.Select(kv => kv).ToList(), size, width);
    }

    /// <summary>交互式启动参数：PowerShell 去掉启动横幅，Windows 上的 bash 作为登录交互 shell；其余不带参数。</summary>
    internal static string[] InteractiveArguments(DetectedShell shell) => shell.Kind switch
    {
        ShellKind.Pwsh or ShellKind.WindowsPowerShell => ["-NoLogo"],
        ShellKind.GitBash => ["--login", "-i"],
        ShellKind.Bash when OperatingSystem.IsWindows() => ["--login", "-i"],
        _ => [],
    };

    private static Dictionary<string, string> BuildEnvironment(SessionCreateParams p, HostSettingsDto settings)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var env = new Dictionary<string, string>(comparer);
        if (settings.BaseEnvironment is { } baseEnv)
        {
            foreach (var (k, v) in baseEnv)
            {
                env[k] = v;
            }
        }
        else
        {
            foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            {
                env[(string)e.Key] = (string?)e.Value ?? string.Empty;
            }
        }
        foreach (var key in env.Keys.Where(k => InternalPrefixes.Any(prefix => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            env.Remove(key);
        }
        if (p.Env is { } overrides)
        {
            foreach (var (k, v) in overrides)
            {
                if (v is null)
                {
                    env.Remove(k);
                }
                else if (!InternalPrefixes.Any(prefix => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    env[k] = v;
                }
            }
        }
        env["TERM"] = "xterm-256color";
        env["COLORTERM"] = "truecolor";
        env["TERM_PROGRAM"] = "TKCode";
        env["TERM_PROGRAM_VERSION"] = typeof(SessionLauncher).Assembly.GetName().Version?.ToString(3) ?? "0";
        return env;
    }

    /// <summary>按合并后环境的 PATH 解析命令（Windows 另按 PATHEXT）。</summary>
    private static string? ResolveCommand(string command, Dictionary<string, string> env)
    {
        if (Path.IsPathRooted(command) || command.Contains('/') || (OperatingSystem.IsWindows() && command.Contains('\\')))
        {
            return File.Exists(command) ? Path.GetFullPath(command) : null;
        }
        var extensions = OperatingSystem.IsWindows()
            ? ["", .. (env.GetValueOrDefault("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)]
            : new[] { "" };
        foreach (var dir in (env.GetValueOrDefault("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, command + ext);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        return null;
    }
}
