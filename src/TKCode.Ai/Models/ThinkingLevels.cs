namespace TKCode.Ai.Models;

public static class ThinkingLevels
{
    private static readonly string[] Ladder = ["none", "minimal", "low", "medium", "high", "xhigh", "max"];

    /// <summary>Anthropic effort 取值（规格 docs/protocols/anthropic-messages.md §2.8；不支持 none / minimal）。</summary>
    internal static readonly string[] AnthropicEfforts = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>
    /// 模型选择器中可选的档位：只列出会按原样生效的档位，避免出现选了也会被就近替换的选项。
    /// 只有开关的厂商（Kimi K2.x、DashScope 等）表示为 Off / High。
    /// </summary>
    public static IReadOnlyList<ThinkingLevel> Available(ModelInfo model)
    {
        if (!model.Reasoning)
        {
            return [];
        }
        switch (model.Api)
        {
            case KnownApis.AnthropicMessages:
            {
                var c = CompatResolver.ResolveAnthropic(model);
                return c.Thinking switch
                {
                    AnthropicThinkingMode.None => [],
                    AnthropicThinkingMode.Budget => [ThinkingLevel.Off, ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High],
                    _ when !c.SupportsEffort => c.SupportsDisabledThinking ? [ThinkingLevel.Off, ThinkingLevel.High] : [],
                    _ => WithOff(c.SupportsDisabledThinking, FromEfforts(model.Compat?.ReasoningEfforts ?? AnthropicEfforts)),
                };
            }
            case KnownApis.OpenAIChatCompletions:
            {
                var c = CompatResolver.ResolveChat(model);
                return c.ThinkingFormat switch
                {
                    ChatThinkingFormat.None => [],
                    ChatThinkingFormat.ThinkingType or ChatThinkingFormat.EnableThinking =>
                        c.SupportsDisabledThinking ? [ThinkingLevel.Off, ThinkingLevel.High] : [],
                    ChatThinkingFormat.ThinkingTypeAndEffort => WithOff(c.SupportsDisabledThinking, FromEfforts(c.ReasoningEfforts ?? Ladder)),
                    _ => FromEfforts(c.ReasoningEfforts ?? Ladder),
                };
            }
            default:
                return FromEfforts(model.Compat?.ReasoningEfforts ?? Ladder);
        }
    }

    /// <summary>新会话的默认档位：优先 High，否则取可用档位中最高的一档。</summary>
    public static ThinkingLevel Default(ModelInfo model)
    {
        var available = Available(model);
        return available.Count == 0 ? ThinkingLevel.Off
            : available.Contains(ThinkingLevel.High) ? ThinkingLevel.High
            : available[^1];
    }

    private static ThinkingLevel[] FromEfforts(IReadOnlyList<string> efforts) =>
        // Ladder 与 ThinkingLevel 逐位对应（none ↔ Off）。
        Ladder.Where(efforts.Contains).Select(e => (ThinkingLevel)Array.IndexOf(Ladder, e)).ToArray();

    private static ThinkingLevel[] WithOff(bool canDisable, ThinkingLevel[] levels)
    {
        var on = levels.Where(l => l != ThinkingLevel.Off);
        return canDisable ? [ThinkingLevel.Off, .. on] : on.ToArray();
    }

    /// <summary>
    /// 在模型支持的取值中为 <paramref name="level"/> 选择 effort（规格：省略字段不等于关闭）。
    /// Off → "none"（不支持时取最低档）；其它档位不支持时先向上、再向下就近取值。
    /// </summary>
    public static string ChooseEffort(ThinkingLevel level, IReadOnlyList<string>? supported)
    {
        var allowed = supported is { Count: > 0 } ? supported : Ladder;
        var wanted = ToEffort(level) ?? "none";
        if (allowed.Contains(wanted))
        {
            return wanted;
        }
        var start = Array.IndexOf(Ladder, wanted);
        if (wanted == "none")
        {
            return Ladder.First(allowed.Contains);
        }
        for (var i = start + 1; i < Ladder.Length; i++)
        {
            if (allowed.Contains(Ladder[i]))
            {
                return Ladder[i];
            }
        }
        for (var i = start - 1; i >= 0; i--)
        {
            if (allowed.Contains(Ladder[i]))
            {
                return Ladder[i];
            }
        }
        return allowed[0];
    }

    /// <summary>映射到 OpenAI / Anthropic 的 effort 字符串；Off 返回 null。</summary>
    public static string? ToEffort(ThinkingLevel level) => level switch
    {
        ThinkingLevel.Off => null,
        ThinkingLevel.Minimal => "minimal",
        ThinkingLevel.Low => "low",
        ThinkingLevel.Medium => "medium",
        ThinkingLevel.High => "high",
        ThinkingLevel.XHigh => "xhigh",
        _ => "max",
    };

    /// <summary>Anthropic effort 不支持 minimal，向上取 low。</summary>
    public static string? ToAnthropicEffort(ThinkingLevel level) =>
        level == ThinkingLevel.Minimal ? "low" : ToEffort(level);

    /// <summary>旧式 budget_tokens 模式的思考预算。</summary>
    public static int ToBudgetTokens(ThinkingLevel level) => level switch
    {
        ThinkingLevel.Minimal => 1024,
        ThinkingLevel.Low => 2048,
        ThinkingLevel.Medium => 8192,
        _ => 16384,
    };
}
