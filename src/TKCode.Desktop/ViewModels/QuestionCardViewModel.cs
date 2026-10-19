using System.ComponentModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using TKCode.Desktop.Localization;

namespace TKCode.Desktop.ViewModels;

/// <summary>转录问答卡片里的一行：问题与回答。</summary>
public sealed record QuestionCardLine(string Header, string Question, string Answer, bool IsAnswered)
{
    /// <summary>未回答的占位（“未回答”）；等待回答时没有。</summary>
    public bool ShowPlaceholder => !IsAnswered && Answer.Length > 0;

    public bool HasHeader => Header.Length > 0;
}

/// <summary>
/// 提问工具（内置 ask_user、Claude AskUserQuestion、Codex request_user_input）在转录里的独立卡片
/// （docs/design/ask-user.md §7.4）：不并入活动组，轮次结束后也不折叠。
/// 内容取自工具结果的 Details（kind = question）；还没有 Details 时按参数显示问题，状态为“等待回答”。
/// 申请进入 Plan 模式（enter_plan_mode / Claude EnterPlanMode，docs/design/plan-mode.md §14.2）沿用同一张卡片：
/// 标题“进入 Plan 模式？”，正文是模型给的理由，结局取自 Details（kind = enterPlanMode）。
/// </summary>
public sealed partial class QuestionCardViewModel : TranscriptItemViewModel
{
    public QuestionCardViewModel(ToolCallBlockViewModel call)
    {
        Call = call;
        call.PropertyChanged += OnCallChanged;
        Refresh();
    }

    public ToolCallBlockViewModel Call { get; }

    /// <summary>申请进入 Plan 模式的确认（而不是一般的提问）。</summary>
    public bool IsEnterPlan => Call.Category == ToolCategory.EnterPlan;

    public Icons.LucideKind Icon => IsEnterPlan ? Icons.LucideKind.ClipboardList : Icons.LucideKind.MessageCircleQuestion;

    /// <summary>pending / answered / declined / aborted；进入 Plan 模式时为 pending / accepted / declined / aborted。</summary>
    [ObservableProperty]
    public partial string Status { get; private set; } = "pending";

    [ObservableProperty]
    public partial IReadOnlyList<QuestionCardLine> Lines { get; private set; } = [];

    public bool IsPending => Status == "pending";

    public string StatusText => Strings.Get(Status switch
    {
        "answered" => TKCodeLangResourceKind.QuestionAnswered,
        "accepted" => TKCodeLangResourceKind.EnterPlanAccepted,
        "declined" => IsEnterPlan ? TKCodeLangResourceKind.EnterPlanDeclined : TKCodeLangResourceKind.QuestionDeclined,
        "aborted" => TKCodeLangResourceKind.QuestionAborted,
        _ => TKCodeLangResourceKind.QuestionWaiting,
    });

    public string Title => Strings.Get(IsEnterPlan ? TKCodeLangResourceKind.EnterPlanTitle : TKCodeLangResourceKind.StepQuestion);

    partial void OnStatusChanged(string value)
    {
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(StatusText));
    }

    private void OnCallChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ToolCallBlockViewModel.DetailsJson) or nameof(ToolCallBlockViewModel.Status)
            or nameof(ToolCallBlockViewModel.Arguments) or nameof(ToolCallBlockViewModel.IsStreaming))
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        if (IsEnterPlan)
        {
            RefreshEnterPlan();
            return;
        }
        if (Parse(Call.DetailsJson, details: true) is { } fromDetails)
        {
            (Status, Lines) = fromDetails;
            return;
        }
        var lines = Parse(Call.Arguments, details: false)?.Lines ?? Lines;
        Lines = lines;
        // 没有 Details 就结束了：工具出错（例如参数被拒绝）或被中止
        Status = Call.Status switch
        {
            ToolCallStatus.Failed => "aborted",
            ToolCallStatus.Succeeded => "answered",
            _ => "pending",
        };
    }

    /// <summary>
    /// 进入 Plan 模式：Details（<c>{"kind":"enterPlanMode","status":…,"reason":…}</c>）给出结局；
    /// 没有 Details 时理由取自参数，结局按工具状态（Claude 的 EnterPlanMode 成功即已进入）。
    /// </summary>
    private void RefreshEnterPlan()
    {
        var (status, reason) = ParseEnterPlan(Call.DetailsJson, details: true) ?? (null, null);
        reason ??= ParseEnterPlan(Call.Arguments, details: false)?.Reason;
        // 等待回答时的 Details 是 pending；被停止时结果里没有新的 Details，按工具状态判断
        if (status == "pending" && !Call.IsBusy)
        {
            status = null;
        }
        Status = status ?? Call.Status switch
        {
            ToolCallStatus.Failed => "aborted",
            ToolCallStatus.Succeeded => "accepted",
            _ => "pending",
        };
        Lines = string.IsNullOrWhiteSpace(reason) ? [] : [new QuestionCardLine("", reason.Trim(), "", false)];
    }

    internal static (string? Status, string? Reason)? ParseEnterPlan(string? json, bool details)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || (details && !(root.TryGetProperty("kind", out var kind) && kind.ValueEquals("enterPlanMode"))))
            {
                return null;
            }
            return (details ? Str(root, "status") : null, Str(root, "reason"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>解析 Details（带回答）或工具参数（只有问题；Claude 与内置的形状相同，Codex 多了 id）。</summary>
    internal static (string Status, IReadOnlyList<QuestionCardLine> Lines)? Parse(string? json, bool details)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || (details && !(root.TryGetProperty("kind", out var kind) && kind.ValueEquals("question")))
                || !root.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            var status = details && root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "pending";
            var lines = new List<QuestionCardLine>();
            foreach (var q in questions.EnumerateArray())
            {
                var text = Str(q, "question") ?? "";
                var header = Str(q, "header") ?? "";
                var parts = new List<string>();
                if (q.ValueKind == JsonValueKind.Object && q.TryGetProperty("selected", out var selected) && selected.ValueKind == JsonValueKind.Array)
                {
                    parts.AddRange(selected.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
                }
                if (Str(q, "text") is { Length: > 0 } typed)
                {
                    parts.Add("“" + typed + "”");
                }
                var answered = parts.Count > 0;
                lines.Add(new QuestionCardLine(header, text,
                    answered ? string.Join(", ", parts) : details && status == "answered" ? Strings.Get(TKCodeLangResourceKind.QuestionUnanswered) : "",
                    answered));
            }
            return (status, lines);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // 只对对象取属性：JsonElement.TryGetProperty 对非对象抛 InvalidOperationException（questions 里可能混入字符串）
    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(Title));
        Refresh();
    }
}
