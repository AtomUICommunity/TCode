using TCode.AgentHost;
using TCode.Ai.Messages;
using Xunit;

namespace TCode.AgentHost.Tests;

public sealed class HistoryPagingTests
{
    private static AssistantMessage Reply(string text) => new([new TextContent(text)]) { Api = "fake", Provider = "fake", Model = "m" };

    [Fact]
    public void Pages_StartAtUserMessages_AndNeverSplitToolResults()
    {
        // 轮次：u0 a t a | u1 a | u2 a t a
        Message[] messages =
        [
            UserMessage.FromText("u0"), Reply("a"), new ToolResultMessage("c", "Bash", [new TextContent("out")]), Reply("b"),
            UserMessage.FromText("u1"), Reply("c"),
            UserMessage.FromText("u2"), Reply("d"), new ToolResultMessage("d", "Bash", [new TextContent("out")]), Reply("e"),
        ];
        Assert.Equal(6, HistoryPaging.StartOfRecentTurns(messages, messages.Length, 1));
        Assert.Equal(4, HistoryPaging.StartOfRecentTurns(messages, messages.Length, 2));
        Assert.Equal(0, HistoryPaging.StartOfRecentTurns(messages, messages.Length, 5));
        // 下一页：取 4 之前的 1 轮
        Assert.Equal(0, HistoryPaging.StartOfRecentTurns(messages, 4, 1));
        Assert.Equal(0, HistoryPaging.StartOfRecentTurns(messages, 0, 3));
    }

    [Fact]
    public void LongText_KeepsHeadAndTail()
    {
        var text = new string('a', 30_000) + new string('m', 40_000) + new string('z', 10_000);
        var truncated = HistoryPaging.Truncate(text);
        Assert.True(truncated.Length < HistoryPaging.MaxTextLength + 100);
        Assert.StartsWith(new string('a', 1000), truncated);
        Assert.EndsWith(new string('z', 8192), truncated);
        Assert.Contains("characters omitted", truncated);
        Assert.Same("short", HistoryPaging.Truncate("short"));
    }
}
