using TKCode.Agent.Coding.Tools.Agents;
using TKCode.Agent.Coding.Tools.Questions;
using TKCode.Agent.Coding.Tools.Web;

namespace TKCode.Agent.Coding.Tools;

/// <summary>宿主注入给内置工具的外部能力。没有提供的能力，对应的工具不会出现在工具集中。</summary>
public sealed record ToolServices
{
    public static ToolServices None { get; } = new();

    /// <summary>web_fetch 使用的抓取器（null 表示禁用网页抓取）。</summary>
    public WebFetcher? WebFetcher { get; init; }

    /// <summary>web_search 的搜索后端（未配置时为 null）。</summary>
    public IWebSearchProvider? WebSearch { get; init; }

    /// <summary>子 Agent 与跨会话消息的协调器（null 表示不提供 agent / send_message / list_agents）。</summary>
    public IAgentCoordinator? Agents { get; init; }

    /// <summary>为会话创建动态工具来源（MCP；null 表示没有）。每个会话（含子 Agent）各创建一个，会话关闭时释放。</summary>
    public Func<ToolContext, IDynamicToolSource>? DynamicTools { get; init; }

    /// <summary>向用户提问（null 表示不提供 ask_user；子 Agent 总是 null）。</summary>
    public IUserInputService? UserInput { get; init; }
}
