using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkiaSharp;
using StreamMark.Mermaid;
using TCode.Desktop.Icons;
using TCode.Desktop.Localization;

namespace TCode.Desktop.Previews;

/// <summary>
/// 图片预览：PNG / JPEG / WebP / BMP / ICO / GIF（含动图与动态 WebP）/ SVG。按扩展名或文件头识别。
/// </summary>
public sealed class ImagePreviewProvider : IFilePreviewProvider
{
    public const long MaxBytes = 64 * 1024 * 1024;

    // 动图所有帧解码后的内存上限，超出时只显示第一帧
    private const long MaxAnimationBytes = 256L * 1024 * 1024;

    private static readonly SvgImageRasterizer Svg = new();

    public LucideKind Icon => LucideKind.Image;
    public TCodeLangResourceKind Title => TCodeLangResourceKind.PreviewImage;

    public bool CanPreview(FileProbe probe) => probe.Length <= MaxBytes && FormatOf(probe) is not null;

    /// <summary>格式名（用于信息栏）；不是图片时为 null。</summary>
    public static string? FormatOf(FileProbe probe)
    {
        var head = probe.Head.AsSpan();
        if (head.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G']))
        {
            return "PNG";
        }
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
        {
            return "JPEG";
        }
        if (head.StartsWith("GIF8"u8))
        {
            return "GIF";
        }
        if (head.Length >= 12 && head.StartsWith("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8))
        {
            return "WebP";
        }
        if (head.StartsWith("BM"u8) && probe.Extension == ".bmp")
        {
            return "BMP";
        }
        return probe.Extension switch
        {
            ".ico" => "ICO",
            ".svg" when !probe.IsBinary => "SVG",
            _ => null,
        };
    }

    public async Task<FilePreviewViewModel> CreateAsync(FileProbe probe, CancellationToken cancellationToken)
    {
        var format = FormatOf(probe)!;
        var bytes = await probe.ReadBytesAsync(cancellationToken);
        return await Task.Run(() => format == "SVG" ? DecodeSvg(bytes, probe.Length) : DecodeRaster(bytes, format, probe.Length), cancellationToken);
    }

    private static ImagePreviewViewModel DecodeSvg(byte[] bytes, long length)
    {
        // 先取自然尺寸，再按较高分辨率栅格化（长边至少 2048px）：小图标适应窗口放大后仍然清晰
        using (Svg.Rasterize(bytes, 0, out var natural))
        {
            if (natural.Width <= 0 || natural.Height <= 0)
            {
                throw new InvalidDataException(Strings.Get(TCodeLangResourceKind.ImageDecodeFailed));
            }
            var scale = Math.Max(2, 2048.0 / Math.Max(natural.Width, natural.Height));
            var target = (int)Math.Min(Svg.MaxDimension, natural.Width * scale);
            var bitmap = Svg.Rasterize(bytes, target, out _) ?? throw new InvalidDataException(Strings.Get(TCodeLangResourceKind.ImageDecodeFailed));
            return new ImagePreviewViewModel([bitmap], [], natural, "SVG", length, isVector: true);
        }
    }

    private static ImagePreviewViewModel DecodeRaster(byte[] bytes, string format, long length)
    {
        if (format is "GIF" or "WebP")
        {
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            if (codec is { FrameCount: > 1 } && (long)codec.Info.Width * codec.Info.Height * 4 * codec.FrameCount <= MaxAnimationBytes)
            {
                return DecodeAnimation(codec, format, length);
            }
        }
        using var stream = new MemoryStream(bytes);
        Bitmap still;
        try
        {
            still = new Bitmap(stream);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidDataException(Strings.Get(TCodeLangResourceKind.ImageDecodeFailed), ex);
        }
        return new ImagePreviewViewModel([still], [], still.PixelSize, format, length, isVector: false);
    }

    private static ImagePreviewViewModel DecodeAnimation(SKCodec codec, string format, long length)
    {
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var frameInfos = codec.FrameInfo;
        var decoded = new SKBitmap[frameInfos.Length];
        var frames = new List<Bitmap>(frameInfos.Length);
        var delays = new List<TimeSpan>(frameInfos.Length);
        try
        {
            for (var i = 0; i < frameInfos.Length; i++)
            {
                var frame = new SKBitmap(info);
                decoded[i] = frame;
                // 依赖前一帧的增量帧：先复制所依赖帧的像素再叠加
                var required = frameInfos[i].RequiredFrame;
                if (required >= 0 && decoded[required] is { } prior)
                {
                    prior.CopyTo(frame);
                }
                else
                {
                    frame.Erase(SKColors.Transparent);
                }
                codec.GetPixels(info, frame.GetPixels(), new SKCodecOptions(i, required));
                frames.Add(new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, frame.GetPixels(), new PixelSize(info.Width, info.Height),
                    new Vector(96, 96), frame.RowBytes));
                // 与浏览器一致：过短的帧间隔按 100ms 处理
                var duration = frameInfos[i].Duration;
                delays.Add(TimeSpan.FromMilliseconds(duration <= 10 ? 100 : duration));
            }
        }
        catch
        {
            foreach (var frame in frames)
            {
                frame.Dispose();
            }
            throw;
        }
        finally
        {
            foreach (var bitmap in decoded)
            {
                bitmap?.Dispose();
            }
        }
        return new ImagePreviewViewModel(frames, delays, new PixelSize(info.Width, info.Height), format, length, isVector: false);
    }
}

/// <summary>
/// 图片视图模型：帧（静态图只有一帧）、帧间隔、自然尺寸，以及缩放状态。
/// <see cref="IsFit"/> 时由视图按窗口计算 <see cref="Zoom"/>；其他时候 <see cref="Zoom"/> 为固定倍数。
/// </summary>
public sealed partial class ImagePreviewViewModel : FilePreviewViewModel
{
    public const double MinZoom = 0.05;
    public const double MaxZoom = 32;

    private static readonly double[] Steps = [0.05, 0.1, 0.25, 0.5, 0.75, 1, 1.5, 2, 3, 4, 6, 8, 12, 16, 24, 32];

    public ImagePreviewViewModel(IReadOnlyList<Bitmap> frames, IReadOnlyList<TimeSpan> delays, PixelSize size, string format, long length, bool isVector)
    {
        Frames = frames;
        Delays = delays;
        PixelSize = size;
        Format = format;
        Length = length;
        IsVector = isVector;
    }

    public IReadOnlyList<Bitmap> Frames { get; }

    /// <summary>每帧的显示时长；静态图为空。</summary>
    public IReadOnlyList<TimeSpan> Delays { get; }

    /// <summary>图片的自然尺寸（SVG 为声明的尺寸，位图可能以更高分辨率栅格化）。</summary>
    public PixelSize PixelSize { get; }

    public string Format { get; }
    public long Length { get; }

    /// <summary>矢量图：适应窗口时允许放大。</summary>
    public bool IsVector { get; }

    public bool IsAnimated => Frames.Count > 1;

    /// <summary>信息栏：尺寸 · 大小 · 格式（· 帧数）。</summary>
    public string Info
    {
        get
        {
            var size = Length >= 1024 * 1024 ? $"{Length / 1024.0 / 1024.0:0.#} MB" : Length >= 1024 ? $"{Length / 1024.0:0.#} KB" : $"{Length} B";
            var info = $"{PixelSize.Width} × {PixelSize.Height} · {size} · {Format}";
            return IsAnimated ? info + " · " + Strings.Format(TCodeLangResourceKind.ImageFramesFormat, Frames.Count) : info;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomText))]
    public partial double Zoom { get; set; } = 1;

    [ObservableProperty]
    public partial bool IsFit { get; set; } = true;

    public string ZoomText => $"{Math.Round(Zoom * 100)}%";

    [RelayCommand]
    private void Fit() => IsFit = true;

    [RelayCommand]
    private void ActualSize()
    {
        IsFit = false;
        Zoom = 1;
    }

    [RelayCommand]
    private void ZoomIn() => SetZoom(Steps.FirstOrDefault(s => s > Zoom * 1.001, MaxZoom));

    [RelayCommand]
    private void ZoomOut() => SetZoom(Steps.LastOrDefault(s => s < Zoom * 0.999, MinZoom));

    public void SetZoom(double zoom)
    {
        IsFit = false;
        Zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
    }

    public override void Dispose()
    {
        foreach (var frame in Frames)
        {
            frame.Dispose();
        }
    }
}
