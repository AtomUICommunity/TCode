using System.Text;
using ModelContextProtocol.Protocol;
using TCode.Config;
using ContentBlock = TCode.Ai.Messages.ContentBlock;
using ImageContent = TCode.Ai.Messages.ImageContent;
using TextContent = TCode.Ai.Messages.TextContent;

namespace TCode.Mcp;

public sealed record McpResourceInfo(string Server, string Uri, string Name, string? Title, string? Description, string? MimeType);

public sealed record McpPromptArgumentInfo(string Name, string? Description, bool Required);

public sealed record McpPromptInfo(string Server, string Name, string? Title, string? Description, IReadOnlyList<McpPromptArgumentInfo> Arguments);

public sealed record McpCatalog(IReadOnlyList<McpResourceInfo> Resources, IReadOnlyList<McpPromptInfo> Prompts);

/// <summary>
/// Composer 的 @ 资源与 / 提示词（docs/design/mcp.md §8.1、§8.2）：列出项目可用服务器的资源与提示词，
/// 发送时由 Host 读取资源 / 展开提示词，内容不经过 Desktop。
/// </summary>
public static class McpContext
{
    /// <summary>附在用户消息中的资源文本上限（字符）。</summary>
    public const int MaxResourceText = 100_000;

    public static async Task<McpCatalog> CatalogAsync(McpManager manager, string? cwd, CancellationToken ct)
    {
        var resources = new List<McpResourceInfo>();
        var prompts = new List<McpPromptInfo>();
        foreach (var entry in manager.Resolve(cwd).Where(e => e.Availability == McpServerAvailability.Ready))
        {
            var connection = entry.Connection ?? manager.Acquire(entry.Config, cwd, lease: false);
            if (!await connection.WaitReadyAsync(TimeSpan.FromSeconds(3), ct) || connection.Client is not { } client)
            {
                continue;
            }
            var caps = client.ServerCapabilities;
            try
            {
                if (caps.Resources is not null)
                {
                    resources.AddRange((await client.ListResourcesAsync(cancellationToken: ct)).Select(r =>
                        new McpResourceInfo(connection.Key, r.Uri, r.Name, r.Title, r.Description, r.MimeType)));
                }
                if (caps.Prompts is not null)
                {
                    prompts.AddRange((await client.ListPromptsAsync(cancellationToken: ct)).Select(p => new McpPromptInfo(connection.Key, p.Name, p.Title,
                        p.Description, p.ProtocolPrompt.Arguments?.Select(a => new McpPromptArgumentInfo(a.Name, a.Description, a.Required == true)).ToList() ?? [])));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                connection.AppendLog($"Listing resources / prompts failed: {ex.Message}");
            }
        }
        return new McpCatalog(resources, prompts);
    }

    /// <summary>读取资源，转为附在用户消息中的内容块（文本包在 &lt;mcp_resource&gt; 中；图片按模型能力附上或写占位）。</summary>
    public static async Task<IReadOnlyList<ContentBlock>> ReadResourceAsync(McpManager manager, string? cwd, string server, string uri, bool supportsImages,
        CancellationToken ct)
    {
        var client = await ClientAsync(manager, cwd, server, ct);
        var result = await client.ReadResourceAsync(uri, cancellationToken: ct);
        var blocks = new List<ContentBlock>();
        foreach (var content in result.Contents)
        {
            switch (content)
            {
                case TextResourceContents text:
                    var body = text.Text.Length > MaxResourceText
                        ? text.Text[..MaxResourceText] + $"\n[truncated: {text.Text.Length - MaxResourceText} more characters]"
                        : text.Text;
                    blocks.Add(new TextContent($"<mcp_resource server=\"{server}\" uri=\"{text.Uri}\">\n{body}\n</mcp_resource>"));
                    break;
                case BlobResourceContents blob when supportsImages && blob.MimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true:
                    blocks.Add(new ImageContent(Encoding.ASCII.GetString(blob.Blob.Span), blob.MimeType));
                    break;
                case BlobResourceContents blob:
                    blocks.Add(new TextContent($"<mcp_resource server=\"{server}\" uri=\"{blob.Uri}\">[{blob.MimeType ?? "binary"}, {blob.DecodedData.Length / 1024} KB omitted]</mcp_resource>"));
                    break;
            }
        }
        return blocks;
    }

    /// <summary>
    /// 展开提示词：user 消息作为正文，assistant 消息标注 [assistant] 后并入；资源与图片按 <see cref="ReadResourceAsync"/> 的方式处理。
    /// </summary>
    public static async Task<IReadOnlyList<ContentBlock>> ExpandPromptAsync(McpManager manager, string? cwd, string server, string name,
        IReadOnlyDictionary<string, string> arguments, bool supportsImages, CancellationToken ct)
    {
        var client = await ClientAsync(manager, cwd, server, ct);
        var result = await client.GetPromptAsync(name, arguments.ToDictionary(a => a.Key, a => (object?)a.Value), cancellationToken: ct);
        var blocks = new List<ContentBlock>();
        var text = new StringBuilder();
        void Flush()
        {
            if (text.Length > 0)
            {
                blocks.Add(new TextContent(text.ToString().Trim()));
                text.Clear();
            }
        }
        foreach (var message in result.Messages)
        {
            var prefix = message.Role == Role.Assistant ? "[assistant] " : "";
            switch (message.Content)
            {
                case TextContentBlock t:
                    text.Append(prefix).Append(t.Text).Append("\n\n");
                    break;
                case ImageContentBlock image when supportsImages:
                    Flush();
                    blocks.Add(new ImageContent(Encoding.ASCII.GetString(image.Data.Span), image.MimeType));
                    break;
                case EmbeddedResourceBlock { Resource: TextResourceContents r }:
                    text.Append($"<mcp_resource server=\"{server}\" uri=\"{r.Uri}\">\n{r.Text}\n</mcp_resource>\n\n");
                    break;
                case ResourceLinkBlock link:
                    text.Append($"[resource] {link.Name} <{link.Uri}>\n\n");
                    break;
                default:
                    text.Append($"[{message.Content.Type} content omitted]\n\n");
                    break;
            }
        }
        Flush();
        return blocks;
    }

    private static async Task<ModelContextProtocol.Client.McpClient> ClientAsync(McpManager manager, string? cwd, string server, CancellationToken ct)
    {
        var entry = manager.Resolve(cwd).FirstOrDefault(e => e.Config.Key == server && e.Availability == McpServerAvailability.Ready)
                    ?? throw new McpUnavailableException(server, "not configured for this project");
        var connection = entry.Connection ?? manager.Acquire(entry.Config, cwd, lease: false);
        if (!await connection.WaitReadyAsync(entry.Config.StartupTimeout, ct) || connection.Client is not { } client)
        {
            throw new McpUnavailableException(server, connection.Status.Error);
        }
        return client;
    }
}
