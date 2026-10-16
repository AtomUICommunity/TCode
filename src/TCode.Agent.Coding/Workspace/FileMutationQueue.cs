using System.Collections.Concurrent;

namespace TCode.Agent.Coding.Workspace;

/// <summary>同一文件（按真实路径）的写入串行执行，不同文件之间仍可并行。</summary>
public sealed class FileMutationQueue
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(PathUtil.Comparer);

    public async Task<T> RunAsync<T>(string path, Func<Task<T>> mutation, CancellationToken cancellationToken = default)
    {
        var gate = _locks.GetOrAdd(PathUtil.RealPath(path), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await mutation();
        }
        finally
        {
            gate.Release();
        }
    }
}
