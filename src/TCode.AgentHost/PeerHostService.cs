using Microsoft.Extensions.Hosting;
using TCode.AgentHost.Peers;

namespace TCode.AgentHost;

/// <summary>启动跨进程会话消息的 socket；Host 退出时删除 socket 与注册文件。</summary>
public sealed class PeerHostService(PeerDirectory peers, SessionManager sessions) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        peers.Start(sessions.Coordinator);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await peers.DisposeAsync();
}
