using TCode.Desktop.Controls.Composer;
using Xunit;

namespace TCode.Desktop.Tests;

/// <summary>输入框文档模型（docs/design/composer.md §6.1）：tag 占一个位置、整体删除，撤销按单词合并。</summary>
public sealed class ComposerDocumentTests
{
    private static readonly TagSegment Foo = new(TagKind.File, "src/Foo.cs", "Foo.cs");

    [Fact]
    public void Tag_OccupiesOnePosition_AndProjectsToPlainText()
    {
        var doc = new ComposerDocument();
        doc.Insert(0, "看下  的 bug");
        doc.InsertTag(3, Foo);

        Assert.Equal(10, doc.Length);
        Assert.Equal(Foo, doc.TagAt(3));
        Assert.Null(doc.TagAt(2));
        Assert.Equal("看下 @src/Foo.cs 的 bug", doc.ToPlainText());
        Assert.Equal(3, doc.Segments.Count);
    }

    [Fact]
    public void PlainText_QuotesPathsWithSpaces_AndMarksFolders()
    {
        Assert.Equal("@\"My Docs/a.md\"", new TagSegment(TagKind.File, "My Docs/a.md", "a.md").ToPlainText());
        Assert.Equal("@src/", new TagSegment(TagKind.Folder, "src", "src").ToPlainText());
        Assert.Equal("/git:commit", new TagSegment(TagKind.Command, "git:commit", "git:commit").ToPlainText());
    }

    [Fact]
    public void DeletingAcrossTag_RemovesItWhole_AndMergesText()
    {
        var doc = new ComposerDocument([new TextSegment("a "), Foo, new TextSegment(" b")]);

        doc.Delete(2, 1, EditKind.DeleteBackward);

        Assert.Equal("a  b", doc.ToPlainText());
        Assert.Single(doc.Segments);
    }

    [Fact]
    public void Slice_KeepsTagsAndPartialText()
    {
        var doc = new ComposerDocument([new TextSegment("hello "), Foo, new TextSegment(" world")]);

        var slice = doc.Slice(3, 6);

        Assert.Equal([new TextSegment("lo "), Foo, new TextSegment(" w")], slice);
    }

    [Fact]
    public void Typing_MergesByWord_InUndo()
    {
        var doc = new ComposerDocument();
        var caret = 0;
        foreach (var c in "hello world")
        {
            doc.Insert(caret, c.ToString(), EditKind.Typing);
            caret++;
        }

        Assert.Equal(6, doc.Undo());
        Assert.Equal("hello ", doc.ToPlainText());
        Assert.Equal(0, doc.Undo());
        Assert.Equal("", doc.ToPlainText());
        Assert.False(doc.CanUndo);

        Assert.Equal(6, doc.Redo());
        Assert.Equal(11, doc.Redo());
        Assert.Equal("hello world", doc.ToPlainText());
    }

    [Fact]
    public void TagInsertion_IsItsOwnUndoStep()
    {
        var doc = new ComposerDocument();
        doc.Insert(0, "a", EditKind.Typing);
        doc.InsertTag(1, Foo);
        doc.Insert(2, "b", EditKind.Typing);

        doc.Undo();
        Assert.Equal("a@src/Foo.cs", doc.ToPlainText());
        doc.Undo();
        Assert.Equal("a", doc.ToPlainText());
    }

    [Fact]
    public void ConsecutiveBackspaces_MergeIntoOneUndo_AndRestoreTag()
    {
        var doc = new ComposerDocument([new TextSegment("x "), Foo, new TextSegment("yz")]);
        for (var caret = doc.Length; caret > 1; caret--)
        {
            doc.Delete(caret - 1, 1, EditKind.DeleteBackward);
        }
        Assert.Equal("x", doc.ToPlainText());

        Assert.Equal(5, doc.Undo());
        Assert.Equal([new TextSegment("x "), Foo, new TextSegment("yz")], doc.Segments);
        Assert.False(doc.CanUndo);
    }

    [Fact]
    public void BreakUndoGroup_StartsNewStep()
    {
        var doc = new ComposerDocument();
        doc.Insert(0, "ab", EditKind.Typing);
        doc.BreakUndoGroup();
        doc.Insert(2, "cd", EditKind.Typing);

        doc.Undo();
        Assert.Equal("ab", doc.ToPlainText());
    }

    [Fact]
    public void NewEdit_ClearsRedo()
    {
        var doc = new ComposerDocument();
        doc.Insert(0, "a");
        doc.Undo();
        doc.Insert(0, "b");

        Assert.False(doc.CanRedo);
        Assert.Null(doc.Redo());
    }

    [Fact]
    public void WordBoundaries_TreatTagAsOneWord()
    {
        // "foo.bar " + [tag] + " baz"
        var doc = new ComposerDocument([new TextSegment("foo.bar "), Foo, new TextSegment(" baz")]);

        Assert.Equal(13, doc.NextWordBoundary(9));
        Assert.Equal(9, doc.NextWordBoundary(8));
        Assert.Equal(8, doc.PreviousWordBoundary(10));
        Assert.Equal(8, doc.PreviousWordBoundary(9));
        Assert.Equal(4, doc.PreviousWordBoundary(8));
        Assert.Equal(3, doc.NextWordBoundary(0));
        Assert.Equal((8, 9), doc.WordAt(8));
        Assert.Equal((10, 13), doc.WordAt(11));
    }

    [Fact]
    public void SetContent_ResetsUndo_AndRaisesChange()
    {
        var doc = new ComposerDocument();
        doc.Insert(0, "draft");
        DocumentChange? change = null;
        doc.Changed += c => change = c;

        doc.SetContent([]);

        Assert.True(doc.IsEmpty);
        Assert.False(doc.CanUndo);
        Assert.Equal(new DocumentChange(0, 5, 0), change);
    }

    [Fact]
    public void OutOfRangeEdits_AreClamped()
    {
        var doc = new ComposerDocument([new TextSegment("abc")]);

        doc.Delete(2, 100);
        doc.Insert(99, "!");

        Assert.Equal("ab!", doc.ToPlainText());
        Assert.Null(doc.CharAt(3));
        Assert.Equal('!', doc.CharAt(2));
    }
}
