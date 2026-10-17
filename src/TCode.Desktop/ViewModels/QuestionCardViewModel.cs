using System.ComponentModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using TCode.Desktop.Localization;

namespace TCode.Desktop.ViewModels;

/// <summary>转录问答卡片里的一行：问题与回答。</summary>
public sealed record QuestionCardLine(string Header, string Question, string Answer, bool IsAnswered)
{
    /// <summary>未回答的占位（“未回答”）；等待回答时没有。</summary>
    public bool ShowPlaceholder => !IsAnswered && Answer.Length > 0;
}

/// <summary>
/// 提问工具（内置 ask_user、Claude AskUserQuestion、Codex request_user_input）在转录里的独立卡片
/// （docs/design/ask-user.md §7.4）：不并入活动组，轮次结束后也不折叠。
/// 内容取自工具结果的 Details（kind = question）；还没有 Details 时按参数显示问题，状态为“等待回答”。
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

    /// <summary>pending / answered / declined / aborted。</summary>
    [ObservableProperty]
    public partial string Status { get; private set; } = "pending";

    [ObservableProperty]
    public partial IReadOnlyList<QuestionCardLine> Lines { get; private set; } = [];

    public bool IsPending => Status == "pending";

    public string StatusText => Strings.Get(Status switch
    {
        "answered" => TCodeLangResourceKind.QuestionAnswered,
        "declined" => TCodeLangResourceKind.QuestionDeclined,
        "aborted" => TCodeLangResourceKind.QuestionAborted,
        _ => TCodeLangResourceKind.QuestionWaiting,
    });

    public string Title => Strings.Get(TCodeLangResourceKind.StepQuestion);

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

    /// <summary>解析 Details（带回答）或工具参数（只有问题；Claude 与内置的形状相同，Codex 多了 id）。</summary>
    private static (string Status, IReadOnlyList<QuestionCardLine> Lines)? Parse(string? json, bool details)
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
                if (q.TryGetProperty("selected", out var selected) && selected.ValueKind == JsonValueKind.Array)
                {
                    parts.AddRange(selected.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
                }
                if (Str(q, "text") is { Length: > 0 } typed)
                {
                    parts.Add("“" + typed + "”");
                }
                var answered = parts.Count > 0;
                lines.Add(new QuestionCardLine(header, text,
                    answered ? string.Join(", ", parts) : details && status == "answered" ? Strings.Get(TCodeLangResourceKind.QuestionUnanswered) : "",
                    answered));
            }
            return (status, lines);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(Title));
        Refresh();
    }
}
