using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCode.Desktop.Icons;
using TCode.Desktop.Localization;

namespace TCode.Desktop.ViewModels;

/// <summary>diff 的一行：+ 新增、- 删除、空格为上下文、@@ 为块头。</summary>
public sealed class DiffLineViewModel(string text, DiffLineKind kind)
{
    public string Text { get; } = text;
    public DiffLineKind Kind { get; } = kind;
    public bool IsAdded => Kind == DiffLineKind.Added;
    public bool IsRemoved => Kind == DiffLineKind.Removed;
    public bool IsHunk => Kind == DiffLineKind.Hunk;
}

public enum DiffLineKind
{
    Context,
    Added,
    Removed,
    Hunk,
}

/// <summary>
/// 工具 Details（JSON，kind 区分类型，格式见 TCode.Agent.Coding.Tools.ToolDetails）的界面解析。
/// 桌面端不引用 Agent 程序集，只读取需要的字段；不认识的 kind 忽略。
/// </summary>
public sealed record ParsedToolDetails
{
    public string Kind { get; init; } = "";

    public IReadOnlyList<DiffLineViewModel> DiffLines { get; init; } = [];
    public int Added { get; init; }
    public int Removed { get; init; }

    public string? SubagentName { get; init; }
    public string? SubagentStatus { get; init; }
    public string? SubagentActivity { get; init; }
    public int ToolUses { get; init; }
    public long Tokens { get; init; }

    public string? OutputPath { get; init; }
    public int? ExitCode { get; init; }

    public IReadOnlyList<TaskItemViewModel>? Tasks { get; init; }

    /// <summary>diff 太长时只显示开头（完整内容仍在文件里）。</summary>
    private const int MaxDiffLines = 400;

    public static ParsedToolDetails? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var kind = Str(root, "kind") ?? "";
            return kind switch
            {
                "diff" => new ParsedToolDetails
                {
                    Kind = kind,
                    DiffLines = DiffLinesOf(Str(root, "diff") ?? ""),
                    Added = Int(root, "added") ?? 0,
                    Removed = Int(root, "removed") ?? 0,
                },
                "subagent" => new ParsedToolDetails
                {
                    Kind = kind,
                    SubagentName = Str(root, "name"),
                    SubagentStatus = Str(root, "status"),
                    SubagentActivity = Str(root, "currentActivity"),
                    ToolUses = Int(root, "toolUses") ?? 0,
                    Tokens = root.TryGetProperty("tokens", out var t) && t.TryGetInt64(out var tokens) ? tokens : 0,
                },
                "command" => new ParsedToolDetails { Kind = kind, OutputPath = Str(root, "outputPath"), ExitCode = Int(root, "exitCode") },
                "backgroundTask" => new ParsedToolDetails { Kind = kind, OutputPath = Str(root, "outputPath") },
                "tasks" => new ParsedToolDetails
                {
                    Kind = kind,
                    Tasks = root.TryGetProperty("tasks", out var tasks) && tasks.ValueKind == JsonValueKind.Array
                        ? tasks.EnumerateArray().Select(e => new TaskItemViewModel(Str(e, "id") ?? "", Str(e, "subject") ?? "",
                            Str(e, "status") ?? "pending", Str(e, "activeForm"))).ToArray()
                        : [],
                },
                _ => new ParsedToolDetails { Kind = kind },
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<DiffLineViewModel> DiffLinesOf(string diff)
    {
        var lines = new List<DiffLineViewModel>();
        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("+++ ", StringComparison.Ordinal) || line.Length == 0)
            {
                continue;
            }
            if (lines.Count >= MaxDiffLines)
            {
                lines.Add(new DiffLineViewModel("…", DiffLineKind.Hunk));
                break;
            }
            lines.Add(line[0] switch
            {
                '+' => new DiffLineViewModel(line, DiffLineKind.Added),
                '-' => new DiffLineViewModel(line, DiffLineKind.Removed),
                '@' => new DiffLineViewModel(line, DiffLineKind.Hunk),
                _ => new DiffLineViewModel(line, DiffLineKind.Context),
            });
        }
        return lines;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}

/// <summary>任务清单中的一项。</summary>
public sealed class TaskItemViewModel(string id, string subject, string status, string? activeForm)
{
    public string Id { get; } = id;
    public string Subject { get; } = subject;
    public string Status { get; } = status;
    public bool IsCompleted => Status == "completed";
    public bool IsInProgress => Status == "in_progress";
    public bool IsPending => Status == "pending";

    /// <summary>进行中显示进行时文案（“正在修复…”），否则显示标题。</summary>
    public string DisplayText => IsInProgress && activeForm is { Length: > 0 } active ? active : Subject;
}

/// <summary>
/// 会话的任务清单（来自 task_* 工具结果的全量快照），显示在输入框上方；全部完成后收起。
/// </summary>
public sealed partial class TaskListViewModel : ObservableObject
{
    public ObservableCollection<TaskItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    public bool IsVisible => Items.Any(i => !i.IsCompleted);

    public string Header => Strings.Format(TCodeLangResourceKind.TasksHeaderFormat, Items.Count(i => i.IsCompleted), Items.Count);

    public void Update(IReadOnlyList<TaskItemViewModel> items)
    {
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(Header));
    }

    public void RefreshLanguage() => OnPropertyChanged(nameof(Header));

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/// <summary>
/// 不是用户输入的消息：后台任务完成通知（task-notification）或其他 Agent 会话的消息（session-message）。
/// 显示为一行可展开的提示，而不是用户气泡。
/// </summary>
public sealed partial class NotificationItemViewModel : TranscriptItemViewModel
{
    public NotificationItemViewModel(string source, string text)
    {
        Source = source;
        Text = text;
        IsSessionMessage = source == "session-message";
        if (IsSessionMessage)
        {
            _from = Attribute(text, "from");
            Body = Between(text, ">", "</session-message>")?.Trim() ?? text;
        }
        else
        {
            _status = Element(text, "status");
            _subject = Element(text, "description") ?? Element(text, "agent") ?? Element(text, "task-id");
            Body = (Element(text, "result") ?? text).Trim();
        }
    }

    private readonly string? _from;
    private readonly string? _status;
    private readonly string? _subject;

    public string Source { get; }
    public string Text { get; }
    public string Body { get; }
    public bool IsSessionMessage { get; }

    public LucideKind Icon => IsSessionMessage ? LucideKind.MessageSquare : _status is "failed" ? LucideKind.CircleAlert : LucideKind.ListChecks;

    public string Header => IsSessionMessage
        ? Strings.Format(TCodeLangResourceKind.SessionMessageFromFormat, _from ?? "?")
        : Strings.Format(TCodeLangResourceKind.TaskNotificationFormat, _subject ?? "", StatusText);

    private string StatusText => Strings.Get(_status switch
    {
        "completed" => TCodeLangResourceKind.ToolDone,
        "stopped" => TCodeLangResourceKind.Aborted,
        _ => TCodeLangResourceKind.ToolFailed,
    });

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    public void RefreshLanguage() => OnPropertyChanged(nameof(Header));

    private static string? Element(string text, string name) => Between(text, $"<{name}>", $"</{name}>")?.Trim();

    private static string? Attribute(string text, string name)
    {
        var key = name + "=\"";
        var start = text.IndexOf(key, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }
        start += key.Length;
        var end = text.IndexOf('"', start);
        return end < 0 ? null : text[start..end].Replace("&quot;", "\"", StringComparison.Ordinal);
    }

    private static string? Between(string text, string open, string close)
    {
        var start = text.IndexOf(open, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }
        start += open.Length;
        var end = text.IndexOf(close, start, StringComparison.Ordinal);
        return end < 0 ? null : text[start..end];
    }
}

/// <summary>已完成的任务加删除线。</summary>
public static class TaskDecorations
{
    public static Avalonia.Data.Converters.IValueConverter Strike { get; } =
        new Avalonia.Data.Converters.FuncValueConverter<bool, Avalonia.Media.TextDecorationCollection?>(done =>
            done ? Avalonia.Media.TextDecorations.Strikethrough : null);
}
