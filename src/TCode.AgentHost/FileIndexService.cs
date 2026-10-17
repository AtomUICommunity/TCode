using TCode.Agent.Coding.Workspace;
using TCode.Rpc;

namespace TCode.AgentHost;

/// <summary>
/// 各目录的 @ 文件索引（docs/design/composer.md §8.1），所有会话与窗口共用。
/// 同时最多保留 <see cref="MaxIndexes"/> 个，超出时淘汰最久未使用的（释放其文件监听）。
/// </summary>
public sealed class FileIndexService : IDisposable
{
    private const int MaxIndexes = 8;

    private readonly Dictionary<string, WorkspaceFileIndex> _indexes = new(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal);

    public async Task<FileSearchResultDto> SearchAsync(FileSearchRequest request, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(request.Cwd))
        {
            return new FileSearchResultDto([], true, false);
        }
        var index = Get(Path.GetFullPath(request.Cwd));
        var result = await index.SearchAsync(request.Query, Math.Clamp(request.Limit, 1, 200), cancellationToken);
        return new FileSearchResultDto(result.Hits.Select(h => new FileSearchItemDto(h.Path, h.IsDirectory, h.Score, h.Positions)).ToList(),
            result.Complete, result.Indexing);
    }

    private WorkspaceFileIndex Get(string root)
    {
        lock (_indexes)
        {
            if (!_indexes.TryGetValue(root, out var index))
            {
                if (_indexes.Count >= MaxIndexes)
                {
                    var oldest = _indexes.Values.MinBy(i => i.LastUsed)!;
                    _indexes.Remove(oldest.Root);
                    oldest.Dispose();
                }
                index = new WorkspaceFileIndex(root);
                _indexes[root] = index;
            }
            return index;
        }
    }

    public void Dispose()
    {
        lock (_indexes)
        {
            foreach (var index in _indexes.Values)
            {
                index.Dispose();
            }
            _indexes.Clear();
        }
    }
}
