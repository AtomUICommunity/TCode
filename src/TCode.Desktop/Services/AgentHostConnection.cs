using System.Diagnostics;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using TCode.Rpc;

namespace TCode.Desktop.Services;

/// <summary>
/// 管理 Agent Host 子进程：按需启动、建立 stdio JSON-RPC、把 Host 推送的会话事件派发到 UI 线程。
/// Host 崩溃不会影响 UI；下一次调用会重新拉起进程。
/// </summary>
public sealed class AgentHostConnection(ILogger<AgentHostConnection> logger) : IAgentHostClient, IDisposable
{
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private Process? _process;
    private JsonRpcConnection? _rpc;
    private IAgentHostServer? _server;

    /// <summary>会话事件（已切换到 UI 线程）。</summary>
    public event Action<SessionEventNotification>? SessionEvent;

    /// <summary>Host 断开（进程退出或崩溃）。</summary>
    public event Action<string>? Disconnected;

    /// <summary>工具审批处理器（UI 弹窗）。未设置时默认拒绝。</summary>
    public Func<ToolApprovalRequest, Task<ToolApprovalResponse>>? ApprovalHandler { get; set; }

    public async Task<IAgentHostServer> GetServerAsync(CancellationToken ct = default)
    {
        if (_server is not null && _rpc is { IsConnected: true })
        {
            return _server;
        }
        await _startLock.WaitAsync(ct);
        try
        {
            if (_server is not null && _rpc is { IsConnected: true })
            {
                return _server;
            }
            StartProcess();
            var info = await _server!.InitializeAsync(new InitializeRequest(RpcProtocol.Version, "TCode.Desktop"), ct);
            logger.LogInformation("Agent host started: pid {Pid}, version {Version}", info.ProcessId, info.HostVersion);
            return _server;
        }
        finally
        {
            _startLock.Release();
        }
    }

    private void StartProcess()
    {
        // TCODE_AGENTHOST_DIR 用于测试（直接使用 Host 项目的构建输出）。
        var dir = Environment.GetEnvironmentVariable("TCODE_AGENTHOST_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(AppContext.BaseDirectory, "agenthost");
        var apphost = Path.Combine(dir, OperatingSystem.IsWindows() ? "TCode.AgentHost.exe" : "TCode.AgentHost");
        var start = File.Exists(apphost)
            ? new ProcessStartInfo(apphost)
            : new ProcessStartInfo("dotnet", $"\"{Path.Combine(dir, "TCode.AgentHost.dll")}\"");
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.WorkingDirectory = dir;

        var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start agent host.");
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                logger.LogDebug("[host] {Line}", e.Data);
            }
        };
        process.BeginErrorReadLine();

        var rpc = RpcSerialization.CreateConnection(process.StandardInput.BaseStream, process.StandardOutput.BaseStream);
        AgentHostClientDispatcher.Register(rpc, this);
        _server = new AgentHostServerProxy(rpc);
        rpc.Disconnected += reason =>
        {
            logger.LogWarning("Agent host disconnected: {Reason}", reason);
            Dispatcher.UIThread.Post(() => Disconnected?.Invoke(reason));
        };
        rpc.Start();
        _process = process;
        _rpc = rpc;
    }

    /// <summary>
    /// 会话事件先进入队列，界面线程每次取出当前积压的全部事件依次处理：高频增量（流式文本、工具参数）
    /// 不再每个都单独排队到界面线程，事件顺序保持不变。
    /// </summary>
    void IAgentHostClient.OnSessionEvent(SessionEventNotification notification)
    {
        _events.Enqueue(notification);
        if (Interlocked.Exchange(ref _drainScheduled, 1) == 0)
        {
            Dispatcher.UIThread.Post(DrainEvents);
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentQueue<SessionEventNotification> _events = new();
    private int _drainScheduled;

    private void DrainEvents()
    {
        Volatile.Write(ref _drainScheduled, 0);
        while (_events.TryDequeue(out var notification))
        {
            SessionEvent?.Invoke(notification);
        }
    }

    /// <summary>settings.json 变化后 Host 推送的最新模型列表（UI 线程上触发）。</summary>
    public event Action<ModelsChangedNotification>? ModelsChanged;

    void IAgentHostClient.OnModelsChanged(ModelsChangedNotification notification) =>
        Dispatcher.UIThread.Post(() => ModelsChanged?.Invoke(notification));

    async Task<ToolApprovalResponse> IAgentHostClient.RequestToolApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken)
    {
        var handler = ApprovalHandler;
        if (handler is null)
        {
            return new ToolApprovalResponse(false, "No approval handler.");
        }
        return await Dispatcher.UIThread.InvokeAsync(() => handler(request));
    }

    public void Dispose()
    {
        try
        {
            _rpc?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (_process is { HasExited: false } p)
            {
                p.StandardInput.Close();
                if (!p.WaitForExit(2000))
                {
                    p.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error while stopping agent host");
        }
    }
}
