using System.Runtime.InteropServices;

namespace TKCode.Config;

/// <summary>
/// 未处理异常的崩溃日志：进程因未处理异常终止前，把异常全文写到 ~/.tkcode/logs/crash-&lt;process&gt;-yyyyMMdd-HHmmss.log。
/// NativeAOT 下未处理异常只打印到 stderr 然后 abort()，从 Finder 启动时这些信息会丢失，系统崩溃报告里也没有托管符号。
/// 未观察的 Task 异常不会终止进程，只追加到同一天的 crash-&lt;process&gt;-unobserved-yyyyMMdd.log。
/// </summary>
public static class CrashLog
{
    private static readonly object Gate = new();
    private static string _directory = string.Empty;
    private static string _process = string.Empty;
    private static string _version = string.Empty;
    private static Exception? _lastWritten;

    public static string LogDirectory => _directory;

    /// <summary>进程启动时调用一次。<paramref name="process"/> 用于文件名（desktop / host）。</summary>
    public static void Install(string directory, string process, string version)
    {
        _directory = directory;
        _process = process;
        _version = version;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                Write(ex, e.IsTerminating ? "unhandled (terminating)" : "unhandled");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) => WriteUnobserved(e.Exception);
    }

    /// <summary>
    /// 写一份崩溃日志并返回路径；失败时返回 null（磁盘满、无权限等不能再引发异常）。
    /// 同一个异常对象经多个入口（如 UI 线程与 AppDomain）上报时只写一次。
    /// </summary>
    public static string? Write(Exception exception, string source)
    {
        if (_directory.Length == 0)
        {
            return null;
        }
        lock (Gate)
        {
            if (ReferenceEquals(_lastWritten, exception))
            {
                return null;
            }
            _lastWritten = exception;
            var path = Path.Combine(_directory, $"crash-{_process}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            return TryAppend(path, Format(exception, source)) ? path : null;
        }
    }

    private static void WriteUnobserved(Exception exception)
    {
        if (_directory.Length == 0)
        {
            return;
        }
        lock (Gate)
        {
            TryAppend(Path.Combine(_directory, $"crash-{_process}-unobserved-{DateTime.Now:yyyyMMdd}.log"), Format(exception, "unobserved task exception"));
        }
    }

    internal static string Format(Exception exception, string source) =>
        $"""
         ==== {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {source}
         Process:   {_process} {_version} (pid {Environment.ProcessId})
         OS:        {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}
         Runtime:   {RuntimeInformation.FrameworkDescription} {RuntimeInformation.ProcessArchitecture}
         Thread:    {Environment.CurrentManagedThreadId} {Thread.CurrentThread.Name}
         Uptime:    {Uptime()}

         {exception}

         """;

    private static string Uptime()
    {
        try
        {
            return (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).ToString("g");
        }
        catch (Exception)
        {
            return "?";
        }
    }

    private static bool TryAppend(string path, string text)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream);
            writer.Write(text);
            writer.Flush();
            stream.Flush(flushToDisk: true);  // 随后就是 abort()，必须落盘
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
