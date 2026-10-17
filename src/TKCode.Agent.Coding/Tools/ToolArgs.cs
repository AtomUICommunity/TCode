using System.Text.Json;

namespace TKCode.Agent.Coding.Tools;

/// <summary>工具参数读取：缺少必填参数或类型不对时抛出 <see cref="ToolArgumentException"/>（循环会把它转成错误结果）。</summary>
internal static class ToolArgs
{
    public static string Required(this JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new ToolArgumentException($"Missing required string parameter '{name}'.");

    public static string? String(this JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static int? Int(this JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v))
        {
            return null;
        }
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var i) => i,
            JsonValueKind.Number => (int)Math.Clamp(v.GetDouble(), int.MinValue, int.MaxValue),
            JsonValueKind.String when int.TryParse(v.GetString(), out var i) => i,
            JsonValueKind.Null => null,
            _ => throw new ToolArgumentException($"Parameter '{name}' must be an integer."),
        };
    }

    public static bool? Bool(this JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v))
        {
            return null;
        }
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(v.GetString(), out var b) => b,
            JsonValueKind.Null => null,
            _ => throw new ToolArgumentException($"Parameter '{name}' must be a boolean."),
        };
    }

    public static IReadOnlyList<string>? StringArray(this JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (v.ValueKind == JsonValueKind.String)
        {
            return [v.GetString()!];
        }
        if (v.ValueKind != JsonValueKind.Array)
        {
            throw new ToolArgumentException($"Parameter '{name}' must be an array of strings.");
        }
        return v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray();
    }
}

public sealed class ToolArgumentException(string message) : Exception(message);
