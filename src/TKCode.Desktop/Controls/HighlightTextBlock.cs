using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace TKCode.Desktop.Controls;

/// <summary>按字符下标高亮部分文字的 TextBlock（补全列表中命中查询的字符）。</summary>
public sealed class HighlightTextBlock : TextBlock
{
    public static readonly StyledProperty<string?> SourceTextProperty =
        AvaloniaProperty.Register<HighlightTextBlock, string?>(nameof(SourceText));

    public static readonly StyledProperty<IReadOnlyList<int>?> HighlightsProperty =
        AvaloniaProperty.Register<HighlightTextBlock, IReadOnlyList<int>?>(nameof(Highlights));

    public static readonly StyledProperty<IBrush?> HighlightBrushProperty =
        AvaloniaProperty.Register<HighlightTextBlock, IBrush?>(nameof(HighlightBrush));

    public string? SourceText { get => GetValue(SourceTextProperty); set => SetValue(SourceTextProperty, value); }
    public IReadOnlyList<int>? Highlights { get => GetValue(HighlightsProperty); set => SetValue(HighlightsProperty, value); }
    public IBrush? HighlightBrush { get => GetValue(HighlightBrushProperty); set => SetValue(HighlightBrushProperty, value); }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceTextProperty || change.Property == HighlightsProperty || change.Property == HighlightBrushProperty)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        var text = SourceText ?? "";
        var hits = Highlights is { Count: > 0 } h ? new HashSet<int>(h) : null;
        if (hits is null)
        {
            Inlines = null;
            Text = text;
            return;
        }
        var inlines = new InlineCollection();
        var start = 0;
        while (start < text.Length)
        {
            var highlighted = hits.Contains(start);
            var end = start + 1;
            while (end < text.Length && hits.Contains(end) == highlighted)
            {
                end++;
            }
            var run = new Run(text[start..end]);
            if (highlighted)
            {
                run.FontWeight = FontWeight.SemiBold;
                if (HighlightBrush is { } brush)
                {
                    run.Foreground = brush;
                }
            }
            inlines.Add(run);
            start = end;
        }
        Text = null;
        Inlines = inlines;
    }
}
