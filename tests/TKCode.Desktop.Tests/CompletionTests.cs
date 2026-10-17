using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using TKCode.Config;
using TKCode.Desktop.Controls.Composer;
using TKCode.Desktop.Icons;
using TKCode.Desktop.ViewModels.Completion;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>统一补全（docs/design/composer.md §7）与模糊匹配（§8.2）。</summary>
public sealed class CompletionTests
{
    [Fact]
    public void Fuzzy_PrefersBoundariesAndFileNames()
    {
        Assert.Null(FuzzyMatcher.Match("xyz", "src/Composer.cs"));
        Assert.Equal(new FuzzyMatch(0, []), FuzzyMatcher.Match("", "anything"));

        // 命中位置用于高亮
        Assert.Equal([4, 12], FuzzyMatcher.Match("cv", "src/ComposerView.cs")!.Value.Positions);

        int Score(string q, string c) => FuzzyMatcher.Score(q, c, FuzzyMatcher.NameStart(c))!.Value;
        // 文件名命中优先于目录命中
        Assert.True(Score("comp", "src/Composer.cs") > Score("comp", "components/a/Readme.md"));
        // 单词 / 驼峰边界优先于词中
        Assert.True(Score("cv", "ComposerView.cs") > Score("cv", "recvbuf.cs"));
        // 连续命中优先于分散命中
        Assert.True(Score("doc", "ComposerDocument.cs") > Score("doc", "dxoxc.cs"));
        // 不区分大小写
        Assert.NotNull(FuzzyMatcher.Match("README", "readme.md"));
    }

    [Theory]
    [InlineData("@", 1, '@', "")]
    [InlineData("看下 @src/Fo", 10, '@', "src/Fo")]
    [InlineData("/rev", 4, '/', "rev")]
    [InlineData("a\n/re", 5, '/', "re")]
    [InlineData("看下 @src 其他", 7, '@', "src")]
    public void Detect_FindsQueryAtCaret(string text, int caret, char trigger, string query)
    {
        var q = CompletionViewModel.Detect(new ComposerDocument([new TextSegment(text)]), caret);
        Assert.NotNull(q);
        Assert.Equal((trigger, query), (q.Trigger, q.Text));
        Assert.Equal(caret, q.End);
    }

    [Theory]
    [InlineData("mail@host", 9)]   // @ 前面不是空白
    [InlineData("a/b", 3)]         // 路径里的 /
    [InlineData("@a @b ", 6)]      // 光标后面是空白
    [InlineData("@a@b", 4)]
    [InlineData("看下", 2)]
    public void Detect_IgnoresNonQueries(string text, int caret) =>
        Assert.Null(CompletionViewModel.Detect(new ComposerDocument([new TextSegment(text)]), caret));

    [Fact]
    public void Detect_TagIsABoundary_ButNotASeparator()
    {
        var tag = new TagSegment(TagKind.File, "a.cs", "a.cs");
        Assert.Null(CompletionViewModel.Detect(new ComposerDocument([tag, new TextSegment("@x")]), 3));
        Assert.Equal("x", CompletionViewModel.Detect(new ComposerDocument([tag, new TextSegment(" @x")]), 4)!.Text);
    }

    [Fact]
    public async Task Accept_ReplacesQueryWithTag_AndRunsActions()
    {
        await HeadlessApp.Session.Dispatch(async () =>
        {
            var ran = 0;
            var doc = new ComposerDocument();
            var files = new FakeProvider('@', 0, q => [
                Item("ComposerView.cs", new TagSegment(TagKind.File, "src/ComposerView.cs", "ComposerView.cs"), q),
                Item("Readme.md", new TagSegment(TagKind.File, "Readme.md", "Readme.md"), q),
            ]);
            var commands = new FakeProvider('/', 0, q => [new CompletionItem("review", null, LucideKind.SquareTerminal, null, new CompletionAction(Run: () => ran++))]);
            var completion = new CompletionViewModel(doc, [files, commands], NullLogger.Instance);

            doc.Insert(0, "看下 @cv");
            await Settle();
            Assert.True(completion.IsOpen);
            Assert.Equal("ComposerView.cs", Assert.Single(completion.Items).Title);
            Assert.True(completion.AcceptHighlighted());
            Assert.Equal("看下 @src/ComposerView.cs ", doc.ToPlainText());
            Assert.False(completion.IsOpen);

            // / 命令：删掉查询文字后执行
            doc.Insert(doc.Length, "/re");
            await Settle();
            Assert.True(completion.AcceptHighlighted());
            Assert.Equal(1, ran);
            Assert.Equal("看下 @src/ComposerView.cs ", doc.ToPlainText());

            // 空查询：按提供者顺序全部列出，↓ 移动高亮
            doc.Insert(doc.Length, "@");
            await Settle();
            Assert.Equal(2, completion.Items.Count);
            completion.Move(1);
            Assert.True(completion.Items[1].IsHighlighted);
            Assert.False(completion.Items[0].IsHighlighted);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Escape_KeepsClosedForSameQuery_UntilQueryMoves()
    {
        await HeadlessApp.Session.Dispatch(async () =>
        {
            var doc = new ComposerDocument();
            var files = new FakeProvider('@', 0, q => [Item("a.cs", new TagSegment(TagKind.File, "a.cs", "a.cs"), q)]);
            var completion = new CompletionViewModel(doc, [files], NullLogger.Instance);

            doc.Insert(0, "@a");
            await Settle();
            Assert.True(completion.IsOpen);
            completion.Dismiss();
            doc.Insert(2, ".");
            await Settle();
            Assert.False(completion.IsOpen);

            // 删掉查询后重新输入：再次打开
            doc.Delete(0, 3);
            await Settle();
            doc.Insert(0, "@");
            await Settle();
            Assert.True(completion.IsOpen);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SlowProvider_ShowsLoading_AndStaleResultsAreDropped()
    {
        await HeadlessApp.Session.Dispatch(async () =>
        {
            var doc = new ComposerDocument();
            var release = new TaskCompletionSource();
            var slow = new FakeProvider('@', 0, q => [Item("late.cs", new TagSegment(TagKind.File, "late.cs", "late.cs"), q)], release.Task);
            var completion = new CompletionViewModel(doc, [slow], NullLogger.Instance);

            doc.Insert(0, "@l");
            await Settle();
            Assert.True(completion.IsOpen);
            Assert.True(completion.HasEmptyText);
            Assert.Empty(completion.Items);

            // 结果到达前查询已关闭：丢弃
            doc.Delete(0, 2);
            await Settle();
            release.SetResult();
            await Settle();
            Assert.False(completion.IsOpen);
            Assert.Empty(completion.Items);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private static CompletionItem Item(string title, TagSegment tag, string query) =>
        FuzzyMatcher.Match(query, title) is { } m
            ? new CompletionItem(title, null, LucideKind.File, null, new CompletionAction(tag), m.Score, m.Positions)
            : new CompletionItem("", null, LucideKind.File, null, new CompletionAction(), int.MinValue);

    private static async Task Settle()
    {
        for (var i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    private sealed class FakeProvider(char trigger, int order, Func<string, IReadOnlyList<CompletionItem>> items, Task? gate = null) : ICompletionProvider
    {
        public char Trigger => trigger;

        public int Order => order;

        public async ValueTask<IReadOnlyList<CompletionItem>> QueryAsync(string query, CancellationToken cancellationToken)
        {
            if (gate is not null)
            {
                await gate;
            }
            return items(query).Where(i => i.Score != int.MinValue).ToList();
        }
    }
}
