using Microsoft.Extensions.Logging.Abstractions;
using TKCode.Agent.Coding.Tools.Agents;
using TKCode.Agent.Coding.Tools.Questions;
using TKCode.AgentHost.Catalog;
using TKCode.AgentHost.Peers;
using TKCode.Ai;
using TKCode.Ai.Fake;
using TKCode.Ai.Messages;
using TKCode.Rpc;
using TKCode.Runtimes;
using Xunit;

namespace TKCode.AgentHost.Tests;

/// <summary>内置 ask_user 经 Host 的 input/request 到 UI（docs/design/ask-user.md §4、§6）。</summary>
public sealed class AskUserTests
{
    private const string Questions =
        """{"questions":[{"question":"Which DB?","header":"Database","options":[{"label":"Postgres","description":"Full"},{"label":"SQLite","description":"Small"}]},{"question":"Which features?","header":"Features","multiSelect":true,"options":[{"label":"Auth","description":"Login"},{"label":"Logs","description":"Logging"}]}]}""";

    private static SessionManager CreateManager()
    {
        var catalog = new ModelCatalog(NullLogger<ModelCatalog>.Instance, new BuiltInCatalog(),
            new ModelDiscovery(new HttpClient(), NullLogger<ModelDiscovery>.Instance));
        return new SessionManager(new WireApiRegistry([new FakeWireApi(TimeSpan.Zero)]), catalog, new RuntimeRegistry([]),
            new PeerDirectory(NullLogger<PeerDirectory>.Instance), new TKCode.Mcp.McpManager(new TKCode.Mcp.McpHost(), null, () => []),
            NullLogger<SessionManager>.Instance);
    }

    private static string TempDir() => Directory.CreateTempSubdirectory("tkcode-ask-").FullName;

    [Fact]
    public async Task AskUser_GoesToTheClient_AndTheAnswerBecomesTheToolResult()
    {
        await using var sessions = CreateManager();
        var client = new QuestionClient(_ => Task.FromResult(new UserInputResponse(UserInputResponse.Answered,
            [new UserAnswerDto("Which DB?", ["SQLite"]), new UserAnswerDto("Which features?", ["Auth"], "audit trail")])));
        sessions.AttachClient(client);
        var session = sessions.Create(TempDir(), FakeWireApi.Models[1]);
        Assert.Contains(((TKCode.Agent.Agent)session.Agent).Tools, t => t.Name == AskUserTool.ToolName);

        await session.Agent.PromptAsync(UserMessage.FromText("mcp:ask_user " + Questions));
        await session.Agent.WaitForIdleAsync();

        var request = Assert.Single(client.Requests);
        Assert.Equal(("builtin", session.Id), (request.Source, request.SessionId));
        Assert.Equal(["Which DB?", "Which features?"], request.Questions.Select(q => q.Id));
        Assert.True(request.Questions[1].MultiSelect);
        Assert.True(request.Questions[0].AllowOther);
        var result = session.Agent.Messages.OfType<ToolResultMessage>().Single();
        Assert.Equal(request.ToolCallId, result.ToolCallId);
        Assert.False(result.IsError);
        Assert.Equal("The user answered: \"Which DB?\"=\"SQLite\", \"Which features?\"=\"Auth, audit trail\". Some answers are in the user's own words; " +
            "read them carefully and follow what they actually say, even if it asks for clarification, a change of course, or that you not proceed.",
            ((TextContent)result.Content[0]).Text);
    }

    [Fact]
    public async Task AskUser_Declined_TellsTheModelToStop()
    {
        await using var sessions = CreateManager();
        sessions.AttachClient(new QuestionClient(_ => Task.FromResult(UserInputResponse.Decline)));
        var session = sessions.Create(TempDir(), FakeWireApi.Models[1]);
        await session.Agent.PromptAsync(UserMessage.FromText("mcp:ask_user " + Questions));
        await session.Agent.WaitForIdleAsync();
        var result = session.Agent.Messages.OfType<ToolResultMessage>().Single();
        Assert.False(result.IsError);
        Assert.Equal(AskUserTool.DeclinedMessage, ((TextContent)result.Content[0]).Text);
    }

    [Fact]
    public async Task AskUser_WithoutClient_ReturnsUnavailable()
    {
        await using var sessions = CreateManager();
        var session = sessions.Create(TempDir(), FakeWireApi.Models[1]);
        await session.Agent.PromptAsync(UserMessage.FromText("mcp:ask_user " + Questions));
        await session.Agent.WaitForIdleAsync();
        var result = session.Agent.Messages.OfType<ToolResultMessage>().Single();
        Assert.True(result.IsError);
        Assert.Equal(AskUserTool.UnavailableMessage, ((TextContent)result.Content[0]).Text);
    }

    [Fact]
    public async Task AskUser_Abort_CancelsTheRequest()
    {
        await using var sessions = CreateManager();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sessions.AttachClient(new QuestionClient(async ct =>
        {
            asked.TrySetResult();
            await using (ct.Register(() => cancelled.TrySetResult()))
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            return UserInputResponse.Decline;
        }));
        var session = sessions.Create(TempDir(), FakeWireApi.Models[1]);
        var run = session.Agent.PromptAsync(UserMessage.FromText("mcp:ask_user " + Questions));
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        session.Agent.Abort();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        var result = session.Agent.Messages.OfType<ToolResultMessage>().Single();
        Assert.Equal("Operation aborted.", ((TextContent)result.Content[0]).Text);
    }

    [Fact]
    public async Task Subagents_DoNotGetAskUser()
    {
        await using var sessions = CreateManager();
        var client = new QuestionClient(_ => Task.FromResult(UserInputResponse.Decline));
        sessions.AttachClient(client);
        var parent = sessions.Create(TempDir(), FakeWireApi.Models[1]);
        // 假模型只在工具存在时才调用它：子 Agent 没有 ask_user，所以不会向用户提问
        var handle = await sessions.Coordinator.SpawnAsync(
            new SubagentRequest(parent.Id, "ask", "mcp:ask_user " + Questions, SubagentTypes.GeneralPurpose, null, null), CancellationToken.None);
        var outcome = await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("completed", outcome.Status);
        Assert.Empty(client.Requests);
    }

    private sealed class QuestionClient(Func<CancellationToken, Task<UserInputResponse>> answer) : IAgentHostClient
    {
        public List<UserInputRequest> Requests { get; } = [];

        public Task<UserInputResponse> RequestUserInputAsync(UserInputRequest request, CancellationToken cancellationToken = default)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }
            return answer(cancellationToken);
        }

        public void OnSessionEvent(SessionEventNotification notification)
        {
        }

        public void OnModelsChanged(ModelsChangedNotification notification)
        {
        }

        public Task<ToolApprovalResponse> RequestToolApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolApprovalResponse(true));

        public void OnMcpChanged(McpChangedNotification notification)
        {
        }

        public Task<McpElicitResponse> RequestMcpElicitationAsync(McpElicitRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new McpElicitResponse("cancel"));

        public Task<McpOpenUrlResponse> OpenMcpUrlAsync(McpOpenUrlRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new McpOpenUrlResponse(false));

        public void OnAssetsChanged(AssetsChangedNotification notification)
        {
        }
    }
}
