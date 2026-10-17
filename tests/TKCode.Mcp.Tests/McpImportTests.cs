using System.Text.Json.Nodes;
using TKCode.Config;
using Xunit;

namespace TKCode.Mcp.Tests;

/// <summary>导入各客户端的 MCP 配置（docs/design/mcp.md §12）与导出。</summary>
public class McpImportTests
{
    private static McpImportCandidate Single(McpImportResult result, string key) => result.Servers.Single(s => s.Key == key);

    [Fact]
    public void ClaudeDesktopAndCursor_McpServersWrapper()
    {
        var result = McpImport.Parse("""
            {
              "mcpServers": {
                "filesystem": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "/Users/me/Desktop"] },
                "github": { "url": "https://api.githubcopilot.com/mcp/", "headers": { "Authorization": "Bearer ${env:GITHUB_TOKEN}" } },
                "local-db": { "command": "uvx", "args": ["mcp-db"], "env": { "PORT": 5432, "DEBUG": true, "SKIP": null }, "envFile": "${workspaceFolder}/.env" }
              }
            }
            """);
        Assert.Null(result.Error);
        Assert.Equal("mcpServers", result.Format);
        Assert.Equal(McpTransportKind.Stdio, Single(result, "filesystem").Parsed.Transport);
        Assert.Equal(McpImportNoteKind.RunsCommand, Single(result, "filesystem").Notes[0].Kind);
        var github = Single(result, "github").Parsed;
        Assert.Equal(McpTransportKind.Http, github.Transport);
        Assert.Equal("Bearer ${env:GITHUB_TOKEN}", github.Headers["Authorization"]);
        var db = Single(result, "local-db").Parsed;
        Assert.Equal("5432", db.Env["PORT"]);
        Assert.Equal("true", db.Env["DEBUG"]);
        Assert.False(db.Env.ContainsKey("SKIP"));
        Assert.Equal("${workspaceFolder}/.env", db.EnvFile);
        Assert.Null(db.Error);
    }

    [Fact]
    public void BareEntriesAndSingleServer()
    {
        var bare = McpImport.Parse("""
            "context7": { "command": "npx", "args": ["-y", "@upstash/context7-mcp"] },
            """);
        Assert.Equal("context7", Assert.Single(bare.Servers).Key);

        var single = McpImport.Parse("""{ "type": "http", "url": "https://mcp.notion.com/mcp" }""", singleName: "Notion MCP");
        Assert.Equal("single", single.Format);
        Assert.Equal("Notion-MCP", Assert.Single(single.Servers).Key);

        var map = McpImport.Parse("""{ "a": { "command": "x" }, "b": { "url": "https://b" } }""");
        Assert.Equal(2, map.Servers.Count);
    }

    [Fact]
    public void ClaudeCode_TypesOauthAndProjectScope()
    {
        var result = McpImport.Parse("""
            {
              "numStartups": 3,
              "mcpServers": {
                "sentry": { "type": "http", "url": "https://mcp.sentry.dev/mcp", "oauth": { "clientId": "abc", "callbackPort": 8080, "authServerMetadataUrl": "https://x" } },
                "legacy": { "type": "sse", "url": "https://legacy.example.com/sse", "headersHelper": "/bin/get-token" },
                "socket": { "type": "ws", "url": "wss://x" }
              },
              "projects": {
                "/work/app": { "mcpServers": { "local-only": { "command": "./bin/server" } } },
                "/other": { "mcpServers": { "elsewhere": { "command": "x" } } }
              }
            }
            """, projectPath: "/work/app/");
        Assert.Equal("claude-code", result.Format);
        Assert.Equal(["sentry", "legacy", "socket", "local-only"], result.Servers.Select(s => s.Key));
        var sentry = Single(result, "sentry");
        Assert.Equal(8080, sentry.Parsed.OAuth?.CallbackPort);
        Assert.Equal("abc", sentry.Parsed.OAuth?.ClientId);
        Assert.Contains("oauth.authServerMetadataUrl", sentry.Dropped);
        Assert.Equal(McpTransportKind.Sse, Single(result, "legacy").Parsed.Transport);
        Assert.Contains(Single(result, "legacy").Notes, n => n.Kind == McpImportNoteKind.HeadersHelper);
        Assert.NotNull(Single(result, "socket").Error);
    }

    [Fact]
    public void VsCode_ServersAndInputs()
    {
        var result = McpImport.Parse("""
            {
              // VS Code 用户配置
              "inputs": [
                { "type": "promptString", "id": "perplexity-key", "description": "Perplexity API Key", "password": true }
              ],
              "servers": {
                "perplexity": {
                  "type": "stdio",
                  "command": "npx",
                  "args": ["-y", "server-perplexity-ask"],
                  "env": { "PERPLEXITY_API_KEY": "${input:perplexity-key}" },
                  "dev": { "watch": "src/**/*.ts" }
                },
                "fetch": { "type": "http", "url": "https://example.com/mcp", "sandbox": {} }
              }
            }
            """);
        Assert.Equal("vscode", result.Format);
        var perplexity = Single(result, "perplexity");
        var input = Assert.Single(perplexity.Inputs);
        Assert.Equal("perplexity-key", input.Id);
        Assert.True(input.Password);
        Assert.Contains("dev", perplexity.Dropped);
        var resolved = perplexity.Resolve(new Dictionary<string, string> { ["perplexity-key"] = "{env:PPLX}" }, false);
        Assert.Equal("{env:PPLX}", resolved["env"]!["PERPLEXITY_API_KEY"]!.ToString());
        Assert.Contains("sandbox", Single(result, "fetch").Dropped);
    }

    [Fact]
    public void Opencode_CommandArraysAndEnvironment()
    {
        var result = McpImport.Parse("""
            {
              "$schema": "https://opencode.ai/config.json",
              "mcp": {
                "everything": { "type": "local", "command": ["npx", "-y", "@modelcontextprotocol/server-everything"], "environment": { "A": "1" }, "enabled": false },
                "jira": { "type": "remote", "url": "https://jira.example.com/mcp", "headers": { "Authorization": "Bearer {env:JIRA}" } }
              }
            }
            """);
        Assert.Equal("opencode", result.Format);
        var everything = Single(result, "everything").Parsed;
        Assert.Equal("npx", everything.Command);
        Assert.Equal(["-y", "@modelcontextprotocol/server-everything"], everything.Args);
        Assert.Equal("1", everything.Env["A"]);
        Assert.False(everything.Enabled);
        Assert.Equal(McpTransportKind.Http, Single(result, "jira").Parsed.Transport);
    }

    [Fact]
    public void Zed_OldAndNewCommandShapes()
    {
        var result = McpImport.Parse("""
            {
              "theme": "One Dark",
              "context_servers": {
                "old": { "source": "custom", "command": { "path": "/usr/local/bin/mcp", "args": ["--stdio"], "env": { "X": "1" } } },
                "new": { "command": "uvx", "args": ["mcp-server-git"] },
                "remote": { "url": "https://zed.example.com/mcp" }
              }
            }
            """);
        Assert.Equal("zed", result.Format);
        var old = Single(result, "old").Parsed;
        Assert.Equal("/usr/local/bin/mcp", old.Command);
        Assert.Equal(["--stdio"], old.Args);
        Assert.Equal("1", old.Env["X"]);
        Assert.Empty(Single(result, "old").Dropped);
        Assert.Equal(McpTransportKind.Http, Single(result, "remote").Parsed.Transport);
    }

    [Fact]
    public void WindsurfGeminiCline_FieldVariants()
    {
        var result = McpImport.Parse("""
            {
              "mcpServers": {
                "ws": { "serverUrl": "https://windsurf.example.com/mcp", "disabledTools": ["delete"] },
                "gem": { "httpUrl": "https://gemini.example.com/mcp", "timeout": 30000, "trust": true, "excludeTools": ["rm"], "includeTools": ["ls"] },
                "cline": { "command": "node", "args": ["server.js"], "disabled": true, "autoApprove": ["read_file"], "timeout": 120, "transportType": "stdio" }
              }
            }
            """);
        var ws = Single(result, "ws").Parsed;
        Assert.Equal("https://windsurf.example.com/mcp", ws.Url);
        Assert.Equal(["delete"], ws.DisabledTools);
        var gem = Single(result, "gem");
        Assert.Equal(TimeSpan.FromSeconds(30), gem.Parsed.ToolTimeout);
        Assert.Equal(["rm"], gem.Parsed.DisabledTools);
        Assert.Contains(gem.Notes, n => n.Kind == McpImportNoteKind.Trust);
        Assert.Contains(gem.Notes, n => n.Kind == McpImportNoteKind.AllowList);
        var cline = Single(result, "cline");
        Assert.False(cline.Parsed.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(120), cline.Parsed.ToolTimeout);
        Assert.Equal(["read_file"], cline.AutoApprove);
        // 自动批准默认不导入，需要用户确认
        Assert.Empty(cline.Parsed.AlwaysAllow);
        Assert.Equal(["read_file"], McpServerConfig.ParseServers(new JsonObject { ["c"] = cline.Resolve(new Dictionary<string, string>(), true) },
            McpConfigSource.Global).Single().AlwaysAllow);
    }

    [Fact]
    public void Codex_Toml()
    {
        var result = McpImport.Parse("""
            model = "gpt-6"
            # MCP servers
            [mcp_servers.docs]
            command = "npx"
            args = ["-y", "mcp-docs"]  # inline comment
            env = { "API_KEY" = "abc", MODE = 'prod' }
            startup_timeout_sec = 20
            tool_timeout_sec = 90.5
            enabled_tools = ["search"]

            [mcp_servers.linear]
            url = "https://mcp.linear.app/mcp"
            bearer_token_env_var = "LINEAR_TOKEN"
            enabled = false

            [mcp_servers.linear.env_http_headers]
            "X-Team" = "LINEAR_TEAM"

            [mcp_servers."quoted name"]
            command = '''C:\tools\server.exe'''
            args = [
              "--a",
              "--b",
            ]
            disabled_tools = ["danger"]
            """);
        Assert.Null(result.Error);
        Assert.Equal("codex", result.Format);
        var docs = Single(result, "docs").Parsed;
        Assert.Equal(["-y", "mcp-docs"], docs.Args);
        Assert.Equal("prod", docs.Env["MODE"]);
        Assert.Equal(TimeSpan.FromSeconds(20), docs.StartupTimeout);
        Assert.Equal(TimeSpan.FromSeconds(91), docs.ToolTimeout);
        Assert.Contains(Single(result, "docs").Notes, n => n.Kind == McpImportNoteKind.AllowList);
        var linear = Single(result, "linear").Parsed;
        Assert.Equal("Bearer {env:LINEAR_TOKEN}", linear.Headers["Authorization"]);
        Assert.Equal("{env:LINEAR_TEAM}", linear.Headers["X-Team"]);
        Assert.False(linear.Enabled);
        var quoted = Single(result, "quoted-name").Parsed;
        Assert.Equal(@"C:\tools\server.exe", quoted.Command);
        Assert.Equal(["--a", "--b"], quoted.Args);
        Assert.Equal(["danger"], quoted.DisabledTools);
    }

    [Fact]
    public void InvalidInput_ReportsError()
    {
        Assert.NotNull(McpImport.Parse("not json at all").Error);
        Assert.NotNull(McpImport.Parse("""{ "theme": "dark" }""").Error);
        Assert.NotNull(McpImport.Parse("[mcp_servers.x\ncommand = 1").Error);
        Assert.NotNull(McpImport.Parse("").Error);
    }

    [Fact]
    public void Export_UsesDollarVariablesAndRoundTrips()
    {
        var servers = McpServerConfig.ParseServers((JsonObject)JsonNode.Parse("""
            {
              "fs": { "command": "npx", "args": ["{workspaceFolder}"], "env": { "TOKEN": "{env:FS_TOKEN}", "B": "$OTHER" } },
              "gh": { "url": "https://x/mcp", "headers": { "Authorization": "Bearer {env:GH}" }, "enabled": false },
              "old": { "type": "sse", "url": "https://y/sse" }
            }
            """)!, McpConfigSource.Global);
        var json = McpImport.Export(servers);
        Assert.Contains("\"${FS_TOKEN}\"", json);
        Assert.Contains("\"${OTHER}\"", json);
        Assert.Contains("Bearer ${GH}", json);
        Assert.Contains("\"${workspaceFolder}\"", json);
        Assert.Contains("\"type\": \"sse\"", json);
        var back = McpImport.Parse(json);
        Assert.Equal(3, back.Servers.Count);
        Assert.False(back.Servers.Single(s => s.Key == "gh").Parsed.Enabled);
        Assert.Equal(McpTransportKind.Sse, back.Servers.Single(s => s.Key == "old").Parsed.Transport);

        var expanded = McpImport.Export(servers.Where(s => s.Key == "fs"), expand: true, cwd: "/ws");
        Assert.Contains("\"/ws\"", expanded);
    }

    [Fact]
    public void Discover_FindsKnownFiles()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-mcp-discover-").FullName;
        Directory.CreateDirectory(Path.Combine(home, ".cursor"));
        File.WriteAllText(Path.Combine(home, ".cursor", "mcp.json"), """{ "mcpServers": { "c": { "command": "x" } } }""");
        Directory.CreateDirectory(Path.Combine(home, ".codex"));
        File.WriteAllText(Path.Combine(home, ".codex", "config.toml"), "[mcp_servers.k]\ncommand = \"y\"\n");
        File.WriteAllText(Path.Combine(home, ".claude.json"), """{ "projects": {} }""");
        var project = Directory.CreateTempSubdirectory("tkcode-mcp-discover-proj-").FullName;
        Directory.CreateDirectory(Path.Combine(project, ".vscode"));
        File.WriteAllText(Path.Combine(project, ".vscode", "mcp.json"), """{ "servers": { "v": { "url": "https://v" } } }""");

        var previous = Environment.GetEnvironmentVariable("CODEX_HOME");
        Environment.SetEnvironmentVariable("CODEX_HOME", null);
        try
        {
            var sources = McpImport.Discover(project, home);
            Assert.Contains(sources, s => s.Client == "Cursor" && s.Result.Servers.Single().Key == "c");
            Assert.Contains(sources, s => s.Client == "Codex" && s.Result.Servers.Single().Key == "k");
            Assert.Contains(sources, s => s.Client == "VS Code (project)");
            Assert.DoesNotContain(sources, s => s.Client == "Claude Code");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previous);
        }
    }
}
