using Avalonia.VisualTree;
using StreamMark.Avalonia.Rendering;
using TKCode.Desktop.Previews;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using Xunit;

namespace TKCode.Desktop.Tests;

public sealed partial class ReadmeScreenshotTests
{
    private static async Task CaptureArticleAsync(MainWindowViewModel vm, MainWindow window, string project, string output)
    {
        window.Height = 960;
        vm.Workbench.IsMaximized = true;
        var samples = new (string Name, string Markdown)[]
        {
            ("markdown-rich", RichMarkdown),
            ("markdown-diagram", DiagramMarkdown),
            ("markdown-diff", DiffMarkdown),
        };
        foreach (var (name, markdown) in samples)
        {
            var file = Path.Combine(project, name + ".md");
            File.WriteAllText(file, markdown);
            vm.Workbench.OpenFile(file);
            await WaitUntil(() => vm.Workbench.ActiveFile?.Preview is MarkdownPreviewViewModel, "markdown preview loaded");
            if (name == "markdown-diagram")
            {
                await WaitUntil(() => window.GetVisualDescendants().OfType<DiagramBlockView>().Any(v => v.IsShowingDiagram), "article diagram rendered");
            }
            await Settle(40);
            SaveTo(window, output, name + "-zh");
        }
        Assert.True(File.Exists(Path.Combine(output, "markdown-rich-zh.png")));
    }

    private const string RichMarkdown = """
        # StreamMark 原生 Markdown 渲染

        同一篇文档，组合 **中文与粗体**、*强调*、`行内代码`、表格、代码高亮和数学公式。

        | 内容 | 渲染方式 | 使用场景 |
        | :--- | :--- | :--- |
        | C# 代码 | TextMate 语法高亮 | 实现与审查 |
        | 数学公式 | RaTeX 原生排版 | 算法推导 |
        | 架构图 | Mermaid 矢量绘制 | 设计讨论 |

        ```csharp
        // 消费流式增量，交给独立的 Markdown 文档处理
        var reply = new MarkdownStream(profile: services.Profile);
        transcript.Entries.Add(new MarkdownEntry(reply));
        await foreach (var delta in response)
        {
            reply.Push(delta.Text);
        }
        reply.Complete();
        ```

        ### 数学公式与正文一起排版

        自注意力的核心计算：

        $$
        \mathrm{Attention}(Q,K,V)=\mathrm{softmax}\left(\frac{QK^{\mathsf T}}{\sqrt{d_k}}\right)V
        $$

        > 解析与重型渲染放到后台，界面线程按帧应用变化。
        """;

    private const string DiagramMarkdown = """
        # Agent 的运行时与事件流

        桌面端负责交互，独立 Host 负责会话；不同运行时汇入同一套转录界面。

        ```mermaid
        flowchart TD
          desktop["Avalonia 桌面端"] -->|"JSON-RPC / stdio"| host["Agent Host"]
          host --> builtin["内置 Agent"]
          host --> claude["本机 Claude Code"]
          host --> codex["Codex app-server"]
          builtin --> api["模型 API"]
          claude --> events["统一 SessionEvent"]
          codex --> events
          api --> events
          events --> markdown["StreamMark 转录与渲染"]
        ```

        | 层次 | 负责的事情 |
        | :--- | :--- |
        | Desktop | 输入、审批、文件工作台与渲染 |
        | Agent Host | 会话路由、工具执行与持久化 |
        | Runtime | 模型请求、工具循环与中断 |
        """;

    private const string DiffMarkdown = """
        # 把 Agent 的修改展示清楚

        Markdown 中的 diff 使用增删背景和语法颜色，帮助用户核对实际改动。

        ```diff
        --- a/ForecastService.cs
        +++ b/ForecastService.cs
        @@ -1,6 +1,9 @@
         public async Task<Forecast> GetAsync(string city)
         {
        -    return await client.FetchAsync(city);
        +    return await cache.GetOrCreateAsync(city, entry =>
        +    {
        +        entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
        +        return client.FetchAsync(city);
        +    });
         }
        ```

        ### 验证步骤

        - [x] 检查缓存键是否区分城市
        - [x] 检查过期时间
        - [ ] 运行测试并审查结果

        <details open>
        <summary>为什么保留操作细节</summary>

        用户可以查看工具输入、运行输出和文件差异，再决定是否接受修改。

        </details>

        > 工具执行状态、文件审查与最终回复，应当能够对应起来。
        """;
}
