using TCode.Agent.Coding.Assets;
using TCode.Agent.Coding.Prompt;
using TCode.Agent.Coding.Sessions;
using TCode.Agent.Coding.Tools;
using TCode.Agent.Coding.Workspace;
using TCode.Ai;
using TCode.Ai.Messages;
using TCode.Ai.Models;

namespace TCode.Agent.Coding;

/// <summary>
/// 编码会话：在 Agent 循环之上加入工作目录、系统提示、内置工具与持久化。
/// 循环可以是内置的 <see cref="TCode.Agent.Agent"/>，也可以是外部运行时（<see cref="IExternalAgentLoop"/>）。
/// 持久化监听器最先订阅，保证 MessageEnd 写盘后才会继续执行工具（屏障语义）。
/// </summary>
public sealed class AgentSession : IAsyncDisposable
{
    private readonly IDisposable _persistence;

    private AgentSession(IAgentLoop agent, SessionStore store, ToolContext? tools = null)
    {
        Agent = agent;
        Store = store;
        Tools = tools;
        Workspace = tools?.Workspace ?? new WorkspaceRoots(store.Cwd);
        _persistence = agent.Subscribe(async (e, ct) =>
        {
            if (e is MessageEndEvent end)
            {
                await store.AppendMessageAsync(end.Message, CancellationToken.None);
            }
        });
        if (agent is IExternalAgentLoop external)
        {
            // 原生会话 id 一确定就记下来（文件创建前先缓存，随第一条消息写入），用于之后恢复。
            external.NativeSessionIdChanged += id =>
                _ = store.AppendRuntimeSessionAsync(external.RuntimeId, id, CancellationToken.None);
        }
        if (tools is not null)
        {
            tools.BackgroundTasks.Completed += OnBackgroundTaskCompleted;
            tools.Tasks.Changed += OnTasksChanged;
        }
    }

    public IAgentLoop Agent { get; }

    /// <summary>外部运行时的会话（内置 Agent 时为 null）。</summary>
    public IExternalAgentLoop? External => Agent as IExternalAgentLoop;
    public SessionStore Store { get; }
    public string Id => Store.SessionId;
    public string Cwd => Store.Cwd;

    /// <summary>工作区根目录（cwd 与额外加入的目录），决定只读工具的免审批范围。</summary>
    public WorkspaceRoots Workspace { get; }

    /// <summary>内置工具共享的会话状态（外部运行时为 null）。</summary>
    public ToolContext? Tools { get; }

    private SessionRole _role = SessionRole.Agent;

    /// <summary>会话的 role（docs/design/composer.md §10.3）。内置 Agent 在下一批用户输入前按它过滤工具、更新 role 节。</summary>
    public SessionRole Role
    {
        get => _role;
        set
        {
            var previous = _role;
            _role = value;
            if (Tools is not null)
            {
                Tools.Role = value;
            }
            // 外部运行时：只读由运行时原生收紧，说明随下一条用户消息发送一次（docs/design/composer.md §11）
            if (External is { } external)
            {
                external.SetMode(value.Name, value.IsReadOnly, SessionRole.ExternalReminder(previous, value));
            }
        }
    }

    /// <summary>“本会话允许”的审批记录（仅内存）。</summary>
    public SessionApprovals Approvals { get; init; } = new();

    /// <summary>
    /// 用户选择的上下文预算（≤ 模型上下文窗口）。用于计算占用比例，后续也作为压缩阈值的依据；
    /// 当前各厂商没有对应的请求参数。
    /// </summary>
    public int ContextWindow { get; set; }

    public static AgentSession Create(WireApiRegistry apis, ModelInfo model, string cwd, string? sessionsRoot = null,
        Func<string, string?>? getApiKey = null, BuiltinOptions? options = null)
        => CreateBuiltin(apis, model, SessionStore.Create(cwd, sessionsRoot), getApiKey, options);

    /// <summary>
    /// 在给定的会话存储上创建内置 Agent 会话：新会话、子 Agent，或尚无消息的会话从外部运行时换回内置 Agent。
    /// 换运行时的调用方负责先释放旧会话；会话 id 与文件路径保持不变。
    /// </summary>
    public static AgentSession CreateBuiltin(WireApiRegistry apis, ModelInfo model, SessionStore store, Func<string, string?>? getApiKey = null,
        BuiltinOptions? options = null)
    {
        var (agent, tools) = CreateBuiltinLoop(apis, model, store, null, getApiKey, options ?? new BuiltinOptions());
        return new AgentSession(agent, store, tools)
        {
            ContextWindow = model.ContextWindow,
            Approvals = options?.Approvals ?? new SessionApprovals(),
        };
    }

    public static AgentSession Open(WireApiRegistry apis, ModelInfo model, string filePath, Func<string, string?>? getApiKey = null,
        BuiltinOptions? options = null)
    {
        var (store, snapshot) = SessionStore.OpenSnapshot(filePath);
        var (agent, tools) = CreateBuiltinLoop(apis, model, store, snapshot, getApiKey, options ?? new BuiltinOptions());
        agent.LoadHistory(ImageAttachments.Load(snapshot.Messages));
        tools.Tasks.Restore(snapshot.Tasks);
        return new AgentSession(agent, store, tools)
        {
            ContextWindow = model.ContextWindow,
            Approvals = options?.Approvals ?? new SessionApprovals(),
        };
    }

    /// <summary>内置 Agent 循环：系统提示（按节，冻结开头并追加变化）、工具集、会话 id 与 Key 获取方式。</summary>
    private static (Agent Agent, ToolContext Tools) CreateBuiltinLoop(WireApiRegistry apis, ModelInfo model, SessionStore store,
        SessionSnapshot? snapshot, Func<string, string?>? getApiKey, BuiltinOptions options)
    {
        var workspace = options.Workspace ?? new WorkspaceRoots(store.Cwd);
        var toolContext = new ToolContext(workspace, store.SessionId, options.BackgroundTasks, options.Tasks)
        {
            Services = options.Services,
        };
        var tools = BuiltinTools.Create(toolContext, options.ToolSet);
        var prompt = snapshot is null
            ? SystemPromptTracker.Create(store, workspace, tools, model, options.ExtraPromptSections)
            : SystemPromptTracker.Restore(store, snapshot, workspace, tools, model, options.ExtraPromptSections);
        var agent = new Agent(apis, model, prompt.InitialPrompt, tools)
        {
            SessionId = store.SessionId,
            GetApiKey = getApiKey,
        };
        var dynamic = options.Services.DynamicTools?.Invoke(toolContext);
        toolContext.DynamicTools = dynamic;
        var firstTurn = true;
        agent.ContextUpdates = async ct =>
        {
            // 每批用户输入之前：取当前的 MCP 工具、skills 与 role，同一轮内保持不变
            var set = DynamicToolSet.Empty;
            if (dynamic is not null)
            {
                try
                {
                    set = await dynamic.RefreshAsync(firstTurn, ct);
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                }
            }
            firstTurn = false;
            var role = toolContext.Role ?? SessionRole.Agent;
            // 从其他模式切回 Agent：模式节换成“可以动手了”的说明，而不是直接删掉（docs/design/composer.md §18.4）
            if (role.Section is null && role.Name == SessionRole.AgentName && prompt.HasSection(SessionRole.SectionName))
            {
                role = SessionRole.AgentResumed;
            }
            IReadOnlyList<IAgentTool> all = set.Tools.Count == 0 ? tools : [.. tools, .. set.Tools];
            agent.Tools = role.Tools is ["all"] ? all : all.Where(role.Allows).ToArray();
            prompt.Dynamic = role.Section is { } section ? [.. set.Sections, section] : set.Sections;
            var updates = await prompt.UpdatesAsync(agent.Model, ct);
            agent.SystemPrompt = prompt.InitialPrompt;
            return updates;
        };
        toolContext.ModelSupportsImages = () => agent.Model.SupportsImages;
        toolContext.CurrentModel = () => agent.Model;
        return (agent, toolContext);
    }

    /// <summary>
    /// 在同一个会话存储上换成外部运行时（尚无消息的会话从内置 Agent 换到外部运行时）。
    /// 调用方负责先释放旧会话；会话 id 与文件路径保持不变。
    /// </summary>
    public static AgentSession Rebind(SessionStore store, IExternalAgentLoop loop) =>
        new(loop, store) { ContextWindow = loop.Model.ContextWindow };

    /// <summary>用外部运行时创建新会话。<paramref name="createLoop"/> 收到会话存储（含 TCode 会话 id）。</summary>
    public static AgentSession Create(string cwd, Func<SessionStore, IExternalAgentLoop> createLoop, string? sessionsRoot = null)
    {
        var store = SessionStore.Create(cwd, sessionsRoot);
        var loop = createLoop(store);
        return new AgentSession(loop, store) { ContextWindow = loop.Model.ContextWindow };
    }

    /// <summary>打开外部运行时的会话：<paramref name="createLoop"/> 从快照中取得原生会话 id 以便恢复。</summary>
    public static AgentSession Open(string filePath, Func<SessionStore, SessionSnapshot, IExternalAgentLoop> createLoop)
    {
        var (store, snapshot) = SessionStore.OpenSnapshot(filePath);
        var loop = createLoop(store, snapshot);
        loop.LoadHistory(ImageAttachments.Load(snapshot.Messages));
        return new AgentSession(loop, store) { ContextWindow = loop.Model.ContextWindow };
    }

    /// <summary>空闲时开始新一轮；运行中则按 <paramref name="whileRunning"/> 排队。</summary>
    public PromptDisposition Prompt(string text, StreamingBehavior whileRunning = StreamingBehavior.FollowUp)
        => Prompt(UserMessage.FromText(text), whileRunning);

    /// <inheritdoc cref="Prompt(string, StreamingBehavior)"/>
    public PromptDisposition Prompt(UserMessage message, StreamingBehavior whileRunning = StreamingBehavior.FollowUp)
    {
        if (!Agent.IsRunning)
        {
            try
            {
                _ = Agent.PromptAsync(message);
                return PromptDisposition.Started;
            }
            catch (InvalidOperationException)
            {
                // 与另一路输入竞争：已经开始运行，改为排队。
            }
        }
        if (whileRunning == StreamingBehavior.Steer)
        {
            Agent.Steer(message);
        }
        else
        {
            Agent.FollowUp(message);
        }
        return PromptDisposition.Queued;
    }

    /// <summary>
    /// 投递非用户输入（后台任务通知、其他会话的消息）：空闲时开始新一轮，运行中则在当前轮结束后插入。
    /// </summary>
    public PromptDisposition Deliver(UserMessage message) => Prompt(message, StreamingBehavior.Steer);

    private void OnBackgroundTaskCompleted(BackgroundTask task, string? owner)
    {
        if (owner is not null && owner != Id)
        {
            return;
        }
        Deliver(new UserMessage([new TextContent(BackgroundTasks.FormatNotification(task))]) { Source = "task-notification" });
    }

    private void OnTasksChanged(IReadOnlyList<TaskItem> tasks) =>
        _ = Store.AppendTasksAsync(tasks, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        if (Tools is not null)
        {
            Tools.BackgroundTasks.Completed -= OnBackgroundTaskCompleted;
            Tools.Tasks.Changed -= OnTasksChanged;
            await Tools.BackgroundTasks.StopAllAsync(Id);
        }
        Agent.Abort();
        await Agent.WaitForIdleAsync();
        if (Tools?.DynamicTools is { } dynamic)
        {
            await dynamic.DisposeAsync();
        }
        _persistence.Dispose();
        if (Agent is IExternalAgentLoop external)
        {
            await external.DisposeAsync();
        }
    }
}

public enum StreamingBehavior
{
    Steer,
    FollowUp,
}

public enum PromptDisposition
{
    Started,
    Queued,
}
