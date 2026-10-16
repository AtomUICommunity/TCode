using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TCode.Desktop.Icons;
using TCode.Desktop.Localization;
using TCode.Desktop.Services;
using TCode.Rpc;

namespace TCode.Desktop.ViewModels;

/// <summary>侧栏会话列表中的一行。会话在 UI 中打开后关联 <see cref="Live"/>，状态随事件更新。</summary>
public sealed partial class SessionItemViewModel : ObservableObject
{
    public SessionItemViewModel(string sessionId, string filePath, string cwd)
    {
        SessionId = sessionId;
        FilePath = filePath;
        Cwd = cwd;
        ProjectName = System.IO.Path.GetFileName(cwd.TrimEnd(System.IO.Path.DirectorySeparatorChar));
    }

    public string SessionId { get; }
    public string FilePath { get; }
    public string Cwd { get; }
    public string ProjectName { get; }
    public string ProjectInitial => ProjectName.Length > 0 ? ProjectName[..1].ToUpperInvariant() : "?";

    [ObservableProperty]
    public partial string? Title { get; set; }

    [ObservableProperty]
    public partial string? ModelId { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset LastActivity { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowIdleDot), nameof(ShowSpinner))]
    public partial bool IsRunning { get; set; }

    /// <summary>有等待批准的操作：侧栏显示琥珀色圆点。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowIdleDot), nameof(ShowSpinner))]
    public partial bool IsWaitingApproval { get; set; }

    public bool ShowSpinner => IsRunning && !IsWaitingApproval;
    public bool ShowIdleDot => !IsRunning && !IsWaitingApproval;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial SessionViewModel? Live { get; set; }

    public string DisplayTitle => CustomTitle ?? (string.IsNullOrWhiteSpace(Title) ? Strings.Get(TCodeLangResourceKind.NewSession) : Title);

    /// <summary>用户重命名的标题（优先于第一条用户消息）。</summary>
    [ObservableProperty]
    public partial string? CustomTitle { get; set; }

    partial void OnCustomTitleChanged(string? value) => OnPropertyChanged(nameof(DisplayTitle));

    [ObservableProperty]
    public partial bool IsPinned { get; set; }

    /// <summary>外部运行时的原生会话 id（复制会话 ID 时一并复制）。</summary>
    public string? NativeSessionId { get; set; }

    /// <summary>右键菜单与行内重命名的执行者（由 <see cref="ConversationViewModel"/> 设置）。</summary>
    internal ConversationViewModel? Owner { get; set; }

    /// <summary>只有内置 Agent 的会话能在新会话中继续（外部 Agent 的上下文在 CLI 里）。</summary>
    public bool CanFork => Runtime is null;

    public string PinMenuText => Strings.Get(IsPinned ? TCodeLangResourceKind.SessionUnpin : TCodeLangResourceKind.SessionPin);

    public string RevealMenuText => Strings.Get(OperatingSystem.IsWindows() ? TCodeLangResourceKind.SessionRevealExplorer
        : OperatingSystem.IsMacOS() ? TCodeLangResourceKind.SessionRevealFinder : TCodeLangResourceKind.SessionRevealFolder);

    partial void OnIsPinnedChanged(bool value) => OnPropertyChanged(nameof(PinMenuText));

    // ---- 行内重命名 ----

    [ObservableProperty]
    public partial bool IsRenaming { get; private set; }

    [ObservableProperty]
    public partial string EditTitle { get; set; } = "";

    [RelayCommand]
    private void BeginRename()
    {
        EditTitle = DisplayTitle;
        IsRenaming = true;
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    [RelayCommand]
    private async Task CommitRenameAsync()
    {
        if (!IsRenaming)
        {
            return;
        }
        IsRenaming = false;
        var title = EditTitle.Trim();
        if (title != DisplayTitle && Owner is not null)
        {
            await Owner.RenameSessionAsync(this, title);
        }
    }

    [RelayCommand]
    private Task TogglePinAsync() => Owner?.SetPinnedAsync(this, !IsPinned) ?? Task.CompletedTask;

    [RelayCommand]
    private Task DeleteAsync() => Owner?.DeleteSessionAsync(this) ?? Task.CompletedTask;

    [RelayCommand]
    private Task ForkAsync() => Owner?.ForkSessionAsync(this) ?? Task.CompletedTask;

    [RelayCommand]
    private Task CopyIdAsync() => Owner?.CopySessionIdAsync(this) ?? Task.CompletedTask;

    [RelayCommand]
    private void Reveal() => Owner?.RevealSessionFile(this);
    public string? ModelIconKey => ModelId is null ? null : ModelIcons.ForModel(ModelId);

    /// <summary>外部运行时会话：侧栏显示运行时小图标。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RuntimeIconKey), nameof(HasRuntime), nameof(RuntimeTip))]
    public partial string? Runtime { get; set; }

    public bool HasRuntime => Runtime is not null;

    partial void OnRuntimeChanged(string? value) => OnPropertyChanged(nameof(CanFork));
    public string? RuntimeIconKey => Runtime is null ? null : ModelIcons.ForProvider(Runtime);
    public string? RuntimeTip => Runtime is null ? null : Strings.Format(TCodeLangResourceKind.RuntimeBadgeTip, Runtime);

    /// <summary>右侧的相对时间：刚刚 / 5 分钟 / 3 小时 / 2 天。</summary>
    public string RelativeTime
    {
        get
        {
            var age = DateTimeOffset.Now - LastActivity;
            return age.TotalMinutes < 1 ? Strings.Get(TCodeLangResourceKind.TimeNow)
                : age.TotalHours < 1 ? Strings.Format(TCodeLangResourceKind.MinutesAgoFormat, (int)age.TotalMinutes)
                : age.TotalDays < 1 ? Strings.Format(TCodeLangResourceKind.HoursAgoFormat, (int)age.TotalHours)
                : Strings.Format(TCodeLangResourceKind.DaysAgoFormat, (int)age.TotalDays);
        }
    }

    partial void OnTitleChanged(string? value) => OnPropertyChanged(nameof(DisplayTitle));
    partial void OnModelIdChanged(string? value) => OnPropertyChanged(nameof(ModelIconKey));
    partial void OnLastActivityChanged(DateTimeOffset value) => OnPropertyChanged(nameof(RelativeTime));

    public void Refresh()
    {
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(RelativeTime));
        OnPropertyChanged(nameof(PinMenuText));
        OnPropertyChanged(nameof(RevealMenuText));
    }

    /// <summary>从打开的会话同步标题、模型、运行状态与最近活动时间。</summary>
    public void SyncFrom(SessionViewModel live)
    {
        Title = live.Title ?? Title;
        ModelId = live.ModelId;
        Runtime = live.Runtime;
        IsWaitingApproval = live.HasPendingApproval;
        IsRunning = live.IsRunning;
        LastActivity = live.LastActivity;
    }
}

/// <summary>
/// 主区域：当前会话（或尚未创建会话的空状态）、Composer、模型选择器、项目 / 分支，以及全部会话的列表。
/// 每个已打开的会话对应一个 <see cref="SessionViewModel"/>；Host 事件按 SessionId 分发，后台会话也保持最新。
/// </summary>
public sealed partial class ConversationViewModel : ObservableObject
{
    private readonly AgentHostConnection _host;
    private readonly StateService _settings;
    private readonly UserSettingsService _userSettings;
    private readonly ILogger<ConversationViewModel> _logger;
    private readonly Dictionary<string, SessionViewModel> _open = [];
    /// <summary>导航历史：会话 ID，null 表示新对话（空状态）。</summary>
    private readonly List<string?> _history = [null];
    private int _historyIndex;
    private bool _navigating;

    public ConversationViewModel(AgentHostConnection host, StateService settings, UserSettingsService userSettings,
        ILogger<ConversationViewModel> logger)
    {
        _host = host;
        _userSettings = userSettings;
        _host.ModelsChanged += OnModelsChanged;
        _host.ApprovalHandler = HandleApprovalAsync;
        _settings = settings;
        _logger = logger;
        _host.SessionEvent += OnSessionEvent;
        _host.Disconnected += reason =>
        {
            foreach (var session in _open.Values)
            {
                session.SetRunning(false);
                foreach (var approval in session.PendingApprovals.ToList())
                {
                    approval.Cancel();
                }
            }
            _open.Clear();
            foreach (var item in Sessions)
            {
                item.IsRunning = false;
                item.Live = null;
            }
            Active = null;
            StatusMessage = Strings.Format(TCodeLangResourceKind.StatusHostDisconnectedFormat, reason);
        };
        BranchPicker = new BranchPickerViewModel(SwitchBranchAsync, IsWorkspaceBusy);
        Picker = new ModelPickerViewModel(settings);
        Picker.SelectionChanged += () =>
        {
            _ = SyncSessionOptionsAsync();
            OnPropertyChanged(nameof(ShowImagesUnsupported));
        };
        Attachments.CollectionChanged += (_, _) =>
        {
            SendCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasAttachments));
            OnPropertyChanged(nameof(ShowImagesUnsupported));
        };
        Permission.ModeChosen += mode => _ = ApplyPermissionAsync(mode);
        Permission.Show(RememberedPermission());
        Strings.LanguageChanged += (_, _) => RefreshLanguage();
        _ = InitializeAsync();
    }

    public ModelPickerViewModel Picker { get; }

    /// <summary>权限选择器（询问 / 自动放行 / 完全授权）：显示当前会话的档位；空状态显示项目记住的档位。</summary>
    public PermissionPickerViewModel Permission { get; } = new();

    /// <summary>全部会话（按最近活动倒序）。侧栏按项目筛选后显示。</summary>
    public ObservableCollection<SessionItemViewModel> Sessions { get; } = [];

    public BranchPickerViewModel BranchPicker { get; }

    public string DeviceName => DeviceInfo.Name;

    /// <summary>当前会话；null 表示空状态（下一次发送时创建会话）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(HeaderTitle), nameof(HeaderSubtitle), nameof(HeaderIconKey))]
    public partial SessionViewModel? Active { get; private set; }

    public bool IsEmpty => Active is null || Active.IsEmpty;

    partial void OnActiveChanged(SessionViewModel? value) =>
        Permission.Show(value is null ? RememberedPermission() : value.Options.PermissionMode);

    /// <summary>新会话使用的项目；null 表示不在项目中工作（使用用户主目录）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorkspaceName), nameof(HeaderSubtitle))]
    public partial WorkspaceInfo? Workspace { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BranchName))]
    public partial string? Branch { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string ComposerText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    /// <summary>输入框中待发送的图片（粘贴或选择后已保存到附件目录）。</summary>
    public ObservableCollection<ImageAttachmentViewModel> Attachments { get; } = [];

    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>带图片但当前模型不支持图片：只发送图片路径。</summary>
    public bool ShowImagesUnsupported => HasAttachments && Picker.Selected is { Model.SupportsImages: false };

    private int _nextImageNumber = 1;

    /// <summary>添加一张图片附件；返回它（占位符由输入框插入到光标处）。</summary>
    public ImageAttachmentViewModel AddAttachment(string path, string mimeType)
    {
        var attachment = new ImageAttachmentViewModel(path, mimeType, _nextImageNumber++);
        Attachments.Add(attachment);
        return attachment;
    }

    [RelayCommand]
    private void RemoveAttachment(ImageAttachmentViewModel attachment)
    {
        Attachments.Remove(attachment);
        ComposerText = ImagePrompt.RemovePlaceholder(ComposerText, attachment.Number);
        if (Attachments.Count == 0)
        {
            _nextImageNumber = 1;
        }
    }

    [ObservableProperty]
    public partial string? StatusMessage { get; private set; }

    /// <summary>上下文占用比例（0–1）：最近一次助手回复的 输入 + 缓存 + 输出 tokens 除以上下文预算。</summary>
    [ObservableProperty]
    public partial double ContextUsage { get; private set; }

    [ObservableProperty]
    public partial string ContextUsageText { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContextUsage))]
    public partial string? ContextUsageTip { get; private set; }

    public bool HasContextUsage => ContextUsageTip is not null;

    public string WorkspaceName => Workspace?.Name ?? Strings.Get(TCodeLangResourceKind.NoProjectName);
    public string BranchName => Branch ?? Strings.Get(TCodeLangResourceKind.NoBranch);

    public string HeaderTitle => Active?.Title ?? Strings.Get(TCodeLangResourceKind.NewSession);
    public string? HeaderIconKey => Active?.ModelIconKey ?? Picker.Selected?.IconKey;
    public string HeaderSubtitle => Strings.Format(TCodeLangResourceKind.SessionSubtitleFormat,
        Active?.ProjectName ?? WorkspaceName, DeviceName);

    /// <summary>切换项目时通知侧栏同步选中状态。</summary>
    public event Action? WorkspaceChanged;

    public void SetWorkspace(WorkspaceInfo? workspace)
    {
        Workspace = workspace;
        Branch = workspace?.ReadBranch();
        BranchPicker.Load(Branch, workspace?.ReadBranches() ?? []);
        _settings.Current.LastWorkspace = workspace?.Path;
        _settings.Save();
        WorkspaceChanged?.Invoke();
        if (Active is not null && workspace is not null && !SamePath(Active.Cwd, workspace.Path))
        {
            NewChat();
        }
        else if (Active is null)
        {
            Permission.Show(RememberedPermission());
        }
    }

    /// <summary>当前项目中有会话正在运行时不允许切换分支（避免 Agent 读写到一半时工作区变化）。</summary>
    private bool IsWorkspaceBusy() =>
        Workspace is { } workspace && _open.Values.Any(s => s.IsRunning && SamePath(s.Cwd, workspace.Path));

    private async Task<string?> SwitchBranchAsync(string branch)
    {
        if (Workspace is not { } workspace)
        {
            return null;
        }
        var error = await GitService.SwitchAsync(workspace.Path, branch);
        Branch = workspace.ReadBranch();
        BranchPicker.Load(Branch, workspace.ReadBranches());
        return error;
    }

    [RelayCommand]
    public void NewChat()
    {
        Push(null);
        Active = null;
        Picker.SetScope(null);
        foreach (var item in Sessions)
        {
            item.IsSelected = false;
        }
        StatusMessage = null;
        IsRunning = false;
        UpdateContextUsage();
    }

    [RelayCommand]
    private async Task OpenSessionAsync(SessionItemViewModel item)
    {
        Push(item.SessionId);
        foreach (var s in Sessions)
        {
            s.IsSelected = s == item;
        }
        try
        {
            if (item.Live is null)
            {
                var server = await _host.GetServerAsync();
                // 只取最近若干轮，更早的消息在滚动到顶部时分页加载
                var result = await server.OpenSessionAsync(item.FilePath, HistoryTurnsPerPage);
                var live = Track(new SessionViewModel(result.Session) { Title = item.CustomTitle ?? item.Title });
                live.LoadHistory(result.History, result.HistoryStart);
                live.SetRunning(result.IsRunning);
                item.Live = live;
                item.SyncFrom(live);
            }
            Activate(item.Live);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Open session failed");
            StatusMessage = Strings.Format(TCodeLangResourceKind.StatusSendFailedFormat, ex.Message);
        }
    }

    /// <summary>每页历史的轮数（每轮从一条用户消息开始）。</summary>
    private const int HistoryTurnsPerPage = 20;

    /// <summary>加载当前会话更早的一页历史（滚动到顶部时由界面调用）。返回是否插入了内容。</summary>
    public async Task<bool> LoadEarlierHistoryAsync()
    {
        if (Active is not { HasEarlierHistory: true, IsLoadingEarlier: false } session)
        {
            return false;
        }
        session.IsLoadingEarlier = true;
        try
        {
            var server = await _host.GetServerAsync();
            var page = await server.GetHistoryAsync(session.SessionId, session.HistoryStart, HistoryTurnsPerPage);
            session.PrependHistory(page.History, page.Start);
            return page.History.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Load earlier history failed");
            return false;
        }
        finally
        {
            session.IsLoadingEarlier = false;
        }
    }

    /// <summary>立即发送排队的消息：运行中插入当前轮（steer，在下一步生效），空闲时直接开始新的一轮。</summary>
    internal Task SendQueuedNowAsync(QueuedPromptViewModel queued) => SendQueuedAsync(queued, steer: queued.Session.IsRunning);

    /// <summary>上一轮被停止或出错后，继续发出队列中的消息。</summary>
    [RelayCommand]
    private Task ResumeQueueAsync() =>
        Active?.QueuedPrompts.FirstOrDefault() is { } next ? SendQueuedAsync(next, steer: Active.IsRunning) : Task.CompletedTask;

    private async Task SendQueuedAsync(QueuedPromptViewModel queued, bool steer)
    {
        var session = queued.Session;
        session.QueuedPrompts.Remove(queued);
        session.IsQueuePaused = false;
        try
        {
            var server = await _host.GetServerAsync();
            await server.PromptAsync(new PromptRequest(session.SessionId, queued.Text,
                steer ? StreamingBehaviorDto.Steer : StreamingBehaviorDto.FollowUp, ToDtos(queued.Images)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Send queued prompt failed");
            // 发送失败：放回队首并暂停，避免丢失
            session.QueuedPrompts.Insert(0, queued);
            session.IsQueuePaused = true;
            StatusMessage = Strings.Format(TCodeLangResourceKind.StatusSendFailedFormat, ex.Message);
        }
    }

    private bool CanSend() => !string.IsNullOrWhiteSpace(ComposerText) || Attachments.Count > 0;

    private static IReadOnlyList<ImageAttachmentDto>? ToDtos(IReadOnlyList<ImageAttachmentViewModel> images) =>
        images.Count == 0 ? null : images.Select(i => new ImageAttachmentDto(i.Path, i.MimeType)).ToArray();

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var images = Attachments.ToArray();
        var draft = ComposerText;
        // 占位符展开为「[Image #N: 路径]」；图片本身由 AgentHost 读取附件文件、以 base64 发送
        var text = ImagePrompt.Expand(ComposerText.Trim(), images);
        if (text.Length == 0)
        {
            return;
        }
        ComposerText = "";
        Attachments.Clear();
        _nextImageNumber = 1;
        try
        {
            var server = await _host.GetServerAsync();
            if (Active is null)
            {
                var cwd = Workspace?.Path ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var model = Picker.Selected?.Model.Ref ?? "fake/fake-sonnet";
                var session = await server.CreateSessionAsync(new CreateSessionRequest(cwd, model, Picker.Options with { PermissionMode = Permission.Mode }));
                var live = Track(new SessionViewModel(session) { Title = ImagePrompt.Collapse(text) });
                Picker.ApplyHostOptions(session.Options);
                var item = new SessionItemViewModel(session.SessionId, session.FilePath, session.Cwd) { Live = live, Owner = this };
                item.SyncFrom(live);
                Sessions.Insert(0, item);
                foreach (var s in Sessions)
                {
                    s.IsSelected = s == item;
                }
                Push(item.SessionId);
                Activate(live);
            }
            else if (Active!.IsRunning)
            {
                // 运行中再次发送：显示在输入框上方的队列里，本轮正常结束后按顺序发出（可编辑、移除或立即插入）
                Active.QueuedPrompts.Add(new QueuedPromptViewModel(text, images, Active) { Owner = this });
                StatusMessage = null;
                return;
            }
            if (Active is { IsRunning: false } idle)
            {
                await server.PromptAsync(new PromptRequest(idle.SessionId, text, Images: ToDtos(images)));
            }
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Prompt failed");
            StatusMessage = Strings.Format(TCodeLangResourceKind.StatusSendFailedFormat, ex.Message);
            // 发送失败：恢复输入框（原文与图片）
            ComposerText = draft;
            foreach (var image in images)
            {
                Attachments.Add(image);
            }
            _nextImageNumber = images.Length == 0 ? 1 : images.Max(i => i.Number) + 1;
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (Active is { } session)
        {
            var server = await _host.GetServerAsync();
            await server.AbortAsync(session.SessionId);
        }
    }

    private bool CanGoBack() => _historyIndex > 0;

    private bool CanGoForward() => _historyIndex < _history.Count - 1;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private Task GoBackAsync() => NavigateAsync(_historyIndex - 1);

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private Task GoForwardAsync() => NavigateAsync(_historyIndex + 1);

    private async Task NavigateAsync(int index)
    {
        _navigating = true;
        try
        {
            // 历史中的会话可能已不在列表中（例如 Host 重启），跳过这样的记录。
            var target = _history[index];
            var item = target is null ? null : Sessions.FirstOrDefault(s => s.SessionId == target);
            _historyIndex = index;
            if (item is null)
            {
                NewChat();
            }
            else
            {
                await OpenSessionAsync(item);
            }
        }
        finally
        {
            _navigating = false;
            GoBackCommand.NotifyCanExecuteChanged();
            GoForwardCommand.NotifyCanExecuteChanged();
        }
    }

    private void Push(string? sessionId)
    {
        if (_navigating || _history[_historyIndex] == sessionId)
        {
            return;
        }
        _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        _history.Add(sessionId);
        _historyIndex = _history.Count - 1;
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
    }

    // ---- 会话列表：分页加载 ----

    private const int SessionPageSize = 50;
    private int? _nextSessionOffset;
    private bool _loadingSessions;
    private string? _sessionFilter;

    /// <summary>还有更多会话可以加载（侧栏滚动到底部时加载下一页）。</summary>
    public bool HasMoreSessions => _nextSessionOffset is not null;

    /// <summary>侧栏筛选的项目变化：按项目重新分页加载。</summary>
    public Task SetSessionFilterAsync(string? cwd)
    {
        _sessionFilter = cwd;
        return RefreshSessionsAsync();
    }

    /// <summary>从 Host 重新加载第一页（至少覆盖已加载的数量），保留已打开会话的实时状态。</summary>
    public async Task RefreshSessionsAsync()
    {
        try
        {
            var server = await _host.GetServerAsync();
            var count = Math.Max(SessionPageSize, Sessions.Count(s => s.Live is null || !string.IsNullOrEmpty(s.Title)));
            var page = await server.QuerySessionsAsync(_sessionFilter, 0, count);
            var existing = Sessions.ToDictionary(s => s.SessionId);
            Sessions.Clear();
            foreach (var summary in page.Items)
            {
                Sessions.Add(Merge(existing.GetValueOrDefault(summary.SessionId), summary));
            }
            _nextSessionOffset = page.NextOffset;
            // 刚创建、尚未写盘的会话不在文件列表中
            foreach (var item in existing.Values.Where(i => i.Live is not null && !Sessions.Contains(i)))
            {
                Sessions.Insert(0, item);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "List sessions failed");
        }
        OnPropertyChanged(nameof(HasMoreSessions));
    }

    [RelayCommand]
    private async Task LoadMoreSessionsAsync()
    {
        if (_loadingSessions || _nextSessionOffset is not { } offset)
        {
            return;
        }
        _loadingSessions = true;
        try
        {
            var server = await _host.GetServerAsync();
            var page = await server.QuerySessionsAsync(_sessionFilter, offset, SessionPageSize);
            var known = Sessions.Select(s => s.SessionId).ToHashSet();
            foreach (var summary in page.Items.Where(s => !known.Contains(s.SessionId)))
            {
                Sessions.Add(Merge(null, summary));
            }
            _nextSessionOffset = page.NextOffset;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Load more sessions failed");
        }
        finally
        {
            _loadingSessions = false;
        }
        OnPropertyChanged(nameof(HasMoreSessions));
    }

    private SessionItemViewModel Merge(SessionItemViewModel? item, SessionSummaryDto summary)
    {
        item ??= new SessionItemViewModel(summary.SessionId, summary.FilePath, summary.Cwd) { Owner = this };
        item.Owner = this;
        if (summary.HasCustomTitle)
        {
            item.CustomTitle = summary.Title;
        }
        else
        {
            item.Title ??= summary.Title;
        }
        item.IsPinned = summary.IsPinned;
        item.NativeSessionId = summary.NativeSessionId;
        item.ModelId ??= summary.ModelId;
        item.Runtime ??= summary.Runtime;
        item.LastActivity = summary.LastModified.ToLocalTime();
        item.IsRunning = summary.IsRunning || item.Live?.IsRunning == true;
        if (item.Live is { } live)
        {
            item.SyncFrom(live);
        }
        return item;
    }

    // ---- 会话右键菜单 ----

    /// <summary>危险操作的确认框（标题、正文）；由窗口提供，测试中可替换。未设置时视为取消。</summary>
    public Func<string, string, Task<bool>>? ConfirmDangerAsync { get; set; }

    /// <summary>写入剪贴板；由窗口提供。</summary>
    public Func<string, Task>? SetClipboardAsync { get; set; }

    internal async Task RenameSessionAsync(SessionItemViewModel item, string title)
    {
        await RunSessionActionAsync(async server =>
        {
            await server.RenameSessionAsync(item.FilePath, title);
            item.CustomTitle = title.Length == 0 ? null : title;
            if (item.Live is { } live)
            {
                live.Title = item.CustomTitle ?? item.Title;
            }
            OnPropertyChanged(nameof(HeaderTitle));
        });
    }

    internal Task SetPinnedAsync(SessionItemViewModel item, bool pinned) => RunSessionActionAsync(async server =>
    {
        await server.PinSessionAsync(item.FilePath, pinned);
        item.IsPinned = pinned;
        // 置顶在前：重新排序已加载的会话
        var ordered = Sessions.OrderByDescending(s => s.IsPinned).ThenByDescending(s => s.LastActivity).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var index = Sessions.IndexOf(ordered[i]);
            if (index != i)
            {
                Sessions.Move(index, i);
            }
        }
    });

    internal async Task DeleteSessionAsync(SessionItemViewModel item)
    {
        var confirmed = ConfirmDangerAsync is { } confirm && await confirm(Strings.Get(TCodeLangResourceKind.SessionDeleteConfirmTitle),
            Strings.Format(TCodeLangResourceKind.SessionDeleteConfirmText, item.DisplayTitle));
        if (!confirmed)
        {
            return;
        }
        await RunSessionActionAsync(async server =>
        {
            // 会话文件尚未创建（新会话还没有消息）时只需关闭
            if (File.Exists(item.FilePath))
            {
                await server.DeleteSessionAsync(item.FilePath);
            }
            else
            {
                await server.CloseSessionAsync(item.SessionId);
            }
            if (Active is { } active && active.SessionId == item.SessionId)
            {
                NewChat();
            }
            _open.Remove(item.SessionId);
            Sessions.Remove(item);
        });
    }

    internal Task ForkSessionAsync(SessionItemViewModel item) => RunSessionActionAsync(async server =>
    {
        var result = await server.ForkSessionAsync(item.FilePath);
        var live = Track(new SessionViewModel(result.Session) { Title = item.CustomTitle ?? item.Title });
        live.LoadHistory(result.History, result.HistoryStart);
        var fork = new SessionItemViewModel(result.Session.SessionId, result.Session.FilePath, result.Session.Cwd)
        {
            Live = live,
            Owner = this,
            CustomTitle = item.CustomTitle,
        };
        fork.SyncFrom(live);
        Sessions.Insert(Sessions.TakeWhile(s => s.IsPinned).Count(), fork);
        foreach (var s in Sessions)
        {
            s.IsSelected = s == fork;
        }
        Push(fork.SessionId);
        Activate(live);
    });

    internal async Task CopySessionIdAsync(SessionItemViewModel item)
    {
        if (SetClipboardAsync is { } copy)
        {
            await copy(item.NativeSessionId is { } native ? $"{item.SessionId}\n{native}" : item.SessionId);
        }
    }

    /// <summary>在系统文件管理器中显示会话文件。</summary>
    internal void RevealSessionFile(SessionItemViewModel item)
    {
        try
        {
            var path = File.Exists(item.FilePath) ? item.FilePath : Path.GetDirectoryName(item.FilePath)!;
            var start = OperatingSystem.IsMacOS() ? new System.Diagnostics.ProcessStartInfo("open") { ArgumentList = { "-R", path } }
                : OperatingSystem.IsWindows() ? new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                : new System.Diagnostics.ProcessStartInfo("xdg-open") { ArgumentList = { Path.GetDirectoryName(path)! } };
            start.UseShellExecute = false;
            System.Diagnostics.Process.Start(start)?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reveal session file failed");
        }
    }

    private async Task RunSessionActionAsync(Func<IAgentHostServer, Task> action)
    {
        try
        {
            await action(await _host.GetServerAsync());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session action failed");
            StatusMessage = Strings.Format(TCodeLangResourceKind.StatusSessionActionFailedFormat, ex.Message);
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            var server = await _host.GetServerAsync();
            Picker.Load(await server.ListModelsAsync(), _userSettings.Current.Model);
            OnPropertyChanged(nameof(HeaderIconKey));
            await RefreshSessionsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start agent host");
            StatusMessage = Strings.Format(TCodeLangResourceKind.StatusHostStartFailedFormat, ex.Message);
        }
        finally
        {
            IsInitialized = true;
        }
    }

    /// <summary>首次连接 Agent Host、加载模型与会话列表已完成（成功或失败）：启动加载页据此退场。</summary>
    [ObservableProperty]
    public partial bool IsInitialized { get; private set; }

    private SessionViewModel Track(SessionViewModel session)
    {
        _open[session.SessionId] = session;
        // 本轮正常结束：发出队列中的下一条（后台会话同样继续）
        session.RunEnded += completed =>
        {
            if (completed && session.QueuedPrompts.FirstOrDefault() is { } next)
            {
                _ = SendQueuedAsync(next, steer: false);
            }
        };
        session.PendingApprovals.CollectionChanged += (_, _) =>
        {
            if (Sessions.FirstOrDefault(s => s.SessionId == session.SessionId) is { } item)
            {
                item.SyncFrom(session);
            }
        };
        session.Items.CollectionChanged += (_, _) =>
        {
            if (session == Active)
            {
                OnPropertyChanged(nameof(IsEmpty));
            }
        };
        session.PropertyChanged += (_, e) =>
        {
            if (session != Active)
            {
                return;
            }
            switch (e.PropertyName)
            {
                case nameof(SessionViewModel.IsRunning):
                    IsRunning = session.IsRunning;
                    break;
                case nameof(SessionViewModel.LastUsageTokens):
                    UpdateContextUsage();
                    break;
                case nameof(SessionViewModel.Title):
                    OnPropertyChanged(nameof(HeaderTitle));
                    break;
                case nameof(SessionViewModel.ModelId):
                    OnPropertyChanged(nameof(HeaderIconKey));
                    break;
                case nameof(SessionViewModel.PickerScope):
                    Picker.SetScope(session.PickerScope);
                    break;
                case nameof(SessionViewModel.Options) when session == Active:
                    Permission.Show(session.Options.PermissionMode);
                    break;
            }
        };
        return session;
    }

    private void Activate(SessionViewModel session)
    {
        Active = session;
        IsRunning = session.IsRunning;
        // 有历史的会话只能切换到同一运行时（或内置 Agent）的模型
        Picker.SetScope(session.PickerScope);
        Picker.ShowSession(session.ModelId, session.Options);
        StatusMessage = null;
        UpdateContextUsage();
        OnPropertyChanged(nameof(HeaderIconKey));
    }

    private void OnSessionEvent(SessionEventNotification n)
    {
        if (!_open.TryGetValue(n.SessionId, out var session))
        {
            return;
        }
        session.Apply(n.Event);
        if (Sessions.FirstOrDefault(s => s.SessionId == n.SessionId) is { } item)
        {
            item.SyncFrom(session);
            // 有新活动的会话移到列表顶部
            var index = Sessions.IndexOf(item);
            if (n.Event is MessageStarted { Role: "user" } && index > 0)
            {
                Sessions.Move(index, 0);
            }
        }
    }

    /// <summary>
    /// Host 请求审批：加入对应会话的待审批队列，在 Composer 上方显示，等待用户选择。
    /// 未在 UI 中打开的会话（理论上不会发生）直接拒绝。
    /// </summary>
    private Task<ToolApprovalResponse> HandleApprovalAsync(ToolApprovalRequest request)
    {
        if (!_open.TryGetValue(request.SessionId, out var session))
        {
            return Task.FromResult(new ToolApprovalResponse(false, "Session is not open in the UI."));
        }
        var approval = new ApprovalRequestViewModel(request);
        session.AddApproval(approval);
        if (Sessions.FirstOrDefault(s => s.SessionId == session.SessionId) is { } item)
        {
            item.SyncFrom(session);
        }
        return approval.Result;
    }

    // ---- 权限档位 ----

    /// <summary>项目记住的档位；“完全授权”从不记住，所以新会话至多是“自动放行”。</summary>
    private string RememberedPermission() =>
        Workspace?.Path is { } path && _settings.Current.PermissionModes.TryGetValue(path, out var mode)
            ? PermissionPickerViewModel.Normalize(mode)
            : PermissionPickerViewModel.Ask;

    /// <summary>
    /// 用户在选择器中选定档位：按项目记住（完全授权记为“询问”），并同步到当前会话；
    /// 放开后顺带处理已在等待的审批。失败时恢复会话的实际档位。
    /// </summary>
    private async Task ApplyPermissionAsync(string mode)
    {
        if (Workspace?.Path is { } path)
        {
            _settings.Current.PermissionModes[path] = mode == PermissionPickerViewModel.FullAccess ? PermissionPickerViewModel.Ask : mode;
            _settings.Save();
        }
        if (Active is not { } session)
        {
            return; // 空状态：创建会话时随 CreateSessionRequest 发送
        }
        try
        {
            var server = await _host.GetServerAsync();
            session.Options = await server.SetOptionsAsync(session.SessionId, new SessionOptionsDto(PermissionMode: mode));
            if (session.Options.PermissionMode != PermissionPickerViewModel.Ask)
            {
                foreach (var pending in session.PendingApprovals.ToList())
                {
                    pending.AllowOnceCommand.Execute(null);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Set permission mode failed");
            StatusMessage = Strings.Format(TCodeLangResourceKind.StatusSendFailedFormat, ex.Message);
        }
        if (session == Active)
        {
            Permission.Show(session.Options.PermissionMode);
        }
    }

    /// <summary>settings.json 变化后 Host 推送的新模型列表：刷新选择器；当前会话的模型被移除时提示。</summary>
    private void OnModelsChanged(ModelsChangedNotification n)
    {
        Picker.Load(n.Models, _userSettings.Current.Model);
        if (Active is { } session)
        {
            Picker.ShowSession(session.ModelId, session.Options);
        }
        OnPropertyChanged(nameof(HeaderIconKey));
        ModelsChanged?.Invoke(n);
    }

    /// <summary>转发 Host 的模型变化（设置页刷新厂商状态用）。</summary>
    public event Action<ModelsChangedNotification>? ModelsChanged;

    private void UpdateContextUsage()
    {
        var budget = Picker.ContextWindowValue;
        var tokens = Active?.LastUsageTokens ?? 0;
        if (tokens <= 0 || budget <= 0)
        {
            ContextUsage = 0;
            ContextUsageText = "";
            ContextUsageTip = null;
            return;
        }
        ContextUsage = Math.Clamp((double)tokens / budget, 0, 1);
        ContextUsageText = $"{Math.Round(ContextUsage * 100):0}%";
        ContextUsageTip = Strings.Format(TCodeLangResourceKind.ContextUsageFormat, tokens.ToString("N0"), budget.ToString("N0"));
    }

    /// <summary>选择器变化时同步到当前会话：换模型走 session/setModel，其余走 session/setOptions；以 Host 返回值为准。</summary>
    private async Task SyncSessionOptionsAsync()
    {
        OnPropertyChanged(nameof(HeaderIconKey));
        UpdateContextUsage();
        if (Active is not { } session || Picker.Selected is not { } selected)
        {
            return;
        }
        try
        {
            var server = await _host.GetServerAsync();
            if (session.ModelId != selected.Model.Ref)
            {
                await server.SetModelAsync(session.SessionId, selected.Model.Ref);
                session.ModelId = selected.Model.Ref;
                // 空会话可以换到其它运行时；以所选模型的运行时为准
                session.Runtime = selected.Model.Runtime;
                if (Sessions.FirstOrDefault(s => s.SessionId == session.SessionId) is { } item)
                {
                    item.SyncFrom(session);
                }
            }
            session.Options = await server.SetOptionsAsync(session.SessionId, Picker.Options);
            Picker.ApplyHostOptions(session.Options);
            UpdateContextUsage();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sync session options failed");
            // Host 拒绝（例如有历史的会话跨运行时切换）：把选择器恢复到会话当前的模型，避免反复重试
            Picker.ShowSession(session.ModelId, session.Options);
            StatusMessage = Strings.Format(TCodeLangResourceKind.StatusSendFailedFormat, ex.Message);
        }
    }

    private void RefreshLanguage()
    {
        OnPropertyChanged(nameof(WorkspaceName));
        OnPropertyChanged(nameof(BranchName));
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(HeaderSubtitle));
        foreach (var item in Sessions)
        {
            item.Refresh();
        }
        foreach (var session in _open.Values)
        {
            session.RefreshLanguage();
        }
        UpdateContextUsage();
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
