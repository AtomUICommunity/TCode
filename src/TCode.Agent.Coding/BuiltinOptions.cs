using TCode.Agent.Coding.Prompt;
using TCode.Agent.Coding.Tools;
using TCode.Agent.Coding.Tools.Tasks;
using TCode.Agent.Coding.Workspace;

namespace TCode.Agent.Coding;

/// <summary>内置 Agent 会话的构建选项。子 Agent 通过共享工作区、后台任务、任务清单与审批记录与父会话协作。</summary>
public sealed record BuiltinOptions
{
    public WorkspaceRoots? Workspace { get; init; }
    public BackgroundTasks? BackgroundTasks { get; init; }
    public TaskList? Tasks { get; init; }
    public SessionApprovals? Approvals { get; init; }
    public ToolSet ToolSet { get; init; } = ToolSet.Full;

    /// <summary>追加在 cwd 节之前的系统提示节（例如子 Agent 的角色说明）。</summary>
    public IReadOnlyList<PromptSection>? ExtraPromptSections { get; init; }

    public ToolServices Services { get; init; } = ToolServices.None;
}

public enum ToolSet
{
    /// <summary>全部工具（主会话）。</summary>
    Full,

    /// <summary>不能再派生子 Agent（general-purpose 子 Agent）。</summary>
    NoSubagents,

    /// <summary>只读：不能写文件、不能派生子 Agent（explore / plan 子 Agent）。</summary>
    ReadOnly,
}
