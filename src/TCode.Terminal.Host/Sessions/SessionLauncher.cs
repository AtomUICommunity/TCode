using TCode.Terminal.Core;
using TCode.Terminal.Protocol;
using TCode.Terminal.Pty;

namespace TCode.Terminal.Host.Sessions;

/// <summary>
/// session/create 的启动参数解析（04 §4.3）：命令、cwd、环境。profile 探测与 shell 集成注入属于 TCode.Terminal.Shell（M4），
/// 这里只处理"显式命令"和"系统默认 shell"两种情况。
/// </summary>
internal static class SessionLauncher
{
    /// <summary>永远不进入 shell 环境的内部变量（04 §13.2 第 5 条）。</summary>
    private static readonly string[] InternalPrefixes = ["TCODE_PTYHOST_"];

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
        else if (OperatingSystem.IsWindows())
        {
            file = env.GetValueOrDefault("ComSpec") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
            args = [];
        }
        else
        {
            file = env.GetValueOrDefault("SHELL") is { Length: > 0 } shell && File.Exists(shell) ? shell : "/bin/sh";
            args = [];
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
        env["TERM_PROGRAM"] = "TCode";
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
