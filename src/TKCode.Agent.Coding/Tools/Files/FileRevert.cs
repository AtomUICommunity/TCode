using System.Security.Cryptography;

namespace TKCode.Agent.Coding.Tools.Files;

/// <summary>
/// 撤销 Agent 的文件改动。改动后的文件内容必须与记录的 SHA-256 一致，否则拒绝（文件已被用户或其他工具改过，不覆盖）。
/// 新建的文件（<paramref name="restoreText"/> 为 null）撤销时删除。
/// </summary>
public static class FileRevert
{
    /// <summary>超过此大小的文件不保存改动前的快照，对应改动不可撤销。</summary>
    public const int MaxSnapshotBytes = 512 * 1024;

    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>撤销一次（或一轮内合并的）改动。返回错误文本；成功时为 null。</summary>
    public static string? Revert(string path, string expectedSha256, string? restoreText)
    {
        if (!File.Exists(path))
        {
            return restoreText is null ? null : $"{path} no longer exists.";
        }
        var current = File.ReadAllBytes(path);
        if (!string.Equals(Sha256(current), expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return $"{path} has changed since the agent edited it; revert skipped.";
        }
        if (restoreText is null)
        {
            File.Delete(path);
            return null;
        }
        var format = TextFile.Decode(current);
        File.WriteAllBytes(path, format.Encode(restoreText));
        return null;
    }
}
