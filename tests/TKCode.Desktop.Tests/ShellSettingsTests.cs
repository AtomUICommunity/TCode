using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Localization;
using TKCode.Shell;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>默认 shell 的设置：写回 agent.shell.profile / terminal.defaultProfile，以及设置页的选择器。</summary>
public sealed class ShellSettingsTests
{
    private const string Pwsh = "C:/pf/PowerShell/7/pwsh.exe";
    private const string WindowsPowerShell = "C:/win/System32/WindowsPowerShell/v1.0/powershell.exe";
    private const string GitBash = "C:/pf/Git/bin/bash.exe";
    private const string Cmd = "C:/win/System32/cmd.exe";

    /// <summary>扮演一台装有 PowerShell 7、Windows PowerShell、Git Bash 的 Windows 机器。</summary>
    private sealed class FakeWindows : IShellSystem
    {
        private static readonly HashSet<string> Files = [Pwsh, WindowsPowerShell, GitBash, Cmd, "D:/tools/nu.exe"];

        public ShellOs Os => ShellOs.Windows;

        public string? GetEnvironmentVariable(string name) => name switch
        {
            "ProgramFiles" => "C:/pf",
            "SystemRoot" => "C:/win",
            "PATH" => "D:/tools",
            _ => null,
        };

        public bool FileExists(string path) => Files.Contains(path.Replace('\\', '/'));

        public IReadOnlyList<string> GetDirectories(string path) =>
            path.Replace('\\', '/') == "C:/pf/PowerShell" ? ["C:/pf/PowerShell/7"] : [];

        public Version? GetFileVersion(string path) => path.Replace('\\', '/') == Pwsh ? new Version(7, 5, 1, 0) : null;

        public IReadOnlyList<string> ReadAllLines(string path) => [];
    }

    private static ShellPickerViewModel Picker(ShellUse use, string? preference, List<string?> saved) =>
        new(use, TKCodeLangResourceKind.AgentShellLabel, TKCodeLangResourceKind.AgentShellHint, () => preference, saved.Add, new ShellCatalog(new FakeWindows()));

    [Fact]
    public void SetShell_WritesNestedFields_AndRemovesEmptySections()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("tkcode-shell-settings-").FullName, "settings.json");
        using var settings = new UserSettingsService(NullLogger<UserSettingsService>.Instance, path);

        settings.SetAgentShell("git-bash");
        settings.SetTerminalShell(@"C:\Program Files\PowerShell\7-preview\pwsh.exe");
        var written = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("git-bash", written["agent"]!["shell"]!["profile"]!.GetValue<string>());
        Assert.Equal(@"C:\Program Files\PowerShell\7-preview\pwsh.exe", written["terminal"]!["defaultProfile"]!.GetValue<string>());
        Assert.Equal("git-bash", settings.Current.Agent!.Shell!.Profile);
        Assert.Equal(@"C:\Program Files\PowerShell\7-preview\pwsh.exe", settings.Current.Terminal!.DefaultProfile);

        // auto 不落盘：字段与变空的 agent / shell / terminal 节一起删除
        settings.SetAgentShell(null);
        settings.SetTerminalShell(null);
        var cleaned = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.False(cleaned.ContainsKey("agent"));
        Assert.False(cleaned.ContainsKey("terminal"));
        Assert.Null(settings.Current.Agent);
    }

    [Fact]
    public void SetShell_KeepsSiblingFields()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("tkcode-shell-settings-").FullName, "settings.json");
        File.WriteAllText(path, """{ "agent": { "shell": { "profile": "pwsh" }, "other": 1 }, "terminal": { "defaultProfile": "cmd" } }""");
        using var settings = new UserSettingsService(NullLogger<UserSettingsService>.Instance, path);

        settings.SetAgentShell(null);

        var written = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Null(written["agent"]!["shell"]);
        Assert.Equal(1, written["agent"]!["other"]!.GetValue<int>());
        Assert.Equal("cmd", written["terminal"]!["defaultProfile"]!.GetValue<string>());
    }

    [Fact]
    public async Task Picker_ListsAutoAndDetectedShells_AndMarksTheCurrentOne()
    {
        var picker = Picker(ShellUse.Terminal, null, []);
        await picker.RefreshAsync();

        Assert.Equal(["auto", "pwsh", "powershell", "git-bash", "cmd"], picker.Choices.Select(c => c.Value));
        Assert.Equal("auto", picker.Choices.Single(c => c.IsSelected).Value);
        Assert.Equal($"{Strings.Get(TKCodeLangResourceKind.ShellAuto)} · PowerShell 7.5.1", picker.Label);
        Assert.Equal(Strings.Format(TKCodeLangResourceKind.ShellCurrent, Pwsh), picker.Detail);
        Assert.True(picker.HasDetail);
    }

    [Fact]
    public async Task Picker_ForTheAgent_DoesNotOfferCmd()
    {
        var picker = Picker(ShellUse.Agent, "git-bash", []);
        await picker.RefreshAsync();

        Assert.Equal(["auto", "pwsh", "powershell", "git-bash"], picker.Choices.Select(c => c.Value));
        Assert.Equal("git-bash", picker.Choices.Single(c => c.IsSelected).Value);
        Assert.Equal("Git Bash", picker.Label);
    }

    [Fact]
    public async Task Picker_CustomPath_IsShownAsItsOwnSelectedChoice()
    {
        var picker = Picker(ShellUse.Agent, "\"D:/tools/nu.exe\"", []);
        await picker.RefreshAsync();

        var selected = picker.Choices.Single(c => c.IsSelected);
        Assert.Equal("D:/tools/nu.exe", selected.Value);
        Assert.Equal("D:/tools/nu.exe", picker.CustomPath);
        Assert.Equal(Strings.Format(TKCodeLangResourceKind.ShellCurrent, "D:/tools/nu.exe"), picker.Detail);
    }

    [Fact]
    public async Task Picker_UnavailableShell_ExplainsTheFallback()
    {
        var picker = Picker(ShellUse.Agent, "zsh", []);
        await picker.RefreshAsync();

        Assert.Equal(Strings.Format(TKCodeLangResourceKind.ShellUnavailable, "zsh"), picker.Label);
        Assert.Equal(Strings.Format(TKCodeLangResourceKind.ShellFallback, "zsh", "PowerShell 7.5.1"), picker.Detail);
    }

    [Fact]
    public async Task Picker_SavesChoices_AndTreatsAutoAndBlankAsDefault()
    {
        var saved = new List<string?>();
        var picker = Picker(ShellUse.Terminal, null, saved);
        await picker.RefreshAsync();
        var closed = 0;
        picker.CloseRequested += () => closed++;

        picker.ChooseCommand.Execute(picker.Choices.Single(c => c.Value == "powershell"));
        picker.ChooseCommand.Execute(picker.Choices.Single(c => c.Value == "auto"));
        picker.CustomPath = "  \"E:\\shells\\fish.exe\"  ";
        picker.ApplyCustomPathCommand.Execute(null);
        picker.CustomPath = "   ";
        picker.ApplyCustomPathCommand.Execute(null);

        Assert.Equal(["powershell", null, @"E:\shells\fish.exe"], saved);
        Assert.Equal(3, closed);
    }
}
