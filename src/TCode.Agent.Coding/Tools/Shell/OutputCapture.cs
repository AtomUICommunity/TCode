using System.Text;

namespace TCode.Agent.Coding.Tools.Shell;

/// <summary>
/// 命令输出：完整内容流式写入日志文件，内存中只保留滚动的尾部。
/// 交给模型时保留最后 2000 行 / 50KB（先到为准），被截断时附上日志路径。
/// </summary>
public sealed class OutputCapture : IAsyncDisposable
{
    public const int MaxLines = 2000;
    public const int MaxBytes = 50 * 1024;
    private const int TailChars = 256 * 1024;

    private readonly Lock _gate = new();
    private readonly StringBuilder _tail = new();
    private readonly StreamWriter _log;
    private long _totalChars;
    private long _totalLines;

    public OutputCapture(string logPath)
    {
        LogPath = logPath;
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        _log = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
        {
            AutoFlush = true,
        };
    }

    public string LogPath { get; }

    public long TotalChars
    {
        get
        {
            lock (_gate)
            {
                return _totalChars;
            }
        }
    }

    public void Append(ReadOnlySpan<char> chunk)
    {
        lock (_gate)
        {
            _log.Write(chunk);
            _tail.Append(chunk);
            _totalChars += chunk.Length;
            foreach (var c in chunk)
            {
                if (c == '\n')
                {
                    _totalLines++;
                }
            }
            if (_tail.Length > TailChars * 2)
            {
                _tail.Remove(0, _tail.Length - TailChars);
            }
        }
    }

    /// <summary>当前的尾部快照（用于进度显示）。</summary>
    public string Snapshot(int maxChars = 16 * 1024)
    {
        lock (_gate)
        {
            var start = Math.Max(0, _tail.Length - maxChars);
            return _tail.ToString(start, _tail.Length - start);
        }
    }

    /// <summary>交给模型的尾部：最后 2000 行或 50KB；返回值中的 Truncated 表示省略了前面的内容。</summary>
    public (string Text, bool Truncated, long TotalLines) Tail()
    {
        string text;
        long totalChars;
        long totalLines;
        lock (_gate)
        {
            text = _tail.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
            totalChars = _totalChars;
            totalLines = _totalLines + (_tail.Length > 0 && _tail[^1] != '\n' ? 1 : 0);
        }
        var truncated = totalChars > _tail.Length;
        var lines = text.Split('\n');
        var keep = new List<string>();
        var bytes = 0;
        for (var i = lines.Length - 1; i >= 0 && keep.Count < MaxLines; i--)
        {
            var size = Encoding.UTF8.GetByteCount(lines[i]) + 1;
            if (bytes + size > MaxBytes)
            {
                if (keep.Count == 0)
                {
                    // 单行超长：保留该行的末尾部分。
                    keep.Add(lines[i][^Math.Min(lines[i].Length, MaxBytes / 4)..]);
                }
                truncated = true;
                break;
            }
            keep.Add(lines[i]);
            bytes += size;
            if (keep.Count == MaxLines && i > 0)
            {
                truncated = true;
            }
        }
        keep.Reverse();
        return (string.Join('\n', keep), truncated, totalLines);
    }

    public async ValueTask DisposeAsync()
    {
        await _log.DisposeAsync();
    }
}
