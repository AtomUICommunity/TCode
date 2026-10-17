using TKCode.Ai.Models;

namespace TKCode.Agent.Coding.Assets;

/// <summary>
/// 内置的提示词型 command（/init、/commit）：输入框里和自定义 command 一样插入 tag，发送时 Host 把正文发给模型
/// （docs/design/composer.md §9.3）。正文是嵌入的资源（Assets/BuiltinPrompts），
/// <c>{{model_name}}</c> 与 <c>{{model_id}}</c> 在展开时替换为当前会话的模型。
/// </summary>
public static class BuiltinPromptCommands
{
    private static readonly Dictionary<string, string> Resources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["init"] = "TKCode.Agent.Coding.Assets.BuiltinPrompts.init.md",
        ["commit"] = "TKCode.Agent.Coding.Assets.BuiltinPrompts.commit.md",
    };

    public static bool Contains(string name) => Resources.ContainsKey(name);

    /// <summary>展开后的提示词；<paramref name="name"/> 不是内置命令时返回 null。</summary>
    public static string? Render(string name, ModelInfo model)
    {
        if (!Resources.TryGetValue(name, out var resource))
        {
            return null;
        }
        using var stream = typeof(BuiltinPromptCommands).Assembly.GetManifestResourceStream(resource)
                           ?? throw new InvalidOperationException($"Missing built-in prompt resource {resource}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd()
            .Replace("{{model_name}}", model.Name, StringComparison.Ordinal)
            .Replace("{{model_id}}", model.Id, StringComparison.Ordinal)
            .Trim();
    }
}
