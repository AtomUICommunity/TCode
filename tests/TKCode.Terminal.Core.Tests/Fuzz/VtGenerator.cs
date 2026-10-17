using System.Text;

namespace TKCode.Terminal.Core.Tests.Fuzz;

/// <summary>
/// 结构化随机字节流（11 §1.1.5）：在合法序列模板（CSI、SGR 冒号子参数、OSC、DCS、APC、UTF-8 多字节、组合字符、
/// ZWJ 序列、宽字符）中随机插入 ESC / CAN / SUB / C1 / 超长参数 / 超长 OSC / 截断。确定性：同一种子同一输出。
/// </summary>
internal sealed class VtGenerator(int seed)
{
    private static readonly string[] Finals = ["A", "B", "C", "D", "E", "F", "G", "H", "J", "K", "L", "M", "P", "S", "T", "X", "@", "b", "d", "f", "g", "h", "l", "m", "n", "r", "s", "u", "t", "q", "c", "x", "Z", "`", "a", "e"];
    private static readonly string[] Privates = ["", "", "", "?", ">", "<", "=", "!"];
    private static readonly string[] Intermediates = ["", "", "", "", " ", "$", "\"", "'", "#", "*"];
    private static readonly string[] Text = ["hello", "世界", "é", "é", "👨‍👩‍👧", "🇨🇳", "a‍b", "\t", "x", "abcdefghijklmnopqrstuvwxyz0123456789", "─│┌┐", "ﾊﾟ", "­", "​"];
    private static readonly int[] Modes = [1, 3, 5, 6, 7, 12, 25, 40, 45, 47, 66, 69, 1000, 1002, 1003, 1004, 1005, 1006, 1007, 1015, 1016, 1047, 1048, 1049, 2004, 2026, 2027, 2031, 9001];

    private readonly Random _random = new(seed);

    public byte[] Next(int bytes)
    {
        var sb = new StringBuilder(bytes + 256);
        while (Encoding.UTF8.GetByteCount(sb.ToString()) < bytes)
        {
            Append(sb);
        }
        var data = Encoding.UTF8.GetBytes(sb.ToString());
        // 截断：随机去掉末尾若干字节（可能截在 UTF-8 / 转义序列中间）
        return _random.Next(4) == 0 ? data[..Math.Max(0, data.Length - _random.Next(1, 6))] : data;
    }

    private void Append(StringBuilder sb)
    {
        switch (_random.Next(16))
        {
            case 0 or 1 or 2 or 3:
                sb.Append(Text[_random.Next(Text.Length)]);
                break;
            case 4 or 5:
                sb.Append("\x1b[").Append(Privates[_random.Next(Privates.Length)]).Append(Params()).Append(Intermediates[_random.Next(Intermediates.Length)])
                    .Append(Finals[_random.Next(Finals.Length)]);
                break;
            case 6:
                // SGR，含冒号子参数（下划线样式、真彩）
                sb.Append("\x1b[").Append(_random.Next(3) switch
                {
                    0 => $"38:2::{_random.Next(256)}:{_random.Next(256)}:{_random.Next(256)}",
                    1 => $"4:{_random.Next(6)}",
                    _ => $"{_random.Next(110)};{_random.Next(110)}",
                }).Append('m');
                break;
            case 7:
                sb.Append("\x1b[?").Append(Modes[_random.Next(Modes.Length)]).Append(_random.Next(2) == 0 ? 'h' : 'l');
                break;
            case 8:
                sb.Append("\x1b]").Append(_random.Next(5) switch
                {
                    0 => $"0;title {_random.Next()}",
                    1 => $"8;;https://example.com/{_random.Next()}",
                    2 => "133;" + "ABCD"[_random.Next(4)],
                    3 => $"7;file://host/tmp/{_random.Next()}",
                    _ => new string('x', _random.Next(1, 9000)),  // 超长 OSC
                }).Append(_random.Next(3) switch { 0 => "\a", 1 => "\x1b\\", _ => "" });
                break;
            case 9:
                sb.Append(_random.Next(3) switch
                {
                    0 => "\x1bP+q544e\x1b\\",            // XTGETTCAP
                    1 => "\x1bP$qm\x1b\\",               // DECRQSS
                    _ => "\x1b_Ga=q,i=1;AAAA\x1b\\",     // Kitty 图形查询
                });
                break;
            case 10:
                // 控制字符与打断：ESC、CAN、SUB、C1（UTF-8 编码的 U+0080–U+009F）
                sb.Append("\x1b\x18\x1a\r\n\b\a\x0e\x0f\u0084\u0085\u008d\u009b"[_random.Next(13)]);
                break;
            case 11:
                sb.Append("\x1b").Append("78DEMcHN=>()*+"[_random.Next(14)]).Append(_random.Next(3) == 0 ? "0B" [_random.Next(2)].ToString() : "");
                break;
            case 12:
                sb.Append($"\x1b[{_random.Next(1, 30)};{_random.Next(1, 90)}H");
                break;
            case 13:
                sb.Append("\x1b[").Append(new string('9', _random.Next(1, 40))).Append(Finals[_random.Next(Finals.Length)]);  // 超大参数
                break;
            case 14:
                sb.Append("\x1b[").Append(string.Join(';', Enumerable.Range(0, _random.Next(20, 40)).Select(_ => _random.Next(1000)))).Append('m');  // 参数过多
                break;
            default:
                sb.Append($"\x1b[{_random.Next(1, 30)};{_random.Next(1, 30)}r");
                break;
        }
    }

    private string Params() => _random.Next(4) switch
    {
        0 => string.Empty,
        1 => _random.Next(100).ToString(),
        2 => $"{_random.Next(100)};{_random.Next(100)}",
        _ => $"{_random.Next(100)};{_random.Next(100)};{_random.Next(100)}",
    };
}
