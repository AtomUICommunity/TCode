using System.Text.RegularExpressions;

namespace TKCode.Desktop.Icons;

/// <summary>
/// 模型 ID / Provider ID → LobeHub 图标键。
/// 规则移植自 lobe-icons 的 src/features/modelConfig.ts（按顺序匹配，第一条命中生效；只保留本地有图标的品牌）
/// 与 providerConfig.tsx（Provider ID 精确匹配，含少量别名）。
/// </summary>
public static partial class ModelIcons
{
    private static readonly (Regex Pattern, string Key)[] ModelRules =
    [
        (Rule("gpt-3|gpt-4|gpt-5|gpt-6|gpt-oss"), "openai"),
        (Rule("o1-|^o1|/o1|o3-|^o3|/o3|o4-|^o4|/o4"), "openai"),
        (Rule("codex|davinci|babbage|whisper-|tts-|text-embedding-|omni-moderation|computer-use|chatgpt"), "openai"),
        (Rule("^gpt-|/gpt-|openai"), "openai"),
        (Rule("^glm-5|/glm-5|/glm5|-glm-4|^glm-4|/glm-4|/glm4|-glm-5"), "zai"),
        (Rule("^glm-|/glm-|chatglm|-glm-"), "chatglm"),
        (Rule("claude"), "claude"),
        (Rule("anthropic"), "anthropic"),
        (Rule("titan"), "aws"),
        (Rule("nemotron|openreasoning|nemoretriever|neva-|nv-"), "nvidia"),
        (Rule("llama|/l3"), "meta"),
        (Rule("gemini"), "gemini"),
        (Rule("gemma"), "gemma"),
        (Rule("kimi|moonshot"), "moonshot"),
        (Rule(@"qwen|qwq|qvq|wanx|wan\d/|wan\d\.\d-|tongyi|gte-rerank"), "qwen"),
        (Rule("minimax|abab"), "minimax"),
        (Rule("mistral|mixtral|codestral|devstral|pixtral|magistral|ministral|voxtral"), "mistral"),
        (Rule("pplx|sonar"), "perplexity"),
        (Rule("^yi-|/yi-|-yi-"), "yi"),
        (Rule("^openrouter"), "openrouter"),
        (Rule("command"), "cohere"),
        (Rule("step"), "stepfun"),
        (Rule("baichuan"), "baichuan"),
        (Rule("ernie|irag"), "wenxin"),
        (Rule("^ep-|doubao-"), "doubao"),
        (Rule("hunyuan|hy3"), "hunyuan"),
        (Rule("skylark|seed-|bytedance"), "bytedance"),
        (Rule("wizardlm|/phi-|^phi-|-phi-|mai-|microsoft"), "microsoft"),
        (Rule("^grok-|/grok-"), "grok"),
        (Rule("(^|/)muse-(spark|wonder)($|-)"), "meta"),
        (Rule("deepseek"), "deepseek"),
        (Rule("^veo-|/veo-|^veo3"), "vertexai"),
        (Rule("google|learnlm"), "google"),
        (Rule("baidu|qianfan"), "baidu"),
        (Rule("^composer|/composer|-composer"), "cursor"),
    ];

    private static readonly Dictionary<string, string> ProviderAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chatgpt"] = "openai",
        ["github-copilot"] = "githubcopilot",
        ["qwen"] = "alibabacloud",
        ["dashscope"] = "alibabacloud",
        ["meta"] = "metaai",
        ["zhipuai"] = "zhipu",
        ["claude-code"] = "claudecode",
        ["alibaba"] = "alibabacloud",
        ["kimi"] = "moonshot",
        ["google-vertex"] = "vertexai",
        ["amazon-bedrock"] = "bedrock",
    };

    /// <summary>模型图标；无匹配时返回 null（显示通用图标）。</summary>
    public static string? ForModel(string modelId)
    {
        var id = modelId.ToLowerInvariant();
        foreach (var (pattern, key) in ModelRules)
        {
            if (pattern.IsMatch(id))
            {
                return key;
            }
        }
        return null;
    }

    /// <summary>Provider 图标：ID 与图标键相同或命中别名时返回，否则 null。</summary>
    public static string? ForProvider(string providerId)
    {
        // 目录中同一厂商常有地区 / 套餐变体（moonshotai-cn、zhipuai-coding-plan…），依次去掉后缀再匹配
        var id = providerId.ToLowerInvariant();
        foreach (var candidate in Candidates(id))
        {
            var key = ProviderAliases.GetValueOrDefault(candidate) ?? candidate;
            if (ProviderIconPaths.Mono.ContainsKey(key))
            {
                return key;
            }
        }
        return null;
    }

    private static IEnumerable<string> Candidates(string id)
    {
        // ACP 运行时：acp:<agent>
        if (id.StartsWith("acp:", StringComparison.Ordinal))
        {
            id = id[4..];
        }
        yield return id;
        var trimmed = VariantSuffix().Replace(id, "");
        yield return trimmed;
        if (trimmed.EndsWith("ai", StringComparison.Ordinal) && trimmed.Length > 4)
        {
            yield return trimmed[..^2];
        }
        if (trimmed.IndexOf('-') is > 0 and var dash)
        {
            yield return trimmed[..dash];
        }
    }

    [GeneratedRegex("(-(cn|china|global|intl|international|coding-plan|token-plan|step-plan|plan|ai|cloud))+$")]
    private static partial Regex VariantSuffix();

    private static Regex Rule(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
