using System.Text;
using System.Text.Json;

namespace TCode.Agent.Coding.Tools.Tasks;

/// <summary>任务清单工具的公共部分：结果附带全量清单（Details）供 UI 显示。</summary>
internal static class TaskToolSupport
{
    public static readonly IReadOnlyList<string> Guidelines =
    [
        "Use the task tools (task_create, task_update, task_list) to plan and track multi-step work; skip them for simple tasks that take only a few steps.",
        "Mark a task in_progress when you start it and completed as soon as it is done; keep only one task in_progress at a time.",
    ];

    public static AgentToolResult WithList(string text, TaskList list) =>
        AgentToolResult.Text(text) with { Details = new TaskListDetails(list.Items).ToJson() };

    public static string Describe(TaskItem item)
    {
        var sb = new StringBuilder($"#{item.Id} [{item.Status}] {item.Subject}");
        if (item.Owner is { } owner)
        {
            sb.Append($" (owner: {owner})");
        }
        if (item.BlockedBy.Count > 0)
        {
            sb.Append($" (blocked by: {string.Join(", ", item.BlockedBy.Select(b => "#" + b))})");
        }
        return sb.ToString();
    }
}

public sealed class TaskCreateTool(ToolContext context) : IAgentTool
{
    public string Name => "task_create";
    public string Label => "Tool.TaskCreate";

    public string Description =>
        "Create a task in the session's task list to plan and track multi-step work. New tasks start as pending. " +
        "Use subject for a short imperative title and description for details and acceptance criteria.";

    public string ParametersSchemaJson => """
        {"type":"object","properties":{
          "subject":{"type":"string","description":"A brief title in imperative form, e.g. 'Fix authentication bug'."},
          "description":{"type":"string","description":"What needs to be done, with enough detail to complete it."},
          "activeForm":{"type":"string","description":"Present continuous form shown while in progress, e.g. 'Fixing authentication bug'."}},
         "required":["subject","description"],"additionalProperties":false}
        """;

    public string PromptSnippet => "Create tasks to plan and track multi-step work";
    public IReadOnlyList<string> PromptGuidelines => TaskToolSupport.Guidelines;
    public ToolPermission DescribePermission(JsonElement arguments) => ToolPermission.NoAccess;

    public Task<AgentToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var args = invocation.Arguments;
        var item = context.Tasks.Create(args.Required("subject"), args.String("description") ?? "", args.String("activeForm"));
        return Task.FromResult(TaskToolSupport.WithList($"Created task #{item.Id}: {item.Subject}", context.Tasks));
    }
}

public sealed class TaskGetTool(ToolContext context) : IAgentTool
{
    public string Name => "task_get";
    public string Label => "Tool.TaskGet";
    public string Description => "Get the full details of a task by id, including its description and dependencies.";

    public string ParametersSchemaJson => """
        {"type":"object","properties":{"task_id":{"type":"string","description":"The task id."}},"required":["task_id"],"additionalProperties":false}
        """;

    public ToolPermission DescribePermission(JsonElement arguments) => ToolPermission.NoAccess;

    public Task<AgentToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var id = invocation.Arguments.Required("task_id").TrimStart('#');
        if (context.Tasks.Get(id) is not { } item)
        {
            return Task.FromResult(AgentToolResult.Error($"Task #{id} not found."));
        }
        var sb = new StringBuilder(TaskToolSupport.Describe(item)).Append('\n');
        if (item.Description.Length > 0)
        {
            sb.Append('\n').Append(item.Description).Append('\n');
        }
        if (item.Blocks.Count > 0)
        {
            sb.Append($"\nBlocks: {string.Join(", ", item.Blocks.Select(b => "#" + b))}\n");
        }
        return Task.FromResult(AgentToolResult.Text(sb.ToString()));
    }
}

public sealed class TaskListTool(ToolContext context) : IAgentTool
{
    public string Name => "task_list";
    public string Label => "Tool.TaskList";
    public string Description => "List all tasks in the session's task list with their status, owner and blockers.";
    public string ParametersSchemaJson => """{"type":"object","properties":{},"additionalProperties":false}""";
    public ToolPermission DescribePermission(JsonElement arguments) => ToolPermission.NoAccess;

    public Task<AgentToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var items = context.Tasks.Items;
        var text = items.Count == 0 ? "No tasks." : string.Join('\n', items.Select(TaskToolSupport.Describe));
        return Task.FromResult(TaskToolSupport.WithList(text, context.Tasks));
    }
}

public sealed class TaskUpdateTool(ToolContext context) : IAgentTool
{
    public string Name => "task_update";
    public string Label => "Tool.TaskUpdate";

    public string Description =>
        "Update a task: change its status (pending, in_progress, completed, or deleted to remove it), subject, description, " +
        "active form or owner, or add dependencies with addBlockedBy / addBlocks (task ids).";

    public string ParametersSchemaJson => """
        {"type":"object","properties":{
          "task_id":{"type":"string","description":"The task id."},
          "status":{"type":"string","enum":["pending","in_progress","completed","deleted"]},
          "subject":{"type":"string"},
          "description":{"type":"string"},
          "activeForm":{"type":"string"},
          "owner":{"type":"string","description":"Agent name that owns the task."},
          "addBlockedBy":{"type":"array","items":{"type":"string"},"description":"Task ids that must be completed before this one."},
          "addBlocks":{"type":"array","items":{"type":"string"},"description":"Task ids that cannot start until this one is completed."}},
         "required":["task_id"],"additionalProperties":false}
        """;

    public ToolPermission DescribePermission(JsonElement arguments) => ToolPermission.NoAccess;

    public Task<AgentToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var args = invocation.Arguments;
        var id = args.Required("task_id").TrimStart('#');
        var status = args.String("status");
        if (status is not null && status != "deleted" && !TaskList.Statuses.Contains(status))
        {
            return Task.FromResult(AgentToolResult.Error("status must be one of: pending, in_progress, completed, deleted."));
        }
        if (status == "deleted")
        {
            return Task.FromResult(context.Tasks.Update(id, t => t, delete: true) is null
                ? AgentToolResult.Error($"Task #{id} not found.")
                : TaskToolSupport.WithList($"Deleted task #{id}.", context.Tasks));
        }

        foreach (var blocker in args.StringArray("addBlockedBy") ?? [])
        {
            if (!context.Tasks.Link(blocker.TrimStart('#'), id))
            {
                return Task.FromResult(AgentToolResult.Error($"Cannot add dependency: task #{blocker} or #{id} not found."));
            }
        }
        foreach (var blocked in args.StringArray("addBlocks") ?? [])
        {
            if (!context.Tasks.Link(id, blocked.TrimStart('#')))
            {
                return Task.FromResult(AgentToolResult.Error($"Cannot add dependency: task #{id} or #{blocked} not found."));
            }
        }

        var updated = context.Tasks.Update(id, t => t with
        {
            Status = status ?? t.Status,
            Subject = args.String("subject") ?? t.Subject,
            Description = args.String("description") ?? t.Description,
            ActiveForm = args.String("activeForm") ?? t.ActiveForm,
            Owner = args.String("owner") ?? t.Owner,
        });
        if (updated is null)
        {
            return Task.FromResult(AgentToolResult.Error($"Task #{id} not found."));
        }
        var note = updated.Status == "in_progress" && updated.BlockedBy.Any(b => context.Tasks.Get(b)?.Status != "completed")
            ? " Note: it is still blocked by unfinished tasks."
            : "";
        return Task.FromResult(TaskToolSupport.WithList($"Updated task #{id}: {TaskToolSupport.Describe(updated)}.{note}", context.Tasks));
    }
}
