using System.Diagnostics;

namespace TCode.Desktop.Services;

/// <summary>本机信息。macOS 使用“共享”设置中的电脑名称（例如 “token的MacBook Air”），其它平台使用主机名。</summary>
public static class DeviceInfo
{
    private static readonly Lazy<string> LazyName = new(ReadName);

    /// <summary>环境变量 TCODE_DEVICE_NAME 优先（README 截图等场景不暴露真实电脑名）。</summary>
    public static string Name => Environment.GetEnvironmentVariable("TCODE_DEVICE_NAME") is { Length: > 0 } name ? name : LazyName.Value;

    private static string ReadName()
    {
        if (OperatingSystem.IsMacOS())
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo("/usr/sbin/scutil", "--get ComputerName")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (process is not null)
                {
                    var name = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit(1000);
                    if (name.Length > 0)
                    {
                        return name;
                    }
                }
            }
            catch (Exception)
            {
                // 读取失败时退回主机名。
            }
        }
        return Environment.MachineName;
    }
}
