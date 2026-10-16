using System.Runtime.InteropServices;
using System.Text;

namespace TCode.Terminal.Pty.Windows;

public enum ConPtyImplementation
{
    /// <summary>随包的 conpty.dll + OpenConsole.exe（Microsoft.Windows.Console.ConPTY）。</summary>
    Bundled,

    /// <summary>系统 kernel32 的 ConPTY，且有 ReleasePseudoConsole（Windows 11 24H2+）。</summary>
    Inbox,

    /// <summary>系统 kernel32 的 ConPTY，没有 ReleasePseudoConsole：EOF 要靠关闭伪控制台产生。</summary>
    InboxNoRelease,
}

/// <summary>
/// ConPTY 函数的加载（03-pty.md §7.1）。只按绝对路径加载随包的 conpty.dll：Windows 11 的 System32 下也有一个
/// conpty.dll，裸名加载会悄悄绑定到系统实现；找不到 OpenConsole.exe 时 conpty.dll 会静默回退到系统 conhost，所以一并检查。
/// </summary>
internal static unsafe class ConPtyApi
{
    private static readonly Lock LoadLock = new();
    private static bool _loaded;

    public static delegate* unmanaged<Coord, nint, nint, uint, nint*, int> Create;
    public static delegate* unmanaged<nint, Coord, int> Resize;
    public static delegate* unmanaged<nint, void> Close;
    public static delegate* unmanaged<nint, int> Release;
    public static delegate* unmanaged<nint, int, int> Clear;

    public static ConPtyImplementation Implementation { get; private set; }

    /// <summary>降级原因（为空表示使用随包实现）。</summary>
    public static string? DegradedReason { get; private set; }

    public static void EnsureLoaded(string baseDirectory)
    {
        lock (LoadLock)
        {
            if (_loaded)
            {
                return;
            }
            var dll = Path.Combine(baseDirectory, "conpty.dll");
            var host = Path.Combine(baseDirectory, NativeArchitectureFolder(), "OpenConsole.exe");
            nint lib = 0;
            if (File.Exists(dll) && File.Exists(host))
            {
                fixed (char* p = dll)
                {
                    lib = Win32.LoadLibraryExW(p, 0, Win32.LoadLibrarySearchDllLoadDir | Win32.LoadLibrarySearchSystem32);
                }
            }
            if (lib != 0 && NativeLibrary.TryGetExport(lib, "ConptyCreatePseudoConsole", out var create) &&
                NativeLibrary.TryGetExport(lib, "ConptyResizePseudoConsole", out var resize) &&
                NativeLibrary.TryGetExport(lib, "ConptyClosePseudoConsole", out var close))
            {
                Create = (delegate* unmanaged<Coord, nint, nint, uint, nint*, int>)create;
                Resize = (delegate* unmanaged<nint, Coord, int>)resize;
                Close = (delegate* unmanaged<nint, void>)close;
                Release = (delegate* unmanaged<nint, int>)TryGet(lib, "ConptyReleasePseudoConsole");
                Clear = (delegate* unmanaged<nint, int, int>)TryGet(lib, "ConptyClearPseudoConsole");
                Implementation = ConPtyImplementation.Bundled;
            }
            else
            {
                DegradedReason = lib == 0
                    ? $"bundled ConPTY not found ({dll}, {host})"
                    : "bundled conpty.dll lacks the expected exports";
                var k32 = NativeLibrary.Load("kernel32.dll");  // KnownDLL，裸名安全
                Create = (delegate* unmanaged<Coord, nint, nint, uint, nint*, int>)NativeLibrary.GetExport(k32, "CreatePseudoConsole");
                Resize = (delegate* unmanaged<nint, Coord, int>)NativeLibrary.GetExport(k32, "ResizePseudoConsole");
                Close = (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(k32, "ClosePseudoConsole");
                Release = (delegate* unmanaged<nint, int>)TryGet(k32, "ReleasePseudoConsole");
                Clear = null;
                Implementation = Release != null ? ConPtyImplementation.Inbox : ConPtyImplementation.InboxNoRelease;
            }
            _loaded = true;
        }
    }

    private static nint TryGet(nint lib, string name) => NativeLibrary.TryGetExport(lib, name, out var p) ? p : 0;

    /// <summary>OpenConsole 按**本机**架构选择子目录：x64 进程在 arm64 Windows 上模拟运行时仍用 arm64 的 OpenConsole。</summary>
    private static string NativeArchitectureFolder()
    {
        ushort process, native;
        if (Win32.IsWow64Process2(Win32.GetCurrentProcess(), &process, &native))
        {
            return native == 0xAA64 ? "arm64" : "x64";  // IMAGE_FILE_MACHINE_ARM64 / AMD64
        }
        return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
    }
}

/// <summary>Windows 命令行与环境块（03-pty.md §9）。</summary>
internal static class WindowsCommandLine
{
    /// <summary>按 CommandLineToArgvW 的规则拼接参数。</summary>
    public static string Build(string file, IEnumerable<string> arguments)
    {
        var sb = new StringBuilder();
        Append(sb, file);
        foreach (var argument in arguments)
        {
            sb.Append(' ');
            Append(sb, argument);
        }
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            sb.Append(arg);
            return;
        }
        sb.Append('"');
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
            }
            else
            {
                sb.Append('\\', backslashes);
            }
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
    }

    /// <summary>UTF-16 环境块："K=V\0…\0\0"，按名称不区分大小写排序（CreateProcess 的要求）。</summary>
    public static char[] BuildEnvironmentBlock(IEnumerable<KeyValuePair<string, string>> environment)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in environment.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (key.Length == 0 || key.Contains('\0') || value.Contains('\0'))
            {
                continue;
            }
            sb.Append(key).Append('=').Append(value).Append('\0');
        }
        sb.Append('\0');
        if (sb.Length == 1)
        {
            sb.Append('\0');
        }
        return sb.ToString().ToCharArray();
    }
}
