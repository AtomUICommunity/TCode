using TKCode.Shell;
using Xunit;

namespace TKCode.Shell.Tests;

/// <summary>内存里的假系统：路径一律用 Path.Combine 拼，因此 Windows 的探测规则在 macOS / Linux 上也能验证。</summary>
internal sealed class FakeShellSystem(ShellOs os) : IShellSystem
{
    private readonly HashSet<string> _files = new(os == ShellOs.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _environment = new(os == ShellOs.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Dictionary<string, Version> _versions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string[]> _text = new(StringComparer.Ordinal);

    public ShellOs Os => os;

    public FakeShellSystem Env(string name, string? value)
    {
        _environment[name] = value;
        return this;
    }

    public FakeShellSystem File(string path, Version? version = null)
    {
        _files.Add(path);
        if (version is not null)
        {
            _versions[path] = version;
        }
        return this;
    }

    public FakeShellSystem Text(string path, params string[] lines)
    {
        _text[path] = lines;
        return this;
    }

    public string? GetEnvironmentVariable(string name) => _environment.GetValueOrDefault(name);

    public bool FileExists(string path) => _files.Contains(path);

    public IReadOnlyList<string> GetDirectories(string path)
    {
        var prefix = path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return _files
            .Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && f.IndexOf(Path.DirectorySeparatorChar, prefix.Length) > 0)
            .Select(f => f[..f.IndexOf(Path.DirectorySeparatorChar, prefix.Length)])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Version? GetFileVersion(string path) => _versions.GetValueOrDefault(path);

    public IReadOnlyList<string> ReadAllLines(string path) => _text.GetValueOrDefault(path) ?? [];
}

public sealed class ShellCatalogTests
{
    private const string ProgramFiles = @"C:\Program Files";
    private const string SystemRoot = @"C:\Windows";
    private static readonly string WindowsPowerShell = Path.Combine(SystemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    private static readonly string Cmd = Path.Combine(SystemRoot, "System32", "cmd.exe");
    private static readonly string GitBash = Path.Combine(ProgramFiles, "Git", "bin", "bash.exe");
    private static readonly string Pwsh7 = Path.Combine(ProgramFiles, "PowerShell", "7", "pwsh.exe");

    private static FakeShellSystem Windows() => new FakeShellSystem(ShellOs.Windows)
        .Env("ProgramFiles", ProgramFiles)
        .Env("ProgramW6432", ProgramFiles)
        .Env("ProgramFiles(x86)", @"C:\Program Files (x86)")
        .Env("SystemRoot", SystemRoot)
        .Env("LOCALAPPDATA", @"C:\Users\me\AppData\Local")
        .Env("USERPROFILE", @"C:\Users\me")
        .File(WindowsPowerShell)
        .File(Cmd);

    private static ShellSelection Resolve(FakeShellSystem system, string? preference, ShellUse use) => new ShellCatalog(system).Resolve(preference, use);

    [Fact]
    public void Windows_PrefersPowerShell7_OverGitBash_ForAgentAndTerminal()
    {
        var system = Windows().File(Pwsh7).File(GitBash);

        var agent = Resolve(system, null, ShellUse.Agent);
        Assert.Equal("pwsh", agent.Shell.Id);
        Assert.Equal(Pwsh7, agent.Shell.Executable);
        Assert.Null(agent.Warning);
        Assert.Equal("pwsh", Resolve(system, "auto", ShellUse.Terminal).Shell.Id);
    }

    [Fact]
    public void Windows_PicksTheNewestMajorVersion()
    {
        var pwsh8 = Path.Combine(ProgramFiles, "PowerShell", "8", "pwsh.exe");
        var system = Windows().File(Pwsh7).File(pwsh8).File(Path.Combine(ProgramFiles, "PowerShell", "6", "pwsh.exe"));

        var shell = Resolve(system, null, ShellUse.Agent).Shell;

        Assert.Equal(pwsh8, shell.Executable);
        Assert.Equal("PowerShell 8", shell.Label);
    }

    [Fact]
    public void Windows_PreviewIsListedButNeverChosenAutomatically()
    {
        var preview = Path.Combine(ProgramFiles, "PowerShell", "7-preview", "pwsh.exe");
        var system = Windows().File(Pwsh7).File(Path.Combine(ProgramFiles, "PowerShell", "8-preview", "pwsh.exe")).File(preview);

        var catalog = new ShellCatalog(system);

        Assert.Equal(Pwsh7, catalog.Resolve(null, ShellUse.Agent).Shell.Executable);
        var listed = catalog.Detect().Single(s => s.Id == "pwsh-preview");
        Assert.Equal(Path.Combine(ProgramFiles, "PowerShell", "8-preview", "pwsh.exe"), listed.Executable);
        Assert.Equal(listed.Executable, catalog.Resolve("pwsh-preview", ShellUse.Agent).Shell.Executable);
    }

    [Fact]
    public void Windows_StoreAliasWithUnreadableVersionCountsAsNewest()
    {
        var store = Path.Combine(@"C:\Users\me\AppData\Local", "Microsoft", "WindowsApps", "Microsoft.PowerShell_8wekyb3d8bbwe", "pwsh.exe");
        var system = Windows().File(Pwsh7).File(store);

        Assert.Equal(store, Resolve(system, null, ShellUse.Agent).Shell.Executable);
    }

    [Fact]
    public void Windows_SameMajorPrefersStoreOverTraditionalInstall()
    {
        var store = Path.Combine(@"C:\Users\me\AppData\Local", "Microsoft", "WindowsApps", "Microsoft.PowerShell_8wekyb3d8bbwe", "pwsh.exe");
        var system = Windows().File(Pwsh7).File(store, new Version(7, 4, 6, 0));

        var shell = Resolve(system, null, ShellUse.Agent).Shell;

        Assert.Equal(store, shell.Executable);
        Assert.Equal("PowerShell 7.4.6", shell.Label);
    }

    [Fact]
    public void Windows_FindsPwshOnPath_WhenNotInstalledInTheUsualPlaces()
    {
        var shim = Path.Combine(@"C:\tools\pwsh", "pwsh.exe");
        var system = Windows().Env("PATH", @"C:\tools\pwsh").File(shim, new Version(7, 5, 0, 0));

        Assert.Equal(shim, Resolve(system, null, ShellUse.Terminal).Shell.Executable);
    }

    [Fact]
    public void Windows_WithoutPwsh_AgentFallsBackToGitBashThenWindowsPowerShell_TerminalToWindowsPowerShell()
    {
        var withGit = Windows().File(GitBash);
        Assert.Equal("git-bash", Resolve(withGit, null, ShellUse.Agent).Shell.Id);
        Assert.Equal("powershell", Resolve(withGit, null, ShellUse.Terminal).Shell.Id);

        var plain = Windows();
        Assert.Equal("powershell", Resolve(plain, null, ShellUse.Agent).Shell.Id);
    }

    [Fact]
    public void Windows_NothingDetected_FallsBackToSystemCommands()
    {
        var system = new FakeShellSystem(ShellOs.Windows).Env("ComSpec", @"D:\sys\cmd.exe");

        var agent = Resolve(system, null, ShellUse.Agent).Shell;
        Assert.Equal("powershell.exe", agent.Executable);
        Assert.Equal(ShellKind.WindowsPowerShell, agent.Kind);
        var terminal = Resolve(system, null, ShellUse.Terminal).Shell;
        Assert.Equal(@"D:\sys\cmd.exe", terminal.Executable);
        Assert.Equal(ShellKind.Cmd, terminal.Kind);
    }

    [Fact]
    public void Windows_FindsGitBashThroughGitOnPath()
    {
        var root = Path.Combine(@"D:\dev", "Git");
        var bash = Path.Combine(root, "usr", "bin", "bash.exe");
        var system = Windows().Env("PATH", Path.Combine(root, "cmd")).File(Path.Combine(root, "cmd", "git.exe")).File(bash);

        Assert.Equal(bash, new ShellCatalog(system).Detect().Single(s => s.Id == "git-bash").Executable);
    }

    [Fact]
    public void Windows_DetectListsShellsInMenuOrder()
    {
        var system = Windows().File(Pwsh7).File(GitBash);

        Assert.Equal(["pwsh", "powershell", "git-bash", "cmd"], new ShellCatalog(system).Detect().Select(s => s.Id));
    }

    [Fact]
    public void Preference_ById_IsHonored_AndCmdIsRejectedOnlyForTheAgent()
    {
        var system = Windows().File(Pwsh7).File(GitBash);

        Assert.Equal("git-bash", Resolve(system, "Git-Bash", ShellUse.Agent).Shell.Id);
        Assert.Equal("powershell", Resolve(system, "powershell", ShellUse.Terminal).Shell.Id);

        var agentCmd = Resolve(system, "cmd", ShellUse.Agent);
        Assert.Equal("pwsh", agentCmd.Shell.Id);
        Assert.Contains("cmd.exe", agentCmd.Warning);
        var terminalCmd = Resolve(system, "cmd", ShellUse.Terminal);
        Assert.Equal("cmd", terminalCmd.Shell.Id);
        Assert.Null(terminalCmd.Warning);
    }

    [Fact]
    public void Preference_ByPath_AndByProgramName()
    {
        var nu = Path.Combine(@"C:\tools", "nu.exe");
        var custom = Path.Combine(@"E:\shells", "fish.exe");
        var system = Windows().File(Pwsh7).File(nu).File(custom).Env("PATH", @"C:\tools");

        var byPath = Resolve(system, "\"" + custom + "\"", ShellUse.Agent);
        Assert.Equal(custom, byPath.Shell.Executable);
        Assert.Equal(ShellKind.Fish, byPath.Shell.Kind);
        Assert.Null(byPath.Warning);

        var byName = Resolve(system, "nu", ShellUse.Terminal);
        Assert.Equal(nu, byName.Shell.Executable);
        Assert.Equal(ShellKind.Nu, byName.Shell.Kind);
    }

    [Fact]
    public void Preference_ThatCannotBeFound_FallsBackToAutoWithWarning()
    {
        var system = Windows().File(Pwsh7);

        foreach (var missing in new[] { "zsh", @"C:\nowhere\bash.exe", "pwsh-preview" })
        {
            var selection = Resolve(system, missing, ShellUse.Agent);
            Assert.Equal("pwsh", selection.Shell.Id);
            Assert.NotNull(selection.Warning);
        }
    }

    [Fact]
    public void Unix_Auto_KeepsTheExistingBehavior()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // 用 / 开头的路径描述 Unix 文件系统
        }
        var zsh = Path.Combine(Path.DirectorySeparatorChar.ToString(), "bin", "zsh");
        var bash = Path.Combine(Path.DirectorySeparatorChar.ToString(), "bin", "bash");
        var sh = Path.Combine(Path.DirectorySeparatorChar.ToString(), "bin", "sh");
        var fish = Path.Combine(Path.DirectorySeparatorChar.ToString(), "opt", "bin", "fish");

        var withZsh = new FakeShellSystem(ShellOs.MacOS).Env("SHELL", zsh).File(zsh).File(bash).File(sh);
        Assert.Equal(zsh, Resolve(withZsh, null, ShellUse.Agent).Shell.Executable);
        Assert.Equal(zsh, Resolve(withZsh, null, ShellUse.Terminal).Shell.Executable);

        // fish 不适合执行模型写的 bash 命令：Agent 退回 /bin/bash，终端仍用登录 shell
        var withFish = new FakeShellSystem(ShellOs.Linux).Env("SHELL", fish).File(fish).File(bash).File(sh);
        Assert.Equal(bash, Resolve(withFish, null, ShellUse.Agent).Shell.Executable);
        Assert.Equal(fish, Resolve(withFish, null, ShellUse.Terminal).Shell.Executable);

        var bare = new FakeShellSystem(ShellOs.Linux);
        Assert.Equal(sh, Resolve(bare, null, ShellUse.Agent).Shell.Executable);
        Assert.Equal(sh, Resolve(bare, null, ShellUse.Terminal).Shell.Executable);
    }

    [Fact]
    public void Unix_Detect_ReadsEtcShells_SkipsNonShells_AndFindsPwsh()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        string Abs(params string[] parts) => Path.DirectorySeparatorChar + Path.Combine(parts);
        var system = new FakeShellSystem(ShellOs.Linux)
            .Env("SHELL", Abs("usr", "bin", "zsh"))
            .Env("PATH", Abs("home", "me", "bin") + ":" + Abs("usr", "bin"))
            .Text("/etc/shells", "# list", "", Abs("bin", "bash"), Abs("usr", "bin", "bash"), Abs("usr", "sbin", "nologin"), "relative/ignored", Abs("bin", "sh"))
            .File(Abs("usr", "bin", "zsh")).File(Abs("bin", "bash")).File(Abs("usr", "bin", "bash")).File(Abs("usr", "sbin", "nologin")).File(Abs("bin", "sh"))
            .File(Abs("home", "me", "bin", "pwsh"));

        var shells = new ShellCatalog(system).Detect();

        Assert.Equal(["zsh", "bash", "sh", "pwsh"], shells.Select(s => s.Id));
        Assert.Equal(ShellKind.Pwsh, shells[^1].Kind);
        Assert.Equal(Abs("bin", "bash"), shells.Single(s => s.Id == "bash").Executable);
    }

    [Fact]
    public void Detect_IsCachedOnlyWhenAskedTo()
    {
        var system = Windows();
        var uncached = new ShellCatalog(system);
        var cached = new ShellCatalog(system, TimeSpan.FromMinutes(5));
        Assert.DoesNotContain(uncached.Detect(), s => s.Id == "pwsh");
        Assert.DoesNotContain(cached.Detect(), s => s.Id == "pwsh");

        system.File(Pwsh7);

        Assert.Contains(uncached.Detect(), s => s.Id == "pwsh");
        Assert.DoesNotContain(cached.Detect(), s => s.Id == "pwsh");
    }
}
