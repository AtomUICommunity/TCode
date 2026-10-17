using System.Text;
using TCode.Agent.Coding.Prompt;
using TCode.Agent.Coding.Tools.Files;
using TCode.Agent.Coding.Tools.Tasks;
using TCode.Agent.Coding.Workspace;
using TCode.Ai.Models;

namespace TCode.Agent.Coding.Tools;

/// <summary>同一会话内所有内置工具共享的状态。工具实例按会话创建，并持有这份上下文。</summary>
public sealed class ToolContext(WorkspaceRoots workspace, string sessionId, BackgroundTasks? backgroundTasks = null, TaskList? tasks = null)
{
    private readonly HashSet<string> _nestedContextFiles = new(PathUtil.Comparer);
    private readonly Lock _gate = new();

    public WorkspaceRoots Workspace { get; } = workspace;

    public string Cwd => Workspace.Cwd;

    /// <summary>当前会话 id（子 Agent 为子会话自己的 id）。</summary>
    public string SessionId { get; } = sessionId;

    /// <summary>命令输出等临时文件的目录：~/.tcode/tmp/&lt;会话 id&gt;。</summary>
    public string TempDirectory => Path.Combine(TCodePaths.Home, "tmp", SessionId);

    /// <summary>后台任务（主会话与子 Agent 共用）。</summary>
    public BackgroundTasks BackgroundTasks { get; } = backgroundTasks ?? new BackgroundTasks();

    /// <summary>任务清单（主会话与子 Agent 共用）。</summary>
    public TaskList Tasks { get; } = tasks ?? new TaskList();

    /// <summary>宿主提供的外部能力（网页搜索、子 Agent 协调等）；为 null 的能力对应的工具不会出现。</summary>
    public ToolServices Services { get; init; } = ToolServices.None;

    /// <summary>本会话的动态工具来源（MCP）；会话关闭时释放。</summary>
    public IDynamicToolSource? DynamicTools { get; set; }

    /// <summary>会话的 role（类似 Cursor 的模式）：过滤工具并加入 role 节，在下一批用户输入前生效。null 等同于 agent。</summary>
    public Assets.SessionRole? Role { get; set; }

    /// <summary>当前模型（模型可在会话中途切换）。</summary>
    public Func<ModelInfo?> CurrentModel { get; set; } = () => null;

    /// <summary>同一文件的写入串行。</summary>
    public FileMutationQueue Mutations { get; } = new();

    /// <summary>当前模型能否查看图片（模型可在会话中途切换）。</summary>
    public Func<bool> ModelSupportsImages { get; set; } = () => true;

    /// <summary>读过的文件（先读后改、过期检测）。</summary>
    public FileStateTracker Files { get; } = new();

    /// <summary>
    /// 子目录的上下文文件按需加载：访问 cwd 子目录中的文件时，返回途中尚未加载过的 AGENTS.md 等，
    /// 作为 &lt;system-reminder&gt; 附在工具结果后面。cwd 及其上级的文件已在系统提示中，不再重复。
    /// </summary>
    public string? NestedInstructionsFor(string path)
    {
        var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (dir is null || !PathUtil.IsUnder(PathUtil.RealPath(dir), PathUtil.RealPath(Cwd)) || PathUtil.SamePath(dir, Cwd))
        {
            return null;
        }
        var found = new List<string>();
        for (var current = new DirectoryInfo(dir); current is not null && !PathUtil.SamePath(current.FullName, Cwd); current = current.Parent)
        {
            if (SystemPromptBuilder.ContextFileNames.Select(n => Path.Combine(current.FullName, n)).FirstOrDefault(File.Exists) is { } file)
            {
                lock (_gate)
                {
                    if (_nestedContextFiles.Add(PathUtil.RealPath(file)))
                    {
                        found.Add(file);
                    }
                }
            }
        }
        if (found.Count == 0)
        {
            return null;
        }
        found.Reverse();
        var sb = new StringBuilder("<system-reminder>\n");
        foreach (var file in found)
        {
            string content;
            try
            {
                content = File.ReadAllText(file).Trim();
            }
            catch (IOException)
            {
                continue;
            }
            if (content.Length > 16 * 1024)
            {
                content = content[..(16 * 1024)] + "\n[truncated]";
            }
            sb.Append($"Instructions from {file} (they apply to files under {Path.GetDirectoryName(file)}):\n").Append(content).Append("\n\n");
        }
        return sb.ToString().TrimEnd() + "\n</system-reminder>";
    }
}
