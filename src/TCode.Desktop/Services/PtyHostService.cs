using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TCode.Config;
using TCode.Terminal.Core;
using TCode.Terminal.Protocol;

namespace TCode.Desktop.Services;

/// <summary>
/// 桌面端对 pty host 的连接（04 §1.3–§1.4、§13.1）：按需发现或启动当前构建的 host，连接后下发设置，断开后下次使用时重连。
/// host 不随 Desktop 退出；Desktop 崩溃后的会话由新的 Desktop 重新 attach（§10.5）。
/// </summary>
public sealed class PtyHostService(ILogger<PtyHostService> logger) : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private PtyHostClient? _client;

    /// <summary>当前构建的 ID（与 host 的 --build 比较，见 04 §1.2）。</summary>
    public static string Build => PtyHostPaths.ComputeBuild(AppVersion, GhosttyBuildId);

    /// <summary>用户态数据目录（TCODE_HOME），revive 等数据放在它下面。</summary>
    public static string Home => ConfigPaths.Home;

    /// <summary>host 所在目录。TCODE_PTYHOST_DIR 用于测试（直接使用 host 项目的构建输出）。</summary>
    public static string HostDirectory =>
        Environment.GetEnvironmentVariable("TCODE_PTYHOST_DIR") is { Length: > 0 } custom ? custom : Path.Combine(AppContext.BaseDirectory, "ptyhost");

    private static string AppVersion => typeof(PtyHostService).Assembly.GetName().Version?.ToString(3) ?? "0";

    private static string GhosttyBuildId
    {
        get
        {
            var lib = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "ghostty-vt.dll" : OperatingSystem.IsMacOS() ? "libghostty-vt.dylib" : "libghostty-vt.so");
            return File.Exists(lib) ? GhosttyRuntime.ComputeBuildId(lib) : GhosttyRuntime.Version;
        }
    }

    /// <summary>取得一个已认证的连接；已有连接仍然有效时直接返回。</summary>
    public async Task<PtyHostClient> GetAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is { } current && !current.Completion.IsCompleted)
            {
                return current;
            }
            var sw = Stopwatch.StartNew();
            var client = await PtyHostLocator.ConnectOrStartAsync(new PtyHostLaunchOptions
            {
                Home = Home,
                HostDirectory = HostDirectory,
                Build = Build,
                Client = new PtyHostClientOptions { Kind = ClientKind.Ui, ClientVersion = AppVersion, GhosttyBuildId = GhosttyBuildId },
            }, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("pty host {Scope} connection established in {Ms} ms (host {Version})", client.Scope, sw.ElapsedMilliseconds, client.Host.HostVersion);
            if (client.Scope == "full")
            {
                await client.ConfigureAsync(Settings(), cancellationToken).ConfigureAwait(false);
            }
            _client = client;
            return client;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>host/configure 的设置（TODO：从 settings.json 的 terminal 节读取，M4）。</summary>
    private static HostSettingsDto Settings()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            env[(string)e.Key] = (string?)e.Value ?? string.Empty;  // 按键赋值：Windows 的环境变量名可能只在大小写上不同
        }
        return new HostSettingsDto { BaseEnvironment = env };
    }

    public void Dispose()
    {
        _client?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _lock.Dispose();
    }
}
