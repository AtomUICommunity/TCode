using System.Runtime.InteropServices;

namespace TCode.Desktop.Motion;

/// <summary>
/// 动效总开关：设置里的“减少动态效果”、macOS 系统的“减少动态效果”、测试中的强制关闭，任一生效时所有 TCode 动效直接跳到终态。
/// </summary>
public static class MotionSettings
{
    private static bool _userReduceMotion;
    private static bool _forceDisabled;
    private static bool? _systemReduceMotion;

    /// <summary>开关变化（用户设置或系统设置）。</summary>
    public static event Action? Changed;

    public static bool IsEnabled => !_forceDisabled && !_userReduceMotion && !SystemReduceMotion;

    /// <summary>设置项 ui.reduceMotion。</summary>
    public static bool UserReduceMotion
    {
        get => _userReduceMotion;
        set
        {
            if (_userReduceMotion != value)
            {
                _userReduceMotion = value;
                Changed?.Invoke();
            }
        }
    }

    /// <summary>无头测试默认关闭动效，保证截图稳定；动效测试自己打开。</summary>
    public static bool ForceDisabled
    {
        get => _forceDisabled;
        set
        {
            if (_forceDisabled != value)
            {
                _forceDisabled = value;
                Changed?.Invoke();
            }
        }
    }

    public static bool SystemReduceMotion => _systemReduceMotion ??= ReadSystemReduceMotion();

    /// <summary>窗口重新激活时调用：用户可能刚在系统设置里改过。</summary>
    public static void RefreshSystem()
    {
        var value = ReadSystemReduceMotion();
        if (_systemReduceMotion != value)
        {
            _systemReduceMotion = value;
            Changed?.Invoke();
        }
    }

    private static bool ReadSystemReduceMotion()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }
        try
        {
            var workspace = MacInterop.Send(MacInterop.Class("NSWorkspace"), MacInterop.Sel("sharedWorkspace"));
            return workspace != IntPtr.Zero &&
                   MacInterop.SendBool(workspace, MacInterop.Sel("accessibilityDisplayShouldReduceMotion"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>libobjc P/Invoke（NativeAOT 安全）。</summary>
    private static class MacInterop
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";

        [DllImport(ObjC)] private static extern IntPtr objc_getClass(IntPtr name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(IntPtr name);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr sel);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SendBool(IntPtr receiver, IntPtr sel);

        public static IntPtr Class(string name) => WithCString(name, objc_getClass);
        public static IntPtr Sel(string name) => WithCString(name, sel_registerName);

        private static IntPtr WithCString(string s, Func<IntPtr, IntPtr> f)
        {
            var p = Marshal.StringToCoTaskMemUTF8(s);
            try { return f(p); }
            finally { Marshal.FreeCoTaskMem(p); }
        }
    }
}
