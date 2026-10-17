using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TKCode.Agent;
using TKCode.Agent.Coding;
using TKCode.Agent.Coding.Tools;
using TKCode.Agent.Coding.Workspace;
using TKCode.Ai.Messages;
using TKCode.Mcp;
using TKCode.Rpc;
using TKCode.Runtimes;

namespace TKCode.AgentHost;

public sealed record BridgeTokenParams(string Token);

public sealed record BridgeToolDto(string Name, string? Title, string Description, string InputSchemaJson, bool ReadOnlyHint, bool DestructiveHint);

public sealed record BridgeToolsResult(IReadOnlyList<BridgeToolDto> Tools);

public sealed record BridgeCallParams(string Token, string Name, string ArgumentsJson);

/// <param name="ResultJson">MCP CallToolResult（SDK 的 JSON 形式），由桥接进程原样返回给运行时。</param>
public sealed record BridgeCallResult(string ResultJson);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BridgeTokenParams))]
[JsonSerializable(typeof(BridgeToolsResult))]
[JsonSerializable(typeof(BridgeCallParams))]
[JsonSerializable(typeof(BridgeCallResult))]
internal sealed partial class BridgeJsonContext : JsonSerializerContext;

/// <summary>
/// 外部运行时的 MCP 桥接（Host 侧，docs/design/mcp.md §10）：每个外部会话一个随机令牌；
/// 桥接进程（<c>TKCode.AgentHost --mcp-bridge</c>）经本机 Unix socket 调用 bridge/list、bridge/call。
/// 工具来自该会话项目可用的 MCP 服务器（共享连接池），调用前按 TKCode 的审批策略判定。
/// </summary>
public sealed class McpBridgeServer(SessionManager sessions, McpManager mcp, ILogger logger) : IAsyncDisposable
{
    public const string SocketVariable = "TKCODE_MCP_BRIDGE_SOCKET";
    public const string TokenVariable = "TKCODE_MCP_BRIDGE_TOKEN";
    public const string ListMethod = "bridge/list";
    public const string CallMethod = "bridge/call";

    /// <summary>运行时看到的工具名为 mcp__tkcode__&lt;名字&gt;：名字限制在 52 字符内，总长不超过 64。</summary>
    private const int MaxBridgeNameLength = 64 - 12;

    private readonly ConcurrentDictionary<string, Client> _clients = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private Socket? _listener;
    private string? _socketPath;
    private CancellationTokenSource? _cts;

    private sealed class Client(string sessionId, string cwd)
    {
        public string SessionId { get; } = sessionId;
        public string Cwd { get; } = cwd;
        public IDynamicToolSource? Source { get; set; }
        public Dictionary<string, McpTool> Tools { get; } = new(StringComparer.Ordinal);
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    /// <summary>为外部会话生成桥接启动参数；项目没有可用的 MCP 服务器时返回 null（不注入）。</summary>
    public McpBridgeLaunch? LaunchFor(string sessionId, string cwd)
    {
        if (!mcp.Resolve(cwd).Any(e => e.Availability == McpServerAvailability.Ready) || EnsureListening() is not { } socket)
        {
            return null;
        }
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        _clients[token] = new Client(sessionId, cwd);
        var (self, args) = SelfCommand?.Invoke() ?? DefaultSelfCommand();
        return new McpBridgeLaunch(self, args, new Dictionary<string, string> { [SocketVariable] = socket, [TokenVariable] = token });
    }

    /// <summary>启动桥接进程的命令（测试中替换为 dotnet TKCode.AgentHost.dll）。</summary>
    public Func<(string Command, IReadOnlyList<string> Args)>? SelfCommand { get; set; }

    private static (string, IReadOnlyList<string>) DefaultSelfCommand()
    {
        var self = Environment.ProcessPath ?? "TKCode.AgentHost";
        // 开发环境经 dotnet 启动 Host 时，桥接进程同样经 dotnet 启动
        return Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? (self, [Path.Combine(AppContext.BaseDirectory, "TKCode.AgentHost.dll"), "--mcp-bridge"])
            : (self, ["--mcp-bridge"]);
    }

    /// <summary>会话关闭：作废其令牌并释放连接租约。</summary>
    public async Task ForgetAsync(string sessionId)
    {
        foreach (var (token, client) in _clients.Where(c => c.Value.SessionId == sessionId).ToList())
        {
            _clients.TryRemove(token, out _);
            if (client.Source is { } source)
            {
                await source.DisposeAsync();
            }
        }
    }

    private string? EnsureListening()
    {
        lock (_gate)
        {
            if (_socketPath is not null)
            {
                return _socketPath;
            }
            try
            {
                var path = SocketPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                listener.Bind(new UnixDomainSocketEndPoint(path));
                listener.Listen(16);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                _listener = listener;
                _cts = new CancellationTokenSource();
                _ = AcceptLoopAsync(listener, _cts.Token);
                _socketPath = path;
                return path;
            }
            catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                logger.LogWarning(ex, "MCP bridge for external runtimes is unavailable");
                return null;
            }
        }
    }

    private static string SocketPath()
    {
        var preferred = Path.Combine(TKCodePaths.Home, "sockets", $"mcp-{Environment.ProcessId}.sock");
        if (System.Text.Encoding.UTF8.GetByteCount(preferred) < 100)
        {
            return preferred;
        }
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(TKCodePaths.Home)))[..12];
        return Path.Combine(Path.GetTempPath(), $"tkcode-{hash}", $"mcp-{Environment.ProcessId}.sock");
    }

    private async Task AcceptLoopAsync(Socket listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptAsync(ct);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException ex)
            {
                logger.LogWarning(ex, "MCP bridge accept failed");
                continue;
            }
            var stream = new NetworkStream(socket, ownsSocket: true);
            var rpc = RpcSerialization.CreateConnection(stream, stream);
            var j = BridgeJsonContext.Default;
            rpc.AddRequestHandler(ListMethod, j.BridgeTokenParams, j.BridgeToolsResult, (p, c) => ListAsync(p.Token, c));
            rpc.AddRequestHandler(CallMethod, j.BridgeCallParams, j.BridgeCallResult, CallAsync);
            rpc.Disconnected += _ => stream.Dispose();
            rpc.Start();
        }
    }

    private Client Authenticate(string token) =>
        _clients.TryGetValue(token, out var client) ? client : throw new UnauthorizedAccessException("Unknown MCP bridge token.");

    private async Task<BridgeToolsResult> ListAsync(string token, CancellationToken ct)
    {
        var client = Authenticate(token);
        await client.Gate.WaitAsync(ct);
        try
        {
            var first = client.Source is null;
            client.Source ??= mcp.CreateSource(new ToolContext(new WorkspaceRoots(client.Cwd), client.SessionId));
            var set = await client.Source.RefreshAsync(first, ct);
            client.Tools.Clear();
            var tools = set.Tools.OfType<McpTool>().ToList();
            foreach (var group in tools.GroupBy(t => t.ServerKey))
            {
                var names = McpToolNames.Assign(group.Key, group.Select(t => t.Protocol.Name), prefix: "", MaxBridgeNameLength);
                foreach (var tool in group)
                {
                    client.Tools[names[tool.Protocol.Name]] = tool;
                }
            }
            return new BridgeToolsResult(client.Tools.Select(t => new BridgeToolDto(t.Key, t.Value.Protocol.Title, t.Value.Description,
                t.Value.ParametersSchemaJson, t.Value.Protocol.Annotations?.ReadOnlyHint == true, t.Value.Protocol.Annotations?.DestructiveHint == true)).ToList());
        }
        finally
        {
            client.Gate.Release();
        }
    }

    private async Task<BridgeCallResult> CallAsync(BridgeCallParams call, CancellationToken ct)
    {
        var client = Authenticate(call.Token);
        if (!client.Tools.TryGetValue(call.Name, out var tool))
        {
            await ListAsync(call.Token, ct);
            if (!client.Tools.TryGetValue(call.Name, out tool))
            {
                return Error($"Unknown tool '{call.Name}'.");
            }
        }
        JsonElement args;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            args = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return Error($"Invalid tool arguments: {ex.Message}");
        }
        // 审批：与内置 Agent 相同的策略（权限档位取自该外部会话）；运行时自己的询问已对 tkcode 服务器放行
        if (sessions.TryGet(client.SessionId) is not { } session)
        {
            return Error("The TKCode session is no longer open.");
        }
        var toolCall = new ToolCallContent("bridge_" + Guid.NewGuid().ToString("N")[..12], McpToolNames.Prefix + McpBridgeLaunch.ServerName + "__" + call.Name,
            args.GetRawText());
        var decision = await sessions.ApproveAsync(client.SessionId, session,
            new BeforeToolCallContext(tool, toolCall, args, tool.DescribePermission(args)), ct);
        if (decision.Block)
        {
            return Error(decision.Reason ?? "The user denied this tool call.");
        }
        var arguments = args.ValueKind == JsonValueKind.Object
            ? args.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone())
            : new Dictionary<string, object?>();
        McpCallContext.SessionId = client.SessionId;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(tool.Connection.Config.ToolTimeout);
            var result = await tool.Connection.CallToolAsync(tool.Protocol.Name, arguments, null, timeout.Token);
            return new BridgeCallResult(JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(CallToolResult))));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Error($"MCP tool '{tool.Protocol.Name}' timed out.");
        }
        catch (Exception ex) when (ex is McpException or McpUnavailableException or IOException or HttpRequestException)
        {
            return Error(ex.Message);
        }
    }

    private static BridgeCallResult Error(string message) =>
        new(JsonSerializer.Serialize(new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = message }] },
            McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(CallToolResult))));

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _listener?.Dispose();
        foreach (var client in _clients.Values)
        {
            if (client.Source is { } source)
            {
                await source.DisposeAsync();
            }
        }
        _clients.Clear();
        if (_socketPath is not null)
        {
            try
            {
                File.Delete(_socketPath);
            }
            catch (IOException)
            {
            }
        }
    }
}

/// <summary>
/// 桥接进程（<c>TKCode.AgentHost --mcp-bridge</c>）：作为 stdio MCP 服务器被外部运行时启动，
/// 把 tools/list、tools/call 经 Unix socket 转发给启动它的 TKCode Host。stdout 只输出 MCP 消息。
/// </summary>
public static class McpBridgeClient
{
    public static async Task<int> RunAsync()
    {
        var socketPath = Environment.GetEnvironmentVariable(McpBridgeServer.SocketVariable);
        var token = Environment.GetEnvironmentVariable(McpBridgeServer.TokenVariable);
        if (string.IsNullOrEmpty(socketPath) || string.IsNullOrEmpty(token))
        {
            Console.Error.WriteLine("tkcode mcp bridge: missing TKCODE_MCP_BRIDGE_SOCKET / TKCODE_MCP_BRIDGE_TOKEN.");
            return 2;
        }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
        }
        catch (SocketException ex)
        {
            Console.Error.WriteLine($"tkcode mcp bridge: cannot reach TKCode ({ex.Message}).");
            return 3;
        }
        await using var stream = new NetworkStream(socket, ownsSocket: true);
        await using var rpc = RpcSerialization.CreateConnection(stream, stream);
        rpc.Start();
        var j = BridgeJsonContext.Default;

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "tkcode", Version = typeof(McpBridgeClient).Assembly.GetName().Version?.ToString(3) ?? "0.1.0" },
            ServerInstructions = "Tools from the MCP servers configured in TKCode. Tool names are <server>__<tool>.",
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = async (_, ct) =>
                {
                    var tools = await rpc.InvokeAsync(McpBridgeServer.ListMethod, new BridgeTokenParams(token), j.BridgeTokenParams, j.BridgeToolsResult, ct);
                    return new ListToolsResult
                    {
                        Tools = tools.Tools.Select(t =>
                        {
                            using var schema = JsonDocument.Parse(t.InputSchemaJson);
                            return new Tool
                            {
                                Name = t.Name,
                                Title = t.Title,
                                Description = t.Description,
                                InputSchema = schema.RootElement.Clone(),
                                Annotations = t.ReadOnlyHint || t.DestructiveHint
                                    ? new ToolAnnotations { ReadOnlyHint = t.ReadOnlyHint, DestructiveHint = t.DestructiveHint }
                                    : null,
                            };
                        }).ToList(),
                    };
                },
                CallToolHandler = async (context, ct) =>
                {
                    var p = context.Params!;
                    var arguments = p.Arguments is null ? "{}" : JsonSerializer.Serialize(p.Arguments,
                        McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(IDictionary<string, JsonElement>)));
                    var result = await rpc.InvokeAsync(McpBridgeServer.CallMethod, new BridgeCallParams(token, p.Name, arguments), j.BridgeCallParams,
                        j.BridgeCallResult, ct);
                    return (CallToolResult)JsonSerializer.Deserialize(result.ResultJson, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(CallToolResult)))!;
                },
            },
        };
        await using var server = McpServer.Create(new StdioServerTransport(options), options);
        await server.RunAsync();
        return 0;
    }
}
