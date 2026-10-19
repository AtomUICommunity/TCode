using System.Text.Json;
using TKCode.Agent.Coding;
using TKCode.Agent.Coding.Prompt;
using TKCode.Agent.Coding.Tools;
using TKCode.Agent.Coding.Tools.Shell;
using TKCode.Agent.Coding.Workspace;
using TKCode.Ai.Fake;
using TKCode.Ai.Messages;
using TKCode.Config;
using TKCode.Shell;
using Xunit;

namespace TKCode.Agent.Tests;

/// <summary>默认 shell：设置解析、调用方式、系统提示里的 Shell 行，以及 bash 工具真的用了所选 shell。</summary>
public sealed class ShellResolverTests
{
    private static string Text(AgentToolResult result) => string.Concat(result.Content.OfType<TextContent>().Select(t => t.Text));

    [Fact]
    public void ToInfo_PowerShell_UsesNonInteractiveCommandMode_AndUtf8OutputOnWindows()
    {
        var pwsh = ShellResolver.ToInfo(new DetectedShell("pwsh", "PowerShell 7", "pwsh", ShellKind.Pwsh));
        Assert.Equal(["-NoProfile", "-NonInteractive", "-Command"], pwsh.PrefixArguments);
        Assert.Equal("pwsh", pwsh.Name);
        Assert.True(pwsh.IsPowerShell);
        Assert.Equal(OperatingSystem.IsWindows() ? ShellResolver.Utf8OutputPrefix : null, pwsh.CommandPrefix);

        var legacy = ShellResolver.ToInfo(new DetectedShell("powershell", "Windows PowerShell", "powershell.exe", ShellKind.WindowsPowerShell));
        Assert.Equal(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command"], legacy.PrefixArguments);
        Assert.Equal("powershell", legacy.Name);
        Assert.Equal(ShellResolver.Utf8OutputPrefix, legacy.CommandPrefix);
        Assert.StartsWith("[Console]::OutputEncoding=", ShellResolver.Utf8OutputPrefix);
    }

    [Fact]
    public void ToInfo_PosixStyleShells_UseDashC_WithoutPrefix()
    {
        foreach (var (path, kind, name) in new[] { ("/bin/bash", ShellKind.Bash, "bash"), ("C:/Git/bin/bash.exe", ShellKind.GitBash, "bash"), ("/bin/zsh", ShellKind.Zsh, "zsh"), ("/usr/bin/fish", ShellKind.Fish, "fish") })
        {
            var info = ShellResolver.ToInfo(new DetectedShell(name, name, path, kind));
            Assert.Equal(["-c"], info.PrefixArguments);
            Assert.Equal(name, info.Name);
            Assert.Null(info.CommandPrefix);
            Assert.False(info.IsPowerShell);
        }
    }

    [Fact]
    public void Resolve_Preference_ByPath_AndFallbackForUnknown()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var chosen = ShellResolver.Resolve("/bin/sh");
        Assert.Equal("/bin/sh", chosen.Executable);
        Assert.Equal("sh", chosen.Name);

        // 指定的 shell 找不到时退回默认选择，auto / 空也是默认选择（ShellInfo 里的参数列表按引用比较，这里逐项比）
        var fallback = ShellResolver.Current;
        foreach (var preference in new[] { "definitely-not-a-shell", "auto", null })
        {
            var info = ShellResolver.Resolve(preference);
            Assert.Equal(fallback.Executable, info.Executable);
            Assert.Equal(fallback.PrefixArguments, info.PrefixArguments);
        }
    }

    [Fact]
    public async Task BashTool_RunsTheConfiguredShell_AndTheDescriptionDoesNotNameIt()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var root = Directory.CreateTempSubdirectory("tkcode-shell-").FullName;
        var profile = "/bin/sh";
        var context = new ToolContext(new WorkspaceRoots(root), "shell-" + Guid.NewGuid().ToString("N")[..8]) { ShellProfile = () => profile };
        var tool = new BashTool(context);
        var descriptionBefore = tool.Description;

        using var doc = JsonDocument.Parse("""{"command":"echo $0"}""");
        var viaSh = await tool.ExecuteAsync(new ToolInvocation("call", doc.RootElement.Clone()), CancellationToken.None);
        Assert.False(viaSh.IsError, Text(viaSh));
        Assert.Contains("/bin/sh", Text(viaSh));

        // 设置在运行中改变：下一次调用就用新的 shell，而冻结的工具描述保持不变（避免破坏 prompt cache）
        profile = "/bin/bash";
        var viaBash = await tool.ExecuteAsync(new ToolInvocation("call", doc.RootElement.Clone()), CancellationToken.None);
        Assert.Contains("/bin/bash", Text(viaBash));
        Assert.Equal(descriptionBefore, tool.Description);
        Assert.DoesNotContain("/bin/", tool.Description);
        Assert.Contains("Shell line of the environment section", tool.Description);
    }

    [Fact]
    public void SystemPrompt_ShellLine_FollowsTheGivenShell_OrTheDefault()
    {
        var root = Directory.CreateTempSubdirectory("tkcode-shell-prompt-").FullName;
        string Environment(string? shell) => SystemPromptBuilder.Build(new WorkspaceRoots(root), [], FakeWireApi.Models[0], new DateOnly(2026, 10, 19), shell: shell)
            .Single(s => s.Name == "environment").Text;

        Assert.Contains("Shell: pwsh\n", Environment("pwsh"));
        Assert.Contains($"Shell: {ShellResolver.Current.Name}\n", Environment(null));

        // 用户改了 shell：重新计算的 environment 节有差异，作为系统消息补丁通知模型
        var before = SystemPromptBuilder.Build(new WorkspaceRoots(root), [], FakeWireApi.Models[0], new DateOnly(2026, 10, 19), shell: "bash");
        var after = SystemPromptBuilder.Build(new WorkspaceRoots(root), [], FakeWireApi.Models[0], new DateOnly(2026, 10, 19), shell: "pwsh");
        var patch = SystemPromptBuilder.Diff(before, after);
        Assert.NotNull(patch);
        Assert.Contains("Shell: pwsh", patch);
    }

    [Fact]
    public void Settings_ParseShellSections_AndToleratesBadValues()
    {
        var ok = SettingsFile.Parse("""{ "agent": { "shell": { "profile": "pwsh" } }, "terminal": { "defaultProfile": "C:\\tools\\nu.exe" } }""");
        Assert.True(ok.IsValid);
        Assert.Empty(ok.Warnings);
        Assert.Equal("pwsh", ok.Settings!.Agent!.Shell!.Profile);
        Assert.Equal(@"C:\tools\nu.exe", ok.Settings.Terminal!.DefaultProfile);

        // 类型不对只丢掉该字段并给出警告，整个文件仍然有效；未知字段同样只给警告
        var bad = SettingsFile.Parse("""{ "agent": { "shell": { "profile": 7, "extra": 1 }, "tools": {} }, "terminal": { "defaultProfile": ["a"] }, "model": "x/y" }""");
        Assert.True(bad.IsValid);
        Assert.Null(bad.Settings!.Agent!.Shell!.Profile);
        Assert.Null(bad.Settings.Terminal!.DefaultProfile);
        Assert.Equal("x/y", bad.Settings.Model);
        Assert.Contains(bad.Warnings, w => w.Contains("agent.shell.profile"));
        Assert.Contains(bad.Warnings, w => w.Contains("terminal.defaultProfile"));
        Assert.Contains(bad.Warnings, w => w.Contains("agent.shell.extra"));
        Assert.Contains(bad.Warnings, w => w.Contains("agent.tools"));
    }
}
