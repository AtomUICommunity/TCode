using TKCode.Rpc;

namespace TKCode.Desktop.Controls.Composer;

/// <summary>输入框片段与 RPC / 会话记录中的 <see cref="PromptSegmentDto"/> 互转（docs/design/composer.md §12）。</summary>
public static class ComposerSegments
{
    public static string KindOf(TagKind kind) => kind switch
    {
        TagKind.File => "file",
        TagKind.Folder => "folder",
        TagKind.Command => "command",
        TagKind.Skill => "skill",
        TagKind.McpResource => "mcpResource",
        TagKind.McpPrompt => "mcpPrompt",
        TagKind.RuntimeCommand => "runtimeCommand",
        TagKind.AttachedFile => "attachedFile",
        _ => "image",
    };

    public static TagKind? TagKindOf(string kind) => kind switch
    {
        "file" => TagKind.File,
        "folder" => TagKind.Folder,
        "command" => TagKind.Command,
        "skill" => TagKind.Skill,
        "mcpResource" => TagKind.McpResource,
        "mcpPrompt" => TagKind.McpPrompt,
        "image" => TagKind.Image,
        "runtimeCommand" => TagKind.RuntimeCommand,
        "attachedFile" => TagKind.AttachedFile,
        _ => null,
    };

    /// <summary>有 tag 时转为 DTO；只有文字时为 null（直接显示发送的文字即可）。</summary>
    public static IReadOnlyList<PromptSegmentDto>? ToDto(IReadOnlyList<Segment> segments) =>
        segments.Any(s => s is TagSegment) ? ToDtoList(segments) : null;

    /// <summary>总是转为 DTO 列表（只有文字时也是）：草稿落盘用。</summary>
    public static IReadOnlyList<PromptSegmentDto> ToDtoList(IReadOnlyList<Segment> segments) =>
        segments.Select(s => s is TagSegment tag
            ? new PromptSegmentDto(KindOf(tag.Kind), null, tag.Value, tag.Label)
            : new PromptSegmentDto("text", ((TextSegment)s).Text)).ToArray();

    public static IReadOnlyList<Segment> FromDto(IReadOnlyList<PromptSegmentDto> segments) =>
        segments.Select(s => TagKindOf(s.Kind) is { } kind && s.Value is { } value
            ? new TagSegment(kind, value, s.Label ?? value)
            : (Segment)new TextSegment(s.Text ?? "")).ToArray();

    /// <summary>发给模型的文字：command tag 不在其中（由 Host 展开正文后加在前面），其余 tag 按纯文本投影。</summary>
    public static string ModelText(IReadOnlyList<Segment> segments) =>
        ComposerDocument.PlainText(segments.Where(s => s is not TagSegment { Kind: TagKind.Command }));
}
