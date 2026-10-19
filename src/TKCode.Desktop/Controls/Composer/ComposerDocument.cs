namespace TKCode.Desktop.Controls.Composer;

/// <summary>输入框里 tag 的种类（docs/design/composer.md §6.1）。</summary>
public enum TagKind
{
    File,
    Folder,
    Command,
    Skill,
    McpResource,
    McpPrompt,
    Image,

    /// <summary>外部运行时自带的命令：原样以 /名称 发给运行时，由它自己处理。</summary>
    RuntimeCommand,

    /// <summary>
    /// 附件文件（日志、办公文档或其他任意非图片文件）。值为绝对路径。
    /// 文件内容不进会话；发送时纯文本是包着绝对路径的提示，由 Agent 自己读取。
    /// </summary>
    AttachedFile,
}

/// <summary>文档片段：一段文本或一个 tag。</summary>
public abstract record Segment
{
    /// <summary>在文档中占用的位置数：文本按 UTF-16 码元计，tag 恒为 1。</summary>
    public abstract int Length { get; }
}

public sealed record TextSegment(string Text) : Segment
{
    public override int Length => Text.Length;
}

/// <param name="Value">tag 指向的对象：文件为相对路径，附件文件为绝对路径，命令 / skill 为名称，MCP 资源为 URI，图片为附件编号。</param>
/// <param name="Label">显示文字。</param>
public sealed record TagSegment(TagKind Kind, string Value, string Label) : Segment
{
    public override int Length => 1;

    /// <summary>纯文本形式：复制、测量和草稿使用。</summary>
    public string ToPlainText() => Kind switch
    {
        TagKind.File => "@" + QuotePath(Value),
        TagKind.Folder => "@" + QuotePath(Value.EndsWith('/') ? Value : Value + "/"),
        TagKind.Command or TagKind.Skill or TagKind.McpPrompt or TagKind.RuntimeCommand => "/" + Value,
        TagKind.McpResource => "@" + Value,
        TagKind.Image => "[" + Label + "]",
        TagKind.AttachedFile => AttachedFiles.Reference(Value),
        _ => Label,
    };

    private static string QuotePath(string path) => path.Any(char.IsWhiteSpace) ? $"\"{path}\"" : path;
}

/// <summary>一次编辑的分组方式：同类的连续编辑在撤销时合并为一步。</summary>
public enum EditKind
{
    /// <summary>不与前后合并（插入 tag、粘贴、剪切、替换等）。</summary>
    Other,

    /// <summary>键入：连续键入按单词合并。</summary>
    Typing,

    /// <summary>向后删除（退格）：连续退格合并。</summary>
    DeleteBackward,

    /// <summary>向前删除（Delete）：连续删除合并。</summary>
    DeleteForward,
}

/// <param name="Start">变化的起点。</param>
/// <param name="RemovedLength">被删除的位置数。</param>
/// <param name="InsertedLength">新插入的位置数。</param>
public readonly record struct DocumentChange(int Start, int RemovedLength, int InsertedLength);

/// <summary>
/// 输入框的文档模型（docs/design/composer.md §6.1）：一串文本片段与 tag 片段。
/// 位置是整数偏移，文本每个 UTF-16 码元占 1，tag 占 1，所以光标不可能落在 tag 内部，tag 也只能被整体删除。
/// 所有编辑都归结为 <see cref="Replace"/>，撤销栈记录它的逆操作。
/// </summary>
public sealed class ComposerDocument
{
    private const int UndoLimit = 200;

    private readonly List<Segment> _segments = [];
    private readonly List<Edit> _undo = [];
    private readonly List<Edit> _redo = [];

    /// <summary>上一次编辑可以和下一次合并（合并只发生在同类、首尾相接的编辑之间）。</summary>
    private bool _canMerge;

    public ComposerDocument()
    {
    }

    public ComposerDocument(IEnumerable<Segment> segments)
    {
        _segments.AddRange(Normalize(segments));
        Length = _segments.Sum(s => s.Length);
    }

    public int Length { get; private set; }

    /// <summary>每次修改加一，供排版缓存判断是否失效。</summary>
    public int Version { get; private set; }

    public bool IsEmpty => Length == 0;

    public IReadOnlyList<Segment> Segments => _segments;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public event Action<DocumentChange>? Changed;

    /// <summary>纯文本投影：tag 按 <see cref="TagSegment.ToPlainText"/> 展开。</summary>
    public string ToPlainText() => PlainText(_segments);

    public static string PlainText(IEnumerable<Segment> segments) =>
        string.Concat(segments.Select(s => s is TextSegment t ? t.Text : ((TagSegment)s).ToPlainText()));

    /// <summary>与位置一一对应的字符文本：tag 记为 U+FFFC（输入法的 SurroundingText 使用）。</summary>
    public string ToCharText(int start, int length) =>
        string.Concat(Slice(start, length).Select(s => s is TextSegment t ? t.Text : "\uFFFC"));

    /// <summary>位置 <paramref name="position"/> 处的 tag；该位置是文本或越界时为 null。</summary>
    public TagSegment? TagAt(int position)
    {
        var (index, offset) = Locate(position);
        return index < _segments.Count && offset == 0 && _segments[index] is TagSegment tag ? tag : null;
    }

    /// <summary>位置处的字符；tag 返回 U+FFFC，越界返回 null。</summary>
    public char? CharAt(int position)
    {
        if (position < 0 || position >= Length)
        {
            return null;
        }
        var (index, offset) = Locate(position);
        return _segments[index] is TextSegment t ? t.Text[offset] : '￼';
    }

    /// <summary>取出 [start, start+length) 的片段（复制、撤销使用）。</summary>
    public IReadOnlyList<Segment> Slice(int start, int length)
    {
        (start, length) = Clamp(start, length);
        var result = new List<Segment>();
        var end = start + length;
        var position = 0;
        foreach (var segment in _segments)
        {
            var segmentEnd = position + segment.Length;
            if (segmentEnd > start && position < end)
            {
                if (segment is TextSegment text)
                {
                    var from = Math.Max(start, position) - position;
                    var to = Math.Min(end, segmentEnd) - position;
                    result.Add(new TextSegment(text.Text[from..to]));
                }
                else
                {
                    result.Add(segment);
                }
            }
            if (segmentEnd >= end)
            {
                break;
            }
            position = segmentEnd;
        }
        return result;
    }

    public void Insert(int position, string text, EditKind kind = EditKind.Other) =>
        Replace(position, 0, text.Length == 0 ? [] : [new TextSegment(text)], kind);

    public void InsertTag(int position, TagSegment tag) => Replace(position, 0, [tag], EditKind.Other);

    public void Delete(int start, int length, EditKind kind = EditKind.Other) => Replace(start, length, [], kind);

    /// <summary>用 <paramref name="segments"/> 替换 [start, start+length)。返回插入内容之后的位置（光标落点）。</summary>
    public int Replace(int start, int length, IReadOnlyList<Segment> segments, EditKind kind = EditKind.Other)
    {
        (start, length) = Clamp(start, length);
        var inserted = Normalize(segments);
        var insertedLength = inserted.Sum(s => s.Length);
        if (length == 0 && insertedLength == 0)
        {
            return start;
        }
        var removed = Slice(start, length);
        Apply(start, length, inserted);
        Record(new Edit(start, removed, inserted, kind));
        return start + insertedLength;
    }

    /// <summary>替换整个文档（发送后清空、恢复草稿）；清除撤销记录。</summary>
    public void SetContent(IEnumerable<Segment> segments)
    {
        var removedLength = Length;
        _segments.Clear();
        _segments.AddRange(Normalize(segments));
        Length = _segments.Sum(s => s.Length);
        _undo.Clear();
        _redo.Clear();
        _canMerge = false;
        Version++;
        Changed?.Invoke(new DocumentChange(0, removedLength, Length));
    }

    /// <summary>撤销一步；返回撤销后的光标位置，没有可撤销的内容时为 null。</summary>
    public int? Undo()
    {
        if (_undo.Count == 0)
        {
            return null;
        }
        var edit = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Apply(edit.Start, edit.InsertedLength, edit.Removed);
        _redo.Add(edit);
        _canMerge = false;
        return edit.Start + edit.RemovedLength;
    }

    /// <summary>重做一步；返回重做后的光标位置，没有可重做的内容时为 null。</summary>
    public int? Redo()
    {
        if (_redo.Count == 0)
        {
            return null;
        }
        var edit = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Apply(edit.Start, edit.RemovedLength, edit.Inserted);
        _undo.Add(edit);
        _canMerge = false;
        return edit.Start + edit.InsertedLength;
    }

    /// <summary>结束当前的合并分组（光标被移动、失去焦点时调用），下一次键入另起一步撤销。</summary>
    public void BreakUndoGroup() => _canMerge = false;

    /// <summary>
    /// 上一个单词边界（Alt/Ctrl+←、按单词退格）：先跳过空白，再跳过同一类字符；tag 自成一个单词。
    /// </summary>
    public int PreviousWordBoundary(int position)
    {
        position = Math.Clamp(position, 0, Length);
        while (position > 0 && ClassAt(position - 1) == CharClass.Space)
        {
            position--;
        }
        if (position == 0)
        {
            return 0;
        }
        var cls = ClassAt(position - 1);
        if (cls == CharClass.Tag)
        {
            return position - 1;
        }
        while (position > 0 && ClassAt(position - 1) == cls)
        {
            position--;
        }
        return position;
    }

    /// <summary>下一个单词边界（Alt/Ctrl+→、按单词向前删除）。</summary>
    public int NextWordBoundary(int position)
    {
        position = Math.Clamp(position, 0, Length);
        while (position < Length && ClassAt(position) == CharClass.Space)
        {
            position++;
        }
        if (position == Length)
        {
            return Length;
        }
        var cls = ClassAt(position);
        if (cls == CharClass.Tag)
        {
            return position + 1;
        }
        while (position < Length && ClassAt(position) == cls)
        {
            position++;
        }
        return position;
    }

    /// <summary>位置所在单词的范围（双击选择）；位于空白时返回空白段。</summary>
    public (int Start, int End) WordAt(int position)
    {
        if (Length == 0)
        {
            return (0, 0);
        }
        position = Math.Clamp(position, 0, Length - 1);
        var cls = ClassAt(position);
        if (cls == CharClass.Tag)
        {
            return (position, position + 1);
        }
        var start = position;
        var end = position + 1;
        while (start > 0 && ClassAt(start - 1) == cls)
        {
            start--;
        }
        while (end < Length && ClassAt(end) == cls)
        {
            end++;
        }
        return (start, end);
    }

    private enum CharClass
    {
        Space,
        Word,
        Punctuation,
        Tag,
    }

    private CharClass ClassAt(int position) => CharAt(position) switch
    {
        '￼' when TagAt(position) is not null => CharClass.Tag,
        { } c when char.IsWhiteSpace(c) => CharClass.Space,
        { } c when char.IsLetterOrDigit(c) || c == '_' || char.IsSurrogate(c) => CharClass.Word,
        _ => CharClass.Punctuation,
    };

    private void Record(Edit edit)
    {
        _redo.Clear();
        if (_canMerge && _undo.Count > 0 && TryMerge(_undo[^1], edit) is { } merged)
        {
            _undo[^1] = merged;
        }
        else
        {
            _undo.Add(edit);
            if (_undo.Count > UndoLimit)
            {
                _undo.RemoveAt(0);
            }
        }
        _canMerge = edit.Kind != EditKind.Other;
    }

    /// <summary>
    /// 合并规则：
    /// - 键入：首尾相接，且新输入不是在空白之后开始一个新单词；
    /// - 退格：新删除的范围紧挨在上一次之前；
    /// - Delete：在同一位置继续向后删除。
    /// </summary>
    private static Edit? TryMerge(Edit previous, Edit next)
    {
        if (previous.Kind != next.Kind)
        {
            return null;
        }
        switch (next.Kind)
        {
            case EditKind.Typing when next.RemovedLength == 0 && previous.RemovedLength == 0 &&
                                      next.Start == previous.Start + previous.InsertedLength:
            {
                var before = PlainText(previous.Inserted);
                var after = PlainText(next.Inserted);
                if (before.Length > 0 && char.IsWhiteSpace(before[^1]) && after.Length > 0 && !char.IsWhiteSpace(after[0]))
                {
                    return null;
                }
                return previous with { Inserted = Normalize([.. previous.Inserted, .. next.Inserted]) };
            }
            case EditKind.DeleteBackward when next.InsertedLength == 0 && previous.InsertedLength == 0 &&
                                              next.Start + next.RemovedLength == previous.Start:
                return previous with { Start = next.Start, Removed = Normalize([.. next.Removed, .. previous.Removed]) };
            case EditKind.DeleteForward when next.InsertedLength == 0 && previous.InsertedLength == 0 && next.Start == previous.Start:
                return previous with { Removed = Normalize([.. previous.Removed, .. next.Removed]) };
            default:
                return null;
        }
    }

    private void Apply(int start, int length, IReadOnlyList<Segment> inserted)
    {
        var before = Slice(0, start);
        var after = Slice(start + length, Length - start - length);
        var segments = Normalize([.. before, .. inserted, .. after]);
        _segments.Clear();
        _segments.AddRange(segments);
        Length = _segments.Sum(s => s.Length);
        Version++;
        Changed?.Invoke(new DocumentChange(start, length, inserted.Sum(s => s.Length)));
    }

    private (int Index, int Offset) Locate(int position)
    {
        var start = 0;
        for (var i = 0; i < _segments.Count; i++)
        {
            var length = _segments[i].Length;
            if (position < start + length)
            {
                return (i, position - start);
            }
            start += length;
        }
        return (_segments.Count, 0);
    }

    private (int Start, int Length) Clamp(int start, int length)
    {
        start = Math.Clamp(start, 0, Length);
        return (start, Math.Clamp(length, 0, Length - start));
    }

    /// <summary>合并相邻文本、去掉空文本。</summary>
    private static List<Segment> Normalize(IEnumerable<Segment> segments)
    {
        var result = new List<Segment>();
        foreach (var segment in segments)
        {
            switch (segment)
            {
                case TextSegment { Text.Length: 0 }:
                    continue;
                case TextSegment text when result.Count > 0 && result[^1] is TextSegment last:
                    result[^1] = new TextSegment(last.Text + text.Text);
                    break;
                default:
                    result.Add(segment);
                    break;
            }
        }
        return result;
    }

    private sealed record Edit(int Start, IReadOnlyList<Segment> Removed, IReadOnlyList<Segment> Inserted, EditKind Kind)
    {
        public int RemovedLength => Removed.Sum(s => s.Length);
        public int InsertedLength => Inserted.Sum(s => s.Length);
    }
}
