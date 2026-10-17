using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TKCode.Config;

namespace TKCode.Mcp;

public sealed record McpTrustEntry(string Project, string Server, string Fingerprint, bool Trusted);

public sealed record McpTrustFile(List<McpTrustEntry> Entries);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(McpTrustFile))]
internal sealed partial class McpTrustJsonContext : JsonSerializerContext;

/// <summary>
/// 项目级 .mcp.json 服务器的信任记录（docs/design/mcp.md §4.2）：~/.tkcode/mcp/trust.json，
/// 键为 项目根 + 服务器名 + 配置指纹，配置改变后需要重新确认。
/// </summary>
public sealed class McpTrustStore(string? path = null)
{
    private readonly string _path = path ?? Path.Combine(ConfigPaths.Home, "mcp", "trust.json");
    private readonly Lock _gate = new();

    /// <summary>true 信任、false 已拒绝（忽略）、null 尚未决定。</summary>
    public bool? Get(string projectRoot, McpServerConfig config)
    {
        var fingerprint = Fingerprint(config);
        lock (_gate)
        {
            return Read().Entries.FirstOrDefault(e => e.Project == projectRoot && e.Server == config.Key && e.Fingerprint == fingerprint)?.Trusted;
        }
    }

    public void Set(string projectRoot, McpServerConfig config, bool trusted)
    {
        var fingerprint = Fingerprint(config);
        lock (_gate)
        {
            var file = Read();
            file.Entries.RemoveAll(e => e.Project == projectRoot && e.Server == config.Key);
            file.Entries.Add(new McpTrustEntry(projectRoot, config.Key, fingerprint, trusted));
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, McpTrustJsonContext.Default.McpTrustFile));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            File.Move(temp, _path, overwrite: true);
        }
    }

    /// <summary>配置指纹：写法本身（未展开变量），因此 .mcp.json 被改动后需要重新信任。</summary>
    public static string Fingerprint(McpServerConfig config) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(config.ToJson().ToJsonString())))[..16];

    private McpTrustFile Read()
    {
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize(File.ReadAllText(_path), McpTrustJsonContext.Default.McpTrustFile) is { } file)
            {
                return file with { Entries = file.Entries ?? [] };
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
        }
        return new McpTrustFile([]);
    }
}
