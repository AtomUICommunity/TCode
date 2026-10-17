using System.Text.Json.Nodes;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using TKCode.Config;

namespace TKCode.Desktop.Services;

/// <summary>
/// 桌面端对 ~/.tkcode/settings.json 的读写：语言、主题、默认模型，以及设置页对厂商的修改。
/// 文件被外部编辑时（500ms 防抖）重新读取并在 UI 线程触发 <see cref="Changed"/>；厂商与模型的生效由 Agent Host 自己的监听负责。
/// </summary>
public sealed class UserSettingsService : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);
    private readonly ILogger<UserSettingsService> _logger;
    private readonly string _path;
    private FileSystemWatcher? _watcher;
    private Timer? _timer;

    public UserSettingsService(ILogger<UserSettingsService> logger)
        : this(logger, ConfigPaths.SettingsFile)
    {
    }

    public UserSettingsService(ILogger<UserSettingsService> logger, string path)
    {
        _logger = logger;
        _path = path;
        Reload();
        StartWatching();
    }

    public string FilePath => _path;

    /// <summary>最近一次成功解析的配置。</summary>
    public TKCodeSettings Current { get; private set; } = new();

    public IReadOnlyList<string> Errors { get; private set; } = [];

    /// <summary>设置文件内容变化（外部编辑或本服务写入）后在 UI 线程触发。</summary>
    public event Action? Changed;

    public ThemeMode Theme => Current.Theme?.ToLowerInvariant() switch
    {
        "dark" => ThemeMode.Dark,
        "light" => ThemeMode.Light,
        _ => ThemeMode.System,
    };

    public void SetLanguage(string language) => Update(root => root["language"] = language);

    public void SetTheme(ThemeMode mode) => Update(root => root["theme"] = mode.ToString().ToLowerInvariant());

    public void SetReduceMotion(bool value) => Update(root => SettingsFile.Set(root, "reduceMotion", value ? JsonValue.Create(true) : null));

    public void SetOriginalImages(bool value) => Update(root => SettingsFile.Set(root, "originalImages", value ? JsonValue.Create(true) : null));

    public void SetCloseToTray(bool value) => Update(root => SettingsFile.Set(root, "closeToTray", value ? null : JsonValue.Create(false)));

    public void SetDefaultRole(string? role) => Update(root => SettingsFile.Set(root, "defaultRole", role is null or "agent" ? null : JsonValue.Create(role)));

    /// <summary>接收预发布版本：true 写回时删除该字段（默认值）。</summary>
    public void SetReceivePrereleases(bool value) => Update(root => SetUpdateField(root, "prerelease", value ? null : JsonValue.Create(false)));

    /// <summary>自动检查更新：true 写回时删除该字段（默认值）。</summary>
    public void SetAutoCheckUpdates(bool value) => Update(root => SetUpdateField(root, "autoCheck", value ? null : JsonValue.Create(false)));

    private static void SetUpdateField(JsonObject root, string key, JsonNode? value)
    {
        var section = root["update"] as JsonObject;
        if (section is null)
        {
            if (value is null)
            {
                return;
            }
            section = [];
            root["update"] = section;
        }
        SettingsFile.Set(section, key, value);
        if (section.Count == 0)
        {
            root.Remove("update");
        }
    }

    public void SetDefaultModel(string? modelRef) => Update(root => SettingsFile.Set(root, "model", modelRef is null ? null : JsonValue.Create(modelRef)));

    /// <summary>修改 settings.json：只改动涉及的节点，写完立即重新读取并通知。</summary>
    public void Update(Action<JsonObject> mutate)
    {
        try
        {
            SettingsFile.Update(mutate, _path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _logger.LogError(ex, "Failed to write {Path}", _path);
            Errors = [ex.Message];
            Changed?.Invoke();
            return;
        }
        Reload();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _timer?.Dispose();
    }

    private void Reload()
    {
        var result = SettingsFile.Load(_path);
        Errors = result.Errors;
        if (result.Settings is { } settings)
        {
            Current = settings;
        }
    }

    private void StartWatching()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(dir);
            _timer = new Timer(_ => Dispatcher.UIThread.Post(() =>
            {
                Reload();
                Changed?.Invoke();
            }), null, Timeout.Infinite, Timeout.Infinite);
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(_path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
            };
            FileSystemEventHandler schedule = (_, _) => _timer?.Change(Debounce, Timeout.InfiniteTimeSpan);
            _watcher.Changed += schedule;
            _watcher.Created += schedule;
            _watcher.Deleted += schedule;
            _watcher.Renamed += (_, _) => _timer?.Change(Debounce, Timeout.InfiniteTimeSpan);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
        {
            _logger.LogWarning(ex, "Cannot watch {Path}", _path);
        }
    }
}
