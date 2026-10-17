using TCode.Agent.Coding;
using TCode.Agent.Coding.Tools;
using TCode.Ai;
using TCode.Ai.Messages;
using TCode.Config;
using Xunit;

namespace TCode.Mcp.Tests;

/// <summary>内置 Agent 会话 + MCP：工具随轮次进入模型上下文、mcp 节写进开头系统提示、工具调用往返。</summary>
public class McpSessionTests
{
    [Fact]
    public async Task BuiltinSessionCallsMcpToolAndPatchesPromptWhenServerAppears()
    {
        var cwd = Directory.CreateTempSubdirectory("tcode-mcp-session-").FullName;
        var configs = new List<McpServerConfig> { TestSupport.StdioServer() };
        await using var manager = new McpManager(new McpHost(), new McpTrustStore(Path.GetTempFileName()), () => configs);
        var api = new ScriptedWireApi(
            ScriptedWireApi.Turn.Tools(("mcp__test__echo", """{"text":"from model"}""")),
            ScriptedWireApi.Turn.Reply("first done"),
            ScriptedWireApi.Turn.Reply("second done"));
        var session = AgentSession.Create(new WireApiRegistry([api]), ScriptedWireApi.Model, cwd,
            Path.Combine(Path.GetTempPath(), "tcode-mcp-sessions-" + Guid.NewGuid().ToString("N")),
            options: new BuiltinOptions { Services = new ToolServices { DynamicTools = manager.CreateSource } });

        session.Prompt("use the echo tool");
        await session.Agent.WaitForIdleAsync();

        var first = api.Contexts[0];
        Assert.Contains(first.Tools, t => t.Name == "mcp__test__echo");
        // 新会话：mcp 节直接写进开头系统提示，而不是追加补丁
        Assert.Contains("<mcp>", first.SystemPrompt);
        Assert.DoesNotContain(first.Messages, m => m is SystemMessage);
        var toolResult = session.Agent.Messages.OfType<ToolResultMessage>().Single();
        Assert.Equal("echo: from model", string.Join("", toolResult.Content.OfType<TextContent>().Select(t => t.Text)));

        // 服务器被移除：下一批输入前工具消失，并追加一条系统提示补丁
        configs.Clear();
        session.Prompt("again");
        await session.Agent.WaitForIdleAsync();
        var third = api.Contexts[^1];
        Assert.DoesNotContain(third.Tools, t => t.Name.StartsWith("mcp__", StringComparison.Ordinal));
        Assert.Contains(third.Messages, m => m is SystemMessage s && s.Text.Contains("mcp", StringComparison.Ordinal));
        Assert.Equal(first.SystemPrompt, third.SystemPrompt);

        await session.DisposeAsync();
        Assert.All(manager.Connections, c => Assert.Equal(0, c.Leases));
    }
}
