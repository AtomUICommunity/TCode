using TCode.Ai.Messages;
using System.Text.Json.Nodes;
using TCode.Config;
using TCode.Mcp;
using TCode.Rpc;

namespace TCode.AgentHost;

/// <summary>设置页 MCP 分区的 Host 端实现（docs/design/mcp.md §11、§13）。</summary>
public sealed class McpService(McpManager mcp)
{
    public McpServersDto List(string? cwd)
    {
        string? projectError = null;
        if (cwd is not null)
        {
            McpProjectConfig.Load(cwd, out projectError);
        }
        var servers = mcp.Resolve(cwd).Select(ToDto).ToList();
        return new McpServersDto(servers, cwd is null ? null : McpProjectConfig.PathFor(cwd), projectError);
    }

    public async Task<McpToolsDto> ToolsAsync(string key, string? cwd, CancellationToken ct)
    {
        var entry = Find(key, cwd);
        if (entry.Availability != McpServerAvailability.Ready)
        {
            return new McpToolsDto([], $"Server is {entry.Availability.ToString().ToLowerInvariant()}.");
        }
        // 设置页查看工具：没有连接时临时建立（无租约，空闲后关闭）
        var connection = entry.Connection ?? mcp.Acquire(entry.Config, cwd, lease: false);
        if (!await connection.WaitReadyAsync(entry.Config.StartupTimeout, ct))
        {
            return new McpToolsDto([], connection.Status.Error ?? connection.State.ToString());
        }
        var tools = await connection.GetToolsAsync(ct);
        return new McpToolsDto(tools.Select(t => new McpToolDto(
            t.Name,
            t.Title ?? t.Annotations?.Title,
            t.Description,
            t.Annotations?.ReadOnlyHint == true,
            t.Annotations?.DestructiveHint == true,
            t.Annotations?.OpenWorldHint == true,
            t.InputSchema.GetRawText().Length,
            entry.Config.DisabledTools.Contains(t.Name),
            entry.Config.AllowsAlways(t.Name))).ToList(), null);
    }

    public McpLogDto Log(string key, string? cwd) => new(Find(key, cwd).Connection?.Log ?? []);

    public async Task<McpTestResult> TestAsync(McpTestRequest request, CancellationToken ct)
    {
        McpServerConfig config;
        try
        {
            var node = JsonNode.Parse(request.ConfigJson) as JsonObject ?? throw new FormatException("Configuration must be a JSON object.");
            config = McpServerConfig.ParseServers(new JsonObject { [request.Key] = node }, McpConfigSource.Global).Single();
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            return new McpTestResult(false, ex.Message, 0, null, null, null, [], false);
        }
        if (config.Error is { } error)
        {
            return new McpTestResult(false, error, 0, null, null, null, [], false);
        }
        // 临时连接：不进入连接池，测试完立即关闭
        await using var connection = new McpConnection(config, new McpVariables(request.Cwd).Expand(config), mcp.Host, request.Cwd);
        connection.Start();
        var ok = await connection.WaitReadyAsync(config.StartupTimeout, ct);
        var status = connection.Status;
        var tools = ok ? (await connection.GetToolsAsync(ct)).Count : 0;
        return new McpTestResult(ok, ok ? null : status.Error ?? "Timed out.", tools, status.ServerName, status.ServerVersion, status.ProtocolVersion,
            connection.Log, connection.State == McpConnectionState.NeedsAuth);
    }

    public Task RestartAsync(string key, string? cwd, bool login) => mcp.RestartAsync(key, cwd, login);

    public async Task LogoutAsync(string key, string? cwd)
    {
        var entry = Find(key, cwd);
        if (entry.Config.Url is { } url)
        {
            mcp.Host.OAuth?.Store.Remove(new McpVariables(cwd).Expand(entry.Config).Url ?? url);
        }
        if (entry.Connection is { } connection)
        {
            await connection.RestartAsync();
        }
    }

    public void Trust(string key, string cwd, bool trusted)
    {
        var entry = Find(key, cwd);
        if (entry.Config.Source != McpConfigSource.Project)
        {
            throw new InvalidOperationException($"'{key}' is not a project server.");
        }
        mcp.Trust.Set(McpProjectConfig.ProjectRoot(cwd), entry.Config, trusted);
        mcp.Reload();
    }

    public async Task<McpCatalogDto> CatalogAsync(string? cwd, CancellationToken ct)
    {
        var catalog = await McpContext.CatalogAsync(mcp, cwd, ct);
        return new McpCatalogDto(
            catalog.Resources.Select(r => new McpResourceDto(r.Server, r.Uri, r.Name, r.Title, r.Description, r.MimeType)).ToList(),
            catalog.Prompts.Select(p => new McpPromptDto(p.Server, p.Name, p.Title, p.Description,
                p.Arguments.Select(a => new McpPromptArgumentDto(a.Name, a.Description, a.Required)).ToList())).ToList());
    }

    /// <summary>
    /// 展开随消息发送的 MCP 提示词与资源（docs/design/mcp.md §8）：提示词内容放在用户输入之前，资源内容附在末尾。
    /// 读取失败的资源写一行错误说明，不阻止发送。
    /// </summary>
    public async Task<UserMessage> ExpandAsync(string cwd, PromptRequest request, UserMessage message, bool supportsImages, CancellationToken ct)
    {
        var content = new List<ContentBlock>();
        if (request.McpPrompt is { } prompt)
        {
            content.AddRange(await McpContext.ExpandPromptAsync(mcp, cwd, prompt.Server, prompt.Name, prompt.Arguments, supportsImages, ct));
        }
        content.AddRange(message.Content.Where(b => b is not TextContent { Text.Length: 0 }));
        foreach (var resource in request.McpResources ?? [])
        {
            try
            {
                content.AddRange(await McpContext.ReadResourceAsync(mcp, cwd, resource.Server, resource.Uri, supportsImages, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                content.Add(new TextContent($"<mcp_resource server=\"{resource.Server}\" uri=\"{resource.Uri}\">[failed to read: {ex.Message}]</mcp_resource>"));
            }
        }
        return message with { Content = content };
    }

    private McpServerEntry Find(string key, string? cwd) =>
        mcp.Resolve(cwd).FirstOrDefault(e => e.Config.Key == key) ?? throw new KeyNotFoundException($"MCP server '{key}' not found.");

    private static McpServerDto ToDto(McpServerEntry entry)
    {
        var config = entry.Config;
        var status = entry.Connection?.Status;
        var summary = config.Transport == McpTransportKind.Stdio
            ? string.Join(' ', new[] { config.Command ?? "" }.Concat(config.Args.Select(Quote)))
            : config.Url ?? "";
        return new McpServerDto(
            config.Key,
            config.Title,
            config.Source == McpConfigSource.Project ? "project" : "global",
            config.Transport switch { McpTransportKind.Http => "http", McpTransportKind.Sse => "sse", _ => "stdio" },
            summary,
            config.Enabled,
            entry.Availability switch
            {
                McpServerAvailability.Disabled => "disabled",
                McpServerAvailability.Untrusted => "untrusted",
                McpServerAvailability.Ignored => "ignored",
                McpServerAvailability.Invalid => "invalid",
                _ => "ready",
            },
            status?.State switch
            {
                McpConnectionState.Connecting => "connecting",
                McpConnectionState.Connected => "connected",
                McpConnectionState.NeedsAuth => "needsAuth",
                McpConnectionState.Failed => "failed",
                _ => "idle",
            },
            config.Error ?? status?.Error,
            status?.ToolCount ?? 0,
            status?.PromptCount ?? 0,
            status?.ResourceCount ?? 0,
            status?.ProtocolVersion,
            status?.ServerName,
            status?.ServerVersion,
            status?.Instructions,
            entry.ShadowedGlobal,
            entry.MissingVariables,
            config.ToJson().ToJsonString(),
            config.IsRemote && !config.HasStaticAuthorization);
    }

    private static string Quote(string arg) => arg.Contains(' ') ? $"\"{arg}\"" : arg;
}
