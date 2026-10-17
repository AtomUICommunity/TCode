using System.Buffers;
using Avalonia.Input;
using MouseButton = TKCode.Terminal.Core.MouseButton;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKCode.Desktop.Localization;
using TKCode.Desktop.Services;
using TKCode.Desktop.Terminal;
using TKCode.Terminal.Avalonia;
using TKCode.Terminal.Core;
using TKCode.Terminal.Protocol;

namespace TKCode.Desktop.ViewModels;

/// <summary>
/// 工作台的终端标签（01 §7、06 §2）：一个会话 = 一个标签。负责会话生命周期（创建、降级重连、退出后重开、断开后重开），
/// 把视图的按键 / 指针事件编码成字节或核心操作，并把会话的帧切回 UI 线程。
/// </summary>
public sealed partial class TerminalTabViewModel : WorkbenchTabViewModel, IAsyncDisposable
{
    private readonly PtyHostService _hosts;
    private readonly string? _cwd;
    private readonly InputEncoder _encoder = new();  // 只在 UI 线程上使用
    private TerminalSession? _session;
    private PtyHostClient? _client;
    private TerminalSize? _size;
    private TerminalSize? _requestedSize;
    private FrameSnapshot? _pendingFrame;
    private int _framePosted;
    private int _disposed;
    private long _starting;
    private string? _title;
    private bool _exited;
    private bool _lost;
    private bool _dark = true;

    // 指针状态（UI 线程）
    private bool _dragging;
    private ViewportPoint _dragStart;
    private ViewportPoint _lastClick;
    private long _lastClickAt;
    private int _clickCount;

    public TerminalTabViewModel(PtyHostService hosts, string? cwd) : base(WorkbenchTabKind.Terminal)
    {
        _hosts = hosts;
        _cwd = cwd;
    }

    /// <summary>窗口标题（来自 OSC 0/2，缺省为"终端"）。</summary>
    public override string Title => _title ?? WorkbenchTexts.Title(Kind);

    public override void RefreshLanguage()
    {
        base.RefreshLanguage();
        OnPropertyChanged(nameof(Status));
    }

    /// <summary>当前帧。由视图绑定到 <c>TerminalView.Snapshot</c>。</summary>
    [ObservableProperty]
    public partial FrameSnapshot? Frame { get; private set; }

    /// <summary>状态提示（启动中、退出、断开、错误），没有时为空。</summary>
    public string? Status { get; private set; }

    public bool CanRestart => _exited || _lost;

    /// <summary>需要用户确认的粘贴内容（含换行等）；确认前不会发送。</summary>
    public string? PendingPaste { get; private set; }

    /// <summary>有需要用户确认的粘贴。（关闭仍在运行的程序改由工作台弹窗确认。）</summary>
    public bool HasPrompt => PendingPaste is not null;

    /// <summary>确认栏的说明文字。</summary>
    public string? PromptMessage => PendingPaste is not null ? Strings.Get(TKCodeLangResourceKind.TerminalPasteConfirm) : null;

    /// <summary>确认按钮的文字。</summary>
    public string? PromptActionText => PendingPaste is not null ? Strings.Get(TKCodeLangResourceKind.TerminalPasteAction) : null;

    // ---- 搜索栏 ----

    /// <summary>搜索栏是否打开。</summary>
    [ObservableProperty]
    public partial bool IsSearchOpen { get; set; }

    /// <summary>搜索词（输入框的内容）。</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>匹配总数（没有搜索词或没有匹配时为 0）。</summary>
    [ObservableProperty]
    public partial long SearchTotal { get; set; }

    /// <summary>当前匹配的序号，从 1 开始；没有选中时为 0。</summary>
    [ObservableProperty]
    public partial long SearchCurrent { get; set; }

    /// <summary>"当前 / 总数"的显示文字。</summary>
    public string SearchCountText => SearchTotal == 0 ? (SearchText.Length == 0 ? string.Empty : "0/0") : $"{SearchCurrent}/{SearchTotal}";

    partial void OnSearchTextChanged(string value) => UpdateSearch(next: false);

    /// <summary>打开搜索栏（⌘F）。已打开时只重新获得焦点。</summary>
    public void OpenSearch() => IsSearchOpen = true;

    /// <summary>关闭搜索栏（Esc）：清除高亮，焦点交还终端。</summary>
    public void CloseSearch()
    {
        IsSearchOpen = false;
        SearchText = string.Empty;
        SearchTotal = 0;
        SearchCurrent = 0;
        _session?.CloseSearch();
    }

    [RelayCommand]
    private void StepSearchNext() => StepSearch(next: true);

    [RelayCommand]
    private void StepSearchPrevious() => StepSearch(next: false);

    [RelayCommand]
    private void CloseSearchBar() => CloseSearch();

    /// <summary>下一个匹配（Enter）/ 上一个匹配（Shift+Enter）。</summary>
    public void StepSearch(bool next)
    {
        if (_session is { } session && SearchText.Length > 0)
        {
            session.StepSearch(next);
            RefreshSearchCount();
        }
    }

    private void UpdateSearch(bool next)
    {
        if (_session is not { } session)
        {
            return;
        }
        session.SetSearch(SearchText, next);
        RefreshSearchCount();
    }

    /// <summary>从工作线程读取匹配数量（结果切回 UI 线程）。</summary>
    private void RefreshSearchCount()
    {
        if (_session is not { } session)
        {
            return;
        }
        _ = session.Invoke(core => core.ActiveSearch is { } search ? (search.TotalMatches, search.SelectedIndex) : (0L, (long?)null))
            .ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        var (total, index) = t.Result;
                        SearchTotal = total;
                        // SelectedIndex 从最新的匹配（最下面）开始计数，0 = 最新。界面的"当前"与之相同，从 1 开始（实测：Next 的第一个为 1/N）
                        SearchCurrent = index is { } i ? i + 1 : 0;
                        OnPropertyChanged(nameof(SearchCountText));
                    });
                }
            }, TaskScheduler.Default);
    }

    /// <summary>标签被关闭（会话已结束并已从 host 移除）。工作台据此移除标签。</summary>
    public event Action? Closed;

    /// <summary>OSC 52 写入剪贴板（由视图提供实现）。</summary>
    public Func<string, Task>? SetClipboardText { get; set; }

    /// <summary>读取剪贴板文本（粘贴用，由视图提供实现）。</summary>
    public Func<Task<string?>>? GetClipboardText { get; set; }

    /// <summary>打开链接（由视图提供实现；只接受 http / https / mailto）。</summary>
    public Func<Uri, Task>? OpenLink { get; set; }

    // ---- 生命周期 ----

    /// <summary>测试用：直接挂上一个已经创建好的客户端与会话（不经过 host 发现）。</summary>
    internal void AttachForTesting(PtyHostClient client, TerminalSession session)
    {
        _client = client;
        _session = session;
    }

    /// <summary>首次布局得到尺寸时启动会话。之后的尺寸变化通过 <see cref="Resize"/> 处理。</summary>
    public void EnsureStarted(TerminalSize size)
    {
        _size ??= size;
        if (_session is null && Interlocked.CompareExchange(ref _starting, 1, 0) == 0)
        {
            _ = StartAsync(size);
        }
    }

    private async Task StartAsync(TerminalSize size)
    {
        SetStatus(TKCodeLangResourceKind.TerminalStarting);
        try
        {
            var client = await _hosts.GetAsync().ConfigureAwait(false);
            _client = client;
            _ = client.Completion.ContinueWith(_ => Dispatcher.UIThread.Post(OnHostLost), TaskScheduler.Default);
            var session = new TerminalSession(OnSessionFrame, OnSessionEvent);
            var create = new SessionCreateParams
            {
                Size = new TerminalSizeDto(size.Columns, size.Rows, size.CellWidth, size.CellHeight),
                Cwd = _cwd,
                Palette = TerminalTheme.For(Volatile.Read(ref _dark)),
                Kind = "user",
                WidthMode = "compat",
                ShellIntegration = "auto",
            };
            await client.CreateSessionAsync(create, session).ConfigureAwait(false);
            _session = session;
            // 创建期间主题可能已经切换：补发一次当前的调色板
            if (TerminalTheme.For(Volatile.Read(ref _dark)) != create.Palette)
            {
                SendColors(session);
            }
            // 创建期间窗口可能已经改变尺寸：创建完成后补发一次（Resize 在会话创建之前不会生效）
            if (_size is { } latest && latest != size)
            {
                _requestedSize = latest;
                _ = session.RequestResizeAsync(latest);
            }
            Dispatcher.UIThread.Post(() =>
            {
                _exited = false;
                _lost = false;
                SetStatus(null);
                OnPropertyChanged(nameof(CanRestart));
            });
        }
        catch (Exception e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _exited = true;
                OnPropertyChanged(nameof(CanRestart));
                SetStatus(TKCodeLangResourceKind.TerminalStartFailedFormat, e.Message);
            });
        }
        finally
        {
            Interlocked.Exchange(ref _starting, 0);
        }
    }

    /// <summary>退出后或断开后，在相同目录重新打开一个会话（Enter 键触发）。</summary>
    public void Restart()
    {
        if (!CanRestart)
        {
            return;
        }
        var old = _session;
        _session = null;
        _exited = false;
        _lost = false;
        if (old is not null)
        {
            _ = old.DisposeAsync().AsTask();
        }
        if (_size is { } size && Interlocked.CompareExchange(ref _starting, 1, 0) == 0)
        {
            _ = StartAsync(size);
        }
    }

    private void OnHostLost()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }
        _lost = true;
        var old = _session;
        _session = null;
        _client = null;
        _ = old?.DisposeAsync().AsTask();  // 连接已断开：会话随 host 一起结束，只释放本地资源
        OnPropertyChanged(nameof(CanRestart));
        SetStatus(TKCodeLangResourceKind.TerminalLost);
    }

    /// <summary>跟随应用的明暗主题（UI 线程，由视图在主题变化时调用）。会话已经创建时，调色板经 host 更新。</summary>
    public void SetDarkTheme(bool dark)
    {
        if (_dark == dark)
        {
            return;
        }
        Volatile.Write(ref _dark, dark);
        if (_session is { } session)
        {
            SendColors(session);
        }
    }

    private void SendColors(TerminalSession session)
    {
        if (session.Handle is not { } handle)
        {
            return;
        }
        _ = handle.SetColorsAsync(TerminalTheme.For(Volatile.Read(ref _dark))).ContinueWith(
            t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);  // 会话或连接已经结束：没有需要更新的调色板
    }

    /// <summary>
    /// 关闭标签（UI 线程）。会话里有程序在运行时不关闭并返回 false，由调用方弹窗确认后以 <paramref name="force"/> 再次调用。
    /// 关闭成功时引发 <see cref="Closed"/>。
    /// </summary>
    public async Task<bool> CloseAsync(bool force = false)
    {
        var session = _session;
        var client = _client;
        if (session is null || client is null || session.SessionId == 0)
        {
            await DisposeAsync();
            Closed?.Invoke();
            return true;
        }
        CloseResult result;
        try
        {
            result = await client.CloseSessionAsync(session.SessionId, force).ConfigureAwait(true);
        }
        catch (Exception e) when (e is IOException or PtyHostException or ObjectDisposedException)
        {
            // host 已经不可用（连接断开、会话不存在）：不能让标签关不掉，只释放本地资源
            await DisposeAsync();
            Closed?.Invoke();
            return true;
        }
        if (!result.Closed)
        {
            return false;
        }
        await DisposeAsync();
        Closed?.Invoke();
        return true;
    }

    [RelayCommand]
    private async Task ConfirmPromptAsync()
    {
        if (PendingPaste is { } paste)
        {
            PendingPaste = null;
            NotifyPrompt();
            await PasteAsync(paste, allowUnsafe: true).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void CancelPrompt()
    {
        PendingPaste = null;
        NotifyPrompt();
    }

    private void NotifyPrompt()
    {
        OnPropertyChanged(nameof(PendingPaste));
        OnPropertyChanged(nameof(HasPrompt));
        OnPropertyChanged(nameof(PromptMessage));
        OnPropertyChanged(nameof(PromptActionText));
    }

    // ---- 会话事件（工作线程 → UI 线程）----

    private void OnSessionFrame(FrameSnapshot frame)
    {
        // 工作线程上合并：UI 线程只取最新的一帧（输出洪流时不会堆积）
        Volatile.Write(ref _pendingFrame, frame);
        if (Interlocked.CompareExchange(ref _framePosted, 1, 0) == 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref _framePosted, 0);
                if (Volatile.Read(ref _disposed) == 0 && Interlocked.Exchange(ref _pendingFrame, null) is { } latest)
                {
                    Display(latest);
                }
            }, DispatcherPriority.Render);
        }
    }

    /// <summary>显示一帧（UI 线程）。会话的帧经此进入视图；测试也可以直接显示一帧。</summary>
    public void Display(FrameSnapshot frame) => Frame = frame;

    /// <summary>显示状态提示（UI 线程）。</summary>
    public void ShowStatus(string? text) => SetStatus(text);

    private void OnSessionEvent(TerminalSessionEvent e)
    {
        switch (e)
        {
            case TerminalTitleChanged t:
                Dispatcher.UIThread.Post(() =>
                {
                    _title = t.Title.Length > 0 ? t.Title : null;
                    OnPropertyChanged(nameof(Title));
                });
                break;
            case TerminalExited x:
                Dispatcher.UIThread.Post(() =>
                {
                    _exited = true;
                    OnPropertyChanged(nameof(CanRestart));
                    if (x.ExitCode is { } code)
                    {
                        SetStatus(TKCodeLangResourceKind.TerminalExitedFormat, code.ToString());
                    }
                    else
                    {
                        SetStatus(TKCodeLangResourceKind.TerminalExited);
                    }
                });
                break;
            case TerminalClipboardWrite c when SetClipboardText is { } set:
                Dispatcher.UIThread.Post(() => _ = set(System.Text.Encoding.UTF8.GetString(c.Data)));
                break;
            case TerminalResyncRequested:
                Dispatcher.UIThread.Post(() => SetStatus(TKCodeLangResourceKind.TerminalResyncing));
                break;
        }
    }

    private void SetStatus(TKCodeLangResourceKind kind, params string[] args)
    {
        Status = args.Length == 0 ? Strings.Get(kind) : Strings.Format(kind, args);
        OnPropertyChanged(nameof(Status));
    }

    private void SetStatus(string? text)
    {
        Status = text;
        OnPropertyChanged(nameof(Status));
    }

    // ---- 视图输入（UI 线程）----

    public void Resize(TerminalSize size)
    {
        _size = size;
        if (_session is { } session && size != _requestedSize)
        {
            _requestedSize = size;
            _ = session.RequestResizeAsync(size);
        }
        else if (_session is null && !CanRestart)
        {
            EnsureStarted(size);
        }
    }

    /// <summary>
    /// 按键。返回 true 表示已消费。快捷键（复制、粘贴、清屏）在这里处理，其余按键编码后发送给程序。
    /// 复制与否只看当前帧里是否有选区（帧与核心同步），所以这里能同步决定。
    /// </summary>
    public bool HandleKey(TerminalKeyEventArgs e)
    {
        var input = e.Input;
        var mac = OperatingSystem.IsMacOS();
        var mods = input.Mods;
        // macOS 用 ⌘（Super），其他平台用 Ctrl+Shift，与系统终端一致（06 §2）
        var shortcut = mac ? mods.HasFlag(KeyMods.Super) : mods.HasFlag(KeyMods.Ctrl) && mods.HasFlag(KeyMods.Shift);
        if (input.Action == KeyAction.Press && shortcut)
        {
            switch (input.Key)
            {
                case TerminalKey.C when Frame?.Selection.Length > 0:
                    _ = CopySelectionAsync();
                    return true;
                case TerminalKey.V:
                    _ = PasteFromClipboardAsync();
                    return true;
                case TerminalKey.K when mac:
                    ClearScreen();
                    return true;
                case TerminalKey.F:
                    OpenSearch();
                    return true;
            }
        }

        // 搜索栏打开时：Esc 关闭，Enter / Shift+Enter 切换匹配（焦点仍在终端；输入字符由搜索框接收）
        if (IsSearchOpen && input.Action == KeyAction.Press)
        {
            switch (input.Key)
            {
                case TerminalKey.Escape:
                    CloseSearch();
                    return true;
                case TerminalKey.Enter or TerminalKey.NumpadEnter:
                    StepSearch(next: !mods.HasFlag(KeyMods.Shift));
                    return true;
            }
        }

        if (CanRestart)
        {
            if (input.Action == KeyAction.Press && input.Key is (TerminalKey.Enter or TerminalKey.NumpadEnter))
            {
                Restart();
                return true;
            }
            return false;
        }
        if (_session is not { } session)
        {
            return false;
        }
        if (input.Action == KeyAction.Press)
        {
            session.Post(core => core.ScrollViewport(ViewportScroll.Bottom));
        }
        _encoder.Apply(session.Modes);
        Span<byte> buffer = stackalloc byte[128];
        var n = _encoder.EncodeKey(input, buffer);
        if (n > 0)
        {
            session.SendBytes(buffer[..n]);
        }
        return n > 0;
    }

    /// <summary>可打印文本（已应用键盘布局）。</summary>
    public void HandleText(string text)
    {
        if (_session is not { } session || text.Length == 0)
        {
            return;
        }
        session.Post(core => core.ScrollViewport(ViewportScroll.Bottom));
        session.SendBytes(System.Text.Encoding.UTF8.GetBytes(text));
    }

    /// <summary>复制当前选区（取文本在工作线程上完成）。</summary>
    public async Task CopySelectionAsync()
    {
        if (_session is not { } session || SetClipboardText is not { } set)
        {
            return;
        }
        var text = await session.Invoke(core => core.GetSelectionText()).ConfigureAwait(true);
        if (text is { Length: > 0 })
        {
            await set(text).ConfigureAwait(true);
        }
    }

    public async Task PasteFromClipboardAsync()
    {
        if (GetClipboardText is not { } get || await get().ConfigureAwait(true) is not { Length: > 0 } text)
        {
            return;
        }
        await PasteAsync(text).ConfigureAwait(true);
    }

    /// <summary>
    /// 粘贴：按 bracketed paste 编码。包含换行等可能注入命令的内容先请求确认（<see cref="PendingPaste"/>），确认后以 allowUnsafe 再次调用。
    /// </summary>
    public async Task PasteAsync(string text, bool allowUnsafe = false)
    {
        if (_session is not { } session)
        {
            return;
        }
        var (outcome, bytes) = await session.Invoke(core =>
        {
            var buffer = new ArrayBufferWriter<byte>();
            var result = core.EncodePaste(text, allowUnsafe, buffer);
            return (result, buffer.WrittenSpan.ToArray());
        }).ConfigureAwait(true);
        if (outcome == PasteOutcome.RequiresConfirmation)
        {
            PendingPaste = text;
            NotifyPrompt();
            return;
        }
        if (outcome == PasteOutcome.Written)
        {
            session.SendBytes(bytes);
        }
    }

    public void ClearScreen()
    {
        if (_session is { } session)
        {
            session.Post(core => { if (core.IsParserGround) core.Clear(ClearTarget.All); });
        }
    }

    // ---- 指针 ----

    public void HandlePointer(TerminalPointerEventArgs e)
    {
        if (_session is not { } session)
        {
            return;
        }
        var modes = session.Modes;
        var mouseTracking = modes.MouseTracking != MouseTracking.None && !e.Mouse.Mods.HasFlag(KeyMods.Shift);
        if (mouseTracking && e.Kind != TerminalPointerKind.Wheel)
        {
            if (e.Kind == TerminalPointerKind.Press)
            {
                _dragging = true;
            }
            else if (e.Kind == TerminalPointerKind.Release)
            {
                _dragging = false;
            }
            SendMouse(session, e);
            return;
        }
        switch (e.Kind)
        {
            case TerminalPointerKind.Wheel:
                HandleWheel(session, e, modes);
                break;
            case TerminalPointerKind.Press when e.Mouse.Button == MouseButton.Left:
                if (e.Mouse.Mods.HasFlag(KeyMods.Ctrl) || e.Mouse.Mods.HasFlag(KeyMods.Super))
                {
                    _ = OpenLinkAt(session, e.Cell);
                    return;
                }
                var now = Environment.TickCount64;
                _clickCount = now - _lastClickAt < 500 && _lastClick == e.Cell ? Math.Min(_clickCount + 1, 3) : 1;
                (_lastClick, _lastClickAt) = (e.Cell, now);
                _dragStart = e.Cell;
                _dragging = true;
                session.Post(core =>
                {
                    switch (_clickCount)
                    {
                        case 2:
                            core.Select(SelectionRequest.Word(e.Cell));
                            break;
                        case 3:
                            core.Select(SelectionRequest.Line(e.Cell));
                            break;
                        default:
                            core.ClearSelection();
                            break;
                    }
                });
                break;
            case TerminalPointerKind.Move when _dragging && _clickCount == 1:
                var start = _dragStart;
                var end = e.Cell;
                session.Post(core => core.Select(SelectionRequest.Range(start, end)));
                break;
            case TerminalPointerKind.Release when e.Mouse.Button == MouseButton.Left:
                _dragging = false;
                break;
        }
    }

    /// <summary>鼠标上报（程序开启了鼠标模式时）：编码后直接发送。</summary>
    private void SendMouse(TerminalSession session, TerminalPointerEventArgs e)
    {
        _encoder.Apply(session.Modes);
        Span<byte> buffer = stackalloc byte[64];
        var n = _encoder.EncodeMouse(e.Mouse, e.Geometry, anyButtonPressed: _dragging, buffer);
        if (n > 0)
        {
            session.SendBytes(buffer[..n]);
        }
    }

    private void HandleWheel(TerminalSession session, TerminalPointerEventArgs e, InputModes modes)
    {
        var notches = (int)Math.Round(Math.Abs(e.WheelDelta));
        if (notches == 0)
        {
            notches = 1;
        }
        if (modes.MouseTracking != MouseTracking.None)
        {
            // 程序开启了鼠标上报：滚轮作为按钮 4 / 5 报告（交给程序）
            _encoder.Apply(modes);
            Span<byte> buffer = stackalloc byte[64];
            for (var i = 0; i < notches; i++)
            {
                var n = _encoder.EncodeMouse(e.Mouse, e.Geometry, anyButtonPressed: false, buffer);
                if (n > 0)
                {
                    session.SendBytes(buffer[..n]);
                }
            }
            return;
        }
        if (modes.AlternateScreen && modes.AlternateScroll)
        {
            // 备用屏且未开鼠标：滚轮转成方向键（xterm 的 alternate scroll，1007）
            var key = e.WheelDelta > 0 ? TerminalKey.ArrowUp : TerminalKey.ArrowDown;
            _encoder.Apply(modes);
            Span<byte> buffer = stackalloc byte[32];
            for (var i = 0; i < notches * 3; i++)
            {
                var n = _encoder.EncodeKey(new KeyInput(key, KeyAction.Press, KeyMods.None), buffer);
                if (n > 0)
                {
                    session.SendBytes(buffer[..n]);
                }
            }
            return;
        }
        var lines = (long)-Math.Sign(e.WheelDelta) * notches * 3;
        session.Post(core => core.ScrollViewport(ViewportScroll.Lines(lines)));
    }

    private async Task OpenLinkAt(TerminalSession session, ViewportPoint cell)
    {
        var uri = await session.Invoke(core => core.HyperlinkAt(cell)).ConfigureAwait(true);
        if (uri is null || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https" or "mailto"))
        {
            return;  // 只打开安全的协议；其他地址不执行
        }
        if (OpenLink is { } open)
        {
            await open(parsed).ConfigureAwait(true);
        }
    }

    // ---- 释放 ----

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        var session = _session;
        _session = null;
        if (session is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        _encoder.Dispose();
    }
}
