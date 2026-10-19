using Microsoft.Extensions.Logging;

namespace TKCode.Config;

/// <summary>
/// 把日志追加到 &lt;directory&gt;/&lt;prefix&gt;-yyyyMMdd.log（如 ~/.tkcode/logs/host-20261018.log），便于事后排查：
/// 从 Finder / 桌面端启动的进程 stdout、stderr 都不可见。
/// </summary>
public sealed class FileLoggerProvider(string directory, string prefix) : ILoggerProvider
{
    private readonly object _gate = new();

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(string line)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, $"{prefix}-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }
            var text = $"{DateTime.Now:HH:mm:ss.fff} {logLevel,-11} {category}: {formatter(state, exception)}";
            owner.Write(exception is null ? text : $"{text}\n{exception}");
        }
    }
}
