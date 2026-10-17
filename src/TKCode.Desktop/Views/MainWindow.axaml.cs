using System.ComponentModel;
using AtomUI.Desktop.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using StreamMark.Avalonia.Extensions;
using TKCode.Desktop.Controls;
using TKCode.Desktop.Localization;
using TKCode.Desktop.ViewModels;

namespace TKCode.Desktop.Views;

public partial class MainWindow : AtomUI.Desktop.Controls.Window
{
    private const double HeaderHeight = 44;
    private const double MacTrafficLightInset = 78;
    private const double CaptionButtonsWidth = 138;

    private const double PanelTogglesWidth = 64;
    private double _workbenchWidth = 520;
    private double _explorerWidth = 284;
    private bool _explorerResized;  // 用户拖过文件树分割条后，不再按 1/4 上限收窄
    private const double ConversationMinWidth = 360;
    private const double EditorMinWidth = 320;
    private const double ExplorerMinWidth = 200;
    private readonly TranscriptAdapter _transcript;

    private void OnSessionListScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (e.Source is Avalonia.Controls.ScrollViewer viewer && DataContext is MainWindowViewModel vm && vm.Conversation.HasMoreSessions &&
            viewer.Offset.Y + viewer.Viewport.Height >= viewer.Extent.Height - 120)
        {
            vm.Conversation.LoadMoreSessionsCommand.Execute(null);
        }
    }

    /// <summary>双击会话标题：行内重命名。</summary>
    private void OnSessionDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: SessionItemViewModel item })
        {
            item.BeginRenameCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>危险操作确认框（AtomUI MessageBox，确定按钮为“删除”）。</summary>
    private Task<bool> ConfirmDangerAsync(string title, string message) =>
        ConfirmAsync(title, message, Strings.Get(TKCodeLangResourceKind.SessionDelete));

    private async Task<bool> ConfirmAsync(string title, string message, string okText)
    {
        var result = await MessageBox.ShowMessageBoxModalAsync(
            new Avalonia.Controls.TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 360 },
            options: new MessageBoxOptions
            {
                Title = title,
                Style = MessageBoxStyle.Confirm,  // Warning 只有确定按钮；Confirm 才带取消（Esc 也是取消）
                OkButtonText = okText,
                CancelButtonText = Strings.Get(TKCodeLangResourceKind.Cancel),
            },
            topLevel: this);
        return result is DialogCode.Accepted;
    }

    /// <summary>
    /// 会话中的相对图片按会话所在项目解析；本地图片只允许读取已添加的项目目录之下的文件。
    /// </summary>
    private void UpdateImageRoots(MainWindowViewModel vm)
    {
        var cwd = vm.Conversation.Active?.Cwd ?? vm.Conversation.Workspace?.Path;
        StreamMark.Avalonia.Extensions.MarkdownServices.SetImageBaseUri(Transcript, cwd);
        MarkdownSetup.SetAllowedFileRoots(vm.Sidebar.Projects.Select(p => p.Path).Append(cwd ?? ""));
    }

    /// <summary>滚动到接近顶部时加载更早的一页历史（插入后会话视图自己保持当前阅读位置）。</summary>
    private async Task LoadEarlierIfNearTopAsync()
    {
        if (Transcript.Offset.Y > 300 || DataContext is not MainWindowViewModel vm ||
            vm.Conversation.Active is not { HasEarlierHistory: true, IsLoadingEarlier: false })
        {
            return;
        }
        await vm.Conversation.LoadEarlierHistoryAsync();
    }

    private void UpdateScrollToBottom() =>
        Motion.Presence.SetIsPresent(ScrollToBottomButton, !Transcript.IsPinnedToBottom && Transcript.Extent.Height > Transcript.Viewport.Height + 1);

    private void OnScrollToBottomClick(object? sender, RoutedEventArgs e)
    {
        Transcript.ScrollToEnd();
        Motion.Presence.SetIsPresent(ScrollToBottomButton, false);
    }

    private void OnMessageImageTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ImageAttachmentViewModel image)
        {
            ImageOpener.Open(this, image);
        }
    }

    private void OnMarkdownLinkClicked(object? sender, StreamMark.Avalonia.Rendering.LinkClickedEventArgs e)
    {
        if (Uri.TryCreate(e.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
        {
            e.Handled = true;
            _ = OpenLinkAsync(uri, e.RequiresConfirmation);
        }
    }

    // 链接文字看起来是另一个地址（例如 [https://github.com](https://evil.example)）时先确认
    private async Task OpenLinkAsync(Uri uri, bool confirm)
    {
        if (confirm && !await ConfirmAsync(Strings.Get(TKCodeLangResourceKind.LinkConfirmTitle),
                Strings.Format(TKCodeLangResourceKind.LinkConfirmMessageFormat, uri.AbsoluteUri), Strings.Get(TKCodeLangResourceKind.LinkOpen)))
        {
            return;
        }
        await Launcher.LaunchUriAsync(uri);
    }

    public MainWindow()
    {
        InitializeComponent();
        // 任务球里的“更改”入口：打开工作区的变更面板
        TaskOrb.OpenChangesRequested += () => (DataContext as ViewModels.MainWindowViewModel)?.Workbench.OpenChangesCommand.Execute(null);
        ExplorerSplitter.DragCompleted += (_, _) =>
        {
            _explorerResized = true;
            _explorerWidth = MainGrid.ColumnDefinitions[4].Width.Value;
        };
        ConfigureChrome();

        // 会话视图：渲染扩展（公式、代码高亮、图表）对窗口内所有 Markdown 生效
        MarkdownServices.SetServices(this, MarkdownSetup.Services);
        _transcript = new TranscriptAdapter(Transcript.Entries);
        // 发送新消息时回到底部并恢复贴底跟随（流式中的贴底由会话视图自己处理）
        _transcript.UserMessageAdded += () => Transcript.ScrollToEnd();
        // 离开底部（不再贴底跟随）且内容超过一屏时显示“回到底部”
        Transcript.ScrollChanged += (_, _) =>
        {
            UpdateScrollToBottom();
            _ = LoadEarlierIfNearTopAsync();
        };
        // 侧栏会话列表滚到接近底部时加载下一页
        SessionList.AddHandler(Avalonia.Controls.ScrollViewer.ScrollChangedEvent, OnSessionListScrolled);
        // 侧栏开合动画期间会话视图固定宽度（降载：长会话不逐帧重新排版），结束后重排一次
        SidebarReveal.AnimatingChanged += (reveal, animating) =>
        {
            if (animating)
            {
                Freeze(Transcript, Transcript.Bounds.Width + (reveal.IsOpen ? 0 : 256));
            }
            else
            {
                ReleaseFreeze();
            }
        };
        // 系统“减少动态效果”可能在后台被修改
        Activated += (_, _) => Motion.MotionSettings.RefreshSystem();
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        // 把图片拖到消息区域：同样作为输入框的附件（输入框本身目标太小）
        DragDrop.SetAllowDrop(Transcript, true);
        Transcript.AddHandler(DragDrop.DragEnterEvent, (_, e) => DockedComposer.OnDragOver(e));
        Transcript.AddHandler(DragDrop.DragOverEvent, (_, e) => DockedComposer.OnDragOver(e));
        Transcript.AddHandler(DragDrop.DragLeaveEvent, (_, _) => DockedComposer.SetDropTarget(false));
        Transcript.AddHandler(DragDrop.DropEvent, (_, e) => DockedComposer.OnDrop(e));
        // Markdown 链接（会话与文件预览，含图片链接 / 徽章）：网页与邮件交给系统打开
        AddHandler(StreamMark.Avalonia.MarkdownView.LinkClickedEvent, OnMarkdownLinkClicked);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.Conversation.ConfirmDangerAsync = ConfirmDangerAsync;
                vm.Workbench.ConfirmAsync = ConfirmAsync;
                vm.Conversation.SetClipboardAsync = text => Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
                // /role：打开当前可见的那个模式选择器
                vm.Conversation.RolePickerRequested += () => (DockedRolePicker.IsEffectivelyVisible ? DockedRolePicker : EmptyRolePicker).Toggle();
                vm.PropertyChanged += OnViewModelPropertyChanged;
                vm.Conversation.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ConversationViewModel.Active))
                    {
                        _transcript.Attach(vm.Conversation.Active);
                        Transcript.ScrollToEnd();
                        // 切换会话：会话内容淡入并轻微上移
                        if (vm.Conversation.Active is not null)
                        {
                            Motion.Presence.PlayEnter(Transcript, Motion.PresenceEffect.SlideUp);
                        }
                    }
                    // 发送第一条消息：底部输入框从居中输入框的位置落到底部
                    if (e.PropertyName == nameof(ConversationViewModel.IsEmpty) && !vm.Conversation.IsEmpty && EmptyComposer.IsLoaded)
                    {
                        Motion.Morph.Fly(EmptyComposer, DockedComposer, ConversationColumn);
                    }
                    if (e.PropertyName is nameof(ConversationViewModel.Active) or nameof(ConversationViewModel.Workspace))
                    {
                        UpdateImageRoots(vm);
                    }
                };
                vm.Sidebar.Projects.CollectionChanged += (_, _) => UpdateImageRoots(vm);
                _transcript.Attach(vm.Conversation.Active);
                UpdateImageRoots(vm);
                vm.Workbench.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(WorkbenchViewModel.IsMaximized))
                    {
                        UpdateColumns(vm);
                    }
                    // 切换标签页：内容淡入
                    if (e.PropertyName == nameof(WorkbenchViewModel.ActiveTab) && vm.Workbench.ActiveTab is not null && ActiveTabContent.IsLoaded)
                    {
                        Motion.Presence.PlayEnter(ActiveTabContent, Motion.PresenceEffect.Fade);
                    }
                };
                vm.Palette.PropertyChanged += OnPalettePropertyChanged;
                vm.Explorer.CollapseAnimation = AnimateTreeCollapse;
                vm.Palette.NewProjectFolderRequested += () => OnAddProjectClick(null, new RoutedEventArgs());
                UpdateHeaderInsets(vm);
                UpdateColumns(vm, animate: false);
                // 启动加载页：首次连接 Agent Host、加载模型与会话后退场；最多等 12 秒（失败信息显示在主界面）
                if (vm.Conversation.IsInitialized)
                {
                    Splash.Dismiss();
                }
                else
                {
                    vm.Conversation.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName == nameof(ConversationViewModel.IsInitialized) && vm.Conversation.IsInitialized)
                        {
                            Splash.Dismiss();
                        }
                    };
                    DispatcherTimer.RunOnce(Splash.Dismiss, TimeSpan.FromSeconds(12));
                }
            }
        };
        // 加载页退场时主界面淡入（只动透明度：AtomUI 弹层按触发按钮当时的变换后边界定位，入场期间打开下拉不会偏位）
        Splash.Dismissing += () => Motion.Presence.PlayEnter(AppPage, Motion.PresenceEffect.Fade);
    }

    /// <summary>
    /// Cursor 式窗口：不显示 AtomUI 标题栏，内容延伸到窗口顶部。
    /// macOS 保留原生红绿灯并移到侧栏顶部；Windows / Linux 在右上角自绘窗口按钮。
    /// </summary>
    private void ConfigureChrome()
    {
        if (OperatingSystem.IsMacOS())
        {
            // IsTitleBarVisible=False 时 AtomUI 主题会切到 BorderOnly（连红绿灯一起去掉），这里用本地值恢复。
            WindowDecorations = WindowDecorations.Full;
            ExtendClientAreaTitleBarHeightHint = HeaderHeight;
            MacStandardWindowButtons.SetOffsetX(this, 16);
            MacStandardWindowButtons.SetSpacing(this, 20);
        }
        else
        {
            CaptionButtons.IsVisible = true;
        }
    }

    private void UpdateHeaderInsets(MainWindowViewModel vm)
    {
        var left = OperatingSystem.IsMacOS() ? MacTrafficLightInset : 8;
        SidebarHeader.Margin = new Thickness(left, 0, 0, 0);
        SettingsPage.SetTitleBarInset(left);
        MainHeaderLeft.Margin = new Thickness(vm.IsSidebarOpen ? 8 : left, 0, 0, 0);

        // 右上角的面板开关（以及 Windows / Linux 的窗口按钮）落在最右一栏的标题栏上，该栏标题栏右侧留出位置
        var caption = OperatingSystem.IsMacOS() ? 0 : CaptionButtonsWidth;
        PanelToggles.Margin = new Thickness(0, 0, 8 + caption, 0);
        var reserved = 8 + caption + PanelTogglesWidth + 4;
        ConversationHeader.Margin = new Thickness(0, 0, vm.IsWorkbenchOpen || vm.IsExplorerOpen ? 0 : reserved, 0);
        EditorHeaderRight.Margin = new Thickness(0, 0, vm.IsExplorerOpen ? 8 : reserved, 0);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MainWindowViewModel vm)
        {
            return;
        }
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.IsSidebarOpen):
                UpdateHeaderInsets(vm);
                break;
            case nameof(MainWindowViewModel.IsWorkbenchOpen):
            case nameof(MainWindowViewModel.IsExplorerOpen):
                UpdateColumns(vm);
                UpdateHeaderInsets(vm);
                break;
        }
    }

    /// <summary>
    /// 主区域列宽：对话（*）| 分割条 | 编辑区 | 分割条 | 文件树。关闭时记住宽度；编辑区最大化时隐藏对话栏。
    /// 打开 / 关闭 / 最大化时列宽按弹簧动画过渡（animate=false 时直接到位：首次布局、动效关闭）。
    /// </summary>
    private void UpdateColumns(MainWindowViewModel vm, bool animate = true)
    {
        var columns = MainGrid.ColumnDefinitions;
        var maximized = vm.IsWorkbenchOpen && vm.Workbench.IsMaximized;

        if (_columnMotion is not { IsAnimating: true })
        {
            if (columns[2].Width.IsAbsolute && columns[2].ActualWidth > 0 && !_wasMaximized)
            {
                _workbenchWidth = columns[2].ActualWidth;
            }
            // 拖过之后只认拖出的宽度（DragCompleted 里记下），被挤窄不算
            if (!_explorerResized && columns[4].ActualWidth > 0)
            {
                _explorerWidth = columns[4].ActualWidth;
            }
        }

        // 按可用宽度分配：对话栏至少 ConversationMinWidth；文件树未被拖过时不超过 1/4（最大 420），拖过后保持拖出的宽度、
        // 只在放不下时让位；编辑区取剩余与记忆宽度中的较小值
        var available = MainGrid.Bounds.Width > 0 ? MainGrid.Bounds.Width : Bounds.Width - (vm.IsSidebarOpen ? 256 : 0);
        var explorerRoom = available - (maximized ? 0 : ConversationMinWidth) - (vm.IsWorkbenchOpen ? EditorMinWidth : 0) - 2;
        var explorerWant = _explorerResized ? _explorerWidth : Math.Min(Math.Min(_explorerWidth, available * 0.25), 420);
        var explorer = vm.IsExplorerOpen ? Math.Max(ExplorerMinWidth, Math.Min(explorerWant, explorerRoom)) : 0;
        var editorRoom = available - explorer - (maximized ? 0 : ConversationMinWidth) - 2;
        var editor = !vm.IsWorkbenchOpen ? 0 : Math.Max(EditorMinWidth, Math.Min(_workbenchWidth, editorRoom));
        _wasMaximized = maximized;

        void Final()
        {
            columns[0].Width = maximized ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            columns[0].MinWidth = maximized ? 0 : Math.Min(ConversationMinWidth, Math.Max(0, available - explorer - editor - 2));
            ConversationColumn.IsVisible = !maximized;
            WorkbenchSplitter.IsVisible = vm.IsWorkbenchOpen && !maximized;
            columns[2].Width = !vm.IsWorkbenchOpen ? new GridLength(0)
                : maximized ? new GridLength(1, GridUnitType.Star)
                : new GridLength(editor);
            columns[2].MinWidth = vm.IsWorkbenchOpen ? EditorMinWidth : 0;
            columns[4].Width = new GridLength(explorer);
            columns[4].MinWidth = vm.IsExplorerOpen ? ExplorerMinWidth : 0;
            EditorColumn.IsVisible = vm.IsWorkbenchOpen;
            ExplorerColumn.IsVisible = vm.IsExplorerOpen;
            ExplorerSplitter.IsVisible = vm.IsExplorerOpen;
            ReleaseFreeze();
        }

        var fromEditor = EditorColumn.IsVisible ? columns[2].ActualWidth : 0;
        var fromExplorer = ExplorerColumn.IsVisible ? columns[4].ActualWidth : 0;
        var toEditor = !vm.IsWorkbenchOpen ? 0 : maximized ? Math.Max(0, available - explorer - 2) : editor;
        if (!animate || !Motion.MotionSettings.IsEnabled || !IsLoaded ||
            (Math.Abs(fromEditor - toEditor) < 1 && Math.Abs(fromExplorer - explorer) < 1))
        {
            _columnMotion?.Snap(1);
            Final();
            return;
        }

        // 动画期间：对话栏照常占剩余宽度，编辑区 / 文件树按像素宽度插值
        columns[0].Width = new GridLength(1, GridUnitType.Star);
        columns[0].MinWidth = 0;
        columns[2].MinWidth = 0;
        columns[4].MinWidth = 0;
        ConversationColumn.IsVisible = true;
        WorkbenchSplitter.IsVisible = vm.IsWorkbenchOpen && !maximized;
        EditorColumn.IsVisible = true;
        ExplorerColumn.IsVisible = true;
        ExplorerSplitter.IsVisible = vm.IsExplorerOpen;
        // 降载：面板内容固定为较大的那个宽度（左对齐、越界部分被相邻栏或窗口边缘遮住），对话区会话视图固定当前宽度，
        // 动画只移动列边界，结束后各自只重新排版一次
        Freeze(EditorColumn, Math.Max(fromEditor, toEditor));
        Freeze(ExplorerColumn, Math.Max(fromExplorer, explorer));
        Freeze(Transcript, Math.Max(Transcript.Bounds.Width, available - toEditor - explorer - 2));

        // 打断进行中的动画：停在当前宽度，从这里继续
        _columnMotion?.Snap(_columnMotion.Value);
        _columnMotion = new Motion.MotionValue(this, 0, p =>
        {
            columns[2].Width = new GridLength(Math.Max(0, fromEditor + (toEditor - fromEditor) * p));
            columns[4].Width = new GridLength(Math.Max(0, fromExplorer + (explorer - fromExplorer) * p));
        });
        _columnMotion.AnimateTo(1, Motion.MotionTokens.Standard, Final);
    }

    private Motion.MotionValue? _columnMotion;
    private bool _wasMaximized;
    private readonly HashSet<Control> _frozen = [];

    private void Freeze(Control control, double width)
    {
        if (width <= 0)
        {
            return;
        }
        control.Width = width;
        if (control != Transcript)
        {
            control.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        }
        _frozen.Add(control);
    }

    private void ReleaseFreeze()
    {
        if (SidebarReveal.IsAnimating || _columnMotion is { IsAnimating: true })
        {
            return;
        }
        foreach (var control in _frozen)
        {
            control.ClearValue(WidthProperty);
            control.ClearValue(HorizontalAlignmentProperty);
        }
        _frozen.Clear();
    }

    /// <summary>文件树收起目录：子节点一起收起高度并淡出，播完再移除；中途再次展开时恢复原样。</summary>
    private Task AnimateTreeCollapse(FileNodeViewModel folder, IReadOnlyList<FileNodeViewModel> nodes)
    {
        var rows = nodes.Select(n => ExplorerList.ContainerFromItem(n)).OfType<Control>().Select(c => (Row: c, Height: c.Bounds.Height)).ToList();
        if (!Motion.MotionSettings.IsEnabled || rows.Count == 0 || !ExplorerList.IsEffectivelyVisible)
        {
            return Task.CompletedTask;
        }
        var done = new TaskCompletionSource();
        void Restore()
        {
            foreach (var (row, _) in rows)
            {
                row.ClearValue(OpacityProperty);
                row.ClearValue(MaxHeightProperty);
            }
        }
        var progress = new Motion.MotionValue(ExplorerList, 1, p =>
        {
            var q = Math.Clamp(p, 0, 1);
            foreach (var (row, height) in rows)
            {
                row.Opacity = q;
                row.MaxHeight = height * q;
            }
        });
        void OnFolderChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(FileNodeViewModel.IsExpanded) && folder.IsExpanded)
            {
                progress.AnimateTo(1, Motion.MotionTokens.Standard, Finish);
            }
        }
        void Finish()
        {
            folder.PropertyChanged -= OnFolderChanged;
            // 先让视图模型移除行（同步续体），再清掉本地值，回收的容器不会带着折叠状态
            done.TrySetResult();
            Restore();
        }
        folder.PropertyChanged += OnFolderChanged;
        progress.AnimateTo(0, Motion.MotionTokens.Exit, Finish);
        return done.Task;
    }

    private void OnAddPanelClick(object? sender, RoutedEventArgs e) => AddPanelPopup.IsOpen = !AddPanelPopup.IsOpen;

    private void OnAddPanelChosen(object? sender, RoutedEventArgs e) => AddPanelPopup.IsOpen = false;

    private static KeyModifiers CommandModifier => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    /// <summary>
    /// 全局快捷键（⌘ 在 Windows / Linux 上为 Ctrl）：⌘K 命令面板、⌘N 新对话、⇧⌘N 新建项目、⌘B 切换侧栏。
    /// 命令面板打开时接管 ↑ ↓ ↵ Esc Backspace。
    /// </summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }
        var palette = vm.Palette;
        if (palette.IsOpen)
        {
            switch (e.Key)
            {
                case Key.Up:
                    palette.MoveHighlight(-1);
                    e.Handled = true;
                    return;
                case Key.Down:
                    palette.MoveHighlight(1);
                    e.Handled = true;
                    return;
                case Key.Enter:
                    _ = palette.ExecuteHighlightedAsync();
                    e.Handled = true;
                    return;
                case Key.Escape:
                    palette.Back();
                    e.Handled = true;
                    return;
                case Key.Back when palette.TryBackOnEmptySearch():
                    e.Handled = true;
                    return;
            }
        }
        if (vm.IsSettingsOpen && e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            // 先关闭设置页内的弹层（添加厂商），再关闭设置页
            if (vm.Settings.IsPickingTemplate)
            {
                vm.Settings.CloseTemplatePickerCommand.Execute(null);
            }
            else
            {
                vm.CloseSettingsCommand.Execute(null);
            }
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers == CommandModifier)
        {
            switch (e.Key)
            {
                case Key.K:
                    palette.Toggle();
                    e.Handled = true;
                    break;
                case Key.N:
                    palette.Close();
                    vm.Sidebar.NewSessionCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.B:
                    vm.ToggleSidebarCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.OemComma:
                    palette.Close();
                    _ = vm.OpenSettingsCommand.ExecuteAsync(null);
                    e.Handled = true;
                    break;
            }
        }
        else if (e.KeyModifiers == (CommandModifier | KeyModifiers.Shift) && e.Key == Key.N)
        {
            palette.OpenNewProject();
            e.Handled = true;
        }
        else if (e.KeyModifiers == (CommandModifier | KeyModifiers.Shift) && e.Key == Key.E)
        {
            vm.ToggleExplorerCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnPalettePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CommandPaletteViewModel.IsOpen) && sender is CommandPaletteViewModel { IsOpen: true })
        {
            Dispatcher.UIThread.Post(() => PaletteSearch.Focus(), DispatcherPriority.Input);
        }
        // 进入子页：列表从右侧滑入；返回：从左侧滑入
        if (e.PropertyName == nameof(CommandPaletteViewModel.Breadcrumb) && sender is CommandPaletteViewModel { IsOpen: true } palette)
        {
            TKCode.Desktop.Motion.Presence.PlayEnter(PaletteList,
                palette.HasBreadcrumb ? TKCode.Desktop.Motion.PresenceEffect.SlideLeft : TKCode.Desktop.Motion.PresenceEffect.SlideRight);
        }
    }

    private void OnPaletteBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.Palette.Close();
        }
    }

    /// <summary>侧栏“…”：添加项目文件夹。</summary>
    private async void OnAddProjectClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.Get(TKCodeLangResourceKind.SelectFolderTitle),
            AllowMultiple = false,
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path && DataContext is MainWindowViewModel vm)
        {
            vm.Sidebar.AddProject(path);
        }
    }

    private async void OnCopyMessageClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is AssistantMessageItemViewModel message && Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(message.CopyText);
        }
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
