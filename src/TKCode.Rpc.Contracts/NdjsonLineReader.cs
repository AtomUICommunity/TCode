using System.Buffers;

namespace TKCode.Rpc;

/// <summary>
/// 从流中按 <c>\n</c> 切分字节行（不含换行符，去掉行尾 <c>\r</c>）。
/// 用于按行分帧的 JSON 协议；行长度不设上限（外部 CLI 的单条事件可能很大）。
/// </summary>
public sealed class NdjsonLineReader(Stream stream, int bufferSize = 64 * 1024)
{
    private readonly byte[] _buffer = new byte[bufferSize];
    private int _start;
    private int _end;
    private bool _eof;

    /// <summary>返回下一行；流结束时返回 null（结尾不带换行的残余内容作为最后一行返回）。</summary>
    public async ValueTask<byte[]?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        ArrayBufferWriter<byte>? pending = null;
        while (true)
        {
            var available = _buffer.AsSpan(_start, _end - _start);
            var newline = available.IndexOf((byte)'\n');
            if (newline >= 0)
            {
                var line = available[..newline];
                _start += newline + 1;
                return Finish(pending, line);
            }
            if (available.Length > 0)
            {
                pending ??= new ArrayBufferWriter<byte>(Math.Max(available.Length * 2, 256));
                pending.Write(available);
            }
            _start = _end = 0;
            if (_eof)
            {
                return pending is { WrittenCount: > 0 } ? Finish(pending, default) : null;
            }
            var read = await stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                _eof = true;
                continue;
            }
            _end = read;
        }
    }

    private static byte[] Finish(ArrayBufferWriter<byte>? pending, ReadOnlySpan<byte> tail)
    {
        byte[] line;
        if (pending is null)
        {
            line = tail.ToArray();
        }
        else
        {
            pending.Write(tail);
            line = pending.WrittenSpan.ToArray();
        }
        return line.Length > 0 && line[^1] == (byte)'\r' ? line[..^1] : line;
    }
}
