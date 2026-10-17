# TKCode 终端：渲染（TKCode.Terminal.Avalonia）

> 状态：设计稿 v1（2026-10-16）。上游约束见 [01-architecture.md](01-architecture.md)（D5、§5.2 线程、§6.1 FrameSnapshot、§6.5 TerminalView）；依据见 [research/04](research/04-rendering-performance.md)、[research/05](research/05-platform-behavior-config.md) §7、[research/03](research/03-vt-protocol.md) §6。输入侧见 [06-input.md](06-input.md)。
>
> **来源标注**：**[已核实]** = 在只读源码中读到（Avalonia tag `12.1.3`/`8eeda4f`，libghostty-vt `b3dc3d7`，TKCode `dfd3657`，StreamMark `074f7ff`），给出 `文件:行`；**[推断]** = 设计决定或由源码推导，未经运行验证；**[spike]** = 必须先做实验确认的点（§13）。

## 0. 结论摘要

| 主题 | 决定 |
|---|---|
| 绘制入口 | 一个与宿主无关的 `TerminalPainter`，产出"Skia 批次 + GlyphRun"交替的绘制序列；R3 由 `Control.Render` 录制，R1 由 `CompositionCustomVisualHandler.OnRender` 在渲染线程执行。两者共用 Painter、行缓存、sprite、颜色解析，只换 `ITerminalDrawTarget` |
| 文字 | `GlyphRun`，`GlyphInfo.GlyphAdvance` 强制为 cell 宽，按"行 × run"缓存。ASCII 且关闭连字时走 cmap 快路径，不做 shaping |
| 背景 / sprite / 装饰 / 光标 / 图片 | Skia lease（`ISkiaSharpApiLeaseFeature`），与 StreamMark.Mermaid 同一用法；拿不到 lease 时退化为 `FillRectangle` |
| 度量 | 照搬 Ghostty `Metrics.calc`：cell 宽高 round 到**整数设备像素**，基线、下划线位置 round，粗细 ceil 且 ≥1 |
| 行缓存 | 键 = `RowId + Version + FontGeneration`（连字且"光标处断开"时再加光标列）；颜色、选区、搜索、光标**不进键**，绘制时用裁剪覆盖着色 |
| 字体 | 默认按平台（`.SF NS Mono`→Menlo / Cascadia Mono→Consolas / portal monospace）；逐码点回退 + 缓存；系统查找放后台线程，先画占位 |
| 帧调度 | 输入路径无定时器；空闲时渲染循环休眠；光标闪烁由 UI 线程按相位边界唤醒（不逐帧轮询）；mode 2026 由核心 actor 扣住快照 |
| 主题 | `Terminal.*` 资源键 + `TerminalResourceAlias`（同 `MarkdownResourceAlias`）；Desktop 映射 AtomUI token / TKCode 调色板；明暗两套 ANSI 16 色（§11.3） |

## 1. 模块与文件

`src/TKCode.Terminal.Avalonia/Rendering/`：

| 类型 | 职责 | 所在线程（R3 / R1） |
|---|---|---|
| `TerminalView` | 控件本体；布局、度量、选择宿主、转发 UI 状态（§4） | UI |
| `UiRenderHost`（R3） | 重写 `Render(DrawingContext)`，录制 Painter 输出 | UI 录制 → 渲染线程回放 |
| `CompositorRenderHost` + `TerminalVisualHandler`（R1） | 自定义合成视觉，渲染线程绘制 | UI 创建；渲染线程绘制 |
| `TerminalPainter` | 快照 + 行缓存 → 绘制序列（§3） | 渲染所有者线程 |
| `CellMetrics` / `MetricsCalculator` | cell 度量（§5） | UI 计算，作为不可变值下发 |
| `FontSet` / `FontResolver` / `FallbackWorker` | 字面、回退、emoji（§6） | 解析在后台线程；结果不可变 |
| `RowCache` / `RowLayout` / `RunBuilder` | 行布局与 GlyphRun 缓存（§7） | 渲染所有者线程独占 |
| `SpriteRasterizer` / `SpriteAtlas` | 程序化字形（§8） | 渲染所有者线程 |
| `ColorResolver` / `ContrastCache` | 调色板、属性、最小对比度（§9.1、§9.7） | 渲染所有者线程 |
| `OverlayState` | 光标闪烁相位、预编辑、悬停链接、焦点（UI 产出的不可变对象） | UI 产出，渲染方只读 |
| `ImageStore` | Kitty 图形解码后位图（§9.9） | 写：视图核心 actor；读：渲染所有者 |
| `FrameSlot` | 最新快照槽 + Generation + WantFrame 标志（§10） | actor 写，渲染方读 |
| `TerminalTheme` / `TerminalResourceKeys` | 资源键解析为 `RenderTheme`（§11） | UI |
| `RenderStats`（EventSource） | 计数器与耗时（§12） | 任意 |

"渲染所有者线程"：R3 为 UI 线程，R1 为 Avalonia 渲染线程。`RowCache`、`SpriteAtlas`、`ContrastCache` 只由所有者访问，不加锁；跨线程输入一律是不可变对象或 `Volatile` 发布（满足 01 §5.2"渲染线程从不等锁"）。

## 2. 数据流

```
视图核心 actor ──CaptureFrame──▶ FrameSlot.Publish(frame)   (Volatile 交换 + Generation++)
      │                               │ 若宿主空闲：Wake（UI.Post，合并）
      │ ImageStore.Put(图片)           ▼
      │                      ┌──── R3：UI 线程 InvalidateVisual → Render(DrawingContext)
UI 线程 ── OverlayState ─────┤        Painter → DrawingContext（Custom 批次 + DrawGlyphRun）
  (光标相位/预编辑/悬停/焦点) └──── R1：SendHandlerMessage → 渲染线程 OnAnimationFrameUpdate/OnRender
                                      Painter → ImmediateDrawingContext（lease 批次 + DrawGlyphRun）
```

渲染方需要但 01 §6.1 `FrameSnapshot` 没有列出的字段（建议补进快照，见文末"架构问题"）：

| 字段 | 用途 | 来源 |
|---|---|---|
| `SearchHighlights`（视口内 `(row, colStart, colEnd, isActive)`） | 搜索高亮 §9.5 | actor 读 `GHOSTTY_SEARCH_DATA_VIEWPORT_MATCHES` 并换算到视口 [已核实 `search.h` DATA_VIEWPORT_MATCHES] |
| `Hyperlinks`（id → URI） | 悬停提示、OSC 8 下划线 | actor |
| `OverscanAbove/Below`（行） | 平滑滚动 §10.6 | `GHOSTTY_RENDER_STATE_OPTION_OVERSCAN` [已核实 `render.h:392-418`] |
| `Cursor.PasswordInput` | 可选的密码输入提示 | `GHOSTTY_RENDER_STATE_DATA_CURSOR_PASSWORD_INPUT` [已核实 `render.h:361`] |
| `MouseShape`（OSC 22） | 指针形状 | `GHOSTTY_TERMINAL_DATA_MOUSE_SHAPE` [已核实 `terminal.h:2952`] |

## 3. `TerminalPainter`

### 3.1 接口

```csharp
namespace TKCode.Terminal.Avalonia.Rendering;

/// 一帧的输入：全部是不可变值或由渲染所有者独占的对象。
public readonly struct PaintInput
{
    public FrameSnapshot Frame { get; init; }
    public OverlayState Overlay { get; init; }        // 光标相位、预编辑、悬停链接、焦点、选区预览
    public CellMetrics Metrics { get; init; }         // 设备像素整数度量 + 缩放
    public RenderTheme Theme { get; init; }           // 已解析的颜色、最小对比度、选项
    public PixelRect DirtyRows { get; init; }         // 需要重画的行范围（R1 局部失效；R3 全部）
}

public interface ITerminalDrawTarget
{
    bool HasSkia { get; }                              // 拿得到 lease
    void Skia(SkiaBatch batch);                        // 执行一个 Skia 批次（R3：Custom 操作；R1：lease）
    void GlyphRun(IImmutableBrush brush, RowRun run);  // 画一个文字 run
    void Fallback(SkiaBatch batch);                    // 无 Skia 时：只回放 FillRect
}

public sealed class TerminalPainter
{
    public TerminalPainter(RowCache rows, SpriteAtlas sprites, ColorResolver colors, ImageStore images);
    public void Paint(in PaintInput input, ITerminalDrawTarget target);
}
```

`SkiaBatch` 是一个池化的命令数组（`struct` 元素：`FillRect`、`Sprite`、`DecorationPath`、`Image`、`StrokeRect`），只引用不可变数据（颜色值、atlas 页句柄、图片句柄），所以 R3 可以把它交给渲染线程延后执行。批次在 `ICustomDrawOperation.Dispose` 时归还对象池 [推断：需确认 Avalonia 在 render data 替换时 Dispose 自定义操作，§13 S-8]。

### 3.2 绘制顺序（每帧）

顺序取自 Ghostty（bg → 下划线 → 字形 → 删除线）[已核实 research/04 §2.1.9]，按"能否与 GlyphRun 合批"切成 5 段。lease 期间不能调用其他 `ImmediateDrawingContext` 方法 [已核实 `DrawingContextImpl.cs:119-131`，见 research/04 §3.3]，所以段与段之间 Dispose lease。

| 段 | 类型 | 内容（从下到上） |
|---|---|---|
| P0 Under | Skia | ① 用默认背景清空网格区域与内边距；② Kitty 图片 `z < INT32_MIN/2`（below-bg）；③ 每行背景 RLE（§9.2，已叠加选区 / 搜索 / inverse）；④ Kitty 图片 `z < 0`（below-text）；⑤ 下划线、上划线、悬停链接下划线（§8.5）；⑥ sprite 字形（box、block、braille…，§8）；⑦ block 光标的填充矩形 |
| P1 Text | GlyphRun | 所有文字 run；被选区、搜索、block 光标覆盖的区间按裁剪分段换色（§7.5） |
| P2 Over | Skia | ① 删除线；② bar / underline / hollow 光标；③ Kitty 图片 `z ≥ 0`（above-text）；④ 预编辑背景 |
| P3 PreeditText | GlyphRun | 预编辑文字（没有预编辑时跳过） |
| P4 Top | Skia | 预编辑下划线与插入符；调试叠加层（§12） |

没有预编辑、没有 above-text 图片、没有删除线时，P2–P4 为空，整帧只有一次 lease + 一组 GlyphRun。

### 3.3 两种宿主的 `ITerminalDrawTarget`

| 方法 | R3 `DrawingContextTarget` | R1 `ImmediateTarget` |
|---|---|---|
| `Skia(batch)` | `context.Custom(new SkiaBatchOperation(batch, bounds))`；操作的 `Render(ImmediateDrawingContext)` 在渲染线程取 lease 执行 [已核实：`DrawingContext.Custom` 抽象方法 `DrawingContext.cs:243`；渲染线程回放 `RenderDataStream.Replay.cs:57-58`] | `using var lease = feature.Lease(); SkiaBatchExecutor.Run(lease.SkCanvas, batch)` |
| `GlyphRun(brush, run)` | `context.DrawGlyphRun(brush, run.GlyphRun)`（录制时 clone `PlatformImpl` [已核实 `RenderDataDrawingContext.cs:140-148`]） | `ctx.DrawGlyphRun(brush, run.ImmutableRef)`，`ImmutableRef` 由 `GlyphRun.TryCreateImmutableGlyphRunReference()` 创建后缓存在行布局里 [已核实 `GlyphRun.cs:926`、`ImmediateDrawingContext.cs:188`] |
| 拿 lease | 在 `SkiaBatchOperation.Render` 里 `context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature))` | `OnRender` 里同样调用；`CompositorDrawingContextProxy.GetFeature` 先 Flush 再转交 Skia [已核实 research/04 §3.4] |
| 无 Skia | `Fallback`：逐个 `FillRectangle`；sprite 退化为字体字形（行布局里保留字体 run 备份） | 同左（`ImmediateDrawingContext.FillRectangle` [已核实 `:201`]） |

坐标：Painter 内部全部用**设备像素整数**计算，交给目标前除以 `Metrics.Scaling` 换成 DIP（§5.3）。lease 内直接在 `SkCanvas` 上 `Scale(1/scaling)` 前置，然后用整数像素画，保证与网格对齐。

`SkiaBatchExecutor` 规则：

- `FillRect`：同色合并为一次 `SKCanvas.DrawRect`；`SKPaint` 按颜色缓存，`IsAntialias=false`。
- `Sprite`：Alpha8 atlas 页 + 颜色；优先 `SKCanvas.DrawAtlas(image, sprites, transforms, colors, SKBlendMode.Modulate)` 一次画完一个页的所有 sprite；[spike S-5] 验证 A8 图像 + colors 的着色结果，不行就退化为逐个 `DrawImage` + `SKPaint.Color`（Skia 对 alpha-only 图像使用画笔颜色 [推断]）。
- `DecorationPath`：curly / dotted / dashed 下划线按整段路径绘制（§8.5），开 AA。
- `Image`：`SKImage` + 源 / 目标矩形，1:1 时 `SKSamplingOptions(SKFilterMode.Nearest)`，缩放时 Linear。
- 透明度：乘以 `lease.CurrentOpacity`（StreamMark 同样处理 [已核实 `SkiaPictureDiagram.cs:122-131`]）。

## 4. 渲染宿主

### 4.1 R3：`Control.Render`

```csharp
public override void Render(DrawingContext context)
{
    var frame = _slot.Latest;                    // Volatile 读
    if (frame is null) { context.FillRectangle(_theme.BackgroundBrush, Bounds); return; }
    _slot.MarkConsumed(frame.Generation);        // 置 WantFrame，§10.3
    _painter.Paint(new PaintInput { Frame = frame, Overlay = _overlay, Metrics = _metrics,
                                    Theme = _renderTheme, DirtyRows = AllRows }, _drawingTarget);
}
```

- 失效：`FrameSlot` 发布后 actor 调 `Wake`（§10.1）→ UI 线程 `InvalidateVisual()`；整控件重录（Avalonia 收到新 render data 就整体失效 [已核实 research/04 §3.2 `ServerCompositionDrawListVisual.cs:36-39`]）。
- 行缓存命中时每帧 UI 线程只做"遍历可见行 + 录制已有 GlyphRun"，目标 ≤0.5 ms（200×60）。
- 光标闪烁、预编辑、悬停都是 `OverlayState` 变化 → `InvalidateVisual()`。

### 4.2 R1：`CompositionCustomVisualHandler`

已核实的 API（`CompositionCustomVisualHandler.cs:10-111`）：`OnMessage`、`OnAnimationFrameUpdate`、`OnRender(ImmediateDrawingContext)`、`EffectiveSize`、`CompositionNow`、`Invalidate()`、`Invalidate(Rect)`、`RegisterForNextAnimationFrameUpdate()`、`RenderClipIntersectes(Rect)`；`Compositor.CreateCustomVisual(handler)`、`CompositionCustomVisual.SendHandlerMessage`、`ElementComposition.SetElementChildVisual`（`ElementCompositionPreview.cs:21`）。

```csharp
// UI 线程
protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
{
    base.OnAttachedToVisualTree(e);
    var compositor = ElementComposition.GetElementVisual(this)!.Compositor;
    _handler = new TerminalVisualHandler(_slot, _painterFactory);   // _slot 是普通托管对象，跨线程只做 Volatile 读
    _visual = compositor.CreateCustomVisual(_handler);
    _visual.Size = new Vector(Bounds.Width, Bounds.Height);
    ElementComposition.SetElementChildVisual(this, _visual);
    _visual.SendHandlerMessage(new HostMessage.Configure(_metrics, _renderTheme, _overlay));
}
// 尺寸、缩放、主题、OverlayState 变化：_visual.Size = …; _visual.SendHandlerMessage(new HostMessage.…(…))
// 卸载：SetElementChildVisual(this, null); 发送 Detach，handler 释放 atlas 与图片
```

```csharp
// 渲染线程
sealed class TerminalVisualHandler : CompositionCustomVisualHandler
{
    long _drawnGeneration; int _idleTicks; bool _active;
    public override void OnMessage(object m)
    {
        switch (m)
        {
            case HostMessage.Wake: Activate(); break;
            case HostMessage.Overlay o: _overlay = o.State; Invalidate(o.DirtyRect); break;
            case HostMessage.Configure c: Apply(c); Invalidate(); break;   // 度量/主题变：清行缓存
        }
    }
    public override void OnAnimationFrameUpdate()
    {
        var frame = _slot.Latest;
        if (frame is not null && frame.Generation != _drawnGeneration)
        {
            Invalidate(DirtyRect(frame));              // 只失效变化行（Rows 引用不等的行）
            _idleTicks = 0;
        }
        else if (++_idleTicks >= 30) { _active = false; return; }   // 约 250 ms 无变化：休眠
        RegisterForNextAnimationFrameUpdate();
    }
    public override void OnRender(ImmediateDrawingContext ctx) { /* Painter.Paint(…, ImmediateTarget(ctx)) */ }
    void Activate() { if (!_active) { _active = true; _idleTicks = 0; RegisterForNextAnimationFrameUpdate(); } }
}
```

- 脏矩形：默认 `SingleDirtyRectTracker` 会把多个脏区合并成外接矩形 [已核实 research/04 §3.0]。v1 接受；是否开 `CompositionOptions.UseRegionDirtyRectClipping`（[已核实 `CompositionOptions.cs:11`]）由 spike S-3 决定，它是应用级开关，Desktop 决定。
- `OnRender` 里用 `RenderClipIntersectes(rowRect)` 跳过不在裁剪区内的行。
- handler 拿不到 DPI，缩放随 `Configure` 消息下发。

### 4.3 R2（调试开关）

`Control.Render` 里只录一个覆盖整个控件的 `ICustomDrawOperation`，`Render(ImmediateDrawingContext)` 里跑与 R1 完全相同的代码。用途：在不动合成器的前提下单独验证"渲染线程上创建 GlyphRun / 字体解析"（spike S-1），以及作为 R1 出问题时的回退。

### 4.4 宿主开关

| 层 | 名称 | 值 |
|---|---|---|
| `TerminalViewOptions.RenderHost` | `Auto` / `UiThread`(R3) / `CustomOperation`(R2) / `Compositor`(R1) | `Auto`：M3–M5 = R3；M6 起 = R1 |
| settings（高级，`terminal.rendering.host`） | `auto` / `ui` / `customOp` / `compositor` | `auto` |
| 环境变量 | `TKCODE_TERMINAL_RENDER_HOST` | 覆盖 settings，用于排查 |

`Auto` 在以下情况自动降为 R3：`ElementComposition.GetElementVisual(this)` 为 null；Headless 且 spike S-7 结论是自定义视觉不进 `CaptureRenderedFrame`。切换宿主时重建 `RowCache`（R3 缓存 `GlyphRun`，R1 缓存 `ImmutableRef`）。

### 4.5 从 R3 迁移到 R1 的步骤（M6）

| # | 步骤 | 完成标准 |
|---|---|---|
| 1 | Painter、RowCache、SpriteAtlas、ColorResolver 从第一天起就不碰 `Control`/`AvaloniaObject`，只吃 `PaintInput` | R3 阶段代码审查项 |
| 2 | 跑 spike S-1：渲染线程上 `GlyphRun` 构造、`TextShaper.ShapeText`、`FontManager.TryGetGlyphTypeface` 的线程安全 | 不安全 → 行布局构建移到"布局工作线程"，渲染线程只消费已建好的 `RowLayout`（多一帧延迟） |
| 3 | 开 R2 开关跑全部截图测试与 S4/S12 基准 | 像素一致；R2 帧时间达标 |
| 4 | 实现 `TerminalVisualHandler` 与消息；光标闪烁改为 `HostMessage.Overlay` | 截图测试在 R1 下通过（S-7 通过时） |
| 5 | 唤醒路径：actor → `Dispatcher.UIThread.Post(Wake)` → `SendHandlerMessage`；按键时 UI 线程预先发 `Wake`（§10.1） | 输入延迟分段（§12）p50 不劣于 R3 |
| 6 | 跑 S12（StreamMark 流式 + 终端洪流） | 终端帧率不随 UI 线程负载下降 |
| 7 | `Auto` 切到 R1；R3 保留一个版本作回退 | 发布说明 |

## 5. cell 度量与像素对齐

### 5.1 字号单位

字号按点（pt）配置（research/05 §7.1）：macOS `1pt = 1 DIP`，Windows / Linux `1pt = 96/72 DIP`。`emDip = sizePt × unit`；`emPx = emDip × scaling`（`TopLevel.RenderScaling` [已核实 `TopLevel.cs:519`]）。

### 5.2 计算（设备像素）

输入：主字体 regular 字面的 `GlyphTypeface.Metrics`（设计单位，`Ascent` 为负 [已核实 `FontMetrics.cs:6-56`]）。`s = emPx / DesignEmHeight`。

| 量 | 公式 | 取整 | 来源 |
|---|---|---|---|
| `faceW` | ASCII 32..126 的最大 advance × s（`TryGetHorizontalGlyphAdvances` [已核实 `GlyphTypeface.cs:694`]） | — | Ghostty `Metrics.zig:243-327` |
| `cellW` | `faceW + letterSpacing × scaling` | **round**，≥1 | Ghostty 用 round 不用 ceil |
| `faceH` | `(Descent − Ascent + LineGap) × s` | — | |
| `cellH` | `faceH × lineHeight` | **round** | |
| `baselineFromBottom` | `LineGap·s/2 + Descent·s − (cellH − faceH)/2` | round | 行高调整差值上下各半 |
| `baselineY` | `cellH − baselineFromBottom` | 整数 | 字形基线距 cell 顶 |
| `underlineThickness` | `UnderlineThickness·s`；缺失时 `0.15 × exHeight` | **ceil**，≥1 | |
| `underlineY` | `baselineY − UnderlinePosition·s`（Avalonia 符号约定下 UnderlinePosition 为负 [推断：需按字体实测确认符号]） | round | |
| `strikeThickness` / `strikeY` | 同上；缺失时位于 `(exHeight + 粗细)/2` | ceil / round | |
| `overlineY` | 0 | — | |
| `boxThickness` | = `underlineThickness` | — | Ghostty sprite light 线宽 |
| `cursorBarWidth` | = `boxThickness` | — | |
| `cursorUnderlineHeight` | = `underlineThickness` | — | |

缺失度量时的估算：capHeight = 0.75 × ascent，exHeight = 0.75 × capHeight（Ghostty `Metrics.zig:120-230`）。

### 5.3 网格原点与布局

- 内边距 `Options.Padding`（默认 6 DIP）先换成设备像素 round。网格列数 `cols = floor((widthPx − padL − padR) / cellW)`，行数同理；剩余像素放在右侧和底部，用默认背景填充（可选 `padding.extendBackground`：用最近一列 / 行的背景延伸，Ghostty `window-padding-color=extend`）。
- 原点对齐：控件在窗口中的位置可能落在分数设备像素上。R3：`this.TranslatePoint(default, topLevel)` × scaling 取小数部分 `frac`，Painter 整体平移 `−frac`；R1：在 lease 中读 `SkCanvas.TotalMatrix` 的平移分量，同样消除小数。
- `GlyphRun.BaselineOrigin` 用 `(col × cellW, row × cellH + baselineY) / scaling`，换成 DIP 后可能是分数，但对应的设备像素是整数。
- 尺寸变化 → `RequestResize(new TerminalSize(Columns: cols, Rows: rows, CellWidth: cellW, CellHeight: cellH))`。按 01 §6.1，`CellWidth/CellHeight` 是**单个 cell** 的像素尺寸，这里取设备像素整数（供 CSI 14/16 t、2048、SGR-pixels 使用，与 Ghostty 一致 [推断]）；整块区域像素由需要的一方乘出来。网格之外的内边距不计入。

### 5.4 重建触发

| 事件 | 动作 |
|---|---|
| 字体族、字号、行高、字距、粗体字重变化 | 重算度量；`FontGeneration++`；清空行缓存、sprite atlas；重新 `RequestResize` |
| `TopLevel.ScalingChanged`（[已核实 `TopLevel.cs:348`]），窗口移到不同 DPI 的屏幕 | 同上 |
| 回退字体解析完成（§6.3） | `FallbackEpoch++`；只重建带 `PendingFallback` 标记的行 |
| 主题 / 调色板变化 | `ThemeGeneration++`；不清行布局（颜色不进键），清 `ContrastCache` |

## 6. 字体

### 6.1 默认字体（"跟随系统"的解析结果）

来源 research/05 §7.1。解析在 Desktop（需要读系统设置），结果以字体族列表传给 `TerminalViewOptions.Font`。

| 平台 | 首选 | 回退 | 字号 |
|---|---|---|---|
| macOS | `.SF NS Mono`（隐藏系统字体，`/System/Library/Fonts/SFNSMono.ttf`） | Menlo | 12 pt |
| Windows | Cascadia Mono | Consolas | 12 pt |
| Linux | portal `org.freedesktop.appearance` / gsettings `monospace-font-name`（GNOME，默认 Adwaita Mono 11）→ kdeglobals `[General] fixed`（KDE）→ fontconfig `monospace` | DejaVu Sans Mono | 取系统值，缺省 11 pt |

验证：`FontManager.TryGetGlyphTypeface(new Typeface(family))`（[已核实 `FontManager.cs:129`]）成功、`Metrics.IsFixedPitch` 为真、`M` 与 `i` 的 advance 相等（防止误配到比例字体）。`.SF NS Mono` 能否被 Skia 按族名解析是 spike S-9（research/05 标为 [推]）。

### 6.2 字面：真实字面优先，缺失时合成

- `FontSet` 持有 4 个字面：regular / bold / italic / bold-italic，用 `new Typeface(family, style, weight)` + `TryGetGlyphTypeface` 取得。
- Avalonia 在字体族没有对应字面时**自动合成**：Skia 的 `TryCreateGlyphTypeface` 在 `weight ≥ 600 && !IsBold` 时加 `FontSimulations.Bold`，斜体缺失时加 `Oblique` [已核实 `FontManagerImpl.cs:113-121`；`FontCollectionBase.cs:495-503`]；合成实现是 `SKFont.Embolden` + `skewX = -0.3`（约 16.7°）[已核实 `SkiaTypeface.cs:33-40`]。
- 判断是否合成：`glyphTypeface.FontSimulations != None`。设置 `terminal.font.syntheticStyle`（默认 `true`，同 Ghostty `font-synthetic-style`）；为 `false` 时，合成出来的字面改用 regular（只保留颜色等其他属性）。
- 粗体字重 `font.weightBold`（默认 `bold`=700），普通字重 `font.weight`（默认 400）。
- `colors.boldIsBright`（默认 false）：粗体且前景是调色板 0–7 时换成 8–15（§9.1）。

### 6.3 逐码点回退

解析顺序（Ghostty `CodepointResolver.getIndex` [已核实 research/04 §2.1.12]）：

| # | 步骤 | 实现 |
|---|---|---|
| 1 | 程序化字形 | 码点在 §8.1 范围且 `rendering.customGlyphs=true` → sprite，不进字体 |
| 2 | 码点映射覆盖 | `font.codepointMap`（范围 → 字体族），可选 |
| 3 | 主字体同样式 | `CharacterToGlyphMap` 查表（[已核实 `GlyphTypeface.cs:324`]）；emoji 呈现（§6.5）时跳过文本字体 |
| 4 | 主字体 regular | 非 regular 样式找不到时 |
| 5 | 已加载的回退字面 | 按加载顺序 |
| 6 | 配置的回退列表 | `font.fallback`，缺省为 §6.4 的 CJK 列表 |
| 7 | 系统匹配 | `FontManager.TryMatchCharacter(cp, style, weight, stretch, family, culture, out typeface)` [已核实 `FontManager.cs:291`]，**在 `FallbackWorker` 后台线程执行** |
| 8 | 全部失败 | 画"缺字方框" sprite（`U+FFFD` 也找不到时） |

缓存：`Dictionary<ulong, FaceId>`，键 = `cp(21) | style(2)<<21 | presentation(2)<<23`；值是 `FaceId`（`FontSet` 内 `GlyphTypeface` 表下标）。第 7 步未完成时缓存 `Pending`，该行布局标记 `PendingFallback` 并先画占位（空白，不画方框，避免闪烁）；后台完成后 `FallbackEpoch++` 并唤醒渲染，带标记的行重建（WezTerm 做法 [已核实 research/04 §2.3]）。

回退字面尺寸调整（Ghostty `size_adjustment = ic_width` [已核实 research/04 §2.1.12]）：CJK 回退字面测 `水`（U+6C34）的 advance，若 `> 2 × cellW`，按比例缩小 emSize；再保证 `ascent + descent ≤ cellH`。基线与主字体共用 `baselineY`。

### 6.4 CJK 回退列表（默认）

按 UI 语言调整顺序（`zh-Hans` / `zh-Hant` / `ja` / `ko` 各自排第一）[推断：列表为常见系统自带字体]：

| 平台 | 列表 |
|---|---|
| macOS | PingFang SC, PingFang TC, Hiragino Sans, Apple SD Gothic Neo |
| Windows | Microsoft YaHei UI, Microsoft JhengHei UI, Yu Gothic UI, Malgun Gothic, SimSun |
| Linux | Noto Sans Mono CJK SC, Noto Sans CJK SC, Source Han Sans SC, WenQuanYi Micro Hei |

### 6.5 彩色 emoji 与宽字形

- 呈现判定：grapheme 带 VS16，或首码点默认 emoji 呈现（`Codepoint.HasEmojiPresentation` [已核实 research/04 §4.3]）→ emoji 字体；带 VS15 → 文本字体。
- emoji 字体：Apple Color Emoji / Segoe UI Emoji / Noto Color Emoji。Skia 自动绘制 COLR / sbix / CBDT 彩色字形 [推断]，画笔颜色不影响彩色字形。
- 尺寸（Ghostty `SharedGrid.zig:416-432`）：`cover` 方式放进 2 格，居中，左右各留 2.5%：用 `TryGetGlyphMetrics` 取字形包围盒，按 `min((2·cellW·0.95)/w, cellH/h)` 求 emSize。emoji 单独成 run（§7.3）。
- 宽度永远由核心决定（快照里的 wide 标志），渲染器只决定字形放进 1 格还是 2 格（research/03 §6.3 第 6 条）。
- 宽于 1 格的窄字形（Nerd Font 图标等，Ghostty `constraintWidth` [已核实 research/04 §4.3]）：右邻格为空白时允许溢出到 2 格；否则按 1 格等比缩小。溢出只影响字形，背景仍按格画。

## 7. 行缓存

### 7.1 键与生命周期

```csharp
readonly record struct RowKey(ulong RowId, int Version, int FontGeneration, int CursorBreakCol);
// CursorBreakCol：仅当 ligatures=true 且 breakAtCursor=true 且光标在本行时为光标列，否则 -1
```

- `RowId` 跨滚动稳定（01 §6.1），普通滚动全部命中。
- 存储：`Dictionary<ulong, RowLayout>`（按 RowId），条目里存 `Version / FontGeneration / CursorBreakCol`，不匹配则重建并复用旧对象的数组（零分配）。
- 淘汰：每个条目记录最后使用的帧号；超过 120 帧未用且缓存条目数 > `3 × 可见行数` 时淘汰；字体或宿主变化时整体清空。
- `FallbackEpoch` 不进键：带 `PendingFallback` 的条目在 epoch 变化时视为失效。

### 7.2 `RowLayout` 内容

| 字段 | 内容 |
|---|---|
| `BgSpans` | `(colStart, colEnd, ColorRef bg, flags)` RLE，已去掉默认背景段 |
| `Runs` | `RowRun[]`：`FaceId`、`emSize`、`colStart/colEnd`、`StyleIndex`、`GlyphRun`（R3）或 `IImmutableGlyphRunReference`（R1）、`IsEmoji`、`HasBlink` |
| `Sprites` | `(col, SpriteKey, StyleIndex)`；宽 sprite 记 2 格 |
| `Decorations` | 下划线（样式 / 颜色引用）、上划线、删除线、超链接 id 的列区间 |
| `Flags` | `PendingFallback`、`HasBlink`、`HasWideOverflow`、`AllDefaultBg` |

颜色在布局里只存引用（`StyleIndex` 指向 `RowSnapshot.Styles`），绘制时由 `ColorResolver` 解析（§9.1）。

### 7.3 run 切分规则

| 断开条件 | 原因 |
|---|---|
| 样式变化（不含 bg） | fg、粗体 / 斜体、下划线不同 → 字面或颜色不同 |
| bg 变化，**仅当** `minimumContrastRatio > 1` | 最小对比度按 (fg, bg) 调整前景 |
| 字面（`FaceId`）变化 | 回退字体、emoji |
| sprite 格 | 从 run 中移出，进 `Sprites` |
| 宽字符尾格（spacer） | 跳过，不产生字形 |
| 光标列 | 仅 `ligatures=true && breakAtCursor=true`：光标前断开、光标格单独成 run（Ghostty [已核实 research/04 §2.1.10]） |
| 不可见（SGR 8） | 不产生字形 |
| 选区、搜索、block 光标 | **不断开**，绘制时裁剪换色（§7.5） |
| run 长度 | 不限 |

### 7.4 GlyphRun 构建

1. **ASCII 快路径**（全部码点 0x20–0x7E、主字面、`ligatures=false`）：每个字面预先建 `ushort[95]` 字形表；`GlyphInfo(glyph, cluster: i, advance: cellWDip)`。不经过 HarfBuzz。
2. **cmap 路径**（关闭连字、每格一个码点或 grapheme 不需要 shaping）：逐格 `CharacterToGlyphMap` 取字形，同样固定 advance。
3. **shaping 路径**（开启连字，或 grapheme 含组合符 / ZWJ / 复杂文字）：`TextShaper.Current.ShapeText(chars, new TextShaperOptions(face, emSize, bidiLevel: 0, culture, features))`（[已核实 `TextShaper.cs:43`；TKCode 已 `UseHarfBuzz()`，research/04 §4.5]）→ 网格对齐：每个 cluster 的起点强制为 `col × cellW`，cluster 内多个字形保留 HarfBuzz 的相对偏移，最后一个字形的 advance 补齐到下一个 cluster 的列（WT `AtlasEngine.cpp:1173` 同样做法）。
   - `ligatures=false` 时 features 传 `-calt,-liga,-dlig`（Ghostty 建议值）。
   - shaping 结果按 `hash(码点序列, 相对 cluster, FaceId, featuresId)` 缓存（颜色不进键，Ghostty `run.zig:262-287`），容量 4096。
4. `new GlyphRun(face, emSizeDip, chars, glyphInfos, baselineOrigin)`（[已核实 `GlyphRun.cs:63-68`]）；R1 再 `TryCreateImmutableGlyphRunReference()` 并缓存，旧引用在条目重建时 Dispose。

### 7.5 绘制时的覆盖着色

选区、搜索匹配、block 光标会让一个 run 的一部分换前景色。做法（WezTerm 按像素区间切条 [已核实 research/04 §2.3]）：对与覆盖区间相交的 run，先 `PushClip` 非覆盖部分画一次原色，再 `PushClip` 覆盖部分画一次覆盖色。R1 用 `ImmediateDrawingContext.PushClip(Rect)` [已核实 `:277`]，R3 用 `DrawingContext.PushClip` [已核实 `:354`]。一行最多 3 种覆盖（选区、搜索、光标），每个 run 最多 7 段。

## 8. 程序化字形（sprite）与装饰

### 8.1 范围

取 Ghostty / WT / xterm.js / kitty 的并集（research/04 §4.6）。`rendering.customGlyphs=false` 时全部改用字体。

| 优先级 | 范围 | 内容 |
|---|---|---|
| P1（M3） | U+2500–257F | Box Drawing（含圆角 ╭╮╰╯、虚线 ┄┆、对角 ╱╲╳） |
| P1 | U+2580–259F | Block Elements（含 ░▒▓） |
| P1 | U+2800–28FF | Braille |
| P1 | U+E0B0–E0BF、E0D2、E0D4 | Powerline 几何类 |
| P2（M4） | U+1FB00–1FBEF | Legacy Computing（sextant、wedge、对角块等） |
| P2 | U+1CD00–1CDE5 | Octants |
| P2 | U+25E2–25E5、25F8–25FA、25FF | 几何三角 |
| P3（M6） | U+EE00–EE0B | Fira Code 进度条与 spinner |
| P3 | U+F5D0–F60D | git 分支图（kitty 扩展） |
| 内置 | — | 缺字方框（§6.3 第 8 步） |

### 8.2 光栅化

- 每个 sprite：`SKPath` 画到 **A8** `SKBitmap`（`cellW × cellH`，宽 sprite 为 `2·cellW`）。
- 键：`(cp, cellW, cellH, boxThickness)`；打包进 `SpriteAtlas`（1024×1024 A8 页，shelf 打包，满了开新页，最多 8 页，超出时整体清空重建——终端用到的 sprite 种类有限）。
- 栅格化时机：首次在行布局中出现时同步完成（单个 ≤50 µs 目标）；atlas 页从 `SKBitmap` 生成 `SKImage` 的操作在帧内集中做一次。
- 着色：A8 + 画笔颜色（§3.3 `Sprite`）。最小对比度对 box / block / powerline / legacy 豁免（Ghostty [已核实 research/04 §2.1.14]）。

### 8.3 对齐规则（Ghostty，[已核实 research/04 §2.1.13、§4.6]）

| 项 | 规则 |
|---|---|
| 线宽 | light = `boxThickness`，heavy = `2 × boxThickness` |
| 中线位置 | 水平线 y = `(cellH − t) / 2` 向下取整；竖线同理；保证相邻格的线首尾相接 |
| 分数坐标 | `min = s − round((1 − f)·s)`，`max = round(f·s)`：两个半块在奇数尺寸下无缝 |
| 1/8 块（▁▂…） | 用上面的分数公式求像素边界 |
| 阴影 ░▒▓ | 整格填充，alpha 分别为 25% / 50% / 75%（不用点阵，HiDPI 下不出摩尔纹） |
| 盲文 | 点宽 = `min(w/4, h/8)`；剩余像素按"点宽 > 边距 > 间距"分配 |
| 圆角 | 四分之一圆弧，半径 = `min(w, h)/2`，开 AA |
| 虚线 ┄┅┆┇┈┉┊┋ | 2/3/4 段，段长与间隔整数像素，余数分给间隔 |
| 对角线 | 开 AA，从格角到格角（跨格连续） |
| Powerline | 三角形 / 半圆实心；细线版本用 `boxThickness` 描边；高度覆盖整个 cellH |
| Sextant / Octant | 2×3 / 2×4 子块网格，用分数公式求边界，按码点查位表 |

### 8.4 光标形状（作为装饰绘制，不进 atlas）

| 形状 | 矩形 |
|---|---|
| block | 整格（宽字符为 2 格）填充 |
| bar | x = 格左边，宽 `cursorBarWidth`；与 Ghostty 一样贴在格的左侧 |
| underline | y = `underlineY`，高 `cursorUnderlineHeight` |
| hollow | 整格描边，线宽 `boxThickness`（失焦时） |

### 8.5 下划线、删除线、上划线、超链接

画在 P0（下划线、上划线）与 P2（删除线），按**整段列区间**画一条路径，不按格拼 sprite，保证曲线连续。

| 样式（SGR 4:n，[已核实 `sgr.h:102-107`]） | 画法 |
|---|---|
| single | 矩形 `[x0, x1) × [underlineY, +t)` |
| double | 两条，间隔 `t`：第二条在 `underlineY + 2t`，超出 cell 底时整体上移 |
| curly | 正弦路径，周期 = `cellW`，振幅 = `max(1, round(cellW/π/2))`，线宽 `t`，AA；从 `x0` 对齐到格边，跨 run 相位连续（相位按绝对列计算） |
| dotted | 圆点直径 `t`（至少 1 px），间距 `2t`，按绝对 x 对齐 |
| dashed | 段长 `cellW/3 + 1`，间隔同长度，按格重复 |
| 删除线 | 矩形 `[strikeY, +strikeThickness)` |
| 上划线 | 矩形 `[0, t)` |
| 颜色 | SGR 58 下划线颜色，缺省用前景色（已经过 faint / 最小对比度） |

超链接（OSC 8 与检测到的 URL / 路径）：常态不加装饰；悬停时（`OverlayState.HoverLink` 的列区间），没有下划线的加 single，已有 single 的改 double（Ghostty 规则 [已核实 research/04 §4.7]），颜色用 `Terminal.LinkForeground`（未设置时用前景）。

## 9. 颜色、覆盖层与特殊属性

### 9.1 每格颜色解析顺序

```
fg/bg ← ResolvedStyle（ColorRef：Default | Palette(i) | Rgb）对 FrameSnapshot.Palette 解析
bold && boldIsBright && fg 是 Palette(0..7) → Palette(i+8)
inverse → 交换 fg/bg（Default 也参与交换）
faint → fg = mix(fg, bg, 0.5)（等价于 alpha 0.5，Ghostty / xterm.js）
invisible → 不画字形与装饰（背景照画）
选区 → bg = SelectionBackground；fg = SelectionForeground 或自动黑/白（§9.4）
搜索匹配 → bg = SearchMatch[Active]Background；fg 自动黑/白
block 光标所在格 → fg = CursorText（缺省为默认背景色）
最小对比度（§9.7）→ 调整 fg；豁免 sprite 几何范围、emoji、图片
blink 熄灭相位 → 不画字形（§9.8）
```

`FrameSnapshot.Palette` 已处理 DECSCNM 反显（01 §6.1）。颜色值以 `uint`（ARGB）在 Painter 内流转，`IImmutableBrush` 按颜色缓存（`ImmutableSolidColorBrush`）。

### 9.2 背景：按行 RLE

- 每帧先用默认背景清空整个网格 + 内边距（一次 `DrawRect`），所以默认背景段不画。
- 每行 `BgSpans` 合并相邻同色格；选区、搜索、inverse 在 Painter 里与 `BgSpans` 求交后输出，最终每行若干 `FillRect`。
- 宽字符的背景覆盖 2 格。
- 先用 RLE（research/04 §8.2 建议）；如果 S4 基准显示背景是瓶颈，再换成"每格 1 像素位图 + 最近邻放大"一次 `DrawImage`（WT / Ghostty 做法）。

### 9.3 光标

| 项 | 规则 | 来源 |
|---|---|---|
| 形状 | 快照 `CursorState`（DECSCUSR）优先；未设置时用 `cursor.style`：macOS / Linux block，Windows bar | research/05 §7.2 |
| 颜色 | OSC 12 设置的颜色 → `Terminal.CursorColor` → 前景色 | |
| 光标下文字 | block：`Terminal.CursorTextColor`，缺省默认背景色 | Ghostty `cell_text.v.glsl:134-141` |
| 低对比度兜底 | 光标色与所在格背景对比度 < 1.5 → 改用该格前景色 / 背景色反色 | Alacritty `display/content.rs:22,124-136` |
| 失焦 | `cursor.unfocusedStyle`：`outline`（默认，hollow）/ `none` / `same` | VS Code |
| 宽字符 | 光标在宽字符上（`CURSOR_VIEWPORT_WIDE_TAIL` [已核实 `render.h:377`]）→ 覆盖 2 格 | |
| 不可见 | DECTCEM 关闭、视口滚离光标行 → 不画 | |
| 预编辑中 | 不画终端光标，由预编辑插入符代替（§9.6） | |

**闪烁**

| 条件 | 闪烁？ |
|---|---|
| `cursor.blink = false` | 否 |
| `cursor.blink = true` | 由程序决定：`CursorState.Blinking`（DECSCUSR 奇数值 / mode 12，`GHOSTTY_RENDER_STATE_DATA_CURSOR_BLINKING` [已核实 `render.h:358`]） |
| `cursor.blink = system`（默认） | 系统允许闪烁时同上；macOS 默认不闪（Terminal.app / iTerm2） |
| 失焦、窗口最小化、`reduceMotion` | 否（常亮） |
| Linux `cursor-blink-timeout` 到期（无输入 N 秒） | 停在"亮" |

系统闪烁参数（Avalonia 不提供 [已核实 `IPlatformSettings.cs:13-63` 无此项]，由 Desktop 读取后传入 `TerminalViewOptions.CursorBlinkInterval`）：

| 平台 | 来源 | 换算 |
|---|---|---|
| Windows | `GetCaretBlinkTime()`（user32）；返回 `INFINITE` 或 > 10 s 视为不闪 | 返回值即半周期 |
| Linux GNOME | portal / gsettings `org.gnome.desktop.interface cursor-blink`、`cursor-blink-time`（整周期 ms，默认 1200）、`cursor-blink-timeout`（s，默认 10） | 半周期 = time/2 |
| Linux KDE | kdeglobals `CursorBlinkRate` [推断] | ms |
| macOS | `NSTextInsertionPointBlinkPeriodOn/Off`（未公开键，通常不存在）→ 560 ms（iTerm2 的回退值） | 只在 `cursor.blink=true` 时使用 |

相位：有输入或输出时重置为"亮"（节流 500 ms，Ghostty `Termio.zig:714-725`）。计时器在 UI 线程（§10.2）。

### 9.4 选区高亮

- 来自快照 `Selection`（UI 侧核心的当前选区，线性或矩形）。
- `Terminal.SelectionBackground` 必有；`Terminal.SelectionForeground` 可选。未设置前景时按选区背景亮度自动取黑或白（WT / Ghostty）。
- 直接改格子颜色，不用半透明叠加（WT `AtlasEngine.cpp:410-484`），文字清晰度不受影响。

### 9.5 搜索高亮

- 来自快照 `SearchHighlights`（§2，需补进快照）。
- 普通匹配 `Terminal.SearchMatchBackground`，当前匹配 `Terminal.SearchMatchActiveBackground`；前景自动黑 / 白。优先级：当前匹配 > 选区 > 普通匹配。
- 搜索 UI 与线程见 [06-input.md](06-input.md) §9。

### 9.6 预编辑（IME）叠加层

- 数据：`OverlayState.Preedit { Text, CursorPos, CellWidths[] }`，列宽由 UI 线程用 `ghostty_unicode_grapheme_width`（2027 开）或逐码点 `ghostty_unicode_codepoint_width`（2027 关）预测（[已核实 `unicode.h:69,144`，纯函数、线程安全]；06 §4）。
- 位置：从光标格开始。总宽超出行尾时整体左移，使右端对齐行尾；仍放不下（预编辑宽于整行）时从左端裁剪，保证插入符可见。
- 绘制：P2 用默认背景填充覆盖区域 → P3 用默认前景画文字（独立的预编辑行布局，不进行缓存）→ P4 画 1 条 single 下划线（`underlineThickness`）和插入符（bar，`cursorBarWidth`，位置为 `CursorPos` 对应的格边）。
- 预编辑不写入核心，不影响快照（01 §6.1 的快照只含终端状态）。

### 9.7 最小对比度

- 默认 `colors.minimumContrastRatio = 4.5`（research/05 §7.4 建议，VS Code 同值）；系统高对比度时 7；设 1 关闭。
- 算法：WCAG 相对亮度 `(L1+0.05)/(L2+0.05)`。不达标时采用 xterm.js 的逐步调整（[已核实 research/05 §7.4：`Color.ts:296-361`]）：前景比背景暗就每次把 RGB 各乘 0.9（变暗），否则向白色逼近 10%；最多 10 次；仍不达标则取黑或白中对比度高者（Ghostty 的兜底）。这样保留色相，比 Ghostty 直接变黑白更柔和。
- 缓存：`Dictionary<(uint fg, uint bg), uint>`，容量 4096，`ThemeGeneration` 变化时清空。
- 豁免：§8.1 中 box / block / powerline / legacy / octant 范围（几何字符常故意 fg≈bg）、emoji、图片、`invisible`。

### 9.8 特殊属性

| 属性 | 渲染 |
|---|---|
| bold | bold 字面（或合成）；可选 bold-is-bright |
| italic | italic 字面（或合成） |
| faint（SGR 2） | 前景向背景混合 50% |
| inverse（SGR 7） | 交换前景与背景 |
| invisible（SGR 8） | 只画背景 |
| blink（SGR 5/6） | `rendering.textBlink`（默认 true）：可见区域有 blink 格时启动 500 ms 计时器，熄灭相位不画字形；`reduceMotion` 时常亮。Ghostty 不渲染，foot 渲染（research/04 §4.8） |
| strikethrough / overline / underline | §8.5 |

### 9.9 Kitty 图形

数据路径：

1. **视图核心 actor**：每次 `CaptureFrame` 时检查 `GHOSTTY_KITTY_GRAPHICS_DATA_GENERATION`（[已核实 `kitty_graphics.h:166`]），有变化就遍历放置（placement iterator），对每个放置调用 `ghostty_kitty_graphics_placement_render_info` / `placement_rect`（[已核实 `:714-859`]）得到像素矩形、源矩形、z 值。图片像素指针只在下一次修改终端前有效（[已核实 `kitty_graphics.h:30-34`]），所以对"`(imageId, imageGeneration)` 新出现"的图片，在 actor 上把像素复制进 `ImageStore`（RGBA，必要时从 RGB / Gray 扩展）。
2. **快照**：`FrameSnapshot.Images` 只带放置列表（`imageId, imageGeneration, destRectPx, srcRectPx, z`）。
3. **渲染所有者**：按 `(imageId, generation)` 从 `ImageStore` 取像素，首次使用时 `SKImage.FromPixelCopy` 并缓存；R3 / R1 都在 Skia 批次里画。

| 项 | 规则 |
|---|---|
| z 序 | `z < INT32_MIN/2` → P0 ②（在格背景之下，只透过默认背景格显示）；`z < 0` → P0 ④；`z ≥ 0` → P2 ③（[已核实 `kitty_graphics.h:278-281` 三层枚举]） |
| 裁剪 | 先裁到网格矩形（不含内边距），再按源矩形取子图 |
| 采样 | 目标尺寸等于源尺寸时 Nearest，否则 Linear |
| 内存 | `ImageStore` 按字节计 LRU，上限 = 会话的 Kitty 存储上限（host 在 attach 时下发，01 §3.2 规则 2）；不再被任何快照引用的图片先释放 `SKImage` |
| 虚拟放置（Unicode 占位符 U+10EEEE） | v1 支持：`render_info` 已给出格位；P2 优先级（M6 后段） |
| 无 Skia | 不画图片 |

## 10. 帧调度

### 10.1 唤醒

| 来源 | 路径 |
|---|---|
| actor 发布新快照 | `FrameSlot.Publish`；若 `HostIdle` 标志为真（Interlocked 交换为假，保证只投递一次）→ `Dispatcher.UIThread.Post(Wake, DispatcherPriority.Render)`。R3：`InvalidateVisual()`；R1：`SendHandlerMessage(Wake)`（非 UI 线程没有公开的唤醒渲染循环的方法 [已核实 research/04 §3.0 `IRenderLoop.Wakeup` 为 internal]） |
| 按键、IME、粘贴 | UI 线程在把输入交给 IPC 的同时**预先**发 `Wake`（R1），回显帧不必再绕 UI 线程 |
| OverlayState 变化 | R3 `InvalidateVisual()`；R1 `SendHandlerMessage(Overlay)` 并局部 `Invalidate(rect)` |

### 10.2 活跃与空闲

- R1：handler 活跃时每 tick `RegisterForNextAnimationFrameUpdate`；连续 30 tick 无新 Generation 就停止注册，渲染循环可以休眠（research/04 §5.4）。
- 光标闪烁**不**靠逐帧轮询（那样空闲时也会 60–120 Hz 唤醒），改为 UI 线程 `DispatcherTimer` 在相位边界触发一次 `Overlay` 消息，handler 只失效光标所在的矩形。这与 research/04 §5.4"由 `CompositionNow` 驱动"不同，理由是空闲 CPU 目标（S10）[推断]。文字闪烁（SGR 5）同理。
- `RenderHold`（mode 2026）期间 actor 不发布（01 §5.2），渲染方无事可做，自然空闲；超时 1 s 由 actor 处理。

### 10.3 洪流时的节流

渲染方每消费一个 Generation 就置 `FrameSlot.WantFrame = true`；actor 在批次之间看到 `WantFrame` 才 `CaptureFrame`，并清掉标志；解析完所有待处理输入、准备等待前，若状态是脏的也发布一次（research/04 §5.2）。效果：洪流时快照频率 ≤ 显示刷新率，交互时零延迟。

### 10.4 失焦、不可见

| 状态 | 行为 |
|---|---|
| 视图不可见（标签在后台、`IsEffectivelyVisible=false`） | 通知 actor `SetVisible(false)`：继续解析，不生成快照（01 §5.2）；宿主停止计时器 |
| 窗口最小化 / 遮挡 | 同上（`TopLevel` 状态） |
| 失焦 | 光标改 `unfocusedStyle`，停止闪烁；文字闪烁继续（低频） |
| 重新可见 | actor 立即发布一次全量快照（Rows 全部新对象，行缓存按 RowId 仍可命中） |

### 10.5 光标闪烁计时

`DispatcherTimer`（UI 线程），间隔 = 系统半周期；重置条件见 §9.3。`reduceMotion`、失焦、不闪烁时计时器停止。Linux 超时：最后一次输入后 `cursor-blink-timeout` 秒停止。

### 10.6 平滑滚动

- 设置 `terminal.rendering.smoothScrolling`，默认 `false`；全局 `reduceMotion`（TKCode 已有 `MotionSettings.SystemReduceMotion` / `UserReduceMotion`，[已核实 `App.axaml.cs:55-58`]）为真时强制关闭。
- 实现：actor 请求 `OVERSCAN = {above: 1, below: 1}`（[已核实 `render.h:409-418`]），快照带上下各 1 行。触控板精确滚动时，UI 线程把亚行位移 `subRowPx ∈ (−cellH, cellH)` 放进 `OverlayState`，Painter 整体平移网格并多画一行 overscan；累积超过一行时向 actor 发 `ScrollViewport(±1)` 并把 `subRowPx` 减去 `cellH`。
- 鼠标滚轮按"行"跳动时不做动画（v1）；需要时在 M6 后加 100 ms ease-out 的插值，overscan 扩到每次跳动的行数。
- 关闭时 overscan 为 0（避免额外的快照复制）。

### 10.7 滚动条

不在 Painter 里画：`TerminalView` 模板里叠一个 Avalonia `ScrollBar`，绑定快照 `ScrollbarState`，自动隐藏；拖动时发 `ScrollViewport(absolute)`。样式由宿主主题提供（Desktop 用 AtomUI 的滚动条样式）。

## 11. 主题

### 11.1 `Terminal.*` 资源键

`TerminalResourceKeys`（`TKCode.Terminal.Avalonia`，不依赖 AtomUI）。值可以是 `Color`、`ISolidColorBrush`，或 `TerminalResourceAlias(object Key)`（指向宿主的另一个资源键；目标是 `Color` 时自动转画刷），解析方式与 StreamMark `MarkdownResourceAlias` 相同：从 `TerminalView` 自身开始 `TryFindResource(key, ActualThemeVariant, out value)`，因此能找到窗口 / 主题作用域内的 token，并随明暗切换（[已核实 StreamMark `MarkdownResourceAlias.cs`、`AtomUIMarkdownTheme.cs`]）。

| 键 | 类型 | 说明 |
|---|---|---|
| `Terminal.Background` / `Terminal.Foreground` | 颜色 | 默认背景 / 前景（OSC 10/11 可改） |
| `Terminal.CursorColor` / `Terminal.CursorTextColor` | 颜色，可空 | 缺省：前景 / 背景 |
| `Terminal.SelectionBackground` / `Terminal.SelectionForeground` | 颜色 / 可空 | |
| `Terminal.SearchMatchBackground` / `Terminal.SearchMatchActiveBackground` | 颜色 | |
| `Terminal.LinkForeground` | 颜色，可空 | 悬停链接下划线颜色 |
| `Terminal.Ansi.Black` … `Terminal.Ansi.White`、`Terminal.Ansi.BrightBlack` … `Terminal.Ansi.BrightWhite` | 颜色 | 16 色（索引 0–15） |
| `Terminal.FontFamily` / `Terminal.FontSize` | `FontFamily` / `double`（pt） | 未设置时用 `TerminalViewOptions.Font` |
| `Terminal.Padding` | `Thickness` | 默认 6 |
| `Terminal.ScrollBarThumb` | 画刷 | 交给模板 |
| `Terminal.PreeditBackground` | 颜色，可空 | 缺省默认背景 |
| `Terminal.FocusBorder` | 画刷，可空 | 键盘焦点环（可访问性） |

256 色中 16–255 按 xterm 公式生成（6×6×6 立方 + 24 级灰），不做资源键。

`ActualThemeVariantChanged` → 重建 `RenderTheme`、`ThemeGeneration++`、发给宿主；**同时**必须把新的默认调色板交给 pty host（01 §3.2：`SetColors` 是日志记录，两份核心在同一位置更新，OSC 4/10/11 查询应答才一致）。UI 侧核心**不**在本地抢先改调色板；`SetColors` 记录回来之前（约 1 ms）画面仍用旧调色板，可以接受。`ITerminalViewSession` 目前没有发起这个请求的方法（见文末架构问题）。

### 11.2 Desktop 的映射（`TKCodeTerminalTheme : Styles`）

与 `AtomUIMarkdownTheme` 同样写法：`Resources[key] = new TerminalResourceAlias(token)`。

| `Terminal.*` | 映射到 |
|---|---|
| `Background` | `TKCode.TerminalBg`（新增于 `Palette.axaml`；暗 `#141414` = `TKCode.WindowBg`，亮 `#FFFFFF`） |
| `Foreground` | `TKCode.TerminalFg`（新增；暗 `#E6E6E6`，亮 `#1F1F1F`） |
| `CursorColor` | `TKCode.TextPrimary` |
| `SelectionBackground` | `TKCode.AccentSoftBg`（暗 `#2E2A4A` / 亮 `#ECE9FE`） |
| `SearchMatchBackground` | AtomUI `SharedTokenKind.ColorWarningBg` |
| `SearchMatchActiveBackground` | AtomUI `SharedTokenKind.ColorWarningBorder` [推断：token 名按 AtomUI 现有 `ColorWarning*` 系列，实现时核对] |
| `LinkForeground` | AtomUI `SharedTokenKind.ColorLink` |
| `FocusBorder` | `TKCode.Accent` |
| `Ansi.*` | `Palette.axaml` 的 ThemeDictionaries 中新增 `Terminal.Ansi.*`（§11.3） |

用户配置 `terminal.colors.dark` / `colors.light`（research/05 §10.3）覆盖上述映射，由 Desktop 直接写进 `TerminalView.Resources`。

### 11.3 默认 ANSI 16 色

设计取向：暗色沿用 TKCode 已有的文件图标色系（`TKCode.File*`），亮色把"白"映射为深灰（VS Code Light 的做法），保证在白底上可读。表中对比度为与默认背景的 WCAG 对比度（脚本计算）。

| 索引 | 名称 | 暗（bg `#141414`） | 对比度 | 亮（bg `#FFFFFF`） | 对比度 |
|---|---|---|---|---|---|
| fg | 前景 | `#E6E6E6` | 14.8 | `#1F1F1F` | 16.5 |
| 0 | Black | `#2C2C2E` | 1.3 | `#1F1F1F` | 16.5 |
| 1 | Red | `#E8645A` | 5.6 | `#B3261E` | 6.5 |
| 2 | Green | `#5EBE84` | 8.1 | `#1A7A40` | 5.4 |
| 3 | Yellow | `#E2C05C` | 10.5 | `#8A6100` | 5.5 |
| 4 | Blue | `#5AA2F0` | 6.9 | `#1F66C8` | 5.5 |
| 5 | Magenta | `#A88BF0` | 6.7 | `#7A4FD8` | 5.3 |
| 6 | Cyan | `#4FC1C9` | 8.6 | `#0E7A84` | 5.1 |
| 7 | White | `#C8C8CA` | 11.0 | `#6B6B70` | 5.3 |
| 8 | BrightBlack | `#808086` | 4.7 | `#76767B` | 4.5 |
| 9 | BrightRed | `#FF7B72` | 7.3 | `#D0392E` | 4.9 |
| 10 | BrightGreen | `#7EE0A1` | 11.5 | `#24874C` | 4.5 |
| 11 | BrightYellow | `#F5D97A` | 13.2 | `#956A00` | 4.8 |
| 12 | BrightBlue | `#82BEFF` | 9.4 | `#2A6FD6` | 4.8 |
| 13 | BrightMagenta | `#C4B3FF` | 9.9 | `#8455E6` | 4.8 |
| 14 | BrightCyan | `#7FDDE3` | 11.7 | `#10828D` | 4.6 |
| 15 | BrightWhite | `#F5F5F5` | 16.9 | `#3A3A3C` | 11.4 |

- 暗色 Black（0）有意接近背景：它主要被用作背景色（状态栏、选中行）；作前景时由最小对比度兜底。
- 亮色所有前景色 ≥ 4.5，所以亮色主题下最小对比度几乎不会触发，颜色保真。
- 光标：暗 `#F0F0F0` / 亮 `#1A1A1A`（`TKCode.TextPrimary`）；光标下文字取默认背景。

## 12. 性能预算与测量点

### 12.1 预算（200×60，缩放 2×）

| 项 | 预算 | 依据 |
|---|---|---|
| Painter 全量一帧（R1 渲染线程 / R3 渲染线程回放） | p99 ≤ 2 ms | 01 §1、research/04 §7.3 |
| 1–3 脏行（R1 局部失效） | ≤ 0.3 ms | research/04 §7.3 |
| R3 UI 线程录制（缓存全命中） | ≤ 0.5 ms | [推断] |
| R1 UI 线程每帧 | ≤ 0.1 ms（只做输入与唤醒） | research/04 §0.7 |
| 行布局重建：ASCII 快路径 / shaping | ≤ 20 µs / ≤ 150 µs 每行 | [推断] |
| sprite 首次栅格化 | ≤ 50 µs 每个 | [推断] |
| 系统回退查找 | 不在渲染线程；首帧不阻塞 | WezTerm |
| GC | 稳态零分配（行布局复用数组、批次池化） | research/04 §7.3 |
| 空闲 | 10 个终端空闲 + 光标闪烁 < 0.5% CPU | S10 |

### 12.2 测量点

`RenderStats : EventSource`（名称 `TKCode-Terminal-Render`，AOT 安全的 EventCounters）：

| 计数器 | 含义 |
|---|---|
| `paint-us`（p50/p95/p99，滑动窗口） | Painter.Paint 耗时 |
| `record-us` | R3 的 `Render` 录制耗时 |
| `rows-rebuilt` / `rows-hit` | 行缓存重建 / 命中 |
| `glyphruns-built`、`shape-cache-hit` | |
| `sprites-rasterized`、`atlas-pages` | |
| `fallback-pending`、`fallback-resolved` | 后台字体查找 |
| `frames-painted`、`generations-skipped` | 渲染方跳过的 Generation（洪流时正常） |
| `host-idle` | 宿主是否在休眠 |
| `contrast-cache-size`、`image-bytes` | |

输入延迟分段（与 06 §12 共用时间戳，`Stopwatch.GetTimestamp`）：`KeyDown` → `SendInput` 返回 → 核心处理到回显所在的输出块 → 包含回显的快照发布 → `OnRender`/`Render` 结束。调试叠加层（`TerminalViewOptions.ShowRenderStats`）在 P4 画这些数字。

### 12.3 research/04 §11 spike 落地

| # | spike | 实施 | 通过标准 | 里程碑 |
|---|---|---|---|---|
| S-1（04 #1） | 渲染线程上 `FontManager` / `TextShaper` / `GlyphRun` 的线程安全 | `TKCode.Terminal.Benchmarks` 里 8 个线程并发构建同一组行布局 10 万次，比对字形与 advance；再在 R2 开关下跑截图测试 | 无异常、结果一致；否则采用"布局工作线程"方案（§4.5 步骤 2） | M3 末 |
| S-2（04 #2） | handler 自轮询开销、休眠与唤醒 | S10（10 终端空闲 10 min）+ S8（Typometer） | 空闲 < 0.5% CPU；唤醒后首帧 ≤ 1 vsync | M6 |
| S-3（04 #3） | 单脏矩形合并的实际收益、`UseRegionDirtyRectClipping` 副作用 | S4（vim 滚动、htop）开关对比，同时测 TKCode 其他界面的帧时间 | 打开后终端 p99 降 ≥ 30% 且其他界面无回退才建议 Desktop 打开 | M6 |
| S-4（04 #4） | GlyphRun 在 Metal / ANGLE / GLX / Software 下的成本 | 4 个后端 × 200×60 全量重画，AOT 二进制 | 全部 p99 ≤ 2 ms；Software 后端允许 ≤ 6 ms | M3 |
| S-5 | A8 atlas + `DrawAtlas` colors 着色 | 截图测试比较 `DrawAtlas` 与逐个 `DrawImage` 的像素 | 一致则用 DrawAtlas | M3 |
| S-6（04 #6） | libghostty-vt 每帧 FFI 成本 | 属于 02-core-ghostty；渲染侧只验证"每帧 FFI 次数 ∝ 脏行数" | `CaptureFrame` 全脏 ≤ 50 µs | M1 |
| S-7（04 #7） | Headless 下 `CompositionCustomVisual` 是否进入 `CaptureRenderedFrame` | 最小测试：自定义视觉画一个红色方块后截图 | 能截到 → R1 也跑截图测试；否则 R1 只跑 R2 等价测试 | M6 开始前 |
| S-8 | R3 下 `ICustomDrawOperation` 的 Dispose 时机 | 计数测试：连续 1000 帧后池中对象数稳定 | 无泄漏 | M3 |
| S-9 | `.SF NS Mono` 能否被 Skia 解析 | Headless 截图 + `TryGetGlyphTypeface` 返回的 FamilyName | 失败则默认改为 Menlo | M3 |
| （04 #5） | AOT 下向量路径 | 属于核心，不在本文 | — | — |

## 13. 截图测试清单

基础设施：沿用 `tests/TKCode.Desktop.Tests/HeadlessApp`（`UseSkia()` + `UseHeadlessDrawing=false` [已核实 `HeadlessApp.cs:16-19`]），新增 `TerminalTestHost`：用回放实现的 `ITerminalViewSession` 直接喂字节（01 §6.5），`Step` 后 `CaptureRenderedFrame`。每个用例 = **像素探针断言**（`PixelProbe`：取指定设备像素的颜色，断言等于 / 接近期望）+ 保存 PNG 供人工查看。只有 sprite 类用例额外保留金样图（`tests/TKCode.Desktop.Tests/Terminal/golden/`，容差 0）。R3 必跑；S-7 通过后 R1 再跑一遍并比较两者像素完全一致。

| # | 用例 | 关键断言 |
|---|---|---|
| T1 | ASCII 网格对齐，缩放 1.0 / 1.25 / 1.5 / 2.0 | 每列字形墨迹落在 `[col·cellW, (col+1)·cellW)`；cellW/H 为整数像素 |
| T2 | 16 / 256 / truecolor 前景与背景 | 背景探针颜色精确；RLE 相邻同色无缝 |
| T3 | bold / italic / faint / inverse / invisible / bold-is-bright | faint 颜色 = 50% 混合；invisible 处等于背景 |
| T4 | 5 种下划线 + SGR 58 颜色 + 删除线 + 上划线 | 下划线行像素在 `underlineY` 处；curly 跨格相位连续 |
| T5 | Box drawing 拼接（`┼─│╋━┃╭╮╰╯` 组成网格） | 相邻格连线像素无断点、无重叠加深 |
| T6 | Block elements（▀▄█▌▐ 与 1/8 块、░▒▓） | 半块在奇数 cellH 下无缝；阴影 alpha |
| T7 | Braille 全集抽样 | 点位置与点宽 |
| T8 | Powerline E0B0–E0B3 与背景拼接 | 三角形边缘与相邻背景无缝隙 |
| T9 | Sextant / octant / wedge（M4） | 子块边界 |
| T10 | 宽字符 CJK + 回退字体 | 字形在 2 格内；回退前先画占位、`FallbackEpoch` 后出现字形 |
| T11 | emoji：单个、VS16、ZWJ 家庭、旗帜、肤色（兼容模式与 2027） | 宽度来自核心；字形居中于 2 格 |
| T12 | 组合字符（é = e + U+0301、天城文） | 附着在前一格 |
| T13 | 光标 block / bar / underline / hollow、失焦、宽字符上 | 形状矩形；block 下文字色为 CursorText |
| T14 | 光标闪烁相位（手动时钟） | 亮 / 灭两帧 |
| T15 | 选区：线性、矩形、跨软换行 | 覆盖段换色，未覆盖段原色（裁剪着色正确） |
| T16 | 搜索高亮：普通与当前匹配 | 颜色优先级 |
| T17 | 预编辑：中间、靠右溢出、宽于整行 | 位置、下划线、插入符 |
| T18 | 最小对比度：亮色主题下黄色字 | 调整后对比度 ≥ 4.5；box drawing 不被调整 |
| T19 | 明暗主题切换 | 背景与 16 色随主题；行缓存未重建（计数器） |
| T20 | Kitty 图片三个 z 层 | 遮挡顺序；网格外被裁掉 |
| T21 | OSC 8 / URL 悬停下划线（single → double） | |
| T22 | 连字开关（含 `->`、`=>`）与光标处断开 | 关闭时逐字符；开启时光标格单独 |
| T23 | 缩放变化（1.0 → 2.0）重建 | 度量与 atlas 重建，无旧尺寸残留 |
| T24 | 平滑滚动 `subRowPx` | 网格整体平移，overscan 行可见 |
| T25 | 无 Skia（`UseHeadlessDrawing=true`）退化 | 背景与文字仍正确，不抛异常 |
| T26 | 文字闪烁（SGR 5）与 `reduceMotion` | |
| T27 | 1000 帧压力：行缓存与批次池不增长 | 计数器稳定（S-8） |

---

## 发现的架构问题 / 待决问题（供修订 01）

1. `FrameSnapshot` 缺少渲染必需字段：搜索高亮、超链接 id → URI 表、overscan 行、OSC 22 指针形状（§2）。
2. 01 §3.2 规则 1 已把 `SetColors`、`Clear` 列为日志记录，但 `ITerminalViewSession` 仍没有发起这两种请求的方法（例如 `RequestSetColors(TerminalPalette)`、`RequestClear(ClearKind)`）；主题切换（§11.1）和 ⌘K（06 §2）都需要。
3. research/04 建议光标闪烁由渲染线程 `CompositionNow` 驱动；本文改为 UI 线程按相位唤醒，以满足空闲 CPU 目标（§10.2），需确认。
