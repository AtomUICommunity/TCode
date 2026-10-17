using System.Text;
using System.Text.Json;
using TCode.Agent.Coding.Tools;
using TCode.Agent.Coding.Tools.Files;
using TCode.Agent.Coding.Workspace;
using TCode.Ai.Messages;
using Xunit;

namespace TCode.Agent.Tests;

public sealed class FileRevertTests
{
    private readonly string _root = Directory.CreateTempSubdirectory("tcode-revert-").FullName;
    private readonly ToolContext _context;

    public FileRevertTests() => _context = new ToolContext(new WorkspaceRoots(_root), "test-session");

    private static Task<AgentToolResult> Run(IAgentTool tool, string json)
    {
        using var doc = JsonDocument.Parse(json);
        return tool.ExecuteAsync(new ToolInvocation("call", doc.RootElement.Clone()), CancellationToken.None);
    }

    private static DiffDetails Details(AgentToolResult result) => (DiffDetails)ToolDetails.FromJson(result.Details)!;

    [Fact]
    public async Task Edit_RecordsSnapshotAndAfterHash_AndRevertRestoresOriginal()
    {
        var path = Path.Combine(_root, "a.txt");
        File.WriteAllText(path, "one\ntwo\nthree\n");
        await Run(new ReadTool(_context), $$"""{"path":"{{Escape(path)}}"}""");

        var result = await Run(new EditTool(_context), $$"""{"path":"{{Escape(path)}}","edits":[{"oldText":"two","newText":"TWO"}]}""");
        var details = Details(result);

        Assert.Equal("one\ntwo\nthree\n", details.Before);
        Assert.Equal(FileRevert.Sha256(File.ReadAllBytes(path)), details.AfterSha256);
        Assert.Null(FileRevert.Revert(path, details.AfterSha256!, details.Before));
        Assert.Equal("one\ntwo\nthree\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task Revert_RefusesWhenFileChangedSinceAgentEdit()
    {
        var path = Path.Combine(_root, "b.txt");
        File.WriteAllText(path, "alpha\n");
        await Run(new ReadTool(_context), $$"""{"path":"{{Escape(path)}}"}""");
        var details = Details(await Run(new WriteTool(_context), $$"""{"path":"{{Escape(path)}}","content":"beta\n"}"""));

        File.WriteAllText(path, "user edit\n");
        var error = FileRevert.Revert(path, details.AfterSha256!, details.Before);

        Assert.NotNull(error);
        Assert.Equal("user edit\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task Revert_DeletesFilesCreatedByAgent()
    {
        var path = Path.Combine(_root, "new.txt");
        var details = Details(await Run(new WriteTool(_context), $$"""{"path":"{{Escape(path)}}","content":"hi\n"}"""));

        Assert.True(details.Created);
        Assert.Null(details.Before);
        Assert.Null(FileRevert.Revert(path, details.AfterSha256!, null));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Revert_KeepsBomAndCrlfOfTheFile()
    {
        var path = Path.Combine(_root, "crlf.txt");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("x\r\ny\r\n")]);
        await Run(new ReadTool(_context), $$"""{"path":"{{Escape(path)}}"}""");
        var details = Details(await Run(new EditTool(_context), $$"""{"path":"{{Escape(path)}}","edits":[{"oldText":"y","newText":"Y"}]}"""));

        Assert.Null(FileRevert.Revert(path, details.AfterSha256!, details.Before));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(path)[..3]);
        Assert.Equal("x\r\ny\r\n", Encoding.UTF8.GetString(File.ReadAllBytes(path)[3..]));
    }

    [Fact]
    public void Revert_ReportsMissingFileWhenRestoreNeeded()
    {
        var error = FileRevert.Revert(Path.Combine(_root, "gone.txt"), "abc", "text");
        Assert.NotNull(error);
    }

    private static string Escape(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);
}
