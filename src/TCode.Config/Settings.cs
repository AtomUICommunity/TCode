using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TCode.Config;

/// <summary>~/.tcode/settings.json 的内容。格式见 docs/settings.md。所有字段可空：null 表示使用默认值。</summary>
public sealed record TCodeSettings
{
    /// <summary>新会话的默认模型：&lt;厂商 id&gt;/&lt;模型 id&gt;。</summary>
    public string? Model { get; init; }
    public string? Language { get; init; }
    public string? Theme { get; init; }

    /// <summary>减少动态效果：true 时界面动效直接跳到终态（macOS 系统的“减少动态效果”同样生效）。</summary>
    public bool? ReduceMotion { get; init; }

    /// <summary>Markdown 中的图片按原始清晰度显示：默认（false）按显示尺寸压缩解码以节省内存，点击图片查看时仍是原图。</summary>
    public bool? OriginalImages { get; init; }

    /// <summary>点窗口关闭按钮时隐藏到系统托盘、进程继续运行（默认 true）；false 时关闭窗口即退出。</summary>
    public bool? CloseToTray { get; init; }
    public Dictionary<string, ProviderSettings> Providers { get; init; } = new(StringComparer.Ordinal);

    /// <summary>内置 Agent 的 web_search 后端；未配置时不提供该工具。</summary>
    public WebSearchSettings? WebSearch { get; init; }

    /// <summary>内置 Agent 的权限相关设置。</summary>
    public PermissionSettings? Permissions { get; init; }

    /// <summary>新会话的默认模式：agent / plan / acp，或自定义 role 名（docs/design/composer.md §10.3）；null 为 agent。</summary>
    public string? DefaultRole { get; init; }

    /// <summary>skills（~/.tcode/skills 与项目 .agents/skills）。</summary>
    public SkillsSettings? Skills { get; init; }

    /// <summary>应用内更新（docs/design/auto-update.md §8）。</summary>
    public UpdateSettings? Update { get; init; }

    /// <summary>内置 Agent 自动压缩上下文（默认 true，docs/design/compaction.md §4）；关闭后仍可用 /compact 手动压缩。</summary>
    public bool? AutoCompact { get; init; }

    /// <summary>MCP 服务器（原样保留，按项单独解析，一项出错不影响其他项；见 <see cref="McpServerConfig.ParseServers"/>）。</summary>
    public JsonObject? McpServers { get; init; }
}

public sealed record WebSearchSettings
{
    /// <summary>brave | tavily | exa | searxng。</summary>
    public string? Provider { get; init; }

    /// <summary>明文、{env:VAR} 或 $VAR；SearXNG 不需要。</summary>
    public string? ApiKey { get; init; }

    /// <summary>自定义端点（SearXNG 必填：实例地址）。</summary>
    public string? BaseUrl { get; init; }
}

public sealed record UpdateSettings
{
    /// <summary>也接收 GitHub 上标为 Pre-release 的版本（默认 true）；false 时只接收正式版。</summary>
    public bool? Prerelease { get; init; }

    /// <summary>启动 60 秒后及之后每 6 小时自动检查（默认 true）；false 时只能在「关于」页手动检查。</summary>
    public bool? AutoCheck { get; init; }
}

public sealed record SkillsSettings
{
    /// <summary>在系统提示里列出 skill，让模型自己发现并加载（默认 true）；关闭后只能用 /skill 名 显式调用。</summary>
    public bool? AutoDiscover { get; init; }
}

public sealed record PermissionSettings
{
    /// <summary>除 cwd 外也视为工作区的目录（其中的只读操作免审批）。</summary>
    public List<string>? AdditionalDirectories { get; init; }
}

public sealed record ProviderSettings
{
    public string? Name { get; init; }
    /// <summary>chat | responses | messages。</summary>
    public string? Api { get; init; }
    public string? BaseUrl { get; init; }
    /// <summary>明文、{env:VAR} 或 $VAR；"" 表示无需 Key（本地服务）。</summary>
    public string? ApiKey { get; init; }
    public bool? Enabled { get; init; }
    public Dictionary<string, string>? Headers { get; init; }
    /// <summary>写了就只启用列出的模型；值为对目录元数据的覆盖。</summary>
    public Dictionary<string, ModelSettings>? Models { get; init; }
    /// <summary>厂商差异的兜底开关，由 Host 按 ModelCompat 解析。</summary>
    public JsonObject? Compat { get; init; }
}

public sealed record ModelSettings
{
    /// <summary>该模型使用的线协议（chat / responses / messages）；省略时跟随厂商。</summary>
    public string? Api { get; init; }
    /// <summary>该模型的 Base URL；省略时跟随厂商。</summary>
    public string? BaseUrl { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public int? ContextWindow { get; init; }
    /// <summary>新会话默认的上下文预算；省略时用目录值或全局默认（272K）。</summary>
    public int? DefaultContextWindow { get; init; }
    public int? MaxTokens { get; init; }
    public bool? Reasoning { get; init; }
    public List<string>? Efforts { get; init; }
    public bool? Images { get; init; }
    public bool? FastMode { get; init; }
    public JsonObject? Compat { get; init; }
}

/// <summary>读取结果：解析失败时 <see cref="Settings"/> 为 null，调用方应保留上一次的配置。</summary>
public sealed record SettingsLoadResult(TCodeSettings? Settings, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool IsValid => Settings is not null;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TCodeSettings))]
[JsonSerializable(typeof(McpServerSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
