# 04 · 终端组件调研：缓冲区数据结构、渲染与性能

> 调研日期：2026-10-16　｜　范围：缓冲区/回滚、渲染管线、字体字形、线程模型、输入/IME/可访问性、性能目标与基准计划
> 结论适用于两种终端核心：**纯 C# 核心**，或 **libghostty-vt 经 FFI 提供屏幕状态**。

## 读前说明

**来源标注**

- **[已核实]**：在下表所列 commit 的源码或仓库文档中亲眼读到。
- **[推断]**：由源码推算、由架构推论，或属于本文提出的设计与目标。
- 引用格式为 `仓库/路径:行号 @ commit`。

**源码版本**

源码均为浅克隆，只读，未构建也未运行。克隆位于 scratchpad `repos/render/`。

| 仓库 | commit / 版本 | 日期 |
|---|---|---|
| ghostty-org/ghostty | `b3dc3d7` | 2026-10-15 |
| alacritty/alacritty | `5421470` | 2026-10-16 |
| alacritty/vte | `abeae76`（crate 0.15.0，即 Alacritty 依赖的版本） | 2026-02-28 |
| wezterm/wezterm | `e2a98e1` | 2026-10-10 |
| microsoft/terminal | `44b440b` | 2026-10-13 |
| xtermjs/xterm.js | `991857a` | 2026-10-13 |
| dnkl/foot | `cb27717` | 2026-09-18 |
| kovidgoyal/kitty | `f426c08` | 2026-10-16 |
| contour-terminal/contour | `8f3a4a4` | 2026-10-16 |
| AvaloniaUI/Avalonia | tag `12.1.3`（`8eeda4f`） | 2026-09-22 |
| TKCode | `dfd3657` | — |
| StreamMark | `074f7ff` | 2026-10-16 |

---

## 0. 结论摘要

### 0.1 缓冲区：8 字节 cell + 按页管理

一流终端的 cell 大小如下：

| 终端 | 每 cell 字节数 | 说明 |
|---|---|---|
| Ghostty | 8 | `packed struct(u64)`，样式按页去重 |
| xterm.js | 12 | — |
| foot | 12 | — |
| Windows Terminal | 约 5.2 | UTF-16 加列偏移表，属性按行做 RLE |
| Alacritty | 24 | — |
| WezTerm | 24 | 回滚区会压缩成 "UTF-8 文本 + 属性游程" |
| kitty | 32 | CPU 12 + GPU 20 |

推荐纯 C# 核心照搬 Ghostty 的布局：

- 8 字节 cell；
- 页内 style 表，u16 id，带引用计数；
- grapheme 放在旁路存储；
- 行环形索引，滚动为 O(1)；
- 回滚上限按页裁剪，冷页压实。

在 120 列下，每 1 万行回滚约 9.7 MB；压实后约 1–3 MB。[推断]

### 0.2 渲染：推荐在 Avalonia 渲染线程上绘制

推荐方案（Avalonia 12.1.3）：

- 用 `CompositionCustomVisual` + `CompositionCustomVisualHandler` 在渲染线程绘制。
- 用 `ImmediateDrawingContext.DrawGlyphRun` 绘制文字。
- 用 `ISkiaSharpApiLeaseFeature` 绘制背景和程序化字形（sprite）。
- UI 线程完全不参与每帧绘制，只负责输入、IME 和布局。

已核实的 API 支撑：

- `CompositionCustomVisualHandler` 提供 `OnRender`、`Invalidate(Rect)` 和 `RegisterForNextAnimationFrameUpdate`。
- 12.1.3 中实现 `IPlatformRenderInterface` 的只有 Skia 和 Headless 两种，所以 Skia lease 在 6 个桌面 RID 上都可用。
- Headless 测试只要 `UseSkia()` + `UseHeadlessDrawing=false`，lease 同样可用。TKCode 的测试已经是这样配置的。

### 0.3 线程：每个终端一读线程加一核心线程（actor 模型），渲染线程永不等锁

- **读线程**：从 PTY 读入，使用 4 块 × 64 KiB 的有界缓冲池，池满即形成背压。
- **核心线程**：独占终端状态，负责解析、执行命令，并发布不可变的"帧快照"。
- **渲染线程**：每个 vsync 原子地取最新快照，从不等待终端锁。

这样做的原因：Avalonia 的渲染线程由整个窗口共用，一旦阻塞，整个应用都会掉帧。参考了 Ghostty 的 RenderState 两阶段更新和 contour 的 try-lock 双缓冲。[推断：设计]

### 0.4 解析快路径：.NET 有现成积木，但缺 UTF-8 → UTF-32 的批量 API

可直接使用的 API [已核实]：

- `IndexOfAnyExceptInRange(0x20, 0x7E)`
- `SearchValues<byte>`
- `Ascii.IsValid`
- `Utf8.ToUtf16` / `Utf8.IsValid`
- `Vector128/256/512.Widen`

缺口：.NET 10 没有 UTF-8 → UTF-32 的批量 API [已核实]。ASCII 部分需要自己用向量做 widen；非 ASCII 部分退回 `Rune.DecodeFromUtf8`。

各家的做法：

- Ghostty 用 Highway 加 simdutf，实现"找 ESC + 解码"一体化。
- vte 只用 `memchr(ESC)` 加 `str::from_utf8`。
- WT 用 SSE2/NEON 找控制字符。

### 0.5 文字：网格对齐，程序化绘制图形字符

- x 坐标强制对齐网格。cell 宽高四舍五入到整数设备像素。
- 下划线粗细取 ceil，且不小于 1。
- 以下区段一律程序化绘制，保证像素对齐：
  - box drawing、block、braille；
  - powerline 中的几何类字符；
  - legacy computing 和 octant；
  - 各种下划线样式与光标。
- 连字：按同样式 run 用 HarfBuzz 做 shaping（Avalonia `TextShaper` + `FontFeatures`）。在光标处断开 run。纯 ASCII 且关闭连字时走 cmap 快路径，不做 shaping。

### 0.6 输入与可访问性

**输入**

- 用 `TextInputMethodClient` 承担 IME：`SupportsPreedit=true`，预编辑文字自己画在光标处，`CursorRectangle` 用于定位候选框。
- `KeyDown` 负责编码特殊键和 Ctrl/Alt 组合，`TextInput` 负责可打印文本。
- Win32 上有一个重要细节：KeyDown 设了 `Handled` 之后，随后的 `WM_CHAR` 会被吞掉 [已核实]。

**可访问性**

- Avalonia 12.1.3 没有公开的 `ITextProvider`（UIA TextPattern）[已核实]。
- 第一版用两样东西替代：
  - 隐藏的 LiveSetting 播报元素（Win32 上 Name 变化会触发 `LiveRegionChanged`，[已核实]）；
  - 一个"可访问缓冲视图"，即只读 TextBox，参考 VS Code 的 Accessible View。

### 0.7 数字目标 [推断：目标值]

| 项目 | 目标 |
|---|---|
| 核心吞吐（关闭渲染） | ASCII ≥ 300 MB/s，dense SGR ≥ 100 MB/s |
| 开启渲染后 | 不低于关闭渲染时的 80% |
| 帧预算（120 Hz） | 渲染线程绘制 p99 ≤ 2 ms；UI 线程每帧 ≤ 0.1 ms |
| 快照发布 | ≤ 50 µs |
| 回滚内存 | 每 1 万行 × 120 列 ≤ 10 MB，压实后 ≤ 3 MB |
| 输入延迟 | 按键到 Present 的自身附加时间 p50 ≤ 1 ms，另加等待 vsync；端到端（Typometer）不超过同机 Ghostty/Alacritty + 2 ms |

### 0.8 FFI 路线（libghostty-vt）要点 [已核实]

- C API 已经相当完整：Terminal、RenderState（含脏行迭代、整行 `CELLS_RAW` 批量视图）、key/mouse 编码器、选区、搜索、压缩。
- 但 API 明确标为 "work-in-progress… not yet stable"。
- 库不带线程、PTY 和渲染，锁、mode 2026 超时和压缩调度都要嵌入方自己做。
- 默认 `max_scrollback_bytes = 10_000`，几乎等于没有回滚，必须显式设置。

---

## 1. 一流终端的缓冲区与渲染：对比总表

### 1.1 Cell 与行存储

| 终端 | cell 结构（字节） | 颜色存储 | grapheme / 组合字符 | 行与滚动 | 来源 |
|---|---|---|---|---|---|
| **Ghostty** | `packed struct(u64)` **8 B**：content_tag(2)，content(24：u21 码点、调色板或 RGB)，style_id(u16)，wide(2)，protected，hyperlink，semantic(2) | cell 里只存 style_id，Style 在页内 `RefCountedSet` 去重；"只有背景色"的 cell 直接把 RGB 内联进 content | `.codepoint_grapheme`，后续码点放在页内 `BitmapAllocator`（4×u21 一块）+ `grapheme_map[cell offset]`，每 cell 上限 64 个码点 | Row 也是 `packed struct(u64)`，包含 wrap/styled/dirty 等标志；PageList 是页的双向链表，页约 400 KB，全部用 offset 指针 | `ghostty/src/terminal/page.zig:2008-2235, 90-99, 1890 @ b3dc3d7` |
| **Alacritty** | `Cell{c: char, fg, bg: Color, flags: u16, extra: Option<Arc<CellExtra>>}`，≤**24 B**（有测试断言） | 内联 `Color`（Named/Spec RGB/Indexed） | 没有 grapheme 概念，零宽字符追加到 `extra.zerowidth`，最多 9 个 | `Storage{inner: Vec<Row>, zero, len}` 旋转 `zero` 实现 O(1) 滚动；`Row{inner: Vec<Cell>, occ}` | `alacritty_terminal/src/term/cell.rs:141-147,308-314`；`grid/storage.rs:33-53,180-185 @ 5421470` |
| **WezTerm** | `Cell{text: TeenyString(8), attrs: CellAttributes(16)}` = **24 B** | 只有调色板色走 `SmallColor`；truecolor、下划线色、超链接放进 `Box<FatAttributes>` | `TeenyString` 内联整个 grapheme（<8 字节） | `VecDeque<Line>`；`CellStorage::{V(Vec<Cell>), C(ClusteredLine)}`，回滚行压成"UTF-8 文本 + 属性游程" | `wezterm-cell/src/lib.rs:719-729,1029-1036`；`wezterm-surface/src/line/clusterline.rs:18-42 @ e2a98e1` |
| **Windows Terminal** | 每行一段 UTF-16 `_chars` 加 `_charOffsets[cols+1]`，约 **4 B/cell**；属性用 `til::small_rle<TextAttribute(18B)>` 按行 RLE | `TextColor` 4 B（Default/Index16/Index256/Rgb） | 字素按 UTF-16 存放，偏移表最高位 `0x8000` 标记宽字符的尾半格 | 一次 `VirtualAlloc(MEM_RESERVE)` 预留，按需 commit（每次预读 128 行）；`_firstRow` 环形，O(1) 滚动 | `terminal/src/buffer/out/Row.hpp:241-320`；`TextAttribute.cpp:10-15`；`textBuffer.cpp:92-143,728-746 @ 44b440b` |
| **xterm.js** | `Uint32Array` 每格 3 个 u32 = **12 B**：content(width 2 / combined 1 / codepoint 21)，fg(flags 8 + RGB 24)，bg(flags 8 + RGB 24) | 内联 | `_combined[index]: string` 稀疏表；扩展属性（下划线样式/颜色、URL）放在 `_extendedAttrs` 稀疏表 | `CircularList`，满了就 `recycle()` 复用最旧的行 | `xterm.js/src/common/buffer/BufferLine.ts:14-34,71-74`；`Constants.ts:36-157 @ 991857a` |
| **foot** | `struct cell{char32_t wc; struct attributes attrs;}` = **12 B**（`static_assert`）；attrs 8 B 位域，含 `clean` 脏位 | fg/bg 各 24 bit，加 2 bit `color_src`（调色板只存索引） | `wc ∈ [0x200000, …]` 是 composed 二叉树的 key；`CELL_SPACER+n` 表示宽字符的占位格 | 行指针环形数组，长度为 2 的幂，用掩码取下标；行懒分配；滚动只改 `offset` | `foot/terminal.h:36-72,148-160,220-247`；`grid.h:34-61 @ cb27717` |
| **kitty** | `CPUCell` 12 B + `GPUCell` 20 B = **32 B**（两个数组，SoA 的一半） | GPUCell 里放 fg/bg/decoration_fg | `TextCache` 索引（哈希表 + arena，带 GC），每 cell ≤ 24 码点 | `line_map` 间接索引；历史按 2048 行一个 mmap segment | `kitty/kitty/line.h:12-100`；`history.c:17-44 @ f426c08` |
| **contour** | `LineSoA`：码点、宽度、SGR(16B)、超链接等分层 SoA，约 **29 B/cell** [推断] | SGR 内联 | 每行一个 `clusterPool` | 空行不展开（O(1)），用 `LineShape::Uniform` 走整行快路径 | `contour/src/vtbackend/grid/LineSoA.hpp:43-180 @ 8f3a4a4` |

### 1.2 脏跟踪、渲染缓存与图集

| 终端 | 脏跟踪 | 每帧工作量 | 字形缓存与图集 | 来源 |
|---|---|---|---|---|
| **Ghostty** | 三层：`Row.dirty`（可假阳性、不可假阴性）、`Page.dirty`、全局 `Terminal.Dirty`/`Screen.Dirty`。由 `RenderState.beginUpdate` 统一消费，用 8 行一组的 `@Vector` 掩码扫描 | 只重建脏行的 `CellText`；bg 是 `[rows*cols]u8x4` 加一个全屏三角形 | skyline 打包；一张灰度 atlas 加一张 BGRA atlas，起始 512，满了翻倍；键 `GlyphKey packed u64`；shaping 缓存以 run hash（码点 + 相对 cluster + font index）为键 | `ghostty/src/terminal/render.zig:552-597,766-833`；`src/font/Atlas.zig`；`src/font/shaper/Cache.zig:31-44` |
| **Alacritty** | 每行 `[left,right]` 区间，再加"上一帧/当前帧光标点"；所有滚动都直接标全脏 | **每帧全量**重画，damage 只交给 Wayland 合成器 | 行式 atlas 1024²，满了就新建一张；`GlyphKey{font_key,size,char}`；不做 shaping 和连字 | `term/mod.rs:137-226,458-486`；`alacritty/src/display/mod.rs:607-623,838,878`；`renderer/text/atlas.rs:12` |
| **WezTerm** | 每行一个 `SequenceNo`；普通滚动不改 seqno（用 `StableRowIndex`） | 每帧全量重建 quad，但有 5 层缓存（seqno→shape hash、line quad、line element、cluster shape、glyph） | guillotiere 分配器，从 128 起翻倍；回退字体在后台线程查找 | `wezterm-gui/src/termwindow/render/pane.rs:371-554`；`mod.rs:430-436` |
| **Windows Terminal** | `invalidatedRows` 加每行 `dirtyTop/Bottom`；滚动交给 `Present1` 的 scroll rect | 每帧对所有行重新生成 quad（很便宜），只有失效行重新 shaping | `stb_rect_pack`，满了整张清空（无 LRU）；颜色不进缓存键；背景用"每格 1 像素"的颜色位图加全屏 quad | `src/renderer/atlas/BackendD3D.cpp:1119,1706-1721`；`AtlasEngine.cpp:58-263` |
| **xterm.js** | `DirtyRowTracker` 维护 start/end，再由 `RenderDebouncer` 合并到 rAF | WebGL：逐格比较 4×u32 model，不同才更新；每帧整屏绘制 | 多页 atlas（512 起，单页上限 4096），4 页合并成 1；**颜色进缓存键**，因为背景色一起光栅化 | `addons/addon-webgl/src/WebglRenderer.ts:354-600`；`TextureAtlas.ts:60-61,155-277` |
| **foot** | 每 cell 一个 `clean` 位、每行一个 `dirty`、滚动损伤列表（会合并） | **CPU（pixman）**按 cell 跳过干净格；多线程按行分发；buffer age 复用上一帧 | 用 fcft 光栅化 A8 mask；box drawing 自绘并缓存 | `foot/render.c:696-705,3325-3719,2184-2300` |
| **kitty** | `LineAttrs.has_dirty_text` | 只要有变化就整屏 `rows*cols*20B` 重写 GPU 缓冲，只对脏行重新 shaping | `GL_TEXTURE_2D_ARRAY` sprite map，每个字形单独 `glTexSubImage3D` | `kitty/shaders.c:937-960,1229-1253` |

### 1.3 IO、线程与锁

| 终端 | PTY 读取 | 解析在哪里、是否持锁 | 渲染侧持锁时间 | mode 2026 超时 | 来源 |
|---|---|---|---|---|---|
| **Ghostty** | POSIX 两段：`io-gather` 读线程配 4 块 × 64 KiB 环形缓冲（背压），`io-reader` 解析线程；macOS 每次读约 1 KiB，所以要 gather | reader 线程**持渲染器 mutex**直接解析，每批 64 KiB，批次边界调用 `yieldToDemand()`（最多让 1 ms） | 只执行 `beginUpdate`（复制脏行原始 cell），样式反规范化在锁外的 `endUpdate` | 1000 ms | `src/termio/Exec.zig:1264-1352`；`Termio.zig:697`；`renderer/State.zig:35-105`；`generic.zig:1400-1558` |
| **Alacritty** | 单线程，1 MiB 栈缓冲；先 `try_lock_unfair`，拿不到继续读，攒满再阻塞拿锁 | 持锁解析，最多 64 KiB 后放锁；`FairMutex` 用两把锁实现 lease 公平 | UI 线程持锁收集 `RenderableCell` 到 Vec 后立刻 drop 锁 | 150 ms，缓冲上限 2 MiB（vte） | `alacritty_terminal/src/event_loop.rs:24-27,104-171`；`sync.rs:11-48`；`vte/src/ansi.rs:36-39 @ abeae76` |
| **WezTerm** | 读线程（1 MiB）经 socketpair 交给解析线程 | 解析**在锁外**产出 `Vec<Action>`，3 ms 合并窗口，只在应用 action 时持锁 | GUI **在锁内**渲染（缓存未命中时 shaping 也在锁内） | 无超时 | `mux/src/lib.rs:118,142-247,283-345` |
| **Windows Terminal** | ConPTY 重叠 IO，128 KiB 缓冲 | 持写锁整块解析；锁是**公平的 ticket lock** | 锁内 shaping（DirectWrite），锁外 Present | 等待最多 100 ms | `src/renderer/base/renderer.cpp:402-468,574-610`；`ConptyConnection.cpp:737-843` |
| **xterm.js** | （浏览器）单线程 | `WriteBuffer` 按 12 ms 时间片解析，然后 `setTimeout` 让出 | rAF 合并渲染 | 1000 ms | `src/common/input/WriteBuffer.ts:11-34,219-315`；`RenderService.ts:21-23` |
| **foot** | 单线程 epoll，24 KiB × 最多 10 次读 | 主线程解析；有两个延迟渲染定时器（lower 0.5 ms，upper 8.33 ms） | 多个 worker 线程按行渲染 | 1 s | `foot/terminal.c:243-366,3834-3862`；`config.c:3652-3653` |
| **kitty** | I/O 线程直接读进 parser 的 1 MiB 缓冲；macOS 会 gather 到 64 KiB | 主线程解析；`input_delay` 3 ms，回显（<1 KiB）立即处理 | `repaint_delay` 10 ms | 2000 ms（渲染快照） | `kitty/child-monitor.c:1748-1797,2061-2100`；`vt-parser.c:18-27,1593-1604` |
| **contour** | — | 终端线程构建 `RenderBuffer` | 双缓冲交换时写端只做 `try_lock`，拿不到就放弃 | — | `src/vtbackend/render/RenderBuffer.cpp:59-86` |

---

## 2. 各终端详述

### 2.1 Ghostty（最重要的参照，也是 libghostty-vt 的来源）

#### 2.1.1 Page 内存布局

**分配** [已核实]

- POSIX 上用 `mmap(PRIVATE|ANONYMOUS)`，Windows 上用 `VirtualAlloc`。依赖 OS 提供清零页，所以全零即为空 cell，也不需要初始化 cell（`page.zig:34-81, 263, 1742-1756`）。
- 页池是 `UntouchedPool`，闲置的池项只占虚拟内存。

**标准容量**（`page.zig:1890`）

```zig
pub const std_capacity: Capacity = .{
    .cols = 215, .rows = 215, .styles = 128,
    .grapheme_bytes = if (builtin.is_test) 512 else 8192,
};
```

- 页大约 400 KB（`include/ghostty/vt/terminal.h:2217-2219` 的注释原文 "about 400KB"）。
- 列数不同时，`Capacity.adjust` 在总字节不变的前提下用行数换列数。按此推算：80 列约 575 行/页，120 列约 390 行/页 [推断]。

**页内引用全部是 `Offset(T)`**（u32 相对偏移，`size.zig:8-70`）。整页可以 memcpy、压缩、序列化，不需要修指针。

#### 2.1.2 Row 与 Cell [已核实]

```zig
// ghostty/src/terminal/page.zig:2008-2131, 2137-2235 @ b3dc3d7
pub const Row = packed struct(u64) {
    cells: Offset(Cell),               // u32
    wrap: bool, wrap_continuation: bool, grapheme: bool,
    styled: bool,                      // 可假阳性；跳过 unstyled 行让 erase 快约 4x
    hyperlink: bool,
    semantic_prompt: SemanticPrompt,   // u2
    kitty_virtual_placeholder: bool,
    dirty: bool,
    _padding: u23 = 0,
};
pub const Cell = packed struct(u64) {
    content_tag: ContentTag,           // codepoint / codepoint_grapheme / bg_color_palette / bg_color_rgb
    content: packed union { codepoint: {data: u21, _pad: u3}, color_palette: {...}, color_rgb: RGB }, // 24 bit
    style_id: StyleId,                 // u16, 0 = 默认样式
    wide: Wide,                        // narrow / wide / spacer_tail / spacer_head
    protected: bool, hyperlink: bool,
    semantic_content: SemanticContent, // u2
    _padding: u16 = 0,
};
```

`page.Mask(T, fields, n)` 把多个 cell 的底层 u64 放进 `@Vector` 做批量掩码比较（`page.zig:2467`）。用到它的地方有三处：

- print 快路径判断"目标格是否简单"；
- RenderState 扫描脏行；
- 检测样式 run。

**对 C# 的启示** [推断]：用 `ulong` 做 cell，配合 `Vector256<ulong>`，可以一一对应地实现。

#### 2.1.3 样式、grapheme 与超链接 [已核实]

- **Style 结构**：fg、bg、underline_color 三种颜色（`union{none, palette:u8, rgb}`），加 `flags: packed u16`（bold/italic/faint/blink/inverse/invisible/strikethrough/overline，以及 3 bit 下划线）。哈希时打包成 `PackedStyle = packed struct(u128)` 并断言 16 字节（`style.zig:21-60,604,672-678`）。
- **StyleSet**：每页一个 `RefCountedSet`，开放寻址加 Robin Hood，load factor 0.8125。引用计数归零的项会延迟删除，在此之前还能"复活"（`ref_counted_set.zig:19-23,52-131`）。标准页大约能放 103 种样式 [推断]。
- **容量溢出**：页内样式或 grapheme 表写满时，`increaseCapacity` 新建一页，把该维度翻倍后整页 clone 过去；到达上限后 split（`PageList.zig:4157-4219`；`Screen.zig:2527-2646`）。

**为什么要按页存样式** [推断]：样式 id 只需 u16 且在页内有效。页被裁剪时它的样式随之释放，不会出现"全局样式表被 truecolor 刷爆"的问题。这一点对纯 C# 核心同样关键。

#### 2.1.4 回滚与压缩 [已核实]

**上限按字节计**（`src/config/Config.zig:1394,1412,1436`）：

- `scrollback-limit-bytes` 默认 50,000,000；
- `scrollback-limit-lines` 默认无限；
- `scrollback-compression` 默认 true。

按 400 KB 一页推算，50 MB 约 125 页，80 列时约 7.2 万行 [推断]。

**`grow()` 的两条路径**（`PageList.zig:4003-4130`）：

- 快路径：末页还有空余行就直接 `rows += 1`。
- 超限时：把首页弹出，`memset(0)` 后作为新的末页复用，整个过程零分配。

**压缩**（`src/terminal/compress*.zig`，`mem.zig:56-160`）：

- 算法是 LZ4 块压缩。
- 压缩后原映射 decommit：Linux 用 `MADV_DONTNEED`，Darwin 用 `MADV_FREE_REUSABLE`，Windows 用 `DiscardVirtualMemory`。
- 只压完全位于活动区之前、且不与 viewport 相交的页。
- 调度：终端空闲 250 ms 后开始，每步间隔 1 ms，用 `tryLock` 取锁（`renderer/Thread.zig:755-870`）。
- 文档称文本类历史一般能压到 10–30%（`Config.zig:1423-1426`）。

**libghostty-vt 的默认值不一样**：

```zig
// ghostty/src/terminal/Terminal.zig:285-287 @ b3dc3d7
/// The maximum size of scrollback in bytes. Null is unlimited and zero
/// disables scrollback.
max_scrollback_bytes: ?usize = 10_000,
```

10 KB 不足一页，实际会被抬到最小值"活动区所需页数 + 1 页"（`PageList.zig:7135-7176`）。嵌入方必须设置 `GHOSTTY_TERMINAL_OPT_SCROLLBACK_MAX_BYTES`。[已核实；后果为推断]

#### 2.1.5 Reflow [已核实]

- 入口在 `PageList.resize`（`:1261-1340`）：只改行数时不 reflow；改列数时调用 `resizeCols`（`:1343-1586`），用 `ReflowCursor` 逐行遍历**整个屏幕加全部回滚**。
- 源页处理完立即回收，复用为下一个目标页。
- 有两项缓存：`StyleCache`（src→dst style id 映射）和 `cap_memo`。
- 代价：resize 会先强制解压所有页（`:1263-1267`，注释 `TODO(mitchellh): Deferred reflow on non-viewport/non-active pages`），所以每次列变化是 O(全部回滚 cell)，同步且急切。
- alt screen 不 reflow。
- termio 线程把 resize 合并 25 ms 再处理（`termio/Thread.zig:31`）。

#### 2.1.6 解析快路径 [已核实]

**批处理主循环**：`stream.zig:705-843` 在 release 构建中每批最多处理 4096 个码点（`var cp_buf: [4096]u32`）。

**Ground 状态的处理**：

```zig
// ghostty/src/simd/vt.zig:6
extern "c" fn ghostty_simd_decode_utf8_until_control_seq(
    input: [*]const u8, count: usize, output: [*]u32, output_count: *usize) usize;
```

- 实现在 `src/simd/vt.cpp`，用 Google Highway 做多目标动态分发：
  - ASCII 段：一次装一个向量，`FindFirstTrue(Eq(ESC) | Ge(0x80))`，同时把字节加宽成 u32 写出（`:297-362`）；
  - 遇到非 ASCII：`simdutf::convert_utf8_to_utf32_with_errors`（`:158-183`）。
- 解码之后再用 `@Vector(u32)` 做 `(cp & ~0x9F) == 0` 测试，把 C0/C1 切出去，可打印段作为 `print_slice` 整段交给 handler。

**print 快路径**：`Terminal.printSliceFast/printSliceFill`（`Terminal.zig:581,876-899`）用 `Mask(Cell,{content_tag,style_id,wide,hyperlink},4)` 一次判断 4 个目标格，批量写入。

**码点宽度不走 SIMD**：用的是查找表，≤0xFF 硬编码为宽度 1（`src/unicode/main.zig:26`）。

#### 2.1.7 IO 线程与锁 [已核实]

```zig
// ghostty/src/termio/Exec.zig:1306-1352 @ b3dc3d7
const buffer_count = 4;                 // 环形缓冲个数 = 背压上限（"Less than 4 there are minor slowdowns"）
const buffer_capacity = 64 * 1024;      // 一批 = 一次加锁的工作量
const bridge_threshold = 1024;          // ≥1KiB 视为饱和流
const bridge_spin_max = 16;
const bridge_poll_timeout_ms = 1;
const gather_budget_ns = 3 * std.time.ns_per_ms;

// ghostty/src/termio/Termio.zig:697
pub fn processOutput(self: *Termio, buf: []const u8) void {
    self.renderer_state.mutex.lockUncancelable(global.io());
    defer self.renderer_state.mutex.unlock(global.io());
    self.processOutputLocked(buf);   // queueRender() + terminal_stream.nextSlice(buf)
}
```

**两段式读取**：

- 原因：macOS 上一次 read 最多拿到约 1 KiB，单线程循环会让生产者停顿。
- 批次交付规则：交互式的小输出在第一次 EAGAIN 时立即交付；饱和流先自旋最多 16 次，再以 1 ms poll 衔接，每批总预算 3 ms。
- 背压：4 块缓冲都在途时，gather 线程阻塞。PTY 内核队列于是保持满，反压子进程。
- Windows 仍是单线程循环，缓冲只有 1024 字节（`Exec.zig:1784`）。

**锁的公平性**（`renderer/State.zig:35-105`）：mutex 不公平，所以加了 "demand handoff"：

- 渲染线程调用 `lockDemand()`，把 `demand` 计数加一；
- IO 线程在每个 64 KiB 批次边界调用 `yieldToDemand()`，用 futex 等最多 1 ms，让渲染线程先拿到锁。

#### 2.1.8 RenderState（两阶段快照）[已核实]

`src/terminal/render.zig:19-118` 的注释说明：它取代了 1.2.x 时"每帧 clone viewport"的做法，因为 clone 会阻塞 IO。

- **`beginUpdate`**（持锁）：按页遍历视口行，消费 page/row 的 dirty 位，只对脏行 `fastmem.copy` 原始 cell，grapheme 拷进行内 arena（`:511,766-833,1408-1470`）。
- **`endUpdate`**（无锁）：样式反规范化。之后渲染器做 regex 链接匹配、搜索高亮、`rebuildCells`、shaping 和 GPU 提交，全部在锁外（`generic.zig:1555-1700`）。
- 每行带 `Row.Id{serial, y}`，可以跨更新识别同一行，用来缓存已 shaping 的行（`:371`）。

#### 2.1.9 渲染器 [已核实]

**节拍**

- 没有固定帧率，也没有合并定时器：每次 wakeup 都立即执行 `updateFrame` 和 `drawFrame`。旧的 10 ms 定时器已被注释掉（`renderer/Thread.zig:474-517`）。
- 唤醒用 `xev.Async`，多次 notify 自然合并成一次。
- macOS 有 CVDisplayLink：只在 vsync 时 draw，没有变化时停止（`generic.zig:1297-1303`）。
- 光标闪烁间隔 `CURSOR_BLINK_INTERVAL = 600` ms；IO 有输出时重置闪烁，节流 500 ms（`Thread.zig:20`；`Termio.zig:714-725`）。
- 动画（自定义 shader、kitty 动图）使用 `draw_interval_ms = 8`（`generic.zig:1102`）。
- 交换链 3 缓冲，用 `frame_sema` 限制在飞帧数（`Metal.zig:38`；`generic.zig:299-347`）。

**GPU cell**：

```zig
// ghostty/src/renderer/metal/shaders.zig:267-293 @ b3dc3d7
pub const CellText = extern struct {     // @sizeOf == 32（测试断言）
    glyph_pos: [2]u32 align(8), glyph_size: [2]u32 align(8),
    bearings: [2]i16 align(4), grid_pos: [2]u16 align(4),
    color: [4]u8 align(4),               // A 通道用于 faint 和光标透明度
    atlas: Atlas align(1),               // grayscale / color
    bools: packed struct(u8){ no_min_contrast, is_cursor_glyph, _pad: u6 } align(1),
};
pub const CellBg = [4]u8;                // bg 是 [rows*cols] 扁平数组，用一个全屏三角形采样
```

**每个 cell 的绘制顺序**（`generic.zig:2914-3360`）：bg → 下划线（压在字形下面）→ 上划线 → 字形 → 删除线。

- **block 光标下的文字**：顶点着色器按 `cursor_pos` 把它改成 `cursor-text` 色（`cell_text.v.glsl:134-141`）。
- **faint**：alpha = `ceil(0.5*255)`。

#### 2.1.10 run 切分与 shaping [已核实]

`RunIterator.next`（`src/font/shaper/run.zig:47-305`）在以下位置切断 run：

- 选区边界；
- 除 bg 以外任何样式变化（`comparableStyle` 先把 bg 置空再比较）；
- 字体 face 变化；
- 光标：光标前一格切断，光标格单独成 run，从而在光标下禁用连字（可配置 `font-shaping-break`）；
- 硬编码的坏连字 `f+l`、`f+i`、`s+t`。

此外：

- run 没有最大长度限制。
- 只看 grapheme 的第一个附加码点决定 VS15/VS16 呈现方式，VS 本身不送进 shaper。
- shaping 缓存键是 Wyhash(码点 + 相对 cluster + 长度 + font_index)，颜色不进键（`run.zig:262-287`；`Cache.zig:31-44`）。
- HarfBuzz 后端强制 LTR；每个 cluster 的 x 对齐到网格，advance 只用于 cluster 内部偏移（`harfbuzz.zig:131-300`）。
- 默认特性 `liga=1`。要关掉编程连字，文档建议配 `-calt`；全部关闭用 `-calt, -liga, -dlig`（`feature.zig:287-289`；`Config.zig:236-253`）。

#### 2.1.11 字体度量 [已核实]

```zig
// ghostty/src/font/Metrics.zig:243-327 @ b3dc3d7
const face_width  = face.cell_width;                 // ASCII 32..126 的最大 advance
const face_height = face.lineHeight();               // ascent - descent + line_gap
const cell_width  = @round(face_width);              // round，不是 ceil
const cell_height = @round(face_height);
const face_baseline = face.line_gap/2 - face.descent;
const cell_baseline = @round(face_baseline - (cell_height - face_height) / 2);  // 从底部量
const underline_thickness     = @max(1, @ceil(face.underlineThickness()));
const strikethrough_thickness = @max(1, @ceil(face.strikethroughThickness()));
const underline_position = @round(top_to_baseline - face.underlinePosition());  // 从顶部量
```

**缺失度量的估算**（`:120-230`）：

- cap height = 0.75 × ascent
- ex height = 0.75 × cap height
- 下划线粗细 = 0.15 × ex height

**`adjust-cell-height`**：把差值上下各分一半，基线和各条装饰线跟着平移（`:360-405`）。

**点数到像素**：`px = pt × dpi / 72`；`xdpi = round(content_scale × default_dpi)`，其中 default_dpi 在 macOS 为 72，其他平台为 96（`face.zig:29,52-56`）。

#### 2.1.12 字体回退 [已核实]

`CodepointResolver.getIndex`（`src/font/CodepointResolver.zig:85-235`）依次尝试：

1. `font-codepoint-map` 覆盖；
2. sprite face；
3. 已加载字体，要求 style 和 presentation 精确匹配；
4. 非 regular 样式时改用 regular 再找；
5. 系统发现回退字体。新找到的字体带 `size_adjustment = .ic_width`，即按"水"字宽度与主字体对齐（`Collection.zig:572-660`）；
6. regular + 任意 presentation。

**合成字形**：

- 合成粗体：CoreText 用描边 `max(pt/14,1)`，FreeType 用 `FT_Outline_Embolden`。
- 合成斜体：CoreText 斜切约 15°，FreeType 12°。
- 开关：`font-synthetic-style`，默认全开。

**emoji 约束**：强制 `.size=.cover`，居中，左右 padding 0.025（`SharedGrid.zig:416-432`）。

**光栅化**：灰度 AA 加亚像素定位，不做 LCD 子像素（`coretext.zig:404-416`）。

#### 2.1.13 Sprite（程序化字形）[已核实]

文件在 `src/font/sprite/`，用 z2d 画布在 `alpha8` 表面上绘制。函数名形如 `draw<MIN>_<MAX>`，编译期扫描函数名自动生成码点范围表（`Face.zig:56-160`）。

| 文件 | 范围 |
|---|---|
| box.zig | U+2500–257F |
| block.zig | U+2580–259F |
| braille.zig | U+2800–28FF |
| powerline.zig | E0B0–E0BF、E0D2、E0D4（只实现几何类，不含风格化字形） |
| geometric_shapes.zig | U+25E2–25E5、25F8–25FA、25FF |
| branch.zig | U+F5D0–F60D（git 分支图） |
| symbols_for_legacy_computing(_supplement).zig | 1FB00–1FBEF 若干段；1CC1B–1CEAF 若干段（含 octant 1CD00–1CDE5） |
| special.zig | 下划线（single/double/dotted/dashed/curly）、删除线、上划线、各种光标（rect/hollow/bar/underline） |

**像素对齐规则**（`draw/common.zig:22-36,205-233`）：

- 线宽：base = `metrics.box_thickness`（默认等于下划线粗细）；light = base，heavy = 2 × base。
- 分数坐标：
  ```
  Fraction.min = s - round((1-f)*s)
  Fraction.max = round(f*s)
  ```
  这样两个半块在奇数尺寸下也能无缝拼合。
- 盲文：点宽 = `min(w/4, h/8)`，剩余像素按"点宽 > 边距 > 间距"的优先级分配（`braille.zig:57-120`）。

#### 2.1.14 其他渲染细节 [已核实]

- **minimum-contrast**：在顶点着色器里逐字形实例计算 WCAG 对比度。不达标时改用黑或白中对比更高的一个。box、block、powerline 等字符豁免（`common.glsl:80-110`；`cell.zig:294-345`）。默认值为 1，即关闭。
- **alpha-blending**：macOS 默认 `native`，其他平台默认 `linear-corrected`。后者用前景和背景的亮度反推 alpha，以接近 gamma 正确的混合（`cell_text.f.glsl:34-66`）。
- **SGR 5 闪烁文本**：终端记录了这个标志，但渲染器从不读取，所以**不渲染**。

### 2.2 Alacritty（与 vte）

#### 环形缓冲 [已核实]

```rust
// alacritty_terminal/src/grid/storage.rs:13,33-53,180-185 @ 5421470
const MAX_CACHE_SIZE: usize = 1_000;
pub struct Storage<T> { inner: Vec<Row<T>>, zero: usize, visible_lines: usize, len: usize }
pub fn rotate(&mut self, count: isize) {
    let len = self.inner.len();
    self.zero = (self.zero as isize + count + len as isize) as usize % len;
}
```

- **`scroll_up`**：当 `region.start == 0` 时只调用 `rotate`，被滚出的行自动进入历史；带 margin 时逐行 swap（`grid/mod.rs:252-307`）。
- **`Row`**：`occ` 记录写过的上界，reset 时只清 `[0..occ]`（`row.rs:17-25,73-92`）。
- **扩容与收缩**：一次至少扩 1000 行；`inner.len() > len + 1000` 时才 truncate（`storage.rs:107-138`）。

#### Damage 与重绘 [已核实]

- 字符写入本身不标 damage，而是把"上一帧光标点"和"当前光标点"并入每行的 `[left,right]` 区间（`term/mod.rs:458-486`）。
- 所有滚动、颜色变化、alt screen 切换、resize 都直接标全脏。
- 前端每帧 `renderer.clear()` 后 `draw_cells` 遍历全部可见 cell。damage 只用于 Wayland 的 `swap_buffers_with_damage`（`display/mod.rs:607-623,838,878`），也就是说，**damage 不减少 GPU 或 CPU 工作量，只是给合成器的提示**。

#### 线程与锁 [已核实]

```rust
// alacritty_terminal/src/event_loop.rs:24,27 @ 5421470
pub(crate) const READ_BUFFER_SIZE: usize = 0x10_0000;   // 1 MiB
const MAX_LOCKED_READ: usize = u16::MAX as usize;       // 65535
// alacritty_terminal/src/sync.rs:11-48
pub struct FairMutex<T> { data: Mutex<T>, next: Mutex<()> }
pub fn lock(&self) -> MutexGuard<'_, T> { let _next = self.next.lock(); self.data.lock() }
pub fn lease(&self) -> MutexGuard<'_, ()> { self.next.lock() }
```

FairMutex 的工作方式：

- PTY 线程在整个读批次期间持有 `lease`（也就是 `next` 锁），自己用 unfair 方式取 `data`。
- UI 线程的 `lock()` 必须先排队拿 `next`，所以只能在两个批次之间插进来，但一定插得进来。

**帧调度**：vsync 被显式关闭（`SwapInterval::DontWait`），由 `FrameTimer` 按显示器刷新间隔对齐下一帧（`display/mod.rs:512-515,1576-1601`）。

#### vte 的 ground 快路径 [已核实]

```rust
// vte/src/lib.rs:593-620 @ abeae76 (0.15.0)
/// The ground state is handled separately since it can only be left using
/// the escape character (`\x1b`). This allows more efficient parsing by
/// using SIMD search with [`memchr`].
fn advance_ground<P: Perform>(&mut self, performer: &mut P, bytes: &[u8]) -> usize {
    let plain_chars = memchr::memchr(0x1B, bytes).unwrap_or(num_bytes);
    ...
    match str::from_utf8(&bytes[..plain_chars]) { Ok(parsed) => Self::ground_dispatch(performer, parsed), ... }
// vte/src/ansi.rs:36,39
const SYNC_UPDATE_TIMEOUT: Duration = Duration::from_millis(150);
const SYNC_BUFFER_SIZE: usize = 0x20_0000;   // 2 MiB
```

这和 .NET 里的 `span.IndexOf((byte)0x1B)`（已向量化）加 `Utf8.IsValid` 是同一个思路。

**Reflow** [已核实]：`grow_columns` 从下往上，把带 `WRAPLINE` 标记的行拼接起来；`shrink_columns` 截断后把溢出部分前置到下一行（`grid/resize.rs:101-388`）。代价为 O(行数 × 列数)，并且要重新分配 `Vec<Row>`。alt screen 不 reflow。

**默认回滚**：10,000 行，上限 100,000（`alacritty/src/config/scrolling.rs:7,34`）。

**字体**：不做 shaping，也不做连字（在源码里 grep harfbuzz 无结果）。atlas 为行式 1024²（`renderer/text/atlas.rs:12`）。内置绘制范围：U+2500–259F、1FB00–1FB3B、1FB82–1FB8B、E0B0–E0B3（`builtin_font.rs:29-39`）。

### 2.3 WezTerm

**Cell** [已核实]（`wezterm-cell/src/lib.rs:719-729,1029-1036`）

- `Cell = TeenyString(8) + CellAttributes(16) = 24 B`。
- truecolor、下划线色、超链接都放进 `Box<FatAttributes>`。在 VecStorage 里每个 truecolor cell 各持有一个 Box，内存和分配成本都偏高 [推断]。

**ClusteredLine** [已核实]（`wezterm-surface/src/line/clusterline.rs:18-42`；`line.rs:102-115,760-793,1047-1075`）

```rust
struct Cluster { cell_width: u16, attrs: CellAttributes }
pub(crate) struct ClusteredLine { text: String, is_double_wide: Option<Box<FixedBitSet>>, clusters: Vec<Cluster>, len: u32, ... }
```

- 新行默认就是 C（clustered）形态，追加写入有快路径。
- 随机写会把它展开成 `Vec<Cell>`。
- 行滚出屏幕顶部时调用 `compress_for_scrollback()` 压回 C 形态。
- 这正是"冷行压实"的参考实现：普通 shell 输出每行只占"文本字节 + 每个属性游程约 24 B"。

**StableRowIndex 与 seqno** [已核实]（`term/src/screen.rs:646-761`）

- 普通滚动不改变行的 seqno 和 shape hash，所以缓存全部命中，只有新露出的行算脏。
- GUI 每帧仍然遍历全部可见行，但走 5 层缓存：
  1. seqno → shape hash；
  2. line quad，键里包含像素 y，滚动后会失效；
  3. 整行 element；
  4. cluster shape；
  5. glyph。

  见 `termwindow/render/pane.rs:371-554`。

**回退字体异步解析** [已核实]（`wezterm-font/src/lib.rs:193-203,520-557`）

- 缺字的码点交给后台线程做系统字体查找，第一帧先画占位。
- 找到后递增 `shape_generation`，清空 shaping 缓存。
- 首帧不会被系统字体查找阻塞，这个做法值得借鉴。

**光标下的连字** [已核实]（`screen_line.rs:546-620`）：不拆连字，而是按光标和选区的像素区间把字形纹理切成最多 7 条竖条，每条用各自的颜色。

**默认回滚**：3500 行（`config/src/config.rs:1705-1716`）。

### 2.4 Windows Terminal（AtlasEngine）

**ROW** [已核实]（`src/buffer/out/Row.hpp:241-320`）

- 文本是 UTF-16 `_chars` 加 `_charOffsets[cols+1]` 偏移表。宽字符的尾半格复用同一个偏移，并置最高位 `0x8000`。例如 `"a猫d"` 存为 `0 1 1 2 3`。
- 属性按行 RLE。
- `ROW::_init` 用 AVX2/SSE 批量填充。为此把数组补齐到 16B 对齐，注释原文："wastes up to 5.8% memory but increases overall scrolling performance by around 40%"（`Row.hpp:101-122`）。

**内存** [已核实]

- 实测注释（`textBuffer.hpp:383-387`）："120 columns = 80KB chunks, 5.6MB buffer at 9001 rows"，折合每 cell 约 5.2 B。
- 一次 `VirtualAlloc(MEM_RESERVE)` 预留，按需 commit（每次预读 128 行）。注释说 conhost 启动内存因此从约 7 MB 降到约 2 MB。

**AtlasEngine**

主要设计 [已核实]：

- **ShapedRow 缓存**：每行缓存 shaping 结果，只有失效行重新 shaping；滚动时只轮换行指针（`AtlasEngine.cpp:166-235`）。
- **网格对齐**：shaping 后把 cluster 的 advance 强制对齐到网格（`:1173`）：
  ```cpp
  _api.glyphAdvances[nextCluster - 1] += expectedAdvance - actualAdvance;
  ```
- **颜色位图**：背景色、前景色、下划线色各一张"每格 1 个 u32"的位图。背景只画**一个全屏 quad**，像素着色器按 `position / cellSize` 采样（`shader_ps.hlsl:38-44`）。D2D 回退路径则用 bitmap brush 做最近邻拉伸（`BackendD2D.cpp:188-207`）。
- **选区**：直接写进颜色位图，没有叠加层（`AtlasEngine.cpp:410-484`）。
- **atlas**：`stb_rect_pack` 分配，满了先 flush 再整张清空，没有 LRU。缓存键是 `IDWriteFontFace*` + glyphIndex + 行渲染方式（LineRendition），颜色不进键（`BackendD3D.cpp:1706-1721`；`BackendD3D.h:104-153`）。
- **QuadInstance** 只有 20 字节。注释说调用量可达"每秒 5M 次"，结论是"实例数据打包得越紧越好"（`BackendD3D.h:61-100`；`BackendD3D.cpp:964-1037`）。
- **BuiltinGlyphs**：覆盖 U+2500–259F、E0B0–E0BF、1FB00–1FB94，每字符最多 4 条 32 位指令（`BuiltinGlyphs.h:13-30`）。

线程与帧节奏 [已核实]：

- 渲染线程在锁内完成 shaping，锁外 Present（`renderer.cpp:402-468`）。
- 锁是**公平的 ticket lock**（`src/inc/til/ticket_lock.h:10`），输出洪流下渲染线程不会饿死。
- 交换链 `FLIP_SEQUENTIAL` + `SetMaximumFrameLatency(1)`，配合 frame latency waitable object（`AtlasEngine.r.cpp:352-536`）。

**Reflow** [已核实]：新建一个 TextBuffer，把旧内容流式复制进去，然后 swap（`textBuffer.cpp:2718-2957`）。整个过程同步执行，并持有终端锁。

### 2.5 xterm.js

**BufferLine** [已核实]

```ts
// xterm.js/src/common/buffer/BufferLine.ts:14-34 @ 991857a
// [0]: content `uint32_t` - wcwidth(2) comb(1) codepoint(21)
// [1]: fg      `uint32_t` - flags(8) r(8) g(8) b(8)
// [2]: bg      `uint32_t` - flags(8) r(8) g(8) b(8)
// src/common/input/WriteBuffer.ts:11-34
DISCARD_WATERMARK = 50000000,      // ~50 MB，超过直接 throw
WRITE_TIMEOUT_MS = 12,             // 每片最多解析 12ms 后让出
WRITE_BUFFER_LENGTH_THRESHOLD = 50
```

**流控** [已核实]（`WriteBuffer.ts:161-174,219-315`）：

- `write(data, callback)` 中的 callback 在该块**解析完成**时触发，生产者据此按高低水位暂停或恢复 PTY 读取。
- 用户输入后的第一个 `write` 会同步立即解析，这是一项延迟优化。

**解析器** [已核实]（`EscapeSequenceParser.ts:33-90,684-770`）：

- 转移表是 `Uint16Array`，下标 `state<<8|code`；所有 ≥0xA0 的码点归为同一列。
- 有 EXE 快路径和 CSI 快路径。
- 可打印字符做 4 路展开扫描，整段交给 print handler。

**WebGL 渲染器** [已核实]：

- `RenderModel` 每格 4 个 u32；每格四个值都与上一帧相同就跳过。
- glyph atlas 的**缓存键包含颜色**：字形连同背景色一起光栅化，以换取次像素效果，代价是 atlas 会膨胀。
- 页大小 512，单页上限 4096²。页数达到上限时，挑 4 张合并成 1 张；凑不齐就全部清空（`TextureAtlas.ts:34-46,155-277`）。

**customGlyphs**（默认 true）[已核实]：覆盖 2500–259F、2800–28FF、E0A0–E0BF、EE00–EE0B、F5D0–F60D、1FB00–1FBFA（`CustomGlyphDefinitions.ts`）。

**可访问性** [已核实]（`AccessibilityManager.ts:15-17,64-87,141-160`）：

- 屏幕外放一个 `role=list` 的 DOM，每行一个节点，再加一个 `aria-live="assertive"` 区域。
- 每次最多播报 `MAX_ROWS_TO_READ = 20` 行，超出则提示 "too much output"。
- 用户自己输入的字符不重复播报。

**默认值**：回滚 1000 行，`minimumContrastRatio = 1`（OptionsService.ts:34,44）。

### 2.6 foot（CPU 渲染加 damage tracking 的标杆）

**为什么用 CPU 还能这么快** [已核实]：

1. **cell 级跳过**：`if (cell->attrs.clean) return 0;`（`render.c:696-705`）。
2. **行级多线程**：主线程把脏行号推入共享队列，worker 各自 pop，在指向**同一块共享内存**的各自的 `pixman_image` 上画，并各自累计 damage 区域（`render.c:3556-3588,2184-2300`）。worker 数默认等于 CPU 核数（`config.c:3627`）。
3. **滚动不重画**：在 memmove 和 "SHM scrolling" 之间二选一（`render.c:1319-1430`）。后者预留 512 MB 虚拟空间，滚动只移动 buffer 的 offset，用 `fallocate(PUNCH_HOLE)` 释放前面的物理页（`shm.c:395-450,657-800`）。判断规则：
   ```c
   bool try_shm_scroll = shm_can_scroll(buf) &&
       (dmg->lines + dmg->region.start + (term->rows - dmg->region.end)) < term->rows / 2;
   ```
4. **Buffer age**：age==0 直接复用；age==1 只从上一帧拷贝"上一帧 dirty 减去本帧整行重画"的部分；空闲时由 worker 预拷（pre-apply damage）（`render.c:3198-3293,5408-5480`）。
5. **延迟渲染合并输出**：
   ```c
   // foot/config.c:3652-3653 @ cb27717
   .delayed_render_lower_ns = 500000,         /* 0.5ms */
   .delayed_render_upper_ns = 16666666 / 2,   /* half a frame period (60Hz) */
   ```
   lower 定时器在每次收到 PTY 数据时重置，upper 只启动一次，任一到期就渲染。另外，只要还有挂起的 frame callback，就只把帧标为 pending，等回调来了再画（`render.c:5146-5218,4313-4359`）。
6. **PGO**：`INSTALL.md:74-76`："foot runs **much** faster when compiled with gcc instead of clang. A profile-guided gcc build can be more than twice as fast as a clang build."

**其他实现细节** [已核实]：

- **ASCII 快路径**是一个函数指针 `ascii_printer_fast`，开启 sixel、OSC 8、插入模式等任一特性时会切换到 generic 版本（`terminal.c:4112-4180`）。没有 SIMD。
- **PTY 读取**：单线程，24 KiB 栈缓冲，每个事件最多读 10 次（`terminal.c:243-297`）。
- **交互式 resize**：拖动期间只对一份快照做截断式 resize；停止 100 ms（`resize-delay-ms`）后，才做真正的 reflow 并发送 TIOCSWINSZ，期间暂停读取 PTY（`render.c:4379-4400`；`terminal.c:264-272`）。这对大回滚下的 resize 卡顿是很好的参考。
- **box drawing 线宽**：`0.04pt × cell 对角线 × dpi/72`，heavy = 3 × light（`box-drawing.c:3346-3352`）。

**对 Skia CPU 路径的启示** [推断]：

- 可以给同一块像素内存创建多个 `SKSurface`，按行并行绘制，行与行互不重叠。
- foot 的 memfd punch-hole 技巧依赖 Linux 和 wl_shm，跨平台用不了。替代方案是"位图按 y 方向做成环形，提交时分两段绘制"。
- 但 Avalonia 的 WriteableBitmap 每次修改都要整图快照并上传（见 §3.4），foot 的这些优化在 Avalonia 里只能拿到一半。

### 2.7 kitty

**Cell** [已核实]：`CPUCell` 12 B + `GPUCell` 20 B，每格共 32 B（`line.h:12-100`）。默认回滚 2000 行；pager 历史以带 SGR 的 UTF-8 文本存放，默认 0（关闭），不压缩（`options/definition.py:524,746`）。

**SIMD** [已核实]（`simd-string.c:15-130,343-430`）

- 提供的函数：`utf8_decode_to_esc`、`find_either_of_two_bytes`、`printable_ascii_run_length` 等。
- 通过 simde 编出 128 和 256 两版；x86 上另有 AVX-512 版。
- 运行时分派。ARM64 直接用 avx2 版，由 simde 翻译成 NEON。
- 可打印 ASCII 的写入：先用 `printable_ascii_run_length` 求出 run 长度，再按模板 memcpy 整块 cell（`screen.c:1360-1395`）。
- 预取下一个历史写入槽，注释称能让批量文本吞吐提升约 35%（`history.c:333-345`）。

**IO 与调度** [已核实]

- I/O 线程直接读进 parser 的 1 MiB 缓冲。
- macOS 上一次读满 1 KiB 就继续读，最多 gather 到 64 KiB（`child-monitor.c:1748-1764`）。
- 默认 `input_delay 3`、`repaint_delay 10`、`sync_to_monitor yes`（`definition.py:1491-1535`）。

**连字** [已核实]：`disable_ligatures` 可选 never/cursor/always，默认 never；cursor 模式下在光标处把 run 切成三段（`fonts.c:2135-2170`）。

**box drawing** [已核实]：在 `decorations.c:2363` 中用 C 实现，4× 超采样后下采样；覆盖范围比 foot 宽，包含 powerline。

**性能文档**（`docs/performance.rst:72-82`）[已核实]：

- 测试环境：Ryzen 7 PRO 5850U，Linux/X11。
- 方法：`kitten __benchmark__` 用 `\x1b[?2026h…l` 包住数据以**抑制渲染，只测解析**（`tools/cmd/benchmark/main.go:65-90`）。
- 结果见 §2.9。

### 2.8 contour

- **行存储** [已核实]：用 `LineSoA` 分层 SoA，空行不展开；`LineShape::Uniform`（整行同一样式、每格一个码点）时整行批量输出（`LineSoA.hpp:43-180`；`Line.hpp:52-80`）。
- **双缓冲** [已核实]：`RenderDoubleBuffer` 交换时，终端线程（写端）对 `readerLock` **只做 `try_lock`**；渲染线程还在读时就放弃这次交换，绝不让渲染线程拖慢终端线程（`RenderBuffer.cpp:59-86`）。

**对本项目的意义** [推断]：这与本文推荐的"核心线程发布快照、渲染线程永不等锁"方向一致，只是持锁的一方相反。

---

### 2.9 公开的吞吐与延迟数据

标注说明：[已核实] 表示已抓取原页面核对过；[摘要] 表示只来自搜索摘要或二手转述，原文未核实。

#### 吞吐

**kitty `kitten __benchmark__`**

- 来源：`kitty/docs/performance.rst:72-82 @ f426c08`，以及 https://sw.kovidgoyal.net/kitty/performance/ 。[已核实]
- 环境：Ryzen 7 PRO 5850U，Linux/X11。
- 方法：用 2026 包住数据**抑制渲染**，所以只测解析。
- 单位 MB/s，越高越好：

| 终端 | ASCII | Unicode | CSI | Images | 平均 |
|---|---:|---:|---:|---:|---:|
| kitty 0.33 | 121.8 | 105.0 | 59.8 | 251.6 | 134.55 |
| gnome-terminal 3.50.1 | 33.4 | 55.0 | 16.1 | 142.8 | 61.83 |
| alacritty 0.13.1 | 43.1 | 46.5 | 32.5 | 94.1 | 54.05 |
| wezterm 20230712 | 16.4 | 26.0 | 11.1 | 140.5 | 48.5 |
| xterm 389 | 47.7 | 18.3 | 0.6 | 56.3 | 30.72 |

**foot 的 vtebench 结果**

- 来源：`foot/doc/benchmark.md @ cb27717`。[已核实]
- 日期 2022-05-12；环境 i9-9900，窗口 2040x1884；单位 ms，越低越好：

| 测试 | foot GCC+PGO | foot | Alacritty 0.10.1 | XTerm 372 |
|---|---:|---:|---:|---:|
| cursor motion | 10.40 | 14.07 | 24.97 | 1622.86 |
| dense cells | 29.58 | 45.46 | 97.45 | 2323 |
| light cells | 4.34 | 4.40 | 12.84 | 49.81 |
| scrolling | 135.31 | 116.35 | 121.69 | 4041 |
| scrolling fullscreen | 5.70 | 5.66 | 10.92 | 124.25 |
| unicode | 11.56 | 10.92 | 15.94 | 4779 |

**其他吞吐数据**

| 终端 / 来源 | 数据 |
|---|---|
| **vtebench 本身** | [已核实] 只测 "the speed at which a terminal reads from the PTY"，不测帧率和延迟。默认每个样本 ≥1 MiB、预热 1 次、最长 10 s。`dense_cells` 每格都输出 `\e[38;5;fg;48;5;bg;1;3;4m<char>`。来源 https://github.com/alacritty/vtebench |
| **Ghostty devlog 006**（2024-02-12） | [已核实] SIMD ASCII 处理比标量快 7.32×，与 memcpy 基线持平（52.3 ms vs 52.7 ms）；UTF-8→UTF-32 快 16.6×。`cat` 5.4 MB 日文文本：Ghostty 73 ms、Alacritty 66 ms、Terminal.app 124 ms、WezTerm 140 ms、kitty 392 ms、iTerm2 470 ms。作者自称这个测试 "very contrived"。来源 https://mitchellh.com/writing/ghostty-devlog-006 |
| **Ghostty 1.3.0**（2026-03-09） | [已核实] renderer 持锁时间降为 1/2 到 1/5，"in most frames, the renderer doesn't hold the lock at all"。来源 https://ghostty.org/docs/install/release-notes/1-3-0 |
| **Ghostty（第三方转述）** | [摘要] 150 MB ASCII `cat`：nightly 575 ms，1.3.2 版 1.5 s，Alacritty 1.2 s，Kitty 1.7 s（约 2026-07）。来源 https://digg.com/ai/tfu92ilx |
| **第三方横测** | [已核实，日期不明] 11 MB `cat`：foot 251 ms、kitty 401 ms、alacritty 404 ms、ghostty 407 ms、wezterm 1246 ms。来源 https://github.com/vizenc/terminal-benchmark |
| **Windows Terminal** | [已核实] AtlasEngine 相对旧引擎：纯文本约 2×，彩色 VT ≥10×（PR #11623，2021）。ConPTY 重写后 WT 吞吐约 20×（PR #17510，2024-08）。1.22 版纯文本 I/O 最高 16×。1.25 版重新启用 PGO，再快 10–20%（2026-03-05 博文） |
| **xterm.js** | [已核实] 吞吐 "rather low (5 - 35 MB/s)"。写缓冲硬上限 50 MB。建议水位 HIGH = 100000、LOW = 10000，HIGH 不宜超过 500 KB。来源 https://xtermjs.org/docs/guides/flowcontrol/ |

#### 输入延迟

| 来源 | 方法 | 关键数字（ms） |
|---|---|---|
| beuke.org，2024-03 [已核实] | Typometer（纯软件，不含键盘和显示器），Ryzen 4750U，Arch + Xorg | min/max/avg：xterm 2.8/9.8/5.3；**alacritty 0.13.1 5.2/17.8/6.9**；kitty 调优后 8.1/16.3/10.7；**kitty 默认 11.5/34.4/23.8**；wezterm 11.3/40.9/26.1；gnome-terminal 29.0/32.3/30.2 |
| Zutty，2021-01 [已核实] | Typometer，60 Hz，无合成器 | mean：xterm 2.21；Zutty 6.51；Kitty 8.96；Alacritty 17.48；gnome-terminal 27.98 |
| vizenc [已核实，日期不明] | 高速摄像，从键灯亮到字符出现，含显示链路 | avg：foot 15.0；alacritty 16.7；kitty 18.3；wezterm 30.8；ghostty 38.3 |
| Chad Austin，2024-02 [已核实] | 240 Hz 摄像，Win10，4K@120 Hz | conhost 33.3；WezTerm 62.5–66.7；Alacritty 62.5；WT 1.18 66.7。作者后来补充 "WT 1.19 contains a fix that reduces latency by half" |
| Dan Luu，2017-07 [已核实] | 按键到屏幕截图，macOS 10.12 | idle p50：terminal.app 6，alacritty 31，iterm2 44 |

**结论** [推断]

1. 延迟的大头是**调度延迟**，不是渲染速度。kitty 默认的 `input_delay 3`/`repaint_delay 10` 让平均延迟达到 23.8 ms，调优后降到 10.7 ms。因此本设计的交互路径**不加任何合并定时器**。
2. Windows 上的延迟主要由合成器、交换链和 vsync 链路决定。WT 用 `SetMaximumFrameLatency(1)` 加 waitable object 来压缩这一段。Avalonia 的交换链参数不是我们能改的，所以 Windows 端只能把"自身附加延迟"压到最低。

### 2.10 内存、默认回滚与 reflow 横向对比

| 终端 | 每 cell | 1 万行 × 120 列（推算） | 默认回滚 | 回滚上限计量 | 冷数据压缩 | reflow |
|---|---|---|---|---|---|---|
| Ghostty | 8 B，另有每行 8 B | ≈ 9.7 MB（按页触碰时提交） | 50 MB（≈7.2 万行 @80 列） | **字节** | LZ4，压到 10–30%（文档值） | 急切执行，O(全部回滚)，并解压所有页；resize 合并 25 ms |
| libghostty-vt | 同上 | 同上 | **10 KB**（会被抬到最小页数） | 字节 | 有，但由嵌入方调度 | 同上 |
| Alacritty | ≤24 B | ≈ 28.8 MB | 10,000（上限 100,000） | 行 | 无 | O(行 × 列)，并新建 Vec |
| WezTerm | 活动区 24 B；回滚为 clustered | 活动区 28.8 MB；回滚 ≈ 1–2 MB [推断] | 3,500 | 行 | 行压实（文本 + 游程） | `rewrap_lines` O(行) |
| Windows Terminal | ≈5.2 B（实测注释） | ≈ 6.2 MB | 9001 [推断，依据注释示例] | 行 | 无（只按需 commit） | 新建 buffer 后复制，持锁 |
| xterm.js | 12 B | ≈ 14.4 MB，另有 JS 对象开销 | 1,000 | 行 | 空闲时回收内存 | 原地执行，同步 |
| foot | 12 B | ≈ 14.4 MB | 1,000（行数向上取 2 的幂） | 行 | 无（行懒分配） | 新建 grid；拖动期间先截断，空闲 100 ms 后才真正 reflow |
| kitty | 32 B | ≈ 38.4 MB | 2,000，另有 pager 历史 0 MB | 行 | 无 | — |
| contour | ≈29 B | ≈ 35 MB（空行 O(1)） | — | 行 | 空行不展开 | — |

**结论** [推断]

- **用户心智与内存控制的折中**：用户习惯按"行"理解回滚，但按"字节"更能控制内存。建议配置项暴露为"行数"，内部换算成页数上限；也可以像 Ghostty 一样同时提供两种上限。
- **reflow 的代价都与回滚大小成正比，必须离开 UI 线程。** 具体做法：
  - 采用 foot 的"交互期间截断，空闲后再 reflow"，或采用 Ghostty 的 25 ms 合并；
  - reflow 在核心线程完成后发布新快照，再发送 `TIOCSWINSZ`。

---

## 3. Avalonia 12.1.3 的绘制路径

### 3.0 先弄清 Avalonia 12 的渲染管线 [已核实]

**UI 线程 `Control.Render` 只做录制**

`Control.Render(DrawingContext)` 在 UI 线程上执行，但只是把绘制命令录成 opcode 流（`RenderDataStream`）。真正的光栅化发生在合成器的渲染线程上：

```csharp
// Avalonia/src/Avalonia.Base/Rendering/Composition/Drawing/RenderDataDrawingContext.cs:140-148 @ 12.1.3
public override void Custom(ICustomDrawOperation custom) => Stream.DrawCustom(custom);
public override void DrawGlyphRun(IBrush? foreground, GlyphRun? glyphRun)
{   ...
    Stream.DrawGlyphRun(foreground.GetServer(_compositor), glyphRun.PlatformImpl.Clone());
}
// RenderDataStream.Replay.cs:57-58 —— 渲染线程回放
public void OnDrawCustom(ICustomDrawOperation? operation)
    => operation?.Render(new ImmediateDrawingContext(_context, false));
```

**渲染线程与 UI 线程的关系**

- 桌面平台的渲染定时器都在后台线程运行（`RunsInBackground => true`）：
  - Windows：`DxgiConnection` / `DirectCompositionConnection` / `WinUiCompositorConnection`；
  - macOS：`AvaloniaNativeRenderTimer`；
  - X11：`SleepLoopRenderTimer` / `DefaultRenderTimer`；
  - Wayland：`WaylandWorker.RenderTimer`。
- UI 到合成器**同一时间只有一个批次在途**（`MediaContext.Compositor.cs:64-82`，`CommitCompositorsWithThrottling`）。UI 线程卡住，新帧就提交不上去。

**脏矩形**

- 合成器保留上一帧（layer），只重画脏矩形（`ServerCompositionTarget.cs:198-260`）。
- 默认是 `SingleDirtyRectTracker`，即把所有脏区合并成**一个外接矩形**。要多矩形或 region 裁剪，必须在应用级打开 `CompositionOptions.UseRegionDirtyRectClipping`（`ServerCompositionTarget.cs:65-75`；`CompositionOptions.cs`）。Avalonia 12.1 博文也写明"多 dirty-rect 裁剪默认关闭（#21553）"。

**渲染后端**

- 12.1.3 中实现 `IPlatformRenderInterface` 的只有 Skia（`src/Skia/Avalonia.Skia/PlatformRenderInterface.cs`）和 Headless 桩（`HeadlessPlatformRenderInterface.cs`，其中 `GetFeature` 一律返回 null，见 `:516`）。Direct2D 后端已在 12.0 移除。
- 各平台默认渲染模式：

| 平台 | 默认顺序 | 来源 |
|---|---|---|
| macOS | Metal → OpenGl → Software | `AvaloniaNativePlatformExtensions.cs:67-72` |
| Win32 | AngleEgl → Software | `Win32PlatformOptions.cs:118-121` |
| X11 | Glx → Software | `X11Platform.cs:410-414` |

- 版本：SkiaSharp 3.119.4，HarfBuzzSharp 8.3.1.3（`Directory.Packages.props:14,57`）。

**跨线程限制**

- 合成器服务端对象只能"under compositor lock"访问，也就是只能在渲染回调里访问（`ServerCompositor.cs:314-319`）。
- `IRenderLoop.Wakeup()` 是 `internal`（`IRenderLoop.cs`），**非 UI 线程没有公开的方法唤醒空闲的渲染循环**，只能 `Dispatcher.UIThread.Post` 给 UI 线程，再提交一个批次。

### 3.1 四条路径对比

| | (a) `Control.Render` + `DrawGlyphRun` | (b) `ICustomDrawOperation` + Skia lease | (c) `CompositionCustomVisualHandler` | (d) `WriteableBitmap` CPU 光栅化 |
|---|---|---|---|---|
| 绘制发生在 | UI 线程录制，渲染线程回放 | UI 线程只录一个 op，渲染线程执行 `Render(ImmediateDrawingContext)` | **渲染线程** `OnRender` | 任意线程画进位图，UI 线程 `DrawImage` |
| 每帧需要 UI 线程吗 | 需要（`InvalidateVisual` → 重录） | 需要（`InvalidateVisual` → 新 op） | **活跃期不需要**（自轮询）；只有从空闲唤醒时需要 | 需要（`InvalidateVisual`） |
| 局部重绘 | 整个控件都算脏 | 整个 op 的 Bounds 都算脏 | `Invalidate(Rect)` 加上 `RenderClipIntersectes` | 整个位图都算脏 |
| 文字 | `GlyphRun`，Skia 后端缓存 `SKTextBlob` | `SKTextBlob` / `DrawGlyphRun` 均可 | 均可 | Skia CPU 光栅 |
| 依赖的非稳定 API | 无 | `ISkiaSharpApiLeaseFeature` 是 `[Unstable]` | 只有用 lease 时才依赖 | 无 |
| 估算：200×60 网格、2× 缩放下每帧成本 [推断，需 spike] | UI 线程 50–150 µs 加脏行 GlyphRun 构建；渲染线程 0.3–1.5 ms | UI 线程约 10 µs；渲染线程同 (c) | UI 线程 0；渲染线程全量 0.3–1.5 ms，1–3 个脏行 0.05–0.3 ms | 光栅 3–10 ms，外加整图快照 24 MB 和 GPU 上传 2–5 ms |
| 实现复杂度 | 低（与 CodeView 同类） | 中 | 中高 | 中 |
| 主要风险 | 与 UI 线程抖动耦合：StreamMark 流式排版、GC、布局都会推迟终端帧 | 同 (a)；没有 Skia 时 lease 为 null | 跨线程唤醒受限；需要自己管理 Size 和 DPI；API 文档不完整 | 带宽：每帧整图复制加上传 |

### 3.2 (a) `Control.Render` + `DrawGlyphRun` [已核实 API]

```csharp
// Avalonia.Base/Media/DrawingContext.cs:262, 442
public abstract void DrawGlyphRun(IBrush? foreground, GlyphRun glyphRun);
public PushedState PushTextOptions(TextOptions textOptions);
// Avalonia.Base/Media/GlyphRun.cs:63-68
public GlyphRun(GlyphTypeface glyphTypeface, double fontRenderingEmSize, ReadOnlyMemory<char> characters,
                IReadOnlyList<GlyphInfo> glyphInfos, Point? baselineOrigin = null, int biDiLevel = 0)
// Media/TextFormatting/GlyphInfo.cs:8
public readonly record struct GlyphInfo(ushort GlyphIndex, int GlyphCluster, double GlyphAdvance, Vector GlyphOffset = default)
```

**Skia 后端怎么画 GlyphRun**（`src/Skia/Avalonia.Skia/GlyphRunImpl.cs:22-131`）：

- 构造时取每个字形的 bounds（`SKFont.GetGlyphWidths`）。
- 按 `TextOptions` 用 `TwoLevelCache` 缓存最多 3 种 `SKTextBlob`（`AllocatePositionedRun` + `SetPositions`）。

**做法** [推断]

- 按"行 × 样式 run"缓存 `GlyphRun`，`GlyphInfo.GlyphAdvance` 固定为 cell 宽。
- 每帧都要重新录制全部可见行，因为 Render 是全量替换，`ServerCompositionDrawListVisual.cs:36-39` 收到新的 render data 就 `InvalidateContent`。
- 优点：只用稳定 API；Headless 绘制桩下也能运行；与 TKCode 的 `CodeView`（`src/TKCode.Desktop/Controls/CodeView.cs:69,313-370`，按行缓存 `TextLayout`）一脉相承，可以作为 MVP。
- 缺点：终端帧率受制于 UI 线程。TKCode 的 UI 线程同时承担 StreamMark 的流式 Markdown 渲染，Agent 大量输出时两者会争用。

### 3.3 (b) `ICustomDrawOperation` + `ISkiaSharpApiLeaseFeature` [已核实 API]

```csharp
// Avalonia.Base/Rendering/SceneGraph/CustomDrawOperation.cs:8-31
public interface ICustomDrawOperation : IEquatable<ICustomDrawOperation>, IDisposable
{ Rect Bounds { get; } bool HitTest(Point p); void Render(ImmediateDrawingContext context); }
// Skia/Avalonia.Skia/ISkiaSharpApiLeaseFeature.cs:8-28
[Unstable] public interface ISkiaSharpApiLeaseFeature { ISkiaSharpApiLease Lease(); }
[Unstable] public interface ISkiaSharpApiLease : IDisposable
{ SKCanvas SkCanvas { get; } GRContext? GrContext { get; } SKSurface? SkSurface { get; }
  double CurrentOpacity { get; } ISkiaSharpPlatformGraphicsApiLease? TryLeasePlatformGraphicsApi(); }
// Skia/Avalonia.Skia/DrawingContextImpl.cs:897-902
public object? GetFeature(Type t) { if (t == typeof(ISkiaSharpApiLeaseFeature)) return new SkiaLeaseFeature(this); return null; }
```

**lease 的约束**

- lease 期间如果调用其他 `ImmediateDrawingContext` 方法，会抛出 "The underlying graphics API is currently leased"（`DrawingContextImpl.cs:119-131,228-233`）。所以必须**先 Dispose lease，再调用 `DrawGlyphRun`**，二者不能交错。
- Dispose 时会恢复 canvas 的矩阵（`:139-147`）。
- GPU 模式下 `GRContext` 在整个 DrawingContext 生命周期内都被 `Monitor.Enter` 锁住（`:197-198`）。

**AOT** [推断，有强证据]

- SkiaSharp 3.119.4 程序集带 `IsTrimmable` 元数据（在 nuget 缓存的 DLL 中能看到）。
- TKCode 已经引用 `StreamMark.Mermaid`（`src/TKCode.Desktop/TKCode.Desktop.csproj:24`，`PublishAot=true`），而它正是用 `ICustomDrawOperation` + `ISkiaSharpApiLeaseFeature` 画 SKPicture（`StreamMark/src/StreamMark.Mermaid/SkiaPictureDiagram.cs:90-130`）。在现有 AOT 0 警告的约束下，这条路已经走通。

**非 Skia 后端时的退化**

- 唯一的非 Skia 情况是 Headless 的 `UseHeadlessDrawing=true`（默认值，见 `AvaloniaHeadlessPlatform.cs:115`），此时 `TryGetFeature` 返回 null。
- 应对：要么什么都不画，要么回退到 (a)。
- TKCode 的测试用的是 `UseSkia()` + `UseHeadlessDrawing = false`（`tests/TKCode.Desktop.Tests/HeadlessApp.cs:18-19`），lease 可用 [推断]。

### 3.4 (c) `CompositionCustomVisual` + `CompositionCustomVisualHandler`（推荐）[已核实 API]

```csharp
// Avalonia.Base/Rendering/Composition/CompositionCustomVisualHandler.cs:10-111 @ 12.1.3（节选）
public abstract class CompositionCustomVisualHandler
{
    public virtual void OnMessage(object message) { }              // 渲染线程：处理 UI 发来的消息
    public virtual void OnAnimationFrameUpdate() { }               // 渲染线程：每个 tick 一次（需注册）
    public abstract void OnRender(ImmediateDrawingContext drawingContext);
    protected Vector EffectiveSize { get; }
    protected TimeSpan CompositionNow { get; }                     // 合成器时钟，可驱动光标闪烁
    public virtual Rect GetRenderBounds();
    protected void Invalidate();  protected void Invalidate(Rect rc);       // 局部脏区
    protected void RegisterForNextAnimationFrameUpdate();
    protected bool RenderClipIntersectes(Rect rc);                  // 原文拼写如此
}
// Compositor.Factories.cs:38
public CompositionCustomVisual CreateCustomVisual(CompositionCustomVisualHandler handler);
// CompositionCustomVisual.cs:21-29 —— UI 线程发消息，随下一个批次投递
public void SendHandlerMessage(object message) { ... Compositor.RequestCompositionUpdate(OnCompositionUpdate); ... }
// ElementCompositionPreview.cs:21（类名 ElementComposition）
public static void SetElementChildVisual(Visual visual, CompositionVisual? compositionVisual);
```

**渲染线程上的 Skia lease**：`ServerCompositionCustomVisual.RenderCore` 用 `new ImmediateDrawingContext(ctx.Canvas, …)` 调用 `OnRender`（`ServerCustomCompositionVisual.cs:71-92`）。`CompositorDrawingContextProxy.GetFeature` 先 `Flush()`，再转交给底层的 Skia 上下文（`DrawingContextProxy.cs:293-297`），所以在 `OnRender` 里同样拿得到 `ISkiaSharpApiLeaseFeature`。

**线程模型与帧调度** [推断：设计；所用 API 已核实]

1. UI 线程在 `OnAttachedToVisualTree` 里创建 visual，并在布局时同步 `visual.Size`（类型是 `Vector`，见 `composition-schema.xml:26`）。
2. 核心线程每次发布新快照时，递增一个 `Volatile` 代数 `FrameGeneration`。
3. handler 处于"活跃"状态时，每个 tick 在 `OnAnimationFrameUpdate` 里比较代数。有变化就 `Invalidate(dirtyRowsRect)`，然后再次 `RegisterForNextAnimationFrameUpdate()`。
4. 连续 N 帧（例如 30 帧，约 250 ms）没有变化，就停止注册，渲染循环可以休眠。
5. 从空闲唤醒的两种途径：
   - 核心线程 `Dispatcher.UIThread.Post(() => visual.SendHandlerMessage(Wake))`；
   - UI 线程在收到按键或 IME 事件时**预先**发送 `Wake`。这样按键回显的那一帧无需再绕一次 UI 线程。
6. `OnRender` 取最新快照，只重建脏行的行缓存，绘制与裁剪区相交的行。

**风险** [推断]

- `Invalidate(Rect)` 在默认的单脏矩形模式下会被合并成外接矩形。例如第 3 行和第 50 行同时变化，结果等于重画第 3 到 50 行。
- `ImmediateDrawingContext` 没有 `PushTextOptions` / `PushRenderOptions`，文字 AA 模式只能用默认值，或者在 lease 里自己控制。
- 官方文档提到的 `RequestNextFrameRendering()` 在 12.1.3 的 handler 中**不存在**（它属于 `OpenGlControlBase`）。文档的 Skia 示例还停留在 11.2 和 SkiaSharp 2.88，**以源码为准**。

### 3.5 (d) `WriteableBitmap` CPU 光栅化（foot 风格）[已核实 API]

```csharp
// Avalonia.Base/Media/Imaging/WriteableBitmap.cs:24,67
public WriteableBitmap(PixelSize size, Vector dpi, PixelFormat? format = null, AlphaFormat? alphaFormat = null);
public ILockedFramebuffer Lock();
// Skia/Avalonia.Skia/WriteableBitmapImpl.cs:122-137,170-174,189-210
public void Draw(DrawingContextImpl context, ...) { lock (_lock) { if (_image == null || !_imageValid)
    { _image = GetSnapshot(); /* SKImage.FromPixels(...)：整图拷贝 */ } context.Canvas.DrawImage(_image, ...); } }
// BitmapFramebuffer：构造时 Monitor.Enter(parent._lock)；Dispose 时 _imageValid = false
```

**代价**：每次修改后，下一次绘制都要整图 `SKImage.FromPixels` 快照；在 GPU 后端还要再整图上传一次。`Lock()` 期间渲染线程的 `Draw` 也会被阻塞。

**估算** [推断]：3200×1900×4 B ≈ 24 MB/帧，60 fps 时约 1.5 GB/s 的拷贝加上传。

**缓解方法**：

- 按行带分块，例如每 8 行一块 `WriteableBitmap`，只有脏块需要重新上传；
- 多线程按行光栅化，参考 foot；
- 在 Skia CPU 上直接用 `SKSurface.Create(info, framebuffer.Address, rowBytes)` 绘制。

**适用场景**：只适合"GPU 不可用"或"需要确定性像素输出"的场合。不推荐作为主路径。

### 3.6 其他可能但不推荐的路径

| 路径 | 不推荐的原因 |
|---|---|
| `OpenGlControlBase` | macOS 默认后端是 Metal，GL 互操作问题多 |
| `CompositionDrawingSurface.UpdateWithKeyedMutexAsync/…Semaphores…` 导入外部 GPU 图像 | 需要自己维护 D3D、Metal、Vulkan 三套渲染器（`CompositionDrawingSurface.cs:22-70`）。只有将来真的要做"Ghostty 式自有 GPU 渲染器"时才值得 |

---

## 4. 字体与字形方案

### 4.1 等宽 cell 度量（设备像素对齐）

**Avalonia 提供的度量 API** [已核实]

- `GlyphTypeface.Metrics` 是 `FontMetrics`（设计单位）：`DesignEmHeight`、`Ascent`（**负值**，在基线之上）、`Descent`（正值）、`LineGap`、`UnderlinePosition/Thickness`、`StrikethroughPosition/Thickness`、`IsFixedPitch`。其中 `LineSpacing = Descent - Ascent + LineGap`（`FontMetrics.cs:6-56`）。
- 取 advance：`GlyphTypeface.CharacterToGlyphMap.GetGlyphs(...)` 加 `TryGetHorizontalGlyphAdvances(...)`（`CharacterToGlyphMap.cs:102`；`GlyphTypeface.cs:694`）。

**算法** [推断：照搬 Ghostty `Metrics.calc`，换算到 Avalonia 的符号约定]

```csharp
// 示意代码：所有量先换算到"设备像素"再取整
double px = fontSizePt * dpi / 72;                 // TKCode 内部以 DIP 为准：px = fontSizeDip * renderScaling
double s  = px / m.DesignEmHeight;
double faceW = MaxAdvance(gt, ' '..'~') * s;       // Ghostty：ASCII 32..126 的最大 advance
double faceH = (m.Descent - m.Ascent + m.LineGap) * s;
int cellW = (int)Math.Round(faceW);                // round（Ghostty），不用 ceil
int cellH = (int)Math.Round(faceH * lineHeightFactor);
double faceBaselineFromBottom = m.LineGap * s / 2 + m.Descent * s;
int baselineFromBottom = (int)Math.Round(faceBaselineFromBottom - (cellH - faceH) / 2);
int baselineY = cellH - baselineFromBottom;        // 字形基线距 cell 顶部
int underlineThick = Math.Max(1, (int)Math.Ceiling(m.UnderlineThickness * s));
// 布局用 DIP：cellWDip = cellW / renderScaling（可以是分数），保证每条 cell 边界都落在整数设备像素上
```

**适配要点**

- 订阅 `TopLevel` 的缩放变化（RenderScaling 改变时），重新计算度量、清空行缓存和 sprite atlas。
- 网格原点也要对齐到整数设备像素：在渲染线程用 lease 的 `SkCanvas.TotalMatrix`，或者从 UI 线程把 scaling 作为消息发给 handler。

### 4.2 逐字符字体回退

| 步骤 | Ghostty 的做法 | 在 Avalonia / TKCode 中的实现 [推断] |
|---|---|---|
| 1 | `font-codepoint-map` 覆盖 | 配置项"码点范围 → 字体" |
| 2 | sprite face（程序化字符） | 先判断是否属于 §4.6 的范围 |
| 3 | 已加载字体，style 和 presentation 精确匹配 | 主字体 `CharacterToGlyphMap.TryGetGlyph`，再查已用过的回退字体 |
| 4 | 非 regular 时退回 regular | 同 |
| 5 | 系统发现，`size_adjustment = ic_width` | `FontManager.TryMatchCharacter(cp, style, weight, stretch, family, culture, out Typeface)`（`FontManager.cs:291-293`，Skia 实现最终调用 `SKFontManager.MatchCharacter`，见 `FontManagerImpl.cs:93`）。CJK 回退字体按"水"字的 advance 与 2×cellW 的比例调整 em 尺寸 |
| 6 | 最后用 U+FFFD | 同 |

**缓存与异步**

- 结果缓存为 `(codepoint, styleBits, presentation) → faceIndex`，即 Ghostty `SharedGrid.codepoints` 的 u64 键。
- **系统字体查找放到后台**，参考 WezTerm：首帧先画占位方框，查到之后递增 `fontGeneration`，让含该码点的行缓存失效。这样渲染线程不会被 fontconfig 或 CoreText 的查询卡住。
- 风险 [推断]：Avalonia 没有承诺 `FontManager` / `TextShaper` 在非 UI 线程上的线程安全（内部多用并发字典）。需要 spike 验证。如果不安全，就把字体解析和 shaping 放到专门的工作线程，渲染线程只消费结果。

### 4.3 宽字符与彩色 emoji

- **宽度由核心决定**：wcwidth 表，或 mode 2027 grapheme 宽度。渲染器只负责把字形放进 1 格或 2 格。
- **核心宽度表**：Avalonia 12.1 的 `Codepoint` 提供 `EastAsianWidthClass`、`IsEmoji`、`HasEmojiPresentation` 和 `GraphemeBreakClass`（`Codepoint.cs:64-96`）[已核实]。但终端需要与主流终端（Ghostty、xterm）一致的宽度语义，所以**建议自建三级查找表**，作为 `static ReadOnlySpan<byte>` 放进 RVA 数据段，对 AOT 友好。FFI 路线可以直接调用 `ghostty_unicode_codepoint_width/grapheme_width` [推断]。
- **emoji**：遇到 VS16 或默认 emoji 呈现时，优先选彩色 emoji 字体（Apple Color Emoji / Segoe UI Emoji / Noto Color Emoji）。按 Ghostty 的约束，以 `cover` 方式居中放进 2 格，左右各留 2.5% 内边距（`SharedGrid.zig:416-432`）。Skia 会自动绘制 COLR、CBDT 和 sbix 格式的彩色字形 [推断]。
- **宽字形溢出**：Nerd Font 图标这类宽于 1 格的字形，参考 Ghostty 的 `constraintWidth`：右邻格为空时允许占 2 格，否则按 `fit` 缩小（`cell.zig:250-291`）。

### 4.4 粗体、斜体与合成字形

- 优先使用字体族里真正的 Bold / Italic 字面，通过 `Typeface(family, style, weight)` 加 `FontManager.TryGetGlyphTypeface` 获取。
- 没有对应字面时，用 `GlyphTypeface(IPlatformTypeface, FontSimulations)`。Skia 端的实现是 `SKFont.Embolden` 加 `skewX: -0.3f`，约 16.7°（`SkiaTypeface.cs:33-40`）[已核实]。
- 作为对比，Ghostty 的斜切角度在 CoreText 上约为 15°，FreeType 上为 12°。
- 可选"粗体用亮色"（bold-is-bright）：把调色板 0–7 映射到 8–15。

### 4.5 连字与 shaping

**API** [已核实]

- `TextShaper.Current.ShapeText(ReadOnlyMemory<char>, TextShaperOptions)` 返回 `ShapedBuffer`（`TextShaper.cs:43`）。
- `TextShaperOptions` 的参数包括 `GlyphTypeface`、`FontRenderingEmSize`、`BidiLevel`、`Culture`、`IncrementalTabWidth`、`LetterSpacing` 和 `FontFeatures`（`TextShaperOptions.cs:32-61`）。
- 注意 Avalonia 12 把 text shaper 从渲染后端中解耦了，必须调用 `UseHarfBuzz()`。TKCode 已经调用了（`src/TKCode.Desktop/SmokeMode.cs:56-57`）。

**策略** [推断，综合 Ghostty、kitty、WT 的做法]

- **run 切分**：以下情况都要断开：样式变化（bg 除外）、face 变化、选区边界、**光标位置**（可配置）、spacer 格。
- **强制网格对齐**：每个 cluster 的 x 设为 `col × cellW`，advance 只用于 cluster 内部的偏移（参见 Ghostty `harfbuzz.zig:131-300` 和 WT `AtlasEngine.cpp:1173`）。
- **shaping 缓存**：键用 `hash(codepoints, 相对 cluster, faceIndex, featuresId)`，颜色不进键（参见 Ghostty `run.zig:262-287`）。
- **快路径**：关闭连字，或者 run 中全是"不参与连字的字符"时，跳过 HarfBuzz，直接用 cmap 的 `GetGlyphs` 加固定 advance 构建 `GlyphInfo`。这是 ASCII 吞吐的关键。
- **默认值**：
  - Ghostty：默认开 `liga`；用户要关编程连字时配 `-calt`。
  - kitty：`disable_ligatures` 默认 `never`。
  - WezTerm：默认开 `kern/liga/clig`。
  - Alacritty：不支持连字。

### 4.6 程序化绘制的字符（保证像素对齐）

**建议覆盖的范围**（取 Ghostty、WT、xterm.js、kitty 的并集）：

| 范围 | 内容 | 谁实现了 |
|---|---|---|
| U+2500–257F | Box Drawing | 全部 |
| U+2580–259F | Block Elements（包括 ░▒▓ 阴影） | 全部 |
| U+2800–28FF | Braille | Ghostty、xterm.js、WezTerm、foot、kitty |
| U+E0B0–E0BF（另加 E0D2、E0D4） | Powerline 几何类 | Ghostty、WT、xterm.js、WezTerm、kitty（foot 不支持） |
| U+1FB00–1FBEF | Symbols for Legacy Computing（sextant、wedge 等） | Ghostty、WT（到 1FB94）、xterm.js、kitty |
| U+1CD00–1CDE5 | Octants | Ghostty、foot、WezTerm、kitty |
| U+25E2–25E5、25F8–25FA、25FF | 几何三角 | Ghostty |
| U+EE00–EE0B | Fira Code 进度条和 spinner | xterm.js、WezTerm、kitty |
| U+F5D0–F60D | git 分支图（Kitty 扩展） | Ghostty、xterm.js、WezTerm、kitty |
| 非 Unicode | 下划线（single/double/curly/dotted/dashed）、删除线、上划线、光标（block/bar/underline/hollow） | Ghostty `special.zig` |

**实现** [推断]

- 用 SkiaSharp `SKPath` 和 `SKCanvas` 画到 **A8（Alpha8）** 位图。
- 缓存键为 `(codepoint, cellW, cellH, thickness)`，打包进一张 sprite atlas（`SKImage`）。
- 绘制时用 Alpha8 图像配合 paint 颜色着色（Skia 会把 alpha-only 图像按 paint color 着色），或者用 `SKCanvas.DrawAtlas` 带 colors 批量绘制。SkiaSharp 3.119.4 提供 `DrawAtlas`、`AllocateHorizontalRun`、`AllocatePositionedRun`（在程序集元数据中确认）。

**线宽与取整** [已核实：来源规则]

- Ghostty：base = 下划线粗细（ceil 且 ≥1）；light = base，heavy = 2 × base。
- 分数坐标：`min = s - round((1-f)·s)`，`max = round(f·s)`，保证两半块拼接时无缝。
- 盲文：点宽 = `min(w/4, h/8)`。
- foot 的另一套规则：`0.04pt × 对角线 × dpi/72`，heavy = 3 × light。
- 建议采用 Ghostty 规则，因为它与字体的下划线粗细一致。

### 4.7 下划线、删除线、上划线与超链接

- **位置和粗细**：来自 `FontMetrics.UnderlinePosition/Thickness` 和 `StrikethroughPosition/Thickness`（设计单位）。粗细 ceil 且 ≥1；位置 round。
  - Ghostty 的位置从 cell 顶部量：`top_to_baseline - underlinePosition`。
  - 字体没给度量时，用 Ghostty 的估算：下划线粗细 = 0.15 × ex height，删除线位于 `(ex + 粗细)/2`。
- **样式**：
  - double：上下各偏移一个粗细。
  - curly：Ghostty 用单周期贝塞尔曲线，振幅 `w/π`；kitty 用余弦曲线；foot 用三角波。
  - dotted：圆点。
  - dashed：每段 `w/3 + 1`。
  - 全部作为 sprite，按 cell 宽度平铺。
  - 下划线颜色来自 SGR 58。
- **绘制顺序**：bg → 下划线 → 字形 → 删除线（Ghostty）。下划线压在字形之下，可以避开下行字母的视觉冲突。
- **超链接（OSC 8）悬停**：Ghostty 的规则是，没有下划线的画单线，已有单下划线的改成双线。

### 4.8 光标、选区、最小对比度、暗淡与闪烁

| 特性 | 推荐做法 | 参照 |
|---|---|---|
| 光标形状 | block / bar（跨在两格之间，向左偏移 `(t+1)/2`）/ underline / hollow（失焦时）；遵从 DECSCUSR | Ghostty `renderer/cursor.zig:35-67`；WT `_drawCursorBackground` |
| 光标下的文字 | block 光标所在格的文字改用 `cursor-text` 色，默认取背景色 | Ghostty `cell_text.v.glsl:134-141` |
| 光标颜色兜底 | 光标颜色与格子的对比度低于 1.5 时，改用反色 | Alacritty `display/content.rs:22,124-136` |
| 闪烁 | 每 600 ms 切换一次，有输出或输入时重置（节流 500 ms）；失焦时不闪烁；由渲染线程的 `CompositionNow` 驱动 | Ghostty `Thread.zig:20`；`Termio.zig:714-725` |
| 选区 | **直接改格子颜色**，不用半透明叠加层：bg 用选区色，fg 按选区背景亮度自动取黑或白；没有配置选区色时 fg/bg 互换 | WT `AtlasEngine.cpp:333,410-484`；Ghostty `generic.zig:3112-3178` |
| 最小对比度 | 按 WCAG `(L1+0.05)/(L2+0.05)` 计算；不达标时 Ghostty 直接改成黑或白，xterm.js 每次调 10% 亮度迭代；按 `(fg,bg)` 对缓存；box、block、powerline 豁免；默认 1（关闭） | Ghostty `common.glsl:80-110`；xterm.js `Color.ts:296-384`；VS Code 取值范围 1–21 |
| faint（暗淡） | 前景 alpha 0.5，等价于在不透明背景上向背景色混合 50% | Ghostty `faint-opacity` 0.5；xterm.js `DIM_OPACITY = 0.5`；foot 用 `dim.amount 1.5` 混合 |
| 文字闪烁（SGR 5） | 可选实现：只有可见区域存在 blink 格时才启动 500 ms 定时器 | foot `terminal.c:511-512`；Ghostty 不渲染 |
| 背景 | 每行把相同 bg 合并成一次 `FillRectangle`；或者用 WT/Ghostty 的"每格 1 像素颜色位图 + 最近邻放大"，一次 `DrawImage`（lease 里用 `SKSamplingOptions(SKFilterMode.Nearest)`）完成全部背景 | WT `shader_ps.hlsl:38-44`；`BackendD2D.cpp:188-207` |

### 4.9 HiDPI、像素对齐与抗锯齿

- **对齐**：所有 cell 边界、基线、装饰线都放在整数设备像素上（§4.1）。如果用 `GlyphRun`，`BaselineOrigin` 也必须是整数设备像素。
- **字形定位**：Avalonia Skia 的 `GlyphRunImpl` 默认 `SubpixelAntialias` + `Subpixel = true` + `BaselineSnap`（`GlyphRunImpl.cs:133-170`）。终端字形本来就按整数像素放置，"亚像素定位"带来的收益有限。
- **LCD 子像素 AA**：要求背景不透明。TKCode 如果用半透明或亚克力背景，就只能用灰度 AA。
  - Ghostty 一律用灰度 AA。
  - WT 默认 ClearType。
  - 建议默认灰度，不透明背景时可选 LCD [推断]。
- **缩放变化**：重新光栅化 sprite，清空行缓存。Skia 自带的字形缓存会自动按新尺寸生成 [推断]。

---

## 5. 线程模型方案

### 5.1 三种模型对比

| 模型 | 代表 | 优点 | 缺点 | 放到 Avalonia 里 |
|---|---|---|---|---|
| **共享锁** | Ghostty（mutex + demand handoff）、Alacritty（FairMutex lease）、WT（公平 ticket lock） | 实现直接；渲染器能读到最新状态 | 必须解决锁饥饿；渲染侧要在锁内复制或 shaping | 渲染线程由**整个窗口共用**，在 `OnRender` 里等终端锁会拖慢全应用的帧。只有 try-lock 加"拿不到就沿用上一帧"才能接受 |
| **actor + 快照发布**（推荐） | contour（`RenderDoubleBuffer` try-lock 交换）、WezTerm（锁外解析产出 Action）、Ghostty RenderState 的思路 | 渲染线程**永不等锁**；核心状态单线程独占，没有数据竞争；libghostty-vt 本身也不是线程安全的，正好匹配 | 快照复制有开销，但只复制脏行；UI 发出的命令有一次线程跳转（约 10–50 µs） | 与 `CompositionCustomVisualHandler` 天然契合 |
| **单线程** | xterm.js（12 ms 时间片）、foot（epoll 单线程 + 渲染 worker） | 简单，没有锁 | UI 线程负担重；吞吐受限（xterm.js 只有 5–35 MB/s） | 不合适：TKCode 的 UI 线程还要跑 Agent UI |

### 5.2 推荐模型：每个终端两个线程，再加共用的渲染线程 [推断：设计]

**读线程**

- 阻塞读 PTY，从 4 块 × 64 KiB 的缓冲池取块。参数取自 Ghostty `Exec.zig:1306-1313`：buffer_count 小于 4 会有轻微变慢，64 KiB 既是一批解析量，也是一次持锁的工作量。
- 池里没有空闲块时，读线程阻塞。PTY 内核缓冲随之填满，子进程被反压。**没有无界队列**，对比 xterm.js 是到 50 MB 才丢数据。
- macOS 上 PTY 每次 read 只给约 1 KiB。读线程要循环读，直到 EAGAIN、满 64 KiB 或用完 3 ms 预算，参考 Ghostty `gather_budget_ns` 和 kitty `PTY_GATHER_MAX 64KiB`。这要求 fd 非阻塞，并用 `poll()`。`LibraryImport` 可以 AOT 安全地完成这些调用 [推断]。

**核心线程（actor）**

- 独占 `ITerminalCore`，只等待一个信号，来源有两个：读线程送来的数据块，以及命令队列（resize、视口滚动、选区、按键编码、粘贴、搜索）。
- 解析按批进行。每批之后检查渲染端设置的"想要帧"标志：设置了，并且 mode 2026 没有挂起，就**发布快照**。数据洪流期间，发布频率因此不超过 vsync。
- 解析完当前所有输入、准备再次等待之前，只要状态是脏的，也发布一次。
- effects（标题、铃声、OSC 52、通知、DA/DSR 应答）都在本线程同步产生。需要 UI 处理的投递给 UI 线程；应答直接写回 PTY。
- **写 PTY**：由核心线程负责，或者另开一个写线程（参考 Ghostty `termio/Thread.zig`）。fd 用非阻塞，`EAGAIN` 时排队。UI 线程**从不**阻塞在 `write()` 上。

**渲染线程（Avalonia 共用）**

- handler 在 `OnAnimationFrameUpdate` 里读 `Generation`，在 `OnRender` 里取最新快照。整个过程不加锁，只有 `Volatile` 读。

**UI 线程**

负责输入、IME、选区手势、布局与 `Size`、AutomationPeer，以及从空闲状态唤醒渲染。

### 5.3 快照的数据结构与回收

```csharp
// 示意代码（推断：设计）
public sealed class FrameSnapshot
{
    public long Generation;
    public int Cols, Rows;
    public RowSnapshot[] RowsInView;       // 未变化的行与上一快照共享同一对象，因此"只复制脏行"
    public CursorState Cursor;             // 位置、形状、是否可见、是否闪烁
    public TerminalColors Colors;          // 调色板，已处理 reverse video
    public TerminalModes Modes;            // DECCKM、2004、1004、鼠标模式、kitty 键盘 flags，供 UI 线程编码按键
    public SelectionSpan? Selection;
    public ScrollbarState Scrollbar;
}
public sealed class RowSnapshot
{
    public long RowId; public int Version; // RowId 跨滚动保持不变，相当于 Ghostty 的 Row.Id{serial,y}
    public ulong[] Cells;                  // 快照自有的 8 B/格格式：码点 21、宽度 2、行内样式索引 16、grapheme 标志等
    public ResolvedStyle[] Styles;         // 本行去重后的已解析样式：fg/bg RGB、flags、下划线样式/颜色、超链接 id
    public GraphemeSideTable? Graphemes;   // 稀疏表：col → 码点序列
    public bool SoftWrapped;
}
```

**回收**：用代际（epoch）方式。渲染线程开始使用某个快照时写入 `RenderEpoch`；核心线程回收 `RetiredAt < RenderEpoch` 的 RowSnapshot 和 FrameSnapshot，放回对象池。稳态下零分配。

**快照成本** [推断]：

- 全脏时 200×60×8 B = 96 KB 的 memcpy，加上样式解析，量级是 10–30 µs。
- 典型交互只有 1–3 行脏，量级是 1 µs。

**两种核心如何填充快照** [推断，FFI 部分基于已核实的 API]：

| | 纯 C# 核心 | libghostty-vt |
|---|---|---|
| 脏信息来源 | 自有的 Row.dirty 和页脏位，参考 Ghostty | `ghostty_render_state_update(state, terminal)` 之后用 `row_iterator_next_dirty` |
| 取 cell | 直接复制 cell 和样式 | 每行一次 `ROW_DATA_CELLS_RAW` 拿整行 u64 视图（`render.h:446-460`）。位布局**不受 ABI 保护**，启动时从 `ghostty_type_json` 的清单解析位偏移。只在 style_id 变化时调用 `ROW_CELLS_DATA_STYLE`；grapheme 用 `GRAPHEMES_LEN/BUF` |
| 收尾 | — | `ghostty_render_state_clean` |
| 每帧 FFI 调用 | — | 约（脏行数 × 常数），不是（cell 数 × 常数）。`c-vt-render` 示例里逐 cell 迭代的写法在 .NET 里会有大约 4 万次 P/Invoke / 帧，必须避免 |

### 5.4 帧调度与 mode 2026 [推断：设计；对照值已核实]

**交互路径不加合并定时器。** 依据是 §2.9 的延迟数据：kitty 默认的 3/10 ms 延迟让平均延迟变为 23.8 ms，而调优后只有 10.7 ms。

- 按键时，UI 线程立刻 `SendHandlerMessage(Wake)`，渲染端进入"活跃轮询"。
- 回显数据到达后，核心线程发布快照；下一个 vsync 渲染线程检测到代数变化，立即重画。

**洪流时**：渲染线程每个 tick 设置一次"想要帧"，核心线程在批次之间响应。效果相当于 foot 的 upper 定时器，但不另设定时器。

**mode 2026**：持有期间核心线程不发布快照，超时 **1000 ms**（与 Ghostty、foot、xterm.js 一致）。各家超时对照：Alacritty（vte）150 ms 并限 2 MiB 缓冲；kitty 2000 ms；WT 最多等 100 ms；WezTerm 无超时。规范本身没有规定超时，只说 "won't be worse than having no synchronized output"。

**光标闪烁**：由渲染线程用 `CompositionNow` 计时，周期 600 ms（Ghostty）。有输出或输入时重置相位。失焦时停止闪烁，并让渲染循环进入休眠。

**空闲判定**：连续 30 帧无变化，就停止 `RegisterForNextAnimationFrameUpdate`。10 个空闲终端的 CPU 占用应接近 0（见基准测试计划 §9 场景 S10）。

### 5.5 Resize 与 reflow

1. UI 线程只把新尺寸作为命令发给核心线程，并对连续的 resize 做合并：Ghostty 合并 25 ms，foot 在拖动期间先做截断，停 100 ms 后才真正 reflow。
2. 核心线程执行 reflow，然后发布全脏快照，最后发送 `TIOCSWINSZ` 或 `ResizePseudoConsole`。
3. reflow 期间暂停读取 PTY，参考 foot `terminal.c:264-272`。

libghostty-vt 的 reflow 会解压全部回滚页，大回滚下更需要合并（Ghostty `PageList.zig:1263-1267`）。

---

## 6. 输入、IME 与可访问性

### 6.1 KeyDown 与 TextInput 的分工 [API 已核实；策略为推断]

`KeyEventArgs` 提供 `Key`（随键盘布局变化的虚拟键）、`PhysicalKey`（物理位置）、`KeySymbol`（该键会输出的字符）、`KeyModifiers` 和 `KeyDeviceType`（`KeyEventArgs.cs:34-75`）。实现 Kitty 键盘协议时，`PhysicalKey` 和 `KeySymbol` 都要用到。

| 输入 | 事件 | 处理方式 |
|---|---|---|
| 可打印字符，包括 AltGr、死键组合结果、IME 提交 | `TextInput` | 编码成 UTF-8 直接写入。Kitty 协议在 report-all-keys 模式下例外 |
| Ctrl + 字母或符号 | `KeyDown` | legacy 编码（0x01–0x1A，Ctrl+[ 为 ESC 等）或 Kitty CSI-u。**Win32 会丢弃 <0x20 的 WM_CHAR**，所以必须在 KeyDown 里编码 |
| Alt + 键 | `KeyDown` | 加 ESC 前缀。macOS 提供"Option 当 Alt"设置，可分左右（参考 Ghostty `macos-option-as-alt`） |
| 方向键、功能键、Home/End/PgUp/PgDn | `KeyDown` | 按 DECCKM / DECKPAM 模式编码 |
| 粘贴 | 命令 | 开启 bracketed paste（mode 2004）时包裹，并做安全检查（参考 libghostty `ghostty_paste_is_safe`） |
| 焦点进入/离开 | `GotFocus` / `LostFocus` | 开启 mode 1004 时上报 |

**Win32 的关键细节** [已核实]：

```csharp
// Avalonia/src/Windows/Avalonia.Win32/WindowImpl.AppWndProc.cs:261-275, 975-988 @ 12.1.3
case WindowsMessage.WM_CHAR:
    if (Imm32InputMethod.Current.IsComposing) break;          // IME 组合期间不产生 TextInput
    if (ToInt32(wParam) >= 32 && !_ignoreWmChar) { e = new RawTextInputEventArgs(...); }
...
if (message == WindowsMessage.WM_KEYDOWN) {
    if (e is RawKeyEventArgs args && args.Key == Key.ImeProcessed) _ignoreWmChar = true;
    else _ignoreWmChar = e.Handled;                           // KeyDown 被 Handled，随后的 WM_CHAR 就被吞掉
}
```

由此得出的规则：

1. KeyDown 里**只对自己编码了的键**设置 `Handled = true`，其余放行，让 TextInput 送达文本。
2. `Key.ImeProcessed` 一律忽略。
3. AltGr 在 Windows 上表现为 Ctrl+Alt。如果此时 `KeySymbol` 是可打印字符，就当作文本处理，不按 Ctrl+Alt 组合编码。
4. 死键不处理，由平台在 TextInput 里送来组合后的字符。

### 6.2 IME（`TextInputMethodClient`）[API 已核实]

```csharp
// Avalonia.Base/Input/TextInput/TextInputMethodClient.cs @ 12.1.3（节选）
public abstract class TextInputMethodClient {
    public abstract Visual TextViewVisual { get; }
    public abstract bool SupportsPreedit { get; }
    public abstract bool SupportsSurroundingText { get; }
    public abstract string SurroundingText { get; }
    public abstract Rect CursorRectangle { get; }        // 候选框定位
    public abstract TextSelection Selection { get; set; }
    public virtual void SetPreeditText(string? preeditText, int? cursorPos);
    protected virtual void RaiseCursorRectangleChanged(); // 另有 TextViewVisualChanged、SurroundingTextChanged、SelectionChanged、ResetRequested、InputPaneActivationRequested
}
// InputElement.cs:136-347：TextInputMethodClientRequested 路由事件，在事件参数里设置 e.Client
```

- 只有 `SupportsPreedit == true` 时，各平台才会把组合串交给客户端：Win32 见 `Imm32InputMethod.cs:275-285`，macOS 见 `AvaloniaNativeTextInputMethod.cs:141`；Linux 的 IBus/Fcitx（DBus）和 Wayland text-input-v3 也都支持。返回 false 时，Windows 上由 IMM 自己绘制组合窗口 [推断]。
- 文档有出入：官方 Text Input 指南仍写着旧的 `ITextInputMethodClient` + `SetClient`，**以源码为准**。

**终端用的实现** [推断：设计]

```csharp
sealed class TerminalImeClient(TerminalView view) : TextInputMethodClient
{
    public override Visual TextViewVisual => view;
    public override bool SupportsPreedit => true;            // 预编辑文字自己画在光标处
    public override bool SupportsSurroundingText => false;   // 终端没有可编辑的上下文
    public override string SurroundingText => string.Empty;
    public override Rect CursorRectangle => view.CursorCellRect();   // DIP，相对 TextViewVisual
    public override TextSelection Selection { get => default; set { } }
    public override void SetPreeditText(string? text, int? cursorPos) => view.SetPreedit(text, cursorPos);
    public void OnCursorMoved() => RaiseCursorRectangleChanged();     // 光标格变化时触发，按帧节流
}
// TerminalView 中：TextInputMethodClientRequested += (_, e) => e.Client = _ime;
// 提交的文本从 TextInput 事件到达 → 写入 PTY；预编辑期间不写 PTY
```

**预编辑的绘制**：参考 Ghostty。

- 从光标处开始，按 wcwidth 占格，覆盖在终端内容上方，加下划线。
- 预编辑期间光标强制显示为 block（`renderer/cursor.zig:35-67`）。
- 预编辑串保存在 UI 侧，以叠加层消息的形式发给 handler，不进入终端核心。

### 6.3 可访问性 [已核实现状；方案为推断]

**Avalonia 12.1.3 现状**

- 公开的 provider 只有 `IExpandCollapseProvider`、`IInvokeProvider`、`IRangeValueProvider`、`IScrollProvider`、`ISelectionProvider`、`ISelectionItemProvider`、`IToggleProvider`、`IValueProvider`、`IRootProvider`、`IEmbeddedRootProvider`（`src/Avalonia.Controls/Automation/Provider/`）。
- **没有 `ITextProvider`**。`ITextProvider` / `ITextRangeProvider` 只是 Win32 互操作层的声明（`Avalonia.Win32.Automation/Interop/`）。
- 连 `TextBoxAutomationPeer` 也只实现了 `IValueProvider`（`TextBoxAutomationPeer.cs:6`）。
- **Live region 可用**：`AutomationProperties.LiveSetting`（`AutomationProperties.cs:239`）。在 Win32 上，peer 的 `Name` 变化且 LiveSetting 不为 Off 时，会触发 UIA `LiveRegionChanged`（`AutomationNode.cs:267-298`）。macOS 端会把 LiveSetting 透传出去（`AvnAutomationPeer.cs:65`）。Linux 端有 AT-SPI2 后端，其中包含 `AtSpiTextHandler`。

**参照系**

- **xterm.js**：屏幕外放一个 `role=list` 的行 DOM，配合 `aria-live`，每次最多读 20 行。
- **VS Code**：Accessible View（⌥F2 / Alt+F2）可以逐字、逐行浏览终端缓冲。依赖 shell integration 时，还能用 Go to Symbol 在命令之间跳转。
- **Windows Terminal**：UIA 文本提供器，加 `UiaRenderer` 按渲染刷新率批量发送通知（spec #13666）。
- **macOS Terminal**：VoiceOver 可以朗读窗口文字。

**方案（v1）**

1. `TerminalAutomationPeer : ControlAutomationPeer`
   - `ControlType = Document`，Name 设为标题。
   - 实现只读的 `IValueProvider`，Value 为视口文本（按需生成，限制长度）。
   - 实现 `IScrollProvider`，映射到回滚。
2. **播报器**：一个隐藏子元素，`LiveSetting = Polite`。
   - 核心线程把"新完成的输出行"交给 UI 线程，UI 线程按 300–1000 ms 节流后更新 Name。超过 20 行时改为摘要提示，这是 xterm.js 的做法。
   - 用户自己输入的回显不播报，参考 xterm.js `_charsToConsume`。
3. **可访问缓冲视图**：快捷键 Alt+F2，与 VS Code 一致。
   - 打开一个只读 `TextBox`，内容是回滚的纯文本。它自带平台文本无障碍，屏幕阅读器可以逐字、逐行浏览。
   - 有 shell integration（OSC 133）时，支持在命令之间跳转。
4. 提供 `minimumContrastRatio` 设置，范围 1–21，默认 1。检测到屏幕阅读器时，自动启用播报。
5. **长期**：要做完整的 UIA TextPattern 和 NSAccessibility 文本支持，就得向 Avalonia 贡献跨平台的 `ITextProvider`，或者写平台专用代码。见 §10 决策点 3。

---

## 7. 推荐架构

### 7.1 总体结构

```
                 ┌──────────────── PTY（openpty / ConPTY）────────────────┐
                 │                                                          │
  [读线程] read()/poll，4×64 KiB 池（满即阻塞 = 背压；macOS 需 gather）   [写] ← 核心线程（按键、粘贴、DA/DSR 应答）
                 │ Chunk
                 ▼
  [核心线程 = actor] 独占 ITerminalCore（ManagedCore 或 GhosttyVtCore）
     · 按批（≤64 KiB）解析与执行      · 命令：resize / 视口滚动 / 选区 / 搜索 / 按键编码
     · effects → UI：标题 / 铃声 / OSC 52 / 通知     · mode 2026 hold（1 s 超时）
     · 发布 FrameSnapshot：只复制脏行，未变行共享 → Volatile 交换 + Generation++
                 │                                                 ▲ Wake（只在从空闲唤醒时）
                 ▼                                                 │
  [Avalonia 渲染线程] TerminalVisualHandler : CompositionCustomVisualHandler
     · OnAnimationFrameUpdate：比较 Generation 或光标闪烁相位 → Invalidate(rect)；空闲 30 帧后休眠
     · OnRender：RowCache（按 RowId+Version 和 fontGeneration）→
         ① 背景 / sprite / 下划线（Skia lease） ② 文字：ImmediateDrawingContext.DrawGlyphRun
         ③ 删除线 / 光标 / 预编辑 / 选区（lease）
                 ▲ Size、缩放、主题、预编辑等消息（SendHandlerMessage）
  [UI 线程] TerminalView : Control —— 布局、键盘/IME/鼠标、选区手势、AutomationPeer、可访问缓冲视图
```

### 7.2 组件要点

**1. `ITerminalCore`**（两种实现共用一个接口）[推断：设计]

```csharp
public interface ITerminalCore : IDisposable          // 只允许核心线程调用
{
    void Write(ReadOnlySpan<byte> data);
    void Resize(int cols, int rows, int cellWidthPx, int cellHeightPx);
    void ScrollViewport(in ViewportScroll request);
    void UpdateSelection(in SelectionGesture gesture);  string? GetSelectionText();
    int  EncodeKey(in KeyInput key, Span<byte> destination);   // legacy 或 Kitty 协议
    bool SynchronizedOutputHold { get; }                       // mode 2026
    void CaptureDirty(FrameSnapshotBuilder builder);           // 只复制脏行，然后清除脏标记
    void CompactIdle(TimeSpan budget);                          // 冷页压实或压缩
}
```

- **`ManagedCore`**：
  - 页式存储，每页约 256–512 KB 的 `ulong` cell 加行头。页面放在 `NativeMemory.AlignedAlloc`，或者 pinned 的 `GC.AllocateUninitializedArray`，GC 不需要扫描。
  - 每页有自己的样式集（u16 id，带引用计数），grapheme 走旁路存储。
  - 行使用环形索引。
  - 冷页压实成"UTF-8 文本 + 样式游程"（WezTerm 式），或者用 `System.IO.Compression`（Brotli 或 Deflate 的 span API，AOT 安全）压缩。
- **`GhosttyVtCore`**：
  - 用 `[LibraryImport]` + `DisableRuntimeMarshalling` 声明调用，通过 NativeAOT `DirectPInvoke` 静态链接 `libghostty-vt.a`。zig 可以交叉编译到 6 个 RID [推断]。
  - 创建后立刻设置 `SCROLLBACK_MAX_BYTES`。
  - RENDER_HOLD 回调交给核心线程计时（1 s 超时）。
  - 空闲 250 ms 后调用 `ghostty_terminal_compress`。
  - 锁定一个 commit，并用一层薄适配器隔离 API 变化。
  - 生态现状：
    - [摘要] 已有第三方 .NET 绑定，如 NuGet `LibGhostty.Net` 1.0.2、`DeBlasis.GhosttyVt`（deblasis/libghostty-vt-dotnet 有每日 CI 构建）。可以参考它们的构建脚本，但不建议直接依赖。
    - [已核实] 上游至今没有打版本 tag。2026-04 起 Ghostty 宣布迁出 GitHub，GitHub 上保留只读镜像，所以源码获取方式要预留调整空间。

**2. 解析快路径**（`ManagedCore`）[推断：设计；API 已核实存在于 .NET 10 参考程序集]

```csharp
// Ground 状态：先找"非可打印 ASCII"的第一个位置（向量化），整段批量写入
int run = input.IndexOfAnyExceptInRange((byte)0x20, (byte)0x7E);   // -1 表示整段都是可打印 ASCII
if (run < 0) run = input.Length;
PrintAscii(input[..run]);   // 有效列内：cells[x+i] = template | b[i]（可以用 Vector256.Widen 分三级加宽到 ulong）
// 遇到 ≥0x80：Utf8 解码。.NET 没有 UTF-8→UTF-32 的批量 API，
// 逐个 Rune.DecodeFromUtf8(src, out Rune r, out int n)（返回 Done / NeedMoreData / InvalidData）；
// 跨块截断时保留尾部字节（等价于 Utf8.ToUtf16 的 isFinalBlock:false）
// 遇到 ESC：CSI 快路径（ESC [ 参数 终止符）绕过通用状态机，参考 xterm.js 和 Ghostty
```

- Unicode 宽度：三级查找表，`static ReadOnlySpan<byte>` RVA 数据，≤0xFF 直接返回 1（参考 Ghostty）。
- print 快路径的条件：非插入模式、wraparound 开启、目标格"简单"（没有 grapheme、没有超链接、不是宽字符）。参考 Ghostty `printSliceFast`。

**3. 渲染**：§3.4、§4。

**4. 输入、IME、可访问性**：§6。

### 7.3 数字目标

全部为 [推断：目标值]。"依据"一列是同类终端的已核实数据。

| 指标 | 目标 | 依据 / 说明 |
|---|---|---|
| 核心解析吞吐，ASCII，关闭渲染（2026 抑制） | ManagedCore ≥ 300 MB/s；GhosttyVtCore ≥ 500 MB/s | kitty 0.33：121.8 MB/s；Ghostty 的 SIMD ASCII 接近 memcpy |
| dense SGR（vtebench `dense_cells` 类） | ≥ 100 MB/s | kitty CSI：59.8 MB/s |
| Unicode / CJK | ≥ 100 MB/s | kitty Unicode：105 MB/s |
| 端到端 `cat`（开启渲染） | ≥ 关闭渲染时的 0.8 倍；150 MB ASCII ≤ 1 s | [摘要] Ghostty nightly 575 ms、Alacritty 1.2 s |
| 渲染线程每帧（200×60，2×） | 全量重画 p99 ≤ 2 ms；1–3 个脏行 ≤ 0.3 ms | 120 Hz 一帧 8.33 ms，并且要和应用其他内容共用 |
| UI 线程每帧 | ≤ 0.1 ms（只做输入和唤醒） | 每帧绘制不经过 UI 线程 |
| 快照发布 | 全脏 ≤ 50 µs；渲染线程等锁时间 = 0 | Ghostty 1.3：多数帧根本不持锁 |
| 输入延迟（自身附加） | KeyDown 到 Present 提交 p50 ≤ 1 ms，另加等待 vsync；p99 ≤ 1 帧 | — |
| 输入延迟（端到端 Typometer） | 平均值不超过同机 Alacritty 或 Ghostty + 2 ms | beuke：alacritty 平均 6.9 ms；xterm 5.3 ms |
| 回滚内存（每 1 万行 × 120 列） | 未压实 ≤ 10 MB；压实或压缩后 ≤ 3 MB | Ghostty 8 B/cell ≈ 9.7 MB；LZ4 后 10–30% |
| reflow | 1 万行 ≤ 15 ms；10 万行 ≤ 150 ms；在核心线程上执行，不阻塞 UI 和渲染 | 各家都是 O(回滚) 同步执行 |
| 空闲 CPU | 10 个空闲终端（光标闪烁中）合计 < 0.5% | 失焦时停止闪烁，渲染循环休眠 |
| GC | 解析和绘制热路径稳态零分配；洪流下 Gen0 ≤ 1 次/s | 对象池加代际回收 |

---

## 8. 可选路线及取舍

### 8.1 渲染宿主路线

| 路线 | 渲染宿主 | 优点 | 缺点 | 定位 |
|---|---|---|---|---|
| **R1（推荐）** | (c) `CompositionCustomVisual`，文字用 `GlyphRun`，背景和 sprite 用 Skia lease | 帧与 UI 线程解耦；局部失效；延迟最低 | 复杂度最高；依赖 `[Unstable]` 的 lease；从空闲唤醒要绕 UI 线程；文档不全 | 正式版 |
| R2 | (b) `ICustomDrawOperation` | 绘制代码与 R1 共用一套 Painter；比 R1 简单 | 每帧经过 UI 线程 | 作为 R1 的回退开关 |
| R3（MVP） | (a) `Control.Render` + `GlyphRun` | 只用稳定 API；落地最快；与 CodeView 同构 | 帧率与 UI 线程耦合；整个控件重画 | 第一个可用版本，用来验证核心 |
| R4 | (d) CPU 光栅化到 `WriteableBitmap` | 像素输出确定；不依赖 GPU | 带宽开销大；HiDPI 下慢 | 只做软件兜底或截图 |
| R5 | 自有 GPU 渲染器（通过 `CompositionDrawingSurface` 导入 Metal/D3D/Vulkan 纹理） | 理论性能最高（Ghostty/WT 式实例化 quad） | 要写三套图形 API；AOT 和互操作都复杂 | 不建议 |

**推进顺序** [推断]：

1. 先写一套与宿主无关的 `TerminalPainter`。它的输入是快照加行缓存，输出是对 `ImmediateDrawingContext` 和 lease 的调用。
2. 用 R3 跑通核心和渲染的正确性。
3. 切换到 R1，并保留 R2 作为开关。

(a) 用的是 `DrawingContext`，(b)(c) 用的是 `ImmediateDrawingContext`。两者的 API 很接近，用一个薄接口就能统一。

### 8.2 次要取舍

| 议题 | 选项 | 建议 [推断] |
|---|---|---|
| 连字默认值 | 开（Ghostty、kitty、WezTerm）/ 关（Alacritty 不支持） | 默认**关**，保住 ASCII 快路径的吞吐；设置里提供"开启，并在光标处断开" |
| 文字抗锯齿 | 灰度（Ghostty）/ LCD（WT 默认 ClearType） | 默认灰度，与透明或亚克力背景兼容；背景不透明时可选 LCD |
| 背景绘制 | 每行 RLE `FillRectangle` / 每格 1 像素位图配最近邻放大（WT、Ghostty） | 先用 RLE（简单），基准测试显示是瓶颈时再换位图 |
| 回滚计量 | 行（多数终端）/ 字节（Ghostty） | 对外暴露行数，内部按页上限执行；默认 10,000 行（同 Alacritty） |
| 冷数据 | 不压缩 / 行压实（WezTerm）/ 通用压缩（Ghostty LZ4） | ManagedCore 先做行压实，可选 Brotli；GhosttyVtCore 用库自带的压缩 |
| 每个终端的线程数 | 读线程 + 核心线程（推荐）/ 合并成一个（Alacritty 式） | 分开：macOS 的 gather 和解析互不阻塞。终端很多时可以改成共享核心线程池（每个 actor 一个串行队列） |
| 程序化字形范围 | 最小集（2500–259F）/ 全集（§4.6） | 全集。只依赖路径绘制，工作量可控，可以参考 WT 每字符 4 条指令的编码，或 xterm.js 的路径定义 |
| SGR 5 闪烁 | 不渲染（Ghostty）/ 渲染（foot） | 可选实现，默认开，只在屏幕上有 blink 格时才计时 |

---

## 9. 基准测试计划

### 9.1 工具与方法

- **微基准：BenchmarkDotNet**，`[MemoryDiagnoser]` 加 `[DisassemblyDiagnoser]`。
  - **JIT 和 NativeAOT 两种工具链都要跑**：产品发布的是 AOT 版本，而 AOT 没有动态 PGO。
  - AOT 的向量路径要单独核实。NativeAOT 默认指令集基线较低，`Vector256/512` 路径可能落到回退实现，需要按 RID 评估 `IlcInstructionSet` [推断]。
- **数据集**（录制成字节文件，所有测试使用同一份）：
  - vtebench 的生成器：`dense_cells`、`light_cells`、`medium_cells`、`cursor_motion`、`scrolling*`、`unicode`、`sync_medium_cells`；
  - Ghostty devlog 用过的 5.4 MB 日文文本；
  - enwik8（WT 用过）；
  - 拼接的大源码文件（100 MB）；
  - `seq 1 10000000`；
  - truecolor 渐变生成器；
  - 用 `script` 或 asciinema 录制的 vim 滚动、htop/btop 60 s、emoji ZWJ 序列。
- **端到端：在真实窗口内运行**。
  - 内置计数器用 `EventSource` 或 `EventCounters`（AOT 安全），记录：已解析字节、发布的快照数、渲染帧数、`OnRender` 耗时的 p50/p95/p99、快照耗时、空闲状态、GC 次数。
  - Avalonia 合成器自带 FPS 和帧时间叠加层（`Server/FpsCounter.cs`、`FrameTimeGraph.cs`），可以辅助观察。
- **延迟**：
  - Typometer：纯软件，可用于 macOS、Windows、X11，与 beuke.org 和 Zutty 的测法可比。
  - 内部分段时间戳：KeyDown → 写 PTY → 回显被解析 → 快照发布 → `OnRender` 结束。
  - 可选：240 fps 高速摄像抽查，参考 Chad Austin 和 vizenc 的方法。
- **对照组**：同机运行 Ghostty、Alacritty、kitty、WezTerm，Windows 上加 WT，macOS 上加 Terminal.app 和 iTerm2。只比较相对值，不比较绝对值。
- **Headless 回归**：用 TKCode 现有的 `HeadlessApp`（`UseSkia` + `UseHeadlessDrawing=false`）做像素快照测试，覆盖 box drawing 拼接、光标形状、宽字符、emoji、下划线样式和 HiDPI 缩放。这种模式只测 CPU 侧的帧构建耗时，不代表 GPU 性能。

### 9.2 场景

| # | 场景 | 输入或命令 | 测量 | 通过线 |
|---|---|---|---|---|
| S1 | `cat` 大文件 | 100 MB ASCII 源码；150 MB 纯 ASCII | 核心吞吐（BDN，关闭渲染）；端到端耗时（含渲染） | ≥ 300 MB/s；端到端 ≥ 0.8× |
| S2 | `seq` | `seq 1 10000000`（约 78 MB，行很短） | 每行开销：滚动、换行、整行清除 | ≥ 200 MB/s |
| S3 | 彩色密集 | vtebench `dense_cells`、`medium_cells`；truecolor 渐变 | SGR 解析和样式去重；页样式表溢出路径 | ≥ 100 MB/s，无分配 |
| S4 | 全屏刷新 | vim 滚动录制；htop/btop 运行 60 s；`sync_medium_cells` | 每帧脏行数、`OnRender` p99、mode 2026 下是否撕裂 | p99 ≤ 2 ms；无撕裂 |
| S5 | Unicode 和 emoji 密集 | 5.4 MB 日文文本；emoji ZWJ 序列和旗帜；组合字符 | 宽度表、grapheme 处理、回退字体首帧（不阻塞）、彩色字形 | ≥ 100 MB/s；回退查找不阻塞渲染 |
| S6 | 光标移动 | vtebench `cursor_motion`；`scrolling_*_region` | CSI 快路径；区域滚动 | 不低于 Alacritty 的同项结果 |
| S7 | resize 与 reflow | 回滚 1 万行和 10 万行，宽度在 80↔200 之间每 16 ms 切换一次，持续 5 s | reflow 耗时、UI 帧时间、PTY 暂停时长 | 1 万行 ≤ 15 ms，10 万行 ≤ 150 ms；UI 不掉帧 |
| S8 | 输入延迟 | Typometer：空闲时和后台有 `yes` 洪流时各测一次 | min/avg/max、自身附加分段 | 平均值 ≤ 对照组 + 2 ms |
| S9 | 内存 | 1 万、10 万行的纯文本、彩色、CJK 三类内容 | 托管堆、原生页、压实后的大小 | 每 1 万行 × 120 列 ≤ 10 MB，压实后 ≤ 3 MB |
| S10 | 空闲 | 10 个终端空闲，光标闪烁，持续 10 min | CPU、唤醒次数 | < 0.5% CPU |
| S11 | 背压 | `yes` 运行 30 s 后按 Ctrl-C（xterm.js 指南推荐的测法） | 内存是否有界；Ctrl-C 到提示符的时间 | 内存平稳；≤ 100 ms |
| S12 | 与 Agent UI 共存 | 终端洪流的同时，StreamMark 流式渲染长 Markdown | 两边的帧时间 | 终端帧率不受 UI 线程负载影响（R1 的核心卖点） |

### 9.3 CI 门禁

- **每次 PR**：BenchmarkDotNet 的短作业（`--job short`）只跑 S1、S3、S5 的核心吞吐和快照成本，与基线 JSON 比较，回退超过 10% 即判失败（建议在固定硬件上运行）。
- **每晚**：完整的 S1–S12。结果写入 `artifacts/bench/`，保留趋势。

---

## 10. 需要用户决策的点

1. **渲染宿主与非稳定 API**：选 R1（`CompositionCustomVisual` 渲染线程绘制）还是 R3（`Control.Render`，UI 线程驱动）？
   - R1 帧与 UI 线程解耦，延迟和流畅度最好，但复杂度高，并依赖 `[Unstable]` 的 `ISkiaSharpApiLeaseFeature`。StreamMark 已经在用这个 API，Avalonia 小版本升级时可能需要跟进。
   - R3 简单、稳定，但 Agent 大量流式输出时，终端会和 UI 线程争用。
   - 建议：先做 R3 作为 MVP，再切换到 R1。
2. **回滚的内存预算与压缩**：
   - 默认回滚按行算（建议 1 万行）还是按字节算（Ghostty 默认 50 MB）？
   - 是否在第一版就做冷页压实或压缩？压实后 1 万行约 1–3 MB，不压实约 10 MB。压实带来的代价是：搜索、reflow、视图回看时需要按需解压，代码复杂度也会上升。
   - 选 libghostty-vt 时，这项改为配置字节上限，再加压缩调度。
3. **可访问性做到什么程度**：Avalonia 12.1.3 没有跨平台的 `ITextProvider`。
   - v1 方案：LiveSetting 播报 + 可访问缓冲视图（只读 TextBox）+ `IValueProvider`。
   - 完整方案：屏幕阅读器可以在终端里逐字导航（UIA TextPattern / NSAccessibility 文本）。这需要给 Avalonia 贡献补丁或写平台专用代码，工作量可能以周计。

另有一项与本路线相关，但应在核心选型报告中决策：纯 C# 核心还是 libghostty-vt。从本路线看，两种都能接到同一个快照接口上。差别在于：

- libghostty-vt：吞吐、内存、压缩和键盘协议都更成熟，但 API 不稳定、需要原生构建，回滚默认值也要设置。
- 纯 C# 核心：完全可控，但 VT 正确性和性能都要自己补。

---

## 11. 风险与待验证的 spike

| # | 风险 | 验证方法 |
|---|---|---|
| 1 | `FontManager`、`TextShaper` 和 `GlyphRun.TryCreateImmutableGlyphRunReference` 在渲染线程上调用是否线程安全（Avalonia 没有承诺） | 在压力下从多线程调用，并比对结果；如果不安全，就把字体解析和 shaping 移到工作线程 |
| 2 | `CompositionCustomVisualHandler` 自轮询的开销，以及空闲休眠和唤醒的行为 | 跑 S10 和 S8 |
| 3 | 默认的单脏矩形合并下，局部重画的实际收益；开启 `UseRegionDirtyRectClipping` 的副作用 | 跑 S4，比较开关前后的结果 |
| 4 | GlyphRun 在各后端（Metal、ANGLE、GLX、Software）下的每帧成本 | 4 个后端 × 200×60 全量重画 |
| 5 | NativeAOT 下向量路径能否启用（指令集基线问题） | 跑 BDN 的 AOT 作业，并检查反汇编 |
| 6 | libghostty-vt 能否静态链接进 6 个 RID 的 AOT 产物，以及每帧的 FFI 成本 | 用 zig 交叉编译，配合 `DirectPInvoke`，再跑 S1 和 S4 |
| 7 | Headless 下 `CompositionCustomVisual` 能否参与 `CaptureRenderedFrame` | 写一个最小快照测试 |

---

## 12. 来源列表

### 12.1 源码（本地浅克隆，只读）

- **Ghostty** `@ b3dc3d7`：
  - 页与 cell：`src/terminal/page.zig`、`PageList.zig`、`size.zig`；样式：`style.zig`、`ref_counted_set.zig`；`hyperlink.zig`；压缩与内存：`compress*.zig`、`mem.zig`。
  - 渲染状态与解析：`render.zig`、`stream.zig`、`Parser.zig`、`Terminal.zig`、`Screen.zig`、`modes.zig`；`src/simd/vt.zig`、`vt.cpp`、`index_of.zig`、`codepoint_width.zig`；`src/unicode/main.zig`。
  - IO 线程：`src/termio/Exec.zig`、`Termio.zig`、`Thread.zig`、`stream_handler.zig`。
  - 渲染器：`src/renderer/generic.zig`、`Thread.zig`、`State.zig`、`cell.zig`、`cursor.zig`、`size.zig`、`metal/shaders.zig`；着色器 `shaders/glsl/*.glsl`。
  - 字体：`src/font/Metrics.zig`、`Atlas.zig`、`SharedGrid.zig`、`SharedGridSet.zig`、`Collection.zig`、`CodepointResolver.zig`、`discovery.zig`、`shaper/run.zig`、`shaper/Cache.zig`、`shaper/harfbuzz.zig`、`face/coretext.zig`、`face/freetype.zig`、`sprite/Face.zig`、`sprite/draw/*.zig`。
  - 配置：`src/config/Config.zig`。
  - libghostty-vt：`include/ghostty/vt.h`、`include/ghostty/vt/{terminal,render,screen}.h`、`src/lib_vt.zig`、`example/c-vt-render/src/main.c`、`README.md`。
- **Alacritty** `@ 5421470`：`alacritty_terminal/src/grid/{storage,mod,row,resize}.rs`、`term/{cell,mod}.rs`、`event_loop.rs`、`sync.rs`；`alacritty/src/display/{mod,damage,content}.rs`、`renderer/text/{atlas,glyph_cache,builtin_font,glsl3,gles2}.rs`、`config/scrolling.rs`、`event.rs`、`scheduler.rs`。
- **vte** `@ abeae76`（0.15.0）：`src/lib.rs:593-620`、`src/ansi.rs:36-42`。
- **WezTerm** `@ e2a98e1`：`wezterm-cell/src/lib.rs`、`wezterm-surface/src/line/{line,storage,clusterline}.rs`、`cellcluster.rs`、`term/src/screen.rs`、`terminal.rs`、`terminalstate/performer.rs`、`wezterm-gui/src/termwindow/{mod.rs,render/*.rs}`、`glyphcache.rs`、`shapecache.rs`、`customglyph.rs`、`wezterm-font/src/{lib,shaper/harfbuzz}.rs`、`mux/src/{lib,localpane}.rs`、`config/src/config.rs`、`window/src/bitmaps/atlas.rs`。
- **Windows Terminal** `@ 44b440b`：
  - 缓冲区：`src/buffer/out/{Row.hpp,Row.cpp,textBuffer.hpp,textBuffer.cpp,TextAttribute.hpp,TextAttribute.cpp,TextColor.h}`。
  - AtlasEngine：`src/renderer/atlas/{README.md,AtlasEngine.cpp,AtlasEngine.r.cpp,BackendD3D.h,BackendD3D.cpp,BackendD2D.cpp,BuiltinGlyphs.h,BuiltinGlyphs.cpp,common.h,shader_ps.hlsl,dwrite_helpers.cpp}`。
  - 渲染线程：`src/renderer/base/{renderer.cpp,RenderEngineBase.cpp}`。
  - 解析与连接：`src/terminal/parser/stateMachine.cpp`、`src/types/utils.cpp`、`src/cascadia/TerminalConnection/ConptyConnection.cpp`、`src/cascadia/TerminalCore/Terminal.{hpp,cpp}`、`src/inc/til/ticket_lock.h`。
- **xterm.js** `@ 991857a`：`src/common/buffer/{BufferLine,Constants,Buffer,BufferReflow}.ts`、`src/common/CircularList.ts`、`src/common/input/{WriteBuffer,TextDecoder}.ts`、`src/common/parser/EscapeSequenceParser.ts`、`src/common/InputHandler.ts`、`src/browser/services/RenderService.ts`、`RenderDebouncer.ts`、`src/browser/AccessibilityManager.ts`、`addons/addon-webgl/src/{WebglRenderer,GlyphRenderer,RectangleRenderer,RenderModel,TextureAtlas}.ts`、`customGlyphs/*`、`src/common/services/OptionsService.ts`。
- **foot** `@ cb27717`：`terminal.h`、`terminal.c`、`grid.{h,c}`、`render.c`、`shm.c`、`box-drawing.c`、`composed.{h,c}`、`vt.c`、`csi.c`、`config.c`、`doc/benchmark.md`、`doc/foot.ini.5.scd`、`INSTALL.md`、`README.md`。
- **kitty** `@ f426c08`：`kitty/{line.h,history.c,text-cache.c,line-buf.c,simd-string.c,simd-string-impl.h,child-monitor.c,vt-parser.c,screen.c,shaders.c,fonts.c,decorations.c}`、`kitty/shaders/cell.slang`、`kitty/options/definition.py`、`docs/performance.rst`、`tools/cmd/benchmark/main.go`。
- **contour** `@ 8f3a4a4`：`src/vtbackend/grid/LineSoA.hpp`、`Line.hpp`、`CellProxy.hpp`、`render/RenderBuffer.{hpp,cpp}`、`Terminal.cpp`、`src/vtrasterizer/{TextureAtlas,TextRenderer,BoxDrawingRenderer}.*`。
- **Avalonia** tag `12.1.3`（`8eeda4f`）：
  - Composition：`src/Avalonia.Base/Rendering/Composition/{CompositionCustomVisualHandler,CompositionCustomVisual,Compositor.Factories,ElementCompositionPreview,CompositionOptions,CompositionDrawingSurface}.cs`、`Server/{ServerCustomCompositionVisual,ServerCompositionTarget,ServerCompositionDrawListVisual,ServerCompositor,DrawingContextProxy}.cs`、`Drawing/{RenderDataDrawingContext,RenderDataStream.Replay}.cs`、`composition-schema.xml`。
  - 渲染循环与绘制：`src/Avalonia.Base/Rendering/{IRenderTimer,IRenderLoop,RenderLoop}.cs`、`SceneGraph/CustomDrawOperation.cs`；`src/Avalonia.Base/Media/{DrawingContext,ImmediateDrawingContext,GlyphRun,GlyphTypeface,FontMetrics,FontManager,TextOptions,FontSimulations,MediaContext.Clock,MediaContext.Compositor}.cs`、`Media/TextFormatting/{TextShaper,TextShaperOptions,ShapedBuffer,GlyphInfo}.cs`、`Media/TextFormatting/Unicode/Codepoint.cs`、`Media/Fonts/Tables/Cmap/CharacterToGlyphMap.cs`、`Media/Imaging/WriteableBitmap.cs`。
  - 输入：`src/Avalonia.Base/Input/{KeyEventArgs,InputElement}.cs`、`Input/TextInput/*.cs`。
  - Skia：`src/Skia/Avalonia.Skia/{ISkiaSharpApiLeaseFeature,DrawingContextImpl,GlyphRunImpl,WriteableBitmapImpl,SkiaTypeface,FontManagerImpl}.cs`。
  - 平台：`src/Windows/Avalonia.Win32/{WindowImpl.AppWndProc.cs,Input/Imm32InputMethod.cs,Win32PlatformOptions.cs}`、`src/Avalonia.Native/{AvaloniaNativeTextInputMethod,AvaloniaNativePlatformExtensions,AvnAutomationPeer}.cs`、`src/Avalonia.X11/X11Platform.cs`。
  - 可访问性：`src/Avalonia.Controls/Automation/{AutomationProperties.cs,Provider/*,Peers/*}`、`src/Windows/Avalonia.Win32.Automation/AutomationNode.cs`、`src/Avalonia.FreeDesktop.AtSpi/Handlers/*`。
  - Headless 与依赖版本：`src/Headless/Avalonia.Headless/{AvaloniaHeadlessPlatform,HeadlessPlatformRenderInterface}.cs`、`Directory.Packages.props`。
- **TKCode** `@ dfd3657`：`src/TKCode.Desktop/Controls/CodeView.cs`、`src/TKCode.Desktop/TKCode.Desktop.csproj`、`src/TKCode.Desktop/SmokeMode.cs`、`tests/TKCode.Desktop.Tests/HeadlessApp.cs`。
- **StreamMark** `@ 074f7ff`：`src/StreamMark.Mermaid/SkiaPictureDiagram.cs`、`src/StreamMark.Avalonia/Rendering/TextSegmentView.cs`、`src/StreamMark.Avalonia/Transcript/TranscriptPanel.cs`。
- **.NET 10 参考程序集**（`Microsoft.NETCore.App.Ref/10.0.9` 的 XML 文档）：`Utf8.{IsValid,ToUtf16,FromUtf16}`、`Ascii.{IsValid,ToUtf16}`、`SearchValues.Create(ReadOnlySpan<byte>)`、`MemoryExtensions.IndexOfAnyExcept(SearchValues)`、`IndexOfAnyExceptInRange`、`Rune.DecodeFromUtf8`、`Vector128.Widen/WidenLower`、`Vector512`。
- **SkiaSharp 3.119.4**（nuget 缓存中 DLL 的元数据）：`SKTextBlobBuilder.AllocateHorizontalRun/AllocatePositionedRun/AllocateRawPositionedRun`、`SKCanvas.DrawAtlas`、`IsTrimmable`。

### 12.2 网页（检索日期 2026-10-16）

- vtebench：https://github.com/alacritty/vtebench
- Ghostty：
  - Devlog 006（2024-02-12）：https://mitchellh.com/writing/ghostty-devlog-006
  - "Everyone Should Know SIMD"（2026-07-22）：https://mitchellh.com/writing/everyone-should-know-simd
  - 1.3.0 release notes（2026-03-09）：https://ghostty.org/docs/install/release-notes/1-3-0
  - libghostty-vt 文档：https://libghostty.tip.ghostty.org/
  - "Libghostty Is Coming"（2025-09-22）：https://mitchellh.com/writing/libghostty-is-coming（路径按站点惯例推断，未逐字核对）
  - "Ghostty Is Leaving GitHub"（2026-04-28）：GitHub 上会保留只读镜像（据网页检索）
- 第三方横测：https://github.com/vizenc/terminal-benchmark
- [摘要] Ghostty 150 MB `cat` 转述：https://digg.com/ai/tfu92ilx
- kitty performance：https://sw.kovidgoyal.net/kitty/performance/
- foot benchmark（2022-05-12）：https://codeberg.org/dnkl/foot/raw/branch/master/doc/benchmark.md
- Windows Terminal：
  - AtlasEngine PR #11623：https://github.com/microsoft/terminal/pull/11623
  - ConPTY PR #17510：https://github.com/microsoft/terminal/pull/17510
  - WT 1.22（2024-08-27）：https://devblogs.microsoft.com/commandline/windows-terminal-preview-1-22-release
  - WT 1.25（2026-03-05）：https://devblogs.microsoft.com/commandline/windows-terminal-preview-1-25-release/
  - UIA 通知 spec：microsoft/terminal #13666（2022-11-03，作者 Carlos Zamora；具体 URL 未核实）
- xterm.js：
  - flow control：https://xtermjs.org/docs/guides/flowcontrol/
  - screenReaderMode：https://xtermjs.org/docs/api/terminal/interfaces/iterminaloptions/
- 延迟测量：
  - beuke.org（2024-03）：https://beuke.org/terminal-latency/
  - Zutty（2021-01）：https://tomscii.sig7.se/2021/01/Typing-latency-of-Zutty
  - Zutty 吞吐（2020-12）：https://tomscii.sig7.se/2020/12/A-totally-biased-comparison-of-Zutty
  - Dan Luu（2017-07）：https://danluu.com/term-latency/
  - Chad Austin（2024-02）：https://chadaustin.me/2024/02/windows-terminal-latency/
- Synchronized output 规范：https://github.com/contour-terminal/vt-extensions/blob/master/synchronized-output.md
- VS Code 无障碍：https://code.visualstudio.com/docs/configure/accessibility/accessibility
- macOS Terminal 与 VoiceOver：https://support.apple.com/guide/terminal/trml1020/mac
- Avalonia：
  - custom rendering（2026-07-21）：https://docs.avaloniaui.net/docs/graphics-animation/custom-rendering
  - text input（2026-03-09）：https://docs.avaloniaui.net/docs/input-interaction/text-input
  - TextInputMethodClient API：https://docs.avaloniaui.net/api/avalonia/input/textinput/textinputmethodclient
  - 12 breaking changes：https://docs.avaloniaui.net/docs/avalonia12-breaking-changes
  - 12.0 博文（2026-04-07）：https://avaloniaui.net/blog/avalonia-12
  - 12.1 博文（2026-07-08）：https://avaloniaui.net/blog/release-12-1
  - accessibility：https://docs.avaloniaui.net/docs/app-development/accessibility
- .NET：
  - Utf8：https://learn.microsoft.com/en-us/dotnet/api/system.text.unicode.utf8?view=net-10.0
  - Rune.DecodeFromUtf8：https://learn.microsoft.com/en-us/dotnet/api/system.text.rune.decodefromutf8?view=net-10.0
  - Ascii：https://learn.microsoft.com/en-us/dotnet/api/system.text.ascii?view=net-10.0
  - SearchValues：https://learn.microsoft.com/en-us/dotnet/api/system.buffers.searchvalues-1?view=net-10.0
  - Performance Improvements in .NET 10（2025-09-10）：https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-10/
