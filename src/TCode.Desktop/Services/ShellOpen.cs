using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace TCode.Desktop.Services;

/// <summary>用系统默认程序打开文件或文件夹（访达 / 资源管理器 / xdg-open）。</summary>
public static class ShellOpen
{
    public static void Open(string path, ILogger logger)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("open") { ArgumentList = { path }, UseShellExecute = false });
            }
            else if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { path }, UseShellExecute = false });
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to open {Path}", path);
        }
    }
}
