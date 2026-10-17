using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace TCode.Config;

/// <summary>
/// 读取 TOML 的一个子集（用于导入 Codex 的 ~/.codex/config.toml，docs/design/mcp.md §12.1）：
/// 表与数组表头、点号键、基本 / 字面量字符串（含多行）、整数、浮点、布尔、数组（可跨行）、内联表、# 注释。
/// 日期时间按字符串保留。结果是 JsonObject 树。
/// </summary>
public static class McpToml
{
    public static JsonObject Parse(string text) => new Parser(text).Parse();

    private sealed class Parser(string text)
    {
        private int _pos;
        private int _line = 1;

        public JsonObject Parse()
        {
            var root = new JsonObject();
            var current = root;
            while (true)
            {
                SkipWhitespaceAndNewlines();
                if (_pos >= text.Length)
                {
                    return root;
                }
                var c = text[_pos];
                if (c == '#')
                {
                    SkipComment();
                    continue;
                }
                if (c == '[')
                {
                    var arrayTable = Peek(1) == '[';
                    _pos += arrayTable ? 2 : 1;
                    var path = ParseKeyPath();
                    SkipInlineWhitespace();
                    Expect(']');
                    if (arrayTable)
                    {
                        Expect(']');
                    }
                    current = arrayTable ? AppendArrayTable(root, path) : Table(root, path);
                    EndOfLine();
                    continue;
                }
                var keys = ParseKeyPath();
                SkipInlineWhitespace();
                Expect('=');
                SkipInlineWhitespace();
                var value = ParseValue();
                var target = keys.Count > 1 ? Table(current, keys.Take(keys.Count - 1).ToList()) : current;
                target[keys[^1]] = value;
                EndOfLine();
            }
        }

        private static JsonObject Table(JsonObject root, IReadOnlyList<string> path)
        {
            var node = root;
            foreach (var key in path)
            {
                switch (node[key])
                {
                    case JsonObject obj:
                        node = obj;
                        break;
                    case JsonArray { Count: > 0 } array when array[^1] is JsonObject last:
                        node = last;
                        break;
                    default:
                        var created = new JsonObject();
                        node[key] = created;
                        node = created;
                        break;
                }
            }
            return node;
        }

        private static JsonObject AppendArrayTable(JsonObject root, IReadOnlyList<string> path)
        {
            var parent = Table(root, path.Take(path.Count - 1).ToList());
            if (parent[path[^1]] is not JsonArray array)
            {
                array = [];
                parent[path[^1]] = array;
            }
            var item = new JsonObject();
            array.Add((JsonNode)item);
            return item;
        }

        private List<string> ParseKeyPath()
        {
            var keys = new List<string>();
            while (true)
            {
                SkipInlineWhitespace();
                keys.Add(ParseKey());
                SkipInlineWhitespace();
                if (Peek(0) == '.')
                {
                    _pos++;
                    continue;
                }
                return keys;
            }
        }

        private string ParseKey()
        {
            var c = Peek(0);
            if (c == '"')
            {
                return ParseBasicString();
            }
            if (c == '\'')
            {
                return ParseLiteralString();
            }
            var start = _pos;
            while (_pos < text.Length && (char.IsAsciiLetterOrDigit(text[_pos]) || text[_pos] is '_' or '-'))
            {
                _pos++;
            }
            if (_pos == start)
            {
                throw Error("Expected a key");
            }
            return text[start.._pos];
        }

        private JsonNode? ParseValue()
        {
            var c = Peek(0);
            switch (c)
            {
                case '"':
                    return JsonValue.Create(StartsWith("\"\"\"") ? ParseMultilineBasic() : ParseBasicString());
                case '\'':
                    return JsonValue.Create(StartsWith("'''") ? ParseMultilineLiteral() : ParseLiteralString());
                case '[':
                    return ParseArray();
                case '{':
                    return ParseInlineTable();
            }
            if (StartsWith("true"))
            {
                _pos += 4;
                return JsonValue.Create(true);
            }
            if (StartsWith("false"))
            {
                _pos += 5;
                return JsonValue.Create(false);
            }
            var start = _pos;
            while (_pos < text.Length && text[_pos] is not (',' or ']' or '}' or '\n' or '\r' or '#') && !(text[_pos] == ' ' && !LooksLikeDateTime(start)))
            {
                _pos++;
            }
            var raw = text[start.._pos].Trim().Replace("_", "", StringComparison.Ordinal);
            if (raw.Length == 0)
            {
                throw Error("Expected a value");
            }
            if (long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
            {
                return JsonValue.Create(integer);
            }
            if (raw.StartsWith("0x", StringComparison.Ordinal) && long.TryParse(raw[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            {
                return JsonValue.Create(hex);
            }
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                return JsonValue.Create(number);
            }
            // 日期时间等：按字符串保留
            return JsonValue.Create(text[start.._pos].Trim());
        }

        private bool LooksLikeDateTime(int start) => _pos - start == 10 && text[start..(start + 10)].Count(ch => ch == '-') == 2;

        private JsonArray ParseArray()
        {
            Expect('[');
            var array = new JsonArray();
            while (true)
            {
                SkipWhitespaceCommentsAndNewlines();
                if (Peek(0) == ']')
                {
                    _pos++;
                    return array;
                }
                array.Add(ParseValue());
                SkipWhitespaceCommentsAndNewlines();
                if (Peek(0) == ',')
                {
                    _pos++;
                }
            }
        }

        private JsonObject ParseInlineTable()
        {
            Expect('{');
            var obj = new JsonObject();
            while (true)
            {
                SkipWhitespaceCommentsAndNewlines();
                if (Peek(0) == '}')
                {
                    _pos++;
                    return obj;
                }
                var keys = ParseKeyPath();
                SkipInlineWhitespace();
                Expect('=');
                SkipInlineWhitespace();
                var value = ParseValue();
                var target = keys.Count > 1 ? Table(obj, keys.Take(keys.Count - 1).ToList()) : obj;
                target[keys[^1]] = value;
                SkipWhitespaceCommentsAndNewlines();
                if (Peek(0) == ',')
                {
                    _pos++;
                }
            }
        }

        private string ParseBasicString()
        {
            Expect('"');
            var sb = new StringBuilder();
            while (_pos < text.Length && text[_pos] != '"')
            {
                if (text[_pos] is '\n' or '\r')
                {
                    throw Error("Unterminated string");
                }
                AppendChar(sb);
            }
            Expect('"');
            return sb.ToString();
        }

        private string ParseMultilineBasic()
        {
            _pos += 3;
            SkipOneNewline();
            var sb = new StringBuilder();
            while (_pos < text.Length && !StartsWith("\"\"\""))
            {
                if (text[_pos] == '\\' && _pos + 1 < text.Length && text[_pos + 1] is '\n' or '\r' or ' ' or '\t')
                {
                    // 行尾反斜杠：去掉换行与下一行开头的空白
                    _pos++;
                    while (_pos < text.Length && char.IsWhiteSpace(text[_pos]))
                    {
                        CountLine();
                        _pos++;
                    }
                    continue;
                }
                CountLine();
                AppendChar(sb);
            }
            _pos += 3;
            return sb.ToString();
        }

        private string ParseLiteralString()
        {
            Expect('\'');
            var start = _pos;
            while (_pos < text.Length && text[_pos] != '\'')
            {
                if (text[_pos] == '\n')
                {
                    throw Error("Unterminated string");
                }
                _pos++;
            }
            var value = text[start.._pos];
            Expect('\'');
            return value;
        }

        private string ParseMultilineLiteral()
        {
            _pos += 3;
            SkipOneNewline();
            var start = _pos;
            var end = text.IndexOf("'''", _pos, StringComparison.Ordinal);
            if (end < 0)
            {
                throw Error("Unterminated string");
            }
            var value = text[start..end];
            _line += value.Count(ch => ch == '\n');
            _pos = end + 3;
            return value;
        }

        private void AppendChar(StringBuilder sb)
        {
            var c = text[_pos++];
            if (c != '\\')
            {
                sb.Append(c);
                return;
            }
            var e = text[_pos++];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case 'u' or 'U':
                    var length = e == 'u' ? 4 : 8;
                    var code = int.Parse(text.AsSpan(_pos, length), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    sb.Append(char.ConvertFromUtf32(code));
                    _pos += length;
                    break;
                default:
                    throw Error($"Invalid escape \\{e}");
            }
        }

        private void EndOfLine()
        {
            SkipInlineWhitespace();
            if (Peek(0) == '#')
            {
                SkipComment();
            }
            if (_pos < text.Length && text[_pos] is not ('\n' or '\r'))
            {
                throw Error("Expected end of line");
            }
        }

        private void SkipComment()
        {
            while (_pos < text.Length && text[_pos] != '\n')
            {
                _pos++;
            }
        }

        private void SkipInlineWhitespace()
        {
            while (_pos < text.Length && text[_pos] is ' ' or '\t')
            {
                _pos++;
            }
        }

        private void SkipWhitespaceAndNewlines()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos]))
            {
                CountLine();
                _pos++;
            }
        }

        private void SkipWhitespaceCommentsAndNewlines()
        {
            while (true)
            {
                SkipWhitespaceAndNewlines();
                if (Peek(0) == '#')
                {
                    SkipComment();
                    continue;
                }
                return;
            }
        }

        private void SkipOneNewline()
        {
            if (Peek(0) == '\r')
            {
                _pos++;
            }
            if (Peek(0) == '\n')
            {
                _pos++;
                _line++;
            }
        }

        private void CountLine()
        {
            if (_pos < text.Length && text[_pos] == '\n')
            {
                _line++;
            }
        }

        private char Peek(int offset) => _pos + offset < text.Length ? text[_pos + offset] : '\0';

        private bool StartsWith(string s) => string.CompareOrdinal(text, _pos, s, 0, s.Length) == 0;

        private void Expect(char c)
        {
            if (Peek(0) != c)
            {
                throw Error($"Expected '{c}'");
            }
            _pos++;
        }

        private FormatException Error(string message) => new($"TOML line {_line}: {message}.");
    }
}
