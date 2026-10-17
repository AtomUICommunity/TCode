using TKCode.Agent.Coding.Sessions;
using TKCode.AgentHost;
using TKCode.Ai.Messages;
using TKCode.Config;
using Xunit;

namespace TKCode.AgentHost.Tests;

/// <summary>
/// 图片数据不常驻：用户附图只记路径，工具图片转存到附件目录；会话记录与历史只存路径，发给模型时才读回；
/// 不支持图片的模型只收到文本。
/// </summary>
public sealed class ImageAttachmentStorageTests : IDisposable
{
    private readonly string? _savedHome = Environment.GetEnvironmentVariable(ConfigPaths.HomeVariable);
    private readonly string _home = Directory.CreateTempSubdirectory("tkcode-attach-home-").FullName;

    public ImageAttachmentStorageTests() => Environment.SetEnvironmentVariable(ConfigPaths.HomeVariable, _home);

    public void Dispose() => Environment.SetEnvironmentVariable(ConfigPaths.HomeVariable, _savedHome);

    [Fact]
    public void Prompt_KeepsPathOnly_AndForModelReadsData()
    {
        var dir = Directory.CreateTempSubdirectory("tkcode-attach-").FullName;
        var path = Path.Combine(dir, "a.png");
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
        var missing = Path.Combine(dir, "gone.png");

        var prompt = ImageAttachments.CreatePrompt("看 [Image #1: a] [Image #2: gone]", [(path, "image/png"), (missing, "image/png")]);
        var images = prompt.Content.OfType<ImageContent>().ToArray();
        Assert.Equal([path, missing], images.Select(i => i.Path));
        Assert.All(images, i => Assert.False(i.HasData));
        Assert.IsType<TextContent>(prompt.Content[^1]);
        Assert.Same(prompt, ImageAttachments.ForStorage(prompt));
        Assert.Same(prompt, ImageAttachments.Offload(prompt));

        // 读不到的图片保留路径以维持编号，但不发给模型
        var sent = Assert.Single(prompt.ForModel(supportsImages: true).Content.OfType<ImageContent>());
        Assert.Equal(Convert.ToBase64String(File.ReadAllBytes(path)), sent.Data);
        Assert.Empty(prompt.ForModel(supportsImages: false).Content.OfType<ImageContent>());
        var plain = UserMessage.FromText("x");
        Assert.Same(plain, plain.ForModel(supportsImages: false));
    }

    [Fact]
    public void ToolImage_IsOffloadedToAttachments_ByContentHash_AndReadBackForModel()
    {
        var data = Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 9, 8, 7]);
        var result = new ToolResultMessage("c1", "read", [new TextContent("Image x.png"), new ImageContent(data, "image/png")]);

        var offloaded = Assert.IsType<ToolResultMessage>(ImageAttachments.Offload(result));
        var image = Assert.Single(offloaded.Content.OfType<ImageContent>());
        Assert.False(image.HasData);
        Assert.StartsWith(ImageAttachments.ToolRoot, image.Path);
        Assert.EndsWith(".png", image.Path);
        Assert.Equal(data, Convert.ToBase64String(File.ReadAllBytes(image.Path!)));
        // 相同内容复用同一个文件
        Assert.Equal(image.Path, Assert.Single(((ToolResultMessage)ImageAttachments.Offload(result)).Content.OfType<ImageContent>()).Path);

        Assert.Equal(data, Assert.Single(offloaded.ForModel().Content.OfType<ImageContent>()).Data);
        File.Delete(image.Path!);
        Assert.Equal(["Image x.png"], offloaded.ForModel().Content.Select(c => Assert.IsType<TextContent>(c).Text));
    }

    [Fact]
    public void SessionTitle_CollapsesImageReferences()
    {
        var title = SessionIndex.TitleOf([UserMessage.FromText("对比 [Image #1: /Users/a/.tkcode/attachments/2026-10/x.png] 的差异")]);
        Assert.Equal("对比 [Image #1] 的差异", title);
    }
}
