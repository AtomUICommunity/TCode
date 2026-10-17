using System.Diagnostics;
using System.Text;

namespace TKCode.Agent.Coding.Processes;

/// <summary>运行一个外部程序并收集输出（rg、git 等辅助命令用）。取消时终止整个进程树。</summary>
public static class ProcessRunner
{
    public sealed record Result(int ExitCode, string Stdout, string Stderr, bool Truncated);

    public static async Task<Result> RunAsync(string fileName, IEnumerable<string> arguments, string workingDirectory,
        CancellationToken cancellationToken, int maxOutputChars = 4 * 1024 * 1024)
    {
        var info = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        using var process = new Process { StartInfo = info };
        process.Start();
        process.StandardInput.Close();

        var stdout = new StringBuilder();
        var truncated = false;
        using var killOnCancel = cancellationToken.Register(() => Kill(process));
        var stderrTask = ReadLimitedAsync(process.StandardError, 64 * 1024);
        var buffer = new char[16 * 1024];
        int read;
        while ((read = await process.StandardOutput.ReadAsync(buffer, CancellationToken.None)) > 0)
        {
            if (stdout.Length + read > maxOutputChars)
            {
                stdout.Append(buffer, 0, Math.Max(0, maxOutputChars - stdout.Length));
                truncated = true;
                Kill(process);
                break;
            }
            stdout.Append(buffer, 0, read);
        }
        var stderr = await stderrTask;
        await process.WaitForExitAsync(CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();
        return new Result(truncated ? 0 : process.ExitCode, stdout.ToString(), stderr, truncated);
    }

    public static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader, int max)
    {
        var sb = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer, CancellationToken.None)) > 0)
        {
            if (sb.Length < max)
            {
                sb.Append(buffer, 0, Math.Min(read, max - sb.Length));
            }
        }
        return sb.ToString();
    }

    /// <summary>在 PATH 中查找可执行文件（Windows 上补 .exe / .cmd）。</summary>
    public static string? FindExecutable(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        string[] extensions = OperatingSystem.IsWindows() ? [".exe", ".cmd", ".bat", ""] : [""];
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir.Trim('"'), name + ext);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        // macOS 上从 Finder 启动的应用 PATH 很短，补充常见位置。
        foreach (var dir in (string[])["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin"])
        {
            var candidate = Path.Combine(dir, name);
            if (!OperatingSystem.IsWindows() && File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }
}
