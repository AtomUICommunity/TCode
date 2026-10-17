using System.Text.Json;
using TCode.Desktop.Icons;
using TCode.Desktop.Localization;

namespace TCode.Desktop.ViewModels;

/// <summary>工具调用在会话中的类别：决定步骤行的图标、动词与活动组摘要里的计数项。</summary>
public enum ToolCategory
{
    Command,
    Read,
    Edit,
    Search,
    Fetch,
    Task,
    Plan,
    /// <summary>MCP 服务器的工具（mcp__&lt;服务器&gt;__&lt;工具&gt;）。</summary>
    Mcp,
    /// <summary>加载 skill（内置 Agent 的 skill、Claude 的 Skill）。</summary>
    Skill,
    Other,
}

/// <summary>
/// 工具调用的展示：按工具名归类（内置 Agent、Claude、Codex、ACP 的常见工具名），从参数中取出主参数
/// （命令、路径、搜索词、URL…）作为步骤行的单行文本，展开后显示完整参数。
/// </summary>
public static class ToolPresentation
{
    public static ToolCategory Classify(string toolName) => toolName.StartsWith("mcp__", StringComparison.Ordinal) ? ToolCategory.Mcp : toolName.ToLowerInvariant() switch
    {
        "bash" or "shell" or "run_shell_command" or "exec" or "exec_command" or "run_terminal_cmd" or "terminal"
            or "local_shell" or "bashoutput" => ToolCategory.Command,
        "read" or "read_file" or "view" or "view_image" or "notebookread" or "open_file" or "read_text_file" => ToolCategory.Read,
        "edit" or "write" or "multiedit" or "notebookedit" or "apply_patch" or "write_file" or "edit_file"
            or "str_replace_editor" or "create_file" or "write_text_file" => ToolCategory.Edit,
        "grep" or "glob" or "search" or "find" or "list_directory" or "ls" or "codebase_search" or "file_search"
            or "grep_search" or "toolsearch" => ToolCategory.Search,
        "webfetch" or "web_fetch" or "websearch" or "web_search" or "fetch" => ToolCategory.Fetch,
        "task" or "agent" => ToolCategory.Task,
        "skill" => ToolCategory.Skill,
        "task_create" or "task_update" or "task_get" or "task_list" or "todowrite" or "update_plan" or "taskcreate" or "taskupdate"
            => ToolCategory.Plan,
        _ => ToolCategory.Other,
    };

    public static LucideKind IconFor(ToolCategory category) => category switch
    {
        ToolCategory.Command => LucideKind.SquareTerminal,
        ToolCategory.Read => LucideKind.FileText,
        ToolCategory.Edit => LucideKind.Pencil,
        ToolCategory.Search => LucideKind.Search,
        ToolCategory.Fetch => LucideKind.Globe,
        ToolCategory.Task => LucideKind.Bot,
        ToolCategory.Plan => LucideKind.ListChecks,
        ToolCategory.Mcp => LucideKind.Plug,
        ToolCategory.Skill => LucideKind.Sparkles,
        _ => LucideKind.Wrench,
    };

    /// <summary>步骤行的动词；其他工具显示工具名（ACP 的工具名本身就是一句标题时不再重复）。</summary>
    public static string VerbFor(ToolCategory category, string toolName) => category switch
    {
        ToolCategory.Command => Strings.Get(TCodeLangResourceKind.StepRun),
        ToolCategory.Read => Strings.Get(TCodeLangResourceKind.StepRead),
        ToolCategory.Edit => Strings.Get(TCodeLangResourceKind.StepEdit),
        ToolCategory.Search => Strings.Get(TCodeLangResourceKind.StepSearch),
        ToolCategory.Fetch => Strings.Get(TCodeLangResourceKind.StepFetch),
        ToolCategory.Task => Strings.Get(TCodeLangResourceKind.StepTask),
        ToolCategory.Plan => Strings.Get(TCodeLangResourceKind.StepPlan),
        ToolCategory.Mcp => McpLabel(toolName),
        ToolCategory.Skill => Strings.Get(TCodeLangResourceKind.StepSkill),
        _ => toolName,
    };

    /// <summary>
    /// MCP 工具名 → “服务器 · 工具”。内置 Agent 与 Codex 为 mcp__&lt;服务器&gt;__&lt;工具&gt;；
    /// 外部运行时经 TCode 桥接时为 mcp__tcode__&lt;服务器&gt;__&lt;工具&gt;。
    /// </summary>
    public static string McpLabel(string toolName)
    {
        var parts = toolName["mcp__".Length..].Split("__", 3);
        if (parts.Length == 3 && parts[0] == "tcode")
        {
            return $"{parts[1]} · {parts[2]}";
        }
        return parts.Length >= 2 ? $"{parts[0]} · {string.Join("__", parts[1..])}" : toolName;
    }

    private static readonly string[] CommandKeys = ["command", "cmd"];
    private static readonly string[] PathKeys = ["file_path", "path", "filePath", "target_file", "notebook_path", "uri"];
    private static readonly string[] SearchKeys = ["pattern", "query", "glob", "regex", "path"];
    private static readonly string[] FetchKeys = ["url", "query"];
    private static readonly string[] TaskKeys = ["description", "prompt"];
    private static readonly string[] PlanKeys = ["subject", "status", "task_id"];
    private static readonly string[] SkillKeys = ["name", "skill", "command"];

    /// <summary>主参数（完整文本，可能多行）；参数不是完整 JSON（仍在流式）时返回 null。</summary>
    public static string? PrimaryArgument(ToolCategory category, string argumentsJson)
    {
        JsonElement args;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            args = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
        if (args.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        return category switch
        {
            ToolCategory.Command => First(args, CommandKeys),
            ToolCategory.Read => First(args, PathKeys),
            ToolCategory.Edit => First(args, PathKeys) ?? ChangedPaths(args),
            ToolCategory.Search => First(args, SearchKeys),
            ToolCategory.Fetch => First(args, FetchKeys),
            ToolCategory.Task => First(args, TaskKeys),
            ToolCategory.Plan => First(args, PlanKeys),
            ToolCategory.Skill => First(args, SkillKeys),
            _ => FirstString(args),
        };
    }

    /// <summary>流式参数中提取主参数时扫描的长度（主参数通常是第一个字段）。</summary>
    public const int PartialScanLength = 4096;

    /// <summary>
    /// 从尚不完整的参数 JSON 开头提取主参数的值（不做完整解析）：找到 "key": " 后读取到未转义的引号或扫描末尾。
    /// </summary>
    public static string? PartialPrimaryArgument(ToolCategory category, System.Text.StringBuilder raw)
    {
        var keys = category switch
        {
            ToolCategory.Command => CommandKeys,
            ToolCategory.Read or ToolCategory.Edit => PathKeys,
            ToolCategory.Search => SearchKeys,
            ToolCategory.Fetch => FetchKeys,
            ToolCategory.Task => TaskKeys,
            ToolCategory.Skill => SkillKeys,
            _ => null,
        };
        if (keys is null)
        {
            return null;
        }
        var head = raw.ToString(0, Math.Min(raw.Length, PartialScanLength));
        foreach (var key in keys)
        {
            var at = head.IndexOf($"\"{key}\"", StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }
            var i = at + key.Length + 2;
            while (i < head.Length && head[i] is ' ' or ':' or '\t' or '\n' or '\r')
            {
                i++;
            }
            if (i >= head.Length || head[i] != '"')
            {
                continue;
            }
            var value = new System.Text.StringBuilder();
            for (i++; i < head.Length; i++)
            {
                var c = head[i];
                if (c == '"')
                {
                    break;
                }
                if (c == '\\' && i + 1 < head.Length)
                {
                    var next = head[++i];
                    value.Append(next switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => next });
                    continue;
                }
                value.Append(c);
            }
            if (value.Length > 0)
            {
                return value.ToString();
            }
        }
        return null;
    }

    /// <summary>单行显示：换行与多余空白压成一个空格。</summary>
    public static string SingleLine(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string? First(JsonElement args, string[] keys)
    {
        foreach (var key in keys)
        {
            if (args.TryGetProperty(key, out var value))
            {
                switch (value.ValueKind)
                {
                    case JsonValueKind.String when value.GetString() is { Length: > 0 } s:
                        return s;
                    case JsonValueKind.Array:
                        // Codex 等把命令拆成 argv 数组
                        var parts = value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()).ToArray();
                        if (parts.Length > 0)
                        {
                            return string.Join(' ', parts);
                        }
                        break;
                }
            }
        }
        return null;
    }

    private static string? ChangedPaths(JsonElement args)
    {
        if (!args.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var paths = changes.EnumerateArray()
            .Select(c => c.ValueKind == JsonValueKind.Object && c.TryGetProperty("path", out var p) ? p.GetString() : null)
            .OfType<string>().ToArray();
        return paths.Length == 0 ? null : string.Join(", ", paths);
    }

    private static string? FirstString(JsonElement args) =>
        args.EnumerateObject().Select(p => p.Value).FirstOrDefault(v => v.ValueKind == JsonValueKind.String) is { ValueKind: JsonValueKind.String } s
            ? s.GetString()
            : null;

    /// <summary>活动组摘要：“思考 2 次 · 运行 4 条命令 · 读取 3 个文件”。</summary>
    public static string Summary(int thoughts, IEnumerable<ToolCategory> tools)
    {
        var parts = new List<string>();
        if (thoughts == 1)
        {
            parts.Add(Strings.Get(TCodeLangResourceKind.ThoughtProcess));
        }
        else if (thoughts > 1)
        {
            parts.Add(Strings.Format(TCodeLangResourceKind.ActivityThoughtMany, thoughts));
        }
        foreach (var group in tools.GroupBy(c => c).OrderBy(g => g.Key))
        {
            var n = group.Count();
            parts.Add(group.Key switch
            {
                ToolCategory.Command => Count(n, TCodeLangResourceKind.ActivityCommandOne, TCodeLangResourceKind.ActivityCommandMany),
                ToolCategory.Read => Count(n, TCodeLangResourceKind.ActivityReadOne, TCodeLangResourceKind.ActivityReadMany),
                ToolCategory.Edit => Count(n, TCodeLangResourceKind.ActivityEditOne, TCodeLangResourceKind.ActivityEditMany),
                ToolCategory.Search => Count(n, TCodeLangResourceKind.ActivitySearchOne, TCodeLangResourceKind.ActivitySearchMany),
                ToolCategory.Fetch => Count(n, TCodeLangResourceKind.ActivityFetchOne, TCodeLangResourceKind.ActivityFetchMany),
                ToolCategory.Task => Count(n, TCodeLangResourceKind.ActivityTaskOne, TCodeLangResourceKind.ActivityTaskMany),
                _ => Count(n, TCodeLangResourceKind.ActivityToolOne, TCodeLangResourceKind.ActivityToolMany),
            });
        }
        return string.Join(" · ", parts);
    }

    private static string Count(int n, TCodeLangResourceKind one, TCodeLangResourceKind many) =>
        n == 1 ? Strings.Get(one) : Strings.Format(many, n);
}
