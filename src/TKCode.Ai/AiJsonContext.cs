using System.Text.Json.Serialization;
using TKCode.Ai.Messages;
using TKCode.Ai.Models;

namespace TKCode.Ai;

/// <summary>规范消息模型的源生成序列化上下文（AOT 友好）。</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(Message))]
[JsonSerializable(typeof(ContentBlock))]
[JsonSerializable(typeof(ModelInfo))]
[JsonSerializable(typeof(Usage))]
public sealed partial class AiJsonContext : JsonSerializerContext;
