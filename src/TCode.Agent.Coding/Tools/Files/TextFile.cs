using System.Text;

namespace TCode.Agent.Coding.Tools.Files;

/// <summary>
/// 文本文件的读写：识别 UTF-8 BOM 与换行风格，内部统一用 LF；写回时恢复原样。
/// 前 8KB 含 NUL 字节视为二进制文件。
/// </summary>
public sealed record TextFile(string Text, bool HasBom, string NewLine)
{
    private const int BinaryProbeBytes = 8192;
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static bool LooksBinary(ReadOnlySpan<byte> bytes)
    {
        var probe = bytes[..Math.Min(bytes.Length, BinaryProbeBytes)];
        return probe.IndexOf((byte)0) >= 0;
    }

    public static TextFile Decode(byte[] bytes)
    {
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = Utf8NoBom.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        var crlf = text.Contains("\r\n", StringComparison.Ordinal);
        return new TextFile(crlf ? text.Replace("\r\n", "\n", StringComparison.Ordinal) : text, bom, crlf ? "\r\n" : "\n");
    }

    public static async Task<TextFile> ReadAsync(string path, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct);
        if (LooksBinary(bytes))
        {
            throw new ToolArgumentException($"{path} appears to be a binary file.");
        }
        return Decode(bytes);
    }

    /// <summary>把 LF 文本按本文件的风格编码。</summary>
    public byte[] Encode(string lfText)
    {
        var text = NewLine == "\n" ? lfText : lfText.Replace("\n", NewLine, StringComparison.Ordinal);
        var body = Utf8NoBom.GetBytes(text);
        return HasBom ? [0xEF, 0xBB, 0xBF, .. body] : body;
    }

    public static string NormalizeNewLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal);
}
