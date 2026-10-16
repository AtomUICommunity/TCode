using TCode.Agent.Coding;
using TCode.Agent.Coding.Sessions;
using Microsoft.Extensions.Logging;
using TCode.Ai.Messages;
using TCode.Ai.Models;
using TCode.Config;
using TCode.Rpc;
using TCode.Runtimes;

namespace TCode.AgentHost;

public sealed class AgentHostServer(
    SessionManager sessions,
    ModelCatalog catalog,
    ProviderTester tester,
    TCode.AgentHost.Catalog.CatalogSync catalogSync,
    RuntimeRegistry runtimes,
    SessionIndex index,
    ILogger<AgentHostServer> logger)
    : IAgentHostServer
{
    private const int MaxListedSessions = 200;

    public Task<HostInfo> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ProtocolVersion != RpcProtocol.Version)
        {
            throw new InvalidOperationException($"Protocol mismatch: client {request.ProtocolVersion}, host {RpcProtocol.Version}.");
        }
        var version = typeof(AgentHostServer).Assembly.GetName().Version?.ToString() ?? "0";
        return Task.FromResult(new HostInfo(RpcProtocol.Version, version, Environment.ProcessId));
    }

    public Task<IReadOnlyList<ModelDto>> ListModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(AllModelDtos(catalog, runtimes));

    /// <summary>目录模型在前，外部运行时（已就绪）的模型在后。</summary>
    public static IReadOnlyList<ModelDto> AllModelDtos(ModelCatalog catalog, RuntimeRegistry runtimes) =>
        [.. ModelDtos(catalog), .. RuntimeModelDtos(runtimes)];

    public static IEnumerable<ModelDto> RuntimeModelDtos(RuntimeRegistry runtimes) =>
        runtimes.All.Where(r => r.Descriptor.Status == RuntimeStatus.Ready)
            .SelectMany(r => r.Descriptor.Models.Select(m => SessionOptions.ToDto(m, r.DisplayName, r.Id)));

    public Task<IReadOnlyList<RuntimeDto>> ListRuntimesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RuntimeDto>>(runtimes.All.Select(r => r.Descriptor).Select(d => new RuntimeDto(
            d.Id, d.DisplayName, (RuntimeStatusDto)(int)d.Status, d.Version, d.ExecutablePath, d.AuthMethod, d.Error, d.Models.Count,
            d.Capabilities.SupportsSteering, d.Capabilities.SupportsResume)).ToArray());

    /// <summary>模型选择器中的模型（以 Ref 区分不同厂商的同名模型）。</summary>
    public static IReadOnlyList<ModelDto> ModelDtos(ModelCatalog catalog)
    {
        var names = catalog.Providers.ToDictionary(p => p.Id, p => p.Name);
        return catalog.Models.Select(m => SessionOptions.ToDto(m, names.GetValueOrDefault(m.Provider, m.Provider))).ToArray();
    }

    public Task<ProvidersSnapshotDto> ListProvidersAsync(CancellationToken cancellationToken = default)
    {
        var providers = catalog.Providers.Select(p => new ProviderDto(
            p.Id, p.Name, p.Api ?? "", p.BaseUrl, p.IsBuiltIn, p.IsConfigured, p.Enabled, p.IsActive,
            (KeySourceDto)(int)p.Key.Source, p.Key.Variable,
            p.Key is { Source: KeySource.Settings, Value: { } value } ? Secrets.Mask(value) : null,
            p.HasModelsOverride,
            p.Models.Select(m => new ProviderModelDto(m.Model.Id, m.Model.Name, m.Enabled, m.Model.ContextWindow, m.Model.Reasoning,
                m.Model.SupportsImages, m.InCatalog, m.Model.MaxTokens, m.Model.SupportsFastMode,
                m.Catalog?.Cost is [{ } input, ..] ? input : null,
                m.Catalog?.Cost is [_, { } output, ..] ? output : null,
                m.IsDiscovered, m.HasOverride, m.Api, m.ApiOverridden)).ToArray(),
            p.Errors,
            p.SupportsDiscovery,
            p.DiscoveryError)).ToArray();
        return Task.FromResult(new ProvidersSnapshotDto(providers, catalog.Errors, catalog.Warnings, ConfigPaths.SettingsFile, catalog.BuiltIn.Generated));
    }

    public Task<IReadOnlyList<CatalogProviderDto>> ListCatalogProvidersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CatalogProviderDto>>(catalog.BuiltIn.Providers
            .Select(p => new CatalogProviderDto(p.Key, p.Value.Name, p.Value.Api, p.Value.BaseUrl, p.Value.Env, p.Value.Models.Count, p.Value.KeyOptional))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray());

    public Task ReloadSettingsAsync(CancellationToken cancellationToken = default)
    {
        catalog.Reload();
        return Task.CompletedTask;
    }

    public Task<ProviderTestResult> TestProviderAsync(ProviderTestRequest request, CancellationToken cancellationToken = default) =>
        tester.TestAsync(request, cancellationToken);

    public Task RefreshProviderModelsAsync(string providerId, CancellationToken cancellationToken = default) =>
        catalog.RefreshModelsAsync(providerId, cancellationToken);

    public Task<CatalogStatusDto> GetCatalogStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(catalogSync.Status());

    public Task<CatalogUpdateDto> CheckCatalogAsync(CancellationToken cancellationToken = default) =>
        catalogSync.CheckAsync(catalog.Providers.Where(p => p.IsConfigured).Select(p => p.Id).ToHashSet(), cancellationToken);

    public Task<CatalogStatusDto> ApplyCatalogUpdateAsync(CancellationToken cancellationToken = default) => Task.FromResult(catalogSync.Apply());

    public Task DiscardCatalogUpdateAsync(CancellationToken cancellationToken = default)
    {
        catalogSync.Discard();
        return Task.CompletedTask;
    }

    public async Task<SessionDto> CreateSessionAsync(CreateSessionRequest request, CancellationToken cancellationToken = default)
    {
        var external = runtimes.FindModel(request.ModelId);
        var model = external?.Model ?? catalog.Get(request.ModelId);
        var session = external is var (runtime, _) ? sessions.CreateExternal(request.Cwd, runtime, model) : sessions.Create(request.Cwd, model);
        SessionOptions.Initialize(session);
        await session.Store.AppendModelChangeAsync(model.Provider, model.Id, cancellationToken);
        var options = SessionOptions.Apply(session, request.Options);
        // 会话文件在第一条消息时才创建；这条参数记录先缓存在 SessionStore 中，随第一条消息写入。
        await session.Store.AppendOptionsChangeAsync(session.Agent.ThinkingLevel, session.Agent.FastMode, session.ContextWindow, cancellationToken,
            session.Agent.PermissionMode);
        return new SessionDto(session.Id, session.Cwd, ModelCatalog.RefOf(model), session.Store.FilePath, options, session.External?.RuntimeId);
    }

    public Task<IReadOnlyList<SessionSummaryDto>> ListSessionsAsync(string? cwd, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SessionSummaryDto>>(Filter(index.Refresh(), cwd).Take(MaxListedSessions).Select(ToSummary).ToArray());

    public Task<SessionPageDto> QuerySessionsAsync(string? cwd, int offset, int limit, CancellationToken cancellationToken = default)
    {
        var all = Filter(index.Refresh(), cwd).ToArray();
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, MaxListedSessions);
        var page = all.Skip(offset).Take(limit).Select(ToSummary).ToArray();
        var next = offset + page.Length < all.Length ? offset + page.Length : (int?)null;
        return Task.FromResult(new SessionPageDto(page, all.Length, next));
    }

    private static IEnumerable<SessionIndexEntry> Filter(IEnumerable<SessionIndexEntry> entries, string? cwd) =>
        cwd is null ? entries : entries.Where(e => SamePath(e.Cwd, cwd));

    private SessionSummaryDto ToSummary(SessionIndexEntry e)
    {
        var provider = e.ModelRef is { } model && model.IndexOf('/') is var slash and > 0 ? model[..slash] : null;
        var runtime = e.Runtime ?? runtimes.Find(provider)?.Id;
        return new SessionSummaryDto(e.SessionId, e.FilePath, e.Cwd, e.CustomTitle ?? e.FirstMessageTitle, e.ModelRef,
            new DateTimeOffset(e.LastWriteTicks, TimeSpan.Zero), sessions.IsRunning(e.SessionId), runtime, e.Pinned,
            e.CustomTitle is not null, e.NativeSessionId);
    }

    public async Task RenameSessionAsync(string filePath, string title, CancellationToken cancellationToken = default)
    {
        title = title.Trim();
        if (sessions.FindByFile(filePath) is { } open)
        {
            await open.Store.AppendMetaAsync(title, null, cancellationToken);
        }
        else
        {
            await SessionStore.AppendMetaToFileAsync(RequireSessionFile(filePath), title, null, cancellationToken);
        }
    }

    public async Task PinSessionAsync(string filePath, bool pinned, CancellationToken cancellationToken = default)
    {
        if (sessions.FindByFile(filePath) is { } open)
        {
            await open.Store.AppendMetaAsync(null, pinned, cancellationToken);
        }
        else
        {
            await SessionStore.AppendMetaToFileAsync(RequireSessionFile(filePath), null, pinned, cancellationToken);
        }
    }

    public async Task DeleteSessionAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var path = RequireSessionFile(filePath);
        if (sessions.FindByFile(path) is { } open)
        {
            open.Agent.Abort();
            await sessions.CloseAsync(open.Id);
        }
        File.Delete(path);
        index.Forget(path);
        logger.LogInformation("Session file {File} deleted", path);
    }

    /// <summary>在新会话中继续：复制当前分支的消息与参数到同一项目下的新会话（外部 Agent 的上下文在 CLI 里，无法复制）。</summary>
    public async Task<OpenSessionResult> ForkSessionAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var snapshot = SessionStore.Read(RequireSessionFile(filePath));
        if (RuntimeOf(snapshot) is not null)
        {
            throw new InvalidOperationException("Sessions of external agents cannot be continued in a new session.");
        }
        if (snapshot.Messages.Count == 0)
        {
            throw new InvalidOperationException("The session has no messages to continue from.");
        }
        var store = SessionStore.Create(snapshot.Cwd);
        if (snapshot.ModelId is not null)
        {
            await store.AppendModelChangeAsync(snapshot.ModelProvider ?? "", snapshot.ModelId, cancellationToken);
        }
        var permission = snapshot.PermissionMode is TCode.Agent.PermissionMode.FullAccess ? TCode.Agent.PermissionMode.Ask : snapshot.PermissionMode;
        await store.AppendOptionsChangeAsync(snapshot.ThinkingLevel, snapshot.FastMode, snapshot.ContextWindow, cancellationToken, permission);
        if (snapshot.CustomTitle is { } title)
        {
            await store.AppendMetaAsync(title, null, cancellationToken);
        }
        foreach (var message in snapshot.Messages)
        {
            await store.AppendMessageAsync(message, cancellationToken);
        }
        return await OpenSessionAsync(store.FilePath, cancellationToken);
    }

    /// <summary>只允许操作会话目录下的 .jsonl 文件。</summary>
    private static string RequireSessionFile(string filePath)
    {
        var full = Path.GetFullPath(filePath);
        var root = Path.GetFullPath(SessionStore.DefaultRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            !full.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            throw new InvalidOperationException($"'{filePath}' is not a TCode session file.");
        }
        return full;
    }

    public Task<OpenSessionResult> OpenSessionAsync(string filePath, CancellationToken cancellationToken = default) =>
        OpenSessionAsync(filePath, null, cancellationToken);

    public Task<HistoryPageDto> GetHistoryAsync(string sessionId, int before, int turnLimit, CancellationToken cancellationToken = default)
    {
        var messages = sessions.Get(sessionId).Agent.Messages;
        before = Math.Clamp(before, 0, messages.Count);
        var start = HistoryPaging.StartOfRecentTurns(messages, before, Math.Max(1, turnLimit));
        return Task.FromResult(new HistoryPageDto(ToHistory(messages, start, before), start));
    }

    public Task<OpenSessionResult> OpenSessionAsync(string filePath, int? turnLimit, CancellationToken cancellationToken = default)
    {
        if (sessions.FindByFile(filePath) is { } open)
        {
            return Task.FromResult(Describe(open, turnLimit));
        }
        var snapshot = SessionStore.Read(filePath);
        AgentSession session;
        if (runtimes.Find(RuntimeOf(snapshot)) is { } runtime)
        {
            // 外部运行时的会话只能在同一运行时中继续；CLI 尚未探测完成或已卸载时用占位模型，提问时再报错。
            var model = runtimes.FindModel(runtime.Id, snapshot.ModelId)?.Model
                        ?? runtime.Descriptor.Models.FirstOrDefault()
                        ?? RuntimeModels.Placeholder(runtime, snapshot.ModelId);
            session = sessions.OpenExternal(filePath, runtime, model);
        }
        else
        {
            // 会话记录的模型不在当前目录中（例如未配置 API Key）时退回第一个可用模型。
            var model = catalog.Find(snapshot.ModelProvider, snapshot.ModelId) ?? catalog.Find(catalog.DefaultModelRef) ?? catalog.Models[0];
            session = sessions.Open(filePath, model);
        }
        SessionOptions.Initialize(session);
        // 完全授权不沿用：重新打开的会话回到“询问”；其余档位按记录恢复
        var permission = snapshot.PermissionMode is TCode.Agent.PermissionMode.FullAccess or null ? TCode.Agent.PermissionMode.Ask : snapshot.PermissionMode.Value;
        SessionOptions.Apply(session, new SessionOptionsDto(snapshot.ThinkingLevel?.ToString(), snapshot.FastMode, snapshot.ContextWindow,
            PermissionMode: SessionOptions.ToWire(permission)));
        return Task.FromResult(Describe(session, turnLimit));
    }

    public Task<PromptResult> PromptAsync(PromptRequest request, CancellationToken cancellationToken = default)
    {
        var behavior = request.WhileRunning == StreamingBehaviorDto.Steer ? StreamingBehavior.Steer : StreamingBehavior.FollowUp;
        var message = TCode.Agent.Coding.Sessions.ImageAttachments.CreatePrompt(request.Text,
            (request.Images ?? []).Select(i => (i.Path, i.MimeType)));
        var disposition = sessions.Get(request.SessionId).Prompt(message, behavior);
        return Task.FromResult(new PromptResult(disposition == PromptDisposition.Started));
    }

    public Task AbortAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        sessions.Get(sessionId).Agent.Abort();
        return Task.CompletedTask;
    }

    public async Task<SessionOptionsDto> SetModelAsync(string sessionId, string modelId, CancellationToken cancellationToken = default)
    {
        var session = sessions.Get(sessionId);
        var target = runtimes.FindModel(modelId);
        var model = target?.Model ?? catalog.Get(modelId);
        if (target?.Runtime.Id != session.External?.RuntimeId)
        {
            // 换到另一个运行时（或内置 ↔ 外部）：只有尚无消息的会话可以，换掉底层的 Agent 循环
            var permission = session.Agent.PermissionMode;
            session = await sessions.SwitchRuntimeAsync(sessionId, target?.Runtime, model);
            SessionOptions.Initialize(session);
            session.Agent.PermissionMode = permission; // 权限档位是用户的选择，不随运行时重置
        }
        session.Agent.Model = model;
        await session.Store.AppendModelChangeAsync(model.Provider, model.Id, cancellationToken);
        return await SetOptionsAsync(sessionId, new SessionOptionsDto(), cancellationToken);
    }

    public async Task<SessionOptionsDto> SetOptionsAsync(string sessionId, SessionOptionsDto options, CancellationToken cancellationToken = default)
    {
        var session = sessions.Get(sessionId);
        var applied = SessionOptions.Apply(session, options);
        await session.Store.AppendOptionsChangeAsync(session.Agent.ThinkingLevel, session.Agent.FastMode, session.ContextWindow, cancellationToken,
            session.Agent.PermissionMode);
        return applied;
    }

    public Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default) => sessions.CloseAsync(sessionId);

    private OpenSessionResult Describe(AgentSession session, int? turnLimit = null)
    {
        var messages = session.Agent.Messages;
        var start = turnLimit is { } limit ? HistoryPaging.StartOfRecentTurns(messages, messages.Count, Math.Max(1, limit)) : 0;
        return new OpenSessionResult(
            new SessionDto(session.Id, session.Cwd, ModelCatalog.RefOf(session.Agent.Model), session.Store.FilePath, SessionOptions.Current(session),
                session.External?.RuntimeId),
            ToHistory(messages, start, messages.Count),
            session.Agent.IsRunning,
            start);
    }

    private static HistoryMessageDto[] ToHistory(IReadOnlyList<Message> messages, int start, int end)
    {
        var result = new List<HistoryMessageDto>(end - start);
        for (var i = start; i < end; i++)
        {
            if (ToHistory(messages[i]) is { } dto)
            {
                result.Add(dto);
            }
        }
        return result.ToArray();
    }

    private static HistoryMessageDto? ToHistory(Message message) => message switch
    {
        UserMessage u => new HistoryMessageDto("user", u.Timestamp, u.Content.Select(c => c switch
        {
            TextContent t => new HistoryText(t.Text),
            ImageContent { Path: { } path } => (HistoryBlockDto?)new HistoryImage(path),
            _ => null,
        }).OfType<HistoryBlockDto>().ToArray(), Source: u.Source),
        AssistantMessage a => new HistoryMessageDto("assistant", a.Timestamp,
            a.Content.Select(ToHistory).OfType<HistoryBlockDto>().ToArray(),
            a.StopReason.ToString(), a.ErrorMessage,
            a.Usage is { } usage ? new UsageDto(usage.Input, usage.Output, usage.CacheRead, usage.CacheWrite) : null),
        ToolResultMessage r => new HistoryMessageDto("toolResult", r.Timestamp,
            [new HistoryToolResult(r.ToolCallId, r.ToolName, r.IsError,
                HistoryPaging.Truncate(string.Concat(r.Content.OfType<TextContent>().Select(t => t.Text))), r.Details)]),
        _ => null,
    };

    private static HistoryBlockDto? ToHistory(ContentBlock block) => block switch
    {
        TextContent t => new HistoryText(t.Text),
        ThinkingContent t => new HistoryThinking(HistoryPaging.Truncate(t.Thinking)),
        ToolCallContent c => new HistoryToolCall(c.Id, c.Name, c.ArgumentsJson),
        _ => null,
    };

    /// <summary>会话所属的外部运行时：优先取记录的原生会话，其次看模型厂商是否为运行时 id。</summary>
    private string? RuntimeOf(SessionSnapshot snapshot) =>
        snapshot.Runtime ?? (runtimes.Find(snapshot.ModelProvider) is { } runtime ? runtime.Id : null);

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
