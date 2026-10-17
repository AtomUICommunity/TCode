using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using TCode.Config;
using TCode.Runtimes.Discovery;

namespace TCode.Mcp;

public enum McpConnectionState
{
    Idle,
    Connecting,
    Connected,
    NeedsAuth,
    Failed,
}

/// <summary>连接的状态快照（设置页与日志用）。</summary>
public sealed record McpConnectionStatus(
    McpConnectionState State,
    string? Error,
    int ToolCount,
    int PromptCount,
    int ResourceCount,
    string? ProtocolVersion,
    string? ServerName,
    string? ServerVersion,
    string? Instructions);

/// <summary>连接池提供给连接的宿主能力。</summary>
public sealed class McpHost
{
    public ILoggerFactory? LoggerFactory { get; init; }
    public ILogger Logger { get; init; } = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    /// <summary>服务器请求用户输入（elicitation）。没有设置时一律 cancel。</summary>
    public Func<McpElicitation, CancellationToken, ValueTask<ElicitResult>>? Elicit { get; set; }

    /// <summary>远程服务器的 OAuth（null 表示不处理 OAuth，401 直接进入 NeedsAuth）。</summary>
    public McpOAuth? OAuth { get; set; }

    public ExecutableLocator Locator { get; init; } = ExecutableLocator.Default;

    public string ClientVersion { get; init; } = typeof(McpHost).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
}

/// <summary>一次 elicitation 请求：来自哪个服务器、哪个会话（工具调用期间可知）。</summary>
public sealed record McpElicitation(string ServerKey, string ServerTitle, string? SessionId, ElicitRequestParams Request);

/// <summary>当前工具调用所属的会话（elicitation 弹窗定位用）。</summary>
public static class McpCallContext
{
    private static readonly AsyncLocal<string?> Current = new();

    public static string? SessionId
    {
        get => Current.Value;
        set => Current.Value = value;
    }
}

/// <summary>
/// 一个 MCP 服务器连接（docs/design/mcp.md §6）：后台建立连接，意外断开后按退避重连，连续失败 5 次置为 Failed；
/// 远程服务器返回 401 时置为 NeedsAuth，等待用户登录后 <see cref="Restart"/>。
/// </summary>
public sealed class McpConnection : IAsyncDisposable
{
    private const int MaxAttempts = 5;
    private const int StderrCapacity = 1000;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private readonly McpHost _host;
    private readonly Lock _gate = new();
    private readonly LinkedList<string> _stderr = new();
    private readonly SemaphoreSlim _toolsGate = new(1, 1);
    private CancellationTokenSource _lifetime = new();
    private Task? _loop;
    private TaskCompletionSource _settled = NewSignal();
    private McpClient? _client;
    private McpConnectionState _state = McpConnectionState.Idle;
    private string? _error;
    private IReadOnlyList<Tool> _tools = [];
    private DateTimeOffset _toolsFetchedAt;
    private TimeSpan _toolsTtl;
    private bool _toolsStale = true;
    private int _promptCount;
    private int _resourceCount;
    private bool _interactiveAuth;

    public McpConnection(McpServerConfig config, McpExpansion expansion, McpHost host, string? workspace = null)
    {
        Config = config;
        Expansion = expansion;
        Workspace = workspace;
        _host = host;
        Id = IdFor(config, expansion);
    }

    /// <summary>连接池的键：服务器键 + 展开后的连接参数（stdio 包含 cwd，HTTP 不含）。</summary>
    public string Id { get; }
    public McpServerConfig Config { get; }

    /// <summary>创建连接时的项目目录（展开 ${workspaceFolder}、读取项目 .mcp.json 用）。</summary>
    public string? Workspace { get; }
    public McpExpansion Expansion { get; }
    public string Key => Config.Key;

    /// <summary>持有本连接的会话数（由连接池维护）。</summary>
    internal int Leases { get; set; }

    /// <summary>配置已变化：租约归零后立即关闭，而不是等空闲超时。</summary>
    internal bool Obsolete { get; set; }

    internal DateTimeOffset ReleasedAt { get; set; }

    /// <summary>状态变化、工具列表变化。</summary>
    public event Action<McpConnection>? Changed;

    public McpClient? Client
    {
        get
        {
            lock (_gate)
            {
                return _client;
            }
        }
    }

    public McpConnectionState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public McpConnectionStatus Status
    {
        get
        {
            lock (_gate)
            {
                return new McpConnectionStatus(_state, _error, _tools.Count, _promptCount, _resourceCount,
                    _client?.NegotiatedProtocolVersion, _client?.ServerInfo.Name, _client?.ServerInfo.Version, _client?.ServerInstructions);
            }
        }
    }

    /// <summary>最近的 stderr 行（stdio）与连接错误。</summary>
    public IReadOnlyList<string> Log
    {
        get
        {
            lock (_stderr)
            {
                return _stderr.ToArray();
            }
        }
    }

    /// <summary>开始连接（幂等）。</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null)
            {
                return;
            }
            _state = McpConnectionState.Connecting;
            _loop = Task.Run(() => RunAsync(_lifetime.Token));
        }
        Changed?.Invoke(this);
    }

    /// <summary>断开并重新连接（重试按钮；登录后 <paramref name="interactiveAuth"/> 为 true 允许打开浏览器）。</summary>
    public async Task RestartAsync(bool interactiveAuth = false)
    {
        await StopAsync();
        lock (_gate)
        {
            _interactiveAuth = interactiveAuth;
            _lifetime = new CancellationTokenSource();
            _settled = NewSignal();
        }
        Start();
    }

    /// <summary>OAuth 回调里判断是否允许打开浏览器（只有用户点了“登录”才允许）。</summary>
    public bool InteractiveAuthAllowed
    {
        get
        {
            lock (_gate)
            {
                return _interactiveAuth;
            }
        }
    }

    /// <summary>等待连接结束“连接中”状态（成功、需要登录或失败），最多 <paramref name="timeout"/>。返回是否已连接。</summary>
    public async Task<bool> WaitReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task settled;
        lock (_gate)
        {
            if (_state == McpConnectionState.Connected)
            {
                return true;
            }
            if (_state is McpConnectionState.Failed or McpConnectionState.NeedsAuth)
            {
                return false;
            }
            settled = _settled.Task;
        }
        try
        {
            await settled.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
        }
        return State == McpConnectionState.Connected;
    }

    /// <summary>当前工具列表：收到 list_changed 或超过 TTL 时重新获取（失败时沿用旧列表）。</summary>
    public async Task<IReadOnlyList<Tool>> GetToolsAsync(CancellationToken cancellationToken)
    {
        var client = Client;
        if (client is null || State != McpConnectionState.Connected)
        {
            return [];
        }
        await _toolsGate.WaitAsync(cancellationToken);
        try
        {
            bool fresh;
            lock (_gate)
            {
                var age = DateTimeOffset.UtcNow - _toolsFetchedAt;
                // 没有 TTL 时同一轮内（2s）不重复获取
                fresh = !_toolsStale && age < (_toolsTtl > TimeSpan.Zero ? _toolsTtl : TimeSpan.FromSeconds(2));
                if (fresh)
                {
                    return _tools;
                }
            }
            try
            {
                await FetchToolsAsync(client, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppendLog($"tools/list failed: {ex.Message}");
            }
            lock (_gate)
            {
                return _tools;
            }
        }
        finally
        {
            _toolsGate.Release();
        }
    }

    public async Task<CallToolResult> CallToolAsync(string name, IReadOnlyDictionary<string, object?> arguments,
        IProgress<ProgressNotificationValue>? progress, CancellationToken cancellationToken)
    {
        if (Client is not { } client || State != McpConnectionState.Connected)
        {
            if (!await WaitReadyAsync(Config.StartupTimeout, cancellationToken) || Client is not { } ready)
            {
                throw new McpUnavailableException(Key, Status.Error);
            }
            client = ready;
        }
        return await client.CallToolAsync(name, arguments, progress, cancellationToken: cancellationToken);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            SetState(McpConnectionState.Connecting, null);
            McpClient? client = null;
            try
            {
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
                startup.CancelAfter(Config.StartupTimeout);
                client = await McpClient.CreateAsync(CreateTransport(), CreateOptions(), _host.LoggerFactory, startup.Token);
                await LoadAsync(client, startup.Token);
                lock (_gate)
                {
                    _client = client;
                    // 登录只在用户发起的那次连接中允许打开浏览器；之后的重连使用保存的令牌
                    _interactiveAuth = false;
                }
                attempt = 0;
                SetState(McpConnectionState.Connected, null);
                var details = await client.Completion.WaitAsync(ct);
                // 连接意外结束（进程退出 / 网络断开）：记录原因后重连
                var reason = Describe(details);
                AppendLog($"Connection closed: {reason}");
                lock (_gate)
                {
                    _client = null;
                }
                await client.DisposeAsync();
                client = null;
                if (IsUnauthorized(details.Exception) || details is HttpClientCompletionDetails { HttpStatusCode: HttpStatusCode.Unauthorized })
                {
                    SetState(McpConnectionState.NeedsAuth, "Authentication required.");
                    return;
                }
                attempt++;
                if (attempt >= MaxAttempts)
                {
                    SetState(McpConnectionState.Failed, reason);
                    return;
                }
                SetState(McpConnectionState.Connecting, reason);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (client is not null)
                {
                    await client.DisposeAsync();
                }
                var message = ex is OperationCanceledException
                    ? $"Timed out after {Config.StartupTimeout.TotalSeconds:0}s while connecting."
                    : Flatten(ex);
                AppendLog(message);
                if (IsUnauthorized(ex))
                {
                    SetState(McpConnectionState.NeedsAuth, "Authentication required.");
                    return;
                }
                attempt++;
                if (attempt >= MaxAttempts)
                {
                    SetState(McpConnectionState.Failed, message);
                    return;
                }
                lock (_gate)
                {
                    _error = message;
                }
            }
            var delay = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, Math.Pow(2, attempt - 1)));
            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task LoadAsync(McpClient client, CancellationToken ct)
    {
        client.RegisterNotificationHandler(NotificationMethods.ToolListChangedNotification, (_, _) =>
        {
            lock (_gate)
            {
                _toolsStale = true;
            }
            Changed?.Invoke(this);
            return ValueTask.CompletedTask;
        });
        var caps = client.ServerCapabilities;
        if (caps.Tools is not null)
        {
            await FetchToolsAsync(client, ct);
        }
        int prompts = 0, resources = 0;
        if (caps.Prompts is not null)
        {
            try
            {
                prompts = (await client.ListPromptsAsync(cancellationToken: ct)).Count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppendLog($"prompts/list failed: {ex.Message}");
            }
        }
        if (caps.Resources is not null)
        {
            try
            {
                resources = (await client.ListResourcesAsync(cancellationToken: ct)).Count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppendLog($"resources/list failed: {ex.Message}");
            }
        }
        lock (_gate)
        {
            _promptCount = prompts;
            _resourceCount = resources;
        }
    }

    private async Task FetchToolsAsync(McpClient client, CancellationToken ct)
    {
        var tools = new List<Tool>();
        TimeSpan? ttl = null;
        string? cursor = null;
        // 手动分页：每页的 TTL 取最小值；空串游标也表示还有下一页（规格 §4.1）
        for (var page = 0; page < 100; page++)
        {
            var result = await client.ListToolsAsync(new ListToolsRequestParams { Cursor = cursor }, ct);
            tools.AddRange(result.Tools);
            if (result.TimeToLive is { } pageTtl)
            {
                ttl = ttl is null || pageTtl < ttl ? pageTtl : ttl;
            }
            if (result.NextCursor is null)
            {
                break;
            }
            cursor = result.NextCursor;
        }
        lock (_gate)
        {
            _tools = tools;
            _toolsTtl = ttl ?? TimeSpan.Zero;
            _toolsFetchedAt = DateTimeOffset.UtcNow;
            _toolsStale = false;
        }
        Changed?.Invoke(this);
    }

    private IClientTransport CreateTransport()
    {
        if (Config.Transport == McpTransportKind.Stdio)
        {
            var (command, args, path) = ResolveCommand(Expansion.Command ?? "", Expansion.Args);
            var env = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var (k, v) in Expansion.Env)
            {
                env[k] = v;
            }
            if (path is not null && !env.ContainsKey("PATH"))
            {
                env["PATH"] = path;
            }
            return new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = Key,
                Command = command,
                Arguments = args,
                WorkingDirectory = Expansion.Cwd is { } cwd && Directory.Exists(cwd) ? cwd : null,
                EnvironmentVariables = env,
                InheritEnvironmentVariables = true,
                ShutdownTimeout = TimeSpan.FromSeconds(5),
                StandardErrorLines = AppendLog,
            });
        }
        var headers = new Dictionary<string, string>(Expansion.Headers, StringComparer.OrdinalIgnoreCase);
        return new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = Key,
            Endpoint = new Uri(Expansion.Url!),
            TransportMode = Config.Transport == McpTransportKind.Sse ? HttpTransportMode.Sse : HttpTransportMode.AutoDetect,
            AdditionalHeaders = headers,
            ConnectionTimeout = Config.StartupTimeout,
            OAuth = Config.HasStaticAuthorization ? null : _host.OAuth?.OptionsFor(this),
        });
    }

    /// <summary>
    /// 解析 stdio 命令：不是路径时用登录 shell 的 PATH 查找（GUI 启动的应用拿不到 shell PATH）；
    /// Windows 上的 .cmd / .bat（例如 npx）经 cmd /c 启动。
    /// </summary>
    private (string Command, IList<string> Args, string? Path) ResolveCommand(string command, IReadOnlyList<string> args)
    {
        var resolved = command;
        string? childPath = null;
        if (!Path.IsPathRooted(command) && !command.Contains('/') && !command.Contains('\\'))
        {
            if (_host.Locator.Find(command) is { } found)
            {
                resolved = found;
                childPath = _host.Locator.ChildPath(found);
            }
        }
        if (OperatingSystem.IsWindows() && Path.GetExtension(resolved) is var ext
            && (ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            return ("cmd.exe", ["/c", resolved, .. args], childPath);
        }
        return (resolved, args.ToList(), childPath);
    }

    private McpClientOptions CreateOptions() => new()
    {
        ClientInfo = new Implementation { Name = "TCode", Version = _host.ClientVersion },
        Capabilities = new ClientCapabilities
        {
            Elicitation = new ElicitationCapability { Form = new(), Url = new() },
        },
        InitializationTimeout = Config.StartupTimeout,
        Handlers = new McpClientHandlers
        {
            ElicitationHandler = async (request, ct) =>
            {
                if (request is null || _host.Elicit is not { } elicit)
                {
                    return new ElicitResult { Action = "cancel" };
                }
                return await elicit(new McpElicitation(Key, Config.Title, McpCallContext.SessionId, request), ct);
            },
        },
    };

    private void SetState(McpConnectionState state, string? error)
    {
        lock (_gate)
        {
            _state = state;
            _error = error;
            if (state is McpConnectionState.Connected or McpConnectionState.Failed or McpConnectionState.NeedsAuth or McpConnectionState.Idle)
            {
                _settled.TrySetResult();
            }
            else if (_settled.Task.IsCompleted)
            {
                _settled = NewSignal();
            }
            if (state != McpConnectionState.Connected)
            {
                _tools = state == McpConnectionState.Connecting ? _tools : [];
            }
        }
        Changed?.Invoke(this);
    }

    internal void AppendLog(string line)
    {
        lock (_stderr)
        {
            _stderr.AddLast(line);
            while (_stderr.Count > StderrCapacity)
            {
                _stderr.RemoveFirst();
            }
        }
    }

    private async Task StopAsync()
    {
        Task? loop;
        McpClient? client;
        lock (_gate)
        {
            _lifetime.Cancel();
            loop = _loop;
            _loop = null;
            client = _client;
            _client = null;
        }
        if (client is not null)
        {
            try
            {
                await client.DisposeAsync();
            }
            catch (Exception ex)
            {
                _host.Logger.LogDebug(ex, "MCP server {Server} did not shut down cleanly", Key);
            }
        }
        if (loop is not null)
        {
            try
            {
                await loop;
            }
            catch (Exception)
            {
                // 循环内部已记录
            }
        }
        SetState(McpConnectionState.Idle, null);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifetime.Dispose();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal static bool IsUnauthorized(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } or McpAuthRequiredException)
            {
                return true;
            }
            if (e is AggregateException agg && agg.InnerExceptions.Any(IsUnauthorized))
            {
                return true;
            }
        }
        return false;
    }

    private static string Describe(ClientCompletionDetails details) => details switch
    {
        StdioClientCompletionDetails stdio => $"process exited (code {stdio.ExitCode?.ToString() ?? "?"})"
                                              + (stdio.StandardErrorTail is { Count: > 0 } tail ? ": " + tail[^1] : ""),
        HttpClientCompletionDetails { HttpStatusCode: { } status } => $"HTTP {(int)status} {status}",
        _ => details.Exception?.Message ?? "closed",
    };

    private static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null && parts.Count < 3; e = e.InnerException)
        {
            if (!parts.Contains(e.Message))
            {
                parts.Add(e.Message);
            }
        }
        return string.Join(" → ", parts);
    }

    /// <summary>连接池的键（不创建连接）。</summary>
    public static string IdFor(McpServerConfig config, McpExpansion e)
    {
        var sb = new StringBuilder();
        sb.Append(config.Source).Append('\n').Append(config.Key).Append('\n').Append(config.Transport).Append('\n');
        if (config.Transport == McpTransportKind.Stdio)
        {
            sb.Append(e.Command).Append('\n').AppendJoin('\u0001', e.Args).Append('\n').Append(e.Cwd).Append('\n');
            foreach (var (k, v) in e.Env.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                sb.Append(k).Append('=').Append(v).Append('\n');
            }
        }
        else
        {
            sb.Append(e.Url).Append('\n');
            foreach (var (k, v) in e.Headers.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append(k).Append('=').Append(v).Append('\n');
            }
            sb.Append(config.OAuth?.ClientId).Append(config.OAuth?.CallbackPort).Append(config.OAuth?.Scopes);
        }
        sb.Append(config.StartupTimeout.TotalSeconds);
        return config.Key + ":" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }
}

/// <summary>工具调用时服务器不可用。</summary>
public sealed class McpUnavailableException(string server, string? reason)
    : Exception($"MCP server '{server}' is not connected" + (reason is { Length: > 0 } ? $": {reason}" : "."));
