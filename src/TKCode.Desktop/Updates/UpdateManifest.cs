using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TKCode.Desktop.Updates;

/// <summary>Release 中的 manifest.json（auto-update.md §4）。由发布 CI 生成并用 ECDSA P-256 签名。</summary>
public sealed record UpdateManifest
{
    public int Schema { get; init; }
    public string Version { get; init; } = "";
    /// <summary>stable | preview，与 Release 的 prerelease 标志一致。</summary>
    public string Channel { get; init; } = "stable";
    public DateTimeOffset? PublishedAt { get; init; }
    public string? NotesUrl { get; init; }
    /// <summary>按运行时标识（osx-arm64、win-x64 …）索引的安装程序。</summary>
    public Dictionary<string, UpdateAsset> Assets { get; init; } = [];
}

public sealed record UpdateAsset
{
    public string Name { get; init; } = "";
    public long Size { get; init; }
    /// <summary>小写十六进制。</summary>
    public string Sha256 { get; init; } = "";
    /// <summary>最低系统版本（主.次），省略表示不限。</summary>
    public string? MinOs { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true)]
[JsonSerializable(typeof(UpdateManifest))]
internal sealed partial class UpdateManifestJsonContext : JsonSerializerContext;

/// <summary>清单签名校验：ECDSA P-256 + SHA-256，签名为 DER（openssl dgst -sha256 -sign 的输出）。</summary>
public static class ManifestVerifier
{
    /// <summary>
    /// 编译进客户端的发布公钥（SubjectPublicKeyInfo PEM）。可以有多把：换钥时新版本同时带上新旧公钥，过渡一个版本后去掉旧钥（§4）。
    /// 私钥只在 CI secret UPDATE_SIGNING_KEY 中，生成方法见 tools/release/gen-update-key.sh。
    /// </summary>
    public static IReadOnlyList<string> ReleaseKeys { get; } =
    [
        """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEh8irs9HeQxHlBucVkM48qhlHJfuZ
        tQm0rMfXotCUK43WleXrj2CXe4seUCnZF+vkl+kY9uV5xZXI7kXFJSUvqw==
        -----END PUBLIC KEY-----
        """,
    ];

    /// <summary>签名有效（任意一把公钥验证通过）时解析并返回清单，否则返回 null。</summary>
    public static UpdateManifest? VerifyAndParse(byte[] manifest, byte[] signature, IReadOnlyList<string> publicKeys)
    {
        if (!Verify(manifest, signature, publicKeys))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize(manifest, UpdateManifestJsonContext.Default.UpdateManifest);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool Verify(byte[] data, byte[] signature, IReadOnlyList<string> publicKeys)
    {
        foreach (var pem in publicKeys)
        {
            try
            {
                using var key = ECDsa.Create();
                key.ImportFromPem(pem);
                if (key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            {
                // 格式错误的公钥视为不匹配
            }
        }
        return false;
    }
}
