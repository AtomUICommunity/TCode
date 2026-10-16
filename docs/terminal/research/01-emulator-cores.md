# 终端模拟核心选型调研（01）

状态：调研完成（2026-10-16），待决策
范围：终端模拟核心，即 VT 解析、屏幕与光标状态、回滚缓冲、resize reflow，以及选择和搜索辅助。PTY、SSH、渲染器、输入法由其他调研负责，本文只在它们影响核心选型时提及。
关联文档：[architecture.md](../../architecture.md)（原计划使用 Iciclecreek.Avalonia.Terminal）、StreamMark [decisions.md](../../../../StreamMark/docs/decisions.md)（RaTeX 原生库 + 6 RID CI 的先例）

**标注约定**：

- 带来源键的结论（例如 `[G:render.h:42]`）为**已核实**，依据是阅读源码或头文件、官方元数据及 GitHub / NuGet / crates.io API 查询。
- 标 **[推断]** 的内容是基于已核实事实的推理或估算，没有实测。
- 仓库均为 2026-10-16 拉取的 `--depth 1` 快照，提交号见 §8。

---

## 1. 结论摘要

1. **libghostty-vt 是目前唯一同时满足以下三点的核心：功能完整、为自绘渲染器设计、提供 C ABI。**
   - 它的 render state API 直接提供：全局和逐行 dirty、跨帧稳定的行 ID（可按行缓存整形结果，与 StreamMark 按块缓存 `TextLayout` 的做法一致）、overscan（平滑滚动）、逐行选区区间、光标与调色板。还能一次取出整行 cell（`CELLS_RAW`），减少 FFI 调用 [G:render.h:42-200,456-460]。
   - 现成功能：Kitty 键盘编码器、paste 安全检查、OSC 133 prompt 导航和命令输出选区、含回滚的增量搜索、终端快照编码与恢复、回滚压缩。
   - 代价：头文件明确写着"API 未稳定、一定会变"，上游也没有打过 libghostty 版本 tag [G:vt.h:10,25][G:README.md:160,168]。我们需要 pin commit、自己维护绑定并跟随上游；构建需要 Zig 0.16.0 [G:build.zig.zon:6]。
2. **6 个 RID 已有人在 CI 中跑通。**
   - RoyalTerminal（MIT，.NET 10 + Avalonia 12.1.3）为 win / linux / osx × x64 / arm64 构建了 libghostty-vt，产物 1.7–2.1 MB；Linux 包因未 strip 为 9.7 MB [RT:release.yml][NUGET:RT-native]。
   - 但它在固定提交上打了 12 个正确性补丁，多数是 render state 漏标 dirty [RT:ghostty-vt-extensions/README.md:30]。这说明 C API 层还年轻，我们要有自己的差分测试和快照测试。
3. **.NET 生态中最强的纯托管核心是 XTerm.NET。**
   - 它是 Iciclecreek.Avalonia.Terminal 的底层，自称 xterm.js 的 C# 移植，MIT 许可，2026-10-14 仍在发版 [IC:README.md:15][XN:csproj]。
   - 功能面广：Kitty 键盘、2026 / 2027、OSC 133、sixel / Kitty 图形、reflow。代码中没有反射。
   - 不足：单人主导；每个 cell 24 字节；没有逐行 dirty 和行 ID；grapheme 表是进程级静态表，永不回收，上限 10 万条 [XN:ClusterTable.cs:40-43]。
   - 适合作**保底**，不适合作首选。
4. **其余原生核心都有明显缺口。**
   - **alacritty_terminal**：质量高，但没有 C API，也没有 grapheme 聚类（ucs-detect 37 分）。不支持 OSC 7 / 133 和图形协议，Kitty 键盘的编码逻辑在 alacritty 应用里，不在这个 crate 里 [AL][VTE:ansi.rs:1350-1521][UCS]。
   - **wezterm-term**：功能全，但没有发布到 crates.io，WezTerm 正式版停在 2024-02 [CRATES][GH-API]。
   - **libvterm**：上游停在 0.3.3（2023-08）。不支持 Kitty 键盘、2026、2027、OSC 133，回滚由宿主自己存 [LV]。
   - **Windows Terminal TerminalCore**：依赖 WinRT 和 HRESULT，不能单独复用 [MSWT]。
5. **xterm.js + WebView 不适合 TCode。**
   - Avalonia.Controls.WebView 12.1.1 是 MIT 许可，并声明 AOT 兼容。但它在 Windows / macOS 上嵌入的是原生子窗口，窗口内的覆盖层会被遮挡；Linux 默认需要 WPE 运行库 [WV]。
   - xterm.js 稳定版 6.0.0 还没有 Kitty 键盘（2026-01 才进 master），ucs-detect 只有 26 分；仓库有 26 个未关闭的 IME issue，其中包括中文输入法丢字 [XJ][UCS][GH-API]。
   - 这与"自绘、与原生 UI 融合、中文输入"的目标冲突。
6. **纯 C# 自研**要做到"行为与系统终端一致"，估计需要 16–25K 行实现加同等规模的测试，约 5–7 人月，之后还有持续的长尾兼容问题 **[推断]**（§4.8）。

**推荐路线 B：libghostty-vt + 自己维护的 C# 绑定（LibraryImport）+ 自己构建 6 个 RID 的原生库。渲染器只依赖我们自己的 C# 门面接口。XTerm.NET 作为路线 B 受阻时的托管保底，不预先做双引擎。** 详见 §6。

---

## 2. 评估口径

核心问题：**自绘的 Avalonia 渲染器能否每帧低成本地拿到 cell 级屏幕状态和变化信息。** 具体要求如下：

| 需求 | 说明 |
|---|---|
| cell 级读取 | 码点或 grapheme、宽度（宽字符及其 spacer）、前景 / 背景 / 下划线颜色、SGR 属性、超链接、语义类型（prompt / input / output） |
| 变化信息 | 整帧是否变化、哪些行变化、行身份是否保持（用于复用每行的整形结果） |
| 视口 | 视口在回滚中的位置、滚动条数据、平滑滚动时需要的额外行 |
| 同步输出 | mode 2026 期间冻结在程序希望显示的那一帧 |
| 线程 | PTY 读线程写入、UI 线程渲染，锁的持有时间要短 |
| AOT | P/Invoke 用 `LibraryImport`，回调用 `[UnmanagedCallersOnly]`，不用反射 |

---

## 3. 对比表

### 3.1 基本信息与维护状况

| 候选 | 形态 | 许可证 | 最新版本（日期） | 最近 commit | 维护状况 |
|---|---|---|---|---|---|
| **libghostty-vt** | Zig 实现，C ABI（约 207 个导出函数） | MIT | 未打 libghostty tag。Ghostty 应用 v1.3.1（2026-03-13），主干为 1.3.2-dev | 2026-10-15；`include/ghostty/vt` 近 90 天 82 次提交 | 非常活跃，Ghostty 官方 [G][GH-API] |
| **XTerm.NET**（+ Iciclecreek） | 纯 C# | MIT | XTerm.NET 3.0.0 / Iciclecreek 4.1.0（均为 2026-10-14） | 2026-10-14 | 活跃；作者 2 人（tomlm 主导）[XN][IC][NUGET] |
| **RoyalTerminal** | C# 全栈：libghostty-vt（打过补丁）+ 托管 VT 双引擎 | MIT | 0.6.3（NuGet） | 2026-10-09 | 活跃；公司项目（Royal Apps），28★，2026-02 创建 [RT][GH-API] |
| DeBlasis.GhosttyVt（libghostty-vt-dotnet） | C# 绑定 | MIT | 1.3.2-ci.*，每天跟随 ghostty main | 2026-10-16 | 个人项目，2★，只有 3 个 RID [DN] |
| Dotty | 纯 C#（含 Avalonia 控件） | Apache-2.0 | — | 2026-07-05 | 低频 [DT] |
| XtermSharp | 纯 C#（xterm.js 移植） | MIT | NuGet 1.0.0-alpha.10 | 2020-12-04 | 停滞 [XS][GH-API] |
| VtNetCore | 纯 C# | MIT | 1.0.30 | 2022-10-16 | 停滞 [VN][GH-API] |
| **alacritty_terminal** | Rust crate，无 C API | Apache-2.0 | 0.26.0（2026-04-06） | 2026-10-16 | 活跃；Zed 使用其 fork [AL][CRATES][ZED] |
| wezterm-term / termwiz | Rust，无 C API；wezterm-term 未发布到 crates.io | MIT | termwiz 0.23.3（2025-03）；WezTerm 正式版 20240203 | 2026-10-10 | 维持中，节奏放缓 [WZ][CRATES][GH-API] |
| libvterm | C99 | MIT | 0.3.3（2023-08） | neovim 已把它 vendor 进 `src/nvim/vterm` 并自行修改；neovim/libvterm 镜像已归档 | 上游停滞 [LV][NV] |
| contour vtbackend | C++20，无 C API | Apache-2.0 | v0.7.0（2026-08-17） | 2026-10-16 | 活跃，主要由一人维护 [CT] |
| **xterm.js** | TypeScript，需 WebView | MIT | 6.0.0（2025-12-22）；beta 6.1.0-beta.315 | 2026-10-13 | 非常活跃 [XJ][NPM] |

### 3.2 协议与功能覆盖

图例：✅ 内置；⚠️ 部分支持或需要 addon / 宿主补齐；❌ 不支持。

| 候选 | truecolor | OSC 8 | OSC 52 | OSC 133 | OSC 7 | Kitty 键盘 | 2026 同步输出 | 2027 grapheme | sixel | Kitty 图形 | reflow |
|---|---|---|---|---|---|---|---|---|---|---|---|
| libghostty-vt | ✅ | ✅ | ✅ 读写回调 | ✅ 回调 + prompt 导航 + `select_output` | ✅ 7 / 9 / 1337 | ✅ 编码器，5 个 flag | ✅ render hold 回调 | ✅ | ❌ | ✅ 需宿主提供 PNG 解码 | ✅ 主屏（alt 屏不 reflow） |
| XTerm.NET | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| alacritty_terminal | ✅ | ✅ | ✅ | ❌ | ❌ | ⚠️ 只跟踪模式，编码不在 crate 内 | ✅（vte，150 ms 超时） | ❌ 逐码点 unicode-width | ❌ | ❌ | ✅ |
| wezterm-term | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| libvterm 0.3.3 | ✅ | ❌（neovim 分支自行添加） | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ 每格最多 6 个码点 | ❌ | ❌ | ⚠️ 有，但回滚由宿主保存 |
| xterm.js 6.0 | ✅ | ✅ | ⚠️ addon | ⚠️ 需自己注册 OSC handler | ⚠️ 同左 | ⚠️ 只在 master | ✅ | ⚠️ 实验 addon | ⚠️ addon-image | ⚠️ 只在 master | ✅ |

来源：

- libghostty-vt：[G:terminal.h:84-105（effects 表）, 2391, 3023][G:key/encoder.h:57,104][G:modes.h][G:kitty_graphics.h]；没有 sixel 的依据是 `src/terminal` 下只有 DA 常量里出现 sixel。
- XTerm.NET：[XN:README.md:20-40][XN:TerminalMode.cs:135,251][XN:InputHandler.Osc.cs:111]。
- alacritty_terminal：[AL:term/mod.rs:14,81][VTE:ansi.rs:36-48,1350-1521][AL:alacritty/src/input/keyboard.rs:298]。
- wezterm-term：[WZ:escape-parser/osc.rs:41-53][WZ:term/terminalstate/{kitty,sixel,iterm}.rs]。
- libvterm：[LV:vterm.h:24,541-571][LV:state.c:828-908,1777-1803]。
- xterm.js：[XJ:InputHandler.ts:298,2036][XJ:addons/*]；Kitty 键盘于 2026-01-09 合入 master（2265fa2ad）。

### 3.3 集成维度（与自绘渲染器和 TCode 硬约束相关）

| 候选 | cell 级 API | dirty / 行身份 | 线程模型 | NativeAOT | 6 RID 构建 | 原生产物 | 每 cell 内存 | 一致性测试 / Unicode |
|---|---|---|---|---|---|---|---|---|
| libghostty-vt | render state 行迭代器：逐 cell `get` / `get_multi`，或整行 `CELLS_RAW`（packed u64，位布局从 `ghostty_type_json` 读取） | 全局 FALSE / PARTIAL / FULL + 逐行 dirty + 行 ID + `next_dirty` 迭代器 | 库内不建线程；单个 terminal 不线程安全；回调在写入线程同步执行，禁止重入；render state 分两阶段更新，只有 begin 阶段需要持锁 | C ABI → `LibraryImport` + `UnmanagedCallersOnly`，可动态或静态链接 | Zig 交叉编译。上游 CI 覆盖 macOS ×2、Linux ×2、x86_64-windows-gnu，并在 Windows runner 上原生构建；win-arm64 不在上游 CI 中 | 1.7–2.1 MB | 8 B，样式按页去重 | 上游 CI 跑 esctest（只报告，失败不阻断构建）+ AFL++ fuzz；Ghostty 应用 ucs-detect 93 分 |
| XTerm.NET | 托管 `BufferLine` / `BufferCell`（struct），直接读取 | 无逐行 dirty，只有 `Scrolled` / `LineFed` / `BufferChanged` 等事件 | 无锁，由宿主串行化（Iciclecreek 用 `_terminalLock`） | 纯托管，无反射；未声明 `IsAotCompatible`，需要实测 | 无需原生构建 | — | 24 B + 行对象 | 自带 vttest 无头驱动（与 tmux 对照）；ucs-detect 截图各项 100% |
| alacritty_terminal | `renderable_content()` 迭代器，需要自写 C ABI，把数据拷成平面数组 | `TermDamage::Full`，或 `Partial`（行 + 左右列范围） | `Term` 不是 Sync；crate 自带 EventLoop 线程、FairMutex，以及 tty 模块（含 ConPTY） | 自写 cdylib，与 RaTeX 同一路线 | cargo-zigbuild（RaTeX 已验证） | **[推断]** 1–2 MB | 24 B（有测试断言 ≤ 24） | 45 个 ref 录制测试（含 vttest 场景）；ucs-detect 37 分 |
| wezterm-term | `Line` / `Cell`（Rust），需要自写 C ABI | 每行一个 SequenceNo（`changed_since`） | 单线程 | 同上 | 同上，但只能用 git 依赖 | **[推断]** 2–4 MB | 24 B | ucs-detect 71 分 |
| libvterm | `vterm_screen_get_cell`，逐 cell 读取 | damage 回调（矩形，可合并） | 单线程，回调式 | C，最简单 | 任何 C 编译器（zig cc） | **[推断]** < 200 KB | 由宿主决定 | 45 个 `.test` 脚本 |
| xterm.js | 不对原生渲染器开放，在 DOM / WebGL 里自己渲染 | — | JS 单线程 | 取决于 WebView 包（声明 AOT 兼容） | 无需原生构建，但 Linux 需要 WPE 或 WebKitGTK 系统库 | 依赖 WebView 运行时 | 12 B（3 × uint32） | ucs-detect 26 分 |

来源：

- libghostty-vt：[G:render.h][G:terminal.h:79-82,3284][G:page.zig:2137][G:test.yml:610-619,936-951][G:test/esctest/README.md][NUGET:RT-native]。
- XTerm.NET：[XN:BufferCell.cs:25][XN:docs/perf-results.md][IC:TerminalView.Input.cs:1018][IC:compliance.png]。
- alacritty_terminal：[AL:term/mod.rs:178,458,637][AL:cell.rs:308-313][AL:tests/ref]。
- wezterm-term：[WZ:wezterm-cell/lib.rs:1033][WZ:wezterm-surface/line.rs:283]。
- xterm.js：[XJ:BufferLine.ts:20]。
- ucs-detect 分数：[UCS]。注意这是**整机终端**的测试结果（含其渲染器和配置），不是核心库的单元测试。

### 3.4 吞吐数据（公开可查的部分）

| 对象 | 数据 | 来源 / 口径 |
|---|---|---|
| Ghostty 解析器 | SIMD 路径处理 ASCII 比标量路径快 7.3 倍，接近 memcpy 基线；UTF-8 解码快 16.6 倍（M3 Max）。只有相对值，没有 MB/s | [DEV6] |
| XTerm.NET | 240 × 67 headless：scroll-ascii 126.4 MiB/s，unicode 64.6 MiB/s；字节入口 scroll-ascii 594.2 MiB/s（两张表口径不同）；150 MB cat 端到端（经 Avalonia 渲染）119.2 MiB/s，此前的 1.1.0 只有 9.3 MiB/s | [XN:docs/perf-results.md:13-20,38-45,77-84]。这些优化来自 John Campion Jr 的性能 fork，文档已在上游仓库中 |
| 整机终端 | kitty 0.33 的 benchmark：ASCII 121.8 / alacritty 0.13.1 43.1 / wezterm 20230712 16.4 MB/s（2024 年左右数据，不含 Ghostty）；社区对 Ghostty 1.3.x 的 cat 测试结果互相矛盾 | [KP][WEB-BENCH] |

**[推断]** 公开数据大多是整机测试（PTY + 渲染），无法横向比较核心本身。选定方案后，应在独立库里建立 headless 吞吐基准（参考 XTerm.NET 的 Bench 语料：scroll-ascii / sgr-churn / truecolor / alt-redraw / unicode / flood），并与 libghostty-vt 原生基线对比。

---

## 4. 各候选详述

### 4.1 libghostty-vt（Ghostty）

#### 4.1.1 定位与状态

- **定义**：从 Ghostty 中抽出的 C 库，"实现现代终端模拟器的核心部分：解析转义序列、维护终端状态、编码输入事件；处理回滚、换行、resize reflow" [G:vt.h:15-23]。
- **稳定性声明**：
  - 头文件写明："WARNING: This is an incomplete, work-in-progress API. It is not yet stable and is definitely going to change." [G:vt.h:10]
  - README 写明功能本身很稳定（已在 Ghostty GUI 中长期验证），"but the API signatures are still in flux"；"We haven't tagged libghostty with a version yet" [G:README.md:158-170]。
  - 2025-09-22 的公告博客曾计划约半年内发布带 tag 的 libghostty-vt [BLOG]。到 2026-10 仍未发布，比计划晚了约 7 个月。
- **变动频率**：`include/ghostty/vt` 共 242 次提交，最近 90 天 82 次（2026-07-16 至 2026-10-16），最近 5 次在 10-10 至 10-14 之间，包括 CSI 8 t、prompt 导航、paste 保护等改动 [GH-API]。
- **为 ABI 兼容所做的设计**（在头文件中核实）：
  - 所有枚举都以 `int` 为底层类型，并带 `*_MAX_VALUE = INT_MAX` 哨兵 [G:types.h]。
  - 23 个结构体采用 sized-struct 模式（首字段 `size`，用 `GHOSTTY_INIT_SIZED` 初始化）。
  - 部分结构体标注 "frozen layout"。
  - `ghostty_type_json()` 返回当前构建的类型清单，包含偏移、大小、枚举值、packed 位布局。官方明确要求绑定从清单读取，不要硬编码，并说明"这些布局不承诺跨版本稳定" [G:types.h:355-410]。
  - **[推断]** 上游在为将来的 ABI 稳定做准备，但现阶段一切以 pin 住的 commit 为准。

#### 4.1.2 API 能力总览

`vt.h` 汇总了 30 个子头文件（另有 `key/`、`mouse/` 下 4 个），合计约 14K 行（注释很多）[G:include/ghostty/vt/*]。

| 分组 | 头文件 | 能力 | 主要 API |
|---|---|---|---|
| Terminal | terminal.h（3455 行） | 创建 / 释放 / RIS；`vt_write`（输入不可信，永不失败）；`vt_write_until_ground`（在序列边界插入带外序列）；resize（主屏 reflow，同时更新像素尺寸，解除 2026，发送 2048 尺寸报告）；视口滚动（按行、按页、**按 prompt**）；48 个选项、42 个可查询数据项（光标、模式、滚动条、标题、pwd、颜色、鼠标形状、内存占用等） | `ghostty_terminal_new/free/reset/resize/set/get/get_multi/vt_write/scroll_viewport` |
| Effects | terminal.h:56-105 | 副作用全部通过回调交给宿主：write_pty、bell、title、pwd、ENQ、XTVERSION、尺寸查询、颜色方案查询、DA、剪贴板读写（OSC 52 / 1337 / 5522）、桌面通知（OSC 9 / 777）、进度（OSC 9;4）、程序状态（OSC 7501）、未知 APC / OSC 透传、**render hold（2026）**、**semantic prompt（OSC 133）**、RIS、CSI 8 t | `GHOSTTY_TERMINAL_OPT_*` |
| 颜色主题 | terminal.h:172-232 | 前景 / 背景 / 光标 / 256 色调色板分两层："默认值"由宿主设置，"覆盖值"由程序通过 OSC 4 / 10 / 11 / 12 设置；可分别读取实际值和默认值 | `OPT_COLOR_*`、`DATA_COLOR_*` |
| Render state | render.h（1104 行） | 见 §4.1.3 | `ghostty_render_state_*` |
| 屏幕类型 | screen.h | `GhosttyCell` 是 packed `uint64_t`；内容 tag（码点 / grapheme / 只有背景色）；宽度属性（narrow / wide / spacer_tail / spacer_head）；语义类型（output / input / prompt）；超链接与保护标记；行的 wrap / continuation / prompt 等标记 | `ghostty_cell_get`、`ghostty_row_get` |
| 样式 | style.h | `GhosttyStyle`：前景、背景、下划线颜色，bold / italic / faint / blink / inverse / invisible / strikethrough / overline，以及下划线样式 | `ghostty_style_default/is_default` |
| Grid ref | grid_ref.h、grid_ref_tracked.h | 非跟踪引用（快照）和跟踪引用（跟随滚动、回滚裁剪、reflow）；文档注明不适合放在渲染主循环里 | `ghostty_terminal_grid_ref(_track)` |
| 选区 | selection.h（1086 行） | 选全部 / 行 / 词 / 两点间的词 / **命令输出**；调整、包含判断、格式化复制；矩形选区；**手势状态机**（按下、拖动、释放、自动滚动、deep press） | `ghostty_terminal_select_*`、`ghostty_selection_gesture_*` |
| 搜索 | search.h（502 行） | 覆盖主屏、alt 屏及回滚；能处理 resize / reflow / 重置 / 裁剪；`tick` 不碰终端，**可以放到工作线程**；`feed` 需要独占终端；匹配结果就是选区 | `ghostty_search_new/feed/tick/run/get` |
| Formatter | formatter.h | 把终端内容输出为纯文本、VT 序列或 HTML | `ghostty_formatter_*` |
| 快照 | snapshot.h（616 行） | 带 CRC 的二进制快照，包括未完成的解析器输入；先恢复到 READY（可以开始渲染），再增量补齐更早的回滚 | `ghostty_snapshot_encode/decoder_*` |
| 回滚压缩 | terminal.h:41-52, 3248-3300 | 由宿主驱动的增量压缩：宿主根据活动令牌计时，空闲时分步执行；对逻辑内容透明 | `ghostty_terminal_compression_activity/compress` |
| 键盘编码 | key/encoder.h、key/event.h | 支持 legacy 和 Kitty 协议（5 个 flag）、cursor / keypad application 模式、alt 发 ESC 前缀、macOS option-as-alt、backarrow；可直接从终端同步模式（`setopt_from_terminal`） | `ghostty_key_encoder_*` |
| 鼠标 / 焦点 | mouse/*.h、focus.h | X10 / normal / button / any 跟踪；SGR / UTF-8 / urxvt / SGR-pixels 编码；焦点进出报告 | `ghostty_mouse_encoder_*`、`ghostty_focus_encode` |
| Paste | paste.h | `ghostty_terminal_paste` 按终端当前状态决定行为：Kitty 5522 paste 事件、bracketed paste、未包裹时 `\n` → `\r`；可能注入命令的内容返回 `GHOSTTY_REJECTED`，由宿主确认后再带 `allow_unsafe` 重试；也有不依赖终端的 `is_safe` / `encode` | [G:paste.h:16-60] |
| OSC / SGR 解析器 | osc.h、sgr.h | 可单独使用的 OSC 解析器（28 种命令）和 SGR 解析器 | `ghostty_osc_*`、`ghostty_sgr_*` |
| Unicode | unicode.h | 与终端排版一致的码点宽度和 grapheme 宽度，可用来**预测 IME 预编辑文本的列宽** | `ghostty_unicode_codepoint_width/grapheme_width` |
| Kitty 图形 | kitty_graphics.h（871 行） | 遍历 placement、读取图片像素、计算渲染矩形；需要宿主通过 `ghostty_sys_set(GHOSTTY_SYS_OPT_DECODE_PNG)` 注入 PNG 解码器 | `ghostty_kitty_graphics_*` |
| 其他 | allocator.h、sys.h、build_info.h、modes.h、size_report.h、color.h | 自定义分配器、日志、构建信息（SIMD / Kitty 图形 / tmux control mode / 版本）、43 个模式（含 2004 / 2026 / 2027 / 2031 / 2048 / 5522）、颜色解析 | — |

#### 4.1.3 Render state：给自绘渲染器的接口

关键设计 [G:render.h:22-200]：

- **只在 update 调用期间需要访问终端。** 两阶段更新：持锁执行 `begin_update`，解锁，再执行 `end_update`（只用 render state 自己的内存）。这样 IO 线程几乎不会被渲染阻塞。
- **两层 dirty。** 全局状态为 `FALSE` / `PARTIAL` / `FULL`，另有逐行 dirty，两者由调用方分别清除，`ghostty_render_state_clean()` 一次清掉两层。`row_iterator_next_dirty` 只遍历变化的行。
- **行身份（Row Identity）。** 每行有一个跨帧稳定、永不复用的 ID，滚动时 ID 跟着行走。只要"ID 存在且该行不 dirty"，就可以复用缓存的整形结果或纹理。dirty 判断偏保守，例如视口滚动后目前所有行都会被标为 dirty。
- **Overscan。** 可以请求视口上下各多抓几行，用于亚行级平滑滚动，`VIEWPORT_Y` 用有符号值表示行的位置。
- **Render hold（2026）。** 在回调中更新 render state，抓到程序希望显示的那一帧；hold 期间不再更新；超时（建议 1 秒）由宿主自行解除。
- **批量读取。** `ROW_DATA_CELLS_RAW` 返回一整行 `GhosttyCell` 的借用视图（`ptr` + `len`），注释说这是给"调用边界开销大"的宿主准备的；位布局要从 `ghostty_type_json` 解析 [G:render.h:446-460]。

摘录：

```c
// 每帧（UI 线程）
lock(term);  ghostty_render_state_begin_update(rs, term);  /* 同时读 DATA_SCROLLBAR */  unlock(term);
ghostty_render_state_end_update(rs);
ghostty_render_state_get(rs, GHOSTTY_RENDER_STATE_DATA_DIRTY, &dirty);       // FALSE / PARTIAL / FULL
ghostty_render_state_get(rs, GHOSTTY_RENDER_STATE_DATA_ROW_ITERATOR, &rows);
while (ghostty_render_state_row_iterator_next_dirty(rows, &y)) {
    ghostty_render_state_row_get_multi(rows, n, keys /*ID, VIEWPORT_Y, SELECTION, CELLS_RAW*/, vals, NULL);
    // 有样式的 cell：row_cells_select(x) + get(STYLE / FG_COLOR / BG_COLOR / GRAPHEMES_UTF8)
}
ghostty_render_state_clean(rs);
```

**[推断] FFI 开销。**

- 200 × 60 视口全量刷新时，每行只需 3–4 次调用；最坏情况每个有样式的 cell 再加 2 次，合计约 2.5 万次调用。
- `LibraryImport` 的无封送 blittable 调用开销在个位纳秒级，每帧约 0.05–0.25 ms，在 16.6 ms 帧预算内。
- 如果 profile 显示瓶颈，可以像 RoyalTerminal 那样加一个 Zig shim，导出"一次调用拷出整行的平面结构体数组"。

#### 4.1.4 构建、平台与产物

| 项 | 事实 |
|---|---|
| 工具链 | Zig **0.16.0**（`minimum_zig_version`）[G:build.zig.zon:6]。Zig 0.17.0 已于 2026-10-01 发布 [ZIG]；RoyalTerminal 记录"需要 Zig 0.16.0 恰好这个版本，升到 0.17 需要协调的 ABI 和行为评审"[RT:build-test-release.md:38] |
| 构建命令 | `zig build -Demit-lib-vt`，同时产出 shared 与 static 库；也提供 CMake 包装（内部调用 zig），可用 `ghostty_vt_add_target(... ZIG_TARGET x86_64-linux-gnu)` 交叉编译 [G:CMakeLists.txt:1-60] |
| 依赖 | vt 模块用 simdutf、Highway（SIMD，`no_libcxx`）、Wuffs（Kitty 图形像素处理）、uucode，**不用** oniguruma；CI 断言 `.so` 不依赖 libc++ [G:GhosttyZig.zig:72-156][G:test.yml:464-469] |
| 静态库 | 打包 compiler_rt 和 ubsan_rt，启用 PIC；Windows 静态库需要链接 ntdll / kernel32，MSVC 下关闭 stack protector [G:GhosttyLibVt.zig:239-271] |
| 上游 CI 目标 | aarch64 / x86_64 macOS、aarch64 / x86_64 Linux、x86_64-linux-musl、x86_64-windows-gnu；"x86_64-windows-msvc 交叉编译还不行（找不到 msvc 头文件）"，改在 Windows runner 上原生构建并运行 `test-lib-vt`；另有 wasm、freestanding、android、xcframework [G:test.yml:610-648,936-951] |
| win-arm64 | **不在上游 CI 中**。RoyalTerminal 在 Windows runner 上用 `-Dtarget=aarch64-windows-gnu` 构建 [RT:release.yml:264] |
| Windows x64 兼容性 | RoyalTerminal 发现默认构建可能在启动阶段就执行 AVX 指令，在不支持 AVX 的虚拟机或模拟环境中直接崩溃，所以发布版改用 `-Dcpu=x86_64-vzeroupper -Dsimd=false` 的标量构建，并在 CI 中反汇编确认没有 AVX [RT:windows-x64-native-compatibility.md:24-45] |
| 产物大小 | RoyalTerminal 0.6.3 的包：osx-arm64 1.94 MB、osx-x64 2.07 MB、win-x64 1.72 MB（标量）、win-arm64 1.74 MB；linux-x64 / arm64 约 9.7 MB（`file` 显示 with debug_info, not stripped）。导出约 220 个 `ghostty_*` 符号（含 RoyalTerminal 自己的扩展）[NUGET:RT-native] |
| 许可证 | MIT [GH-API]。依赖是否均为宽松许可，**[推断]** 需在发布时逐一核对，并附第三方声明 |

#### 4.1.5 一致性与质量保障

- `test/esctest`：在 PTY 下用 libghostty-vt 无头运行 esctest2，并把终端的回应（DECRQCRA 校验和、DA、光标位置等）写回。CI 的 `test-esctest` job **只报告，不因失败阻断构建** [G:test/esctest/README.md]。
- `test/fuzz-libghostty`：AFL++ 覆盖 OSC 解析器、VT 解析器和完整 stream（SIMD 与标量两条路径）[G:test/fuzz-libghostty/README.md]。
- `src/terminal` 共约 18.4 万行 Zig，含数千个内联测试 [G]。
- ucs-detect（Ghostty 1.3.1 整机）：总分 93，WIDE / NARROW / LANG / ZWJ / VS16 / VS15 全部 100，支持 2027 [UCS]。

#### 4.1.6 已有绑定与使用者

- **.NET**：
  - RoyalTerminal.GhosttySharp（MIT，全部为 `LibraryImport`，但链接的是打过补丁的构建）[RT]。
  - DeBlasis.GhosttyVt（`IsAotCompatible=true`，每天跟随 main，只有 win-x64 / linux-x64 / osx-arm64）[DN:README][DN:Ghostty.Vt.csproj:8]。
  - LibGhostty.Net（NuGet，没有源码仓库）[NUGET]。
- **其他语言**：go-libghostty（mitchellh，155★）、libghostty-rs（394★）、libghostty-cpp、Dart / Flutter、Python、Elixir、Node-API（coder）、Odin、MoonBit 等 [AW][GH-API]。
- **参考实现**：Ghostling，用约 2000 行 C（单个 `main.c`，68 KB）加 raylib 实现的最小终端 [AW][GH-API]。
- **使用者**：awesome-libghostty 收录约 130 个项目，包括大量面向 AI Agent 的终端（cmux、Supacode 等），以及 Windows 上的 phantty / mightty [AW]。

#### 4.1.7 RoyalTerminal 发现的问题（对路线 B 的风险提示）

RoyalTerminal 在 pin 住的 `b40acce5` 上通过构建时生成 overlay 的方式应用了 12 处修正（不改 submodule；每处修正都校验原文件 SHA-256 和片段数量）[RT:ghostty-vt-extensions/README.md:28-80]。例如：

- mode 2027 关闭时追加零宽字符，没有标记光标行 dirty，导致 render state 只显示基字符。
- 覆盖软换行宽字符的任一半时，前一行没有标记 dirty。
- OSC 133 和隐式续行修改了行元数据，但没有标记 dirty。
- Kitty 动画删除帧时索引错误。
- 流续传导出时没有规范化 APC→C1 的切换。

这些问题是否已在当前 HEAD（b3dc3d7）修复，**未核实**。**[推断]** render state 这层的 dirty 正确性还在打磨，路线 B 需要自己做"增量渲染结果 vs 全量重建结果"的差分测试。

### 4.2 .NET 生态

#### 4.2.1 Iciclecreek.Avalonia.Terminal / XTerm.NET（architecture.md 原计划）

- **构成**：Iciclecreek 是 Avalonia 控件（`TerminalControl` / `TerminalWindow`，含渲染、输入、PTY 接线），模拟核心是 **XTerm.NET**，PTY 是 **Porta.Pty** [IC:README.md:15]。三者作者相同（tomlm，另有 John Campion Jr 参与），均为 MIT。
- **依赖**：Avalonia 12.0.4、Avalonia.Skia、SkiaSharp.HarfBuzz 3.119.4、Unicode.net 2.0.0、Wcwidth 4.0.1、Porta.Pty 2.2.3、XTerm.NET 3.0.0 [IC:csproj:39-48]。没有声明 `IsAotCompatible`；Porta.Pty 声明了 `IsAotCompatible=true` [PP:csproj:11]。
- **渲染方式**：以 `GlyphRun` 快速路径为主，回退到 `FormattedText`；图片用 Skia `ICustomDrawOperation` 图层；多个终端共享节流帧，**默认 30 FPS** [IC:TerminalView.Render.cs:1689-1900][IC:README.md:219]。它是一个完整控件，与我们"自绘渲染层 + 独立库"的计划重叠。如果采用，实际可用的只有其中的 XTerm.NET。
- **XTerm.NET**：
  - 约 2.9 万行 C#，测试约 3.3 万行，net10.0。
  - 代码分布：InputHandler 共 9.1K、Buffer 4.0K、Common 4.1K、Graphics 3.5K、Parser 1.5K、Input 1.6K。
  - 功能：Kitty 键盘、左右边距、OSC 133（reflow 后仍保留）/ 8 / 52 / 9 / 99 / 777、sixel、Kitty 图形（含动画和 Unicode placeholder）、Kitty text sizing（OSC 66）、XTGETTCAP、mode 2027、宽度表与 python wcwidth 逐码点对齐 [XN:README.md:17-45]。
  - 代码中没有 `System.Reflection` 用法，也没有锁 [XN:grep]。
- **弱点**：
  - 没有逐行 dirty 和行 ID。
  - `BufferCell` 为 24 字节；grapheme 存在进程级静态 `ClusterTable`（两个 `ConcurrentDictionary`），永不回收，上限 10 万条，超过后只显示基字符 [XN:ClusterTable.cs:22-45]。
  - 单人主导，大版本有破坏性变更（2.0 删除了 per-cell 图片 API，3.0 改了 sixel 存储）[XN:README.md:47-140]。

#### 4.2.2 RoyalTerminal（royalapplications）

- **定位**：".NET 10 高性能终端栈"，包括与后端无关的 Avalonia 控件、Skia CPU 渲染器、HarfBuzz 整形、着色器，以及 PTY / SSH / Telnet / Serial 传输。VT 有两套：`GhosttyVtProcessor`（libghostty-vt）和 `BasicVtProcessor`（托管，约 1.65 万行，对照原生做差分测试）[RT:README.md][RT:architecture.md]。
- **规模**：GhosttySharp 1.26 万行、Vt.Ghostty 4.9K、Vt.Managed 16.5K、Avalonia 控件 16.8K、Skia 渲染 9.4K、Terminal 契约与屏幕模型 28.1K；共有 463 处 `LibraryImport`；全仓库**没有** `IsAotCompatible` 或 `PublishAot` 声明 [RT:grep]。
- **原生**：submodule pin ghostty `b40acce5`，加 overlay 和自有扩展（drag-drop、通知、窗口 resize 回调），6 个 RID 全部构建 [RT:ghostty-integration.md:16][RT:release.yml]。
- **启示**：
  1. 6 RID + Zig 0.16 已被验证可行。
  2. 它的 Ghostty 处理器每帧把 render state **拷贝进托管的 `TerminalScreen`**（`lock (_screen.SyncRoot)`）[RT:GhosttyVtProcessor.cs:999]，多了一次拷贝。
  3. 项目非常庞大（文档极长，**[推断]** 大量由 AI 生成），版本 0.x，28★，不适合作为依赖，适合作为参考。

#### 4.2.3 其他

| 项目 | 说明 |
|---|---|
| Dotty（starling-browser/dotty） | 纯托管引擎 + Avalonia 控件 + MCP，约 1.5 万行，Apache-2.0，最近提交 2026-07。awesome-libghostty 把它列为基于 libghostty 的项目，但在当前提交 1c0b397 的源码中找不到任何 ghostty 引用，按纯托管处理 [DT][AW] |
| XtermSharp | Miguel de Icaza 的 xterm.js C# 移植，master 最后提交 2020-12 [XS] |
| VtNetCore | 最后提交 2022-10；有派生版 AgentSpace.VtDotNet（readonly struct cell、环形回滚）[VN][NUGET] |
| Terminal.Emulation / Terminal.Avalonia（b-y-t-e） | 纯 C# VT500 引擎，0.4.4，仓库已不可访问 [NUGET] |
| VirtualTerminal（Rikitav）、CodeBrix.Terminal、AvaloniaTerminal、SvcSystems.UI.Terminal | 小众，下载量和星数都很低 [NUGET] |
| XenoAtom.Ansi / XenoAtom.Terminal | ANSI 生成和 tokenizer、Console 替代品，**不是**终端模拟器 [NUGET] |

### 4.3 alacritty_terminal（Rust）

- **基本情况**：crate 0.26.0（2026-04-06），Apache-2.0，约 1.17 万行，基于 `vte` 0.15 [AL:Cargo.toml:2-28][CRATES]。Zed 使用的是 zed-industries 的 **fork**（固定在某个 rev）[ZED]。
- **API 形状**：
  - `Term<T: EventListener>::new(config, dims, listener)`；用 `vte::ansi::Processor::advance(&mut term, bytes)` 喂入数据。
  - `term.damage()` 返回 `TermDamage::Full`，或 `Partial`（`LineDamageBounds{line,left,right}` 迭代器）。
  - `renderable_content()` 返回显示迭代器、选区、光标；`resize()` 做 reflow。
  - 事件枚举有 `Title`、`ClipboardStore/Load`、`ColorRequest`、`PtyWrite`、`TextAreaSizeRequest`、`Bell`、`ChildExit` 等，其中三项格式化器是 `Arc<dyn Fn>` [AL:term/mod.rs:178,410-660,2393][AL:event.rs:14-50]。
  - crate 自带 `event_loop`（PTY 读线程）、`sync::FairMutex`、`tty`（Unix 和 Windows ConPTY）。
- **Cell**：`{c: char, fg, bg, flags, extra: Option<Arc<CellExtra{zerowidth, underline_color, hyperlink}>>}`，≤ 24 B。零宽字符追加到 cell 上，没有 grapheme 聚类 [AL:cell.rs:132-160,308-313]。
- **缺口**：
  - OSC 只支持 0 / 2 / 4 / 8 / 10–12 / 50 / 52 / 104 / 110–112，**没有 7 / 133 / 9 / 777** [VTE:ansi.rs:1350-1521]。
  - 没有 2027、sixel、Kitty 图形。
  - Kitty 键盘只记录模式，**编码在 alacritty 应用里**（依赖 winit 类型），需要我们自己移植 [AL:alacritty/src/input/keyboard.rs:298-306]。
  - ucs-detect 37 分（ZWJ 1、VS15 0）[UCS]。
- **自写 C ABI 的工作量** **[推断]**：
  - Rust cdylib 约 2–3K 行：生命周期、写入、resize、事件回调（通过函数指针和 userdata）、按 damage 把视口 cell 拷进调用方提供的平面缓冲区、选区、`RegexSearch` 搜索、模式查询。
  - C# 绑定约 2K 行；移植键盘编码约 1K 行。
  - 补 OSC 7 / 133 / 通知需要 fork vte 或在字节流上预扫描。
  - 合计 5–8 周，6 RID 走 RaTeX 的 cargo-zigbuild 路线即可。之后要长期维护 fork，Zed 走的就是这条路。

### 4.4 wezterm-term / termwiz（Rust）

- `wezterm-term`（MIT）的 README 说明：完整的转义解析、键盘和鼠标编码、含回滚的屏幕模型、sixel 和 iTerm2 图片、OSC 8；不含 GUI 和 PTY；入口是 `Terminal::advance_bytes` [WZ:term/README.md][WZ:term/terminal.rs:164]。
- 另外已核实支持：OSC 7 / 133（`FinalTermSemanticPrompt`）/ 52 / 通知、Kitty 键盘、Kitty 图形、2026、grapheme（finl_unicode）、bidi [WZ:osc.rs:41-53][WZ:terminalstate/*.rs]。
- 变化跟踪：每行一个 `SequenceNo`（`Line::changed_since`）；`Cell` 24 B [WZ:line.rs:283][WZ:wezterm-cell/lib.rs:1033]。
- **问题**：
  - `wezterm-term` **没有发布到 crates.io**，只能用 git 依赖；termwiz 最新 0.23.3（2025-03）；WezTerm 正式版停在 20240203 [CRATES][GH-API]。
  - 依赖树较重（image、finl_unicode、wezterm-* 一组 crate）。
  - ucs-detect 71 分 [UCS]。
  - 自写 C ABI 的工作量与 alacritty 相当，但上游节奏放缓，维护风险更高 **[推断]**。

### 4.5 libvterm（C）

- 上游 0.3.3，文件时间戳为 2023-08-04；主仓库在 Launchpad（bzr）[LV]。neovim 已把它 vendor 进 `src/nvim/vterm` 并持续修改（最近一次 2026-10-06），`neovim/libvterm` 镜像已归档 [NV][GH-API]。
- **API**：
  - 状态层 `VTermState` + 屏幕层 `VTermScreen`。
  - 屏幕通过回调输出 `damage`（矩形，可用 `set_damage_merge` 合并）、`moverect`、`sb_pushline` / `sb_popline` / `sb_clear`（**回滚由宿主保存**）。
  - `vterm_screen_enable_reflow`；每格最多 `VTERM_MAX_CHARS_PER_CELL = 6` 个码点 [LV:vterm.h:14-24,439-571]。
- **缺口**：没有 Kitty 键盘、2026、2027、OSC 8（上游）、OSC 133、图形 [LV:state.c]。
- 约 5.8K 行，最容易构建。但要"行为与系统终端一致"，需要在它之上补大量功能，并接受上游停滞。

### 4.6 其他原生实现

| 候选 | 结论 |
|---|---|
| Windows Terminal TerminalCore | `Terminal.hpp` 依赖 `winrt::Microsoft::Terminal::Core`、`HRESULT`、`til/winrt.h` 以及控制台的 buffer / renderer / adapter，只能在 Windows 上构建，**不能单独跨平台复用** [MSWT]。Windows 上值得复用的是 ConPTY（`Microsoft.Windows.Console.ConPTY` NuGet），属于 PTY 调研 |
| contour vtbackend | C++20，Apache-2.0；ucs-detect 99 分（最高）；但没有 C API，依赖 GSL、boxed-cpp、libunicode 等，用 CMake 构建 6 RID 并自写 C 包装的成本高；主要由一人维护 [CT][UCS] |
| foot | 只支持 Wayland 的 C 应用，没有可嵌入的库 |
| kitty | GPL-3.0，C + Python，没有可嵌入的核心，许可证也不适合 Apache-2.0 的 TCode |

### 4.7 xterm.js + WebView（VS Code / Tabby / Hyper 方案）

- **xterm.js**：
  - 6.0.0（2025-12-22）为最新稳定版，beta 6.1.0-beta.315（2026-10-13）[NPM]。
  - `src/common` 约 1.53 万行，`src/browser` 约 1.3 万行，addons 约 1.62 万行 [XJ]。
  - Kitty 键盘（`KittyKeyboard.ts`）和 win32-input-mode 在 2026-01 合入 master，**稳定版没有** [GH-API]。
  - 默认回滚 1000 行，每 cell 12 B [XJ:OptionsService.ts:34][XJ:BufferLine.ts:20]。
  - ucs-detect（6.1.0 beta）26 分，2027 不支持 [UCS]。
- **Avalonia 12 的 WebView**：
  - 官方包 `Avalonia.Controls.WebView` 12.1.1（2026-10-12），MIT，依赖 Avalonia ≥ 12.0.0；README 声明兼容 AOT [WV:nuget]。
  - 平台引擎：Windows 用 WebView2；macOS 用 WKWebView；Linux 默认用 WPE WebKit（离屏 SHM 渲染，需要用户安装 `libwpewebkit-2.0` 等），也可改用 WebKitGTK [WV:docs]。
  - 实现方式：Win / macOS 走 `NativeControlHost`，离屏缓冲走 composition host [WV:README]。
  - JS 互操作：`InvokeScript(string)` 和 `WebMessageReceived`（`invokeCSharpAction`）[WV:docs]。
- **融合问题** **[推断]**，基于 NativeControlHost 的机制和上述事实：
  1. **遮挡（airspace）**：Win / macOS 上原生子窗口压在 Avalonia 内容之上。窗口内的覆盖层（AtomUI 的消息提示、拖拽预览、StreamMark 的 `OverlayLayer` 卡片）会被遮住；以独立窗口实现的 Popup 不受影响。
  2. **焦点与快捷键**：焦点进入 WebView 后，Avalonia 的 KeyBinding 收不到按键，需要在 JS 中拦截再桥接回来；Tab 焦点遍历要特殊处理。
  3. **IME**：xterm.js 依赖隐藏的 textarea。仓库有 26 个未关闭的 IME issue，其中有"豆包输入法在 macOS 英文模式下第二个字符丢失"、"首个提交字符丢失"、"日文输入法组合重复"等，均为 2026 年的报告 [GH-API]。
  4. **数据通路**：PTY 字节要经 C# → 字符串（base64）→ JS 往返，高吞吐时额外占用 CPU 并增加延迟。
  5. **主题**：AtomUI token 需要同步成 CSS 变量。
  6. **资源**：WebView2 会启动多个浏览器进程，冷启动约数百毫秒。
  7. **Linux 发行**：WPE 不是常见桌面发行版的默认组件。
- 变体：coder/ghostty-web（MIT，2975★）提供与 xterm.js API 兼容、基于 libghostty-vt WASM 的实现，可以缓解 Unicode 和功能缺口，但 WebView 带来的问题照旧 [AW][GH-API]。

### 4.8 纯 C# 自研：工作量估算

参考规模（只统计实现代码，测试单列）：

| 实现 | 语言 | 实现行数 | 测试 | 覆盖范围 |
|---|---|---|---|---|
| xterm.js `src/common` | TS | 15.3K（parser 2.2K、buffer 2.5K、input 2.1K、`InputHandler.ts` 3.7K） | 14.2K | 不含渲染；图形、grapheme、剪贴板在 addons 中（16.2K） |
| XTerm.NET | C# | 29.1K（InputHandler* 9.1K、Buffer 4.0K、Common 4.1K、Graphics 3.5K） | 33.0K | 含 sixel / Kitty 图形 |
| RoyalTerminal Vt.Managed | C# | 16.5K（另有契约和屏幕模型 28.1K） | — | 对标 libghostty-vt |
| alacritty_terminal | Rust | 11.7K（不含 vte） | 有 | 无图形，无 grapheme |
| wezterm term + escape-parser + surface + cell | Rust | 10.2K + 10.9K + 5.2K + 1.9K | 有 | 全功能 |
| libvterm 0.3.3 | C | 5.8K | 45 个脚本 | 无回滚存储 |
| Ghostty `src/terminal` | Zig | 184K，含内联测试、C API 25.5K、kitty 18.9K、snapshot 14.4K、formatter 7.6K；核心部分：Terminal 17.3K、Screen 12.8K、PageList 20.8K、page 4.5K、Parser 1.1K、stream 5.7K | 内联 | 全功能 |

TCode 自研的模块拆分与估算 **[推断]**，按 1 名熟练开发者计：

| 模块 | 内容 | 行数 | 人周 |
|---|---|---|---|
| Parser | DEC ANSI 状态机（Paul Williams）、跨块 UTF-8、C0 / C1、CSI 参数与子参数、OSC / DCS / APC 收集与长度上限 | 1.5–2K | 1–2 |
| State / Screen | 光标、约 45 个模式、G0–G3 字符集、制表位、上下和左右边距、SGR（下划线样式和颜色）、DECSC / DECRC、1047 / 1049、DECRQM、DSR / DA / XTVERSION / XTWINOPS | 6–9K | 6–9 |
| Buffer | 行存储、回滚环形缓冲、样式去重、grapheme 存储、软换行标记、reflow、宽字符 spacer 规则、逐行 dirty 和行 ID | 3–5K | 4–6 |
| Unicode | 从 UCD 生成宽度表和 grapheme 断句表的生成器、mode 2027 | 1–2K + 生成的表 | 2 |
| 输入编码 | legacy 键盘、modifyOtherKeys、Kitty 键盘（5 个 flag + 栈）、各种鼠标模式和编码、焦点、bracketed paste 与安全检查 | 2–3K | 2–3 |
| 外围功能 | 选区（词 / 行 / 矩形 / 命令输出）、含回滚的搜索、formatter、OSC 7 / 8 / 52 / 133 / 9 / 777 | 2–4K | 3–4 |
| 图形（可选） | sixel、Kitty graphics | 4–8K | 4–8 |
| 测试 | 单元测试 + 无头 vttest / esctest + ucs-detect | 约与实现等量 | 已计入上面 |
| **合计（不含图形）** | | **16–25K + 同量测试** | **约 20–30 人周（5–7 人月）** |

**[推断]** AI 辅助能压缩编码时间，但"与系统终端一致"的长尾（vim / tmux / htop / less / fzf / 各种 shell 集成、ConPTY 重写过的输出、CJK 和 emoji 边界情况）需要真实程序回归，无法省掉。

---

## 5. 可选路线

### A：纯 C# 自研核心

| | |
|---|---|
| 优点 | 零原生依赖，AOT 天然没问题；数据结构完全按自绘渲染器设计（行 ID、dirty、grapheme 存储与 StreamMark 风格一致）；调试只需一种语言 |
| 缺点 | 5–7 人月才能接近 XTerm.NET 或 libghostty-vt 的覆盖度；性能要靠长期优化（XTerm.NET 从 9.3 优化到 119 MiB/s 花了一个专门的性能 fork）；一致性问题只能自己发现 |
| 风险 | 进度不可控；长尾 bug 长期拖累"与系统终端一致"的目标 |
| 工作量 | 核心 20–30 人周，不含渲染、PTY、SSH **[推断]** |

### A′：以 XTerm.NET 为核心（fork 或直接依赖）+ 自绘渲染

| | |
|---|---|
| 优点 | 今天就能用；纯 C#；功能面广（Kitty 键盘、2026 / 2027、OSC 133、图形）；MIT；作者活跃；有 vttest 驱动和 ucs-detect 结果 |
| 缺点 | 没有逐行 dirty 和行 ID，要在外面自己做 diff 或补丁；每 cell 24 B；静态 `ClusterTable` 不回收；破坏性大版本频繁；没有 reflow 之外的高级功能（快照、回滚压缩、命令输出选区要自己做） |
| 风险 | 单人主导，可能停更；fork 后要自己维护约 2.9 万行代码 |
| 工作量 | 集成 2–3 周，加上 dirty 跟踪和 AOT 实测；长期维护成本中等 **[推断]** |

### B：libghostty-vt + 自维护 C# 绑定（推荐）

| | |
|---|---|
| 优点 | 功能和一致性最完整的可嵌入核心，Ghostty GUI 已长期验证其逻辑；render state 正是为自绘渲染器设计的（两阶段更新、两层 dirty、行 ID、overscan、render hold）；SIMD 解析；每 cell 8 B，回滚可压缩；内置对 AI 场景有用的功能：OSC 133 prompt 导航和命令输出选区、formatter（纯文本 / HTML）、快照恢复；MIT；6 RID 已被他人验证；生态活跃（约 130 个下游项目，其中 2 个 .NET 绑定） |
| 缺点 | API 未稳定、没有版本 tag，需要 pin commit 并定期升级；工具链要求 Zig 0.16.0 恰好这个版本；没有 sixel；跨 FFI 回调要注意线程和重入 |
| 风险 | ① API 变动导致绑定频繁调整。② render state dirty 的边界 bug（RoyalTerminal 已修 12 处）。③ Windows x64 需要用基线 CPU 或关闭 SIMD 构建，性能会下降。④ win-arm64 没有上游 CI。⑤ 上游方向由 Ghostty 主导，我们的需求只能通过 PR 或本地 overlay 解决 |
| 工作量 | 原生构建与 CI 约 1 周；v1 所需约 80 个函数的绑定 2–3K 行，1.5–2 周；C# 门面和线程模型 1–2 周；差分和一致性测试 1–2 周；**核心层合计 5–7 周**，不含渲染、PTY、SSH **[推断]** |

### C：alacritty_terminal + 自写 C ABI（Rust）

| | |
|---|---|
| 优点 | 成熟稳定，Zed 在用；Rust → C ABI → .NET 与 RaTeX 完全同一路线；API 简洁（`TermDamage` 有列范围）；crate 自带 PTY 和 ConPTY |
| 缺点 | 没有 grapheme 聚类（ucs-detect 37 分）、没有 OSC 7 / 133 / 通知、没有图形；Kitty 键盘编码要自己移植；C ABI 完全由我们设计和维护 |
| 风险 | 要补功能就得 fork vte 或 alacritty_terminal（Zed 就是这样做的），之后长期合并上游 |
| 工作量 | 5–8 周，补齐 grapheme、OSC 133 等还要另加数周 **[推断]** |

### D：xterm.js 跑在 WebView 中

| | |
|---|---|
| 优点 | 生态最大（VS Code 同款）；渲染、选择、搜索、链接都是现成的；不需要原生构建 |
| 缺点 | 与"自绘、原生融合"的目标相反；稳定版没有 Kitty 键盘；Unicode 分数低；IME 问题多（含中文）；Win / macOS 有遮挡问题；Linux 需要 WPE；数据要跨 JS 桥；每个 WebView 有独立进程开销 |
| 风险 | 体验不一致（主题、焦点、快捷键、输入法）且不受我们控制；Linux 发行和支持成本高 |
| 工作量 | 集成 3–4 周，之后是持续的融合问题 **[推断]** |

### E：B 为主 + 托管引擎保底（RoyalTerminal 模式）

| | |
|---|---|
| 优点 | 原生库加载失败（老 CPU、未知平台）时仍可用 |
| 缺点 | 两套引擎、两套语义，要做差分测试；工作量接近 A + B |
| 结论 | **不建议预先做**。只把核心抽象成接口，以后需要时再加一个托管实现（例如适配 XTerm.NET） |

---

## 6. 推荐及理由

**选择路线 B**。理由按 TCode 的硬约束逐条对应：

| 约束 | B 的满足情况 |
|---|---|
| 完整终端、行为与系统终端一致 | Ghostty 的核心就是一个完整终端的核心，并在 CI 中跑 esctest 和 fuzz；Unicode 表现接近最好（ucs-detect 93 分） |
| 自绘渲染、性能 | render state 的设计与我们的需求一一对应；行 ID 可以直接用作每行 `GlyphRun` 或 `TextLayout` 缓存的 key |
| NativeAOT 0 警告 | 只用 `LibraryImport`、blittable 结构体、`[UnmanagedCallersOnly]` 回调和 `delegate* unmanaged`，不需要反射；DeBlasis.GhosttyVt 已声明 `IsAotCompatible` |
| 6 RID | RoyalTerminal 已用 Zig 0.16 全部构建；我们的 CI 可沿用 StreamMark 的矩阵（每个 RID 在原生 runner 上构建，或在 macOS 上交叉编译） |
| 以后接入 SSH | 核心只接收字节流，与传输方式无关；快照 API 可用于断线重连后恢复画面 |
| AI Agent 场景 | OSC 133 语义区和 `select_output` 能准确取出"某条命令的输出"交给 Agent；formatter 可导出纯文本或 HTML |

**落地要点** **[推断]**，供后续设计文档细化：

1. **pin 上游 commit**，把它记录在仓库里（类似 DeBlasis 的 `ghostty-upstream.json`），按固定节奏（例如每月）升级。升级时：重新生成绑定；比对 `ghostty_type_json` 清单；运行差分和一致性测试。
2. **绑定分两层**：
   - 底层：从头文件生成的 `LibraryImport` 原始声明。
   - 门面：我们自己的稳定 C# API（`TerminalCore`、`RenderSnapshot` 等）。渲染器和控件只依赖门面，保留以后换核心（A′ 或 E）的可能。
3. **线程模型**：
   - PTY 读线程在持锁时调用 `vt_write`。`write_pty` 等回调只入队，不阻塞，不重入。
   - UI 帧回调里：持锁执行 `begin_update`，读滚动条，解锁；然后执行 `end_update`，遍历 dirty 行。
   - 搜索的 `tick` 放到工作线程；回滚压缩由空闲计时器驱动。
4. **原生构建**：
   - 优先**动态库**（`runtimes/<rid>/native/`，与 RaTeX 一致）。静态链接进 NativeAOT 可执行文件时，Zig 静态库自带的 compiler_rt 可能与工具链运行库冲突，留到以后评估。
   - Windows x64 先用基线 CPU 构建。是否启用 SIMD 在实测后决定（要么用运行时分派，要么接受标量性能）。
5. **测试**：
   - 无头 esctest / vttest 驱动（参考 Ghostty 的 `test/esctest` 和 XTerm.NET 的 `tools/vttest`）。
   - 每帧"增量渲染结果 = 全量重建结果"的差分快照测试，专门捕获 dirty 漏标。
   - ucs-detect 回归。
   - 与 libghostty-vt 原生基线对比的吞吐基准。
6. **如果路线 B 在 PoC 阶段（建议 1–2 周）遇到无法绕过的问题**（AOT 链接、某个 RID 跑不起来、API 缺口），退回 **A′（XTerm.NET）**，门面接口保持不变。

---

## 7. 需要用户决策的点

1. **是否接受"上游 API 尚未冻结"的原生核心（路线 B）**，并为此承担 pin commit、自维护绑定、Zig 0.16 工具链和定期升级的成本？如果不接受，选纯托管的 A′（XTerm.NET）或 A（自研），代价是功能、性能和长期维护的差距。
2. **是否放弃 architecture.md 中"直接使用 Iciclecreek.Avalonia.Terminal"的计划**，改为仿照 StreamMark 建独立库（核心门面 + 自绘渲染 + AtomUI 主题包）？现成控件（Iciclecreek、RoyalTerminal）能更快可用，但渲染、主题、AOT 都不受我们控制。
3. **v1 的协议范围**：图形协议（Kitty graphics / sixel）、Kitty 键盘协议、OSC 133 shell 集成脚本自动注入是否进 v1？这决定渲染层和输入层的工作量；路线 B 本身没有 sixel，如需 sixel 要另行实现。

---

## 8. 来源

拉取日期 2026-10-16，均为 `git clone --depth 1`，路径为仓库内相对路径。

| 键 | 来源 |
|---|---|
| G | ghostty-org/ghostty @ `b3dc3d794513`（2026-10-15）：`include/ghostty/vt.h`、`include/ghostty/vt/*.h`、`README.md`、`build.zig.zon`、`CMakeLists.txt`、`src/build/GhosttyLibVt.zig`、`src/build/GhosttyZig.zig`、`src/build/SharedDeps.zig`、`src/terminal/page.zig`、`src/terminal/modes.zig`、`.github/workflows/test.yml`、`test/esctest/README.md`、`test/fuzz-libghostty/README.md`、`test/windows/README.md` — https://github.com/ghostty-org/ghostty |
| BLOG | Mitchell Hashimoto, "Libghostty Is Coming"（2025-09-22）— https://mitchellh.com/writing/libghostty-is-coming |
| DEV6 | Ghostty Devlog 006 — https://mitchellh.com/writing/ghostty-devlog-006 |
| AW | Uzaaft/awesome-libghostty README（2026-10-16）— https://github.com/Uzaaft/awesome-libghostty |
| RT | royalapplications/RoyalTerminal @ `01833b8`（2026-10-09）：`README.md`、`docs/articles/{ghostty-integration,architecture,build-test-release,windows-x64-native-compatibility}.md`、`native/ghostty-vt-extensions/README.md`、`.github/workflows/release.yml`、`src/RoyalTerminal.Terminal.Vt.Ghostty/Terminal/GhosttyVtProcessor.cs`、`src/RoyalTerminal.GhosttySharp/*` — https://github.com/royalapplications/RoyalTerminal |
| NUGET:RT-native | RoyalApps.RoyalTerminal.GhosttySharp.Native.{OSX,Linux64,Win64} 0.6.3 的 nupkg 内容（`unzip -l`、`file`、`nm`）— https://www.nuget.org/packages/RoyalApps.RoyalTerminal.GhosttySharp.Native.OSX |
| DN | deblasis/libghostty-vt-dotnet @ `c6f23d8`（2026-10-16）：`README.md`、`ghostty-upstream.json`、`src/Ghostty.Vt/Ghostty.Vt.csproj` — https://github.com/deblasis/libghostty-vt-dotnet |
| IC | tomlm/Iciclecreek.Avalonia.Terminal @ `cb710a5`（2026-10-14）：`README.md`、`compliance.png`、`src/Iciclecreek.Avalonia.TerminalWindow/*` — https://github.com/tomlm/Iciclecreek.Avalonia.Terminal |
| XN | tomlm/XTerm.NET @ `453ed95`（2026-10-14）：`README.md`、`docs/perf-results.md`、`src/XTerm.NET/*`、`tools/README.md` — https://github.com/tomlm/XTerm.NET |
| PP | tomlm/Porta.Pty（2026-10-01）：`src/Porta.Pty/Porta.Pty.csproj` — https://github.com/tomlm/Porta.Pty |
| DT | starling-browser/dotty @ `1c0b397`（2026-07-05）— https://github.com/starling-browser/dotty |
| XS | migueldeicaza/XtermSharp @ `1bed529`（2020-12-04）— https://github.com/migueldeicaza/XtermSharp |
| VN | darrenstarr/VtNetCore @ `4e723ac`（2022-10-16）— https://github.com/darrenstarr/VtNetCore |
| AL | alacritty/alacritty @ `5421470`（2026-10-16）：`alacritty_terminal/{Cargo.toml,src/term/mod.rs,src/term/cell.rs,src/event.rs,tests/ref}`、`alacritty/src/input/keyboard.rs` — https://github.com/alacritty/alacritty |
| VTE | alacritty/vte @ `abeae76`（2026-02-28）：`src/ansi.rs` — https://github.com/alacritty/vte |
| ZED | zed-industries/zed：`crates/terminal/Cargo.toml`、根 `Cargo.toml:534`（alacritty_terminal 的 git fork）— https://github.com/zed-industries/zed |
| WZ | wezterm/wezterm @ `e2a98e1`（2026-10-10，稀疏检出）：`term/`、`wezterm-escape-parser/`、`wezterm-surface/`、`wezterm-cell/` — https://github.com/wezterm/wezterm |
| LV | libvterm 0.3.3 源码包（文件时间 2023-08-04）：`include/vterm.h`、`src/state.c`、`t/` — https://www.leonerd.org.uk/code/libvterm/ |
| NV | neovim/neovim `src/nvim/vterm/`（最近提交 2026-10-06）；neovim/libvterm（archived）— https://github.com/neovim/neovim/tree/master/src/nvim/vterm |
| MSWT | microsoft/terminal `src/cascadia/TerminalCore/Terminal.hpp` — https://github.com/microsoft/terminal |
| CT | contour-terminal/contour `src/vtbackend`，release v0.7.0.8982（2026-08-17）— https://github.com/contour-terminal/contour |
| XJ | xtermjs/xterm.js @ `991857a`（2026-10-13）：`package.json`、`src/common/**`、`src/browser/**`、`addons/**` — https://github.com/xtermjs/xterm.js |
| NPM | @xterm/xterm dist-tags（latest 6.0.0 于 2025-12-22；beta 6.1.0-beta.315）— https://registry.npmjs.org/@xterm/xterm |
| WV | Avalonia.Controls.WebView 12.1.1 — https://www.nuget.org/packages/Avalonia.Controls.WebView/12.1.1 ；NativeWebView 文档 — https://docs.avaloniaui.net/controls/web/nativewebview ；仓库 README — https://github.com/AvaloniaUI/Avalonia.Controls.WebView |
| UCS | ucs-detect 结果汇总 — https://ucs-detect.readthedocs.io/results.html |
| KP | kitty performance — https://sw.kovidgoyal.net/kitty/performance/ |
| WEB-BENCH | 社区基准（结论互相矛盾，只作背景）：https://github.com/pg83/shitty 、https://github.com/yanyaoer/tty7 |
| CRATES | crates.io API：alacritty_terminal、vte、termwiz、wezterm-term（不存在）— https://crates.io |
| ZIG | Zig 下载索引（0.16.0 于 2026-04-13，0.17.0 于 2026-10-01）— https://ziglang.org/download/index.json |
| NUGET | NuGet 搜索与注册 API（Iciclecreek、XTerm.NET、Porta.Pty、DeBlasis.GhosttyVt、RoyalApps.*、XenoAtom.*、AgentSpace.VtDotNet、Terminal.Emulation、LibGhostty.Net 等）— https://api.nuget.org |
| GH-API | GitHub REST API 查询（2026-10-16）：仓库元数据（许可证、星数、pushed_at）、release / tag 日期、`include/ghostty/vt` 提交计数、xterm.js 的 Kitty 键盘首次提交和 IME issue 搜索 |
