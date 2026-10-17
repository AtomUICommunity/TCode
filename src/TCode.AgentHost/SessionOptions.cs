using TCode.Agent.Coding;
using TCode.Agent.Coding.Assets;
using TCode.Ai.Models;
using TCode.Agent;
using TCode.Rpc;
using TCode.Runtimes;

namespace TCode.AgentHost;

/// <summary>模型能力 → RPC 描述，以及会话参数的校验与就近修正。</summary>
internal static class SessionOptions
{
    /// <summary>
    /// 默认上下文预算：模型窗口更大时，选择器提供“默认预算 / 模型最大窗口”两档，新会话用前者。
    /// 个别模型的默认预算由目录或 settings.json 的 defaultContextWindow 单独指定（<see cref="ModelInfo.DefaultContextWindow"/>）。
    /// </summary>
    public const int DefaultContextWindow = 272_000;

    /// <param name="supportsSubagents">运行时能派生子 Agent（ACP 协调模式依赖它）；内置 Agent 总是可以。</param>
    public static ModelDto ToDto(ModelInfo m, string providerName, string? runtime = null, bool supportsSubagents = true) => new(
        m.Id, m.Name, m.Provider, m.Api, m.Reasoning, m.Description,
        ThinkingLevels.Available(m).Select(l => l.ToString()).ToArray(),
        ThinkingLevels.Default(m).ToString(),
        ContextWindows(m),
        m.SupportsFastMode,
        ModelCatalog.RefOf(m),
        providerName,
        runtime,
        m.SupportsImages,
        supportsSubagents);

    public static IReadOnlyList<int> ContextWindows(ModelInfo m)
    {
        var preferred = m.DefaultContextWindow ?? DefaultContextWindow;
        return preferred > 0 && m.ContextWindow > preferred ? [preferred, m.ContextWindow] : [m.ContextWindow];
    }

    /// <summary>
    /// 把请求的参数合并到会话上并返回生效值；null 字段沿用当前值。
    /// role 按 <paramref name="assets"/>（会话项目的 command / skill / role）解析，找不到时保持原来的 role。
    /// </summary>
    public static SessionOptionsDto Apply(AgentSession session, SessionOptionsDto? requested, AgentAssetCatalog? assets = null)
    {
        // ACP 依赖子 Agent：运行时没有子 Agent 时保持原来的模式
        if (requested?.Role is { } roleName && SessionRole.Resolve(roleName, assets ?? AgentAssetCatalog.Empty) is { } role &&
            (role.Name != SessionRole.AcpName || session.External is not { SupportsSubagents: false }))
        {
            session.Role = role;
        }
        var model = session.Agent.Model;
        var thinking = requested?.ThinkingLevel is { } t && Enum.TryParse<ThinkingLevel>(t, ignoreCase: true, out var parsed)
            ? parsed
            : session.Agent.ThinkingLevel;
        session.Agent.ThinkingLevel = Nearest(ThinkingLevels.Available(model), thinking);
        session.Agent.FastMode = (requested?.FastMode ?? session.Agent.FastMode) && model.SupportsFastMode;
        var windows = ContextWindows(model);
        var window = requested?.ContextWindow ?? session.ContextWindow;
        session.ContextWindow = windows.FirstOrDefault(w => w >= window, windows[^1]);
        if (ParsePermission(requested?.PermissionMode) is { } permission)
        {
            session.Agent.PermissionMode = permission;
        }
        return Current(session);
    }

    public static SessionOptionsDto Current(AgentSession session) =>
        new(session.Agent.ThinkingLevel.ToString(), session.Agent.FastMode, session.ContextWindow,
            ToWire(session.Agent.PermissionMode), session.Role.Name);

    /// <summary>线上取值为 camelCase：ask / autoApprove / fullAccess。</summary>
    public static string ToWire(PermissionMode mode) => mode switch
    {
        PermissionMode.AutoApprove => "autoApprove",
        PermissionMode.FullAccess => "fullAccess",
        _ => "ask",
    };

    public static PermissionMode? ParsePermission(string? value) =>
        value is not null && Enum.TryParse<PermissionMode>(value, ignoreCase: true, out var mode) ? mode : null;

    /// <summary>新会话的初始参数：模型默认思考档位、关闭 fast mode、最小的上下文预算。</summary>
    public static void Initialize(AgentSession session)
    {
        var model = session.Agent.Model;
        session.Agent.ThinkingLevel = ThinkingLevels.Default(model);
        session.Agent.FastMode = false;
        session.Agent.PermissionMode = PermissionMode.Ask;
        session.Role = SessionRole.Agent;
        session.ContextWindow = ContextWindows(model)[0];
    }

    /// <summary>不可用时先向上、再向下取最近的档位（与适配器的 effort 选择方向一致）。</summary>
    private static ThinkingLevel Nearest(IReadOnlyList<ThinkingLevel> available, ThinkingLevel wanted)
    {
        if (available.Count == 0 || available.Contains(wanted))
        {
            return available.Count == 0 ? ThinkingLevel.Off : wanted;
        }
        var higher = available.Where(l => l > wanted).ToArray();
        return higher.Length > 0 ? higher.Min() : available.Where(l => l < wanted).Max();
    }
}
