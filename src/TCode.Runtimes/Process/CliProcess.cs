using System.Diagnostics;
using TCode.Runtimes.Discovery;

namespace TCode.Runtimes.Process;

/// <summary>
/// 外部 CLI 子进程：重定向 stdio，持续读取 stderr 并只保留最后若干行（用于错误提示），
/// 结束时先关闭 stdin 等待自行退出，超时后结束整个进程树。
/// </summary>
public sealed class CliProcess : IAsyncDisposable
{
    private const int StderrTailLines = 40;

    private readonly System.Diagnostics.Process _process;
    private readonly Queue<string> _stderrTail = new();
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    private CliProcess(System.Diagnostics.Process process)
    {
        _process = process;
    }

    public Stream StandardInput => _process.StandardInput.BaseStream;
    public Stream StandardOutput => _process.StandardOutput.BaseStream;
    public int ProcessId => _process.Id;

    /// <summary>进程退出时完成，结果为退出码。</summary>
    public Task<int> Exited => _exited.Task;

    public bool HasExited => _exited.Task.IsCompleted;

    /// <summary>stderr 的最后几行（已去掉空行）。</summary>
    public string StderrTail
    {
        get
        {
            lock (_stderrTail)
            {
                return string.Join('\n', _stderrTail);
            }
        }
    }

    /// <summary>
    /// 启动 CLI。PATH 由 <see cref="ExecutableLocator.ChildPath"/> 生成；<paramref name="environment"/> 中值为 null 的变量会被移除。
    /// </summary>
    public static CliProcess Start(string executable, IEnumerable<string> arguments, string? workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null, ExecutableLocator? locator = null)
    {
        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        info.Environment["PATH"] = (locator ?? ExecutableLocator.Default).ChildPath(executable);
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                if (value is null)
                {
                    info.Environment.Remove(key);
                }
                else
                {
                    info.Environment[key] = value;
                }
            }
        }

        var process = new System.Diagnostics.Process { StartInfo = info, EnableRaisingEvents = true };
        var cli = new CliProcess(process);
        process.ErrorDataReceived += (_, e) => cli.OnStderr(e.Data);
        process.Exited += (_, _) => cli._exited.TrySetResult(SafeExitCode(process));
        process.Start();
        process.BeginErrorReadLine();
        if (process.HasExited)
        {
            cli._exited.TrySetResult(SafeExitCode(process));
        }
        return cli;
    }

    private void OnStderr(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }
        lock (_stderrTail)
        {
            _stderrTail.Enqueue(line.Length > 2000 ? line[..2000] : line);
            while (_stderrTail.Count > StderrTailLines)
            {
                _stderrTail.Dequeue();
            }
        }
    }

    /// <summary>描述进程意外退出（含退出码与 stderr 末尾），用于错误消息。</summary>
    public string DescribeExit(string name)
    {
        var code = HasExited ? $" with code {Exited.Result}" : "";
        var tail = StderrTail;
        return tail.Length == 0 ? $"{name} exited{code}." : $"{name} exited{code}: {tail}";
    }

    /// <summary>关闭 stdin 并等待退出；超时后结束整个进程树。</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        try
        {
            _process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }
        if (await Task.WhenAny(Exited, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false) != Exited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            await Task.WhenAny(Exited, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        }
        _process.Dispose();
    }

    private static int SafeExitCode(System.Diagnostics.Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    /// <summary>运行一个短命令（探测版本、认证状态），返回 stdout；超时或失败返回 null。</summary>
    public static async Task<string?> RunToCompletionAsync(string executable, IEnumerable<string> arguments, TimeSpan timeout,
        CancellationToken cancellationToken, ExecutableLocator? locator = null)
    {
        await using var cli = Start(executable, arguments, null, null, locator);
        try
        {
            cli._process.StandardInput.Close();
        }
        catch (IOException)
        {
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            var output = await cli._process.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            var code = await cli.Exited.WaitAsync(cts.Token).ConfigureAwait(false);
            return code == 0 ? output : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
