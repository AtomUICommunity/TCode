namespace TCode.Terminal.Pty.Windows;

/// <summary>
/// Windows 前台进程的启发式判断（03-pty.md §8.2）：Windows 没有 tcgetpgrp，用 Toolhelp32 快照求 shell 的后代，
/// 排除 conhost / OpenConsole，取创建时间最晚的后代。创建时间早于父进程的"子进程"是 pid 复用，丢弃。
/// </summary>
internal static unsafe class ProcessTree
{
    internal readonly record struct Entry(int Pid, int ParentPid, string Name, long CreatedAt);

    /// <summary>取一次快照并判断；失败时返回 null。单次约 1–5 ms，按需调用。</summary>
    public static ForegroundInfo? Foreground(int shellPid)
    {
        var entries = Snapshot();
        return entries is null ? null : Pick(shellPid, entries);
    }

    /// <summary>纯函数部分（跨平台测试）。shell 不在快照里（已退出）时返回 null。</summary>
    internal static ForegroundInfo? Pick(int shellPid, IReadOnlyList<Entry> entries)
    {
        var byParent = new Dictionary<int, List<Entry>>();
        Entry? shell = null;
        foreach (var e in entries)
        {
            if (e.Pid == shellPid)
            {
                shell = e;
            }
            if (!byParent.TryGetValue(e.ParentPid, out var list))
            {
                byParent[e.ParentPid] = list = [];
            }
            list.Add(e);
        }
        if (shell is not { } root)
        {
            return null;
        }

        Entry? newest = null;
        var stack = new Stack<Entry>();
        var visited = new HashSet<int> { root.Pid };
        stack.Push(root);
        while (stack.Count > 0)
        {
            var parent = stack.Pop();
            if (!byParent.TryGetValue(parent.Pid, out var children))
            {
                continue;
            }
            foreach (var child in children)
            {
                // pid 复用：父进程退出后 pid 被新进程拿到，原来的子进程看起来像它的后代
                if (child.CreatedAt != 0 && parent.CreatedAt != 0 && child.CreatedAt < parent.CreatedAt || !visited.Add(child.Pid))
                {
                    continue;
                }
                stack.Push(child);
                if (IsConsoleHost(child.Name))
                {
                    continue;
                }
                if (newest is null || child.CreatedAt > newest.Value.CreatedAt)
                {
                    newest = child;
                }
            }
        }
        return newest is { } fg ? new ForegroundInfo(fg.Pid, StripExe(fg.Name), false) : new ForegroundInfo(root.Pid, StripExe(root.Name), true);
    }

    private static bool IsConsoleHost(string name) =>
        name.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("OpenConsole.exe", StringComparison.OrdinalIgnoreCase);

    private static string StripExe(string name) => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private static List<Entry>? Snapshot()
    {
        var snapshot = Win32.CreateToolhelp32Snapshot(Win32.Th32csSnapProcess, 0);
        if (snapshot == -1)
        {
            return null;
        }
        try
        {
            var entries = new List<Entry>(256);
            var pe = new Win32.ProcessEntry32W { Size = (uint)sizeof(Win32.ProcessEntry32W) };
            for (var ok = Win32.Process32FirstW(snapshot, &pe); ok; ok = Win32.Process32NextW(snapshot, &pe))
            {
                var name = new string(pe.ExeFile);
                entries.Add(new Entry((int)pe.ProcessId, (int)pe.ParentProcessId, name, CreatedAt(pe.ProcessId)));
            }
            return entries;
        }
        finally
        {
            Win32.CloseHandle(snapshot);
        }
    }

    private static long CreatedAt(uint pid)
    {
        var process = Win32.OpenProcess(Win32.ProcessQueryLimitedInformation, 0, pid);
        if (process == 0)
        {
            return 0;  // 无权限（其他会话 / 受保护进程）：不参与 pid 复用校验
        }
        try
        {
            long created, exited, kernel, user;
            return Win32.GetProcessTimes(process, &created, &exited, &kernel, &user) ? created : 0;
        }
        finally
        {
            Win32.CloseHandle(process);
        }
    }
}
