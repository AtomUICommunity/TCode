using TCode.Ai.Messages;
using Xunit;

namespace TCode.Mcp.Tests;

/// <summary>Composer 的 @ 资源与 / 提示词（Host 侧读取与展开）。</summary>
public class McpContextTests
{
    [Fact]
    public async Task ListsReadsResourcesAndExpandsPrompts()
    {
        var config = TestSupport.StdioServer();
        var cwd = Directory.CreateTempSubdirectory("tcode-mcp-ctx-").FullName;
        await using var manager = new McpManager(new McpHost(), new McpTrustStore(Path.GetTempFileName()), () => [config]);
        // 第一次可能还在连接：等连接就绪后再取目录
        await manager.Acquire(config, cwd, lease: false).WaitReadyAsync(TimeSpan.FromSeconds(20), CancellationToken.None);

        var catalog = await McpContext.CatalogAsync(manager, cwd, CancellationToken.None);
        Assert.Equal(["test://readme", "test://pixel"], catalog.Resources.Select(r => r.Uri));
        var prompt = Assert.Single(catalog.Prompts);
        Assert.Equal("review", prompt.Name);
        Assert.True(Assert.Single(prompt.Arguments).Required);

        var readme = Assert.Single(await McpContext.ReadResourceAsync(manager, cwd, "test", "test://readme", true, CancellationToken.None));
        Assert.Equal("<mcp_resource server=\"test\" uri=\"test://readme\">\n# Readme\nHello resource.\n</mcp_resource>", ((TextContent)readme).Text);
        Assert.IsType<ImageContent>(Assert.Single(await McpContext.ReadResourceAsync(manager, cwd, "test", "test://pixel", true, CancellationToken.None)));
        Assert.Contains("omitted", ((TextContent)Assert.Single(await McpContext.ReadResourceAsync(manager, cwd, "test", "test://pixel", false,
            CancellationToken.None))).Text);

        var expanded = await McpContext.ExpandPromptAsync(manager, cwd, "test", "review", new Dictionary<string, string> { ["file"] = "a.cs" }, true,
            CancellationToken.None);
        Assert.Equal("Please review a.cs.\n\n[assistant] Sure, send it.", ((TextContent)Assert.Single(expanded)).Text);

        await Assert.ThrowsAsync<McpUnavailableException>(() =>
            McpContext.ReadResourceAsync(manager, cwd, "missing", "x://y", true, CancellationToken.None));
    }
}
