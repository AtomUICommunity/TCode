using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace TKCode.Terminal.Protocol;

public sealed record PtyHostLaunchOptions
{
    public required string Home { get; init; }

    /// <summary>host 所在目录（含 tkcode-ptyhost、tkcode-pty-spawn、原生库）。</summary>
    public required string HostDirectory { get; init; }

    public required string Build { get; init; }
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public PtyHostClientOptions Client { get; init; } = new();

    /// <summary>传给 host 的日志级别。</summary>
    public string? LogLevel { get; init; }
}

/// <summary>host 进程启动失败（退出码见 04 §1.3）。</summary>
public sealed class PtyHostStartException(string message, int? exitCode = null) : Exception(message)
{
    public int? ExitCode { get; } = exitCode;
}

/// <summary>发现或启动 host（只有 Desktop 使用，04 §1.3–§1.4、§13.1）。</summary>
public static class PtyHostLocator
{
    public const int ExitAlreadyRunning = 2;
    public const int ExitBuildMismatch = 3;
    public const int ExitSetupFailed = 4;

    /// <summary>
    /// 连接当前构建的 host：拿得到生命周期锁说明 host 已死（清理陈旧文件，返回 null）；拿不到就读 json 连接并完成握手。
    /// </summary>
    public static async Task<PtyHostClient?> TryConnectAsync(string home, string build, PtyHostClientOptions? options = null, CancellationToken ct = default)
    {
        var registration = PtyHostPaths.Registration(home, build);
        using (var probe = PtyHostPaths.TryLock(PtyHostPaths.LifetimeLock(home, build)))
        {
            if (probe is not null)
            {
                // 锁拿得到：没有活着的 host（进程退出时内核自动释放锁，所以 pid 复用不会误判）
                if (ReadRegistration(registration) is { } stale && !OperatingSystem.IsWindows() && !stale.Endpoint.StartsWith(PtyTransport.PipePrefix, StringComparison.Ordinal))
                {
                    TryDelete(stale.Endpoint);
                }
                TryDelete(registration);
                return null;
            }
        }
        var reg = ReadRegistration(registration) ?? throw new PtyHostStartException("the pty host holds its lock but has no registration (still starting?)");
        return await PtyHostClient.ConnectAsync(new PtyHostEndpoint(reg.Endpoint, Handshake.FromBase64Url(reg.Token), reg.InstanceId), options, ct).ConfigureAwait(false);
    }

    /// <summary>先找当前构建的 host；找不到就启动（§1.3），等待 READY，然后连接。多个启动者由 start.lock 串行化。</summary>
    public static async Task<PtyHostClient> ConnectOrStartAsync(PtyHostLaunchOptions o, CancellationToken ct = default)
    {
        Directory.CreateDirectory(o.Home);
        if (!PtyHostPaths.TryPrivateDirectory(PtyHostPaths.RunDirectory(o.Home)))
        {
            throw new PtyHostStartException($"{PtyHostPaths.RunDirectory(o.Home)} is not a private directory");
        }
        if (await TryConnectQuietlyAsync(o, ct).ConfigureAwait(false) is { } existing)
        {
            return existing;
        }
        using var startLock = await AcquireStartLockAsync(o, ct).ConfigureAwait(false);
        if (await TryConnectQuietlyAsync(o, ct).ConfigureAwait(false) is { } started)
        {
            return started;  // 别的窗口刚启动了它
        }
        var exitCode = await LaunchAsync(o, ct).ConfigureAwait(false);
        if (exitCode is { } code and not ExitAlreadyRunning)
        {
            throw new PtyHostStartException(code switch
            {
                ExitBuildMismatch => "the pty host belongs to a different installation (build mismatch)",
                ExitSetupFailed => "the pty host could not create its runtime directory or socket",
                _ => $"the pty host exited with code {code}",
            }, code);
        }
        return await TryConnectAsync(o.Home, o.Build, o.Client, ct).ConfigureAwait(false)
               ?? throw new PtyHostStartException("the pty host reported READY but is not running");
    }

    /// <summary>其他构建（遗留）的 host（§1.5）。</summary>
    public static IReadOnlyList<PtyHostRegistration> FindLegacyHosts(string home, string currentBuild)
    {
        var dir = PtyHostPaths.RunDirectory(home);
        if (!Directory.Exists(dir))
        {
            return [];
        }
        var result = new List<PtyHostRegistration>();
        foreach (var file in Directory.EnumerateFiles(dir, "ptyhost-*.json"))
        {
            if (ReadRegistration(file) is { } reg && reg.Build != currentBuild)
            {
                using var probe = PtyHostPaths.TryLock(PtyHostPaths.LifetimeLock(home, reg.Build));
                if (probe is null)
                {
                    result.Add(reg);  // 锁被持有：还活着
                }
            }
        }
        return result;
    }

    private static async Task<PtyHostClient?> TryConnectQuietlyAsync(PtyHostLaunchOptions o, CancellationToken ct)
    {
        try
        {
            return await TryConnectAsync(o.Home, o.Build, o.Client, ct).ConfigureAwait(false);
        }
        catch (PtyHostStartException)
        {
            return null;  // 正在启动：等 start.lock 后再试
        }
    }

    private static async Task<FileStream> AcquireStartLockAsync(PtyHostLaunchOptions o, CancellationToken ct)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(o.ReadyTimeout.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            if (PtyHostPaths.TryLock(PtyHostPaths.StartLock(o.Home, o.Build)) is { } stream)
            {
                return stream;
            }
            if (Stopwatch.GetTimestamp() > deadline)
            {
                throw new PtyHostStartException("timed out waiting for another process to start the pty host");
            }
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
    }

    /// <summary>启动 host 并等 READY 行；host 以退出码结束（例如 2 = 已有 host）时返回退出码。</summary>
    private static async Task<int?> LaunchAsync(PtyHostLaunchOptions o, CancellationToken ct)
    {
        var exe = Path.Combine(o.HostDirectory, OperatingSystem.IsWindows() ? "tkcode-ptyhost.exe" : "tkcode-ptyhost");
        if (!File.Exists(exe))
        {
            throw new PtyHostStartException($"pty host not found: {exe}");
        }
        List<string> hostArgs = ["--home", Path.GetFullPath(o.Home), "--build", o.Build, "--ready-fd", "1", "--parent-pid", Environment.ProcessId.ToString()];
        if (o.LogLevel is { } level)
        {
            hostArgs.AddRange(["--log-level", level]);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(o.ReadyTimeout);
        if (OperatingSystem.IsWindows())
        {
            return await WindowsHostLauncher.LaunchAsync(exe, hostArgs, timeout.Token).ConfigureAwait(false);
        }
        // Unix：经 helper --detach（setsid、复位信号、关闭 fd≥3 后 exec），stdout 管道读 READY
        var helper = Path.Combine(o.HostDirectory, "tkcode-pty-spawn");
        var psi = new ProcessStartInfo(helper)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in (string[])["--detach", "--stdout-fd", "1", "--", exe, .. hostArgs])
        {
            psi.ArgumentList.Add(a);
        }
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("TKCODE_PTYHOST_", StringComparison.Ordinal)).ToList())
        {
            psi.Environment.Remove(key);
        }
        using var process = Process.Start(psi) ?? throw new PtyHostStartException($"failed to start {helper}");
        process.StandardInput.Close();
        _ = process.StandardError.ReadToEndAsync(CancellationToken.None);  // 只排空，避免 host 写满 stderr 管道
        return await ReadReadyAsync(process.StandardOutput, () => process.HasExited ? process.ExitCode : null, timeout.Token).ConfigureAwait(false);
    }

    internal static async Task<int?> ReadReadyAsync(TextReader stdout, Func<int?> exitCode, CancellationToken ct)
    {
        try
        {
            var line = await stdout.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is not null && line.StartsWith("READY ", StringComparison.Ordinal))
            {
                return null;
            }
            for (var i = 0; i < 50 && exitCode() is null; i++)
            {
                await Task.Delay(20, ct).ConfigureAwait(false);
            }
            return exitCode() ?? throw new PtyHostStartException($"unexpected output from the pty host: {line}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested || true)
        {
            throw new PtyHostStartException("timed out waiting for the pty host to become ready");
        }
    }

    public static PtyHostRegistration? ReadRegistration(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllBytes(path), PtyHostJsonContext.Default.PtyHostRegistration) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
