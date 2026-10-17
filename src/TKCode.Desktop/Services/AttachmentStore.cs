using Avalonia;
using Avalonia.Media.Imaging;
using TKCode.Config;

namespace TKCode.Desktop.Services;

/// <summary>
/// 客户端附件目录（~/.tkcode/attachments/yyyy-MM）：粘贴或选择的图片先保存到这里，
/// 提示词中引用这个路径，AgentHost 读取文件后以 base64 发给模型。
/// 图片超过各家的单图上限（按 base64 5 MB 计，原始约 3.75 MB）时按比例缩小后保存为 PNG。
/// </summary>
public static class AttachmentStore
{
    public const long MaxImageBytes = 3_750_000;
    private const int MaxImageSide = 7_680;

    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
    };

    public static string Root => Path.Combine(ConfigPaths.Home, "attachments");

    /// <summary>扩展名对应的图片类型；不是支持的图片格式时返回 null。</summary>
    public static string? MimeTypeOf(string path) => MimeTypes.GetValueOrDefault(Path.GetExtension(path));

    /// <summary>保存剪贴板中的位图（PNG）。</summary>
    public static string SaveBitmap(Bitmap bitmap)
    {
        var path = NewPath(".png");
        SavePng(bitmap, path);
        return path;
    }

    /// <summary>复制图片文件到附件目录（大图转换为缩小后的 PNG）。返回新路径与类型；不是图片时返回 null。</summary>
    public static (string Path, string MimeType)? ImportFile(string source)
    {
        if (MimeTypeOf(source) is not { } mime || !File.Exists(source))
        {
            return null;
        }
        if (new FileInfo(source).Length <= MaxImageBytes)
        {
            var path = NewPath(Path.GetExtension(source).ToLowerInvariant());
            File.Copy(source, path);
            return (path, mime);
        }
        using var bitmap = new Bitmap(source);
        return (SaveBitmap(bitmap), "image/png");
    }

    private static void SavePng(Bitmap bitmap, string path)
    {
        var size = bitmap.PixelSize;
        var scale = Math.Min(1.0, (double)MaxImageSide / Math.Max(size.Width, size.Height));
        while (true)
        {
            if (scale >= 1.0)
            {
                bitmap.Save(path);
            }
            else
            {
                using var scaled = bitmap.CreateScaledBitmap(
                    new PixelSize(Math.Max(1, (int)(size.Width * scale)), Math.Max(1, (int)(size.Height * scale))));
                scaled.Save(path);
            }
            if (new FileInfo(path).Length <= MaxImageBytes || scale < 0.05)
            {
                return;
            }
            scale *= 0.75;
        }
    }

    private static string NewPath(string extension)
    {
        var dir = Path.Combine(Root, DateTime.Now.ToString("yyyy-MM"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}{extension}");
    }
}
