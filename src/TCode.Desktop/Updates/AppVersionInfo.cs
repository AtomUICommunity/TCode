using System.Reflection;
using System.Runtime.InteropServices;

namespace TCode.Desktop.Updates;

/// <summary>
/// 当前程序的版本与平台（auto-update.md §3）。版本唯一来源是发布 CI 的 -p:Version（来自 git tag）；
/// 本地构建为 0.0.0-dev，不做自动检查。InformationalVersion 的 +commit 后缀由 SDK 的 SourceLink 附加。
/// </summary>
public static class AppVersionInfo
{
    private static readonly string Informational =
        typeof(AppVersionInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-dev";

    public static SemVersion Current { get; } = SemVersion.TryParse(Informational, out var v) ? v : SemVersion.Parse("0.0.0-dev");

    /// <summary>构建所用的 commit（前 7 位），没有时为 null。</summary>
    public static string? Commit { get; } = Informational.IndexOf('+') is var i and >= 0 && Informational.Length > i + 1
        ? Informational[(i + 1)..Math.Min(Informational.Length, i + 8)]
        : null;

    /// <summary>本地开发构建（0.0.0-dev 等）：不做自动检查，手动检查给出提示。</summary>
    public static bool IsDevBuild => Current.Prerelease.StartsWith("dev", StringComparison.Ordinal);

    /// <summary>界面显示用：1.2.0 (abc1234)。</summary>
    public static string Display => Commit is null ? Current.ToString() : $"{Current} ({Commit})";

    /// <summary>与发布产物、清单 assets 的键一致的运行时标识。</summary>
    public static string Rid { get; } = ComputeRid();

    private static string ComputeRid()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64",
        };
        return $"{os}-{arch}";
    }

    /// <summary>当前系统版本（与清单的 minOs 比较；只比较主.次）。</summary>
    public static Version OsVersion => Environment.OSVersion.Version;
}
