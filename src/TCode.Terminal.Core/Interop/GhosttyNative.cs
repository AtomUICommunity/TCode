using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: DisableRuntimeMarshalling]

namespace TCode.Terminal.Core.Interop;

/// <summary>
/// libghostty-vt 的原始声明（M0 子集，手写）。M1 起由 tools/GhosttyBindgen 从头文件生成，
/// 见 docs/terminal/02-core-ghostty.md §4–§5。只允许门面层（TerminalCore 等）调用。
/// </summary>
internal static unsafe partial class GhosttyNative
{
    private const string Lib = "ghostty-vt";

    // —— 终端 ——
    [LibraryImport(Lib, EntryPoint = "ghostty_terminal_new")]
    public static partial GhosttyResult TerminalNew(void* allocator, nint* terminal, ushort cols, ushort rows);

    [LibraryImport(Lib, EntryPoint = "ghostty_terminal_free")]
    public static partial void TerminalFree(nint terminal);

    [LibraryImport(Lib, EntryPoint = "ghostty_terminal_reset")]
    public static partial void TerminalReset(nint terminal);

    [LibraryImport(Lib, EntryPoint = "ghostty_terminal_resize")]
    public static partial GhosttyResult TerminalResize(nint terminal, ushort cols, ushort rows, uint cellWidthPx, uint cellHeightPx);

    [LibraryImport(Lib, EntryPoint = "ghostty_terminal_set")]
    public static partial GhosttyResult TerminalSet(nint terminal, GhosttyTerminalOption option, void* value);

    [LibraryImport(Lib, EntryPoint = "ghostty_terminal_get")]
    public static partial GhosttyResult TerminalGet(nint terminal, GhosttyTerminalData data, void* output);

    [LibraryImport(Lib, EntryPoint = "ghostty_terminal_vt_write")]
    public static partial void TerminalVtWrite(nint terminal, byte* data, nuint length);

    // —— render state（M0：逐格读取；M1 起改为 CELLS_RAW 整行读取，02 §8）——
    [LibraryImport(Lib, EntryPoint = "ghostty_render_state_new")]
    public static partial GhosttyResult RenderStateNew(void* allocator, nint* state);

    [LibraryImport(Lib, EntryPoint = "ghostty_render_state_free")]
    public static partial void RenderStateFree(nint state);

    [LibraryImport(Lib, EntryPoint = "ghostty_render_state_update")]
    public static partial GhosttyResult RenderStateUpdate(nint state, nint terminal);

    [LibraryImport(Lib, EntryPoint = "ghostty_render_state_get")]
    public static partial GhosttyResult RenderStateGet(nint state, GhosttyRenderStateData data, void* output);

    [LibraryImport(Lib, EntryPoint = "ghostty_render_state_row_iterator_new")]
    public static partial GhosttyResult RowIteratorNew(void* allocator, nint* iterator);

    [LibraryImport(Lib, EntryPoint = "ghostty_render_state_row_iterator_free")]
    public static partial void RowIteratorFree(nint iterator);

    [LibraryImport(Lib, EntryPoint = "ghostty_render_state_row_iterator_next")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool RowIteratorNext(nint iterator);

    [LibraryImport(Lib, EntryPoint = "ghostty_render_state_row_get")]
    public static partial GhosttyResult RowGet(nint iterator, GhosttyRenderStateRowData data, void* output);

    [LibraryImport(Lib, EntryPoint = "ghostty_render_state_row_cells_new")]
    public static partial GhosttyResult RowCellsNew(void* allocator, nint* cells);

    [LibraryImport(Lib, EntryPoint = "ghostty_render_state_row_cells_free")]
    public static partial void RowCellsFree(nint cells);

    [LibraryImport(Lib, EntryPoint = "ghostty_render_state_row_cells_next")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool RowCellsNext(nint cells);

    [LibraryImport(Lib, EntryPoint = "ghostty_render_state_row_cells_get")]
    public static partial GhosttyResult RowCellsGet(nint cells, GhosttyRenderStateRowCellsData data, void* output);

    [LibraryImport(Lib, EntryPoint = "ghostty_cell_get")]
    public static partial GhosttyResult CellGet(ulong cell, GhosttyCellData data, void* output);

    // —— formatter ——
    [LibraryImport(Lib, EntryPoint = "ghostty_formatter_terminal_new")]
    public static partial GhosttyResult FormatterTerminalNew(void* allocator, nint* formatter, nint terminal, GhosttyFormatterTerminalOptions options);

    [LibraryImport(Lib, EntryPoint = "ghostty_formatter_format_alloc")]
    public static partial GhosttyResult FormatterFormatAlloc(nint formatter, void* allocator, byte** output, nuint* length);

    [LibraryImport(Lib, EntryPoint = "ghostty_formatter_free")]
    public static partial void FormatterFree(nint formatter);

    // —— 快照 ——
    [LibraryImport(Lib, EntryPoint = "ghostty_snapshot_encode_alloc")]
    public static partial GhosttyResult SnapshotEncodeAlloc(nint terminal, void* allocator, byte** output, nuint* length);

    [LibraryImport(Lib, EntryPoint = "ghostty_snapshot_decoder_new_buf")]
    public static partial GhosttyResult SnapshotDecoderNewBuf(void* allocator, nint* decoder, byte* data, nuint length);

    [LibraryImport(Lib, EntryPoint = "ghostty_snapshot_decoder_set")]
    public static partial GhosttyResult SnapshotDecoderSet(nint decoder, GhosttySnapshotDecoderOption option, void* value);

    [LibraryImport(Lib, EntryPoint = "ghostty_snapshot_decoder_decode")]
    public static partial GhosttyResult SnapshotDecoderDecode(nint decoder, nint* terminal);

    [LibraryImport(Lib, EntryPoint = "ghostty_snapshot_decoder_free")]
    public static partial void SnapshotDecoderFree(nint decoder);

    // —— 内存、构建信息、类型清单 ——
    [LibraryImport(Lib, EntryPoint = "ghostty_free")]
    public static partial void Free(void* allocator, byte* pointer, nuint length);

    [LibraryImport(Lib, EntryPoint = "ghostty_build_info")]
    public static partial GhosttyResult BuildInfo(GhosttyBuildInfo data, void* output);

    [LibraryImport(Lib, EntryPoint = "ghostty_type_json")]
    public static partial byte* TypeJson();

    // —— TCode overlay 扩展（native/ghostty/overlays，O3 / O4）——
    [LibraryImport(Lib, EntryPoint = "tcode_ghostty_set_clipboard_read_deferred")]
    public static partial void SetClipboardReadDeferred(nint terminal, [MarshalAs(UnmanagedType.U1)] bool deferred);

    [LibraryImport(Lib, EntryPoint = "tcode_ghostty_clipboard_read_terminator")]
    public static partial byte ClipboardReadTerminator(nint terminal);

    [LibraryImport(Lib, EntryPoint = "tcode_ghostty_modify_other_keys_2")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool ModifyOtherKeys2(nint terminal);

    /// <summary>头文件中的 <c>static inline ghostty_mode_new</c>：低 15 位为模式号，最高位表示 ANSI 模式。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ModeNew(ushort value, bool ansi) => (ushort)((value & 0x7FFF) | (ansi ? 0x8000 : 0));
}
