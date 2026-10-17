using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using TKCode.Terminal.Core;
using TKCode.Terminal.Protocol;
using TKCode.Terminal.Pty;

namespace TKCode.Terminal.Host.Sessions;

internal enum ShareAccess
{
    None,
    Read,
    Write,
}

/// <summary>
/// 一个终端会话（04 §5）：PTY 连接 + 镜像核心 + 输出日志 + 订阅者。镜像核心只由 actor 使用（01 §5.1）：
/// 所有改动核心的操作都经 <see cref="Post"/> / <see cref="InvokeAsync{T}"/> 排进同一个先进先出队列，输出块也一样，
/// 所以 attach 的"取 S、编码快照、登记游标"天然是原子的（§8.2）。
/// </summary>
internal sealed partial class Session : ITerminalEffects, IAsyncDisposable
{
    private readonly Channel<Action> _commands = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _inflight = new(4, 4);  // 读池：最多 4 块在途（01 §5.1）
    private readonly AsyncGate _gate = new();
    private readonly List<Subscription> _subscribers = [];
    private readonly Lock _subscribersLock = new();
    private readonly List<Action<ulong>> _pendingEvents = [];
    private readonly List<NotificationEventDto> _pendingNotifications = [];
    private readonly Task _actor;
    private readonly HostContext _host;
    private Task _pump = Task.CompletedTask;
    private long _focusCounter;
    private long _lastBell;
    private long _notificationWindow;
    private int _notificationsInWindow;
    private bool _pendingClear;
    private LogRecord _pendingClearRecord;
    private TaskCompletionSource<TerminalExit>? _exitTcs;

    public Session(HostContext host, uint id, string persistentId, ITerminalConnection pty, TerminalCore core, SessionCreateParams create, TerminalSize size)
    {
        _host = host;
        Id = id;
        PersistentId = persistentId;
        Pty = pty;
        Core = core;
        Create = create;
        Size = size;
        Persist = create.Persist ?? create.Kind == "user";
        Label = create.Label;
        Palette = create.Palette;
        CreatedAt = DateTimeOffset.UtcNow;
        _actor = Task.Run(ActorLoopAsync);
    }

    public uint Id { get; }
    public string PersistentId { get; }
    public ITerminalConnection Pty { get; }

    /// <summary>镜像核心：只在 actor 上使用。</summary>
    public TerminalCore Core { get; }

    public SessionCreateParams Create { get; }
    public OutputLog Log { get; } = new();
    public TerminalSize Size { get; private set; }
    public PaletteDto? Palette { get; private set; }
    public string? Label { get; set; }
    public bool Persist { get; set; }
    public string Title { get; private set; } = string.Empty;
    public string? Cwd { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? LastOutputAt { get; private set; }
    public TerminalExit? Exit { get; private set; }
    public bool BellPending { get; private set; }
    public ProgressEventDto? Progress { get; private set; }
    public Subscription? SizeOwner { get; private set; }
    public RevivedDto? Revived { get; set; }

    /// <summary>用户共享给某个 Agent 的权限（session/share，01 §8）。</summary>
    public ShareAccess SharedAccess { get; set; }

    public string? SharedWith { get; set; }

    /// <summary>日志上一次被持久化时的 head（04 §10.4：只保存有变化的会话）。</summary>
    public ulong SavedHead { get; set; } = ulong.MaxValue;

    public int Pid => Pty.ProcessId;

    public Task<TerminalExit> Exited => (_exitTcs ??= new TaskCompletionSource<TerminalExit>(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    /// <summary>开始读 PTY。在登记到会话表、可能的 attach 之后调用，保证 shell 的第一段输出不会漏发。</summary>
    public void StartPump()
    {
        _ = Exited;
        _pump = Task.Run(PumpLoopAsync);
    }

    // ---- actor ----

    public void Post(Action action)
    {
        if (!_commands.Writer.TryWrite(action))
        {
            throw new PtyHostException(PtyErrors.SessionNotFound, $"session {Id} is closed");
        }
    }

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception e)
            {
                tcs.SetException(e);
            }
        });
        return tcs.Task;
    }

    private async Task ActorLoopAsync()
    {
        await foreach (var work in _commands.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                work();
            }
            catch (Exception e)
            {
                _host.Log.Error($"session {Id}: {e}");
            }
        }
    }

    private async Task PumpLoopAsync()
    {
        var reader = Pty.Output;
        try
        {
            while (true)
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                await _inflight.WaitAsync().ConfigureAwait(false);
                System.IO.Pipelines.ReadResult result;
                try
                {
                    result = await reader.ReadAsync().ConfigureAwait(false);
                }
                catch
                {
                    _inflight.Release();
                    throw;
                }
                var length = (int)result.Buffer.Length;
                if (length > 0)
                {
                    var buffer = ArrayPool<byte>.Shared.Rent(length);
                    result.Buffer.CopyTo(buffer);
                    reader.AdvanceTo(result.Buffer.End);
                    Post(() =>
                    {
                        try
                        {
                            HandleOutput(buffer.AsSpan(0, length));
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                            _inflight.Release();
                        }
                    });
                }
                else
                {
                    reader.AdvanceTo(result.Buffer.End);
                    _inflight.Release();
                }
                if (result.IsCompleted || result.IsCanceled)
                {
                    break;
                }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _host.Log.Warn($"session {Id}: output pump ended: {e.Message}");
        }
        var exit = await Pty.Exited.ConfigureAwait(false);
        try
        {
            Post(() => OnExited(exit));
        }
        catch (PtyHostException)
        {
            _exitTcs?.TrySetResult(exit);  // 会话已关闭
        }
    }

    // ---- 在 actor 上执行 ----

    private void HandleOutput(ReadOnlySpan<byte> data)
    {
        Core.Write(data);
        Log.AppendOutput(data);
        LastOutputAt = DateTimeOffset.UtcNow;
        AfterCoreChange();
        if (_pendingClear && Core.IsParserGround)
        {
            _pendingClear = false;
            ApplyClear(_pendingClearRecord);
        }
    }

    /// <summary>核心操作之后：按当前 head 发出期间产生的事件，唤醒订阅者，评估流控，回收日志。</summary>
    private void AfterCoreChange()
    {
        var seq = Log.Head;
        if (_pendingEvents.Count > 0)
        {
            var events = _pendingEvents.ToArray();
            _pendingEvents.Clear();
            foreach (var e in events)
            {
                e(seq);
            }
        }
        WakeViews();
        EvaluateGate();
        Log.Trim(_host.Settings.OutputRingBytes, MinAcked());
    }

    private void OnExited(TerminalExit exit)
    {
        Exit = exit;
        var early = DateTimeOffset.UtcNow - CreatedAt < TimeSpan.FromMilliseconds(250);
        Broadcast(PtyMethods.EventExited, new ExitedEventDto(Log.Head, exit.ExitCode, exit.Signal, exit.CoreDumped, early, exit.Message),
            PtyHostJsonContext.Default.ExitedEventDto);
        (_exitTcs ??= new TaskCompletionSource<TerminalExit>()).TrySetResult(exit);
        _host.OnSessionExited(this);
    }

    public ulong Resize(Subscription? caller, TerminalSize size, out bool applied)
    {
        applied = false;
        if (caller is not null)
        {
            lock (_subscribersLock)
            {
                if (SizeOwner is null && caller.IsLiveView)
                {
                    SizeOwner = caller;
                }
                if (SizeOwner != caller)
                {
                    return Log.Head;
                }
            }
        }
        if (size == Size)
        {
            applied = true;
            return Log.Head;
        }
        if (Exit is null)
        {
            Pty.ResizeAsync(size).AsTask().GetAwaiter().GetResult();
        }
        Core.Resize(size);
        Size = size;
        var seq = Log.AppendRecord(LogRecord.Resize(size.Columns, size.Rows, size.CellWidth, size.CellHeight));
        AfterCoreChange();
        applied = true;
        return seq;
    }

    public ulong SetColors(PaletteDto palette)
    {
        Palette = palette;
        Core.SetDefaultColors(ToCorePalette(palette), palette.Scheme == "light" ? ColorScheme.Light : ColorScheme.Dark);
        var seq = Log.AppendRecord(LogRecord.SetColors(palette));
        AfterCoreChange();
        return seq;
    }

    public ulong Reset(bool hard)
    {
        Core.Reset();
        var seq = Log.AppendRecord(LogRecord.Reset(hard));
        AfterCoreChange();
        return seq;
    }

    /// <summary>⌘K（18.1 第 8 条）：只在镜像核心的解析器处于 ground 时追加 Clear，否则推迟到下一个数据块边界。</summary>
    public ulong Clear(bool screen, bool scrollback, bool redrawPrompt)
    {
        var record = LogRecord.Clear(screen, scrollback);
        ulong seq;
        if (Core.IsParserGround)
        {
            seq = ApplyClear(record);
        }
        else
        {
            _pendingClear = true;
            _pendingClearRecord = record;
            seq = Log.Head;
        }
        if (redrawPrompt && Exit is null && Core.CursorAtPrompt)
        {
            Pty.TryWriteReply("\f"u8);
        }
        return seq;
    }

    private ulong ApplyClear(in LogRecord record)
    {
        var target = record.ClearFlags switch { 1 => ClearTarget.Screen, 2 => ClearTarget.Scrollback, _ => ClearTarget.All };
        Core.Clear(target);
        var seq = Log.AppendRecord(record);
        AfterCoreChange();
        return seq;
    }

    /// <summary>
    /// host 自己追加的输出（revive 的恢复内容与分隔文字，04 §10.5）：与 PTY 输出一样经过核心与日志，所以两侧仍然一致。
    /// </summary>
    public void InjectOutput(ReadOnlySpan<byte> data) => HandleOutput(data);

    // ---- 订阅 ----

    public AttachResultDto Attach(Subscription subscription, AttachParams p)
    {
        byte[]? snapshot = null;
        var format = "none";
        ulong cursor;
        if (p.ResumeFrom is { } resume && resume >= Log.Start && resume <= Log.Head)
        {
            cursor = resume;
            format = "resume";
        }
        else if (subscription.Mode == SubscriptionMode.View && p.Snapshot != "none")
        {
            cursor = Log.Head;
            if (p.Snapshot == "vt")
            {
                snapshot = Encoding.UTF8.GetBytes(Core.FormatVt());
                format = "vt";
            }
            else
            {
                try
                {
                    snapshot = Core.EncodeSnapshot();
                }
                catch (Exception e)
                {
                    throw new PtyHostException(PtyErrors.SnapshotFailed, e.Message);
                }
                format = "ghostty";
            }
        }
        else
        {
            cursor = Log.Head;
        }
        subscription.Start(cursor, snapshot, format == "vt" ? SnapshotFormat.Vt : SnapshotFormat.Ghostty);
        bool isOwner;
        lock (_subscribersLock)
        {
            _subscribers.Add(subscription);
            subscription.FocusOrder = ++_focusCounter;
            if (subscription.Mode == SubscriptionMode.View && (SizeOwner is null || p.ClaimSize))
            {
                SizeOwner = subscription;
            }
            isOwner = SizeOwner == subscription;
        }
        var result = new AttachResultDto
        {
            AttachId = subscription.AttachId,
            SnapshotSeq = cursor,
            SnapshotFormat = format,
            SnapshotLength = snapshot?.Length ?? 0,
            CoreOptions = CoreOptions(),
            Metadata = Metadata(),
            IsSizeOwner = isOwner,
        };
        if (subscription.Mode == SubscriptionMode.View)
        {
            BellPending = false;
            _pendingNotifications.Clear();
        }
        return result;
    }

    public void Detach(Subscription subscription)
    {
        subscription.MarkDetached();
        lock (_subscribersLock)
        {
            _subscribers.Remove(subscription);
            if (SizeOwner == subscription)
            {
                // 所有权交给最近一次 focus 的其余 View 订阅者（§9.2）
                SizeOwner = _subscribers.Where(s => s.IsLiveView).MaxBy(s => s.FocusOrder);
            }
        }
        subscription.Client.Connection.RemoveProducer(subscription);
        EvaluateGate();
    }

    public bool Focus(Subscription subscription)
    {
        lock (_subscribersLock)
        {
            subscription.FocusOrder = ++_focusCounter;
            if (subscription.IsLiveView)
            {
                SizeOwner = subscription;
            }
            return SizeOwner == subscription;
        }
    }

    /// <summary>主视图（§11.1）：最近一次 focus / attach 的 View 订阅者。</summary>
    public Subscription? PrimaryView
    {
        get
        {
            lock (_subscribersLock)
            {
                return _subscribers.Where(s => s.IsLiveView).MaxBy(s => s.FocusOrder);
            }
        }
    }

    public Subscription[] Subscribers
    {
        get
        {
            lock (_subscribersLock)
            {
                return [.. _subscribers];
            }
        }
    }

    public void Resync(Subscription subscription, string reason)
    {
        if (subscription.State != SubscriptionState.Live)
        {
            return;
        }
        subscription.MarkResync();
        subscription.Client.Connection.Notify(PtyMethods.EventResync, new ResyncEventDto(reason), PtyHostJsonContext.Default.ResyncEventDto, Id);
        EvaluateGate();
    }

    // ---- 流控（§7.2）----

    internal void OnAcked() => EvaluateGate();

    private void EvaluateGate()
    {
        var head = Log.Head;
        ulong max = 0;
        lock (_subscribersLock)
        {
            foreach (var s in _subscribers)
            {
                if (s.IsLiveView)
                {
                    max = Math.Max(max, head - Math.Min(head, s.Acked));
                }
            }
        }
        if (_gate.IsOpen && max > FlowControl.HighWater)
        {
            _gate.Close();
        }
        else if (!_gate.IsOpen && max < FlowControl.LowWater)
        {
            _gate.Open();
        }
    }

    public bool ReadingPaused => !_gate.IsOpen;

    private ulong MinAcked()
    {
        var min = Log.Head;
        lock (_subscribersLock)
        {
            foreach (var s in _subscribers)
            {
                if (s.IsLiveView)
                {
                    min = Math.Min(min, s.Acked);
                }
            }
        }
        return min;
    }

    /// <summary>每秒一次（host 定时器）：慢订阅者降级（§7.4）。</summary>
    public void Tick()
    {
        var head = Log.Head;
        foreach (var s in Subscribers)
        {
            if (!s.IsLiveView)
            {
                continue;
            }
            if (head - Math.Min(head, s.Acked) >= FlowControl.HighWater)
            {
                if (s.LaggingSince == 0)
                {
                    s.LaggingSince = Stopwatch.GetTimestamp();
                }
                else if (FlowControl.Elapsed(s.LaggingSince, FlowControl.LaggingTimeout))
                {
                    Resync(s, "lagging");
                }
            }
            else
            {
                s.LaggingSince = 0;
            }
        }
    }

    private void WakeViews()
    {
        foreach (var s in Subscribers)
        {
            if (s.IsLiveView)
            {
                s.Client.Connection.Wake();
            }
        }
    }

    // ---- 广播 ----

    private void Broadcast<T>(string method, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, bool uiOnly = false)
    {
        foreach (var s in Subscribers)
        {
            if (s.State == SubscriptionState.Detached || (uiOnly && s.Client.Kind != ClientKind.Ui))
            {
                continue;
            }
            s.Client.Connection.Notify(method, value, type, Id);
        }
    }

    private bool HasUiView => Subscribers.Any(s => s.IsLiveView && s.Client.Kind == ClientKind.Ui);

    // ---- 元数据 ----

    public CoreOptionsDto CoreOptions() => new()
    {
        Cols = Size.Columns,
        Rows = Size.Rows,
        CellWidth = Size.CellWidth,
        CellHeight = Size.CellHeight,
        ScrollbackMaxBytes = _host.CoreOptions.ScrollbackMaxBytes,
        ScrollbackMaxLines = _host.CoreOptions.ScrollbackMaxLines,
        WidthMode = Create.WidthMode,
        Palette = Palette,
    };

    private AttachMetadataDto Metadata() => new()
    {
        Title = Title,
        Cwd = Cwd,
        Pid = Pid,
        State = Exit is null ? "running" : "exited",
        Exit = Exit is { } e ? new ExitDto(e.ExitCode, e.Signal, e.CoreDumped, e.Message) : null,
        BellPending = BellPending,
        PendingNotifications = [.. _pendingNotifications],
        Progress = Progress,
    };

    public SessionInfoDto Info()
    {
        var subs = Subscribers;
        return new SessionInfoDto
        {
            SessionId = Id,
            PersistentId = PersistentId,
            Kind = Create.Kind,
            Owner = Create.Kind == "agent" ? $"agent:{Create.AgentSessionId}" : "user",
            AgentSessionId = Create.AgentSessionId,
            Label = Label,
            Title = Title,
            Cwd = Cwd,
            Pid = Pid,
            State = Exit is null ? "running" : "exited",
            Exit = Exit is { } e ? new ExitDto(e.ExitCode, e.Signal, e.CoreDumped, e.Message) : null,
            Size = new TerminalSizeDto(Size.Columns, Size.Rows, Size.CellWidth, Size.CellHeight),
            CreatedAt = CreatedAt,
            LastOutputAt = LastOutputAt,
            Subscribers = new SubscriberCountDto(subs.Count(s => s.Client.Kind == ClientKind.Ui), subs.Count(s => s.Client.Kind == ClientKind.Agent)),
            Tags = Create.Tags,
            Revived = Revived is not null,
        };
    }

    internal static TerminalPalette ToCorePalette(PaletteDto p)
    {
        static TerminalRgb Rgb(int v) => new((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        var colors = new TerminalRgb[256];
        for (var i = 0; i < 256; i++)
        {
            colors[i] = Rgb(i < p.Palette.Length ? p.Palette[i] : 0);
        }
        return new TerminalPalette(Rgb(p.Foreground), Rgb(p.Background), p.Cursor is { } c ? Rgb(c) : null, colors);
    }

    // ---- 关闭 ----

    public async ValueTask DisposeAsync()
    {
        foreach (var s in Subscribers)
        {
            Detach(s);
        }
        try
        {
            await Pty.CloseAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _host.Log.Warn($"session {Id}: close failed: {e.Message}");
        }
        await _pump.ConfigureAwait(false);
        // 让 OnExited 等排队中的命令执行完，再停 actor
        var done = new TaskCompletionSource();
        if (_commands.Writer.TryWrite(() => done.TrySetResult()))
        {
            await done.Task.ConfigureAwait(false);
        }
        _commands.Writer.TryComplete();
        await _actor.ConfigureAwait(false);
        await Pty.DisposeAsync().ConfigureAwait(false);
        Core.Dispose();
        Log.Clear();
    }
}
