using TCode.Agent.Coding.Tools.Agents;
using TCode.Agent.Coding.Tools.Files;
using TCode.Agent.Coding.Tools.Search;
using TCode.Agent.Coding.Tools.Shell;
using TCode.Agent.Coding.Tools.Tasks;
using TCode.Agent.Coding.Tools.Web;

namespace TCode.Agent.Coding.Tools;

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
        return tools;
    }
}
