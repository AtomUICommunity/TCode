using TCode.Ai;
using TCode.Ai.Messages;
using TCode.Ai.Models;

namespace TCode.Agent.Compaction;

/// <summary>压缩的触发方式（docs/design/compaction.md §5）。</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<CompactionTrigger>))]
public enum CompactionTrigger
{
    /// <summary>用户执行 /compact。</summary>
    Manual,

    /// <summary>请求前上下文超过阈值。</summary>
    Auto,

    /// <summary>请求因上下文超窗失败后。</summary>
    Reactive,
}

/// <summary>交给压缩器的输入：当前会话的完整请求上下文。</summary>
/// <param name="Complete">
/// 以当前会话的模型、思考档位与 Key 发一次不带缓存写入的请求（分叉请求，§6.1）；返回完整的助手消息（失败时 StopReason 为 Error）。
/// </param>
public sealed record CompactionRequest(
    CompactionTrigger Trigger,
    string? Instructions,
    ModelInfo Model,
    string SystemPrompt,
    IReadOnlyList<ToolDefinition> Tools,
    IReadOnlyList<Message> Messages,
    long PreTokens,
    Func<LlmContext, int, CancellationToken, Task<AssistantMessage>> Complete)
{
    /// <summary>压缩后没有新的用户输入：模型应直接接着干活（工具轮之间的自动压缩、超窗后的重试）。</summary>
    public bool ResumesWork { get; init; }
}

/// <summary>压缩结果：新的开头系统提示与替换后的历史（摘要消息 + 重新附加的内容）。</summary>
public sealed record CompactionResult(string SystemPrompt, IReadOnlyList<Message> Messages, string Summary, long PostTokens);

/// <summary>压缩失败（摘要请求失败、没有可压缩的内容等）；Message 面向用户。</summary>
public sealed class CompactionException(string message) : Exception(message);

/// <summary>
/// 生成摘要并构造替换历史。实现负责持久化（压缩记录、新的系统提示）；返回的消息由 Agent 追加，
/// 随 MessageEnd 照常写入会话文件。
/// </summary>
public interface ICompactor
{
    Task<CompactionResult> CompactAsync(CompactionRequest request, CancellationToken cancellationToken);
}
