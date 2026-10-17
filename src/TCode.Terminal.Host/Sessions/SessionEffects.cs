using System.Diagnostics;
using System.Text;
using TCode.Terminal.Core;
using TCode.Terminal.Protocol;

namespace TCode.Terminal.Host.Sessions;

/// <summary>
/// 镜像核心的副作用（04 §11）：应答写回 PTY；标题、cwd、铃声、通知、命令边界变成带 seq 的事件；
/// OSC 52 按策略转发给主视图。UI 侧核心的同类回调一律忽略，所以这些效果只在这里发生一次。
/// 回调在 actor 的 Core.Write 内同步触发：只记录事件，Write 返回、日志追加之后再按当时的 head 发出。
/// </summary>
internal sealed partial class Session
{
    private const int MaxTitleBytes = 4096;
    private const int MaxNotificationTitle = 256;
    private const int MaxNotificationBody = 4096;
    private const int MaxPendingNotifications = 20;
    private const int MaxClipboardBytes = 8 << 20;

    void ITerminalEffects.WriteToPty(ReadOnlySpan<byte> reply)
    {
        if (Exit is null)
        {
            Pty.TryWriteReply(reply);
        }
    }

    void ITerminalEffects.TitleChanged(string title)
    {
        title = Truncate(title, MaxTitleBytes);
        Title = title;
        _pendingEvents.Add(seq => Broadcast(PtyMethods.EventTitle, new TitleEventDto(seq, title), PtyHostJsonContext.Default.TitleEventDto));
    }

    void ITerminalEffects.WorkingDirectoryChanged(string raw)
    {
        var path = VerifyCwd(raw);
        if (path is not null)
        {
            Cwd = path;
        }
        _pendingEvents.Add(seq => Broadcast(PtyMethods.EventCwd, new CwdEventDto(seq, raw, path, path is not null), PtyHostJsonContext.Default.CwdEventDto));
    }

    void ITerminalEffects.Bell()
    {
        // 同一会话 100 ms 内只发一次（§4.4）
        var now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(_lastBell, now) < TimeSpan.FromMilliseconds(100))
        {
            return;
        }
        _lastBell = now;
        if (!HasUiView)
        {
            BellPending = true;
        }
        _pendingEvents.Add(seq => Broadcast(PtyMethods.EventBell, new SeqEventDto(seq), PtyHostJsonContext.Default.SeqEventDto, uiOnly: true));
    }

    void ITerminalEffects.Notification(string title, string body)
    {
        var now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(_notificationWindow, now) >= TimeSpan.FromSeconds(1))
        {
            _notificationWindow = now;
            _notificationsInWindow = 0;
        }
        if (++_notificationsInWindow > Math.Max(1, _host.Settings.NotificationsPerSecond))
        {
            return;  // 限速：超出部分丢弃
        }
        var t = string.IsNullOrEmpty(title) ? null : Truncate(title, MaxNotificationTitle);
        var b = Truncate(body, MaxNotificationBody);
        _pendingEvents.Add(seq =>
        {
            var dto = new NotificationEventDto(seq, t, b);
            if (!HasUiView)
            {
                if (_pendingNotifications.Count == MaxPendingNotifications)
                {
                    _pendingNotifications.RemoveAt(0);
                }
                _pendingNotifications.Add(dto);
            }
            Broadcast(PtyMethods.EventNotification, dto, PtyHostJsonContext.Default.NotificationEventDto, uiOnly: true);
        });
    }

    void ITerminalEffects.Progress(ProgressState state, int? percent)
    {
        var name = state switch
        {
            ProgressState.Set => "normal",
            ProgressState.Error => "error",
            ProgressState.Indeterminate => "indeterminate",
            ProgressState.Pause => "paused",
            _ => "none",
        };
        _pendingEvents.Add(seq =>
        {
            Progress = new ProgressEventDto(seq, name, percent);
            Broadcast(PtyMethods.EventProgress, Progress, PtyHostJsonContext.Default.ProgressEventDto, uiOnly: true);
        });
    }

    void ITerminalEffects.SemanticPrompt(in SemanticPromptEvent e)
    {
        var record = Core.Commands.Current;
        if (record is null)
        {
            return;
        }
        if (e.Kind == SemanticPromptKind.OutputStart)
        {
            var id = record.Id;
            var line = record.CommandLine;
            _pendingEvents.Add(seq => Broadcast(PtyMethods.EventCommandStarted, new CommandStartedEventDto(seq, id, line),
                PtyHostJsonContext.Default.CommandStartedEventDto));
        }
        else if (e.Kind == SemanticPromptKind.CommandEnd && record.StartedAt is { } started)
        {
            var id = record.Id;
            var code = record.ExitCode;
            var duration = (long)((record.EndedAt ?? DateTimeOffset.UtcNow) - started).TotalMilliseconds;
            _pendingEvents.Add(seq => Broadcast(PtyMethods.EventCommandFinished, new CommandFinishedEventDto(seq, id, code, duration),
                PtyHostJsonContext.Default.CommandFinishedEventDto));
        }
    }

    ClipboardWriteResult ITerminalEffects.ClipboardWrite(ClipboardTarget target, string mime, ReadOnlySpan<byte> data)
    {
        // §11.2：deny 时不发；没有 UI 时丢弃（不缓存到以后再写）
        if (_host.Settings.OscWrite == "deny" || data.Length > MaxClipboardBytes || PrimaryView is not { } primary || primary.Client.Kind != ClientKind.Ui)
        {
            return ClipboardWriteResult.Denied;
        }
        var bytes = data.ToArray();
        var name = target == ClipboardTarget.Standard ? "clipboard" : "primary";
        _pendingEvents.Add(seq => primary.Client.Connection.Notify(PtyMethods.EventClipboardWrite, new ClipboardWriteEventDto(seq, name, bytes),
            PtyHostJsonContext.Default.ClipboardWriteEventDto, Id));
        return ClipboardWriteResult.Success;
    }

    ClipboardReadDecision ITerminalEffects.ClipboardRead(in PendingClipboardRead request)
    {
        if (_host.Settings.OscRead == "deny" || PrimaryView is not { } primary || primary.Client.Kind != ClientKind.Ui)
        {
            return ClipboardReadDecision.Denied;
        }
        var pending = request;
        var name = request.Target == ClipboardTarget.Standard ? "clipboard" : "primary";
        _ = AskClipboardAsync(primary.Client, pending, name);
        return ClipboardReadDecision.Deferred;
    }

    /// <summary>向主视图发 ui/clipboardRead（60 s 超时视为拒绝），应答回到 actor 上补发（overlay O3）。</summary>
    private async Task AskClipboardAsync(ClientConnection client, PendingClipboardRead request, string target)
    {
        byte[]? data = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var response = await client.Connection.RequestAsync(PtyMethods.UiClipboardRead, new ClipboardReadRequest(Id, target),
                PtyHostJsonContext.Default.ClipboardReadRequest, PtyHostJsonContext.Default.ClipboardReadResponse, Id, timeout.Token).ConfigureAwait(false);
            data = response.Allow ? response.Data ?? [] : null;
        }
        catch (Exception e)
        {
            _host.Log.Info($"session {Id}: clipboard read not answered: {e.Message}");
        }
        try
        {
            Post(() =>
            {
                if (data is not null && Exit is null)
                {
                    Core.CompleteClipboardRead(request, data);
                }
            });
        }
        catch (PtyHostException)
        {
        }
    }

    /// <summary>OSC 7 来自不可信输入：只有主机名是本机（或为空）且目录存在时才采用（§4.4）。</summary>
    private static string? VerifyCwd(string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != "file")
        {
            return null;
        }
        var host = uri.Host;
        if (host.Length > 0 && host != "localhost" && !host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) &&
            !host.Equals(System.Net.Dns.GetHostName(), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (OperatingSystem.IsWindows() && path.Length > 2 && path[0] == '/' && path[2] == ':')
        {
            path = path[1..];
        }
        return Directory.Exists(path) ? path : null;
    }

    private static string Truncate(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return value;
        }
        var length = Math.Min(value.Length, maxBytes);
        while (length > 0 && Encoding.UTF8.GetByteCount(value.AsSpan(0, length)) > maxBytes)
        {
            length--;
        }
        if (length > 0 && char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }
        return value[..length];
    }
}
