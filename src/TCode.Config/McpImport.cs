using System.Text.Json;
using System.Text.Json.Nodes;

namespace TCode.Config;

public enum McpImportNoteKind
{
    /// <summary>会在本机执行命令（Arg：命令行）。</summary>
    RunsCommand,
    /// <summary>不认识的 type（Arg：取值），已按其他字段推断。</summary>
    UnknownType,
    /// <summary>源配置只允许部分工具（白名单），需要连接后手动停用其余工具。</summary>
    AllowList,
    /// <summary>headersHelper 不支持。</summary>
    HeadersHelper,
    /// <summary>源配置设置了 trust:true（TCode 仍按审批策略询问）。</summary>
    Trust,
    /// <summary>${file:…} 引用不支持。</summary>
    FileReference,
}

/// <summary>导入预览中的提示（界面负责本地化）。</summary>
public sealed record McpImportNote(McpImportNoteKind Kind, string? Arg = null);

/// <summary>VS Code 的 inputs 项：导入时由用户填写（明文或环境变量引用）。</summary>
public sealed record McpImportInput(string Id, string? Description, bool Password);

/// <summary>
/// 导入预览中的一个服务器（docs/design/mcp.md §12）。<see cref="Config"/> 已转为 TCode 写法；
/// <see cref="Dropped"/> 为不会导入的字段，<see cref="Notes"/> 为需要用户留意的提示。
/// </summary>
public sealed record McpImportCandidate(
    string OriginalName,
    string Key,
    JsonObject Config,
    IReadOnlyList<string> Dropped,
    IReadOnlyList<McpImportNote> Notes,
    IReadOnlyList<McpImportInput> Inputs,
    IReadOnlyList<string> AutoApprove,
    string? Error)
{
    /// <summary>规整后的配置（未展开变量）。</summary>
    public McpServerConfig Parsed => McpServerConfig.ParseServers(new JsonObject { [Key] = Config.DeepClone() }, McpConfigSource.Global).Single();

    /// <summary>一行摘要：命令行或 URL。</summary>
    public string Summary => Config["url"] is JsonValue url
        ? url.ToString()
        : string.Join(' ', new[] { Config["command"]?.ToString() ?? "" }
            .Concat((Config["args"] as JsonArray)?.Select(a => a?.ToString() ?? "") ?? []));

    /// <summary>用 inputs 的取值替换 ${input:id}（取值可以是明文或 {env:VAR}）。</summary>
    public JsonObject Resolve(IReadOnlyDictionary<string, string> inputValues, bool includeAutoApprove)
    {
        var obj = (JsonObject)Config.DeepClone();
        if (Inputs.Count > 0)
        {
            ReplaceStrings(obj, s =>
            {
                foreach (var input in Inputs)
                {
                    if (inputValues.TryGetValue(input.Id, out var value))
                    {
                        s = s.Replace("${input:" + input.Id + "}", value, StringComparison.Ordinal);
                    }
                }
                return s;
            });
        }
        if (includeAutoApprove && AutoApprove.Count > 0)
        {
            obj["alwaysAllow"] = new JsonArray(AutoApprove.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
        }
        return obj;
    }

    private static void ReplaceStrings(JsonNode node, Func<string, string> replace)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (obj[key] is JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        obj[key] = replace(s);
                    }
                    else if (obj[key] is { } child)
                    {
                        ReplaceStrings(child, replace);
                    }
                }
                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        array[i] = replace(s);
                    }
                    else if (array[i] is { } child)
                    {
                        ReplaceStrings(child, replace);
                    }
                }
                break;
        }
    }
}

/// <param name="Format">识别出的格式（mcpServers / vscode / opencode / zed / codex / claude-code / single）。</param>
public sealed record McpImportResult(string Format, IReadOnlyList<McpImportCandidate> Servers, string? Error);

/// <summary>本机发现的其他客户端的配置文件。</summary>
public sealed record McpImportSource(string Client, string Path, McpImportResult Result);

/// <summary>
/// 从其他客户端的 MCP 配置导入，以及导出为通用格式（docs/design/mcp.md §12）。
/// 只解析文本与读取已知路径，不执行任何程序。
/// </summary>
public static class McpImport
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>解析粘贴的文本或文件内容，自动识别格式。</summary>
    /// <param name="projectPath">导入 ~/.claude.json 时取该项目的本地级服务器。</param>
    /// <param name="singleName">文本是单个服务器对象时使用的名字。</param>
    public static McpImportResult Parse(string text, string? projectPath = null, string? singleName = null)
    {
        text = text.Trim().TrimStart('﻿');
        if (text.Length == 0)
        {
            return new McpImportResult("", [], "Nothing to import.");
        }
        if (LooksLikeToml(text))
        {
            try
            {
                return FromCodex(McpToml.Parse(text));
            }
            catch (FormatException ex)
            {
                return new McpImportResult("codex", [], ex.Message);
            }
        }
        JsonObject? root = null;
        string? jsonError = null;
        foreach (var candidate in new[] { text, "{" + text.TrimEnd(',') + "}" })
        {
            try
            {
                if (JsonNode.Parse(candidate, documentOptions: DocumentOptions) is JsonObject obj)
                {
                    root = obj;
                    break;
                }
            }
            catch (JsonException ex)
            {
                jsonError ??= ex.Message;
            }
        }
        if (root is null)
        {
            return new McpImportResult("", [], jsonError ?? "Expected a JSON object.");
        }
        return FromJson(root, projectPath, singleName);
    }

    private static bool LooksLikeToml(string text) =>
        text.Contains("[mcp_servers", StringComparison.Ordinal) || (text[0] != '{' && text[0] != '"' && text.Contains(" = ", StringComparison.Ordinal));

    private static McpImportResult FromJson(JsonObject root, string? projectPath, string? singleName)
    {
        if (Get(root, "mcpServers") is JsonObject servers)
        {
            var list = Entries(servers).ToList();
            var format = "mcpServers";
            // ~/.claude.json：用户级之外，再加上当前项目的本地级服务器
            if (Get(root, "projects") is JsonObject projects)
            {
                format = "claude-code";
                if (projectPath is not null && projects.FirstOrDefault(p => SamePath(p.Key, projectPath)).Value is JsonObject project
                    && Get(project, "mcpServers") is JsonObject local)
                {
                    list.AddRange(Entries(local));
                }
            }
            return Result(format, list);
        }
        if (Get(root, "projects") is JsonObject onlyProjects)
        {
            var list = new List<McpImportCandidate>();
            if (projectPath is not null && onlyProjects.FirstOrDefault(p => SamePath(p.Key, projectPath)).Value is JsonObject project
                && Get(project, "mcpServers") is JsonObject local)
            {
                list.AddRange(Entries(local));
            }
            return Result("claude-code", list);
        }
        if (Get(root, "servers") is JsonObject vscode)
        {
            var inputs = (Get(root, "inputs") as JsonArray)?.OfType<JsonObject>()
                .Select(i => new McpImportInput(i["id"]?.ToString() ?? "", i["description"]?.ToString(), i["password"] is JsonValue p && p.GetValueKind() == JsonValueKind.True))
                .Where(i => i.Id.Length > 0).ToList() ?? [];
            return Result("vscode", Entries(vscode, inputs).ToList());
        }
        if (Get(root, "mcp") is JsonObject opencode && opencode.Count > 0 && opencode.All(p => p.Value is JsonObject))
        {
            return Result("opencode", Entries(opencode).ToList());
        }
        if (Get(root, "context_servers") is JsonObject zed)
        {
            return Result("zed", Entries(zed).ToList());
        }
        if (Get(root, "mcp_servers") is JsonObject codexJson)
        {
            return FromCodex(new JsonObject { ["mcp_servers"] = codexJson.DeepClone() });
        }
        if (LooksLikeServer(root))
        {
            var name = singleName ?? Get(root, "name")?.ToString() ?? "server";
            return Result("single", [Convert(name, root, [])]);
        }
        if (root.Count > 0 && root.All(p => p.Value is JsonObject o && LooksLikeServer(o)))
        {
            return Result("mcpServers", Entries(root).ToList());
        }
        return new McpImportResult("", [], "No MCP servers found. Expected an object with \"mcpServers\" (or \"servers\", \"mcp\", \"context_servers\", [mcp_servers]).");
    }

    private static McpImportResult Result(string format, List<McpImportCandidate> list) =>
        new(format, list, list.Count == 0 ? "No MCP servers found." : null);

    private static IEnumerable<McpImportCandidate> Entries(JsonObject servers, IReadOnlyList<McpImportInput>? inputs = null) =>
        servers.Where(p => p.Value is JsonObject).Select(p => Convert(p.Key, (JsonObject)p.Value!, inputs ?? []));

    private static bool LooksLikeServer(JsonObject obj) =>
        Get(obj, "command") is not null || Get(obj, "url") is not null || Get(obj, "serverUrl") is not null || Get(obj, "httpUrl") is not null;

    /// <summary>把一个服务器条目（任意客户端写法）转换为 TCode 写法。</summary>
    internal static McpImportCandidate Convert(string name, JsonObject source, IReadOnlyList<McpImportInput> allInputs)
    {
        var obj = new JsonObject();
        var dropped = new List<string>();
        var notes = new List<McpImportNote>();
        var autoApprove = new List<string>();
        string? error = null;

        JsonNode? command = Get(source, "command");
        var args = new List<string>();
        // Zed 旧格式：command: { path, args, env }
        if (command is JsonObject zedCommand)
        {
            command = Get(zedCommand, "path");
            args.AddRange(Strings(Get(zedCommand, "args")));
            if (Get(zedCommand, "env") is JsonObject zedEnv)
            {
                source = (JsonObject)source.DeepClone();
                source["env"] = zedEnv.DeepClone();
            }
        }
        // opencode / Zed 新格式：command 为数组
        if (command is JsonArray commandArray)
        {
            var parts = Strings(commandArray).ToList();
            command = parts.Count > 0 ? JsonValue.Create(parts[0]) : null;
            args.InsertRange(0, parts.Skip(1));
        }
        args.AddRange(Strings(Get(source, "args")));

        var url = (Get(source, "url") ?? Get(source, "serverUrl") ?? Get(source, "httpUrl"))?.ToString();
        var type = (Get(source, "type") ?? Get(source, "transportType") ?? Get(source, "transport"))?.ToString()?.Trim().ToLowerInvariant();
        switch (type)
        {
            case "sse":
                obj["type"] = "sse";
                break;
            case "ws" or "websocket":
                error = "WebSocket servers are not supported.";
                break;
            case "local" or "stdio" or "http" or "streamable-http" or "streamablehttp" or "remote" or null or "":
                break;
            default:
                notes.Add(new McpImportNote(McpImportNoteKind.UnknownType, type));
                break;
        }
        if (url is { Length: > 0 } && command is null)
        {
            obj["url"] = url;
        }
        else if (command?.ToString() is { Length: > 0 } cmd)
        {
            obj.Remove("type");
            obj["command"] = cmd;
            if (args.Count > 0)
            {
                obj["args"] = new JsonArray(args.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());
            }
        }
        else
        {
            error ??= "Missing \"command\" or \"url\".";
        }

        if (StringMap(Get(source, "env") ?? Get(source, "environment")) is { Count: > 0 } env)
        {
            obj["env"] = env;
        }
        if (Get(source, "cwd")?.ToString() is { Length: > 0 } cwd)
        {
            obj["cwd"] = cwd;
        }
        if (Get(source, "envFile")?.ToString() is { Length: > 0 } envFile)
        {
            obj["envFile"] = envFile;
        }
        if (StringMap(Get(source, "headers")) is { Count: > 0 } headers)
        {
            obj["headers"] = headers;
        }
        if (Get(source, "oauth") is JsonObject oauth)
        {
            var o = new JsonObject();
            if (Get(oauth, "clientId")?.ToString() is { Length: > 0 } clientId)
            {
                o["clientId"] = clientId;
            }
            if (Get(oauth, "clientSecret")?.ToString() is { Length: > 0 } clientSecret)
            {
                o["clientSecret"] = clientSecret;
            }
            if (Get(oauth, "callbackPort") is JsonValue port && port.TryGetValue<int>(out var p))
            {
                o["callbackPort"] = p;
            }
            if ((Get(oauth, "scopes") ?? Get(oauth, "scope")) is { } scopes)
            {
                o["scopes"] = scopes is JsonArray scopeArray ? string.Join(' ', Strings(scopeArray)) : scopes.ToString();
            }
            if (Get(oauth, "authServerMetadataUrl") is not null)
            {
                dropped.Add("oauth.authServerMetadataUrl");
            }
            if (o.Count > 0)
            {
                obj["oauth"] = o;
            }
        }
        if (Get(source, "enabled") is JsonValue enabled && enabled.GetValueKind() == JsonValueKind.False
            || Get(source, "disabled") is JsonValue disabled && disabled.GetValueKind() == JsonValueKind.True)
        {
            obj["enabled"] = false;
        }
        var disabledTools = Strings(Get(source, "disabledTools")).Concat(Strings(Get(source, "excludeTools"))).Distinct().ToList();
        if (disabledTools.Count > 0)
        {
            obj["disabledTools"] = new JsonArray(disabledTools.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
        }
        autoApprove.AddRange(Strings(Get(source, "autoApprove")).Concat(Strings(Get(source, "alwaysAllow"))).Distinct());
        if (Get(source, "includeTools") is not null || Get(source, "enabled_tools") is not null)
        {
            notes.Add(new McpImportNote(McpImportNoteKind.AllowList));
        }
        if (Get(source, "timeout") is JsonValue timeout && timeout.TryGetValue<double>(out var t) && t > 0)
        {
            // Gemini CLI 为毫秒，Cline / Roo 为秒
            obj["toolTimeout"] = (int)Math.Max(1, t > 3600 ? t / 1000 : t);
        }
        if (Get(source, "name") is JsonValue displayName && displayName.ToString() != name)
        {
            obj["name"] = displayName.ToString();
        }

        foreach (var (key, _) in source)
        {
            if (!ConsumedKeys.Contains(key))
            {
                dropped.Add(key);
            }
        }
        if (Get(source, "headersHelper") is not null)
        {
            notes.Add(new McpImportNote(McpImportNoteKind.HeadersHelper));
        }
        if (Get(source, "trust") is JsonValue trust && trust.GetValueKind() == JsonValueKind.True)
        {
            notes.Add(new McpImportNote(McpImportNoteKind.Trust));
        }

        var text = obj.ToJsonString();
        if (text.Contains("${file:", StringComparison.Ordinal) || text.Contains("{file:", StringComparison.Ordinal))
        {
            notes.Add(new McpImportNote(McpImportNoteKind.FileReference));
        }
        var inputs = allInputs.Where(i => text.Contains("${input:" + i.Id + "}", StringComparison.Ordinal)).ToList();
        foreach (var id in InputIds(text).Where(id => inputs.All(i => i.Id != id)))
        {
            inputs.Add(new McpImportInput(id, null, false));
        }
        if (obj["command"] is not null)
        {
            notes.Insert(0, new McpImportNote(McpImportNoteKind.RunsCommand, Summary(obj)));
        }
        return new McpImportCandidate(name, McpServerConfig.SanitizeKey(name), obj, dropped, notes, inputs, autoApprove, error);
    }

    private static string Summary(JsonObject obj) =>
        string.Join(' ', new[] { obj["command"]?.ToString() ?? "" }.Concat((obj["args"] as JsonArray)?.Select(a => a?.ToString() ?? "") ?? []));

    private static readonly HashSet<string> ConsumedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "command", "args", "env", "environment", "cwd", "envFile", "url", "serverUrl", "httpUrl", "type", "transportType", "transport", "headers",
        "oauth", "enabled", "disabled", "disabledTools", "excludeTools", "includeTools", "enabled_tools", "autoApprove", "alwaysAllow",
        "timeout", "name", "headersHelper", "trust", "source",
    };

    private static IEnumerable<string> InputIds(string text)
    {
        var at = 0;
        while ((at = text.IndexOf("${input:", at, StringComparison.Ordinal)) >= 0)
        {
            var end = text.IndexOf('}', at);
            if (end < 0)
            {
                yield break;
            }
            yield return text[(at + 8)..end];
            at = end;
        }
    }

    /// <summary>Codex：[mcp_servers.&lt;name&gt;]（TOML，或 JSON 形式的 mcp_servers）。</summary>
    private static McpImportResult FromCodex(JsonObject root)
    {
        if (Get(root, "mcp_servers") is not JsonObject servers)
        {
            return new McpImportResult("codex", [], "No [mcp_servers] tables found.");
        }
        var list = new List<McpImportCandidate>();
        foreach (var (name, node) in servers)
        {
            if (node is not JsonObject codex)
            {
                continue;
            }
            var source = new JsonObject();
            foreach (var key in new[] { "command", "args", "env", "cwd", "url", "enabled" })
            {
                if (Get(codex, key) is { } value)
                {
                    source[key] = value.DeepClone();
                }
            }
            var headers = StringMap(Get(codex, "http_headers")) ?? new JsonObject();
            if (Get(codex, "bearer_token_env_var")?.ToString() is { Length: > 0 } tokenVar)
            {
                headers["Authorization"] = $"Bearer {{env:{tokenVar}}}";
            }
            if (Get(codex, "env_http_headers") is JsonObject envHeaders)
            {
                foreach (var (header, variable) in envHeaders)
                {
                    headers[header] = $"{{env:{variable}}}";
                }
            }
            if (headers.Count > 0)
            {
                source["headers"] = headers;
            }
            if (Get(codex, "disabled_tools") is JsonArray disabledTools)
            {
                source["disabledTools"] = disabledTools.DeepClone();
            }
            if (Get(codex, "enabled_tools") is { } enabledTools)
            {
                source["enabled_tools"] = enabledTools.DeepClone();
            }
            var candidate = Convert(name, source, []);
            var config = candidate.Config;
            if ((Number(Get(codex, "startup_timeout_sec")) ?? Number(Get(codex, "startup_timeout_ms")) / 1000) is { } startup and > 0)
            {
                config["startupTimeout"] = (int)Math.Ceiling(startup);
            }
            if (Number(Get(codex, "tool_timeout_sec")) is { } toolTimeout and > 0)
            {
                config["toolTimeout"] = (int)Math.Ceiling(toolTimeout);
            }
            var dropped = codex.Select(p => p.Key).Where(k => !CodexKeys.Contains(k)).ToList();
            list.Add(candidate with { Dropped = dropped });
        }
        return Result("codex", list);
    }

    private static readonly HashSet<string> CodexKeys = new(StringComparer.Ordinal)
    {
        "command", "args", "env", "cwd", "url", "enabled", "http_headers", "bearer_token_env_var", "env_http_headers", "disabled_tools",
        "enabled_tools", "startup_timeout_sec", "startup_timeout_ms", "tool_timeout_sec", "env_vars",
    };

    private static double? Number(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) ? d
        : node is JsonValue l && l.TryGetValue<long>(out var n) ? n : null;

    private static JsonNode? Get(JsonObject obj, string key)
    {
        if (obj.TryGetPropertyValue(key, out var exact))
        {
            return exact;
        }
        foreach (var (k, v) in obj)
        {
            if (k.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return v;
            }
        }
        return null;
    }

    private static IEnumerable<string> Strings(JsonNode? node) =>
        node is JsonArray array ? array.Where(v => v is JsonValue).Select(v => v!.ToString()) : [];

    /// <summary>字符串表（值为数字 / 布尔时转为字符串，null 跳过）。</summary>
    private static JsonObject? StringMap(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }
        var result = new JsonObject();
        foreach (var (k, v) in obj)
        {
            if (v is JsonValue value)
            {
                result[k] = value.GetValueKind() == JsonValueKind.String ? value.ToString() : value.ToJsonString();
            }
        }
        return result;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // ---------------- 扫描本机 ----------------

    /// <summary>已知的其他客户端配置文件（docs/design/mcp.md §12.3）；只返回存在且包含服务器的文件。</summary>
    public static IReadOnlyList<McpImportSource> Discover(string? cwd, string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Application Support") : Path.Combine(home, ".config");
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } ch ? ch : Path.Combine(home, ".codex");
        var candidates = new List<(string Client, string Path)>
        {
            ("Claude Desktop", Path.Combine(appData, "Claude", "claude_desktop_config.json")),
            ("Claude Code", Path.Combine(home, ".claude.json")),
            ("Cursor", Path.Combine(home, ".cursor", "mcp.json")),
            ("VS Code", Path.Combine(appData, "Code", "User", "mcp.json")),
            ("Windsurf", Path.Combine(home, ".codeium", "windsurf", "mcp_config.json")),
            ("Windsurf", Path.Combine(OperatingSystem.IsWindows() ? appData : Path.Combine(home, ".config"), "devin", "mcp_config.json")),
            ("Codex", Path.Combine(codexHome, "config.toml")),
            ("Gemini CLI", Path.Combine(home, ".gemini", "settings.json")),
            ("opencode", Path.Combine(home, ".config", "opencode", "opencode.json")),
            ("opencode", Path.Combine(home, ".config", "opencode", "opencode.jsonc")),
            ("Copilot CLI", Path.Combine(home, ".copilot", "mcp-config.json")),
            ("Zed", OperatingSystem.IsWindows() ? Path.Combine(appData, "Zed", "settings.json") : Path.Combine(home, ".config", "zed", "settings.json")),
            ("LM Studio", Path.Combine(home, ".lmstudio", "mcp.json")),
        };
        if (cwd is not null)
        {
            var root = McpProjectConfig.ProjectRoot(cwd);
            candidates.AddRange(
            [
                ("Claude Code (project)", Path.Combine(root, ".mcp.json")),
                ("Cursor (project)", Path.Combine(root, ".cursor", "mcp.json")),
                ("VS Code (project)", Path.Combine(root, ".vscode", "mcp.json")),
                ("Gemini CLI (project)", Path.Combine(root, ".gemini", "settings.json")),
                ("opencode (project)", Path.Combine(root, "opencode.json")),
                ("opencode (project)", Path.Combine(root, "opencode.jsonc")),
            ]);
        }
        var found = new List<McpImportSource>();
        foreach (var (client, path) in candidates.DistinctBy(c => c.Path))
        {
            string text;
            try
            {
                var info = new FileInfo(path);
                // ~/.claude.json 可能很大（会话历史），但 MCP 配置只是其中一部分；超过 50 MB 的文件跳过
                if (!info.Exists || info.Length > 50 * 1024 * 1024)
                {
                    continue;
                }
                text = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            var result = Parse(text, cwd is null ? null : McpProjectConfig.ProjectRoot(cwd));
            if (result.Servers.Count > 0)
            {
                found.Add(new McpImportSource(client, path, result));
            }
        }
        return found;
    }

    // ---------------- 导出 ----------------

    /// <summary>
    /// 导出为通用格式 {"mcpServers": {...}}（Claude Code / Cursor / Copilot CLI 等都能读取）。
    /// TCode 的 {env:VAR} / $VAR 转为 ${VAR}；<paramref name="expand"/> 为 true 时展开为当前值（可能包含密钥）。
    /// </summary>
    public static string Export(IEnumerable<McpServerConfig> servers, bool expand = false, string? cwd = null)
    {
        var root = new JsonObject();
        var map = new JsonObject();
        var variables = new McpVariables(cwd);
        string Value(string s) => expand ? variables.Expand(s) : ToDollarVariables(s);
        foreach (var server in servers.Where(s => s.Error is null))
        {
            var obj = new JsonObject();
            if (server.Transport == McpTransportKind.Stdio)
            {
                obj["command"] = Value(server.Command ?? "");
                if (server.Args.Count > 0)
                {
                    obj["args"] = new JsonArray(server.Args.Select(a => (JsonNode?)JsonValue.Create(Value(a))).ToArray());
                }
                if (server.Env.Count > 0)
                {
                    var env = new JsonObject();
                    foreach (var (k, v) in server.Env)
                    {
                        env[k] = Value(v);
                    }
                    obj["env"] = env;
                }
                if (server.Cwd is { Length: > 0 } c)
                {
                    obj["cwd"] = Value(c);
                }
            }
            else
            {
                obj["type"] = server.Transport == McpTransportKind.Sse ? "sse" : "http";
                obj["url"] = Value(server.Url ?? "");
                if (server.Headers.Count > 0)
                {
                    var headers = new JsonObject();
                    foreach (var (k, v) in server.Headers)
                    {
                        headers[k] = Value(v);
                    }
                    obj["headers"] = headers;
                }
            }
            if (!server.Enabled)
            {
                obj["disabled"] = true;
            }
            map[server.Key] = obj;
        }
        root["mcpServers"] = map;
        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    /// <summary>{env:VAR}、${env:VAR}、整串 $VAR → ${VAR}；{workspaceFolder} → ${workspaceFolder}。</summary>
    public static string ToDollarVariables(string value)
    {
        if (Secrets.VariableOf(value) is { } whole && !value.StartsWith("${", StringComparison.Ordinal))
        {
            return "${" + whole + "}";
        }
        return value
            .Replace("${env:", "${", StringComparison.Ordinal)
            .Replace("{env:", "${", StringComparison.Ordinal)
            .Replace("$${", "${", StringComparison.Ordinal)
            .Replace("{workspaceFolder}", "${workspaceFolder}", StringComparison.Ordinal)
            .Replace("$${workspaceFolder}", "${workspaceFolder}", StringComparison.Ordinal);
    }
}
