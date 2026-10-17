using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace TCode.Desktop.Controls.Composer;

/// <summary>
/// 输入框的逐行排版：直接用 <see cref="TextFormatter"/> 排出各行，自己维护行的起点与纵坐标。
/// 不用 <see cref="TextLayout"/> 的原因：它对 <see cref="ITextSource"/> 不知道文本长度，
/// 以换行结尾时不会补出光标所在的空行（StreamMark 的 SegmentLayout 也是这样处理的）。
/// 只支持从左到右的文字。
/// </summary>
internal sealed class ComposerLayout : IDisposable
{
    private readonly List<Line> _lines = [];
    private readonly int _length;

    public ComposerLayout(ITextSource source, int length, TextParagraphProperties properties, double maxWidth)
    {
        _length = length;
        var formatter = TextFormatter.Current;
        var position = 0;
        var top = 0.0;
        TextLineBreak? previous = null;
        while (position < length)
        {
            var line = formatter.FormatLine(source, position, maxWidth, properties, previous);
            if (line is null || line.Length <= 0)
            {
                line?.Dispose();
                break;
            }
            _lines.Add(new Line(line, position, top, IsEmpty: false));
            position += line.Length;
            top += line.Height;
            previous = line.TextLineBreak;
        }
        // 空文档，或以换行结尾：补一个空行承载光标。行高按一个空格排出（空文本源排出的行只有字号高）
        if (_lines.Count == 0 || _lines[^1].Text.NewLineLength > 0)
        {
            var line = formatter.FormatLine(new SpaceSource(properties.DefaultTextRunProperties), 0, maxWidth, properties)!;
            _lines.Add(new Line(line, length, top, IsEmpty: true));
            top += line.Height;
        }
        Height = top;
        Width = _lines.Max(l => l.IsEmpty ? 0 : l.Text.WidthIncludingTrailingWhitespace);
    }

    public double Height { get; }

    /// <summary>最宽一行的宽度（含行尾空白）。</summary>
    public double Width { get; }

    public int LineCount => _lines.Count;

    /// <summary>首行高度（单行时的行高）。</summary>
    public double FirstLineHeight => _lines[0].Text.Height;

    public double LineTop(int index) => _lines[index].Top;

    public double LineHeight(int index) => _lines[index].Text.Height;

    public (int Start, int End) LineRange(int index)
    {
        var line = _lines[index];
        return line.IsEmpty ? (line.Start, line.Start) : (line.Start, line.Start + line.Text.Length - line.Text.NewLineLength);
    }

    /// <summary>位置所在的行：落在折行处时属于下一行；换行符之前属于本行；文档末尾属于最后一行。</summary>
    public int LineIndexOf(int position)
    {
        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            if (!line.IsEmpty && position < line.Start + line.Text.Length)
            {
                return i;
            }
        }
        return _lines.Count - 1;
    }

    /// <summary>光标的矩形（宽度为 0）。</summary>
    public Rect CaretRect(int position)
    {
        var line = _lines[LineIndexOf(position)];
        var x = line.IsEmpty ? 0 : line.Text.GetDistanceFromCharacterHit(new CharacterHit(position));
        return new Rect(x, line.Top, 0, line.Text.Height);
    }

    /// <summary>点到文档位置：纵向按行，横向按最近的字符边界；tag 按左右半边决定落在前面还是后面。</summary>
    public int HitTest(Point point)
    {
        var index = 0;
        while (index + 1 < _lines.Count && point.Y >= _lines[index + 1].Top)
        {
            index++;
        }
        return HitTestLine(index, point.X);
    }

    /// <summary>指定行内、横坐标 x 处的位置（上下移动光标使用）。</summary>
    public int HitTestLine(int index, double x)
    {
        var line = _lines[Math.Clamp(index, 0, _lines.Count - 1)];
        if (line.IsEmpty)
        {
            return line.Start;
        }
        var hit = line.Text.GetCharacterHitFromDistance(x);
        var position = hit.FirstCharacterIndex + hit.TrailingLength;
        var (start, end) = LineRange(_lines.IndexOf(line));
        // 折行处（没有换行符的行尾）落在本行最后一个字符之前，光标不会跳到下一行开头
        if (line.Text.NewLineLength == 0 && end < _length && position >= end)
        {
            position = end - 1;
        }
        return Math.Clamp(position, start, end);
    }

    /// <summary>[start, end) 的选区矩形，每行一个；选区包含换行符时在行尾多画一小段。</summary>
    public IEnumerable<Rect> RangeRects(int start, int end)
    {
        const double NewlineWidth = 5;
        foreach (var line in _lines)
        {
            var lineEnd = line.Start + (line.IsEmpty ? 0 : line.Text.Length);
            if (line.IsEmpty || end <= line.Start || start >= lineEnd)
            {
                continue;
            }
            var from = Math.Max(start, line.Start);
            var to = Math.Min(end, lineEnd);
            var contentEnd = lineEnd - line.Text.NewLineLength;
            var x1 = line.Text.GetDistanceFromCharacterHit(new CharacterHit(from));
            var x2 = to > contentEnd
                ? line.Text.WidthIncludingTrailingWhitespace + NewlineWidth
                : line.Text.GetDistanceFromCharacterHit(new CharacterHit(to));
            if (x2 > x1)
            {
                yield return new Rect(x1, line.Top, x2 - x1, line.Text.Height);
            }
        }
    }

    /// <summary>[start, end) 内各 tag 的底色矩形（选中的 tag 在底色之上再叠一层选区色）。</summary>
    public IEnumerable<Rect> TagRects(int start, int end)
    {
        foreach (var line in _lines)
        {
            if (line.IsEmpty || end <= line.Start || start >= line.Start + line.Text.Length)
            {
                continue;
            }
            var position = line.Start;
            foreach (var run in line.Text.TextRuns)
            {
                if (run is TagRun tag && position >= start && position < end)
                {
                    var x = line.Text.GetDistanceFromCharacterHit(new CharacterHit(position));
                    yield return tag.ChipRect.Translate(new Vector(x, line.Top));
                }
                position += run.Length;
            }
        }
    }

    /// <summary>位置处的 tag run 及其所在矩形（悬停提示使用）。</summary>
    public (TagRun Run, Rect Bounds)? TagAt(Point point)
    {
        foreach (var line in _lines)
        {
            if (line.IsEmpty || point.Y < line.Top || point.Y >= line.Top + line.Text.Height)
            {
                continue;
            }
            var position = line.Start;
            foreach (var run in line.Text.TextRuns)
            {
                if (run is TagRun tag)
                {
                    var chip = tag.ChipRect.Translate(new Vector(line.Text.GetDistanceFromCharacterHit(new CharacterHit(position)), line.Top));
                    if (chip.Contains(point))
                    {
                        return (tag, chip);
                    }
                }
                position += run.Length;
            }
        }
        return null;
    }

    public void Draw(DrawingContext context, Point origin)
    {
        foreach (var line in _lines)
        {
            if (!line.IsEmpty)
            {
                line.Text.Draw(context, new Point(origin.X, origin.Y + line.Top));
            }
        }
    }

    public void Dispose()
    {
        foreach (var line in _lines)
        {
            line.Text.Dispose();
        }
        _lines.Clear();
    }

    private sealed record Line(TextLine Text, int Start, double Top, bool IsEmpty);

    private sealed class SpaceSource(TextRunProperties properties) : ITextSource
    {
        public TextRun? GetTextRun(int textSourceIndex) => textSourceIndex == 0 ? new TextCharacters(" ", properties) : null;
    }
}
