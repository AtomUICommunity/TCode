using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace TKCode.Desktop.Services;

/// <param name="Arguments">传给 TKCode.exe 的命令行（<see cref="AppCommand.ToCommandLine"/>）。</param>
internal sealed record JumpListEntry(string Title, string Arguments, string? Description);

/// <summary>
/// Windows 任务栏按钮右键菜单（跳转列表）。系统默认只有「固定到任务栏 / 关闭窗口」，而「关闭窗口」只是把 TKCode 隐藏到托盘，
/// 所以这里写入：任务「新会话」「退出 TKCode」和自定义类别「项目」（最近项目）。每个条目都是「TKCode.exe + 参数」，
/// 点击后新进程把命令转发给已运行的实例（<see cref="SingleInstance"/>）；没有运行时则直接启动并执行。
///
/// 用 .NET 的源生成 COM（<c>GeneratedComInterface</c>）调用 ICustomDestinationList，不依赖运行时反射，NativeAOT 安全。
/// 跳转列表只是增强：任何失败都只记一条跟踪日志，不影响启动。
/// </summary>
internal static partial class WindowsJumpList
{
    private static readonly Guid ClsidDestinationList = new("77F10CF0-3DB5-4966-B520-B7C54FD35ED6");
    private static readonly Guid ClsidEnumerableObjectCollection = new("2D3468C1-36A7-43B6-AC24-D3F02FD9607A");
    private static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IidObjectArray = new("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9");

    /// <summary>PKEY_Title：条目在跳转列表里显示的文字。</summary>
    private static readonly PropertyKey KeyTitle = new(new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), 2);

    private const uint ClsctxInprocServer = 1;
    private const ushort VtLpwstr = 31;

    private static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>用新的内容整体替换跳转列表。非 Windows、或不是以 TKCode.exe 运行（例如 dotnet run 的宿主）时什么也不做。</summary>
    public static void Update(string projectsCategory, IReadOnlyList<JumpListEntry> projects, IReadOnlyList<JumpListEntry> tasks)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var exe = Environment.ProcessPath;
        if (exe is null || !string.Equals(Path.GetFileNameWithoutExtension(exe), "TKCode", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        try
        {
            Apply(exe, projectsCategory, projects, tasks);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"TKCode: updating the taskbar jump list failed: {ex.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void Apply(string exe, string projectsCategory, IReadOnlyList<JumpListEntry> projects, IReadOnlyList<JumpListEntry> tasks)
    {
        var list = Create<ICustomDestinationList>(ClsidDestinationList);
        list.BeginList(out var slots, in IidObjectArray, out _);
        try
        {
            // 用户在列表里移除过某个条目后，再次追加该类别会失败；这时只丢掉这个类别，任务照常提交
            var visible = projects.Take((int)Math.Min(slots, int.MaxValue)).ToList();
            if (visible.Count > 0)
            {
                try
                {
                    list.AppendCategory(projectsCategory, Collect(exe, visible));
                }
                catch (COMException ex)
                {
                    Trace.TraceWarning($"TKCode: jump list category was rejected: {ex.Message}");
                }
            }
            list.AddUserTasks(Collect(exe, tasks));
            list.CommitList();
        }
        catch
        {
            list.AbortList();
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    private static IObjectArray Collect(string exe, IReadOnlyList<JumpListEntry> entries)
    {
        var collection = Create<IObjectCollection>(ClsidEnumerableObjectCollection);
        foreach (var entry in entries)
        {
            collection.AddObject(CreateLink(exe, entry));
        }
        return collection;
    }

    [SupportedOSPlatform("windows")]
    private static IShellLinkW CreateLink(string exe, JumpListEntry entry)
    {
        var link = Create<IShellLinkW>(ClsidShellLink);
        link.SetPath(exe);
        link.SetArguments(entry.Arguments);
        link.SetIconLocation(exe, 0);
        if (Path.GetDirectoryName(exe) is { Length: > 0 } directory)
        {
            link.SetWorkingDirectory(directory);
        }
        if (entry.Description is { Length: > 0 and < 1000 } description)
        {
            link.SetDescription(description);
        }
        // 没有 PKEY_Title 的 IShellLink 不会显示在跳转列表里
        var store = (IPropertyStore)link;
        var title = new PropVariant { Vt = VtLpwstr, Data = Marshal.StringToCoTaskMemUni(entry.Title) };
        try
        {
            store.SetValue(in KeyTitle, in title);
            store.Commit();
        }
        finally
        {
            PropVariantClear(ref title);
        }
        return link;
    }

    [SupportedOSPlatform("windows")]
    private static T Create<T>(Guid clsid) where T : class
    {
        Marshal.ThrowExceptionForHR(CoCreateInstance(in clsid, 0, ClsctxInprocServer, in IidUnknown, out var unknown));
        try
        {
            // 包装对象自己持有一份引用，这里归还 CoCreateInstance 返回的那一份
            return (T)Wrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);

    [LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(ref PropVariant pvar);

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    /// <summary>PROPVARIANT 的布局：4 个 16 位头 + 联合体（64 位 24 字节，32 位 16 字节），这里只用到 VT_LPWSTR。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariant
    {
        public ushort Vt;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public nint Data;
        public nint Data2;
    }

    // 下面接口的方法顺序必须与 Windows SDK 头文件中的虚函数表一致（即使有的方法用不到）。

    [GeneratedComInterface]
    [Guid("6332DEBF-87B5-4670-90C0-5E57B408A49E")]
    internal partial interface ICustomDestinationList
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string pszAppID);
        void BeginList(out uint pcMinSlots, in Guid riid, out IObjectArray ppv);
        void AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string pszCategory, IObjectArray poa);
        void AppendKnownCategory(int category);
        void AddUserTasks(IObjectArray poa);
        void CommitList();
        void GetRemovedDestinations(in Guid riid, out IObjectArray ppv);
        void DeleteList([MarshalAs(UnmanagedType.LPWStr)] string pszAppID);
        void AbortList();
    }

    [GeneratedComInterface]
    [Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
    internal partial interface IObjectArray
    {
        void GetCount(out uint pcObjects);
        void GetAt(uint uiIndex, in Guid riid, out nint ppv);
    }

    [GeneratedComInterface]
    [Guid("5632B1A4-E38A-400A-928A-D4CD63230295")]
    internal partial interface IObjectCollection : IObjectArray
    {
        /// <summary>原型是 IUnknown*：IShellLinkW 的指针同样是合法的 IUnknown*。</summary>
        void AddObject(IShellLinkW punk);
        void AddFromArray(IObjectArray poaSource);
        void RemoveObjectAt(uint uiIndex);
        void Clear();
    }

    [GeneratedComInterface]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    internal partial interface IShellLinkW
    {
        void GetPath(nint pszFile, int cch, nint pfd, uint fFlags);
        void GetIDList(out nint ppidl);
        void SetIDList(nint pidl);
        void GetDescription(nint pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory(nint pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments(nint pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out ushort pwHotkey);
        void SetHotkey(ushort wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation(nint pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(nint hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [GeneratedComInterface]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    internal partial interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PropertyKey pkey);
        void GetValue(in PropertyKey key, out PropVariant pv);
        void SetValue(in PropertyKey key, in PropVariant propvar);
        void Commit();
    }
}
