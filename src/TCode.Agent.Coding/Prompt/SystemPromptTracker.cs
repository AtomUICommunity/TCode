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
    private IReadOnlyList<PromptSection> _initial;
    /// <summary>新会话：开头快照写入会话文件之前，可按当时的动态节重建。</summary>
    private readonly bool _initialMutable;
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
        _initialMutable = recorded is null;
    }

    /// <summary>动态节（MCP 等），在 <see cref="UpdatesAsync"/> 之前由调用方更新。</summary>
    public IReadOnlyList<PromptSection> Dynamic { get; set; } = [];

    private IReadOnlyList<PromptSection>? Extra => Dynamic.Count == 0 ? _extra : [.. _extra ?? [], .. Dynamic];

    /// <summary>当前生效的系统提示（尚未记录时为开头的）是否有这一节。</summary>
    public bool HasSection(string name) => (_recorded ?? _initial).Any(s => s.Name == name);

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
        var current = SystemPromptBuilder.Build(_workspace, _tools, model, extra: Extra);
        if (_recorded is null)
        {
            // 新会话第一轮：开头直接包含当时的动态节（例如已连上的 MCP 服务器），不必再追加补丁
            if (_initialMutable)
            {
                _initial = current;
            }
            await _store.AppendSystemPromptAsync(_initial, CancellationToken.None);
            _recorded = _initial;
        }
        if (SystemPromptBuilder.Diff(_recorded, current) is not { } patch)
        {
            return [];
        }
        await _store.AppendSystemPromptAsync(current, CancellationToken.None);
        _recorded = current;
        return [new SystemMessage(patch)];
    }
}
