using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StreamMark.Avalonia;
using TCode.Desktop.Localization;

namespace TCode.Desktop.ViewModels;

public abstract class TranscriptItemViewModel : ObservableObject;

public sealed class UserMessageItemViewModel(string text, IReadOnlyList<string>? images = null) : TranscriptItemViewModel
{
    /// <summary>发送的原文（含「[Image #N: 路径]」引用）。</summary>
    public string Text { get; } = text;

    /// <summary>显示用：图片引用折叠为「[图片 #N]」。</summary>
    public string DisplayText { get; } = ImagePrompt.Collapse(text);

    /// <summary>附图（显示在气泡上方，点击全屏查看）。</summary>
    public IReadOnlyList<ImageAttachmentViewModel> Images { get; } =
        images is null ? [] : images.Select((path, i) => new ImageAttachmentViewModel(path, "", i + 1, thumbnailWidth: 480)).ToArray();

    public bool HasImages => Images.Count > 0;
}

/// <summary>一条助手消息，由按 ContentIndex 排列的块组成（思考 / 文本 / 工具调用）。</summary>
public sealed partial class AssistantMessageItemViewModel(string messageId, DateTimeOffset timestamp) : TranscriptItemViewModel
{
    private readonly Dictionary<int, AssistantBlockViewModel> _byIndex = [];

    public string MessageId { get; } = messageId;
    public DateTimeOffset Timestamp { get; } = timestamp;
    public ObservableCollection<AssistantBlockViewModel> Blocks { get; } = [];

    /// <summary>消息底部的时间，例如 “Oct 16, 10:22 AM”，格式随界面语言。</summary>
    public string TimestampText => Timestamp.ToLocalTime().ToString("g", CultureInfo.CurrentUICulture);

    /// <summary>只在回复结束且有正文时显示时间与复制按钮。</summary>
    public bool ShowFooter => !IsStreaming && Blocks.OfType<TextBlockViewModel>().Any(t => t.Text.Length > 0);

    [ObservableProperty]
    public partial bool IsStreaming { get; set; } = true;

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => ErrorMessage is not null;

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnIsStreamingChanged(bool value) => OnPropertyChanged(nameof(ShowFooter));

    public T GetOrAdd<T>(int index, Func<T> factory) where T : AssistantBlockViewModel
    {
        if (_byIndex.TryGetValue(index, out var block) && block is T typed)
        {
            return typed;
        }
        var created = factory();
        _byIndex[index] = created;
        Blocks.Add(created);
        return created;
    }

    public void Complete()
    {
        IsStreaming = false;
        foreach (var block in Blocks)
        {
            block.IsStreaming = false;
        }
        OnPropertyChanged(nameof(ShowFooter));
    }

    /// <summary>复制按钮的内容：全部正文块。</summary>
    public string CopyText => string.Join("\n\n", Blocks.OfType<TextBlockViewModel>().Select(t => t.Text));

    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(TimestampText));
        foreach (var block in Blocks)
        {
            block.RefreshLanguage();
        }
    }
}

public abstract partial class AssistantBlockViewModel : ObservableObject
{
    /// <summary>把高频属性通知合并到下一次界面空闲时发出（每帧至多一次）。</summary>
    protected void NotifyLater(ref bool pending, string propertyName)
    {
        if (pending)
        {
            return;
        }
        pending = true;
        _pendingNotifications.Add(propertyName);
        if (_pendingNotifications.Count == 1)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(FlushNotifications, Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    private readonly List<string> _pendingNotifications = [];

    private void FlushNotifications()
    {
        var names = _pendingNotifications.ToArray();
        _pendingNotifications.Clear();
        ResetPending();
        foreach (var name in names)
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>派生类在通知发出前清除各自的“待通知”标记。</summary>
    protected virtual void ResetPending()
    {
    }

    [ObservableProperty]
    public partial bool IsStreaming { get; set; } = true;

    partial void OnIsStreamingChanged(bool value) => OnStreamingChanged();

    protected virtual void OnStreamingChanged()
    {
    }

    public virtual void RefreshLanguage()
    {
    }
}

/// <summary>
/// 正文块。实时回复使用 StreamMark 的流式文档 <see cref="Stream"/>（经节奏控制器平滑输出，带渐显与光标），
/// <see cref="Text"/> 的追加会自动送入流中；历史消息（<see cref="FromHistory"/>）不创建流，
/// 由会话视图按需解析（行第一次进入视口时）。
/// </summary>
public sealed partial class TextBlockViewModel : AssistantBlockViewModel
{
    public TextBlockViewModel(bool live = false)
    {
        Stream = new MarkdownStream(smooth: live);
    }

    private TextBlockViewModel(string historyText)
    {
        _buffer.Append(historyText);
        IsStreaming = false;
    }

    /// <summary>历史消息的正文：只保存文本，滚动到可见时才解析 Markdown。</summary>
    public static TextBlockViewModel FromHistory(string text) => new(text);

    /// <summary>流式文档；历史正文为 null（按需解析）。</summary>
    public MarkdownStream? Stream { get; }

    private readonly System.Text.StringBuilder _buffer = new();
    private string? _text;
    private bool _textNotifyPending;

    /// <summary>
    /// 全文。流式增量用 <see cref="Append"/>（直接送入流式文档、追加到缓冲区），全文在读取时才生成，
    /// 属性通知合并到每帧一次，避免长回复时每个增量都复制整段文本。
    /// </summary>
    public string Text
    {
        get => _text ??= _buffer.ToString();
        set
        {
            var old = Text;
            if (value == old)
            {
                return;
            }
            if (Stream is not null)
            {
                if (value.Length > old.Length && value.StartsWith(old, StringComparison.Ordinal))
                {
                    Stream.Push(value[old.Length..]);
                }
                else
                {
                    // 非追加的改写（极少见）：整段重置
                    Stream.Document.Reset(value);
                }
            }
            _buffer.Clear().Append(value);
            _text = value;
            OnPropertyChanged();
        }
    }

    protected override void ResetPending() => _textNotifyPending = false;

    public void Append(string delta)
    {
        if (delta.Length == 0)
        {
            return;
        }
        _buffer.Append(delta);
        _text = null;
        Stream?.Push(delta);
        NotifyLater(ref _textNotifyPending, nameof(Text));
    }

    protected override void OnStreamingChanged()
    {
        if (!IsStreaming)
        {
            Stream?.Complete();
        }
    }
}

/// <summary>思考过程：默认折叠，标题在流式阶段显示“正在思考…”，展开后在左侧引导线旁显示思考内容。</summary>
public sealed partial class ThinkingBlockViewModel : AssistantBlockViewModel
{
    private readonly System.Text.StringBuilder _buffer = new();
    private string? _text;
    private bool _textNotifyPending;

    /// <summary>思考全文（增量用 <see cref="Append"/>；展开时的文本刷新合并到每帧一次）。</summary>
    public string Text
    {
        get => _text ??= _buffer.ToString();
        set
        {
            _buffer.Clear().Append(value);
            _text = value;
            OnPropertyChanged();
        }
    }

    protected override void ResetPending() => _textNotifyPending = false;

    public void Append(string delta)
    {
        _buffer.Append(delta);
        _text = null;
        NotifyLater(ref _textNotifyPending, nameof(Text));
    }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public string Header => Strings.Get(IsStreaming ? TCodeLangResourceKind.Thinking : TCodeLangResourceKind.ThoughtProcess);

    protected override void OnStreamingChanged() => OnPropertyChanged(nameof(Header));

    public override void RefreshLanguage() => OnPropertyChanged(nameof(Header));

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

public enum ToolCallStatus
{
    Streaming,
    Running,
    Succeeded,
    Failed,
}

public sealed partial class ToolCallBlockViewModel(string toolCallId, string toolName) : AssistantBlockViewModel
{
    public string ToolCallId { get; } = toolCallId;
    public string ToolName { get; } = toolName;

    /// <summary>类别：决定图标、动词与活动组摘要中的计数项。</summary>
    public ToolCategory Category { get; } = ToolPresentation.Classify(toolName);

    public Icons.LucideKind Icon => ToolPresentation.IconFor(Category);

    public string Verb => ToolPresentation.VerbFor(Category, ToolName);

    /// <summary>
    /// 步骤行的单行文本：主参数（命令、路径…）。参数流式到达时只扫描开头（主参数通常在最前），
    /// 参数完整后才做一次完整解析——写大文件等长参数不会因每个片段都重新解析而变慢。
    /// </summary>
    public string Summary
    {
        get
        {
            if (IsStreaming)
            {
                return _partialSummary ?? ToolPresentation.SingleLine(Head(Arguments, 160));
            }
            return _summary ??= ToolPresentation.SingleLine(PrimaryArgument ?? Arguments);
        }
    }

    /// <summary>展开后的完整参数：主参数原文，取不到时显示完整 JSON。</summary>
    public string Detail => IsStreaming ? Arguments : _detail ??= PrimaryArgument ?? Arguments;

    private string? PrimaryArgument => ToolPresentation.PrimaryArgument(Category, Arguments);

    private readonly System.Text.StringBuilder _args = new();
    private string? _argsText;
    private string? _summary;
    private string? _detail;
    private string? _partialSummary;
    private bool _argsNotifyPending;

    /// <summary>完整（或截至目前）的参数 JSON。</summary>
    public string Arguments
    {
        get => _argsText ??= _args.ToString();
        set
        {
            _args.Clear().Append(value);
            _argsText = value;
            _summary = _detail = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(Detail));
        }
    }

    /// <summary>追加流式参数片段：只在主参数仍未确定时扫描开头，通知合并到每帧一次。</summary>
    public void AppendArguments(string fragment)
    {
        _args.Append(fragment);
        _argsText = null;
        // 主参数一旦提取到就固定；开头扫描范围内没有时也不再扫描
        if (_partialSummary is null && _args.Length - fragment.Length <= ToolPresentation.PartialScanLength)
        {
            var partial = ToolPresentation.PartialPrimaryArgument(Category, _args);
            _partialSummary = partial is null ? null : ToolPresentation.SingleLine(partial);
        }
        NotifyLater(ref _argsNotifyPending, nameof(Summary));
    }

    protected override void ResetPending() => _argsNotifyPending = false;

    protected override void OnStreamingChanged()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Detail));
    }

    private static string Head(string text, int length) => text.Length <= length ? text : text[..length];

    public bool HasResult => !string.IsNullOrEmpty(Result);

    /// <summary>工具给 UI 的结构化数据（diff、子 Agent 进度、任务清单…），见 <see cref="ParsedToolDetails"/>。</summary>
    [ObservableProperty]
    public partial string? DetailsJson { get; set; }

    private ParsedToolDetails? _parsed;

    public ParsedToolDetails? Parsed => _parsed;

    partial void OnDetailsJsonChanged(string? value)
    {
        _parsed = ParsedToolDetails.Parse(value);
        OnPropertyChanged(nameof(Parsed));
        OnPropertyChanged(nameof(HasDiff));
        OnPropertyChanged(nameof(DiffLines));
        OnPropertyChanged(nameof(DiffStats));
        OnPropertyChanged(nameof(HasSubagent));
        OnPropertyChanged(nameof(SubagentStats));
        OnPropertyChanged(nameof(ShowResult));
    }

    public bool HasDiff => _parsed is { Kind: "diff", DiffLines.Count: > 0 };

    public IReadOnlyList<DiffLineViewModel> DiffLines => _parsed?.DiffLines ?? [];

    /// <summary>“+3 −1”。</summary>
    public string DiffStats => _parsed is { Kind: "diff" } d ? $"+{d.Added} −{d.Removed}" : "";

    public bool HasSubagent => _parsed is { Kind: "subagent" };

    /// <summary>子 Agent 的进度：“reviewer · 12 次工具调用 · 34.5k tokens”。</summary>
    public string SubagentStats => _parsed is { Kind: "subagent" } a
        ? Strings.Format(TCodeLangResourceKind.SubagentStatsFormat, a.SubagentName ?? "", a.ToolUses, FormatTokens(a.Tokens))
        : "";

    /// <summary>有 diff 时结果文本只是一句“已替换…”，不再单独显示。</summary>
    public bool ShowResult => HasResult && !HasDiff;

    private static string FormatTokens(long tokens) => tokens >= 1000 ? $"{tokens / 1000.0:0.#}k" : tokens.ToString(CultureInfo.InvariantCulture);



    [ObservableProperty]
    public partial ToolCallStatus Status { get; set; } = ToolCallStatus.Streaming;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    [NotifyPropertyChangedFor(nameof(ShowResult))]
    public partial string? Result { get; set; }

    [ObservableProperty]
    public partial string? Duration { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public bool IsBusy => Status is ToolCallStatus.Streaming or ToolCallStatus.Running;
    public bool IsFailed => Status == ToolCallStatus.Failed;
    public bool IsSucceeded => Status == ToolCallStatus.Succeeded;

    public string StatusText => Strings.Get(Status switch
    {
        ToolCallStatus.Streaming => TCodeLangResourceKind.ToolPreparing,
        ToolCallStatus.Running => TCodeLangResourceKind.ToolRunning,
        ToolCallStatus.Succeeded => TCodeLangResourceKind.ToolDone,
        _ => TCodeLangResourceKind.ToolFailed,
    });

    partial void OnStatusChanged(ToolCallStatus value)
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsSucceeded));
        OnPropertyChanged(nameof(StatusText));
    }

    public override void RefreshLanguage()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(Verb));
        OnPropertyChanged(nameof(SubagentStats));
    }

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/// <summary>
/// 活动组：两段正文之间连续的思考与工具调用（可跨多次模型请求），显示为一行可折叠的摘要
/// （“思考 2 次 · 运行 4 条命令”），展开后是左侧引导线串起的步骤行。
/// 进行中的组默认展开，结束后自动折叠；用户手动展开 / 折叠后不再自动改变。
/// </summary>
public sealed partial class ActivityGroupViewModel : TranscriptItemViewModel
{
    private bool _userToggled;

    public ObservableCollection<AssistantBlockViewModel> Steps { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>组仍在进行（其后还没有正文，且会话在运行）。</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    partial void OnIsActiveChanged(bool value)
    {
        if (!_userToggled)
        {
            IsExpanded = value;
        }
    }

    /// <summary>按顺序同步步骤（通常只是追加），并刷新摘要。</summary>
    public void SetSteps(IReadOnlyList<AssistantBlockViewModel> steps)
    {
        var common = 0;
        while (common < steps.Count && common < Steps.Count && ReferenceEquals(steps[common], Steps[common]))
        {
            common++;
        }
        while (Steps.Count > common)
        {
            Steps.RemoveAt(Steps.Count - 1);
        }
        for (var i = common; i < steps.Count; i++)
        {
            Steps.Add(steps[i]);
        }
        RefreshSummary();
    }

    public void RefreshSummary() => Summary = ToolPresentation.Summary(
        Steps.OfType<ThinkingBlockViewModel>().Count(),
        Steps.OfType<ToolCallBlockViewModel>().Select(t => t.Category));

    [RelayCommand]
    private void Toggle()
    {
        _userToggled = true;
        IsExpanded = !IsExpanded;
    }
}

/// <summary>会话顶部的提示：还有更早的消息，滚动到顶部时自动加载。</summary>
public sealed class EarlierHistoryViewModel(SessionViewModel session) : TranscriptItemViewModel
{
    public SessionViewModel Session { get; } = session;
}
