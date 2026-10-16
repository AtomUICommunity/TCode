using TCode.Agent.Coding.Sessions;
using TCode.AgentHost;
using TCode.Ai.Messages;
using Xunit;

namespace TCode.AgentHost.Tests;

/// <summary>用户附图：发送时读取附件为 base64；会话记录只存路径，恢复时读回；不支持图片的模型只收到文本。</summary>
public sealed class ImageAttachmentStorageTests
{
    [Fact]
    public void Prompt_ReadsData_StorageKeepsPathOnly_AndLoadRestoresIt()
    {
        var dir = Directory.CreateTempSubdirectory("tcode-attach-").FullName;
        var path = Path.Combine(dir, "a.png");
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
        var missing = Path.Combine(dir, "gone.png");

        var prompt = ImageAttachments.CreatePrompt("看 [Image #1: a] [Image #2: gone]", [(path, "image/png"), (missing, "image/png")]);
        var images = prompt.Content.OfType<ImageContent>().ToArray();
        Assert.Equal([path, missing], images.Select(i => i.Path));
        Assert.Equal(Convert.ToBase64String(File.ReadAllBytes(path)), images[0].Data);
        Assert.False(images[1].HasData); // 读不到：保留路径以维持编号，但不发给模型
        Assert.IsType<TextContent>(prompt.Content[^1]);

        var stored = Assert.IsType<UserMessage>(ImageAttachments.ForStorage(prompt));
        Assert.All(stored.Content.OfType<ImageContent>(), i => Assert.False(i.HasData));

        var loaded = Assert.IsType<UserMessage>(Assert.Single(ImageAttachments.Load([stored])));
        Assert.Equal(images[0].Data, loaded.Content.OfType<ImageContent>().First().Data);

        Assert.Single(prompt.ForModel(supportsImages: true).Content.OfType<ImageContent>());
        Assert.Empty(prompt.ForModel(supportsImages: false).Content.OfType<ImageContent>());
        var plain = UserMessage.FromText("x");
        Assert.Same(plain, plain.ForModel(supportsImages: false));
    }

    [Fact]
    public void SessionTitle_CollapsesImageReferences()
    {
        var title = SessionIndex.TitleOf([UserMessage.FromText("对比 [Image #1: /Users/a/.tcode/attachments/2026-10/x.png] 的差异")]);
        Assert.Equal("对比 [Image #1] 的差异", title);
    }
}
