using Avalonia.Media.Imaging;

namespace TCode.Desktop.ViewModels;

/// <summary>
/// 一张图片附件（输入框里待发送的，或消息气泡里已发送的）。缩略图按需解码一次；
/// 附件文件已被删除时 <see cref="Exists"/> 为 false，界面显示占位。
/// </summary>
/// <param name="thumbnailWidth">缩略图解码宽度（像素，按 2–3 倍屏幕密度留余量）。</param>
public sealed class ImageAttachmentViewModel(string path, string mimeType, int number, int thumbnailWidth = 192)
{
    private Bitmap? _thumbnail;
    private bool _decoded;

    public string Path { get; } = path;
    public string MimeType { get; } = mimeType;
    public int Number { get; } = number;
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Placeholder => ImagePrompt.Placeholder(Number);

    /// <summary>输入框里代表这张图片的 tag：显示「图片 #N」，纯文本投影为占位符「[图片 #N]」。</summary>
    public TCode.Desktop.Controls.Composer.TagSegment Tag =>
        new(TCode.Desktop.Controls.Composer.TagKind.Image, Number.ToString(System.Globalization.CultureInfo.InvariantCulture), Placeholder.Trim('[', ']'));
    public bool Exists => File.Exists(Path);

    public Bitmap? Thumbnail
    {
        get
        {
            if (!_decoded)
            {
                _decoded = true;
                try
                {
                    using var stream = File.OpenRead(Path);
                    _thumbnail = Bitmap.DecodeToWidth(stream, thumbnailWidth);
                }
                catch (Exception)
                {
                    _thumbnail = null; // 文件不存在或不是可解码的图片：显示占位
                }
            }
            return _thumbnail;
        }
    }

    public bool HasThumbnail => Thumbnail is not null;
}
