using Avalonia;
using Avalonia.Media.Imaging;
using StreamMark.Avalonia.Rendering;
using TCode.Desktop.ViewModels;

namespace TCode.Desktop.Controls;

/// <summary>图片附件全屏查看：复用 StreamMark 的图片查看器（与 Markdown 中的图片一致，Esc 或点击关闭）。</summary>
public static class ImageOpener
{
    public static void Open(Visual anchor, ImageAttachmentViewModel image)
    {
        Bitmap bitmap;
        try
        {
            bitmap = new Bitmap(image.Path);
        }
        catch (Exception)
        {
            return; // 附件已删除或无法解码：缩略图处已显示占位
        }
        if (ImageViewer.Show(anchor, bitmap, image.FileName) is null)
        {
            bitmap.Dispose();
        }
    }
}
