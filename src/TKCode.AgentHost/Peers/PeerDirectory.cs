using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using TKCode.Agent.Coding;
using TKCode.Rpc;

namespace TKCode.AgentHost.Peers;

/// <summary>本 Host 提供给其他 Host 的会话（跨进程 list_agents / send_message）。</summary>
public interface IPeerHandler
{
    IReadOnlyList<PeerSessionDto> ListLocal();

    /// <summary>投递来自其他进程的消息；返回投递说明。找不到目标时抛出 <see cref="KeyNotFoundException"/>。</summary>
    string DeliverLocal(PeerSendRequest request);
}

public sealed record PeerSessionDto(string Name, string Ref, string Cwd, string? Title, bool Busy);

public sealed record PeerSendRequest(string To, string FromName, string FromRef, string Message);

public sealed record PeerSendResult(string Result);

public sealed record PeerListResult(int ProcessId, IReadOnlyList<PeerSessionDto> Sessions);

/// <summary>注册目录中的一项：~/.tkcode/peers/&lt;pid&gt;.json。</summary>
public sealed record PeerRegistration(int ProcessId, string Socket, DateTimeOffset StartedAt);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PeerRegistration))]
[JsonSerializable(typeof(PeerListResult))]
[JsonSerializable(typeof(PeerSendRequest))]
[JsonSerializable(typeof(PeerSendResult))]
internal sealed partial class PeerJsonContext : JsonSerializerContext;

/// <summary>
/// 本机 TKCode Host 之间的会话发现与消息投递（docs/design/builtin-agent-tools.md §9.2）：
/// 每个 Host 监听一个 Unix domain socket，并在 ~/.tkcode/peers/ 写一个注册文件；
/// 其他 Host 扫描注册目录（剔除已退出的进程），连上 socket 用 JSON-RPC 调用 peer/list、peer/send。
/// </summary>
public sealed class PeerDirectory(ILogger<PeerDirectory> logger) : IAsyncDisposable
{
    private const string ListMethod = "peer/list";
    private const string SendMethod = "peer/send";
    private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(2);

    private Socket? _listener;
    private CancellationTokenSource? _cts;
    private string? _socketPath;
    private string? _registrationPath;

    public static string RegistryDirectory => Path.Combine(TKCodePaths.Home, "peers");

    public sealed record Peer(int ProcessId, string Socket, PeerSessionDto Session);

    public bool IsListening => _listener is not null;

    /// <summary>测试用：列出本进程自己的注册（同一进程内模拟两个 Host）。</summary>
    public bool IncludeSelf { get; init; }

    public void Start(IPeerHandler handler)
    {
        if (_listener is not null || Environment.GetEnvironmentVariable("TKCODE_DISABLE_PEERS") == "1")
        {
            return;
        }
        try
        {
            _socketPath = SocketPathFor(Environment.ProcessId);
            Directory.CreateDirectory(Path.GetDirectoryName(_socketPath)!);
            if (File.Exists(_socketPath))
            {
                File.Delete(_socketPath);
            }
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
            listener.Listen(16);
            _listener = listener;
            _cts = new CancellationTokenSource();
            _ = AcceptLoopAsync(listener, handler, _cts.Token);

            Directory.CreateDirectory(RegistryDirectory);
            _registrationPath = Path.Combine(RegistryDirectory, $"{Environment.ProcessId}.json");
            File.WriteAllText(_registrationPath, JsonSerializer.Serialize(
                new PeerRegistration(Environment.ProcessId, _socketPath, DateTimeOffset.UtcNow), PeerJsonContext.Default.PeerRegistration));
            logger.LogInformation("Peer socket listening on {Socket}", _socketPath);
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            logger.LogWarning(ex, "Cross-process agent messaging is unavailable");
            _listener?.Dispose();
            _listener = null;
        }
    }

    /// <summary>
    /// macOS 的 sun_path 上限是 104 字节：数据目录过长（例如测试用的临时目录）时，socket 放到系统临时目录下。
    /// </summary>
    private static string SocketPathFor(int pid)
    {
        var preferred = Path.Combine(TKCodePaths.Home, "sockets", $"{pid}.sock");
        if (System.Text.Encoding.UTF8.GetByteCount(preferred) < 100)
        {
            return preferred;
        }
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(TKCodePaths.Home)))[..12];
        return Path.Combine(Path.GetTempPath(), $"tkcode-{hash}", $"{pid}.sock");
    }

    private async Task AcceptLoopAsync(Socket listener, IPeerHandler handler, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(ct);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException ex)
            {
                logger.LogDebug(ex, "Peer accept failed");
                continue;
            }
            _ = ServeAsync(client, handler);
        }
    }

    private async Task ServeAsync(Socket client, IPeerHandler handler)
    {
        await using var stream = new NetworkStream(client, ownsSocket: true);
        await using var rpc = new JsonRpcConnection(stream, stream);
        rpc.AddRequestHandler(ListMethod, PeerJsonContext.Default.PeerListResult,
            _ => Task.FromResult(new PeerListResult(Environment.ProcessId, handler.ListLocal())));
        rpc.AddRequestHandler(SendMethod, PeerJsonContext.Default.PeerSendRequest, PeerJsonContext.Default.PeerSendResult,
            (request, _) => Task.FromResult(new PeerSendResult(handler.DeliverLocal(request))));
        rpc.Start();
        try
        {
            await rpc.Completion.WaitAsync(TimeSpan.FromMinutes(1));
        }
        catch (Exception)
        {
            // 对端断开或超时。
        }
    }

    /// <summary>本机其他 Host 的会话。连不上的 Host 跳过，已退出进程的注册文件顺手清理。</summary>
    public async Task<IReadOnlyList<Peer>> ListPeersAsync(CancellationToken ct)
    {
        var result = new List<Peer>();
        foreach (var registration in Registrations())
        {
            try
            {
                await using var connection = await ConnectAsync(registration.Socket, ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(PeerTimeout);
                var list = await connection.Rpc.InvokeAsync(ListMethod, PeerJsonContext.Default.PeerListResult, timeout.Token);
                result.AddRange(list.Sessions.Select(s => new Peer(registration.ProcessId, registration.Socket, s)));
            }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or RpcConnectionLostException or RpcRemoteException)
            {
                logger.LogDebug(ex, "Peer {Pid} is unreachable", registration.ProcessId);
            }
        }
        return result;
    }

    public async Task<string> SendAsync(Peer peer, PeerSendRequest request, CancellationToken ct)
    {
        await using var connection = await ConnectAsync(peer.Socket, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PeerTimeout);
        try
        {
            var result = await connection.Rpc.InvokeAsync(SendMethod, request, PeerJsonContext.Default.PeerSendRequest,
                PeerJsonContext.Default.PeerSendResult, timeout.Token);
            return result.Result;
        }
        catch (RpcRemoteException ex)
        {
            throw new InvalidOperationException($"Delivery to {peer.Session.Name} failed: {ex.Message}");
        }
    }

    private IEnumerable<PeerRegistration> Registrations()
    {
        if (!Directory.Exists(RegistryDirectory))
        {
            yield break;
        }
        foreach (var file in Directory.EnumerateFiles(RegistryDirectory, "*.json"))
        {
            PeerRegistration? registration = null;
            try
            {
                registration = JsonSerializer.Deserialize(File.ReadAllText(file), PeerJsonContext.Default.PeerRegistration);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
            }
            if (registration is null || (registration.ProcessId == Environment.ProcessId && !IncludeSelf))
            {
                continue;
            }
            if (!IsAlive(registration.ProcessId))
            {
                TryDelete(file);
                continue;
            }
            yield return registration;
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private sealed class Connection(Socket socket, NetworkStream stream, JsonRpcConnection rpc) : IAsyncDisposable
    {
        public JsonRpcConnection Rpc { get; } = rpc;

        public async ValueTask DisposeAsync()
        {
            await Rpc.DisposeAsync();
            await stream.DisposeAsync();
            socket.Dispose();
        }
    }

    private static async Task<Connection> ConnectAsync(string socketPath, CancellationToken ct)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(PeerTimeout);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), timeout.Token);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        var stream = new NetworkStream(socket, ownsSocket: false);
        var rpc = new JsonRpcConnection(stream, stream);
        rpc.Start();
        return new Connection(socket, stream, rpc);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }
        _listener?.Dispose();
        _listener = null;
        if (_registrationPath is not null)
        {
            TryDelete(_registrationPath);
        }
        if (_socketPath is not null)
        {
            TryDelete(_socketPath);
        }
    }
}
