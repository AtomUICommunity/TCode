using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using StreamMark.Avalonia.Transcript;
using TKCode.Desktop.ViewModels;

namespace TKCode.Desktop.Controls;

/// <summary>
/// 把会话的条目（用户消息、助手消息及其思考 / 正文 / 工具调用块）摊平成 StreamMark 会话视图的条目：
/// 正文块是 Markdown 条目（每个顶层块独立虚拟化），其余都是使用数据模板的内容条目。
/// 两段正文之间连续的思考与工具调用（可跨消息）合并为一个活动组；只有一段思考时单独显示。
/// 条目对象按视图模型缓存（活动组按其第一个块缓存），重建时引用不变，会话面板因此复用已有的行与视图。
/// </summary>
public sealed class TranscriptAdapter
{
    private readonly ObservableCollection<TranscriptEntry> _target;
    private readonly Dictionary<object, TranscriptEntry> _cache = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<AssistantMessageItemViewModel> _watched = [];
    private readonly Dictionary<AssistantBlockViewModel, ActivityGroupViewModel> _groups = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ToolCallBlockViewModel, TranscriptItemViewModel> _cards = new(ReferenceEqualityComparer.Instance);
    private SessionViewModel? _session;
    private bool _attaching;

    public TranscriptAdapter(ObservableCollection<TranscriptEntry> target)
    {
        _target = target;
        Localization.Strings.LanguageChanged += (_, _) =>
        {
            foreach (var group in _groups.Values)
            {
                group.RefreshSummary();
            }
            foreach (var card in _cards.Values)
            {
                switch (card)
                {
                    case QuestionCardViewModel question:
                        question.RefreshLanguage();
                        break;
                    case PlanCardViewModel plan:
                        plan.RefreshLanguage();
                        break;
                }
            }
        };
    }

    /// <summary>新的用户消息加入（宿主据此滚动到底部）。</summary>
    public event Action? UserMessageAdded;

    public void Attach(SessionViewModel? session)
    {
        if (ReferenceEquals(session, _session))
        {
            return;
        }
        if (_session is not null)
        {
            _session.Items.CollectionChanged -= OnItemsChanged;
            _session.PropertyChanged -= OnSessionPropertyChanged;
        }
        foreach (var message in _watched)
        {
            message.Blocks.CollectionChanged -= OnBlocksChanged;
            message.PropertyChanged -= OnMessagePropertyChanged;
        }
        _watched.Clear();
        _cache.Clear();
        _groups.Clear();
        _cards.Clear();
        _session = session;
        if (session is not null)
        {
            session.Items.CollectionChanged += OnItemsChanged;
            session.PropertyChanged += OnSessionPropertyChanged;
        }
        _attaching = true;
        try
        {
            Rebuild();
        }
        finally
        {
            _attaching = false;
        }
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Rebuild();
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems?.OfType<UserMessageItemViewModel>().Any() == true)
        {
            UserMessageAdded?.Invoke();
        }
    }

    private void OnBlocksChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionViewModel.IsPondering) or nameof(SessionViewModel.IsRunning)
            or nameof(SessionViewModel.HasEarlierHistory))
        {
            Rebuild();
        }
    }

    // 底栏（时间、复制、错误）只在回复结束或出错时出现
    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AssistantMessageItemViewModel.IsStreaming) or nameof(AssistantMessageItemViewModel.ErrorMessage))
        {
            Rebuild();
        }
    }

    private TranscriptEntry EntryFor(object item) =>
        _cache.TryGetValue(item, out var entry)
            ? entry
            : _cache[item] = item switch
            {
                // 实时回复：流式文档；历史正文：按需解析（行第一次进入视口时才解析 Markdown）
                TextBlockViewModel { Stream: { } stream } live => new MarkdownEntry(stream) { Tag = live },
                TextBlockViewModel history => MarkdownEntry.FromTextDeferred(history.Text, MarkdownSetup.Services.Profile, history),
                _ => new ContentEntry(item),
            };

    /// <summary>
    /// 提问、进入 Plan 模式的确认与计划审批：显示为独立的卡片，不并入活动组
    /// （docs/design/ask-user.md §7.4、docs/design/plan-mode.md §14.2）。
    /// </summary>
    private static bool IsCard(AssistantBlockViewModel block) =>
        block is ToolCallBlockViewModel { Category: ToolCategory.Question or ToolCategory.EnterPlan or ToolCategory.PlanReview };

    private TranscriptItemViewModel CardFor(AssistantBlockViewModel block)
    {
        var call = (ToolCallBlockViewModel)block;
        if (!_cards.TryGetValue(call, out var card))
        {
            card = _cards[call] = call.Category == ToolCategory.PlanReview ? new PlanCardViewModel(call) : new QuestionCardViewModel(call);
        }
        return card;
    }

    private void Rebuild()
    {
        var desired = new List<TranscriptEntry>();
        if (_session is { } session)
        {
            // 还有更早的消息：顶部提示（滚动到顶部时加载）
            if (session.HasEarlierHistory)
            {
                desired.Add(EntryFor(session.EarlierHistory));
            }
            var items = session.Items;
            var start = 0;
            for (var i = 1; i <= items.Count; i++)
            {
                // 一轮 = 一条用户消息到下一条用户消息之前（或列表末尾）
                if (i == items.Count || items[i] is UserMessageItemViewModel)
                {
                    // 最后一轮仍在运行：实时显示（正文留在外面）；其余轮次已结束：收进工作空间
                    if (i < items.Count || !session.IsRunning)
                    {
                        EmitFinishedTurn(items, start, i, desired);
                    }
                    else
                    {
                        EmitLiveTurn(session, items, start, desired);
                    }
                    start = i;
                }
            }
            if (session.IsPondering)
            {
                desired.Add(EntryFor(session));
            }
        }
        Sync(desired);
    }

    /// <summary>进行中的一轮：思考 / 工具在两段正文之间分组，正文与底栏照常显示。</summary>
    private void EmitLiveTurn(SessionViewModel session, IList<TranscriptItemViewModel> items, int start, List<TranscriptEntry> desired)
    {
        var pending = new List<AssistantBlockViewModel>();

        // 收集到的思考 / 工具步骤：遇到正文、消息底栏或下一条用户消息时结束一个组
        void Flush(bool trailing)
        {
            if (pending.Count == 0)
            {
                return;
            }
            if (pending is [ThinkingBlockViewModel single])
            {
                desired.Add(EntryFor(single));
            }
            else
            {
                if (!_groups.TryGetValue(pending[0], out var group))
                {
                    group = _groups[pending[0]] = new ActivityGroupViewModel();
                }
                group.SetSteps(pending);
                group.IsActive = trailing && session.IsRunning;
                desired.Add(EntryFor(group));
            }
            pending.Clear();
        }

        for (var i = start; i < items.Count && (i == start || items[i] is not UserMessageItemViewModel); i++)
        {
            var item = items[i];
            if (item is AssistantMessageItemViewModel message)
            {
                Watch(message);
                foreach (var block in message.Blocks)
                {
                    if (block is TextBlockViewModel)
                    {
                        Flush(trailing: false);
                        desired.Add(EntryFor(block));
                    }
                    else if (IsCard(block))
                    {
                        Flush(trailing: false);
                        desired.Add(EntryFor(CardFor(block)));
                    }
                    else
                    {
                        pending.Add(block);
                    }
                }
                // 本轮仍在进行（工具执行间隙）时，最新消息不显示底栏，活动组也不因此被截断
                var live = session.IsRunning && ReferenceEquals(message, items[^1]);
                if ((message.ShowFooter && !live) || message.HasError)
                {
                    Flush(trailing: false);
                    desired.Add(EntryFor(message));
                }
            }
            else
            {
                Flush(trailing: false);
                desired.Add(EntryFor(item));
            }
        }
        Flush(trailing: true);
    }

    /// <summary>
    /// 已结束的一轮。轮次中途有压缩分隔线（例如批准计划后清空上下文、按计划执行，docs/design/plan-mode.md §11）时，
    /// 分隔线前后各自成段，分隔线留在两段之间。
    /// </summary>
    private void EmitFinishedTurn(IList<TranscriptItemViewModel> items, int start, int end, List<TranscriptEntry> desired)
    {
        for (var i = start; i < end; i++)
        {
            if (items[i] is CompactionItemViewModel divider)
            {
                EmitFinishedSegment(items, start, i, desired);
                desired.Add(EntryFor(divider));
                start = i + 1;
            }
        }
        EmitFinishedSegment(items, start, end, desired);
    }

    /// <summary>已结束的一段：除最后一段正文外的思考、工具、中间正文收进“已工作 N 分 N 秒”的工作空间组，默认收起。</summary>
    private void EmitFinishedSegment(IList<TranscriptItemViewModel> items, int start, int end, List<TranscriptEntry> desired)
    {
        var blocks = new List<AssistantBlockViewModel>();
        var messages = new List<AssistantMessageItemViewModel>();
        var others = new List<TranscriptItemViewModel>();
        for (var i = start; i < end; i++)
        {
            switch (items[i])
            {
                case UserMessageItemViewModel user:
                    desired.Add(EntryFor(user));
                    break;
                case AssistantMessageItemViewModel message:
                    Watch(message);
                    messages.Add(message);
                    blocks.AddRange(message.Blocks);
                    break;
                default:
                    others.Add(items[i]);
                    break;
            }
        }

        // 问答 / 计划卡片不收进工作空间：放在工作空间之后、最后一段正文之前
        var cards = blocks.Where(IsCard).ToList();
        blocks.RemoveAll(IsCard);

        // 最后一段正文留在外面，其余全部收进工作空间
        var last = blocks.FindLastIndex(b => b is TextBlockViewModel);
        var fold = last < 0 ? blocks : blocks.Where((_, index) => index != last).ToList();
        if (fold.Count > 0)
        {
            if (!_groups.TryGetValue(fold[0], out var group))
            {
                group = _groups[fold[0]] = new ActivityGroupViewModel();
            }
            group.SetSteps(fold);
            group.SetWorked(Elapsed(messages));
            desired.Add(EntryFor(group));
        }
        foreach (var card in cards)
        {
            desired.Add(EntryFor(CardFor(card)));
        }
        if (last >= 0)
        {
            desired.Add(EntryFor(blocks[last]));
        }
        // 底栏（时间、复制）只给最后一条消息；出错的消息始终显示错误
        foreach (var message in messages)
        {
            if (message.HasError || (ReferenceEquals(message, messages[^1]) && message.ShowFooter))
            {
                desired.Add(EntryFor(message));
            }
        }
        foreach (var other in others)
        {
            desired.Add(EntryFor(other));
        }
    }

    private static TimeSpan Elapsed(IReadOnlyList<AssistantMessageItemViewModel> messages)
    {
        if (messages.Count == 0)
        {
            return TimeSpan.Zero;
        }
        var end = messages[^1].EndedAt ?? messages[^1].Timestamp;
        var elapsed = end - messages[0].Timestamp;
        return elapsed > TimeSpan.Zero ? elapsed : TimeSpan.Zero;
    }

    private void Watch(AssistantMessageItemViewModel message)
    {
        if (_watched.Add(message))
        {
            message.Blocks.CollectionChanged += OnBlocksChanged;
            message.PropertyChanged += OnMessagePropertyChanged;
        }
    }

    // 增量同步：保留共同前缀与共同后缀，只替换中间部分。流式输出时通常只是在末尾追加，
    // 加载更早的历史时只是在开头插入，已有条目（及其行与视图）都不会被重建。
    private void Sync(List<TranscriptEntry> desired)
    {
        var prefix = 0;
        while (prefix < desired.Count && prefix < _target.Count && ReferenceEquals(desired[prefix], _target[prefix]))
        {
            prefix++;
        }
        var suffix = 0;
        while (suffix < desired.Count - prefix && suffix < _target.Count - prefix &&
               ReferenceEquals(desired[^(suffix + 1)], _target[^(suffix + 1)]))
        {
            suffix++;
        }
        var removeEnd = _target.Count - suffix;
        for (var i = removeEnd - 1; i >= prefix; i--)
        {
            _target.RemoveAt(i);
        }
        // 会话进行中追加的条目播入场动画；初次挂载与加载更早的历史不播
        var animate = !_attaching && _session is { IsLoadingEarlier: false };
        for (var i = prefix; i < desired.Count - suffix; i++)
        {
            if (animate && desired[i] is ContentEntry { Content: { } content })
            {
                Motion.Entrance.Mark(content);
            }
            _target.Insert(i, desired[i]);
        }
    }
}
