using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace TKCode.Terminal.Protocol;

/// <summary>
/// Windows 上脱离 Desktop 启动 host（04 §1.3）：CreateProcessW + DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_BREAKAWAY_FROM_JOB，
/// 用 PROC_THREAD_ATTRIBUTE_HANDLE_LIST 只让子进程继承就绪管道的写端。Desktop 所在的 Job 不允许 breakaway 时
/// （ERROR_ACCESS_DENIED）去掉该标志重试，并告诉 host 它被 Job 绑定（jobBound）。
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsHostLauncher
{
    private const uint DetachedProcess = 0x00000008;
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const int StartfUseStdHandles = 0x00000100;
    private const nint ProcThreadAttributeHandleList = 0x20002;
    private const int ErrorAccessDenied = 5;

    public static async Task<int?> LaunchAsync(string exe, List<string> args, CancellationToken ct)
    {
        using var ready = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var child = ready.ClientSafePipeHandle.DangerousGetHandle();
        var process = Start(exe, args, child, breakaway: true, out var error);
        if (process == 0 && error == ErrorAccessDenied)
        {
            process = Start(exe, [.. args, "--job-bound"], child, breakaway: false, out error);
        }
        if (process == 0)
        {
            throw new PtyHostStartException($"CreateProcessW failed (Win32 error {error})");
        }
        ready.DisposeLocalCopyOfClientHandle();
        try
        {
            using var reader = new StreamReader(ready, Encoding.UTF8);
            return await PtyHostLocator.ReadReadyAsync(reader, () => ExitCode(process), ct).ConfigureAwait(false);
        }
        finally
        {
            CloseHandle(process);  // 不 Kill：host 独立运行
        }
    }

    private static unsafe int? ExitCode(nint process)
    {
        uint code;
        return GetExitCodeProcess(process, &code) && code != 259 /* STILL_ACTIVE */ ? (int)code : null;
    }

    private static unsafe nint Start(string exe, List<string> args, nint stdout, bool breakaway, out int error)
    {
        var commandLine = new StringBuilder(Quote(exe));
        foreach (var a in args)
        {
            commandLine.Append(' ').Append(Quote(a));
        }
        nuint size = 0;
        InitializeProcThreadAttributeList(null, 1, 0, &size);
        var list = NativeMemory.Alloc(size);
        try
        {
            if (!InitializeProcThreadAttributeList(list, 1, 0, &size))
            {
                error = Marshal.GetLastPInvokeError();
                return 0;
            }
            var handles = stackalloc nint[1];
            handles[0] = stdout;
            UpdateProcThreadAttribute(list, 0, ProcThreadAttributeHandleList, handles, (nuint)sizeof(nint), null, null);
            var si = new StartupInfoEx();
            si.StartupInfo.cb = sizeof(StartupInfoEx);
            si.StartupInfo.dwFlags = StartfUseStdHandles;
            si.StartupInfo.hStdOutput = stdout;
            si.StartupInfo.hStdError = stdout;
            si.lpAttributeList = list;
            var flags = DetachedProcess | CreateNewProcessGroup | CreateUnicodeEnvironment | ExtendedStartupInfoPresent | (breakaway ? CreateBreakawayFromJob : 0);
            ProcessInformation pi;
            var cmd = commandLine.ToString() + '\0';
            fixed (char* c = cmd)
            {
                if (!CreateProcessW(null, c, null, null, 1, flags, null, null, &si, &pi))
                {
                    error = Marshal.GetLastPInvokeError();
                    return 0;
                }
            }
            CloseHandle(pi.Thread);
            error = 0;
            return pi.Process;
        }
        finally
        {
            DeleteProcThreadAttributeList(list);
            NativeMemory.Free(list);
        }
    }

    /// <summary>CommandLineToArgvW 的引用规则。</summary>
    private static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return arg;
        }
        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var ch in arg)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }
            sb.Append('\\', ch == '"' ? backslashes * 2 + 1 : backslashes);
            backslashes = 0;
            sb.Append(ch);
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb;
        public nint lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public void* lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process, Thread;
        public int ProcessId, ThreadId;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool InitializeProcThreadAttributeList(void* list, int count, uint flags, nuint* size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool UpdateProcThreadAttribute(void* list, uint flags, nint attribute, void* value, nuint size, void* previous, nuint* returnSize);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial void DeleteProcThreadAttributeList(void* list);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcessW(char* application, char* commandLine, void* pa, void* ta, int inherit, uint flags, void* env, char* cwd,
        StartupInfoEx* si, ProcessInformation* pi);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetExitCodeProcess(nint process, uint* code);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
