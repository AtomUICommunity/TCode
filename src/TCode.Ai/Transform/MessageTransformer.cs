using System.Security.Cryptography;
using System.Text;
using TCode.Ai.Messages;
using TCode.Ai.Models;

namespace TCode.Ai.Transform;

/// <summary>
/// 发送前的历史预处理（跨模型交接）。规则：
/// <list type="bullet">
/// <item>出错 / 中止的助手消息不回放（其中的半截推理或工具调用会导致 API 报错）。</item>
/// <item>同一 (provider, api, model) 的思考块与厂商状态原样保留；来自其它模型的思考转成普通文本，redacted 思考与厂商状态丢弃。</item>
/// <item>工具调用 id 按目标协议规范化，并同步改写对应的工具结果。</item>
/// <item>没有结果的工具调用（孤儿）补一条错误结果；夹在工具调用与结果之间的系统消息挪到结果之后。</item>
/// </list>
/// 注意：这里只构造发往 API 的视图，绝不修改会话历史本身（历史只追加）。
/// </summary>
public static class MessageTransformer
{
    public static IReadOnlyList<Message> Prepare(IReadOnlyList<Message> messages, ModelInfo target, Func<string, string> normalizeToolCallId,
        bool sameProviderReasoning = false)
    {
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var converted = new List<Message>(messages.Count);

        foreach (var message in messages)
        {
            switch (message)
            {
                case AssistantMessage { StopReason: StopReason.Error or StopReason.Aborted }:
                    continue;
                case AssistantMessage assistant:
                    converted.Add(ConvertAssistant(assistant, target, normalizeToolCallId, idMap, sameProviderReasoning));
                    break;
                case ToolResultMessage result when idMap.TryGetValue(result.ToolCallId, out var mapped):
                    converted.Add(result with { ToolCallId = mapped });
                    break;
                default:
                    converted.Add(message);
                    break;
            }
        }
        return FixToolResultOrdering(converted);
    }

    public static bool IsSameModel(AssistantMessage message, ModelInfo target) =>
        message.Provider == target.Provider && message.Api == target.Api && message.Model == target.Id;

    /// <summary>同一 Provider + 同一协议（不要求同一模型）。用于明文推理的跨模型回放。</summary>
    public static bool IsSameProvider(AssistantMessage message, ModelInfo target) =>
        message.Provider == target.Provider && message.Api == target.Api;

    private static AssistantMessage ConvertAssistant(
        AssistantMessage message, ModelInfo target, Func<string, string> normalize, Dictionary<string, string> idMap, bool sameProviderReasoning)
    {
        var same = sameProviderReasoning ? IsSameProvider(message, target) : IsSameModel(message, target);
        var blocks = new List<ContentBlock>(message.Content.Count);
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case ThinkingContent { Redacted: true } when !same:
                    break;
                case ThinkingContent thinking when same && thinking.Signature is not null:
                    blocks.Add(thinking);
                    break;
                case ThinkingContent thinking when string.IsNullOrWhiteSpace(thinking.Thinking):
                    break;
                case ThinkingContent thinking when same:
                    blocks.Add(thinking);
                    break;
                case ThinkingContent thinking:
                    // 不加 <thinking> 标签，避免模型模仿标签格式输出。
                    blocks.Add(new TextContent(thinking.Thinking));
                    break;
                case TextContent text when !same:
                    blocks.Add(text with { ProviderState = null });
                    break;
                case ToolCallContent call when !same:
                    var id = normalize(call.Id);
                    if (id != call.Id)
                    {
                        idMap[call.Id] = id;
                    }
                    blocks.Add(call with { Id = id, ProviderState = null });
                    break;
                case ToolCallContent call:
                    var normalized = normalize(call.Id);
                    if (normalized != call.Id)
                    {
                        idMap[call.Id] = normalized;
                        blocks.Add(call with { Id = normalized });
                    }
                    else
                    {
                        blocks.Add(call);
                    }
                    break;
                default:
                    blocks.Add(block);
                    break;
            }
        }
        return message with { Content = blocks };
    }

    private static List<Message> FixToolResultOrdering(List<Message> messages)
    {
        var result = new List<Message>(messages.Count);
        var pending = new List<ToolCallContent>();
        var held = new List<Message>();

        void Flush()
        {
            foreach (var orphan in pending)
            {
                result.Add(new ToolResultMessage(orphan.Id, orphan.Name, [new TextContent("No result provided")]) { IsError = true });
            }
            pending.Clear();
            result.AddRange(held);
            held.Clear();
        }

        foreach (var message in messages)
        {
            switch (message)
            {
                case ToolResultMessage toolResult:
                    // 没有对应调用的工具结果（所属助手消息已被跳过，或中间插入了用户消息）必须丢弃，
                    // 否则会成为孤儿 tool 消息导致 400（Chat 规格 §11-10/15）。
                    if (pending.RemoveAll(c => c.Id == toolResult.ToolCallId) == 0)
                    {
                        break;
                    }
                    result.Add(toolResult);
                    if (pending.Count == 0)
                    {
                        result.AddRange(held);
                        held.Clear();
                    }
                    break;
                case SystemMessage system when pending.Count > 0:
                    held.Add(system);
                    break;
                case AssistantMessage assistant:
                    Flush();
                    result.Add(assistant);
                    pending.AddRange(assistant.Content.OfType<ToolCallContent>());
                    break;
                default:
                    Flush();
                    result.Add(message);
                    break;
            }
        }
        // 末尾悬空的工具调用不补结果：Agent 循环此时正要执行它们。
        result.AddRange(held);
        return result;
    }

    // ---- 各协议的工具调用 id 规则 ----

    /// <summary>Anthropic：^[a-zA-Z0-9_-]{1,64}$。</summary>
    public static string AnthropicToolCallId(string id)
    {
        var sb = new StringBuilder(id.Length);
        foreach (var c in id)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        }
        var clean = sb.Length == 0 ? "toolu_" + ShortHash(id) : sb.ToString();
        return clean.Length <= 64 ? clean : clean[..40] + "_" + ShortHash(id);
    }

    /// <summary>OpenAI Chat Completions：长度不超过 40。</summary>
    public static string ChatToolCallId(string id) =>
        id.Length is > 0 and <= 40 ? id : "call_" + ShortHash(id);

    /// <summary>OpenAI Responses：call_id 长度不超过 64。</summary>
    public static string ResponsesToolCallId(string id) =>
        id.Length is > 0 and <= 64 ? id : "call_" + ShortHash(id);

    private static string ShortHash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];
}
