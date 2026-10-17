using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Input.TextInput;

namespace TCode.Desktop.Controls.Composer;

/// <summary>输入法与无障碍（docs/design/composer.md §6.3）。</summary>
public partial class ComposerEditor
{
    // 预编辑文字（输入法组字中）：显示在光标处、带下划线，不进入文档
    private string? _preedit;
    private int? _preeditCursor;
    private ImeClient? _ime;

    public bool HasPreedit => !string.IsNullOrEmpty(_preedit);

    /// <summary>设置或清除预编辑文字。组字开始时先删除选区。</summary>
    public void SetPreedit(string? text, int? cursor = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            text = null;
        }
        if (text is not null && _preedit is null && HasSelection)
        {
            DeleteSelection();
        }
        if (text == _preedit && cursor == _preeditCursor)
        {
            return;
        }
        _preedit = text;
        _preeditCursor = cursor;
        _layoutDirty = true;
        InvalidateMeasure();
        InvalidateVisual();
        _ime?.NotifyCursorRectangle();
    }

    /// <summary>文档位置到排版位置：有预编辑文字时，光标之后的位置后移。</summary>
    private int ToLayout(int position) => _preedit is { } p && position > _caret ? position + p.Length : position;

    /// <summary>排版位置到文档位置：落在预编辑文字内时算作光标处。</summary>
    private int FromLayout(int position)
    {
        if (_preedit is not { } p || position <= _caret)
        {
            return position;
        }
        return position <= _caret + p.Length ? _caret : position - p.Length;
    }

    /// <summary>排版用的片段：预编辑文字插在光标处。</summary>
    private IReadOnlyList<Segment> LayoutSegments() => _preedit is { } p
        ? [.. _document.Slice(0, _caret), new PreeditSegment(p), .. _document.Slice(_caret, _document.Length - _caret)]
        : _document.Segments;

    /// <summary>光标在排版中的位置（组字时在预编辑文字内）。</summary>
    private int LayoutCaret => _preedit is { } p ? _caret + Math.Clamp(_preeditCursor ?? p.Length, 0, p.Length) : _caret;

    /// <summary>当前段落（不含换行）及其起点：作为输入法的 SurroundingText，tag 记为 U+FFFC。</summary>
    private (string Text, int Start) Paragraph()
    {
        var (start, end) = ParagraphAt(_caret);
        return (_document.ToCharText(start, end - start), start);
    }

    private sealed class ImeClient(ComposerEditor owner) : TextInputMethodClient
    {
        public override Visual TextViewVisual => owner;

        public override bool SupportsPreedit => true;

        public override bool SupportsSurroundingText => true;

        public override string SurroundingText => owner.Paragraph().Text;

        public override Rect CursorRectangle
        {
            get
            {
                var rect = owner.EnsureLayout().CaretRect(owner.LayoutCaret);
                return new Rect(rect.X + owner.Padding.Left, rect.Y + owner.Padding.Top - owner._scrollY, 1, rect.Height);
            }
        }

        public override TextSelection Selection
        {
            get
            {
                var start = owner.Paragraph().Start;
                return new TextSelection(owner._anchor - start, owner._caret - start);
            }
            set
            {
                var start = owner.Paragraph().Start;
                owner.Select(start + value.Start, start + value.End);
            }
        }

        public override void SetPreeditText(string? preeditText) => owner.SetPreedit(preeditText);

        public override void SetPreeditText(string? preeditText, int? cursorPos) => owner.SetPreedit(preeditText, cursorPos);

        public void NotifyCursorRectangle() => RaiseCursorRectangleChanged();

        public void NotifySelection() => RaiseSelectionChanged();

        public void NotifySurroundingText() => RaiseSurroundingTextChanged();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ComposerEditorAutomationPeer(this);

    private sealed class ComposerEditorAutomationPeer(ComposerEditor owner) : ControlAutomationPeer(owner), IValueProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Edit;

        protected override string? GetNameCore() => base.GetNameCore() is { Length: > 0 } name ? name : owner.PlaceholderText;

        public bool IsReadOnly => false;

        public string? Value => owner.Document.ToPlainText();

        public void SetValue(string? value) => owner.Document.SetContent(string.IsNullOrEmpty(value) ? [] : [new TextSegment(value)]);
    }
}

/// <summary>预编辑文字的排版片段（只在排版中出现，不进入文档）。</summary>
internal sealed record PreeditSegment(string Text) : Segment
{
    public override int Length => Text.Length;
}
