using System.Text.Json;
using TKCode.Ai;
using TKCode.Ai.Messages;

namespace TKCode.Agent;

/// <summary>
/// Agent 工具。Content 交给模型；Details（JSON）仅供 UI 渲染（例如 diff），会持久化但不会发给模型。
/// 工具可抛异常，循环会把异常转成错误结果交还模型。工具实例按会话创建，会话状态由构造参数注入。
/// </summary>
public interface IAgentTool
{
    string Name { get; }

    /// <summary>界面显示名的本地化 key，例如 <c>Tool.Read</c>。</summary>
    string Label { get; }
    string Description { get; }
    string ParametersSchemaJson { get; }

    /// <summary>写进系统提示 tools 节的一行说明；null 表示不列出。</summary>
    string? PromptSnippet => null;

    /// <summary>写进系统提示 rules 节的使用规范（跨工具去重）。</summary>
    IReadOnlyList<string> PromptGuidelines => [];

    /// <summary>同一批调用中只要有一个 Exclusive，整批改为串行。</summary>
    ToolConcurrency Concurrency => ToolConcurrency.Parallel;

    /// <summary>按参数描述本次调用需要的权限（供审批判定）；参数无法解析时也应返回保守的描述。</summary>
    ToolPermission DescribePermission(JsonElement arguments);

    Task<AgentToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken);
}

public enum ToolConcurrency
{
    Parallel,
    Exclusive,
}

public sealed record ToolInvocation(string ToolCallId, JsonElement Arguments, IProgress<AgentToolResult>? Progress = null);

/// <summary>权限类别：决定审批策略（见 docs/design/builtin-agent-tools.md §10）。</summary>
public enum ToolAccess
{
    /// <summary>不涉及外部资源（任务清单、子 Agent 协调等）。</summary>
    None,
    Read,
    Write,
    Execute,
    Network,
    /// <summary>外部工具（MCP 服务器）：Domain 为服务器名，Command 为工具名。</summary>
    External,
}

/// <param name="Paths">涉及的文件 / 目录（绝对路径）。</param>
/// <param name="Command">Execute：完整命令行。</param>
/// <param name="Domain">Network：目标主机；搜索为 null。</param>
/// <param name="Title">审批弹窗的一句话描述。</param>
public sealed record ToolPermission(ToolAccess Access, IReadOnlyList<string>? Paths = null, string? Command = null, string? Domain = null,
    string? Title = null)
{
    public static ToolPermission NoAccess { get; } = new(ToolAccess.None);
}

public sealed record AgentToolResult(IReadOnlyList<ContentBlock> Content)
{
    public bool IsError { get; init; }

    /// <summary>UI 专用的结构化数据（JSON 文本）。</summary>
    public string? Details { get; init; }

    public static AgentToolResult Text(string text) => new([new TextContent(text)]);
    public static AgentToolResult Error(string text) => new([new TextContent(text)]) { IsError = true };
}

public static class AgentToolExtensions
{
    public static ToolDefinition ToDefinition(this IAgentTool tool) => new(tool.Name, tool.Description, tool.ParametersSchemaJson);
}

/// <summary>执行前钩子的结果：Block 为 true 时以错误结果回给模型（例如用户拒绝审批）。</summary>
public sealed record ToolCallDecision(bool Block, string? Reason = null)
{
    public static ToolCallDecision Allow { get; } = new(false);
    public static ToolCallDecision Deny(string reason) => new(true, reason);
}

public sealed record BeforeToolCallContext(IAgentTool Tool, ToolCallContent ToolCall, JsonElement Arguments, ToolPermission Permission);
