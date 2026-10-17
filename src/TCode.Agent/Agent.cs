using System.Diagnostics;
using System.Text.Json;
using TCode.Ai;
using TCode.Ai.Messages;
using TCode.Ai.Models;
using TCode.Ai.Streaming;

namespace TCode.Agent;

public enum QueueMode
{
    /// <summary>每次只取最早的一条。</summary>
    OneAtATime,
    /// <summary>一次取出全部。</summary>
    All,
}

/// <summary>
/// 与协议无关的 Agent 循环：调用模型 → 执行工具 → 回传结果，直到模型不再调用工具。
/// steer：当前轮（含工具）结束后、下一次模型调用前插入；followUp：Agent 即将停止时插入。
/// 历史只追加，从不改写已有消息。
/// </summary>
public sealed class Agent : IAgentLoop
{
    private readonly WireApiRegistry _apis;
    private readonly List<Message> _messages = [];
    private readonly List<Func<AgentEvent, CancellationToken, ValueTask>> _listeners = [];
    private readonly Queue<UserMessage> _steering = new();
    private readonly Queue<UserMessage> _followUps = new();
    private readonly Lock _gate = new();
    private CancellationTokenSource? _runCts;
    private Task _running = Task.CompletedTask;

    public Agent(WireApiRegistry apis, ModelInfo model, string systemPrompt, IReadOnlyList<IAgentTool> tools)
    {
        _apis = apis;
        Model = model;
        SystemPrompt = systemPrompt;
        Tools = tools;
    }

    public ModelInfo Model { get; set; }

    /// <summary>开头的系统提示。只应在 <see cref="ContextUpdates"/> 中替换（新会话首轮前按当时的动态节重建），同一轮内不变。</summary>
    public string SystemPrompt { get; set; }

    /// <summary>工具集。只应在 <see cref="ContextUpdates"/> 中替换（例如 MCP 工具变化），同一轮内不变。</summary>
    public IReadOnlyList<IAgentTool> Tools { get; set; }
    public ThinkingLevel ThinkingLevel { get; set; } = ThinkingLevel.Medium;
    /// <summary>请求 fast mode（模型不支持时由适配器忽略）。</summary>
    public bool FastMode { get; set; }

    /// <summary>权限档位；内置 Agent 由宿主的审批钩子读取（非 Ask 时直接允许）。</summary>
    public PermissionMode PermissionMode { get; set; }
    public QueueMode SteeringMode { get; set; } = QueueMode.OneAtATime;
    public QueueMode FollowUpMode { get; set; } = QueueMode.OneAtATime;
    public string? SessionId { get; init; }
    public Func<string, string?>? GetApiKey { get; init; }

    /// <summary>执行前钩子：审批、拦截危险操作等。</summary>
    public Func<BeforeToolCallContext, CancellationToken, ValueTask<ToolCallDecision>>? BeforeToolCall { get; set; }

    /// <summary>
    /// 每批用户输入（新一次运行、followUp）追加之前调用：返回需要先追加的上下文消息
    /// （例如系统提示某节变化后的 <see cref="SystemMessage"/> 补丁）。历史只追加，不改写开头的系统提示。
    /// </summary>
    public Func<CancellationToken, ValueTask<IReadOnlyList<Message>>>? ContextUpdates { get; set; }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _runCts is not null;
            }
        }
    }

    public IReadOnlyList<Message> Messages
    {
        get
        {
            lock (_gate)
            {
                return _messages.ToArray();
            }
        }
    }

    /// <summary>恢复会话时载入既有历史（不触发事件）。</summary>
    public void LoadHistory(IEnumerable<Message> messages)
    {
        lock (_gate)
        {
            if (_runCts is not null)
            {
                throw new InvalidOperationException("Cannot load history while running.");
            }
            _messages.Clear();
            _messages.AddRange(messages);
        }
    }

    public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> listener)
    {
        lock (_gate)
        {
            _listeners.Add(listener);
        }
        return new Unsubscriber(() =>
        {
            lock (_gate)
            {
                _listeners.Remove(listener);
            }
        });
    }

    /// <summary>开始一次运行。运行中请改用 <see cref="Steer"/> 或 <see cref="FollowUp"/>。</summary>
    public Task PromptAsync(UserMessage message)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_runCts is not null)
            {
                throw new InvalidOperationException("Agent is already running; use Steer() or FollowUp().");
            }
            cts = _runCts = new CancellationTokenSource();
            _running = Task.Run(() => RunAsync([message], cts.Token));
        }
        return _running;
    }

    public void Steer(UserMessage message)
    {
        lock (_gate)
        {
            _steering.Enqueue(message);
        }
    }

    public void FollowUp(UserMessage message)
    {
        lock (_gate)
        {
            _followUps.Enqueue(message);
        }
    }

    public void Abort()
    {
        lock (_gate)
        {
            _runCts?.Cancel();
        }
    }

    /// <summary>等到没有运行为止（包括收尾时因排队消息自动开始的后续运行）。</summary>
    public async Task WaitForIdleAsync()
    {
        while (true)
        {
            Task running;
            lock (_gate)
            {
                running = _running;
            }
            await running;
            lock (_gate)
            {
                if (ReferenceEquals(running, _running) && _runCts is null)
                {
                    return;
                }
            }
        }
    }

    private async Task RunAsync(IReadOnlyList<UserMessage> prompts, CancellationToken ct)
    {
        var newMessages = new List<Message>();
        try
        {
            await EmitAsync(new AgentStartEvent(), ct);
            IReadOnlyList<Message> pending = await WithContextUpdatesAsync(prompts, ct);

            while (true)
            {
                var stopped = false;
                while (true)
                {
                    foreach (var prompt in pending)
                    {
                        await AppendAsync(prompt, newMessages, ct);
                    }
                    pending = [];

                    await EmitAsync(new TurnStartEvent(), ct);
                    var assistant = await StreamAssistantAsync(newMessages, ct);
                    if (assistant.StopReason is StopReason.Error or StopReason.Aborted)
                    {
                        await EmitAsync(new TurnEndEvent(assistant, []), CancellationToken.None);
                        stopped = true;
                        break;
                    }

                    var toolCalls = assistant.Content.OfType<ToolCallContent>().ToList();
                    var results = toolCalls.Count == 0
                        ? []
                        : await ExecuteToolCallsAsync(assistant, toolCalls, newMessages, ct);
                    await EmitAsync(new TurnEndEvent(assistant, results), ct);

                    pending = Dequeue(_steering, SteeringMode);
                    if (pending.Count > 0 || toolCalls.Count > 0)
                    {
                        continue;
                    }
                    break;
                }

                if (stopped)
                {
                    break;
                }
                var followUps = Dequeue(_followUps, FollowUpMode);
                if (followUps.Count == 0)
                {
                    break;
                }
                pending = await WithContextUpdatesAsync(followUps, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 中止：已产生的部分消息已追加，直接结束。
        }
        finally
        {
            await EmitAsync(new AgentEndEvent(newMessages), CancellationToken.None);
            lock (_gate)
            {
                _runCts?.Dispose();
                _runCts = null;
                // 运行收尾期间排进队列的消息（例如后台任务完成通知）不能丢：未被中止时自动开始下一次运行。
                if (!ct.IsCancellationRequested && (_steering.Count > 0 || _followUps.Count > 0))
                {
                    var queued = _steering.Concat(_followUps).ToArray();
                    _steering.Clear();
                    _followUps.Clear();
                    var next = _runCts = new CancellationTokenSource();
                    _running = Task.Run(() => RunAsync(queued, next.Token));
                }
            }
        }
    }

    private async Task<IReadOnlyList<Message>> WithContextUpdatesAsync(IReadOnlyList<UserMessage> prompts, CancellationToken ct)
    {
        if (ContextUpdates is not { } updates)
        {
            return prompts;
        }
        var context = await updates(ct);
        return context.Count == 0 ? prompts : [.. context, .. prompts];
    }

    private async Task<AssistantMessage> StreamAssistantAsync(List<Message> newMessages, CancellationToken ct)
    {
        var messageId = NewId();
        var messages = Messages.Select(m => m is UserMessage user ? user.ForModel(Model.SupportsImages) : m).ToArray();
        var context = new LlmContext(SystemPrompt, messages, Tools.Select(t => t.ToDefinition()).ToArray());
        var options = new StreamOptions
        {
            ApiKey = GetApiKey?.Invoke(Model.Provider),
            ThinkingLevel = ThinkingLevel,
            FastMode = FastMode,
            SessionId = SessionId,
        };

        await EmitAsync(new MessageStartEvent(messageId, null), ct);
        var stream = _apis.Stream(Model, context, options, ct);
        // 不把 ct 传给枚举：流在中止时会以 ErrorEvent 收尾，需要读完它。
        await foreach (var e in stream.WithCancellation(CancellationToken.None))
        {
            if (e is not DoneEvent and not ErrorEvent)
            {
                await EmitAsync(new MessageUpdateEvent(messageId, e), CancellationToken.None);
            }
        }
        var message = await stream.Result;
        await AppendAsync(message, newMessages, CancellationToken.None, messageId);
        return message;
    }

    private async Task<IReadOnlyList<ToolResultMessage>> ExecuteToolCallsAsync(
        AssistantMessage assistant,
        List<ToolCallContent> toolCalls,
        List<Message> newMessages,
        CancellationToken ct)
    {
        var prepared = new List<(ToolCallContent Call, IAgentTool? Tool, JsonElement Args, AgentToolResult? Immediate)>();

        // 预检按源顺序串行执行（审批弹窗不应并发）。
        foreach (var call in toolCalls)
        {
            await EmitAsync(new ToolExecutionStartEvent(call.Id, call.Name, call.ArgumentsJson), ct);
            prepared.Add(await PrepareAsync(assistant, call, ct));
        }

        AgentToolResult[] results;
        if (prepared.Any(p => p.Immediate is null && p.Tool?.Concurrency == ToolConcurrency.Exclusive))
        {
            // 批内有独占工具：按源顺序串行，避免与其它调用交错（例如命令依赖前一次写入）。
            results = new AgentToolResult[prepared.Count];
            for (var i = 0; i < prepared.Count; i++)
            {
                var p = prepared[i];
                results[i] = await RunToolAsync(p.Call, p.Tool, p.Args, p.Immediate, ct);
            }
        }
        else
        {
            results = await Task.WhenAll(prepared.Select(p => RunToolAsync(p.Call, p.Tool, p.Args, p.Immediate, ct)));
        }

        var messages = new List<ToolResultMessage>();
        for (var i = 0; i < prepared.Count; i++)
        {
            var call = prepared[i].Call;
            var message = new ToolResultMessage(call.Id, call.Name, results[i].Content)
            {
                IsError = results[i].IsError,
                Details = results[i].Details,
            };
            messages.Add(message);
            await AppendAsync(message, newMessages, CancellationToken.None);
        }
        return messages;
    }

    private async Task<(ToolCallContent, IAgentTool?, JsonElement, AgentToolResult?)> PrepareAsync(
        AssistantMessage assistant, ToolCallContent call, CancellationToken ct)
    {
        if (assistant.StopReason == StopReason.Length)
        {
            return (call, null, default, AgentToolResult.Error("The response was truncated (max tokens); re-issue the tool call."));
        }
        var tool = Tools.FirstOrDefault(t => t.Name == call.Name);
        if (tool is null)
        {
            return (call, null, default, AgentToolResult.Error($"Tool '{call.Name}' not found."));
        }
        if (call.ArgumentsError is not null)
        {
            return (call, tool, default, AgentToolResult.Error($"Invalid tool arguments: {call.ArgumentsError}"));
        }

        JsonElement args;
        using (var doc = JsonDocument.Parse(call.ArgumentsJson))
        {
            args = doc.RootElement.Clone();
        }

        if (BeforeToolCall is { } hook)
        {
            ToolPermission permission;
            try
            {
                permission = tool.DescribePermission(args);
            }
            catch (Exception ex)
            {
                return (call, tool, args, AgentToolResult.Error($"Invalid tool arguments: {ex.Message}"));
            }
            var decision = await hook(new BeforeToolCallContext(tool, call, args, permission), ct);
            if (decision.Block)
            {
                return (call, tool, args, AgentToolResult.Error(decision.Reason ?? "Tool execution was blocked."));
            }
        }
        return (call, tool, args, null);
    }

    private async Task<AgentToolResult> RunToolAsync(
        ToolCallContent call, IAgentTool? tool, JsonElement args, AgentToolResult? immediate, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        AgentToolResult result;
        if (immediate is not null || tool is null)
        {
            result = immediate ?? AgentToolResult.Error("Tool unavailable.");
        }
        else if (ct.IsCancellationRequested)
        {
            result = AgentToolResult.Error("Operation aborted.");
        }
        else
        {
            var progress = new OrderedProgress(partial => EmitAsync(new ToolExecutionUpdateEvent(call.Id, call.Name, partial), CancellationToken.None));
            try
            {
                result = await tool.ExecuteAsync(new ToolInvocation(call.Id, args, progress), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                result = AgentToolResult.Error("Operation aborted.");
            }
            catch (Exception ex)
            {
                result = AgentToolResult.Error(ex.Message);
            }
            // 进度事件必须先于结束事件送达。
            await progress.DrainAsync();
        }
        await EmitAsync(new ToolExecutionEndEvent(call.Id, call.Name, result, Stopwatch.GetElapsedTime(started)), CancellationToken.None);
        return result;
    }

    private async Task AppendAsync(Message message, List<Message> newMessages, CancellationToken ct, string? messageId = null)
    {
        var id = messageId ?? NewId();
        if (messageId is null)
        {
            await EmitAsync(new MessageStartEvent(id, message), ct);
        }
        lock (_gate)
        {
            _messages.Add(message);
        }
        newMessages.Add(message);
        await EmitAsync(new MessageEndEvent(id, message), CancellationToken.None);
    }

    private IReadOnlyList<UserMessage> Dequeue(Queue<UserMessage> queue, QueueMode mode)
    {
        lock (_gate)
        {
            if (queue.Count == 0)
            {
                return [];
            }
            if (mode == QueueMode.OneAtATime)
            {
                return [queue.Dequeue()];
            }
            var all = queue.ToArray();
            queue.Clear();
            return all;
        }
    }

    private async ValueTask EmitAsync(AgentEvent e, CancellationToken ct)
    {
        Func<AgentEvent, CancellationToken, ValueTask>[] listeners;
        lock (_gate)
        {
            listeners = _listeners.ToArray();
        }
        foreach (var listener in listeners)
        {
            await listener(e, ct);
        }
    }

    private static string NewId() => Guid.CreateVersion7().ToString("N");

    /// <summary>按报告顺序依次投递进度（<see cref="Progress{T}"/> 经线程池投递，可能乱序）。</summary>
    private sealed class OrderedProgress(Func<AgentToolResult, ValueTask> emit) : IProgress<AgentToolResult>
    {
        private readonly Lock _gate = new();
        private Task _tail = Task.CompletedTask;

        public void Report(AgentToolResult value)
        {
            lock (_gate)
            {
                _tail = _tail.ContinueWith(_ => emit(value).AsTask(), CancellationToken.None,
                    TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            }
        }

        public Task DrainAsync()
        {
            lock (_gate)
            {
                return _tail.ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
