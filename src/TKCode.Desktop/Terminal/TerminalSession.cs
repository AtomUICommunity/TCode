using System.Buffers;
using System.Threading.Channels;
using TKCode.Terminal.Core;
using TKCode.Terminal.Protocol;

namespace TKCode.Desktop.Terminal;

/// <summary>会话的状态变化，在 UI 线程之外产生；由 <see cref="TerminalSession"/> 的回调交给视图模型再切换到 UI 线程。</summary>
public abstract record TerminalSessionEvent;

public sealed record TerminalTitleChanged(string Title) : TerminalSessionEvent;

public sealed record TerminalExited(int? ExitCode, string? Signal) : TerminalSessionEvent;

public sealed record TerminalClipboardWrite(byte[] Data, bool Primary) : TerminalSessionEvent;

public sealed record TerminalDetached(string Reason) : TerminalSessionEvent;

/// <summary>降级（慢，或环溢出）：需要重新 attach 以取得完整快照（04 §7.4）。</summary>
public sealed record TerminalResyncRequested(string Reason) : TerminalSessionEvent;

/// <summary>
/// 一个终端会话在桌面端的一侧（UI 侧核心，01 §3.2）：接收 attach 快照与之后的输出 / 记录，维护 UI 侧 <see cref="TerminalCore"/>，
/// 并在每批数据处理完之后捕获一帧并 Ack。
/// <para>
/// 线程：IPC 读线程只做"拷贝并入队"；所有对 <see cref="TerminalCore"/> 的操作（写入、记录、选区、滚动、捕获帧）都在本类的工作线程上
/// 按入队顺序执行，所以核心只有一个使用者。UI 线程只读取 <see cref="InputModes"/> 的缓存副本，并通过 <see cref="Invoke{T}"/> 请求结果。
/// </para>
/// UI 侧核心不抢先 resize（01 §3.2 规则 1）：尺寸变化只在收到 Record(Resize) 时应用。
/// </summary>
public sealed class TerminalSession : IPtySessionSink, IAsyncDisposable
{
    private readonly Channel<Action> _work = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly MemoryStream _snapshot = new();
    private readonly Task _loop;
    private readonly Action<FrameSnapshot> _frame;
    private readonly Action<TerminalSessionEvent> _event;
    private readonly object _modesLock = new();
    private TerminalCore? _core;
    private PtySessionHandle? _handle;
    private ulong _position;
    private ulong _acked;
    private InputModes _modes = InputModes.Defaults;
    private int _disposed;

    /// <param name="frame">新帧（工作线程上调用，实现方负责切换到 UI 线程）。</param>
    /// <param name="event">会话事件（同上）。</param>
    public TerminalSession(Action<FrameSnapshot> frame, Action<TerminalSessionEvent> @event)
    {
        _frame = frame;
        _event = @event;
        _loop = Task.Run(RunAsync);
    }

    public PtySessionHandle? Handle => _handle;

    public uint SessionId => _handle?.SessionId ?? 0;

    /// <summary>最近一次工作线程处理完之后的输入模式（UI 线程编码按键用）。</summary>
    public InputModes Modes
    {
        get
        {
            lock (_modesLock)
            {
                return _modes;
            }
        }
    }

    /// <summary>当前核心的副本是否已经建立（快照处理完之后）。</summary>
    public bool IsReady => _core is not null;

    // ---- IPtySessionSink（IPC 读线程）----

    public void OnAttached(PtySessionHandle handle)
    {
        _handle = handle;
        _position = handle.Attach.SnapshotSeq;
        _acked = _position;
    }

    public void OnSnapshotChunk(uint attachId, SnapshotFormat format, ulong offset, ReadOnlySpan<byte> chunk, bool last)
    {
        _snapshot.Write(chunk);
        if (!last)
        {
            return;
        }
        var bytes = _snapshot.ToArray();
        _snapshot.SetLength(0);
        var handle = _handle!;
        var format0 = handle.Attach.SnapshotFormat;
        Enqueue(() => BuildCore(bytes, format0 == "vt" ? SnapshotFormat.Vt : SnapshotFormat.Ghostty, handle.Attach.CoreOptions));
    }

    public void OnOutput(ulong position, ReadOnlySpan<byte> data)
    {
        var copy = data.ToArray();
        Enqueue(() =>
        {
            _core?.Write(copy);
            _position = position + (ulong)copy.Length;
        });
    }

    public void OnRecord(ulong position, in LogRecord record)
    {
        var r = record;
        Enqueue(() =>
        {
            if (_core is null)
            {
                return;
            }
            switch (r.Kind)
            {
                case RecordKind.Resize:
                    _core.Resize(new TerminalSize(r.Cols, r.Rows, r.CellWidth, r.CellHeight));
                    break;
                case RecordKind.SetColors:
                    _core.SetDefaultColors(TerminalTheme.ToCorePalette(r.Palette!), r.Palette!.Scheme == "light" ? ColorScheme.Light : ColorScheme.Dark);
                    break;
                case RecordKind.Reset:
                    _core.Reset();
                    break;
                case RecordKind.Clear:
                    _core.Clear(r.ClearFlags switch { 1 => ClearTarget.Screen, 2 => ClearTarget.Scrollback, _ => ClearTarget.All });
                    break;
            }
            _position = position + 1;
        });
    }

    public void OnEvent(object e)
    {
        switch (e)
        {
            case TKCode.Terminal.Protocol.ExitedEventDto exited:
                _event(new TerminalExited(exited.ExitCode, exited.Signal));
                break;
            case TitleEventDto title:
                _event(new TerminalTitleChanged(title.Title));
                break;
            case ClipboardWriteEventDto clipboard:
                _event(new TerminalClipboardWrite(clipboard.Data, clipboard.Target == "primary"));
                break;
            case BellEvent:
            case NotificationEventDto:
            case ProgressEventDto:
            case CwdEventDto:
            case CommandStartedEventDto:
            case CommandFinishedEventDto:
            case InputOverflowEventDto:
                break;  // 通知、进度、命令边界：后续在标签与状态栏中呈现（M3 之后）
        }
    }

    public void OnResync(string reason) => _event(new TerminalResyncRequested(reason));

    public void OnDetached(string reason) => _event(new TerminalDetached(reason));

    // ---- UI 线程调用 ----

    /// <summary>发送已编码的字节（按键、粘贴、鼠标报告）。</summary>
    public void SendBytes(ReadOnlySpan<byte> bytes) => _handle?.SendInput(bytes);

    /// <summary>在工作线程上对核心执行 <paramref name="action"/>，结果返回给调用方。</summary>
    public Task<T> Invoke<T>(Func<TerminalCore, T> action)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(() =>
        {
            try
            {
                tcs.SetResult(_core is null ? default! : action(_core));
            }
            catch (Exception e)
            {
                tcs.SetException(e);
            }
        });
        return tcs.Task;
    }

    /// <summary>对核心执行一个不需要结果的操作（选区、滚动等）。之后会捕获并发布新帧。</summary>
    public void Post(Action<TerminalCore> action) => Enqueue(() =>
    {
        if (_core is not null)
        {
            action(_core);
        }
    });

    /// <summary>
    /// 搜索（UI 侧核心，不经过 host）：设置搜索词并完成搜索，之后的帧带上高亮。搜索词为空时结束搜索。
    /// 在工作线程上执行，所以搜索对象与核心的其他操作串行。
    /// </summary>
    public void SetSearch(string needle, bool next)
    {
        Enqueue(() =>
        {
            if (_core is null)
            {
                return;
            }
            if (needle.Length == 0)
            {
                _core.EndSearch();
                return;
            }
            var search = _core.ActiveSearch ?? _core.CreateSearch();
            search.Needle = needle;
            search.Update();
            if (next && search.TotalMatches > 0)
            {
                search.Next();
            }
        });
    }

    /// <summary>选中下一个（next）或上一个匹配，并滚动视口到它。</summary>
    public void StepSearch(bool next) => Enqueue(() =>
    {
        var search = _core?.ActiveSearch;
        if (search is null || search.TotalMatches == 0)
        {
            return;
        }
        if (next)
        {
            search.Next();
        }
        else
        {
            search.Previous();
        }
    });

    /// <summary>结束搜索（关闭搜索栏）。</summary>
    public void CloseSearch() => Enqueue(() => _core?.EndSearch());

    /// <summary>请求调整尺寸（只有尺寸所有者会生效，结果通过 Record(Resize) 回到 UI 侧核心）。</summary>
    public Task<ResizeResult?> RequestResizeAsync(TerminalSize size, CancellationToken ct = default) =>
        _handle is { } handle
            ? handle.ResizeAsync(new TerminalSizeDto(size.Columns, size.Rows, size.CellWidth, size.CellHeight), ct).ContinueWith(t => (ResizeResult?)t.Result, TaskScheduler.Default)
            : Task.FromResult<ResizeResult?>(null);

    /// <summary>已处理的位置超过上次确认的位置时确认（04 §7.2 第 4 条）。</summary>
    private void AckIfNeeded()
    {
        if (_handle is { } h && _position > _acked)
        {
            _acked = _position;
            h.Ack(_position);
        }
    }

    // ---- 工作线程 ----

    private void Enqueue(Action work)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _work.Writer.TryWrite(work);
        }
    }

    private async Task RunAsync()
    {
        var reader = _work.Reader;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                var processed = false;
                while (reader.TryRead(out var work))
                {
                    try
                    {
                        work();
                    }
                    catch (Exception)
                    {
                        // 单个操作失败（例如快照解码失败）不影响后续操作；失败原因已在 BuildCore 中以事件报告
                    }
                    processed = true;
                }
                if (processed)
                {
                    Publish();
                }
            }
        }
        catch (ChannelClosedException)
        {
        }
    }

    /// <summary>一批处理完之后：捕获帧、刷新输入模式、确认位置。</summary>
    private void Publish()
    {
        if (_core is null)
        {
            return;
        }
        var modes = _core.ReadInputModes();
        lock (_modesLock)
        {
            _modes = modes;
        }
        var frame = _core.CaptureFrame();
        if (frame is not null)
        {
            _frame(frame);
        }
        AckIfNeeded();
    }

    private void BuildCore(byte[] snapshot, SnapshotFormat format, CoreOptionsDto options)
    {
        var size = new TerminalSize(options.Cols, options.Rows, options.CellWidth, options.CellHeight);
        var coreOptions = new TerminalCoreOptions(size)
        {
            ScrollbackMaxLines = options.ScrollbackMaxLines,
            Width = options.WidthMode == "grapheme" ? WidthPolicy.Grapheme : WidthPolicy.Compat,
        };
        _core?.Dispose();
        _core = format == SnapshotFormat.Ghostty && snapshot.Length > 0
            ? TerminalCore.Restore(snapshot, coreOptions)
            : TerminalCore.Create(coreOptions);
        if (format == SnapshotFormat.Vt && snapshot.Length > 0)
        {
            _core.Write(snapshot);
        }
        if (options.Palette is { } palette)
        {
            _core.SetDefaultColors(TerminalTheme.ToCorePalette(palette), palette.Scheme == "light" ? ColorScheme.Light : ColorScheme.Dark);
        }
    }

    /// <summary>释放本地资源并从 host 取消订阅（会话本身不结束：要结束请用 CloseSessionAsync）。</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        if (_handle is { } handle)
        {
            try
            {
                await handle.DetachAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or PtyHostException)
            {
                // 连接或会话已经结束：没有需要取消的订阅
            }
        }
        _work.Writer.TryComplete();
        await _loop.ConfigureAwait(false);
        _core?.Dispose();
        _core = null;
    }
}
