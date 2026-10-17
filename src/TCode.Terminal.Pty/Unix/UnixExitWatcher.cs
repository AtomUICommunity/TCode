using System.Collections.Concurrent;

namespace TCode.Terminal.Pty.Unix;

/// <summary>
/// 进程级的子进程退出监视（03 §6.1）：一个线程挂在 epoll + pidfd（Linux ≥ 5.3）或 kqueue（macOS）上；
/// 不支持时（老内核、seccomp）每个子进程一个阻塞 waitpid(pid) 线程。只回收自己登记的 pid，绝不 waitpid(-1)。
/// </summary>
internal static unsafe class UnixExitWatcher
{
    private static readonly ConcurrentDictionary<int, Action<TerminalExit>> Watches = new();
    private static readonly Lock InitLock = new();
    private static bool _started;

    public static void Watch(int pid, Action<TerminalExit> onExit)
    {
        EnsureStarted();
        Watches[pid] = onExit;
        var rc = TcPtyNative.ExitWatchAdd(pid, 0);
        if (rc == 0)
        {
            return;
        }
        if (rc == TcPtyNative.ESrch)
        {
            // 注册前已经退出（僵尸竞态）：立即回收
            Complete(pid);
            return;
        }
        // 不支持 pidfd / kqueue：专用线程阻塞 waitpid(pid)
        var thread = new Thread(() => Complete(pid), 256 * 1024) { IsBackground = true, Name = $"pty-wait-{pid}" };
        thread.Start();
    }

    private static void EnsureStarted()
    {
        lock (InitLock)
        {
            if (_started)
            {
                return;
            }
            var rc = TcPtyNative.ExitWatchInit();
            if (rc != 0)
            {
                throw new PtyException($"tcpty_exitwatch_init failed ({rc}, errno {TcPtyNative.LastErrno()})", nativeError: TcPtyNative.LastErrno());
            }
            new Thread(Loop, 256 * 1024) { IsBackground = true, Name = "pty-exit-watch" }.Start();
            _started = true;
        }
    }

    private static void Loop()
    {
        var events = stackalloc TcPtyExitEvent[16];
        while (true)
        {
            var n = TcPtyNative.ExitWatchWait(events, 16, -1);
            if (n < 0)
            {
                Thread.Sleep(10);
                continue;
            }
            for (var i = 0; i < n; i++)
            {
                Complete(events[i].Pid);
            }
        }
    }

    /// <summary>阻塞回收并通知。kqueue 给出的状态只作参考，权威状态始终来自 waitpid(pid)。</summary>
    private static void Complete(int pid)
    {
        TcPtyExitStatus status = default;
        status.Size = (uint)sizeof(TcPtyExitStatus);
        var rc = TcPtyNative.Reap(pid, 1, &status);
        var exit = rc == 1 ? ToExit(status) : TerminalExit.Unknown;
        if (Watches.TryRemove(pid, out var callback))
        {
            // 回调可能等待输出排空（最长约 1 s），不能占用监视线程
            ThreadPool.UnsafeQueueUserWorkItem(static s => s.callback(s.exit), (callback, exit), preferLocal: false);
        }
    }

    public static TerminalExit ToExit(in TcPtyExitStatus status) => status.Kind switch
    {
        TcPtyNative.ExitCode => new TerminalExit(status.Value, null),
        TcPtyNative.ExitSignal => new TerminalExit(null, TcPtyNative.SignalNameOf(status.Value), status.CoreDumped != 0),
        _ => TerminalExit.Unknown,
    };
}
