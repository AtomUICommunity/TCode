using Microsoft.Extensions.Logging;

namespace TKCode.AgentHost;

/// <summary>把 Host 日志追加到 ~/.tkcode/logs/host-yyyyMMdd.log，便于事后排查（stderr 在从桌面端启动时不可见）。</summary>
public sealed class FileLoggerProvider(string directory) : ILoggerProvider
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
                File.AppendAllText(Path.Combine(directory, $"host-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
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
