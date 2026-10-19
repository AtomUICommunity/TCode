using TKCode.Agent.Diff;
using System.Text.Json;
using AngleSharp.Html.Parser;
using TKCode.Agent;
using TKCode.Agent.Coding.Tools;
using TKCode.Agent.Coding.Tools.Files;
using TKCode.Agent.Coding.Tools.Search;
using TKCode.Agent.Coding.Tools.Tasks;
using TKCode.Agent.Coding.Tools.Web;
using TKCode.Agent.Coding.Workspace;
using TKCode.Ai.Messages;

namespace TKCode.AgentHost;

/// <summary>
/// `TKCode.AgentHost --self-test`：在 NativeAOT 二进制里实际跑一遍依赖反射风险较高的代码路径
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

            var root = Directory.CreateTempSubdirectory("tkcode-selftest-").FullName;
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
            // Plan 模式（docs/design/plan-mode.md）：只读命令规则、审批 Details、plan/review 的源生成序列化
            Check(!PermissionPolicy.Evaluate(new TKCode.Agent.ToolPermission(TKCode.Agent.ToolAccess.Execute, Command: "rm -rf x"),
                TKCode.Agent.PermissionMode.FullAccess, new WorkspaceRoots(root), new SessionApprovals(), readOnly: true).Allowed, "plan read-only command");
            Check(System.Text.Json.JsonDocument.Parse(TKCode.Agent.Plans.PlanReviews.DetailsJson("/p.md", "# P",
                new TKCode.Agent.Plans.PlanReviewResult(TKCode.Agent.Plans.PlanReviewOutcome.Approved, TKCode.Agent.Plans.PlanReviewChoice.Auto)))
                .RootElement.GetProperty("choice").GetString() == "auto", "plan review details");
            var review = System.Text.Json.JsonSerializer.Serialize(new TKCode.Rpc.PlanReviewRequest("s", "r", "builtin", null, "/p.md", "# P", ["auto", "ask"], "ask"),
                TKCode.Rpc.RpcJsonContext.Default.PlanReviewRequest);
            Check(System.Text.Json.JsonSerializer.Deserialize(review, TKCode.Rpc.RpcJsonContext.Default.PlanReviewRequest) is { Choices.Count: 2 }, "plan/review json");
            Check(TKCode.Agent.Coding.Assets.SessionRole.Plan.SectionFor("/p.md", false)!.Text.Contains("Plan file: /p.md\nIt does not exist yet"), "plan mode section");
            new TaskList().Create("t", "d", null);
            Directory.Delete(root, recursive: true);

            // MCP：TKCODE_SELFTEST_MCP 指向一个 stdio MCP 服务器（例如 AOT 发布的 tests/TKCode.Mcp.TestServer）时，实际连上并调用 echo
            if (Environment.GetEnvironmentVariable("TKCODE_SELFTEST_MCP") is { Length: > 0 } mcpServer)
            {
                var config = TKCode.Config.McpServerConfig.From("selftest", new TKCode.Config.McpServerSettings { Command = mcpServer, StartupTimeout = 20 },
                    TKCode.Config.McpConfigSource.Global);
                await using var mcp = new TKCode.Mcp.McpManager(new TKCode.Mcp.McpHost(), new TKCode.Mcp.McpTrustStore(Path.GetTempFileName()), () => [config]);
                var source = mcp.CreateSource(new ToolContext(new WorkspaceRoots(Path.GetTempPath()), "selftest-mcp"));
                var set = await source.RefreshAsync(true, CancellationToken.None);
                var echo = set.Tools.FirstOrDefault(t => t.Name == "mcp__selftest__echo");
                Check(echo is not null && set.Sections.Count == 1, "mcp tools/list");
                if (echo is not null)
                {
                    using var echoArgs = JsonDocument.Parse("""{"text":"aot"}""");
                    var result = await echo.ExecuteAsync(new ToolInvocation("3", echoArgs.RootElement), CancellationToken.None);
                    Check(string.Concat(result.Content.OfType<TextContent>().Select(t => t.Text)) == "echo: aot" && result.Details is { Length: > 0 },
                        "mcp tools/call");
                }
                await source.DisposeAsync();
            }
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
