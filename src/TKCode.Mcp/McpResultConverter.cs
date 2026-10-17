using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using TKCode.Agent;
using TKCode.Ai.Messages;
using ContentBlock = TKCode.Ai.Messages.ContentBlock;
using TextContent = TKCode.Ai.Messages.TextContent;
using ImageContent = TKCode.Ai.Messages.ImageContent;

namespace TKCode.Mcp;

/// <summary>MCP 工具调用在 UI 中的详情（不发给模型，docs/design/mcp.md §5.3）。</summary>
public sealed record McpToolDetails(
    string Server,
    string Tool,
    string? Title,
    bool IsError,
    IReadOnlyList<McpBlockSummary> Blocks,
    JsonElement? Structured = null,
    string? FullOutputPath = null);

public sealed record McpBlockSummary(string Type, string? MimeType = null, string? Uri = null, int? Bytes = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(McpToolDetails))]
internal sealed partial class McpJsonContext : JsonSerializerContext;

/// <summary>把 <see cref="CallToolResult"/> 转成交给模型的内容（docs/design/mcp.md §5.3）。</summary>
public static class McpResultConverter
{
    /// <summary>交给模型的文本总量上限（字符）；超出部分写入文件。</summary>
    public const int MaxTextLength = 100_000;

    /// <param name="spillDirectory">超长时写全文的目录（null 表示直接截断）。</param>
    public static AgentToolResult Convert(CallToolResult result, string server, string tool, string? title, bool supportsImages,
        string? spillDirectory, string callId)
    {
        var blocks = new List<ContentBlock>();
        var summaries = new List<McpBlockSummary>();
        var text = new StringBuilder();

        void FlushText()
        {
            if (text.Length > 0)
            {
                blocks.Add(new TextContent(text.ToString()));
                text.Clear();
            }
        }

        void AppendLine(string line)
        {
            if (text.Length > 0 && text[^1] != '\n')
            {
                text.Append('\n');
            }
            text.Append(line);
        }

        void AddImage(string base64, string mimeType, int bytes)
        {
            if (supportsImages && mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                FlushText();
                blocks.Add(new ImageContent(base64, mimeType));
            }
            else
            {
                AppendLine($"[image: {mimeType}, {bytes / 1024} KB omitted]");
            }
        }

        var hasText = false;
        foreach (var block in result.Content)
        {
            switch (block)
            {
                case TextContentBlock t:
                    hasText = true;
                    if (text.Length > 0)
                    {
                        text.Append('\n');
                    }
                    text.Append(t.Text);
                    summaries.Add(new McpBlockSummary("text", Bytes: t.Text.Length));
                    break;
                case ImageContentBlock image:
                    {
                        var bytes = image.DecodedData.Length;
                        AddImage(Encoding.ASCII.GetString(image.Data.Span), image.MimeType, bytes);
                        summaries.Add(new McpBlockSummary("image", image.MimeType, Bytes: bytes));
                        break;
                    }
                case AudioContentBlock audio:
                    AppendLine($"[audio: {audio.MimeType} omitted]");
                    summaries.Add(new McpBlockSummary("audio", audio.MimeType));
                    break;
                case ResourceLinkBlock link:
                    AppendLine($"[resource] {link.Name} <{link.Uri}>" + (link.MimeType is { } mt ? $" ({mt})" : "")
                               + (link.Description is { Length: > 0 } d ? $" — {d}" : ""));
                    summaries.Add(new McpBlockSummary("resource_link", link.MimeType, link.Uri));
                    break;
                case EmbeddedResourceBlock embedded:
                    switch (embedded.Resource)
                    {
                        case TextResourceContents textResource:
                            hasText = true;
                            AppendLine($"<resource uri=\"{textResource.Uri}\">\n{textResource.Text}\n</resource>");
                            summaries.Add(new McpBlockSummary("resource", textResource.MimeType, textResource.Uri, textResource.Text.Length));
                            break;
                        case BlobResourceContents blob:
                            {
                                var mime = blob.MimeType ?? "application/octet-stream";
                                var bytes = blob.DecodedData.Length;
                                if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                                {
                                    AddImage(Encoding.ASCII.GetString(blob.Blob.Span), mime, bytes);
                                }
                                else
                                {
                                    AppendLine($"[resource {blob.Uri}: {mime}, {bytes / 1024} KB binary omitted]");
                                }
                                summaries.Add(new McpBlockSummary("resource", mime, blob.Uri, bytes));
                                break;
                            }
                    }
                    break;
                default:
                    AppendLine($"[{block.Type} content omitted]");
                    summaries.Add(new McpBlockSummary(block.Type));
                    break;
            }
        }
        // 只有结构化结果时，把它当作文本交给模型
        if (!hasText && result.StructuredContent is { } structured && structured.ValueKind != JsonValueKind.Undefined)
        {
            AppendLine(structured.GetRawText());
        }
        FlushText();
        if (blocks.Count == 0)
        {
            blocks.Add(new TextContent(result.IsError == true ? "The tool reported an error without details." : "(no output)"));
        }

        string? fullPath = null;
        var total = blocks.OfType<TextContent>().Sum(b => b.Text.Length);
        if (total > MaxTextLength)
        {
            (blocks, fullPath) = Truncate(blocks, spillDirectory, callId);
        }
        var details = new McpToolDetails(server, tool, title, result.IsError == true, summaries,
            result.StructuredContent is { ValueKind: not JsonValueKind.Undefined } s ? s : null, fullPath);
        return new AgentToolResult(blocks)
        {
            IsError = result.IsError == true,
            Details = JsonSerializer.Serialize(details, McpJsonContext.Default.McpToolDetails),
        };
    }

    private static (List<ContentBlock> Blocks, string? Path) Truncate(List<ContentBlock> blocks, string? spillDirectory, string callId)
    {
        var all = string.Join("\n", blocks.OfType<TextContent>().Select(b => b.Text));
        string? path = null;
        if (spillDirectory is not null)
        {
            try
            {
                Directory.CreateDirectory(spillDirectory);
                path = Path.Combine(spillDirectory, $"mcp-{McpToolNames.Sanitize(callId)}.txt");
                File.WriteAllText(path, all);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                path = null;
            }
        }
        var head = all[..MaxTextLength];
        var note = path is not null
            ? $"\n\n[Output truncated: showing {MaxTextLength} of {all.Length} characters. Full output saved to {path} — use the read tool with offset to see the rest.]"
            : $"\n\n[Output truncated: showing {MaxTextLength} of {all.Length} characters.]";
        var result = new List<ContentBlock> { new TextContent(head + note) };
        result.AddRange(blocks.Where(b => b is not TextContent));
        return (result, path);
    }
}
