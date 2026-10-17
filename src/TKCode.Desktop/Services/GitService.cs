using System.Diagnostics;

namespace TKCode.Desktop.Services;

/// <summary>调用本机 git（不经过 shell，参数逐个传入）。</summary>
public static class GitService
{
    /// <summary>
    /// git switch &lt;branch&gt;。默认行为是安全的：未提交的修改会带到新分支，若会被覆盖则 git 拒绝并返回错误。
    /// 成功返回 null，失败返回 git 的错误输出。
    /// </summary>
    public static async Task<string?> SwitchAsync(string repository, string branch, CancellationToken ct = default)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("switch");
        start.ArgumentList.Add(branch);
        // 不弹出凭据或编辑器提示
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("git not started");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0 ? null : (await stderr).Trim();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            return ex.Message;
        }
    }
}
