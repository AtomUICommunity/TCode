using System.Diagnostics;
using TKCode.Agent.Coding.Tools.Search;
using TKCode.Agent.Coding.Workspace;
using Xunit;

namespace TKCode.Agent.Tests;

/// <summary>输入框 @ 文件的索引（docs/design/composer.md §8.1）：遵守 .gitignore、推出文件夹、文件变化后刷新、大索引的搜索速度。</summary>
public sealed class WorkspaceFileIndexTests
{
    [Theory]
    [InlineData(SearchBackendKind.Auto)]
    [InlineData(SearchBackendKind.Managed)]
    public async Task Search_RespectsGitignore_AndIncludesFolders(SearchBackendKind backend)
    {
        var previous = SearchBackend.Preferred;
        SearchBackend.Preferred = backend;
        try
        {
            var root = Directory.CreateTempSubdirectory("tkcode-index-").FullName;
            Write(root, ".gitignore", "bin/\n*.log\n");
            Write(root, "src/Composer/ComposerEditor.cs", "");
            Write(root, "src/Composer/ComposerDocument.cs", "");
            Write(root, "src/Views/MainWindow.axaml", "");
            Write(root, "README.md", "");
            Write(root, "bin/Debug/app.dll", "");
            Write(root, "debug.log", "");
            Write(root, "node_modules/pkg/index.js", "");
            using var index = new WorkspaceFileIndex(root);

            var hits = (await index.SearchAsync("comped", 10, CancellationToken.None)).Hits;
            Assert.Equal("src/Composer/ComposerEditor.cs", hits[0].Path);
            Assert.NotEmpty(hits[0].Positions);

            var all = (await index.SearchAsync("", 50, CancellationToken.None)).Hits.Select(h => h.Path).ToList();
            Assert.Contains("src/", all);
            Assert.Contains("src/Composer/", all);
            Assert.DoesNotContain(all, p => p.StartsWith("bin", StringComparison.Ordinal) || p.EndsWith(".log", StringComparison.Ordinal) || p.Contains("node_modules"));
            // 空查询：浅层在前，同层文件夹在前
            Assert.Equal(["src/", ".gitignore", "README.md"], all.Take(3));

            var folder = (await index.SearchAsync("views", 5, CancellationToken.None)).Hits[0];
            Assert.Equal(("src/Views/", true), (folder.Path, folder.IsDirectory));
        }
        finally
        {
            SearchBackend.Preferred = previous;
        }
    }

    [Fact]
    public async Task FileChanges_AreReflectedInLaterSearches()
    {
        var root = Directory.CreateTempSubdirectory("tkcode-index-").FullName;
        Write(root, "a.txt", "");
        using var index = new WorkspaceFileIndex(root);
        Assert.Empty((await index.SearchAsync("fresh", 5, CancellationToken.None)).Hits);

        Write(root, "fresh.md", "");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var hits = (await index.SearchAsync("fresh", 5, CancellationToken.None)).Hits;
            if (hits.Any(h => h.Path == "fresh.md"))
            {
                break;
            }
            Assert.True(DateTime.UtcNow < deadline, "new file not indexed");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task LargeIndex_SearchIsFast()
    {
        // 约 20 万条合成路径：目录名取自有限的词表（接近真实仓库的目录分布），文件名随机
        var random = new Random(7);
        string Word() => new string(Enumerable.Range(0, random.Next(3, 10)).Select(_ => (char)('a' + random.Next(26))).ToArray());
        var vocabulary = Enumerable.Range(0, 30).Select(_ => Word()).ToArray();
        var files = Enumerable.Range(0, 170_000).Select(i =>
            string.Join('/', Enumerable.Range(0, random.Next(1, 4)).Select(_ => vocabulary[random.Next(vocabulary.Length)])) + "/" + Word() +
            (i % 3 == 0 ? "View.cs" : ".ts")).ToList();
        files.Add("src/TKCode.Desktop/Views/ComposerView.axaml.cs");
        var root = Directory.CreateTempSubdirectory("tkcode-index-").FullName;
        using var index = new WorkspaceFileIndex(root, (_, _) => Task.FromResult<IReadOnlyList<string>>(files));

        var first = await index.SearchAsync("composerview", 50, CancellationToken.None);
        Assert.True(first.Complete);
        Assert.Equal("src/TKCode.Desktop/Views/ComposerView.axaml.cs", first.Hits[0].Path);

        var timer = Stopwatch.StartNew();
        const int Rounds = 5;
        foreach (var query in new[] { "cv", "compview", "src/view", "a", "abcdefg" }.Take(Rounds))
        {
            await index.SearchAsync(query, 50, CancellationToken.None);
        }
        var average = timer.Elapsed.TotalMilliseconds / Rounds;
        TestContext.Current.TestOutputHelper?.WriteLine($"average search: {average:F1} ms over {files.Count} files");
        // 设计目标 15 ms（Release、多核）；测试机与 Debug 构建留足余量，只防止数量级的退化
        Assert.True(average < 400, $"average search took {average:F0} ms");
    }

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
