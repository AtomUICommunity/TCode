using System.Globalization;

namespace TKCode.Shell;

/// <summary>
/// 探测本机有哪些 shell，并按设置（<c>agent.shell.profile</c> / <c>terminal.defaultProfile</c>）选出要用的那个。
/// <para>
/// Windows 的默认（auto）顺序：Agent 为 PowerShell 7 最新稳定版 → Git Bash → Windows PowerShell；
/// 终端为 PowerShell 7 → Windows PowerShell → cmd。预览版和其他 pwsh 安装不参与 auto，但可以在设置里指定。
/// macOS / Linux 的 auto 与以往一致：Agent 用 $SHELL（限 bash / zsh）→ /bin/bash → /bin/sh，终端用 $SHELL → /bin/sh。
/// </para>
/// </summary>
public sealed class ShellCatalog(IShellSystem system, TimeSpan cacheFor = default)
{
    /// <summary>进程内共享，探测结果缓存 30 秒：用户装了新 shell 之后不必重启应用。</summary>
    public static ShellCatalog Default { get; } = new(RealShellSystem.Instance, TimeSpan.FromSeconds(30));

    // 同一个 pwsh 大版本有多个安装时的先后：Store（自动更新）> Scoop > dotnet tool > 传统安装 > 32 位。与 Windows Terminal 的排名一致。
    private const int FormWow = 0;
    private const int FormInstalled = 1;
    private const int FormDotnetTool = 2;
    private const int FormScoop = 3;
    private const int FormStore = 4;

    private static readonly HashSet<string> NotShells = new(StringComparer.OrdinalIgnoreCase)
    {
        "rbash", "git-shell", "nologin", "false", "true", "tmux", "screen", "scponly", "rssh",
    };

    private readonly Lock _gate = new();
    private IReadOnlyList<DetectedShell>? _cached;
    private long _cachedAt;

    /// <summary>探测到的 shell，按在设置页里展示的顺序排列。</summary>
    public IReadOnlyList<DetectedShell> Detect()
    {
        lock (_gate)
        {
            if (_cached is not null && cacheFor > TimeSpan.Zero && Environment.TickCount64 - _cachedAt < cacheFor.TotalMilliseconds)
            {
                return _cached;
            }
            _cached = system.Os == ShellOs.Windows ? DetectWindows() : DetectUnix();
            _cachedAt = Environment.TickCount64;
            return _cached;
        }
    }

    /// <summary>
    /// 按设置选 shell：空 / auto 走默认顺序；探测到的 id（pwsh、git-bash……）；可执行文件的绝对路径；PATH 里能找到的程序名。
    /// 指定的 shell 不可用时退回默认顺序，并在 <see cref="ShellSelection.Warning"/> 里说明原因。
    /// </summary>
    public ShellSelection Resolve(string? preference, ShellUse use)
    {
        var pref = preference?.Trim().Trim('"');
        if (string.IsNullOrEmpty(pref) || pref.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return new ShellSelection(Auto(use), null);
        }
        var chosen = Detect().FirstOrDefault(s => s.Id.Equals(pref, StringComparison.OrdinalIgnoreCase));
        if (chosen is null)
        {
            var path = IsPath(pref) ? pref : FindOnPath(pref);
            if (path is not null && system.FileExists(path))
            {
                chosen = new DetectedShell("custom", Path.GetFileName(path), path, DetectedShell.KindOf(path));
            }
        }
        if (chosen is null)
        {
            return new ShellSelection(Auto(use), $"Shell \"{pref}\" was not found; using the default shell.");
        }
        if (use == ShellUse.Agent && chosen.Kind == ShellKind.Cmd)
        {
            // cmd 的引号规则与 `-c` 风格的参数列表不兼容
            return new ShellSelection(Auto(use), "cmd.exe cannot run the agent's commands; using the default shell.");
        }
        return new ShellSelection(chosen, null);
    }

    private DetectedShell Auto(ShellUse use)
    {
        if (system.Os == ShellOs.Windows)
        {
            var detected = Detect();
            string[] order = use == ShellUse.Agent ? ["pwsh", "git-bash", "powershell"] : ["pwsh", "powershell", "cmd"];
            foreach (var id in order)
            {
                if (detected.FirstOrDefault(s => s.Id == id) is { } found)
                {
                    return found;
                }
            }
            // 一个都没探测到：交给系统按 PATH 解析
            return use == ShellUse.Agent
                ? new DetectedShell("powershell", "Windows PowerShell", "powershell.exe", ShellKind.WindowsPowerShell)
                : new DetectedShell("cmd", "Command Prompt", Env("ComSpec") ?? "cmd.exe", ShellKind.Cmd);
        }

        var login = Env("SHELL");
        if (use == ShellUse.Agent)
        {
            if (login is not null && Path.GetFileName(login) is "bash" or "zsh" && system.FileExists(login))
            {
                return UnixShell(login);
            }
            return UnixShell(system.FileExists("/bin/bash") ? "/bin/bash" : "/bin/sh");
        }
        return UnixShell(login is not null && system.FileExists(login) ? login : "/bin/sh");
    }

    // ───────────────────────────── Windows ─────────────────────────────

    private IReadOnlyList<DetectedShell> DetectWindows()
    {
        var result = new List<DetectedShell>();
        var pwsh = FindPwsh().ToList();
        if (Best(pwsh, preview: false) is { } stable)
        {
            result.Add(new DetectedShell("pwsh", stable.Label, stable.Executable, ShellKind.Pwsh));
        }
        if (Best(pwsh, preview: true) is { } beta)
        {
            result.Add(new DetectedShell("pwsh-preview", beta.Label, beta.Executable, ShellKind.Pwsh));
        }

        var root = Env("SystemRoot") ?? Env("windir") ?? @"C:\Windows";
        var windowsPowerShell = Path.Combine(root, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        if (system.FileExists(windowsPowerShell))
        {
            result.Add(new DetectedShell("powershell", "Windows PowerShell 5.1", windowsPowerShell, ShellKind.WindowsPowerShell));
        }
        if (FindGitBash() is { } gitBash)
        {
            result.Add(new DetectedShell("git-bash", "Git Bash", gitBash, ShellKind.GitBash));
        }
        // 不用 %ComSpec%（可能被改写）；找不到 System32 里的才回退
        var cmd = Path.Combine(root, "System32", "cmd.exe");
        if (!system.FileExists(cmd) && Env("ComSpec") is { } comSpec && system.FileExists(comSpec))
        {
            cmd = comSpec;
        }
        if (system.FileExists(cmd))
        {
            result.Add(new DetectedShell("cmd", "Command Prompt", cmd, ShellKind.Cmd));
        }
        return result;
    }

    private sealed record PwshCandidate(string Executable, int Major, int Form, bool Preview, string Label);

    private static PwshCandidate? Best(List<PwshCandidate> candidates, bool preview) => candidates
        .Where(c => c.Preview == preview)
        .OrderByDescending(c => c.Major)
        .ThenByDescending(c => c.Form)
        .ThenBy(c => c.Executable, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault();

    /// <summary>
    /// 找出所有 PowerShell 7+（pwsh.exe）：Program Files（目录名是大版本号，预览版带 -preview）、Store 的应用执行别名、
    /// dotnet tool、Scoop，最后扫 PATH。排名见 <see cref="Best"/>。
    /// </summary>
    private IEnumerable<PwshCandidate> FindPwsh()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (variable, form) in new[] { ("ProgramW6432", FormInstalled), ("ProgramFiles", FormInstalled), ("ProgramFiles(Arm)", FormInstalled), ("ProgramFiles(x86)", FormWow) })
        {
            if (Env(variable) is not { } programFiles)
            {
                continue;
            }
            foreach (var directory in system.GetDirectories(Path.Combine(programFiles, "PowerShell")))
            {
                var name = Path.GetFileName(directory);
                var preview = name.EndsWith("-preview", StringComparison.OrdinalIgnoreCase);
                if (!int.TryParse(preview ? name[..^"-preview".Length] : name, NumberStyles.None, CultureInfo.InvariantCulture, out var major))
                {
                    continue;
                }
                var executable = Path.Combine(directory, "pwsh.exe");
                if (system.FileExists(executable) && seen.Add(executable))
                {
                    yield return new PwshCandidate(executable, major, form, preview, PwshLabel(system.GetFileVersion(executable), major, preview));
                }
            }
        }

        if (Env("LOCALAPPDATA") is { } localAppData)
        {
            var apps = Path.Combine(localAppData, "Microsoft", "WindowsApps");
            foreach (var (package, preview) in new[] { ("Microsoft.PowerShell_8wekyb3d8bbwe", false), ("Microsoft.PowerShellPreview_8wekyb3d8bbwe", true) })
            {
                var executable = Path.Combine(apps, package, "pwsh.exe");
                if (system.FileExists(executable) && seen.Add(executable))
                {
                    yield return StorePwsh(executable, preview);
                }
            }
        }

        if (Env("USERPROFILE") is { } profile)
        {
            var tool = Path.Combine(profile, ".dotnet", "tools", "pwsh.exe");
            if (system.FileExists(tool) && seen.Add(tool))
            {
                yield return Versioned(tool, FormDotnetTool);
            }
        }

        foreach (var root in new[] { ScoopRoot(), ScoopGlobalRoot() }.OfType<string>())
        {
            var executable = Path.Combine(root, "apps", "pwsh", "current", "pwsh.exe");
            if (system.FileExists(executable) && seen.Add(executable))
            {
                yield return Versioned(executable, FormScoop);
            }
        }

        foreach (var directory in PathDirectories())
        {
            var executable = Path.Combine(directory, "pwsh.exe");
            if (!system.FileExists(executable) || !seen.Add(executable))
            {
                continue;
            }
            // PATH 里的 WindowsApps\pwsh.exe 同样是 Store 版的应用执行别名
            yield return directory.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)
                ? StorePwsh(executable, preview: false)
                : Versioned(executable, FormInstalled);
        }
    }

    /// <summary>应用执行别名是 0 字节的重解析点，读不到版本：Store 版自动更新，按最新处理。</summary>
    private PwshCandidate StorePwsh(string executable, bool preview)
    {
        var version = system.GetFileVersion(executable);
        return new PwshCandidate(executable, version?.Major ?? int.MaxValue, FormStore, preview, PwshLabel(version, null, preview));
    }

    /// <summary>目录名里没有版本号的安装（dotnet tool、Scoop、PATH）：读文件版本，读不到时排在最后。</summary>
    private PwshCandidate Versioned(string executable, int form)
    {
        var version = system.GetFileVersion(executable);
        return new PwshCandidate(executable, version?.Major ?? 0, form, Preview: false, PwshLabel(version, null, preview: false));
    }

    private static string PwshLabel(Version? version, int? directoryMajor, bool preview)
    {
        var text = version is not null
            ? $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}"
            : directoryMajor?.ToString(CultureInfo.InvariantCulture);
        return "PowerShell" + (text is null ? "" : " " + text) + (preview ? " Preview" : "");
    }

    /// <summary>
    /// Git for Windows 的 bash.exe：Program Files / 用户目录下的 Git，PATH 里 git.exe 所在的安装，Scoop。
    /// 不用 git-bash.exe（它会另开 mintty 窗口），也不用 System32 里的 bash.exe（那是 WSL 的入口）。
    /// </summary>
    private string? FindGitBash()
    {
        var roots = new List<string>();
        foreach (var variable in new[] { "ProgramW6432", "ProgramFiles", "ProgramFiles(x86)" })
        {
            if (Env(variable) is { } programFiles)
            {
                roots.Add(Path.Combine(programFiles, "Git"));
            }
        }
        if (Env("LOCALAPPDATA") is { } localAppData)
        {
            roots.Add(Path.Combine(localAppData, "Programs", "Git"));
        }
        foreach (var directory in PathDirectories())
        {
            if (system.FileExists(Path.Combine(directory, "git.exe")))
            {
                // <root>\cmd\git.exe、<root>\bin\git.exe、<root>\mingw64\bin\git.exe
                var parent = Path.GetDirectoryName(directory);
                if (parent is not null)
                {
                    roots.Add(parent);
                    if (Path.GetDirectoryName(parent) is { } grandParent)
                    {
                        roots.Add(grandParent);
                    }
                }
            }
        }
        if (ScoopRoot() is { } scoop)
        {
            roots.Add(Path.Combine(scoop, "apps", "git", "current"));
            roots.Add(Path.Combine(scoop, "apps", "git-with-openssh", "current"));
        }
        return roots
            .SelectMany(root => new[] { Path.Combine(root, "bin", "bash.exe"), Path.Combine(root, "usr", "bin", "bash.exe") })
            .FirstOrDefault(system.FileExists);
    }

    // ───────────────────────────── macOS / Linux ─────────────────────────────

    private IReadOnlyList<DetectedShell> DetectUnix()
    {
        var result = new List<DetectedShell>();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        void Add(string path)
        {
            var name = Path.GetFileName(path);
            if (name.Length > 0 && !NotShells.Contains(name) && system.FileExists(path) && ids.Add(name))
            {
                result.Add(UnixShell(path));
            }
        }

        if (Env("SHELL") is { } login)
        {
            Add(login);
        }
        foreach (var line in system.ReadAllLines("/etc/shells"))
        {
            var entry = line.Trim();
            if (entry.StartsWith('/'))
            {
                Add(entry);
            }
        }
        // PowerShell 不在 /etc/shells 里
        foreach (var directory in PathDirectories().Concat(["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/snap/bin"]))
        {
            Add(Path.Combine(directory, "pwsh"));
        }
        return result;
    }

    private static DetectedShell UnixShell(string path)
    {
        var name = Path.GetFileName(path);
        var kind = DetectedShell.KindOf(path);
        var label = kind switch
        {
            ShellKind.Pwsh => "PowerShell",
            ShellKind.Bash => "Bash",
            ShellKind.Zsh => "Zsh",
            ShellKind.Fish => "Fish",
            ShellKind.Nu => "Nushell",
            _ => name,
        };
        return new DetectedShell(name, label, path, kind);
    }

    // ───────────────────────────── 公共 ─────────────────────────────

    private string? Env(string name) => system.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    private string? ScoopRoot() => Env("SCOOP") ?? (Env("USERPROFILE") is { } profile ? Path.Combine(profile, "scoop") : null);

    private string? ScoopGlobalRoot() => Env("SCOOP_GLOBAL") ?? (Env("ProgramData") is { } data ? Path.Combine(data, "scoop") : null);

    private IEnumerable<string> PathDirectories()
    {
        var separator = system.Os == ShellOs.Windows ? ';' : ':';
        foreach (var entry in (Env("PATH") ?? "").Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directory = entry.Trim('"');
            if (directory.Length > 0)
            {
                yield return directory;
            }
        }
    }

    private string? FindOnPath(string name)
    {
        string[] names = system.Os == ShellOs.Windows ? [name + ".exe", name] : [name];
        foreach (var directory in PathDirectories())
        {
            foreach (var candidate in names.Select(n => Path.Combine(directory, n)))
            {
                if (system.FileExists(candidate))
                {
                    return candidate;
                }
            }
        }
        return null;
    }

    private static bool IsPath(string value) => Path.IsPathRooted(value) || value.Contains('/') || value.Contains('\\');
}
