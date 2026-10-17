using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Input;
using Avalonia.Input.Platform;

namespace TCode.Desktop.Controls.Composer;

/// <summary>
/// 输入框的剪贴板（docs/design/composer.md §6.3）：复制时同时写纯文本与内部格式（片段 JSON），
/// 在 TCode 内部粘贴时还原 tag；外部粘贴只有纯文本，一律当作文本。
/// </summary>
public static class ComposerClipboard
{
    public static readonly DataFormat<string> SegmentsFormat = DataFormat.CreateStringApplicationFormat("tcode-composer-segments");

    public static async Task CopyAsync(IClipboard clipboard, IReadOnlyList<Segment> segments)
    {
        var item = new DataTransferItem();
        item.SetText(ComposerDocument.PlainText(segments));
        if (segments.Any(s => s is TagSegment))
        {
            item.Set(SegmentsFormat, Serialize(segments));
        }
        var data = new DataTransfer();
        data.Add(item);
        await clipboard.SetDataAsync(data);
    }

    /// <summary>读取剪贴板：优先内部格式，其次纯文本；都没有时为 null。</summary>
    public static async Task<IReadOnlyList<Segment>?> ReadAsync(IClipboard clipboard)
    {
        if (await clipboard.TryGetValueAsync(SegmentsFormat) is { } json && Deserialize(json) is { } segments)
        {
            return segments;
        }
        return await clipboard.TryGetTextAsync() is { Length: > 0 } text ? [new TextSegment(text)] : null;
    }

    public static string Serialize(IReadOnlyList<Segment> segments) =>
        JsonSerializer.Serialize(segments.Select(ToDto).ToList(), ComposerJsonContext.Default.ListSegmentDto);

    public static IReadOnlyList<Segment>? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, ComposerJsonContext.Default.ListSegmentDto)?.Select(FromDto).OfType<Segment>().ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static SegmentDto ToDto(Segment segment) => segment switch
    {
        TagSegment tag => new SegmentDto(null, tag.Kind, tag.Value, tag.Label),
        TextSegment text => new SegmentDto(text.Text, null, null, null),
        _ => new SegmentDto("", null, null, null),
    };

    private static Segment? FromDto(SegmentDto dto) => dto.Kind is { } kind && dto.Value is { } value
        ? new TagSegment(kind, value, dto.Label ?? value)
        : dto.Text is { } text ? new TextSegment(text) : null;
}

public sealed record SegmentDto(string? Text, TagKind? Kind, string? Value, string? Label);

[JsonSourceGenerationOptions(UseStringEnumConverter = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<SegmentDto>))]
internal sealed partial class ComposerJsonContext : JsonSerializerContext;
