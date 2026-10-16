using System.Collections.Immutable;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.Utilities;
using StreamMark.Core.Highlighting;

namespace TCode.Desktop.Controls;

/// <summary>
/// 只读源码视图：行号槽 + TextMate 着色，按行虚拟化（只排版视口内的行），放在 ScrollViewer 中使用。
/// 行号槽在横向滚动时保持固定。支持拖选、双击选词、三击选行、Ctrl/Cmd+C 复制、Ctrl/Cmd+A 全选。
/// 着色在后台进行：先着色前几百行（首屏），再着色全文；切换明暗主题时重新着色。
/// </summary>
public sealed class CodeView : Control
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<CodeView, string?>(nameof(Text));

    public static readonly StyledProperty<string?> LanguageProperty =
        AvaloniaProperty.Register<CodeView, string?>(nameof(Language));

    /// <summary>着色器；未设置时使用 Markdown 渲染共用的 TextMate 着色器（配色与对话中的代码块一致）。</summary>
    public static readonly StyledProperty<ISyntaxHighlighter?> HighlighterProperty =
        AvaloniaProperty.Register<CodeView, ISyntaxHighlighter?>(nameof(Highlighter));

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner<CodeView>();

    public static readonly StyledProperty<double> FontSizeProperty =
        TextElement.FontSizeProperty.AddOwner<CodeView>();

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<CodeView>();

    public static readonly StyledProperty<double> LineHeightProperty =
        AvaloniaProperty.Register<CodeView, double>(nameof(LineHeight), 20);

    public static readonly StyledProperty<IBrush?> LineNumberForegroundProperty =
        AvaloniaProperty.Register<CodeView, IBrush?>(nameof(LineNumberForeground));

    public static readonly StyledProperty<IBrush?> GutterBackgroundProperty =
        AvaloniaProperty.Register<CodeView, IBrush?>(nameof(GutterBackground));

    public static readonly StyledProperty<IBrush?> SelectionBrushProperty =
        AvaloniaProperty.Register<CodeView, IBrush?>(nameof(SelectionBrush));

    private const int TabSize = 4;
    private const double GutterLeft = 12;
    private const double GutterRight = 16;
    private const double PaddingTop = 8;
    private const double PaddingBottom = 24;
    private const double PaddingRight = 16;
    // 首屏先着色的行数
    private const int FirstPassLines = 400;

    private string _text = "";
    private readonly List<int> _lineStarts = [0];
    private int _maxColumns;
    private IReadOnlyList<ImmutableArray<HighlightToken>>? _tokens;
    private readonly Dictionary<int, TextLayout> _layouts = [];
    private double _charWidth = 7.5;
    private Rect _viewport;
    private CancellationTokenSource? _highlightCts;
    private int _anchor = -1;
    private int _caret = -1;
    private bool _dragging;

    static CodeView()
    {
        AffectsMeasure<CodeView>(TextProperty, FontFamilyProperty, FontSizeProperty, LineHeightProperty);
        AffectsRender<CodeView>(ForegroundProperty, LineNumberForegroundProperty, GutterBackgroundProperty, SelectionBrushProperty);
        FocusableProperty.OverrideDefaultValue<CodeView>(true);
        CursorProperty.OverrideDefaultValue<CodeView>(new Cursor(StandardCursorType.Ibeam));
    }

    public CodeView()
    {
        ClipToBounds = true;
        ActualThemeVariantChanged += (_, _) => Rehighlight();
        EffectiveViewportChanged += (_, e) =>
        {
            _viewport = e.EffectiveViewport;
            InvalidateVisual();
        };
    }

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string? Language { get => GetValue(LanguageProperty); set => SetValue(LanguageProperty, value); }
    public ISyntaxHighlighter? Highlighter { get => GetValue(HighlighterProperty); set => SetValue(HighlighterProperty, value); }
    public FontFamily FontFamily { get => GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double LineHeight { get => GetValue(LineHeightProperty); set => SetValue(LineHeightProperty, value); }
    public IBrush? LineNumberForeground { get => GetValue(LineNumberForegroundProperty); set => SetValue(LineNumberForegroundProperty, value); }
    public IBrush? GutterBackground { get => GetValue(GutterBackgroundProperty); set => SetValue(GutterBackgroundProperty, value); }
    public IBrush? SelectionBrush { get => GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }

    public int LineCount => _lineStarts.Count;

    /// <summary>已有着色结果（可能只是首屏部分）。</summary>
    public bool IsHighlighted => _tokens is not null;

    /// <summary>当前选中的文本（没有选区时为空串）。</summary>
    public string SelectedText
    {
        get
        {
            var (start, end) = SelectionRange;
            return end > start ? _text[start..end] : "";
        }
    }

    private (int Start, int End) SelectionRange =>
        _anchor < 0 || _caret < 0 ? (0, 0) : (Math.Min(_anchor, _caret), Math.Max(_anchor, _caret));

    public void SelectAll()
    {
        _anchor = 0;
        _caret = _text.Length;
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            SetText(Text ?? "");
            Rehighlight();
        }
        else if (change.Property == LanguageProperty || change.Property == HighlighterProperty)
        {
            Rehighlight();
        }
        else if (change.Property == FontFamilyProperty || change.Property == FontSizeProperty || change.Property == ForegroundProperty
                 || change.Property == LineHeightProperty)
        {
            _layouts.Clear();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Rehighlight();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _highlightCts?.Cancel();
        _highlightCts = null;
    }

    private void SetText(string text)
    {
        _text = text;
        _lineStarts.Clear();
        _lineStarts.Add(0);
        _maxColumns = 0;
        var columns = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\n')
            {
                _maxColumns = Math.Max(_maxColumns, columns);
                columns = 0;
                _lineStarts.Add(i + 1);
                continue;
            }
            // 估算显示宽度：制表符到下一个制表位，CJK 等全角字符按两列
            columns = c == '\t' ? (columns / TabSize + 1) * TabSize : columns + (c >= 0x2E80 && !char.IsLowSurrogate(c) ? 2 : 1);
        }
        _maxColumns = Math.Max(_maxColumns, columns);
        _tokens = null;
        _layouts.Clear();
        _anchor = _caret = -1;
    }

    private int LineEnd(int line) => line + 1 < _lineStarts.Count ? _lineStarts[line + 1] - 1 : _text.Length;

    private bool IsDark => ActualThemeVariant == ThemeVariant.Dark || ActualThemeVariant?.InheritVariant == ThemeVariant.Dark;

    private void Rehighlight()
    {
        _highlightCts?.Cancel();
        _highlightCts = null;
        if (_tokens is not null)
        {
            _tokens = null;
            _layouts.Clear();
            InvalidateVisual();
        }
        var highlighter = Highlighter ?? MarkdownSetup.Services.Highlighter;
        var language = Language;
        if (VisualRoot is null || highlighter is null || string.IsNullOrEmpty(language) || _text.Length == 0 || !highlighter.SupportsLanguage(language))
        {
            return;
        }
        var cts = _highlightCts = new CancellationTokenSource();
        var text = _text;
        var theme = IsDark ? HighlightThemeKind.Dark : HighlightThemeKind.Light;
        var firstPassEnd = _lineStarts.Count > FirstPassLines ? _lineStarts[FirstPassLines] : -1;
        _ = Task.Run(() =>
        {
            using var session = highlighter.CreateSession(language, theme);
            // 会话缓存行尾的语法状态：第二次只着色新增的行
            if (firstPassEnd > 0)
            {
                Publish(session.Update(text[..firstPassEnd]));
            }
            if (!cts.IsCancellationRequested)
            {
                Publish(session.Update(text));
            }
        }, cts.Token);

        void Publish(IReadOnlyList<ImmutableArray<HighlightToken>> tokens) => Dispatcher.UIThread.Post(() =>
        {
            if (!cts.IsCancellationRequested && ReferenceEquals(text, _text))
            {
                _tokens = tokens;
                _layouts.Clear();
                InvalidateVisual();
            }
        });
    }

    // ---- 排版 ----

    private double GutterWidth => Math.Max(28, _lineStarts.Count.ToString().Length * _charWidth) + GutterLeft + GutterRight;

    private Typeface Typeface => new(FontFamily);

    private IBrush TextBrush => Foreground ?? Brushes.Gray;

    protected override Size MeasureOverride(Size availableSize)
    {
        using (var probe = new TextLayout("0000000000", Typeface, FontSize, TextBrush))
        {
            _charWidth = probe.WidthIncludingTrailingWhitespace / 10;
        }
        _layouts.Clear();
        return new Size(GutterWidth + _maxColumns * _charWidth + PaddingRight, PaddingTop + _lineStarts.Count * LineHeight + PaddingBottom);
    }

    private TextLayout LayoutFor(int line)
    {
        if (_layouts.TryGetValue(line, out var cached))
        {
            return cached;
        }
        var start = _lineStarts[line];
        var text = _text.Substring(start, LineEnd(line) - start);
        var typeface = Typeface;
        var defaults = new GenericTextRunProperties(typeface, FontSize, null, TextBrush);
        List<ValueSpan<TextRunProperties>> spans = [];
        if (_tokens is { } all && line < all.Count && !all[line].IsDefaultOrEmpty)
        {
            foreach (var token in all[line])
            {
                if (token.Start >= text.Length)
                {
                    break;
                }
                if (token.Foreground == 0 && token.Style == HighlightFontStyle.None)
                {
                    continue;
                }
                var face = new Typeface(typeface.FontFamily,
                    token.Style.HasFlag(HighlightFontStyle.Italic) ? FontStyle.Italic : FontStyle.Normal,
                    token.Style.HasFlag(HighlightFontStyle.Bold) ? FontWeight.Bold : FontWeight.Normal);
                var brush = token.Foreground == 0 ? TextBrush : Brush(token.Foreground);
                spans.Add(new ValueSpan<TextRunProperties>(token.Start, Math.Min(token.Length, text.Length - token.Start),
                    new GenericTextRunProperties(face, FontSize, token.Style.HasFlag(HighlightFontStyle.Underline) ? TextDecorations.Underline : null, brush)));
            }
        }
        var paragraph = new CodeParagraphProperties(defaults, LineHeight, _charWidth * TabSize);
        var layout = new TextLayout(new LineSource(text, defaults, spans), paragraph, TextTrimming.None, double.PositiveInfinity, double.PositiveInfinity, 0);
        _layouts[line] = layout;
        return layout;
    }

    private static readonly Dictionary<uint, IBrush> BrushCache = [];

    private static IBrush Brush(uint argb)
    {
        if (!BrushCache.TryGetValue(argb, out var brush))
        {
            BrushCache[argb] = brush = new ImmutableSolidColorBrush(Color.FromUInt32(argb));
        }
        return brush;
    }

    private (int First, int Last) VisibleLines()
    {
        var viewport = _viewport.Height > 0 ? _viewport : new Rect(Bounds.Size);
        var first = Math.Clamp((int)((viewport.Top - PaddingTop) / LineHeight), 0, _lineStarts.Count - 1);
        var last = Math.Clamp((int)Math.Ceiling((viewport.Bottom - PaddingTop) / LineHeight), 0, _lineStarts.Count - 1);
        return (first, last);
    }

    public override void Render(DrawingContext context)
    {
        var (first, last) = VisibleLines();
        // 只保留视口附近的排版缓存
        if (_layouts.Count > (last - first + 1) * 3)
        {
            foreach (var key in _layouts.Keys.Where(k => k < first - 50 || k > last + 50).ToArray())
            {
                _layouts.Remove(key);
            }
        }

        var gutter = GutterWidth;
        var (selStart, selEnd) = SelectionRange;
        var selection = SelectionBrush;
        for (var line = first; line <= last; line++)
        {
            var y = PaddingTop + line * LineHeight;
            var layout = LayoutFor(line);
            if (selection is not null && selEnd > selStart)
            {
                var lineStart = _lineStarts[line];
                var lineEnd = LineEnd(line);
                var s = Math.Max(selStart, lineStart);
                var e = Math.Min(selEnd, lineEnd);
                if (e > s)
                {
                    foreach (var rect in layout.HitTestTextRange(s - lineStart, e - s))
                    {
                        context.FillRectangle(selection, new Rect(gutter + rect.X, y, rect.Width, LineHeight));
                    }
                }
                // 选区跨过行尾：画出换行符的位置
                if (selStart <= lineEnd && selEnd > lineEnd && lineEnd < _text.Length)
                {
                    var x = layout.HitTestTextPosition(lineEnd - lineStart).X;
                    context.FillRectangle(selection, new Rect(gutter + x, y, _charWidth, LineHeight));
                }
            }
            layout.Draw(context, new Point(gutter, y));
        }

        // 行号槽：横向滚动时固定在视口左侧，盖住滚到它下面的正文
        var left = Math.Max(0, _viewport.X);
        var top = Math.Max(0, _viewport.Y);
        var height = _viewport.Height > 0 ? _viewport.Height : Bounds.Height;
        // 只有横向滚动后正文才会滚到行号下面；未滚动时不画底色，透出编辑区背景
        if (left > 0 && GutterBackground is { } background)
        {
            context.FillRectangle(background, new Rect(left, top, gutter - GutterRight / 2, height));
        }
        var numberBrush = LineNumberForeground ?? TextBrush;
        for (var line = first; line <= last; line++)
        {
            using var number = new TextLayout((line + 1).ToString(), Typeface, FontSize, numberBrush, lineHeight: LineHeight);
            number.Draw(context, new Point(left + gutter - GutterRight - number.WidthIncludingTrailingWhitespace, PaddingTop + line * LineHeight));
        }
    }

    // ---- 选择与复制 ----

    private int HitTest(Point point)
    {
        if (_text.Length == 0)
        {
            return 0;
        }
        var line = Math.Clamp((int)((point.Y - PaddingTop) / LineHeight), 0, _lineStarts.Count - 1);
        if (point.Y < PaddingTop)
        {
            return 0;
        }
        var layout = LayoutFor(line);
        var hit = layout.HitTestPoint(new Point(Math.Max(0, point.X - GutterWidth), LineHeight / 2));
        var position = hit.TextPosition + (hit.IsTrailing ? 1 : 0);
        return _lineStarts[line] + Math.Clamp(position, 0, LineEnd(line) - _lineStarts[line]);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }
        Focus();
        var offset = HitTest(point.Position);
        switch (e.ClickCount)
        {
            case 2:
                (_anchor, _caret) = WordAt(offset);
                break;
            case >= 3:
                var line = LineOf(offset);
                _anchor = _lineStarts[line];
                _caret = Math.Min(_text.Length, LineEnd(line) + 1);
                break;
            default:
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _anchor >= 0)
                {
                    _caret = offset;
                }
                else
                {
                    _anchor = _caret = offset;
                }
                _dragging = true;
                e.Pointer.Capture(this);
                break;
        }
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging)
        {
            return;
        }
        var position = e.GetPosition(this);
        _caret = HitTest(position);
        // 拖到视口外时滚动
        this.BringIntoView(new Rect(position.X - 4, position.Y - 4, 8, 8));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (command && e.Key == Key.A)
        {
            SelectAll();
            e.Handled = true;
        }
        else if (command && e.Key == Key.C && SelectedText is { Length: > 0 } selected)
        {
            _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(selected);
            e.Handled = true;
        }
    }

    private int LineOf(int offset)
    {
        var index = _lineStarts.BinarySearch(offset);
        return index >= 0 ? index : ~index - 1;
    }

    private (int Start, int End) WordAt(int offset)
    {
        static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
        var start = offset;
        var end = offset;
        while (start > 0 && IsWord(_text[start - 1]))
        {
            start--;
        }
        while (end < _text.Length && IsWord(_text[end]))
        {
            end++;
        }
        return end > start ? (start, end) : (offset, Math.Min(offset + 1, _text.Length));
    }

    /// <summary>一行文本 + 着色片段的文本源：片段之间的空隙用默认样式。</summary>
    private sealed class LineSource(string text, TextRunProperties defaults, List<ValueSpan<TextRunProperties>> spans) : ITextSource
    {
        public TextRun? GetTextRun(int textSourceIndex)
        {
            if (textSourceIndex >= text.Length)
            {
                return new TextEndOfParagraph();
            }
            // 片段按起点有序且不重叠；一行只有几十个片段，顺序查找即可
            foreach (var span in spans)
            {
                if (span.Start + span.Length <= textSourceIndex)
                {
                    continue;
                }
                return span.Start <= textSourceIndex
                    ? new TextCharacters(text.AsMemory(textSourceIndex, span.Start + span.Length - textSourceIndex), span.Value)
                    : new TextCharacters(text.AsMemory(textSourceIndex, span.Start - textSourceIndex), defaults);
            }
            return new TextCharacters(text.AsMemory(textSourceIndex), defaults);
        }
    }

    /// <summary>不换行、固定行高、制表位为 <see cref="TabSize"/> 个字符宽。</summary>
    private sealed class CodeParagraphProperties(TextRunProperties defaults, double lineHeight, double tabWidth) : TextParagraphProperties
    {
        public override FlowDirection FlowDirection => FlowDirection.LeftToRight;
        public override TextAlignment TextAlignment => TextAlignment.Left;
        public override double LineHeight => lineHeight;
        public override bool FirstLineInParagraph => true;
        public override TextRunProperties DefaultTextRunProperties => defaults;
        public override TextWrapping TextWrapping => TextWrapping.NoWrap;
        public override double Indent => 0;
        public override double DefaultIncrementalTab => tabWidth;
    }
}
