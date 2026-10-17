using TKCode.Terminal.Host.Sessions;
using TKCode.Terminal.Protocol;
using Xunit;
using static TKCode.Terminal.Host.Tests.Shells;

namespace TKCode.Terminal.Host.Tests;

/// <summary>pty host 端到端（04 §16）：进程内 host + 真实客户端连接 + 真实 PTY。</summary>
public sealed class HostTests
{
    [Fact]
    public async Task HandshakeAuthenticatesAndRejectsWrongToken()
    {
        await using var host = TestHost.Start();
        await using (var client = await host.ConnectAsync())
        {
            Assert.Equal("full", client.Scope);
            Assert.Equal(host.Server.InstanceId, client.Host.InstanceId);
            var info = await client.InfoAsync();
            Assert.Single(info.Connections);
        }
        // 错误令牌：host 的 serverProof 校验不过，客户端拒绝（令牌从未上线）
        var e = await Assert.ThrowsAsync<PtyHostException>(() => host.ConnectAsync(token: Handshake.NewToken()));
        Assert.Equal(PtyErrors.Unauthenticated, e.Code);
    }

    [Fact]
    public async Task OutputReachesReplicaAndGridsMatch()
    {
        await using var host = TestHost.Start();
        await using var client = await host.ConnectAsync();
        using var sink = new ReplicaSink();
        var (created, handle) = await client.CreateSessionAsync(
            Script("printf '\\033]0;hello title\\007\\033[1;31mred\\033[0m line\\n'; exit 3", "echo red line& exit /b 3"), sink);
        Assert.NotNull(handle);
        await Wait.UntilAsync(() => sink.Events.OfType<ExitedEventDto>().Any(), "exit event");
        var exited = sink.Events.OfType<ExitedEventDto>().Single();
        Assert.Equal(3, exited.ExitCode);
        await Wait.UntilAsync(() => sink.Position >= exited.Seq, "output before the exit seq");
        Assert.Contains("red line", sink.Text());
        Assert.Equal(await host.MirrorGridAsync(created.SessionId), sink.Grid());
        if (!OperatingSystem.IsWindows())
        {
            Assert.Contains(sink.Events.OfType<TitleEventDto>(), t => t.Title == "hello title");
        }
    }

    [Fact]
    public async Task AttachDuringFloodIsConsistent()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix shell syntax");
        await using var host = TestHost.Start();
        await using var first = await host.ConnectAsync();
        using var a = new ReplicaSink();
        var (created, _) = await first.CreateSessionAsync(Script("i=0; while [ $i -lt 3000 ]; do printf '\\033[3%dm%05d abcdefghijklmnopqrstuvwxyz\\n' $((i%8)) $i; i=$((i+1)); done; sleep 0.3", ""), a);
        await Wait.UntilAsync(() => a.OutputBytes > 20_000, "flood started");

        // 洪流中途第二个客户端 attach：快照 + 之后的记录必须与镜像核心逐格一致（§8.2）
        await using var second = await host.ConnectAsync();
        using var b = new ReplicaSink();
        await second.AttachAsync(new AttachParams { SessionId = created.SessionId }, b);
        await Wait.UntilAsync(() => a.Events.OfType<ExitedEventDto>().Any() && b.Events.OfType<ExitedEventDto>().Any(), "exit");
        var seq = a.Events.OfType<ExitedEventDto>().Single().Seq;
        await Wait.UntilAsync(() => a.Position >= seq && b.Position >= seq, "replicas caught up");
        var mirror = await host.MirrorGridAsync(created.SessionId);
        Assert.Equal(mirror, a.Grid());
        Assert.Equal(mirror, b.Grid());
        Assert.Contains("02999", a.Text());
    }

    [Fact]
    public async Task SlowSubscriberPausesReadingAndResumesOnAck()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix shell syntax");
        await using var host = TestHost.Start();
        await using var client = await host.ConnectAsync();
        using var sink = new ReplicaSink { AutoAck = false };
        var (created, _) = await client.CreateSessionAsync(Script("head -c 8000000 /dev/zero | tr '\\0' x", ""), sink);
        var session = host.Server.Sessions[created.SessionId];
        await Wait.UntilAsync(() => session.ReadingPaused, "flow gate closed");
        await Task.Delay(300);
        // 不 Ack：发出的数据不超过高水位（加一帧），日志 head 也停在高水位附近
        Assert.InRange(sink.Position, 1UL, FlowControl.HighWater + 64 * 1024 + 8000);
        Assert.InRange(session.Log.Head, 1UL, FlowControl.HighWater + 4 * 300 * 1024);
        sink.AutoAck = true;
        sink.AckNow();
        await Wait.UntilAsync(() => sink.Events.OfType<ExitedEventDto>().Any(), "flood finished after acks", 60);
        var seq = sink.Events.OfType<ExitedEventDto>().Single().Seq;
        await Wait.UntilAsync(() => sink.Position >= seq, "replica caught up");
        Assert.True(sink.OutputBytes >= 8_000_000);
    }

    [Fact]
    public async Task LaggingSubscriberIsResyncedAndSessionContinues()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix shell syntax");
        FlowControl.LaggingTimeout = TimeSpan.FromSeconds(1);
        try
        {
            await using var host = TestHost.Start();
            await using var client = await host.ConnectAsync();
            using var sink = new ReplicaSink { AutoAck = false };
            var (created, _) = await client.CreateSessionAsync(Script("head -c 4000000 /dev/zero | tr '\\0' y; echo; echo done", ""), sink);
            await Wait.UntilAsync(() => sink.Resyncs.Contains("lagging"), "resync", 20);
            // 降级后不再拖住会话：shell 跑完
            await Wait.UntilAsync(async () => (await client.ListSessionsAsync()).Single().State == "exited", "session finished", 30);
            // 重新 attach 得到完整状态
            await client.Connection_Detach(created.SessionId);
            using var fresh = new ReplicaSink();
            await client.AttachAsync(new AttachParams { SessionId = created.SessionId }, fresh);
            await Wait.UntilAsync(() => fresh.Core is not null, "snapshot");
            Assert.Contains("done", fresh.Text());
        }
        finally
        {
            FlowControl.LaggingTimeout = TimeSpan.FromSeconds(10);
        }
    }

    [Fact]
    public async Task ResizeOwnershipAndRecordsReachAllViews()
    {
        await using var host = TestHost.Start();
        await using var c1 = await host.ConnectAsync();
        await using var c2 = await host.ConnectAsync();
        using var s1 = new ReplicaSink();
        using var s2 = new ReplicaSink();
        var (created, h1) = await c1.CreateSessionAsync(Interactive(), s1);
        var h2 = await c2.AttachAsync(new AttachParams { SessionId = created.SessionId }, s2);
        Assert.True(h1!.Attach.IsSizeOwner);
        Assert.False(h2.Attach.IsSizeOwner);

        var denied = await h2.ResizeAsync(new TerminalSizeDto(100, 30, 8, 16));
        Assert.False(denied.Applied);
        Assert.Equal(80, denied.Size.Cols);

        await h2.FocusAsync();  // 最近获得焦点者成为所有者（§9.2）
        var applied = await h2.ResizeAsync(new TerminalSizeDto(100, 30, 8, 16));
        Assert.True(applied.Applied);
        await Wait.UntilAsync(() => s1.Core?.Size.Columns == 100 && s2.Core?.Size.Columns == 100, "resize record applied on both views");

        await h1.FocusAsync();
        Assert.False((await h2.ResizeAsync(new TerminalSizeDto(90, 20, 8, 16))).Applied);
    }

    [Fact]
    public async Task InputEchoesAndRecordsKeepPositionsAligned()
    {
        await using var host = TestHost.Start();
        await using var client = await host.ConnectAsync();
        using var sink = new ReplicaSink();
        var (created, handle) = await client.CreateSessionAsync(Interactive(), sink);
        await Wait.UntilAsync(() => sink.Text().Contains(OperatingSystem.IsWindows() ? ">" : "$"), "prompt");
        handle!.SendInput(Bytes("echo hello-input\r"));
        await Wait.UntilAsync(() => sink.Text().Split('\n').Any(l => l.Trim() == "hello-input"), "echoed output");

        // 穿插 setColors / clear / reset：两侧在同一位置执行
        await handle.SetColorsAsync(new PaletteDto(0xdddddd, 0x111111, null, Enumerable.Range(0, 256).ToArray()));
        await handle.ClearAsync(screen: true, scrollback: true);
        handle.SendInput(Bytes("echo after-clear\r"));
        await Wait.UntilAsync(() => sink.Text().Contains("after-clear") && !sink.Text().Contains("hello-input\n"), "clear applied");
        await Wait.UntilAsync(async () => await host.MirrorGridAsync(created.SessionId) == sink.Grid(), "grids converge");
    }

    [Fact]
    public async Task AgentTokenIsRestricted()
    {
        await using var host = TestHost.Start();
        await using var ui = await host.ConnectAsync();
        var minted = await ui.MintAgentTokenAsync("agent-host");
        var (userSession, _) = await ui.CreateSessionAsync(Interactive(), null);

        await using var agent = await host.ConnectAsync(ClientKind.Agent, minted.Token, minted.TokenId);
        // 受限令牌冒充完整令牌（不带 tokenId）：失败
        await Assert.ThrowsAsync<PtyHostException>(() => host.ConnectAsync(ClientKind.Agent, minted.Token));
        Assert.Equal("agent", agent.Scope);
        var forbidden = await Assert.ThrowsAsync<PtyHostException>(() => agent.InfoAsync());
        Assert.Equal(PtyErrors.Forbidden, forbidden.Code);
        Assert.Empty(await agent.ListSessionsAsync());  // 看不到用户的会话
        var denied = await Assert.ThrowsAsync<PtyHostException>(() => agent.ReadAsync(new ReadParams { SessionId = userSession.SessionId }));
        Assert.Equal(PtyErrors.Forbidden, denied.Code);

        var (agentSession, _) = await agent.CreateSessionAsync(Script("echo from-agent", "echo from-agent") with { Kind = "user" }, null);
        var listed = Assert.Single(await agent.ListSessionsAsync());
        Assert.Equal(("agent", agentSession.SessionId), (listed.Kind, listed.SessionId));  // agent 连接创建的一律是 agent 会话
        await Wait.UntilAsync(async () => (await agent.ReadAsync(new ReadParams { SessionId = agentSession.SessionId })).Text.Contains("from-agent"), "agent read");

        await ui.RevokeAsync(minted.TokenId);
        await agent.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CloseAsksForConfirmationWhileAProgramRuns()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix job control");
        await using var host = TestHost.Start();
        await using var client = await host.ConnectAsync();
        using var sink = new ReplicaSink();
        var (created, handle) = await client.CreateSessionAsync(Interactive(), sink);
        await Wait.UntilAsync(() => sink.Text().Contains('$'), "prompt");
        Assert.False((await client.ForegroundAsync(created.SessionId)).Running);

        handle!.SendInput(Bytes("sleep 30\r"));
        await Wait.UntilAsync(async () => (await client.ForegroundAsync(created.SessionId)).Running, "sleep in foreground");
        var check = await client.QuitCheckAsync();
        Assert.Equal("sleep", Assert.Single(check.Running).Foreground!.Name);

        var notClosed = await client.CloseSessionAsync(created.SessionId, force: false);
        Assert.False(notClosed.Closed);
        Assert.Equal("sleep", notClosed.Confirm!.Foreground!.Name);

        var removed = new TaskCompletionSource<SessionRemovedDto>();
        await using var other = await host.ConnectAsync();
        other.SessionRemoved += r => removed.TrySetResult(r);
        Assert.True((await client.CloseSessionAsync(created.SessionId, force: true)).Closed);
        Assert.Equal(created.SessionId, (await removed.Task.WaitAsync(TimeSpan.FromSeconds(10))).SessionId);
        Assert.Equal("closed", await sink.Detached.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(await client.ListSessionsAsync());
    }

    [Fact]
    public async Task ResumeFromSkipsTheSnapshot()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix shell syntax");
        await using var host = TestHost.Start();
        await using var client = await host.ConnectAsync();
        using var sink = new ReplicaSink();
        var (created, handle) = await client.CreateSessionAsync(Interactive(), sink);
        await Wait.UntilAsync(() => sink.Text().Contains('$'), "prompt");
        var position = sink.Position;
        await handle!.DetachAsync();
        handle = null;

        // 断开期间的输出留在环里；带 resumeFrom 重新 attach 时不发快照，直接从 position 续传（§8.4）
        await client.InputAsync(created.SessionId, "echo while-away\r");
        await Wait.UntilAsync(() => host.Server.Sessions[created.SessionId].Log.Head > position + 20, "output while detached");
        var resumed = await client.AttachAsync(new AttachParams { SessionId = created.SessionId, ResumeFrom = position }, sink);
        Assert.Equal(("resume", position), (resumed.Attach.SnapshotFormat, resumed.Attach.SnapshotSeq));
        await Wait.UntilAsync(() => sink.Text().Split('\n').Any(l => l.Trim() == "while-away"), "resumed output");
        await Wait.UntilAsync(async () => await host.MirrorGridAsync(created.SessionId) == sink.Grid(), "grids converge");
    }

    [Fact]
    public async Task VtSnapshotFormatRestoresTheScreen()
    {
        await using var host = TestHost.Start();
        await using var client = await host.ConnectAsync();
        var (created, _) = await client.CreateSessionAsync(Script("printf 'vt-line\\n'; sleep 5", "echo vt-line& ping -n 5 127.0.0.1 >nul"), null);
        await Wait.UntilAsync(async () => (await client.ReadAsync(new ReadParams { SessionId = created.SessionId })).Text.Contains("vt-line"), "output");
        using var sink = new ReplicaSink();
        var handle = await client.AttachAsync(new AttachParams { SessionId = created.SessionId, Snapshot = "vt" }, sink);
        Assert.Equal("vt", handle.Attach.SnapshotFormat);
        await Wait.UntilAsync(() => sink.Text().Contains("vt-line"), "vt snapshot applied");
    }
}

internal static class ClientTestExtensions
{
    /// <summary>测试用：detach 当前 handle（sink 被替换前）。</summary>
    public static Task Connection_Detach(this PtyHostClient client, uint sessionId) =>
        client.RequestAsync(PtyMethods.SessionDetach, new SessionIdParams(sessionId), PtyHostJsonContext.Default.SessionIdParams, PtyHostJsonContext.Default.EmptyDto);
}

/// <summary>安全回归与升级并存（04 §16 的"安全回归""升级并存"）。</summary>
public sealed class HostSecurityAndUpgradeTests
{
    [Fact]
    public async Task ShellEnvironmentHasNoHostSecretsAndLogsHaveNoContent()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix shell syntax");
        await using var host = TestHost.Start();
        await using var client = await host.ConnectAsync();
        await client.ConfigureAsync(new HostSettingsDto
        {
            BaseEnvironment = new Dictionary<string, string>
            {
                ["PATH"] = Environment.GetEnvironmentVariable("PATH")!, ["TKCODE_PTYHOST_TOKEN"] = "secret", ["TKCODE_PTYHOST_ENDPOINT"] = "x", ["KEEP_ME"] = "1",
            },
        });
        using var sink = new ReplicaSink();
        await client.CreateSessionAsync(Script("env | sort; echo SENTINEL-7f3a", "") with
        {
            Env = new Dictionary<string, string?> { ["TKCODE_PTYHOST_INSTANCE"] = "y" },
        }, sink);
        await Wait.UntilAsync(() => sink.Text().Contains("SENTINEL-7f3a"), "env output");
        var text = sink.Text();
        Assert.DoesNotContain("TKCODE_PTYHOST_", text);
        Assert.Contains("KEEP_ME=1", text);
        Assert.Contains("TERM=xterm-256color", text);
        lock (host.LogLines)
        {
            Assert.DoesNotContain(host.LogLines, l => l.Contains("SENTINEL-7f3a") || l.Contains("secret"));
        }
    }

    [Fact]
    public async Task ForeignBuildClientDrainsTheHost()
    {
        await using var host = TestHost.Start();
        await using var current = await host.ConnectAsync();
        var (created, _) = await current.CreateSessionAsync(Script("echo old-build-session; sleep 30", "echo old-build-session& ping -n 30 127.0.0.1 >nul"), null);
        await Wait.UntilAsync(async () => (await current.ReadAsync(new ReadParams { SessionId = created.SessionId })).Text.Contains("old-build-session"), "output");

        // 升级后的 Desktop（不同的 ghosttyBuildId）连上旧 host
        await using var upgraded = await PtyHostClient.ConnectAsync(host.Endpoint(), new PtyHostClientOptions { GhosttyBuildId = "newer-build" });
        Assert.True(upgraded.Host.Draining);
        var e = await Assert.ThrowsAsync<PtyHostException>(() => upgraded.CreateSessionAsync(Interactive(), null));
        Assert.Equal(PtyErrors.HostDraining, e.Code);
        using var sink = new ReplicaSink();
        var handle = await upgraded.AttachAsync(new AttachParams { SessionId = created.SessionId }, sink);
        Assert.Equal("vt", handle.Attach.SnapshotFormat);
        await Wait.UntilAsync(() => sink.Text().Contains("old-build-session"), "vt attach");

        // 最后一个会话关闭后，遗留 host 立即退出
        var lifetime = new HostLifetime(host.Server);
        await upgraded.CloseSessionAsync(created.SessionId, force: true);
        Assert.Equal("draining", await host.Server.Stopped.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
