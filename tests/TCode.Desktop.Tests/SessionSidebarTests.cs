using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TCode.Desktop.Services;
using TCode.Desktop.ViewModels;
using TCode.Desktop.Views;
using Xunit;

namespace TCode.Desktop.Tests;

/// <summary>
/// 侧栏会话列表：分页加载（首屏 50 条，滚到底部加载更多）、虚拟化（只创建可见行），
/// 以及右键菜单的重命名 / 置顶 / 删除。会话文件直接写到临时数据目录。
/// </summary>
public sealed class SessionSidebarTests
{
    [Fact]
    public async Task ManySessions_ArePagedAndVirtualized_AndCanBeRenamedPinnedDeleted()
    {
        var home = Directory.CreateTempSubdirectory("tcode-ui-home-").FullName;
        var project = Directory.CreateTempSubdirectory("tcode-ui-project-").FullName;
        var dir = Path.Combine(home, "sessions", "--project--");
        Directory.CreateDirectory(dir);
        var start = DateTimeOffset.UtcNow.AddHours(-5);
        for (var i = 0; i < 120; i++)
        {
            var id = $"{i:D32}";
            var file = Path.Combine(dir, $"20261016T0000{i:D3}_{id}.jsonl");
            File.WriteAllText(file,
                $$"""{"type":"session","sessionId":"{{id}}","cwd":"{{project}}","version":1}""" + "\n" +
                $$$"""{"type":"message","id":"m{{{i}}}","message":{"role":"user","content":[{"type":"text","text":"会话 {{{i}}}"}]}}""" + "\n");
            File.SetLastWriteTimeUtc(file, start.AddMinutes(i).UtcDateTime);
        }
        Environment.SetEnvironmentVariable("TCODE_HOME", home);
        Environment.SetEnvironmentVariable("TCODE_AGENTHOST_DIR", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/TCode.AgentHost/bin/Debug/net10.0")));
        Environment.SetEnvironmentVariable("TCODE_FAKE_PROVIDER", "1");
        Environment.SetEnvironmentVariable("TCODE_EXTERNAL_RUNTIMES", "0");
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
            var conversation = vm.Conversation;
            await WaitUntil(() => vm.Sidebar.VisibleSessions.Count >= 50, "first page");

            // 分页：首屏 50 条，最新的在前
            Assert.Equal(50, conversation.Sessions.Count);
            Assert.True(conversation.HasMoreSessions);
            Assert.Equal("会话 119", conversation.Sessions[0].DisplayTitle);
            // 虚拟化：只为可见区域创建行
            var realized = window.GetVisualDescendants().OfType<Button>().Count(b => b.DataContext is SessionItemViewModel);
            Assert.InRange(realized, 1, 40);
            HeadlessApp.Save(window, "sidebar-paged");

            // 滚到底部加载下一页
            var list = window.FindControl<ItemsControl>("SessionList")!;
            var viewer = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            viewer.Offset = new Avalonia.Vector(0, viewer.Extent.Height);
            await WaitUntil(() => conversation.Sessions.Count == 100, "second page");
            viewer.Offset = new Avalonia.Vector(0, viewer.Extent.Height);
            await WaitUntil(() => conversation.Sessions.Count == 120 && !conversation.HasMoreSessions, "last page");

            // 重命名（行内编辑）
            var target = conversation.Sessions.Single(s => s.DisplayTitle == "会话 100");
            target.BeginRenameCommand.Execute(null);
            Assert.True(target.IsRenaming);
            target.EditTitle = "重命名后的会话";
            await target.CommitRenameCommand.ExecuteAsync(null);
            Assert.False(target.IsRenaming);
            Assert.Equal("重命名后的会话", target.DisplayTitle);

            // 置顶：排到最前，刷新后仍在最前且保留新标题
            await target.TogglePinCommand.ExecuteAsync(null);
            Assert.Same(target, conversation.Sessions[0]);
            viewer.Offset = new Avalonia.Vector(0, 0);
            await conversation.RefreshSessionsAsync();
            await Settle();
            Assert.Equal("重命名后的会话", conversation.Sessions[0].DisplayTitle);
            Assert.True(conversation.Sessions[0].IsPinned);
            HeadlessApp.Save(window, "sidebar-pinned");

            // 右键菜单与行内重命名的外观
            var row = window.GetVisualDescendants().OfType<Button>().First(b => b.DataContext == conversation.Sessions[1]);
            row.ContextMenu!.Open(row);
            await Settle();
            HeadlessApp.Save(window, "sidebar-context-menu");
            row.ContextMenu.Close();
            conversation.Sessions[1].BeginRenameCommand.Execute(null);
            await Settle();
            HeadlessApp.Save(window, "sidebar-renaming");
            conversation.Sessions[1].CancelRenameCommand.Execute(null);
            Assert.Equal("会话 119", conversation.Sessions[1].DisplayTitle);

            // 删除：确认后永久删除文件；取消则不删
            var victim = conversation.Sessions.Single(s => s.DisplayTitle == "会话 119");
            conversation.ConfirmDangerAsync = (_, _) => Task.FromResult(false);
            await victim.DeleteCommand.ExecuteAsync(null);
            Assert.True(File.Exists(victim.FilePath));
            conversation.ConfirmDangerAsync = (_, text) => Task.FromResult(text.Contains("会话 119"));
            await victim.DeleteCommand.ExecuteAsync(null);
            Assert.False(File.Exists(victim.FilePath));
            Assert.DoesNotContain(victim, conversation.Sessions);

            // 复制会话 ID
            string? copied = null;
            conversation.SetClipboardAsync = text =>
            {
                copied = text;
                return Task.CompletedTask;
            };
            await target.CopyIdCommand.ExecuteAsync(null);
            Assert.Equal(target.SessionId, copied);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 20_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }
        await Settle();
    }

    private static async Task Settle()
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
        }
    }
}
