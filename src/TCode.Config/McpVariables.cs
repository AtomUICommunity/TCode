using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TCode.Config;

/// <summary>
/// MCP 配置中的变量展开（docs/design/mcp.md §4.1）。为了直接复用其他客户端的配置，同时接受：
/// <c>{env:VAR}</c>、<c>$VAR</c>（整串）、<c>${VAR}</c>、<c>${VAR:-default}</c>、<c>${env:VAR}</c>、
/// <c>${workspaceFolder}</c> / <c>{workspaceFolder}</c>、<c>${workspaceFolderBasename}</c>、<c>${userHome}</c> / <c>{userHome}</c>、
/// <c>${pathSeparator}</c> / <c>${/}</c>。未设置的环境变量展开为空串并记入 <see cref="McpExpansion.Missing"/>。
/// </summary>
public sealed class McpVariables(string? workspaceFolder, Func<string, string?>? environment = null)
{
    private readonly Func<string, string?> _env = environment ?? Environment.GetEnvironmentVariable;

    public string? WorkspaceFolder { get; } = workspaceFolder;

    public static string UserHome => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>配置是否引用了工作区目录（决定 stdio 连接能否跨项目共享）。</summary>
    public static bool UsesWorkspace(McpServerConfig config) =>
        Mentions(config.Command) || config.Args.Any(Mentions) || config.Env.Values.Any(Mentions) || Mentions(config.Cwd) || Mentions(config.EnvFile);

    private static bool Mentions(string? s) => s is not null && s.Contains("workspaceFolder", StringComparison.Ordinal);

    public string Expand(string value, ICollection<string>? missing = null)
    {
        if (value.Length > 1 && value[0] == '$' && value[1] != '{' && value.Skip(1).All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            return Env(value[1..], null, missing);
        }
        var sb = new StringBuilder(value.Length);
        var i = 0;
        while (i < value.Length)
        {
            var dollar = value[i] == '$' && i + 1 < value.Length && value[i + 1] == '{';
            if ((dollar || value[i] == '{') && value.IndexOf('}', i) is var end and > 0)
            {
                var start = i + (dollar ? 2 : 1);
                var body = value[start..end];
                if (Resolve(body, dollar, missing) is { } replacement)
                {
                    sb.Append(replacement);
                    i = end + 1;
                    continue;
                }
            }
            sb.Append(value[i]);
            i++;
        }
        return sb.ToString();
    }

    private string? Resolve(string body, bool dollar, ICollection<string>? missing)
    {
        switch (body)
        {
            case "workspaceFolder":
                return WorkspaceFolder ?? "";
            case "workspaceFolderBasename":
                return WorkspaceFolder is null ? "" : Path.GetFileName(WorkspaceFolder.TrimEnd(Path.DirectorySeparatorChar));
            case "userHome":
                return UserHome;
            case "pathSeparator" or "/" when dollar:
                return Path.DirectorySeparatorChar.ToString();
        }
        if (body.StartsWith("env:", StringComparison.Ordinal))
        {
            var (name, fallback) = SplitDefault(body[4..]);
            return IsVariableName(name) ? Env(name, fallback, missing) : null;
        }
        if (dollar)
        {
            var (name, fallback) = SplitDefault(body);
            return IsVariableName(name) ? Env(name, fallback, missing) : null;
        }
        return null;
    }

    private static (string Name, string? Default) SplitDefault(string body)
    {
        var idx = body.IndexOf(":-", StringComparison.Ordinal);
        return idx < 0 ? (body.Trim(), null) : (body[..idx].Trim(), body[(idx + 2)..]);
    }

    private static bool IsVariableName(string name) =>
        name.Length > 0 && !char.IsDigit(name[0]) && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.');

    private string Env(string name, string? fallback, ICollection<string>? missing)
    {
        if (_env(name) is { Length: > 0 } value)
        {
            return value;
        }
        if (fallback is not null)
        {
            return fallback;
        }
        missing?.Add(name);
        return "";
    }

    /// <summary>展开整条配置（命令、参数、环境变量、cwd、URL、请求头）；envFile 中的变量合并到 Env（配置里显式写的优先）。</summary>
    public McpExpansion Expand(McpServerConfig config)
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        string? envFileError = null;
        if (config.EnvFile is { Length: > 0 } envFile)
        {
            var path = ResolvePath(Expand(envFile, missing));
            try
            {
                foreach (var (k, v) in DotEnv.Parse(File.ReadAllText(path)))
                {
                    env[k] = v;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                envFileError = $"Cannot read envFile \"{path}\": {ex.Message}";
            }
        }
        foreach (var (k, v) in config.Env)
        {
            env[k] = Expand(v, missing);
        }
        var headers = config.Headers.ToDictionary(h => h.Key, h => Expand(h.Value, missing), StringComparer.OrdinalIgnoreCase);
        return new McpExpansion(
            config.Command is null ? null : Expand(config.Command, missing),
            config.Args.Select(a => Expand(a, missing)).ToArray(),
            env,
            config.Cwd is { Length: > 0 } cwd ? ResolvePath(Expand(cwd, missing)) : WorkspaceFolder,
            config.Url is null ? null : Expand(config.Url, missing),
            headers,
            missing.ToArray(),
            envFileError);
    }

    /// <summary>~ 展开；相对路径相对工作区目录。</summary>
    public string ResolvePath(string path)
    {
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            path = UserHome + path[1..];
        }
        if (!Path.IsPathRooted(path) && WorkspaceFolder is not null)
        {
            path = Path.Combine(WorkspaceFolder, path);
        }
        return Path.GetFullPath(path);
    }
}

/// <summary>展开后的连接参数。</summary>
public sealed record McpExpansion(
    string? Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Env,
    string? Cwd,
    string? Url,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyList<string> MissingVariables,
    string? EnvFileError);

/// <summary>最简 dotenv 解析：KEY=VALUE、export 前缀、# 注释、单 / 双引号。</summary>
public static class DotEnv
{
    public static IEnumerable<KeyValuePair<string, string>> Parse(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }
            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line[7..].TrimStart();
            }
            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\''))
            {
                var quote = value[0];
                value = value[1..^1];
                if (quote == '"')
                {
                    value = value.Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\\"", "\"", StringComparison.Ordinal);
                }
            }
            else if (value.IndexOf(" #", StringComparison.Ordinal) is var hash and >= 0)
            {
                value = value[..hash].TrimEnd();
            }
            yield return new(key, value);
        }
    }
}

/// <summary>项目级 .mcp.json（docs/design/mcp.md §4.2）：位于项目根（git 根，没有 git 时为 cwd）。</summary>
public static class McpProjectConfig
{
    public const string FileName = ".mcp.json";

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>cwd 所在的项目根：向上找到 .git 为止；没有时就是 cwd。</summary>
    public static string ProjectRoot(string cwd)
    {
        for (var dir = new DirectoryInfo(cwd); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }
        }
        return cwd;
    }

    public static string PathFor(string cwd) => Path.Combine(ProjectRoot(cwd), FileName);

    /// <summary>读取项目配置；文件不存在时返回空列表。整个文件无法解析时返回 <paramref name="error"/>。</summary>
    public static IReadOnlyList<McpServerConfig> Load(string cwd, out string? error, List<string>? warnings = null)
    {
        error = null;
        var path = PathFor(cwd);
        if (!File.Exists(path))
        {
            return [];
        }
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path), documentOptions: DocumentOptions) is not JsonObject root)
            {
                error = $"{path}: must contain a JSON object.";
                return [];
            }
            var servers = root.FirstOrDefault(p => p.Key.Equals("mcpServers", StringComparison.OrdinalIgnoreCase)).Value as JsonObject;
            return McpServerConfig.ParseServers(servers, McpConfigSource.Project, warnings);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            error = $"{path}: {ex.Message}";
            return [];
        }
    }

    /// <summary>写回一个服务器（null 表示删除），保留文件中的其他内容。</summary>
    public static void Update(string cwd, string key, JsonObject? server)
    {
        var path = PathFor(cwd);
        JsonObject root = File.Exists(path) && JsonNode.Parse(File.ReadAllText(path), documentOptions: DocumentOptions) is JsonObject existing
            ? existing
            : new JsonObject();
        if (root["mcpServers"] is not JsonObject servers)
        {
            servers = new JsonObject();
            root["mcpServers"] = servers;
        }
        if (server is null)
        {
            servers.Remove(key);
        }
        else
        {
            servers[key] = server;
        }
        var json = root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        var temp = path + ".tmp";
        File.WriteAllText(temp, json + "\n", new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}
