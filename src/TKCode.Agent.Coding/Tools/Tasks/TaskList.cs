namespace TKCode.Agent.Coding.Tools.Tasks;

/// <summary>
/// 会话的任务清单（Claude Code TaskCreate 系列的数据模型）。主会话与它的子 Agent 共用；
/// 每次变化触发 <see cref="Changed"/>，由会话写入 JSONL 的全量快照。
/// </summary>
public sealed class TaskList
{
    public static readonly string[] Statuses = ["pending", "in_progress", "completed"];

    private readonly Lock _gate = new();
    private readonly List<TaskItem> _items = [];
    private int _next;

    public event Action<IReadOnlyList<TaskItem>>? Changed;

    public IReadOnlyList<TaskItem> Items
    {
        get
        {
            lock (_gate)
            {
                return _items.ToArray();
            }
        }
    }

    public void Restore(IReadOnlyList<TaskItem>? items)
    {
        if (items is null)
        {
            return;
        }
        lock (_gate)
        {
            _items.Clear();
            _items.AddRange(items);
            _next = items.Select(i => int.TryParse(i.Id, out var n) ? n : 0).DefaultIfEmpty(0).Max();
        }
    }

    public TaskItem? Get(string id)
    {
        lock (_gate)
        {
            return _items.FirstOrDefault(i => i.Id == id);
        }
    }

    public TaskItem Create(string subject, string description, string? activeForm)
    {
        TaskItem item;
        lock (_gate)
        {
            item = new TaskItem((++_next).ToString(), subject, description, "pending") { ActiveForm = activeForm };
            _items.Add(item);
        }
        RaiseChanged();
        return item;
    }

    /// <summary>更新一项；status 为 deleted 时删除，并从其它任务的依赖中移除。返回 null 表示任务不存在。</summary>
    public TaskItem? Update(string id, Func<TaskItem, TaskItem> update, bool delete = false)
    {
        TaskItem? result;
        lock (_gate)
        {
            var index = _items.FindIndex(i => i.Id == id);
            if (index < 0)
            {
                return null;
            }
            if (delete)
            {
                result = _items[index];
                _items.RemoveAt(index);
                for (var i = 0; i < _items.Count; i++)
                {
                    _items[i] = _items[i] with
                    {
                        Blocks = _items[i].Blocks.Where(b => b != id).ToArray(),
                        BlockedBy = _items[i].BlockedBy.Where(b => b != id).ToArray(),
                    };
                }
            }
            else
            {
                result = _items[index] = update(_items[index]);
            }
        }
        RaiseChanged();
        return result;
    }

    /// <summary>建立依赖：<paramref name="blocker"/> 完成之前 <paramref name="blocked"/> 不能开始（双向记录）。</summary>
    public bool Link(string blocker, string blocked)
    {
        lock (_gate)
        {
            var a = _items.FindIndex(i => i.Id == blocker);
            var b = _items.FindIndex(i => i.Id == blocked);
            if (a < 0 || b < 0 || a == b)
            {
                return false;
            }
            _items[a] = _items[a] with { Blocks = _items[a].Blocks.Append(blocked).Distinct().ToArray() };
            _items[b] = _items[b] with { BlockedBy = _items[b].BlockedBy.Append(blocker).Distinct().ToArray() };
        }
        return true;
    }

    public void RaiseChanged() => Changed?.Invoke(Items);
}
