using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using TKCode.Desktop.Controls;
using TKCode.Desktop.Localization;
using TKCode.Rpc;

namespace TKCode.Desktop.ViewModels;

/// <summary>上下文容量卡片的一行图例：类别名、占已用上下文的比例、色点的资源键。</summary>
public sealed record ContextCategoryItem(string Label, string Share, string MarkerKey);

/// <summary>
/// 输入框下方的上下文占用（docs/design/context-usage.md）：占用环与百分比（最近一次回复的 tokens / 上下文预算），
/// 悬停卡片打开时向 Host 取按来源的分类估算与整个会话的平均缓存命中率。
/// </summary>
public sealed partial class ContextUsageViewModel : ObservableObject
{
    /// <summary>分段条的叠放顺序（与上下文的实际顺序一致：系统提示 → 工具定义 → 消息）；颜色跟着类别，不随排名变化。</summary>
    private static readonly (TKCodeLangResourceKind Label, string MarkerKey, Func<ContextBreakdownDto, long> Tokens)[] Kinds =
    [
        (TKCodeLangResourceKind.ContextCategorySystemPrompt, "TKCode.ContextSystemPrompt", b => b.SystemPrompt),
        (TKCodeLangResourceKind.ContextCategorySystemTools, "TKCode.ContextSystemTools", b => b.SystemTools),
        (TKCodeLangResourceKind.ContextCategoryMcpTools, "TKCode.ContextMcpTools", b => b.McpTools),
        (TKCodeLangResourceKind.ContextCategorySkills, "TKCode.ContextSkills", b => b.Skills),
        (TKCodeLangResourceKind.ContextCategoryMessages, "TKCode.ContextMessages", b => b.Messages),
        (TKCodeLangResourceKind.ContextCategoryOther, "TKCode.ContextOther", b => b.Other),
    ];

    private readonly Func<string, CancellationToken, Task<ContextUsageDto>> _load;
    private readonly ILogger _logger;
    private string? _sessionId;
    private long _tokens;
    private long _budget;
    /// <summary>最近一次从 Host 取到的明细（属于 <see cref="_sessionId"/>）。</summary>
    private ContextUsageDto? _details;
    private int _version;

    /// <param name="load">按会话 id 向 Host 取上下文占用明细。</param>
    public ContextUsageViewModel(Func<string, CancellationToken, Task<ContextUsageDto>> load, ILogger logger)
    {
        _load = load;
        _logger = logger;
    }

    /// <summary>占用比例（0–1）。</summary>
    [ObservableProperty]
    public partial double Value { get; private set; }

    [ObservableProperty]
    public partial string PercentText { get; private set; } = "";

    /// <summary>当前会话有用量可显示（占用环可见）。</summary>
    [ObservableProperty]
    public partial bool HasUsage { get; private set; }

    /// <summary>接近自动压缩阈值（占用环变色，卡片底部提示 /compact）。</summary>
    [ObservableProperty]
    public partial bool IsNearlyFull { get; private set; }

    /// <summary>卡片标题右侧：已用 / 预算（比例），数字按界面语言缩写（2.6万/100万、26K / 1M）。</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<BarSegment> Segments { get; private set; } = [];

    /// <summary>图例：按占比从大到小，省略为 0 的类别。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCategories))]
    public partial IReadOnlyList<ContextCategoryItem> Categories { get; private set; } = [];

    public bool HasCategories => Categories.Count > 0;

    /// <summary>外部运行时：Host 不知道它的系统提示与工具，没有分类明细。</summary>
    [ObservableProperty]
    public partial bool IsBreakdownUnavailable { get; private set; }

    /// <summary>整个会话按 token 加权的平均缓存命中率；还没取到或没有任何缓存读写时为“—”。</summary>
    [ObservableProperty]
    public partial string CacheHitRate { get; private set; } = "—";

    /// <summary>悬停卡片是否打开（与 HoverCard.IsOpen 双向绑定）；打开时刷新明细。</summary>
    [ObservableProperty]
    public partial bool IsCardOpen { get; set; }

    partial void OnIsCardOpenChanged(bool value)
    {
        if (value)
        {
            _ = RefreshAsync();
        }
    }

    /// <summary>用量、会话、模型或界面语言变化时调用；<paramref name="tokens"/> 或 <paramref name="budget"/> 不大于 0 表示没有可显示的用量。</summary>
    public void Update(string? sessionId, long tokens, long budget, bool nearlyFull)
    {
        var sessionChanged = sessionId != _sessionId;
        if (sessionChanged)
        {
            // 换了会话：上一个会话的明细不再适用
            _sessionId = sessionId;
            _details = null;
            _version++;
        }
        if (tokens <= 0 || budget <= 0)
        {
            _tokens = _budget = 0;
            HasUsage = false;
            IsCardOpen = false;
            Value = 0;
            PercentText = "";
            IsNearlyFull = false;
            Rebuild();
            return;
        }
        var changed = sessionChanged || tokens != _tokens || budget != _budget;
        _tokens = tokens;
        _budget = budget;
        Value = Math.Clamp((double)tokens / budget, 0, 1);
        PercentText = $"{Math.Round(Value * 100):0}%";
        IsNearlyFull = nearlyFull;
        HasUsage = true;
        Rebuild();
        if (changed && IsCardOpen)
        {
            _ = RefreshAsync();
        }
    }

    private async Task RefreshAsync()
    {
        if (_sessionId is not { } sessionId)
        {
            return;
        }
        var version = ++_version;
        try
        {
            var details = await _load(sessionId, CancellationToken.None);
            if (version == _version)
            {
                _details = details;
                Rebuild();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Load context usage failed");
        }
    }

    private void Rebuild()
    {
        var culture = CultureInfo.CurrentUICulture;
        Summary = HasUsage
            ? Strings.Format(TKCodeLangResourceKind.ContextCardSummaryFormat, Compact(_tokens, culture), Compact(_budget, culture), Percent(Value, "0.#"))
            : "";
        CacheHitRate = _details?.CacheHitRate is { } rate ? Percent(rate, "0.0") : "—";
        IsBreakdownUnavailable = _details is { Breakdown: null };
        var breakdown = _details?.Breakdown;
        var total = breakdown is null ? 0 : Kinds.Sum(k => k.Tokens(breakdown));
        if (breakdown is null || total <= 0)
        {
            // 还没有明细（或外部运行时）：整段一个颜色，跟占用环一致
            Segments = HasUsage ? [new BarSegment(Value, IsNearlyFull ? "TKCode.FileOrange" : "TKCode.TextSecondary")] : [];
            Categories = [];
            return;
        }
        Segments = Kinds.Select(k => new BarSegment(Value * k.Tokens(breakdown) / (double)total, k.MarkerKey)).ToArray();
        Categories = Kinds
            .Select(k => (k, Tokens: k.Tokens(breakdown)))
            .Where(c => c.Tokens > 0)
            .OrderByDescending(c => c.Tokens)
            .Select(c => new ContextCategoryItem(Strings.Get(c.k.Label), Percent((double)c.Tokens / total, "0.0"), c.k.MarkerKey))
            .ToArray();
    }

    private static string Percent(double fraction, string format) =>
        (fraction * 100).ToString(format, CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// 卡片里的大数：中文按万 / 亿（2.6万、100万），其他语言按 K / M（26K、1M），一位小数；小于一个单位时写全。
    /// 两套单位的进位不同，没法只靠文案模板表达，所以按界面语言分支。
    /// </summary>
    internal static string Compact(long value, CultureInfo culture)
    {
        static string Scaled(double v, string unit) => v.ToString(v >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture) + unit;
        if (culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return value >= 100_000_000 ? Scaled(value / 1e8, "亿")
                : value >= 10_000 ? Scaled(value / 1e4, "万")
                : value.ToString("N0", culture);
        }
        return value >= 1_000_000 ? Scaled(value / 1e6, "M")
            : value >= 1_000 ? Scaled(value / 1e3, "K")
            : value.ToString("N0", culture);
    }
}
