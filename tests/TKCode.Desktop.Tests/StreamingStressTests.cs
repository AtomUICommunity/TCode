using System.Diagnostics;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using TKCode.Rpc;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>
/// 运行中的高频事件（在真实窗口中渲染）：长思考 / 长正文的增量与流式工具参数。
/// 每 20 个事件处理一次界面任务（模拟逐帧刷新）。耗时写到临时目录的 tkcode-stream-bench.txt。
/// </summary>
public sealed class StreamingStressTests
{
    [Fact]
    public async Task ManyDeltas_StayFast()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-ui-home-").FullName;
        var project = Directory.CreateTempSubdirectory("tkcode-ui-project-").FullName;
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
            var window = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
            window.Show();
            while (vm.Conversation.Picker.Selected is null)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(20);
            }
            vm.Conversation.ComposerText = "你好";
            vm.Conversation.SendCommand.Execute(null);
            while (vm.Conversation.Active is not { IsRunning: false, Items.Count: >= 2 })
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(20);
            }
            var session = vm.Conversation.Active!;
            var count = 0;
            void Apply(SessionEvent e)
            {
                session.Apply(e);
                if (++count % 20 == 0)
                {
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                }
            }

            var watch = Stopwatch.StartNew();
            Apply(new AgentStarted());
            Apply(new MessageStarted("u", "user", "go"));
            Apply(new MessageStarted("m", "assistant", null));
            for (var i = 0; i < 2000; i++)
            {
                Apply(new ThinkingDelta("m", 0, "thinking about the problem in more detail. "));
            }
            var thinking = watch.ElapsedMilliseconds;
            for (var i = 0; i < 2000; i++)
            {
                Apply(new TextDelta("m", 1, "Here is some **markdown** text with `code` and more words. "));
            }
            var text = watch.ElapsedMilliseconds - thinking;
            var tools = Stopwatch.StartNew();
            var line = "public static int Value = 42; // line of generated code\\n";
            for (var t = 0; t < 50; t++)
            {
                var id = $"c{t}";
                Apply(new ToolCallStarted("m", 2 + t, id, "Write"));
                Apply(new ToolCallDelta("m", 2 + t, "{\"file_path\":\"/tmp/a.cs\",\"content\":\""));
                for (var f = 0; f < 200; f++)
                {
                    Apply(new ToolCallDelta("m", 2 + t, line));
                }
                Apply(new ToolCallDelta("m", 2 + t, "\"}"));
                var args = "{\"file_path\":\"/tmp/a.cs\",\"content\":\"" + string.Concat(Enumerable.Repeat(line, 200)) + "\"}";
                Apply(new ToolCallEnded("m", 2 + t, id, args));
                Apply(new ToolExecutionStarted(id, "Write", args));
                Apply(new ToolExecutionEnded(id, "Write", false, "ok", 5));
            }
            var toolMs = tools.ElapsedMilliseconds;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "tkcode-stream-bench.txt"),
                $"thinking2000={thinking}ms text2000={text}ms tools50x200={toolMs}ms total={watch.ElapsedMilliseconds}ms");
            var message = session.Items.OfType<AssistantMessageItemViewModel>().Last();
            Assert.Equal(52, message.Blocks.Count);
            Assert.Equal("/tmp/a.cs", ((ToolCallBlockViewModel)message.Blocks[^1]).Summary);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }
}
