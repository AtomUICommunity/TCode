namespace TKCode.Agent.Coding.Assets;

/// <summary>
/// 会话的 role（docs/design/composer.md §10.3，类似 Cursor 的模式）：限制可用的工具，并在系统提示中加入 role 节。
/// 内置 agent / plan / acp，自定义 role 来自 ~/.tkcode/roles 与 .agents/roles。每批用户输入前生效（同一轮内不变）。
/// </summary>
/// <param name="Name">role 名（agent / plan / acp / 自定义文件名）。</param>
/// <param name="Instructions">追加到系统提示的指令；null 表示不加。</param>
/// <param name="Tools">all、readonly，或工具名列表（支持 mcp__server__* 通配）。</param>
/// <param name="Model">自定义 role 指定的模型引用；切换由界面完成。</param>
public sealed record SessionRole(string Name, string? Instructions, IReadOnlyList<string> Tools, string? Model = null)
{
    public const string AgentName = "agent";
    public const string PlanName = "plan";
    public const string AcpName = "acp";

    public static readonly string[] BuiltinNames = [AgentName, PlanName, AcpName];

    // 只读 role 去掉的工具：写文件与派生子 Agent（与 ToolSet.ReadOnly 一致）；MCP 工具可能有副作用，也去掉
    private static readonly HashSet<string> WritingTools = new(StringComparer.Ordinal) { "write", "edit", "agent" };

    public static SessionRole Agent { get; } = new(AgentName, null, ["all"]);

    // 提示词的设计与原则见 docs/design/composer.md §18、docs/design/plan-mode.md §12

    /// <summary>
    /// Plan（docs/design/plan-mode.md）：调研、澄清后把计划写进计划文件，用 exit_plan_mode 请求批准。
    /// write / edit 只放行计划文件（ToolContext.InPlanMode），子 Agent 只能是只读的 explore / plan。
    /// 模式节里的 {planPath} / {planFileState} 由 <see cref="SectionFor"/> 填入。
    /// </summary>
    public static SessionRole Plan { get; } = new(PlanName, """
        You are in Plan mode. The user wants to review a plan before anything is changed. Investigate, settle open decisions with the user, write the plan to the plan file, and then ask for approval with exit_plan_mode. Do not implement the plan.

        Plan file: {planPath}
        {planFileState} This is the only file you may write or edit in this mode.

        Work in this order:
        1. Understand the request and the code it touches. Read files yourself for focused questions. For broad questions, launch up to three explore subagents in parallel with the agent tool, each with its own focus. Keep facts you verified separate from assumptions.
        2. For a design with several reasonable options, you may launch a plan subagent with what you found, then check its proposal against the code.
        3. If a choice would materially change scope, behavior, compatibility, data or user experience, and the code does not settle it, ask the user with ask_user. Ask only what you cannot find out yourself.
        4. Write the plan to the plan file in Markdown:
           - Context: the problem, and what will be true when the work is done.
           - Approach: the recommended design and why. Mention a rejected alternative only in a sentence, and only if the user is likely to ask about it.
           - Changes: every file to create or modify, and what changes in it.
           - Edge cases and risks.
           - Verification: the tests and checks that will prove it works.
           Keep it short enough to scan and specific enough to carry out without this conversation.
        5. Call exit_plan_mode. If the user asks for changes, update the plan file and call exit_plan_mode again.

        While you are planning a change, end your turn only by calling ask_user or exit_plan_mode. Do not ask whether the plan is acceptable in any other way: not in text, and not with ask_user. If the user says in a message that they approve, call exit_plan_mode so they can choose how to proceed. If the user only asks a question or wants research rather than a change, answer directly; do not write a plan or call exit_plan_mode.

        Constraints: apart from the plan file you cannot write or edit files or use MCP tools in this mode, and subagents you launch are read-only. Commands you run must not modify the workspace; any command that might will need the user's approval. Never claim that something was changed.
        """, ["readonly", "write", "edit", "agent"]);

    private const string PlanFileMissing = "It does not exist yet; create it with write.";
    private const string PlanFileExists = "A plan from earlier in this session is already there: read it, then update it rather than starting over.";

    /// <summary>
    /// 没有计划文件与审批服务时（没有宿主的会话，例如测试）的 Plan：以一段回复给出计划，由用户手动切换模式。
    /// </summary>
    public static SessionRole PlanWithoutApproval { get; } = new(PlanName, """
        You are in Plan mode. Turn the user's request into one implementation plan that can be reviewed and then carried out. Do not implement it.

        Work in this order:
        1. Investigate the workspace with the read-only tools available to you. Keep facts you verified in the code separate from assumptions, and say which is which.
        2. If a choice would materially change scope, behavior, compatibility, data or user experience, and the code does not settle it, ask the user with ask_user. Ask only what you cannot find out yourself, and do not write the plan until the answers are in.
        3. When the requirements are clear, reply with the plan in Markdown:
           - Goal: what will be true when the work is done.
           - Approach: the design and why, including alternatives you rejected.
           - Changes: every file to create or modify, and what changes in it.
           - Edge cases and risks.
           - Verification: the tests and checks that will prove it works.
           - Open questions, if any remain.
           Do not use ask_user to ask whether the plan is acceptable; end your turn with the plan.
        4. If the user asks for revisions, reply with the complete updated plan, not only the changed parts.

        Constraints: you cannot write or edit files, launch subagents or use MCP tools in this mode, and any command you run must not modify the workspace. Never claim that something was changed. When the user approves the plan, they will switch to Agent or ACP mode to carry it out.
        """, ["readonly"]);

    /// <summary>
    /// ACP（协调模式，参考 NextCoWork 的 acp 模式）：主 Agent 只负责设计架构、拆分任务、派发给子 Agent 并整合结果；
    /// 自己不写文件、不执行命令。子 Agent 仍是完整的 general-purpose Agent，其中的每次工具调用照常审批。
    /// </summary>
    public static SessionRole Acp { get; } = new(AcpName, """
        You are the coordinating agent in ACP mode. You own the architecture, the task breakdown, delegation to subagents, integration of their results and the final report. You do not implement changes yourself.

        Work in this order:
        1. Investigate enough of the workspace to define the architecture and clear task boundaries.
        2. If a choice would materially change scope or behavior and the code does not settle it, ask the user with ask_user before dispatching any work.
        3. Record the plan with task_create: one task per unit of delegated work, with the files it owns. Keep the tasks updated with task_update as work progresses.
        4. Dispatch each task to a general-purpose subagent with the agent tool. A subagent cannot see this conversation, so its prompt must contain:
           - the goal and how it fits the overall design;
           - the relevant facts you found, with file paths;
           - the files it owns, and the files it must not touch;
           - constraints and conventions to follow;
           - how to verify its work, and what to report back (changes made, verification run and its result, open problems).
        5. Run independent tasks in parallel, by issuing several agent calls in one response, only when their files do not overlap. Run dependent or overlapping tasks one after another.
        6. Review every report against the plan. Resolve gaps or conflicts with further delegated tasks; send_message reaches a named subagent again.
        7. Before finishing, delegate verification (tests, build, or a review of the combined changes) to a subagent.
        8. Finish with a report: what was done and by which task, the verification evidence, and anything left unresolved.

        Constraints: you cannot write or edit files, run commands or use MCP tools in this mode; you may read and search to plan and to check results. Never state that something was changed or verified unless a subagent reported it.
        """,
        ["read", "ls", "glob", "grep", "web_fetch", "web_search", "skill", "task_create", "task_get", "task_list", "task_update", "task_stop",
         "agent", "send_message", "list_agents", "ask_user"]);

    /// <summary>从其他模式切回 Agent 时的模式节（新会话一开始就是 Agent 时没有这一节）；{planLine} 由 <see cref="SectionFor"/> 填入。</summary>
    public static SessionRole AgentResumed { get; } = new(AgentName,
        "You are in Agent mode again: you can read, edit and write files and run commands to carry out the user's task.{planLine} " +
        "If a plan was agreed earlier in this conversation, follow it unless the user says otherwise, and say where you deviate from it.",
        ["all"]);

    /// <summary>系统提示中模式节的名称。</summary>
    public const string SectionName = "mode";

    // ---- 外部运行时（docs/design/composer.md §18.8）：工具无法过滤、工具名不同，模式说明随用户消息发送一次 ----

    private const string ExternalPlan = """
        Turn the user's request into one implementation plan that can be reviewed and then carried out. Do not implement it.

        Work in this order:
        1. Investigate the workspace with read-only actions. Keep facts you verified in the code separate from assumptions, and say which is which.
        2. If a choice would materially change scope, behavior, compatibility, data or user experience, and the code does not settle it, ask the user. Ask a few focused questions, then end your turn. Do not guess, and do not write the plan until the answers are in.
        3. When the requirements are clear, reply with the plan in Markdown:
           - Goal: what will be true when the work is done.
           - Approach: the design and why, including alternatives you rejected.
           - Changes: every file to create or modify, and what changes in it.
           - Edge cases and risks.
           - Verification: the tests and checks that will prove it works.
           - Open questions, if any remain.
        4. If the user asks for revisions, reply with the complete updated plan, not only the changed parts.

        Constraints: do not modify the workspace in this mode: no file edits and no commands that change anything. Never claim that something was changed. When the user approves the plan, they will switch to Agent or ACP mode to carry it out.
        """;

    private const string ExternalAcp = """
        You are the coordinating agent. You own the architecture, the task breakdown, delegation to subagents, integration of their results and the final report. You do not implement changes yourself.

        Work in this order:
        1. Investigate enough of the workspace to define the architecture and clear task boundaries.
        2. If a choice would materially change scope or behavior and the code does not settle it, ask the user and end your turn before dispatching any work.
        3. Record the plan in your task list: one task per unit of delegated work, with the files it owns. Keep it updated as work progresses.
        4. Dispatch each task to a subagent with your subagent tool. A subagent cannot see this conversation, so its prompt must contain:
           - the goal and how it fits the overall design;
           - the relevant facts you found, with file paths;
           - the files it owns, and the files it must not touch;
           - constraints and conventions to follow;
           - how to verify its work, and what to report back (changes made, verification run and its result, open problems).
        5. Run independent tasks in parallel, by launching several subagents in one response, only when their files do not overlap. Run dependent or overlapping tasks one after another.
        6. Review every report against the plan. Resolve gaps or conflicts with further delegated tasks.
        7. Before finishing, delegate verification (tests, build, or a review of the combined changes) to a subagent.
        8. Finish with a report: what was done and by which task, the verification evidence, and anything left unresolved.

        Constraints: do not write or edit files or run commands yourself, even though those tools may be available to you; you may read and search to plan and to check results. Never state that something was changed or verified unless a subagent reported it.
        """;

    private const string ExternalAgentResumed =
        "You can read, edit and write files and run commands again to carry out the user's task. " +
        "If a plan was agreed earlier in this conversation, follow it unless the user says otherwise, and say where you deviate from it.";

    /// <summary>
    /// 外部运行时在模式变化后第一轮收到的说明（包在 &lt;system-reminder&gt; 中）；模式没变时为 null。
    /// 切回 Agent 时说明“可以动手了”；新会话一开始就是 Agent 时不发。
    /// </summary>
    public static string? ExternalReminder(SessionRole previous, SessionRole next, string? displayName = null)
    {
        if (string.Equals(previous.Name, next.Name, StringComparison.OrdinalIgnoreCase) && previous.Instructions == next.Instructions)
        {
            return null;
        }
        var title = displayName ?? next.Name switch { PlanName => "Plan", AcpName => "ACP", AgentName => "Agent", var name => name };
        var text = next.Name switch
        {
            AgentName => ExternalAgentResumed,
            PlanName => ExternalPlan,
            AcpName => ExternalAcp,
            _ => next.Instructions,
        };
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        return $"<system-reminder>\nThis conversation is now in {title} mode. Follow these instructions until the mode changes again:\n\n{text.Trim()}\n</system-reminder>";
    }

    public bool IsReadOnly => Tools.Any(t => t.Equals("readonly", StringComparison.OrdinalIgnoreCase));

    /// <summary>按名称解析：内置 role 或 cwd 所在项目的自定义 role；找不到时为 null。</summary>
    public static SessionRole? Resolve(string? name, AgentAssetCatalog catalog)
    {
        switch (name?.ToLowerInvariant())
        {
            case null or "" or AgentName:
                return Agent;
            case PlanName:
                return Plan;
            case AcpName:
                return Acp;
        }
        if (catalog.FindRole(name) is not { } asset)
        {
            return null;
        }
        var body = AgentAssetStore.ReadBody(asset.Path)?.Trim();
        return new SessionRole(asset.Name, string.IsNullOrEmpty(body) ? null : body, asset.Tools ?? ["all"], asset.Model);
    }

    /// <summary>这个 role 下工具是否可用。</summary>
    public bool Allows(IAgentTool tool)
    {
        // 模式专属工具（docs/design/plan-mode.md §8）：与 Tools 列表无关
        if (tool.Name == global::TKCode.Agent.Coding.Tools.Plan.ExitPlanModeTool.ToolName)
        {
            return Name == PlanName && !ReferenceEquals(this, PlanWithoutApproval);
        }
        if (tool.Name == global::TKCode.Agent.Coding.Tools.Plan.EnterPlanModeTool.ToolName)
        {
            return Name == AgentName;
        }
        foreach (var entry in Tools)
        {
            if (entry.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (entry.Equals("readonly", StringComparison.OrdinalIgnoreCase))
            {
                if (!WritingTools.Contains(tool.Name) && !tool.Name.StartsWith("mcp__", StringComparison.Ordinal))
                {
                    return true;
                }
                continue;
            }
            if (entry.EndsWith('*') ? tool.Name.StartsWith(entry[..^1], StringComparison.Ordinal) : tool.Name == entry)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>系统提示的模式节；agent 没有。</summary>
    public Prompt.PromptSection? Section => SectionFor(null, false);

    /// <summary>
    /// 填入计划文件后的模式节（Plan 与切回 Agent）：<paramref name="planFile"/> 为 null 时不提计划文件。
    /// <paramref name="planExists"/> 只在进入 Plan 时取一次，避免模型写文件后模式节反复变化。
    /// </summary>
    public Prompt.PromptSection? SectionFor(string? planFile, bool planExists)
    {
        if (Instructions is not { Length: > 0 } text)
        {
            return null;
        }
        if (ReferenceEquals(this, Plan) && planFile is not null)
        {
            text = text.Replace("{planPath}", planFile).Replace("{planFileState}", planExists ? PlanFileExists : PlanFileMissing);
        }
        else if (ReferenceEquals(this, AgentResumed))
        {
            text = text.Replace("{planLine}", planFile is not null && planExists ? $" The plan for this session is saved at {planFile}." : "");
        }
        return new Prompt.PromptSection(SectionName, text);
    }
}
