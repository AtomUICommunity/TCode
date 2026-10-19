using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using TKCode.Rpc;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>上下文压缩的界面（docs/design/compaction.md §12）：/compact 经真实 Host 压缩，分隔卡片显示状态与摘要。</summary>
public sealed class CompactionSnapshotTests
{
    [Fact]
    public async Task CompactCommand_ThroughRealHost_ShowsCompactionCard_AndSurvivesReload()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-ui-home-").FullName;
        var project = Directory.CreateTempSubdirectory("tkcode-ui-project-").FullName;
        Environment.SetEnvironmentVariable("TKCODE_HOME", home);
        Environment.SetEnvironmentVariable("TKCODE_AGENTHOST_DIR", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/TKCode.AgentHost/bin/Debug/net11.0")));
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
            var window = new MainWindow { DataContext = vm, Width = 1100, Height = 820 };
            window.Show();
            await WaitUntil(() => vm.Conversation.Picker.Selected is not null, "models loaded");
            vm.Conversation.ComposerText = "你好";
            vm.Conversation.SendCommand.Execute(null);
            await WaitUntil(() => vm.Conversation.Active is { IsRunning: false, Items.Count: >= 2 }, "first turn finished");
            var session = vm.Conversation.Active!;

            // 输入 “/compact 指令” 回车：不作为消息发送，而是压缩
            vm.Conversation.ComposerText = "/compact 保留接口名称";
            vm.Conversation.SendCommand.Execute(null);
            await WaitUntil(() => session.Items.OfType<CompactionItemViewModel>().Any(c => c is { IsRunning: false, HasSummary: true }),
                () => $"compaction finished (status: {vm.Conversation.StatusMessage}; items: {string.Join(", ", session.Items.Select(i => i is CompactionItemViewModel c ? c.Header : i.GetType().Name))})");
            Assert.DoesNotContain(session.Items.OfType<UserMessageItemViewModel>(), u => u.Text.StartsWith("/compact", StringComparison.Ordinal));
            var card = Assert.Single(session.Items.OfType<CompactionItemViewModel>());
            Assert.False(card.IsFailed);
            Assert.StartsWith("上下文已压缩 · ", card.Header);
            Assert.DoesNotContain("If you need specific details", card.Summary);
            Assert.Equal("", vm.Conversation.ComposerText);
            card.ToggleCommand.Execute(null);
            await Settle();
            HeadlessApp.Save(window, "compaction-card");

            // 压缩后继续对话；切换出去再打开（历史来自 Host）：卡片仍在、摘要消息不显示为用户气泡
            vm.Conversation.ComposerText = "继续";
            vm.Conversation.SendCommand.Execute(null);
            await WaitUntil(() => session is { IsRunning: false } && session.Items.OfType<UserMessageItemViewModel>().Count() == 2, "turn after compaction");

            var server = await provider.GetRequiredService<AgentHostConnection>().GetServerAsync();
            var history = await server.GetHistoryAsync(session.SessionId, int.MaxValue, 100);
            Assert.Equal("你好", history.History.First(m => m.Role == "user").Blocks.OfType<HistoryText>().Single().Text);
            Assert.Contains(history.History, m => m.Source == "compact-summary");
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CompactionCard_States_Render()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            var running = CompactionItemViewModel.Running(183_402);
            Assert.True(running.IsRunning);
            Assert.False(running.HasSummary);

            var done = CompactionItemViewModel.Running(183_402);
            done.Complete(183_402, 14_211, null);
            done.SetSummary("This session is being continued…\n\nSummary:\n1. Primary Request and Intent: X\n\nIf you need specific details from before compaction, read …");
            Assert.Equal("1. Primary Request and Intent: X", done.Summary);
            Assert.True(done.HasSummary);

            var failed = CompactionItemViewModel.Running(50_000);
            failed.Complete(50_000, null, "500 upstream failure");
            Assert.True(failed.IsFailed);
            Assert.Contains("500 upstream failure", failed.Header);

            var suspended = CompactionItemViewModel.SuspendedNotice("Auto-compact failed 3 times in a row");
            Assert.True(suspended.IsFailed);
            Assert.False(suspended.HasSummary);
            return Task.FromResult(true);
        }, TestContext.Current.CancellationToken);
    }

    private static Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 30_000) => WaitUntil(condition, () => what, timeoutMs);

    private static async Task WaitUntil(Func<bool> condition, Func<string> what, int timeoutMs = 30_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what()}.");
            }
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }
        await Settle();
    }

    private static async Task Settle()
    {
        for (var i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
        }
    }
}
