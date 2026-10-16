using System.Buffers;
using System.Text.Json;

namespace TCode.Runtimes.Json;

/// <summary>用 <see cref="Utf8JsonWriter"/> 构造 JSON（外部协议的载荷形状多变，直接写比定义 DTO 更清晰，且对 AOT 安全）。</summary>
internal static class JsonBuilder
{
    public static JsonElement Object(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        // 写给 CLI 的 stdin 与会话文件，不进入 HTML：用宽松转义保留中文与 '+' 等字符，便于阅读和调试
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }

    public static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // ---------------- 宽松读取 ----------------

    public static string? Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static bool Bool(this JsonElement e, string name, bool fallback = false) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : fallback;

    public static long? Long(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)
            ? n
            : null;

    public static double? Double(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    /// <summary>字段存在且不是 null（补丁语义：省略与 null 都表示不变）。</summary>
    public static JsonElement? Prop(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            ? v
            : null;

    public static JsonElement? Obj(this JsonElement e, string name) =>
        e.Prop(name) is { ValueKind: JsonValueKind.Object } v ? v : null;

    public static IEnumerable<JsonElement> Arr(this JsonElement e, string name) =>
        e.Prop(name) is { ValueKind: JsonValueKind.Array } v ? v.EnumerateArray() : [];

    /// <summary>能力对象：非 null 的对象（包括 <c>{}</c>）表示支持。</summary>
    public static bool HasObject(this JsonElement e, string name) => e.Obj(name) is not null;
}
