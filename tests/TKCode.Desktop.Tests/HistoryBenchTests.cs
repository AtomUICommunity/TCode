using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>
/// 大会话（300 轮、每轮 20KB 工具输出）：打开时只加载最近 20 轮，滚动到顶部时分页加载更早的消息且阅读位置不跳。
/// 打开耗时写到临时目录的 tkcode-bench.txt。
/// </summary>
public sealed class HistoryBenchTests
{
    [Fact]
    public async Task OpenLargeSession()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-bench-ui-").FullName;
        var project = Directory.CreateTempSubdirectory("tkcode-bench-proj-").FullName;
        var dir = Path.Combine(home, "sessions", "--bench--");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "20261016T000000_" + new string('b', 32) + ".jsonl");
        BenchData.Write(file, project);
        Environment.SetEnvironmentVariable("TKCODE_HOME", home);
        Environment.SetEnvironmentVariable("TKCODE_AGENTHOST_DIR", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/TKCode.AgentHost/bin/Debug/net10.0")));
        Environment.SetEnvironmentVariable("TKCODE_FAKE_PROVIDER", "1");
        Environment.SetEnvironmentVariable("TKCODE_EXTERNAL_RUNTIMES", "0");
        var settings = new StateService(Path.Combine(home, "state.json"));
        settings.Current.Workspaces.Add(project);
        settings.Save();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        Program.ConfigureServices(services);
        await using var provider = services.BuildServiceProvider();
        await HeadlessApp.Session.Dispatch(async () =>
        {
            var vm = provider.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm, Width = 1100, Height = 720 };
            window.Show();
            while (vm.Sidebar.VisibleSessions.Count == 0)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(20);
            }
            var item = vm.Sidebar.VisibleSessions[0];
            var watch = Stopwatch.StartNew();
            await vm.Conversation.OpenSessionCommand.ExecuteAsync(item);
            var opened = watch.ElapsedMilliseconds;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var rendered = watch.ElapsedMilliseconds;
            var session = vm.Conversation.Active!;
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "tkcode-bench.txt"),
                $"open+load={opened}ms first-layout={rendered}ms items={session.Items.Count}");

            // 只加载最近 20 轮（每轮：用户消息 + 合并后的助手消息）
            Assert.Equal(40, session.Items.Count);
            Assert.True(session.HasEarlierHistory);
            var lastTool = session.Items.OfType<AssistantMessageItemViewModel>().Last().Blocks.OfType<ToolCallBlockViewModel>().Last();
            Assert.True(lastTool.Result!.Length < 20_000, "tool output is short enough to be sent as-is");

            // 懒解析：历史正文按需解析，只有视口附近的条目已解析
            var transcript = window.FindControl<StreamMark.Avalonia.Transcript.TranscriptView>("Transcript")!;
            var markdown = transcript.Entries.OfType<StreamMark.Avalonia.Transcript.MarkdownEntry>().ToList();
            var parsed = markdown.Count(m => m.IsParsed);
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "tkcode-bench.txt"), $" parsed={parsed}/{markdown.Count}");
            // 每轮只有最后一段正文是 Markdown 条目，中间的正文与工具收进工作空间（纯文本 / 步骤行）
            Assert.Equal(20, markdown.Count);
            Assert.InRange(parsed, 1, 20);
            Assert.True(markdown[^1].IsParsed, "the newest message is visible and parsed");

            // 滚到顶部附近：加载更早的一页，阅读位置保持（第一条可见内容不变）
            transcript.Offset = new Avalonia.Vector(0, 100);
            for (var i = 0; i < 50 && session.Items.Count == 40; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(30);
            }
            Assert.Equal(80, session.Items.Count);
            Assert.True(transcript.Offset.Y > 1000, $"offset {transcript.Offset.Y} should move down with the inserted content");

            // 一直滚到开头：全部 300 轮加载完毕
            for (var guard = 0; guard < 40 && session.HasEarlierHistory; guard++)
            {
                transcript.Offset = new Avalonia.Vector(0, 0);
                for (var i = 0; i < 20 && !session.IsLoadingEarlier; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    await Task.Delay(10);
                }
                for (var i = 0; i < 100 && session.IsLoadingEarlier; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    await Task.Delay(10);
                }
            }
            Assert.False(session.HasEarlierHistory);
            Assert.Equal(600, session.Items.Count);
            Assert.Equal("第 0 轮：请检查一下项目", ((UserMessageItemViewModel)session.Items[0]).Text);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }
}

internal static class BenchData
{
    public static void Write(string file, string cwd)
    {
        using var w = new StreamWriter(file);
        var id = new string('b', 32);
        w.WriteLine($$"""{"type":"session","sessionId":"{{id}}","cwd":"{{cwd}}","version":1}""");
        w.WriteLine("""{"type":"modelChange","id":"mc","provider":"fake","modelId":"fake-sonnet"}""");
        var output = string.Concat(Enumerable.Repeat("line of command output with some text 0123456789\\n", 400));
        var text = "## 结果\\n\\n" + string.Concat(Enumerable.Repeat("- 一些 **markdown** 文本 `code`\\n", 20));
        var thinking = string.Concat(Enumerable.Repeat("Let me think. ", 40));
        var parent = "mc";
        var n = 0;
        string Next(string message)
        {
            var eid = $"e{++n}";
            var line = $$$"""{"type":"message","id":"{{{eid}}}","parentId":"{{{parent}}}","message":{{{message}}}}""";
            parent = eid;
            return line;
        }
        const string usage = "\"usage\":{\"input\":10,\"output\":10,\"cacheRead\":0,\"cacheWrite\":0}";
        for (var t = 0; t < 300; t++)
        {
            w.WriteLine(Next($$$"""{"role":"user","content":[{"type":"text","text":"第 {{{t}}} 轮：请检查一下项目"}]}"""));
            w.WriteLine(Next($$$"""{"role":"assistant","content":[{"type":"thinking","thinking":"{{{thinking}}}"},{"type":"text","text":"{{{text}}}"},{"type":"toolCall","id":"c{{{t}}}","name":"Bash","argumentsJson":"{\"command\":\"git status\"}"}],"api":"fake","provider":"fake","model":"fake-sonnet","stopReason":"ToolUse",{{{usage}}}}"""));
            w.WriteLine(Next($$$"""{"role":"toolResult","toolCallId":"c{{{t}}}","toolName":"Bash","content":[{"type":"text","text":"{{{output}}}"}]}"""));
            w.WriteLine(Next($$$"""{"role":"assistant","content":[{"type":"text","text":"完成。"}],"api":"fake","provider":"fake","model":"fake-sonnet","stopReason":"Stop",{{{usage}}}}"""));
        }
    }
}
