using System.Text.RegularExpressions;
using TCode.Agent.Coding.Workspace;
using TCode.Config;

namespace TCode.Agent.Coding.Assets;

public enum AssetKind
{
    Command,
    Skill,
    Role,
}

public enum AssetSource
{
    Global,
    Project,
}

/// <summary>
/// 一个 command / skill / role（docs/design/composer.md §4）。只含元数据，正文在用到时读取（<see cref="AgentAssetStore.ReadBody"/>）。
/// </summary>
/// <param name="Name">名称：command 为文件相对路径去掉 .md、子目录以 : 连接；skill 为目录名；role 为文件名。</param>
/// <param name="Path">定义文件（command / role 的 .md，skill 的 SKILL.md）。</param>
/// <param name="DisplayName">role 的显示名（frontmatter 的 name），没有时与 Name 相同。</param>
/// <param name="Tools">role 的工具：all / readonly，或工具名列表。</param>
/// <param name="Model">role 指定的模型引用（厂商/模型）。</param>
/// <param name="Icon">role 的 Lucide 图标名。</param>
public sealed record AgentAsset(
    AssetKind Kind,
    string Name,
    string? Description,
    AssetSource Source,
    string Path,
    string? DisplayName = null,
    IReadOnlyList<string>? Tools = null,
    string? Model = null,
    string? Icon = null);

/// <summary>被覆盖或被遮住的项：设置页提示用。</summary>
public sealed record AssetShadow(AgentAsset Hidden, AgentAsset By);

public sealed record AssetProblem(string Path, string Message);

/// <summary>一个项目根目录下生效的 command / skill / role。</summary>
public sealed record AgentAssetCatalog(
    IReadOnlyList<AgentAsset> Commands,
    IReadOnlyList<AgentAsset> Skills,
    IReadOnlyList<AgentAsset> Roles,
    IReadOnlyList<AssetShadow> Shadowed,
    IReadOnlyList<AssetProblem> Problems)
{
    public static readonly AgentAssetCatalog Empty = new([], [], [], [], []);

    public AgentAsset? FindCommand(string name) => Commands.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public AgentAsset? FindSkill(string name) => Skills.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public AgentAsset? FindRole(string name) => Roles.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 扫描全局目录（~/.tcode/{commands,skills,roles}）与项目目录（&lt;项目根&gt;/.agents/…），按 §4 的规则合并：
/// 项目覆盖全局同名项；同名的 command 与 skill 以 skill 为准；名称不合法、文件过大、缺少必填字段的项记为问题。
/// </summary>
public static partial class AgentAssetLoader
{
    public const string ProjectDirectory = ".agents";
    public const long MaxFileBytes = 256 * 1024;
    public const int MaxPerDirectory = 500;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex SegmentPattern();

    /// <summary>项目根：含 .git 的上级目录，找不到时为 cwd。</summary>
    public static string ProjectRoot(string cwd) => GitInfo.Find(cwd)?.Root ?? Path.GetFullPath(cwd);

    public static AgentAssetCatalog Load(string globalHome, string? projectRoot)
    {
        var problems = new List<AssetProblem>();
        var global = Scan(globalHome, AssetSource.Global, problems);
        var project = projectRoot is null ? new Scanned([], [], []) : Scan(Path.Combine(projectRoot, ProjectDirectory), AssetSource.Project, problems);
        var shadowed = new List<AssetShadow>();
        var commands = Merge(global.Commands, project.Commands, shadowed);
        var skills = Merge(global.Skills, project.Skills, shadowed);
        var roles = Merge(global.Roles, project.Roles, shadowed);
        // command 与 skill 同名：skill 优先（它还能被模型自动调用）
        var skillNames = skills.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var command in commands.Where(c => skillNames.Contains(c.Name)).ToList())
        {
            commands.Remove(command);
            shadowed.Add(new AssetShadow(command, skills.First(s => s.Name.Equals(command.Name, StringComparison.OrdinalIgnoreCase))));
        }
        return new AgentAssetCatalog(Sorted(commands), Sorted(skills), Sorted(roles), shadowed, problems);
    }

    private static List<AgentAsset> Sorted(List<AgentAsset> assets) => assets.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();

    private static List<AgentAsset> Merge(List<AgentAsset> global, List<AgentAsset> project, List<AssetShadow> shadowed)
    {
        var result = project.ToList();
        var names = project.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in global)
        {
            if (names.Contains(asset.Name))
            {
                shadowed.Add(new AssetShadow(asset, project.First(p => p.Name.Equals(asset.Name, StringComparison.OrdinalIgnoreCase))));
            }
            else
            {
                result.Add(asset);
            }
        }
        return result;
    }

    private sealed record Scanned(List<AgentAsset> Commands, List<AgentAsset> Skills, List<AgentAsset> Roles);

    private static Scanned Scan(string baseDir, AssetSource source, List<AssetProblem> problems) => new(
        ScanCommands(Path.Combine(baseDir, "commands"), source, problems),
        ScanSkills(Path.Combine(baseDir, "skills"), source, problems),
        ScanRoles(Path.Combine(baseDir, "roles"), source, problems));

    private static List<AgentAsset> ScanCommands(string dir, AssetSource source, List<AssetProblem> problems)
    {
        var result = new List<AgentAsset>();
        if (!Directory.Exists(dir))
        {
            return result;
        }
        foreach (var file in SafeFiles(dir, "*.md", SearchOption.AllDirectories, problems))
        {
            var relative = Path.GetRelativePath(dir, file).Replace('\\', '/');
            var segments = relative[..^3].Split('/');
            if (!segments.All(s => SegmentPattern().IsMatch(s)))
            {
                problems.Add(new AssetProblem(file, $"Invalid command name '{relative[..^3]}': use letters, digits, '.', '-' and '_'."));
                continue;
            }
            if (Read(file, problems) is not { } frontmatter)
            {
                continue;
            }
            var description = frontmatter.GetString("description") is { Length: > 0 } d ? d : FirstLine(frontmatter.Body);
            result.Add(new AgentAsset(AssetKind.Command, string.Join(':', segments), description, source, file));
        }
        return result;
    }

    private static List<AgentAsset> ScanSkills(string dir, AssetSource source, List<AssetProblem> problems)
    {
        var result = new List<AgentAsset>();
        if (!Directory.Exists(dir))
        {
            return result;
        }
        foreach (var skillDir in SafeDirectories(dir, problems))
        {
            var name = Path.GetFileName(skillDir);
            var file = Path.Combine(skillDir, "SKILL.md");
            if (!File.Exists(file))
            {
                continue;
            }
            if (!SegmentPattern().IsMatch(name))
            {
                problems.Add(new AssetProblem(file, $"Invalid skill name '{name}': use letters, digits, '.', '-' and '_'."));
                continue;
            }
            if (Read(file, problems) is not { } frontmatter)
            {
                continue;
            }
            if (frontmatter.GetString("name") is { Length: > 0 } declared && !declared.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(new AssetProblem(file, $"Skill name '{declared}' does not match its directory '{name}'; using '{name}'."));
            }
            var description = frontmatter.GetString("description");
            if (string.IsNullOrWhiteSpace(description))
            {
                problems.Add(new AssetProblem(file, "Skill has no description; the model will not discover it automatically."));
            }
            result.Add(new AgentAsset(AssetKind.Skill, name, string.IsNullOrWhiteSpace(description) ? null : description, source, file));
        }
        return result;
    }

    private static List<AgentAsset> ScanRoles(string dir, AssetSource source, List<AssetProblem> problems)
    {
        var result = new List<AgentAsset>();
        if (!Directory.Exists(dir))
        {
            return result;
        }
        foreach (var file in SafeFiles(dir, "*.md", SearchOption.TopDirectoryOnly, problems))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!SegmentPattern().IsMatch(name))
            {
                problems.Add(new AssetProblem(file, $"Invalid role name '{name}': use letters, digits, '.', '-' and '_'."));
                continue;
            }
            if (Read(file, problems) is not { } frontmatter)
            {
                continue;
            }
            var tools = frontmatter.GetList("tools") is { Count: > 0 } list ? list : ["all"];
            result.Add(new AgentAsset(AssetKind.Role, name, frontmatter.GetString("description"), source, file,
                DisplayName: frontmatter.GetString("name") is { Length: > 0 } display ? display : name,
                Tools: tools,
                Model: frontmatter.GetString("model") is { Length: > 0 } model ? model : null,
                Icon: frontmatter.GetString("icon") is { Length: > 0 } icon ? icon : null));
        }
        return result;
    }

    private static Frontmatter? Read(string file, List<AssetProblem> problems)
    {
        try
        {
            if (new FileInfo(file).Length > MaxFileBytes)
            {
                problems.Add(new AssetProblem(file, $"File is larger than {MaxFileBytes / 1024} KB and was skipped."));
                return null;
            }
            var frontmatter = Frontmatter.Parse(File.ReadAllText(file));
            foreach (var warning in frontmatter.Warnings)
            {
                problems.Add(new AssetProblem(file, warning));
            }
            return frontmatter;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add(new AssetProblem(file, ex.Message));
            return null;
        }
    }

    private static IEnumerable<string> SafeFiles(string dir, string pattern, SearchOption option, List<AssetProblem> problems)
    {
        try
        {
            var files = Directory.EnumerateFiles(dir, pattern, option).Order(StringComparer.Ordinal).ToList();
            if (files.Count > MaxPerDirectory)
            {
                problems.Add(new AssetProblem(dir, $"More than {MaxPerDirectory} files; only the first {MaxPerDirectory} are used."));
            }
            return files.Take(MaxPerDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add(new AssetProblem(dir, ex.Message));
            return [];
        }
    }

    private static IEnumerable<string> SafeDirectories(string dir, List<AssetProblem> problems)
    {
        try
        {
            var dirs = Directory.EnumerateDirectories(dir).Order(StringComparer.Ordinal).ToList();
            if (dirs.Count > MaxPerDirectory)
            {
                problems.Add(new AssetProblem(dir, $"More than {MaxPerDirectory} skills; only the first {MaxPerDirectory} are used."));
            }
            return dirs.Take(MaxPerDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add(new AssetProblem(dir, ex.Message));
            return [];
        }
    }

    /// <summary>正文第一行非空文字（去掉标题的 #）。</summary>
    private static string? FirstLine(string body) =>
        body.Split('\n').Select(l => l.Trim().TrimStart('#').Trim()).FirstOrDefault(l => l.Length > 0);
}
