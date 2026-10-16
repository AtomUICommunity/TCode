namespace TCode.Runtimes.Acp;

/// <summary>
/// 可直接启动的原生 ACP Agent（规格 §17：只启用 PATH 上能直接 exec 的可执行文件，不运行 npx / uvx）。
/// Claude 与 Codex 走各自的原生协议，不在此表中。
/// </summary>
public sealed record AcpAgentDefinition(
    string Id,
    string DisplayName,
    string Executable,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string?>? Environment = null,
    /// <summary>可以自动调用 authenticate 的认证方法（复用已有凭据、不弹出交互，规格 §4.3）。</summary>
    IReadOnlyList<string>? SilentAuthMethods = null)
{
    public string RuntimeId => "acp:" + Id;
    public string OverrideVariable => "TCODE_ACP_" + Id.ToUpperInvariant().Replace('-', '_') + "_PATH";
}

public static class AcpAgents
{
    /// <summary>规格 §17.2 的 A 类与已确认可执行文件名的 B 类条目。</summary>
    public static IReadOnlyList<AcpAgentDefinition> Known { get; } =
    [
        new("cursor", "Cursor Agent", "cursor-agent", ["acp"], SilentAuthMethods: ["cursor_login"]),
        new("gemini", "Gemini CLI", "gemini", ["--acp"]),
        new("copilot", "GitHub Copilot", "copilot", ["--acp"]),
        new("opencode", "OpenCode", "opencode", ["acp"]),
        new("goose", "goose", "goose", ["acp"]),
        new("kimi", "Kimi CLI", "kimi", ["acp"]),
        new("qwen", "Qwen Code", "qwen", ["--acp"]),
        new("devin", "Devin", "devin", ["acp"]),
        new("kilo", "Kilo", "kilo", ["acp"]),
        new("grok", "Grok Build", "grok", ["agent", "stdio"]),
        new("auggie", "Auggie", "auggie", ["--acp"], new Dictionary<string, string?> { ["AUGMENT_DISABLE_AUTO_UPDATE"] = "1" }),
        new("cline", "Cline", "cline", ["--acp"]),
        new("droid", "Factory Droid", "droid", ["exec", "--output-format", "acp-daemon"],
            new Dictionary<string, string?> { ["DROID_DISABLE_AUTO_UPDATE"] = "true", ["FACTORY_DROID_AUTO_UPDATE_ENABLED"] = "false" }),
        new("qoder", "Qoder CLI", "qodercli", ["--acp"]),
        new("junie", "Junie", "junie", ["--acp=true"]),
        new("mistral-vibe", "Mistral Vibe", "vibe-acp", []),
        new("cortex", "Cortex Code", "cortex", ["acp", "serve"]),
        new("stakpak", "Stakpak", "stakpak", ["acp"]),
        new("vtcode", "VT Code", "vtcode", ["acp"]),
        new("amp", "Amp", "amp-acp", []),
    ];

    /// <summary>
    /// 不能当作 ACP Agent 启动的可执行文件：VS Code 的 Copilot 包装脚本找不到真正的 CLI 时，
    /// 会在 stdout 打印安装提示并读 stdin（规格 §17.1 第 3 条）。
    /// </summary>
    public static bool IsUsable(string executablePath)
    {
        var resolved = executablePath;
        try
        {
            resolved = new FileInfo(executablePath).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? executablePath;
        }
        catch (IOException)
        {
        }
        var normalized = resolved.Replace('\\', '/');
        return !normalized.Contains("/globalStorage/github.copilot-chat/copilotCli/", StringComparison.OrdinalIgnoreCase);
    }
}
