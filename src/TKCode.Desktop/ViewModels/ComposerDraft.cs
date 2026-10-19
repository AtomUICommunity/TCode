using System.Globalization;
using TKCode.Desktop.Controls.Composer;
using TKCode.Rpc;

namespace TKCode.Desktop.ViewModels;

/// <summary>
/// 选中的 MCP 提示词与已填的参数值。<see cref="Prompt"/> 为 null 表示只知道名字（从磁盘读出的草稿），
/// 需要等 MCP 目录加载后再解析（<see cref="McpComposerViewModel.Apply"/>）。
/// </summary>
public sealed record McpPromptSnapshot(string Server, string Name, McpPromptDto? Prompt, IReadOnlyDictionary<string, string> Arguments);

/// <summary>
/// 输入框里属于“某一个会话”的全部状态（docs/design/composer-drafts.md §4.1）：文字与 tag、图片附件、
/// 下一张图片的编号、MCP 提示词。不可变；光标与撤销记录不保留。
/// </summary>
/// <param name="EditedAt">最后一次编辑的时间：草稿从这里起最多保留 <see cref="Services.DraftStore.MaxAge"/>。</param>
public sealed record ComposerDraft(
    IReadOnlyList<Segment> Segments,
    IReadOnlyList<ImageAttachmentViewModel> Images,
    int NextImageNumber,
    McpPromptSnapshot? McpPrompt,
    DateTimeOffset EditedAt)
{
    /// <summary>没有文字 / tag、没有图片、没有 MCP 提示词：不需要保存。</summary>
    public bool IsEmpty => Segments.All(s => s.Length == 0) && Images.Count == 0 && McpPrompt is null;

    /// <summary>
    /// 发送失败要把内容放回去，但用户在等待期间又在同一个输入框里写了新的内容：失败的内容放在前面，空一行接上现有内容。
    /// 现有内容里的图片编号已被占用，失败内容里的图片接在后面重新编号（包括文字里对应的 Image tag）。
    /// </summary>
    public static ComposerDraft Merge(ComposerDraft failed, ComposerDraft existing)
    {
        var next = existing.NextImageNumber;
        var images = new List<ImageAttachmentViewModel>(existing.Images);
        var tags = new Dictionary<string, TagSegment>(StringComparer.Ordinal);
        foreach (var image in failed.Images)
        {
            var renumbered = new ImageAttachmentViewModel(image.Path, image.MimeType, next++);
            tags[image.Number.ToString(CultureInfo.InvariantCulture)] = renumbered.Tag;
            images.Add(renumbered);
        }
        var segments = new List<Segment>();
        foreach (var segment in failed.Segments)
        {
            segments.Add(segment is TagSegment { Kind: TagKind.Image } tag && tags.TryGetValue(tag.Value, out var replacement) ? replacement : segment);
        }
        var existingHasContent = existing.Segments.Any(s => s.Length > 0);
        if (existingHasContent && segments.Count > 0)
        {
            segments.Add(new TextSegment("\n\n"));
        }
        segments.AddRange(existing.Segments);
        return new ComposerDraft(segments, images, next, existing.McpPrompt ?? failed.McpPrompt,
            failed.EditedAt > existing.EditedAt ? failed.EditedAt : existing.EditedAt);
    }
}
