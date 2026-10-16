using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TCode.Ai.Messages;
using TCode.Ai.Models;
using TCode.Rpc;
using TCode.Runtimes.Json;

namespace TCode.Runtimes.Codex;

/// <summary>线程级设置：线程启动时发送一次，之后只在变化时作为 turn/start 的覆盖发送（这些覆盖是 sticky 的）。</summary>
internal sealed record CodexThreadSettings(string? Model, string ApprovalPolicy, bool FullAccess, bool NetworkAccess, string? ServiceTier);

/// <summary>
/// 一个 Codex 会话 = 一个 <c>codex app-server</c> 进程 + 一个主线程（规格 docs/protocols/codex-app-server.md）。
/// <list type="bullet">
/// <item>按行分帧、无 <c>jsonrpc</c> 字段；通知与服务端请求在读循环中按到达顺序进入同一个队列，由单个消费者处理。</item>
/// <item>轮次只以本线程、本轮的 <c>turn/completed</c> 结束（§5.6）；其他线程（子 Agent）的通知被忽略。</item>
/// <item>审批在消费者之外异步等待用户；轮次结束或 <c>serverRequest/resolved</c> 后不再回复（§6.1）。</item>
/// </list>
/// </summary>
internal sealed class CodexSession : IRuntimeSession
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InterruptGrace = TimeSpan.FromSeconds(10);

    private readonly JsonRpcConnection _rpc;
    private readonly IAsyncDisposable? _process;
    private readonly Func<string>? _describeExit;
    private readonly IRuntimeHost _host;
    private readonly string? _developerInstructions;
    private readonly Channel<Func<ValueTask>> _queue = Channel.CreateUnbounded<Func<ValueTask>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _gate = new();
    private Task _consumer = Task.CompletedTask;
    private string? _threadId;
    private CodexThreadSettings? _thread;
    private Turn? _turn;
    private CodexUsage? _baseline;

    public CodexSession(Stream stdout, Stream stdin, IRuntimeHost host, string? developerInstructions,
        IAsyncDisposable? process = null, Func<string>? describeExit = null)
    {
        _host = host;
        _developerInstructions = developerInstructions;
        _process = process;
        _describeExit = describeExit;
        _rpc = new JsonRpcConnection(stdout, stdin, new JsonRpcConnectionOptions
        {
            Framing = JsonRpcFraming.NewlineDelimited,
            WriteVersionField = false,
            SendCancelNotifications = false,
            InlineRequestDispatch = true,
        });
        _rpc.SetFallbackNotificationHandler(OnNotification);
        _rpc.SetFallbackRequestHandler(OnRequestAsync);
    }

    public string? NativeSessionId => _threadId;

    /// <summary>thread/start 响应中实际生效的模型。</summary>
    public string? EffectiveModel => _thread?.Model;

    public static string ClientVersion { get; set; } =
        (typeof(CodexSession).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.0").Split('+')[0];

    // ---------------- 握手 ----------------

    /// <summary>initialize + initialized（§2）。返回 initialize 的结果。</summary>
    public async Task<JsonElement> InitializeAsync(CancellationToken ct)
    {
        _rpc.Start();
        _consumer = Task.Run(ConsumeAsync);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HandshakeTimeout);
        var result = await _rpc.InvokeRawAsync("initialize", JsonBuilder.Object(w =>
        {
            w.WriteStartObject("clientInfo");
            w.WriteString("name", "tcode");
            w.WriteString("title", "TCode");
            w.WriteString("version", ClientVersion);
            w.WriteEndObject();
            w.WriteStartObject("capabilities");
            w.WriteBoolean("experimentalApi", false);
            w.WriteStartArray("optOutNotificationMethods");
            w.WriteStringValue("remoteControl/status/changed");
            w.WriteEndArray();
            w.WriteEndObject();
        }), timeout.Token);
        _rpc.NotifyRaw("initialized", null);
        return result;
    }

    public Task<JsonElement> InvokeAsync(string method, JsonElement? parameters, CancellationToken ct) =>
        _rpc.InvokeRawAsync(method, parameters, ct);

    /// <summary>恢复（thread/resume，失败回退 thread/start）或新建线程（§3）。</summary>
    public async Task StartThreadAsync(string cwd, TurnOptions options, string? resumeThreadId, CancellationToken ct)
    {
        var settings = Settings(options);
        if (resumeThreadId is not null)
        {
            try
            {
                var resumed = await _rpc.InvokeRawAsync("thread/resume", ThreadParams(cwd, settings, resumeThreadId), ct);
                Adopt(resumed, settings, isNew: false);
                return;
            }
            catch (RpcRemoteException)
            {
                // rollout 不存在等：回退为新线程，并上报新的 id（§3.3）
            }
        }
        var started = await _rpc.InvokeRawAsync("thread/start", ThreadParams(cwd, settings, null), ct);
        Adopt(started, settings, isNew: true);
    }

    private void Adopt(JsonElement response, CodexThreadSettings requested, bool isNew)
    {
        var threadId = response.Obj("thread")?.Str("id") ?? throw new InvalidOperationException("Codex returned no thread id.");
        lock (_gate)
        {
            _threadId = threadId;
            // 以服务端实际生效的值为基线（config.toml 可能设置了 service_tier 等）：
            // TCode 的设置与之不同时，turn/start 会显式覆盖（快速模式关闭时发 null 清除）
            var sandbox = response.Obj("sandbox");
            _thread = requested with
            {
                Model = response.Str("model") ?? requested.Model,
                ServiceTier = response.Str("serviceTier"),
                ApprovalPolicy = response.Str("approvalPolicy") ?? requested.ApprovalPolicy,
                FullAccess = sandbox is { } sb ? sb.Str("type") == "dangerFullAccess" : requested.FullAccess,
                NetworkAccess = sandbox is { } net ? net.Bool("networkAccess") : requested.NetworkAccess,
            };
            // 新线程的累计用量从 0 开始；恢复的线程在本轮第一条用量通知时用 total − last 作为基线（§5.5）
            _baseline = isNew ? CodexUsage.Zero : null;
        }
        _host.OnNativeSessionId(threadId);
    }

    private JsonElement ThreadParams(string cwd, CodexThreadSettings settings, string? resumeThreadId) => JsonBuilder.Object(w =>
    {
        if (resumeThreadId is not null)
        {
            w.WriteString("threadId", resumeThreadId);
            w.WriteBoolean("excludeTurns", true);
        }
        w.WriteString("cwd", cwd);
        if (settings.Model is not null)
        {
            w.WriteString("model", settings.Model);
        }
        w.WriteString("approvalPolicy", settings.ApprovalPolicy);
        w.WriteString("sandbox", settings.FullAccess ? "danger-full-access" : "workspace-write");
        if (settings.ServiceTier is not null)
        {
            w.WriteString("serviceTier", settings.ServiceTier);
        }
        if (_developerInstructions is not null)
        {
            w.WriteString("developerInstructions", _developerInstructions);
        }
        w.WriteStartObject("config");
        w.WriteString("model_reasoning_summary", "auto");
        w.WriteEndObject();
    });

    /// <summary>
    /// 权限档位 → Codex 预设（§7.3）：询问 = on-request + workspace-write；自动放行 = never + workspace-write + 联网；
    /// 完全授权 = never + danger-full-access。
    /// </summary>
    private static CodexThreadSettings Settings(TurnOptions options) => new(
        options.Model.Id == CodexRuntime.DefaultModelId ? null : options.Model.Id,
        options.AutoApprove ? "never" : "on-request",
        options.Permission == TCode.Agent.PermissionMode.FullAccess,
        options.Permission == TCode.Agent.PermissionMode.AutoApprove,
        options.FastMode && options.Model.SupportsFastMode ? "priority" : null);

    // ---------------- 轮次 ----------------

    public async Task RunTurnAsync(UserMessage message, TurnOptions options, TurnWriter writer, CancellationToken cancellationToken)
    {
        var turn = new Turn(writer, options);
        await RunOnQueueAsync(() =>
        {
            _turn = turn;
            return ValueTask.CompletedTask;
        });

        try
        {
            var response = await _rpc.InvokeRawAsync("turn/start", TurnParams(message, options), cancellationToken);
            if (response.Obj("turn")?.Str("id") is { } id)
            {
                await RunOnQueueAsync(() => AdoptTurnIdAsync(turn, id));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // turn/start 尚未返回就被取消：仍需等待 turn/started 之后中断（§4.3）
        }
        catch (RpcRemoteException ex)
        {
            await RunOnQueueAsync(() => FinishAsync(turn, StopReason.Error, $"Codex: {ex.Message}"));
            return;
        }

        using (cancellationToken.Register(() => Enqueue(() => RequestInterruptAsync(turn))))
        {
            var completed = turn.Completed.Task;
            await Task.WhenAny(completed, _rpc.Completion);
            if (!completed.IsCompleted && !_rpc.IsConnected)
            {
                await RunOnQueueAsync(() => FinishAsync(turn, StopReason.Error, _describeExit?.Invoke() ?? "Codex exited unexpectedly."));
                throw new InvalidOperationException(_describeExit?.Invoke() ?? "Codex exited unexpectedly.");
            }
            if (cancellationToken.IsCancellationRequested && !completed.IsCompleted &&
                await Task.WhenAny(completed, Task.Delay(InterruptGrace)) != completed)
            {
                await RunOnQueueAsync(() => FinishAsync(turn, StopReason.Aborted, "Request aborted"));
                // 中断后不结束的进程视为卡死：交给 ExternalAgent 丢弃，下一次提问时 thread/resume。
                throw new TimeoutException($"Codex did not stop within {InterruptGrace.TotalSeconds:0} seconds after interrupt.");
            }
            await completed;
        }
    }

    // UserInput[]：文本，以及图片（base64 data URL）
    private static void WriteInput(System.Text.Json.Utf8JsonWriter w, UserMessage message)
    {
        w.WriteStartArray("input");
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case TextContent text:
                    w.WriteStartObject();
                    w.WriteString("type", "text");
                    w.WriteString("text", text.Text);
                    w.WriteStartArray("text_elements");
                    w.WriteEndArray();
                    w.WriteEndObject();
                    break;
                case ImageContent { HasData: true } image:
                    w.WriteStartObject();
                    w.WriteString("type", "image");
                    w.WriteString("url", $"data:{image.MimeType};base64,{image.Data}");
                    w.WriteEndObject();
                    break;
            }
        }
        w.WriteEndArray();
    }

    private JsonElement TurnParams(UserMessage message, TurnOptions options) => JsonBuilder.Object(w =>
    {
        w.WriteString("threadId", _threadId);
        WriteInput(w, message);

        // sticky 覆盖：只在与线程当前设置不同时发送（§4.1、§7.3）
        var wanted = Settings(options);
        var current = _thread!;
        if (wanted.Model is not null && wanted.Model != current.Model)
        {
            w.WriteString("model", wanted.Model);
        }
        if (wanted.ApprovalPolicy != current.ApprovalPolicy)
        {
            w.WriteString("approvalPolicy", wanted.ApprovalPolicy);
        }
        if (wanted.FullAccess != current.FullAccess || (!wanted.FullAccess && wanted.NetworkAccess != current.NetworkAccess))
        {
            w.WriteStartObject("sandboxPolicy");
            if (wanted.FullAccess)
            {
                w.WriteString("type", "dangerFullAccess");
            }
            else
            {
                w.WriteString("type", "workspaceWrite");
                w.WriteBoolean("networkAccess", wanted.NetworkAccess);
            }
            w.WriteEndObject();
        }
        if (wanted.ServiceTier != current.ServiceTier)
        {
            if (wanted.ServiceTier is null)
            {
                w.WriteNull("serviceTier"); // 显式 null 表示清除
            }
            else
            {
                w.WriteString("serviceTier", wanted.ServiceTier);
            }
        }
        _thread = current with { Model = wanted.Model ?? current.Model, ApprovalPolicy = wanted.ApprovalPolicy, FullAccess = wanted.FullAccess,
            NetworkAccess = wanted.FullAccess ? current.NetworkAccess : wanted.NetworkAccess, ServiceTier = wanted.ServiceTier };

        var efforts = options.Model.Compat?.ReasoningEfforts;
        var off = options.ThinkingLevel == ThinkingLevel.Off;
        if (efforts is { Count: > 0 })
        {
            w.WriteString("effort", off && !efforts.Contains("none") ? efforts[0] : ThinkingLevels.ChooseEffort(options.ThinkingLevel, efforts));
        }
        // 每轮都请求推理摘要，否则多数配置下没有任何思考增量（§4.1）
        w.WriteString("summary", off ? "none" : "auto");
    });

    /// <summary>运行中追加输入（turn/steer，§4.2）；失败时返回 false，由调用方排队到下一轮。</summary>
    public async ValueTask<bool> SteerAsync(UserMessage message, CancellationToken cancellationToken)
    {
        var turnId = _turn?.Id;
        if (turnId is null || _threadId is null)
        {
            return false;
        }
        try
        {
            await _rpc.InvokeRawAsync("turn/steer", JsonBuilder.Object(w =>
            {
                w.WriteString("threadId", _threadId);
                w.WriteString("expectedTurnId", turnId);
                WriteInput(w, message);
            }), cancellationToken);
            return true;
        }
        catch (RpcRemoteException)
        {
            return false;
        }
    }

    private ValueTask AdoptTurnIdAsync(Turn turn, string turnId)
    {
        if (turn.Id is null && !turn.Done)
        {
            turn.Id = turnId;
            if (turn.InterruptPending)
            {
                SendInterrupt(turn);
            }
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>中断（§10.1）：已知 turnId 时发 turn/interrupt，否则等 turn/started 后再发；未决审批交给服务端清除。</summary>
    private ValueTask RequestInterruptAsync(Turn turn)
    {
        if (turn.Done || turn.InterruptPending)
        {
            return ValueTask.CompletedTask;
        }
        turn.InterruptPending = true;
        turn.ApprovalScope.Cancel();
        if (turn.Id is not null)
        {
            SendInterrupt(turn);
        }
        return ValueTask.CompletedTask;
    }

    private void SendInterrupt(Turn turn) =>
        _ = _rpc.InvokeRawAsync("turn/interrupt", JsonBuilder.Object(w =>
        {
            w.WriteString("threadId", _threadId);
            w.WriteString("turnId", turn.Id);
        })).ContinueWith(t => _ = t.Exception, TaskScheduler.Default);

    // ---------------- 入站消息 ----------------

    private void OnNotification(string method, JsonElement? parameters)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } p)
        {
            return;
        }
        Enqueue(() => HandleNotificationAsync(method, p));
    }

    private async ValueTask HandleNotificationAsync(string method, JsonElement p)
    {
        // 只处理主线程的通知；子 Agent 线程与全局通知不影响当前轮次（§1.3）
        if (p.Str("threadId") is { } threadId && threadId != _threadId)
        {
            return;
        }
        var turn = _turn;
        switch (method)
        {
            case "turn/started" when turn is not null:
                if (p.Obj("turn")?.Str("id") is { } startedId)
                {
                    await AdoptTurnIdAsync(turn, startedId);
                }
                break;
            case "turn/completed" or "turn/failed" or "turn/aborted" when turn is not null:
                await OnTurnCompletedAsync(turn, p.Obj("turn") ?? default, method);
                break;
            case "item/started" when turn is not null && IsCurrent(turn, p):
                await OnItemStartedAsync(turn, p.Obj("item") ?? default);
                break;
            case "item/completed" when turn is not null && IsCurrent(turn, p):
                await OnItemCompletedAsync(turn, p.Obj("item") ?? default);
                break;
            case "item/agentMessage/delta" when turn is not null && IsCurrent(turn, p):
                if (p.Str("itemId") is { } messageId && p.Str("delta") is { } text && turn.Blocks.TryGetValue(messageId, out var textBlock))
                {
                    await turn.Writer.AppendAsync(textBlock.Handle, text);
                }
                break;
            case "item/reasoning/summaryPartAdded" when turn is not null && IsCurrent(turn, p):
                if (p.Str("itemId") is { } partItem && turn.Blocks.TryGetValue(partItem, out var part) && part.HasText)
                {
                    part.PendingSeparator = true;
                }
                break;
            case "item/reasoning/summaryTextDelta" when turn is not null && IsCurrent(turn, p):
                if (p.Str("itemId") is { } reasoningId && p.Str("delta") is { } summary)
                {
                    await AppendReasoningAsync(turn, reasoningId, summary);
                }
                break;
            case "item/commandExecution/outputDelta" when turn is not null && IsCurrent(turn, p):
                if (p.Str("itemId") is { } commandId && p.Str("delta") is { } output && turn.Tools.TryGetValue(commandId, out var command))
                {
                    command.Output.Append(output);
                    await turn.Writer.ToolStartedAsync(command.Id, command.Name, command.ArgumentsJson);
                    await turn.Writer.ToolProgressAsync(command.Id, command.Name, output);
                }
                break;
            case "item/fileChange/patchUpdated" when turn is not null && IsCurrent(turn, p):
                if (p.Str("itemId") is { } patchId && turn.Tools.TryGetValue(patchId, out var patch) && p.Prop("changes") is { } changes)
                {
                    patch.Changes = changes.Clone();
                }
                break;
            case "item/mcpToolCall/progress" when turn is not null && IsCurrent(turn, p):
                if (p.Str("itemId") is { } mcpId && p.Str("message") is { } progress && turn.Tools.TryGetValue(mcpId, out var mcp))
                {
                    await turn.Writer.ToolProgressAsync(mcp.Id, mcp.Name, progress);
                }
                break;
            case "thread/tokenUsage/updated":
                OnUsage(turn, p.Obj("tokenUsage") ?? default);
                break;
            case "error" when turn is not null && IsCurrent(turn, p):
                if (!p.Bool("willRetry") && p.Obj("error")?.Str("message") is { } error)
                {
                    turn.LastError = error;
                }
                break;
            case "serverRequest/resolved":
                if (p.Prop("requestId") is { } requestId && turn?.Approvals.Remove(requestId.GetRawText(), out var pending) == true)
                {
                    pending.Cancel();
                }
                break;
            // 其他通知（thread/status/changed、turn/plan/updated、turn/diff/updated、warning 等）首版忽略；未知通知一律忽略（§5.1）
        }
    }

    /// <summary>通知属于当前轮次（turnId 尚未确定时视为属于当前轮次）。跨轮次到达的后台命令完成通知被忽略（§5.2 第 4 条）。</summary>
    private static bool IsCurrent(Turn turn, JsonElement p) =>
        !turn.Done && (turn.Id is null || p.Str("turnId") is not { } id || id == turn.Id);

    private async ValueTask OnItemStartedAsync(Turn turn, JsonElement item)
    {
        if (item.Str("id") is not { } id)
        {
            return;
        }
        switch (item.Str("type"))
        {
            case "agentMessage":
                turn.Blocks[id] = new Block(await turn.Writer.BeginBlockAsync(BlockKind.Text));
                break;
            case "reasoning":
                break; // 第一个增量到达时才开块，避免空的思考块（§5.4）
            case "userMessage" or "contextCompaction" or "plan":
                break;
            default:
                var tool = ToolOf(item);
                if (tool is null)
                {
                    break;
                }
                turn.Tools[id] = tool;
                await turn.Writer.ToolCallAsync(tool.Id, tool.Name, tool.ArgumentsJson);
                // 命令与文件修改可能需要审批：审批通过、出现输出或完成时才算开始执行；其他工具立即开始
                if (turn.Options.AutoApprove || tool.Kind is not ("commandExecution" or "fileChange"))
                {
                    await turn.Writer.ToolStartedAsync(tool.Id, tool.Name, tool.ArgumentsJson);
                }
                break;
        }
    }

    private async ValueTask OnItemCompletedAsync(Turn turn, JsonElement item)
    {
        if (item.Str("id") is not { } id)
        {
            return;
        }
        switch (item.Str("type"))
        {
            case "agentMessage":
                if (turn.Blocks.Remove(id, out var text))
                {
                    await turn.Writer.EndBlockAsync(text.Handle, item.Str("text"));
                }
                else if (item.Str("text") is { Length: > 0 } whole)
                {
                    await turn.Writer.TextAsync(whole);
                }
                break;
            case "reasoning":
                var summary = string.Join("\n\n", item.Arr("summary").Select(s => s.GetString()).OfType<string>());
                if (turn.Blocks.Remove(id, out var reasoning))
                {
                    await turn.Writer.EndBlockAsync(reasoning.Handle, summary.Length > 0 ? summary : null);
                }
                else if (summary.Length > 0)
                {
                    await turn.Writer.ThinkingAsync(summary);
                }
                break;
            case "userMessage" or "contextCompaction" or "plan":
                break;
            default:
                if (!turn.Tools.TryGetValue(id, out var tool))
                {
                    tool = ToolOf(item);
                    if (tool is null)
                    {
                        break;
                    }
                    turn.Tools[id] = tool;
                }
                await CompleteToolAsync(turn, tool, item);
                break;
        }
    }

    private async ValueTask AppendReasoningAsync(Turn turn, string itemId, string delta)
    {
        if (!turn.Blocks.TryGetValue(itemId, out var block))
        {
            block = new Block(await turn.Writer.BeginBlockAsync(BlockKind.Thinking));
            turn.Blocks[itemId] = block;
        }
        if (block.PendingSeparator)
        {
            // 摘要分段之间没有分隔符，补空行（§5.2 第 6 条）
            await turn.Writer.AppendAsync(block.Handle, "\n\n");
            block.PendingSeparator = false;
        }
        await turn.Writer.AppendAsync(block.Handle, delta);
        block.HasText = true;
    }

    private static ToolState? ToolOf(JsonElement item)
    {
        var id = item.Str("id")!;
        switch (item.Str("type"))
        {
            case "commandExecution":
                return new ToolState(id, "commandExecution", "shell", JsonBuilder.Object(w =>
                {
                    w.WriteString("command", item.Str("command"));
                    w.WriteString("cwd", item.Str("cwd"));
                    if (item.Prop("commandActions") is { } actions)
                    {
                        w.WritePropertyName("commandActions");
                        actions.WriteTo(w);
                    }
                }).GetRawText()) { Title = item.Str("command") };
            case "fileChange":
                var changes = item.Prop("changes") ?? JsonBuilder.Parse("[]");
                return new ToolState(id, "fileChange", "apply_patch", JsonBuilder.Object(w =>
                {
                    w.WritePropertyName("changes");
                    changes.WriteTo(w);
                }).GetRawText()) { Changes = changes.Clone() };
            case "mcpToolCall":
                var arguments = item.Prop("arguments");
                return new ToolState(id, "mcpToolCall", $"mcp__{item.Str("server")}__{item.Str("tool")}",
                    arguments is { ValueKind: JsonValueKind.Object } a ? a.GetRawText() : "{}");
            case "webSearch":
                return new ToolState(id, "webSearch", "web_search", JsonBuilder.Object(w => w.WriteString("query", item.Str("query"))).GetRawText());
            case "imageView":
                return new ToolState(id, "imageView", "view_image", JsonBuilder.Object(w => w.WriteString("path", item.Str("path"))).GetRawText());
            case "dynamicToolCall" or "collabAgentToolCall":
                var args = item.Prop("arguments");
                return new ToolState(id, item.Str("type")!, item.Str("tool") ?? item.Str("type")!,
                    args is { ValueKind: JsonValueKind.Object } d ? d.GetRawText() : "{}");
            case "imageGeneration":
                return new ToolState(id, "imageGeneration", "image_generation", "{}");
            default:
                return null; // 未知 type 忽略（§12 第 19 条）
        }
    }

    private static async ValueTask CompleteToolAsync(Turn turn, ToolState tool, JsonElement item)
    {
        if (tool.Done)
        {
            return;
        }
        tool.Done = true;
        var status = item.Str("status");
        string text;
        var isError = status is not ("completed" or null);
        string? details = null;
        switch (tool.Kind)
        {
            case "commandExecution":
                var exitCode = item.Long("exitCode");
                isError |= exitCode is not (null or 0);
                text = status == "declined"
                    ? "The user declined to run this command."
                    : item.Str("aggregatedOutput") ?? tool.Output.ToString();
                if (exitCode is { } code && code != 0 && status != "declined")
                {
                    text = (text.Length > 0 ? text.TrimEnd() + "\n" : "") + $"Exit code {code}";
                }
                break;
            case "fileChange":
                var changes = item.Prop("changes") ?? tool.Changes;
                details = changes is { } c ? JsonBuilder.Object(w =>
                {
                    w.WritePropertyName("changes");
                    c.WriteTo(w);
                }).GetRawText() : null;
                var paths = changes?.ValueKind == JsonValueKind.Array
                    ? changes.Value.EnumerateArray().Select(x => x.Str("path")).OfType<string>().ToArray()
                    : [];
                text = status switch
                {
                    "declined" => "The user declined this change.",
                    "failed" => "Failed to apply the change.",
                    _ => paths.Length == 0 ? "No files changed." : "Modified " + string.Join(", ", paths),
                };
                break;
            case "mcpToolCall":
                text = item.Obj("error")?.Str("message")
                       ?? string.Join("\n", (item.Obj("result") ?? default).Arr("content").Where(x => x.Str("type") == "text")
                           .Select(x => x.Str("text")).OfType<string>());
                break;
            case "webSearch":
                text = item.Str("query") is { } query ? $"Searched: {query}" : "Searched the web.";
                break;
            case "imageGeneration":
                text = item.Str("savedPath") is { } saved ? $"Saved image to {saved}" : "Image generated.";
                break;
            default:
                text = isError ? "Tool failed." : "Done.";
                break;
        }
        await turn.Writer.ToolCompletedAsync(tool.Id, tool.Name, text, isError, tool.ArgumentsJson, details);
        // 工具结果关闭了草稿：其后的文本 / 思考块已失效，重新开始
        turn.Blocks.Clear();
    }

    private async ValueTask OnTurnCompletedAsync(Turn turn, JsonElement completed, string method)
    {
        if (turn.Done || (turn.Id is not null && completed.Str("id") is { } id && id != turn.Id))
        {
            return; // 旧的或重复的 completed（§12 第 14 条）
        }
        var status = method switch
        {
            "turn/failed" => "failed",
            "turn/aborted" => "interrupted",
            _ => completed.Str("status"),
        };
        var (reason, message) = status switch
        {
            "completed" => (StopReason.Stop, (string?)null),
            "interrupted" => (StopReason.Aborted, "Request aborted"),
            _ => (StopReason.Error, completed.Obj("error")?.Str("message") ?? turn.LastError ?? "Codex turn failed"),
        };
        await FinishAsync(turn, reason, message);
    }

    private async ValueTask FinishAsync(Turn turn, StopReason reason, string? message)
    {
        if (turn.Done)
        {
            return;
        }
        turn.Done = true;
        turn.ApprovalScope.Cancel();
        foreach (var tool in turn.Tools.Values.Where(t => !t.Done))
        {
            tool.Done = true;
            await turn.Writer.ToolCompletedAsync(tool.Id, tool.Name, "Operation aborted.", true, tool.ArgumentsJson);
        }
        if (turn.Usage is { } usage)
        {
            turn.Writer.SetUsage(usage);
        }
        await turn.Writer.CompleteAsync(reason, message);
        _turn = null;
        turn.Completed.TrySetResult();
    }

    /// <summary>整轮用量 = 轮末 total − 轮初 total；Input 扣除缓存（§5.5）。</summary>
    private void OnUsage(Turn? turn, JsonElement tokenUsage)
    {
        if (CodexUsage.Parse(tokenUsage.Obj("total")) is not { } total)
        {
            return;
        }
        var baseline = _baseline ?? (CodexUsage.Parse(tokenUsage.Obj("last")) is { } last ? total - last : total);
        _baseline ??= baseline;
        if (turn is null)
        {
            _baseline = total;
            return;
        }
        turn.StartTotal ??= baseline;
        turn.Usage = (total - turn.StartTotal.Value).ToUsage();
        _baseline = total;
    }

    // ---------------- 服务端请求 ----------------

    private Task<JsonElement?> OnRequestAsync(JsonElement id, string method, JsonElement? parameters, CancellationToken ct)
    {
        var p = parameters ?? default;
        return method switch
        {
            "item/commandExecution/requestApproval" or "item/fileChange/requestApproval" or "item/permissions/requestApproval"
                => ApproveAsync(id, method, p, ct),
            "item/tool/requestUserInput" => Task.FromResult<JsonElement?>(JsonBuilder.Parse("""{"answers":{}}""")),
            "mcpServer/elicitation/request" => Task.FromResult<JsonElement?>(JsonBuilder.Parse("""{"action":"decline"}""")),
            "item/tool/call" => Task.FromResult<JsonElement?>(JsonBuilder.Parse(
                """{"success":false,"contentItems":[{"type":"inputText","text":"TCode does not provide dynamic tools"}]}""")),
            _ => throw new RpcRemoteException(JsonRpcConnection.MethodNotFound, $"unsupported method: {method}"),
        };
    }

    /// <summary>
    /// 审批（§6.8）：在队列中按顺序登记（以便从 item 中取补丁），再异步等待用户。
    /// 轮次结束、被中断或收到 serverRequest/resolved 时不回复（服务端已清除该请求，§6.1）。
    /// </summary>
    private async Task<JsonElement?> ApproveAsync(JsonElement id, string method, JsonElement p, CancellationToken ct)
    {
        var prepared = new TaskCompletionSource<(Turn Turn, RuntimeApprovalRequest Request, CancellationTokenSource Scope)?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(() =>
        {
            var turn = _turn;
            if (turn is null || turn.Done || turn.InterruptPending)
            {
                prepared.TrySetResult(null);
                return ValueTask.CompletedTask;
            }
            var scope = CancellationTokenSource.CreateLinkedTokenSource(turn.ApprovalScope.Token);
            turn.Approvals[id.GetRawText()] = scope;
            prepared.TrySetResult((turn, BuildApproval(turn, method, p), scope));
            return ValueTask.CompletedTask;
        });
        if (await prepared.Task is not { } ready)
        {
            throw new RpcSuppressReplyException();
        }
        var (turnRef, request, approvalScope) = ready;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(approvalScope.Token, ct);
        ApprovalDecision decision;
        try
        {
            decision = await _host.RequestApprovalAsync(request, linked.Token);
        }
        catch (Exception) when (linked.IsCancellationRequested)
        {
            throw new RpcSuppressReplyException();
        }
        if (decision.Outcome == ApprovalOutcome.Cancel)
        {
            // 轮次被中断时宿主也会返回 Cancel：先让队列处理已排入的中断，再判断请求是否已作废
            await RunOnQueueAsync(() => ValueTask.CompletedTask);
        }
        if (approvalScope.IsCancellationRequested)
        {
            throw new RpcSuppressReplyException();
        }
        turnRef.Approvals.Remove(id.GetRawText());
        if (decision.IsAllowed && method != "item/permissions/requestApproval")
        {
            Enqueue(() => turnRef.Tools.TryGetValue(request.ToolCallId, out var tool) && !tool.Done
                ? turnRef.Writer.ToolStartedAsync(tool.Id, tool.Name, tool.ArgumentsJson)
                : ValueTask.CompletedTask);
        }
        return method == "item/permissions/requestApproval"
            ? PermissionsReply(p, decision)
            : JsonBuilder.Object(w => w.WriteString("decision", decision.Outcome switch
            {
                ApprovalOutcome.AllowOnce => "accept",
                ApprovalOutcome.AllowForSession => "acceptForSession",
                ApprovalOutcome.Cancel => "cancel",
                _ => "decline",
            }));
    }

    private static JsonElement PermissionsReply(JsonElement p, ApprovalDecision decision) => JsonBuilder.Object(w =>
    {
        if (decision.IsAllowed && p.Obj("permissions") is { } permissions)
        {
            w.WritePropertyName("permissions");
            permissions.WriteTo(w);
            w.WriteString("scope", decision.Outcome == ApprovalOutcome.AllowForSession ? "session" : "turn");
        }
        else
        {
            w.WriteStartObject("permissions");
            w.WriteEndObject();
        }
    });

    private static RuntimeApprovalRequest BuildApproval(Turn turn, string method, JsonElement p)
    {
        var itemId = p.Str("itemId") ?? "";
        turn.Tools.TryGetValue(itemId, out var tool);
        switch (method)
        {
            case "item/commandExecution/requestApproval":
                var host = p.Obj("networkApprovalContext")?.Str("host");
                var title = host is not null ? $"Network access: {host}"
                    : p.Str("kind") == "writeStdin" ? "Write to terminal"
                    : p.Str("command") ?? tool?.Title;
                var args = JsonBuilder.Object(w =>
                {
                    w.WriteString("command", p.Str("command"));
                    w.WriteString("cwd", p.Str("cwd"));
                    if (p.Prop("commandActions") is { } actions)
                    {
                        w.WritePropertyName("commandActions");
                        actions.WriteTo(w);
                    }
                }).GetRawText();
                return new RuntimeApprovalRequest(itemId, "shell", args, ApprovalKind.Command, title, p.Str("reason"));
            case "item/fileChange/requestApproval":
                // params 中没有 changes：补丁取自同一 itemId 的 fileChange item（§6.3）
                var changes = tool?.Changes;
                var paths = changes?.ValueKind == JsonValueKind.Array
                    ? changes.Value.EnumerateArray().Select(c => c.Str("path")).OfType<string>().ToArray()
                    : [];
                return new RuntimeApprovalRequest(itemId, "apply_patch", tool?.ArgumentsJson ?? "{}", ApprovalKind.FileChange,
                    paths.Length > 0 ? string.Join(", ", paths) : "Apply patch", p.Str("reason"));
            default:
                return new RuntimeApprovalRequest(itemId, "request_permissions", p.Obj("permissions")?.GetRawText() ?? "{}", ApprovalKind.Tool,
                    "Additional permissions", p.Str("reason"));
        }
    }

    // ---------------- 队列 ----------------

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
                // 单条消息处理失败不影响后续（宽松解析，§12 第 24 条）
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_turn is { Done: false } turn)
        {
            Enqueue(() => RequestInterruptAsync(turn));
            await Task.WhenAny(turn.Completed.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        }
        await _rpc.DisposeAsync();
        _queue.Writer.TryComplete();
        if (_process is not null)
        {
            await _process.DisposeAsync();
        }
        await _consumer;
    }

    // ---------------- 状态 ----------------

    private sealed class Turn(TurnWriter writer, TurnOptions options)
    {
        public TurnWriter Writer { get; } = writer;
        public TurnOptions Options { get; } = options;
        public string? Id { get; set; }
        public bool Done { get; set; }
        public bool InterruptPending { get; set; }
        public string? LastError { get; set; }
        public CodexUsage? StartTotal { get; set; }
        public Usage? Usage { get; set; }
        public Dictionary<string, Block> Blocks { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ToolState> Tools { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, CancellationTokenSource> Approvals { get; } = new(StringComparer.Ordinal);
        public CancellationTokenSource ApprovalScope { get; } = new();
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Block(int handle)
    {
        public int Handle { get; } = handle;
        public bool HasText { get; set; }
        public bool PendingSeparator { get; set; }
    }

    private sealed class ToolState(string id, string kind, string name, string argumentsJson)
    {
        public string Id { get; } = id;
        public string Kind { get; } = kind;
        public string Name { get; } = name;
        public string ArgumentsJson { get; } = argumentsJson;
        public string? Title { get; init; }
        public JsonElement? Changes { get; set; }
        public StringBuilder Output { get; } = new();
        public bool Done { get; set; }
    }
}

/// <summary>Codex 的 TokenUsageBreakdown（inputTokens 含缓存，outputTokens 含推理）。</summary>
internal readonly record struct CodexUsage(long Input, long CachedInput, long CacheWrite, long Output, long Reasoning)
{
    public static CodexUsage Zero { get; } = new(0, 0, 0, 0, 0);

    public static CodexUsage? Parse(JsonElement? e) => e is { } v
        ? new CodexUsage(v.Long("inputTokens") ?? 0, v.Long("cachedInputTokens") ?? 0, v.Long("cacheWriteInputTokens") ?? 0,
            v.Long("outputTokens") ?? 0, v.Long("reasoningOutputTokens") ?? 0)
        : null;

    public static CodexUsage operator -(CodexUsage a, CodexUsage b) =>
        new(a.Input - b.Input, a.CachedInput - b.CachedInput, a.CacheWrite - b.CacheWrite, a.Output - b.Output, a.Reasoning - b.Reasoning);

    public Usage ToUsage() => new(Math.Max(0, Input - CachedInput - CacheWrite), Math.Max(0, Output), Math.Max(0, CachedInput),
        Math.Max(0, CacheWrite), Math.Max(0, Reasoning));
}
