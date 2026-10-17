using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace TCode.Terminal.Protocol;

/// <summary>
/// 传输（04 §2.1）：macOS / Linux 为 AF_UNIX 流式 socket，Windows 为字节模式命名管道。endpoint 字符串即 json 中的 <c>endpoint</c>：
/// Unix 为 socket 路径，Windows 为 <c>\\.\pipe\&lt;name&gt;</c>。
/// </summary>
public static partial class PtyTransport
{
    public const string PipePrefix = @"\\.\pipe\";

    public static async Task<Stream> ConnectAsync(string endpoint, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            // CurrentUserOnly：连接后校验管道的所有者是当前用户（防止抢注的管道冒充 host，§2.3）
            var pipe = new NamedPipeClientStream(".", PipeName(endpoint), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
                return pipe;
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public static string PipeName(string endpoint) => endpoint.StartsWith(PipePrefix, StringComparison.Ordinal) ? endpoint[PipePrefix.Length..] : endpoint;

    /// <summary>开始监听。Unix：bind 前删除残留的 socket 文件（调用方已持有生命周期锁），bind 后 chmod 0600。</summary>
    public static PtyListener Listen(string endpoint) =>
        OperatingSystem.IsWindows() ? new PipeListener(PipeName(endpoint)) : new UnixListener(endpoint);

    // ---- 对端凭据（§2.3 第 2 层）----

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEuid();

    /// <summary>Unix：对端 uid 必须等于自己的 euid。读不到凭据时拒绝。</summary>
    internal static bool PeerIsSameUser(Socket socket)
    {
        Span<byte> buffer = stackalloc byte[128];
        try
        {
            if (OperatingSystem.IsLinux())
            {
                // SOL_SOCKET=1, SO_PEERCRED=17 → struct ucred { pid_t pid; uid_t uid; gid_t gid; }
                var n = socket.GetRawSocketOption(1, 17, buffer);
                return n >= 12 && BitConverter.ToUInt32(buffer[4..]) == GetEuid();
            }
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            {
                // SOL_LOCAL=0, LOCAL_PEERCRED=1 → struct xucred { u_int cr_version; uid_t cr_uid; ... }
                var n = socket.GetRawSocketOption(0, 1, buffer);
                return n >= 8 && BitConverter.ToUInt32(buffer[4..]) == GetEuid();
            }
        }
        catch (SocketException)
        {
            return false;
        }
        return false;
    }
}

/// <summary>监听器：AcceptAsync 返回已通过对端校验的连接流。</summary>
public abstract class PtyListener : IDisposable
{
    public abstract string Endpoint { get; }

    /// <summary>接受下一个连接；对端校验失败的连接被关闭并跳过（返回前记录在 <see cref="RejectedPeers"/>）。</summary>
    public abstract Task<Stream> AcceptAsync(CancellationToken cancellationToken);

    public int RejectedPeers { get; protected set; }

    public abstract void Dispose();
}

[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
internal sealed class UnixListener : PtyListener
{
    private readonly Socket _socket;
    private readonly string _path;

    public UnixListener(string path)
    {
        _path = path;
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _socket.Bind(new UnixDomainSocketEndPoint(path));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _socket.Listen(32);
    }

    public override string Endpoint => _path;

    public override async Task<Stream> AcceptAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var client = await _socket.AcceptAsync(cancellationToken).ConfigureAwait(false);
            if (PtyTransport.PeerIsSameUser(client))
            {
                return new NetworkStream(client, ownsSocket: true);
            }
            RejectedPeers++;
            client.Dispose();
        }
    }

    public override void Dispose()
    {
        _socket.Dispose();
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// Windows 命名管道监听：始终保持一个空闲实例（§2.1）。CurrentUserOnly 让 .NET 只给当前用户授权。
/// 04 §2.1 要求的 PIPE_REJECT_REMOTE_CLIENTS 与 FILE_FLAG_FIRST_PIPE_INSTANCE 需要自己调 CreateNamedPipeW，尚未实现（13 M2 状态）。
/// </summary>
internal sealed class PipeListener(string name) : PtyListener
{
    private readonly CancellationTokenSource _disposed = new();

    public override string Endpoint => PtyTransport.PipePrefix + name;

    public override async Task<Stream> AcceptAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposed.Token);
        var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.WaitForConnectionAsync(linked.Token).ConfigureAwait(false);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public override void Dispose() => _disposed.Cancel();
}
