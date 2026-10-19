using TKCode.Desktop.Controls.Composer;
using TKCode.Desktop.Services;

namespace TKCode.Desktop.ViewModels;

/// <summary>翻历史的一步：要显示的历史消息，或浏览结束时要恢复的草稿。</summary>
public readonly record struct HistoryStep(UserMessageItemViewModel? Entry, ComposerDraft? Restore);

/// <summary>
/// 输入框里 ↑ ↓ 翻当前会话发送过的输入（docs/design/composer-drafts.md §5）。
/// 历史就是会话里的用户消息，不另存；这个类只管“正在浏览第几条”和浏览前的草稿（<see cref="Stash"/>），
/// 不依赖界面，可以直接单元测试。
/// </summary>
public sealed class InputHistory
{
    private int _index = -1;
    private UserMessageItemViewModel? _current;

    /// <summary>正在浏览历史：输入框里显示的是历史消息，不是用户的草稿。</summary>
    public bool IsBrowsing => _index >= 0;

    /// <summary>开始浏览时输入框里的草稿；浏览中切换会话、落盘保存的是它，不是翻出来的历史消息。</summary>
    public ComposerDraft? Stash { get; private set; }

    /// <summary>
    /// ↑ 是否应该翻到更早的一条：没有选区、不在输入法预编辑中，并且——
    /// 还没开始浏览时光标必须在文档开头（光标在中间时第一下 ↑ 只是把光标移到开头，不会因为误按顶掉正在写的内容）；
    /// 已经在浏览时（草稿已暂存）光标在第一行就行。
    /// </summary>
    public bool ShouldRecallOlder(int caret, bool onFirstLine, bool hasSelection, bool hasPreedit) =>
        !hasSelection && !hasPreedit && (IsBrowsing ? onFirstLine : caret == 0);

    /// <summary>↓ 是否应该翻到更新的一条：正在浏览，光标在最后一行，没有选区，也不在输入法预编辑中。</summary>
    public bool ShouldRecallNewer(bool onLastLine, bool hasSelection, bool hasPreedit) =>
        IsBrowsing && onLastLine && !hasSelection && !hasPreedit;

    /// <summary>结束浏览并丢掉暂存的草稿（用户编辑、发送、切换会话）。</summary>
    public void End()
    {
        _index = -1;
        _current = null;
        Stash = null;
    }

    /// <summary>
    /// 往更早的一条走。<paramref name="entries"/> 由新到旧。没有更早的就返回 null、状态不变。
    /// 第一步先用 <paramref name="capture"/> 取下当前草稿暂存。
    /// </summary>
    public HistoryStep? Older(IReadOnlyList<UserMessageItemViewModel> entries, Func<ComposerDraft> capture)
    {
        Align(entries);
        var next = _index + 1;
        if (next >= entries.Count)
        {
            return null;
        }
        if (!IsBrowsing)
        {
            Stash = capture();
        }
        _index = next;
        _current = entries[next];
        return new HistoryStep(_current, null);
    }

    /// <summary>往更新的一条走；越过最新一条时恢复浏览前的草稿并结束浏览。没有在浏览时返回 null。</summary>
    public HistoryStep? Newer(IReadOnlyList<UserMessageItemViewModel> entries)
    {
        if (!IsBrowsing)
        {
            return null;
        }
        Align(entries);
        if (_index <= 0)
        {
            var stash = Stash;
            End();
            return new HistoryStep(null, stash);
        }
        _index--;
        _current = entries[Math.Min(_index, entries.Count - 1)];
        return new HistoryStep(_current, null);
    }

    /// <summary>
    /// 浏览期间列表变了：排队的消息自动发出（新条目出现在最新的一端），或翻页加载了更早的（出现在最旧的一端）。
    /// 按正在显示的那一条重新定位，位置才不会错。
    /// </summary>
    private void Align(IReadOnlyList<UserMessageItemViewModel> entries)
    {
        if (!IsBrowsing)
        {
            return;
        }
        var found = _current is null ? -1 : IndexOf(entries, _current);
        _index = found >= 0 ? found : Math.Clamp(_index, 0, Math.Max(0, entries.Count - 1));
    }

    private static int IndexOf(IReadOnlyList<UserMessageItemViewModel> entries, UserMessageItemViewModel entry)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            if (ReferenceEquals(entries[i], entry))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// 会话里可以翻出来的输入，由新到旧：连续相同的只算一条，空消息不算。
    /// 只取已经加载的部分，更早的由 <see cref="ConversationViewModel"/> 翻页加载。
    /// </summary>
    public static List<UserMessageItemViewModel> Sources(IEnumerable<TranscriptItemViewModel> items)
    {
        var result = new List<UserMessageItemViewModel>();
        foreach (var message in items.OfType<UserMessageItemViewModel>().Reverse())
        {
            if (message.Text.Length == 0 && !message.HasSegments && !message.HasImages)
            {
                continue;
            }
            if (result.Count > 0 && Same(result[^1], message))
            {
                continue;
            }
            result.Add(message);
        }
        return result;
    }

    private static bool Same(UserMessageItemViewModel a, UserMessageItemViewModel b) =>
        a.Text == b.Text && a.Images.Select(i => i.Path).SequenceEqual(b.Images.Select(i => i.Path));

    /// <summary>
    /// 把已发送的消息还原成可以编辑的草稿：有 tag 的消息用发送时的片段，其余用显示文字（图片占位符换成 Image tag）。
    /// 图片按它们在文字里出现的顺序对应 <see cref="UserMessageItemViewModel.Images"/> 并重新编号为 1..n
    /// （片段里保留的是发送时的原始编号，消息里的图片是按顺序编号的，不能直接按编号对应）；
    /// 没有出现在文字里的图片追加在末尾；文件已经不存在的图片丢掉，连同 tag。
    /// 消息里没有 MCP 提示词的信息，所以也不会恢复提示词卡片。
    /// </summary>
    public static ComposerDraft ToDraft(UserMessageItemViewModel message, DateTimeOffset editedAt, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var segments = new List<Segment>();
        var images = new List<ImageAttachmentViewModel>();
        var next = 0;

        void AddImage()
        {
            var index = next++;
            if (index >= message.Images.Count || !exists(message.Images[index].Path))
            {
                return;
            }
            var source = message.Images[index];
            var image = new ImageAttachmentViewModel(source.Path, AttachmentStore.MimeTypeOf(source.Path) ?? "image/png", images.Count + 1);
            images.Add(image);
            segments.Add(image.Tag);
        }

        if (message.Segments is { } document)
        {
            foreach (var segment in document.Segments)
            {
                if (segment is TagSegment { Kind: TagKind.Image })
                {
                    AddImage();
                }
                else
                {
                    segments.Add(segment);
                }
            }
        }
        else
        {
            foreach (var (text, number) in ImagePrompt.SplitPlaceholders(message.DisplayText))
            {
                if (number is not null)
                {
                    AddImage();
                }
                else
                {
                    segments.Add(new TextSegment(text!));
                }
            }
        }
        // 没有在文字里出现的图片（删掉了占位符、附件还在）：追加在末尾
        while (next < message.Images.Count)
        {
            if (segments.Count > 0 && segments[^1] is TextSegment { Text: var tail } && tail.Length > 0 && !char.IsWhiteSpace(tail[^1]))
            {
                segments.Add(new TextSegment(" "));
            }
            AddImage();
        }
        return new ComposerDraft(segments, images, images.Count + 1, null, editedAt);
    }
}
