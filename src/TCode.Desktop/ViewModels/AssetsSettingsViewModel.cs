using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TCode.Config;
using TCode.Desktop.Icons;
using TCode.Desktop.Localization;
using TCode.Desktop.Services;
using TCode.Rpc;

namespace TCode.Desktop.ViewModels;

/// <summary>设置页中的一个 command / skill / role。</summary>
public sealed class AssetItemViewModel(AgentAssetDto dto, AssetsSettingsViewModel owner)
{
    public AgentAssetDto Dto { get; } = dto;
    public AssetsSettingsViewModel Owner { get; } = owner;
    public string Title => Dto.Kind switch
    {
        "command" or "skill" => "/" + Dto.Name,
        _ => Dto.DisplayName is { } display && display != Dto.Name ? $"{display}  ({Dto.Name})" : Dto.Name,
    };
    public string? Description => Dto.Description;
    public bool HasDescription => !string.IsNullOrEmpty(Description);
    public string Source => Completion.AssetCatalogCache.SourceLabel(Dto.Source);
    public string Path => Dto.Path;

    /// <summary>role 的附加信息：工具与模型。</summary>
    public string? Detail => Dto.Kind == "role"
        ? string.Join(" · ", new[] { Dto.Tools is { Count: > 0 } tools ? "tools: " + string.Join(", ", tools) : null, Dto.Model }.OfType<string>())
        : null;

    public bool HasDetail => !string.IsNullOrEmpty(Detail);
}

/// <summary>被覆盖或被遮住的项、解析问题：一行文字加文件路径。</summary>
public sealed record AssetNoticeViewModel(string Text, string Path);

/// <summary>
/// 设置 › Commands、Skills 与模式（docs/design/composer.md §14）：列出当前项目生效的项与来源，打开所在文件，
/// 按模板新建（全局 ~/.tcode 或项目 .agents），查看被覆盖的项与解析问题；以及新会话的默认模式。
/// 内容由 Host 的 assets/list 提供，定义文件变化（assets/changed）时自动刷新。
/// </summary>
public sealed partial class AssetsSettingsViewModel : ObservableObject
{
    private readonly AgentHostConnection _host;
    private readonly UserSettingsService _settings;
    private readonly Func<string?> _cwd;
    private readonly ILogger _logger;

    public AssetsSettingsViewModel(AgentHostConnection host, UserSettingsService settings, Func<string?> cwd, ILogger logger)
    {
        _host = host;
        _settings = settings;
        _cwd = cwd;
        _logger = logger;
        host.AssetsChanged += changed => _ = RefreshAsync();
        settings.Changed += RefreshDefaultRole;
        RefreshDefaultRole();
    }

    public ObservableCollection<AssetItemViewModel> Commands { get; } = [];
    public ObservableCollection<AssetItemViewModel> Skills { get; } = [];
    public ObservableCollection<AssetItemViewModel> Roles { get; } = [];
    public ObservableCollection<AssetNoticeViewModel> Notices { get; } = [];
    public ObservableCollection<SettingsChoiceViewModel> DefaultRoles { get; } = [];

    public bool HasNoCommands => Commands.Count == 0;
    public bool HasNoSkills => Skills.Count == 0;
    public bool HasNoRoles => Roles.Count == 0;
    public bool HasNotices => Notices.Count > 0;

    /// <summary>项目根（.agents 所在目录）；不在项目中时为 null，只能新建全局项。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProject), nameof(ProjectDirectory))]
    public partial string? ProjectRoot { get; private set; }

    public bool HasProject => ProjectRoot is not null;
    public string GlobalDirectory => ConfigPaths.Home;
    public string? ProjectDirectory => ProjectRoot is null ? null : System.IO.Path.Combine(ProjectRoot, ".agents");

    public async Task RefreshAsync()
    {
        AgentAssetsDto assets;
        try
        {
            var server = await _host.GetServerAsync();
            assets = await server.ListAssetsAsync(_cwd());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Listing commands, skills and roles failed");
            return;
        }
        ProjectRoot = assets.Root;
        Fill(Commands, assets.Commands);
        Fill(Skills, assets.Skills);
        Fill(Roles, assets.Roles);
        Notices.Clear();
        foreach (var shadow in assets.Shadowed)
        {
            Notices.Add(new AssetNoticeViewModel(Strings.Format(TCodeLangResourceKind.AssetShadowedFormat,
                KindLabel(shadow.Hidden.Kind), shadow.Hidden.Name, AssetSourceLabel(shadow.Hidden.Source),
                KindLabel(shadow.By.Kind), AssetSourceLabel(shadow.By.Source)), shadow.Hidden.Path));
        }
        foreach (var problem in assets.Problems)
        {
            Notices.Add(new AssetNoticeViewModel(problem.Message, problem.Path));
        }
        OnPropertyChanged(nameof(HasNoCommands));
        OnPropertyChanged(nameof(HasNoSkills));
        OnPropertyChanged(nameof(HasNoRoles));
        OnPropertyChanged(nameof(HasNotices));
        RefreshDefaultRole();
    }

    private void Fill(ObservableCollection<AssetItemViewModel> target, IReadOnlyList<AgentAssetDto> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(new AssetItemViewModel(item, this));
        }
    }

    private static string KindLabel(string kind) => Strings.Get(kind switch
    {
        "skill" => TCodeLangResourceKind.AssetKindSkill,
        "role" => TCodeLangResourceKind.AssetKindRole,
        _ => TCodeLangResourceKind.AssetKindCommand,
    });

    private static string AssetSourceLabel(string source) => Completion.AssetCatalogCache.SourceLabel(source);

    // ---- 默认模式 ----

    private void RefreshDefaultRole()
    {
        var current = _settings.Current.DefaultRole ?? RolePickerViewModel.Agent;
        DefaultRoles.Clear();
        DefaultRoles.Add(new SettingsChoiceViewModel(RolePickerViewModel.Agent, Strings.Get(TCodeLangResourceKind.RoleAgent)));
        DefaultRoles.Add(new SettingsChoiceViewModel(RolePickerViewModel.Plan, Strings.Get(TCodeLangResourceKind.RolePlan)));
        DefaultRoles.Add(new SettingsChoiceViewModel(RolePickerViewModel.Acp, Strings.Get(TCodeLangResourceKind.RoleAcp)));
        foreach (var role in Roles)
        {
            DefaultRoles.Add(new SettingsChoiceViewModel(role.Dto.Name, role.Dto.DisplayName ?? role.Dto.Name));
        }
        // settings.json 里写的是当前项目没有的自定义 role：仍然列出，便于看到当前值
        if (DefaultRoles.All(r => !r.Value.Equals(current, StringComparison.OrdinalIgnoreCase)))
        {
            DefaultRoles.Add(new SettingsChoiceViewModel(current, current));
        }
        foreach (var choice in DefaultRoles)
        {
            choice.IsSelected = choice.Value.Equals(current, StringComparison.OrdinalIgnoreCase);
        }
        OnPropertyChanged(nameof(DefaultRoleLabel));
    }

    public string DefaultRoleLabel => DefaultRoles.FirstOrDefault(r => r.IsSelected)?.Label ?? Strings.Get(TCodeLangResourceKind.RoleAgent);

    [RelayCommand]
    private void ChooseDefaultRole(SettingsChoiceViewModel choice) => _settings.SetDefaultRole(choice.Value);

    // ---- 打开与新建 ----

    [RelayCommand]
    private void Open(AssetItemViewModel item) => ShellOpen.Open(item.Path, _logger);

    [RelayCommand]
    private void Reveal(AssetItemViewModel item) => ShellOpen.Open(System.IO.Path.GetDirectoryName(item.Path)!, _logger);

    [RelayCommand]
    private void OpenNotice(AssetNoticeViewModel notice) => ShellOpen.Open(notice.Path, _logger);

    /// <summary>新建：参数为 "command|global" 之类（种类|位置）。按模板写入一个不重名的文件后打开它。</summary>
    [RelayCommand]
    private void Create(string spec)
    {
        var parts = spec.Split('|');
        var kind = parts[0];
        var baseDir = parts.ElementAtOrDefault(1) == "project" && ProjectDirectory is { } project ? project : GlobalDirectory;
        try
        {
            var path = AssetTemplates.Write(baseDir, kind);
            ShellOpen.Open(path, _logger);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Creating a {Kind} failed", kind);
        }
    }
}

/// <summary>新建 command / skill / role 的模板（docs/design/composer.md §18.9）。</summary>
public static class AssetTemplates
{
    public const string Command = """
        ---
        description: One line shown in the / list. Say what the command does.
        ---
        Write the prompt here, as you would type it to the agent: the goal, the files or areas involved, and what to report back.
        Text typed after the command in the composer is added below this prompt.
        """;

    public const string Skill = """
        ---
        name: {name}
        description: What this skill does and when to use it. The model decides whether to load the skill from this sentence alone, so name the tasks, file types and keywords it applies to.
        ---
        # {name}

        ## When to use
        - The situations this skill is for, and the ones it is not for.

        ## Steps
        1. The first thing to do.
        2. The next thing to do.

        ## Notes
        - Conventions, pitfalls and checks to run before finishing.
        - Paths in this file are relative to this skill's directory; put scripts and references next to SKILL.md.
        """;

    public const string Role = """
        ---
        name: {name}
        description: One line shown in the mode picker.
        tools: all          # all | readonly | [read, grep, glob, ...]
        # model: provider/model   # switch to this model when the mode is chosen
        # icon: eye               # a Lucide icon name
        ---
        You are in {name} mode. Say in one or two sentences what this mode is for.

        Work in this order:
        1. The first step.
        2. The next step.

        Constraints: what the agent must not do in this mode. Keep this consistent with `tools` above.

        Finish with: what the final reply should contain.
        """;

    /// <summary>在 baseDir 下按种类写入模板（command → commands/new-command.md，skill → skills/new-skill/SKILL.md，role → roles/new-mode.md），已存在时加序号。</summary>
    public static string Write(string baseDir, string kind)
    {
        for (var n = 1; ; n++)
        {
            var suffix = n == 1 ? "" : "-" + n;
            var (name, path, template) = kind switch
            {
                "skill" => ($"new-skill{suffix}", Path.Combine(baseDir, "skills", $"new-skill{suffix}", "SKILL.md"), Skill),
                "role" => ($"new-mode{suffix}", Path.Combine(baseDir, "roles", $"new-mode{suffix}.md"), Role),
                _ => ($"new-command{suffix}", Path.Combine(baseDir, "commands", $"new-command{suffix}.md"), Command),
            };
            if (File.Exists(path) || kind == "skill" && Directory.Exists(Path.GetDirectoryName(path)))
            {
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, template.Replace("{name}", name, StringComparison.Ordinal) + "\n");
            return path;
        }
    }
}
