using TCode.Ai.Models;
using TCode.Runtimes.Discovery;
using TCode.Runtimes.Process;

namespace TCode.Runtimes.Acp;

/// <summary>
/// 一个原生 ACP Agent（规格 docs/protocols/acp-v1.md）。
/// 模型列表只有建立会话后才能从 config options 得知；为避免探测时在 Agent 历史中留下空会话，
/// 首次只提供 “Agent 默认模型”，第一个会话启动后更新并缓存到磁盘。
/// </summary>
public sealed class AcpRuntime(AcpAgentDefinition agent, RuntimeCache? cache = null, ExecutableLocator? locator = null) : IAgentRuntime
{
    public const string DefaultModelId = "default";
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(10);

    private readonly ExecutableLocator _locator = locator ?? ExecutableLocator.Default;
    private RuntimeDescriptor _descriptor = new(agent.RuntimeId, agent.DisplayName, RuntimeStatus.Unknown);

    public string Id => agent.RuntimeId;
    public string DisplayName => agent.DisplayName;
    public RuntimeDescriptor Descriptor => _descriptor;
    public event Action? DescriptorChanged;

    private static readonly RuntimeCapabilities Capabilities = new()
    {
        SupportsSteering = false,
        SupportsResume = true,
        SupportsModelSwitch = true,
        StreamsToolArguments = false,
    };

    public async Task<RuntimeDescriptor> ProbeAsync(CancellationToken cancellationToken)
    {
        var path = _locator.Find(agent.Executable, agent.OverrideVariable, accept: AcpAgents.IsUsable);
        if (path is null)
        {
            return _descriptor = new RuntimeDescriptor(Id, DisplayName, RuntimeStatus.NotInstalled);
        }
        // 只用 --version 探测：不要用 “acp --help” 一类命令，有的 CLI 会把未知参数当作 prompt（§17.1）。
        var output = await CliProcess.RunToCompletionAsync(path, ["--version"], VersionTimeout, cancellationToken, _locator);
        var version = output?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (output is null)
        {
            return _descriptor = new RuntimeDescriptor(Id, DisplayName, RuntimeStatus.Error, path, null, null,
                $"'{Path.GetFileName(path)} --version' failed.") { Capabilities = Capabilities };
        }
        var cached = cache?.Read(Id, path, version, RuntimeCacheJsonContext.Default.IReadOnlyListCachedModel);
        return _descriptor = new RuntimeDescriptor(Id, DisplayName, RuntimeStatus.Ready, path, version)
        {
            Capabilities = Capabilities,
            Models = [DefaultModel(), .. (cached ?? []).Select(ToModel)],
        };
    }

    public async Task<IRuntimeSession> StartSessionAsync(RuntimeSessionRequest request, CancellationToken cancellationToken)
    {
        if (_descriptor is not { Status: RuntimeStatus.Ready, ExecutablePath: { } path })
        {
            throw new InvalidOperationException($"{DisplayName} is not available ({_descriptor.Error ?? _descriptor.Status.ToString()}).");
        }
        var process = CliProcess.Start(path, agent.Arguments, request.Cwd, agent.Environment, _locator);
        var session = new AcpSession(process.StandardOutput, process.StandardInput, agent, request.Host, process,
            () => process.DescribeExit(DisplayName));
        try
        {
            await session.StartAsync(request.Cwd, request.ResumeSessionId, cancellationToken);
        }
        catch (Exception ex)
        {
            await session.DisposeAsync();
            if (ex is TCode.Rpc.RpcConnectionLostException or OperationCanceledException && process.HasExited)
            {
                throw new InvalidOperationException(process.DescribeExit(DisplayName), ex);
            }
            throw;
        }
        UpdateModels(session.Config);
        return session;
    }

    /// <summary>会话报告了可选模型：更新描述并缓存（下次启动即可在选择器中选择）。</summary>
    private void UpdateModels(AcpConfigState config)
    {
        var efforts = config.ReasoningEfforts;
        var models = config.Models
            .Select(v => new CachedModel(v.Value, v.Name, v.Description, efforts.Count > 0 ? efforts : null))
            .ToArray();
        if (models.Length == 0 || _descriptor.ExecutablePath is not { } path)
        {
            return;
        }
        var current = _descriptor.Models;
        var next = models.Select(ToModel).Prepend(DefaultModel()).ToArray();
        if (current.Select(m => (m.Id, m.Name)).SequenceEqual(next.Select(m => (m.Id, m.Name))))
        {
            return;
        }
        cache?.Write(Id, path, _descriptor.Version, models, RuntimeCacheJsonContext.Default.IReadOnlyListCachedModel);
        _descriptor = _descriptor with { Models = next };
        DescriptorChanged?.Invoke();
    }

    private ModelInfo DefaultModel() =>
        RuntimeModels.Create(this, DefaultModelId, "Default", $"{DisplayName} chooses the model", reasoning: false,
            supportsImages: true);

    private ModelInfo ToModel(CachedModel m) => RuntimeModels.Create(this, m.Id, m.Name, m.Description,
        reasoning: m.ReasoningEfforts is { Count: > 0 }, supportsImages: true) with
    {
        Compat = m.ReasoningEfforts is { Count: > 0 } efforts ? new ModelCompat { ReasoningEfforts = efforts } : null,
    };
}
