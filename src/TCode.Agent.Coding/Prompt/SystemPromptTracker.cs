using TCode.Agent.Coding.Sessions;
using TCode.Agent.Coding.Workspace;
using TCode.Ai.Messages;
using TCode.Ai.Models;

namespace TCode.Agent.Coding.Prompt;

/// <summary>
/// 维护内置 Agent 会话的系统提示：开头的系统提示冻结（保证 prompt cache 与历史前缀稳定），
/// 每批用户输入前重新计算各节，有变化时记录新快照并返回一条 SystemMessage 补丁。
/// </summary>
public sealed class SystemPromptTracker
{
    private readonly SessionStore _store;
    private readonly WorkspaceRoots _workspace;
    private readonly IReadOnlyList<IAgentTool> _tools;
    private readonly IReadOnlyList<PromptSection>? _extra;
    private readonly IReadOnlyList<PromptSection> _initial;
    /// <summary>会话文件中当前生效的快照；null 表示尚未记录（新会话在第一次运行时写入开头快照）。</summary>
    private IReadOnlyList<PromptSection>? _recorded;

    private SystemPromptTracker(SessionStore store, WorkspaceRoots workspace, IReadOnlyList<IAgentTool> tools,
        IReadOnlyList<PromptSection> initial, IReadOnlyList<PromptSection>? recorded, IReadOnlyList<PromptSection>? extra)
    {
        _extra = extra;
        _store = store;
        _workspace = workspace;
        _tools = tools;
        _initial = initial;
        _recorded = recorded;
    }

    /// <summary>发给模型的开头系统提示。</summary>
    public string InitialPrompt => SystemPromptBuilder.Render(_initial);

    public static SystemPromptTracker Create(SessionStore store, WorkspaceRoots workspace, IReadOnlyList<IAgentTool> tools, ModelInfo model,
        IReadOnlyList<PromptSection>? extra = null) =>
        new(store, workspace, tools, SystemPromptBuilder.Build(workspace, tools, model, extra: extra), null, extra);

    /// <summary>恢复会话：沿用记录的开头与当前快照；旧会话没有记录时按当前环境重建。</summary>
    public static SystemPromptTracker Restore(SessionStore store, SessionSnapshot snapshot, WorkspaceRoots workspace,
        IReadOnlyList<IAgentTool> tools, ModelInfo model, IReadOnlyList<PromptSection>? extra = null) =>
        snapshot.InitialSystemPrompt is { } initial
            ? new(store, workspace, tools, initial, snapshot.SystemPrompt ?? initial, extra)
            : Create(store, workspace, tools, model, extra);

    public async ValueTask<IReadOnlyList<Message>> UpdatesAsync(ModelInfo model, CancellationToken cancellationToken)
    {
        if (_recorded is null)
        {
            await _store.AppendSystemPromptAsync(_initial, CancellationToken.None);
            _recorded = _initial;
        }
        var current = SystemPromptBuilder.Build(_workspace, _tools, model, extra: _extra);
        if (SystemPromptBuilder.Diff(_recorded, current) is not { } patch)
        {
            return [];
        }
        await _store.AppendSystemPromptAsync(current, CancellationToken.None);
        _recorded = current;
        return [new SystemMessage(patch)];
    }
}
