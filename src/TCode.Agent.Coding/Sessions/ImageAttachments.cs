using System.Security.Cryptography;
using TCode.Ai.Messages;

namespace TCode.Agent.Coding.Sessions;

/// <summary>
/// 图片数据不常驻内存，也不写进会话记录：用户附图本来就是文件；工具产生的图片（read、MCP）进入历史时
/// 转存到 ~/.tcode/attachments/tool（按内容哈希命名）。历史与会话记录只保存路径，每次发给模型前
/// 由 <see cref="ImageContent.WithData"/> 读回。附件文件已不存在时这张图片不再发给模型（提示词中的路径仍在）。
/// </summary>
public static class ImageAttachments
{
    /// <summary>工具图片的转存目录。</summary>
    public static string ToolRoot => Path.Combine(TCodePaths.Home, "attachments", "tool");

    /// <summary>
    /// 用户输入：图片在前（各家都建议图片先于文字），文本在后。图片只记路径，发送时才读取；
    /// 读不到的图片仍保留路径，使消息中图片的顺序与提示词里的编号一致。
    /// </summary>
    public static UserMessage CreatePrompt(string text, IEnumerable<(string Path, string MimeType)> images)
    {
        var content = new List<ContentBlock>();
        content.AddRange(images.Select(i => new ImageContent("", i.MimeType) { Path = i.Path }));
        content.Add(new TextContent(text));
        return new UserMessage(content);
    }

    /// <summary>写入会话记录的版本：带路径的图片去掉数据。</summary>
    public static Message ForStorage(Message message) => Map(message, image => image.Path is not null && image.HasData ? image with { Data = "" } : image);

    /// <summary>进入历史的版本：没有路径的图片转存为文件（转存失败时保留数据），带路径的图片去掉数据。</summary>
    public static Message Offload(Message message) => Map(message, image => image switch
    {
        { HasData: false } => image,
        { Path: not null } => image with { Data = "" },
        _ => Save(image) is { } path ? image with { Data = "", Path = path } : image,
    });

    private static Message Map(Message message, Func<ImageContent, ImageContent> map)
    {
        var content = message switch
        {
            UserMessage u => u.Content,
            ToolResultMessage r => r.Content,
            _ => null,
        };
        if (content is null || !content.Any(c => c is ImageContent))
        {
            return message;
        }
        var mapped = content.Select(c => c is ImageContent image ? map(image) : c).ToArray();
        if (mapped.SequenceEqual(content, ReferenceEqualityComparer.Instance))
        {
            return message;
        }
        return message switch
        {
            UserMessage u => u with { Content = mapped },
            ToolResultMessage r => r with { Content = mapped },
            _ => message,
        };
    }

    private static string? Save(ImageContent image)
    {
        try
        {
            var bytes = Convert.FromBase64String(image.Data);
            var path = Path.Combine(ToolRoot, Convert.ToHexStringLower(SHA256.HashData(bytes))[..32] + Extension(image.MimeType));
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(ToolRoot);
                var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, path, overwrite: true);
            }
            return path;
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Extension(string mimeType) => mimeType.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/bmp" => ".bmp",
        _ => ".img",
    };
}
