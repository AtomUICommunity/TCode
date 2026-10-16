using System.Runtime.InteropServices;

namespace TCode.Terminal.Pty.Windows;

/// <summary>kernel32 互操作（03-pty.md §7.6）。只用 blittable 类型与指针。</summary>
internal static unsafe partial class Win32
{
    private const string Kernel32 = "kernel32.dll";

    public const uint ExtendedStartupInfoPresent = 0x00080000;
    public const uint CreateUnicodeEnvironment = 0x00000400;
    public const int StartfUseStdHandles = 0x00000100;
    public const nint ProcThreadAttributePseudoConsole = 0x00020016;
    public const nint ProcThreadAttributeJobList = 0x0002000D;
    public const uint LoadLibrarySearchDllLoadDir = 0x00000100;
    public const uint LoadLibrarySearchSystem32 = 0x00000800;
    public const int ErrorBrokenPipe = 109;
    public const int ErrorNoData = 232;
    public const uint Infinite = 0xFFFFFFFF;
    public const int JobObjectExtendedLimitInformation = 9;
    public const uint JobObjectLimitKillOnJobClose = 0x00002000;
    public const uint JobObjectLimitBreakawayOk = 0x00000800;

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreatePipe(nint* readPipe, nint* writePipe, void* attributes, uint size);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReadFile(nint file, byte* buffer, uint toRead, uint* read, void* overlapped);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WriteFile(nint file, byte* buffer, uint toWrite, uint* written, void* overlapped);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PeekNamedPipe(nint pipe, void* buffer, uint size, uint* read, uint* totalAvailable, uint* leftThisMessage);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CancelSynchronousIo(nint thread);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InitializeProcThreadAttributeList(void* list, int count, uint flags, nuint* size);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UpdateProcThreadAttribute(void* list, uint flags, nint attribute, void* value, nuint size, void* previous, nuint* returnSize);

    [LibraryImport(Kernel32)]
    public static partial void DeleteProcThreadAttributeList(void* list);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateProcessW(char* application, char* commandLine, void* processAttributes, void* threadAttributes,
        int inheritHandles, uint creationFlags, void* environment, char* currentDirectory, StartupInfoEx* startupInfo, ProcessInformation* info);

    [LibraryImport(Kernel32, SetLastError = true)]
    public static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(nint process, uint* exitCode);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(nint process, uint exitCode);

    [LibraryImport(Kernel32, SetLastError = true)]
    public static partial nint CreateJobObjectW(void* attributes, char* name);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetInformationJobObject(nint job, int infoClass, void* info, uint length);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateJobObject(nint job, uint exitCode);

    [LibraryImport(Kernel32, SetLastError = true)]
    public static partial nint LoadLibraryExW(char* fileName, nint file, uint flags);

    [LibraryImport(Kernel32)]
    public static partial nint GetCurrentThread();

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DuplicateHandle(nint sourceProcess, nint source, nint targetProcess, nint* target, uint access, int inherit, uint options);

    [LibraryImport(Kernel32)]
    public static partial nint GetCurrentProcess();

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWow64Process2(nint process, ushort* processMachine, ushort* nativeMachine);
}

[StructLayout(LayoutKind.Sequential)]
internal struct Coord
{
    public short X;
    public short Y;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct StartupInfoEx
{
    public int Cb;
    public char* Reserved;
    public char* Desktop;
    public char* Title;
    public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
    public short ShowWindow;
    public short CbReserved2;
    public void* Reserved2;
    public nint StdInput, StdOutput, StdError;
    public void* AttributeList;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ProcessInformation
{
    public nint Process;
    public nint Thread;
    public int ProcessId;
    public int ThreadId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct JobObjectExtendedLimitInformation
{
    public long PerProcessUserTimeLimit;
    public long PerJobUserTimeLimit;
    public uint LimitFlags;
    public nuint MinimumWorkingSetSize;
    public nuint MaximumWorkingSetSize;
    public uint ActiveProcessLimit;
    public nuint Affinity;
    public uint PriorityClass;
    public uint SchedulingClass;
    public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    public nuint ProcessMemoryLimit;
    public nuint JobMemoryLimit;
    public nuint PeakProcessMemoryUsed;
    public nuint PeakJobMemoryUsed;
}
