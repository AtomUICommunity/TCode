using System.Collections.Concurrent;
using System.Security.Cryptography;
using TCode.Agent.Coding.Workspace;

namespace TCode.Agent.Coding.Tools.Files;

/// <summary>
/// 记录 Agent 读过的文件（修改时间、大小、内容哈希），用于“先读后改”与过期检测：
/// 文件在读取之后被别人改过时，拒绝编辑并要求重新读取。
/// </summary>
public sealed class FileStateTracker
{
    private readonly ConcurrentDictionary<string, Stamp> _stamps = new(PathUtil.Comparer);

    private sealed record Stamp(DateTime ModifiedUtc, long Length, byte[] Hash);

    public void Record(string path, byte[] content)
    {
        var info = new FileInfo(path);
        _stamps[PathUtil.RealPath(path)] = new Stamp(info.LastWriteTimeUtc, content.Length, SHA256.HashData(content));
    }

    public bool HasRead(string path) => _stamps.ContainsKey(PathUtil.RealPath(path));

    /// <summary>文件自上次读取（或本工具写入）后没有变化时返回 null，否则返回给模型的错误说明。</summary>
    public string? CheckFresh(string path, byte[] current)
    {
        if (!_stamps.TryGetValue(PathUtil.RealPath(path), out var stamp))
        {
            return $"You must read {path} with the read tool before modifying it.";
        }
        var info = new FileInfo(path);
        if (info.LastWriteTimeUtc == stamp.ModifiedUtc && current.Length == stamp.Length)
        {
            return null;
        }
        // mtime 变了但内容相同（例如 touch、格式化后还原）时仍允许。
        return current.Length == stamp.Length && SHA256.HashData(current).AsSpan().SequenceEqual(stamp.Hash)
            ? null
            : $"{path} has been modified since it was last read (by the user, a formatter or another agent). Read it again before editing.";
    }
}
