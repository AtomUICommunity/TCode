using Xunit;
using System.Text.Json;
using TCode.Terminal.Core;
using TCode.Terminal.Core.Interop;

namespace TCode.Terminal.Core.Tests;

/// <summary>原生库加载、构建信息、以及手写结构体与 ghostty_type_json() 清单的布局比对（02 §4.2）。</summary>
public sealed class RuntimeTests
{
    [Fact]
    public void ReportsVersionAndBuildId()
    {
        Assert.False(string.IsNullOrEmpty(GhosttyRuntime.Version));
        var library = Directory.GetFiles(AppContext.BaseDirectory, "*ghostty-vt.*").Single(f => !f.EndsWith(".txt"));
        Assert.Matches("\\+sha256:[0-9a-f]{64}$", GhosttyRuntime.ComputeBuildId(library));
    }

    // DisableRuntimeMarshalling 下 bool 是 1 字节，Marshal.SizeOf / OffsetOf 用的是封送布局，不能用来测量；
    // 这里直接按内存地址计算（M1 起由生成器为每个结构体生成同样的测试）。
    public static TheoryData<string> Structs => ["GhosttyString", "GhosttyTerminalModeConfig", "GhosttyFormatterScreenExtra",
        "GhosttyFormatterTerminalExtra", "GhosttyFormatterTerminalOptions"];

    [Theory]
    [MemberData(nameof(Structs))]
    public void StructLayoutMatchesTypeManifest(string name)
    {
        var (size, offsets) = Measure(name);
        using var doc = JsonDocument.Parse(GhosttyRuntime.TypeManifestJson());
        var manifest = doc.RootElement.GetProperty("types").GetProperty(name);
        Assert.Equal(manifest.GetProperty("size").GetInt32(), size);
        var expected = manifest.GetProperty("fields").EnumerateObject().ToDictionary(f => f.Name, f => f.Value.GetProperty("offset").GetInt32());
        Assert.Equal(expected.OrderBy(p => p.Key), offsets.OrderBy(p => p.Key));
    }

    private static unsafe (int Size, Dictionary<string, int> Offsets) Measure(string name)
    {
        static int Off(void* field, void* start) => (int)((byte*)field - (byte*)start);
        switch (name)
        {
            case "GhosttyString":
            {
                GhosttyString s;
                return (sizeof(GhosttyString), new() { ["ptr"] = Off(&s.Ptr, &s), ["len"] = Off(&s.Len, &s) });
            }
            case "GhosttyTerminalModeConfig":
            {
                GhosttyTerminalModeConfig s;
                return (sizeof(GhosttyTerminalModeConfig), new() { ["mode"] = Off(&s.Mode, &s), ["value"] = Off(&s.Value, &s) });
            }
            case "GhosttyFormatterScreenExtra":
            {
                GhosttyFormatterScreenExtra s;
                return (sizeof(GhosttyFormatterScreenExtra), new()
                {
                    ["size"] = Off(&s.Size, &s), ["cursor"] = Off(&s.Cursor, &s), ["style"] = Off(&s.Style, &s),
                    ["hyperlink"] = Off(&s.Hyperlink, &s), ["protection"] = Off(&s.Protection, &s),
                    ["kitty_keyboard"] = Off(&s.KittyKeyboard, &s), ["charsets"] = Off(&s.Charsets, &s),
                });
            }
            case "GhosttyFormatterTerminalExtra":
            {
                GhosttyFormatterTerminalExtra s;
                return (sizeof(GhosttyFormatterTerminalExtra), new()
                {
                    ["size"] = Off(&s.Size, &s), ["palette"] = Off(&s.Palette, &s), ["modes"] = Off(&s.Modes, &s),
                    ["scrolling_region"] = Off(&s.ScrollingRegion, &s), ["tabstops"] = Off(&s.Tabstops, &s),
                    ["pwd"] = Off(&s.Pwd, &s), ["keyboard"] = Off(&s.Keyboard, &s), ["screen"] = Off(&s.Screen, &s),
                });
            }
            case "GhosttyFormatterTerminalOptions":
            {
                GhosttyFormatterTerminalOptions s;
                return (sizeof(GhosttyFormatterTerminalOptions), new()
                {
                    ["size"] = Off(&s.Size, &s), ["emit"] = Off(&s.Emit, &s), ["unwrap"] = Off(&s.Unwrap, &s),
                    ["trim"] = Off(&s.Trim, &s), ["extra"] = Off(&s.Extra, &s), ["selection"] = Off(&s.Selection, &s),
                });
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(name));
        }
    }
}
