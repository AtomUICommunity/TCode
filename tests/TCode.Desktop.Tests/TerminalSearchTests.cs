using System.Collections.Concurrent;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using TCode.Desktop.Services;
using TCode.Desktop.Terminal;
using TCode.Desktop.ViewModels;
using TCode.Terminal.Core;
using TCode.Terminal.Protocol;
using Xunit;

namespace TCode.Desktop.Tests;

/// <summary>搜索栏（06 §2）：搜索词、"当前 / 总数"、切换匹配。经真实会话管道（UI 侧核心）执行。</summary>
public sealed class TerminalSearchTests
{
    private static TerminalTabViewModel Tab() => new(new PtyHostService(NullLogger<PtyHostService>.Instance), cwd: null);

    /// <summary>等待条件，同时泵 UI 线程（视图模型的结果通过 Dispatcher 切回 UI 线程）。</summary>
    private static async Task Until(Func<bool> condition, string what)
    {
        await Wait.UntilAsync(() => { Dispatcher.UIThread.RunJobs(); return condition(); }, what);
    }

    [Fact]
    public async Task CountsMatchesAndStepsThroughThem()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX shell");
        await HeadlessApp.Session.Dispatch(async () =>
        {
        await using var host = await InProcessHost.StartAsync();
        await using var client = await host.ConnectAsync();
        var frames = new ConcurrentQueue<FrameSnapshot>();
        await using var session = new TerminalSession(frames.Enqueue, _ => { });
        await client.CreateSessionAsync(new SessionCreateParams
        {
            Size = new TerminalSizeDto(40, 10, 8, 16),
            Launch = new LaunchDto("/bin/sh", ["-c", "for i in 1 2 3 4; do echo line-$i foo; done; sleep 30"]),
        }, session);
        await Wait.UntilAsync(() => frames.Any(f => f.Rows.Any(r => r.TextAt(0) == "l")), "output");

        var tab = Tab();
        tab.AttachForTesting(client, session);
        tab.OpenSearch();
        Assert.True(tab.IsSearchOpen);
        Assert.Equal(string.Empty, tab.SearchCountText);

        tab.SearchText = "foo";
        await Until(() => tab.SearchTotal == 4, "four matches");
        Assert.Equal(0, tab.SearchCurrent);  // 尚未选中任何匹配
        Assert.Equal("0/4", tab.SearchCountText);

        // 实测（TerminalSearch 的 SelectedIndex 从最新的匹配开始计数）：Next 的第一个是最下面的匹配，显示为 1/4
        tab.StepSearch(next: true);
        await Until(() => tab.SearchCurrent == 1, "first match selected");
        Assert.Equal("1/4", tab.SearchCountText);

        tab.StepSearch(next: true);
        await Until(() => tab.SearchCurrent == 2, "second match");
        Assert.Equal("2/4", tab.SearchCountText);

        tab.SearchText = "nomatch-xyz";
        await Until(() => tab.SearchTotal == 0, "no matches");
        Assert.Equal("0/0", tab.SearchCountText);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ClosingClearsHighlightsAndTheQuery()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX shell");
        await HeadlessApp.Session.Dispatch(async () =>
        {
        await using var host = await InProcessHost.StartAsync();
        await using var client = await host.ConnectAsync();
        var frames = new ConcurrentQueue<FrameSnapshot>();
        await using var session = new TerminalSession(frames.Enqueue, _ => { });
        await client.CreateSessionAsync(new SessionCreateParams
        {
            Size = new TerminalSizeDto(40, 10, 8, 16),
            Launch = new LaunchDto("/bin/sh", ["-c", "echo hello-search; sleep 30"]),
        }, session);
        await Wait.UntilAsync(() => frames.Any(f => f.Rows.Any(r => r.TextAt(0) == "h")), "output");

        var tab = Tab();
        tab.AttachForTesting(client, session);
        tab.OpenSearch();
        tab.SearchText = "hello";
        await Until(() => tab.SearchTotal == 1, "match");

        tab.CloseSearch();
        Assert.False(tab.IsSearchOpen);
        Assert.Equal(string.Empty, tab.SearchText);
        Assert.Equal(0, tab.SearchTotal);
        await Until(() => frames.Last().Search is null, "highlights cleared");
        }, CancellationToken.None);
    }
}
