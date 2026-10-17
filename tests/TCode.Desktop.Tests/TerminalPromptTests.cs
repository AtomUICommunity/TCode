using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using TCode.Desktop.Services;
using TCode.Desktop.Terminal;
using TCode.Desktop.ViewModels;
using TCode.Terminal.Core;
using TCode.Terminal.Protocol;
using Xunit;

namespace TCode.Desktop.Tests;

/// <summary>终端的确认栏（06 §2）：含换行的粘贴需要确认；关闭仍在运行的程序需要确认。真实 host 与真实会话。</summary>
public sealed class TerminalPromptTests
{
    private static TerminalTabViewModel Tab() => new(new PtyHostService(NullLogger<PtyHostService>.Instance), cwd: null);

    [Fact]
    public async Task MultiLinePasteWaitsForConfirmation()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX shell");
        await using var host = await InProcessHost.StartAsync();
        await using var client = await host.ConnectAsync();
        await using var session = new TerminalSession(_ => { }, _ => { });
        await client.CreateSessionAsync(new SessionCreateParams { Size = new TerminalSizeDto(80, 24, 8, 16), Launch = new LaunchDto("/bin/sh", ["-c", "sleep 5"]) }, session);
        await Wait.UntilAsync(() => session.IsReady, "ready");

        var tab = Tab();
        tab.AttachForTesting(client, session);
        await tab.PasteAsync("echo one\necho two");   // 换行：可能直接执行命令，先确认
        Assert.Equal("echo one\necho two", tab.PendingPaste);
        Assert.True(tab.HasPrompt);
        Assert.Equal(TCode.Desktop.Localization.Strings.Get(TCode.Desktop.Localization.TCodeLangResourceKind.TerminalPasteConfirm), tab.PromptMessage);

        tab.CancelPromptCommand.Execute(null);
        Assert.Null(tab.PendingPaste);
        Assert.False(tab.HasPrompt);

        await tab.PasteAsync("echo one\necho two");
        await tab.ConfirmPromptCommand.ExecuteAsync(null);  // 确认：以 allowUnsafe 发送，确认栏消失
        Assert.Null(tab.PendingPaste);
        Assert.False(tab.HasPrompt);
    }

    [Fact]
    public async Task SingleLinePasteIsSentWithoutAsking()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX shell");
        await using var host = await InProcessHost.StartAsync();
        await using var client = await host.ConnectAsync();
        await using var session = new TerminalSession(_ => { }, _ => { });
        await client.CreateSessionAsync(new SessionCreateParams { Size = new TerminalSizeDto(80, 24, 8, 16), Launch = new LaunchDto("/bin/sh", ["-c", "sleep 5"]) }, session);
        await Wait.UntilAsync(() => session.IsReady, "ready");
        var tab = Tab();
        tab.AttachForTesting(client, session);
        await tab.PasteAsync("plain text");
        Assert.Null(tab.PendingPaste);
    }

    [Fact]
    public async Task ClosingARunningProgramAsksFirst()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX shell");
        await using var host = await InProcessHost.StartAsync();
        await using var client = await host.ConnectAsync();
        var events = new ConcurrentQueue<TerminalSessionEvent>();
        await using var session = new TerminalSession(_ => { }, events.Enqueue);
        // 交互 shell（有作业控制）：sleep 成为前台进程组，之后才能被判定为"程序在运行"
        var (created, _) = await client.CreateSessionAsync(new SessionCreateParams
        {
            Size = new TerminalSizeDto(80, 24, 8, 16),
            Launch = new LaunchDto("/bin/sh", ["-i"]),
            Env = new Dictionary<string, string?> { ["PS1"] = "$ " },
        }, session);

        await Wait.UntilAsync(async () => (await client.ForegroundAsync(created.SessionId)).Reason == "idle", "shell prompt");
        session.SendBytes("sleep 30\r"u8.ToArray());
        await Wait.UntilAsync(() => session.IsReady, "ready");
        await Wait.UntilAsync(async () => (await client.ForegroundAsync(created.SessionId)).Running, "sleep in foreground");

        var tab = Tab();
        tab.AttachForTesting(client, session);
        var closed = false;
        tab.Closed += () => closed = true;

        Assert.False(await tab.CloseAsync());          // 程序在运行：不关闭，请求确认
        Assert.True(tab.PendingClose);
        Assert.True(tab.HasPrompt);
        Assert.False(closed);
        Assert.Equal(TCode.Desktop.Localization.Strings.Get(TCode.Desktop.Localization.TCodeLangResourceKind.TerminalCloseRunning), tab.PromptMessage);

        await tab.ConfirmPromptCommand.ExecuteAsync(null);  // 确认：强制关闭
        Assert.True(closed);
        Assert.Empty(await client.ListSessionsAsync());
    }
}
