using TKCode.Agent.Coding.Tools;
using Xunit;

namespace TKCode.Agent.Tests;

/// <summary>后台任务列表变化：启动与结束都通知，并带上发起任务的会话（任务球据此只显示本会话的任务）。</summary>
public sealed class BackgroundTasksChangedTests
{
    [Fact]
    public void Start_and_Complete_raise_Changed_with_the_owning_session()
    {
        var tasks = new BackgroundTasks();
        var owners = new List<string?>();
        tasks.Changed += owners.Add;

        var task = tasks.Start("bash", "npm run dev", null, () => Task.CompletedTask, "session-1");
        Assert.Equal(["session-1"], owners);

        tasks.Complete(task, "completed", "exit 0");
        Assert.Equal(["session-1", "session-1"], owners);
        Assert.Equal("completed", task.Status);
    }

    [Fact]
    public void Completing_twice_does_not_raise_Changed_again()
    {
        var tasks = new BackgroundTasks();
        var count = 0;
        tasks.Changed += _ => count++;

        var task = tasks.Start("agent", "reviewer", null, () => Task.CompletedTask);
        tasks.Complete(task, "completed", null);
        tasks.Complete(task, "failed", null);

        Assert.Equal(2, count);
        Assert.Equal("completed", task.Status);
    }
}
