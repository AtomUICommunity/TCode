using System.Runtime.InteropServices;
using Avalonia.Platform;

namespace TKCode.Desktop.Services;

/// <summary>
/// macOS 上 Window.Icon 不会改 Dock 图标；未打包成 .app 直接运行时 Dock 会显示默认的 "exec"。
/// 这里通过 libobjc 设置 NSApplication.applicationIconImage（纯 P/Invoke，NativeAOT 安全）。
/// </summary>
internal static class MacDockIcon
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjC)] private static extern IntPtr objc_getClass(IntPtr name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(IntPtr name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr sel, IntPtr arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr sel, IntPtr arg, nint length);

    public static void Apply()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://TKCode/Assets/app-logo.png"));
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();

            var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                var data = Send(Class("NSData"), Sel("dataWithBytes:length:"), pinned.AddrOfPinnedObject(), bytes.Length);
                var image = Send(Send(Class("NSImage"), Sel("alloc")), Sel("initWithData:"), data);
                if (image == IntPtr.Zero)
                {
                    return;
                }
                var app = Send(Class("NSApplication"), Sel("sharedApplication"));
                Send(app, Sel("setApplicationIconImage:"), image);
            }
            finally
            {
                pinned.Free();
            }
        }
        catch
        {
            // 图标仅是装饰，失败不影响启动
        }
    }

    private static IntPtr Class(string name) => WithCString(name, objc_getClass);
    private static IntPtr Sel(string name) => WithCString(name, sel_registerName);

    private static IntPtr WithCString(string s, Func<IntPtr, IntPtr> f)
    {
        var p = Marshal.StringToCoTaskMemUTF8(s);
        try { return f(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }
}
