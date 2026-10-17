using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace TCode.Terminal.Protocol;

/// <summary>
/// 控制帧的 JSON 信封（04 §3.3）：<c>{"id","method","params"}</c> / <c>{"id","result"}</c> / <c>{"id","error"}</c> / <c>{"method","params"}</c>。
/// 解析时不建 JsonDocument：用 Utf8JsonReader 扫一遍，记下 params / result 的字节区间，再按方法名用对应的源生成类型反序列化。
/// </summary>
public readonly struct Envelope
{
    public uint? Id { get; init; }
    public string? Method { get; init; }

    /// <summary>params 或 result 的原始 JSON（没有时为空）。</summary>
    public ReadOnlyMemory<byte> Body { get; init; }

    public ErrorDto? Error { get; init; }

    public static Envelope Parse(ReadOnlyMemory<byte> json)
    {
        var reader = new Utf8JsonReader(json.Span, new JsonReaderOptions { MaxDepth = 16 });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new ProtocolException("envelope is not a JSON object");
        }
        uint? id = null;
        string? method = null;
        ReadOnlyMemory<byte> body = default;
        ErrorDto? error = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("id"u8))
            {
                reader.Read();
                id = reader.GetUInt32();
            }
            else if (reader.ValueTextEquals("method"u8))
            {
                reader.Read();
                method = reader.GetString();
            }
            else if (reader.ValueTextEquals("params"u8) || reader.ValueTextEquals("result"u8))
            {
                reader.Read();
                var start = (int)reader.TokenStartIndex;
                reader.Skip();
                body = json[start..(int)reader.BytesConsumed];
            }
            else if (reader.ValueTextEquals("error"u8))
            {
                reader.Read();
                var start = (int)reader.TokenStartIndex;
                reader.Skip();
                error = JsonSerializer.Deserialize(json.Span[start..(int)reader.BytesConsumed], PtyHostJsonContext.Default.ErrorDto);
            }
            else
            {
                reader.Read();
                reader.Skip();  // 未知字段忽略
            }
        }
        return new Envelope { Id = id, Method = method, Body = body, Error = error };
    }

    public T Read<T>(JsonTypeInfo<T> type)
    {
        if (Body.IsEmpty)
        {
            throw new PtyHostException(PtyErrors.InvalidParams, "missing params");
        }
        try
        {
            return JsonSerializer.Deserialize(Body.Span, type) ?? throw new PtyHostException(PtyErrors.InvalidParams, "null params");
        }
        catch (JsonException e)
        {
            throw new PtyHostException(PtyErrors.InvalidParams, e.Message);
        }
    }

    public static byte[] Request<T>(uint id, string method, T value, JsonTypeInfo<T> type) => Write(id, method, "params", value, type);

    public static byte[] Notification<T>(string method, T value, JsonTypeInfo<T> type) => Write(null, method, "params", value, type);

    public static byte[] Response<T>(uint id, T value, JsonTypeInfo<T> type) => Write(id, null, "result", value, type);

    public static byte[] ErrorResponse(uint id, int code, string message)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("id"u8, id);
            w.WritePropertyName("error"u8);
            JsonSerializer.Serialize(w, new ErrorDto(code, message), PtyHostJsonContext.Default.ErrorDto);
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] Write<T>(uint? id, string? method, string bodyName, T value, JsonTypeInfo<T> type)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            if (id is { } i)
            {
                w.WriteNumber("id"u8, i);
            }
            if (method is not null)
            {
                w.WriteString("method"u8, method);
            }
            w.WritePropertyName(bodyName);
            JsonSerializer.Serialize(w, value, type);
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    public override string ToString() => $"id={Id} method={Method} body={Encoding.UTF8.GetString(Body.Span)}";
}
