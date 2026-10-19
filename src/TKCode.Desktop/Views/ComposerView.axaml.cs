using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TKCode.Desktop.Localization;
using TKCode.Desktop.Controls;
using TKCode.Desktop.Controls.Composer;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.ViewModels.Completion;

namespace TKCode.Desktop.Views;

public partial class ComposerView : UserControl
{
    // 紧凑形态下输入框以外占用的宽度（附件、模型选择器、发送、内边距），只在单行时记录
    private double _compactChrome = double.NaN;
    private const double MinSingleLineInputWidth = 160;
    private ConversationViewModel? _vm;
    private readonly EditorCompletionHost _completionHost;

    public ComposerView()
    {
        InitializeComponent();
        // 隧道阶段拦截：Enter 发送，Shift+Enter 交给输入框换行。
        Input.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        // 粘贴：剪贴板里有图片时作为图片附件，其他文件插入为附件 tag（只带绝对路径），否则照常粘贴文本
        Input.Pasting += OnPasting;
        // 拖入：图片成为图片附件；其他文件不读入会话，插入附件 tag，发送时把绝对路径包进提示词
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, (_, e) => OnDragOver(e), RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        AddHandler(DragDrop.DragOverEvent, (_, e) => OnDragOver(e), RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetDropTarget(false), RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        AddHandler(DragDrop.DropEvent, (_, e) => OnDrop(e), RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        // 补全：获得焦点的输入框提供光标位置；光标移动时重新检测查询
        _completionHost = new EditorCompletionHost(Input);
        Input.GotFocus += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.Completion.Host = _completionHost;
                _vm.Completion.Invalidate();
            }
        };
        Input.SelectionChanged += (_, _) =>
        {
            if (_vm?.Completion.Host == _completionHost)
            {
                _vm.Completion.Invalidate();
            }
        };
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.Attachments.CollectionChanged -= OnAttachmentsChanged;
                _vm.Completion.HighlightChanged -= OnCompletionHighlightChanged;
                _vm.Completion.PropertyChanged -= OnCompletionPropertyChanged;
                _vm.ModelPickerRequested -= OnModelPickerRequested;
            }
            _vm = DataContext as ConversationViewModel;
            if (_vm is not null)
            {
                _vm.Attachments.CollectionChanged += OnAttachmentsChanged;
                _vm.Completion.HighlightChanged += OnCompletionHighlightChanged;
                _vm.Completion.PropertyChanged += OnCompletionPropertyChanged;
                _vm.ModelPickerRequested += OnModelPickerRequested;
            }
            UpdateCompletionPopup();
            UpdateMultiline();
        };
        Input.ContentChanged += (_, _) => UpdateMultiline();
        Input.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
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
        var document = Input.Document;
        var available = Frame.Bounds.Width - _compactChrome - Input.Padding.Left - Input.Padding.Right - 4;
        // 栏宽太窄（编辑区打开时）单行输入框会被挤得只剩几个字宽，同样切到两段
        var needs = _vm is { HasAttachments: true } || document.ToCharText(0, document.Length).Contains('\n') ||
                    available < MinSingleLineInputWidth || (!document.IsEmpty && Input.SingleLineWidth > available);
        if (needs == multiline)
        {
            return;
        }
        // 单行 ↔ 两段：附件、模型选择器、发送按钮从原位置滑到新位置；外框贴底向上长高（SmoothSize），文字随之露出，圆角平滑过渡
        TKCode.Desktop.Motion.Morph.Run(Frame, [Attach, Pickers, SendArea], () => Classes.Set("multiline", needs));
    }

    private void OnAttachmentsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateMultiline();

    private void OnPasting(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
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
            // 复制的文件（Finder 等）优先：图片导入为图片附件，其他文件只插入绝对路径；其次是位图（截图）
            var files = await clipboard.TryGetFilesAsync();
            var paths = files?.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray() ?? [];
            if (paths.Length > 0)
            {
                foreach (var path in paths)
                {
                    if (AttachmentStore.MimeTypeOf(path) is not null)
                    {
                        var imported = await Task.Run(() => AttachmentStore.ImportFile(path));
                        if (imported is not null)
                        {
                            AddImage(imported);
                            continue;
                        }
                    }
                    AddLocalPath(path);
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
            App.Services?.GetService<ILogger<ComposerView>>()?.LogWarning(ex, "Paste failed");
        }
        // 没有文件：照常粘贴文本（不再触发本处理器）
        await Input.PasteAsync(raiseEvent: false);
    }

    /// <summary>拖入的内容里是否有图片（图片文件或位图）。</summary>
    public static bool HasImages(IDataTransfer data) =>
        ImageFiles(data).Length > 0 || data.Contains(DataFormat.Bitmap);

    /// <summary>拖入的内容里能否放下：图片，或任意本地文件 / 文件夹。</summary>
    private static bool CanDrop(IDataTransfer data) => HasImages(data) || LocalPaths(data).Length > 0;

    private static string[] LocalPaths(IDataTransfer data) =>
        data.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray() ?? [];

    private static string[] ImageFiles(IDataTransfer data) =>
        data.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().Where(p => AttachmentStore.MimeTypeOf(p) is not null).ToArray() ?? [];

    /// <summary>拖动经过（输入框自身，或会话区域转发）：有图片或本地文件时显示可放下的高亮。</summary>
    public void OnDragOver(DragEventArgs e)
    {
        if (_vm is null || !CanDrop(e.DataTransfer))
        {
            return;
        }
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
        SetDropTarget(true);
    }

    public void SetDropTarget(bool active) => Classes.Set("drop-target", active);

    /// <summary>放下：图片文件逐个导入，其次是位图；其他文件插入为附件 tag（绝对路径），目录仍是文件夹 tag。</summary>
    public void OnDrop(DragEventArgs e)
    {
        SetDropTarget(false);
        if (_vm is not { } vm || !CanDrop(e.DataTransfer))
        {
            return;
        }
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
        var paths = LocalPaths(e.DataTransfer);
        // 扩展名像图片、但是目录（例如 pics.png）时不当图片导入
        var files = paths.Where(p => File.Exists(p) && AttachmentStore.MimeTypeOf(p) is not null).ToArray();
        var images = new HashSet<string>(files, StringComparer.Ordinal);
        var others = paths.Where(p => !images.Contains(p)).ToArray();
        if (others.Length > 0)
        {
            InsertWithSpacing(others.SelectMany(p => new Segment[] { AttachedFiles.SegmentForLocalPath(p, vm.CurrentCwd), new TextSegment(" ") }).SkipLast(1).ToList());
            Input.Focus();
        }
        // 纯位图（截图拖入）才当图片；拖文件时数据里可能附带图标，不要再当成一张图片
        var bitmap = files.Length == 0 && others.Length == 0 ? e.DataTransfer.TryGetBitmap() : null;
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
            FileTypeFilter = [AllFilesType(), ImageFilesType(), DocumentFilesType()],
        });
        foreach (var path in files.Select(f => f.TryGetLocalPath()).OfType<string>())
        {
            if (AttachmentStore.MimeTypeOf(path) is not null)
            {
                var imported = await Task.Run(() => AttachmentStore.ImportFile(path));
                if (imported is not null)
                {
                    AddImage(imported);
                    continue;
                }
            }
            AddLocalPath(path);
        }
        Input.Focus();
    }

    /// <summary>所有文件。macOS 用 public.item；其他平台用通配模式。</summary>
    private static FilePickerFileType AllFilesType() => new(Strings.Get(TKCodeLangResourceKind.AllFilesTypeName))
    {
        Patterns = ["*.*", "*"],
        AppleUniformTypeIdentifiers = ["public.item"],
        MimeTypes = ["*/*"],
    };

    private static FilePickerFileType ImageFilesType() => new(Strings.Get(TKCodeLangResourceKind.ImageFileTypeName))
    {
        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp"],
        AppleUniformTypeIdentifiers = ["public.image"],
        MimeTypes = ["image/*"],
    };

    /// <summary>日志、文本和常见办公文档。任意其他格式走 <see cref="AllFilesType"/>。</summary>
    private static FilePickerFileType DocumentFilesType() => new(Strings.Get(TKCodeLangResourceKind.DocumentFileTypeName))
    {
        Patterns =
        [
            "*.txt", "*.log", "*.md", "*.csv", "*.json", "*.xml", "*.yaml", "*.yml", "*.pdf",
            "*.xls", "*.xlsx", "*.doc", "*.docx", "*.ppt", "*.pptx",
        ],
        AppleUniformTypeIdentifiers =
        [
            "public.text",
            "public.plain-text",
            "public.comma-separated-values-text",
            "public.json",
            "public.xml",
            "com.adobe.pdf",
            "com.microsoft.excel.xls",
            "org.openxmlformats.spreadsheetml.sheet",
            "com.microsoft.word.doc",
            "org.openxmlformats.wordprocessingml.document",
            "com.microsoft.powerpoint.ppt",
            "org.openxmlformats.presentationml.presentation",
        ],
        MimeTypes =
        [
            "text/*",
            "application/pdf",
            "application/json",
            "application/xml",
            "application/vnd.ms-excel",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "application/msword",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "application/vnd.ms-powerpoint",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ],
    };

    /// <summary>加入附件，并在光标处插入图片 tag。</summary>
    private void AddImage((string Path, string MimeType)? file)
    {
        if (file is not { } f || _vm is null)
        {
            return;
        }
        var attachment = _vm.AddAttachment(f.Path, f.MimeType);
        InsertWithSpacing([attachment.Tag]);
    }

    /// <summary>在光标处插入片段：与前面的文字之间补一个空格，后面跟一个空格。</summary>
    private void InsertWithSpacing(IReadOnlyList<Segment> segments)
    {
        var caret = Input.SelectionStart;
        var needsSpace = caret > 0 && Input.Document.CharAt(caret - 1) is { } c && !char.IsWhiteSpace(c);
        Input.InsertSegments([.. needsSpace ? [new TextSegment(" ")] : Array.Empty<Segment>(), .. segments, new TextSegment(" ")]);
    }

    /// <summary>非图片的本地路径：文件是附件 tag，目录是文件夹 tag。</summary>
    private void AddLocalPath(string path)
    {
        if (_vm is null)
        {
            return;
        }
        InsertWithSpacing([AttachedFiles.SegmentForLocalPath(path, _vm.CurrentCwd)]);
    }

    private void OnCutClick(object? sender, RoutedEventArgs e) => _ = Input.CutAsync();

    private void OnCopyClick(object? sender, RoutedEventArgs e) => _ = Input.CopyAsync();

    private void OnPasteClick(object? sender, RoutedEventArgs e) => _ = Input.PasteAsync();

    private void OnSelectAllClick(object? sender, RoutedEventArgs e) => Input.SelectAll();

    private void OnThumbnailTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ImageAttachmentViewModel image)
        {
            ImageOpener.Open(this, image);
        }
    }

    /// <summary>/model：只在提供光标的那个输入框上打开它自己的模型选择器。</summary>
    private void OnModelPickerRequested()
    {
        if (_vm?.Completion.Host == _completionHost)
        {
            Picker.Toggle();
        }
    }

    private void OnCompletionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CompletionViewModel.IsOpen) or nameof(CompletionViewModel.Query))
        {
            UpdateCompletionPopup();
        }
    }

    /// <summary>
    /// 补全弹层只在提供光标的那个输入框上打开（空状态与停靠的两个输入框共用同一个 ViewModel），
    /// 锚定在查询起点（@ 或 /）的位置，向上展开。
    /// </summary>
    private void UpdateCompletionPopup()
    {
        var completion = _vm?.Completion;
        var open = completion is { IsOpen: true, Query: not null } && completion.Host == _completionHost && IsEffectivelyVisible;
        if (open)
        {
            // 横向对齐到查询起点（弹层自身的 Popup 不使用 PlacementRect）；不超出输入框右边
            const double PopupWidth = 440;
            var x = Input.CaretBounds(completion!.Query!.Start).X - 12;
            CompletionPopup.HorizontalOffset = Math.Clamp(x, -8, Math.Max(-8, Input.Bounds.Width - PopupWidth + 8));
        }
        if (CompletionPopup.IsOpen != open)
        {
            CompletionPopup.IsOpen = open;
        }
    }

    /// <summary>点击弹层外（轻触关闭）：当作 Esc，同一个查询不再弹出。</summary>
    private void OnCompletionPopupClosed(object? sender, EventArgs e)
    {
        if (_vm?.Completion is { IsOpen: true } completion && completion.Host == _completionHost)
        {
            completion.Dismiss();
        }
    }

    private void OnCompletionHighlightChanged(CompletionItemViewModel item)
    {
        if (_vm?.Completion.Host == _completionHost)
        {
            CompletionList.ContainerFromItem(item)?.BringIntoView();
        }
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        // @ / 补全列表打开时：↑↓ 移动，回车 / Tab 确认，Esc 关闭
        if (DataContext is ConversationViewModel { Completion.IsOpen: true } picking)
        {
            var completion = picking.Completion;
            switch (e.Key)
            {
                case Key.Up:
                    completion.Move(-1);
                    e.Handled = true;
                    return;
                case Key.Down:
                    completion.Move(1);
                    e.Handled = true;
                    return;
                case Key.Enter or Key.Tab when !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                    // Tab 在文件夹上：进入文件夹继续搜索
                    if (e.Key == Key.Tab && completion.TabComplete() || completion.AcceptHighlighted())
                    {
                        e.Handled = true;
                        return;
                    }
                    break;
                case Key.Escape:
                    completion.Dismiss();
                    e.Handled = true;
                    return;
            }
        }
        // Shift+Tab：切换模式（Agent → Plan → ACP → 自定义 role）
        if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.Shift && DataContext is ConversationViewModel cycling)
        {
            cycling.RolePicker.Cycle();
            e.Handled = true;
            return;
        }
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

    /// <summary>补全与输入框之间的桥：读取光标，确认后放置光标。</summary>
    private sealed class EditorCompletionHost(ComposerEditor editor) : ICompletionHost
    {
        public int CaretIndex => editor.CaretIndex;

        public void SetCaret(int position)
        {
            editor.CaretIndex = position;
            editor.Focus();
        }
    }
}
