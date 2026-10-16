using TCode.Terminal.Pty.Unix;
using TCode.Terminal.Pty.Windows;

namespace TCode.Terminal.Pty;

public sealed record LocalPtyOptions
{
    /// <summary>tcode-pty-spawn 的路径（Unix）；默认在应用目录下。</summary>
    public string? HelperPath { get; init; }

    /// <summary>conpty.dll 与 OpenConsole.exe 所在目录（Windows）；默认是应用目录。</summary>
    public string? ConPtyDirectory { get; init; }
}

/// <summary>启动本地 PTY 会话：Unix 用 libtcodepty + spawn helper，Windows 用 ConPTY。</summary>
public static class LocalPty
{
    public static ITerminalConnection Start(PtyLaunch launch, LocalPtyOptions? options = null)
    {
        options ??= new LocalPtyOptions();
        if (OperatingSystem.IsWindows())
        {
            return WindowsPtyConnection.Start(launch, options.ConPtyDirectory ?? AppContext.BaseDirectory);
        }
        return UnixPtyConnection.Start(launch, options.HelperPath ?? Path.Combine(AppContext.BaseDirectory, "tcode-pty-spawn"));
    }
}
