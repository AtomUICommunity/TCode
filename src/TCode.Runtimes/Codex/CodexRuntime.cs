using System.Text.Json;
using TCode.Ai.Models;
using TCode.Runtimes.Discovery;
using TCode.Runtimes.Json;
using TCode.Runtimes.Process;

namespace TCode.Runtimes.Codex;

/// <summary>
/// Codex（<c>codex app-server</c>，规格 docs/protocols/codex-app-server.md）。
/// 探测只调用 initialize、model/list、account/read，不会启动模型轮次（§8.1）。
/// </summary>
public sealed class CodexRuntime(ExecutableLocator? locator = null) : IAgentRuntime
{
    public const string RuntimeId = "codex";
    public const string DefaultModelId = "default";
    public const string DeveloperInstructions = "You are running inside TCode. Prefer concise answers.";
    private static readonly Version MinimumVersion = new(0, 162);

    private readonly ExecutableLocator _locator = locator ?? ExecutableLocator.Default;
    private RuntimeDescriptor _descriptor = new(RuntimeId, "Codex", RuntimeStatus.Unknown);

    public string Id => RuntimeId;
    public string DisplayName => "Codex";
    public RuntimeDescriptor Descriptor => _descriptor;
    public event Action? DescriptorChanged { add { } remove { } }

    private static readonly RuntimeCapabilities Capabilities = new()
    {
        SupportsSteering = true,
        SupportsResume = true,
        SupportsModelSwitch = true,
        StreamsToolArguments = false,
    };

    /// <summary>子进程环境：stderr 用 JSON 行日志，避免 ANSI 颜色码（§1.5）。</summary>
    private static readonly Dictionary<string, string?> Environment = new() { ["LOG_FORMAT"] = "json" };

    public async Task<RuntimeDescriptor> ProbeAsync(CancellationToken cancellationToken)
    {
        var path = _locator.Find("codex", "TCODE_CODEX_PATH");
        if (path is null)
        {
            return _descriptor = new RuntimeDescriptor(Id, DisplayName, RuntimeStatus.NotInstalled);
        }
        try
        {
            await using var cli = CliProcess.Start(path, ["app-server"], null, Environment, _locator);
            var session = new CodexSession(cli.StandardOutput, cli.StandardInput, NullHost.Instance, null);
            try
            {
                var init = await session.InitializeAsync(cancellationToken);
                var version = ParseVersion(init);
                if (Version.TryParse(version, out var parsed) && parsed < MinimumVersion)
                {
                    return _descriptor = new RuntimeDescriptor(Id, DisplayName, RuntimeStatus.Error, path, version, null,
                        $"Codex {version} is too old; TCode requires {MinimumVersion} or newer.") { Capabilities = Capabilities };
                }
                var models = await ListModelsAsync(session, cancellationToken);
                var account = await session.InvokeAsync("account/read", JsonBuilder.Object(w => w.WriteBoolean("refreshToken", false)),
                    cancellationToken);
                var (auth, loggedIn) = DescribeAccount(account);
                return _descriptor = new RuntimeDescriptor(Id, DisplayName, loggedIn ? RuntimeStatus.Ready : RuntimeStatus.Error, path, version, auth,
                    loggedIn ? null : "Codex is not logged in. Run `codex login` in a terminal.")
                {
                    Capabilities = Capabilities,
                    Models = models,
                };
            }
            finally
            {
                await session.DisposeAsync();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return _descriptor = new RuntimeDescriptor(Id, DisplayName, RuntimeStatus.Error, path, null, null, ex.Message)
            {
                Capabilities = Capabilities,
            };
        }
    }

    public async Task<IRuntimeSession> StartSessionAsync(RuntimeSessionRequest request, CancellationToken cancellationToken)
    {
        if (_descriptor is not { Status: RuntimeStatus.Ready, ExecutablePath: { } path })
        {
            throw new InvalidOperationException($"Codex is not available ({_descriptor.Error ?? _descriptor.Status.ToString()}).");
        }
        var cli = CliProcess.Start(path, ["app-server"], request.Cwd, Environment, _locator);
        var session = new CodexSession(cli.StandardOutput, cli.StandardInput, request.Host, DeveloperInstructions, cli,
            () => cli.DescribeExit("Codex"));
        try
        {
            await session.InitializeAsync(cancellationToken);
            await session.StartThreadAsync(request.Cwd, request.Options, request.ResumeSessionId, cancellationToken);
            return session;
        }
        catch (Exception ex)
        {
            await session.DisposeAsync();
            if (ex is TCode.Rpc.RpcConnectionLostException)
            {
                throw new InvalidOperationException(cli.DescribeExit("Codex"), ex);
            }
            throw;
        }
    }

    /// <summary>model/list 翻完所有页，过滤 hidden，默认模型排最前（§8.1）。</summary>
    internal async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CodexSession session, CancellationToken ct)
    {
        var models = new List<(bool IsDefault, ModelInfo Model)>();
        string? cursor = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var page = await session.InvokeAsync("model/list", JsonBuilder.Object(w =>
            {
                if (cursor is not null)
                {
                    w.WriteString("cursor", cursor);
                }
                w.WriteBoolean("includeHidden", false);
            }), ct);
            foreach (var m in page.Arr("data"))
            {
                if (m.Bool("hidden") || (m.Str("model") ?? m.Str("id")) is not { } id)
                {
                    continue;
                }
                models.Add((m.Bool("isDefault"), ToModel(m, id)));
            }
            cursor = page.Str("nextCursor");
        } while (!string.IsNullOrEmpty(cursor) && seen.Add(cursor));
        return models.OrderByDescending(m => m.IsDefault).Select(m => m.Model).ToArray();
    }

    internal ModelInfo ToModel(JsonElement m, string id)
    {
        var efforts = m.Arr("supportedReasoningEfforts").Select(e => e.Str("reasoningEffort")).OfType<string>().ToArray();
        var modalities = m.Prop("inputModalities") is { ValueKind: JsonValueKind.Array } mods
            ? mods.EnumerateArray().Select(x => x.GetString()).ToArray()
            : ["text", "image"];
        return RuntimeModels.Create(this, id, m.Str("displayName"), m.Str("description"), reasoning: efforts.Length > 0,
            contextWindow: 272_000, supportsImages: modalities.Contains("image")) with
        {
            SupportsFastMode = m.Arr("serviceTiers").Any(t => t.Str("id") == "priority"),
            Compat = efforts.Length > 0 ? new ModelCompat { ReasoningEfforts = efforts } : null,
        };
    }

    /// <summary>serverInfo.version，没有时取 userAgent 中第一个 '/' 与空格之间的部分（§2.1）。</summary>
    internal static string? ParseVersion(JsonElement init)
    {
        if (init.Obj("serverInfo")?.Str("version") is { } version)
        {
            return version;
        }
        var agent = init.Str("userAgent");
        var slash = agent?.IndexOf('/') ?? -1;
        if (agent is null || slash < 0)
        {
            return null;
        }
        var end = agent.IndexOf(' ', slash);
        return agent[(slash + 1)..(end < 0 ? agent.Length : end)];
    }

    /// <summary>account/read：requiresOpenaiAuth 且没有账号时视为未登录（§8.2）。</summary>
    internal static (string? Auth, bool LoggedIn) DescribeAccount(JsonElement result)
    {
        var account = result.Obj("account");
        var auth = account?.Str("type") switch
        {
            "chatgpt" => account.Value.Str("planType") is { } plan ? $"ChatGPT ({plan})" : "ChatGPT",
            "apiKey" => "API key",
            "amazonBedrock" => "Bedrock",
            null => null,
            var other => other,
        };
        return (auth ?? (result.Bool("requiresOpenaiAuth") ? null : "Custom provider"), account is not null || !result.Bool("requiresOpenaiAuth"));
    }

    private sealed class NullHost : IRuntimeHost
    {
        public static readonly NullHost Instance = new();

        public ValueTask<ApprovalDecision> RequestApprovalAsync(RuntimeApprovalRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ApprovalDecision.Deny());

        public void OnNativeSessionId(string nativeSessionId)
        {
        }
    }
}
