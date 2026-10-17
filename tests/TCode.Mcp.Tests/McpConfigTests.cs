using System.Text.Json.Nodes;
using TCode.Config;
using Xunit;

namespace TCode.Mcp.Tests;

public class McpConfigTests
{
    [Fact]
    public void ParseServers_InfersTransportAndIsolatesErrors()
    {
        var servers = (JsonObject)JsonNode.Parse("""
            {
              "fs": { "command": "npx", "args": ["-y", "server"], "env": { "A": "1" } },
              "remote": { "url": "https://example.com/mcp", "headers": { "Authorization": "Bearer x" } },
              "legacy": { "type": "sse", "url": "https://example.com/sse" },
              "broken": { "type": "ws", "url": "wss://x" },
              "nourl": { "type": "http" },
              "bad name": { "command": "x" },
              "notobj": 3
            }
            """)!;
        var warnings = new List<string>();
        var list = McpServerConfig.ParseServers(servers, McpConfigSource.Global, warnings).ToDictionary(c => c.Key);

        Assert.Equal(McpTransportKind.Stdio, list["fs"].Transport);
        Assert.Equal(["-y", "server"], list["fs"].Args);
        Assert.Null(list["fs"].Error);
        Assert.Equal(McpTransportKind.Http, list["remote"].Transport);
        Assert.True(list["remote"].HasStaticAuthorization);
        Assert.Equal(McpTransportKind.Sse, list["legacy"].Transport);
        Assert.NotNull(list["broken"].Error);
        Assert.NotNull(list["nourl"].Error);
        Assert.NotNull(list["bad name"].Error);
        Assert.NotNull(list["notobj"].Error);
        Assert.Empty(warnings);
    }

    [Fact]
    public void ParseServers_WarnsUnknownFields()
    {
        var warnings = new List<string>();
        McpServerConfig.ParseServers((JsonObject)JsonNode.Parse("""{ "a": { "command": "x", "autoApprove": [] } }""")!, McpConfigSource.Global, warnings);
        Assert.Contains(warnings, w => w.Contains("mcpServers.a.autoApprove"));
    }

    [Fact]
    public void Settings_BadServerDoesNotInvalidateFile()
    {
        var result = SettingsFile.Parse("""{ "mcpServers": { "ok": { "command": "x" }, "bad": { "args": "notarray" } } }""");
        Assert.True(result.IsValid);
        var servers = McpServerConfig.ParseServers(result.Settings!.McpServers, McpConfigSource.Global);
        Assert.Null(servers.Single(s => s.Key == "ok").Error);
        Assert.NotNull(servers.Single(s => s.Key == "bad").Error);
    }

    [Fact]
    public void ToJson_RoundTrips()
    {
        var config = McpServerConfig.From("gh", new McpServerSettings
        {
            Type = "http", Url = "https://x/mcp", Headers = new() { ["Authorization"] = "Bearer {env:T}" }, AlwaysAllow = ["*"], ToolTimeout = 30,
            OAuth = new McpOAuthSettings { CallbackPort = 8080 },
        }, McpConfigSource.Global);
        var again = McpServerConfig.ParseServers(new JsonObject { ["gh"] = config.ToJson() }, McpConfigSource.Global).Single();
        Assert.Equal(config.Url, again.Url);
        Assert.Equal(config.Headers["Authorization"], again.Headers["Authorization"]);
        Assert.Equal(TimeSpan.FromSeconds(30), again.ToolTimeout);
        Assert.Equal(8080, again.OAuth?.CallbackPort);
        Assert.True(again.AllowsAlways("anything"));
        Assert.Null(again.ToJson()["type"]);
    }

    [Theory]
    [InlineData("{env:TOKEN}", "secret")]
    [InlineData("$TOKEN", "secret")]
    [InlineData("${TOKEN}", "secret")]
    [InlineData("${env:TOKEN}", "secret")]
    [InlineData("Bearer ${TOKEN}", "Bearer secret")]
    [InlineData("${MISSING:-fallback}", "fallback")]
    [InlineData("${workspaceFolder}/x", "/ws/x")]
    [InlineData("{workspaceFolder}", "/ws")]
    [InlineData("${workspaceFolderBasename}", "ws")]
    [InlineData("{not a var}", "{not a var}")]
    [InlineData("${input:key}", "${input:key}")]
    public void Variables_Expand(string input, string expected)
    {
        var vars = new McpVariables("/ws", name => name == "TOKEN" ? "secret" : null);
        Assert.Equal(expected, vars.Expand(input));
    }

    [Fact]
    public void Variables_ReportMissing()
    {
        var vars = new McpVariables("/ws", _ => null);
        var missing = new List<string>();
        Assert.Equal("Bearer ", vars.Expand("Bearer {env:NOPE}", missing));
        Assert.Equal(["NOPE"], missing);
    }

    [Fact]
    public void Expansion_MergesEnvFile()
    {
        var dir = Directory.CreateTempSubdirectory("tcode-mcp-envfile-").FullName;
        File.WriteAllText(Path.Combine(dir, ".env"), "# comment\nA=from-file\nexport B=\"two words\"\nC=3 # trailing\n");
        var config = McpServerConfig.From("x", new McpServerSettings
        {
            Command = "x", EnvFile = "${workspaceFolder}/.env", Env = new() { ["A"] = "explicit" },
        }, McpConfigSource.Global);
        var e = new McpVariables(dir).Expand(config);
        Assert.Equal("explicit", e.Env["A"]);
        Assert.Equal("two words", e.Env["B"]);
        Assert.Equal("3", e.Env["C"]);
        Assert.Equal(dir, e.Cwd);
        Assert.True(McpVariables.UsesWorkspace(config));
    }

    [Theory]
    [InlineData("github", "github")]
    [InlineData("My Server.v2", "My-Server-v2")]
    [InlineData("  @scope/pkg  ", "scope-pkg")]
    [InlineData("!!!", "server")]
    public void SanitizeKey(string input, string expected) => Assert.Equal(expected, McpServerConfig.SanitizeKey(input));

    [Fact]
    public void ProjectConfig_FindsGitRootAndWritesBack()
    {
        var root = Directory.CreateTempSubdirectory("tcode-mcp-proj-").FullName;
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var sub = Directory.CreateDirectory(Path.Combine(root, "src", "a")).FullName;
        File.WriteAllText(Path.Combine(root, ".mcp.json"), """
            // Claude Code style
            { "mcpServers": { "db": { "command": "db-mcp", "args": ["${DB_URL}"] } }, "other": 1 }
            """);
        var servers = McpProjectConfig.Load(sub, out var error);
        Assert.Null(error);
        Assert.Equal(McpConfigSource.Project, servers.Single().Source);
        McpProjectConfig.Update(sub, "web", new JsonObject { ["url"] = "https://x" });
        var text = File.ReadAllText(Path.Combine(root, ".mcp.json"));
        Assert.Contains("\"other\": 1", text);
        Assert.Equal(2, McpProjectConfig.Load(sub, out _).Count);
    }
}
