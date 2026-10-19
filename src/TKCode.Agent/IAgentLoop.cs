using TKCode.Ai.Messages;
using TKCode.Ai.Models;

namespace TKCode.Agent;

/// <summary>
/// 会话所驱动的 Agent 循环。内置实现是 <see cref="Agent"/>（TKCode 自己调用模型、执行工具）；
/// 外部运行时（Claude Agent、Codex、ACP Agent）把 CLI 的事件翻译成同一套 <see cref="AgentEvent"/>，
/// 使持久化、RPC 推送和历史恢复对两者完全一致。
/// </summary>
public interface IAgentLoop
{
    ModelInfo Model { get; set; }
    ThinkingLevel ThinkingLevel { get; set; }
    bool FastMode { get; set; }

    /// <summary>权限档位：审批是否询问用户、外部运行时的沙箱范围。</summary>
    PermissionMode PermissionMode { get; set; }

    bool IsRunning { get; }
    IReadOnlyList<Message> Messages { get; }

    /// <summary>恢复会话时载入既有历史（不触发事件）。</summary>
    void LoadHistory(IEnumerable<Message> messages);

    /// <summary>监听器按订阅顺序依次 await（MessageEnd 是持久化屏障）。</summary>
    IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> listener);

    /// <summary>开始一次运行；运行中请改用 <see cref="Steer"/> 或 <see cref="FollowUp"/>。</summary>
    Task PromptAsync(UserMessage message);

    void Steer(UserMessage message);
    void FollowUp(UserMessage message);
    void Abort();
    Task WaitForIdleAsync();
}

/// <summary>
/// 会话的权限档位（三档）。各运行时的映射见 docs/design/external-agent-runtimes.md §6.1。
/// 提问类请求（AskUserQuestion 等）在任何档位下都不会被自动回答。
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<PermissionMode>))]
public enum PermissionMode
{
    /// <summary>询问（默认）：修改文件、执行命令等操作逐项请用户审批。</summary>
    Ask,

    /// <summary>自动放行：审批自动允许，但保留运行时的沙箱（Codex 限于工作区内写入，允许联网）。</summary>
    AutoApprove,

    /// <summary>完全授权：不询问、不限沙箱（Claude bypassPermissions、Codex danger-full-access）。</summary>
    FullAccess,
}

/// <summary>由外部 CLI 驱动的循环：会话需要记住运行时与 CLI 侧的原生会话 id 以便恢复。</summary>
public interface IExternalAgentLoop : IAgentLoop, IAsyncDisposable
{
    /// <summary>运行时 id（同时也是其模型的 Provider），例如 <c>claude-code</c>、<c>codex</c>、<c>acp:cursor</c>。</summary>
    string RuntimeId { get; }

    /// <summary>CLI 侧的会话 id（Claude session_id / Codex threadId / ACP sessionId）。</summary>
    string? NativeSessionId { get; }

    /// <summary>原生会话 id 首次确定或改变（例如恢复失败后新建）时触发。</summary>
    event Action<string>? NativeSessionIdChanged;

    /// <summary>运行时能派生子 Agent（ACP 协调模式依赖它）。</summary>
    bool SupportsSubagents { get; }

    /// <summary>
    /// 运行时原生实现 plan 模式（自带提示词、计划文件与审批工具，例如 Claude Code）：切到 Plan 时不再发 TKCode 的模式说明
    /// （docs/design/plan-mode.md §12.8）。
    /// </summary>
    bool NativePlanMode => false;

    /// <summary>
    /// 设置会话的模式（docs/design/composer.md §11）：<paramref name="readOnly"/> 由运行时原生收紧；
    /// <paramref name="reminder"/> 在下一轮随用户消息发给 Agent 一次（只发给 Agent，不进转录），null 表示不需要说明。
    /// </summary>
    void SetMode(string name, bool readOnly, string? reminder);

    /// <summary>运行时自带的斜杠命令列表变化：(名称, 描述, 参数提示)。</summary>
    event Action<IReadOnlyList<(string Name, string? Description, string? ArgumentHint)>>? CommandsChanged;
}
