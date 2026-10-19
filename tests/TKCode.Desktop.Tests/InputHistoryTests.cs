using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using TKCode.Desktop.Controls.Composer;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using TKCode.Rpc;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>输入框里 ↑ ↓ 翻当前会话发送过的输入（docs/design/composer-drafts.md §5）。</summary>
public sealed class InputHistoryTests
{
    private static UserMessageItemViewModel Message(string text, string[]? images = null, PromptSegmentDto[]? segments = null) =>
        new(text, images, segments);

    private static ComposerDraft Draft(string text) =>
        new([new TextSegment(text)], [], 1, null, DateTimeOffset.UtcNow);

    private static string TextOf(ComposerDraft draft) => ComposerDocument.PlainText(draft.Segments);

    [Fact]
    public void Sources_AreNewestFirst_WithoutAdjacentDuplicatesOrEmptyMessages()
    {
        TranscriptItemViewModel[] items =
        [
            Message("一"), Message("二"), Message("二"), Message(""), new AssistantMessageItemViewModel("a1", DateTimeOffset.UtcNow), Message("三"), Message("二"),
        ];
        Assert.Equal(["二", "三", "二", "一"], InputHistory.Sources(items).Select(m => m.Text));
    }

    [Fact]
    public void WalkingBackAndForth_RestoresTheStashedDraft_AndStopsAtTheOldestEntry()
    {
        var entries = InputHistory.Sources([Message("一"), Message("二"), Message("三")]);
        var history = new InputHistory();
        Assert.Null(history.Newer(entries));
        var live = Draft("写了一半");

        Assert.Equal("三", history.Older(entries, () => live)!.Value.Entry!.Text);
        Assert.True(history.IsBrowsing);
        Assert.Same(live, history.Stash);
        Assert.Equal("二", history.Older(entries, () => throw new InvalidOperationException("只在开始浏览时暂存"))!.Value.Entry!.Text);
        Assert.Equal("一", history.Older(entries, () => live)!.Value.Entry!.Text);
        Assert.Null(history.Older(entries, () => live));
        Assert.True(history.IsBrowsing);

        Assert.Equal("二", history.Newer(entries)!.Value.Entry!.Text);
        Assert.Equal("三", history.Newer(entries)!.Value.Entry!.Text);
        var back = history.Newer(entries)!.Value;
        Assert.Null(back.Entry);
        Assert.Same(live, back.Restore);
        Assert.False(history.IsBrowsing);
        Assert.Null(history.Stash);
    }

    [Fact]
    public void NewMessagesArrivingWhileBrowsing_DoNotMoveTheCursorOffItsEntry()
    {
        var items = new List<TranscriptItemViewModel> { Message("一"), Message("二") };
        var history = new InputHistory();
        Assert.Equal("二", history.Older(InputHistory.Sources(items), () => Draft(""))!.Value.Entry!.Text);
        Assert.Equal("一", history.Older(InputHistory.Sources(items), () => Draft(""))!.Value.Entry!.Text);

        // 排队的消息自动发出：新消息出现在最新的一端，当前仍然是“一”，再往更新走是“二”
        items.Add(Message("三"));
        Assert.Equal("二", history.Newer(InputHistory.Sources(items))!.Value.Entry!.Text);
        Assert.Equal("三", history.Newer(InputHistory.Sources(items))!.Value.Entry!.Text);
    }

    [Fact]
    public void EarlierMessagesLoadedWhileBrowsing_DoNotMoveTheCursorOffItsEntry()
    {
        var items = new List<TranscriptItemViewModel> { Message("三") };
        var history = new InputHistory();
        Assert.Equal("三", history.Older(InputHistory.Sources(items), () => Draft(""))!.Value.Entry!.Text);
        Assert.Null(history.Older(InputHistory.Sources(items), () => Draft("")));

        // 翻页加载了更早的两条：接着往前翻是“二”，再往前是“一”，往回是“三”
        items.Insert(0, Message("二"));
        items.Insert(0, Message("一"));
        Assert.Equal("二", history.Older(InputHistory.Sources(items), () => Draft(""))!.Value.Entry!.Text);
        Assert.Equal("一", history.Older(InputHistory.Sources(items), () => Draft(""))!.Value.Entry!.Text);
        Assert.Equal("二", history.Newer(InputHistory.Sources(items))!.Value.Entry!.Text);
    }

    [Fact]
    public void End_DropsTheStash()
    {
        var entries = InputHistory.Sources([Message("一")]);
        var history = new InputHistory();
        history.Older(entries, () => Draft("草稿"));
        history.End();
        Assert.False(history.IsBrowsing);
        Assert.Null(history.Stash);
        Assert.Null(history.Newer(entries));
    }

    [Theory]
    [InlineData(0, true, false, false, true)]
    [InlineData(3, true, false, false, false)] // 还没开始浏览、光标在中间：先让编辑器把光标移到开头
    [InlineData(0, true, true, false, false)] // 有选区
    [InlineData(0, true, false, true, false)] // 输入法预编辑中
    public void RecallOlder_BeforeBrowsing_NeedsTheCaretAtTheStart(int caret, bool firstLine, bool selection, bool preedit, bool expected) =>
        Assert.Equal(expected, new InputHistory().ShouldRecallOlder(caret, firstLine, selection, preedit));

    [Fact]
    public void WhileBrowsing_UpAndDownOnlyNeedTheFirstAndLastLine()
    {
        var history = new InputHistory();
        Assert.False(history.ShouldRecallNewer(true, false, false));
        history.Older(InputHistory.Sources([Message("一"), Message("二")]), () => Draft(""));

        // 草稿已经暂存：光标在第一行（不必在开头）就能继续往前翻
        Assert.True(history.ShouldRecallOlder(4, true, false, false));
        Assert.False(history.ShouldRecallOlder(4, false, false, false));
        Assert.True(history.ShouldRecallNewer(true, false, false));
        Assert.False(history.ShouldRecallNewer(false, false, false));
        Assert.False(history.ShouldRecallNewer(true, true, false));
        Assert.False(history.ShouldRecallNewer(true, false, true));
    }

    [Fact]
    public void ToDraft_KeepsTagsFromTheSentSegments()
    {
        var message = Message("展开后的正文", segments:
        [
            new("command", Value: "review", Label: "review"),
            new("text", " 看下 "),
            new("file", Value: "src/a.cs", Label: "a.cs"),
        ]);
        var draft = InputHistory.ToDraft(message, DateTimeOffset.UtcNow);
        Assert.Equal([TagKind.Command, TagKind.File], draft.Segments.OfType<TagSegment>().Select(t => t.Kind));
        Assert.Equal(" 看下 ", draft.Segments.OfType<TextSegment>().Single().Text);
        Assert.Empty(draft.Images);
        Assert.Equal(1, draft.NextImageNumber);
    }

    [Fact]
    public void ToDraft_MapsImagesByOrderOfAppearance_AndRenumbers()
    {
        // 发送时图片 #2、#5（中间的被删过）；消息里的图片按顺序编号为 1、2
        var message = Message("比较 ", ["/tmp/x.png", "/tmp/y.png"], segments:
        [
            new("text", "比较 "),
            new("image", Value: "2", Label: "图片 #2"),
            new("text", " 和 "),
            new("image", Value: "5", Label: "图片 #5"),
        ]);
        var draft = InputHistory.ToDraft(message, DateTimeOffset.UtcNow, _ => true);
        Assert.Equal(["/tmp/x.png", "/tmp/y.png"], draft.Images.Select(i => i.Path));
        Assert.Equal([1, 2], draft.Images.Select(i => i.Number));
        Assert.Equal(["1", "2"], draft.Segments.OfType<TagSegment>().Select(t => t.Value));
        Assert.Equal(3, draft.NextImageNumber);
        Assert.All(draft.Images, i => Assert.Equal("image/png", i.MimeType));
    }

    [Fact]
    public void ToDraft_DropsMissingImagesWithTheirTags()
    {
        var message = Message("看 ", ["/tmp/gone.png", "/tmp/kept.png"], segments:
        [
            new("text", "看 "),
            new("image", Value: "1", Label: "图片 #1"),
            new("image", Value: "2", Label: "图片 #2"),
        ]);
        var draft = InputHistory.ToDraft(message, DateTimeOffset.UtcNow, path => path.EndsWith("kept.png"));
        Assert.Equal(["/tmp/kept.png"], draft.Images.Select(i => i.Path));
        Assert.Equal([1], draft.Images.Select(i => i.Number));
        Assert.Equal(["1"], draft.Segments.OfType<TagSegment>().Select(t => t.Value));
    }

    [Fact]
    public void ToDraft_WithoutSegments_TurnsPlaceholdersIntoTags_AndAppendsUnreferencedImages()
    {
        var message = Message("对比 [图片 #1] 即可", ["/tmp/a.png", "/tmp/b.png"]);
        var draft = InputHistory.ToDraft(message, DateTimeOffset.UtcNow, _ => true);
        Assert.Equal(["/tmp/a.png", "/tmp/b.png"], draft.Images.Select(i => i.Path));
        Assert.Equal([1, 2], draft.Images.Select(i => i.Number));
        // 文字里的占位符成为第 1 个 tag；没有出现在文字里的第二张追加在末尾（前面补一个空格）
        Assert.Equal(["对比 ", " 即可", " "], draft.Segments.OfType<TextSegment>().Select(t => t.Text));
        Assert.Equal(["1", "2"], draft.Segments.OfType<TagSegment>().Select(t => t.Value));
        Assert.IsType<TagSegment>(draft.Segments[^1]);
    }

    [Fact]
    public async Task UpAndDown_BrowseTheSessionsSentMessages_WithTheStashedDraftRestored()
    {
        await using var app = await TestApp.StartAsync();
        await HeadlessApp.Session.Dispatch(async () =>
        {
            var conversation = app.Conversation;
            var vm = app.Services.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
            window.Show();
            await app.WaitUntil(() => conversation.Picker.Selected is not null, "models");
            foreach (var text in new[] { "第一条", "第二条", "第三条" })
            {
                await app.Send(text);
                var session = conversation.Active!;
                await app.WaitUntil(() => !session.IsRunning && session.Items.OfType<UserMessageItemViewModel>().Any(u => u.Text == text), text);
            }
            var editor = window.FindControl<ComposerView>("DockedComposer")!.FindControl<ComposerEditor>("Input")!;
            editor.Focus();

            // 空输入框：↑ 一路翻到最早，再按不动；↓ 一路回来，最后是原来的（空）草稿
            Press(window, Key.Up);
            Assert.Equal("第三条", conversation.ComposerText);
            Assert.Equal(0, editor.CaretIndex);
            Press(window, Key.Up);
            Press(window, Key.Up);
            Assert.Equal("第一条", conversation.ComposerText);
            Press(window, Key.Up);
            Assert.Equal("第一条", conversation.ComposerText);
            Press(window, Key.Down);
            Assert.Equal("第二条", conversation.ComposerText);
            Assert.Equal(editor.Document.Length, editor.CaretIndex);
            Press(window, Key.Down);
            Assert.Equal("第三条", conversation.ComposerText);
            Press(window, Key.Down);
            Assert.Equal("", conversation.ComposerText);
            Assert.False(conversation.History.IsBrowsing);

            // 有草稿：光标不在开头时第一次 ↑ 只是把光标移到开头，第二次才翻历史；↓ 回到暂存的草稿
            conversation.ComposerText = "写了一半";
            editor.CaretIndex = editor.Document.Length;
            Press(window, Key.Up);
            Assert.Equal("写了一半", conversation.ComposerText);
            Assert.Equal(0, editor.CaretIndex);
            Press(window, Key.Up);
            Assert.Equal("第三条", conversation.ComposerText);
            Press(window, Key.Down);
            Assert.Equal("写了一半", conversation.ComposerText);
            Assert.False(conversation.History.IsBrowsing);

            // 编辑了翻出来的消息：不再是浏览，↓ 不再动它
            editor.CaretIndex = 0;
            Press(window, Key.Up);
            Assert.Equal("第三条", conversation.ComposerText);
            window.KeyTextInput("改");
            Assert.False(conversation.History.IsBrowsing);
            Assert.Equal("改第三条", conversation.ComposerText);
            Press(window, Key.Down);
            Assert.Equal("改第三条", conversation.ComposerText);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SwitchingSessionWhileBrowsing_SavesTheRealDraft_NotTheRecalledMessage()
    {
        await using var app = await TestApp.StartAsync();
        await HeadlessApp.Session.Dispatch(async () =>
        {
            var conversation = app.Conversation;
            await app.WaitUntil(() => conversation.Picker.Selected is not null, "models");
            await app.Send("发过的消息");
            var a = conversation.Active!;
            await app.WaitUntil(() => !a.IsRunning && a.Items.OfType<UserMessageItemViewModel>().Any(), "turn");

            conversation.ComposerText = "A 的草稿";
            Assert.True(conversation.RecallOlder());
            Assert.Equal("发过的消息", conversation.ComposerText);

            conversation.NewChat();
            Assert.False(conversation.History.IsBrowsing);
            Assert.Equal("A 的草稿", TextOf(app.Drafts.Get(a.SessionId)!));
            await conversation.OpenSessionCommand.ExecuteAsync(conversation.Sessions.Single(s => s.SessionId == a.SessionId));
            Assert.Equal("A 的草稿", conversation.ComposerText);
            Assert.False(conversation.History.IsBrowsing);

            // 浏览中落盘（退出）：保存的也是真正的草稿
            Assert.True(conversation.RecallOlder());
            conversation.FlushDraft();
            await app.Drafts.FlushAsync();
            Assert.Equal("A 的草稿", TextOf(app.Drafts.Get(a.SessionId)!));
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Up_AtTheOldestLoadedMessage_LoadsEarlierHistoryAndKeepsGoing()
    {
        string home;
        string sessionId;
        const int total = 22;
        await using (var first = await TestApp.StartAsync())
        {
            home = first.Home;
            await HeadlessApp.Session.Dispatch(async () =>
            {
                var conversation = first.Conversation;
                await first.WaitUntil(() => conversation.Picker.Selected is not null, "models");
                for (var i = 1; i <= total; i++)
                {
                    await first.Send($"消息 {i:00}");
                    var session = conversation.Active!;
                    await first.WaitUntil(() => !session.IsRunning && session.Items.OfType<UserMessageItemViewModel>().Count() == i, $"turn {i}", 30_000);
                }
                return true;
            }, TestContext.Current.CancellationToken);
            sessionId = first.Conversation.Sessions.Single().SessionId;
        }

        // 重启后打开：只加载最近 20 轮，更早的 2 轮要翻页
        await using var second = await TestApp.StartAsync(home);
        await HeadlessApp.Session.Dispatch(async () =>
        {
            var conversation = second.Conversation;
            await second.WaitUntil(() => conversation.Sessions.Any(s => s.SessionId == sessionId), "sessions");
            await conversation.OpenSessionCommand.ExecuteAsync(conversation.Sessions.Single(s => s.SessionId == sessionId));
            var session = conversation.Active!;
            Assert.Equal(20, session.Items.OfType<UserMessageItemViewModel>().Count());
            Assert.True(session.HasEarlierHistory);

            for (var i = 0; i < 20; i++)
            {
                Assert.True(conversation.RecallOlder());
            }
            Assert.Equal("消息 03", conversation.ComposerText);
            // 已加载的翻完了：这一下触发翻页，加载完成后自动继续
            Assert.True(conversation.RecallOlder());
            await second.WaitUntil(() => conversation.ComposerText == "消息 02", "earlier page");
            Assert.False(session.HasEarlierHistory);
            Assert.True(conversation.RecallOlder());
            Assert.Equal("消息 01", conversation.ComposerText);
            Assert.False(conversation.RecallOlder());
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private static void Press(Window window, Key key)
    {
        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }
}
