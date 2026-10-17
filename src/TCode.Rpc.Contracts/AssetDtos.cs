namespace TCode.Rpc;

/// <summary>command / skill / role（docs/design/composer.md §4），只含元数据。</summary>
/// <param name="Kind">command / skill / role。</param>
/// <param name="Source">global / project。</param>
/// <param name="Tools">role 的工具：all / readonly，或工具名列表。</param>
public sealed record AgentAssetDto(
    string Kind,
    string Name,
    string? Description,
    string Source,
    string Path,
    string? DisplayName = null,
    IReadOnlyList<string>? Tools = null,
    string? Model = null,
    string? Icon = null);

/// <summary>被覆盖或被遮住的项（设置页提示）。</summary>
public sealed record AgentAssetShadowDto(AgentAssetDto Hidden, AgentAssetDto By);

public sealed record AgentAssetProblemDto(string Path, string Message);

/// <param name="Root">项目根（.agents 所在目录）；没有 cwd 时为 null。</param>
public sealed record AgentAssetsDto(
    string? Root,
    IReadOnlyList<AgentAssetDto> Commands,
    IReadOnlyList<AgentAssetDto> Skills,
    IReadOnlyList<AgentAssetDto> Roles,
    IReadOnlyList<AgentAssetShadowDto> Shadowed,
    IReadOnlyList<AgentAssetProblemDto> Problems);

public sealed record AssetsParams(string? Cwd);

/// <param name="Root">变化的项目根；null 表示全局目录变化（所有项目都受影响）。</param>
public sealed record AssetsChangedNotification(string? Root);
