using System.Diagnostics;

namespace TCode.Runtimes.Discovery;

/// <summary>
/// 查找用户安装的 CLI（免配置）。顺序：环境变量覆盖 → 当前 PATH → 登录 shell 的 PATH 快照 → 已知安装目录。
/// 从 Finder / Dock 启动的应用继承不到 shell 配置的 PATH，所以需要登录 shell 快照与已知目录兜底。
/// </summary>
public sealed class ExecutableLocator
{
    private static readonly TimeSpan ShellTimeout = TimeSpan.FromSeconds(5);
    private const string Marker = "__TCODE_PATH__=";

    private readonly Func<string, string?> _getEnv;
    private readonly Lazy<IReadOnlyList<string>> _loginShellPath;

    public ExecutableLocator(Func<string, string?>? getEnv = null, Func<IReadOnlyList<string>>? loginShellPath = null)
    {
        _getEnv = getEnv ?? Environment.GetEnvironmentVariable;
        _loginShellPath = new Lazy<IReadOnlyList<string>>(loginShellPath ?? ReadLoginShellPath, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public static ExecutableLocator Default { get; } = new();

    /// <summary>
    /// 查找可执行文件。<paramref name="overrideVariable"/> 指向的路径存在时优先使用（例如 <c>TCODE_CODEX_PATH</c>）；
    /// <paramref name="extraDirectories"/> 为该 CLI 特有的安装目录（例如 <c>~/.claude/local</c>）。
    /// </summary>
    public string? Find(string name, string? overrideVariable = null, IEnumerable<string>? extraDirectories = null,
        Func<string, bool>? accept = null)
    {
        if (overrideVariable is not null && _getEnv(overrideVariable) is { Length: > 0 } overridden)
        {
            return File.Exists(overridden) ? Path.GetFullPath(overridden) : null;
        }
        foreach (var dir in CandidateDirectories(extraDirectories))
        {
            foreach (var file in CandidateFiles(name))
            {
                var path = Path.Combine(dir, file);
                if (IsExecutable(path) && (accept is null || accept(path)))
                {
                    return path;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// 子进程的 PATH：可执行文件所在目录放在最前（<c>#!/usr/bin/env node</c> 一类的脚本才能找到同目录的 node），
    /// 其后是当前 PATH 与登录 shell PATH。
    /// </summary>
    public string ChildPath(string executablePath)
    {
        var dirs = new List<string>();
        AddUnique(dirs, Path.GetDirectoryName(executablePath));
        foreach (var dir in SplitPath(_getEnv("PATH")))
        {
            AddUnique(dirs, dir);
        }
        foreach (var dir in _loginShellPath.Value)
        {
            AddUnique(dirs, dir);
        }
        return string.Join(Path.PathSeparator, dirs);
    }

    private IEnumerable<string> CandidateDirectories(IEnumerable<string>? extra)
    {
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var dir in SplitPath(_getEnv("PATH")).Concat(_loginShellPath.Value).Concat(extra ?? []).Concat(KnownDirectories()))
        {
            if (dir.Length > 0 && seen.Add(dir))
            {
                yield return dir;
            }
        }
    }

    private IEnumerable<string> KnownDirectories()
    {
        var home = _getEnv(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            if (_getEnv("APPDATA") is { } appData)
            {
                yield return Path.Combine(appData, "npm");
            }
            if (_getEnv("LOCALAPPDATA") is { } local)
            {
                yield return Path.Combine(local, "Programs");
                yield return Path.Combine(local, "pnpm");
            }
            yield return Path.Combine(home, ".local", "bin");
            yield return Path.Combine(home, ".bun", "bin");
            yield return Path.Combine(home, "scoop", "shims");
            yield break;
        }
        yield return Path.Combine(home, ".local", "bin");
        yield return "/opt/homebrew/bin";
        yield return "/usr/local/bin";
        yield return Path.Combine(home, ".npm-global", "bin");
        if (_getEnv("NPM_CONFIG_PREFIX") is { Length: > 0 } npmPrefix)
        {
            yield return Path.Combine(npmPrefix, "bin");
        }
        yield return Path.Combine(home, ".volta", "bin");
        yield return Path.Combine(home, ".bun", "bin");
        yield return Path.Combine(home, "Library", "pnpm");
        yield return Path.Combine(home, ".local", "share", "pnpm");
        if (_getEnv("PNPM_HOME") is { Length: > 0 } pnpm)
        {
            yield return pnpm;
        }
        // nvm / fnm：取版本目录下的 bin，新版本优先
        foreach (var root in new[] { Path.Combine(home, ".nvm", "versions", "node"), Path.Combine(home, ".local", "share", "fnm", "node-versions"),
                     Path.Combine(home, "Library", "Application Support", "fnm", "node-versions") })
        {
            if (!Directory.Exists(root))
            {
                continue;
            }
            foreach (var version in SafeEnumerate(root).OrderByDescending(d => d, StringComparer.Ordinal))
            {
                yield return Path.Combine(version, "bin");
                yield return Path.Combine(version, "installation", "bin");
            }
        }
    }

    private IEnumerable<string> CandidateFiles(string name)
    {
        if (!OperatingSystem.IsWindows() || Path.HasExtension(name))
        {
            return [name];
        }
        var extensions = (_getEnv("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries);
        return extensions.Select(e => name + e.ToLowerInvariant()).Prepend(name + ".exe").Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsExecutable(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }
        if (OperatingSystem.IsWindows())
        {
            return true;
        }
        try
        {
            return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IEnumerable<string> SafeEnumerate(string root)
    {
        try
        {
            return Directory.GetDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SplitPath(string? path) =>
        (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void AddUnique(List<string> dirs, string? dir)
    {
        if (!string.IsNullOrEmpty(dir) && !dirs.Contains(dir))
        {
            dirs.Add(dir);
        }
    }

    /// <summary>启动一次登录交互 shell 读取 PATH（rc 文件可能输出杂讯，用标记定位）。失败或超时返回空。</summary>
    private static IReadOnlyList<string> ReadLoginShellPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return [];
        }
        var shell = Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } s && File.Exists(s) ? s : "/bin/zsh";
        if (!File.Exists(shell))
        {
            return [];
        }
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new ProcessStartInfo(shell)
                {
                    ArgumentList = { "-ilc", $"printf '%s\\n' \"{Marker}$PATH\"" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                },
            };
            process.Start();
            process.StandardInput.Close();
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            var output = process.StandardOutput.ReadToEndAsync();
            if (!output.Wait(ShellTimeout) || !process.WaitForExit(ShellTimeout))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
                return [];
            }
            var line = output.Result.Split('\n').LastOrDefault(l => l.StartsWith(Marker, StringComparison.Ordinal));
            return line is null ? [] : SplitPath(line[Marker.Length..]).ToArray();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return [];
        }
    }
}
