using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TKCode.Desktop.Controls.Composer;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using TKCode.Rpc;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>
/// 输入框草稿按会话保存（docs/design/composer-drafts.md §4）：模型本身，以及经由真实 Host 的会话切换。
/// </summary>
public sealed class SessionDraftTests
{
    private static ComposerDraft Draft(string text, DateTimeOffset at, params ImageAttachmentViewModel[] images) =>
        new([new TextSegment(text), .. images.Select(i => (Segment)i.Tag)], images, images.Length == 0 ? 1 : images.Max(i => i.Number) + 1, null, at);

    [Fact]
    public void DraftStore_RemovesEmptyAndExpiredDrafts()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero));
        var store = new DraftStore(null, clock);
        store.Set("a", Draft("hello", clock.Value));
        Assert.NotNull(store.Get("a"));

        // 最后一次编辑起最多 3 天：刚好 3 天还在，再多一点就没了
        clock.Value += DraftStore.MaxAge;
        Assert.NotNull(store.Get("a"));
        clock.Value += TimeSpan.FromMinutes(1);
        Assert.Null(store.Get("a"));

        // 空草稿等于删除
        store.Set("b", Draft("x", clock.Value));
        store.Set("b", Draft("", clock.Value));
        Assert.Null(store.Get("b"));
        store.Set("c", Draft("y", clock.Value));
        store.Remove("c");
        Assert.Null(store.Get("c"));
    }

    [Fact]
    public void Merge_PutsFailedContentFirst_AndRenumbersItsImages()
    {
        var now = DateTimeOffset.UtcNow;
        var failed = Draft("失败的 ", now, new ImageAttachmentViewModel("/tmp/f1.png", "image/png", 1), new ImageAttachmentViewModel("/tmp/f2.png", "image/png", 2));
        var existing = Draft("新写的 ", now.AddSeconds(5), new ImageAttachmentViewModel("/tmp/e1.png", "image/png", 1));

        var merged = ComposerDraft.Merge(failed, existing);

        // 现有图片编号保持不变，失败的图片接在后面：1 | 2、3
        Assert.Equal([1, 2, 3], merged.Images.Select(i => i.Number));
        Assert.Equal(["/tmp/e1.png", "/tmp/f1.png", "/tmp/f2.png"], merged.Images.Select(i => i.Path));
        Assert.Equal(4, merged.NextImageNumber);
        Assert.Equal(existing.EditedAt, merged.EditedAt);
        // 文字：失败的在前（它的 Image tag 跟着改号），空一行，再接现有内容
        Assert.Equal(["2", "3", "1"], merged.Segments.OfType<TagSegment>().Select(t => t.Value));
        Assert.Equal(["失败的 ", "\n\n", "新写的 "], merged.Segments.OfType<TextSegment>().Select(t => t.Text));
        Assert.Equal(["Image", "Image", "Image"], merged.Segments.OfType<TagSegment>().Select(t => t.Kind.ToString()));
    }

    [Fact]
    public void Merge_WithEmptyExisting_DoesNotAddASeparator()
    {
        var now = DateTimeOffset.UtcNow;
        var merged = ComposerDraft.Merge(Draft("abc", now), new ComposerDraft([], [], 1, null, default));
        Assert.Equal("abc", ComposerDocument.PlainText(merged.Segments));
    }

    [Fact]
    public async Task SwitchingSessions_SwapsTheWholeDraft()
    {
        await using var app = await TestApp.StartAsync();
        await HeadlessApp.Session.Dispatch(async () =>
        {
            var conversation = app.Conversation;
            var drafts = app.Drafts;
            await app.WaitUntil(() => conversation.Picker.Selected is not null, "models");

            // 在空状态发送，创建会话 A；发送清掉了“新对话”的草稿
            await app.Send("第一个会话");
            var a = conversation.Active!;
            await app.WaitUntil(() => a is { IsRunning: false, Items.Count: >= 2 }, "turn A");
            Assert.Equal("", conversation.ComposerText);

            // A 里写了一半，带一张图片
            conversation.ComposerText = "A 里没写完的话 ";
            var image = conversation.AddAttachment("/tmp/tkcode-a.png", "image/png");
            conversation.Composer.InsertTag(conversation.Composer.Length, image.Tag);
            Assert.Equal(2, conversation.AddAttachment("/tmp/tkcode-a2.png", "image/png").Number);
            conversation.RemoveAttachmentCommand.Execute(conversation.Attachments[1]);

            // 新对话：看不到 A 的草稿、附件
            conversation.NewChat();
            Assert.Null(conversation.Active);
            Assert.Equal("", conversation.ComposerText);
            Assert.Empty(conversation.Attachments);
            conversation.ComposerText = "新对话的草稿";

            // 回到 A：文字、tag、附件都在，下一张图片的编号接着往后
            await conversation.OpenSessionCommand.ExecuteAsync(conversation.Sessions.Single(s => s.SessionId == a.SessionId));
            Assert.Same(a, conversation.Active);
            Assert.StartsWith("A 里没写完的话 ", conversation.ComposerText);
            Assert.Single(conversation.Attachments);
            Assert.Equal("/tmp/tkcode-a.png", conversation.Attachments[0].Path);
            Assert.Equal(3, conversation.AddAttachment("/tmp/tkcode-a3.png", "image/png").Number);
            conversation.RemoveAttachmentCommand.Execute(conversation.Attachments[1]);

            // 新对话的草稿还在；用它创建会话 B，B 与 A 的草稿互不影响
            conversation.NewChat();
            Assert.Equal("新对话的草稿", conversation.ComposerText);
            Assert.Empty(conversation.Attachments);
            await app.Send("新对话的草稿");
            var b = conversation.Active!;
            Assert.NotSame(a, b);
            await app.WaitUntil(() => b is { IsRunning: false, Items.Count: >= 2 }, "turn B");
            Assert.Equal("", conversation.ComposerText);
            Assert.Empty(conversation.Attachments);
            Assert.Null(drafts.Get(DraftStore.NewChatKey));

            conversation.ComposerText = "B 的草稿";
            await conversation.OpenSessionCommand.ExecuteAsync(conversation.Sessions.Single(s => s.SessionId == a.SessionId));
            Assert.StartsWith("A 里没写完的话 ", conversation.ComposerText);
            await conversation.OpenSessionCommand.ExecuteAsync(conversation.Sessions.Single(s => s.SessionId == b.SessionId));
            Assert.Equal("B 的草稿", conversation.ComposerText);
            Assert.Empty(conversation.Attachments);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FailedSend_ReturnsToTheSessionItBelongedTo_AndDeletingASessionDropsItsDraft()
    {
        await using var app = await TestApp.StartAsync();
        await HeadlessApp.Session.Dispatch(async () =>
        {
            var conversation = app.Conversation;
            var drafts = app.Drafts;
            await app.WaitUntil(() => conversation.Picker.Selected is not null, "models");
            await app.Send("A");
            var a = conversation.Active!;
            await app.WaitUntil(() => a is { IsRunning: false, Items.Count: >= 2 }, "turn A");
            conversation.NewChat();
            await app.Send("B");
            var b = conversation.Active!;
            await app.WaitUntil(() => b is { IsRunning: false, Items.Count: >= 2 }, "turn B");

            // 在 B 里，A 的发送失败了：内容回到 A 的草稿，B 的输入框不受影响
            conversation.ComposerText = "B 正在写";
            conversation.RestoreFailedDraft(a.SessionId, Draft("A 失败的消息", DateTimeOffset.UtcNow));
            Assert.Equal("B 正在写", conversation.ComposerText);
            await conversation.OpenSessionCommand.ExecuteAsync(conversation.Sessions.Single(s => s.SessionId == a.SessionId));
            Assert.Equal("A 失败的消息", conversation.ComposerText);

            // 失败时输入框里已经有新写的内容：失败的在前，空一行接上
            conversation.ComposerText = "又写了一点";
            conversation.RestoreFailedDraft(a.SessionId, Draft("再次失败", DateTimeOffset.UtcNow));
            Assert.Equal("再次失败\n\n又写了一点", conversation.ComposerText);

            // 删除会话：它的草稿一并丢掉（当前会话被删时先切走，再删）
            conversation.ConfirmDangerAsync = (_, _) => Task.FromResult(true);
            conversation.ComposerText = "要被删掉的草稿";
            var itemA = conversation.Sessions.Single(s => s.SessionId == a.SessionId);
            await conversation.DeleteSessionAsync(itemA);
            await app.WaitUntil(() => conversation.Sessions.All(s => s.SessionId != a.SessionId), "delete A");
            Assert.Null(drafts.Get(a.SessionId));
            Assert.Null(conversation.Active);
            Assert.Equal("", conversation.ComposerText);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Drafts_AreRestoredAfterRestartingTheApp()
    {
        string home;
        string sessionId;
        var png = Path.Combine(Directory.CreateTempSubdirectory("tkcode-img-").FullName, "p.png");
        await File.WriteAllBytesAsync(png, [1, 2, 3], TestContext.Current.CancellationToken);
        await using (var first = await TestApp.StartAsync())
        {
            home = first.Home;
            await HeadlessApp.Session.Dispatch(async () =>
            {
                var conversation = first.Conversation;
                await first.WaitUntil(() => conversation.Picker.Selected is not null, "models");
                await first.Send("会话");
                var a = conversation.Active!;
                sessionId = a.SessionId;
                await first.WaitUntil(() => a is { IsRunning: false, Items.Count: >= 2 }, "turn");
                conversation.ComposerText = "重启前没发的话 ";
                var image = conversation.AddAttachment(png, "image/png");
                conversation.Composer.InsertTag(conversation.Composer.Length, image.Tag);
                conversation.NewChat();
                conversation.ComposerText = "新对话里没发的话";
                // 退出时的落盘：不等防抖
                conversation.FlushDraft();
                await first.Drafts.FlushAsync();
                return true;
            }, TestContext.Current.CancellationToken);
            sessionId = first.Conversation.Sessions.Single().SessionId;
        }

        await using var second = await TestApp.StartAsync(home);
        await HeadlessApp.Session.Dispatch(async () =>
        {
            var conversation = second.Conversation;
            // 启动后草稿在后台读入：新对话的草稿先出现，会话的在打开时出现
            await second.WaitUntil(() => conversation.ComposerText == "新对话里没发的话", "new-chat draft");
            await second.WaitUntil(() => conversation.Sessions.Any(s => s.SessionId == sessionId), "sessions");
            await conversation.OpenSessionCommand.ExecuteAsync(conversation.Sessions.Single(s => s.SessionId == sessionId));
            Assert.StartsWith("重启前没发的话 ", conversation.ComposerText);
            var restored = Assert.Single(conversation.Attachments);
            Assert.Equal(png, restored.Path);
            Assert.Contains(conversation.Composer.Segments, s => s is TagSegment { Kind: TagKind.Image });
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SwitchingToADraftEndingInAMention_DoesNotOpenCompletion()
    {
        await using var app = await TestApp.StartAsync();
        await HeadlessApp.Session.Dispatch(async () =>
        {
            var conversation = app.Conversation;
            await app.WaitUntil(() => conversation.Picker.Selected is not null, "models");
            await app.Send("A");
            var a = conversation.Active!;
            await app.WaitUntil(() => a is { IsRunning: false, Items.Count: >= 2 }, "turn A");

            conversation.ComposerText = "看一下 @src";
            conversation.NewChat();
            await conversation.OpenSessionCommand.ExecuteAsync(conversation.Sessions.Single(s => s.SessionId == a.SessionId));
            Assert.Equal("看一下 @src", conversation.ComposerText);
            await app.Settle();
            conversation.Completion.Refresh();
            Assert.False(conversation.Completion.IsOpen);
            return true;
        }, TestContext.Current.CancellationToken);
    }
}
