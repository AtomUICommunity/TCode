using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TCode.Rpc;

namespace TCode.AgentHost;

/// <summary>在 stdio 上建立 JSON-RPC 连接；连接断开（UI 退出）时结束进程。</summary>
public sealed class RpcHostService(
    AgentHostServer server,
    SessionManager sessions,
    IHostApplicationLifetime lifetime,
    ILogger<RpcHostService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var rpc = RpcSerialization.CreateConnection(Console.OpenStandardOutput(), Console.OpenStandardInput());
        AgentHostServerDispatcher.Register(rpc, server);
        sessions.AttachClient(new AgentHostClientProxy(rpc));
        rpc.Start();
        logger.LogInformation("TCode.AgentHost listening on stdio (pid {Pid})", Environment.ProcessId);

        try
        {
            await rpc.Completion.WaitAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "RPC connection closed with error");
        }
        await sessions.DisposeAsync();
        lifetime.StopApplication();
    }
}
