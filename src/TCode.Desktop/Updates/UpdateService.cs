using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using TCode.Config;
using TCode.Desktop.Services;

namespace TCode.Desktop.Updates;

public enum UpdateStatus
{
    Idle,
    Checking,
    UpToDate,
    /// <summary>发现新版本（<see cref="UpdateService.CanInstallInApp"/> 为 false 时只能打开下载页面）。</summary>
    Available,
    Downloading,
    /// <summary>安装程序已下载并校验，等待「安装并重启」。</summary>
    Ready,
    Installing,
    Failed,
}

/// <summary>
/// 应用内更新（docs/design/auto-update.md）：启动 60 秒后检查一次、之后每 6 小时检查；发现新版本 → 后台下载安装程序 → 启动安装程序后退出。
/// 状态在 UI 线程上更新（从 UI 线程调用时 await 回到 UI 线程），供提醒卡片与「关于」页绑定。
/// </summary>
public sealed partial class UpdateService : ObservableObject, IDisposable
{
    public static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private const string NotesFile = "notes.md";

    private readonly StateService _state;
    private readonly UserSettingsService _settings;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly HttpClient _http;
    private readonly GitHubReleaseFeed _feed;
    private readonly UpdateDownloader _downloader;
    private readonly IReadOnlyList<string> _keys;
    private readonly string _updatesDirectory;
    private readonly bool _autoCheckAllowed;
    private CancellationTokenSource? _schedule;
    private CancellationTokenSource? _work;
    private string? _installerPath;
    private bool _started;

    public UpdateService(StateService state, UserSettingsService settings, ILogger<UpdateService> logger)
        : this(state, settings, logger, new UpdateServiceOptions())
    {
    }

    public UpdateService(StateService state, UserSettingsService settings, ILogger logger, UpdateServiceOptions options)
    {
        _state = state;
        _settings = settings;
        _logger = logger;
        _time = options.TimeProvider;
        Current = options.CurrentVersion ?? AppVersionInfo.Current;
        Rid = options.Rid ?? AppVersionInfo.Rid;
        Target = options.Target ?? UpdateInstaller.Detect(AppContext.BaseDirectory);
        _updatesDirectory = options.UpdatesDirectory ?? Path.Combine(ConfigPaths.Home, "updates");
        var feedOverride = options.FeedOverride ?? Environment.GetEnvironmentVariable("TCODE_UPDATE_FEED");
        _keys = options.PublicKeys ?? ReleaseKeysFor(feedOverride);
        _http = options.HttpClient ?? CreateHttpClient(Current, Rid);
        _feed = new GitHubReleaseFeed(_http, _keys, feedOverride);
        _downloader = new UpdateDownloader(_http);
        IsDevBuild = Current.Prerelease.StartsWith("dev", StringComparison.Ordinal);
        // 开发构建不自动检查；指定了测试源时例外，方便端到端测试
        _autoCheckAllowed = Environment.GetEnvironmentVariable("TCODE_NO_UPDATE") != "1" && (!IsDevBuild || feedOverride is { Length: > 0 });
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>测试源（TCODE_UPDATE_FEED）可以额外信任 TCODE_UPDATE_KEY 指向的公钥；没有测试源时只信任编译进来的发布公钥。</summary>
    private static IReadOnlyList<string> ReleaseKeysFor(string? feedOverride)
    {
        if (feedOverride is { Length: > 0 } && Environment.GetEnvironmentVariable("TCODE_UPDATE_KEY") is { Length: > 0 } keyFile && File.Exists(keyFile))
        {
            return [.. ManifestVerifier.ReleaseKeys, File.ReadAllText(keyFile)];
        }
        return ManifestVerifier.ReleaseKeys;
    }

    private static HttpClient CreateHttpClient(SemVersion version, string rid)
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            // 超时由调用方的 CancellationToken 控制：检查 60 秒；下载可能很久，由用户取消
            Timeout = Timeout.InfiniteTimeSpan,
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"TCode/{version} ({rid})");
        return http;
    }

    public SemVersion Current { get; }
    public string Rid { get; }
    public InstallTarget Target { get; }
    public bool IsDevBuild { get; }
    public bool CanInstallInApp => Target.CanInstall;

    /// <summary>默认也接收预发布版本（settings.json 的 update.prerelease，默认 true）。</summary>
    public UpdateChannel Channel => _settings.Current.Update?.Prerelease == false ? UpdateChannel.Stable : UpdateChannel.Preview;
    public bool AutoCheck => _settings.Current.Update?.AutoCheck != false;

    [ObservableProperty]
    public partial UpdateStatus Status { get; private set; }

    /// <summary>当前发现（或已下载）的新版本。</summary>
    [ObservableProperty]
    public partial UpdateCandidate? Candidate { get; private set; }

    /// <summary>下载进度 0–1。</summary>
    [ObservableProperty]
    public partial double Progress { get; private set; }

    /// <summary>最近一次失败的原因（手动检查、下载、安装）。</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    public partial DateTimeOffset? LastCheckAt { get; private set; }

    /// <summary>
    /// 更新说明（Release 正文，Markdown，由 StreamMark 渲染）：发现新版本时取自 Release；下载时另存为暂存目录的 notes.md，
    /// 因此「已就绪」恢复后与「已更新到 …」时也能显示。
    /// </summary>
    [ObservableProperty]
    public partial string? Notes { get; private set; }

    /// <summary>右下角提醒卡片是否显示。</summary>
    [ObservableProperty]
    public partial bool IsCardVisible { get; set; }

    /// <summary>本次启动是从上一个版本更新而来（卡片显示「已更新到 …」）。</summary>
    [ObservableProperty]
    public partial bool JustUpdated { get; private set; }

    /// <summary>上次启动了安装程序，但当前仍是旧版本（安装被取消或失败）。</summary>
    [ObservableProperty]
    public partial bool WasInterrupted { get; private set; }

    /// <summary>发现新版本或状态变化时触发（卡片之外的入口，例如侧栏小圆点）。</summary>
    public bool HasUpdate => Status is UpdateStatus.Available or UpdateStatus.Downloading or UpdateStatus.Ready;

    partial void OnStatusChanged(UpdateStatus value) => OnPropertyChanged(nameof(HasUpdate));

    /// <summary>下载页面（ManualOnly 或卡片的「查看更新说明」）。</summary>
    public string ReleasePageUrl => Candidate?.NotesUrl ?? $"https://github.com/{GitHubReleaseFeed.Repository}/releases";

    /// <summary>当前版本的 Release 页面（「已更新到 …」卡片）。</summary>
    public string CurrentReleaseUrl => $"https://github.com/{GitHubReleaseFeed.Repository}/releases/tag/v{Current}";

    /// <summary>主窗口显示后调用一次：恢复上次的状态（已更新 / 未完成 / 已下载），然后开始定时检查。</summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }
        _started = true;
        LastCheckAt = _state.Current.Update.LastCheckAt;
        _ = RestoreAsync();
        Reschedule(InitialDelay);
    }

    /// <summary>「关于」页的「检查更新」：立即检查，并把下一次自动检查推迟到 6 小时后。</summary>
    public Task CheckNowAsync()
    {
        Reschedule(Interval);
        return CheckAsync(manual: true);
    }

    private void Reschedule(TimeSpan delay)
    {
        _schedule?.Cancel();
        _schedule?.Dispose();
        _schedule = new CancellationTokenSource();
        _ = LoopAsync(delay, _schedule.Token);
    }

    private async Task LoopAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(delay, _time, cancellationToken);
                delay = Interval;
                if (_autoCheckAllowed && AutoCheck)
                {
                    await CheckAsync(manual: false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>检查一次。自动检查失败只写日志；手动检查失败显示原因。</summary>
    public async Task CheckAsync(bool manual)
    {
        if (Status is UpdateStatus.Checking or UpdateStatus.Downloading or UpdateStatus.Installing || (!manual && Status == UpdateStatus.Ready))
        {
            return;
        }
        if (manual && IsDevBuild && !_autoCheckAllowed)
        {
            Error = null;
            Status = UpdateStatus.Idle;
            return;
        }
        var previous = Status;
        Status = UpdateStatus.Checking;
        Error = null;
        var record = _state.Current.Update;
        var cachePath = Path.Combine(_updatesDirectory, "releases-cache.json");
        var cache = new ReleaseListCache { ETag = record.ETag, Body = File.Exists(cachePath) ? await File.ReadAllTextAsync(cachePath) : null };
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var result = await _feed.CheckAsync(Current, Channel, Rid, AppVersionInfo.OsVersion, cache, timeout.Token);
            record.ETag = cache.ETag;
            if (cache.Body is { } body)
            {
                Directory.CreateDirectory(_updatesDirectory);
                await File.WriteAllTextAsync(cachePath, body);
            }
            record.LastCheckAt = LastCheckAt = _time.GetUtcNow();
            _state.Save();
            var found = result.Candidate;
            if (found is null)
            {
                Status = previous == UpdateStatus.Ready ? UpdateStatus.Ready : UpdateStatus.UpToDate;
                return;
            }
            if (previous == UpdateStatus.Ready && Candidate is { } pending && found.Version <= pending.Version)
            {
                Status = UpdateStatus.Ready;
                return;
            }
            // 更新的版本作废已下载的安装包
            if (previous == UpdateStatus.Ready)
            {
                DiscardPending();
            }
            Candidate = found;
            Notes = found.Notes;
            Status = UpdateStatus.Available;
            if (!manual && record.SkippedVersion != found.Version.ToString())
            {
                IsCardVisible = true;
            }
        }
        catch (Exception ex) when (ex is UpdateFeedException or OperationCanceledException or IOException)
        {
            _logger.LogWarning(ex, "Update check failed");
            if (manual)
            {
                Error = ex is OperationCanceledException ? "Timed out." : ex.Message;
                // 已发现或已下载的版本仍然有效：保留原状态，只显示这次检查失败的原因
                Status = previous is UpdateStatus.Available or UpdateStatus.Ready ? previous : UpdateStatus.Failed;
            }
            else
            {
                Status = previous == UpdateStatus.Checking ? UpdateStatus.Idle : previous;
            }
        }
    }

    /// <summary>「更新」：下载安装程序并做平台校验，完成后进入 Ready 并显示卡片。</summary>
    public async Task DownloadAsync()
    {
        if (Candidate is not { } candidate || !CanInstallInApp || Status is UpdateStatus.Downloading or UpdateStatus.Ready or UpdateStatus.Installing)
        {
            return;
        }
        _work?.Dispose();
        _work = new CancellationTokenSource();
        Status = UpdateStatus.Downloading;
        Error = null;
        Progress = 0;
        var directory = Path.Combine(_updatesDirectory, candidate.Version.ToString());
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, "manifest.json"), candidate.ManifestBytes, _work.Token);
            await File.WriteAllBytesAsync(Path.Combine(directory, "manifest.json.sig"), candidate.SignatureBytes, _work.Token);
            if (candidate.Notes is { Length: > 0 } notes)
            {
                await File.WriteAllTextAsync(Path.Combine(directory, NotesFile), notes, _work.Token);
            }
            var progress = new Progress<double>(p => Progress = p);
            var path = await _downloader.DownloadAsync(candidate.AssetUrl, directory, candidate.Asset, progress, _work.Token);
            await UpdateInstaller.PrepareAsync(Target, path, _work.Token);
            _installerPath = path;
            _state.Current.Update.PendingVersion = candidate.Version.ToString();
            _state.Save();
            Status = UpdateStatus.Ready;
            IsCardVisible = true;
        }
        catch (OperationCanceledException)
        {
            Status = UpdateStatus.Available;
        }
        catch (Exception ex) when (ex is UpdateFeedException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Update download failed");
            Error = ex.Message;
            Status = UpdateStatus.Failed;
            IsCardVisible = true;
        }
    }

    /// <summary>取消正在进行的下载（已下载的部分保留，下次续传）。</summary>
    public void CancelDownload() => _work?.Cancel();

    /// <summary>「跳过此版本」：自动检查不再提醒这个版本。</summary>
    public void Skip()
    {
        if (Candidate is { } candidate)
        {
            _state.Current.Update.SkippedVersion = candidate.Version.ToString();
            _state.Save();
        }
        IsCardVisible = false;
    }

    /// <summary>「稍后」：只关闭本次提醒。</summary>
    public void Later()
    {
        IsCardVisible = false;
        JustUpdated = false;
        WasInterrupted = false;
    }

    /// <summary>
    /// 启动安装程序（macOS / Linux：替换安装位置并启动新版本）。成功返回 true，调用方随后退出程序；失败时显示原因并返回 false。
    /// 调用前应已确认运行中的会话、关闭 pty host（auto-update.md §7）。
    /// </summary>
    public bool Install()
    {
        if (Status != UpdateStatus.Ready || _installerPath is null || Candidate is null)
        {
            return false;
        }
        Status = UpdateStatus.Installing;
        _state.Current.Update.InstallingVersion = Candidate.Version.ToString();
        _state.Save();
        try
        {
            UpdateInstaller.Launch(Target, _installerPath);
            return true;
        }
        catch (Exception ex) when (ex is UpdateFeedException or IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogError(ex, "Failed to start the installer");
            _state.Current.Update.InstallingVersion = null;
            _state.Save();
            Error = ex.Message;
            Status = UpdateStatus.Failed;
            IsCardVisible = true;
            return false;
        }
    }

    /// <summary>失败后的「重试」：下载失败重新下载，检查失败重新检查。</summary>
    public Task RetryAsync()
    {
        if (Candidate is not null && CanInstallInApp)
        {
            Status = UpdateStatus.Available;
            return DownloadAsync();
        }
        return CheckNowAsync();
    }

    /// <summary>
    /// 启动时恢复：上次启动了安装程序 → 判断成功或未完成；有已下载的版本 → 重新验签和校验后回到 Ready。
    /// </summary>
    private async Task RestoreAsync()
    {
        var record = _state.Current.Update;
        try
        {
            if (record.InstallingVersion is { } installing && SemVersion.TryParse(installing, out var installingVersion))
            {
                record.InstallingVersion = null;
                if (Current >= installingVersion)
                {
                    record.PendingVersion = null;
                    record.SkippedVersion = null;
                    _state.Save();
                    var notesPath = Path.Combine(_updatesDirectory, installing, NotesFile);
                    Notes = File.Exists(notesPath) ? await File.ReadAllTextAsync(notesPath) : null;
                    CleanupDirectory(keep: null);
                    UpdateInstaller.CleanupLeftovers(Target);
                    JustUpdated = true;
                    IsCardVisible = true;
                    return;
                }
                _state.Save();
                WasInterrupted = true;
            }
            UpdateInstaller.CleanupLeftovers(Target);
            if (record.PendingVersion is { } pendingText && SemVersion.TryParse(pendingText, out var pending) && pending > Current && CanInstallInApp &&
                await LoadPendingAsync(pending) is { } restored)
            {
                Candidate = restored.Candidate;
                Notes = restored.Candidate.Notes;
                _installerPath = restored.Path;
                Status = UpdateStatus.Ready;
                CleanupDirectory(keep: pendingText);
                IsCardVisible = WasInterrupted;
                return;
            }
            if (record.PendingVersion is not null)
            {
                record.PendingVersion = null;
                _state.Save();
            }
            WasInterrupted = false;
            CleanupDirectory(keep: null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UpdateFeedException)
        {
            _logger.LogWarning(ex, "Could not restore update state");
        }
    }

    /// <summary>从暂存目录恢复已下载的版本：重新验签清单、校验安装程序；macOS / Linux 还要确认 .new 目录仍在（不在就重新解压）。</summary>
    private async Task<(UpdateCandidate Candidate, string Path)?> LoadPendingAsync(SemVersion version)
    {
        var directory = Path.Combine(_updatesDirectory, version.ToString());
        var manifestPath = Path.Combine(directory, "manifest.json");
        var signaturePath = Path.Combine(directory, "manifest.json.sig");
        if (!File.Exists(manifestPath) || !File.Exists(signaturePath))
        {
            return null;
        }
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
        var signature = await File.ReadAllBytesAsync(signaturePath);
        if (ManifestVerifier.VerifyAndParse(manifestBytes, signature, _keys) is not { } manifest || manifest.Version != version.ToString() ||
            !manifest.Assets.TryGetValue(Rid, out var asset))
        {
            return null;
        }
        var path = Path.Combine(directory, Path.GetFileName(asset.Name));
        if (!await UpdateDownloader.MatchesAsync(path, asset, CancellationToken.None))
        {
            return null;
        }
        if (UpdateInstaller.InstallPath(Target) is { } installPath && !Directory.Exists(UpdateInstaller.StagedDirectory(installPath)))
        {
            await UpdateInstaller.PrepareAsync(Target, path, CancellationToken.None);
        }
        var notesPath = Path.Combine(directory, NotesFile);
        var notes = File.Exists(notesPath) ? await File.ReadAllTextAsync(notesPath) : null;
        var candidate = new UpdateCandidate(version, manifest.Channel == "preview", manifest, manifestBytes, signature, asset, "", notes, manifest.NotesUrl);
        return (candidate, path);
    }

    private void DiscardPending()
    {
        _installerPath = null;
        _state.Current.Update.PendingVersion = null;
        _state.Save();
        CleanupDirectory(keep: null);
        if (UpdateInstaller.InstallPath(Target) is { } installPath)
        {
            UpdateInstaller.DeleteDirectory(UpdateInstaller.StagedDirectory(installPath));
        }
    }

    /// <summary>删除暂存目录中除 <paramref name="keep"/> 以外的版本。</summary>
    private void CleanupDirectory(string? keep)
    {
        if (!Directory.Exists(_updatesDirectory))
        {
            return;
        }
        foreach (var dir in Directory.GetDirectories(_updatesDirectory))
        {
            if (Path.GetFileName(dir) != keep)
            {
                UpdateInstaller.DeleteDirectory(dir);
            }
        }
    }

    private void OnSettingsChanged()
    {
        OnPropertyChanged(nameof(Channel));
        OnPropertyChanged(nameof(AutoCheck));
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _schedule?.Cancel();
        _schedule?.Dispose();
        _work?.Cancel();
        _work?.Dispose();
        _http.Dispose();
    }
}

/// <summary>测试与端到端测试用的注入点；默认值即生产配置。</summary>
public sealed record UpdateServiceOptions
{
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public SemVersion? CurrentVersion { get; init; }
    public string? Rid { get; init; }
    public InstallTarget? Target { get; init; }
    public string? UpdatesDirectory { get; init; }
    public string? FeedOverride { get; init; }
    public IReadOnlyList<string>? PublicKeys { get; init; }
    public HttpClient? HttpClient { get; init; }
}
