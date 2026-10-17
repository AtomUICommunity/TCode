using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TCode.Ai.Messages;
using TCode.Ai.Models;
using TCode.Rpc;
using TCode.Runtimes.Json;

namespace TCode.Runtimes.Claude;

/// <summary>
/// 一个 Claude Agent 会话 = 一个 <c>claude -p</c> stream-json 进程（规格 docs/protocols/claude-code-stream-json.md）。
/// <list type="bullet">
/// <item>stdin / stdout 都是 NDJSON；stdin 在会话期间保持打开，否则审批请求会在 CLI 侧失败（§3.4）。</item>
/// <item>对话帧与 CLI 的控制请求按到达顺序进入同一个队列，由单个消费者处理；对 TCode 请求的应答在读循环中直接完成。</item>
/// <item>以 stream_event 为主产生增量，完整 assistant 帧只用于校正与补齐（§14.2）；轮次只以 result 帧结束（§9.2）。</item>
/// </list>
/// </summary>
internal sealed class ClaudeSession : IRuntimeSession
{
    private static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StopSettle = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan InterruptGrace = TimeSpan.FromSeconds(10);
    private const string DenyMessage = "用户拒绝了这次操作。";

    private readonly Stream _stdout;
    private readonly Stream _stdin;
    private readonly IRuntimeHost _host;
    private readonly IAsyncDisposable? _process;
    private readonly Func<string>? _describeExit;
    private readonly Func<string> _newUuid;
    private readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<Func<ValueTask>> _queue = Channel.CreateUnbounded<Func<ValueTask>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<string?>? _startupFailure;
    private Task _reader = Task.CompletedTask;
    private Task _writer = Task.CompletedTask;
    private Task _consumer = Task.CompletedTask;
    private int _interrupts;
    private string? _sessionId;
    private string? _appliedModel;
    private string? _appliedEffort;
    private Turn? _turn;
    private bool _sawInit;

    public ClaudeSession(Stream stdout, Stream stdin, IRuntimeHost host, string? sessionId, IAsyncDisposable? process = null,
        Func<string>? describeExit = null, Func<string>? newUuid = null)
    {
        _stdout = stdout;
        _stdin = stdin;
        _host = host;
        _sessionId = sessionId;
        _process = process;
        _describeExit = describeExit;
        _newUuid = newUuid ?? (() => Guid.NewGuid().ToString());
    }

    public string? NativeSessionId => _sessionId;

    /// <summary>initialize 应答（模型、命令、账号）。</summary>
    public JsonElement InitializeResponse { get; private set; }

    /// <summary>启动时（--model / --effort）已生效的设置，之后只在变化时在线修改。</summary>
    public void SetInitialOptions(string? model, string? effort, string? permissionMode = null)
    {
        _appliedModel = model;
        _appliedEffort = effort;
        _appliedPermissionMode = permissionMode;
    }

    private string? _appliedPermissionMode;

    // ---------------- 启动 ----------------

    /// <summary>发送 initialize 并等待应答；启动阶段的错误 result 或进程退出会让等待立即失败（§5.1、附录 e2）。</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        _startupFailure = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _writer = Task.Run(WriteLoopAsync);
        _reader = Task.Run(ReadLoopAsync);
        _consumer = Task.Run(ConsumeAsync);

        var init = SendControlAsync("tcode_init_1", w =>
        {
            w.WriteString("subtype", "initialize");
            w.WriteNull("hooks");
        }, InitializeTimeout, ct);
        var first = await Task.WhenAny(init, _startupFailure.Task);
        if (first != init)
        {
            var reason = await _startupFailure.Task;
            throw new InvalidOperationException(reason ?? _describeExit?.Invoke() ?? "Claude exited during startup.");
        }
        InitializeResponse = await init;
        _startupFailure.TrySetResult(null);
        _host.OnCommands(ParseCommands(InitializeResponse));
    }

    /// <summary>initialize 应答中的 commands：[{name, description, argumentHint}]（规格附录 e1）。</summary>
    internal static IReadOnlyList<RuntimeCommand> ParseCommands(JsonElement initialize) =>
        initialize.Arr("commands").Select(c => c.Str("name") is { Length: > 0 } name
                ? new RuntimeCommand(name.TrimStart('/'), c.Str("description") is { Length: > 0 } d ? d : null,
                    c.Str("argumentHint") is { Length: > 0 } hint ? hint : null)
                : null)
            .OfType<RuntimeCommand>().ToList();

    // ---------------- 轮次 ----------------

    public async Task RunTurnAsync(UserMessage message, TurnOptions options, TurnWriter writer, CancellationToken cancellationToken)
    {
        await ApplyOptionsAsync(options, cancellationToken);
        var turn = new Turn(writer);
        await RunOnQueueAsync(() =>
        {
            _turn = turn;
            return ValueTask.CompletedTask;
        });
        WriteUserMessage(message, priority: null);

        using (cancellationToken.Register(() => Enqueue(() => InterruptAsync(turn))))
        {
            var done = turn.Completed.Task;
            await Task.WhenAny(done, _exited.Task);
            if (!done.IsCompleted)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    await Task.WhenAny(done, Task.Delay(InterruptGrace));
                }
                if (!done.IsCompleted)
                {
                    var error = _exited.Task.IsCompleted
                        ? _describeExit?.Invoke() ?? "Claude exited unexpectedly."
                        : $"Claude did not stop within {InterruptGrace.TotalSeconds:0} seconds after interrupt.";
                    await RunOnQueueAsync(() => FinishAsync(turn, cancellationToken.IsCancellationRequested ? StopReason.Aborted : StopReason.Error, error));
                    // 进程不可用：交给 ExternalAgent 丢弃，下一次提问用 --resume 恢复
                    throw new InvalidOperationException(error);
                }
            }
            await done;
        }
    }

    /// <summary>运行中追加消息：并入当前轮次的下一个步骤边界（priority:"next"，与当前轮次共用一个 result）。</summary>
    public ValueTask<bool> SteerAsync(UserMessage message, CancellationToken cancellationToken)
    {
        if (_turn is not { Done: false } || _exited.Task.IsCompleted)
        {
            return ValueTask.FromResult(false);
        }
        WriteUserMessage(message, "next");
        return ValueTask.FromResult(true);
    }

    /// <summary>在线修改权限模式、模型与 effort（§5.3–§5.5）；模型与 effort 失败只忽略，本轮沿用当前设置。</summary>
    private async Task ApplyOptionsAsync(TurnOptions options, CancellationToken ct)
    {
        var permission = ClaudeRuntime.PermissionModeFor(options);
        if (permission != _appliedPermissionMode)
        {
            // 权限切换失败时不能静默沿用旧模式（用户以为已经收紧或放开），直接报错
            await SendControlAsync(NextId("mode"), w =>
            {
                w.WriteString("subtype", "set_permission_mode");
                w.WriteString("mode", permission);
            }, ControlTimeout, ct);
            _appliedPermissionMode = permission;
        }
        var model = options.Model.Id;
        if (model != _appliedModel)
        {
            try
            {
                await SendControlAsync(NextId("model"), w =>
                {
                    w.WriteString("subtype", "set_model");
                    w.WriteString("model", model);
                }, ControlTimeout, ct);
                _appliedModel = model;
            }
            catch (InvalidOperationException)
            {
            }
        }
        var effort = ClaudeRuntime.EffortFor(options.Model, options.ThinkingLevel);
        if (effort != _appliedEffort)
        {
            try
            {
                await SendControlAsync(NextId("settings"), w =>
                {
                    w.WriteString("subtype", "apply_flag_settings");
                    w.WriteStartObject("settings");
                    if (effort is null)
                    {
                        w.WriteNull("effortLevel");
                    }
                    else
                    {
                        w.WriteString("effortLevel", effort);
                    }
                    w.WriteEndObject();
                }, ControlTimeout, ct);
                _appliedEffort = effort;
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private int _controlSeq;

    private string NextId(string purpose) => $"tcode_{purpose}_{Interlocked.Increment(ref _controlSeq)}";

    private void WriteUserMessage(UserMessage message, string? priority) => Write(w =>
    {
        w.WriteString("type", "user");
        w.WriteStartObject("message");
        w.WriteString("role", "user");
        w.WriteStartArray("content");
        // 图片放在文本之前（§7.2）
        foreach (var image in message.Content.OfType<ImageContent>())
        {
            w.WriteStartObject();
            w.WriteString("type", "image");
            w.WriteStartObject("source");
            w.WriteString("type", "base64");
            w.WriteString("media_type", image.MimeType);
            w.WriteString("data", image.Data);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        foreach (var text in message.Content.OfType<TextContent>())
        {
            w.WriteStartObject();
            w.WriteString("type", "text");
            w.WriteString("text", text.Text);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteNull("parent_tool_use_id");
        w.WriteString("uuid", _newUuid());
        if (priority is not null)
        {
            w.WriteString("priority", priority);
        }
    });

    /// <summary>
    /// 中断（§10.1）：发 interrupt（cancel_queued），关闭等待中的审批（CLI 会用 control_cancel_request 撤回）；
    /// 应答后 3 秒仍没有 result 就视为已结算。
    /// </summary>
    private ValueTask InterruptAsync(Turn turn)
    {
        if (turn.Done || turn.Stopping)
        {
            return ValueTask.CompletedTask;
        }
        turn.Stopping = true;
        turn.ApprovalScope.Cancel();
        var request = SendControlAsync($"tcode_int_{Interlocked.Increment(ref _interrupts)}", w =>
        {
            w.WriteString("subtype", "interrupt");
            w.WriteBoolean("cancel_queued", true);
        }, ControlTimeout, CancellationToken.None);
        _ = request.ContinueWith(async _ =>
        {
            await Task.Delay(StopSettle);
            Enqueue(() => FinishAsync(turn, StopReason.Aborted, "Request aborted"));
        }, TaskScheduler.Default);
        return ValueTask.CompletedTask;
    }

    // ---------------- 帧处理（消费者线程） ----------------

    private async ValueTask HandleFrameAsync(JsonElement frame)
    {
        var turn = _turn;
        switch (frame.Str("type"))
        {
            case "system":
                OnSystem(frame);
                break;
            case "stream_event" when turn is not null && frame.Prop("parent_tool_use_id") is null:
                await OnStreamEventAsync(turn, frame.Obj("event") ?? default);
                break;
            case "assistant" when turn is not null && frame.Prop("parent_tool_use_id") is null:
                await OnAssistantAsync(turn, frame);
                break;
            case "user" when turn is not null && frame.Prop("parent_tool_use_id") is null && !frame.Bool("isReplay"):
                await OnToolResultsAsync(turn, frame);
                break;
            case "tool_progress" when turn is not null && frame.Str("tool_use_id") is { } progressId && turn.Tools.TryGetValue(progressId, out var tool):
                await turn.Writer.ToolStartedAsync(tool.Id, tool.Name, tool.ArgumentsJson);
                break;
            case "result":
                await OnResultAsync(turn, frame);
                break;
            case "control_request":
                OnControlRequest(frame);
                break;
            case "control_cancel_request":
                if (frame.Str("request_id") is { } cancelled && turn is not null && turn.Approvals.Remove(cancelled, out var approval))
                {
                    approval.Cancel(); // 撤回的请求不再应答（§4.3）
                }
                break;
            // command_lifecycle、rate_limit_event、keep_alive 等首版忽略；未知 type 一律忽略（§3.1）
        }
    }

    private void OnSystem(JsonElement frame)
    {
        if (frame.Str("subtype") != "init" || frame.Str("session_id") is not { } sessionId)
        {
            return;
        }
        // 每个轮次都会重发 init：只有第一次建立会话，之后只刷新元数据（§8.1.1）
        if (!_sawInit || sessionId != _sessionId)
        {
            _sawInit = true;
            _sessionId = sessionId;
            _host.OnNativeSessionId(sessionId);
        }
        if (_turn is { } turn && frame.Str("model") is { } model && model != turn.Writer.Model.Id)
        {
            // 记录实际模型（别名解析后的 ID），但保持 Provider 不变
            turn.ActualModel = model;
        }
    }

    private async ValueTask OnStreamEventAsync(Turn turn, JsonElement e)
    {
        switch (e.Str("type"))
        {
            case "message_start":
                turn.StreamedMessages.Add(e.Obj("message")?.Str("id") ?? "");
                turn.CurrentMessage = e.Obj("message")?.Str("id");
                turn.Blocks.Clear();
                break;
            case "content_block_start" when e.Long("index") is { } index && e.Obj("content_block") is { } block:
                switch (block.Str("type"))
                {
                    case "text":
                        turn.Blocks[(int)index] = new Block(BlockKind.Text, await turn.Writer.BeginBlockAsync(BlockKind.Text));
                        break;
                    case "thinking":
                        turn.Blocks[(int)index] = new Block(BlockKind.Thinking, await turn.Writer.BeginBlockAsync(BlockKind.Thinking));
                        break;
                    case "tool_use" or "server_tool_use" or "mcp_tool_use" when block.Str("id") is { } id && block.Str("name") is { } name:
                        if (!turn.Writer.IsToolCallDeclared(id))
                        {
                            turn.Blocks[(int)index] = new Block(BlockKind.ToolCall, await turn.Writer.BeginBlockAsync(BlockKind.ToolCall, id, name)) { ToolId = id };
                            turn.Tools[id] = new ToolState(id, name);
                        }
                        break;
                }
                break;
            case "content_block_delta" when e.Long("index") is { } index && turn.Blocks.TryGetValue((int)index, out var target) && e.Obj("delta") is { } delta:
                var piece = delta.Str("type") switch
                {
                    "text_delta" => delta.Str("text"),
                    "thinking_delta" => delta.Str("thinking"),
                    "input_json_delta" => delta.Str("partial_json"),
                    _ => null, // signature_delta、citations_delta 不显示
                };
                if (!string.IsNullOrEmpty(piece))
                {
                    await turn.Writer.AppendAsync(target.Handle, piece);
                    if (target.ToolId is not null)
                    {
                        target.ArgumentsBuffer.Append(piece);
                    }
                }
                break;
            case "content_block_stop" when e.Long("index") is { } index && turn.Blocks.TryGetValue((int)index, out var stopped):
                await EndStreamBlockAsync(turn, stopped, null);
                break;
            case "message_stop":
                // 被切断的工具块没有 content_block_stop：以已收到的参数结束（§8.2）
                foreach (var open in turn.Blocks.Values.Where(b => !b.Ended).ToArray())
                {
                    await EndStreamBlockAsync(turn, open, null);
                }
                turn.CurrentMessage = null;
                break;
        }
    }

    private static async ValueTask EndStreamBlockAsync(Turn turn, Block block, string? finalText)
    {
        if (block.Ended)
        {
            return;
        }
        block.Ended = true;
        if (block.ToolId is { } toolId && turn.Tools.TryGetValue(toolId, out var tool))
        {
            var args = finalText ?? (block.ArgumentsBuffer.Length == 0 ? "{}" : block.ArgumentsBuffer.ToString());
            tool.ArgumentsJson = IsJsonObject(args) ? args : "{}";
            await turn.Writer.EndBlockAsync(block.Handle, tool.ArgumentsJson);
            return;
        }
        await turn.Writer.EndBlockAsync(block.Handle, finalText);
    }

    /// <summary>
    /// 完整 assistant 帧：已有流事件的消息只用来校正内容与提供工具的权威参数；
    /// 没有流事件的消息（合成错误、旧版本）由它合成完整内容（§14.2）。
    /// </summary>
    private async ValueTask OnAssistantAsync(Turn turn, JsonElement frame)
    {
        var message = frame.Obj("message") ?? default;
        var id = message.Str("id") ?? "";
        if (frame.Str("error") is { } errorKind)
        {
            turn.AssistantError = errorKind;
        }
        var streamed = turn.StreamedMessages.Contains(id);
        foreach (var block in message.Arr("content"))
        {
            var type = block.Str("type");
            if (streamed)
            {
                switch (type)
                {
                    case "tool_use" when block.Str("id") is { } toolId:
                        var open = turn.Blocks.Values.FirstOrDefault(b => b.ToolId == toolId);
                        if (open is not null)
                        {
                            await EndStreamBlockAsync(turn, open, block.Prop("input")?.GetRawText() ?? "{}");
                        }
                        break;
                    case "text" or "thinking":
                        var kind = type == "text" ? BlockKind.Text : BlockKind.Thinking;
                        var current = turn.Blocks.Values.Where(b => b.Kind == kind && !b.Ended).OrderBy(b => b.Handle).FirstOrDefault();
                        if (current is not null)
                        {
                            await EndStreamBlockAsync(turn, current, block.Str(type == "text" ? "text" : "thinking"));
                        }
                        break;
                }
                continue;
            }
            switch (type)
            {
                case "text" when block.Str("text") is { Length: > 0 } text:
                    await turn.Writer.TextAsync(text);
                    break;
                case "thinking" when block.Str("thinking") is { Length: > 0 } thinking:
                    await turn.Writer.ThinkingAsync(thinking);
                    break;
                case "tool_use" when block.Str("id") is { } toolId && block.Str("name") is { } name:
                    var args = block.Prop("input")?.GetRawText() ?? "{}";
                    turn.Tools.TryAdd(toolId, new ToolState(toolId, name) { ArgumentsJson = args });
                    await turn.Writer.ToolCallAsync(toolId, name, args);
                    break;
            }
        }
    }

    /// <summary>主会话 user 帧中的 tool_result：结束对应工具并保留输出（§8.4）。合成标记与回显不产生事件。</summary>
    private static async ValueTask OnToolResultsAsync(Turn turn, JsonElement frame)
    {
        foreach (var block in (frame.Obj("message") ?? default).Arr("content"))
        {
            if (block.Str("type") != "tool_result" || block.Str("tool_use_id") is not { } id)
            {
                continue;
            }
            turn.Tools.TryGetValue(id, out var tool);
            if (tool is { Done: true })
            {
                continue;
            }
            var text = block.Prop("content") switch
            {
                { ValueKind: JsonValueKind.String } s => s.GetString() ?? "",
                { ValueKind: JsonValueKind.Array } a => string.Join("\n", a.EnumerateArray().Where(c => c.Str("type") == "text")
                    .Select(c => c.Str("text")).OfType<string>()),
                _ => "",
            };
            var details = frame.Prop("tool_use_result") is { } structured ? structured.GetRawText() : null;
            if (tool is not null)
            {
                tool.Done = true;
            }
            await turn.Writer.ToolCompletedAsync(id, tool?.Name ?? "tool", text, block.Bool("is_error"), tool?.ArgumentsJson ?? "{}", details);
            // 工具结果关闭了草稿，之后的流事件块属于新消息
        }
    }

    /// <summary>result 帧结束轮次（§14.3）。</summary>
    private async ValueTask OnResultAsync(Turn? turn, JsonElement frame)
    {
        if (_startupFailure is { Task.IsCompleted: false } startup)
        {
            // 启动阶段的错误 result（例如恢复不存在的会话）：initialize 等待立即失败（附录 e2）
            startup.TrySetResult(ErrorText(frame) ?? "Claude failed to start.");
            return;
        }
        if (turn is null || turn.Done)
        {
            return; // 后台唤醒轮次或已结算的停止：首版不展示
        }
        if (frame.Obj("usage") is { } usage)
        {
            turn.Writer.SetUsage(new Usage(usage.Long("input_tokens") ?? 0, usage.Long("output_tokens") ?? 0,
                usage.Long("cache_read_input_tokens") ?? 0, usage.Long("cache_creation_input_tokens") ?? 0));
        }
        var terminal = frame.Str("terminal_reason");
        var subtype = frame.Str("subtype");
        var (reason, message) = (terminal is "aborted_streaming" or "aborted_tools" || turn.Stopping) switch
        {
            true => (StopReason.Aborted, "Request aborted"),
            false when subtype == "success" && !frame.Bool("is_error") => (StopReason.Stop, (string?)null),
            _ => (StopReason.Error, ErrorText(frame) ?? "Claude run failed."),
        };
        await FinishAsync(turn, reason, message);
    }

    /// <summary>错误文本：errors（去掉内部诊断）→ result → 按 subtype 的固定文案 → HTTP 状态（§13）。</summary>
    private static string? ErrorText(JsonElement frame)
    {
        var errors = frame.Arr("errors").Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null)
            .Where(e => !string.IsNullOrWhiteSpace(e) && !e.StartsWith("[ede_diagnostic]", StringComparison.Ordinal))
            .ToArray();
        if (errors.Length > 0)
        {
            return string.Join("\n", errors);
        }
        if (frame.Str("result") is { Length: > 0 } result)
        {
            return result;
        }
        return frame.Str("subtype") switch
        {
            "error_max_turns" => "Claude reached the maximum number of turns.",
            "error_max_budget_usd" => "Claude reached the budget limit.",
            "error_max_structured_output_retries" => "Claude could not produce valid structured output.",
            _ => frame.Long("api_error_status") is { } status ? $"API error (HTTP {status})" : null,
        };
    }

    private async ValueTask FinishAsync(Turn turn, StopReason reason, string? message)
    {
        if (turn.Done)
        {
            return;
        }
        turn.Done = true;
        turn.ApprovalScope.Cancel();
        foreach (var open in turn.Blocks.Values.Where(b => !b.Ended).ToArray())
        {
            await EndStreamBlockAsync(turn, open, null);
        }
        foreach (var tool in turn.Tools.Values.Where(t => !t.Done && turn.Writer.IsToolCallDeclared(t.Id)))
        {
            tool.Done = true;
            await turn.Writer.ToolCompletedAsync(tool.Id, tool.Name, "Operation aborted.", true, tool.ArgumentsJson);
        }
        await turn.Writer.CompleteAsync(reason, message);
        _turn = null;
        turn.Completed.TrySetResult();
    }

    // ---------------- CLI 的控制请求 ----------------

    private void OnControlRequest(JsonElement frame)
    {
        var requestId = frame.Str("request_id");
        var request = frame.Obj("request") ?? default;
        if (requestId is null)
        {
            return;
        }
        switch (request.Str("subtype"))
        {
            case "can_use_tool":
                BeginApproval(requestId, request);
                break;
            case "request_user_dialog":
                break; // 只发给声明过对应 kind 的客户端；收到未声明的 kind 不应答（§4.4）
            default:
                Respond(requestId, error: $"Unsupported control request subtype: {request.Str("subtype")}");
                break;
        }
    }

    /// <summary>
    /// can_use_tool（§6.1）：交给 TCode 审批，后台等待，不阻塞帧处理。
    /// 被 control_cancel_request 撤回或轮次结束时不应答；其他失败一律 deny。
    /// </summary>
    private void BeginApproval(string requestId, JsonElement request)
    {
        var toolName = request.Str("tool_name") ?? "tool";
        var toolUseId = request.Str("tool_use_id") ?? "";
        var input = request.Obj("input") ?? JsonBuilder.Parse("{}");
        var turn = _turn;
        if (turn is null || turn.Done || turn.Stopping)
        {
            RespondDeny(requestId, toolUseId, "The turn has ended.");
            return;
        }
        if (toolName == "AskUserQuestion")
        {
            // 提问不是审批：问答面板（input/request）完成之前，请模型自行判断
            RespondDeny(requestId, toolUseId, "TCode cannot show interactive questions yet. Continue with your best judgment and state your assumptions.");
            return;
        }
        var scope = CancellationTokenSource.CreateLinkedTokenSource(turn.ApprovalScope.Token);
        turn.Approvals[requestId] = scope;
        var suggestions = request.Prop("permission_suggestions") is { ValueKind: JsonValueKind.Array } s ? s.Clone() : (JsonElement?)null;
        var canRemember = !request.Bool("suppress_always_allow_rule");
        var title = request.Str("description") ?? request.Str("title") ?? request.Str("display_name");
        var approval = new RuntimeApprovalRequest(toolUseId, toolName, input.GetRawText(), KindOf(toolName), title,
            StripAnsi(request.Str("decision_reason")), canRemember);
        _ = DecideAsync(turn, requestId, toolUseId, input, suggestions, approval, scope);
    }

    private async Task DecideAsync(Turn turn, string requestId, string toolUseId, JsonElement input, JsonElement? suggestions,
        RuntimeApprovalRequest approval, CancellationTokenSource scope)
    {
        ApprovalDecision decision;
        try
        {
            decision = await _host.RequestApprovalAsync(approval, scope.Token);
        }
        catch (Exception) when (scope.IsCancellationRequested)
        {
            return; // 撤回或轮次结束：不应答
        }
        catch (Exception ex)
        {
            decision = ApprovalDecision.Deny($"Approval failed: {ex.Message}");
        }
        if (decision.Outcome == ApprovalOutcome.Cancel)
        {
            // 轮次被中断时宿主也会返回 Cancel：先让队列处理已排入的中断，再判断审批是否已被撤销
            await RunOnQueueAsync(() => ValueTask.CompletedTask);
        }
        if (scope.IsCancellationRequested)
        {
            return;
        }
        Enqueue(() =>
        {
            turn.Approvals.Remove(requestId);
            return ValueTask.CompletedTask;
        });
        if (!decision.IsAllowed)
        {
            RespondDeny(requestId, toolUseId, decision.Message ?? DenyMessage, decision.Outcome == ApprovalOutcome.Cancel);
            return;
        }
        Respond(requestId, w =>
        {
            w.WriteString("behavior", "allow");
            w.WritePropertyName("updatedInput");
            input.WriteTo(w);
            if (decision.Outcome == ApprovalOutcome.AllowForSession)
            {
                WriteSessionPermissions(w, suggestions, approval.ToolName);
            }
            w.WriteString("toolUseID", toolUseId);
            w.WriteString("decisionClassification", decision.Outcome == ApprovalOutcome.AllowForSession ? "user_permanent" : "user_temporary");
        });
        Enqueue(() => turn.Tools.TryGetValue(toolUseId, out var tool)
            ? turn.Writer.ToolStartedAsync(tool.Id, tool.Name, tool.ArgumentsJson)
            : ValueTask.CompletedTask);
    }

    /// <summary>“本会话允许”：复制 CLI 的建议并把 destination 改写为 session，避免写入设置文件（§15）。</summary>
    private static void WriteSessionPermissions(Utf8JsonWriter w, JsonElement? suggestions, string toolName)
    {
        w.WriteStartArray("updatedPermissions");
        if (suggestions is { } list && list.GetArrayLength() > 0)
        {
            foreach (var suggestion in list.EnumerateArray())
            {
                w.WriteStartObject();
                foreach (var property in suggestion.EnumerateObject())
                {
                    if (property.Name != "destination")
                    {
                        property.WriteTo(w);
                    }
                }
                w.WriteString("destination", "session");
                w.WriteEndObject();
            }
        }
        else
        {
            w.WriteStartObject();
            w.WriteString("type", "addRules");
            w.WriteStartArray("rules");
            w.WriteStartObject();
            w.WriteString("toolName", toolName);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteString("behavior", "allow");
            w.WriteString("destination", "session");
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private void RespondDeny(string requestId, string toolUseId, string message, bool interrupt = false) => Respond(requestId, w =>
    {
        w.WriteString("behavior", "deny");
        w.WriteString("message", message);
        if (interrupt)
        {
            w.WriteBoolean("interrupt", true);
        }
        w.WriteString("toolUseID", toolUseId);
        w.WriteString("decisionClassification", "user_reject");
    });

    private void Respond(string requestId, Action<Utf8JsonWriter>? body = null, string? error = null) => Write(w =>
    {
        w.WriteString("type", "control_response");
        w.WriteStartObject("response");
        w.WriteString("subtype", error is null ? "success" : "error");
        w.WriteString("request_id", requestId);
        if (error is not null)
        {
            w.WriteString("error", error);
        }
        else
        {
            w.WriteStartObject("response");
            body?.Invoke(w);
            w.WriteEndObject();
        }
        w.WriteEndObject();
    });

    private static ApprovalKind KindOf(string toolName) => toolName switch
    {
        "Bash" or "BashOutput" or "KillShell" => ApprovalKind.Command,
        "Edit" or "Write" or "MultiEdit" or "NotebookEdit" => ApprovalKind.FileChange,
        _ => ApprovalKind.Tool,
    };

    private static string? StripAnsi(string? text) =>
        text is null ? null : System.Text.RegularExpressions.Regex.Replace(text, @"\x1B\[[0-9;?]*[ -/]*[@-~]", "");

    private static bool IsJsonObject(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // ---------------- TCode 的控制请求 ----------------

    private async Task<JsonElement> SendControlAsync(string requestId, Action<Utf8JsonWriter> request, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;
        Write(w =>
        {
            w.WriteString("type", "control_request");
            w.WriteString("request_id", requestId);
            w.WriteStartObject("request");
            request(w);
            w.WriteEndObject();
        });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        cts.CancelAfter(timeout);
        try
        {
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(_exited.Task.IsCompleted
                ? _describeExit?.Invoke() ?? "Claude exited."
                : $"Claude did not answer '{requestId}' in time.");
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    private void OnControlResponse(JsonElement frame)
    {
        var response = frame.Obj("response") ?? default;
        if (response.Str("request_id") is not { } id || !_pending.TryRemove(id, out var tcs))
        {
            return;
        }
        if (response.Str("subtype") == "error")
        {
            tcs.TrySetException(new InvalidOperationException(response.Str("error") ?? "Control request failed."));
        }
        else
        {
            tcs.TrySetResult(response.Obj("response") ?? JsonBuilder.Parse("{}"));
        }
    }

    // ---------------- 传输 ----------------

    private void Write(Action<Utf8JsonWriter> body)
    {
        var json = JsonBuilder.Object(body);
        _outbox.Writer.TryWrite(Encoding.UTF8.GetBytes(json.GetRawText() + "\n"));
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var line in _outbox.Reader.ReadAllAsync(_lifetime.Token))
            {
                await _stdin.WriteAsync(line, _lifetime.Token);
                await _stdin.FlushAsync(_lifetime.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // EPIPE：进程已退出，由读循环处理
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            var lines = new NdjsonLineReader(_stdout);
            while (await lines.ReadLineAsync(_lifetime.Token) is { } line)
            {
                var span = line.AsSpan().TrimStart(" \t\r"u8);
                if (span.Length == 0 || span[0] != (byte)'{')
                {
                    continue; // 非 JSON 行（例如调试输出）跳过（§3.1）
                }
                JsonElement frame;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    frame = doc.RootElement.Clone();
                }
                catch (JsonException)
                {
                    continue;
                }
                if (frame.Str("type") == "control_response")
                {
                    OnControlResponse(frame);
                }
                else
                {
                    Enqueue(() => HandleFrameAsync(frame));
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
        finally
        {
            _exited.TrySetResult();
            _startupFailure?.TrySetResult(null);
            foreach (var id in _pending.Keys)
            {
                if (_pending.TryRemove(id, out var tcs))
                {
                    tcs.TrySetException(new InvalidOperationException(_describeExit?.Invoke() ?? "Claude exited."));
                }
            }
        }
    }

    private void Enqueue(Func<ValueTask> work) => _queue.Writer.TryWrite(work);

    private Task RunOnQueueAsync(Func<ValueTask> work)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(async () =>
        {
            try
            {
                await work();
                done.TrySetResult();
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });
        return done.Task;
    }

    private async Task ConsumeAsync()
    {
        await foreach (var work in _queue.Reader.ReadAllAsync())
        {
            try
            {
                await work();
            }
            catch (Exception)
            {
                // 单帧处理失败不影响后续（宽松解析）
            }
        }
    }

    /// <summary>拆除（§10.3）：在跑的轮次先中断（最多 2 秒），然后关闭 stdin，由 CliProcess 等待退出并结束进程树。</summary>
    /// <summary>会话结束时删除的临时文件（--mcp-config）。</summary>
    public string? TempFile { get; init; }

    public async ValueTask DisposeAsync()
    {
        if (TempFile is { } temp)
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }
        }
        if (_turn is { Done: false } turn)
        {
            Enqueue(() => InterruptAsync(turn));
            await Task.WhenAny(turn.Completed.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        }
        _outbox.Writer.TryComplete();
        await _writer;
        if (_process is not null)
        {
            await _process.DisposeAsync();
        }
        else
        {
            _stdin.Dispose();
        }
        await _lifetime.CancelAsync();
        await _reader;
        _queue.Writer.TryComplete();
        await _consumer;
    }

    // ---------------- 状态 ----------------

    private sealed class Turn(TurnWriter writer)
    {
        public TurnWriter Writer { get; } = writer;
        public HashSet<string> StreamedMessages { get; } = new(StringComparer.Ordinal);
        public string? CurrentMessage { get; set; }
        public Dictionary<int, Block> Blocks { get; } = [];
        public Dictionary<string, ToolState> Tools { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, CancellationTokenSource> Approvals { get; } = new(StringComparer.Ordinal);
        public CancellationTokenSource ApprovalScope { get; } = new();
        public string? AssistantError { get; set; }
        public string? ActualModel { get; set; }
        public bool Stopping { get; set; }
        public bool Done { get; set; }
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Block(BlockKind kind, int handle)
    {
        public BlockKind Kind { get; } = kind;
        public int Handle { get; } = handle;
        public string? ToolId { get; init; }
        public StringBuilder ArgumentsBuffer { get; } = new();
        public bool Ended { get; set; }
    }

    private sealed class ToolState(string id, string name)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string ArgumentsJson { get; set; } = "{}";
        public bool Done { get; set; }
    }
}
