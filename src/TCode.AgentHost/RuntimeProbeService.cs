using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TCode.Runtimes;

namespace TCode.AgentHost;

/// <summary>启动后在后台探测外部运行时；完成后由 <see cref="RuntimeRegistry.Changed"/> 触发 models/changed。</summary>
public sealed class RuntimeProbeService(RuntimeRegistry runtimes, ILogger<RuntimeProbeService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (runtimes.All.Count == 0)
        {
            return;
        }
        await runtimes.ProbeAllAsync(stoppingToken);
        foreach (var d in runtimes.All.Select(r => r.Descriptor))
        {
            logger.LogInformation("Runtime {Id}: {Status} {Version} {Path} models={Models} {Error}",
                d.Id, d.Status, d.Version, d.ExecutablePath, d.Models.Count, d.Error);
        }
    }
}
