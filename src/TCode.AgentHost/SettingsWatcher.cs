using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TCode.Config;

namespace TCode.AgentHost;

/// <summary>
/// 监听 ~/.tcode/settings.json。编辑器保存时常见“写临时文件 + 改名”或多次写入，统一在 500ms 防抖后重新加载。
/// </summary>
public sealed class SettingsWatcher(ModelCatalog catalog, ILogger<SettingsWatcher> logger) : IHostedService, IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);
    private FileSystemWatcher? _watcher;
    private Timer? _timer;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(ConfigPaths.Home);
            _timer = new Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
            _watcher = new FileSystemWatcher(ConfigPaths.Home, Path.GetFileName(ConfigPaths.SettingsFile))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
            };
            _watcher.Changed += (_, _) => Schedule();
            _watcher.Created += (_, _) => Schedule();
            _watcher.Deleted += (_, _) => Schedule();
            _watcher.Renamed += (_, _) => Schedule();
            _watcher.Error += (_, e) => logger.LogWarning(e.GetException(), "settings.json watcher error");
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
        {
            logger.LogWarning(ex, "Cannot watch {File}; changes require a restart or settings/reload", ConfigPaths.SettingsFile);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
        _timer?.Dispose();
        _timer = null;
    }

    private void Schedule() => _timer?.Change(Debounce, Timeout.InfiniteTimeSpan);

    private void Reload()
    {
        try
        {
            catalog.Reload();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Reloading settings failed");
        }
    }
}
