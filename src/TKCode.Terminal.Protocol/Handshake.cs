using System.Security.Cryptography;

namespace TKCode.Terminal.Protocol;

/// <summary>HMAC 双向挑战应答（04 §2.4）：令牌本身从不上线。</summary>
public static class Handshake
{
    public const int TokenSize = 32;
    public const int NonceSize = 32;

    private static ReadOnlySpan<byte> ServerLabel => "tkcode-ptyhost/1/server"u8;
    private static ReadOnlySpan<byte> ClientLabel => "tkcode-ptyhost/1/client"u8;

    public static byte[] NewNonce() => RandomNumberGenerator.GetBytes(NonceSize);

    public static byte[] NewToken() => RandomNumberGenerator.GetBytes(TokenSize);

    public static byte[] ServerProof(ReadOnlySpan<byte> token, ReadOnlySpan<byte> clientNonce, ReadOnlySpan<byte> serverNonce) =>
        Mac(token, ServerLabel, clientNonce, serverNonce);

    public static byte[] ClientProof(ReadOnlySpan<byte> token, ReadOnlySpan<byte> serverNonce, ReadOnlySpan<byte> clientNonce) =>
        Mac(token, ClientLabel, serverNonce, clientNonce);

    public static bool Verify(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual) =>
        expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);

    private static byte[] Mac(ReadOnlySpan<byte> key, ReadOnlySpan<byte> label, ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var message = new byte[label.Length + a.Length + b.Length];
        label.CopyTo(message);
        a.CopyTo(message.AsSpan(label.Length));
        b.CopyTo(message.AsSpan(label.Length + a.Length));
        return HMACSHA256.HashData(key, message);
    }

    public static string ToBase64Url(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
