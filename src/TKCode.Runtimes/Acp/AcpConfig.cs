using System.Text.Json;
using TKCode.Ai.Models;
using TKCode.Runtimes.Json;

namespace TKCode.Runtimes.Acp;

/// <summary>ACP 会话配置项（规格 §10.1）。select 的分组选项被展平，保留分组名。未知 type 的选项被跳过。</summary>
internal sealed record AcpConfigOption(string Id, string Name, string? Category, bool IsBoolean, string? CurrentValue, bool CurrentBool,
    IReadOnlyList<AcpConfigValue> Values);

internal sealed record AcpConfigValue(string Value, string Name, string? Description, string? Group);

/// <summary>会话配置的完整状态：config options，以及旧接口 modes / models（规格 §10.5、§10.6）。</summary>
internal sealed class AcpConfigState
{
    public IReadOnlyList<AcpConfigOption> Options { get; private set; } = [];
    public string? LegacyCurrentModel { get; private set; }
    public IReadOnlyList<AcpConfigValue> LegacyModels { get; private set; } = [];

    public AcpConfigOption? ByCategory(string category) =>
        Options.FirstOrDefault(o => !o.IsBoolean && string.Equals(o.Category, category, StringComparison.Ordinal));

    /// <summary>应用会话响应（new / load / resume）或 set_config_option 的响应：字段出现时整体替换。</summary>
    public void ApplyResponse(JsonElement result)
    {
        if (result.Prop("configOptions") is { ValueKind: JsonValueKind.Array } options)
        {
            Options = ParseOptions(options);
        }
        if (result.Obj("models") is { } models)
        {
            LegacyCurrentModel = models.Str("currentModelId");
            LegacyModels = models.Arr("availableModels")
                .Select(m => m.Str("modelId") is { } id ? new AcpConfigValue(id, m.Str("name") ?? id, m.Str("description"), null) : null)
                .OfType<AcpConfigValue>().ToArray();
        }
    }

    public void ApplyUpdate(JsonElement update) => ApplyResponse(update);

    public void SetLegacyModel(string modelId) => LegacyCurrentModel = modelId;

    private static AcpConfigOption[] ParseOptions(JsonElement array)
    {
        var result = new List<AcpConfigOption>();
        foreach (var o in array.EnumerateArray())
        {
            if (o.Str("id") is not { } id)
            {
                continue;
            }
            var name = o.Str("name") ?? id;
            switch (o.Str("type"))
            {
                case "select":
                    var values = new List<AcpConfigValue>();
                    foreach (var item in o.Arr("options"))
                    {
                        if (item.Str("group") is { } group)
                        {
                            values.AddRange(item.Arr("options").Select(v => Value(v, group)).OfType<AcpConfigValue>());
                        }
                        else if (Value(item, null) is { } value)
                        {
                            values.Add(value);
                        }
                    }
                    result.Add(new AcpConfigOption(id, name, o.Str("category"), false, o.Str("currentValue"), false, values));
                    break;
                case "boolean":
                    result.Add(new AcpConfigOption(id, name, o.Str("category"), true, null, o.Bool("currentValue"), []));
                    break;
            }
        }
        return result.ToArray();
    }

    private static AcpConfigValue? Value(JsonElement v, string? group) =>
        v.Str("value") is { } value ? new AcpConfigValue(value, v.Str("name") ?? value, v.Str("description"), group) : null;

    // ---------------- 与 TKCode 的对应 ----------------

    /// <summary>可选模型：优先 category=model 的配置项，其次旧的 models 字段。</summary>
    public IReadOnlyList<AcpConfigValue> Models => ByCategory("model")?.Values ?? LegacyModels;

    public string? CurrentModel => ByCategory("model")?.CurrentValue ?? LegacyCurrentModel;

    /// <summary>思考深度选项值中能对应到 TKCode 档位名称的部分（按 value，其次按 name 匹配）。</summary>
    public IReadOnlyList<string> ReasoningEfforts =>
        ByCategory("thought_level") is { } option
            ? option.Values.Select(v => EffortName(v)).OfType<string>().Distinct().ToArray()
            : [];

    /// <summary>为 TKCode 档位选择 thought_level 的取值；没有该配置项时返回 null。</summary>
    public string? ChooseThoughtValue(ThinkingLevel level)
    {
        if (ByCategory("thought_level") is not { Values.Count: > 0 } option)
        {
            return null;
        }
        var mapped = option.Values.Select(v => (Value: v.Value, Effort: EffortName(v))).Where(v => v.Effort is not null).ToArray();
        if (mapped.Length == 0)
        {
            return null;
        }
        var effort = ThinkingLevels.ChooseEffort(level, mapped.Select(m => m.Effort!).ToArray());
        return mapped.First(m => m.Effort == effort).Value;
    }

    private static readonly string[] Ladder = ["none", "minimal", "low", "medium", "high", "xhigh", "max"];

    private static string? EffortName(AcpConfigValue v)
    {
        foreach (var candidate in new[] { v.Value, v.Name })
        {
            var key = candidate.Trim().ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");
            key = key switch
            {
                "off" or "disabled" => "none",
                "extrahigh" => "xhigh",
                "maximum" => "max",
                _ => key,
            };
            if (Ladder.Contains(key))
            {
                return key;
            }
        }
        return null;
    }
}
