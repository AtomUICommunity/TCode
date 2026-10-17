using TCode.Agent.Coding;
using TCode.Agent.Coding.Compaction;
using TCode.Agent.Coding.Sessions;
using TCode.Agent.Compaction;
using TCode.Ai;
using TCode.Ai.Messages;
using TCode.Ai.Models;
using TCode.Ai.Streaming;
using Xunit;

namespace TCode.Agent.Tests;

/// <summary>上下文压缩（docs/design/compaction.md）：计量与阈值、超窗识别、摘要请求与重试、历史替换与会话文件。</summary>
public sealed class CompactionTests
{
    /// <summary>窗口 60K、输出 4K：阈值 = 60K − 4K − 13K = 43K。</summary>
    private static readonly ModelInfo Model = new()
    {
        Id = "compact-test", Name = "Compact Test", Provider = "fake", Api = "lambda", ContextWindow = 60_000, MaxTokens = 4_000,
    };

    /// <summary>按处理函数逐次回复的模拟协议；记录每次请求的上下文与参数。</summary>
    private sealed class LambdaWireApi(Func<LlmContext, StreamOptions, Reply> handler) : IWireApi
    {
        public string Api => "lambda";
        public List<(LlmContext Context, StreamOptions Options)> Requests { get; } = [];

        public AssistantMessageStream Stream(ModelInfo model, LlmContext context, StreamOptions options, CancellationToken cancellationToken)
        {
            Reply reply;
            lock (Requests)
            {
                Requests.Add((context, options));
                reply = handler(context, options);
            }
            var stream = new AssistantMessageStream();
            var builder = new AssistantMessageBuilder(model, stream) { Usage = reply.Usage ?? Usage.Empty };
            builder.Start();
            if (reply.Error is { } error)
            {
                builder.Fail(new Exception(error), aborted: false);
                return stream;
            }
            foreach (var (name, args) in reply.Calls ?? [])
            {
                var call = builder.BeginToolCall($"call_{Guid.NewGuid():N}"[..16], name);
                builder.AppendDelta(call, args);
                builder.EndBlock(call);
            }
            if (reply.Text is { } text)
            {
                var index = builder.BeginText();
                builder.AppendDelta(index, text);
                builder.EndBlock(index);
            }
            builder.Finish();
            return stream;
        }
    }

    private sealed record Reply(string? Text = null, Usage? Usage = null, string? Error = null, IReadOnlyList<(string, string)>? Calls = null);

    private static bool IsSummaryRequest(LlmContext context) =>
        context.Messages.LastOrDefault() is UserMessage u && ContextAccounting.TextOf(u.Content).StartsWith("CRITICAL: Respond with TEXT ONLY", StringComparison.Ordinal);

    private static Usage Tokens(long input) => new(input, 100, 0, 0);

    private static (AgentSession Session, LambdaWireApi Api) CreateSession(Func<LlmContext, StreamOptions, Reply> handler, string? root = null)
    {
        root ??= Directory.CreateTempSubdirectory("tcode-compact-").FullName;
        var api = new LambdaWireApi(handler);
        var session = AgentSession.Create(new WireApiRegistry([api]), Model, root, Path.Combine(root, "sessions"));
        return (session, api);
    }

    private static List<AgentEvent> Record(AgentSession session)
    {
        var events = new List<AgentEvent>();
        session.Agent.Subscribe((e, _) =>
        {
            lock (events)
            {
                events.Add(e);
            }
            return ValueTask.CompletedTask;
        });
        return events;
    }

    [Fact]
    public void Thresholds_FollowClaudeCodeFormula_WithSmallWindowFloor()
    {
        var big = new ModelInfo { Id = "m", Name = "m", Provider = "p", Api = "x", ContextWindow = 1_050_000, MaxTokens = 128_000 };
        var t = ContextAccounting.Thresholds(272_000, big);
        Assert.Equal(272_000 - 20_000 - 13_000, t.AutoCompactAt);
        Assert.Equal(t.AutoCompactAt - 20_000, t.WarnAt);
        Assert.Equal(1_050_000 - 20_000 - 3_000, t.BlockAt);
        Assert.Equal(ContextLevel.Warn, t.LevelOf(t.WarnAt));
        Assert.Equal(ContextLevel.Compact, t.LevelOf(t.AutoCompactAt));

        // 小窗口：纯减法会得到 2.6K，至少取有效窗口的 60%
        var small = new ModelInfo { Id = "s", Name = "s", Provider = "p", Api = "x", ContextWindow = 32_000, MaxTokens = 16_384 };
        Assert.Equal((long)((32_000 - 16_384) * 0.6), ContextAccounting.Thresholds(0, small).AutoCompactAt);
    }

    [Fact]
    public void ContextTokens_LastUsagePlusEstimateOfLaterMessages()
    {
        var assistant = new AssistantMessage([new TextContent("hi")]) { Api = "x", Provider = "p", Model = "m", Usage = new Usage(1000, 50, 4000, 200) };
        IReadOnlyList<Message> messages = [UserMessage.FromText("hello"), assistant, UserMessage.FromText(new string('a', 400))];
        Assert.Equal(1000 + 50 + 4000 + 200 + 100, ContextAccounting.ContextTokens(messages));
        // 失败的回复不算
        var failed = assistant with { StopReason = StopReason.Error, Usage = new Usage(9999, 0, 0, 0) };
        Assert.Equal(1000 + 50 + 4000 + 200 + 100 + ContextAccounting.Estimate(failed), ContextAccounting.ContextTokens([.. messages, failed]));
    }

    [Theory]
    [InlineData("400 prompt is too long: 215000 tokens > 200000 maximum", 15_000L)]
    [InlineData("400 This model's maximum context length is 128000 tokens. However, your messages resulted in 130512 tokens.", 2_512L)]
    [InlineData("400 Your input exceeds the context window of this model. Please adjust your input and try again.", null)]
    [InlineData("400 input length and `max_tokens` exceed context limit: 188240 + 21333 > 200000", null)]
    public void ContextOverflow_RecognizesProviderMessages(string message, long? excess)
    {
        Assert.True(ContextOverflow.IsOverflowMessage(message));
        Assert.Equal(excess, ContextOverflow.Excess(message));
    }

    [Fact]
    public void ContextOverflow_IgnoresOtherErrors() =>
        Assert.False(ContextOverflow.IsOverflowMessage("429 Rate limit reached for requests"));

    [Fact]
    public void PostProcess_StripsAnalysisAndFormatsSummary()
    {
        Assert.Equal("Summary:\n1. Primary Request:\n   do it\n\n2. Next", CompactionPrompts.PostProcess(
            "<analysis>\nthinking\n</analysis>\n\n<summary>\n1. Primary Request:\n   do it\n\n\n\n2. Next\n</summary>"));
        // 没有标签：取全文；被截断的 summary 也可用
        Assert.Equal("plain text", CompactionPrompts.PostProcess("<analysis>x</analysis>plain text"));
        Assert.Equal("Summary:\npartial", CompactionPrompts.PostProcess("<analysis>x</analysis><summary>partial"));
    }

    [Fact]
    public void CompactPrompt_AppendsAdditionalInstructions()
    {
        var prompt = CompactionPrompts.CompactPrompt("focus on tests");
        Assert.StartsWith("CRITICAL: Respond with TEXT ONLY", prompt);
        Assert.Contains("\n\nAdditional Instructions:\nfocus on tests\n\nREMINDER:", prompt);
        Assert.DoesNotContain("Additional Instructions", CompactionPrompts.CompactPrompt(null));
    }

    [Fact]
    public void Group_KeepsToolResultsWithTheirCall_AndDropCountCoversExcess()
    {
        AssistantMessage A(string text) => new([new TextContent(text)]) { Api = "x", Provider = "p", Model = "m" };
        IReadOnlyList<Message> messages =
        [
            UserMessage.FromText("u1"), A("a1"), new ToolResultMessage("c1", "read", [new TextContent(new string('x', 4000))]),
            UserMessage.FromText("u2"), A("a2"),
            A("a3"),
        ];
        var groups = Compactor.Group(messages);
        Assert.Equal(3, groups.Count);
        Assert.Equal(3, groups[0].Count);
        Assert.IsType<UserMessage>(groups[1][0]);

        Assert.Equal(1, Compactor.DropCount(groups, 0, 500));
        Assert.Equal(2, Compactor.DropCount(groups, 0, 1_005));
        Assert.Equal(1, Compactor.DropCount(groups, 0, null));
    }

    [Fact]
    public async Task AutoCompact_BeforeNewInput_ReplacesHistoryAndPersists()
    {
        var turn = 0;
        var (session, api) = CreateSession((context, _) => IsSummaryRequest(context)
            ? new Reply("<analysis>notes</analysis><summary>1. Primary Request and Intent: build it</summary>", Tokens(500))
            : new Reply($"answer {++turn}", Tokens(turn == 1 ? 50_000 : 3_000)));
        await using var _ = session;
        session.ContextWindow = Model.ContextWindow;
        var events = Record(session);

        session.Prompt("first");
        await session.Agent.WaitForIdleAsync();
        session.Prompt("second");
        await session.Agent.WaitForIdleAsync();

        // 摘要请求：与会话相同的系统提示与工具，末尾追加压缩提示，不写缓存
        var summary = api.Requests.Single(r => IsSummaryRequest(r.Context));
        Assert.Equal(api.Requests[0].Context.SystemPrompt, summary.Context.SystemPrompt);
        Assert.Equal(api.Requests[0].Context.Tools.Count, summary.Context.Tools.Count);
        Assert.True(summary.Options.SkipCacheWrite);
        Assert.Equal(Model.MaxTokens, summary.Options.MaxTokens);
        // 新输入在压缩之后追加，不进摘要
        Assert.DoesNotContain(summary.Context.Messages, m => m is UserMessage u && ContextAccounting.TextOf(u.Content) == "second");

        var messages = session.Agent.Messages;
        var first = Assert.IsType<UserMessage>(messages[0]);
        Assert.Equal(CompactionPrompts.SummarySource, first.Source);
        var text = ContextAccounting.TextOf(first.Content);
        Assert.Contains("Summary:\n1. Primary Request and Intent: build it", text);
        Assert.Contains(session.Store.FilePath, text);
        Assert.DoesNotContain("without asking the user", text);
        Assert.Equal("second", ContextAccounting.TextOf(Assert.IsType<UserMessage>(messages[^2]).Content));
        Assert.Equal("answer 2", ContextAccounting.TextOf(Assert.IsType<AssistantMessage>(messages[^1]).Content));

        var end = Assert.Single(events.OfType<CompactionEndEvent>());
        Assert.Equal(CompactionTrigger.Auto, end.Trigger);
        Assert.Null(end.Error);
        Assert.Equal(50_100, end.PreTokens);
        // 显示用的完整历史：压缩前的消息仍在
        Assert.Equal("first", ContextAccounting.TextOf(Assert.IsType<UserMessage>(session.History[0]).Content));
        Assert.Equal(end.Replaced.Count + messages.Count, session.History.Count);

        // 会话文件：恢复后模型历史从摘要开始，压缩前的消息只用于显示
        var snapshot = SessionStore.Read(session.Store.FilePath);
        Assert.Equal(messages.Count, snapshot.Messages.Count);
        Assert.Equal(CompactionPrompts.SummarySource, Assert.IsType<UserMessage>(snapshot.Messages[0]).Source);
        Assert.Equal(end.Replaced.Count, snapshot.EarlierMessages.Count);
        Assert.Equal(CompactionTrigger.Auto, snapshot.Compaction!.Trigger);
        Assert.NotNull(snapshot.InitialSystemPrompt);

        // 重新打开：压缩前的消息不常驻，回看时才从文件读取，下标与原会话一致
        await using var reopened = AgentSession.Open(new WireApiRegistry([api]), Model, session.Store.FilePath);
        var history = reopened.History;
        Assert.Equal(session.History.Count, history.Count);
        Assert.Equal("first", ContextAccounting.TextOf(Assert.IsType<UserMessage>(history[0]).Content));
        Assert.Equal(CompactionPrompts.SummarySource, Assert.IsType<UserMessage>(history[end.Replaced.Count]).Source);
    }

    [Fact]
    public async Task ManualCompact_WhenIdle_RunsAsItsOwnRun_WithInstructions()
    {
        var (session, api) = CreateSession((context, _) => IsSummaryRequest(context)
            ? new Reply("<summary>S</summary>", Tokens(500))
            : new Reply("ok", Tokens(1_000)));
        await using var _ = session;
        var events = Record(session);
        session.Prompt("hello");
        await session.Agent.WaitForIdleAsync();

        await session.CompactAsync("keep the API names");
        await session.Agent.WaitForIdleAsync();

        var request = api.Requests.Single(r => IsSummaryRequest(r.Context));
        Assert.Contains("Additional Instructions:\nkeep the API names", ContextAccounting.TextOf(((UserMessage)request.Context.Messages[^1]).Content));
        Assert.Contains(events, e => e is CompactionEndEvent { Trigger: CompactionTrigger.Manual, Error: null });
        Assert.IsType<AgentEndEvent>(events[^1]);
        Assert.Equal(CompactionPrompts.SummarySource, Assert.IsType<UserMessage>(session.Agent.Messages[0]).Source);
        Assert.Equal("keep the API names", SessionStore.Read(session.Store.FilePath).Compaction!.Instructions);
    }

    [Fact]
    public async Task Overflow_CompactsAndRetriesTheRequest()
    {
        var calls = 0;
        var (session, _) = CreateSession((context, _) =>
        {
            if (IsSummaryRequest(context))
            {
                return new Reply("<summary>S</summary>", Tokens(500));
            }
            calls++;
            return calls switch
            {
                1 => new Reply("first answer", Tokens(1_000)),
                2 => new Reply(Error: "400 prompt is too long: 70000 tokens > 60000 maximum"),
                _ => new Reply("retried", Tokens(2_000)),
            };
        });
        await using var _ = session;
        var events = Record(session);
        session.Prompt("one");
        await session.Agent.WaitForIdleAsync();
        session.Prompt("two");
        await session.Agent.WaitForIdleAsync();

        Assert.Contains(events, e => e is CompactionEndEvent { Trigger: CompactionTrigger.Reactive, Error: null });
        var messages = session.Agent.Messages;
        Assert.Contains("without asking the user", ContextAccounting.TextOf(((UserMessage)messages[0]).Content));
        Assert.Equal("retried", ContextAccounting.TextOf(Assert.IsType<AssistantMessage>(messages[^1]).Content));
    }

    [Fact]
    public async Task SummaryRequestTooLong_DropsOldestGroups_ThenSucceeds()
    {
        var summaries = 0;
        var (session, api) = CreateSession((context, _) =>
        {
            if (IsSummaryRequest(context))
            {
                return ++summaries == 1 ? new Reply(Error: "400 prompt is too long") : new Reply("<summary>S</summary>");
            }
            return new Reply("ok", Tokens(1_000));
        });
        await using var _ = session;
        for (var i = 0; i < 6; i++)
        {
            session.Prompt($"message {i}");
            await session.Agent.WaitForIdleAsync();
        }
        await session.CompactAsync(null);
        await session.Agent.WaitForIdleAsync();

        var retry = api.Requests.Where(r => IsSummaryRequest(r.Context)).Last();
        Assert.Equal(CompactionPrompts.TruncationMarker, ContextAccounting.TextOf(((UserMessage)retry.Context.Messages[0]).Content));
        Assert.DoesNotContain(retry.Context.Messages, m => m is UserMessage u && ContextAccounting.TextOf(u.Content) == "message 0");
        Assert.Contains("NOT covered by this summary", ContextAccounting.TextOf(((UserMessage)session.Agent.Messages[0]).Content));
        Assert.True(SessionStore.Read(session.Store.FilePath).Compaction!.TruncatedForRetry);
    }

    [Fact]
    public async Task ForkReplyWithToolCall_FallsBackToSerializedSummary()
    {
        var (session, api) = CreateSession((context, _) =>
        {
            if (IsSummaryRequest(context))
            {
                return new Reply(Calls: [("read", """{"path":"x"}""")]);
            }
            if (context.SystemPrompt == CompactionPrompts.FallbackSystemPrompt)
            {
                return new Reply("<summary>fallback</summary>");
            }
            return new Reply("ok", Tokens(1_000));
        });
        await using var _ = session;
        session.Prompt("hello");
        await session.Agent.WaitForIdleAsync();
        await session.CompactAsync(null);
        await session.Agent.WaitForIdleAsync();

        var fallback = api.Requests.Single(r => r.Context.SystemPrompt == CompactionPrompts.FallbackSystemPrompt);
        Assert.Empty(fallback.Context.Tools);
        var serialized = ContextAccounting.TextOf(((UserMessage)fallback.Context.Messages.Single()).Content);
        Assert.StartsWith("[User]: hello\n\n[Assistant]: ok", serialized);
        Assert.Contains("Summary:\nfallback", ContextAccounting.TextOf(((UserMessage)session.Agent.Messages[0]).Content));
    }

    [Fact]
    public async Task FailedCompaction_LeavesHistoryUnchanged_AndSuspendsAfterThreeAutoFailures()
    {
        var (session, _) = CreateSession((context, _) => IsSummaryRequest(context) || context.SystemPrompt == CompactionPrompts.FallbackSystemPrompt
            ? new Reply(Error: "500 upstream failure")
            : new Reply("ok", Tokens(50_000)));
        await using var _ = session;
        var events = Record(session);
        for (var i = 0; i < 5; i++)
        {
            session.Prompt($"p{i}");
            await session.Agent.WaitForIdleAsync();
        }

        Assert.Equal(3, events.OfType<CompactionEndEvent>().Count(e => e.Error is not null));
        Assert.Single(events.OfType<CompactionSuspendedEvent>());
        Assert.NotNull(((Agent)session.Agent).AutoCompactSuspended);
        Assert.Equal("p0", ContextAccounting.TextOf(Assert.IsType<UserMessage>(session.Agent.Messages[0]).Content));
    }

    [Fact]
    public async Task Compaction_ReattachesRecentFiles_AndRequiresReReadForOthers()
    {
        var root = Directory.CreateTempSubdirectory("tcode-compact-").FullName;
        var small = Path.Combine(root, "small.txt");
        var large = Path.Combine(root, "large.txt");
        await File.WriteAllTextAsync(small, "alpha\nbeta\n");
        await File.WriteAllTextAsync(large, string.Join("\n", Enumerable.Range(0, 3_000).Select(i => $"line {i} with some padding text")));
        var reads = 0;
        var (session, _) = CreateSession((context, _) =>
        {
            if (IsSummaryRequest(context))
            {
                return new Reply("<summary>S</summary>");
            }
            return ++reads switch
            {
                1 => new Reply(Calls: [("read", $$"""{"path":"{{small.Replace("\\", "\\\\")}}"}"""), ("read", $$"""{"path":"{{large.Replace("\\", "\\\\")}}"}""")]),
                _ => new Reply("done", Tokens(1_000)),
            };
        }, root);
        await using var _ = session;
        session.Agent.PermissionMode = PermissionMode.FullAccess;
        session.Prompt("read both");
        await session.Agent.WaitForIdleAsync();
        await session.CompactAsync(null);
        await session.Agent.WaitForIdleAsync();

        var context = Assert.IsType<UserMessage>(session.Agent.Messages[1]);
        Assert.Equal(CompactionPrompts.ContextSource, context.Source);
        var text = ContextAccounting.TextOf(context.Content);
        Assert.True(text.Contains("     1\talpha"), text);
        // 记录的是解析过符号链接的真实路径（macOS 的 /var → /private/var）
        Assert.Matches(@"Referenced file \S*large\.txt", text);
        Assert.True(session.Tools!.Files.HasRead(small));
        Assert.False(session.Tools.Files.HasRead(large));
    }
}
