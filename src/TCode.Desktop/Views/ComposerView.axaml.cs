using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TCode.Desktop.Localization;
using TCode.Desktop.Controls;
using TCode.Desktop.Services;
using TCode.Desktop.ViewModels;

namespace TCode.Desktop.Views;

public partial class ComposerView : UserControl
{
    // 紧凑形态下输入框以外占用的宽度（附件、模型选择器、发送、内边距），只在单行时记录
    private double _compactChrome = double.NaN;
    private ConversationViewModel? _vm;
    private bool _pastingText;

    public ComposerView()
    {
        InitializeComponent();
        // 隧道阶段拦截：Enter 发送，Shift+Enter 交给输入框换行。
        Input.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        // 粘贴：剪贴板里有图片（截图或图片文件）时作为附件，否则照常粘贴文本
        Input.AddHandler(TextBox.PastingFromClipboardEvent, OnPasting);
        // 拖入图片：隧道阶段拦截，先于输入框自己的文本拖放；不是图片的拖放照常交给输入框
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, (_, e) => OnDragOver(e), RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        AddHandler(DragDrop.DragOverEvent, (_, e) => OnDragOver(e), RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetDropTarget(false), RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        AddHandler(DragDrop.DropEvent, (_, e) => OnDrop(e), RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.Attachments.CollectionChanged -= OnAttachmentsChanged;
            }
            _vm = DataContext as ConversationViewModel;
            if (_vm is not null)
            {
                _vm.Attachments.CollectionChanged += OnAttachmentsChanged;
            }
            UpdateMultiline();
        };
        Input.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty || e.Property == BoundsProperty)
            {
                UpdateMultiline();
            }
        };
        Frame.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
            {
                UpdateMultiline();
            }
        };
    }

    public void FocusInput() => Input.Focus();

    /// <summary>
    /// 紧凑形态：内容放不下一行时切到上下两段布局。判断始终以“单行时输入框的宽度”为准，
    /// 切换后输入框变宽也不会因此再切回来（避免来回抖动）。
    /// </summary>
    private void UpdateMultiline()
    {
        if (!Classes.Contains("compact"))
        {
            return;
        }
        var multiline = Classes.Contains("multiline");
        if (!multiline && Input.Bounds.Width > 0)
        {
            _compactChrome = Frame.Bounds.Width - Input.Bounds.Width;
        }
        if (double.IsNaN(_compactChrome) || Frame.Bounds.Width <= 0)
        {
            return;
        }
        var text = Input.Text ?? string.Empty;
        var available = Frame.Bounds.Width - _compactChrome - Input.Padding.Left - Input.Padding.Right - 4;
        var needs = _vm is { HasAttachments: true } || text.Contains('\n') || (text.Length > 0 && MeasureWidth(text) > available);
        if (needs == multiline)
        {
            return;
        }
        // 单行 ↔ 两段：附件、模型选择器、发送按钮从原位置滑到新位置；外框贴底向上长高（SmoothSize），文字随之露出，圆角平滑过渡
        TCode.Desktop.Motion.Morph.Run(Frame, [Attach, Pickers, SendArea], () => Classes.Set("multiline", needs));
    }

    private void OnAttachmentsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateMultiline();

    private void OnPasting(object? sender, RoutedEventArgs e)
    {
        if (_pastingText || _vm is null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }
        e.Handled = true;
        _ = PasteAsync(clipboard);
    }

    private async Task PasteAsync(Avalonia.Input.Platform.IClipboard clipboard)
    {
        try
        {
            // 复制的图片文件（Finder 等）优先；其次是位图（截图、浏览器复制的图片）
            var files = await clipboard.TryGetFilesAsync();
            var imported = files?.Select(f => f.TryGetLocalPath()).OfType<string>().Where(p => AttachmentStore.MimeTypeOf(p) is not null).ToArray() ?? [];
            if (imported.Length > 0)
            {
                foreach (var path in imported)
                {
                    AddImage(await Task.Run(() => AttachmentStore.ImportFile(path)));
                }
                return;
            }
            if (await clipboard.TryGetBitmapAsync() is { } bitmap)
            {
                using (bitmap)
                {
                    // 大截图编码 PNG 可能要上百毫秒，放到后台
                    AddImage((await Task.Run(() => AttachmentStore.SaveBitmap(bitmap)), "image/png"));
                }
                return;
            }
        }
        catch (Exception ex)
        {
            App.Services?.GetService<ILogger<ComposerView>>()?.LogWarning(ex, "Paste image failed");
        }
        // 没有图片：照常粘贴文本（跳过本处理器）
        _pastingText = true;
        try
        {
            Input.Paste();
        }
        finally
        {
            _pastingText = false;
        }
    }

    /// <summary>拖入的内容里是否有图片（图片文件或位图）。</summary>
    public static bool HasImages(IDataTransfer data) =>
        ImageFiles(data).Length > 0 || data.Contains(DataFormat.Bitmap);

    private static string[] ImageFiles(IDataTransfer data) =>
        data.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().Where(p => AttachmentStore.MimeTypeOf(p) is not null).ToArray() ?? [];

    /// <summary>拖动经过（输入框自身，或会话区域转发）：有图片时显示可放下的高亮。</summary>
    public void OnDragOver(DragEventArgs e)
    {
        if (_vm is null || !HasImages(e.DataTransfer))
        {
            return;
        }
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
        SetDropTarget(true);
    }

    public void SetDropTarget(bool active) => Classes.Set("drop-target", active);

    /// <summary>放下：图片文件逐个导入，其次是位图；占位符插入到光标处。</summary>
    public void OnDrop(DragEventArgs e)
    {
        SetDropTarget(false);
        if (_vm is null || !HasImages(e.DataTransfer))
        {
            return;
        }
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
        var files = ImageFiles(e.DataTransfer);
        var bitmap = files.Length == 0 ? e.DataTransfer.TryGetBitmap() : null;
        _ = AddDroppedAsync(files, bitmap);
    }

    private async Task AddDroppedAsync(string[] files, Avalonia.Media.Imaging.Bitmap? bitmap)
    {
        try
        {
            foreach (var path in files)
            {
                AddImage(await Task.Run(() => AttachmentStore.ImportFile(path)));
            }
            if (bitmap is not null)
            {
                using (bitmap)
                {
                    AddImage((await Task.Run(() => AttachmentStore.SaveBitmap(bitmap)), "image/png"));
                }
            }
            Input.Focus();
        }
        catch (Exception ex)
        {
            App.Services?.GetService<ILogger<ComposerView>>()?.LogWarning(ex, "Drop image failed");
        }
    }

    private async void OnAttachClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType(Strings.Get(TCodeLangResourceKind.ImageFileTypeName))
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp"],
                    AppleUniformTypeIdentifiers = ["public.image"],
                    MimeTypes = ["image/*"],
                },
            ],
        });
        foreach (var path in files.Select(f => f.TryGetLocalPath()).OfType<string>())
        {
            AddImage(await Task.Run(() => AttachmentStore.ImportFile(path)));
        }
        Input.Focus();
    }

    /// <summary>加入附件，并在光标处插入占位符「[图片 #N]」。</summary>
    private void AddImage((string Path, string MimeType)? file)
    {
        if (file is not { } f || _vm is null)
        {
            return;
        }
        var attachment = _vm.AddAttachment(f.Path, f.MimeType);
        var text = Input.Text ?? "";
        var caret = Math.Clamp(Input.CaretIndex, 0, text.Length);
        var before = caret > 0 && !char.IsWhiteSpace(text[caret - 1]) ? " " : "";
        Input.SelectedText = before + attachment.Placeholder + " ";
    }

    private void OnThumbnailTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ImageAttachmentViewModel image)
        {
            ImageOpener.Open(this, image);
        }
    }

    private double MeasureWidth(string text)
    {
        using var layout = new TextLayout(text, new Typeface(Input.FontFamily, Input.FontStyle, Input.FontWeight),
            Input.FontSize, null);
        return layout.WidthIncludingTrailingWhitespace;
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift) || DataContext is not ConversationViewModel vm)
        {
            return;
        }
        e.Handled = true;
        if (vm.SendCommand.CanExecute(null))
        {
            vm.SendCommand.Execute(null);
        }
    }
}
