using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKCode.Desktop.Localization;
using TKCode.Rpc;

namespace TKCode.Desktop.ViewModels;

/// <summary>审批面板里的一行：一种批准方式，或最后的“继续完善”（<see cref="IsRevise"/>）。</summary>
public sealed partial class PlanReviewOptionViewModel(PlanReviewViewModel owner, int index, string? choice) : ObservableObject
{
    public PlanReviewViewModel Owner { get; } = owner;
    public int Index { get; } = index;

    /// <summary>批准方式（clear / auto / ask / acp）；“继续完善”为 null。</summary>
    public string? Choice { get; } = choice;

    public bool IsRevise => Choice is null;

    /// <summary>行首的序号（1–9）。</summary>
    public string Number => Index < 9 ? (Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : "";

    public string Label => Owner.LabelFor(Choice);

    /// <summary>标签后面的补充（清空上下文：规划用掉的上下文比例）。</summary>
    public string? Suffix => Owner.SuffixFor(Choice);

    public bool HasSuffix => !string.IsNullOrEmpty(Suffix);

    [ObservableProperty]
    public partial bool IsHighlighted { get; set; }

    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Suffix));
    }
}

/// <summary>
/// 一次计划审批（Host 的 plan/review，docs/design/plan-mode.md §13.1、§14.1）：审批面板替换输入框，
/// 渲染计划、可在面板里编辑或用外部编辑器修改，按请求给出的顺序列出批准方式，最后一行是“继续完善”（附反馈）。
/// 确认、暂不批准（Esc）或被 Host 撤回（<see cref="Cancel"/>）后从队列移除。
/// </summary>
public sealed partial class PlanReviewViewModel : ObservableObject
{
    private readonly TaskCompletionSource<PlanReviewResponse> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<string>? _openFile;
    private readonly int? _contextPercent;

    /// <summary>计划文件最近一次读到的内容（外部编辑器改动后据此判断是否变化）。</summary>
    private string _fileSnapshot;

    /// <param name="sourceName">运行时的显示名（Claude Agent）；内置 Agent 为 null。</param>
    /// <param name="contextPercent">规划用掉的上下文比例（0–100）；不知道时为 null，选项上不显示。</param>
    /// <param name="openFile">用系统默认程序打开计划文件。</param>
    public PlanReviewViewModel(PlanReviewRequest request, string? sourceName = null, int? contextPercent = null, Action<string>? openFile = null)
    {
        Request = request;
        SourceName = sourceName;
        _contextPercent = contextPercent;
        _openFile = openFile;
        OriginalPlan = Normalize(request.Plan);
        _fileSnapshot = OriginalPlan;
        Plan = OriginalPlan;
        EditText = OriginalPlan;
        var choices = request.Choices.Where(c => c is "clear" or "auto" or "ask" or "acp").Distinct().ToList();
        Options = [.. choices.Select((c, i) => new PlanReviewOptionViewModel(this, i, c)), new PlanReviewOptionViewModel(this, choices.Count, null)];
        Highlight = 0;
        SyncHighlight();
    }

    public PlanReviewRequest Request { get; }
    public Task<PlanReviewResponse> Result => _result.Task;

    /// <summary>运行时的显示名；内置 Agent 为 null。</summary>
    public string? SourceName { get; }

    public event Action<PlanReviewViewModel>? Decided;

    public string Title => SourceName is { Length: > 0 } name
        ? Strings.Format(TKCodeLangResourceKind.PlanReviewTitleFormat, name)
        : Strings.Get(TKCodeLangResourceKind.PlanReviewTitle);

    public string FileName => Path.GetFileName(Request.PlanPath);

    public string PlanPath => Request.PlanPath;

    /// <summary>Host 发来的计划（审批时展示的版本）。</summary>
    public string OriginalPlan { get; }

    /// <summary>当前显示的计划：编辑或外部编辑器修改后的版本。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEdited), nameof(ShowEditedTag))]
    public partial string Plan { get; private set; }

    /// <summary>编辑框里的 Markdown 源码（编辑结束时写回 <see cref="Plan"/>）。</summary>
    [ObservableProperty]
    public partial string EditText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditButtonText), nameof(ShowEditedTag))]
    public partial bool IsEditing { get; private set; }

    /// <summary>计划被改过（面板里编辑或外部编辑器）：批准时一并返回。</summary>
    public bool IsEdited => !string.Equals(Plan, OriginalPlan, StringComparison.Ordinal);

    public bool ShowEditedTag => IsEdited && !IsEditing;

    public string EditButtonText => Strings.Get(IsEditing ? TKCodeLangResourceKind.PlanReviewEditDone : TKCodeLangResourceKind.PlanReviewEdit);

    /// <summary>选项：请求给出的批准方式，最后一行是“继续完善”。</summary>
    public IReadOnlyList<PlanReviewOptionViewModel> Options { get; }

    public IEnumerable<PlanReviewOptionViewModel> ApproveOptions => Options.Where(o => !o.IsRevise);

    public PlanReviewOptionViewModel ReviseOption => Options[^1];

    /// <summary>当前选中的行（0..Options.Count-1；最后一行是“继续完善”）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReviseHighlighted), nameof(Selected))]
    public partial int Highlight { get; set; }

    public PlanReviewOptionViewModel Selected => Options[Math.Clamp(Highlight, 0, Options.Count - 1)];

    public bool IsReviseHighlighted => Selected.IsRevise;

    [ObservableProperty]
    public partial string Feedback { get; set; } = "";

    public string ReviseNumber => ReviseOption.Number;

    public string FeedbackPlaceholder => Strings.Get(TKCodeLangResourceKind.PlanReviewFeedbackPlaceholder);

    public string HintsText => Strings.Format(TKCodeLangResourceKind.PlanReviewHintsFormat, Math.Min(Options.Count, 9));

    /// <summary>选“继续完善”时编辑过的计划会被丢弃：第一次确认先给出提示（§14.1）。</summary>
    [ObservableProperty]
    public partial bool ShowDiscardWarning { get; private set; }

    /// <summary>同一会话里排在后面的审批数（标题旁的 +N）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMore), nameof(MoreText))]
    public partial int MoreCount { get; set; }

    public bool HasMore => MoreCount > 0;
    public string MoreText => "+" + MoreCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public bool IsDecided => _result.Task.IsCompleted;

    partial void OnHighlightChanged(int value)
    {
        SyncHighlight();
        if (!IsReviseHighlighted)
        {
            ShowDiscardWarning = false;
        }
    }

    partial void OnFeedbackChanged(string value)
    {
        // 在反馈框里打字即选中“继续完善”
        if (value.Length > 0 && !IsReviseHighlighted)
        {
            Highlight = ReviseOption.Index;
        }
    }

    private void SyncHighlight()
    {
        foreach (var option in Options)
        {
            option.IsHighlighted = option.Index == Highlight;
        }
    }

    internal string LabelFor(string? choice)
    {
        var full = Request.PermissionMode == PermissionPickerViewModel.FullAccess;
        return choice switch
        {
            "clear" => Strings.Get(full ? TKCodeLangResourceKind.PlanReviewChoiceClearFull : TKCodeLangResourceKind.PlanReviewChoiceClear),
            "auto" => Strings.Get(full ? TKCodeLangResourceKind.PlanReviewChoiceFull : TKCodeLangResourceKind.PlanReviewChoiceAuto),
            "ask" => Strings.Get(TKCodeLangResourceKind.PlanReviewChoiceAsk),
            "acp" => Strings.Format(TKCodeLangResourceKind.PlanReviewChoiceAcpFormat, PermissionLabel(Request.PermissionMode)),
            _ => Strings.Get(TKCodeLangResourceKind.PlanReviewRevise),
        };
    }

    internal string? SuffixFor(string? choice) =>
        choice == "clear" && _contextPercent is { } percent
            ? Strings.Format(TKCodeLangResourceKind.PlanReviewContextUsedFormat,
                percent < 1 ? "<1" : percent.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : null;

    private static string PermissionLabel(string mode) => Strings.Get(PermissionPickerViewModel.Normalize(mode) switch
    {
        PermissionPickerViewModel.AutoApprove => TKCodeLangResourceKind.PermissionAuto,
        PermissionPickerViewModel.FullAccess => TKCodeLangResourceKind.PermissionFull,
        _ => TKCodeLangResourceKind.PermissionAsk,
    });

    public void MoveHighlight(int delta) => Highlight = (Highlight + delta + Options.Count) % Options.Count;

    /// <summary>选中一行（点击或数字键）；选到“继续完善”时视图把焦点放进反馈框。</summary>
    [RelayCommand]
    public void Select(PlanReviewOptionViewModel? option)
    {
        if (option is not null && ReferenceEquals(option.Owner, this))
        {
            Highlight = option.Index;
        }
    }

    /// <summary>编辑 ↔ 渲染：结束编辑时把源码写回计划（改过就标为“已编辑”）。</summary>
    [RelayCommand]
    public void ToggleEdit()
    {
        if (IsEditing)
        {
            CommitEdit();
            IsEditing = false;
        }
        else
        {
            EditText = Plan;
            IsEditing = true;
        }
    }

    private void CommitEdit()
    {
        if (IsEditing)
        {
            Plan = Normalize(EditText);
        }
    }

    [RelayCommand]
    private void OpenInEditor()
    {
        CommitEdit();
        _openFile?.Invoke(Request.PlanPath);
    }

    /// <summary>
    /// 面板重新获得焦点（从外部编辑器回来）：重读计划文件，有变化就刷新显示并按“已编辑”处理。返回是否有变化。
    /// </summary>
    public bool RefreshFromFile()
    {
        if (IsDecided)
        {
            return false;
        }
        string text;
        try
        {
            if (!File.Exists(Request.PlanPath))
            {
                return false;
            }
            text = Normalize(File.ReadAllText(Request.PlanPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        if (text.Length == 0 || string.Equals(text, _fileSnapshot, StringComparison.Ordinal))
        {
            return false;
        }
        _fileSnapshot = text;
        Plan = text;
        EditText = text;
        return true;
    }

    /// <summary>确认当前选中的行：批准方式 → 批准（带编辑后的计划）；“继续完善” → 退回并附反馈。</summary>
    [RelayCommand]
    public void Confirm()
    {
        CommitEdit();
        var selected = Selected;
        if (selected.IsRevise)
        {
            // 编辑过的计划不随“继续完善”保存（模型接下来会自己改计划文件），先提示一次
            if (IsEdited && !ShowDiscardWarning)
            {
                ShowDiscardWarning = true;
                return;
            }
            var feedback = Feedback.Trim();
            Decide(new PlanReviewResponse(PlanReviewResponse.Revise, Feedback: feedback.Length == 0 ? null : feedback));
            return;
        }
        Decide(new PlanReviewResponse(PlanReviewResponse.Approved, selected.Choice, EditedPlan: IsEdited ? Plan : null));
    }

    /// <summary>暂不批准（Esc）：模型收到“未批准，结束本轮”。</summary>
    [RelayCommand]
    public void Dismiss() => Decide(PlanReviewResponse.Dismiss);

    /// <summary>Host 撤回请求（停止、运行时取消）或会话关闭：移除面板。</summary>
    public void Cancel() => Decide(PlanReviewResponse.Dismiss);

    private void Decide(PlanReviewResponse response)
    {
        if (_result.TrySetResult(response))
        {
            IsEditing = false;
            Decided?.Invoke(this);
        }
    }

    /// <summary>比较与返回用的规范形式：统一换行，去掉首尾空白（Host 读取计划时同样去掉了首尾空白）。</summary>
    private static string Normalize(string text) => text.Replace("\r\n", "\n").Trim();

    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(EditButtonText));
        OnPropertyChanged(nameof(FeedbackPlaceholder));
        OnPropertyChanged(nameof(HintsText));
        foreach (var option in Options)
        {
            option.RefreshLanguage();
        }
    }
}
