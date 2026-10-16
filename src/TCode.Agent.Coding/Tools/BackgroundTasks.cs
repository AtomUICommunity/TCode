using System.Collections.Concurrent;
using System.Text;

namespace TCode.Agent.Coding.Tools;

/// <summary>一个后台任务（后台命令或后台子 Agent）。</summary>
public sealed class BackgroundTask
{
    private readonly Func<Task> _stop;

    internal BackgroundTask(string id, string type, string description, string? outputPath, Func<Task> stop)
    {
        Id = id;
        Type = type;
        Description = description;
        OutputPath = outputPath;
        _stop = stop;
    }

    public string Id { get; }

    /// <summary>bash / agent。</summary>
    public string Type { get; }
    public string Description { get; }
    public string? OutputPath { get; }

    /// <summary>running / completed / failed / stopped。</summary>
    public string Status { get; internal set; } = "running";

    /// <summary>结束时的摘要（例如退出码、子 Agent 的最终报告）。</summary>
    public string? Result { get; internal set; }

    public bool IsRunning => Status == "running";

    /// <summary>发起任务的会话；null 表示主会话。</summary>
    public string? Owner { get; internal init; }

    /// <summary>已请求停止：之后进程退出时状态记为 stopped 而不是 failed。</summary>
    public bool StopRequested { get; internal set; }

    internal Task StopAsync() => _stop();
}

/// <summary>
/// 会话的后台任务。任务结束时触发 <see cref="Completed"/>，由会话向 Agent 注入 &lt;task-notification&gt;。
/// 主会话与它的子 Agent 共用同一个实例。
/// </summary>
public sealed class BackgroundTasks
{
    private readonly ConcurrentDictionary<string, BackgroundTask> _tasks = new(StringComparer.Ordinal);
    private int _next;

    /// <summary>任务结束（完成、失败或被停止）。参数为结束的任务与应通知的会话 id（null 表示主会话）。</summary>
    public event Action<BackgroundTask, string?>? Completed;

    public IReadOnlyList<BackgroundTask> All => _tasks.Values.OrderBy(t => t.Id, StringComparer.Ordinal).ToArray();

    public BackgroundTask? Find(string id) => _tasks.GetValueOrDefault(id);

    /// <param name="ownerSessionId">发起任务的会话（子 Agent 的后台任务结束时通知子 Agent 自己）。</param>
    public BackgroundTask Start(string type, string description, string? outputPath, Func<Task> stop, string? ownerSessionId = null)
    {
        var id = (type == "agent" ? "a" : "b") + Interlocked.Increment(ref _next);
        var task = new BackgroundTask(id, type, description, outputPath, stop) { Owner = ownerSessionId };
        _tasks[id] = task;
        return task;
    }

    public void Complete(BackgroundTask task, string status, string? result)
    {
        lock (task)
        {
            if (!task.IsRunning)
            {
                return;
            }
            task.Status = status;
            task.Result = result;
        }
        Completed?.Invoke(task, task.Owner);
    }

    public async Task<bool> StopAsync(string id)
    {
        if (!_tasks.TryGetValue(id, out var task) || !task.IsRunning)
        {
            return false;
        }
        task.StopRequested = true;
        await task.StopAsync();
        Complete(task, "stopped", task.Result);
        return true;
    }

    /// <summary>停止某个会话发起的全部后台任务（会话关闭时调用）。</summary>
    public async Task StopAllAsync(string? ownerSessionId)
    {
        foreach (var task in _tasks.Values.Where(t => t.IsRunning && t.Owner == ownerSessionId).ToArray())
        {
            try
            {
                task.StopRequested = true;
                await task.StopAsync();
            }
            catch (Exception)
            {
                // 关闭会话时尽力而为。
            }
        }
    }

    /// <summary>交给模型的通知正文（Claude Code 的 task-notification 形态）。</summary>
    public static string FormatNotification(BackgroundTask task)
    {
        var sb = new StringBuilder("<task-notification>\n");
        sb.Append("<task-id>").Append(task.Id).Append("</task-id>\n");
        sb.Append("<type>").Append(task.Type).Append("</type>\n");
        sb.Append("<status>").Append(task.Status).Append("</status>\n");
        sb.Append("<description>").Append(task.Description).Append("</description>\n");
        if (task.OutputPath is { } output)
        {
            sb.Append("<output-file>").Append(output).Append("</output-file>\n");
        }
        if (task.Result is { Length: > 0 } result)
        {
            sb.Append("<result>\n").Append(result.Trim()).Append("\n</result>\n");
        }
        sb.Append("</task-notification>\n");
        sb.Append("This is an automated notification from the task system, not a message from the user.");
        return sb.ToString();
    }
}
