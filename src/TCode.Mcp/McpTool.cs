using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using TCode.Agent;
using TCode.Agent.Coding.Tools;
using TCode.Ai.Messages;
using ContentBlock = TCode.Ai.Messages.ContentBlock;
using TextContent = TCode.Ai.Messages.TextContent;
using ImageContent = TCode.Ai.Messages.ImageContent;

namespace TCode.Mcp;

/// <summary>把一个 MCP 工具适配为内置 Agent 的工具（docs/design/mcp.md §5.2）。</summary>
public sealed class McpTool : IAgentTool
{
    private const int MaxDescriptionLength = 2048;

    private readonly McpConnection _connection;
    private readonly ToolContext _context;

    public McpTool(McpConnection connection, Tool tool, string modelName, ToolContext context)
    {
        _connection = connection;
        _context = context;
        Protocol = tool;
        Name = modelName;
        Description = BuildDescription(tool);
        ParametersSchemaJson = NormalizeSchema(tool.InputSchema);
    }

    /// <summary>服务器给出的原始定义。</summary>
    public Tool Protocol { get; }
    public McpConnection Connection => _connection;
    public string ServerKey => _connection.Key;

    public string Name { get; }
    public string Label => "Tool.Mcp";
    public string Description { get; }
    public string ParametersSchemaJson { get; }

    /// <summary>模型可见定义的指纹：变化时需要替换工具。</summary>
    public string Signature => $"{Name}\n{Description}\n{ParametersSchemaJson}";

    public ToolPermission DescribePermission(JsonElement arguments)
    {
        if (_connection.Config.AllowsAlways(Protocol.Name))
        {
            return ToolPermission.NoAccess;
        }
        var title = $"{_connection.Config.Title} · {Protocol.Title ?? Protocol.Annotations?.Title ?? Protocol.Name}";
        return new ToolPermission(ToolAccess.External, Command: Protocol.Name, Domain: _connection.Key, Title: title);
    }

    public async Task<AgentToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (invocation.Arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in invocation.Arguments.EnumerateObject())
            {
                arguments[property.Name] = property.Value.Clone();
            }
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_connection.Config.ToolTimeout);
        var progress = invocation.Progress is { } report
            ? new ProgressRelay(value => report.Report(AgentToolResult.Text(FormatProgress(value))))
            : null;
        McpCallContext.SessionId = _context.SessionId;
        CallToolResult result;
        try
        {
            result = await _connection.CallToolAsync(Protocol.Name, arguments, progress, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AgentToolResult.Error($"MCP tool '{Protocol.Name}' timed out after {_connection.Config.ToolTimeout.TotalSeconds:0}s.");
        }
        catch (McpProtocolException ex)
        {
            return AgentToolResult.Error($"MCP error {(int)ex.ErrorCode}: {ex.Message}");
        }
        catch (Exception ex) when (ex is McpException or McpUnavailableException or IOException or HttpRequestException)
        {
            return AgentToolResult.Error(ex.Message);
        }
        return McpResultConverter.Convert(result, _connection.Key, Protocol.Name, Protocol.Title, _context.ModelSupportsImages(),
            _context.TempDirectory, invocation.ToolCallId);
    }

    private static string FormatProgress(ProgressNotificationValue value)
    {
        var amount = value.Total is { } total ? $"{value.Progress}/{total}" : $"{value.Progress}";
        return value.Message is { Length: > 0 } message ? $"{message} ({amount})" : amount;
    }

    private static string BuildDescription(Tool tool)
    {
        var description = tool.Description?.Trim() ?? "";
        if (tool.Title is { Length: > 0 } title && !title.Equals(tool.Name, StringComparison.Ordinal))
        {
            description = description.Length == 0 ? title : $"{title} — {description}";
        }
        if (description.Length > MaxDescriptionLength)
        {
            description = description[..MaxDescriptionLength] + "…";
        }
        return description.Length == 0 ? tool.Name : description;
    }

    /// <summary>保底处理：根节点必须是 type:object；去掉根节点的 $schema（部分厂商会拒绝）。其余原样传递，保持非 strict。</summary>
    internal static string NormalizeSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return """{"type":"object","properties":{}}""";
        }
        var node = JsonNode.Parse(schema.GetRawText()) as JsonObject ?? [];
        node.Remove("$schema");
        if (node["type"] is null)
        {
            node["type"] = "object";
        }
        if (node["type"] is JsonValue type && type.ToString() == "object" && node["properties"] is null)
        {
            node["properties"] = new JsonObject();
        }
        return node.ToJsonString();
    }

    private sealed class ProgressRelay(Action<ProgressNotificationValue> report) : IProgress<ProgressNotificationValue>
    {
        public void Report(ProgressNotificationValue value) => report(value);
    }
}
