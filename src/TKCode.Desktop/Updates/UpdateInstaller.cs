using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace TKCode.Desktop.Updates;

/// <summary>当前安装方式（auto-update.md §7.4）：决定能否在应用内安装，以及用哪种安装程序。</summary>
public abstract record InstallTarget
{
    /// <summary>macOS：从可写位置的 .app 包内运行，下载 zip，由 TKCode 退出前自己替换 .app。</summary>
    public sealed record MacApp(string BundlePath) : InstallTarget;

    /// <summary>Windows：由 Inno Setup 安装（目录里有 unins000.exe），下载 setup.exe 静默安装。</summary>
    public sealed record WindowsInstaller(string InstallDirectory) : InstallTarget;

    /// <summary>Linux：可写的 tar.gz 解压目录，由 TKCode 退出前自己替换。</summary>
    public sealed record LinuxDirectory(string InstallDirectory) : InstallTarget;

    /// <summary>不支持应用内安装：只提醒，按钮为「打开下载页面」。</summary>
    public sealed record ManualOnly(ManualReason Reason) : InstallTarget;

    public bool CanInstall => this is not ManualOnly;
}

public enum ManualReason
{
    /// <summary>macOS 从 DMG 卷或 App Translocation 路径运行：需要先拖到「应用程序」。</summary>
    MacNotInstalled,
    /// <summary>Windows 便携方式运行（不是安装程序装的）。</summary>
    Portable,
    /// <summary>安装目录不可写（/opt、/usr、系统包管理器、标准用户的「应用程序」等）。</summary>
    ReadOnly,
    /// <summary>不在已知的安装布局中（例如开发时从 bin 目录运行）。</summary>
    Unknown,
}

/// <summary>检测安装方式、校验下载的安装程序、启动安装程序（auto-update.md §6、§7）。</summary>
public static class UpdateInstaller
{
    public static InstallTarget Detect(string baseDirectory)
    {
        var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        if (OperatingSystem.IsMacOS())
        {
            // …/TKCode.app/Contents/MacOS
            var contents = Path.GetDirectoryName(dir);
            var bundle = contents is null ? null : Path.GetDirectoryName(contents);
            if (bundle is null || !bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(dir) != "MacOS")
            {
                return new InstallTarget.ManualOnly(ManualReason.Unknown);
            }
            if (bundle.Contains("/AppTranslocation/", StringComparison.Ordinal) || bundle.StartsWith("/Volumes/", StringComparison.Ordinal))
            {
                return new InstallTarget.ManualOnly(ManualReason.MacNotInstalled);
            }
            // 替换 .app 要在它所在的目录里改名
            return Path.GetDirectoryName(bundle) is { } folder && IsWritable(folder)
                ? new InstallTarget.MacApp(bundle)
                : new InstallTarget.ManualOnly(ManualReason.ReadOnly);
        }
        if (OperatingSystem.IsWindows())
        {
            return File.Exists(Path.Combine(dir, "unins000.exe"))
                ? new InstallTarget.WindowsInstaller(dir)
                : new InstallTarget.ManualOnly(ManualReason.Portable);
        }
        if (!File.Exists(Path.Combine(dir, "agenthost", "TKCode.AgentHost")) || !File.Exists(Path.Combine(dir, "ptyhost", "tkcode-ptyhost")))
        {
            return new InstallTarget.ManualOnly(ManualReason.Unknown);
        }
        return IsWritable(dir) && Path.GetDirectoryName(dir) is { } parent && IsWritable(parent)
            ? new InstallTarget.LinuxDirectory(dir)
            : new InstallTarget.ManualOnly(ManualReason.ReadOnly);
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".tkcode-write-test-{Environment.ProcessId}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ---- 下载后的平台校验（§6） ----

    /// <summary>
    /// 下载完成后的平台校验，通过才进入「已就绪」。
    /// 两个平台都解压到安装位置旁的 .new 并自检；macOS 在当前应用已签名时，新的 .app 还必须由同一个 Team 签名并经过公证。
    /// 失败抛出 <see cref="UpdateFeedException"/>。
    /// </summary>
    public static async Task PrepareAsync(InstallTarget target, string installerPath, CancellationToken cancellationToken)
    {
        switch (target)
        {
            case InstallTarget.MacApp mac:
                await ExtractMacAsync(mac.BundlePath, installerPath, cancellationToken).ConfigureAwait(false);
                break;
            case InstallTarget.LinuxDirectory linux:
                await ExtractLinuxAsync(linux.InstallDirectory, installerPath, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>新的 .app 必须通过 codesign 严格校验、Team 与当前应用相同，并且 Gatekeeper 认可为已公证。</summary>
    private static async Task VerifyMacBundleAsync(string bundle, string staged, CancellationToken cancellationToken)
    {
        var (_, current) = await RunAsync("/usr/bin/codesign", ["-dv", bundle], cancellationToken).ConfigureAwait(false);
        var team = TeamIdentifier(current);
        if (team is null)
        {
            // 未签名的本地构建：没有可比较的 Team，只依靠清单签名与 SHA-256
            return;
        }
        var (verifyExit, verify) = await RunAsync("/usr/bin/codesign", ["--verify", "--strict", "--deep", staged], cancellationToken, TimeSpan.FromSeconds(120))
            .ConfigureAwait(false);
        if (verifyExit != 0)
        {
            throw new UpdateFeedException($"The update's code signature is invalid: {verify.Trim()}");
        }
        var (_, signer) = await RunAsync("/usr/bin/codesign", ["-dv", staged], cancellationToken).ConfigureAwait(false);
        if (TeamIdentifier(signer) != team)
        {
            throw new UpdateFeedException($"The update is not signed by team {team}.");
        }
        var (exit, output) = await RunAsync("/usr/sbin/spctl", ["-a", "-t", "exec", "-vv", staged], cancellationToken, TimeSpan.FromSeconds(120))
            .ConfigureAwait(false);
        // 用户关闭了 Gatekeeper（spctl --master-disable）时只能看到 override，不再要求公证来源
        if (exit != 0 || !(output.Contains("Notarized Developer ID", StringComparison.Ordinal) || output.Contains("security disabled", StringComparison.Ordinal)))
        {
            throw new UpdateFeedException("The update is not notarized.");
        }
    }

    /// <summary>codesign -dv 输出中的 TeamIdentifier；未签名或 ad-hoc 签名时为 null。</summary>
    internal static string? TeamIdentifier(string codesignOutput) =>
        Regex.Match(codesignOutput, @"^TeamIdentifier=([A-Z0-9]{10})\s*$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>就地替换的安装位置：Linux 的安装目录或 macOS 的 .app；其他方式为 null。</summary>
    public static string? InstallPath(InstallTarget target) => target switch
    {
        InstallTarget.LinuxDirectory linux => linux.InstallDirectory,
        InstallTarget.MacApp mac => mac.BundlePath,
        _ => null,
    };

    /// <summary>新版本的暂存位置：安装位置旁边（同一文件系统，替换时可以直接 rename）。</summary>
    public static string StagedDirectory(string installPath) => installPath + ".new";

    /// <summary>解压 tar.gz（顶层是一个目录）到 &lt;安装目录&gt;.new，检查三个可执行文件并运行 pty host 自检。</summary>
    internal static async Task ExtractLinuxAsync(string installDirectory, string archive, CancellationToken cancellationToken, bool selfTest = true)
    {
        var staged = await ExtractAsync(installDirectory, null, async scratch =>
        {
            await using var file = File.OpenRead(archive);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            // TarFile 在目标路径经过符号链接目录时（例如 macOS 的 /var → /private/var）会抛 ArgumentOutOfRangeException，先解析成真实路径
            await TarFile.ExtractToDirectoryAsync(gzip, RealPath(scratch), overwriteFiles: true, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
        await CheckStagedAsync(staged, staged, selfTest, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 解压 zip（顶层是 TKCode.app）到 &lt;.app&gt;.new：用 ditto 保留签名需要的符号链接与扩展属性，
    /// 校验签名与公证后检查三个可执行文件并运行 pty host 自检。
    /// </summary>
    internal static async Task ExtractMacAsync(string bundle, string archive, CancellationToken cancellationToken, bool selfTest = true)
    {
        var staged = await ExtractAsync(bundle, ".app", async scratch =>
        {
            var (exit, output) = await RunAsync("/usr/bin/ditto", ["-x", "-k", archive, scratch], cancellationToken, TimeSpan.FromSeconds(300)).ConfigureAwait(false);
            if (exit != 0)
            {
                throw new UpdateFeedException($"Could not extract the update: {output.Trim()}");
            }
        }).ConfigureAwait(false);
        if (!File.Exists(Path.Combine(staged, "Contents", "Info.plist")))
        {
            DeleteDirectory(staged);
            throw new UpdateFeedException("The archive must contain an application bundle.");
        }
        try
        {
            await VerifyMacBundleAsync(bundle, staged, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            DeleteDirectory(staged);
            throw;
        }
        await CheckStagedAsync(staged, Path.Combine(staged, "Contents", "MacOS"), selfTest, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>解压到 &lt;安装位置&gt;.extract，要求顶层只有一个目录（可以要求扩展名，例如 .app），再把它移到 &lt;安装位置&gt;.new。</summary>
    private static async Task<string> ExtractAsync(string installPath, string? topLevelExtension, Func<string, Task> extract)
    {
        var staged = StagedDirectory(installPath);
        var scratch = installPath + ".extract";
        DeleteDirectory(staged);
        DeleteDirectory(scratch);
        Directory.CreateDirectory(scratch);
        try
        {
            await extract(scratch).ConfigureAwait(false);
            var top = Directory.GetDirectories(scratch);
            if (top.Length != 1 || Directory.GetFiles(scratch).Length != 0 ||
                (topLevelExtension is not null && Path.GetExtension(top[0]) != topLevelExtension))
            {
                throw new UpdateFeedException("The archive must contain a single top-level directory.");
            }
            Directory.Move(top[0], staged);
            return staged;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            DeleteDirectory(staged);
            throw new UpdateFeedException($"Could not extract the update: {ex.Message}", ex);
        }
        finally
        {
            DeleteDirectory(scratch);
        }
    }

    /// <summary>检查三个可执行文件都在，并运行 pty host 自检；失败时删除暂存目录。</summary>
    private static async Task CheckStagedAsync(string staged, string programDirectory, bool selfTest, CancellationToken cancellationToken)
    {
        foreach (var exe in new[] { "TKCode", "agenthost/TKCode.AgentHost", "ptyhost/tkcode-ptyhost" })
        {
            if (!File.Exists(Path.Combine(programDirectory, exe)))
            {
                DeleteDirectory(staged);
                throw new UpdateFeedException($"The update is missing {exe}.");
            }
        }
        if (selfTest)
        {
            var (exit, output) = await RunAsync(Path.Combine(programDirectory, "ptyhost", "tkcode-ptyhost"), ["--self-test"], cancellationToken, TimeSpan.FromSeconds(60))
                .ConfigureAwait(false);
            if (exit != 0)
            {
                DeleteDirectory(staged);
                throw new UpdateFeedException($"The new terminal service failed its self-test ({exit}): {output.Trim()}");
            }
        }
    }

    /// <summary>逐级解析路径中的符号链接目录，返回真实路径。</summary>
    internal static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, part);
            var info = new DirectoryInfo(next);
            current = info.Exists && info.ResolveLinkTarget(returnFinalTarget: true) is { } target ? target.FullName : next;
        }
        return current;
    }

    // ---- 安装（§7） ----

    /// <summary>
    /// 启动安装程序（Windows）或替换安装位置并启动新版本（macOS / Linux）。返回后调用方应立即退出程序。
    /// </summary>
    public static void Launch(InstallTarget target, string installerPath)
    {
        switch (target)
        {
            case InstallTarget.WindowsInstaller:
                Process.Start(WindowsStartInfo(installerPath))?.Dispose();
                break;
            case InstallTarget.MacApp mac:
                Swap(mac.BundlePath, start: bundle => Process.Start(MacStartInfo(bundle))?.Dispose());
                break;
            case InstallTarget.LinuxDirectory linux:
                Swap(linux.InstallDirectory, start: dir => Process.Start(new ProcessStartInfo(Path.Combine(dir, "TKCode")) { UseShellExecute = false })?.Dispose());
                break;
            default:
                throw new InvalidOperationException("This installation cannot be updated in place.");
        }
    }

    /// <summary>Inno Setup 静默安装；/UPDATE=1 让脚本在安装完成后重新启动 TKCode（Packaging/windows/TKCode.iss）。</summary>
    internal static ProcessStartInfo WindowsStartInfo(string setup)
    {
        var info = new ProcessStartInfo(setup) { UseShellExecute = true };
        foreach (var arg in new[] { "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/UPDATE=1" })
        {
            info.ArgumentList.Add(arg);
        }
        return info;
    }

    /// <summary>通过 LaunchServices 打开替换后的 .app；-n：旧进程还没退出，不加会只激活旧进程。</summary>
    internal static ProcessStartInfo MacStartInfo(string bundle)
    {
        var info = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
        info.ArgumentList.Add("-n");
        info.ArgumentList.Add(bundle);
        return info;
    }

    /// <summary>
    /// 安装位置 → .old，.new → 安装位置，然后启动新版本；任何一步失败都恢复原状并抛出异常。
    /// Unix 上运行中的文件可以改名（旧 inode 仍然有效），所以当前进程可以自己完成替换。
    /// </summary>
    internal static void Swap(string installDirectory, Action<string> start)
    {
        var staged = StagedDirectory(installDirectory);
        var old = installDirectory + ".old";
        if (!Directory.Exists(staged))
        {
            throw new UpdateFeedException("The update has not been prepared.");
        }
        DeleteDirectory(old);
        Directory.Move(installDirectory, old);
        try
        {
            Directory.Move(staged, installDirectory);
        }
        catch
        {
            Directory.Move(old, installDirectory);
            throw;
        }
        try
        {
            start(installDirectory);
        }
        catch
        {
            Directory.Move(installDirectory, staged);
            Directory.Move(old, installDirectory);
            throw;
        }
    }

    /// <summary>新版本启动后清理上次替换留下的目录（.old / .extract）。</summary>
    public static void CleanupLeftovers(InstallTarget target)
    {
        if (InstallPath(target) is { } path)
        {
            DeleteDirectory(path + ".old");
            DeleteDirectory(path + ".extract");
        }
    }

    internal static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static async Task<(int Exit, string Output)> RunAsync(string file, string[] args, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        using var process = Process.Start(info) ?? throw new UpdateFeedException($"Could not start {file}.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = process.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new UpdateFeedException($"{Path.GetFileName(file)} timed out.");
        }
        return (process.ExitCode, await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false));
    }
}
