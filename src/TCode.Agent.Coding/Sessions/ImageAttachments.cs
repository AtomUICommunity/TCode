using TCode.Ai.Messages;

namespace TCode.Agent.Coding.Sessions;

/// <summary>
/// 用户附图的持久化约定：会话记录只保存附件路径（数据置空），恢复会话时再从文件读回 base64。
/// 附件文件已不存在时数据保持为空，这张图片不再发给模型（提示词中的路径仍在）。
/// </summary>
public static class ImageAttachments
{
    /// <summary>
    /// 用户输入：图片在前（各家都建议图片先于文字），文本在后。图片文件读不到时仍保留路径（数据为空，不发给模型），
    /// 使消息中图片的顺序与提示词里的编号一致。
    /// </summary>
    public static UserMessage CreatePrompt(string text, IEnumerable<(string Path, string MimeType)> images)
    {
        var content = new List<ContentBlock>();
        content.AddRange(images.Select(i => Read(new ImageContent("", i.MimeType) { Path = i.Path })));
        content.Add(new TextContent(text));
        return new UserMessage(content);
    }

    /// <summary>写入会话记录的版本：带路径的图片去掉数据。</summary>
    public static Message ForStorage(Message message) =>
        message is UserMessage user && user.Content.Any(c => c is ImageContent { Path: not null, HasData: true })
            ? user with
            {
                Content = user.Content.Select(c => c is ImageContent { Path: not null } image ? image with { Data = "" } : c).ToArray(),
            }
            : message;

    /// <summary>从附件文件读回图片数据。</summary>
    public static IReadOnlyList<Message> Load(IReadOnlyList<Message> messages)
    {
        if (!messages.Any(m => m is UserMessage user && user.Content.Any(NeedsData)))
        {
            return messages;
        }
        return messages.Select(m => m is UserMessage user && user.Content.Any(NeedsData)
            ? user with { Content = user.Content.Select(c => NeedsData(c) ? Read((ImageContent)c) : c).ToArray() }
            : m).ToArray();
    }

    private static bool NeedsData(ContentBlock block) => block is ImageContent { Path: not null, HasData: false };

    private static ImageContent Read(ImageContent image)
    {
        try
        {
            return File.Exists(image.Path) ? image with { Data = Convert.ToBase64String(File.ReadAllBytes(image.Path!)) } : image;
        }
        catch (IOException)
        {
            return image;
        }
        catch (UnauthorizedAccessException)
        {
            return image;
        }
    }
}
