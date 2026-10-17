using TKCode.Terminal.Pty.Unix;
using TKCode.Terminal.Pty.Windows;

namespace TKCode.Terminal.Pty;

public sealed record LocalPtyOptions
{
    /// <summary>tkcode-pty-spawn 的路径（Unix）；默认在应用目录下。</summary>
    public string? HelperPath { get; init; }

    /// <summary>conpty.dll 与 OpenConsole.exe 所在目录（Windows）；默认是应用目录。</summary>
    public string? ConPtyDirectory { get; init; }
}

/// <summary>启动本地 PTY 会话：Unix 用 libtkcodepty + spawn helper，Windows 用 ConPTY。</summary>
public static class LocalPty
{
    public static ITerminalConnection Start(PtyLaunch launch, LocalPtyOptions? options = null)
    {
        options ??= new LocalPtyOptions();
        if (OperatingSystem.IsWindows())
        {
            return WindowsPtyConnection.Start(launch, options.ConPtyDirectory ?? AppContext.BaseDirectory);
        }
        return UnixPtyConnection.Start(launch, options.HelperPath ?? Path.Combine(AppContext.BaseDirectory, "tkcode-pty-spawn"));
    }

    /// <summary>Windows：首个会话启动后实际使用的 ConPTY 实现（诊断与自检）；其他平台或尚未加载时为 null。</summary>
    public static ConPtyImplementation? ConPtyImplementation => OperatingSystem.IsWindows() && ConPtyApi.IsLoaded ? ConPtyApi.Implementation : null;

    /// <summary>Windows：没用上随包 ConPTY 的原因（为空表示用的是随包实现）。</summary>
    public static string? ConPtyDegradedReason => OperatingSystem.IsWindows() ? ConPtyApi.DegradedReason : null;
}
