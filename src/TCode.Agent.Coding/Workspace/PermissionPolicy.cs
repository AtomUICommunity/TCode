namespace TCode.Agent.Coding.Workspace;

/// <summary>审批判定的结果。Allowed 为 false 时需要询问用户。</summary>
/// <param name="Kind">审批类别：read / fileChange / command / network。</param>
/// <param name="SessionKey">可以“本会话允许”时的记录键；null 表示只能逐次允许。</param>
public sealed record PermissionVerdict(bool Allowed, string? Kind = null, string? Title = null, string? SessionKey = null)
{
    public static PermissionVerdict Allow { get; } = new(true);
}

/// <summary>会话内“本会话允许”的记录，只存在内存中。</summary>
public sealed class SessionApprovals
{
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public bool Contains(string key)
    {
        lock (_gate)
        {
            return _keys.Contains(key);
        }
    }

    public void Add(string key)
    {
        lock (_gate)
        {
            _keys.Add(key);
        }
    }
}

/// <summary>
/// 内置 Agent 的审批策略（docs/design/builtin-agent-tools.md §10）：
/// 工作区内只读免审批；工作区外读取、写文件、命令、网络在 Ask 档询问；AutoApprove 仍询问工作区外写入与敏感文件。
/// </summary>
public static class PermissionPolicy
{
    public static PermissionVerdict Evaluate(ToolPermission permission, PermissionMode mode, WorkspaceRoots workspace, SessionApprovals approvals)
    {
        if (mode == PermissionMode.FullAccess || permission.Access == ToolAccess.None)
        {
            return PermissionVerdict.Allow;
        }
        var paths = permission.Paths ?? [];
        var sensitive = paths.Any(WorkspaceRoots.IsSensitive);
        var outside = paths.Where(p => !workspace.Contains(p)).ToArray();

        switch (permission.Access)
        {
            case ToolAccess.Read:
                if (!sensitive && (outside.Length == 0 || mode == PermissionMode.AutoApprove))
                {
                    return PermissionVerdict.Allow;
                }
                if (sensitive)
                {
                    return new(false, "read", permission.Title ?? paths[0]);
                }
                var readKey = "read:" + DirectoryKey(outside[0]);
                return approvals.Contains(readKey) && outside.All(p => DirectoryKey(p) == DirectoryKey(outside[0]))
                    ? PermissionVerdict.Allow
                    : new(false, "read", permission.Title ?? outside[0], readKey);

            case ToolAccess.Write:
                if (sensitive)
                {
                    return new(false, "fileChange", permission.Title ?? paths[0]);
                }
                if (outside.Length > 0)
                {
                    return new(false, "fileChange", permission.Title ?? outside[0]);
                }
                return mode == PermissionMode.AutoApprove || approvals.Contains("edit")
                    ? PermissionVerdict.Allow
                    : new(false, "fileChange", permission.Title ?? paths.FirstOrDefault(), "edit");

            case ToolAccess.Execute:
                var command = permission.Command ?? "";
                if (mode == PermissionMode.AutoApprove || (ReadOnlyCommands.IsReadOnly(command) && StaysInWorkspace(command, workspace)))
                {
                    return PermissionVerdict.Allow;
                }
                var commandKey = "command:" + CommandPrefix(command);
                return approvals.Contains(commandKey) ? PermissionVerdict.Allow : new(false, "command", permission.Title ?? command, commandKey);

            case ToolAccess.Network:
                if (mode == PermissionMode.AutoApprove)
                {
                    return PermissionVerdict.Allow;
                }
                var networkKey = permission.Domain is { } domain ? "network:" + domain.ToLowerInvariant() : "network:search";
                return approvals.Contains(networkKey) ? PermissionVerdict.Allow : new(false, "network", permission.Title ?? permission.Domain, networkKey);

            case ToolAccess.External:
                // MCP 工具：annotations（readOnlyHint 等）不可信，不作为放行依据；“始终允许”由工具在 DescribePermission 中返回 None。
                if (mode == PermissionMode.AutoApprove)
                {
                    return PermissionVerdict.Allow;
                }
                var server = permission.Domain ?? "";
                var toolKey = $"mcp:{server}/{permission.Command}";
                return approvals.Contains(toolKey) || approvals.Contains($"mcp:{server}/*")
                    ? PermissionVerdict.Allow
                    : new(false, "mcp", permission.Title ?? permission.Command, toolKey);

            default:
                return new(false, "tool", permission.Title);
        }
    }

    /// <summary>
    /// “本会话允许此命令”的前缀：第一个词，加上紧随其后的子命令（不以 - 开头）。
    /// 例如 <c>git commit -m x</c> → <c>git commit</c>，<c>npm test</c> → <c>npm test</c>。
    /// </summary>
    public static string CommandPrefix(string command)
    {
        var words = command.Trim().Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        return words.Length switch
        {
            0 => "",
            1 => words[0],
            _ when words[1].StartsWith('-') || words[1].Contains('/') || words[1].Contains('.') => words[0],
            _ => words[0] + " " + words[1],
        };
    }

    /// <summary>只读命令的参数中出现的路径都在工作区内且不是敏感文件（否则只读命令也能绕过读取边界）。</summary>
    private static bool StaysInWorkspace(string command, WorkspaceRoots workspace)
    {
        foreach (var raw in command.Split([' ', '\t', '|', ';', '&'], StringSplitOptions.RemoveEmptyEntries))
        {
            var word = raw.Trim('\'', '"');
            var value = word.StartsWith('-') && word.IndexOf('=') is var eq and > 0 ? word[(eq + 1)..] : word;
            if (value.Length == 0 || value.StartsWith('-'))
            {
                continue;
            }
            var looksLikePath = value.StartsWith('/') || value.StartsWith('~') || value.Contains("..") || value.Contains('\\')
                                || (value.Length > 1 && value[1] == ':');
            var path = workspace.Resolve(value);
            if ((looksLikePath && !workspace.Contains(path)) || WorkspaceRoots.IsSensitive(path))
            {
                return false;
            }
        }
        return true;
    }

    private static string DirectoryKey(string path) =>
        (Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path).TrimEnd(Path.DirectorySeparatorChar);
}
