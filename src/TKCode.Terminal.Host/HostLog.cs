using System.Collections.Concurrent;

namespace TKCode.Terminal.Host;

/// <summary>
/// host 日志（04 §1.9）：后台线程批量写文件，单文件 ≤ 5 MiB、最多 10 个。从不记录终端内容、输入、剪贴板与环境变量的值。
/// 没有文件路径时（测试、--self-test）写到 <see cref="Sink"/>。
/// </summary>
public sealed class HostLog : IDisposable
{
    public enum Level { Trace, Debug, Info, Warn, Error }

    private const long MaxFileBytes = 5L << 20;
    private const int MaxFiles = 10;

    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), 10_000);
    private readonly string? _directory;
    private readonly Thread? _writer;
    private readonly Level _level;

    public HostLog(string? directory, Level level = Level.Info, Action<string>? sink = null)
    {
        _directory = directory;
        _level = level;
        Sink = sink;
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
            _writer = new Thread(WriteLoop) { IsBackground = true, Name = "ptyhost-log" };
            _writer.Start();
        }
    }

    public Action<string>? Sink { get; }

    public static Level ParseLevel(string? value) => value?.ToLowerInvariant() switch
    {
        "trace" => Level.Trace,
        "debug" => Level.Debug,
        "warn" or "warning" => Level.Warn,
        "error" => Level.Error,
        _ => Level.Info,
    };

    public void Trace(string message) => Write(Level.Trace, message);
    public void Debug(string message) => Write(Level.Debug, message);
    public void Info(string message) => Write(Level.Info, message);
    public void Warn(string message) => Write(Level.Warn, message);
    public void Error(string message) => Write(Level.Error, message);

    private void Write(Level level, string message)
    {
        if (level < _level)
        {
            return;
        }
        var line = $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {level.ToString().ToUpperInvariant(),-5} {message}";
        Sink?.Invoke(line);
        if (_writer is not null)
        {
            _queue.TryAdd(line);
        }
    }

    private void WriteLoop()
    {
        StreamWriter? writer = null;
        long written = 0;
        try
        {
            foreach (var line in _queue.GetConsumingEnumerable())
            {
                if (writer is null || written > MaxFileBytes)
                {
                    writer?.Dispose();
                    writer = Open();
                    written = 0;
                }
                writer.WriteLine(line);
                written += line.Length + 1;
                if (_queue.Count == 0)
                {
                    writer.Flush();
                }
            }
        }
        catch (Exception)
        {
            // 日志失败不能影响 host
        }
        finally
        {
            writer?.Dispose();
        }
    }

    private StreamWriter Open()
    {
        var path = Path.Combine(_directory!, $"ptyhost-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
        var old = Directory.GetFiles(_directory!, "ptyhost-*.log").Order(StringComparer.Ordinal).ToList();
        foreach (var f in old.Take(Math.Max(0, old.Count - (MaxFiles - 1))))
        {
            try
            {
                File.Delete(f);
            }
            catch (IOException)
            {
            }
        }
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return new StreamWriter(stream) { AutoFlush = false };
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer?.Join(TimeSpan.FromSeconds(2));
    }
}
