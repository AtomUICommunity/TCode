using TKCode.Agent.Diff;
using System.Text;
using System.Text.Json;
using TKCode.Agent.Coding.Tools;
using TKCode.Agent.Coding.Tools.Files;
using TKCode.Agent.Coding.Workspace;
using TKCode.Ai.Messages;
using Xunit;

namespace TKCode.Agent.Tests;

public sealed class FileToolTests
{
    private readonly string _root = Directory.CreateTempSubdirectory("tkcode-files-").FullName;
    private readonly ToolContext _context;

    public FileToolTests() => _context = new ToolContext(new WorkspaceRoots(_root), "test-session");

    private static Task<AgentToolResult> Run(IAgentTool tool, string json)
    {
        using var doc = JsonDocument.Parse(json);
        return tool.ExecuteAsync(new ToolInvocation("call", doc.RootElement.Clone()), CancellationToken.None);
    }

    private static string Text(AgentToolResult result) => string.Concat(result.Content.OfType<TextContent>().Select(t => t.Text));

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task Read_ReturnsLineNumbers_AndPagesLargeFiles()
    {
        Write("a.txt", string.Join('\n', Enumerable.Range(1, 2500).Select(i => $"line {i}")) + "\n");
        var first = await Run(new ReadTool(_context), """{"path":"a.txt"}""");
        var text = Text(first);
        Assert.StartsWith("     1\tline 1\n", text);
        Assert.Contains("[Showing lines 1-2000 of 2500. Use offset=2001 to continue.]", text);

        var next = Text(await Run(new ReadTool(_context), """{"path":"a.txt","offset":2400,"limit":2}"""));
        Assert.StartsWith("  2400\tline 2400\n  2401\tline 2401\n", next);
    }

    [Fact]
    public async Task Read_RejectsBinaryAndDirectories_AndReturnsImages()
    {
        File.WriteAllBytes(Path.Combine(_root, "bin.dat"), [1, 2, 0, 3]);
        Assert.True((await Run(new ReadTool(_context), """{"path":"bin.dat"}""")).IsError);
        Directory.CreateDirectory(Path.Combine(_root, "dir"));
        Assert.Contains("ls tool", Text(await Run(new ReadTool(_context), """{"path":"dir"}""")));

        File.WriteAllBytes(Path.Combine(_root, "pic.dat"), [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]);
        var image = await Run(new ReadTool(_context), """{"path":"pic.dat"}""");
        Assert.Equal("image/png", Assert.Single(image.Content.OfType<ImageContent>()).MimeType);

        _context.ModelSupportsImages = () => false;
        var noVision = await Run(new ReadTool(_context), """{"path":"pic.dat"}""");
        Assert.Empty(noVision.Content.OfType<ImageContent>());
    }

    [Fact]
    public async Task Write_RequiresReadBeforeOverwrite_AndCreatesNewFilesWithDiff()
    {
        var created = await Run(new WriteTool(_context), """{"path":"new/dir/file.cs","content":"class A {}\n"}""");
        Assert.False(created.IsError);
        var details = Assert.IsType<DiffDetails>(ToolDetails.FromJson(created.Details));
        Assert.True(details.Created);
        Assert.Equal(1, details.Added);

        Write("existing.txt", "old\n");
        var blocked = await Run(new WriteTool(_context), """{"path":"existing.txt","content":"new\n"}""");
        Assert.True(blocked.IsError);
        Assert.Contains("read", Text(blocked));

        await Run(new ReadTool(_context), """{"path":"existing.txt"}""");
        Assert.False((await Run(new WriteTool(_context), """{"path":"existing.txt","content":"new\n"}""")).IsError);
        Assert.Equal("new\n", File.ReadAllText(Path.Combine(_root, "existing.txt")));
    }

    [Fact]
    public async Task Edit_RejectsStaleFile_AndAllowsConsecutiveEdits()
    {
        var path = Write("s.txt", "alpha\nbeta\n");
        await Run(new ReadTool(_context), """{"path":"s.txt"}""");
        Assert.False((await Run(new EditTool(_context), """{"path":"s.txt","edits":[{"oldText":"alpha","newText":"ALPHA"}]}""")).IsError);
        // 自己写入后可以继续编辑，无需重新读取
        Assert.False((await Run(new EditTool(_context), """{"path":"s.txt","edits":[{"oldText":"beta","newText":"BETA"}]}""")).IsError);

        File.WriteAllText(path, "changed by someone else\n");
        var stale = await Run(new EditTool(_context), """{"path":"s.txt","edits":[{"oldText":"changed","newText":"x"}]}""");
        Assert.True(stale.IsError);
        Assert.Contains("modified since it was last read", Text(stale));
    }

    [Fact]
    public void Apply_MultipleEditsAgainstOriginal_AndReportsErrors()
    {
        var text = "a = 1\nb = 2\nc = 3\n";
        Assert.Equal("a = 10\nb = 2\nc = 30\n", EditTool.Apply(text, [new("a = 1", "a = 10"), new("c = 3", "c = 30")]));

        var duplicate = Assert.Throws<EditTool.EditException>(() => EditTool.Apply("x\nx\n", [new("x", "y")]));
        Assert.Contains("Found 2 occurrences", duplicate.Message);
        var overlap = Assert.Throws<EditTool.EditException>(() => EditTool.Apply(text, [new("a = 1\nb", "q"), new("b = 2", "r")]));
        Assert.Contains("overlap", overlap.Message);
        Assert.Throws<EditTool.EditException>(() => EditTool.Apply(text, [new("missing", "y")]));
        Assert.Throws<EditTool.EditException>(() => EditTool.Apply(text, [new("", "y")]));
    }

    [Fact]
    public void Apply_FuzzyMatch_KeepsOriginalBytesOnUnchangedLines()
    {
        // 文件里是弯引号和行尾空格；模型给的是直引号、没有行尾空格。
        var text = "say(“hello”);   \nkeep(‘x’);\nend();\n";
        var result = EditTool.Apply(text, [new("say(\"hello\");\nkeep('x');", "say(\"bye\");\nkeep('x');")]);
        Assert.Equal("say(\"bye\");\nkeep(‘x’);\nend();\n", result);
    }

    [Fact]
    public async Task Edit_PreservesCrLfAndBom_AndProducesDiffDetails()
    {
        var path = Path.Combine(_root, "win.cs");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("one\r\ntwo\r\nthree\r\n")]);
        await Run(new ReadTool(_context), """{"path":"win.cs"}""");
        var result = await Run(new EditTool(_context), """{"path":"win.cs","edits":[{"oldText":"two\nthree","newText":"2\n3"}]}""");

        Assert.False(result.IsError, Text(result));
        var bytes = File.ReadAllBytes(path);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal("one\r\n2\r\n3\r\n", Encoding.UTF8.GetString(bytes[3..]));
        var diff = Assert.IsType<DiffDetails>(ToolDetails.FromJson(result.Details));
        Assert.Equal((2, 2, 2), (diff.Added, diff.Removed, diff.FirstChangedLine ?? 0));
        Assert.Contains("-two\n-three\n+2\n+3\n", diff.Diff);
    }

    [Fact]
    public void ParseEdits_ToleratesCommonModelMistakes()
    {
        static IReadOnlyList<EditTool.Replacement> Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return EditTool.ParseEdits(doc.RootElement);
        }
        Assert.Single(Parse("""{"path":"a","edits":"[{\"oldText\":\"a\",\"newText\":\"b\"}]"}"""));
        Assert.Single(Parse("""{"path":"a","edits":{"oldText":"a","newText":"b"}}"""));
        Assert.Single(Parse("""{"path":"a","old_string":"a","new_string":"b"}"""));
    }

    [Fact]
    public async Task NestedContextFiles_AreAttachedOnceWhenEnteringSubdirectory()
    {
        Write("pkg/AGENTS.md", "pkg rules");
        Write("pkg/a.txt", "a\n");
        Write("pkg/b.txt", "b\n");
        var first = Text(await Run(new ReadTool(_context), """{"path":"pkg/a.txt"}"""));
        Assert.Contains("<system-reminder>", first);
        Assert.Contains("pkg rules", first);
        var second = Text(await Run(new ReadTool(_context), """{"path":"pkg/b.txt"}"""));
        Assert.DoesNotContain("pkg rules", second);
    }

    [Fact]
    public void LineDiff_ProducesHunksWithContext()
    {
        var oldText = string.Join('\n', Enumerable.Range(1, 30)) + "\n";
        var newText = oldText.Replace("\n5\n", "\nfive\n").Replace("\n25\n", "\ntwenty-five\n");
        var result = LineDiff.Compute(oldText, newText, "n.txt");
        Assert.Equal(2, result.UnifiedDiff.Split("@@ -").Length - 1);
        Assert.Contains("@@ -1,9 +1,9 @@", result.UnifiedDiff);
        Assert.Equal(5, result.FirstChangedLine);
    }
}
