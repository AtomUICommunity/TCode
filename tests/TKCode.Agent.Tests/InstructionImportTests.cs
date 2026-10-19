using TKCode.Agent.Coding.Prompt;
using TKCode.Agent.Coding.Tools;
using TKCode.Agent.Coding.Workspace;
using Xunit;

namespace TKCode.Agent.Tests;

/// <summary>AGENTS.md 的 @import（docs/design/memory.md §7）。</summary>
public sealed class InstructionImportTests
{
    private readonly string _root = Directory.CreateTempSubdirectory("tkcode-import-").FullName;

    private string Repo => Path.Combine(_root, "repo");

    private string WriteFile(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Find_SkipsCodeAndEmails_AndHandlesEscapedSpaces()
    {
        const string content = """
            @a.md first
            text @b.md, `@c.md` and ``x `@d.md` y``
            ```
            @e.md
            ```
            mail me@f.md
            @g\ h.md
            ~~~~
            ~~~
            @i.md
            ~~~~
            lone ` @j.md
            `x`@k.md and `y` @l.md
            """;

        Assert.Equal(["a.md", "b.md,", "g h.md", "j.md", "l.md"], InstructionImports.Find(content));
    }

    [Fact]
    public void Resolve_IsRelativeToTheImportingFile_AndRetriesWithoutTrailingPunctuation()
    {
        var importer = WriteFile("repo/docs/AGENTS.md", "");
        var style = WriteFile("repo/docs/style.md", "style");
        WriteFile("repo/docs/with space.md", "spaced");

        Assert.Equal(style, InstructionImports.Resolve(importer, "style.md"));
        Assert.Equal(style, InstructionImports.Resolve(importer, "style.md),"));
        Assert.Equal(style, InstructionImports.Resolve(importer, style));
        Assert.Equal(Path.Combine(Repo, "docs", "with space.md"), InstructionImports.Resolve(importer, "with space.md"));
        Assert.Null(InstructionImports.Resolve(importer, "missing.md"));
        Assert.Null(InstructionImports.Resolve(importer, "."));
    }

    [Fact]
    public void ContextFiles_FollowImportsDepthFirst_UpToFourHops_InsideTheProjectOnly()
    {
        Directory.CreateDirectory(Path.Combine(Repo, ".git"));
        var outside = WriteFile("outside.md", "outside");
        File.CreateSymbolicLink(Path.Combine(Repo, "link.md"), outside);
        WriteFile("repo/AGENTS.md", $"Root\n@docs/a.md\n@../outside.md\n@{outside}\n@link.md\n@AGENTS.md\n@other.md");
        WriteFile("repo/docs/a.md", "A @b.md");
        WriteFile("repo/docs/b.md", "B @c.md");
        WriteFile("repo/docs/c.md", "C @d.md @a.md");
        WriteFile("repo/docs/d.md", "D @e.md");
        WriteFile("repo/docs/e.md", "E is five hops away");
        WriteFile("repo/other.md", "Other");
        WriteFile("repo/sub/AGENTS.md", "Sub @../docs/a.md");

        var files = SystemPromptBuilder.LoadContextFiles(Path.Combine(Repo, "sub"))
            .Where(f => f.Path.StartsWith(_root, StringComparison.Ordinal)).ToList();

        string P(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(
            [P("repo/AGENTS.md"), P("repo/docs/a.md"), P("repo/docs/b.md"), P("repo/docs/c.md"), P("repo/docs/d.md"), P("repo/other.md"), P("repo/sub/AGENTS.md")],
            files.Select(f => f.Path));
        Assert.Equal("D @e.md", files[4].Content);
    }

    [Fact]
    public void Expand_WithoutScope_ImportsAnywhere()
    {
        var outside = WriteFile("elsewhere/notes.md", "notes");
        var global = WriteFile("home/AGENTS.md", $"@{outside}");
        var seen = new HashSet<string>(PathUtil.Comparer);

        var imported = InstructionImports.Expand(global, File.ReadAllText(global), null, seen.Add).ToList();

        Assert.Equal([(outside, "notes")], imported);
        Assert.Empty(InstructionImports.Expand(global, File.ReadAllText(global), PathUtil.RealPath(Repo), new HashSet<string>().Add));
    }

    [Fact]
    public void NestedInstructions_ExpandImportsOnce()
    {
        Directory.CreateDirectory(Path.Combine(Repo, ".git"));
        WriteFile("repo/pkg/AGENTS.md", "Package rules @notes.md");
        var notes = WriteFile("repo/pkg/notes.md", "Package notes");
        var file = WriteFile("repo/pkg/a.cs", "");
        var context = new ToolContext(new WorkspaceRoots(Repo), "session");

        var reminder = context.NestedInstructionsFor(file);

        Assert.NotNull(reminder);
        Assert.Contains("Package rules @notes.md", reminder);
        Assert.Contains($"Instructions from {notes} (imported by {Path.Combine(Repo, "pkg", "AGENTS.md")}):\nPackage notes", reminder);
        Assert.Null(context.NestedInstructionsFor(file));
    }
}
