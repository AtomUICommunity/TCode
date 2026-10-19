using TKCode.Agent.Coding.Tools.Agents;
using TKCode.Agent.Coding.Tools.Files;
using TKCode.Agent.Coding.Tools.Plan;
using TKCode.Agent.Coding.Tools.Questions;
using TKCode.Agent.Coding.Tools.Search;
using TKCode.Agent.Coding.Tools.Shell;
using TKCode.Agent.Coding.Tools.Tasks;
using TKCode.Agent.Coding.Tools.Web;

namespace TKCode.Agent.Coding.Tools;

/// <summary>按工具集与宿主提供的能力创建会话的内置工具。</summary>
public static class BuiltinTools
{
    public static IReadOnlyList<IAgentTool> Create(ToolContext context, ToolSet set)
    {
        var readOnly = set == ToolSet.ReadOnly;
        var tools = new List<IAgentTool> { new ReadTool(context) };
        if (!readOnly)
        {
            tools.Add(new WriteTool(context));
            tools.Add(new EditTool(context));
        }
        tools.AddRange([new LsTool(context), new GlobTool(context), new GrepTool(context), new BashTool(context), new TaskStopTool(context)]);
        tools.AddRange([new TaskCreateTool(context), new TaskGetTool(context), new TaskListTool(context), new TaskUpdateTool(context)]);
        if (context.Services.WebFetcher is not null)
        {
            tools.Add(new WebFetchTool(context));
        }
        if (context.Services.WebSearch is not null)
        {
            tools.Add(new WebSearchTool(context));
        }
        if (context.Services.Agents is not null)
        {
            if (set == ToolSet.Full)
            {
                tools.Add(new AgentTool(context));
            }
            tools.Add(new SendMessageTool(context));
            tools.Add(new ListAgentsTool(context));
        }
        if (context.Services.UserInput is not null)
        {
            tools.Add(new AskUserTool(context));
        }
        // 模式专属：exit_plan_mode 只在 Plan、enter_plan_mode 只在 Agent 模式下出现（SessionRole.Allows）
        if (set == ToolSet.Full && context.Services.PlanMode is not null && context.PlanFile is not null)
        {
            tools.Add(new ExitPlanModeTool(context));
            tools.Add(new EnterPlanModeTool(context));
        }
        return tools;
    }
}
