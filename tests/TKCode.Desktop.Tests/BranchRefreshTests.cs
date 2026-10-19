using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>
/// 底部分支选择器要跟上应用外的分支变化（终端 / Agent 里 git switch -c、新建、删除），
/// 而不是只在切换项目或在弹层内切换时才读取一次。
/// </summary>
public sealed class BranchRefreshTests
{
    [Fact]
    public void Picker_Reloads_When_Opened()
    {
        var project = NewRepository();
        var workspace = new WorkspaceInfo(project, "demo");
        BranchPickerViewModel? picker = null;
        picker = new BranchPickerViewModel(_ => Task.FromResult<string?>(null), () => false,
            () => picker!.Load(workspace.ReadBranch(), workspace.ReadBranches()));
        picker.Load(workspace.ReadBranch(), workspace.ReadBranches());
        Assert.Equal("main", picker.Current);
        Assert.DoesNotContain(picker.Items, b => b.Name == "topic");

        // 应用外新建并切到新分支
        Git(project, "switch", "-q", "-c", "topic");
        Assert.Equal("main", picker.Current);

        picker.OnOpened();

        Assert.Equal("topic", picker.Current);
        Assert.Equal("topic", picker.DisplayName);
        Assert.Equal(["topic", "main"], picker.Items.Select(b => b.Name));
        Assert.True(picker.Items[0].IsCurrent);
    }

    [Fact]
    public void Picker_Load_Keeps_Items_When_Nothing_Changed()
    {
        var project = NewRepository();
        var workspace = new WorkspaceInfo(project, "demo");
        var picker = new BranchPickerViewModel(_ => Task.FromResult<string?>(null), () => false, () => { });
        picker.Load(workspace.ReadBranch(), workspace.ReadBranches());
        var first = picker.Items.ToArray();

        picker.Load(workspace.ReadBranch(), workspace.ReadBranches());

        Assert.Equal(first, picker.Items);
    }

    [Fact]
    public async Task Watcher_Fires_When_Branch_Is_Created_Or_Switched()
    {
        var project = NewRepository();
        var changed = new SemaphoreSlim(0);
        using var watcher = new GitBranchWatcher(project, () => changed.Release());

        Git(project, "switch", "-q", "-c", "topic");
        Assert.True(await changed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "switch -c 后应收到通知");
        Assert.Equal("topic", new WorkspaceInfo(project, "demo").ReadBranch());

        while (changed.CurrentCount > 0)
        {
            await changed.WaitAsync(TestContext.Current.CancellationToken);
        }
        Git(project, "branch", "another");
        Assert.True(await changed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "branch 后应收到通知");
        Assert.Contains("another", new WorkspaceInfo(project, "demo").ReadBranches());
    }

    [Fact]
    public void Watcher_Ignores_Non_Repository()
    {
        var folder = Directory.CreateTempSubdirectory("tkcode-nogit-").FullName;
        using var watcher = new GitBranchWatcher(folder, () => { });
    }

    private static string NewRepository()
    {
        var project = Directory.CreateTempSubdirectory("tkcode-branch-").FullName;
        Git(project, "init", "-q", "-b", "main");
        Git(project, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "--allow-empty", "-m", "init");
        return project;
    }

    private static void Git(string cwd, params string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }
}
