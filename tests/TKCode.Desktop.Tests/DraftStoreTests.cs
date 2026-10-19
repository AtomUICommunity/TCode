using System.Text.Json;
using TKCode.Desktop.Controls.Composer;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>可以手动拨动的时钟。</summary>
internal sealed class FakeClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Value { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Value;
}

/// <summary>草稿落盘（docs/design/composer-drafts.md §4.4）：往返、过期清理、损坏文件、丢失的图片。</summary>
public sealed class DraftStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tkcode-drafts-").FullName;
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero));

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string DraftsDir => Path.Combine(_dir, "drafts");

    private string Image(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    private DraftStore NewStore() => new(DraftsDir, _clock);

    private ComposerDraft Draft(string text, IReadOnlyList<ImageAttachmentViewModel>? images = null, McpPromptSnapshot? mcp = null)
    {
        images ??= [];
        return new ComposerDraft([new TextSegment(text), .. images.Select(i => (Segment)i.Tag), new TagSegment(TagKind.File, "src/a.cs", "a.cs")],
            images, images.Count == 0 ? 1 : images.Max(i => i.Number) + 1, mcp, _clock.Value);
    }

    [Fact]
    public async Task Draft_SurvivesARestart_WithTagsImagesAndMcpPrompt()
    {
        var png = Image("a.png");
        var mcp = new McpPromptSnapshot("srv", "review", null, new Dictionary<string, string> { ["file"] = "a.cs" });
        var store = NewStore();
        store.Set("0123456789abcdef", Draft("看看 ", [new ImageAttachmentViewModel(png, "image/png", 2)], mcp));
        store.Set(DraftStore.NewChatKey, Draft("新对话"));
        await store.FlushAsync();
        Assert.True(File.Exists(Path.Combine(DraftsDir, "0123456789abcdef.json")));

        var restarted = NewStore();
        Assert.Equal(2, await restarted.LoadAsync());
        var draft = restarted.Get("0123456789abcdef")!;
        Assert.Equal("看看 ", ((TextSegment)draft.Segments[0]).Text);
        var image = Assert.Single(draft.Images);
        Assert.Equal((png, "image/png", 2), (image.Path, image.MimeType, image.Number));
        Assert.Equal(3, draft.NextImageNumber);
        Assert.Equal([TagKind.Image, TagKind.File], draft.Segments.OfType<TagSegment>().Select(t => t.Kind));
        Assert.Equal("2", draft.Segments.OfType<TagSegment>().First().Value);
        Assert.Equal(("srv", "review"), (draft.McpPrompt!.Server, draft.McpPrompt.Name));
        Assert.Null(draft.McpPrompt.Prompt);
        Assert.Equal("a.cs", draft.McpPrompt.Arguments["file"]);
        Assert.Equal(_clock.Value, draft.EditedAt);
        Assert.Equal("新对话", ((TextSegment)restarted.Get(DraftStore.NewChatKey)!.Segments[0]).Text);
    }

    [Fact]
    public async Task MissingImageFile_IsDroppedTogetherWithItsTag()
    {
        var kept = Image("kept.png");
        var gone = Image("gone.png");
        var store = NewStore();
        store.Set("k", Draft("x", [new ImageAttachmentViewModel(kept, "image/png", 1), new ImageAttachmentViewModel(gone, "image/png", 2)]));
        await store.FlushAsync();
        File.Delete(gone);

        var restarted = NewStore();
        await restarted.LoadAsync();
        var draft = restarted.Get("k")!;
        Assert.Equal([kept], draft.Images.Select(i => i.Path));
        Assert.Equal(["1", "src/a.cs"], draft.Segments.OfType<TagSegment>().Select(t => t.Value));
    }

    [Fact]
    public async Task EmptyOrRemovedDraft_DeletesTheFile()
    {
        var store = NewStore();
        store.Set("a", Draft("x"));
        store.Set("b", Draft("y"));
        await store.FlushAsync();
        Assert.True(File.Exists(Path.Combine(DraftsDir, "a.json")));

        store.Set("a", new ComposerDraft([], [], 1, null, _clock.Value));
        store.Remove("b");
        await store.FlushAsync();
        Assert.Empty(Directory.GetFiles(DraftsDir, "*.json"));
    }

    [Fact]
    public async Task Load_CleansUpDraftsOlderThanThreeDays_AndSurvivesCorruptFiles()
    {
        var store = NewStore();
        store.Set("old", Draft("旧的"));
        await store.FlushAsync();
        _clock.Value += DraftStore.MaxAge - TimeSpan.FromHours(1);
        store.Set("fresh", Draft("新的") with { EditedAt = _clock.Value });
        var broken = Path.Combine(DraftsDir, "broken.json");
        File.WriteAllText(broken, "{ not json");
        File.SetLastWriteTimeUtc(broken, _clock.Value.UtcDateTime);
        await store.FlushAsync();

        // 再过两小时：old 超过 3 天，fresh 才过了两小时
        _clock.Value += TimeSpan.FromHours(2);
        var restarted = NewStore();
        Assert.Equal(1, await restarted.LoadAsync());
        Assert.Null(restarted.Get("old"));
        Assert.NotNull(restarted.Get("fresh"));
        await restarted.FlushAsync();
        Assert.False(File.Exists(Path.Combine(DraftsDir, "old.json")));
        Assert.True(File.Exists(Path.Combine(DraftsDir, "fresh.json")));
        // 损坏但还没过期的文件留着，下一次保存会覆盖它
        Assert.True(File.Exists(Path.Combine(DraftsDir, "broken.json")));
    }

    [Fact]
    public async Task Get_IgnoresAndDeletesADraftThatExpiredWhileTheAppWasOpen()
    {
        var store = NewStore();
        store.Set("a", Draft("x"));
        await store.FlushAsync();
        _clock.Value += DraftStore.MaxAge + TimeSpan.FromMinutes(1);
        Assert.Null(store.Get("a"));
        await store.FlushAsync();
        Assert.False(File.Exists(Path.Combine(DraftsDir, "a.json")));
    }

    [Fact]
    public async Task LoadDoesNotBringBackAKeyRemovedMeanwhile_NorOverwriteANewerOne()
    {
        var first = NewStore();
        first.Set("gone", Draft("会被删掉"));
        first.Set("newer", Draft("磁盘上的旧内容"));
        await first.FlushAsync();

        var restarted = NewStore();
        restarted.Remove("gone");
        restarted.Set("newer", Draft("内存里的新内容"));
        await restarted.LoadAsync();
        await restarted.FlushAsync();
        Assert.Null(restarted.Get("gone"));
        Assert.Equal("内存里的新内容", ((TextSegment)restarted.Get("newer")!.Segments[0]).Text);
        Assert.False(File.Exists(Path.Combine(DraftsDir, "gone.json")));
    }

    [Fact]
    public async Task UnsafeKeys_AreKeptInMemoryButNeverTouchTheDisk()
    {
        var store = NewStore();
        store.Set("../escape", Draft("x"));
        store.Set("a/b", Draft("y"));
        await store.FlushAsync();
        Assert.NotNull(store.Get("../escape"));
        Assert.False(File.Exists(Path.Combine(_dir, "escape.json")));
        Assert.False(Directory.Exists(DraftsDir) && Directory.GetFiles(DraftsDir, "*", SearchOption.AllDirectories).Length > 0);
    }

    [Fact]
    public async Task FileFormat_IsPlainJson_WithSegmentsAsPromptSegments()
    {
        var store = NewStore();
        store.Set("a", Draft("你好"));
        await store.FlushAsync();
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(DraftsDir, "a.json")));
        Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
        var segments = json.RootElement.GetProperty("segments");
        Assert.Equal("text", segments[0].GetProperty("kind").GetString());
        Assert.Equal("你好", segments[0].GetProperty("text").GetString());
        Assert.Equal("file", segments[1].GetProperty("kind").GetString());
        Assert.Equal("src/a.cs", segments[1].GetProperty("value").GetString());
    }
}
