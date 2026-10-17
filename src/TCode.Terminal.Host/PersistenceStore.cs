using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TCode.Terminal.Core;
using TCode.Terminal.Protocol;

namespace TCode.Terminal.Host;

/// <summary>revive 条目的 meta.json（04 §10.2）。</summary>
public sealed record ReviveMeta
{
    public int FormatVersion { get; init; } = 1;
    public required string PersistentId { get; init; }
    public DateTimeOffset SavedAt { get; init; }
    public string Reason { get; init; } = "periodic";
    public string? HostVersion { get; init; }
    public string? GhosttyBuildId { get; init; }
    public string? SnapshotSha256 { get; init; }
    public string? ProfileId { get; init; }
    public LaunchDto? Launch { get; init; }
    public string? InitialCwd { get; init; }
    public string? Cwd { get; init; }
    public string? Label { get; init; }
    public string? Title { get; init; }
    public string Kind { get; init; } = "user";
    public TerminalSizeDto? Size { get; init; }
    public string WidthMode { get; init; } = "compat";
    public int Lines { get; init; }
    public ExitDto? Exit { get; init; }
    public bool HadRunningProgram { get; init; }
    public Dictionary<string, string>? Tags { get; init; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true)]
[JsonSerializable(typeof(ReviveMeta))]
internal sealed partial class ReviveJsonContext : JsonSerializerContext;

/// <summary>一次恢复所需的内容：二进制快照（兼容时）或 VT 文本（跨版本回退）。</summary>
internal sealed record RevivePayload(ReviveMeta Meta, byte[]? Snapshot, string? Vt);

/// <summary>
/// revive 存储（04 §10）：<c>$TCODE_HOME/terminal/revive/&lt;persistentId&gt;/</c>，目录 0700、文件 0600。
/// 原子写入：三个文件都先写临时文件再 rename，meta.json 最后写；没有 meta.json 的目录视为不完整，启动时清理。
/// </summary>
internal sealed class PersistenceStore(string root, string ghosttyBuildId, string hostVersion)
{
    public string Root { get; } = root;

    public string GhosttyBuildId { get; } = ghosttyBuildId;

    private string Dir(string persistentId)
    {
        if (persistentId.Length is 0 or > 64 || persistentId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
        {
            throw new PtyHostException(PtyErrors.InvalidParams, $"invalid persistentId {persistentId}");
        }
        return Path.Combine(Root, persistentId);
    }

    /// <summary>
    /// 写一个条目。<paramref name="fullSnapshot"/> 是 actor 上编码的完整快照；这里（工作线程）解码进回滚上限为 N 行的临时终端，
    /// 再编码得到裁剪后的快照，同一个终端的 formatter 生成 tail.vt（§10.3）。libghostty 按页裁剪，实际保留 ≥ N 行。
    /// </summary>
    public ReviveMeta Save(ReviveMeta meta, byte[] fullSnapshot, TerminalSize size, int scrollbackLines)
    {
        PtyHostPaths.TryPrivateDirectory(Root);
        var dir = Dir(meta.PersistentId);
        PtyHostPaths.TryPrivateDirectory(dir);
        byte[] snapshot;
        string vt;
        int lines;
        using (var trimmed = TerminalCore.Restore(fullSnapshot, new TerminalCoreOptions(size) { ScrollbackMaxLines = Math.Max(0, scrollbackLines) }))
        {
            snapshot = trimmed.EncodeSnapshot();
            vt = trimmed.FormatVt();
            lines = trimmed.FormatPlainText().Count(c => c == '\n') + 1;
        }
        meta = meta with
        {
            HostVersion = hostVersion,
            GhosttyBuildId = GhosttyBuildId,
            SnapshotSha256 = Convert.ToHexStringLower(SHA256.HashData(snapshot)),
            Lines = lines,
        };
        PtyHostPaths.WriteAtomically(Path.Combine(dir, "snapshot.bin"), snapshot);
        PtyHostPaths.WriteAtomically(Path.Combine(dir, "tail.vt"), Encoding.UTF8.GetBytes(vt));
        PtyHostPaths.WriteAtomically(Path.Combine(dir, "meta.json"), JsonSerializer.SerializeToUtf8Bytes(meta, ReviveJsonContext.Default.ReviveMeta));
        return meta;
    }

    public ReviveMeta? ReadMeta(string persistentId)
    {
        var path = Path.Combine(Dir(persistentId), "meta.json");
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllBytes(path), ReviveJsonContext.Default.ReviveMeta) : null;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>读取恢复内容：同一 libghostty 构建、且校验和一致时用二进制快照，否则用 tail.vt（快照格式 v1 没有兼容保证，§10.2）。</summary>
    public RevivePayload Load(string persistentId)
    {
        var meta = ReadMeta(persistentId) ?? throw new PtyHostException(PtyErrors.ReviveNotFound, $"revive entry {persistentId} not found");
        var dir = Dir(persistentId);
        byte[]? snapshot = null;
        if (meta.GhosttyBuildId == GhosttyBuildId && File.Exists(Path.Combine(dir, "snapshot.bin")))
        {
            var bytes = File.ReadAllBytes(Path.Combine(dir, "snapshot.bin"));
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) == meta.SnapshotSha256)
            {
                snapshot = bytes;
            }
        }
        var vtPath = Path.Combine(dir, "tail.vt");
        return new RevivePayload(meta, snapshot, File.Exists(vtPath) ? File.ReadAllText(vtPath) : null);
    }

    public ReviveEntryDto[] List()
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }
        var result = new List<ReviveEntryDto>();
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            var id = Path.GetFileName(dir);
            ReviveMeta? meta;
            try
            {
                meta = ReadMeta(id);
            }
            catch (PtyHostException)
            {
                continue;
            }
            if (meta is null)
            {
                continue;
            }
            result.Add(new ReviveEntryDto
            {
                PersistentId = meta.PersistentId,
                SavedAt = meta.SavedAt,
                Reason = meta.Reason,
                Label = meta.Label,
                Title = meta.Title,
                Cwd = meta.Cwd,
                ProfileId = meta.ProfileId,
                Kind = meta.Kind,
                Lines = meta.Lines,
                Compatible = meta.GhosttyBuildId == GhosttyBuildId,
                Tags = meta.Tags,
            });
        }
        return [.. result.OrderBy(e => e.SavedAt)];
    }

    public bool Delete(string persistentId)
    {
        var dir = Dir(persistentId);
        if (!Directory.Exists(dir))
        {
            return false;
        }
        Directory.Delete(dir, recursive: true);
        return true;
    }

    public void Clear()
    {
        if (Directory.Exists(Root))
        {
            foreach (var dir in Directory.EnumerateDirectories(Root))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    /// <summary>启动时清理不完整的条目（没有 meta.json）。</summary>
    public void RemoveIncomplete()
    {
        if (!Directory.Exists(Root))
        {
            return;
        }
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            if (!File.Exists(Path.Combine(dir, "meta.json")))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
