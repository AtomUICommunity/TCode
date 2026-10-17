using System.Diagnostics;
using System.Runtime.InteropServices;
using TCode.Terminal.Protocol;

namespace TCode.Terminal.Host;

/// <summary>只计清醒时间的单调时钟（04 §1.7）：Unix 的 Stopwatch（CLOCK_MONOTONIC / CLOCK_UPTIME_RAW）睡眠时不前进；Windows 用 QueryUnbiasedInterruptTime。</summary>
internal static partial class AwakeClock
{
    public static TimeSpan Now
    {
        get
        {
            if (OperatingSystem.IsWindows() && QueryUnbiasedInterruptTime(out var t))
            {
                return TimeSpan.FromTicks((long)t);  // 100 ns 单位
            }
            return Stopwatch.GetElapsedTime(0);
        }
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryUnbiasedInterruptTime(out ulong time);
}

/// <summary>
/// host 的生命周期（04 §1.7）：
/// <list type="bullet">
/// <item>最后一个 UI 连接异常断开（没有 host/quit）后开始计宽限期（默认 30 min，只计清醒时间）；任何 UI 连接完成认证就取消。</item>
/// <item>到期：没有前台程序的会话按退出处理，有程序在运行的会话保留，直到程序结束或达到 keepAliveMaxHours（2026-10-17 决定）。</item>
/// <item>用户以 keepAlive 退出：不计宽限期，只受 keepAliveMaxHours 限制。</item>
/// <item>没有会话也没有连接：60 s 后退出。</item>
/// </list>
/// </summary>
internal sealed class HostLifetime
{
    private readonly PtyHostServer _server;
    private readonly Lock _lock = new();
    private TimeSpan? _orphanedAt;
    private TimeSpan? _emptyAt;
    private TimeSpan? _keepAliveAt;
    private bool _graceExpired;
    private TimeSpan _lastSweep;
    private int _sweeping;

    public HostLifetime(PtyHostServer server)
    {
        _server = server;
        server.ConnectionsChanged += Evaluate;
        server.Ticked += Evaluate;
        server.QuitRequested += mode =>
        {
            lock (_lock)
            {
                _keepAliveAt = mode == "keepAlive" ? AwakeClock.Now : null;
            }
        };
    }

    /// <summary>空 host 的退出等待（测试可缩短）。</summary>
    public TimeSpan IdleExit { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>宽限期；为 null 时取 host/configure 的 orphanGraceMinutes。</summary>
    public TimeSpan? GraceOverride { get; set; }

    private TimeSpan Grace => GraceOverride ?? TimeSpan.FromMinutes(Math.Max(0, _server.Context.Settings.Persistence.OrphanGraceMinutes));

    private TimeSpan? KeepAliveMax => _server.Context.Settings.Persistence.KeepAliveMaxHours is { } h ? TimeSpan.FromHours(h) : null;

    public void Evaluate()
    {
        lock (_lock)
        {
            var now = AwakeClock.Now;
            if (_server.Draining && _server.SessionCount == 0)
            {
                Stop("draining");  // 遗留 host 的最后一个会话关闭后立即退出（§1.5）
                return;
            }
            if (_server.UiConnections > 0)
            {
                _orphanedAt = null;
                _emptyAt = null;
                _keepAliveAt = null;
                _graceExpired = false;
                return;
            }
            if (_server.SessionCount == 0 && _server.ConnectionCount == 0 && !_graceExpired)
            {
                _emptyAt ??= now;
                if (now - _emptyAt >= IdleExit)
                {
                    Stop("idle");
                }
                return;
            }
            _emptyAt = null;
            if (_keepAliveAt is { } since)
            {
                if (KeepAliveMax is { } max && now - since >= max)
                {
                    Stop("keepAliveMaxHours");
                }
                return;
            }
            _orphanedAt ??= now;
            if (_graceExpired)
            {
                // 宽限期已过：保留的会话在程序结束后关闭（每 2 s 检查一次）；全部关闭后退出。keepAliveMaxHours 仍然是上限
                if (_server.SessionCount == 0 || (KeepAliveMax is { } max && now - _orphanedAt - Grace >= max))
                {
                    Stop("orphanTimeout");
                }
                else if (now - _lastSweep >= TimeSpan.FromSeconds(2))
                {
                    Sweep(now);
                }
                return;
            }
            if (now - _orphanedAt >= Grace)
            {
                _graceExpired = true;
                Sweep(now);
            }
        }
    }

    private void Sweep(TimeSpan now)
    {
        _lastSweep = now;
        if (Interlocked.Exchange(ref _sweeping, 1) == 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _server.CloseIdleSessionsAsync("orphanTimeout").ConfigureAwait(false);
                }
                finally
                {
                    Volatile.Write(ref _sweeping, 0);
                }
            });
        }
    }

    private void Stop(string reason) => _ = Task.Run(() => _server.ShutdownAsync(reason));
}
