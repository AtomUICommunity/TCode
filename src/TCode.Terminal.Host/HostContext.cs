using TCode.Terminal.Core;
using TCode.Terminal.Host.Sessions;
using TCode.Terminal.Protocol;

namespace TCode.Terminal.Host;

/// <summary>会话需要的 host 级状态。</summary>
internal sealed class HostContext(HostLog log, Action<Session> onSessionExited)
{
    public HostLog Log { get; } = log;

    /// <summary>最近一次 host/configure 的设置（整份替换）。</summary>
    public HostSettingsDto Settings { get; set; } = new();

    /// <summary>镜像核心的基础选项（回滚上限等，18.1 第 9 条：修改只对新会话生效）。</summary>
    public TerminalCoreOptions CoreOptions { get; set; } = new(new TerminalSize(80, 24, 0, 0)) { DeferClipboardRead = true };

    public void OnSessionExited(Session session) => onSessionExited(session);
}

/// <summary>ULID（时间有序的 26 字符标识，Crockford base32）：instanceId 与 persistentId。</summary>
internal static class Ulid
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string New()
    {
        Span<byte> bytes = stackalloc byte[16];
        var ms = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var i = 0; i < 6; i++)
        {
            bytes[i] = (byte)(ms >> (40 - i * 8));
        }
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes[6..]);
        var value = new System.Numerics.BigInteger(bytes, isUnsigned: true, isBigEndian: true);
        Span<char> chars = stackalloc char[26];
        for (var i = 25; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(value % 32)];
            value /= 32;
        }
        return new string(chars);
    }
}
