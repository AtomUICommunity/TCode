using System.IO.Pipes;
using System.Text.Json;
using TKCode.Runtimes.Acp;
using TKCode.Runtimes.Claude;
using TKCode.Runtimes.Codex;
using Xunit;

namespace TKCode.Runtimes.Tests;

/// <summary>TKCode MCP 桥接注入三种外部运行时的写法（docs/design/mcp.md §10）。</summary>
public class McpBridgeInjectionTests
{
    private static readonly McpBridgeLaunch Bridge = new("/opt/tkcode/TKCode.AgentHost", ["--mcp-bridge"],
        new Dictionary<string, string> { ["TKCODE_MCP_BRIDGE_SOCKET"] = "/tmp/s.sock", ["TKCODE_MCP_BRIDGE_TOKEN"] = "secret" });

    [Fact]
    public void Claude_McpConfigFileIsPrivate()
    {
        var path = ClaudeRuntime.WriteMcpConfig(Bridge);
        try
        {
            var server = JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("mcpServers").GetProperty("tkcode");
            Assert.Equal("/opt/tkcode/TKCode.AgentHost", server.GetProperty("command").GetString());
            Assert.Equal("--mcp-bridge", server.GetProperty("args")[0].GetString());
            Assert.Equal("secret", server.GetProperty("env").GetProperty("TKCODE_MCP_BRIDGE_TOKEN").GetString());
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Codex_ThreadConfigOverridesMcpServers()
    {
        await using var session = new CodexSession(new MemoryStream(), new MemoryStream(), new NullHost(), null) { McpBridge = Bridge };
        var thread = session.ThreadParams("/work", new CodexThreadSettings(null, "on-request", false, false, null), null);
        var server = thread.GetProperty("config").GetProperty("mcp_servers.tkcode");
        Assert.Equal("/opt/tkcode/TKCode.AgentHost", server.GetProperty("command").GetString());
        Assert.Equal("secret", server.GetProperty("env").GetProperty("TKCODE_MCP_BRIDGE_TOKEN").GetString());
        Assert.Equal("auto", thread.GetProperty("config").GetProperty("model_reasoning_summary").GetString());
    }

    [Fact]
    public async Task Acp_SessionNewAddsStdioServer()
    {
        var agent = new AcpAgentDefinition("example", "Example", "example", ["acp"]);
        await using var session = new AcpSession(new MemoryStream(), new MemoryStream(), agent, new NullHost()) { McpBridge = Bridge };
        var server = Assert.Single(session.SessionParams(null, "/work").GetProperty("mcpServers").EnumerateArray());
        Assert.Equal("tkcode", server.GetProperty("name").GetString());
        Assert.Equal("/opt/tkcode/TKCode.AgentHost", server.GetProperty("command").GetString());
        var env = server.GetProperty("env").EnumerateArray().ToDictionary(e => e.GetProperty("name").GetString()!, e => e.GetProperty("value").GetString());
        Assert.Equal("secret", env["TKCODE_MCP_BRIDGE_TOKEN"]);

        await using var plain = new AcpSession(new MemoryStream(), new MemoryStream(), agent, new NullHost());
        Assert.Empty(plain.SessionParams(null, "/work").GetProperty("mcpServers").EnumerateArray());
    }

    private sealed class NullHost : IRuntimeHost
    {
        public ValueTask<ApprovalDecision> RequestApprovalAsync(RuntimeApprovalRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ApprovalDecision.Deny("test"));

        public void OnNativeSessionId(string nativeSessionId)
        {
        }
    }
}
