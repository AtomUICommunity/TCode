using System.IO.Pipelines;
using TCode.Terminal.Core;

namespace TCode.Terminal.Pty;

/// <summary>
/// 终端的"连接"：本地 PTY、以后的 SSH 通道、测试回放都实现它（01-architecture.md §6.2，research/06 §6）。
/// 字节流不解码，编码交给 VT 解析器。
/// </summary>
public interface ITerminalConnection : IAsyncDisposable
{
    /// <summary>"local-pty"、"replay"……</summary>
    string Kind { get; }

    ConnectionCapabilities Capabilities { get; }

    /// <summary>程序输出。消费方 AdvanceTo 之前，读线程最多再读一个缓冲窗口（约 256 KiB）的数据，再多就停止读取（背压）。</summary>
    PipeReader Output { get; }

    /// <summary>用户输入（按键、粘贴、鼠标报告都已编码为字节）。复制后入队，立即完成。</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default);

    /// <summary>镜像核心的查询应答：不阻塞，与用户输入进入同一个先进先出队列（03 §5.3）。</summary>
    bool TryWriteReply(ReadOnlySpan<byte> reply);

    /// <summary>去抖由调用方负责；没有 Resize 能力时忽略。</summary>
    ValueTask ResizeAsync(TerminalSize size, CancellationToken cancellationToken = default);

    ValueTask<bool> SignalAsync(TerminalSignal signal, CancellationToken cancellationToken = default);

    /// <summary>进程退出、并且输出已读到 EOF 或静默超时之后完成（03 §6.1）。</summary>
    Task<TerminalExit> Exited { get; }

    ValueTask<ForegroundInfo?> GetForegroundAsync(CancellationToken cancellationToken = default);

    ValueTask<string?> GetWorkingDirectoryAsync(CancellationToken cancellationToken = default);

    /// <summary>关闭：挂断（SIGHUP / ClosePseudoConsole），超过 <paramref name="grace"/> 仍未退出则强制结束。</summary>
    ValueTask CloseAsync(TimeSpan grace);
}

[Flags]
public enum ConnectionCapabilities
{
    None = 0,
    Resize = 1,
    PixelSize = 2,
    Signals = 4,
    ForegroundProcess = 8,
    WorkingDirectory = 16,
    ClearBuffer = 32,
}

public enum TerminalSignal
{
    Hup,
    Int,
    Quit,
    Term,
    Kill,
}

/// <summary>退出信息：ExitCode 与 Signal 至多一个非空；两者都为空表示原因未知（例如子进程已被别人回收）。</summary>
public sealed record TerminalExit(int? ExitCode, string? Signal, bool CoreDumped = false, string? Message = null)
{
    public static readonly TerminalExit Unknown = new(null, null);
}

/// <summary>前台进程。<see cref="IsShell"/> 为 true 表示前台就是 shell 本身（没有程序在运行）。</summary>
public sealed record ForegroundInfo(int? Pid, string? Name, bool IsShell);

/// <summary>Unicode 宽度模式。只影响 Windows（映射为 ConPTY 的 GLYPH_WIDTH_* 标志）；Unix 内核 PTY 不测量字宽。</summary>
public enum PtyWidthMode
{
    Compat,
    Grapheme,
}

/// <summary>启动参数。环境变量是完整的环境块，增删策略由 TCode.Terminal.Shell 决定。</summary>
public sealed record PtyLaunch(
    string File,
    IReadOnlyList<string> Arguments,
    string? Argv0,
    string? WorkingDirectory,
    IReadOnlyList<KeyValuePair<string, string>> Environment,
    TerminalSize InitialSize,
    PtyWidthMode WidthMode = PtyWidthMode.Compat)
{
    /// <summary>Windows：原样传给 CreateProcessW 的命令行（cmd.exe 等不按 CommandLineToArgvW 解析的程序）。</summary>
    public string? WindowsCommandLine { get; init; }

    /// <summary>Windows：东亚歧义宽度按宽字符（ConPTY 0x20，仅 1.25+）。</summary>
    public bool AmbiguousWide { get; init; }
}

public sealed class PtyException(string message, int stage = 0, int nativeError = 0) : Exception(message)
{
    /// <summary>失败阶段（Unix：TCPTY_STAGE_*）。</summary>
    public int Stage { get; } = stage;

    /// <summary>原始 errno 或 Win32 错误码。</summary>
    public int NativeError { get; } = nativeError;
}
