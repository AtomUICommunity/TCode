# TCode 终端：协议支持范围与兼容性规格

> 状态：设计稿 v1（2026-10-17）。上位文档：[01-architecture.md](01-architecture.md)；核心实现：[02-core-ghostty.md](02-core-ghostty.md)；pty host：[04-pty-host.md](04-pty-host.md)；输入：[06-input.md](06-input.md)。
> 本文把 [research/03-vt-protocol.md](research/03-vt-protocol.md)（下称 R03）的建议**落实为决策**：每项功能由谁实现（libghostty-vt 原生、镜像核心的 effects、UI 侧、host、不支持）、最终的应答字符串、环境变量、宽度模式、安全规则和测试方式。序列与模式号保留原文。

## 0. 标注

| 标记 | 含义 |
|---|---|
| `[H:file:行]` / `[S:file:行]` | **已核实**：ghostty @ `b3dc3d7` 的头文件（`include/ghostty/vt/`）/ 实现（`src/terminal/`） |
| `[R03 §x]` | 调研报告的章节 |
| `[02 §x]` `[04 §x]` `[06 §x]` | 本目录的设计文档 |
| **O1–O4** | [02 §2.4] 的 overlay 补丁（9001 登记、5522 停用、OSC 52 读延后应答、modifyOtherKeys 导出） |
| **[推断]** / **[待测]** | 未核实 / 需要测试确认 |

"实现方"一栏的取值：

| 取值 | 含义 |
|---|---|
| **原生** | libghostty-vt 内部完成（解析、状态、应答字节），TCode 只做配置 |
| **原生 + 回调** | libghostty 解析，结果通过回调交给 TCode；回调在**镜像核心**（pty host）或 **UI 侧核心**上处理，见 [02 §7.2] |
| **TCode** | TCode 自己实现（托管代码、host 或视图） |
| **O*n*** | 依赖 overlay 补丁；补丁不可用时按 [02 §2.4] 的退路降级 |
| **—** | 不支持 |

---

## 1. 结论摘要

1. **绝大多数协议由 libghostty-vt 原生完成**，包括全部 Tier 0、DECRQM、DECRQSS、XTGETTCAP、颜色查询、Kitty 键盘、2026 / 2027 / 2031 / 2048、OSC 133、Kitty 图形。TCode 需要提供的只有：DA / XTVERSION / 尺寸 / 明暗这 4 个"答案"（回调）、副作用（标题、剪贴板、通知、进度、命令跟踪）、输入编码的平台部分（win32-input-mode）、以及安全策略。
2. **所有应答只由镜像核心写回 PTY**；UI 侧核心算出同样的应答后丢弃 [A01 §3.1]。
3. **与 R03 建议的主要偏差**（全部来自 libghostty 的现有行为，v1 接受）：XTGETTCAP 用 Ghostty 自带的能力表、未知名字不应答；DECRQSS 不支持 `" q`；CSI 参数上限 24（R03 建议 32）；字形簇每格上限 64 码点（R03 建议 32）；标题截断到 1024 字节（R03 建议 4 KiB）；1005 / 1015 鼠标编码被实现而不是忽略；DECRQM 对 1001、2（VT52）回答 0 而不是 4；没有"歧义宽度按宽字符"的选项。详见 §8。
4. **Windows 宽度锁定为字形簇模式**（ConPTY 的字宽在创建时固定、不理会 2027）；macOS / Linux 默认兼容模式，程序开启 2027 后切换（§5）。
5. **v1 明确不支持**：sixel、iTerm2 内联图像、Kitty 动画自动播放、Kitty 粘贴事件（5522）、OSC 99、OSC 1337 `SetMark` 等扩展、矩形区域操作、DECDHL/DECDWL、2033 可见性的真实值（永远报告"可见"）。

---

## 2. 查询应答（最终字符串）

所有应答由镜像核心产生，经会话写队列写回 PTY，**同一批输入中的查询按顺序应答**（libghostty 在 `vt_write` 中同步调用 `write_pty`，天然保序 [H:terminal.h:79-84]）。表中 `ESC` = `\x1b`，`ST` = `ESC \`。

| 查询 | 应答 | 实现方 | 与 libghostty 的映射 |
|---|---|---|---|
| DA1 `CSI c` / `CSI 0 c` | `ESC [ ? 62 ; 22 ; 52 c`；`clipboard.oscWrite = deny` 时 `ESC [ ? 62 ; 22 c` | 原生 + 回调 | `GHOSTTY_TERMINAL_OPT_DEVICE_ATTRIBUTES`：`primary = { conformance_level = 62, features = [22, 52 或无] }`；库按 `ESC [ ? Pp ; Ps… c` 编码 [H:device.h:82-100][S:device_attributes.zig:70-74]。理由见 R03 §3.2（不声明 4 / 21 / 28）。`52` 是否出现由 `host/configure` 下发 [04 §11.2] |
| DA2 `CSI > c` / `CSI > 0 c` | `ESC [ > 1 ; 10 ; 0 c`（2026-10-17 用户决定，与 Ghostty 一致） | 原生 + 回调 | 同一回调的 `secondary = { device_type = 1, firmware_version = 10, rom_cartridge = 0 }`，库编码为 `ESC [ > Pp ; Pv ; Pc c` [S:device_attributes.zig:88-95]。与 Ghostty 一致，兼容性经过大量用户验证。**已知影响**：vim 按 DA2 版本号自动启用 SGR 鼠标（≥ 277）、DECRQSS 光标样式（≥ 279）、下划线颜色（≥ 2500）[R03 §3.3]，版本 10 不会触发这些自动探测。宽于 223 列时 vim 的鼠标需要用户在 vimrc 中设置 `ttymouse=sgr`（Ghostty 用户的通行做法）；neovim 不依赖 DA2，不受影响 |
| DA3 `CSI = c` | `ESC P ! \| 00000000 ST` | 原生 + 回调 | `tertiary.unit_id = 0`，库编码为 8 位大写十六进制 [S:device_attributes.zig:112-120] |
| XTVERSION `CSI > q` / `CSI > 0 q` | `ESC P > \| TCode <版本> ST`，例 `ESC P > \| TCode 0.9.0 ST` | 原生 + 回调 | `GHOSTTY_TERMINAL_OPT_XTVERSION` 返回 `"TCode 0.9.0"`，库包装为 `ESC P > \| {s} ESC \`；回调返回空串时库用 `libghostty` [S:stream_terminal.zig:1788-1797]。版本 = Desktop 的 SemVer（与 `TERM_PROGRAM_VERSION` 相同） |
| DSR 5 `CSI 5 n` | `ESC [ 0 n` | 原生 | [S:stream_terminal.zig:1722-1724] |
| CPR `CSI 6 n` | `ESC [ r ; c R`（1 起算；DECOM 置位时相对滚动区） | 原生 | [S:stream_terminal.zig:1712-1736] |
| DECXCPR `CSI ? 6 n` | `ESC [ ? r ; c ; 1 R` | 原生 | 同上 |
| 明暗 `CSI ? 996 n` | 暗色 `ESC [ ? 997 ; 1 n`，亮色 `ESC [ ? 997 ; 2 n` | 原生 + 回调 | `GHOSTTY_TERMINAL_OPT_COLOR_SCHEME`：返回最近一次 `SetColors` 记录携带的明暗（Desktop 主题）[S:device_status.zig:33-41] |
| 2031 主动通知 | 同上 | TCode（host） | 主题切换 → `SetColors` 记录；镜像核心若 `DATA_MODE(2031)` 为真，用 `ghostty_color_scheme_report_encode` 编码后写入 PTY [H:color_scheme.h:61-65] |
| 可见性 `CSI ? 998 n` / 2033 | 永远 `ESC [ ? 999 ; 1 n`（可见） | 原生 | C API 没有设置可见性的接口，`flags.visible` 默认 true [S:Terminal.zig:136]。Tier 2 |
| DECRQM `CSI [?] Ps $ p` | `ESC [ [?] Ps ; Pm $ y` | 原生 | 按模式表应答；表外的模式答 0 [S:stream_terminal.zig:1908-1940]。9001 依赖 O1；5522 经 O2 答 0 |
| Kitty 键盘查询 `CSI ? u` | `ESC [ ? flags u` | 原生 | [S:stream_terminal.zig:1942-1950] |
| DECRQSS `DCS $ q Pt ST` | `m`：`ESC P 1 $ r <SGR> m ST`（颜色用冒号写法 `38:2::r:g:b`）；`SP q`：`ESC P 1 $ r Ps SP q ST`；`r`：`ESC P 1 $ r t ; b r ST`；`s`：仅 DECLRMM 置位时 `ESC P 1 $ r l ; r s ST`，否则 `ESC P 0 $ r ST`；其他（含 `" q`）：`ESC P 0 $ r ST` | 原生 | 内容完全由终端状态格式化 [S:dcs.zig:250-300] |
| XTGETTCAP `DCS + q hex… ST` | 每个已知名字一条 `ESC P 1 + r <hex>=<hex> ST`；`TN` 答 `xterm-256color`；**未知名字不应答** | 原生 | 能力表是 Ghostty 编译期内置的 `xterm-ghostty` 表（含 `Co=256`、`RGB=8`、`Tc`、`Su`、`Smulx`、`Setulc`、`Ss`、`Se`、`Ms`、`Sync`、`BE`/`BD`/`PS`/`PE`、`XM`、`fe`/`fd`、`kbs=^?` 等）；`TN` 由 `GHOSTTY_TERMINAL_OPT_TERMINFO_NAME` 设置，未设置时不应答 [S:stream_terminal.zig:852-895][S:terminfo/ghostty.zig][H:terminal.h:2366-2380] |
| OSC 4 / 10 / 11 / 12 `?` | `ESC ] 4 ; n ; rgb:RRRR/GGGG/BBBB <终止符>`、`ESC ] 1x ; rgb:… <终止符>`（16 位分量，**终止符与请求相同**） | 原生 | [S:stream_terminal.zig:2131-2189] |
| XTWINOPS `CSI 14 t` | `ESC [ 4 ; 高px ; 宽px t`（= 行×cell 高、列×cell 宽，物理像素） | 原生 + 回调 | `GHOSTTY_TERMINAL_OPT_SIZE`：返回 `{ rows, columns, cell_width, cell_height }`，即核心最近一次 `Resize` 的 `TerminalSize`（cell 像素 [A01 §6.1]）[S:size_report.zig:45-92] |
| `CSI 16 t` | `ESC [ 6 ; cell高 ; cell宽 t` | 同上 | 同上 |
| `CSI 18 t` | `ESC [ 8 ; rows ; cols t` | 同上 | 同上 |
| `CSI 19 t` | `ESC [ 9 ; rows ; cols t` | 同上 | 同上（库按 VTE 的做法报告文本区） |
| `CSI 21 t`（标题）、`CSI 20 t` | 不应答 | 原生 | `GHOSTTY_TERMINAL_OPT_TITLE_REPORT = false`（默认）[H:terminal.h:2296-2306] |
| 2048 带内尺寸报告 | `ESC [ 48 ; rows ; cols ; 高px ; 宽px t` | 原生 + 回调 | 同一个 `SIZE` 回调。开启 2048 时立即报告一次；之后每次 `ghostty_terminal_resize` 报告一次 [H:terminal.h:3027-3030][S:stream_terminal.zig:2016-2019]。host 先执行 `TIOCSWINSZ` / `ResizePseudoConsole`，再在日志中追加 Resize 记录，所以报告晚于 PTY 尺寸更新（[2048] 规范要求）|
| OSC 52 读 `ESC ] 52 ; c ; ? <终止符>` | 允许：`ESC ] 52 ; <c\|p\|s> ; <base64> <原终止符>`；拒绝或超时：不应答 | 原生 + 回调 + TCode（O3）| 镜像核心回调登记请求后不应答；host 发 `clipboard/readRequest` 给主视图，UI 回 `clipboard/readResponse` 后，host 编码并写入 PTY [A01 §3.2 规则 6][02 §14.3]。O3 不可用时回调内同步阻塞（最多 30 s），拒绝时库回空剪贴板 |
| OSC 7501 查询 `ESC ] 7501 ; ? ST` | `ESC ] 7501 ; ?:states=idle,working,done,blocked,error:kinds=permission,question,auth ST` | 原生 + 回调 | 镜像核心安装了 `PROGRAM_STATUS` 回调时库自动应答 [H:terminal.h:2507-2528] |
| ENQ `0x05` | 无 | 原生 | 不安装 `ENQUIRY` 回调 [H:terminal.h:1688-1703]（R03 §1.4：answerback 永远为空） |
| DECRQCRA `CSI … * y` | 默认无；测试模式下应答校验和 | 原生 | `GHOSTTY_TERMINAL_OPT_XT_CHECKSUM_REPORT` 只在 esctest 测试模式打开 [H:terminal.h:2470-2484] |
| Kitty 图形 `a=q` 等 | `ESC _ G i=<id>[,…] ; OK ST` 或错误码 | 原生 | 只允许直接传输（§7.3）|

ConPTY 握手：ConPTY 启动时发出 `ESC[c`、`ESC[?1004h`、`ESC[?9001h` [R03 §4.5]。镜像核心在 PTY 读线程启动后立即开始解析并应答 DA1，满足 ConPTY 的等待 [R02 §3 W3]。

---

## 3. 由 effects 或宿主层实现的功能

| 功能 | 序列 | libghostty 回调 | 处理位置与行为 |
|---|---|---|---|
| 窗口标题 | OSC 0 / 2（OSC 1 图标名忽略） | `TITLE_CHANGED`（标题已写入终端，回调里读 `DATA_TITLE`）[H:terminal.h:1725-1738] | 两份核心都处理：镜像核心记入会话元数据（持久化、Agent、attach），UI 侧核心更新标签。显示前按 §6.2 清洗 |
| 标题栈 | `CSI 22 / 23 ; … t` | 原生 | — |
| 工作目录 | OSC 7、OSC 9;9、OSC 1337 `CurrentDir` | `PWD_CHANGED`，读 `DATA_PWD`（原文，不解析）[H:terminal.h:1860-1883] | 镜像核心：解析 `file://` URI、校验（§6.5），用于"在相同目录打开"；UI 侧：显示 |
| 响铃 | BEL | `BELL` | UI 侧核心：视觉 / 声音按设置；镜像核心只在无 UI 时记 `bellPending` [04 §11.3] |
| 写剪贴板 | OSC 52、OSC 1337 `Copy`、OSC 5522 写 | `CLIPBOARD_WRITE`（已解码、按 MIME 分组）[H:terminal.h:852-881] | 镜像核心按 `clipboard.oscWrite` 策略经 host 发 `session/clipboardWrite` 给主视图 [04 §11.2] |
| 读剪贴板 | OSC 52 `?` | `CLIPBOARD_READ` | 见 §2 和 §6.1 |
| OSC 5522 读 | kitty 剪贴板协议 | `CLIPBOARD_READ` | v1 回 `UNSUPPORTED`（库按协议回 ENOSYS）|
| 桌面通知 | OSC 9 `<text>`、OSC 777 `notify` | `DESKTOP_NOTIFICATION` [H:terminal.h:1077-1111] | 镜像核心 → Desktop；窗口未聚焦时显示系统通知，限频（§6.6）。OSC 9 与 ConEmu 子命令、`9;4` 的区分由库完成 [R03 §2.5] |
| 进度 | OSC 9;4 | `PROGRESS_REPORT`（`progress = -1` 表示省略）[H:terminal.h:1113-1169] | UI 侧：标签进度条；镜像核心：保存最新状态，attach 时补发 |
| 程序状态 | OSC 7501（Claude Code 等会发送） | `PROGRAM_STATUS` [H:terminal.h:1171-1395] | 镜像核心按协议规则维护记录表（≤ 256 条），转发给 Desktop / Agent；新提示符出现（OSC 133 PROMPT_START）时清除 working / blocked 记录 |
| 语义提示 | OSC 133 A/B/C/D/L/N/P/I（含 kitty `cmdline=`、`D;exit`） | 原生（网格语义标记、按 prompt 滚动、`select_output`）+ `SEMANTIC_PROMPT` | 镜像核心：`CommandTracker` [02 §12]；UI 侧：命令块装饰 |
| VS Code 集成 | OSC 633 | `UNKNOWN_SEQUENCE`（需设置 `UNKNOWN_MAX_BYTES`）[H:terminal.h:131-197] | 镜像核心解析 A/B/C/D/E/P 进 `CommandTracker`；**不产生网格语义标记**，所以 `select_output` / 按 prompt 跳转对只发 633 的 shell 无效 [02 §12.3] |
| 同步输出 | mode 2026 | 原生 + `RENDER_HOLD` | UI 侧核心冻结帧、1 s 超时后忽略 hold（不修改模式）[02 §8.7] |
| 2031 通知 | — | — | host（§2） |
| 2048 | — | `SIZE` 回调 | §2 |
| 窗口尺寸请求 | `CSI 8 ; rows ; cols t` | `RESIZE_WINDOW` | 不安装回调：忽略 |
| 鼠标指针形状 | OSC 22 | 原生（`DATA_MOUSE_SHAPE`） | UI 侧：`FrameSnapshot.PointerShape` |
| 超链接 | OSC 8 | 原生（格子属性；URI 经 grid ref 读取） | UI 侧：显示、悬停、打开策略（§6.3） |
| win32-input-mode | `CSI ? 9001 h/l` | O1（登记进模式表） | 状态在核心；编码由 Windows 平台层完成（`CSI Vk;Sc;Uc;Kd;Cs;Rc _`）[06 §3.7] |
| 粘贴 | 2004 | 原生（`ghostty_terminal_paste`：剥离危险控制字符、包裹、安全检查）| UI 侧核心的 `EncodePaste`（捕获作用域）[02 §13.3] |
| 焦点上报 | 1004 | 原生（模式）+ `ghostty_focus_encode` | UI 线程 |
| 重置 | RIS | 原生 + `RESET` | 两边清空各自的 TCode 侧缓存（命令记录、程序状态） |

---

## 4. 环境变量与 TERM

由 `TCode.Terminal.Shell` 的启动构造器设置（完整的环境变量策略见 07）。

| 变量 | 值 | 平台 | 说明 |
|---|---|---|---|
| `TERM` | `xterm-256color` | 全部（Windows 也设置：WSL 通过 `WSLENV` 之外的方式不继承，但 Git Bash / MSYS、`ssh.exe` 会读取）| v1 不发布自定义 terminfo [R03 §7.3][A01 D9]。与 XTGETTCAP 的 `TN` 一致（`OPT_TERMINFO_NAME`）|
| `COLORTERM` | `truecolor` | 全部 | neovim 等据此直接启用真彩色 [R03 §7.2] |
| `TERM_PROGRAM` | `TCode` | 全部 | 与 XTVERSION 名称一致 |
| `TERM_PROGRAM_VERSION` | Desktop 的 SemVer，例如 `0.9.0` | 全部 | 与 XTVERSION 版本一致 |
| 清除 | `VTE_VERSION`、`KITTY_WINDOW_ID`、`KITTY_PID`、`WT_SESSION`、`WT_PROFILE_ID`、`ITERM_SESSION_ID`、`TERM_SESSION_ID`、`TERMINFO`、`GHOSTTY_RESOURCES_DIR`、`GHOSTTY_BIN_DIR` | 全部 | 继承自启动 TCode 的父终端时会误导程序（Ghostty 的做法 [R03 §7.3]）**[推断：列表需要 07 最终确认]** |

SSH（D4 第一期，系统 `ssh -t`）：远端 `TERM` 仍为 `xterm-256color`；`COLORTERM`、`TERM_PROGRAM*` 只在远端 `AcceptEnv` 允许时传递。所有查询应答在 SSH 下照常工作，这是"依赖查询而不是 terminfo"的主要理由 [R03 §7.3]。

tmux：tmux 查询 XTVERSION 得到 `TCode`，不在它的已知表中，退回 `xterm*` 的默认特性；文档建议用户配置 `set -as terminal-features ',xterm-256color:RGB:hyperlinks:usstyle:extkeys'`，长期向 tmux 提交 PR 把 TCode 加入表中 [R03 §7.3]。

---

## 5. Unicode 宽度

### 5.1 两种模式（libghostty 已核实的行为）

| | 兼容模式（2027 复位） | 字形簇模式（2027 置位） |
|---|---|---|
| 规则 | 逐码点计宽；零宽码点附着到前一格；VS16 / VS15 只在前一格是 emoji 时附着，**不改变宽度**；ZWJ 不合并，序列中每个 emoji 各自计宽 [S:Terminal.zig:1475-1530] | UAX #29 分簇；VS16 → 2、VS15 → 1（仅对合法的 emoji 变体序列）；ZWJ 序列整体宽 2 [S:Terminal.zig:1286-1460][H:unicode.h:96-140] |
| 列宽预测 API | `ghostty_unicode_codepoint_width` 逐码点求和 | `ghostty_unicode_grapheme_width` |
| 码点宽度表 | Ghostty 内置表：东亚宽 / 全角、默认 emoji 呈现、区域指示符为 2；组合符、默认可忽略码点（ZWJ、VS 等）为 0；≤ U+00FF 一律为 1 [H:unicode.h:40-58] | 同左 |
| 每格码点上限 | 64（超出静默丢弃）[S:page.zig:92] | 同左 |

### 5.2 平台策略

| 平台 | 默认 | 程序发 `CSI ? 2027 h/l` | ConPTY 标志 |
|---|---|---|---|
| macOS / Linux | 兼容模式（2027 复位）[A01 D9] | 切换；DECRQM 2027 回答 1 / 2。neovim 会主动开启 [R03 §6.3] | — |
| Windows（ConPTY：本地 shell、WSL、`ssh.exe`）| **字形簇模式，锁定**（2027 的复位默认值设为置位，[02 §13.4]）| libghostty 仍会执行复位（没有"永久置位"开关），DECRQM 回答 1 / 2 而不是 WT 的 3。ConPTY 的字宽在创建时固定、不理会 2027，所以程序复位 2027 后，emoji 序列的布局可能与 ConPTY 内部缓冲区不一致 —— v1 接受，记为已知问题 | `PSEUDOCONSOLE_GLYPH_WIDTH_GRAPHEMES`（0x08）[R02 §3.2] |

理由：ConPTY 自己维护一份屏幕缓冲区，按创建时的字宽算法排布，并据此生成光标定位序列；模拟器必须与它一致，否则 CJK / emoji 会让光标错位 [R02 §3 W5]。Windows Terminal 同样把 2027 视为永久置位 [R03 §2.2]。

### 5.3 其他

- **歧义宽度（EAW = A）**：一律窄（1）。libghostty 没有"按宽字符处理"的选项，v1 不提供该设置（R03 §6.3 建议的"宽"选项推迟）；ConPTY 不设 `PSEUDOCONSOLE_AMBIGUOUS_IS_WIDE`。
- **区域指示符**：兼容模式下每个 RI 宽 2（Ghostty 表），与 macOS libc `wcwidth` 的 1 不同 [R03 §6.2]。这是 Ghostty 的既有行为，接受。
- **CPR 与网格一致**：宽度只以核心网格为准，ucs-detect 就是通过 CPR 测量的 [R03 §6.3]。
- **渲染不改变宽度**：字体测得的字形宽度不影响格子宽度（渲染层溢出或缩放，见 05）。
- **IME 预编辑**：UI 线程按 `InputModes.GraphemeCluster` 选择上面两个预测 API [02 §13.4]。

---

## 6. 安全规则的落地

### 6.1 OSC 52 剪贴板

| 操作 | 默认策略（`clipboard.oscWrite` / `oscRead`） | 实现 |
|---|---|---|
| 写 | `allow`，每次写入在 UI 上显示短暂提示 | 镜像核心 `CLIPBOARD_WRITE` → host → 主视图 [04 §11.2]。上限：OSC 52 受库的 OSC 长度上限约束（可分配缓冲最大 8 MiB [S:osc.zig:361-365]）；OSC 5522 写入受 `CLIPBOARD_WRITE_MAX_BYTES` 约束（设为 8 MiB）[H:terminal.h:2393-2414]。无 UI 连接时丢弃 |
| 读 | `ask`：按"会话 + 来源"记住"本会话允许"；SSH 会话单独询问 | O3 延后应答流程（§2）；拒绝、超时（60 s）、无 UI 时**不应答** |
| DA1 的 `52` | `oscWrite ≠ deny` 时声明 | §2 |

### 6.2 回显类查询

| 查询 | 落地 |
|---|---|
| `CSI 21 t` / `CSI 20 t` | `TITLE_REPORT = false`（库默认）。头文件写明原因：程序可设置标题再查回，向输入流注入命令 [H:terminal.h:2296-2306] |
| DECRQSS | 库只应答 `m`、`SP q`、`r`、`s`，内容由终端状态格式化，**不回显请求文本** [S:dcs.zig:250-300]（iTerm2、mintty 的 CVE 来源 [R03 §8.2]）|
| XTGETTCAP | 应答只含请求名的十六进制和编译期常量；未知名字不应答 |
| DECRQCRA | 默认关闭（逐格校验和可读出整屏）[H:terminal.h:2470-2482]；只在测试模式打开 |
| OSC 50 字体查询、OSC 1337 `ReportVariable` / `RequestUpload` | 不支持 |
| ENQ | 不应答 |
| 标题内容 | 库截断到 1024 字节 [S:stream_terminal.zig:1871-1886]。TCode 在 `TitleChanged` 中再去除 C0 / C1 控制字符和双向控制符（U+202A–202E、U+2066–2069）后再显示 |
| OSC 7501 文本 | `title` / `message` 已解码且无控制字符，仍按头文件要求去除不可见格式字符，并标明来源终端 [H:terminal.h:1348-1353] |

### 6.3 超链接（OSC 8）

UI 侧实现（05 / 06）：默认普通点击不打开，需 ⌘ / Ctrl + 点击；悬停显示真实 URI；协议白名单 http、https、mailto、file（本机）；其他协议弹确认；`file://` 的主机名必须为空、`localhost` 或本机；链接文本看起来像 URL 但主机不同时醒目提示；打开时只把 URL 作为参数传给系统 API [R03 §8.3]。URI 长度由库的 OSC 上限约束；显式 `id=` 拿不到（[02 §8.5]），按 URI 分组。

### 6.4 粘贴注入

- 库的 `ghostty_terminal_paste`：把不安全的控制字节替换为空格（与 xterm `disallowedPasteControls` 同源），2004 置位时包裹 `ESC[200~ … ESC[201~`，未置位时 `\n` → `\r`；内容含 `ESC[201~`（括号模式）或换行（非括号模式）时返回 `GHOSTTY_REJECTED` [H:paste.h:16-60,193-211]。
- TCode：`REJECTED` → 确认框（预览前几行）→ `allow_unsafe` 重试；另提供"总是确认多行粘贴"选项（用 `ghostty_paste_is_safe` 预检）。
- Agent 写入：host 用 `ghostty_paste_encode`，永不放行不安全内容 [02 §13.3]。

### 6.5 不可信的输入信道

| 信道 | 落地 |
|---|---|
| OSC 7 / OSC 9;9 / OSC 1337 `CurrentDir` / OSC 633 `P;Cwd=` | 库只存原文 [H:terminal.h:1867-1871]。镜像核心解析：只接受 `file://` 且主机为空、`localhost` 或本机名；百分号解码；必须是存在的绝对路径；**从不拼进命令行**（WT CVE-2022-44702）；不做 DNS 查询 |
| OSC 633 `E`（命令行） | 只有 nonce 与本会话注入脚本生成的 nonce 一致时才采用；只用于显示 |
| OSC 133 `C;cmdline=` | 只用于显示，不执行、不写入历史 |
| 窗口尺寸请求 `CSI 8 t` | 忽略 |
| Kitty 图形 `t=f/t/s` | 两份核心都关闭（`KITTY_IMAGE_MEDIUM_* = false`，与库默认一致 [S:Terminal.zig:318-325]）；只允许直接传输 [02 §11.2] |

### 6.6 通知与频率限制

桌面通知（OSC 9、777）：默认只在窗口失焦时显示系统通知；每会话每 10 s 最多 3 条，超出合并为一条"N 条通知"**[推断：取值待 UI 设计确认]**；内容只作纯文本。

### 6.7 资源上限（DoS）

| 项 | 值 | 来源 |
|---|---|---|
| CSI 参数个数 | 24（超出部分丢弃）| 库 `MAX_PARAMS` [S:Parser.zig:209]（R03 建议 32，见 §8）|
| intermediate 个数 | 4 | 库 `MAX_INTERMEDIATE` [S:Parser.zig:196]（R03 建议 2）|
| OSC 长度 | 2048 字节栈缓冲，可分配上限 8 MiB | [S:osc.zig:361-365] |
| 未知 OSC / APC 转交 | 4 KiB（`UNKNOWN_MAX_BYTES`），超出截断并标记 `truncated` | [H:terminal.h:2345-2364] |
| APC（Kitty 图形） | 库默认上限 | `APC_MAX_BYTES_KITTY` 保持默认，两份核心一致 [02 §7.1] |
| 标题 | 1024 字节 | §6.2 |
| 每格字形簇 | 64 码点 | §5.1 |
| Kitty 键盘栈 | 8（每屏）| R03 §4.4（Ghostty `kitty/key.zig:9`）|
| Kitty 图像存储 | 每屏 64 MB（两份核心一致）| [02 §11.3] |
| 写回 PTY 的应答 | 应答进入会话写队列；写队列上限见 [04 §7.5]（8 MiB）| 程序高速发查询时应答不会无限缓冲 |
| 回滚 | 行数 + 字节双上限 | [02 §10] |

### 6.8 Agent

Agent 只能写它自己创建或被用户共享的终端；写入经过 TCode 审批；读取用户终端内容需要授权（09）。写入路径使用 §6.4 的编码，不走原始字节。

---

## 7. 功能分级表

图例：v1 ✅ 支持；◐ 部分支持（说明）；✗ 不支持。测试：**U** = 核心单元测试（字节 → 网格 / 应答断言，`TCode.Terminal.Core.Tests`）；**E** = esctest2（测试模式，CI）；**R** = 真实程序录制回放快照；**K** = 键盘 / 鼠标编码表驱动测试；**C** = ucs-detect；**M** = 手工验收（vttest 菜单等，不用合成 GUI 输入）；**D** = 差分测试（[02 §15]）；**P** = PTY / host 集成测试。

### 7.1 Tier 0

| 功能 | v1 | 实现方 | 测试 |
|---|---|---|---|
| 光标移动（CUU…VPR）、DECAWM 与延迟换行、擦除（ED 0–3、EL、ECH）、ICH/DCH/IL/DL、SU/SD/IND/RI/NEL、DECSTBM、DECOM、DECSC/DECRC、SCOSC/SCORC | ✅ | 原生 | U、E、D |
| SGR 基本属性、256 色、真彩色（分号与冒号写法）| ✅ | 原生 | U、E；DECRQSS 回读（neovim 探测法）|
| DECTCEM、备用屏 47/1047/1048/1049、DECCKM、DECKPAM/DECKPNM、?66、IRM、LNM | ✅ | 原生 | U、E、R |
| 制表位（HTS、TBC、CHT、CBT、DECST8C）、字符集（ASCII、DEC 线框、UK；SO/SI）、REP | ✅ | 原生 | U、E |
| DECSTR、RIS | ✅ | 原生（+ `RESET` 回调）| U、E |
| 括号粘贴 2004 | ✅ | 原生 + UI 侧 `EncodePaste` | U、K |
| 焦点事件 1004 | ✅ | 原生 + `ghostty_focus_encode` | K、P（ConPTY 握手）|
| 鼠标 9/1000/1002/1003 + 1006 | ✅ | 原生 + UI 线程编码器 | K |
| DECSCUSR（0 = 用户默认形状）| ✅ | 原生；`DEFAULT_CURSOR_STYLE/BLINK` 来自设置 | U |
| DA1 / DA2 / DSR / CPR / DECRQM | ✅ | 原生 + 回调（§2）| U（应答字节断言，含 `CSI ? u` + DA1 的顺序）、E |
| 窗口标题 OSC 0/2 | ✅ | 原生 + 回调 | U |
| 超链接 OSC 8 | ✅ | 原生 + UI | U、R |

### 7.2 Tier 1

| 功能 | v1 | 实现方 | 测试 |
|---|---|---|---|
| 同步输出 2026（1 s 超时）| ✅ | 原生 + UI 侧 render hold | U、R（neovim / Claude Code 回放）|
| DECRQM 全表 | ✅ | 原生（9001 依赖 O1）| U |
| 下划线样式 `4:0–5`、`21`、下划线颜色 58/59、上划线 53/55 | ✅ | 原生（渲染见 05）| U |
| SGR 221 / 222 | ✗ | — | — |
| xterm 修饰键编码、modifyOtherKeys（`CSI > 4 ; n m`）| ✅ | 原生状态 + UI 线程编码器（modifyOtherKeys 状态经 O4 读出）| K |
| Kitty 键盘协议（5 个 flag、栈）| ✅ | 原生 + 编码器 | K、R |
| OSC 52 写 / 读 | ✅ | 原生 + 回调 + host（读依赖 O3）| U、P |
| OSC 7 / 9;9 / 1337 CurrentDir | ✅ | 原生 + 回调 | U |
| OSC 133（A/B/C/D/L/N/P/I）| ✅ | 原生 + 回调 | U、R（bash / zsh / fish / pwsh 集成脚本）|
| OSC 633 | ◐ 只做命令跟踪，无网格语义 | 原生（unknown 回调）+ 镜像核心 | U |
| OSC 9;4 进度、OSC 9 / 777 通知 | ✅ | 原生 + 回调 | U |
| 颜色查询 / 设置 OSC 4/10/11/12/104/110–112 | ✅ | 原生 | U |
| 2031 调色板变更通知、`CSI ? 996 n` | ✅ | 原生 + host | U、P |
| 2048 带内尺寸报告 | ✅ | 原生 + `SIZE` 回调 | U、P（顺序：TIOCSWINSZ 在前）|
| 2027 字形簇模式 | ✅（Windows 锁定为置位，§5.2）| 原生 | U、C |
| XTVERSION | ✅ | 原生 + 回调 | U |
| XTGETTCAP | ◐ Ghostty 能力表，未知名字不应答 | 原生 | U |
| DECRQSS | ◐ 不支持 `" q` | 原生 | U、E |
| XTWINOPS 14/16/18/19、22/23（标题栈）| ✅；其他操作忽略；21/20 关闭 | 原生 + `SIZE` 回调 | U、E |
| 左右边距 69 / DECSLRM | ✅ | 原生 | E |
| 鼠标 SGR-Pixels 1016、alternate scroll 1007 | ✅ | 原生 + 编码器 / UI 滚轮逻辑 | K |
| G2/G3、SS2/SS3、LS2/LS3 | ✅ | 原生 | U |
| win32-input-mode 9001（Windows）| ✅ | O1 + Windows 平台层编码 | K、P（ConPTY + PSReadLine、Far）|
| DECALN | ✅ | 原生 | E |
| Kitty 图形协议（直接传输、PNG / RGB / RGBA / zlib、放置、删除、z 层、Unicode 占位符、客户端驱动换帧）| ✅（D8 提升到 v1）| 原生 + 托管 PNG 解码器 + UI 渲染；占位符解码由 TCode 实现 | U、D、R（`kitten icat`、yazi、neovim 图片插件）|

### 7.3 Tier 2 与不支持

| 功能 | v1 | 说明 |
|---|---|---|
| Kitty 图形：文件 / 临时文件 / 共享内存传输 | ✗ | 两份核心各读一次，临时文件 / 共享内存读完即删，必然分叉；也有本地文件探测风险 [02 §11.2] |
| Kitty 图形：动画自动播放 | ✗ | C API 没有 tick 接口 [02 §8.9] |
| Kitty 图形：重连后恢复已有图像 | ✗ | 快照不含图像 [02 §11.5] |
| sixel | ✗ | libghostty 不实现；DA1 不声明 `4` |
| iTerm2 内联图像 OSC 1337 `File=` | ✗ | |
| OSC 1337 其他（`SetMark`、`SetUserVar` 等）| ✗ | 1337 编号被库识别，不会转交 unknown 回调，无法自行实现 |
| OSC 99（kitty 通知）| ✗ | 库已解析但不交给 C API |
| OSC 66（文本缩放）| ✗ | render state 不提供缩放信息 |
| OSC 5522 kitty 剪贴板协议 | ◐ 写入随 OSC 52 策略；读取回 ENOSYS | |
| 5522 粘贴事件 | ✗ | O2 停用 [A01 §3.2 规则 7] |
| 2033 可见性报告 | ◐ 永远报告可见 | 无设置接口 |
| OSC 7501 程序状态 | ✅（超出 R03 的 Tier 2 预期）| 库原生 + 回调，Claude Code 已发送 |
| XTSAVE / XTRESTORE（`CSI ? Pm s/r`）| ✅ | 原生 |
| DECSCA / DECSED / DECSEL | ✅ | 原生 |
| 反向换行 45 / 1045 | ✅ | 原生 |
| 矩形区域操作（DECFRA、DECERA、DECCRA…）| ✗ | DA1 不声明 `28`（否则 ConPTY 会改用 DECCRA [R03 §2.3]）|
| DECDHL / DECDWL | ✗ **[未核实：未在库中找到对应处理]** | |
| DECCOLM（?3）| ◐ | 库在 `?40` 允许时执行 deccolm；不改窗口尺寸 |
| 鼠标 1005 / 1015 | ◐ 库实现了这两种编码 | R03 建议忽略；接受库的行为 |
| 鼠标 1001、DEC Locator | ✗ | DECRQM 回答 0 |
| VT52 模式 | ✗ | DECRQM 2 回答 0 |
| tmux 控制模式 | ✗ | 库有编译开关，v1 不启用 |
| Glyph Protocol（Ghostty 自有 APC）| ✗ | 两份核心都关闭（快照不含注册的字形）[02 §3] |

---

## 8. 与 R03 建议的偏差

| R03 建议 | v1 实际 | 原因 | 影响 |
|---|---|---|---|
| XTGETTCAP 用 TCode 自己的编译期能力表，未知名字回 `DCS 0 + r hex ST`（§3.6）| Ghostty 内置的 `xterm-ghostty` 能力表 + `TN=xterm-256color`；未知名字不应答 | 库的实现 [S:stream_terminal.zig:852-863]；没有配置接口 | 使用超时等待应答的客户端（neovim `vim.tty.query`）会在未知名字上等到超时。能力表中的按键串与库的编码器一致，所以不会自相矛盾 |
| DECRQSS 支持 `" q`（§3.9）| 回答无效 | 库只支持 4 种 | 极少使用 |
| CSI 参数 32 个（§1.5）| 24 个 | 库常量 | Kakoune 的 17 参数 SGR 不受影响 |
| 字形簇上限 32（§6.4）| 64 | 库常量 | 无 |
| 标题 4 KiB（§1.5）| 1024 字节 | 库常量 | 无 |
| 1005 / 1015 忽略、DECRQM 回答 4 | 库实现了这两种编码 | 库行为 | 只影响主动请求它们的旧程序 |
| DECRQM：1001、2（VT52）、80（sixel）回答 4 | 回答 0 | 不在库的模式表中 | 探测这些模式的程序视为"不认识"，行为相同 |
| 歧义宽度可选"宽"（§6.3）| 不提供 | 库无此选项 | CJK 旧软件用户无法调整 |
| OSC 52 写入的数据不是合法 base64 时忽略（§8.1）| 由库解码；非法数据的处理是库的行为 **[待测]** | — | 在 U 测试中确认，若库会清空剪贴板则在 `ClipboardWrite` 中对空内容做保护 |
| Kitty 图形 Tier 2，v1 只"安全跳过"（§2.3）| v1 支持 | D8 决策 | 见 §7.2 |
| 2033 可见性"可以早做"（§2.3）| 永远可见 | 无接口 | 后台标签的 TUI 无法降低刷新率 |

---

## 9. 测试计划要点

完整计划见 11 号文档；这里只列与协议直接相关的门槛。

| 套件 | 内容 | 门槛 |
|---|---|---|
| 应答断言（U）| §2 表中每一行至少一个用例；`CSI ? u` + `CSI c` 的应答顺序；OSC 查询的 BEL / ST 终止符各一例；ConPTY 启动握手字节 | 全部通过 |
| esctest2（E）| 测试模式（DECRQCRA、XTWINOPS 全集打开），无头 PTY 宿主运行，`--expected-terminal=xterm` | Tier 0 / 1 相关测试通过率 ≥ 95%，失败项进 known-bugs（含 §8 的已知偏差）|
| 回放快照（R）| vim、htop、tmux、less、fzf、neovim（含 2027、2026、kitty 键盘探测）、Claude Code、Codex 的录制；xterm.js 与 Alacritty 的语料 | 三平台逐字节一致 |
| 编码表（K）| 键 × 修饰 × 模式（legacy / DECCKM / mok2 / kitty 各 flag / 9001）；鼠标按钮 × 修饰 × 编码 × 坐标边界 | 全部通过 |
| ucs-detect（C）| 每个发布版本；macOS / Linux 兼容模式与 2027 模式各一次，Windows 字形簇模式 | 2027 模式下 WIDE / ZWJ / VS16 / VS15 ≥ 98% |
| 差分（D）| [02 §15] | 0 差异 |
| vttest（M）| 菜单 1–8、11 | 人工验收通过 |

---

## 10. 待决问题

1. ~~DA2 应答~~：已决定用 Ghostty 的 `>1;10;0c`（2026-10-17）。
2. **Windows 程序复位 2027 后的错位**：是否值得为 Windows 构建再加一个 overlay，让 2027 永久置位（DECRQM 回答 3）。本文建议不做，先收集实际案例（[02] S8）。
3. **XTGETTCAP 未知名字不应答**：如果 neovim 等出现明显的启动延迟，考虑加 overlay 对未知名字回 `DCS 0 + r`。
4. **通知限频的具体取值**（§6.6）。
