namespace TKCode.Ai;

/// <summary>
/// 线协议标识。用开放字符串而不是枚举，以便扩展自定义协议。
/// 一个协议对应一个 <see cref="IWireApi"/> 适配器，厂商差异由兼容配置描述。
/// </summary>
public static class KnownApis
{
    public const string OpenAIChatCompletions = "openai-completions";
    public const string OpenAIResponses = "openai-responses";
    public const string AnthropicMessages = "anthropic-messages";

    /// <summary>本地脚本化的模拟协议，用于 UI 开发与测试。</summary>
    public const string Fake = "fake";
}
