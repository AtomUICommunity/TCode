using TCode.Agent.Coding.Prompt;

namespace TCode.Agent.Coding.Tools;

/// <summary>
/// 会话中可能变化的工具来源（MCP 服务器，docs/design/mcp.md §5.4）。
/// 只在每批用户输入之前调用 <see cref="RefreshAsync"/>：同一轮内工具集与系统提示不变。会话关闭时释放（释放连接租约）。
/// </summary>
public interface IDynamicToolSource : IAsyncDisposable
{
    /// <param name="firstTurn">会话的第一批输入：可以等待尚在连接的服务器（有上限）。</param>
    ValueTask<DynamicToolSet> RefreshAsync(bool firstTurn, CancellationToken cancellationToken);
}

/// <param name="Tools">追加在内置工具之后的工具。</param>
/// <param name="Sections">追加到系统提示的节（例如 mcp）；变化时以补丁消息告知模型。</param>
public sealed record DynamicToolSet(IReadOnlyList<IAgentTool> Tools, IReadOnlyList<PromptSection> Sections)
{
    public static DynamicToolSet Empty { get; } = new([], []);
}
