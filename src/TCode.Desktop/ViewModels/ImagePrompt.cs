using System.Text;
using System.Text.RegularExpressions;
using TCode.Desktop.Localization;

namespace TCode.Desktop.ViewModels;

/// <summary>
/// 提示词中的图片引用。输入框里是占位符「[图片 #1]」（随界面语言，也认英文「[Image #1]」），
/// 发送时展开为「[Image #1: 附件路径]」，让模型和 Agent 都知道图片在哪里；
/// 消息气泡里再折叠回占位符显示。
/// </summary>
public static partial class ImagePrompt
{
    [GeneratedRegex(@"\[(?:图片|Image) #(\d+)\]")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"\[Image #(\d+): [^\]\n]+\]")]
    private static partial Regex ExpandedPattern();

    public static string Placeholder(int number) => Strings.Format(TCodeLangResourceKind.ImagePlaceholderFormat, number);

    /// <summary>
    /// 把占位符展开为带路径的引用；文本里没有占位符的附件追加在末尾。
    /// 引用按附件顺序重新编号为 1..n（移除再添加后编号可能不连续），与消息里图片的显示顺序一致。
    /// </summary>
    public static string Expand(string text, IReadOnlyList<ImageAttachmentViewModel> images)
    {
        if (images.Count == 0)
        {
            return text;
        }
        var byNumber = images.Select((image, index) => (image, number: index + 1)).ToDictionary(x => x.image.Number);
        var used = new HashSet<int>();
        var expanded = PlaceholderPattern().Replace(text, m =>
        {
            var number = int.Parse(m.Groups[1].ValueSpan);
            if (!byNumber.TryGetValue(number, out var entry))
            {
                return m.Value;
            }
            used.Add(number);
            return Reference(entry.number, entry.image);
        });
        var missing = byNumber.Values.Where(x => !used.Contains(x.image.Number)).ToArray();
        if (missing.Length == 0)
        {
            return expanded;
        }
        var builder = new StringBuilder(expanded.TrimEnd());
        foreach (var (image, number) in missing)
        {
            builder.Append(builder.Length == 0 ? "" : "\n").Append(Reference(number, image));
        }
        return builder.ToString();
    }

    /// <summary>显示用：带路径的引用折叠回占位符。</summary>
    public static string Collapse(string text) =>
        text.Contains("[Image #", StringComparison.Ordinal)
            ? ExpandedPattern().Replace(text, m => Placeholder(int.Parse(m.Groups[1].ValueSpan)))
            : text;

    /// <summary>会话标题：折叠图片引用后的首行，过长时截断（与 Agent Host 的 SessionIndex.TitleOf 一致）。</summary>
    public static string? Title(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var line = Collapse(text.Trim()).Split('\n')[0].Trim();
        return line.Length > MaxTitleLength ? line[..MaxTitleLength] + "…" : line;
    }

    private const int MaxTitleLength = 80;

    private static string Reference(int number, ImageAttachmentViewModel image) => $"[Image #{number}: {image.Path}]";
}
