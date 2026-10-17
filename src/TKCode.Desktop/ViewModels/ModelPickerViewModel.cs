using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKCode.Desktop.Icons;
using TKCode.Desktop.Localization;
using TKCode.Desktop.Services;
using TKCode.Rpc;

namespace TKCode.Desktop.ViewModels;

/// <summary>模型选择器顶部的页签：收藏或某个 Provider。</summary>
public sealed partial class ModelTabViewModel(string? providerId, string? iconKey, string title, bool isRuntime = false) : ObservableObject
{
    /// <summary>外部运行时（Agents 分组）的页签。</summary>
    public bool IsRuntime { get; } = isRuntime;

    /// <summary>Agents 分组的第一个页签：左侧显示分隔线。</summary>
    public bool StartsRuntimeGroup { get; init; }

    /// <summary>null 表示收藏页签。</summary>
    public string? ProviderId { get; } = providerId;
    public string? IconKey { get; } = iconKey;
    public string Title { get; } = title;
    public bool IsFavorites => ProviderId is null;

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>当前会话可以切换到该页签的模型。</summary>
    [ObservableProperty]
    public partial bool IsAvailable { get; set; } = true;
}

public sealed partial class ModelOptionViewModel(ModelDto model) : ObservableObject
{
    public ModelDto Model { get; } = model;
    public string Name => Model.Name;
    /// <summary>芯片与列表中显示的短名：图标已表明品牌时省略品牌前缀（Claude Opus 5.5 → Opus 5.5）。</summary>
    public string ShortName { get; } = model.Name.StartsWith("Claude ", StringComparison.Ordinal) ? model.Name["Claude ".Length..] : model.Name;
    public string? Description => Model.Description;
    public string? IconKey { get; } = ModelIcons.ForModel(model.Id) ?? ModelIcons.ForProvider(model.Provider);

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool IsFavorite { get; set; }

    /// <summary>当前列表中的快捷键（⌘1…⌘9），超出 9 项时为 null。</summary>
    [ObservableProperty]
    public partial string? Shortcut { get; set; }

    [ObservableProperty]
    public partial bool IsHighlighted { get; set; }
}

/// <summary>参数子菜单中的一项（例如推理强度 High，上下文 200K）。</summary>
public sealed partial class ModelParameterChoiceViewModel(object value, string label, bool isDefault) : ObservableObject
{
    public object Value { get; } = value;
    public string Label { get; } = label;
    public bool IsDefault { get; } = isDefault;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

public enum ModelParameterKind
{
    Reasoning,
    ContextWindow,
    FastMode,
}

/// <summary>选择器底部的参数行（推理强度 / 上下文窗口 / 快速模式），展开后在右侧显示子菜单。</summary>
public sealed partial class ModelParameterViewModel(ModelParameterKind kind) : ObservableObject
{
    public ModelParameterKind Kind { get; } = kind;
    public ObservableCollection<ModelParameterChoiceViewModel> Choices { get; } = [];

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial string ValueLabel { get; set; } = "";

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }
}

/// <summary>
/// 模型选择器：Provider 页签 + 搜索 + 模型列表（收藏、快捷键）+ 会话参数。
/// 参数值在本地保存，新建会话时随 CreateSessionRequest 发送；会话存在时经 session/setOptions 同步，并以 Host 返回值为准。
/// </summary>
public sealed partial class ModelPickerViewModel : ObservableObject
{
    private readonly StateService _settings;
    private readonly List<ModelOptionViewModel> _models = [];
    private ModelTabViewModel? _activeTab;

    public ModelPickerViewModel(StateService settings)
    {
        _settings = settings;
        Reasoning = new ModelParameterViewModel(ModelParameterKind.Reasoning);
        ContextWindow = new ModelParameterViewModel(ModelParameterKind.ContextWindow);
        FastMode = new ModelParameterViewModel(ModelParameterKind.FastMode);
        Parameters = [Reasoning, ContextWindow, FastMode];
        Strings.LanguageChanged += (_, _) => RefreshParameters();
    }

    public ObservableCollection<ModelTabViewModel> Tabs { get; } = [];

    /// <summary>当前全部可用模型（设置页“默认模型”下拉用）。</summary>
    public IReadOnlyList<ModelDto> AllModels => _models.Select(m => m.Model).ToArray();
    public ObservableCollection<ModelOptionViewModel> VisibleModels { get; } = [];
    public IReadOnlyList<ModelParameterViewModel> Parameters { get; }
    public ModelParameterViewModel Reasoning { get; }
    public ModelParameterViewModel ContextWindow { get; }
    public ModelParameterViewModel FastMode { get; }

    /// <summary>选择或参数变化（由用户触发）时通知会话同步。</summary>
    public event Action? SelectionChanged;

    /// <summary>
    /// 请求关闭弹层。弹层的打开状态由各 ModelPickerView 自己持有：空状态与会话中各有一个 Composer，
    /// 若把 IsOpen 放在共享的 ViewModel 上，两个弹层会同时打开。
    /// </summary>
    public event Action? CloseRequested;

    [ObservableProperty]
    public partial ModelOptionViewModel? Selected { get; private set; }

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    public partial ModelParameterViewModel? ExpandedParameter { get; private set; }

    [ObservableProperty]
    public partial string ThinkingLevel { get; private set; } = "Off";

    [ObservableProperty]
    public partial int ContextWindowValue { get; private set; }

    [ObservableProperty]
    public partial bool IsFastMode { get; private set; }

    /// <summary>芯片上的参数摘要，例如 “High · 200K · Off”。</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    public bool IsEmpty => VisibleModels.Count == 0;
    public bool IsFavoritesEmpty => _activeTab is { IsFavorites: true } && VisibleModels.Count == 0 && Search.Length == 0;

    public SessionOptionsDto Options => new(ThinkingLevel, IsFastMode, ContextWindowValue);

    partial void OnSearchChanged(string value) => RefreshList();

    /// <summary>弹层关闭后复位搜索与子菜单。</summary>
    public void OnClosed()
    {
        Search = "";
        CollapseParameter();
        ActivateTab(Tabs.FirstOrDefault(t => t.ProviderId == Selected?.Model.Provider) ?? _activeTab);
    }

    /// <summary>
    /// 加载（或在 settings.json 变化后重新加载）模型列表。已选模型仍存在时保持选择，否则依次退回
    /// 上次使用的模型、settings.json 的默认模型、第一个模型。
    /// </summary>
    /// <returns>选中的模型是否因列表变化而改变。</returns>
    public bool Load(IReadOnlyList<ModelDto> models, string? defaultModelRef = null)
    {
        var previous = Selected?.Model.Ref;
        _models.Clear();
        var favorites = _settings.Current.FavoriteModels.ToHashSet();
        _models.AddRange(models.Select(m => new ModelOptionViewModel(m) { IsFavorite = favorites.Contains(m.Ref) }));

        var activeProvider = _activeTab?.ProviderId;
        Tabs.Clear();
        Tabs.Add(new ModelTabViewModel(null, null, Strings.Get(TKCodeLangResourceKind.Favorites)));
        // 内置厂商在前；外部运行时（claude-code / codex / acp:*）组成 Agents 分组排在后面
        var first = true;
        foreach (var group in models.GroupBy(m => m.Provider).OrderBy(g => g.First().Runtime is null ? 0 : 1))
        {
            var isRuntime = group.First().Runtime is not null;
            Tabs.Add(new ModelTabViewModel(group.Key, ModelIcons.ForProvider(group.Key), group.First().ProviderName, isRuntime)
            {
                StartsRuntimeGroup = isRuntime && first,
            });
            first &= !isRuntime;
        }
        ApplyScopeToTabs();

        var selected = Find(previous) ?? Find(_settings.Current.LastModelId) ?? Find(defaultModelRef) ?? _models.FirstOrDefault();
        if (selected is not null)
        {
            ApplySelection(selected, new SessionOptionsDto(previous is null ? _settings.Current.LastThinkingLevel : ThinkingLevel,
                previous is null ? _settings.Current.LastFastMode : IsFastMode,
                previous is null ? _settings.Current.LastContextWindow : ContextWindowValue));
        }
        else
        {
            Selected = null;
            RefreshParameters();
        }
        ActivateTab(Tabs.FirstOrDefault(t => t.ProviderId == (activeProvider ?? selected?.Model.Provider)) ?? Tabs.FirstOrDefault());
        return previous is not null && previous != Selected?.Model.Ref;
    }

    /// <summary>
    /// 当前会话的运行时范围：null 表示新会话（全部模型可选）；"" 表示内置会话（只能选内置模型）；
    /// 其它值表示外部运行时会话（只能选同一运行时的模型）。Host 会拒绝跨运行时切换。
    /// </summary>
    public string? Scope { get; private set; }

    public void SetScope(string? scope)
    {
        if (Scope == scope)
        {
            return;
        }
        Scope = scope;
        ApplyScopeToTabs();
        RefreshList();
    }

    private bool InScope(ModelDto model) => Scope is null || (model.Runtime ?? "") == Scope;

    private void ApplyScopeToTabs()
    {
        foreach (var tab in Tabs.Where(t => t.ProviderId is not null))
        {
            var models = _models.Where(m => m.Model.Provider == tab.ProviderId).ToList();
            tab.IsAvailable = models.Count == 0 || models.Any(m => InScope(m.Model));
        }
        if (_activeTab is { IsAvailable: false })
        {
            ActivateTab(Tabs.FirstOrDefault(t => t.IsAvailable && t.ProviderId == Selected?.Model.Provider) ?? Tabs.FirstOrDefault());
        }
    }

    private ModelOptionViewModel? Find(string? modelRef) =>
        modelRef is null ? null : _models.FirstOrDefault(m => m.Model.Ref == modelRef) ?? _models.FirstOrDefault(m => m.Model.Id == modelRef);

    /// <summary>以 Host 返回的生效参数为准刷新显示（不触发 <see cref="SelectionChanged"/>）。</summary>
    public void ApplyHostOptions(SessionOptionsDto options)
    {
        ThinkingLevel = options.ThinkingLevel ?? ThinkingLevel;
        IsFastMode = options.FastMode ?? IsFastMode;
        ContextWindowValue = options.ContextWindow ?? ContextWindowValue;
        RefreshParameters();
    }

    /// <summary>切换到已有会话时显示它的模型与参数（不触发 <see cref="SelectionChanged"/>）。</summary>
    public void ShowSession(string modelRef, SessionOptionsDto options)
    {
        if (Find(modelRef) is not { } model)
        {
            return;
        }
        foreach (var m in _models)
        {
            m.IsSelected = m == model;
        }
        Selected = model;
        ApplyHostOptions(options);
    }

    [RelayCommand]
    private void ActivateTab(ModelTabViewModel? tab)
    {
        foreach (var t in Tabs)
        {
            t.IsActive = t == tab;
        }
        _activeTab = tab;
        Search = "";
        RefreshList();
    }

    [RelayCommand]
    private void Select(ModelOptionViewModel model)
    {
        ApplySelection(model, Options);
        CloseRequested?.Invoke();
        Persist();
        SelectionChanged?.Invoke();
    }

    /// <summary>按引用（厂商/模型）选择模型，与在列表中点选相同；找不到时返回 false（role 指定的模型不可用）。</summary>
    public bool SelectByRef(string modelRef)
    {
        if (Find(modelRef) is not { } model)
        {
            return false;
        }
        if (Selected?.Model.Ref != model.Model.Ref)
        {
            Select(model);
        }
        return true;
    }

    /// <summary>⌘1…⌘9：选择当前列表中的第 n 项。</summary>
    public bool SelectByShortcut(int number)
    {
        if (number < 1 || number > VisibleModels.Count || number > 9)
        {
            return false;
        }
        Select(VisibleModels[number - 1]);
        return true;
    }

    [RelayCommand]
    private void ToggleFavorite(ModelOptionViewModel model)
    {
        model.IsFavorite = !model.IsFavorite;
        var favorites = _settings.Current.FavoriteModels;
        favorites.Remove(model.Model.Ref);
        if (model.IsFavorite)
        {
            favorites.Add(model.Model.Ref);
        }
        _settings.Save();
        if (_activeTab is { IsFavorites: true })
        {
            RefreshList();
        }
    }

    [RelayCommand]
    private void ToggleParameter(ModelParameterViewModel parameter)
    {
        if (!parameter.IsEnabled)
        {
            return;
        }
        var expand = ExpandedParameter != parameter;
        CollapseParameter();
        if (expand)
        {
            parameter.IsExpanded = true;
            ExpandedParameter = parameter;
        }
    }

    public void CollapseParameter()
    {
        foreach (var p in Parameters)
        {
            p.IsExpanded = false;
        }
        ExpandedParameter = null;
    }

    [RelayCommand]
    private void ChooseParameter(ModelParameterChoiceViewModel choice)
    {
        switch (ExpandedParameter?.Kind)
        {
            case ModelParameterKind.Reasoning:
                ThinkingLevel = (string)choice.Value;
                break;
            case ModelParameterKind.ContextWindow:
                ContextWindowValue = (int)choice.Value;
                break;
            case ModelParameterKind.FastMode:
                IsFastMode = (bool)choice.Value;
                break;
            default:
                return;
        }
        CollapseParameter();
        RefreshParameters();
        Persist();
        SelectionChanged?.Invoke();
    }

    private void ApplySelection(ModelOptionViewModel model, SessionOptionsDto wanted)
    {
        foreach (var m in _models)
        {
            m.IsSelected = m == model;
        }
        Selected = model;
        var dto = model.Model;
        ThinkingLevel = Nearest(dto.ThinkingLevels, wanted.ThinkingLevel ?? dto.DefaultThinkingLevel) ?? dto.DefaultThinkingLevel;
        ContextWindowValue = dto.ContextWindows.FirstOrDefault(w => w >= (wanted.ContextWindow ?? 0), dto.ContextWindows[^1]);
        IsFastMode = (wanted.FastMode ?? false) && dto.SupportsFastMode;
        RefreshParameters();
    }

    private void Persist()
    {
        _settings.Current.LastModelId = Selected?.Model.Ref;
        _settings.Current.LastThinkingLevel = ThinkingLevel;
        _settings.Current.LastFastMode = IsFastMode;
        _settings.Current.LastContextWindow = ContextWindowValue;
        _settings.Save();
    }

    private void RefreshList()
    {
        IEnumerable<ModelOptionViewModel> source = (Search.Length > 0
            ? _models.Where(m => m.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
                                 m.Model.Id.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
                                 m.Model.Provider.Contains(Search, StringComparison.OrdinalIgnoreCase))
            : _activeTab is { IsFavorites: true } ? _models.Where(m => m.IsFavorite)
            : _models.Where(m => m.Model.Provider == _activeTab?.ProviderId)).Where(m => InScope(m.Model));
        VisibleModels.Clear();
        var index = 0;
        foreach (var m in source)
        {
            index++;
            m.Shortcut = index <= 9 ? $"{ShortcutPrefix}{index}" : null;
            VisibleModels.Add(m);
        }
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsFavoritesEmpty));
    }

    private void RefreshParameters()
    {
        var dto = Selected?.Model;
        Reasoning.Title = Strings.Get(TKCodeLangResourceKind.Reasoning);
        ContextWindow.Title = Strings.Get(TKCodeLangResourceKind.ContextWindow);
        FastMode.Title = Strings.Get(TKCodeLangResourceKind.FastMode);

        Reasoning.IsEnabled = dto is { ThinkingLevels.Count: > 1 };
        Reasoning.ValueLabel = dto is { ThinkingLevels.Count: > 0 } ? ThinkingLabel(ThinkingLevel) : Strings.Get(TKCodeLangResourceKind.ThinkingOff);
        Fill(Reasoning, dto?.ThinkingLevels.Select(l => ((object)l, ThinkingLabel(l), l == dto.DefaultThinkingLevel, l == ThinkingLevel)));

        ContextWindow.IsEnabled = dto is { ContextWindows.Count: > 1 };
        ContextWindow.ValueLabel = FormatTokens(ContextWindowValue);
        Fill(ContextWindow, dto?.ContextWindows.Select((w, i) => ((object)w, FormatTokens(w), i == 0, w == ContextWindowValue)));

        FastMode.IsEnabled = dto?.SupportsFastMode == true;
        FastMode.ValueLabel = Strings.Get(IsFastMode ? TKCodeLangResourceKind.On : TKCodeLangResourceKind.Off);
        Fill(FastMode, dto?.SupportsFastMode == true
            ? [(false, Strings.Get(TKCodeLangResourceKind.Off), true, !IsFastMode), (true, Strings.Get(TKCodeLangResourceKind.On), false, IsFastMode)]
            : null);

        Summary = string.Join(" · ", Reasoning.ValueLabel, ContextWindow.ValueLabel, FastMode.ValueLabel);
    }

    private static void Fill(ModelParameterViewModel parameter, IEnumerable<(object Value, string Label, bool IsDefault, bool IsSelected)>? choices)
    {
        parameter.Choices.Clear();
        foreach (var (value, label, isDefault, isSelected) in choices ?? [])
        {
            parameter.Choices.Add(new ModelParameterChoiceViewModel(value, label, isDefault) { IsSelected = isSelected });
        }
    }

    /// <summary>与 Host 的修正方向一致：不可用时先向上、再向下取最近的档位。</summary>
    private static string? Nearest(IReadOnlyList<string> available, string wanted)
    {
        if (available.Count == 0 || available.Contains(wanted))
        {
            return available.Count == 0 ? null : wanted;
        }
        var order = Enum.GetNames<ThinkingLevelOrder>();
        var index = Array.IndexOf(order, wanted);
        return available.FirstOrDefault(l => Array.IndexOf(order, l) > index) ?? available.LastOrDefault(l => Array.IndexOf(order, l) < index) ?? available[0];
    }

    public static string ThinkingLabel(string level) => Strings.Get(level switch
    {
        "Off" => TKCodeLangResourceKind.ThinkingOff,
        "Minimal" => TKCodeLangResourceKind.ThinkingMinimal,
        "Low" => TKCodeLangResourceKind.ThinkingLow,
        "Medium" => TKCodeLangResourceKind.ThinkingMedium,
        "High" => TKCodeLangResourceKind.ThinkingHigh,
        "XHigh" => TKCodeLangResourceKind.ThinkingXHigh,
        _ => TKCodeLangResourceKind.ThinkingMax,
    });

    public static string FormatTokens(int tokens) =>
        tokens >= 1_000_000 && tokens % 1_000_000 == 0 ? $"{tokens / 1_000_000}M"
        : tokens >= 1_000 ? $"{tokens / 1_000}K"
        : tokens.ToString();

    private static string ShortcutPrefix => OperatingSystem.IsMacOS() ? "⌘" : "Ctrl+";

    /// <summary>与 TKCode.Ai.Models.ThinkingLevel 的顺序一致（Desktop 不引用 TKCode.Ai）。</summary>
    private enum ThinkingLevelOrder
    {
        Off, Minimal, Low, Medium, High, XHigh, Max,
    }
}
