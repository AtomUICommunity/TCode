using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using TCode.Desktop.Icons;

namespace TCode.Desktop.Controls.Composer;

/// <summary>tag 的外观（随主题变化，由 <see cref="ComposerEditor"/> 提供）。</summary>
/// <param name="MaxWidth">tag 的最大宽度；更长的文字在中间截断。</param>
public sealed record TagStyle(IBrush Background, IBrush Foreground, double MaxWidth);

/// <summary>
/// 文档里的一个 tag（docs/design/composer.md §6.2）：圆角底色、按种类的图标、文字。
/// 作为 <see cref="DrawableTextRun"/> 嵌入正文排版，占一个文本位置；不会被拆到两行。
/// </summary>
internal sealed class TagRun : DrawableTextRun
{
    // 底色左右与相邻文字之间的间隙、底色内边距、图标与文字间距
    private const double Gap = 2;
    private const double PaddingX = 5;
    private const double IconSpacing = 3;

    private static readonly Dictionary<LucideKind, Geometry[]> Icons = [];

    private readonly TagStyle _style;
    private readonly TextLayout _label;
    private readonly Geometry[] _icon;
    private readonly double _iconSize;
    private readonly Pen _iconPen;

    public TagRun(TagSegment tag, TextRunProperties properties, TagStyle style)
    {
        Tag = tag;
        Properties = properties;
        _style = style;
        var fontSize = properties.FontRenderingEmSize;
        _iconSize = Math.Round(fontSize * 0.95);
        _icon = IconFor(tag.Kind);
        // 线宽以 24×24 的 viewBox 为单位（Lucide 为 2），随图标一起缩放
        _iconPen = new Pen(style.Foreground, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var maxLabel = Math.Max(fontSize * 2, style.MaxWidth - Gap * 2 - PaddingX * 2 - _iconSize - IconSpacing);
        _label = CreateLabel(tag.Label, properties, style.Foreground, maxLabel);
    }

    public TagSegment Tag { get; }

    public override TextRunProperties Properties { get; }

    public override Size Size => new(Gap * 2 + PaddingX * 2 + _iconSize + IconSpacing + _label.WidthIncludingTrailingWhitespace, _label.Height);

    public override double Baseline => _label.Baseline;

    /// <summary>底色区域（相对于 run 的原点），供命中测试与悬停提示使用。</summary>
    public Rect ChipRect => new(Gap, 1, Size.Width - Gap * 2, Size.Height - 2);

    public override void Draw(DrawingContext drawingContext, Point origin)
    {
        var chip = ChipRect.Translate(new Vector(origin.X, origin.Y));
        drawingContext.DrawRectangle(_style.Background, null, chip, 5, 5);
        var iconX = chip.X + PaddingX;
        var iconY = origin.Y + (Size.Height - _iconSize) / 2;
        // 图标按 24×24 的 viewBox 缩放
        using (drawingContext.PushTransform(Matrix.CreateScale(_iconSize / 24, _iconSize / 24) * Matrix.CreateTranslation(iconX, iconY)))
        {
            foreach (var geometry in _icon)
            {
                drawingContext.DrawGeometry(null, _iconPen, geometry);
            }
        }
        _label.Draw(drawingContext, new Point(iconX + _iconSize + IconSpacing, origin.Y));
    }

    private static TextLayout CreateLabel(string text, TextRunProperties properties, IBrush foreground, double maxWidth)
    {
        TextLayout Make(string s) => new(s, properties.Typeface, properties.FontRenderingEmSize, foreground);
        var layout = Make(text);
        if (layout.WidthIncludingTrailingWhitespace <= maxWidth || text.Length <= 2)
        {
            return layout;
        }
        // 中间截断：保留开头和结尾（文件名在结尾），二分查找能放下的最多字符数
        var low = 1;
        var high = text.Length - 1;
        var best = Make("…");
        while (low <= high)
        {
            var keep = (low + high) / 2;
            var head = (keep + 1) / 3;
            var candidate = Make(text[..head] + "…" + text[^(keep - head)..]);
            if (candidate.WidthIncludingTrailingWhitespace <= maxWidth)
            {
                best = candidate;
                low = keep + 1;
            }
            else
            {
                high = keep - 1;
            }
        }
        return best;
    }

    private static Geometry[] IconFor(TagKind kind)
    {
        var icon = kind switch
        {
            TagKind.Folder => LucideKind.Folder,
            TagKind.Command or TagKind.McpPrompt or TagKind.RuntimeCommand => LucideKind.SquareTerminal,
            TagKind.Skill => LucideKind.Sparkles,
            TagKind.McpResource => LucideKind.Plug,
            TagKind.Image => LucideKind.Image,
            _ => LucideKind.FileText,
        };
        lock (Icons)
        {
            if (!Icons.TryGetValue(icon, out var geometries))
            {
                geometries = LucidePaths.For(icon).Select(d => (Geometry)StreamGeometry.Parse(d.TrimStart('#'))).ToArray();
                Icons[icon] = geometries;
            }
            return geometries;
        }
    }
}

/// <summary>把 <see cref="ComposerDocument"/> 的片段提供给 <see cref="TextLayout"/>：文本为 <see cref="TextCharacters"/>，tag 为 <see cref="TagRun"/>。</summary>
internal sealed class ComposerTextSource : ITextSource
{
    private readonly (int Start, Segment Segment)[] _segments;
    private readonly TextRunProperties _properties;
    private readonly TagStyle _tagStyle;
    private readonly Dictionary<int, TagRun> _tags = [];
    private TextRunProperties? _preeditProperties;

    public ComposerTextSource(IReadOnlyList<Segment> segments, TextRunProperties properties, TagStyle tagStyle)
    {
        _properties = properties;
        _tagStyle = tagStyle;
        _segments = new (int, Segment)[segments.Count];
        var start = 0;
        for (var i = 0; i < segments.Count; i++)
        {
            _segments[i] = (start, segments[i]);
            start += segments[i].Length;
        }
    }

    public TextRun? GetTextRun(int textSourceIndex)
    {
        foreach (var (start, segment) in _segments)
        {
            if (textSourceIndex >= start + segment.Length)
            {
                continue;
            }
            if (segment is TextSegment text)
            {
                return new TextCharacters(text.Text.AsMemory(textSourceIndex - start), _properties);
            }
            if (segment is PreeditSegment preedit)
            {
                _preeditProperties ??= new GenericTextRunProperties(_properties.Typeface, _properties.FontRenderingEmSize,
                    TextDecorations.Underline, _properties.ForegroundBrush);
                return new TextCharacters(preedit.Text.AsMemory(textSourceIndex - start), _preeditProperties);
            }
            if (!_tags.TryGetValue(start, out var run))
            {
                run = new TagRun((TagSegment)segment, _properties, _tagStyle);
                _tags[start] = run;
            }
            return run;
        }
        return null;
    }

    /// <summary>已创建的 tag run（排版之后可用），键为文档位置。</summary>
    public IReadOnlyDictionary<int, TagRun> Tags => _tags;
}
