using System.Text.Json;

namespace TKCode.Agent.Coding.Sessions;

/// <summary>
/// 按行读取会话文件（UTF-8 字节，不先转成 string）：大的工具输出行不会在大对象堆上留下 UTF-16 副本。
/// 跳过空白行；返回的内存只在下一次 <see cref="Next"/> 之前有效。
/// </summary>
internal sealed class Utf8LineReader(string path) : IDisposable
{
    private readonly FileStream _stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1,
        FileOptions.SequentialScan);
    private byte[] _buffer = new byte[64 * 1024];
    private int _start;
    private int _end;
    private bool _eof;
    private bool _started;

    public ReadOnlyMemory<byte>? Next()
    {
        while (true)
        {
            var newline = _buffer.AsSpan(_start, _end - _start).IndexOf((byte)'\n');
            if (newline >= 0 || (_eof && _start < _end))
            {
                var length = newline >= 0 ? newline : _end - _start;
                var line = new ReadOnlyMemory<byte>(_buffer, _start, length);
                _start += newline >= 0 ? length + 1 : length;
                if (!line.Span.Trim(" \t\r"u8).IsEmpty)
                {
                    return line;
                }
                continue;
            }
            if (_eof)
            {
                return null;
            }
            Fill();
        }
    }

    private void Fill()
    {
        if (_start > 0)
        {
            _buffer.AsSpan(_start, _end - _start).CopyTo(_buffer);
            _end -= _start;
            _start = 0;
        }
        if (_end == _buffer.Length)
        {
            // 一行比缓冲区长：扩容（只在读这一行期间存在）
            Array.Resize(ref _buffer, _buffer.Length * 2);
        }
        var read = _stream.Read(_buffer, _end, _buffer.Length - _end);
        if (read == 0)
        {
            _eof = true;
            return;
        }
        _end += read;
        if (!_started)
        {
            _started = true;
            if (_buffer.AsSpan(0, _end).StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            {
                _start = 3;
            }
        }
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>会话列表只需要树结构的条目：非用户消息只记 id 与 parentId，不解析正文。从不写入文件。</summary>
internal sealed record SkippedEntry(string Id, string? ParentId) : TreeEntry(Id, ParentId)
{
    /// <summary>非用户消息的行返回其位置；其他行（以及无法判断的行）返回 null，由调用方照常解析。</summary>
    public static SkippedEntry? TryRead(ReadOnlySpan<byte> line)
    {
        try
        {
            var reader = new Utf8JsonReader(line);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }
            string? id = null;
            string? parentId = null;
            var skippedMessage = false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("type"u8))
                {
                    reader.Read();
                    if (!reader.ValueTextEquals("message"u8))
                    {
                        return null;
                    }
                }
                else if (reader.ValueTextEquals("message"u8))
                {
                    reader.Read();
                    // 消息的判别字段 role 总是第一个属性；用户消息（会话标题）照常解析
                    var probe = reader;
                    if (reader.TokenType != JsonTokenType.StartObject || !probe.Read() || !probe.ValueTextEquals("role"u8) || !probe.Read() ||
                        probe.ValueTextEquals("user"u8))
                    {
                        return null;
                    }
                    reader.Skip();
                    skippedMessage = true;
                }
                else if (reader.ValueTextEquals("id"u8))
                {
                    reader.Read();
                    id = reader.GetString();
                }
                else if (reader.ValueTextEquals("parentId"u8))
                {
                    reader.Read();
                    parentId = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }
            return skippedMessage && id is not null ? new SkippedEntry(id, parentId) : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
