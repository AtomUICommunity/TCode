namespace TKCode.Rpc;

/// <summary>设置页 MCP 分区的服务器列表（docs/design/mcp.md §11）。</summary>
/// <param name="ProjectFile">当前项目的 .mcp.json 路径（cwd 为 null 时为 null）。</param>
/// <param name="ProjectError">.mcp.json 无法解析时的错误。</param>
public sealed record McpServersDto(IReadOnlyList<McpServerDto> Servers, string? ProjectFile, string? ProjectError);

/// <param name="Key">服务器名（工具名前缀）。</param>
/// <param name="Source">global | project。</param>
/// <param name="Transport">stdio | http | sse。</param>
/// <param name="Summary">命令行或 URL（未展开变量）。</param>
/// <param name="Availability">ready | disabled | untrusted | ignored | invalid。</param>
/// <param name="State">idle | connecting | connected | needsAuth | failed（没有连接时为 idle）。</param>
/// <param name="ConfigJson">配置原文（TKCode 写法），用于编辑表单。</param>
/// <param name="ShadowedGlobal">项目配置覆盖了同名的全局配置。</param>
public sealed record McpServerDto(
    string Key,
    string Title,
    string Source,
    string Transport,
    string Summary,
    bool Enabled,
    string Availability,
    string State,
    string? Error,
    int ToolCount,
    int PromptCount,
    int ResourceCount,
    string? ProtocolVersion,
    string? ServerName,
    string? ServerVersion,
    string? Instructions,
    bool ShadowedGlobal,
    IReadOnlyList<string> MissingVariables,
    string ConfigJson,
    bool UsesOAuth);

/// <param name="Disabled">在 disabledTools 中（不暴露给模型）。</param>
/// <param name="AlwaysAllow">在 alwaysAllow 中（Ask 档免审批）。</param>
/// <param name="SchemaChars">参数 schema 的字符数（估算上下文占用）。</param>
public sealed record McpToolDto(
    string Name,
    string? Title,
    string? Description,
    bool ReadOnlyHint,
    bool DestructiveHint,
    bool OpenWorldHint,
    int SchemaChars,
    bool Disabled,
    bool AlwaysAllow);

public sealed record McpToolsDto(IReadOnlyList<McpToolDto> Tools, string? Error);

/// <param name="ConfigJson">未保存的配置（TKCode 写法）。</param>
public sealed record McpTestRequest(string Key, string ConfigJson, string? Cwd);

public sealed record McpTestResult(bool Ok, string? Error, int ToolCount, string? ServerName, string? ServerVersion, string? ProtocolVersion,
    IReadOnlyList<string> Log, bool NeedsAuth);

public sealed record McpServerParams(string Key, string? Cwd);

public sealed record McpCwdParams(string? Cwd);

public sealed record McpRestartParams(string Key, string? Cwd, bool Login);

public sealed record McpTrustParams(string Key, string Cwd, bool Trusted);

public sealed record McpLogDto(IReadOnlyList<string> Lines);

/// <summary>Host → UI：MCP 服务器状态或配置变化（UI 重新拉取列表）。</summary>
public sealed record McpChangedNotification(string? Key);

/// <summary>Host → UI：MCP 服务器请求用户输入（elicitation，规格 §5.1）。</summary>
/// <param name="SessionId">发起请求的工具调用所属会话；无法确定时为 null（全局弹窗）。</param>
/// <param name="Mode">form | url。</param>
/// <param name="SchemaJson">form 模式的 requestedSchema（受限的扁平 JSON Schema）。</param>
public sealed record McpElicitRequest(string? SessionId, string Server, string ServerTitle, string Mode, string Message, string? SchemaJson, string? Url);

/// <param name="Action">accept | decline | cancel。</param>
/// <param name="ContentJson">form 模式 accept 时提交的对象。</param>
public sealed record McpElicitResponse(string Action, string? ContentJson = null);

/// <summary>Host → UI：用系统浏览器打开 OAuth 授权页。</summary>
public sealed record McpOpenUrlRequest(string Server, string Url);

public sealed record McpOpenUrlResponse(bool Opened);

public sealed record McpResourceDto(string Server, string Uri, string Name, string? Title, string? Description, string? MimeType);

public sealed record McpPromptArgumentDto(string Name, string? Description, bool Required);

public sealed record McpPromptDto(string Server, string Name, string? Title, string? Description, IReadOnlyList<McpPromptArgumentDto> Arguments);

/// <summary>Composer 的 @ 资源与 / 提示词。</summary>
public sealed record McpCatalogDto(IReadOnlyList<McpResourceDto> Resources, IReadOnlyList<McpPromptDto> Prompts);

/// <summary>随用户消息发送的资源引用（Host 读取内容后附在消息中）。</summary>
public sealed record McpResourceRefDto(string Server, string Uri);

/// <summary>随用户消息发送的提示词（Host 展开后放在消息开头）。</summary>
public sealed record McpPromptRefDto(string Server, string Name, IReadOnlyDictionary<string, string> Arguments);
