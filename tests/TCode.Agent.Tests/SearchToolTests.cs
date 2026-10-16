using System.Text.Json;
using TCode.Agent.Coding.Processes;
using TCode.Agent.Coding.Tools;
using TCode.Agent.Coding.Tools.Search;
using TCode.Agent.Coding.Workspace;
using TCode.Ai.Messages;
using Xunit;

namespace TCode.Agent.Tests;

/// <summary>搜索工具会切换全局的后端偏好，因此放在同一个测试类里串行执行。</summary>
public sealed class SearchToolTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tcode-search-").FullName;
    private readonly ToolContext _context;

    public SearchToolTests()
    {
        _context = new ToolContext(new WorkspaceRoots(_root), "test-session");
        Write(".gitignore", "build/\n*.log\n!keep.log\n");
        Write("src/App.cs", "class App\n{\n    // TODO: wire up\n    void Run() {}\n}\n");
        Write("src/Util.cs", "static class Util { /* todo later */ }\n");
        Write("src/deep/Nested.cs", "// TODO nested\n");
        Write("build/Gen.cs", "// TODO generated\n");
        Write("debug.log", "TODO in log\n");
        Write("keep.log", "TODO kept\n");
        Write(".hidden/conf.cs", "// TODO hidden\n");
    }

    public void Dispose() => SearchBackend.Preferred = SearchBackendKind.Auto;

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private async Task<string> Run(IAgentTool tool, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = await tool.ExecuteAsync(new ToolInvocation("call", doc.RootElement.Clone()), CancellationToken.None);
        Assert.False(result.IsError, string.Concat(result.Content.OfType<TextContent>().Select(t => t.Text)));
        return string.Concat(result.Content.OfType<TextContent>().Select(t => t.Text));
    }

    private async Task InitGitAsync()
    {
        var git = ProcessRunner.FindExecutable("git")!;
        await ProcessRunner.RunAsync(git, ["init", "-q"], _root, CancellationToken.None);
    }

    public static TheoryData<SearchBackendKind> Backends()
    {
        var data = new TheoryData<SearchBackendKind> { SearchBackendKind.Managed };
        if (ProcessRunner.FindExecutable("git") is not null)
        {
            data.Add(SearchBackendKind.GitGrep);
        }
        if (ProcessRunner.FindExecutable("rg") is not null)
        {
            data.Add(SearchBackendKind.Ripgrep);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Grep_RespectsGitignore_IncludesDotfiles_AndFormatsMatches(SearchBackendKind backend)
    {
        if (backend == SearchBackendKind.GitGrep)
        {
            await InitGitAsync();
        }
        SearchBackend.Preferred = backend;

        var content = await Run(new GrepTool(_context), """{"pattern":"TODO"}""");
        Assert.Contains("src/App.cs:3:     // TODO: wire up", content);
        Assert.Contains("src/deep/Nested.cs:1: // TODO nested", content);
        Assert.Contains(".hidden/conf.cs:1:", content);
        Assert.Contains("keep.log:1:", content);
        Assert.DoesNotContain("build/Gen.cs", content);
        Assert.DoesNotContain("debug.log", content);

        var files = await Run(new GrepTool(_context), """{"pattern":"todo","ignoreCase":true,"glob":"*.cs","outputMode":"files_with_matches"}""");
        Assert.Contains("src/Util.cs", files);
        Assert.DoesNotContain("keep.log", files);

        var withContext = await Run(new GrepTool(_context), """{"pattern":"wire up","path":"src/App.cs","context":1,"literal":true}""");
        Assert.Contains("src/App.cs-2- {", withContext);
        Assert.Contains("src/App.cs:3:", withContext);
        Assert.Contains("src/App.cs-4-", withContext);

        Assert.Contains("No matches", await Run(new GrepTool(_context), """{"pattern":"does-not-exist-anywhere"}"""));
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Glob_MatchesAnyDepthAndAnchoredPatterns(SearchBackendKind backend)
    {
        SearchBackend.Preferred = backend == SearchBackendKind.Ripgrep ? backend : SearchBackendKind.Managed;
        var all = await Run(new GlobTool(_context), """{"pattern":"*.cs"}""");
        Assert.Contains("src/deep/Nested.cs", all);
        Assert.Contains(".hidden/conf.cs", all);
        Assert.DoesNotContain("build/Gen.cs", all);

        var anchored = await Run(new GlobTool(_context), """{"pattern":"src/*.cs"}""");
        Assert.Contains("src/App.cs", anchored);
        Assert.DoesNotContain("Nested.cs", anchored);

        var braces = await Run(new GlobTool(_context), """{"pattern":"**/{App,Nested}.cs"}""");
        Assert.Equal(2, braces.Trim().Split('\n').Length);
    }

    [Fact]
    public async Task Ls_ListsSortedEntriesWithDirectorySuffix()
    {
        var text = await Run(new LsTool(_context), "{}");
        var lines = text.Trim().Split('\n');
        Assert.Contains(".gitignore", lines);
        Assert.Contains("src/", lines);
        Assert.Equal(lines.Order(StringComparer.OrdinalIgnoreCase), lines);
    }
}
