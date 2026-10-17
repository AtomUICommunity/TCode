using System.Runtime.InteropServices;

[assembly: System.Runtime.CompilerServices.DisableRuntimeMarshalling]

namespace TCode.Terminal.Pty.Unix;

/// <summary>libtcodepty（native/tcodepty/include/tcodepty.h）。所有可变参数的系统调用都在 C 里完成（research/02 §1.5）。</summary>
internal static unsafe partial class TcPtyNative
{
    private const string Lib = "libtcodepty";

    public const int AbiMajor = 1;

    // 可移植错误码（tcodepty.h）
    public const int EAgain = -1;
    public const int EIo = -2;
    public const int ESrch = -5;
    public const int EChild = -6;
    public const int ENoSys = -7;
    public const int EPerm = -3;

    public const uint SpawnIutf8 = 1;
    public const uint GatherEof = 1;
    public const uint GatherWoken = 2;
    public const int PollIn = 1, PollOut = 2, PollHup = 4, PollWoken = 8;
    public const int ExitCode = 1, ExitSignal = 2;

    [LibraryImport(Lib, EntryPoint = "tcpty_version")] public static partial int Version();
    [LibraryImport(Lib, EntryPoint = "tcpty_last_errno")] public static partial int LastErrno();
    [LibraryImport(Lib, EntryPoint = "tcpty_strerror")] public static partial int StrError(int errno, byte* buf, int cap);
    [LibraryImport(Lib, EntryPoint = "tcpty_init")] public static partial int Init(TcPtyInitInfo* info);
    [LibraryImport(Lib, EntryPoint = "tcpty_spawn")] public static partial int Spawn(TcPtySpawnOptions* options, TcPtySpawnResult* result);
    [LibraryImport(Lib, EntryPoint = "tcpty_spawn_pipe")] public static partial int SpawnPipe(TcPtyPipeOptions* options, TcPtyPipeResult* result);
    [LibraryImport(Lib, EntryPoint = "tcpty_set_nonblock")] public static partial int SetNonblock(int fd, int enable);
    [LibraryImport(Lib, EntryPoint = "tcpty_close")] public static partial int Close(int fd);
    [LibraryImport(Lib, EntryPoint = "tcpty_wake_create")] public static partial int WakeCreate(int* readFd, int* writeFd);
    [LibraryImport(Lib, EntryPoint = "tcpty_wake_signal")] public static partial int WakeSignal(int writeFd);
    [LibraryImport(Lib, EntryPoint = "tcpty_read_gather")]
    public static partial int ReadGather(int fd, int wakeFd, byte* buf, int cap, TcPtyGatherConfig* config, uint* flags);
    [LibraryImport(Lib, EntryPoint = "tcpty_read")] public static partial int Read(int fd, byte* buf, int cap);
    [LibraryImport(Lib, EntryPoint = "tcpty_write")] public static partial int Write(int fd, byte* buf, int length);
    [LibraryImport(Lib, EntryPoint = "tcpty_wait")] public static partial int Wait(int fd, int wakeFd, int events, int timeoutMs, uint* revents);
    [LibraryImport(Lib, EntryPoint = "tcpty_resize")] public static partial int Resize(int fd, ushort cols, ushort rows, ushort xpixel, ushort ypixel);
    [LibraryImport(Lib, EntryPoint = "tcpty_get_winsize")] public static partial int GetWinsize(int fd, ushort* output);
    [LibraryImport(Lib, EntryPoint = "tcpty_set_thread_qos_interactive")] public static partial void SetThreadQosInteractive();
    [LibraryImport(Lib, EntryPoint = "tcpty_reap")] public static partial int Reap(int pid, int block, TcPtyExitStatus* status);
    [LibraryImport(Lib, EntryPoint = "tcpty_decode_wait_status")] public static partial int DecodeWaitStatus(int raw, TcPtyExitStatus* status);
    [LibraryImport(Lib, EntryPoint = "tcpty_exitwatch_init")] public static partial int ExitWatchInit();
    [LibraryImport(Lib, EntryPoint = "tcpty_exitwatch_add")] public static partial int ExitWatchAdd(int pid, uint flags);
    [LibraryImport(Lib, EntryPoint = "tcpty_exitwatch_wait")] public static partial int ExitWatchWait(TcPtyExitEvent* events, int cap, int timeoutMs);
    [LibraryImport(Lib, EntryPoint = "tcpty_exitwatch_wake")] public static partial int ExitWatchWake();
    [LibraryImport(Lib, EntryPoint = "tcpty_signal_number")] public static partial int SignalNumber(int portable);
    [LibraryImport(Lib, EntryPoint = "tcpty_signal_name")] public static partial int SignalName(int signo, byte* buf, int cap);
    [LibraryImport(Lib, EntryPoint = "tcpty_kill")] public static partial int Kill(int pid, int signo);
    [LibraryImport(Lib, EntryPoint = "tcpty_killpg")] public static partial int KillPg(int pgid, int signo);
    [LibraryImport(Lib, EntryPoint = "tcpty_get_fg_pgrp")] public static partial int GetForegroundProcessGroup(int masterFd);
    [LibraryImport(Lib, EntryPoint = "tcpty_get_process_name")] public static partial int GetProcessName(int pid, byte* buf, int cap);
    [LibraryImport(Lib, EntryPoint = "tcpty_get_cwd")] public static partial int GetCwd(int pid, byte* buf, int cap);
    [LibraryImport(Lib, EntryPoint = "tcpty_get_termios_state")] public static partial int GetTermiosState(int fd, uint* flags);

    /// <summary>可移植信号号（tcodepty.h 的 TCPTY_SIG_*）。</summary>
    public static int Signal(TerminalSignal signal) => SignalNumber(signal switch
    {
        TerminalSignal.Hup => 1,
        TerminalSignal.Int => 2,
        TerminalSignal.Quit => 3,
        TerminalSignal.Kill => 4,
        _ => 5,
    });

    public static string SignalNameOf(int signo)
    {
        var buf = stackalloc byte[32];
        var n = SignalName(signo, buf, 32);
        return System.Text.Encoding.UTF8.GetString(buf, Math.Min(n, 31));
    }

    /// <summary>调用 buf/cap 形式的字符串函数；返回 null 表示失败。</summary>
    public static string? ReadString(delegate*<int, byte*, int, int> fn, int arg)
    {
        var buf = stackalloc byte[4096];
        var n = fn(arg, buf, 4096);
        return n < 0 ? null : System.Text.Encoding.UTF8.GetString(buf, Math.Min(n, 4095));
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct TcPtyInitInfo
{
    public uint Size;
    public int PidfdSupported;
    public int SigchldWasIgnored;
    public int StdioRepaired;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct TcPtySpawnOptions
{
    public uint Size;
    public uint Flags;
    public byte* HelperPath;
    public byte* File;
    public byte** Argv;
    public byte** Envp;
    public byte* Cwd;
    public ushort Cols, Rows, XPixel, YPixel;
    public uint StatusTimeoutMs;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TcPtySpawnResult
{
    public uint Size;
    public int MasterFd;
    public int Pid;
    public int Stage;
    public int Error;
    public uint Warnings;
    public int WarnError;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct TcPtyPipeOptions
{
    public uint Size;
    public byte* HelperPath;
    public byte* File;
    public byte** Argv;
    public byte** Envp;
    public byte* Cwd;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TcPtyPipeResult
{
    public uint Size;
    public int Pid;
    public int StdinFd;
    public int StdoutFd;
    public int StderrFd;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TcPtyGatherConfig
{
    public uint Size;
    public int BridgeThreshold;
    public int SpinMax;
    public int BridgePollMs;
    public int BudgetUs;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TcPtyExitStatus
{
    public uint Size;
    public int Kind;
    public int Value;
    public int CoreDumped;
    public int Raw;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TcPtyExitEvent
{
    public int Pid;
    public int HasStatus;
    public int StatusRaw;
}
