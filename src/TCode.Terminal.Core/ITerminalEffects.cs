namespace TCode.Terminal.Core;

/// <summary>
/// 终端副作用。在调用 <see cref="TerminalCore.Write"/> 的线程上同步触发；实现方不能阻塞，也不能重入 TerminalCore。
/// 回调里的借用数据（span）只在回调期间有效，需要保留时必须复制。
/// M0 子集，完整定义见 01-architecture.md §6.1。
/// </summary>
public interface ITerminalEffects
{
    /// <summary>查询应答等需要写回 PTY 的字节。镜像核心写回 PTY；UI 侧核心丢弃。</summary>
    void WriteToPty(ReadOnlySpan<byte> reply);

    void Bell();

    void TitleChanged(string title);
}

public sealed class NullTerminalEffects : ITerminalEffects
{
    public static readonly NullTerminalEffects Instance = new();

    public void WriteToPty(ReadOnlySpan<byte> reply)
    {
    }

    public void Bell()
    {
    }

    public void TitleChanged(string title)
    {
    }
}
