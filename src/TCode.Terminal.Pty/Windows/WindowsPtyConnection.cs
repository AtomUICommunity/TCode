using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using TCode.Terminal.Core;

namespace TCode.Terminal.Pty.Windows;

/// <summary>
/// Windows 本地 PTY（ConPTY，03-pty.md §7）。读线程在 CreateProcess 之前启动，以便镜像核心及时应答 ConPTY 的 DA1；
/// 关闭时读线程继续读到 BROKEN_PIPE，ClosePseudoConsole 在单独的线程上调用（W1）。
/// </summary>
internal sealed class WindowsPtyConnection : ITerminalConnection
{
    private static readonly PipeOptions OutputPipe = new(
        pool: MemoryPool<byte>.Shared, readerScheduler: PipeScheduler.Inline, writerScheduler: PipeScheduler.Inline,
        pauseWriterThreshold: 256 * 1024, resumeWriterThreshold: 128 * 1024, minimumSegmentSize: 64 * 1024,
        useSynchronizationContext: false);

    private readonly Pipe _output = new(OutputPipe);
    private readonly Channel<byte[]> _writes = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource<TerminalExit> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _readerDone = new();
    private readonly Lock _closeLock = new();
    private readonly nint _inWrite;
    private readonly nint _outRead;
    private readonly Thread _readThread;
    private readonly Thread _writeThread;
    private nint _pseudoConsole;
    private nint _process;
    private nint _job;
    private nint _readThreadHandle;
    private long _startedAt;
    private long _lastOutputTick;
    private volatile bool _discarding;
    private bool _pseudoConsoleClosed;
    private int _closed;

    private WindowsPtyConnection(nint pseudoConsole, nint inWrite, nint outRead)
    {
        _pseudoConsole = pseudoConsole;
        _inWrite = inWrite;
        _outRead = outRead;
        _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "pty-read" };
        _writeThread = new Thread(WriteLoop) { IsBackground = true, Name = "pty-write" };
    }

    public string Kind => "local-pty";

    public unsafe ConnectionCapabilities Capabilities => ConnectionCapabilities.Resize | ConnectionCapabilities.Signals |
        (ConPtyApi.Clear != null ? ConnectionCapabilities.ClearBuffer : ConnectionCapabilities.None);

    public PipeReader Output => _output.Reader;

    public Task<TerminalExit> Exited => _exited.Task;

    public int ProcessId { get; private set; }

    public ConPtyImplementation Implementation => ConPtyApi.Implementation;

    public bool CwdFallback { get; private set; }

    public static unsafe WindowsPtyConnection Start(PtyLaunch launch, string baseDirectory)
    {
        ConPtyApi.EnsureLoaded(baseDirectory);

        nint inRead, inWrite, outRead, outWrite;
        if (!Win32.CreatePipe(&inRead, &inWrite, null, 0))
        {
            throw Error("CreatePipe");
        }
        if (!Win32.CreatePipe(&outRead, &outWrite, null, 0))
        {
            var error = Error("CreatePipe");
            Win32.CloseHandle(inRead);
            Win32.CloseHandle(inWrite);
            throw error;
        }

        nint hpc;
        var size = new Coord { X = checked((short)launch.InitialSize.Columns), Y = checked((short)launch.InitialSize.Rows) };
        var hr = ConPtyApi.Create(size, inRead, outWrite, Flags(launch), &hpc);
        // ConPTY 内部已经 DuplicateHandle，这两端可以立即关闭
        Win32.CloseHandle(inRead);
        Win32.CloseHandle(outWrite);
        if (hr < 0)
        {
            Win32.CloseHandle(inWrite);
            Win32.CloseHandle(outRead);
            throw new PtyException($"CreatePseudoConsole failed (HRESULT 0x{hr:X8})", nativeError: hr);
        }

        var connection = new WindowsPtyConnection(hpc, inWrite, outRead);
        connection._readThread.Start();
        connection._writeThread.Start();
        try
        {
            connection.StartProcess(launch);
        }
        catch
        {
            connection.ClosePseudoConsoleOnce();
            connection._readThread.Join(1000);
            Win32.CloseHandle(inWrite);
            Win32.CloseHandle(outRead);
            throw;
        }
        return connection;
    }

    private static uint Flags(PtyLaunch launch)
    {
        // 随包 1.25：宽度标志必须与模拟器的宽度模型一致（W5）。系统 conhost 必须传 0（旧版本中 0x8 是已删除的 PASSTHROUGH_MODE）。
        if (ConPtyApi.Implementation != ConPtyImplementation.Bundled)
        {
            return 0;
        }
        var flags = launch.WidthMode == PtyWidthMode.Grapheme ? 0x08u : 0x10u;
        return launch.AmbiguousWide ? flags | 0x20u : flags;
    }

    private unsafe void StartProcess(PtyLaunch launch)
    {
        _job = CreateJob();

        nuint listSize = 0;
        Win32.InitializeProcThreadAttributeList(null, 2, 0, &listSize);
        var list = NativeMemory.Alloc(listSize);
        try
        {
            if (!Win32.InitializeProcThreadAttributeList(list, 2, 0, &listSize))
            {
                throw Error("InitializeProcThreadAttributeList");
            }
            try
            {
                // PSEUDOCONSOLE 传的是 HPCON 值本身，不是它的地址
                if (!Win32.UpdateProcThreadAttribute(list, 0, Win32.ProcThreadAttributePseudoConsole, (void*)_pseudoConsole,
                        (nuint)sizeof(nint), null, null))
                {
                    throw Error("UpdateProcThreadAttribute(PSEUDOCONSOLE)");
                }
                var job = _job;
                if (job != 0)
                {
                    Win32.UpdateProcThreadAttribute(list, 0, Win32.ProcThreadAttributeJobList, &job, (nuint)sizeof(nint), null, null);
                }

                var si = new StartupInfoEx { Cb = sizeof(StartupInfoEx), Flags = Win32.StartfUseStdHandles, AttributeList = list };
                var commandLine = (launch.WindowsCommandLine ?? WindowsCommandLine.Build(launch.File, launch.Arguments)) + '\0';
                var environment = WindowsCommandLine.BuildEnvironmentBlock(launch.Environment);
                var cwd = launch.WorkingDirectory;
                if (string.IsNullOrEmpty(cwd) || !Directory.Exists(cwd))
                {
                    CwdFallback = !string.IsNullOrEmpty(cwd);
                    cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                }
                cwd += '\0';
                var cmd = commandLine.ToCharArray();
                ProcessInformation pi;
                fixed (char* pCmd = cmd)
                fixed (char* pEnv = environment)
                fixed (char* pCwd = cwd)
                {
                    // bInheritHandles 必须为 FALSE：否则 TCode 的可继承句柄会泄漏给 shell（W9）
                    if (!Win32.CreateProcessW(null, pCmd, null, null, 0,
                            Win32.ExtendedStartupInfoPresent | Win32.CreateUnicodeEnvironment, pEnv, pCwd, &si, &pi))
                    {
                        throw Error($"CreateProcess({launch.File})");
                    }
                }
                Win32.CloseHandle(pi.Thread);
                _process = pi.Process;
                ProcessId = pi.ProcessId;
                _startedAt = Environment.TickCount64;
            }
            finally
            {
                Win32.DeleteProcThreadAttributeList(list);
            }
        }
        finally
        {
            NativeMemory.Free(list);
        }

        // 之后输出管道上的 BROKEN_PIPE 就是"所有客户端都已断开"的可靠 EOF
        if (ConPtyApi.Release != null)
        {
            ConPtyApi.Release(_pseudoConsole);
        }
        new Thread(WaitForExit, 256 * 1024) { IsBackground = true, Name = $"pty-wait-{ProcessId}" }.Start();
    }

    private static unsafe nint CreateJob()
    {
        var job = Win32.CreateJobObjectW(null, null);
        if (job == 0)
        {
            return 0;
        }
        var info = new JobObjectExtendedLimitInformation
        {
            LimitFlags = Win32.JobObjectLimitKillOnJobClose | Win32.JobObjectLimitBreakawayOk,
        };
        Win32.SetInformationJobObject(job, Win32.JobObjectExtendedLimitInformation, &info, (uint)sizeof(JobObjectExtendedLimitInformation));
        return job;
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

    public unsafe ValueTask ResizeAsync(TerminalSize size, CancellationToken cancellationToken = default)
    {
        lock (_closeLock)
        {
            if (!_pseudoConsoleClosed)
            {
                ConPtyApi.Resize(_pseudoConsole, new Coord { X = checked((short)size.Columns), Y = checked((short)size.Rows) });
            }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> SignalAsync(TerminalSignal signal, CancellationToken cancellationToken = default)
    {
        switch (signal)
        {
            case TerminalSignal.Int:
                // ConPTY 把输入中的 0x03 转换为 CTRL_C_EVENT
                return ValueTask.FromResult(TryWriteReply([0x03]));
            case TerminalSignal.Kill:
                return ValueTask.FromResult(_job != 0 ? Win32.TerminateJobObject(_job, 1) : Win32.TerminateProcess(_process, 1));
            case TerminalSignal.Hup or TerminalSignal.Term:
                ClosePseudoConsoleAsync();
                return ValueTask.FromResult(true);
            default:
                return ValueTask.FromResult(false);
        }
    }

    // Windows 没有 tcgetpgrp；进程树判断（Toolhelp32）在 M3 实现，cwd 只来自 OSC 7 / 9;9（W11）
    public ValueTask<ForegroundInfo?> GetForegroundAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<ForegroundInfo?>(null);

    public ValueTask<string?> GetWorkingDirectoryAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>(null);

    /// <summary>用户执行"清除缓冲区"时同步清空 conhost 的缓冲区，否则下一次重绘会把旧内容画回来（W6）。</summary>
    public unsafe ValueTask ClearBufferAsync(bool keepCursorRow)
    {
        lock (_closeLock)
        {
            if (!_pseudoConsoleClosed && ConPtyApi.Clear != null)
            {
                ConPtyApi.Clear(_pseudoConsole, keepCursorRow ? 1 : 0);
            }
        }
        return ValueTask.CompletedTask;
    }

    public async ValueTask CloseAsync(TimeSpan grace)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            await _exited.Task.ConfigureAwait(false);
            return;
        }
        _writes.Writer.TryComplete();
        // W2：客户端还在启动时关闭会弹出 0xc0000142 错误框
        var sinceStart = Environment.TickCount64 - _startedAt;
        if (sinceStart < 500)
        {
            await Task.WhenAny(_exited.Task, Task.Delay(TimeSpan.FromMilliseconds(500 - sinceStart))).ConfigureAwait(false);
        }
        _discarding = true;
        _output.Writer.CancelPendingFlush();
        ClosePseudoConsoleAsync();
        if (await Task.WhenAny(_exited.Task, Task.Delay(grace)).ConfigureAwait(false) != _exited.Task)
        {
            Win32.TerminateProcess(_process, 1);
            await Task.WhenAny(_exited.Task, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
        }
        if (!_readerDone.Wait(1000) && _readThreadHandle != 0)
        {
            Win32.CancelSynchronousIo(_readThreadHandle);
        }
        Win32.CloseHandle(_inWrite);
        Win32.CloseHandle(_outRead);
        if (_exited.Task.IsCompleted)
        {
            Win32.CloseHandle(_process);  // 等待线程已结束；否则保留句柄，让它继续等待
        }
        ReleaseJob();
    }

    /// <summary>正常关闭：清除 KILL_ON_JOB_CLOSE 再关闭 Job，用户从终端 start 出去的程序保留（03 §7.5）。</summary>
    private unsafe void ReleaseJob()
    {
        if (_job == 0)
        {
            return;
        }
        var info = new JobObjectExtendedLimitInformation { LimitFlags = Win32.JobObjectLimitBreakawayOk };
        Win32.SetInformationJobObject(_job, Win32.JobObjectExtendedLimitInformation, &info, (uint)sizeof(JobObjectExtendedLimitInformation));
        Win32.CloseHandle(_job);
        _job = 0;
    }

    public ValueTask DisposeAsync() => CloseAsync(TimeSpan.FromSeconds(2));

    /// <summary>ClosePseudoConsole 绝不在读线程、终结器或 UI 线程上调用（W1）。</summary>
    private void ClosePseudoConsoleAsync() => new Thread(ClosePseudoConsoleOnce) { IsBackground = true, Name = "pty-close" }.Start();

    private unsafe void ClosePseudoConsoleOnce()
    {
        nint hpc;
        lock (_closeLock)
        {
            if (_pseudoConsoleClosed)
            {
                return;
            }
            _pseudoConsoleClosed = true;
            hpc = _pseudoConsole;
            _pseudoConsole = 0;
        }
        ConPtyApi.Close(hpc);
    }

    private unsafe void ReadLoop()
    {
        nint self;
        Win32.DuplicateHandle(Win32.GetCurrentProcess(), Win32.GetCurrentThread(), Win32.GetCurrentProcess(), &self, 0, 0, 2 /* SAME_ACCESS */);
        _readThreadHandle = self;
        var writer = _output.Writer;
        byte[]? scratch = null;
        try
        {
            while (true)
            {
                uint read;
                bool ok;
                if (_discarding)
                {
                    scratch ??= new byte[64 * 1024];
                    fixed (byte* p = scratch)
                    {
                        ok = Win32.ReadFile(_outRead, p, (uint)scratch.Length, &read, null);
                    }
                    if (!ok || read == 0)
                    {
                        break;
                    }
                    continue;
                }
                var memory = writer.GetMemory(64 * 1024);
                using (var pin = memory.Pin())
                {
                    ok = Win32.ReadFile(_outRead, (byte*)pin.Pointer, (uint)memory.Length, &read, null);
                }
                if (!ok || read == 0)
                {
                    break;  // ERROR_BROKEN_PIPE：所有客户端都已断开
                }
                writer.Advance((int)read);
                Volatile.Write(ref _lastOutputTick, Environment.TickCount64);
                var flush = writer.FlushAsync();
                var result = flush.IsCompleted ? flush.Result : flush.AsTask().GetAwaiter().GetResult();
                if (result.IsCompleted)
                {
                    _discarding = true;
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

    private unsafe void WriteLoop()
    {
        var reader = _writes.Reader;
        try
        {
            while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (reader.TryRead(out var data))
                {
                    fixed (byte* p = data)
                    {
                        for (var offset = 0; offset < data.Length;)
                        {
                            uint written;
                            var chunk = (uint)Math.Min(4096, data.Length - offset);
                            if (!Win32.WriteFile(_inWrite, p + offset, chunk, &written, null))
                            {
                                return;
                            }
                            offset += (int)written;
                        }
                    }
                }
            }
        }
        catch (ChannelClosedException)
        {
        }
    }

    private unsafe void WaitForExit()
    {
        Win32.WaitForSingleObject(_process, Win32.Infinite);
        uint code;
        var exit = Win32.GetExitCodeProcess(_process, &code) ? new TerminalExit(unchecked((int)code), null) : TerminalExit.Unknown;
        // 没有 ReleasePseudoConsole 时，EOF 只会在伪控制台关闭后出现
        if (ConPtyApi.Implementation == ConPtyImplementation.InboxNoRelease)
        {
            ClosePseudoConsoleAsync();
        }
        var exitedAt = Environment.TickCount64;
        var deadline = exitedAt + 1000;
        while (!_readerDone.IsSet && Environment.TickCount64 < deadline &&
               Environment.TickCount64 - Math.Max(exitedAt, Volatile.Read(ref _lastOutputTick)) < 100)
        {
            _readerDone.Wait(20);
        }
        _exited.TrySetResult(exit);
    }

    private static PtyException Error(string what)
    {
        var error = Marshal.GetLastPInvokeError();
        return new PtyException($"{what} failed (Win32 error {error})", nativeError: error);
    }
}
