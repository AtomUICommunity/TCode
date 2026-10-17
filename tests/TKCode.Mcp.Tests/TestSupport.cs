using System.Runtime.CompilerServices;
using System.Text.Json;
using TKCode.Agent;
using TKCode.Agent.Coding.Tools;
using TKCode.Agent.Coding.Workspace;
using TKCode.Ai.Messages;
using TKCode.Config;

namespace TKCode.Mcp.Tests;

internal static class TestSupport
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ConfigPaths.HomeVariable)))
        {
            Environment.SetEnvironmentVariable(ConfigPaths.HomeVariable, Directory.CreateTempSubdirectory("tkcode-mcp-test-home-").FullName);
        }
    }

    /// <summary>tests/TKCode.Mcp.TestServer 的构建产物（与测试同一配置）。</summary>
    public static string ServerDll
    {
        get
        {
            var baseDir = AppContext.BaseDirectory;
            var configuration = baseDir.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
            for (var dir = new DirectoryInfo(baseDir); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "tests", "TKCode.Mcp.TestServer", "bin", configuration, "net10.0", "TKCode.Mcp.TestServer.dll");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            throw new FileNotFoundException("TKCode.Mcp.TestServer.dll not found; build tests/TKCode.Mcp.TestServer first.");
        }
    }

    public static McpServerConfig StdioServer(string key = "test", Dictionary<string, string>? env = null, IReadOnlyList<string>? disabled = null,
        IReadOnlyList<string>? alwaysAllow = null) =>
        McpServerConfig.From(key, new McpServerSettings
        {
            Command = "dotnet",
            Args = [ServerDll],
            Env = env,
            DisabledTools = disabled?.ToList(),
            AlwaysAllow = alwaysAllow?.ToList(),
            StartupTimeout = 20,
        }, McpConfigSource.Global);

    public static ToolContext Context(string? cwd = null) =>
        new(new WorkspaceRoots(cwd ?? Directory.CreateTempSubdirectory("tkcode-mcp-ws-").FullName), "s" + Guid.NewGuid().ToString("N")[..8]);

    public static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static string Text(AgentToolResult result) => string.Join("\n", result.Content.OfType<TextContent>().Select(t => t.Text));
}
