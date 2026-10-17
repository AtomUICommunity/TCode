using TKCode.Terminal.Protocol;
using Xunit;
using static TKCode.Terminal.Host.Tests.Shells;

namespace TKCode.Terminal.Host.Tests;

/// <summary>宽限期与空闲退出（04 §1.7，2026-10-17 决定：有程序在运行的会话保留）。</summary>
public sealed class LifetimeTests
{
    [Fact]
    public async Task GraceExpiryClosesIdleSessionsButKeepsRunningPrograms()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix job control");
        await using var host = TestHost.Start();
        var lifetime = new HostLifetime(host.Server) { GraceOverride = TimeSpan.FromSeconds(1), IdleExit = TimeSpan.FromSeconds(1) };
        uint idle, busy;
        await using (var ui = await host.ConnectAsync())
        {
            using var a = new ReplicaSink();
            using var b = new ReplicaSink();
            (var c1, _) = await ui.CreateSessionAsync(Interactive(), a);
            (var c2, var h2) = await ui.CreateSessionAsync(Interactive(), b);
            (idle, busy) = (c1.SessionId, c2.SessionId);
            await Wait.UntilAsync(() => a.Text().Contains('$') && b.Text().Contains('$'), "prompts");
            h2!.SendInput(Bytes("sleep 4\r"));
            await Wait.UntilAsync(async () => (await ui.ForegroundAsync(busy)).Running, "sleep running");
        }
        // UI 异常断开 → 宽限期（1 s）到期：空闲会话关闭，运行 sleep 的保留
        await Wait.UntilAsync(() => !host.Server.Sessions.ContainsKey(idle), "idle session closed", 10);
        Assert.True(host.Server.Sessions.ContainsKey(busy));
        // 程序结束（sleep 4 之后 shell 回到提示符）不会自动关闭 shell；保留的会话里 shell 退出后 host 才退出
        await using (var ui = await host.ConnectAsync())
        {
            await ui.InputAsync(busy, "exit\r");
        }
        Assert.Equal("orphanTimeout", await host.Server.Stopped.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public async Task UiReconnectCancelsTheGracePeriod()
    {
        await using var host = TestHost.Start();
        var lifetime = new HostLifetime(host.Server) { GraceOverride = TimeSpan.FromSeconds(2) };
        await using (var ui = await host.ConnectAsync())
        {
            await ui.CreateSessionAsync(Interactive(), null);
        }
        await Task.Delay(1000);
        await using var back = await host.ConnectAsync();
        await Task.Delay(2500);
        Assert.Equal(1, host.Server.SessionCount);
        Assert.False(host.Server.Stopped.IsCompleted);
    }

    [Fact]
    public async Task EmptyHostExits()
    {
        await using var host = TestHost.Start();
        var lifetime = new HostLifetime(host.Server) { IdleExit = TimeSpan.FromSeconds(1) };
        Assert.Equal("idle", await host.Server.Stopped.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
