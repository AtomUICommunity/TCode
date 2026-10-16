using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using TCode.Desktop.Previews;

namespace TCode.Desktop.Controls;

/// <summary>
/// 图片查看区：透明部分画棋盘格，默认适应窗口（位图不放大），滚轮以指针为中心缩放、拖动平移、
/// 双击在适应窗口与 100% 之间切换；放大到 2 倍以上按像素显示。动图按帧间隔播放（仅在挂到界面上时）。
/// 缩放状态保存在 <see cref="ImagePreviewViewModel"/> 中，工具栏按钮直接操作它。
/// </summary>
public sealed class ZoomImageView : Control
{
    public static readonly StyledProperty<ImagePreviewViewModel?> ImageProperty =
        AvaloniaProperty.Register<ZoomImageView, ImagePreviewViewModel?>(nameof(Image));

    private const double Padding = 24;
    private const double CheckerCell = 8;

    private ImagePreviewViewModel? _image;
    private Vector _offset;
    private Point? _dragStart;
    private Vector _dragOrigin;
    private int _frame;
    private DispatcherTimer? _timer;
    private IBrush? _checker;

    static ZoomImageView()
    {
        ClipToBoundsProperty.OverrideDefaultValue<ZoomImageView>(true);
    }

    public ZoomImageView()
    {
        ActualThemeVariantChanged += (_, _) =>
        {
            _checker = null;
            InvalidateVisual();
        };
    }

    public ImagePreviewViewModel? Image { get => GetValue(ImageProperty); set => SetValue(ImageProperty, value); }

    private Size ContentSize => _image is null ? default : new Size(_image.PixelSize.Width, _image.PixelSize.Height);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ImageProperty)
        {
            if (_image is not null)
            {
                _image.PropertyChanged -= OnImagePropertyChanged;
            }
            _image = Image;
            if (_image is not null && VisualRoot is not null)
            {
                _image.PropertyChanged += OnImagePropertyChanged;
            }
            _frame = 0;
            _offset = default;
            UpdateFit();
            UpdateAnimation();
            InvalidateVisual();
        }
        else if (change.Property == BoundsProperty)
        {
            UpdateFit();
            ClampOffset();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_image is not null)
        {
            _image.PropertyChanged += OnImagePropertyChanged;
        }
        UpdateFit();
        UpdateAnimation();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_image is not null)
        {
            _image.PropertyChanged -= OnImagePropertyChanged;
        }
        UpdateAnimation();
    }

    private void OnImagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ImagePreviewViewModel.IsFit))
        {
            _offset = default;
            UpdateFit();
        }
        else if (e.PropertyName == nameof(ImagePreviewViewModel.Zoom))
        {
            ClampOffset();
        }
        InvalidateVisual();
    }

    /// <summary>适应窗口时的缩放倍数：留出边距；位图最多 100%，矢量图可放大。</summary>
    private double FitZoom()
    {
        var content = ContentSize;
        var width = Bounds.Width - Padding * 2;
        var height = Bounds.Height - Padding * 2;
        if (content.Width <= 0 || content.Height <= 0 || width <= 0 || height <= 0)
        {
            return 1;
        }
        var fit = Math.Min(width / content.Width, height / content.Height);
        return Math.Clamp(_image!.IsVector ? fit : Math.Min(fit, 1), ImagePreviewViewModel.MinZoom, ImagePreviewViewModel.MaxZoom);
    }

    private void UpdateFit()
    {
        if (_image is { IsFit: true } image && Bounds.Width > 0)
        {
            image.Zoom = FitZoom();
        }
    }

    private Rect ImageRect()
    {
        var zoom = _image?.Zoom ?? 1;
        var size = ContentSize * zoom;
        var origin = new Point((Bounds.Width - size.Width) / 2, (Bounds.Height - size.Height) / 2) + _offset;
        return new Rect(origin, size);
    }

    // 图片比视口小时居中不动；比视口大时最多平移到边缘
    private void ClampOffset()
    {
        var size = ContentSize * (_image?.Zoom ?? 1);
        var maxX = Math.Max(0, (size.Width - Bounds.Width) / 2 + Padding);
        var maxY = Math.Max(0, (size.Height - Bounds.Height) / 2 + Padding);
        _offset = new Vector(Math.Clamp(_offset.X, -maxX, maxX), Math.Clamp(_offset.Y, -maxY, maxY));
    }

    public override void Render(DrawingContext context)
    {
        if (_image is null || _image.Frames.Count == 0)
        {
            return;
        }
        var rect = ImageRect();
        context.FillRectangle(_checker ??= CreateChecker(), rect);
        var bitmap = _image.Frames[Math.Min(_frame, _image.Frames.Count - 1)];
        // 放大查看像素时不插值
        var mode = !_image.IsVector && _image.Zoom >= 2 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality;
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = mode }))
        {
            context.DrawImage(bitmap, new Rect(bitmap.Size), rect);
        }
    }

    private IBrush CreateChecker()
    {
        var dark = ActualThemeVariant == ThemeVariant.Dark || ActualThemeVariant?.InheritVariant == ThemeVariant.Dark;
        var a = dark ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Color.FromRgb(0xFF, 0xFF, 0xFF);
        var b = dark ? Color.FromRgb(0x38, 0x38, 0x38) : Color.FromRgb(0xE6, 0xE6, 0xE6);
        var group = new DrawingGroup
        {
            Children =
            {
                new GeometryDrawing { Brush = new SolidColorBrush(a), Geometry = new RectangleGeometry(new Rect(0, 0, CheckerCell * 2, CheckerCell * 2)) },
                new GeometryDrawing { Brush = new SolidColorBrush(b), Geometry = new RectangleGeometry(new Rect(0, 0, CheckerCell, CheckerCell)) },
                new GeometryDrawing { Brush = new SolidColorBrush(b), Geometry = new RectangleGeometry(new Rect(CheckerCell, CheckerCell, CheckerCell, CheckerCell)) },
            },
        };
        return new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.None,
            DestinationRect = new RelativeRect(0, 0, CheckerCell * 2, CheckerCell * 2, RelativeUnit.Absolute),
        };
    }

    // ---- 交互 ----

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_image is null || e.Delta.Y == 0)
        {
            return;
        }
        ZoomAt(e.GetPosition(this), _image.Zoom * Math.Pow(1.15, e.Delta.Y));
        e.Handled = true;
    }

    // 以 point 为中心缩放：该点下的图片位置保持不动
    private void ZoomAt(Point point, double zoom)
    {
        var image = _image!;
        var before = ImageRect();
        var relative = new Point((point.X - before.X) / before.Width, (point.Y - before.Y) / before.Height);
        image.SetZoom(zoom);
        var after = ImageRect();
        _offset += new Vector(point.X - (after.X + relative.X * after.Width), point.Y - (after.Y + relative.Y * after.Height));
        ClampOffset();
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_image is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        if (e.ClickCount == 2)
        {
            if (_image.IsFit)
            {
                ZoomAt(e.GetPosition(this), 1);
            }
            else
            {
                _image.FitCommand.Execute(null);
            }
        }
        else
        {
            _dragStart = e.GetPosition(this);
            _dragOrigin = _offset;
            e.Pointer.Capture(this);
            Cursor = new Cursor(StandardCursorType.SizeAll);
        }
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragStart is { } start)
        {
            _offset = _dragOrigin + (e.GetPosition(this) - start);
            ClampOffset();
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragStart = null;
        e.Pointer.Capture(null);
        Cursor = null;
    }

    // ---- 动图 ----

    private void UpdateAnimation()
    {
        var play = _image is { IsAnimated: true } && VisualRoot is not null;
        if (!play)
        {
            _timer?.Stop();
            _timer = null;
            return;
        }
        if (_timer is null)
        {
            _timer = new DispatcherTimer { Interval = _image!.Delays[_frame] };
            _timer.Tick += OnFrameTick;
            _timer.Start();
        }
    }

    private void OnFrameTick(object? sender, EventArgs e)
    {
        if (_image is not { IsAnimated: true } image || _timer is null)
        {
            return;
        }
        _frame = (_frame + 1) % image.Frames.Count;
        _timer.Interval = image.Delays[_frame];
        InvalidateVisual();
    }
}
