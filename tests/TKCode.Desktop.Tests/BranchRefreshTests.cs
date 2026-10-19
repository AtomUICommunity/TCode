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
    public void Reads_Branches_In_Linked_Worktree()
    {
        var main = NewRepository();
        Git(main, "branch", "feature");
        var worktree = Path.Combine(Directory.CreateTempSubdirectory("tkcode-worktree-").FullName, "wt");
        Git(main, "worktree", "add", "-q", "-b", "wt-branch", worktree);
        Assert.True(File.Exists(Path.Combine(worktree, ".git")), "worktree 的 .git 应是文件");

        var info = new WorkspaceInfo(worktree, "wt");

        // HEAD 来自 worktree 自己的 git 目录，分支列表来自主仓库
        Assert.Equal("wt-branch", info.ReadBranch());
        Assert.Equal(["feature", "main", "wt-branch"], info.ReadBranches());

        Git(worktree, "switch", "-q", "-c", "topic");
        Assert.Equal("topic", info.ReadBranch());
        Assert.Contains("topic", info.ReadBranches());
        Assert.Equal("main", new WorkspaceInfo(main, "main").ReadBranch());
    }

    [Fact]
    public void Reads_Branches_When_Git_Dir_Is_A_File()
    {
        // submodule 与 --separate-git-dir 的 .git 是指向别处的文件
        var root = Directory.CreateTempSubdirectory("tkcode-gitfile-").FullName;
        var work = Path.Combine(root, "work");
        Git(root, "init", "-q", "-b", "main", $"--separate-git-dir={Path.Combine(root, "elsewhere.git")}", work);
        Git(work, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "--allow-empty", "-m", "init");
        Git(work, "branch", "feature");
        Assert.True(File.Exists(Path.Combine(work, ".git")));

        var info = new WorkspaceInfo(work, "work");

        Assert.Equal("main", info.ReadBranch());
        Assert.Equal(["feature", "main"], info.ReadBranches());
    }

    [Fact]
    public async Task Watcher_Fires_For_Worktree_Switch_And_For_Branch_Created_In_Main_Repository()
    {
        var main = NewRepository();
        var worktree = Path.Combine(Directory.CreateTempSubdirectory("tkcode-worktree-").FullName, "wt");
        Git(main, "worktree", "add", "-q", "-b", "wt-branch", worktree);
        var changed = new SemaphoreSlim(0);
        using var watcher = new GitBranchWatcher(worktree, () => changed.Release());

        Git(worktree, "switch", "-q", "-c", "topic");
        Assert.True(await changed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "worktree 里 switch -c 后应收到通知");

        while (changed.CurrentCount > 0)
        {
            await changed.WaitAsync(TestContext.Current.CancellationToken);
        }
        Git(main, "branch", "from-main");
        Assert.True(await changed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "主仓库里新建分支后应收到通知");
        Assert.Contains("from-main", new WorkspaceInfo(worktree, "wt").ReadBranches());
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
