using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using TKCode.Desktop.Controls.Composer;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TKCode.Desktop.Localization;
using TKCode.Desktop.Services;
using TKCode.Rpc;

namespace TKCode.Desktop.ViewModels;

/// <summary>需要用户处理的 MCP 服务器（需要登录 / 项目服务器待信任），显示在 Composer 上方。</summary>
public sealed class McpAttentionViewModel(McpServerDto server, string? cwd, McpComposerViewModel owner)
{
    public McpServerDto Server { get; } = server;
    public bool NeedsAuth => Server.State == "needsAuth";
    public bool Untrusted => Server.Availability == "untrusted";
    public string Text => NeedsAuth
        ? Strings.Format(TKCodeLangResourceKind.McpAttentionNeedsAuthFormat, Server.Title)
        : Strings.Format(TKCodeLangResourceKind.McpAttentionUntrustedFormat, Server.Title, Server.Summary);
    internal string? Cwd { get; } = cwd;
    public McpComposerViewModel Owner { get; } = owner;
}

/// <summary>Composer 中已选的资源（发送时由 Host 读取内容）。</summary>
public sealed partial class McpPromptArgumentViewModel(McpPromptArgumentDto argument) : ObservableObject
{
    public McpPromptArgumentDto Argument { get; } = argument;
    public string Label => Argument.Name + (Argument.Required ? " *" : "");
    public string? Description => Argument.Description;

    [ObservableProperty]
    public partial string Value { get; set; } = "";
}

/// <summary>
/// Composer 的 MCP 引用（docs/design/mcp.md §8.1、§8.2）：需要登录 / 信任的服务器提示，选中的提示词在输入框上方展开参数。
/// 资源与提示词的选择由统一补全完成（<see cref="Completion.McpResourceProvider"/>、<see cref="Completion.McpPromptProvider"/>），
/// 资源以 tag 留在输入框里；发送时把引用交给 Host 展开。
/// </summary>
public sealed partial class McpComposerViewModel : ObservableObject
{
    private static readonly TimeSpan CatalogTtl = TimeSpan.FromSeconds(30);

    private readonly AgentHostConnection _host;
    private readonly Func<string?> _cwd;
    private readonly ILogger _logger;
    private McpCatalogDto? _catalog;
    private string? _catalogCwd;
    private DateTimeOffset _catalogAt;
    private Task<McpCatalogDto>? _loading;

    public McpComposerViewModel(AgentHostConnection host, Func<string?> cwd, ILogger logger)
    {
        _host = host;
        _cwd = cwd;
        _logger = logger;
        host.McpChanged += notification =>
        {
            _catalog = null;
            _ = RefreshAttentionAsync();
        };
    }

    // ---- 需要处理的服务器（docs/design/mcp.md §11 会话提示） ----

    public ObservableCollection<McpAttentionViewModel> Attention { get; } = [];
    public bool HasAttention => Attention.Count > 0;
    private readonly HashSet<string> _dismissed = new(StringComparer.Ordinal);

    public async Task RefreshAttentionAsync()
    {
        var cwd = _cwd();
        IReadOnlyList<McpServerDto> servers;
        try
        {
            var server = await _host.GetServerAsync();
            servers = (await server.ListMcpServersAsync(cwd)).Servers;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Listing MCP servers for the composer failed");
            return;
        }
        Attention.Clear();
        foreach (var s in servers.Where(s => s.State == "needsAuth" || s.Availability == "untrusted"))
        {
            if (!_dismissed.Contains(DismissKey(s)))
            {
                Attention.Add(new McpAttentionViewModel(s, cwd, this));
            }
        }
        OnPropertyChanged(nameof(HasAttention));
    }

    private static string DismissKey(McpServerDto s) => $"{s.Source}:{s.Key}:{s.State}:{s.Availability}:{s.ConfigJson}";

    [RelayCommand]
    private async Task LoginAsync(McpAttentionViewModel item)
    {
        try
        {
            var server = await _host.GetServerAsync();
            await server.RestartMcpServerAsync(item.Server.Key, item.Cwd, login: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP login failed");
        }
    }

    [RelayCommand]
    private Task TrustAsync(McpAttentionViewModel item) => SetTrustAsync(item, true);

    [RelayCommand]
    private Task IgnoreAsync(McpAttentionViewModel item) => SetTrustAsync(item, false);

    private async Task SetTrustAsync(McpAttentionViewModel item, bool trusted)
    {
        if (item.Cwd is not { } cwd)
        {
            return;
        }
        try
        {
            var server = await _host.GetServerAsync();
            await server.TrustMcpServerAsync(item.Server.Key, cwd, trusted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP trust failed");
        }
        await RefreshAttentionAsync();
    }

    [RelayCommand]
    private void Dismiss(McpAttentionViewModel item)
    {
        _dismissed.Add(DismissKey(item.Server));
        Attention.Remove(item);
        OnPropertyChanged(nameof(HasAttention));
    }

    // ---- 资源与提示词目录（补全使用，缓存 30 秒，服务器变化时失效） ----

    public Task<McpCatalogDto> GetCatalogAsync()
    {
        var cwd = _cwd();
        if (_catalog is not null && _catalogCwd == cwd && DateTimeOffset.UtcNow - _catalogAt <= CatalogTtl)
        {
            return Task.FromResult(_catalog);
        }
        return _loading ??= LoadAsync(cwd);
    }

    private async Task<McpCatalogDto> LoadAsync(string? cwd)
    {
        try
        {
            var server = await _host.GetServerAsync();
            _catalog = await server.GetMcpCatalogAsync(cwd);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Loading MCP catalog failed");
            _catalog = new McpCatalogDto([], []);
        }
        finally
        {
            _loading = null;
        }
        _catalogCwd = cwd;
        _catalogAt = DateTimeOffset.UtcNow;
        return _catalog;
    }

    // ---- 选中的提示词 ----

    public ObservableCollection<McpPromptArgumentViewModel> PromptArguments { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrompt), nameof(PromptTitle), nameof(HasPromptArguments))]
    public partial McpPromptDto? Prompt { get; private set; }

    public bool HasPrompt => Prompt is not null;
    public bool HasPromptArguments => PromptArguments.Count > 0;
    public string? PromptTitle => Prompt is { } p ? $"/{p.Server}:{p.Name}" + (p.Title is { Length: > 0 } t ? $" — {t}" : "") : null;

    /// <summary>选中一个提示词：在输入框上方展开它的参数。</summary>
    public void SelectPrompt(McpPromptDto prompt)
    {
        PromptArguments.Clear();
        foreach (var argument in prompt.Arguments)
        {
            var item = new McpPromptArgumentViewModel(argument);
            item.PropertyChanged += (_, _) =>
            {
                if (!IsRestoring)
                {
                    ArgumentEdited?.Invoke();
                }
            };
            PromptArguments.Add(item);
        }
        Prompt = prompt;
    }

    [RelayCommand]
    private void ClearPrompt()
    {
        Prompt = null;
        PromptArguments.Clear();
    }

    /// <summary>资源 tag 的值：服务器与 URI 以第一个冒号分隔。</summary>
    public static string ResourceTagValue(McpResourceDto resource) => $"{resource.Server}:{resource.Uri}";

    /// <summary>输入框中的资源 tag 对应的引用。</summary>
    public static IReadOnlyList<McpResourceRefDto> ResourcesIn(IEnumerable<Segment> segments) =>
        segments.OfType<TagSegment>().Where(t => t.Kind == TagKind.McpResource && t.Value.Contains(':'))
            .Select(t => new McpResourceRefDto(t.Value[..t.Value.IndexOf(':')], t.Value[(t.Value.IndexOf(':') + 1)..]))
            .Distinct().ToList();

    /// <summary>发送：取出资源引用（来自输入框的 tag）与提示词并清空提示词；必填参数缺失时返回错误且不清空。</summary>
    public (IReadOnlyList<McpResourceRefDto>? Resources, McpPromptRefDto? Prompt, string? Error) Take(IEnumerable<Segment> segments)
    {
        McpPromptRefDto? prompt = null;
        if (Prompt is { } p)
        {
            if (PromptArguments.FirstOrDefault(a => a.Argument.Required && string.IsNullOrWhiteSpace(a.Value)) is { } missing)
            {
                return (null, null, Strings.Format(TKCodeLangResourceKind.McpPromptArgumentMissingFormat, missing.Argument.Name));
            }
            prompt = new McpPromptRefDto(p.Server, p.Name,
                PromptArguments.Where(a => a.Value.Length > 0).ToDictionary(a => a.Argument.Name, a => a.Value));
        }
        var resources = ResourcesIn(segments);
        ClearPrompt();
        return (resources.Count == 0 ? null : resources, prompt, null);
    }

    // ---- 草稿（docs/design/composer-drafts.md §4） ----

    private int _applyVersion;

    /// <summary>选中的提示词与已填的参数值；没有选中时为 null。</summary>
    public McpPromptSnapshot? Capture() => Prompt is { } p
        ? new McpPromptSnapshot(p.Server, p.Name, p,
            PromptArguments.Where(a => a.Value.Length > 0).ToDictionary(a => a.Argument.Name, a => a.Value))
        : null;

    /// <summary>
    /// 恢复草稿里的提示词（null 为清空）。快照里只有名字时（从磁盘读出）等 MCP 目录加载后再解析；
    /// 目录里找不到、或期间已经换了草稿 / 用户选了别的提示词，就放弃。
    /// </summary>
    public void Apply(McpPromptSnapshot? snapshot)
    {
        var version = ++_applyVersion;
        ClearPrompt();
        if (snapshot is null)
        {
            return;
        }
        if (snapshot.Prompt is { } prompt)
        {
            Restore(prompt, snapshot.Arguments);
            return;
        }
        _ = ResolveAsync(snapshot, version);
    }

    private async Task ResolveAsync(McpPromptSnapshot snapshot, int version)
    {
        try
        {
            var catalog = await GetCatalogAsync();
            if (version != _applyVersion || Prompt is not null)
            {
                return;
            }
            if (catalog.Prompts.FirstOrDefault(p => p.Server == snapshot.Server && p.Name == snapshot.Name) is { } prompt)
            {
                Restore(prompt, snapshot.Arguments);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Restoring the MCP prompt of a draft failed");
        }
    }

    private void Restore(McpPromptDto prompt, IReadOnlyDictionary<string, string> arguments)
    {
        IsRestoring = true;
        try
        {
            SelectPrompt(prompt);
            foreach (var argument in PromptArguments)
            {
                if (arguments.TryGetValue(argument.Argument.Name, out var value))
                {
                    argument.Value = value;
                }
            }
        }
        finally
        {
            IsRestoring = false;
        }
    }

    /// <summary>正在从草稿恢复提示词：它引起的变化不算用户编辑（否则草稿的保留时间会被一次次刷新）。</summary>
    public bool IsRestoring { get; private set; }

    /// <summary>用户修改了提示词参数的值。</summary>
    public event Action? ArgumentEdited;
}

/// <summary>
/// 用户消息中由 Host 附上的资源内容，在界面上折叠为 @服务器:URI。
/// 正文里已经有这个引用（输入框中的资源 tag）时整块去掉，不再重复显示。
/// </summary>
public static partial class McpMentions
{
    [GeneratedRegex("""\s*<mcp_resource server="([^"]*)" uri="([^"]*)">.*?</mcp_resource>""", RegexOptions.Singleline)]
    private static partial Regex ResourcePattern();

    public static string Collapse(string text)
    {
        if (!text.Contains("<mcp_resource ", StringComparison.Ordinal))
        {
            return text;
        }
        var body = ResourcePattern().Replace(text, "");
        return ResourcePattern().Replace(text, m =>
        {
            var mention = $"@{m.Groups[1].Value}:{m.Groups[2].Value}";
            return body.Contains(mention, StringComparison.Ordinal) ? "" : " " + mention;
        }).Trim();
    }
}
