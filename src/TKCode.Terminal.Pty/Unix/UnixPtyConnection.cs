using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Threading.Channels;
using TKCode.Terminal.Core;

namespace TKCode.Terminal.Pty.Unix;

/// <summary>
/// Unix 本地 PTY（03-pty.md §2–§6）：posix_spawn + tkcode-pty-spawn helper；读线程 gather + PipeWriter 背压；
/// 写线程 FIFO；进程级退出监视；关闭时 SIGHUP 进程组 → 关闭 master → 超时 SIGKILL。
/// </summary>
internal sealed class UnixPtyConnection : ITerminalConnection
{
    private static readonly PipeOptions OutputPipe = new(
        pool: MemoryPool<byte>.Shared, readerScheduler: PipeScheduler.Inline, writerScheduler: PipeScheduler.Inline,
        pauseWriterThreshold: 256 * 1024, resumeWriterThreshold: 128 * 1024, minimumSegmentSize: 64 * 1024,
        useSynchronizationContext: false);

    private readonly Pipe _output = new(OutputPipe);
    private readonly Channel<byte[]> _writes = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource<TerminalExit> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _readerDone = new();
    private readonly Lock _signalLock = new();
    private readonly int _master;
    private readonly int _wakeRead;
    private readonly int _wakeWrite;
    private readonly Thread _readThread;
    private readonly Thread _writeThread;
    private TerminalSize _size;
    private bool _reaped;
    private volatile bool _discarding;
    private long _lastOutputTick;
    private int _closed;

    private UnixPtyConnection(int pid, int master, int wakeRead, int wakeWrite, TerminalSize size, bool cwdFallback)
    {
        ProcessId = pid;
        _master = master;
        _wakeRead = wakeRead;
        _wakeWrite = wakeWrite;
        _size = size;
        CwdFallback = cwdFallback;
        _readThread = new Thread(ReadLoop) { IsBackground = true, Name = $"pty-read-{pid}" };
        _writeThread = new Thread(WriteLoop) { IsBackground = true, Name = $"pty-write-{pid}" };
    }

    public string Kind => "local-pty";

    public ConnectionCapabilities Capabilities => ConnectionCapabilities.Resize | ConnectionCapabilities.PixelSize |
        ConnectionCapabilities.Signals | ConnectionCapabilities.ForegroundProcess | ConnectionCapabilities.WorkingDirectory;

    public PipeReader Output => _output.Reader;

    public Task<TerminalExit> Exited => _exited.Task;

    /// <summary>直接子进程（即 shell，2026-10-17 起不经 login 包装），也是它的进程组和会话 id。</summary>
    public int ProcessId { get; }

    /// <summary>请求的 cwd 不可用，已回退到 $HOME 或 /。</summary>
    public bool CwdFallback { get; }

    public static unsafe UnixPtyConnection Start(PtyLaunch launch, string helperPath)
    {
        UnixPtyRuntime.EnsureInitialized();
        if (!File.Exists(helperPath))
        {
            throw new PtyException($"PTY spawn helper not found: {helperPath}");
        }
        TcPtySpawnResult result = default;
        result.Size = (uint)sizeof(TcPtySpawnResult);
        int rc;
        using (var strings = new NativeStringBlock())
        {
            var argv = new List<string>(launch.Arguments.Count + 1) { launch.Argv0 ?? Path.GetFileName(launch.File) };
            argv.AddRange(launch.Arguments);
            var options = new TcPtySpawnOptions
            {
                Size = (uint)sizeof(TcPtySpawnOptions),
                Flags = TcPtyNative.SpawnIutf8,
                HelperPath = strings.Add(helperPath),
                File = strings.Add(launch.File),
                Argv = strings.AddArray(argv),
                Envp = strings.AddArray(launch.Environment.Select(p => $"{p.Key}={p.Value}")),
                Cwd = strings.Add(launch.WorkingDirectory),
                Cols = checked((ushort)launch.InitialSize.Columns),
                Rows = checked((ushort)launch.InitialSize.Rows),
                XPixel = PixelSize(launch.InitialSize.PixelWidth),
                YPixel = PixelSize(launch.InitialSize.PixelHeight),
            };
            rc = TcPtyNative.Spawn(&options, &result);
        }
        if (rc != 0)
        {
            throw new PtyException(
                $"Failed to start {launch.File}: stage {result.Stage}, {UnixPtyRuntime.StrError(result.Error)} (errno {result.Error})",
                result.Stage, result.Error);
        }

        int wakeRead, wakeWrite;
        if (TcPtyNative.WakeCreate(&wakeRead, &wakeWrite) != 0 || TcPtyNative.SetNonblock(result.MasterFd, 1) != 0)
        {
            var errno = TcPtyNative.LastErrno();
            TcPtyNative.Kill(result.Pid, TcPtyNative.Signal(TerminalSignal.Kill));
            TcPtyNative.Close(result.MasterFd);
            throw new PtyException($"Failed to set up PTY I/O (errno {errno})", nativeError: errno);
        }
        var connection = new UnixPtyConnection(result.Pid, result.MasterFd, wakeRead, wakeWrite, launch.InitialSize,
            (result.Warnings & 1) != 0);
        connection._readThread.Start();
        connection._writeThread.Start();
        UnixExitWatcher.Watch(result.Pid, connection.OnChildExited);
        return connection;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default)
    {
        if (!input.IsEmpty)
        {
            _writes.Writer.TryWrite(input.ToArray());
        }
        return ValueTask.CompletedTask;
    }

    public bool TryWriteReply(ReadOnlySpan<byte> reply) => reply.IsEmpty || _writes.Writer.TryWrite(reply.ToArray());

    public ValueTask ResizeAsync(TerminalSize size, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _closed) == 0)
        {
            _size = size;
            TcPtyNative.Resize(_master, checked((ushort)size.Columns), checked((ushort)size.Rows),
                PixelSize(size.PixelWidth), PixelSize(size.PixelHeight));
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> SignalAsync(TerminalSignal signal, CancellationToken cancellationToken = default)
    {
        lock (_signalLock)
        {
            if (_reaped)
            {
                return ValueTask.FromResult(false);  // 回收之后 pid 可能被复用，不再发信号
            }
            var signo = TcPtyNative.Signal(signal);
            var fg = Volatile.Read(ref _closed) == 0 ? TcPtyNative.GetForegroundProcessGroup(_master) : -1;
            if (fg > 1 && fg != ProcessId)
            {
                TcPtyNative.KillPg(fg, signo);
            }
            var rc = TcPtyNative.KillPg(ProcessId, signo);
            return ValueTask.FromResult(rc == 0);
        }
    }

    public unsafe ValueTask<ForegroundInfo?> GetForegroundAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _closed) != 0 || _reaped)
        {
            return ValueTask.FromResult<ForegroundInfo?>(null);
        }
        var fg = TcPtyNative.GetForegroundProcessGroup(_master);
        if (fg <= 0)
        {
            return ValueTask.FromResult<ForegroundInfo?>(null);
        }
        var name = TcPtyNative.ReadString(&TcPtyNative.GetProcessName, fg);
        return ValueTask.FromResult<ForegroundInfo?>(new ForegroundInfo(fg, name, fg == ProcessId));
    }

    public unsafe ValueTask<string?> GetWorkingDirectoryAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_reaped ? null : TcPtyNative.ReadString(&TcPtyNative.GetCwd, ProcessId));

    /// <summary>测试与诊断：内核记录的窗口尺寸（列、行、x 像素、y 像素）。</summary>
    internal unsafe (int Cols, int Rows, int XPixel, int YPixel) KernelWinsize()
    {
        var ws = stackalloc ushort[4];
        TcPtyNative.GetWinsize(_master, ws);
        return (ws[0], ws[1], ws[2], ws[3]);
    }

    public async ValueTask CloseAsync(TimeSpan grace)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            await _exited.Task.ConfigureAwait(false);
            return;
        }
        _writes.Writer.TryComplete();
        int lastForeground;
        lock (_signalLock)
        {
            lastForeground = -1;
            if (!_reaped)
            {
                lastForeground = TcPtyNative.GetForegroundProcessGroup(_master);
                var hup = TcPtyNative.Signal(TerminalSignal.Hup);
                if (lastForeground > 1 && lastForeground != ProcessId)
                {
                    TcPtyNative.KillPg(lastForeground, hup);
                }
                TcPtyNative.KillPg(ProcessId, hup);
            }
        }
        // 先让读线程退出（唤醒是粘性的），再关闭 master：内核随之挂断整个会话
        _discarding = true;
        _output.Writer.CancelPendingFlush();
        TcPtyNative.WakeSignal(_wakeWrite);
        _readThread.Join(500);
        _writeThread.Join(500);
        TcPtyNative.Close(_master);
        TcPtyNative.Close(_wakeRead);
        TcPtyNative.Close(_wakeWrite);

        if (await Task.WhenAny(_exited.Task, Task.Delay(grace)).ConfigureAwait(false) != _exited.Task)
        {
            lock (_signalLock)
            {
                if (!_reaped)
                {
                    var kill = TcPtyNative.Signal(TerminalSignal.Kill);
                    if (lastForeground > 1 && lastForeground != ProcessId)
                    {
                        TcPtyNative.KillPg(lastForeground, kill);
                    }
                    TcPtyNative.KillPg(ProcessId, kill);
                    TcPtyNative.Kill(ProcessId, kill);
                }
            }
            await Task.WhenAny(_exited.Task, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() => CloseAsync(TimeSpan.FromSeconds(2));

    private unsafe void ReadLoop()
    {
        TcPtyNative.SetThreadQosInteractive();
        var writer = _output.Writer;
        var config = new TcPtyGatherConfig
        {
            Size = (uint)sizeof(TcPtyGatherConfig), BridgeThreshold = 1024, SpinMax = 16, BridgePollMs = 1, BudgetUs = 3000,
        };
        byte[]? scratch = null;
        try
        {
            while (true)
            {
                uint flags;
                int n;
                if (_discarding)
                {
                    scratch ??= new byte[64 * 1024];
                    fixed (byte* p = scratch)
                    {
                        n = TcPtyNative.ReadGather(_master, _wakeRead, p, scratch.Length, &config, &flags);
                    }
                    if (n < 0 || (flags & (TcPtyNative.GatherEof | TcPtyNative.GatherWoken)) != 0)
                    {
                        break;
                    }
                    continue;
                }
                var memory = writer.GetMemory(64 * 1024);
                using (var pin = memory.Pin())
                {
                    n = TcPtyNative.ReadGather(_master, _wakeRead, (byte*)pin.Pointer, memory.Length, &config, &flags);
                }
                if (n < 0)
                {
                    break;
                }
                if (n > 0)
                {
                    writer.Advance(n);
                    Volatile.Write(ref _lastOutputTick, Environment.TickCount64);
                    var flush = writer.FlushAsync();
                    // 阻塞在这里就是背压：消费方不读，读线程就不再从 PTY 读取
                    var result = flush.IsCompleted ? flush.Result : flush.AsTask().GetAwaiter().GetResult();
                    if (result.IsCompleted)
                    {
                        _discarding = true;
                    }
                }
                if ((flags & (TcPtyNative.GatherEof | TcPtyNative.GatherWoken)) != 0)
                {
                    break;
                }
            }
            writer.Complete();
        }
        catch (Exception ex)
        {
            writer.Complete(ex);
        }
        finally
        {
            _readerDone.Set();
        }
    }

    private void WriteLoop()
    {
        var reader = _writes.Reader;
        try
        {
            while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (reader.TryRead(out var data))
                {
                    if (!WriteAll(data))
                    {
                        return;  // 进程已不在（EIO），丢弃剩余输入
                    }
                }
            }
        }
        catch (ChannelClosedException)
        {
        }
    }

    private unsafe bool WriteAll(byte[] data)
    {
        var offset = 0;
        fixed (byte* p = data)
        {
            while (offset < data.Length)
            {
                var chunk = Math.Min(4096, data.Length - offset);
                var n = TcPtyNative.Write(_master, p + offset, chunk);
                if (n > 0)
                {
                    offset += n;
                    continue;
                }
                if (n == TcPtyNative.EAgain)
                {
                    uint revents;
                    TcPtyNative.Wait(_master, _wakeRead, TcPtyNative.PollOut, -1, &revents);
                    if ((revents & TcPtyNative.PollWoken) != 0)
                    {
                        return false;
                    }
                    continue;
                }
                return false;
            }
        }
        return true;
    }

    private void OnChildExited(TerminalExit exit)
    {
        lock (_signalLock)
        {
            _reaped = true;
        }
        // 排空：等读线程读到 EOF，或输出静默 ≥ 100 ms，最长 1 s（后台进程仍持有 slave 时读不到 EOF）
        var exitedAt = Environment.TickCount64;
        var deadline = exitedAt + 1000;
        while (!_readerDone.IsSet && Environment.TickCount64 < deadline &&
               Environment.TickCount64 - Math.Max(exitedAt, Volatile.Read(ref _lastOutputTick)) < 100)
        {
            _readerDone.Wait(20);
        }
        _exited.TrySetResult(exit);
    }

    private static ushort PixelSize(int value) => (ushort)Math.Clamp(value, 0, ushort.MaxValue);
}

/// <summary>进程级初始化：tcpty_init（SIGCHLD 处置、fd 0–2 补位）与 ABI 版本检查。</summary>
internal static unsafe class UnixPtyRuntime
{
    private static readonly Lazy<TcPtyInitInfo> Info = new(() =>
    {
        var version = TcPtyNative.Version();
        if (version >> 16 != TcPtyNative.AbiMajor)
        {
            throw new PtyException($"libtkcodepty ABI {version >> 16}.{version & 0xffff} does not match expected {TcPtyNative.AbiMajor}.x");
        }
        var info = new TcPtyInitInfo { Size = (uint)sizeof(TcPtyInitInfo) };
        TcPtyNative.Init(&info);
        return info;
    });

    public static void EnsureInitialized() => _ = Info.Value;

    public static bool PidfdSupported => Info.Value.PidfdSupported != 0;

    public static string StrError(int errno)
    {
        var buf = stackalloc byte[256];
        var n = TcPtyNative.StrError(errno, buf, 256);
        return Encoding.UTF8.GetString(buf, Math.Min(n, 255));
    }
}
