using TCode.Desktop.ViewModels;
using TCode.Rpc;
using Xunit;

namespace TCode.Desktop.Tests;

/// <summary>文件改动：diff 行号、按文件合并、撤销的分支（不经过界面）。</summary>
public sealed class FileChangeTests
{
    [Fact]
    public void DiffLines_NumbersFollowHunkHeaders()
    {
        const string diff = """
            --- a/x.txt
            +++ b/x.txt
            @@ -3,3 +3,4 @@
             keep
            -old
            +new
            +more
             tail

            """;

        var lines = ParsedToolDetails.DiffLinesOf(diff);

        Assert.Equal(DiffLineKind.Hunk, lines[0].Kind);
        Assert.Equal((3, 3), (lines[1].OldNumber, lines[1].NewNumber));
        Assert.Equal((4, null), (lines[2].OldNumber, lines[2].NewNumber));
        Assert.Equal((null, 4), (lines[3].OldNumber, lines[3].NewNumber));
        Assert.Equal((null, 5), (lines[4].OldNumber, lines[4].NewNumber));
        Assert.Equal((5, 6), (lines[5].OldNumber, lines[5].NewNumber));
        Assert.Equal("+", lines[3].Sign);
    }

    [Fact]
    public void Group_KeepsFirstSnapshotAndLastFingerprint_PerFile()
    {
        var edits = new[]
        {
            Edit("/p/a.cs", before: "one", after: "h1", added: 1, removed: 1),
            Edit("/p/b.cs", before: "b0", after: "hb", added: 2, removed: 0),
            Edit("/p/a.cs", before: "two", after: "h2", added: 3, removed: 0),
        };

        var files = FileChangeSet.Group(edits, _ => Task.FromResult(new RevertFileResult(null)));

        Assert.Equal(["/p/a.cs", "/p/b.cs"], files.Select(f => f.Path).ToArray());
        var a = files[0];
        Assert.Equal("one", a.Before);
        Assert.Equal("h2", a.AfterSha256);
        Assert.Equal((4, 1), (a.Added, a.Removed));
        Assert.True(a.CanRevert);
    }

    [Fact]
    public async Task Revert_NewFile_SendsDeleteRequest()
    {
        RevertFileRequest? sent = null;
        var files = FileChangeSet.Group([Edit("/p/new.txt", created: true, after: "h")], request =>
        {
            sent = request;
            return Task.FromResult(new RevertFileResult(null));
        });

        await files[0].RevertCoreAsync();

        Assert.Equal(new RevertFileRequest("/p/new.txt", "h", null), sent);
        Assert.True(files[0].IsReverted);
    }

    [Fact]
    public async Task Revert_WithoutSnapshot_IsNotAvailable()
    {
        var called = false;
        var files = FileChangeSet.Group([Edit("/p/big.bin", before: null, after: "h")], _ =>
        {
            called = true;
            return Task.FromResult(new RevertFileResult(null));
        });

        Assert.False(files[0].CanRevert);
        await files[0].RevertCoreAsync();
        Assert.False(called);
    }

    [Fact]
    public async Task Revert_HostRefusal_KeepsFileAndShowsError()
    {
        var files = FileChangeSet.Group([Edit("/p/a.cs", before: "x", after: "h")],
            _ => Task.FromResult(new RevertFileResult("has changed since the agent edited it")));

        await files[0].RevertCoreAsync();

        Assert.False(files[0].IsReverted);
        Assert.True(files[0].HasError);
    }

    private static ParsedToolDetails Edit(string path, string? before = null, string? after = null, bool created = false,
        int added = 1, int removed = 0) => new()
    {
        Kind = "diff",
        Path = path,
        Before = before,
        AfterSha256 = after,
        Created = created,
        Added = added,
        Removed = removed,
        DiffLines = [new DiffLineViewModel("+x", DiffLineKind.Added)],
    };
}
