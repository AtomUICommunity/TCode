using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TKCode.Desktop.Controls.Composer;
using TKCode.Desktop.Icons;
using TKCode.Desktop.Localization;

namespace TKCode.Desktop.ViewModels.Completion;

/// <summary>正在输入的补全查询：触发字符、查询词、在文档中的范围 [Start, End)（含触发字符）。</summary>
public sealed record CompletionQuery(char Trigger, string Text, int Start, int End);

/// <summary>选中一项后做什么：插入 tag（替换查询文字），或删掉查询文字后执行一个动作（切换 role、选 MCP 提示词等）。</summary>
public sealed record CompletionAction(TagSegment? Tag = null, Action? Run = null);

/// <param name="Title">标题。</param>
/// <param name="Subtitle">副标题：描述或所在目录。</param>
/// <param name="Source">右侧的来源标记（项目 / 全局 / 服务器名…）。</param>
/// <param name="Score">与查询的匹配分数；查询为空时按提供者给出的顺序。</param>
/// <param name="Highlights">标题中命中查询的字符下标。</param>
/// <param name="TabText">按 Tab 时用这段文字替换查询并继续补全（例如进入文件夹）；null 时 Tab 与回车相同。</param>
public sealed record CompletionItem(string Title, string? Subtitle, LucideKind Icon, string? Source, CompletionAction Action,
    int Score = 0, int[]? Highlights = null, string? TabText = null);

/// <summary>补全来源（docs/design/composer.md §7.2）。</summary>
public interface ICompletionProvider
{
    /// <summary>'@' 或 '/'。</summary>
    char Trigger { get; }

    /// <summary>查询为空时的分组顺序（越小越靠前）。</summary>
    int Order { get; }

    ValueTask<IReadOnlyList<CompletionItem>> QueryAsync(string query, CancellationToken cancellationToken);
}

/// <summary>正在编辑文档的输入框：提供光标位置，并在确认补全后放置光标。</summary>
public interface ICompletionHost
{
    int CaretIndex { get; }

    void SetCaret(int position);
}

public sealed partial class CompletionItemViewModel(CompletionItem item) : ObservableObject
{
    public CompletionItem Item { get; } = item;
    public string Title => Item.Title;
    public string? Subtitle => Item.Subtitle;
    public LucideKind Icon => Item.Icon;
    public string? Source => Item.Source;
    public IReadOnlyList<int> Highlights => Item.Highlights ?? [];

    [ObservableProperty]
    public partial bool IsHighlighted { get; set; }
}

/// <summary>
/// 输入框的统一补全（docs/design/composer.md §7）：在光标处检测 @查询 / /查询，
/// 防抖后并行询问各提供者，先到的结果先显示；↑↓ 选择，回车 / Tab 确认，Esc 关闭（直到查询换了位置才再次打开）。
/// </summary>
public sealed partial class CompletionViewModel : ObservableObject
{
    public const int MaxItems = 50;
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(60);

    private readonly ComposerDocument _document;
    private readonly IReadOnlyList<ICompletionProvider> _providers;
    private readonly ILogger _logger;
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _queryCts;
    private bool _refreshPosted;
    // Esc 关闭的查询（按起点和触发字符记）：同一个查询不再自动打开
    private (int Start, char Trigger)? _dismissed;
    private readonly List<CompletionItem> _results = [];
    private int _pending;

    public CompletionViewModel(ComposerDocument document, IEnumerable<ICompletionProvider> providers, ILogger logger)
    {
        _document = document;
        _providers = providers.ToList();
        _logger = logger;
        _debounce = new DispatcherTimer { Interval = Debounce };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            StartQuery();
        };
        document.Changed += _ => Invalidate();
    }

    /// <summary>当前获得焦点的输入框；没有时光标视为在文档末尾（例如直接设置文本）。</summary>
    public ICompletionHost? Host { get; set; }

    public ObservableCollection<CompletionItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmptyText))]
    public partial string? EmptyText { get; private set; }

    public bool HasEmptyText => EmptyText is not null;

    [ObservableProperty]
    public partial CompletionQuery? Query { get; private set; }

    /// <summary>高亮项变化（视图据此滚动到可见）。</summary>
    public event Action<CompletionItemViewModel>? HighlightChanged;

    private int Caret => Math.Clamp(Host?.CaretIndex ?? _document.Length, 0, _document.Length);

    /// <summary>文档或光标变化：合并到下一次调度时重新检测（编辑与随后的光标移动只算一次）。</summary>
    public void Invalidate()
    {
        if (_refreshPosted)
        {
            return;
        }
        _refreshPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshPosted = false;
            Refresh();
        });
    }

    /// <summary>立即重新检测查询；查询变化时（防抖后）重新询问提供者。</summary>
    public void Refresh()
    {
        var detected = Detect(_document, Caret);
        // 被 Esc 关闭的查询：查询消失或换了位置后才恢复
        if (_dismissed is { } dismissed && (detected is null || (detected.Start, detected.Trigger) != dismissed))
        {
            _dismissed = null;
        }
        var query = _dismissed is null ? detected : null;
        if (query is null)
        {
            CloseList();
            return;
        }
        if (query == Query && IsOpen)
        {
            return;
        }
        var triggerChanged = Query?.Trigger != query.Trigger || Query?.Start != query.Start;
        Query = query;
        if (!_providers.Any(p => p.Trigger == query.Trigger))
        {
            CloseList();
            return;
        }
        if (triggerChanged)
        {
            // 新的查询：立即询问（第一次打开不等防抖），之后的输入防抖
            _debounce.Stop();
            StartQuery();
        }
        else
        {
            _debounce.Stop();
            _debounce.Start();
        }
    }

    /// <summary>
    /// 光标处的查询（§7.1）：从光标向前取一段不含空白的文字，以 @ 或 / 开头，且前面是文档开头、空白或换行。
    /// tag 也算边界（U+FFFC），紧跟在 tag 后面的 @ 不触发。
    /// </summary>
    public static CompletionQuery? Detect(ComposerDocument document, int caret)
    {
        var start = caret;
        while (start > 0 && document.CharAt(start - 1) is { } c && !char.IsWhiteSpace(c) && c != '￼')
        {
            start--;
        }
        if (start == caret || document.CharAt(start) is not ('@' or '/') || start > 0 && document.CharAt(start - 1) == '￼')
        {
            return null;
        }
        var trigger = document.CharAt(start)!.Value;
        var text = document.ToCharText(start + 1, caret - start - 1);
        // 查询里再出现触发字符（例如 @a@b）不算查询
        return text.Contains(trigger) && trigger == '@' ? null : new CompletionQuery(trigger, text, start, caret);
    }

    private async void StartQuery()
    {
        if (Query is not { } query)
        {
            return;
        }
        _queryCts?.Cancel();
        var cts = new CancellationTokenSource();
        _queryCts = cts;
        _results.Clear();
        var providers = _providers.Where(p => p.Trigger == query.Trigger).ToList();
        _pending = providers.Count;
        Publish(query);
        await Task.WhenAll(providers.Select(async provider =>
        {
            IReadOnlyList<CompletionItem> items;
            try
            {
                items = await provider.QueryAsync(query.Text, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Completion provider {Provider} failed", provider.GetType().Name);
                items = [];
            }
            if (cts.IsCancellationRequested)
            {
                return;
            }
            // 每个提供者的结果先到先显示
            _results.AddRange(items.Select((item, index) => (item, index)).Select(x => x.item with
            {
                // 查询为空时按分组顺序，同组内保持提供者给出的顺序
                Score = query.Text.Length == 0 ? -(provider.Order * 10_000 + x.index) : x.item.Score,
            }));
            _pending--;
            Publish(query);
        }));
    }

    private void Publish(CompletionQuery query)
    {
        if (Query != query)
        {
            return;
        }
        var highlighted = Items.FirstOrDefault(i => i.IsHighlighted)?.Item;
        Items.Clear();
        foreach (var item in _results.OrderByDescending(i => i.Score).ThenBy(i => i.Title.Length).Take(MaxItems))
        {
            Items.Add(new CompletionItemViewModel(item));
        }
        // 结果增量到达时尽量保持原来的高亮项
        var keep = highlighted is null ? null : Items.FirstOrDefault(i => i.Item == highlighted);
        if ((keep ?? Items.FirstOrDefault()) is { } first)
        {
            first.IsHighlighted = true;
        }
        EmptyText = Items.Count > 0 ? null
            : Strings.Get(_pending > 0 ? TKCodeLangResourceKind.CompletionLoading : TKCodeLangResourceKind.CompletionNoResults);
        IsOpen = true;
    }

    public void Move(int delta)
    {
        if (Items.Count == 0)
        {
            return;
        }
        var index = Items.ToList().FindIndex(i => i.IsHighlighted);
        var next = index < 0 ? 0 : Math.Clamp(index + delta, 0, Items.Count - 1);
        for (var i = 0; i < Items.Count; i++)
        {
            Items[i].IsHighlighted = i == next;
        }
        HighlightChanged?.Invoke(Items[next]);
    }

    /// <summary>确认高亮项（回车 / Tab）；没有可选项时返回 false。</summary>
    public bool AcceptHighlighted()
    {
        if (!IsOpen || Items.FirstOrDefault(i => i.IsHighlighted) is not { } item)
        {
            return false;
        }
        Pick(item);
        return true;
    }

    /// <summary>Tab：高亮项有 <see cref="CompletionItem.TabText"/> 时用它替换查询文字并继续补全；返回是否处理。</summary>
    public bool TabComplete()
    {
        if (!IsOpen || Query is not { } query || Items.FirstOrDefault(i => i.IsHighlighted)?.Item.TabText is not { } text)
        {
            return false;
        }
        var end = Math.Max(query.Start, Math.Min(Caret, _document.Length));
        var caret = _document.Replace(query.Start, end - query.Start, [new TextSegment(text)]);
        Host?.SetCaret(caret);
        Invalidate();
        return true;
    }

    /// <summary>确认一项：tag 替换查询文字（后面跟一个空格）；动作先删掉查询文字再执行。</summary>
    [RelayCommand]
    private void Pick(CompletionItemViewModel item)
    {
        if (Query is not { } query)
        {
            return;
        }
        // 查询在打开列表之后可能又被编辑过：以当前光标为终点
        var end = Math.Max(query.Start, Math.Min(Caret, _document.Length));
        var action = item.Item.Action;
        IReadOnlyList<Segment> replacement = action.Tag is { } tag ? [tag, new TextSegment(" ")] : [];
        CloseList();
        var caret = _document.Replace(query.Start, end - query.Start, replacement);
        Host?.SetCaret(caret);
        action.Run?.Invoke();
    }

    /// <summary>Esc：关闭并记住这个查询，继续输入时不再弹出，换一个查询后恢复。</summary>
    public void Dismiss()
    {
        if (Query is { } query)
        {
            _dismissed = (query.Start, query.Trigger);
        }
        CloseList();
    }

    private void CloseList()
    {
        _debounce.Stop();
        _queryCts?.Cancel();
        _queryCts = null;
        _results.Clear();
        Query = null;
        Items.Clear();
        EmptyText = null;
        IsOpen = false;
    }
}
