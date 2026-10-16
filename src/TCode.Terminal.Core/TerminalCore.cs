using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using TCode.Terminal.Core.Interop;

namespace TCode.Terminal.Core;

/// <summary>
/// 一个终端状态机（libghostty-vt）。非线程安全：只能由其所属的 actor 线程调用
/// （docs/terminal/01-architecture.md §6.1、02-core-ghostty.md §6、§14）。
/// M0：只实现写入、resize、模式查询、纯文本导出与快照往返；其余门面 API 在 M1 补齐。
/// </summary>
public sealed unsafe class TerminalCore : IDisposable
{
    private nint _terminal;
    private GCHandle<TerminalCore> _self;
    private readonly ITerminalEffects _effects;
    private bool _inCallback;
    private nint _renderState;
    private nint _rowIterator;
    private nint _rowCells;
    private long _generation;

    private TerminalCore(nint terminal, ITerminalEffects effects)
    {
        _terminal = terminal;
        _effects = effects;
        _self = new GCHandle<TerminalCore>(this);
        InstallCallbacks();
    }

    public static TerminalCore Create(in TerminalCoreOptions options, ITerminalEffects? effects = null)
    {
        GhosttyRuntime.EnsureInitialized();
        nint terminal;
        Check(GhosttyNative.TerminalNew(null, &terminal, checked((ushort)options.Size.Columns), checked((ushort)options.Size.Rows)));
        var core = new TerminalCore(terminal, effects ?? NullTerminalEffects.Instance);
        core.Configure(options);
        return core;
    }

    /// <summary>从 <see cref="EncodeSnapshot"/> 的结果恢复（同一 libghostty 构建之间使用）。</summary>
    public static TerminalCore Restore(ReadOnlySpan<byte> snapshot, in TerminalCoreOptions options, ITerminalEffects? effects = null)
    {
        GhosttyRuntime.EnsureInitialized();
        nint decoder, terminal = 0;
        fixed (byte* data = snapshot)
        {
            Check(GhosttyNative.SnapshotDecoderNewBuf(null, &decoder, data, (nuint)snapshot.Length));
            try
            {
                // 恢复后继续跟踪未完成的输入，保证可以再次编码快照（重连后再重连）
                nuint maxContinuation = (nuint)options.ContinuationMaxBytes;
                bool retain = true;
                Check(GhosttyNative.SnapshotDecoderSet(decoder, GhosttySnapshotDecoderOption.MaxContinuationBytes, &maxContinuation));
                Check(GhosttyNative.SnapshotDecoderSet(decoder, GhosttySnapshotDecoderOption.RetainContinuation, &retain));
                Check(GhosttyNative.SnapshotDecoderDecode(decoder, &terminal));
            }
            finally
            {
                GhosttyNative.SnapshotDecoderFree(decoder);
            }
        }
        // 快照恢复终端状态（含回滚上限等），不恢复宿主回调与只属于本进程的选项，所以重新配置。
        var core = new TerminalCore(terminal, effects ?? NullTerminalEffects.Instance);
        core.Configure(options);
        return core;
    }

    public TerminalSize Size
    {
        get
        {
            ushort cols, rows;
            Check(GhosttyNative.TerminalGet(Handle, GhosttyTerminalData.Cols, &cols));
            Check(GhosttyNative.TerminalGet(Handle, GhosttyTerminalData.Rows, &rows));
            return new TerminalSize(cols, rows, 0, 0);
        }
    }

    public (int Column, int Row) Cursor
    {
        get
        {
            ushort x, y;
            Check(GhosttyNative.TerminalGet(Handle, GhosttyTerminalData.CursorX, &x));
            Check(GhosttyNative.TerminalGet(Handle, GhosttyTerminalData.CursorY, &y));
            return (x, y);
        }
    }

    /// <summary>解析器是否处于 ground 状态（没有未完成的转义序列）。host 只在 ground 时追加 Clear 记录。</summary>
    public bool IsParserGround
    {
        get
        {
            bool ground;
            Check(GhosttyNative.TerminalGet(Handle, GhosttyTerminalData.VtGround, &ground));
            return ground;
        }
    }

    /// <summary>OSC 0/2 设置的标题；没有时为空字符串。</summary>
    public string Title
    {
        get
        {
            GhosttyString title;
            var r = GhosttyNative.TerminalGet(Handle, GhosttyTerminalData.Title, &title);
            return r == GhosttyResult.NoValue || title.Len == 0 ? string.Empty : Decode(title);
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        EnsureNotInCallback();
        fixed (byte* p = data)
        {
            GhosttyNative.TerminalVtWrite(Handle, p, (nuint)data.Length);
        }
    }

    public void Resize(TerminalSize size)
    {
        EnsureNotInCallback();
        Check(GhosttyNative.TerminalResize(Handle, checked((ushort)size.Columns), checked((ushort)size.Rows),
            (uint)size.CellWidth, (uint)size.CellHeight));
    }

    public void Reset()
    {
        EnsureNotInCallback();
        GhosttyNative.TerminalReset(Handle);
    }

    /// <summary>DEC 私有模式（<paramref name="ansi"/> 为 false）或 ANSI 模式的当前值；未知模式返回 false。</summary>
    public bool GetMode(int mode, bool ansi = false)
    {
        var config = new GhosttyTerminalModeConfig { Mode = GhosttyNative.ModeNew(checked((ushort)mode), ansi) };
        var r = GhosttyNative.TerminalGet(Handle, GhosttyTerminalData.Mode, &config);
        return r == GhosttyResult.Success && config.Value;
    }

    /// <summary>xterm modifyOtherKeys 2 是否开启（overlay O4）。</summary>
    public bool ModifyOtherKeys2 => GhosttyNative.ModifyOtherKeys2(Handle);

    /// <summary>当前屏幕（含回滚）的纯文本：软换行合并，去掉行尾空白。</summary>
    public string FormatPlainText()
    {
        EnsureNotInCallback();
        var options = GhosttyFormatterTerminalOptions.Create(GhosttyFormatterFormat.Plain);
        options.Unwrap = true;
        options.Trim = true;
        nint formatter;
        Check(GhosttyNative.FormatterTerminalNew(null, &formatter, Handle, options));
        try
        {
            byte* output;
            nuint length;
            Check(GhosttyNative.FormatterFormatAlloc(formatter, null, &output, &length));
            if (output == null || length == 0)
            {
                return string.Empty;  // 空屏时库返回空指针
            }
            try
            {
                return Encoding.UTF8.GetString(output, checked((int)length));
            }
            finally
            {
                GhosttyNative.Free(null, output, length);
            }
        }
        finally
        {
            GhosttyNative.FormatterFree(formatter);
        }
    }

    /// <summary>
    /// 捕获当前视口（M0：全量逐格读取，未做脏行增量与样式去重；02 §8 的增量算法在 M1 实现）。
    /// 只在 UI 侧核心上使用。
    /// </summary>
    public FrameSnapshot CaptureFrame()
    {
        EnsureNotInCallback();
        if (_renderState == 0)
        {
            nint state, iterator, cells;
            Check(GhosttyNative.RenderStateNew(null, &state));
            Check(GhosttyNative.RowIteratorNew(null, &iterator));
            Check(GhosttyNative.RowCellsNew(null, &cells));
            (_renderState, _rowIterator, _rowCells) = (state, iterator, cells);
        }
        Check(GhosttyNative.RenderStateUpdate(_renderState, Handle));

        ushort cols, rows;
        GhosttyColorRgb fg, bg;
        Check(GhosttyNative.RenderStateGet(_renderState, GhosttyRenderStateData.Cols, &cols));
        Check(GhosttyNative.RenderStateGet(_renderState, GhosttyRenderStateData.Rows, &rows));
        Check(GhosttyNative.RenderStateGet(_renderState, GhosttyRenderStateData.ColorForeground, &fg));
        Check(GhosttyNative.RenderStateGet(_renderState, GhosttyRenderStateData.ColorBackground, &bg));

        var snapshotRows = new RowSnapshot[rows];
        var iteratorHandle = _rowIterator;
        Check(GhosttyNative.RenderStateGet(_renderState, GhosttyRenderStateData.RowIterator, &iteratorHandle));
        var text = stackalloc byte[256];
        var y = 0;
        while (y < rows && GhosttyNative.RowIteratorNext(_rowIterator))
        {
            var cellsHandle = _rowCells;
            Check(GhosttyNative.RowGet(_rowIterator, GhosttyRenderStateRowData.Cells, &cellsHandle));
            var cells = new TerminalCell[cols];
            for (var x = 0; x < cols && GhosttyNative.RowCellsNext(_rowCells); x++)
            {
                ulong raw;
                int wide;
                Check(GhosttyNative.RowCellsGet(_rowCells, GhosttyRenderStateRowCellsData.Raw, &raw));
                Check(GhosttyNative.CellGet(raw, GhosttyCellData.Wide, &wide));
                var buffer = new GhosttyBuffer { Ptr = text, Cap = 256 };
                var r = GhosttyNative.RowCellsGet(_rowCells, GhosttyRenderStateRowCellsData.GraphemesUtf8, &buffer);
                string? value = r == GhosttyResult.Success && buffer.Len > 0 ? Encoding.UTF8.GetString(text, (int)buffer.Len) : null;
                GhosttyColorRgb cfg, cbg;
                TerminalRgb? cellFg = GhosttyNative.RowCellsGet(_rowCells, GhosttyRenderStateRowCellsData.FgColor, &cfg) == GhosttyResult.Success
                    ? new TerminalRgb(cfg.R, cfg.G, cfg.B) : null;
                TerminalRgb? cellBg = GhosttyNative.RowCellsGet(_rowCells, GhosttyRenderStateRowCellsData.BgColor, &cbg) == GhosttyResult.Success
                    ? new TerminalRgb(cbg.R, cbg.G, cbg.B) : null;
                cells[x] = new TerminalCell(value, (CellWidth)wide, cellFg, cellBg);
            }
            snapshotRows[y++] = new RowSnapshot(cells);
        }
        for (; y < rows; y++)
        {
            snapshotRows[y] = new RowSnapshot(new TerminalCell[cols]);
        }

        bool hasCursor, cursorVisible;
        ushort cx = 0, cy = 0;
        int cursorStyle;
        Check(GhosttyNative.RenderStateGet(_renderState, GhosttyRenderStateData.CursorViewportHasValue, &hasCursor));
        Check(GhosttyNative.RenderStateGet(_renderState, GhosttyRenderStateData.CursorVisible, &cursorVisible));
        Check(GhosttyNative.RenderStateGet(_renderState, GhosttyRenderStateData.CursorVisualStyle, &cursorStyle));
        if (hasCursor)
        {
            Check(GhosttyNative.RenderStateGet(_renderState, GhosttyRenderStateData.CursorViewportX, &cx));
            Check(GhosttyNative.RenderStateGet(_renderState, GhosttyRenderStateData.CursorViewportY, &cy));
        }

        return new FrameSnapshot
        {
            Generation = ++_generation,
            Columns = cols,
            Rows = snapshotRows,
            Cursor = new TerminalCursor(cx, cy, hasCursor && cursorVisible, (CursorStyle)cursorStyle),
            Foreground = new TerminalRgb(fg.R, fg.G, fg.B),
            Background = new TerminalRgb(bg.R, bg.G, bg.B),
        };
    }

    /// <summary>完整快照（包括未完成的解析器输入），用于 pty host 重连与持久化（04 §8、§10）。</summary>
    public byte[] EncodeSnapshot()
    {
        EnsureNotInCallback();
        byte* output;
        nuint length;
        Check(GhosttyNative.SnapshotEncodeAlloc(Handle, null, &output, &length));
        try
        {
            return new ReadOnlySpan<byte>(output, checked((int)length)).ToArray();
        }
        finally
        {
            GhosttyNative.Free(null, output, length);
        }
    }

    public void Dispose()
    {
        if (_terminal == 0)
        {
            return;
        }
        // 子对象先于终端释放（02 §4.6）
        if (_renderState != 0)
        {
            GhosttyNative.RowCellsFree(_rowCells);
            GhosttyNative.RowIteratorFree(_rowIterator);
            GhosttyNative.RenderStateFree(_renderState);
            _renderState = 0;
        }
        // 先释放终端，再释放 GCHandle：否则最后一次回调可能拿到已释放的句柄（02 §4.5）。
        GhosttyNative.TerminalFree(_terminal);
        _terminal = 0;
        _self.Dispose();
    }

    private nint Handle
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ObjectDisposedException.ThrowIf(_terminal == 0, this);
            return _terminal;
        }
    }

    private void Configure(in TerminalCoreOptions options)
    {
        // Ghostty 自有的字形协议注册的字形不进快照，开启会导致重连后两份核心不一致（02 §3）。
        bool glyphProtocol = false;
        Check(GhosttyNative.TerminalSet(_terminal, GhosttyTerminalOption.GlyphProtocol, &glyphProtocol));
        // 库默认的回滚字节上限很小，必须显式设置（research/04 §0.8）。
        nuint maxBytes = (nuint)options.ScrollbackMaxBytes;
        Check(GhosttyNative.TerminalSet(_terminal, GhosttyTerminalOption.ScrollbackMaxBytes, &maxBytes));
        if (options.ScrollbackMaxLines is { } lines)
        {
            nuint maxLines = (nuint)lines;
            Check(GhosttyNative.TerminalSet(_terminal, GhosttyTerminalOption.ScrollbackMaxLines, &maxLines));
        }
        if (options.Size.CellWidth > 0 || options.Size.CellHeight > 0)
        {
            Resize(options.Size);
        }
        GhosttyNative.SetClipboardReadDeferred(_terminal, options.DeferClipboardRead);
        // 跟踪未完成的转义序列 / UTF-8，快照因此可以在任意字节位置编码（02 §9）
        nuint continuation = (nuint)options.ContinuationMaxBytes;
        Check(GhosttyNative.TerminalSet(_terminal, GhosttyTerminalOption.ContinuationMaxBytes, &continuation));
    }

    private void InstallCallbacks()
    {
        Check(GhosttyNative.TerminalSet(_terminal, GhosttyTerminalOption.Userdata, (void*)GCHandle<TerminalCore>.ToIntPtr(_self)));
        Check(GhosttyNative.TerminalSet(_terminal, GhosttyTerminalOption.WritePty,
            (delegate* unmanaged[Cdecl]<nint, nint, byte*, nuint, void>)&Callbacks.WritePty));
        Check(GhosttyNative.TerminalSet(_terminal, GhosttyTerminalOption.Bell,
            (delegate* unmanaged[Cdecl]<nint, nint, void>)&Callbacks.Bell));
        Check(GhosttyNative.TerminalSet(_terminal, GhosttyTerminalOption.TitleChanged,
            (delegate* unmanaged[Cdecl]<nint, nint, void>)&Callbacks.TitleChanged));
    }

    private void EnsureNotInCallback()
    {
        if (_inCallback)
        {
            throw new InvalidOperationException("TerminalCore cannot be re-entered from an effect callback.");
        }
    }

    private static string Decode(GhosttyString s) => Encoding.UTF8.GetString(s.Ptr, checked((int)s.Len));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Check(GhosttyResult result, [CallerArgumentExpression(nameof(result))] string expression = "")
    {
        if (result != GhosttyResult.Success)
        {
            Throw(result, expression);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Throw(GhosttyResult result, string expression) => throw result switch
    {
        GhosttyResult.OutOfMemory => new InsufficientMemoryException($"libghostty-vt out of memory: {expression}"),
        _ => new GhosttyException(result, expression),
    };

    /// <summary>原生回调。异常绝不能穿过原生帧，否则进程直接终止（02 §4.5）。</summary>
    private static class Callbacks
    {
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static void WritePty(nint terminal, nint userdata, byte* data, nuint length)
        {
            var core = GCHandle<TerminalCore>.FromIntPtr(userdata).Target;
            core.Invoke(static (c, s) => c._effects.WriteToPty(s), new ReadOnlySpan<byte>(data, checked((int)length)));
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static void Bell(nint terminal, nint userdata)
        {
            var core = GCHandle<TerminalCore>.FromIntPtr(userdata).Target;
            core.Invoke(static (c, _) => c._effects.Bell(), ReadOnlySpan<byte>.Empty);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static void TitleChanged(nint terminal, nint userdata)
        {
            var core = GCHandle<TerminalCore>.FromIntPtr(userdata).Target;
            core.Invoke(static (c, _) => c._effects.TitleChanged(c.TitleNoCheck()), ReadOnlySpan<byte>.Empty);
        }
    }

    private void Invoke(EffectAction action, ReadOnlySpan<byte> data)
    {
        _inCallback = true;
        try
        {
            action(this, data);
        }
        catch (Exception ex)
        {
            CallbackFaults++;
            LastCallbackFault = ex;
        }
        finally
        {
            _inCallback = false;
        }
    }

    private delegate void EffectAction(TerminalCore core, ReadOnlySpan<byte> data);

    // 回调内只允许只读查询（头文件只禁止重入 vt_write）。
    private string TitleNoCheck()
    {
        GhosttyString title;
        var r = GhosttyNative.TerminalGet(_terminal, GhosttyTerminalData.Title, &title);
        return r == GhosttyResult.NoValue || title.Len == 0 ? string.Empty : Decode(title);
    }

    /// <summary>effect 回调中抛出的异常次数（被吞掉并记录，不穿过原生帧）。</summary>
    public int CallbackFaults { get; private set; }

    public Exception? LastCallbackFault { get; private set; }
}
