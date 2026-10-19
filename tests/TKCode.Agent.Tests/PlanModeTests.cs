using System.Text.Json;
using TKCode.Agent.Coding;
using TKCode.Agent.Coding.Assets;
using TKCode.Agent.Coding.Compaction;
using TKCode.Agent.Coding.Tools;
using TKCode.Agent.Coding.Tools.Plan;
using TKCode.Agent.Coding.Workspace;
using TKCode.Agent.Plans;
using TKCode.Agent.Questions;
using TKCode.Ai;
using TKCode.Ai.Messages;
using Xunit;

namespace TKCode.Agent.Tests;

/// <summary>Plan 模式与计划审批（docs/design/plan-mode.md）：工具过滤、计划文件、同一轮内切换模式、清空上下文、enter_plan_mode。</summary>
public sealed class PlanModeTests
{
    static PlanModeTests()
    {
        // 计划文件在 ~/.tkcode/plans 下：测试进程改到临时目录，不碰真实的家目录
        if (Environment.GetEnvironmentVariable(TKCodePaths.HomeVariable) is not { Length: > 0 })
        {
            Environment.SetEnvironmentVariable(TKCodePaths.HomeVariable, Directory.CreateTempSubdirectory("tkcode-plan-home-").FullName);
        }
    }

    /// <summary>模拟宿主：审批或确认后像 SessionManager 一样切换会话的模式。</summary>
    private sealed class FakePlanService : IPlanModeService
    {
        public AgentSession? Session { get; set; }
        public Func<PlanReviewResult> Review { get; init; } = () => PlanReviewResult.Dismissed;
        public Func<UserInputResult> Enter { get; init; } = () => UserInputResult.Declined;
        public List<string> Plans { get; } = [];
        public List<UserQuestion> Questions { get; } = [];

        public Task<PlanReviewResult> ReviewAsync(string sessionId, string toolCallId, string planPath, string plan, CancellationToken cancellationToken)
        {
            Plans.Add(plan);
            var result = Review();
            if (result.Outcome == PlanReviewOutcome.Approved)
            {
                Session!.Role = result.Choice == PlanReviewChoice.Acp ? SessionRole.Acp : SessionRole.Agent;
            }
            return Task.FromResult(result);
        }

        public Task<UserInputResult> ConfirmEnterAsync(string sessionId, string toolCallId, UserQuestion question, CancellationToken cancellationToken)
        {
            Questions.Add(question);
            var result = Enter();
            if (EnterPlanModeTool.Accepted(result))
            {
                Session!.Role = SessionRole.Plan;
            }
            return Task.FromResult(result);
        }
    }

    private static (AgentSession Session, ScriptedWireApi Api, string Root) Create(FakePlanService? service, params ScriptedWireApi.Turn[] turns)
    {
        var root = Directory.CreateTempSubdirectory("tkcode-plan-").FullName;
        var api = new ScriptedWireApi(turns);
        var session = AgentSession.Create(new WireApiRegistry([api]), ScriptedWireApi.Model, root, Path.Combine(root, ".sessions"),
            options: new BuiltinOptions { Services = new ToolServices { PlanMode = service } });
        if (service is not null)
        {
            service.Session = session;
        }
        return (session, api, root);
    }

    private static string Args(Dictionary<string, string> value) => JsonSerializer.Serialize(value, PlanTestJson.Default.DictionaryStringString);

    private static string WriteArgs(string path, string content) =>
        Args(new Dictionary<string, string> { ["path"] = path, ["content"] = content });

    private static IReadOnlyList<string> ToolNames(LlmContext context) => context.Tools.Select(t => t.Name).ToArray();

    private static string TextOf(ToolResultMessage result) => string.Concat(result.Content.OfType<TextContent>().Select(t => t.Text));

    private static async Task RunAsync(AgentSession session, string prompt)
    {
        await session.Agent.PromptAsync(UserMessage.FromText(prompt));
        await session.Agent.WaitForIdleAsync();
    }

    [Fact]
    public async Task PlanMode_HasPlanTools_AndThePlanFileInTheModeSection()
    {
        var service = new FakePlanService();
        var (session, api, _) = Create(service, ScriptedWireApi.Turn.Reply("ok"));
        session.Role = SessionRole.Plan;
        await RunAsync(session, "plan something");

        var context = Assert.Single(api.Contexts);
        var tools = ToolNames(context);
        Assert.Contains(ExitPlanModeTool.ToolName, tools);
        Assert.Contains("write", tools);
        Assert.Contains("edit", tools);
        Assert.DoesNotContain(EnterPlanModeTool.ToolName, tools);
        var planFile = session.Tools!.PlanFile!;
        Assert.Equal(PlanFiles.PathFor(session.Store.FilePath), planFile);
        Assert.Contains($"Plan file: {planFile}\nIt does not exist yet; create it with write.", context.SystemPrompt);
    }

    [Fact]
    public async Task AgentMode_OffersEnterPlanMode_NotExitPlanMode()
    {
        var (session, api, _) = Create(new FakePlanService(), ScriptedWireApi.Turn.Reply("ok"));
        await RunAsync(session, "hi");
        var tools = ToolNames(Assert.Single(api.Contexts));
        Assert.Contains(EnterPlanModeTool.ToolName, tools);
        Assert.DoesNotContain(ExitPlanModeTool.ToolName, tools);
    }

    [Fact]
    public async Task WithoutPlanService_PlanModeFallsBackToAReplyPlan()
    {
        var (session, api, _) = Create(null, ScriptedWireApi.Turn.Reply("ok"));
        session.Role = SessionRole.Plan;
        await RunAsync(session, "plan something");
        var context = Assert.Single(api.Contexts);
        Assert.DoesNotContain(ExitPlanModeTool.ToolName, ToolNames(context));
        Assert.DoesNotContain("write", ToolNames(context));
        Assert.Contains("reply with the plan in Markdown", context.SystemPrompt);
        Assert.Null(session.Tools!.PlanFile);
    }

    [Fact]
    public async Task PlanMode_WritesOnlyThePlanFile()
    {
        var (session, _) = CreateDeferred(new FakePlanService(), (session, i) => i switch
        {
            0 => ScriptedWireApi.Turn.Tools(("write", WriteArgs(Path.Combine(session.Cwd, "src.txt"), "x"))),
            1 => ScriptedWireApi.Turn.Tools(("write", WriteArgs(session.Tools!.PlanFile!, "# Plan"))),
            _ => ScriptedWireApi.Turn.Reply("done"),
        });
        session.Role = SessionRole.Plan;
        await RunAsync(session, "plan");

        var results = session.Agent.Messages.OfType<ToolResultMessage>().ToArray();
        Assert.Equal(2, results.Length);
        Assert.True(results[0].IsError);
        Assert.Equal(session.Tools!.PlanModeWriteMessage, TextOf(results[0]));
        Assert.False(File.Exists(Path.Combine(session.Cwd, "src.txt")));
        Assert.False(results[1].IsError, TextOf(results[1]));
        Assert.Equal("# Plan", File.ReadAllText(session.Tools.PlanFile!));
    }

    /// <summary>按回调逐轮回复：回调可以读取会话（例如计划文件路径）。</summary>
    private sealed class DeferredApi(Func<int, LlmContext, ScriptedWireApi.Turn> next) : IWireApi
    {
        public string Api => "scripted";
        public List<LlmContext> Contexts { get; } = [];

        public TKCode.Ai.Streaming.AssistantMessageStream Stream(TKCode.Ai.Models.ModelInfo model, LlmContext context, StreamOptions options,
            CancellationToken cancellationToken)
        {
            ScriptedWireApi.Turn turn;
            lock (Contexts)
            {
                Contexts.Add(context);
                turn = next(Contexts.Count - 1, context);
            }
            return new ScriptedWireApi(turn).Stream(model, context, options, cancellationToken);
        }
    }

    private static (AgentSession Session, DeferredApi Api) CreateDeferred(FakePlanService service, Func<AgentSession, int, ScriptedWireApi.Turn> next)
    {
        var root = Directory.CreateTempSubdirectory("tkcode-plan-").FullName;
        AgentSession? session = null;
        var api = new DeferredApi((i, _) => next(session!, i));
        session = AgentSession.Create(new WireApiRegistry([api]), ScriptedWireApi.Model, root, Path.Combine(root, ".sessions"),
            options: new BuiltinOptions { Services = new ToolServices { PlanMode = service } });
        service.Session = session;
        return (session, api);
    }

    /// <summary>写计划 → exit_plan_mode → 给定的审批结果 → 之后一轮回复 "done"。</summary>
    private static (AgentSession Session, DeferredApi Api) PlanFlow(FakePlanService service, string plan = "# Plan\n\n- step one") =>
        CreateDeferred(service, (session, i) => i switch
        {
            0 => ScriptedWireApi.Turn.Tools(("write", WriteArgs(session.Tools!.PlanFile!, plan))),
            1 => ScriptedWireApi.Turn.Tools((ExitPlanModeTool.ToolName, "{}")),
            _ => ScriptedWireApi.Turn.Reply("done"),
        });

    [Fact]
    public async Task ExitPlanMode_Approved_SwitchesToAgentInTheSameTurn()
    {
        var service = new FakePlanService { Review = () => new PlanReviewResult(PlanReviewOutcome.Approved, PlanReviewChoice.Auto) };
        var (session, api) = PlanFlow(service);
        session.Role = SessionRole.Plan;
        await RunAsync(session, "plan it");

        Assert.Equal(["# Plan\n\n- step one"], service.Plans);
        Assert.Equal(3, api.Contexts.Count);
        Assert.Contains(ExitPlanModeTool.ToolName, ToolNames(api.Contexts[1]));
        // 批准后的下一次请求：Agent 的工具集，没有 exit_plan_mode
        var after = api.Contexts[2];
        Assert.DoesNotContain(ExitPlanModeTool.ToolName, ToolNames(after));
        Assert.Contains(EnterPlanModeTool.ToolName, ToolNames(after));
        // 工具结果之后紧跟模式节补丁
        var messages = after.Messages;
        var resultIndex = messages.ToList().FindLastIndex(m => m is ToolResultMessage { ToolName: ExitPlanModeTool.ToolName });
        var result = (ToolResultMessage)messages[resultIndex];
        Assert.False(result.IsError);
        Assert.StartsWith("The user approved the plan. Plan mode is over", TextOf(result));
        Assert.Contains("## Approved plan\n\n# Plan\n\n- step one", TextOf(result));
        var patch = Assert.IsType<SystemMessage>(messages[resultIndex + 1]);
        Assert.Contains("You are in Agent mode again", patch.Text);
        Assert.Contains($"The plan for this session is saved at {session.Tools!.PlanFile}.", patch.Text);
        using var details = JsonDocument.Parse(result.Details!);
        Assert.Equal("approved", details.RootElement.GetProperty("status").GetString());
        Assert.Equal("auto", details.RootElement.GetProperty("choice").GetString());
    }

    [Fact]
    public async Task ExitPlanMode_EditedPlan_IsWrittenBackAndFollowed()
    {
        var service = new FakePlanService
        {
            Review = () => new PlanReviewResult(PlanReviewOutcome.Approved, PlanReviewChoice.Ask, EditedPlan: "# Edited plan"),
        };
        var (session, api) = PlanFlow(service);
        session.Role = SessionRole.Plan;
        await RunAsync(session, "plan it");

        var result = session.Agent.Messages.OfType<ToolResultMessage>().Last();
        Assert.Contains("The user edited the plan before approving it", TextOf(result));
        Assert.Contains("## Approved plan\n\n# Edited plan", TextOf(result));
        Assert.Equal("# Edited plan", File.ReadAllText(session.Tools!.PlanFile!).Trim());
        Assert.Equal(3, api.Contexts.Count);
    }

    [Fact]
    public async Task ExitPlanMode_Revise_StaysInPlanWithTheFeedback()
    {
        var service = new FakePlanService { Review = () => new PlanReviewResult(PlanReviewOutcome.Revise, Feedback: "Use SQLite instead.") };
        var (session, api) = PlanFlow(service);
        session.Role = SessionRole.Plan;
        await RunAsync(session, "plan it");

        var result = session.Agent.Messages.OfType<ToolResultMessage>().Last();
        Assert.Equal(ExitPlanModeTool.FeedbackText("Use SQLite instead."), TextOf(result));
        Assert.Contains(ExitPlanModeTool.ToolName, ToolNames(api.Contexts[2]));
        Assert.DoesNotContain(api.Contexts[2].Messages, m => m is SystemMessage s && s.Text.Contains("Agent mode again"));
        Assert.Equal(SessionRole.PlanName, session.Role.Name);
    }

    [Fact]
    public async Task ExitPlanMode_Dismissed_TellsTheModelToEndItsTurn()
    {
        var (session, _) = PlanFlow(new FakePlanService());
        session.Role = SessionRole.Plan;
        await RunAsync(session, "plan it");
        var result = session.Agent.Messages.OfType<ToolResultMessage>().Last();
        Assert.Equal(ExitPlanModeTool.NotApprovedMessage, TextOf(result));
        Assert.False(result.IsError);
    }

    [Fact]
    public async Task ExitPlanMode_WithoutAPlanFile_IsAnError()
    {
        var service = new FakePlanService();
        var (session, _) = CreateDeferred(service, (_, i) => i == 0
            ? ScriptedWireApi.Turn.Tools((ExitPlanModeTool.ToolName, "{}"))
            : ScriptedWireApi.Turn.Reply("done"));
        session.Role = SessionRole.Plan;
        await RunAsync(session, "plan it");
        var result = session.Agent.Messages.OfType<ToolResultMessage>().Single();
        Assert.True(result.IsError);
        Assert.Equal(ExitPlanModeTool.NoPlanMessage(session.Tools!.PlanFile!), TextOf(result));
        Assert.Empty(service.Plans);
    }

    [Fact]
    public async Task ExitPlanMode_ClearContext_ReplacesHistoryWithTheHandoff()
    {
        var service = new FakePlanService { Review = () => new PlanReviewResult(PlanReviewOutcome.Approved, PlanReviewChoice.Clear) };
        var (session, api) = PlanFlow(service);
        session.Role = SessionRole.Plan;
        await RunAsync(session, "plan it");

        Assert.Equal(3, api.Contexts.Count);
        var after = api.Contexts[2];
        var first = Assert.IsType<UserMessage>(after.Messages[0]);
        Assert.Equal(CompactionPrompts.HandoffSource, first.Source);
        var text = string.Concat(first.Content.OfType<TextContent>().Select(t => t.Text));
        Assert.StartsWith("The user approved the plan below in Plan mode and cleared the earlier conversation.", text);
        Assert.Contains("# Plan\n\n- step one", text);
        Assert.Contains(session.Store.FilePath, text);
        // 原来的对话不再发给模型；系统提示重建，模式节是切回 Agent 的说明
        Assert.DoesNotContain(after.Messages, m => m is UserMessage { Source: null });
        Assert.DoesNotContain(after.Messages, m => m is ToolResultMessage);
        Assert.Contains("You are in Agent mode again", after.SystemPrompt);
        Assert.DoesNotContain(ExitPlanModeTool.ToolName, ToolNames(after));

        // 会话文件：重新打开后上下文从交接消息开始
        var snapshot = TKCode.Agent.Coding.Sessions.SessionStore.Read(session.Store.FilePath);
        Assert.Equal(TKCode.Agent.Compaction.CompactionTrigger.PlanHandoff, snapshot.Compaction!.Trigger);
        Assert.Equal(CompactionPrompts.HandoffSource, Assert.IsType<UserMessage>(snapshot.Messages[0]).Source);
    }

    [Fact]
    public async Task EnterPlanMode_Accepted_SwitchesToPlanInTheSameTurn()
    {
        var service = new FakePlanService
        {
            Enter = () => new UserInputResult(UserInputOutcome.Answered, [new UserAnswer(EnterPlanModeTool.QuestionId, [EnterPlanModeTool.PlanOption])]),
        };
        var (session, api) = CreateDeferred(service, (_, i) => i == 0
            ? ScriptedWireApi.Turn.Tools((EnterPlanModeTool.ToolName, """{"reason":"Several designs are possible."}"""))
            : ScriptedWireApi.Turn.Reply("investigating"));
        await RunAsync(session, "add caching");

        var question = Assert.Single(service.Questions);
        Assert.Equal("Several designs are possible.", question.Question);
        Assert.False(question.AllowOther);
        var result = session.Agent.Messages.OfType<ToolResultMessage>().Single();
        Assert.Equal(EnterPlanModeTool.AcceptedMessage, TextOf(result));
        var after = api.Contexts[1];
        Assert.Contains(ExitPlanModeTool.ToolName, ToolNames(after));
        Assert.Contains(after.Messages, m => m is SystemMessage s && s.Text.Contains("You are in Plan mode."));
    }

    [Fact]
    public async Task EnterPlanMode_Declined_StaysInAgent()
    {
        var service = new FakePlanService
        {
            Enter = () => new UserInputResult(UserInputOutcome.Answered, [new UserAnswer(EnterPlanModeTool.QuestionId, [EnterPlanModeTool.ProceedOption])]),
        };
        var (session, api) = CreateDeferred(service, (_, i) => i == 0
            ? ScriptedWireApi.Turn.Tools((EnterPlanModeTool.ToolName, "{}"))
            : ScriptedWireApi.Turn.Reply("ok"));
        await RunAsync(session, "add caching");
        Assert.Equal(EnterPlanModeTool.DeclinedMessage, TextOf(session.Agent.Messages.OfType<ToolResultMessage>().Single()));
        Assert.Equal(SessionRole.AgentName, session.Role.Name);
        Assert.DoesNotContain(ExitPlanModeTool.ToolName, ToolNames(api.Contexts[1]));
    }

    [Theory]
    [InlineData(PermissionMode.AutoApprove)]
    [InlineData(PermissionMode.FullAccess)]
    public void ReadOnlyContext_AsksForCommandsThatMayChangeTheWorkspace(PermissionMode mode)
    {
        var root = Directory.CreateTempSubdirectory("tkcode-plan-").FullName;
        var workspace = new WorkspaceRoots(root);
        var approvals = new SessionApprovals();
        approvals.Add("command:git commit");
        var write = new ToolPermission(ToolAccess.Execute, Command: "git commit -m x", Title: "git commit -m x");
        var verdict = PermissionPolicy.Evaluate(write, mode, workspace, approvals, readOnly: true);
        Assert.False(verdict.Allowed);
        Assert.Equal(PermissionPolicy.ReadOnlyCommandKind, verdict.Kind);
        Assert.Null(verdict.SessionKey);
        Assert.True(PermissionPolicy.Evaluate(new ToolPermission(ToolAccess.Execute, Command: "ls -la"), mode, workspace, approvals, readOnly: true).Allowed);
        // 不在只读上下文：照旧
        Assert.True(PermissionPolicy.Evaluate(write, mode, workspace, approvals).Allowed);
    }

    [Fact]
    public void PlanReviewDetails_RecordOutcomeChoiceAndSnapshot()
    {
        var json = PlanReviews.DetailsJson("/p.md", "# A", new PlanReviewResult(PlanReviewOutcome.Approved, PlanReviewChoice.Acp, EditedPlan: "# B"));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("planReview", root.GetProperty("kind").GetString());
        Assert.Equal("approved", root.GetProperty("status").GetString());
        Assert.Equal("acp", root.GetProperty("choice").GetString());
        Assert.Equal("# B", root.GetProperty("plan").GetString());
        Assert.True(root.GetProperty("edited").GetBoolean());
        using var pending = JsonDocument.Parse(PlanReviews.DetailsJson("/p.md", "# A", null));
        Assert.Equal("pending", pending.RootElement.GetProperty("status").GetString());
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class PlanTestJson : System.Text.Json.Serialization.JsonSerializerContext;
