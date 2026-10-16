using System.Text.Json;
using AngleSharp.Html.Parser;
using TCode.Agent;
using TCode.Agent.Coding.Tools;
using TCode.Agent.Coding.Tools.Files;
using TCode.Agent.Coding.Tools.Search;
using TCode.Agent.Coding.Tools.Tasks;
using TCode.Agent.Coding.Tools.Web;
using TCode.Agent.Coding.Workspace;
using TCode.Ai.Messages;

namespace TCode.AgentHost;

/// <summary>
/// `TCode.AgentHost --self-test`：在 NativeAOT 二进制里实际跑一遍依赖反射风险较高的代码路径
/// （AngleSharp 解析与正文提取、源生成 JSON、正则、文件工具），用于验证 AOT 发布可用。成功时退出码为 0。
/// </summary>
public static class SelfTest
{
    public static async Task<int> RunAsync()
    {
        var failures = new List<string>();
        void Check(bool condition, string name)
        {
            Console.Error.WriteLine($"{(condition ? "ok  " : "FAIL")} {name}");
            if (!condition)
            {
                failures.Add(name);
            }
        }

        try
        {
            var html = "<html><head><title>T</title></head><body><nav>menu</nav><article><h2>Head</h2><p>Hello, <a href='/x'>link</a>, " +
                       string.Concat(Enumerable.Repeat("long enough text, with commas, ", 20)) + "</p><table><tr><th>a</th></tr><tr><td>1</td></tr></table>" +
                       "<pre><code class='language-cs'>var x = 1;</code></pre></article></body></html>";
            var document = new HtmlParser().ParseDocument(html);
            var extracted = ReadabilityExtractor.Extract(document);
            var markdown = new HtmlToMarkdown(new Uri("https://example.com/")).Convert(extracted.Content);
            Check(markdown.Contains("## Head") && markdown.Contains("[link](https://example.com/x)") && markdown.Contains("```cs")
                  && markdown.Contains("| a |") && !markdown.Contains("menu"), "html → markdown");

            var details = new DiffDetails("a.cs", "@@", 1, 2, 3).ToJson();
            Check(ToolDetails.FromJson(details) is DiffDetails { Removed: 2 }, "tool details json");
            Check(ToolDetails.FromJson(new TaskListDetails([new TaskItem("1", "s", "d", "pending")]).ToJson()) is TaskListDetails { Tasks.Count: 1 },
                "task details json");

            var diff = LineDiff.Compute("a\nb\nc\n", "a\nB\nc\n", "f.txt");
            Check(diff.Added == 1 && diff.Removed == 1 && diff.UnifiedDiff.Contains("+B"), "line diff");
            Check(EditTool.Apply("x = “y”;\n", [new EditTool.Replacement("x = \"y\";", "x = \"z\";")]) == "x = \"z\";\n", "fuzzy edit");

            var root = Directory.CreateTempSubdirectory("tcode-selftest-").FullName;
            await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "needle here\n");
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "ignored.txt\n");
            await File.WriteAllTextAsync(Path.Combine(root, "ignored.txt"), "needle ignored\n");
            var context = new ToolContext(new WorkspaceRoots(root), "selftest");
            SearchBackend.Preferred = SearchBackendKind.Managed;
            using var args = JsonDocument.Parse("""{"pattern":"needle"}""");
            var grep = await new GrepTool(context).ExecuteAsync(new ToolInvocation("1", args.RootElement), CancellationToken.None);
            var grepText = string.Concat(grep.Content.OfType<TextContent>().Select(t => t.Text));
            Check(grepText.Contains("a.txt:1:") && !grepText.Contains("ignored.txt"), "managed grep");

            using var readArgs = JsonDocument.Parse("""{"path":"a.txt"}""");
            var read = await new ReadTool(context).ExecuteAsync(new ToolInvocation("2", readArgs.RootElement), CancellationToken.None);
            Check(string.Concat(read.Content.OfType<TextContent>().Select(t => t.Text)).StartsWith("     1\tneedle", StringComparison.Ordinal), "read");

            Check(ReadOnlyCommands.IsReadOnly("git status && rg -n x | head") && !ReadOnlyCommands.IsReadOnly("rm -rf x"), "read-only commands");
            new TaskList().Create("t", "d", null);
            Directory.Delete(root, recursive: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL exception: {ex}");
            failures.Add(ex.GetType().Name);
        }
        Console.Error.WriteLine(failures.Count == 0 ? "self-test passed" : $"self-test failed: {string.Join(", ", failures)}");
        return failures.Count == 0 ? 0 : 1;
    }
}
