using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Threading;
using TCode.Desktop.Icons;
using TCode.Desktop.Localization;
using TCode.Rpc;

namespace TCode.Desktop.ViewModels;

/// <summary>
/// 一个 Host 会话在 UI 中的状态：转录、运行状态、等待首个输出时的“思考中”计时、上下文占用。
/// 会话切换时保留实例，后台会话的事件也会继续更新它，切回来时转录是完整的。
/// </summary>
public sealed partial class SessionViewModel : ObservableObject
{
    private readonly Dictionary<string, ToolCallBlockViewModel> _toolCalls = [];
    private readonly DispatcherTimer _ponderTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeOffset _ponderStart;

    public SessionViewModel(SessionDto session)
    {
        QueuedPrompts.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasQueuedPrompts));
            OnPropertyChanged(nameof(QueueTitle));
        };
        SessionId = session.SessionId;
        FilePath = session.FilePath;
        Cwd = session.Cwd;
        ModelId = session.ModelId;
        Options = session.Options;
        Runtime = session.Runtime;
        _ponderTimer.Tick += (_, _) => UpdatePonderText();
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(PickerScope));
        };
        PendingApprovals.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CurrentApproval));
            OnPropertyChanged(nameof(HasPendingApproval));
            OnPropertyChanged(nameof(HasMoreApprovals));
            OnPropertyChanged(nameof(MoreApprovalsText));
            Changed?.Invoke();
        };
    }

    public string SessionId { get; }

    /// <summary>
    /// 外部运行时 id（claude-code / codex / acp:*）；null 表示内置 Agent。
    /// 没有消息的会话可以在运行时之间切换（Host 替换底层循环），因此可变。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExternal))]
    public partial string? Runtime { get; set; }

    public bool IsExternal => Runtime is not null;

    /// <summary>模型选择器范围：没有历史时可选全部模型，有历史后只能选同一运行时（内置为 ""）。</summary>
    public string? PickerScope => IsEmpty ? null : Runtime ?? "";
    public string FilePath { get; }
    public string Cwd { get; }
    public string ProjectName => System.IO.Path.GetFileName(Cwd.TrimEnd(System.IO.Path.DirectorySeparatorChar));
    public ObservableCollection<TranscriptItemViewModel> Items { get; } = [];

    /// <summary>等待用户决定的工具审批（按到达顺序）。</summary>
    public ObservableCollection<ApprovalRequestViewModel> PendingApprovals { get; } = [];

    public ApprovalRequestViewModel? CurrentApproval => PendingApprovals.FirstOrDefault();
    public bool HasPendingApproval => PendingApprovals.Count > 0;
    public bool HasMoreApprovals => PendingApprovals.Count > 1;
    public string MoreApprovalsText => Strings.Format(TCodeLangResourceKind.ApprovalPendingFormat, PendingApprovals.Count - 1);

    public void AddApproval(ApprovalRequestViewModel approval)
    {
        approval.Decided += a => PendingApprovals.Remove(a);
        PendingApprovals.Add(approval);
    }
    public bool IsEmpty => Items.Count == 0;

    /// <summary>任务清单（task_* 工具的最新快照）。</summary>
    public TaskListViewModel TaskList { get; } = new();

    /// <summary>工具结果带任务清单快照时刷新输入框上方的清单。</summary>
    private void ApplyDetails(ToolCallBlockViewModel call, string? detailsJson)
    {
        if (detailsJson is null)
        {
            return;
        }
        call.DetailsJson = detailsJson;
        if (call.Parsed is { Kind: "tasks", Tasks: { } tasks })
        {
            TaskList.Update(tasks);
        }
    }

    /// <summary>运行中或未结束的事件到达时更新。</summary>
    public event Action? Changed;

    [ObservableProperty]
    public partial string? Title { get; set; }

    [ObservableProperty]
    public partial string ModelId { get; set; }

    [ObservableProperty]
    public partial SessionOptionsDto Options { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    /// <summary>已开始运行、但本轮还没有收到任何输出（等待模型首个 token 或工具后的下一次请求）。</summary>
    [ObservableProperty]
    public partial bool IsPondering { get; private set; }

    [ObservableProperty]
    public partial string PonderText { get; private set; } = "";

    [ObservableProperty]
    public partial DateTimeOffset LastActivity { get; set; } = DateTimeOffset.Now;

    /// <summary>最近一次助手回复的 输入 + 缓存 + 输出 tokens，用于计算上下文占用。</summary>
    [ObservableProperty]
    public partial long LastUsageTokens { get; private set; }

    public string? ModelIconKey => ModelIcons.ForModel(ModelId);

    partial void OnModelIdChanged(string value) => OnPropertyChanged(nameof(ModelIconKey));

    /// <summary>载入最近的历史（<paramref name="start"/> 为其在完整历史中的位置，大于 0 表示还有更早的消息）。</summary>
    public void LoadHistory(IReadOnlyList<HistoryMessageDto> history, int start = 0)
    {
        Items.Clear();
        _toolCalls.Clear();
        foreach (var item in BuildItems(history, latest: true))
        {
            Items.Add(item);
        }
        HistoryStart = start;
    }

    /// <summary>在顶部插入更早的一页历史。</summary>
    public void PrependHistory(IReadOnlyList<HistoryMessageDto> history, int start)
    {
        var items = BuildItems(history, latest: false);
        for (var i = 0; i < items.Count; i++)
        {
            Items.Insert(i, items[i]);
        }
        HistoryStart = start;
    }

    private int _historyKey;

    /// <summary>已加载历史中第一条消息在完整历史中的位置；大于 0 表示还有更早的消息。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEarlierHistory))]
    public partial int HistoryStart { get; private set; }

    public bool HasEarlierHistory => HistoryStart > 0;

    // ---- 运行中发送的消息队列 ----

    private string? _lastStopReason;

    /// <summary>Agent 运行中发送的消息：本轮正常结束后按顺序发出。</summary>
    public System.Collections.ObjectModel.ObservableCollection<QueuedPromptViewModel> QueuedPrompts { get; } = [];

    public bool HasQueuedPrompts => QueuedPrompts.Count > 0;

    public string QueueTitle => Strings.Format(TCodeLangResourceKind.QueueTitleFormat, QueuedPrompts.Count);

    /// <summary>上一轮被停止或出错，队列暂停（需要用户点“继续”）。</summary>
    [ObservableProperty]
    public partial bool IsQueuePaused { get; set; }

    /// <summary>一轮结束；参数表示是否正常完成（未被停止、未出错）。</summary>
    public event Action<bool>? RunEnded;

    /// <summary>会话顶部的“更早的消息”条目（有更早的历史时显示）。</summary>
    public EarlierHistoryViewModel EarlierHistory => _earlier ??= new EarlierHistoryViewModel(this);

    private EarlierHistoryViewModel? _earlier;

    /// <summary>正在加载更早的历史（顶部显示加载提示）。</summary>
    [ObservableProperty]
    public partial bool IsLoadingEarlier { get; set; }

    /// <summary>由历史消息构建会话条目（同一轮的多条助手消息合并为一条）。</summary>
    private List<TranscriptItemViewModel> BuildItems(IReadOnlyList<HistoryMessageDto> history, bool latest)
    {
        var items = new List<TranscriptItemViewModel>();
        AssistantMessageItemViewModel? current = null;
        foreach (var message in history)
        {
            switch (message.Role)
            {
                case "user":
                    current = null;
                    var text = string.Concat(message.Blocks.OfType<HistoryText>().Select(t => t.Text));
                    if (message.Source is { } source)
                    {
                        items.Add(new NotificationItemViewModel(source, text));
                        break;
                    }
                    if (latest)
                    {
                        Title ??= ImagePrompt.Collapse(text);
                    }
                    var images = message.Blocks.OfType<HistoryImage>().Select(i => i.Path).ToArray();
                    items.Add(new UserMessageItemViewModel(text, images));
                    break;
                case "assistant":
                    // 同一轮的多条助手消息（工具调用前后）合并显示为一条。
                    if (current is null)
                    {
                        current = new AssistantMessageItemViewModel($"history-{_historyKey++}", message.Timestamp);
                        items.Add(current);
                    }
                    foreach (var block in message.Blocks)
                    {
                        var key = current.Blocks.Count;
                        switch (block)
                        {
                            case HistoryText t:
                                current.GetOrAdd(key, () => TextBlockViewModel.FromHistory(t.Text));
                                break;
                            case HistoryThinking t when t.Text.Length > 0:
                                current.GetOrAdd(key, () => new ThinkingBlockViewModel { Text = t.Text });
                                break;
                            case HistoryToolCall c:
                                _toolCalls[c.ToolCallId] = current.GetOrAdd(key, () => new ToolCallBlockViewModel(c.ToolCallId, c.ToolName)
                                {
                                    Arguments = c.ArgumentsJson,
                                    Status = ToolCallStatus.Running,
                                });
                                break;
                        }
                    }
                    if (message.StopReason is "Error" or "Aborted")
                    {
                        current.ErrorMessage = ErrorText(message.StopReason, message.ErrorMessage);
                    }
                    if (latest && message.Usage is { } usage)
                    {
                        LastUsageTokens = usage.Input + usage.CacheRead + usage.CacheWrite + usage.Output;
                    }
                    current.Complete();
                    break;
                case "toolResult":
                    foreach (var result in message.Blocks.OfType<HistoryToolResult>())
                    {
                        if (_toolCalls.TryGetValue(result.ToolCallId, out var call))
                        {
                            call.Status = result.IsError ? ToolCallStatus.Failed : ToolCallStatus.Succeeded;
                            call.Result = result.Text.TrimEnd();
                            call.IsStreaming = false;
                            ApplyDetails(call, result.DetailsJson);
                        }
                    }
                    break;
            }
        }
        if (latest && history.Count > 0)
        {
            LastActivity = history[^1].Timestamp.ToLocalTime();
        }
        return items;
    }

    public void SetRunning(bool running)
    {
        IsRunning = running;
        SetPondering(running);
    }

    public void Apply(SessionEvent e)
    {
        LastActivity = DateTimeOffset.Now;
        switch (e)
        {
            case AgentStarted:
                SetRunning(true);
                _lastStopReason = null;
                IsQueuePaused = false;
                break;
            case AgentEnded:
                SetRunning(false);
                // 本轮被停止或出错：排队的消息暂停，不自动发出
                var completed = _lastStopReason is not ("Error" or "Aborted");
                IsQueuePaused = !completed && QueuedPrompts.Count > 0;
                RunEnded?.Invoke(completed);
                break;
            case MessageStarted { Role: "user", Source: { } source } m:
                Items.Add(new NotificationItemViewModel(source, m.Text ?? ""));
                break;
            case MessageStarted { Role: "user" } m:
                Title ??= m.Text is null ? null : ImagePrompt.Collapse(m.Text);
                Items.Add(new UserMessageItemViewModel(m.Text ?? "", m.Images));
                break;
            case MessageStarted { Role: "assistant" } m:
                // 同一轮内工具调用后的后续回复接在同一条消息里显示。
                if (Items.LastOrDefault() is not AssistantMessageItemViewModel)
                {
                    Items.Add(new AssistantMessageItemViewModel(m.MessageId, DateTimeOffset.Now));
                }
                _currentMessageId = m.MessageId;
                _blockOffset = Current?.Blocks.Count ?? 0;
                if (Current is { } existing)
                {
                    existing.IsStreaming = true;
                }
                break;
            case TextDelta d when Current is { } msg:
                SetPondering(false);
                msg.GetOrAdd(_blockOffset + d.ContentIndex, () => new TextBlockViewModel(live: true)).Append(d.Delta);
                break;
            case ThinkingDelta d when Current is { } msg:
                SetPondering(false);
                msg.GetOrAdd(_blockOffset + d.ContentIndex, () => new ThinkingBlockViewModel()).Append(d.Delta);
                break;
            case ToolCallStarted s when Current is { } msg:
                SetPondering(false);
                _toolCalls[s.ToolCallId] = msg.GetOrAdd(_blockOffset + s.ContentIndex, () => new ToolCallBlockViewModel(s.ToolCallId, s.ToolName));
                break;
            case ToolCallDelta d when Current is { } msg:
                msg.GetOrAdd(_blockOffset + d.ContentIndex, () => new ToolCallBlockViewModel("", "tool")).AppendArguments(d.JsonFragment);
                break;
            case ToolCallEnded e2 when _toolCalls.TryGetValue(e2.ToolCallId, out var call):
                call.Arguments = e2.ArgumentsJson;
                call.IsStreaming = false;
                break;
            case MessageEnded { Role: "assistant" } end when Current is { } msg:
                _lastStopReason = end.StopReason;
                msg.Complete();
                if (end.StopReason is "Error" or "Aborted")
                {
                    msg.ErrorMessage = ErrorText(end.StopReason, end.ErrorMessage);
                }
                if (end.Usage is { } usage)
                {
                    LastUsageTokens = usage.Input + usage.CacheRead + usage.CacheWrite + usage.Output;
                }
                break;
            case ToolExecutionStarted s when _toolCalls.TryGetValue(s.ToolCallId, out var call):
                call.Status = ToolCallStatus.Running;
                break;
            case ToolExecutionUpdated update when _toolCalls.TryGetValue(update.ToolCallId, out var call):
                // 命令的实时输出、子 Agent 的当前动作
                if (update.Text.Length > 0)
                {
                    call.Result = update.Text.TrimEnd();
                }
                ApplyDetails(call, update.DetailsJson);
                break;
            case ToolExecutionEnded end when _toolCalls.TryGetValue(end.ToolCallId, out var call):
                call.Status = end.IsError ? ToolCallStatus.Failed : ToolCallStatus.Succeeded;
                call.Result = end.ResultText.TrimEnd();
                ApplyDetails(call, end.DetailsJson);
                call.Duration = end.DurationMs < 1000 ? $"{end.DurationMs:0} ms" : $"{end.DurationMs / 1000:0.0} s";
                // 工具执行完后到下一次模型输出之间同样处于“思考中”。
                SetPondering(IsRunning);
                break;
        }
        Changed?.Invoke();
    }

    public void RefreshLanguage()
    {
        UpdatePonderText();
        foreach (var item in Items.OfType<AssistantMessageItemViewModel>())
        {
            item.RefreshLanguage();
        }
        foreach (var item in Items.OfType<NotificationItemViewModel>())
        {
            item.RefreshLanguage();
        }
        TaskList.RefreshLanguage();
    }

    private string? _currentMessageId;
    private int _blockOffset;

    private AssistantMessageItemViewModel? Current =>
        _currentMessageId is null ? null : Items.LastOrDefault() as AssistantMessageItemViewModel;

    private void SetPondering(bool pondering)
    {
        if (IsPondering == pondering)
        {
            return;
        }
        IsPondering = pondering;
        if (pondering)
        {
            _ponderStart = DateTimeOffset.Now;
            PonderVerb = PickPonderVerb();
            UpdatePonderText();
            _ponderTimer.Start();
        }
        else
        {
            _ponderTimer.Stop();
        }
    }

    /// <summary>等待输出时的俏皮动词（“琢磨中…”），每次进入等待随机换一个。</summary>
    [ObservableProperty]
    public partial string PonderVerb { get; private set; } = "";

    private static string PickPonderVerb()
    {
        var verbs = Strings.Get(TCodeLangResourceKind.PonderVerbs).Split('|', StringSplitOptions.RemoveEmptyEntries);
        return verbs.Length == 0 ? "…" : verbs[Random.Shared.Next(verbs.Length)];
    }

    private void UpdatePonderText()
    {
        var seconds = Math.Max(1, (int)(DateTimeOffset.Now - _ponderStart).TotalSeconds);
        PonderText = $"{seconds}s";
    }

    private static string ErrorText(string stopReason, string? message) =>
        stopReason == "Aborted" ? Strings.Get(TCodeLangResourceKind.Aborted) : message ?? Strings.Get(TCodeLangResourceKind.RequestFailed);
}
