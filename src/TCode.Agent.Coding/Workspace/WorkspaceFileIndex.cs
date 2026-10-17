using TCode.Agent.Coding.Processes;
using TCode.Agent.Coding.Tools.Search;
using TCode.Config;

namespace TCode.Agent.Coding.Workspace;

/// <param name="Path">相对于索引根目录的路径，用 / 分隔；目录以 / 结尾。</param>
/// <param name="Positions">路径中命中查询的字符下标（高亮用）。</param>
public sealed record FileSearchHit(string Path, bool IsDirectory, int Score, int[] Positions);

/// <param name="Complete">索引是否完整（条目超过上限时为 false）。</param>
/// <param name="Indexing">索引正在建立或刷新（结果可能不全或稍旧）。</param>
public sealed record FileSearchResult(IReadOnlyList<FileSearchHit> Hits, bool Complete, bool Indexing);

/// <summary>
/// 输入框 @ 文件的索引（docs/design/composer.md §8.1）：一个目录下的文件与文件夹列表，供模糊搜索。
/// - 列表与 glob / grep 工具共用来源：有 rg 时用 <c>rg --files</c>，否则用 <see cref="FileWalker"/>；都遵守 .gitignore，跳过 .git；
/// - 文件夹由文件路径推出；
/// - 文件系统变化只把索引标记为过期，下一次搜索时在后台重建，期间继续用旧索引返回结果；
/// - 最多 <see cref="MaxEntries"/> 个条目，超过时保留路径较浅的。
/// </summary>
public sealed class WorkspaceFileIndex : IDisposable
{
    public const int MaxEntries = 200_000;

    // 没有 .gitignore 时也不该进入索引的目录
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.Ordinal) { "node_modules", ".venv", "__pycache__" };

    private readonly string _root;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<string>>> _list;
    private readonly FileSystemWatcher? _watcher;
    private readonly object _gate = new();
    private Entry[] _entries = [];
    private bool _complete = true;
    private volatile bool _stale = true;
    private Task? _building;

    /// <param name="list">列出根目录下的文件（相对路径）；默认与搜索工具相同，测试可以替换。</param>
    public WorkspaceFileIndex(string root, Func<string, CancellationToken, Task<IReadOnlyList<string>>>? list = null)
    {
        _root = Path.GetFullPath(root);
        _list = list ?? ListFilesAsync;
        try
        {
            _watcher = new FileSystemWatcher(_root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName };
            _watcher.Created += OnChanged;
            _watcher.Deleted += OnChanged;
            _watcher.Renamed += OnChanged;
            _watcher.Error += (_, _) => _stale = true;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            // 无法监听（例如句柄耗尽）：每次搜索都重建
            _watcher = null;
        }
    }

    public string Root => _root;

    /// <summary>最近一次使用的时间（Host 据此淘汰不再使用的索引）。</summary>
    public DateTime LastUsed { get; private set; } = DateTime.UtcNow;

    /// <summary>
    /// 搜索：空查询按深度与名称列出浅层条目；否则按模糊匹配分数排序（同分时路径短的在前），只为返回的条目计算命中位置。
    /// 索引尚未建立时等待第一次建立完成；之后的过期重建在后台进行。
    /// </summary>
    public async Task<FileSearchResult> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        LastUsed = DateTime.UtcNow;
        var building = EnsureBuilding();
        Entry[] entries;
        lock (_gate)
        {
            entries = _entries;
        }
        if (entries.Length == 0 && building is not null)
        {
            await building.WaitAsync(cancellationToken);
            lock (_gate)
            {
                entries = _entries;
            }
        }
        var indexing = _building is { IsCompleted: false };
        query = query.Trim();
        if (query.Length == 0)
        {
            var shallow = entries.OrderBy(e => e.Depth).ThenBy(e => e.IsDirectory ? 0 : 1).ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
                .Take(limit).Select(e => new FileSearchHit(e.Path, e.IsDirectory, 0, [])).ToList();
            return new FileSearchResult(shallow, _complete, indexing);
        }
        // 分块并行打分，每块保留自己的前 limit 名，最后合并
        var chunk = Math.Max(4096, (entries.Length + Environment.ProcessorCount - 1) / Environment.ProcessorCount);
        var parts = new List<(int Score, int Index)>[(entries.Length + chunk - 1) / chunk];
        Parallel.For(0, parts.Length, new ParallelOptions { CancellationToken = cancellationToken }, p =>
        {
            var best = new PriorityQueue<int, (int Score, int Length)>(limit + 1);
            var end = Math.Min(entries.Length, (p + 1) * chunk);
            for (var i = p * chunk; i < end; i++)
            {
                if (FuzzyMatcher.Score(query, entries[i].Path, entries[i].NameStart) is not { } score)
                {
                    continue;
                }
                // 优先级越小越先出队：分数低、路径长的先被淘汰
                best.Enqueue(i, (score, -entries[i].Path.Length));
                if (best.Count > limit)
                {
                    best.Dequeue();
                }
            }
            var list = new List<(int, int)>(best.Count);
            while (best.TryDequeue(out var index, out var priority))
            {
                list.Add((priority.Score, index));
            }
            parts[p] = list;
        });
        var top = parts.SelectMany(part => part)
            .OrderByDescending(s => s.Score)
            .ThenBy(s => entries[s.Index].Path.Length)
            .ThenBy(s => entries[s.Index].Path, StringComparer.Ordinal)
            .Take(limit)
            .Select(s =>
            {
                var entry = entries[s.Index];
                var match = FuzzyMatcher.Match(query, entry.Path, entry.NameStart);
                return new FileSearchHit(entry.Path, entry.IsDirectory, s.Score, match?.Positions ?? []);
            })
            .ToList();
        return new FileSearchResult(top, _complete, indexing);
    }

    /// <summary>需要时开始（重新）建立索引；返回进行中的建立任务。</summary>
    private Task? EnsureBuilding()
    {
        lock (_gate)
        {
            if ((_stale || _watcher is null) && _building is not { IsCompleted: false })
            {
                _stale = false;
                _building = Task.Run(BuildAsync);
            }
            return _building;
        }
    }

    private async Task BuildAsync()
    {
        IReadOnlyList<string> files;
        try
        {
            files = await _list(_root, CancellationToken.None);
        }
        catch (Exception)
        {
            files = [];
        }
        var directories = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<Entry>(files.Count);
        foreach (var file in files)
        {
            if (file.Split('/').Any(SkippedDirectories.Contains))
            {
                continue;
            }
            entries.Add(new Entry(file, false));
            for (var slash = file.IndexOf('/'); slash > 0; slash = file.IndexOf('/', slash + 1))
            {
                directories.Add(file[..(slash + 1)]);
            }
        }
        entries.AddRange(directories.Select(d => new Entry(d, true)));
        var complete = entries.Count <= MaxEntries;
        var result = complete ? entries.ToArray() : entries.OrderBy(e => e.Depth).Take(MaxEntries).ToArray();
        lock (_gate)
        {
            _entries = result;
            _complete = complete;
        }
    }

    private static async Task<IReadOnlyList<string>> ListFilesAsync(string root, CancellationToken cancellationToken)
    {
        if (SearchBackend.RipgrepPath is { } rg)
        {
            var result = await ProcessRunner.RunAsync(rg, ["--files", "--hidden", "--no-require-git", "--no-messages", "-g", "!.git"], root, cancellationToken);
            if (result.ExitCode is 0 or 1)
            {
                return result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(SearchBackend.NormalizeRelative).ToArray();
            }
        }
        return FileWalker.EnumerateFiles(root, cancellationToken).Select(f => f.RelativePath).ToArray();
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        // .git 内部的变化（提交、切换分支时大量出现）不影响文件列表
        var relative = Path.GetRelativePath(_root, e.FullPath).Replace('\\', '/');
        if (relative == ".git" || relative.StartsWith(".git/", StringComparison.Ordinal))
        {
            return;
        }
        _stale = true;
    }

    public void Dispose() => _watcher?.Dispose();

    private readonly record struct Entry(string Path, bool IsDirectory)
    {
        public int NameStart { get; } = FuzzyMatcher.NameStart(Path);

        public int Depth { get; } = Path.TrimEnd('/').Count(c => c == '/');
    }
}
