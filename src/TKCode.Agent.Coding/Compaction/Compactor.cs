using System.Diagnostics;
using System.Text;
using TKCode.Agent.Coding.Prompt;
using TKCode.Agent.Coding.Sessions;
using TKCode.Agent.Coding.Tools;
using TKCode.Agent.Coding.Tools.Files;
using TKCode.Agent.Compaction;
using TKCode.Agent.Diff;
using TKCode.Ai;
using TKCode.Ai.Messages;

namespace TKCode.Agent.Coding.Compaction;

/// <summary>
/// 内置 Agent 的压缩器（docs/design/compaction.md）：分叉请求生成摘要（失败时回退到独立摘要请求，超长时丢弃最早的轮次重试），
/// 记录压缩、重建系统提示，再构造“摘要 + 重新附加内容”两条用户消息。
/// </summary>
public sealed class Compactor(SessionStore store, ToolContext tools, SystemPromptTracker prompt) : ICompactor
{
    private const int MaxRetries = 3;
    private const int RecentFiles = 5;
    private const int FileTokens = 5_000;
    private const int FilesBudget = 50_000;
    private const int SkillTokens = 5_000;
    private const int SkillsBudget = 25_000;
    private const int SerializedResultChars = 2_000;

    public async Task<CompactionResult> CompactAsync(CompactionRequest request, CancellationToken cancellationToken)
    {
        if (request.Handoff is { } handoff)
        {
            return await HandoffAsync(request, handoff);
        }
        if (!request.Messages.Any(m => m is AssistantMessage))
        {
            throw new CompactionException("Not enough messages to compact.");
        }
        var started = Stopwatch.GetTimestamp();
        var (reply, truncated) = await SummarizeAsync(request, cancellationToken);
        var summary = CompactionPrompts.PostProcess(reply);
        if (summary.Length == 0)
        {
            throw new CompactionException("Failed to generate conversation summary: the model returned no summary.");
        }

        // 会话文件中的顺序：压缩记录 → 新的开头系统提示 → 摘要与重新附加的消息（由 Agent 追加）
        var messages = new List<Message>
        {
            new UserMessage([new TextContent(CompactionPrompts.SummaryMessage(summary, store.FilePath, truncated, request.ResumesWork))])
            {
                Source = CompactionPrompts.SummarySource,
            },
        };
        var context = await BuildContextAsync(request.Messages, cancellationToken);
        if (context is not null)
        {
            messages.Add(new UserMessage([new TextContent(context)]) { Source = CompactionPrompts.ContextSource });
        }

        return await CommitAsync(request, messages, summary, truncated, Stopwatch.GetElapsedTime(started));
    }

    /// <summary>
    /// 批准计划并清空上下文（docs/design/plan-mode.md §11）：不请求模型，历史换成交接消息，
    /// 再附上未完成的任务与仍在运行的后台任务；读取记录清空，文件要按计划重新读。
    /// </summary>
    private async Task<CompactionResult> HandoffAsync(CompactionRequest request, string handoff)
    {
        var started = Stopwatch.GetTimestamp();
        tools.Files.Clear();
        var messages = new List<Message>
        {
            new UserMessage([new TextContent(handoff)]) { Source = CompactionPrompts.HandoffSource },
        };
        var work = OpenWork();
        if (work.Count > 0)
        {
            messages.Add(new UserMessage([new TextContent(
                "<system-reminder>\nContext carried over from the planning conversation:\n\n" + string.Join("\n\n", work) + "\n</system-reminder>")])
            {
                Source = CompactionPrompts.ContextSource,
            });
        }
        return await CommitAsync(request, messages, handoff, false, Stopwatch.GetElapsedTime(started));
    }

    /// <summary>记录压缩、按当前各节重建开头的系统提示（之前的补丁合并进去）；新历史以重新读取的记忆索引开头。</summary>
    private async Task<CompactionResult> CommitAsync(CompactionRequest request, List<Message> messages, string summary, bool truncated, TimeSpan elapsed)
    {
        if (tools.Memory is { } memory)
        {
            // 压缩后的第一次请求就带上最新的记忆索引（docs/design/memory.md §4.2）
            messages.Insert(0, memory.IndexMessage());
        }
        var sections = prompt.Current(request.Model);
        var postTokens = ContextAccounting.EstimateText(SystemPromptBuilder.Render(sections)) + ContextAccounting.Estimate(messages) +
                         request.Tools.Sum(t => ContextAccounting.EstimateText(t.Description) + ContextAccounting.EstimateText(t.ParametersSchemaJson));
        await store.AppendCompactionAsync(request.Trigger, summary, request.PreTokens, postTokens, (long)elapsed.TotalMilliseconds,
            request.Instructions, truncated, CancellationToken.None);
        var rebased = await prompt.RebaseAsync(sections, CancellationToken.None);
        tools.ResetNestedInstructions();
        return new CompactionResult(rebased, messages, summary, postTokens);
    }

    /// <summary>
    /// 主路径：与会话相同的系统提示、工具与历史，末尾追加压缩提示（§6.1）。超窗时丢弃最早的 API 轮组重试（§6.3），
    /// 其他失败（无文本、出现工具调用、协议错误）走序列化的独立摘要请求（§6.2）。
    /// </summary>
    private static async Task<(string Reply, bool Truncated)> SummarizeAsync(CompactionRequest request, CancellationToken ct)
    {
        var maxTokens = Math.Min(request.Model.MaxTokens, ContextAccounting.SummaryMaxTokens);
        var instruction = UserMessage.FromText(CompactionPrompts.CompactPrompt(request.Instructions));
        var groups = Group(request.Messages);
        var dropped = 0;
        IReadOnlyList<Message> history = request.Messages;
        for (var attempt = 0; ; attempt++)
        {
            var reply = await request.Complete(new LlmContext(request.SystemPrompt, [.. history, instruction], request.Tools), maxTokens, ct);
            ct.ThrowIfCancellationRequested();
            if (Usable(reply) is { } text)
            {
                return (text, dropped > 0);
            }
            if (reply.StopReason != StopReason.Error || !ContextOverflow.Is(reply))
            {
                break;
            }
            if (attempt >= MaxRetries)
            {
                throw new CompactionException("Conversation too long to compact, even after dropping the earliest messages. Start a new session.");
            }
            dropped += DropCount(groups, dropped, ContextOverflow.Excess(reply.ErrorMessage));
            if (groups.Count - dropped < 2)
            {
                throw new CompactionException("Not enough messages to compact.");
            }
            history = [UserMessage.FromText(CompactionPrompts.TruncationMarker), .. groups.Skip(dropped).SelectMany(g => g)];
        }

        var serialized = Serialize(history) + "\n\n" + CompactionPrompts.CompactPrompt(request.Instructions);
        var fallback = await request.Complete(new LlmContext(CompactionPrompts.FallbackSystemPrompt, [UserMessage.FromText(serialized)], []), maxTokens, ct);
        ct.ThrowIfCancellationRequested();
        return Usable(fallback) is { } fallbackText
            ? (fallbackText, dropped > 0)
            : throw new CompactionException($"Failed to generate conversation summary: {fallback.ErrorMessage ?? "the model returned no text."}");
    }

    /// <summary>回复可用时返回文本：没有失败、没有调用工具、有文字（被 max_tokens 截断的摘要也接受）。</summary>
    private static string? Usable(AssistantMessage reply)
    {
        if (reply.StopReason is StopReason.Error or StopReason.Aborted || reply.Content.Any(c => c is ToolCallContent))
        {
            return null;
        }
        var text = string.Concat(reply.Content.OfType<TextContent>().Select(t => t.Text)).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// 按 API 轮组切分（§6.3）：每组以一条助手消息为核心，带上它之后的工具结果与之前的用户 / 系统消息。
    /// 从头部丢弃整组不会留下没有调用的工具结果。
    /// </summary>
    internal static List<List<Message>> Group(IReadOnlyList<Message> messages)
    {
        var groups = new List<List<Message>>();
        var current = new List<Message>();
        foreach (var message in messages)
        {
            // 组内已有助手消息、且上一条不是等待回复的输入时，新的输入或助手消息开始下一组
            if (message is not ToolResultMessage && current.Any(m => m is AssistantMessage) && current[^1] is AssistantMessage or ToolResultMessage)
            {
                groups.Add(current);
                current = [];
            }
            current.Add(message);
        }
        if (current.Count > 0)
        {
            groups.Add(current);
        }
        return groups;
    }

    /// <summary>要再丢弃的组数：覆盖错误报出的超出量；不知道超出多少时丢剩余组的 20%（至少 1 组）。</summary>
    internal static int DropCount(List<List<Message>> groups, int alreadyDropped, long? excess)
    {
        var remaining = groups.Count - alreadyDropped;
        if (excess is not { } target)
        {
            return Math.Max(1, remaining / 5);
        }
        long freed = 0;
        var count = 0;
        while (count < remaining - 1 && freed < target)
        {
            freed += ContextAccounting.Estimate(groups[alreadyDropped + count]);
            count++;
        }
        return Math.Max(1, count);
    }

    /// <summary>回退请求用的纯文本历史（§6.2）：不含 thinking，工具结果截断，图片写作 [image]。</summary>
    internal static string Serialize(IReadOnlyList<Message> messages)
    {
        var sb = new StringBuilder();
        foreach (var message in messages)
        {
            switch (message)
            {
                case SystemMessage s:
                    sb.Append("[System]: ").Append(s.Text).Append("\n\n");
                    break;
                case UserMessage u:
                    sb.Append("[User]: ").Append(ContextAccounting.TextOf(u.Content)).Append("\n\n");
                    break;
                case AssistantMessage { StopReason: not (StopReason.Error or StopReason.Aborted) } a:
                    var text = string.Concat(a.Content.OfType<TextContent>().Select(t => t.Text));
                    if (text.Length > 0)
                    {
                        sb.Append("[Assistant]: ").Append(text).Append("\n\n");
                    }
                    var calls = a.Content.OfType<ToolCallContent>().Select(c => $"{c.Name}({Truncate(c.ArgumentsJson)})").ToArray();
                    if (calls.Length > 0)
                    {
                        sb.Append("[Assistant tool calls]: ").AppendJoin("; ", calls).Append("\n\n");
                    }
                    break;
                case ToolResultMessage r:
                    sb.Append("[Tool result]: ").Append(Truncate(ContextAccounting.TextOf(r.Content))).Append("\n\n");
                    break;
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string Truncate(string text) =>
        text.Length <= SerializedResultChars ? text : text[..SerializedResultChars] + $"… [{text.Length - SerializedResultChars} more characters]";

    /// <summary>
    /// 重新附加的内容（§7.3）：最近读过的文件、加载过的 skill、未完成的任务、仍在运行的后台任务。
    /// 读取记录清空后只为附上了全文的文件重新记录。没有任何内容时返回 null。
    /// </summary>
    private async Task<string?> BuildContextAsync(IReadOnlyList<Message> messages, CancellationToken ct)
    {
        var sections = new List<string>();

        var recent = tools.Files.Recent(RecentFiles);
        tools.Files.Clear();
        long fileTokens = 0;
        foreach (var path in recent)
        {
            if (SystemPromptBuilder.ContextFileNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase) || !File.Exists(path))
            {
                continue;
            }
            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(path, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (TextFile.LooksBinary(bytes))
            {
                continue;
            }
            var numbered = Numbered(TextFile.Decode(bytes).Text);
            var tokens = ContextAccounting.EstimateText(numbered);
            if (tokens > FileTokens || fileTokens + tokens > FilesBudget)
            {
                sections.Add($"Referenced file {path} (read before the conversation was compacted; too large to include — read it again if you need it).");
                continue;
            }
            fileTokens += tokens;
            tools.Files.Record(path, bytes);
            sections.Add($"Called the read tool with the following input: {{\"path\":\"{path}\"}}\nResult of calling the read tool:\n{numbered}");
        }

        var skills = new List<string>();
        long skillTokens = 0;
        foreach (var result in messages.OfType<ToolResultMessage>().Where(r => r.ToolName == "skill" && !r.IsError).Reverse())
        {
            var text = ContextAccounting.TextOf(result.Content);
            if (text.Length > ContextAccounting.CharsFor(SkillTokens))
            {
                text = text[..ContextAccounting.CharsFor(SkillTokens)] + "\n[skill content truncated]";
            }
            var tokens = ContextAccounting.EstimateText(text);
            if (skillTokens + tokens > SkillsBudget || skills.Contains(text))
            {
                continue;
            }
            skillTokens += tokens;
            skills.Insert(0, text);
        }
        if (skills.Count > 0)
        {
            sections.Add("Skills loaded earlier in this session (still in effect):\n\n" + string.Join("\n\n", skills));
        }

        sections.AddRange(OpenWork());

        return sections.Count == 0
            ? null
            : "<system-reminder>\nContext restored after compaction:\n\n" + string.Join("\n\n", sections) + "\n</system-reminder>";
    }

    /// <summary>未完成的任务与仍在运行的后台任务（§7.3）。</summary>
    private List<string> OpenWork()
    {
        var sections = new List<string>();
        var open = tools.Tasks.Items.Where(t => t.Status != "completed").ToArray();
        if (open.Length > 0)
        {
            sections.Add("Current task list (unfinished items):\n" + string.Join("\n", open.Select(t => $"#{t.Id} [{t.Status}] {t.Subject}")));
        }

        var background = tools.BackgroundTasks.All.Where(t => t.IsRunning && (t.Owner is null || t.Owner == tools.SessionId)).ToArray();
        if (background.Length > 0)
        {
            sections.Add("Background tasks still running:\n" + string.Join("\n", background.Select(t =>
                $"- {t.Id} ({t.Type}): {Clip(t.Description, 200)}{(t.OutputPath is null ? "" : $" — output: {t.OutputPath}")}")));
        }
        return sections;
    }

    /// <summary>与 read 工具相同的 cat -n 格式。</summary>
    private static string Numbered(string text)
    {
        var lines = LineDiff.SplitLines(text);
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Length > ReadTool.MaxLineLength ? lines[i][..ReadTool.MaxLineLength] + "… [line truncated]" : lines[i];
            sb.Append($"{i + 1,6}\t{line}\n");
        }
        return sb.ToString();
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
