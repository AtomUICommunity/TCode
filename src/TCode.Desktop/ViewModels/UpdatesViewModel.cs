using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TCode.Desktop.Localization;
using TCode.Desktop.Services;
using TCode.Desktop.Updates;

namespace TCode.Desktop.ViewModels;

/// <summary>
/// 更新的界面状态（auto-update.md §9）：右下角提醒卡片、侧栏小圆点、「关于」页的「更新」一节。
/// 安装前确认运行中的会话：卡片切换为确认内容，可选「等待完成后安装」（会话全部空闲后自动继续）。
/// </summary>
public sealed partial class UpdatesViewModel : ObservableObject
{
    private readonly UpdateService _service;
    private readonly UserSettingsService _settings;
    private readonly ConversationViewModel _conversation;
    private readonly PtyHostService _ptyHost;
    private readonly ILogger _logger;

    public UpdatesViewModel(UpdateService service, UserSettingsService settings, ConversationViewModel conversation, PtyHostService ptyHost,
        ILogger<UpdatesViewModel> logger)
    {
        _service = service;
        _settings = settings;
        _conversation = conversation;
        _ptyHost = ptyHost;
        _logger = logger;
        service.PropertyChanged += OnServicePropertyChanged;
        settings.Changed += () =>
        {
            OnPropertyChanged(nameof(IsAutoCheck));
            OnPropertyChanged(nameof(IsReceivingPrereleases));
        };
        Strings.LanguageChanged += (_, _) => RefreshText();
        // 等待中的安装：会话全部空闲后自动继续
        conversation.Sessions.CollectionChanged += (_, _) => _ = ContinueWhenIdleAsync();
    }

    /// <summary>主窗口退出（安装程序已启动）。由视图层设置，测试中替换为记录调用。</summary>
    public Action? Shutdown { get; set; }

    /// <summary>打开设置 › 关于。</summary>
    public Func<Task>? OpenAbout { get; set; }

    public UpdateService Service => _service;

    // ---- 卡片 ----

    public bool IsCardVisible => _service.IsCardVisible;

    /// <summary>安装前确认运行中的会话。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CardMode))]
    public partial bool IsConfirming { get; private set; }

    /// <summary>选择了「等待完成后安装」。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CardMode))]
    public partial bool IsWaitingForIdle { get; private set; }

    [ObservableProperty]
    public partial int RunningCount { get; private set; }

    /// <summary>卡片当前显示的内容。</summary>
    public UpdateCardMode CardMode =>
        IsConfirming ? UpdateCardMode.Confirm
        : IsWaitingForIdle ? UpdateCardMode.Waiting
        : _service.JustUpdated ? UpdateCardMode.Updated
        : _service.Status switch
        {
            UpdateStatus.Downloading => UpdateCardMode.Downloading,
            UpdateStatus.Ready => _service.WasInterrupted ? UpdateCardMode.Interrupted : UpdateCardMode.Ready,
            UpdateStatus.Failed => UpdateCardMode.Failed,
            _ => UpdateCardMode.Available,
        };

    public bool IsAvailableCard => CardMode == UpdateCardMode.Available;
    public bool IsDownloadingCard => CardMode == UpdateCardMode.Downloading;
    public bool IsReadyCard => CardMode is UpdateCardMode.Ready or UpdateCardMode.Interrupted;
    public bool IsFailedCard => CardMode == UpdateCardMode.Failed;
    public bool IsConfirmCard => CardMode == UpdateCardMode.Confirm;
    public bool IsWaitingCard => CardMode == UpdateCardMode.Waiting;
    public bool IsUpdatedCard => CardMode == UpdateCardMode.Updated;

    public string? VersionText => _service.Candidate?.Version.ToString();
    public bool IsPreview => _service.Candidate?.IsPreview == true;
    public bool CanInstallInApp => _service.CanInstallInApp;
    public bool HasUpdate => _service.HasUpdate;
    public double ProgressPercent => _service.Progress * 100;

    public string CardTitle => CardMode switch
    {
        UpdateCardMode.Downloading => Strings.Format(TCodeLangResourceKind.UpdateDownloadingFormat, VersionText),
        UpdateCardMode.Ready => Strings.Format(TCodeLangResourceKind.UpdateReadyFormat, VersionText),
        UpdateCardMode.Interrupted => Strings.Get(TCodeLangResourceKind.UpdateInterrupted),
        UpdateCardMode.Failed => Strings.Get(TCodeLangResourceKind.UpdateFailed),
        UpdateCardMode.Confirm => Strings.Get(TCodeLangResourceKind.UpdateConfirmTitle),
        UpdateCardMode.Waiting => Strings.Get(TCodeLangResourceKind.UpdateWaitingTitle),
        UpdateCardMode.Updated => Strings.Format(TCodeLangResourceKind.UpdateUpdatedFormat, _service.Current),
        _ => Strings.Format(TCodeLangResourceKind.UpdateAvailableFormat, VersionText),
    };

    public string? CardBody => CardMode switch
    {
        UpdateCardMode.Available when !CanInstallInApp => ManualHint,
        UpdateCardMode.Downloading => Strings.Format(TCodeLangResourceKind.UpdateProgressFormat, (int)ProgressPercent),
        UpdateCardMode.Ready => Strings.Get(TCodeLangResourceKind.UpdateReadyHint),
        UpdateCardMode.Interrupted => Strings.Format(TCodeLangResourceKind.UpdateInterruptedHintFormat, VersionText),
        UpdateCardMode.Failed => _service.Error,
        UpdateCardMode.Confirm => Strings.Format(TCodeLangResourceKind.UpdateConfirmHintFormat, RunningCount),
        UpdateCardMode.Waiting => Strings.Get(TCodeLangResourceKind.UpdateWaitingHint),
        _ => null,
    };

    public bool HasCardBody => CardBody is { Length: > 0 };

    public TCode.Desktop.Icons.LucideKind CardIcon => CardMode switch
    {
        UpdateCardMode.Downloading => TCode.Desktop.Icons.LucideKind.Download,
        UpdateCardMode.Ready or UpdateCardMode.Updated => TCode.Desktop.Icons.LucideKind.Check,
        UpdateCardMode.Failed or UpdateCardMode.Interrupted or UpdateCardMode.Confirm => TCode.Desktop.Icons.LucideKind.CircleAlert,
        UpdateCardMode.Waiting => TCode.Desktop.Icons.LucideKind.History,
        _ => TCode.Desktop.Icons.LucideKind.Sparkles,
    };

    private string? ManualHint => _service.Target is InstallTarget.ManualOnly manual
        ? Strings.Get(manual.Reason switch
        {
            ManualReason.MacNotInstalled => TCodeLangResourceKind.UpdateManualMacHint,
            ManualReason.Portable => TCodeLangResourceKind.UpdateManualPortableHint,
            ManualReason.ReadOnly => TCodeLangResourceKind.UpdateManualReadOnlyHint,
            _ => TCodeLangResourceKind.UpdateManualHint,
        })
        : null;

    // ---- 「关于」页 ----

    public string CurrentVersionText => Strings.Format(TCodeLangResourceKind.AboutVersionFormat,
        AppVersionInfo.Commit is { } commit ? $"{_service.Current} ({commit})" : _service.Current.ToString());

    public bool IsAutoCheck
    {
        get => _service.AutoCheck;
        set
        {
            if (value != _service.AutoCheck)
            {
                _settings.SetAutoCheckUpdates(value);
            }
        }
    }

    /// <summary>接收预发布版本更新（默认开启）。</summary>
    public bool IsReceivingPrereleases
    {
        get => _service.Channel == UpdateChannel.Preview;
        set
        {
            if (value != IsReceivingPrereleases)
            {
                _settings.SetReceivePrereleases(value);
            }
        }
    }

    public bool IsChecking => _service.Status == UpdateStatus.Checking;

    /// <summary>「关于」页的状态行；保留了已发现 / 已下载的版本而这次检查失败时，附上失败原因。</summary>
    public string StatusText => _service.Error is { } error && _service.Status is UpdateStatus.Available or UpdateStatus.Ready
        ? BaseStatusText + " · " + Strings.Format(TCodeLangResourceKind.UpdateCheckFailedFormat, error)
        : BaseStatusText;

    private string BaseStatusText => _service.Status switch
    {
        UpdateStatus.Checking => Strings.Get(TCodeLangResourceKind.UpdateChecking),
        UpdateStatus.UpToDate => _service.LastCheckAt is { } at
            ? Strings.Format(TCodeLangResourceKind.UpdateUpToDateFormat, at.ToLocalTime().ToString("HH:mm"))
            : Strings.Get(TCodeLangResourceKind.UpdateUpToDate),
        UpdateStatus.Available => Strings.Format(TCodeLangResourceKind.UpdateAvailableFormat, VersionText),
        UpdateStatus.Downloading => Strings.Format(TCodeLangResourceKind.UpdateDownloadingFormat, VersionText) + " · " + (int)ProgressPercent + "%",
        UpdateStatus.Ready => Strings.Format(TCodeLangResourceKind.UpdateReadyFormat, VersionText),
        UpdateStatus.Installing => Strings.Get(TCodeLangResourceKind.UpdateInstalling),
        UpdateStatus.Failed => Strings.Get(TCodeLangResourceKind.UpdateFailed) + (_service.Error is { } e ? ": " + e : ""),
        _ when _service.IsDevBuild => Strings.Get(TCodeLangResourceKind.UpdateDevBuild),
        _ => _service.LastCheckAt is { } last
            ? Strings.Format(TCodeLangResourceKind.UpdateLastCheckFormat, last.ToLocalTime().ToString("g"))
            : "",
    };

    /// <summary>更新说明（Markdown，「关于」页用 StreamMark 渲染）：待安装的新版本，或刚更新到的当前版本。</summary>
    public string? Notes => _service.Notes;

    public bool HasNotes => Notes is { Length: > 0 } &&
                            (_service.JustUpdated || _service.Status is UpdateStatus.Available or UpdateStatus.Downloading or UpdateStatus.Ready);

    /// <summary>更新说明标题：「TCode 1.2.0 更新说明」。</summary>
    public string NotesTitle => Strings.Format(TCodeLangResourceKind.UpdateNotesTitleFormat,
        _service.JustUpdated ? _service.Current.ToString() : VersionText);
    public bool ShowDownloadButton => _service.Status == UpdateStatus.Available && CanInstallInApp;
    public bool ShowOpenPageButton => _service.Status == UpdateStatus.Available && !CanInstallInApp;
    public bool ShowInstallButton => _service.Status == UpdateStatus.Ready;

    // ---- 命令 ----

    [RelayCommand]
    private Task CheckNowAsync() => _service.CheckNowAsync();

    [RelayCommand]
    private Task DownloadAsync() => _service.DownloadAsync();

    [RelayCommand]
    private void Later()
    {
        IsConfirming = false;
        _service.Later();
    }

    [RelayCommand]
    private void Skip() => _service.Skip();

    [RelayCommand]
    private Task RetryAsync() => _service.RetryAsync();

    [RelayCommand]
    private void OpenReleasePage() =>
        ShellOpen.Open(_service.JustUpdated ? _service.CurrentReleaseUrl : _service.ReleasePageUrl, _logger);

    /// <summary>「更新说明」：打开设置 › 关于（那里有相同的操作按钮，卡片随之收起）。</summary>
    [RelayCommand]
    private async Task ShowNotesAsync()
    {
        _service.IsCardVisible = false;
        if (OpenAbout is { } open)
        {
            await open();
        }
    }

    /// <summary>「安装并重启」：有运行中的会话时先确认。</summary>
    [RelayCommand]
    private Task InstallAsync()
    {
        RunningCount = CountBusySessions();
        if (RunningCount > 0)
        {
            IsConfirming = true;
            _service.IsCardVisible = true;
            return Task.CompletedTask;
        }
        return InstallNowAsync();
    }

    /// <summary>确认框的「立即安装」。</summary>
    [RelayCommand]
    private Task InstallNowAsync()
    {
        IsConfirming = false;
        IsWaitingForIdle = false;
        return InstallCoreAsync();
    }

    /// <summary>确认框的「等待完成后安装」。</summary>
    [RelayCommand]
    private Task InstallWhenIdleAsync()
    {
        IsConfirming = false;
        IsWaitingForIdle = true;
        foreach (var session in _conversation.Sessions)
        {
            session.PropertyChanged -= OnSessionPropertyChanged;
            session.PropertyChanged += OnSessionPropertyChanged;
        }
        return ContinueWhenIdleAsync();
    }

    [RelayCommand]
    private void CancelInstall()
    {
        IsConfirming = false;
        IsWaitingForIdle = false;
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionItemViewModel.IsRunning) or nameof(SessionItemViewModel.IsWaitingApproval))
        {
            _ = ContinueWhenIdleAsync();
        }
    }

    private Task ContinueWhenIdleAsync() =>
        IsWaitingForIdle && CountBusySessions() == 0 ? InstallNowAsync() : Task.CompletedTask;

    private int CountBusySessions() => _conversation.Sessions.Count(s => s.IsRunning || s.IsWaitingApproval);

    /// <summary>终端按 revive 方式退出（保存快照，新版本启动后恢复），然后启动安装程序并退出程序（auto-update.md §7）。</summary>
    private async Task InstallCoreAsync()
    {
        foreach (var session in _conversation.Sessions)
        {
            session.PropertyChanged -= OnSessionPropertyChanged;
        }
        await _ptyHost.QuitForUpdateAsync();
        if (_service.Install())
        {
            Shutdown?.Invoke();
        }
    }

    private void OnServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UpdateService.IsCardVisible))
        {
            OnPropertyChanged(nameof(IsCardVisible));
            return;
        }
        RefreshText();
    }

    private void RefreshText()
    {
        OnPropertyChanged(nameof(CardMode));
        OnPropertyChanged(nameof(IsAvailableCard));
        OnPropertyChanged(nameof(IsDownloadingCard));
        OnPropertyChanged(nameof(IsReadyCard));
        OnPropertyChanged(nameof(IsFailedCard));
        OnPropertyChanged(nameof(IsConfirmCard));
        OnPropertyChanged(nameof(IsWaitingCard));
        OnPropertyChanged(nameof(IsUpdatedCard));
        OnPropertyChanged(nameof(CardTitle));
        OnPropertyChanged(nameof(CardBody));
        OnPropertyChanged(nameof(HasCardBody));
        OnPropertyChanged(nameof(CardIcon));
        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(IsPreview));
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(CurrentVersionText));
        OnPropertyChanged(nameof(IsReceivingPrereleases));
        OnPropertyChanged(nameof(IsAutoCheck));
        OnPropertyChanged(nameof(IsChecking));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(NotesTitle));
        OnPropertyChanged(nameof(ShowDownloadButton));
        OnPropertyChanged(nameof(ShowOpenPageButton));
        OnPropertyChanged(nameof(ShowInstallButton));
    }

    partial void OnIsConfirmingChanged(bool value) => RefreshText();

    partial void OnIsWaitingForIdleChanged(bool value) => RefreshText();

    partial void OnRunningCountChanged(int value) => RefreshText();
}

public enum UpdateCardMode
{
    Available,
    Downloading,
    Ready,
    Interrupted,
    Failed,
    Confirm,
    Waiting,
    Updated,
}
