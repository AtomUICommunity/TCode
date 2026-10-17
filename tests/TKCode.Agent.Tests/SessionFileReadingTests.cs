using System.Text;
using TKCode.Agent.Coding.Sessions;
using TKCode.Ai.Messages;
using Xunit;

namespace TKCode.Agent.Tests;

/// <summary>会话文件按 UTF-8 字节逐行解析；会话列表用的摘要模式跳过非用户消息的正文，其余信息与完整解析一致。</summary>
public sealed class SessionFileReadingTests
{
    private static async Task<string> WriteSessionAsync()
    {
        var root = Directory.CreateTempSubdirectory("tkcode-read-").FullName;
        var store = SessionStore.Create(root, Path.Combine(root, "sessions"));
        await store.AppendModelChangeAsync("p", "m1");
        await store.AppendMessageAsync(UserMessage.FromText("第一个问题\n第二行"));
        // 超过读缓冲区（64K）的行
        await store.AppendMessageAsync(new AssistantMessage([new ThinkingContent(new string('t', 200_000)), new ToolCallContent("c1", "read", "{}")])
        {
            Api = "a", Provider = "p", Model = "m1",
        });
        await store.AppendMessageAsync(new ToolResultMessage("c1", "read", [new TextContent(new string('r', 100_000))]));
        await store.AppendMetaAsync("自定义标题", true);
        await store.AppendModelChangeAsync("p", "m2");
        await store.AppendMessageAsync(UserMessage.FromText("追问"));
        return store.FilePath;
    }

    [Fact]
    public async Task Summary_MatchesFullRead_WithUserMessagesOnly()
    {
        var path = await WriteSessionAsync();

        var full = SessionStore.Read(path);
        var summary = SessionStore.ReadSummary(path);

        Assert.Equal(4, full.Messages.Count);
        Assert.Equal(200_000, Assert.IsType<ThinkingContent>(Assert.IsType<AssistantMessage>(full.Messages[1]).Content[0]).Thinking.Length);
        Assert.Equal(full.Messages.OfType<UserMessage>().Select(Text), summary.Messages.Select(m => Text(Assert.IsType<UserMessage>(m))));
        Assert.Equal((full.SessionId, full.Cwd, full.ModelProvider, full.ModelId, full.CustomTitle, full.IsPinned, full.LeafId),
            (summary.SessionId, summary.Cwd, summary.ModelProvider, summary.ModelId, summary.CustomTitle, summary.IsPinned, summary.LeafId));
        Assert.Equal("m2", summary.ModelId);
    }

    [Fact]
    public async Task Read_ToleratesBom_CrLf_AndBlankLines()
    {
        var path = await WriteSessionAsync();
        var expected = SessionStore.Read(path);
        var lines = await File.ReadAllLinesAsync(path);
        await File.WriteAllTextAsync(path, string.Join("\r\n\r\n  \r\n", lines), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var read = SessionStore.Read(path);
        Assert.Equal(expected.SessionId, read.SessionId);
        Assert.Equal(expected.Messages.Count, read.Messages.Count);
        Assert.Equal("追问", Text(Assert.IsType<UserMessage>(read.Messages[^1])));
        Assert.Equal(expected.Messages.Count(m => m is UserMessage), SessionStore.ReadSummary(path).Messages.Count);
    }

    private static string Text(UserMessage message) => string.Concat(message.Content.OfType<TextContent>().Select(t => t.Text));
}
