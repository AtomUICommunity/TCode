namespace TCode.Terminal.Core;

/// <summary>
/// 创建核心时的配置。镜像核心与 UI 侧核心中"影响状态"的项必须一致（01 §3.2 规则 2），
/// 由 pty host 在 session/attach 的应答中下发。
/// </summary>
public readonly record struct TerminalCoreOptions(TerminalSize Size)
{
    /// <summary>主屏回滚的字节上限。libghostty 的默认值很小，必须显式设置。</summary>
    public long ScrollbackMaxBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>回滚行数上限；null 表示只受字节上限约束。</summary>
    public int? ScrollbackMaxLines { get; init; } = 10_000;

    /// <summary>
    /// 保留的未完成输入（转义序列、UTF-8）上限，0 关闭。开启后快照可以在任意字节位置编码；
    /// 超过上限的超长序列（例如巨大的 Kitty 图像 APC）在其完成之前无法编码快照。
    /// </summary>
    public long ContinuationMaxBytes { get; init; } = 8L * 1024 * 1024;

    /// <summary>OSC 52 读剪贴板改为延后应答（overlay O3）。镜像核心设为 true，由 host 询问 UI 后再写回。</summary>
    public bool DeferClipboardRead { get; init; }
}
