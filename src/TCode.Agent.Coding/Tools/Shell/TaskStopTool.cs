using System.Text.Json;

namespace TCode.Agent.Coding.Tools.Shell;

/// <summary>停止后台任务（后台命令或后台子 Agent）。</summary>
public sealed class TaskStopTool(ToolContext context) : IAgentTool
{
    public string Name => "task_stop";
    public string Label => "Tool.TaskStop";

    public string Description => "Stop a running background task (a background bash command or background agent) by its task id.";

    public string ParametersSchemaJson => """
        {"type":"object","properties":{"task_id":{"type":"string","description":"The id of the background task to stop, e.g. b1 or a2."}},
         "required":["task_id"],"additionalProperties":false}
        """;

    public string PromptSnippet => "Stop a background task";

    public ToolPermission DescribePermission(JsonElement arguments) => ToolPermission.NoAccess;

    public async Task<AgentToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var id = invocation.Arguments.Required("task_id");
        var task = context.BackgroundTasks.Find(id);
        if (task is null)
        {
            var known = context.BackgroundTasks.All;
            return AgentToolResult.Error(known.Count == 0
                ? $"No background task with id '{id}'. There are no background tasks."
                : $"No background task with id '{id}'. Known tasks: {string.Join(", ", known.Select(t => $"{t.Id} ({t.Status}: {t.Description})"))}.");
        }
        if (!task.IsRunning)
        {
            return AgentToolResult.Text($"Task {id} is already {task.Status}.");
        }
        await context.BackgroundTasks.StopAsync(id);
        return AgentToolResult.Text($"Stopped task {id} ({task.Description}).") with
        {
            Details = new BackgroundTaskDetails(task.Id, task.Type, task.Description, "stopped", task.OutputPath).ToJson(),
        };
    }
}
