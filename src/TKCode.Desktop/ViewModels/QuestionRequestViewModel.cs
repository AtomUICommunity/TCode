using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKCode.Desktop.Localization;
using TKCode.Rpc;

namespace TKCode.Desktop.ViewModels;

/// <summary>问答面板里的一个选项（“其他”行不在这里，见 <see cref="QuestionPageViewModel.IsOtherSelected"/>）。</summary>
public sealed partial class QuestionOptionViewModel(QuestionPageViewModel page, int index, UserQuestionOptionDto option) : ObservableObject
{
    public QuestionPageViewModel Page { get; } = page;
    public int Index { get; } = index;

    /// <summary>回答里带回的标签（选项原文）。</summary>
    public string Value { get; } = option.Label;

    /// <summary>显示的标签：进入 Plan 模式的确认按界面语言显示，其余照原文。</summary>
    public string Label => Page.Owner.IsEnterPlanMode ? EnterPlanModeText.Label(Value) ?? Value : Value;

    public string? Description => Page.Owner.IsEnterPlanMode && EnterPlanModeText.Label(Value) is not null
        ? EnterPlanModeText.Description(Value)
        : option.Description;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(HasDescription));
    }

    /// <summary>行首的序号（1–9；更多的选项没有数字快捷键）。</summary>
    public string Number => Index < 9 ? (Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : "";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool IsHighlighted { get; set; }
}

/// <summary>问答面板里的一个问题：选项、可选的“其他”自由输入，单选或多选。</summary>
public sealed partial class QuestionPageViewModel : ObservableObject
{
    public QuestionPageViewModel(QuestionRequestViewModel owner, int index, UserQuestionDto question)
    {
        Owner = owner;
        Index = index;
        Question = question;
        Options = question.Options.Select((o, i) => new QuestionOptionViewModel(this, i, o)).ToArray();
        // 没有选项（Codex options 为空）时只有文本框，必须提供输入
        AllowOther = question.AllowOther || Options.Count == 0;
        Highlight = 0;
        // 初始值就是 0，属性不会通知：直接同步一次，第一行一出现就是高亮的
        OnHighlightChanged(Highlight);
    }

    public QuestionRequestViewModel Owner { get; }
    public int Index { get; }
    public UserQuestionDto Question { get; }
    public IReadOnlyList<QuestionOptionViewModel> Options { get; }
    public bool AllowOther { get; }
    public bool MultiSelect => Question.MultiSelect;
    public bool IsSecret => Question.IsSecret;
    public string Header => Question.Header.Length > 12 ? Question.Header[..12] + "…" : Question.Header;

    /// <summary>问题正文；进入 Plan 模式的确认没有理由时，默认问题按界面语言显示。</summary>
    public string Text => Owner.IsEnterPlanMode && Question.Question == EnterPlanModeText.DefaultQuestion
        ? Strings.Get(TKCodeLangResourceKind.EnterPlanDefaultQuestion)
        : Question.Question;

    /// <summary>“其他”行的序号。</summary>
    public string OtherNumber => Options.Count < 9 ? (Options.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : "";

    public string OtherPlaceholder => Strings.Get(IsSecret ? TKCodeLangResourceKind.QuestionSecretPlaceholder : TKCodeLangResourceKind.QuestionOtherPlaceholder);

    /// <summary>可移动的行数：选项加“其他”。</summary>
    public int RowCount => Options.Count + (AllowOther ? 1 : 0);

    /// <summary>高亮的行：0..Options.Count-1 为选项，Options.Count 为“其他”。</summary>
    [ObservableProperty]
    public partial int Highlight { get; set; }

    [ObservableProperty]
    public partial bool IsOtherSelected { get; set; }

    [ObservableProperty]
    public partial string OtherText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    public bool IsOtherHighlighted => AllowOther && Highlight == Options.Count;

    public bool HasAnswer => Options.Any(o => o.IsSelected) || (IsOtherSelected && !string.IsNullOrWhiteSpace(OtherText));

    /// <summary>提交页上的回答摘要。</summary>
    public string AnswerSummary
    {
        get
        {
            var parts = Options.Where(o => o.IsSelected).Select(o => o.Label).ToList();
            if (IsOtherSelected && !string.IsNullOrWhiteSpace(OtherText))
            {
                parts.Add(IsSecret ? "••••••" : "“" + OtherText.Trim() + "”");
            }
            return parts.Count > 0 ? string.Join(", ", parts) : Strings.Get(TKCodeLangResourceKind.QuestionUnanswered);
        }
    }

    partial void OnHighlightChanged(int value)
    {
        foreach (var option in Options)
        {
            option.IsHighlighted = option.Index == value;
        }
        OnPropertyChanged(nameof(IsOtherHighlighted));
    }

    partial void OnIsOtherSelectedChanged(bool value) => AnswerChanged();

    partial void OnOtherTextChanged(string value)
    {
        // 在“其他”里输入即选中它（单选时取消其他选项）
        if (value.Length > 0 && !IsOtherSelected)
        {
            SelectOther();
        }
        AnswerChanged();
    }

    private void AnswerChanged()
    {
        OnPropertyChanged(nameof(HasAnswer));
        OnPropertyChanged(nameof(AnswerSummary));
        Owner.OnAnswerChanged();
    }

    /// <summary>选择（单选）或切换（多选）一个选项。</summary>
    public void Toggle(QuestionOptionViewModel option)
    {
        Highlight = option.Index;
        if (MultiSelect)
        {
            option.IsSelected = !option.IsSelected;
        }
        else
        {
            foreach (var o in Options)
            {
                o.IsSelected = ReferenceEquals(o, option);
            }
            IsOtherSelected = false;
        }
        AnswerChanged();
    }

    public void SelectOther()
    {
        if (!AllowOther)
        {
            return;
        }
        Highlight = Options.Count;
        if (!MultiSelect)
        {
            foreach (var o in Options)
            {
                o.IsSelected = false;
            }
        }
        IsOtherSelected = true;
    }

    public void ToggleOther()
    {
        if (MultiSelect && IsOtherSelected)
        {
            IsOtherSelected = false;
            return;
        }
        SelectOther();
    }

    public void MoveHighlight(int delta)
    {
        if (RowCount > 0)
        {
            Highlight = (Highlight + delta + RowCount) % RowCount;
        }
    }

    public UserAnswerDto ToAnswer() => new(Question.Id,
        Options.Where(o => o.IsSelected).Select(o => o.Value).ToArray(),
        IsOtherSelected && !string.IsNullOrWhiteSpace(OtherText) ? OtherText.Trim() : null);

    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(OtherPlaceholder));
        OnPropertyChanged(nameof(AnswerSummary));
        OnPropertyChanged(nameof(Text));
        foreach (var option in Options)
        {
            option.RefreshLanguage();
        }
    }
}

/// <summary>
/// 进入 Plan 模式的确认（input/request 的 source 为 enterPlanMode，docs/design/plan-mode.md §7.2、§12.4）：
/// Host 给的是英文的问题与选项，界面按语言显示；回答仍是 Host 的选项原文。
/// </summary>
public static class EnterPlanModeText
{
    /// <summary>SessionManager.EnterPlanModeSource。</summary>
    public const string Source = "enterPlanMode";

    /// <summary>EnterPlanModeTool 的选项与没有理由时的默认问题。</summary>
    public const string PlanOption = "Plan first";
    public const string ProceedOption = "Proceed";
    public const string DefaultQuestion = "Plan before making changes?";

    public static string? Label(string value) => value switch
    {
        PlanOption => Strings.Get(TKCodeLangResourceKind.EnterPlanOptionPlan),
        ProceedOption => Strings.Get(TKCodeLangResourceKind.EnterPlanOptionProceed),
        _ => null,
    };

    public static string? Description(string value) => value == PlanOption ? Strings.Get(TKCodeLangResourceKind.EnterPlanOptionPlanDesc) : null;
}

/// <summary>
/// 一次提问（Host 的 input/request，docs/design/ask-user.md §7）：问答面板替换输入框，一次显示一个问题；
/// 多个问题时最后一页是“提交”。回答、拒绝回答（Esc）或被 Host 撤回（<see cref="Cancel"/>）后从队列移除。
/// </summary>
public sealed partial class QuestionRequestViewModel : ObservableObject
{
    private readonly TaskCompletionSource<UserInputResponse> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public QuestionRequestViewModel(UserInputRequest request, string? sourceName = null)
    {
        Request = request;
        IsEnterPlanMode = request.Source == EnterPlanModeText.Source;
        SourceName = IsEnterPlanMode ? null : sourceName;
        Pages = request.Questions.Select((q, i) => new QuestionPageViewModel(this, i, q)).ToArray();
        Tabs = [.. Pages.Cast<object>()];
        if (HasReview)
        {
            Tabs.Add(ReviewTab.Instance);
        }
        CurrentIndex = 0;
        SyncCurrent();
    }

    public UserInputRequest Request { get; }
    public Task<UserInputResponse> Result => _result.Task;

    /// <summary>运行时的显示名（Claude Agent / Codex）；内置 Agent 为 null。</summary>
    public string? SourceName { get; }

    /// <summary>enter_plan_mode 的确认：标题与选项按界面语言显示（docs/design/plan-mode.md §12.4）。</summary>
    public bool IsEnterPlanMode { get; }

    public Icons.LucideKind Icon => IsEnterPlanMode ? Icons.LucideKind.ClipboardList : Icons.LucideKind.MessageCircleQuestion;

    public event Action<QuestionRequestViewModel>? Decided;

    public IReadOnlyList<QuestionPageViewModel> Pages { get; }

    /// <summary>标签页：每个问题一页，多个问题时末尾是“提交”页（<see cref="ReviewTab"/>）。</summary>
    public ObservableCollection<object> Tabs { get; }

    public bool HasReview => Pages.Count > 1;
    public bool ShowTabs => Pages.Count > 1;

    public string Title => IsEnterPlanMode ? Strings.Get(TKCodeLangResourceKind.EnterPlanTitle)
        : SourceName is { Length: > 0 } name
            ? Strings.Format(TKCodeLangResourceKind.QuestionTitleFormat, name)
            : Strings.Get(TKCodeLangResourceKind.QuestionTitle);

    /// <summary>当前标签页（问题序号；等于 Pages.Count 时是“提交”页）。</summary>
    [ObservableProperty]
    public partial int CurrentIndex { get; set; }

    public bool IsReview => HasReview && CurrentIndex >= Pages.Count;
    public QuestionPageViewModel? Current => CurrentIndex < Pages.Count ? Pages[CurrentIndex] : null;

    public int UnansweredCount => Pages.Count(p => !p.HasAnswer);
    public bool HasUnanswered => UnansweredCount > 0;
    public string UnansweredText => Strings.Format(TKCodeLangResourceKind.QuestionUnansweredFormat, UnansweredCount);

    /// <summary>主按钮：最后一步是“提交”，其余是“下一题”。</summary>
    public bool IsLastStep => HasReview ? IsReview : true;
    public string PrimaryText => Strings.Get(IsLastStep ? TKCodeLangResourceKind.QuestionSubmit : TKCodeLangResourceKind.QuestionNext);

    /// <summary>同一会话里排在后面的提问数（标题旁的 +N）。</summary>
    [ObservableProperty]
    public partial int MoreCount { get; set; }

    public bool HasMore => MoreCount > 0;
    public string MoreText => "+" + MoreCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

    partial void OnMoreCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(MoreText));
    }

    partial void OnCurrentIndexChanged(int value) => SyncCurrent();

    private void SyncCurrent()
    {
        foreach (var page in Pages)
        {
            page.IsCurrent = page.Index == CurrentIndex;
        }
        OnPropertyChanged(nameof(IsReview));
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(PrimaryText));
    }

    internal void OnAnswerChanged()
    {
        OnPropertyChanged(nameof(UnansweredCount));
        OnPropertyChanged(nameof(HasUnanswered));
        OnPropertyChanged(nameof(UnansweredText));
    }

    [RelayCommand]
    private void SelectTab(object? tab)
    {
        CurrentIndex = tab switch
        {
            QuestionPageViewModel page => page.Index,
            ReviewTab => Pages.Count,
            _ => CurrentIndex,
        };
    }

    /// <summary>点击选项：单选时选中并前进，多选时切换勾选。</summary>
    [RelayCommand]
    private void Choose(QuestionOptionViewModel? option)
    {
        if (option is null)
        {
            return;
        }
        option.Page.Toggle(option);
        if (!option.Page.MultiSelect)
        {
            Advance();
        }
    }

    /// <summary>下一题；最后一步时提交。</summary>
    [RelayCommand]
    private void Primary()
    {
        if (IsLastStep)
        {
            Submit();
        }
        else
        {
            CurrentIndex++;
        }
    }

    /// <summary>选中单选项后前进：只有一个问题时直接提交。</summary>
    public void Advance()
    {
        if (!HasReview)
        {
            Submit();
            return;
        }
        CurrentIndex = Math.Min(CurrentIndex + 1, Pages.Count);
    }

    public void MoveTab(int delta)
    {
        var count = Pages.Count + (HasReview ? 1 : 0);
        CurrentIndex = Math.Clamp(CurrentIndex + delta, 0, count - 1);
    }

    [RelayCommand]
    public void Submit() =>
        Decide(new UserInputResponse(UserInputResponse.Answered, Pages.Select(p => p.ToAnswer()).ToArray()));

    [RelayCommand]
    public void Decline() => Decide(UserInputResponse.Decline);

    /// <summary>Host 撤回请求（中止、运行时取消）或会话关闭：移除面板，不再有回答。</summary>
    public void Cancel() => Decide(UserInputResponse.Decline);

    public bool IsDecided => _result.Task.IsCompleted;

    private void Decide(UserInputResponse response)
    {
        if (_result.TrySetResult(response))
        {
            Decided?.Invoke(this);
        }
    }

    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(PrimaryText));
        OnPropertyChanged(nameof(UnansweredText));
        foreach (var page in Pages)
        {
            page.RefreshLanguage();
        }
    }
}

/// <summary>“提交”标签页的占位对象。</summary>
public sealed class ReviewTab
{
    public static ReviewTab Instance { get; } = new();

    public string Header => Strings.Get(TKCodeLangResourceKind.QuestionReviewTab);
}
