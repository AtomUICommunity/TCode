using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TKCode.Desktop.Services;

/// <summary>
/// 单实例：数据目录里的锁文件决定谁是主实例（进程退出或崩溃时由系统释放），主实例在命名管道上收命令。
/// 再次启动 TKCode（开始菜单、跳转列表的条目、点已隐藏到托盘的应用）只把命令转发给主实例然后退出，
/// 不会再多开一个进程和 Agent Host。数据目录不同（TKCODE_HOME）互不干扰；启动参数 --new-instance 跳过。
/// </summary>
internal sealed partial class SingleInstance : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private const int MaxMessageBytes = 64 * 1024;

    private static SingleInstance? _current;

    private readonly FileStream _lock;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(FileStream lockFile, string pipeName)
    {
        _lock = lockFile;
        _pipeName = pipeName;
    }

    /// <summary>主实例收到命令时在后台线程触发；调用方负责切回 UI 线程。</summary>
    public event Action<AppCommand>? CommandReceived;

    /// <summary>
    /// 成为主实例返回 true；已有主实例时把命令转发给它并返回 false（调用方应直接退出）。
    /// 转发失败（主实例卡住）时放弃单实例，照常启动。
    /// </summary>
    public static bool TryClaim(string home, AppCommand command, out SingleInstance? instance)
    {
        instance = null;
        var pipeName = PipeNameFor(home);
        FileStream? lockFile;
        try
        {
            lockFile = TryLock(Path.Combine(home, "instance.lock"));
        }
        catch (UnauthorizedAccessException)
        {
            // 数据目录不可写：没法协调，放弃单实例
            return true;
        }
        if (lockFile is null)
        {
            return !Forward(pipeName, command);
        }
        instance = new SingleInstance(lockFile, pipeName);
        _ = instance.ServeAsync(instance._stop.Token);
        _current = instance;
        return true;
    }

    /// <summary>
    /// 放弃主实例身份（关闭管道、释放锁文件），进程本身继续运行。
    /// 应用内更新要先启动新版本再退出：不放弃的话，新进程会把命令转发给正在退出的旧进程而自己退出。
    /// </summary>
    public static void Release() => _current?.Dispose();

    public void Dispose()
    {
        if (ReferenceEquals(_current, this))
        {
            _current = null;
        }
        _stop.Cancel();
        _lock.Dispose();
    }

    /// <summary>别的进程持有锁时返回 null。</summary>
    private static FileStream? TryLock(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            // FileShare.None：Windows 上独占打开，Unix 上由运行时转为 flock(LOCK_EX | LOCK_NB)
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string PipeNameFor(string home)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(home)));
        return "tkcode-" + Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    private static bool Forward(string pipeName, AppCommand command)
    {
        if (OperatingSystem.IsWindows())
        {
            // 本进程是用户刚点出来的，有权把前台权限交给主实例；否则主实例 Activate() 只会让任务栏按钮闪烁
            AllowSetForegroundWindow(uint.MaxValue);
        }
        var message = Encoding.UTF8.GetBytes(string.Join('\0', command.ToArguments()));
        var deadline = DateTime.UtcNow + ConnectTimeout;
        while (true)
        {
            try
            {
                // 主实例刚拿到锁、管道还没开始监听时连接会超时，重试到截止时间
                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                client.Connect(500);
                client.Write(message);
                client.Flush();
                return true;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    return false;
                }
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>ASFW_ANY：允许任意进程（即主实例）设置前台窗口。</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint dwProcessId);

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                // 客户端写完即关闭；超时是为了不被只连接不写入的客户端卡住
                using var read = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                read.CancelAfter(TimeSpan.FromSeconds(2));
                var command = AppCommand.Parse(await ReadArgumentsAsync(server, read.Token).ConfigureAwait(false));
                CommandReceived?.Invoke(command);
            }
            catch (OperationCanceledException)
            {
                // 停止，或某个客户端读超时：停止时由循环条件退出，否则继续等下一个连接
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task<string[]> ReadArgumentsAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxMessageBytes)
            {
                return [];
            }
        }
        var text = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        return text.Length == 0 ? [] : text.Split('\0');
    }
}
