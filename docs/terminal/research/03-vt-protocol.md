# 终端协议与兼容性规格（VT / xterm 及现代扩展）

> 用途：TCode 终端组件（Avalonia 12.1.3 + .NET 10，NativeAOT，6 个桌面 RID）的**协议规格书**。实现解析器、屏幕模型、输入编码、查询应答与安全策略时以本文为准。
> 编写日期：2026-10-16。序列、模式号、API 名保留原文。
> 目标：默认启动系统 shell，vim / neovim / tmux / htop / less / fzf / Claude Code / Codex 等 TUI 的行为必须与主流终端一致；以后要支持 SSH（远端未必有我们的 terminfo）。

## 0. 来源与可信度

每条关键结论后用方括号标出处。**[已核实]** 表示读过原文或源码；**[推断]** 表示由多份资料推出、没有直接出处；**[未核实]** 表示没查到可靠资料。

| 标记 | 来源（版本 / commit / 日期） |
|---|---|
| **[ctlseqs]** | XTerm Control Sequences，*updated for XTerm Patch #411 (2026/08/23)*，<https://invisible-island.net/xterm/ctlseqs/ctlseqs.html>，已全文通读。引用时写章节名，例如 [ctlseqs §Mouse Tracking] |
| **[xterm-man]** | xterm(1) manpage（同站 `manpage/xterm.html`），资源 `allowWindowOps`、`disallowedWindowOps`、`disallowedPasteControls` |
| **[modkeys]** | Dickey, *XTerm – "Other" Modified Keys*，<https://invisible-island.net/xterm/modified-keys.html> |
| **[ECMA-48]** | ECMA-48 第 5 版（1991，1998 年重印 PDF），§5.4 控制序列、§5.6 控制串、§8.3.117 SGR 等 |
| **[Williams]** | Paul Flo Williams, *A parser for DEC's ANSI-compatible video terminals*，<https://vt100.net/emu/dec_ansi_parser>（最后修订 2017-08-25） |
| **[VT510]** | VT510 Programmer Information，<https://vt100.net/docs/vt510-rm/>：DA1 / DA2 / DA3、DECRQM / DECRPM、DECRQSS / DECRPSS、DECSCUSR、DECSTBM、DECSLRM、DECOM、DECSC、DECSTR、ICH / DCH / ECH / IL / DL、chapter4 §4.3.3 |
| **[kitty-kbd] [kitty-gfx] [kitty-ul] [kitty-misc] [kitty-clip] [kitty-si]** | kitty 仓库 `docs/keyboard-protocol.rst`、`graphics-protocol.rst`、`underlines.rst`、`misc-protocol.rst`、`clipboard.rst`、`shell-integration.rst` @ `f426c08`（2026-10-16，kitty 0.49.2） |
| **[osc8]** | egmontkob, *Hyperlinks in terminal emulators*，<https://gist.github.com/egmontkob/eb114294efbcd5adb1944c9f3cb5feda> |
| **[sync]** | contour `docs/vt-extensions/synchronized-output.md` @ `8f3a4a4`（2026-10-16），以及原始 gist christianparpart/d8a62cc1… |
| **[2027]** | contour-terminal/terminal-unicode-core `spec/terminal-unicode-core.tex` @ `64f5385`（draft rev.1） |
| **[2031]** | contour `docs/vt-extensions/color-palette-update-notifications.md` |
| **[2048]** | rockorager, *In-Band Window Resize Notifications*（gist e695fb2924d36b2bcf1fff4a3704bd83） |
| **[2033]** | rockorager, *Visibility reports*，<https://rockorager.dev/misc/visibility-reports/> |
| **[semprompt]** | Per Bothner, `proposals/semantic-prompts.md`（freedesktop terminal-wg，2019） |
| **[FinalTerm]** | p-e-w/finalterm `src/TerminalStream.vala:375,542-549` @ `f46ffd0` |
| **[iterm2-esc] [iterm2-feat]** | <https://iterm2.com/documentation-escape-codes.html>、<https://iterm2.com/feature-reporting/>（2026-10 抓取） |
| **[vscode-si]** | <https://code.visualstudio.com/docs/terminal/shell-integration>，"Supported escape sequences" 一节 |
| **[conemu]** | <https://conemu.github.io/en/AnsiEscapeCodes.html>，"ConEmu specific OSC" |
| **[wt-progress]** | Microsoft Learn, *Set the progress bar in the Windows Terminal*（updated 2025-08-21） |
| **[wt-4999]** | microsoft/terminal `doc/specs/#4999 - Improved keyboard handling in Conpty.md`（win32-input-mode） |
| **[UAX11]** | UAX #11 rev 46（Unicode 18.0.0，2026-07-31） |
| **[UAX29]** | UAX #29 rev 49（Unicode 18.0.0，2026-09-01） |
| **[wcwidth-spec]** | jquast/wcwidth `docs/specs.rst` @ `bcdf2c1`（v0.9.3） |
| **[ucs-detect]** | jquast/ucs-detect `README.rst`、`data/*.yaml` @ `50f011d`：2026-09/10 实测 35 个以上终端的结果 |
| **[quast-2026]** | Jeff Quast, *Perfecting Terminal Character Width Using Correction Tables*（2026-06-07） |
| **[TTWG]** | Unicode L2/25-263, *Text Terminal Working Group report*（UTC #185） |
| **[dgl-2023]** | David Leadbeater, *ANSI Terminal Security in 2023 and finding 10 CVEs*，<https://dgl.cx/2023/09/ansi-terminal-security> |
| **[ncurses]** | ncurses `terminfo.src` $Revision 1.1198$（invisible-island current） |
| **[ghostty-terminfo]** | <https://ghostty.org/docs/help/terminfo> |
| 源码 | `ghostty@b3dc3d7`（2026-10-15）；`wezterm@e2a98e1`（2026-10-10）；`alacritty@5421470`（2026-10-16，0.18.0-dev）；`alacritty/vte@abeae76`；`xterm.js@991857a`（2026-10-13，package 6.0.0）；`kitty@f426c08`；`microsoft/terminal@44b440b`（2026-10-13）；`iTerm2@5794785`（2026-10-15）；`tmux@bfb01dc`（2026-10-16）；`esctest2@2798f12`；`vttest-snapshots@0229d71`（vttest 2.7 / 20251205）；vim `src/term.c`、`src/optiondefs.h`（master，2026-10-16 抓取）；neovim `src/nvim/tui/tui.c`、`runtime/lua/vim/_core/defaults.lua`（master）；emacs `lisp/term/xterm.el`（master）；openai/codex `codex-rs/tui/src/{tui.rs,terminal_probe.rs,tui/keyboard_modes.rs}`（main）；anthropics/claude-code `CHANGELOG.md`（2.1.301） |
| **[本机]** | macOS 26.6.2：`infocmp -V` 输出 ncurses 6.0.20150808；系统没有 `xterm-ghostty` 条目，有 `tmux-256color`；libc `wcwidth` 实测（见 §6.2） |

文中写到 "Ghostty / WezTerm / WT / xterm.js / iTerm2 / kitty / Alacritty" 的支持情况时，凡是标 ✅ / ❌ 的都查过上表中的源码或 ucs-detect 实测数据。查不到的标"未核实"。

---

## 1. 解析器状态机规格

### 1.1 基本模型

TCode 的解析器以 **[Williams] 的 DEC ANSI 状态机**为骨架，叠加 xterm 及现代终端公认的扩展。Alacritty `vte`、WezTerm `vtparse`、Ghostty `Parser.zig`、Windows Terminal `stateMachine.cpp` 都是这样做的 [已核实：各仓库文件头注释]。

状态（14 个，不含 UTF-8 子状态）：

| 状态 | 进入条件 | 字节处理（GR 区 A0–FF 的处理见 §1.3） |
|---|---|---|
| `ground` | 初始；各序列 dispatch 之后 | 00–17, 19, 1C–1F → **execute**；20–7F → **print**（DEL 7F 实际忽略，§1.4） |
| `escape` | 任意状态遇到 1B | entry 执行 **clear**；C0 → execute；7F 忽略；20–2F → collect，转 `escape_intermediate`；`[`(5B) → `csi_entry`；`]`(5D) → `osc_string`；`P`(50) → `dcs_entry`；`X`(58) `^`(5E) `_`(5F) → `sos_pm_apc_string`；其余 30–7E → **esc_dispatch** 后回 `ground` |
| `escape_intermediate` | ESC 之后来了 20–2F | 20–2F → collect；30–7E → esc_dispatch；C0 → execute |
| `csi_entry` | `ESC [` 或 C1 9B | entry 执行 clear；30–39、3B → param，转 `csi_param`；**3A（冒号）→ 子参数**，转 `csi_param`（这是对 Williams 的偏离，见下）；3C–3F → collect（私有标记），转 `csi_param`；20–2F → collect，转 `csi_intermediate`；40–7E → **csi_dispatch** |
| `csi_param` | | 30–39、3A、3B → param；3C–3F → `csi_ignore`；20–2F → `csi_intermediate`；40–7E → csi_dispatch |
| `csi_intermediate` | | 20–2F → collect；30–3F → `csi_ignore`；40–7E → csi_dispatch |
| `csi_ignore` | 格式错误 | 20–3F、7F 忽略；40–7E → 回 `ground`，**不 dispatch**；C0 仍然 execute |
| `dcs_entry` / `dcs_param` / `dcs_intermediate` | `ESC P` / 90 | 与 CSI 对应状态相同，但 **C0 一律忽略**；40–7E → **hook**，转 `dcs_passthrough` |
| `dcs_passthrough` | | 00–17、19、1C–7E → **put**；7F 忽略；以 ST / CAN / SUB / ESC 结束 → **unhook** |
| `dcs_ignore` | DCS 头部格式错误 | 一律忽略，直到 ST |
| `osc_string` | `ESC ]` / 9D | entry 执行 osc_start；20–7F → osc_put；**BEL(07) 结束 OSC**（xterm 扩展）；其他 C0 忽略；exit 执行 osc_end |
| `sos_pm_apc_string` | `ESC X` / `ESC ^` / `ESC _` | Williams 规定全部忽略；**TCode 必须把 APC 内容交给处理器**（kitty 图形协议用 `ESC _G…ESC \`），SOS / PM 照旧忽略 |

"anywhere"转换，在任何状态下优先生效 [Williams §anywhere；ghostty `parse_table.zig:56-85`]：

- `18` CAN、`1A` SUB：取消当前序列，execute（SUB 本应显示 ␦，xterm 不显示），回 `ground`。
- `1B` ESC：取消当前序列，进入 `escape`。控制串里的 `ESC \` 由此自然变成"ESC 取消串，然后 `\` 作为 ST 的 esc_dispatch，等于空操作"，所以**不需要向前看** [Williams §escape]。
- C1（80–9F）：见 §1.3。

动作的执行顺序是：**旧状态 exit → 转换动作 → 新状态 entry** [Williams §The State Diagram]。

### 1.2 与 Williams 原版的偏离（必须做）

| 偏离 | 理由与来源 |
|---|---|
| CSI 参数里的 `:`（3A）作为**子参数分隔符**，不再进入 ignore | ECMA-48 §5.4.2(b) 允许 3/10 作为参数子串内的分隔；xterm 支持 `38:2:Pi:R:G:B`、`4:3` 等 [ctlseqs §CSI Pm m]；kitty 键盘协议的应答也用冒号 [kitty-kbd]。Ghostty 只对 `m` 接受冒号，其他 final 带冒号就丢弃整个序列（`Parser.zig` csi_dispatch，`warnCsiSepMismatch`）。WT 用白名单 `_CanSeqAcceptSubParam` [`OutputStateMachineEngine.hpp:251`]。**TCode 做法**：解析层保留每个参数的子参数，由 dispatch 决定；`m`、`=u`、`>4:Nm`（XTMODKEYS）、`48t` 之外的 final 带了冒号就丢弃 |
| OSC 可以用 BEL 结束 | [ctlseqs §Operating System Commands]："XTerm accepts either BEL or ST… uses the same terminator used in a query"。**应答必须沿用请求的结束符** |
| ground 与 OSC 中接收 UTF-8 | 见 §1.3 |
| APC 内容不再丢弃 | kitty 图形协议（Tier 2）。即使暂不实现，也要能**正确跳过任意长度的 APC** |
| DCS 的 `put` 接收 80–FF | ghostty `parse_table.zig:240-262`：DCS 载荷（如 tmux 控制模式）承载 UTF-8，不能被 C1 切断 |

### 1.3 C1 与 UTF-8

结论 [已核实 ctlseqs §Control Bytes；ghostty `stream.zig:1213-1240` 及其测试 `stream.zig:3360-3450`；dgl-2023 建议"Do not implement C1 control characters in UTF-8"]：

1. **字节流始终按 UTF-8 解码**。TCode 不支持 ISO-8859 等非 UTF-8 编码，也不支持 `ESC % @`。
2. **原始 8 位 C1 字节（80–9F）在 ground 中视为非法 UTF-8**，输出 U+FFFD，**不当作控制符**。xterm 原文的说明："It is not possible to use a C1 control obtained from decoding the UTF-8 text, because that would require reprocessing the data."
3. **UTF-8 解码得到的 U+0080–U+009F 码点直接丢弃**，既不打印也不执行（与 xterm、Ghostty 一致）。差异提醒：WezTerm 会把解码出的 C1 当控制符执行（`vtparse/src/lib.rs` `next_utf8` 的"Slightly gross special cases"），Alacritty vte 也会执行（`lib.rs` `ground_dispatch`）。TCode 跟随 xterm / Ghostty，这条在安全上更稳妥。
4. 在 escape / CSI / DCS 头部，高位字节只可能来自 C1 或垃圾数据。CSI 参数区只接受 7 位字节。不接受 8 位 C1 引导符（9B / 9D / 90），也就不需要 S8C1T。**`ESC SP G`（S8C1T）直接忽略，应答永远用 7 位形式** [推断：xterm 默认 7 位输出；VT510 S8C1T 页]。
5. OSC 载荷按 UTF-8 收集，例如标题、URL、`OSC 52` 的 base64。CAN / SUB / ESC 能打断它。
6. **UTF-8 解码器状态独立于 VT 状态机**。块边界上被截断的多字节序列要跨 `Feed()` 调用保存（Alacritty `partial_utf8`）。多字节序列被 ESC 打断时，先输出 U+FFFD，再处理 ESC（alacritty-vte `lib.rs` `advance_ground` 的 "Process bytes cut off by escape"）。

### 1.4 C0 语义（ground）

| 字节 | 行为 | 备注 |
|---|---|---|
| NUL 00 | 忽略 | Ghostty 也忽略 SOH / STX（WT #10786）[ghostty `stream.zig` execute] |
| ENQ 05 | 应答空串 | xterm 默认 answerback 为空 [ctlseqs §Single-character functions]。**禁止配置成可变内容** |
| BEL 07 | 响铃 | UI 可设为视觉提示、系统通知或忽略 |
| BS 08 | 光标左移一格，左边界处停止 | 反向换行（45 / 1045）属于 Tier 2 |
| HT 09 | 移到下一个制表位，没有则到右边界 | |
| LF 0A / VT 0B / FF 0C | 都等同于 LF：IND 语义；LNM（`CSI 20 h`）置位时再加 CR | [ctlseqs]："FF is treated the same as LF"、"VT … treated the same as LF" |
| CR 0D | 回到行首；DECOM / DECLRMM 时回到左边距 | |
| SO 0E / SI 0F | LS1 / LS0 切换 GL | 用于 G1 线框字符集 |
| 其他 C0 | 忽略 | |
| DEL 7F | 忽略 | 只在 96 字符集映射到 GL 时可打印 [Williams §ground]，TCode 不支持 96 字符集 |

### 1.5 参数与上限

| 项 | 规范说法 | 各实现 | **TCode 规格** |
|---|---|---|---|
| 参数个数 | VT510 / Williams：16 个，多余的静默忽略 [VT510 §4.3.3；Williams §param] | xterm.js 32 [`Params.ts:78`]；WT 32 [`stateMachine.hpp:32`]；alacritty-vte 32 [`params.rs:5`]；Ghostty 24（因为 Kakoune 发过 17 个参数的 SGR）[`Parser.zig:196-209`]；WezTerm 256；kitty 256 [`vt-parser.c:22`]；iTerm2 feature-reporting 要求 SGR / DECSET 至少支持 15 个 [iterm2-feat §Parsing] | **32 个参数 + 共 256 个子参数槽**。超出部分丢弃，但仍要识别 final 并跳过整个序列。**不要**因为参数溢出就把整个 SGR 作废，前 32 个照常执行（Alacritty 会作废，那是反例） |
| 单个子参数个数 | 无规定 | WT 每个参数最多 6 个 [`stateMachine.hpp:35`]；xterm.js 共 32 个 | 每个参数最多 8 个。`38:2::R:G:B` 需要 5 个 |
| 参数值上限 | VT510：大于 9999 按 9999 处理；Williams：至少要能表示 16383 | WT 65535；xterm.js 0x7FFFFFFF；Ghostty u16 饱和 | **饱和到 65535**，具体控制函数再自行钳制，例如光标移动钳到屏幕尺寸，REP 钳到 65535 |
| 默认值 | 空参数和 0 都表示默认值，但默认值随函数而定（多数为 1）[Williams §param；ECMA-48 §5.4.2(e)] | | 解析层区分"空"和"0"，dispatch 再按函数解释。**例外**：SGR 的空参数等于 0；`CSI 0 J` 就是 `ED 0` |
| 私有标记 | 只能出现在第 1 个字节：`< = > ?` | | 出现在其他位置时进入 ignore |
| intermediate 个数 | 规范不限；DEC 最多用 2 个 | 2（Alacritty、WezTerm）；4（Ghostty） | **最多 2 个**，超出则标记 ignore，dispatch 改为空操作 |
| OSC 长度 | 无规定 | Ghostty 2048 字节以内放栈缓冲，最多 8 MiB [`osc.zig:361-365`]；xterm.js OSC / DCS / APC 载荷上限 10,000,000 字节 [`Constants.ts:67`]；kitty 单个转义序列上限 256 KiB [`vt-parser.c:18-21`]；alacritty-vte no_std 时 1024 | **按 OSC 编号设上限**：标题 4 KiB；OSC 8 的 URI 8 KiB（[osc8] 写到 VTE / iTerm2 用 2083 字节）、id 256 字节；OSC 52 默认 8 MiB（可配置）；OSC 4 / 10–19 / 104 和 133 / 633 / 7 / 9 / 1337 一律 64 KiB；未知编号 4 KiB。超限后剩余字节**吞掉直到 ST / BEL**，不 dispatch |
| DCS 长度 | | 同上 | XTGETTCAP / DECRQSS 4 KiB；sixel 和 tmux 控制模式（Tier 2）流式处理，不整体缓冲；未知 DCS 流式丢弃 |
| APC 长度 | | | kitty 图形每个 chunk 4096 字节（规范要求客户端如此分块）；图像总量另设配额 [kitty-gfx §Image persistence：kitty 每个 buffer 320 MB] |

**防护要求**：所有缓冲都要有上限；超限后进入"吞掉"子状态，**不得**提前回到 ground，否则串尾残余会被当成可打印文本或命令注入。参数数字累加用饱和运算，参见 Ghostty `Parser.zig` 的 `*|=`、`+|=` 和 `param_acc_idx` 溢出保护。用 fuzz 测试保证"任意字节流不崩溃、内存有界"（§9）。

### 1.6 dispatch 规则

- 按 (私有标记, intermediates, final) 选择处理函数，未知组合静默忽略。
- 参数按从左到右顺序处理，冲突时后者覆盖前者，例如 `CSI 7;0 m` 结果是不反显 [Williams §3.5.5]。
- 实现上，SM / RM、DECSET / DECRST 的多参数形式等于逐个执行。

---

## 2. 分级功能矩阵

图例：✅ 支持；❌ 不支持；◐ 部分支持或有附加条件；? 未核实。依据见每节表后的注。

- **Tier 0**：常见 TUI 必需，缺了 vim / tmux / htop / less / fzf / Claude Code / Codex 会出错。
- **Tier 1**：现代主流终端都支持，TUI 会探测后启用，缺了会降级。
- **Tier 2**：可选，以后再做。

### 2.1 Tier 0：基础控制与编辑

| 功能 | 序列 | 语义要点与边界 | Gh / Wz / WT / xjs / iT2 / kit / Ala |
|---|---|---|---|
| 光标移动 | `CUU A` `CUD B` `CUF C` `CUB D` `CNL E` `CPL F` `CHA G` `CUP H` `HVP f` `VPA d` `VPR e` `HPA \`` `HPR a` | 参数 0 等于 1；结果钳到屏幕或滚动区边界。CUU / CUD 在滚动区内时停在边距，在区外时停在屏幕边缘。CUP / HVP / VPA / HPA 受 DECOM 影响。**所有移动都会清除 pending-wrap 标志** | 全部 ✅ |
| 自动换行 DECAWM | `CSI ? 7 h/l`，默认开 | **延迟换行（xenl）**：写满最后一列后光标留在原处，置 pending-wrap，下一个可打印字符到来时才换行。CR / BS / 光标移动会清除该标志。宽字符在最后一列放不下时：本行末格留空（Ghostty 称 spacer_head），整个字符换到下一行 | 全部 ✅ |
| 擦除 | `ED J`（0 / 1 / 2 / **3 清空 scrollback**）`EL K`（0 / 1 / 2）`ECH X` | 被擦除的格子用**当前背景色**填充（bce）[ncurses xterm 条目带 `bce`]。ECH 不移动光标，也不受滚动区限制 [VT510 ECH]。ED / EL 擦到宽字符的一半时，整个宽字符都要清掉 | 全部 ✅ |
| 插入 / 删除 | `ICH @` `DCH P` `IL L` `DL M` | ICH / DCH 只影响光标到右边距之间。IL / DL 只在滚动区内生效，光标在区外时无效，执行后光标回到左边距 [VT510 IL、DL；ghostty `Terminal.zig:2998,3172`]。被移入的空白带当前 SGR 背景。DCH 切开宽字符时用空格替换 [ghostty deleteLines 注释] | 全部 ✅ |
| 滚动 | `SU S` `SD T`（及 ECMA 早期的 `CSI ^`）`IND ESC D` `RI ESC M` `NEL ESC E` | 只在滚动区内滚动。**只有滚动区等于全屏、处于主屏、且行从顶部滚出时**才进入 scrollback。RI 在顶边距时整区下滚 | 全部 ✅ |
| 滚动区 DECSTBM | `CSI t;b r` | 参数非法（t ≥ b）时重置为全屏。设置后光标移到原点（受 DECOM 影响）[VT510 DECSTBM] | 全部 ✅ |
| 原点模式 DECOM | `CSI ? 6 h/l` | 置位时坐标相对滚动区，光标不能移出滚动区。**CPR 应答也取相对坐标** [ghostty `stream_handler.zig:850-860`] | 全部 ✅ |
| 保存 / 恢复光标 | `DECSC ESC 7` / `DECRC ESC 8`；`CSI s` / `CSI u`（SCOSC / SCORC，DECLRMM 置位时 `CSI s` 改为 DECSLRM） | 保存内容：位置、SGR、G0–G3 与 GL / GR、pending-wrap、DECOM、DECSCA、单次切换 [VT510 DECSC]。主屏和 alt 屏**分别保存**。没保存过就 DECRC 时，回到原点并重置属性 | 全部 ✅ |
| SGR 基本属性 | `CSI … m`：0 1 2 3 4 5 7 8 9 21 22 23 24 25 27 28 29 30–37 39 40–47 49 90–97 100–107 | 22 同时清除粗体和淡色。21 表示双下划线（xterm 如此），不是"取消粗体"。8 隐藏，28 显示 [ctlseqs §CSI Pm m；ECMA-48 §8.3.117] | 全部 ✅ |
| 256 色与真彩色 | `38;5;n` `48;5;n` `38;2;r;g;b` `38:5:n` `38:2:Pi:r:g:b` `38:2::r:g:b` | 两种写法都要支持："xterm allows either colons (standard) or semicolons (legacy)… (but after the first colon, colons must be used)" [ctlseqs]。分号写法的 `38;2` 吃掉后面 3 个参数，`38;5` 吃掉 1 个。冒号写法 `38:2:` 后面若有 4 个值，第一个是色彩空间 Pi（忽略）；若只有 3 个值，按 R:G:B 解释（兼容写法，iTerm2 feature 文档要求 "<more subparameters> should be ignored"）[推断 + iterm2-feat §24BIT] | 全部 ✅ |
| 光标显隐 DECTCEM | `CSI ? 25 h/l` | | 全部 ✅ |
| 备用屏 | `?47` `?1047` `?1048` `?1049` | **1049** = 保存光标 + 切换到 alt 屏 + 清空 alt 屏；退出时切回主屏并恢复光标 [ctlseqs DECSET 1049]。1047 退出时如在 alt 屏则先清空 alt 屏。47 只切换，不清屏也不保存光标。alt 屏没有 scrollback。**kitty 键盘协议的标志栈按屏分开**（§4.4） | Gh ✅ Wz ✅ WT ◐（只有 1049）xjs ✅ iT2 ✅ kit ✅ Ala ◐（只有 1049） |
| 光标键模式 DECCKM | `CSI ? 1 h/l` | 置位时方向键、Home、End 发 `SS3`（`ESC O A`）[ctlseqs §PC-Style Function Keys] | 全部 ✅ |
| 小键盘模式 | `ESC =` DECKPAM / `ESC >` DECKPNM；`?66` DECNKM | 见 §4.1 | Gh ✅ Wz ✅ WT ✅ xjs ✅ iT2 ✅ kit ? Ala ? |
| 插入模式 IRM / 自动换行模式 LNM | `CSI 4 h/l` / `CSI 20 h/l` | IRM 置位时打印字符前先执行 ICH。LNM 很少用，但 vttest 会测 | Gh ✅ Wz ✅ WT ✅ xjs ✅ iT2 ✅ kit ✅ Ala ✅ |
| 制表位 | `HTS ESC H`、`TBC CSI g`（0 / 3）、`CHT CSI I`、`CBT CSI Z`、`DECST8C CSI ? 5 W` | 初始每 8 列一个。缩小窗口不丢失制表位，**放大后新增的列也按 8 列间隔补齐** [推断：xterm / ghostty 行为]。TBC 只认 0 和 3 [ctlseqs CSI Ps g] | 全部 ✅ |
| 字符集 | `ESC ( 0` / `ESC ( B` / `ESC ) 0`，SO / SI，`ESC ( A`（英国） | 只需支持 **ASCII、DEC Special Graphics（`0`）和 UK**。DEC 线框映射表见 ghostty `charsets.zig:53-87`（0x60–0x7E 映射到 U+25C6 … U+00B7）。tmux、ncurses 在 `acsc` 里用到它，terminfo 中 `smacs=\E(0`、`rmacs=\E(B`。G2 / G3 和 SS2 / SS3、LS2 / LS3 解析后归入 Tier 1 | 全部 ✅ |
| REP | `CSI n b` | 重复**前一个可打印字符**。前一个是控制符时不起作用 [ECMA-48 §8.3.103]。n 要钳制。ncurses 的 `rep` 能力会用到（xterm-new 包含 `ansi+rep`）[ncurses] | Gh ✅ Wz ✅ WT ✅ xjs ✅ iT2 ✅ kit ✅ Ala ✅ |
| 软复位 / 硬复位 | `DECSTR CSI ! p` / `RIS ESC c` | DECSTR 的复位项见 [VT510 DECSTR 表 5-9]：DECTCEM 开、IRM 关、DECOM 关、**DECAWM 关**（DEC 原文如此；xterm 实际保持开启。**TCode 跟 xterm 一样复位为开**，因为 terminfo `rs2` 中 `\E[!p` 之后不会再开 DECAWM [推断]）、DECCKM 关、DECSTBM 全屏、SGR 0、字符集默认、保存的光标回原点。RIS 额外清屏、清 scrollback，并重置调色板、标题、所有模式、kitty 键盘栈、2027 | 全部 ✅ |
| 括号粘贴 | `CSI ? 2004 h/l` | 粘贴内容用 `ESC[200~ … ESC[201~` 包裹 [ctlseqs §Bracketed Paste Mode]。安全处理见 §8.4 | 全部 ✅ |
| 焦点事件 | `CSI ? 1004 h/l` | 获得焦点发 `CSI I`，失去焦点发 `CSI O` [ctlseqs §FocusIn/FocusOut]。ConPTY 启动时会主动开启 1004（§4.5） | 全部 ✅ |
| 鼠标 | 9 / 1000 / 1002 / 1003 + 1006 | 见 §5 | 全部 ✅ |
| 光标形状 DECSCUSR | `CSI Ps SP q`：0 / 1 闪烁块，2 稳定块，3 / 4 下划线，5 / 6 竖线 | xterm 中 0 等于 1，`7` 恢复为配置值 [ctlseqs]。iTerm2 和 Ghostty 把 0 当作"恢复用户默认"（`Se=\E[0 q` / `\E[2 q`）[iterm2-esc §DECSCUSR 0；ghostty-terminfo]。**TCode：0 = 用户默认形状**（vim / neovim 退出时会发 `Se`）。同时支持 `CSI ? 12 h/l`（闪烁） | 全部 ✅ |
| 设备查询 | DA1 / DA2 / DSR 5 / DSR 6（CPR）/ DECRQM | 应答内容见 §3。vim、neovim、tmux、Emacs、Codex 启动时都会发出查询（§7.3）。**没有应答会导致这些程序超时**，例如 Codex 每次探测等 250 ms [`terminal_probe.rs:21`] | 全部 ✅ |
| 窗口标题 | `OSC 0` / `OSC 2`（标题），`OSC 1`（图标名，可以忽略） | 标题按 UTF-8 解析，截断到 4 KiB，**去掉控制字符**（iTerm2 先把控制字符转成脱字符表示 [`VT100Terminal.m:2721-2731`]）。标题栈见 Tier 1 | 全部 ✅ |
| 超链接 OSC 8 | `OSC 8 ; params ; URI ST` … `OSC 8 ; ; ST` | 超链接是每个格子的一项属性，类似颜色。`id=` 用来把分离的格子连成一个链接。URI 只允许 32–126 范围的字节。安全要求见 §8.3 [osc8] | Gh ✅ Wz ✅ WT ✅ xjs ✅ iT2 ✅ kit ✅ Ala ✅ |

**Tier 0 的注**：Alacritty 支持的模式清单在 `alacritty/vte src/ansi.rs:939-967`；WT 在 `DispatchTypes.hpp:518-549`；Ghostty 在 `modes.zig:362-416`；xterm.js 是 `InputHandler.ts` 中 `setModePrivate` 的 case 列表；kitty 在 `modes.h`；WezTerm 在 `wezterm-escape-parser/src/csi.rs:885-959`；iTerm2 是 `VT100Terminal.m` 的 mode case 列表。

### 2.2 Tier 1：现代主流终端都支持

| 功能 | 序列 | 语义要点与边界 | Gh / Wz / WT / xjs / iT2 / kit / Ala |
|---|---|---|---|
| 同步输出 2026 | `CSI ? 2026 h`（BSU）/ `l`（ESU）；用 `CSI ? 2026 $ p` 探测 | 置位期间渲染器继续显示上一帧，解析照常进行。复位时再取最新状态渲染。**必须有超时**：Ghostty 1000 ms [`termio/Thread.zig:38`]，kitty 默认 2000 ms [`screen.c:3842`]，WT 100 ms [`renderer.cpp:576`]。**TCode 取 1000 ms**。DECRPM 应答 1 / 2 [sync]。neovim 和 tmux 会先探测再使用 [`tui.c:575-605`；`tty.c:413-430`] | ✅ ✅ ✅ ✅ ✅ ✅ ✅ |
| DECRQM / DECRPM | `CSI Ps $ p`、`CSI ? Ps $ p` → `CSI [?]Ps;Pm $ y` | Pm：0 = 不认识，1 = 置位，2 = 复位，3 = 永久置位，4 = 永久复位 [VT510 DECRPM]。**每次只查一个模式**。所有已实现的模式都必须如实应答，否则 neovim / kitty 生态的探测会失败 | ✅ ✅ ✅ ✅ ✅ ✅ ✅ |
| SGR 下划线样式 | `4:0` `4:1` `4:2` `4:3`（波浪）`4:4`（点）`4:5`（虚线），`21`（双下划线） | [kitty-ul] terminfo 扩展能力 `Smulx=\E[4:%p1%dm`。**`4:` 之后的子参数不能被误读成 SGR 4 + 下一个参数**。只出现 `4` 时表示单下划线 | ✅ ✅ ◐（单 / 双 [`adaptDispatchGraphics.cpp:247-253`]）✅ ✅ ✅ ✅ |
| 下划线颜色 | `58:2::r:g:b`、`58:5:n`、`58;5;n`、`58;2;r;g;b`、`59` | 语法同 38 / 48 [kitty-ul]。反显时不反转下划线颜色（未设色时跟随前景）。terminfo 扩展能力 `Setulc`、`Su` | ✅ ✅ ✅ ✅ ✅ ✅ ✅ |
| 上划线 | `SGR 53` / `55` | ECMA-48 定义；terminfo 扩展能力 `Smol` / `Rmol` | ✅ ? ? ? ? ? ? |
| 独立复位粗体 / 淡色 | `SGR 221` / `222` | kitty 扩展 [kitty-misc]。成本低，建议实现 | ? ? ? ? ? ✅ ? |
| DECSCUSR | 见 Tier 0 | | |
| 修饰键编码（xterm） | 方向键、F1–F12、编辑键带修饰参数 `CSI 1;m A`、`CSI n;m ~` | 见 §4.1。属于 Tier 0 级别的必需功能 | ✅ 全部 |
| modifyOtherKeys | `CSI > 4 ; Pv m`（0 / 1 / 2，3 只能通过资源设置）；查询用 `CSI ? 4 m` | 见 §4.2。neovim 在 kitty 键盘协议探测失败后改用它 [`tui.c:316-330,2554-2563`]；vim 默认对 `xterm*` 使用 `mok2` [vim `optiondefs.h:1582-1584`] | ? ✅ ? ? ? ❌（kitty 明确反对）✅（`ansi.rs:1684-1700`） |
| kitty 键盘协议 | `CSI = f ; m u`、`CSI ? u`、`CSI > f u`、`CSI < n u` | 见 §4.4。Claude Code、Codex、neovim、vim、fish 都会使用 | ✅ ◐（需开启 `enable_kitty_keyboard`，默认 false）◐（新代码有；ConPTY 中被强制关闭，见 §4.5）✅ ✅ ✅ ✅（默认开）|
| OSC 52 写剪贴板 | `OSC 52 ; Pc ; base64 ST` | Pc 为空、`c`、`p`、`s`、`0`–`7` 时都按系统剪贴板处理（主选区只在 Linux 有）。数据不是合法 base64 时**清空剪贴板**（xterm 语义）。改为**忽略**更安全 [推断] | ✅ ✅ ✅ ◐（addon）✅（需在偏好设置开启）✅ ✅（默认 OnlyCopy） |
| OSC 52 读剪贴板 | `OSC 52 ; Pc ; ? ST` | **默认询问或拒绝**（§8.1） | ◐ ask / ❌ 忽略 [`performer.rs:792`] / ❌ 忽略 [`OutputStateMachineEngine.cpp:821-830`] / ? / ◐ / ◐ ask / ❌ |
| OSC 7 当前目录 | `OSC 7 ; file://host/path ST` | URL 要做百分号解码。host 不是本机时只显示，不用来新建标签页（SSH 场景）。安全要求见 §8.5 | ✅ ✅ ✅ ❌（核心库没注册）✅ ✅ ?（未核实） |
| OSC 133 语义提示 | `A` `B` `C` `D[;exit]`，扩展 `L` `N` `P` `I` | 见 §2.4 | ✅ ✅ ✅ ❌（核心没有，VS Code 在上层实现）✅ ✅ ❌ |
| OSC 633（VS Code） | `A B C D E P` | 见 §2.4 | ❌ ❌ ✅ —（VS Code 上层）❌ ❌ ❌ |
| OSC 9;4 进度 | `OSC 9 ; 4 ; st ; pr ST` | 见 §2.5 | ✅ ✅ ✅ ◐ addon-progress ✅ ✅（0.47.0）? |
| 颜色查询与设置 | `OSC 4 ; n ; spec|?`、`OSC 10/11/12 ; spec|?`、`OSC 104 / 110 / 111 / 112` | 查询应答 `OSC 11 ; rgb:rrrr/gggg/bbbb ST`（每个分量 16 位）。**应答的结束符与请求相同**。neovim、Codex、tmux、Emacs 用它判断明暗主题 [`tui.c:353`；`tty.c:413-430`；`xterm.el:815-818`] | ✅ ✅ ✅ ✅ ✅ ✅ ✅ |
| 2031 调色板变更通知 | `CSI ? 2031 h/l`；查询 `CSI ? 996 n` → `CSI ? 997 ; 1|2 n` | 1 = 暗色，2 = 亮色。2031 置位后，主题或调色板一变化就主动发送 [2031] | ✅ ❌ ❌ ✅ ✅ ✅ ❌ |
| 2048 带内尺寸报告 | `CSI ? 2048 h/l` → `CSI 48 ; rows ; cols ; hpx ; wpx t` | 开启时**立即报告一次**，之后每次尺寸变化（包括字号变化）在 **PTY 尺寸更新完成后**报告。像素值不可得时填 0 [2048] | ✅ ❌ ❌ ❌ ✅ ✅ ❌ |
| 2027 字形簇模式 | `CSI ? 2027 h/l` | 见 §6 | ✅（可改）◐（永久置位）◐（永久置位）❌ ◐（永久复位）❌ ❌（据 [ucs-detect] data/*.yaml） |
| XTVERSION | `CSI > q` / `CSI > 0 q` → `DCS > | name version ST` | tmux 靠它识别外层终端 [`tty-keys.c` extended DA] | ✅ ✅ ❌ ✅ ✅ ✅ ❌ |
| XTGETTCAP | `DCS + q hex;hex ST` → 每项 `DCS 1 + r hex=hex ST`，未知项 `DCS 0 + r hex ST` | 见 §3.6 | ✅ ✅ ❌ ❌ ✅ ✅ ❌ |
| DECRQSS | `DCS $ q Pt ST` → `DCS 1 $ r … ST` 或 `DCS 0 $ r ST` | **xterm 的有效 / 无效码和 VT510 手册正好相反**（ctlseqs §Further reading："the documented code for a valid or invalid response to DECRQSS was reversed"）。**TCode 按 xterm：1 = 有效**。需要支持：`m`、`r`、`s`、`SP q`、`" q` | ✅（m / SP q / r / s）✅ ✅ ? ✅ ✅ ❌ |
| XTWINOPS 子集 | `CSI 14 t`（文本区像素 → `CSI 4;h;w t`）、`CSI 16 t`（格子像素 → `CSI 6;h;w t`）、`CSI 18 t`（行列 → `CSI 8;rows;cols t`）、`CSI 22;0|1|2 t` 和 `CSI 23;… t`（标题栈） | 其他操作（1–11、13、15、19–21、24+）**默认禁用**。xterm 默认也禁用 GetWinTitle 等 [xterm-man disallowedWindowOps]。只支持 18 t 的话 esctest 的部分用例跑不了（§9） | Gh ◐ / Wz ◐（栈只解析不生效 [`mod.rs:2215-2217`]）/ WT ◐（只有 1,2,7,8,14,16,18 [`DispatchTypes.hpp:584-594`]）/ xjs ◐（可配置）/ iT2 ✅ / kit ✅ / Ala ◐（14,18,22,23） |
| 标题栈 | `CSI 22 ; 0|1|2 t` / `CSI 23 ; … t` | vim 和 neovim 用 terminfo 的 `XT` 推断后会使用。栈深上限 10 [ctlseqs XTWINOPS 说明] | ✅ ❌ ❌ ◐ ✅ ✅ ✅ |
| 左右边距 | `CSI ? 69 h/l`（DECLRMM）、`CSI l;r s`（DECSLRM） | 69 置位时 `CSI s` 不再是 SCOSC。影响 ICH / DCH / IL / DL / 滚动 / CR / 换行。neovim 探测到后用它加速局部滚动 [`tui.c:575-605`]；tmux 有 `margins` 特性 | ✅ ✅ ✅ ❌ ✅ ❌ ❌ |
| 鼠标 SGR-Pixels | `?1016` | §5 | ✅ ✅ ❌ ✅ ✅ ✅ ❌ |
| alternate scroll | `?1007` | alt 屏中滚轮转成方向键。Codex 会主动开启 [`tui.rs` EnableAlternateScroll] | ✅（默认开）? ✅ ? ✅ ? ✅ |
| G2 / G3、SS2 / SS3、LS2 / LS3 | `ESC * x` `ESC + x` `ESC N/O` `ESC n/o` | 解析并维护状态即可；只需要映射 ASCII 和 DEC 线框 | ✅ ◐ ✅ ◐ ✅ ? ? |

### 2.3 Tier 2：可选 / 以后

| 功能 | 序列 | 评估 |
|---|---|---|
| kitty 图形协议 | `ESC _ G k=v,… ; base64 ESC \` | 规模大：传输方式 d / f / t / s（**f / t / s 涉及本地文件和共享内存，有安全约束**，见 [kitty-gfx §The transmission medium]）、分块传输、放置、Unicode 占位符 U+10EEEE、动画、存储配额。支持方：kitty、Ghostty、WezTerm、iTerm2、Konsole（WT ❌，Alacritty ❌）。fzf、neovim、yazi 用它预览图片。**建议**：v1 只做 Tier 1 的"可识别并安全跳过 APC"，并对查询 `a=q` **不应答**（应用会据此判断不支持）[kitty-gfx §Querying support] |
| Sixel | `DCS Pa;Pb;Ph q … ST` | 支持方：WezTerm、WT、iTerm2、xterm、foot、contour；不支持：Ghostty、kitty、Alacritty（ucs-detect 的 sixel 字段）。实现量中等（调色板加位图），但与 reflow、滚动交互复杂。**建议放在 v2**。DA1 中**未实现前不得声明 `4`** |
| iTerm2 内联图像 | `OSC 1337 ; File=… ST` | 与 kitty 图形二选一实现即可 |
| OSC 1337 其他 | `SetUserVar` `CurrentDir` `SetMark` `ClearScrollback` `CursorShape` `RemoteHost` `ReportCellSize` | 建议支持 `CurrentDir`（同 OSC 7）和 `SetMark`（VS Code 也认 [vscode-si]）。其余忽略。**拒绝** `Copy=`、`CopyToClipboard`（与 OSC 52 走同一套权限）、`RequestUpload`、`SetBackgroundImageFile`、`OpenURL`、`Custom` |
| 2033 可见性报告 | `CSI ? 2033 h` → `CSI ? 999 ; 1|2 n`；查询 `CSI ? 998 n` | [2033] 成本低，kitty 和 Ghostty 已支持。有利于后台标签页降低 TUI CPU 占用，可以早做 |
| 5522 kitty 剪贴板 / 粘贴事件 | `OSC 5522` | 多 MIME 剪贴板，带密码授权 [kitty-clip]。v2 再做 |
| OSC 3008、7501 | 层级上下文、程序状态 | Claude Code 已在发送 OSC 7501（changelog 2.1.x："Added Program Status Protocol (OSC 7501) support"）。**v1 静默忽略即可**；如果 UI 想显示 Agent 运行状态，可以作为 Tier 2 的增值功能 |
| 反向换行 | `?45` `?1045` | vttest 会测；bash / readline 不依赖 |
| 矩形区域操作 | DECFRA `$x`、DECERA `$z`、DECCRA `$v`、DECSERA、DECCARA、DECRARA、DECIC / DECDC | WT 和 iTerm2 支持。**DA1 声明 `28` 之后，ConPTY 会改用 DECCRA 做滚动** [WT `getset.cpp:1044`]，所以声明与实现必须一致。esctest 依赖 DECRQCRA（测试模式专用，见 §9） |
| DECDHL / DECDWL | `ESC # 3/4/5/6` | vttest 有专门测试；实际很少用。v2 |
| DECALN | `ESC # 8` | 实现简单（用 E 填满全屏、重置边距、光标归位 [VT510 DECALN]），**vttest / esctest 的基础**。建议放入 Tier 1 |
| XTSAVE / XTRESTORE | `CSI ? Pm s` / `CSI ? Pm r` | 低成本，tmux 和 vim 偶尔使用。kitty 扩展了不带参数的形式 [kitty-misc] |
| XTPUSHSGR / XTPOPSGR | `CSI # {` / `CSI # }` | 低成本，可选 |
| DECSCA / DECSED / DECSEL | `CSI " q`、`CSI ? J`、`CSI ? K` | vttest 会测。低成本 |
| 80 / 132 列 DECCOLM | `?3`（需要 `?40` 允许） | **不改窗口大小**。按 xterm 的 `?40` 默认关闭语义忽略即可，但 DECRPM 要如实应答 |
| 鼠标 1005 / 1015 / 1001、DEC Locator | | §5：解析后忽略，DECRPM 应答 0 或 4 |
| win32-input-mode 9001 | `CSI ? 9001 h/l` | **在 Windows + ConPTY 下应升为 Tier 1**（§4.5）。非 Windows 不需要 |
| VT52 模式 | `?2` 复位 | 不支持；DECRPM 对 2 应答 4（永久复位）[推断] |

### 2.4 OSC 133 / 633 详细规格

**FinalTerm 原始定义** [FinalTerm `TerminalStream.vala:542-549`]：`133;A` 提示开始，`B` 命令输入开始，`C` 命令执行（输出开始），`D` 命令结束，`E` / `F` 文本菜单，`G` 进度，`H` 执行命令。**E–H 现代终端都不实现，TCode 必须忽略**（尤其 `H` 是安全隐患）。

**iTerm2 的约定** [iterm2-esc §Shell Integration/FinalTerm]：
- `D;Ps` 的 Ps 是 0–255 的退出码。
- 如果 `B` 之后、`C` 之前就收到 `D`，表示命令被取消，当前提示状态全部丢弃。
- 光标位置早于 `B` 时，命令视为空。

**Per Bothner 扩展** [semprompt]，Ghostty 实现在 `osc/parsers/semantic_prompt.zig`，WezTerm 在 `osc.rs:706-747`：

| 子命令 | 含义 | 选项 |
|---|---|---|
| `L` | fresh-line：光标不在行首就执行 `\r\n` | |
| `A` | fresh-line，然后开始新命令，进入提示状态 | `aid=`、`cl=line|m|v|w`；kitty 扩展：`k=s`、`redraw=0`、`special_key=1`、`click_events=1|2` [kitty-si] |
| `N` | 同 A，但先结束同 aid 的未闭合命令 | `aid`、`cl` |
| `P` | 显式开始提示 | `k=i|r|c|s`（初始 / 右侧 / 续行 / 次级） |
| `B` / `I` | 提示结束，用户输入开始；`I` 表示输入到行尾为止 | |
| `C` | 输入结束，输出开始 | kitty 扩展：`cmdline=`（%q 编码）、`cmdline_url=`（百分号编码） |
| `D` | 命令结束 | `;exitcode`、`err=`、`aid=` |

选项格式是 `;key=value`，**未知 key 必须忽略** [semprompt §General options syntax]。

**VS Code OSC 633** [vscode-si]：`A B C D` 与 133 相同；`E ; <cmdline> [; nonce]` 中命令行用 `\xAB` 转义，**分号和 0x20 以下字符必须转义**；`P ; Cwd=…`、`IsWindows=`、`HasRichCommandDetection=`。只有 nonce 与本会话一致时，才把 `E` 的内容当作可信命令行。WT 已实现 633（`OscActionCodes::VsCodeAction`）。

**TCode 规格**：
- 解析 133 的 A / B / C / D / L / N / P / I，以及 633 的 A / B / C / D / E / P(Cwd)。在行模型上打语义标记，用于命令块、跳转到上一个提示、退出码徽标、"复制输出"等 UI。
- 光标位于输入区内时，**调整窗口大小时不要 reflow 该区域**（semprompt §Window re-size 的建议）。
- `cl` 点击移动光标属于 Tier 2；实现时必须先检查 shell 是否声明了 `cl=`，未声明不得注入方向键。
- 命令行内容（`E`、`cmdline=`）**只用于显示**，不得自动执行，也不得写入 shell 历史。

### 2.5 OSC 9;4 进度与 OSC 9 通知

- 格式 `OSC 9 ; 4 ; st ; pr ST` [conemu；wt-progress；iterm2-esc §Progress Bar]。
  - st = 0：清除。
  - st = 1：正常，pr 为 0–100，必填。
  - st = 2：错误，pr 可选。
  - st = 3：不确定进度，忽略 pr。
  - st = 4：暂停或警告，pr 可选；iTerm2 规定省略 pr 时保留原百分比。
- 还有简写 `OSC 9 ; 4 ST`，等于清除 [iterm2-esc]。
- `OSC 9 ; <text>` 是 iTerm2 的桌面通知；`OSC 9 ; 1..12` 是 ConEmu 的其他子命令。**解析顺序**：先看第一个字段是不是 ConEmu 子命令号（1–12）。Ghostty `osc9.zig` 就是这样先判断 ConEmu 的。kitty 曾因 `4;` 被当作通知而改为丢弃（changelog 0.4x："Discard OSC 9 notifications that start with `4;`"）。Claude Code 也修过同类问题（"stray '4;0;' desktop notification … in Kitty"）。
- **ConEmu 的 9;1（sleep）、9;2（MessageBox）、9;5、9;6（GuiMacro）、9;7（运行进程）、9;8（输出环境变量）一律忽略**。9;9（cwd）当作 OSC 7 的别名，并按 §8.5 视为不可信（WT 的 CVE-2022-44702 就出在 OSC 9;9 [dgl-2023]）。
- 桌面通知（OSC 9 文本、OSC 777;notify、OSC 99）：**默认只在窗口失焦时显示**，并限制频率 [推断]。

---

## 3. 查询应答规格

总原则：
- (1) **只声明真正实现了的功能**。tmux、ConPTY、Emacs、vim 都会依据应答改变行为（§7.3）。
- (2) **应答内容只能由终端自身状态格式化生成**，绝不回显攻击者可控的文本 [dgl-2023]。
- (3) 应答通过单一写队列写回 PTY，保证顺序。DA1 往往被用作"栅栏"：先发功能查询，再发 DA1；只收到 DA1 应答就说明不支持 [kitty-kbd §Detection；kitty-gfx §Querying]。所以**同一批输入中的查询必须按顺序应答**。

### 3.1 各终端实际应答（对照）

| 终端 | DA1 | DA2 | XTVERSION |
|---|---|---|---|
| xterm #411 | `?63;1;2;3;4;6;9;15;17;22;28c` | `>41;411;0c`（推断自 ncurses `xterm-p370` 的 `rv=\E\\[>41;[1-6][0-9][0-9];0c`） | `XTerm(411)` [ucs-detect xterm.yaml] |
| Ghostty | `?62;22;52c`（OSC 52 写被拒时为 `?62;22c`）[`stream_handler.zig:825-848`] | `>1;10;0c` | `ghostty 1.3.1` [`stream_handler.zig:975-990`] |
| WezTerm | `?65;4;6;18;22;52c` | `>1;277;0c`（注释写明是为了让 vim 选 SGR 鼠标）[`mod.rs:1407-1432`] | `WezTerm <ver>` |
| Windows Terminal | `?61;4;6;7;14;21;22;23;24;28;32;42;52c` [`adaptDispatch.cpp:1435-1462`] | `>0;10;1c` | 无 |
| xterm.js | `?1;2c` | `>0;276;0c` [`InputHandler.ts:1707-1752`] | `xterm.js(<ver>)` |
| iTerm2 | `?64;1;2;4;6;17;18;21;22;52c` [ucs-detect] | `>Pp;2500;0c`（xtermVersion 默认 2500 [`iTermAdvancedSettingsModel.m:453`]） | `iTerm2 3.7.3` |
| kitty | `?62;52;c` [`window.py:664-669`] | `>1;4000+major;minor c` [`setup.py:759-761`] | `kitty(0.49.2)` |
| Alacritty | `?6c` | `>0;<ver>;1c` [`term/mod.rs:1257-1272`] | 无 |

### 3.2 DA1：`CSI c` / `CSI 0 c`

**建议应答**：`CSI ? 62 ; 22 ; 52 c`。只有用户允许 OSC 52 写入时才带 `52`。

理由：
- 62 表示"VT220 级"，与 Ghostty、kitty 一致。不声明 VT420 / 525 级别，因为 tmux 会把 DA1 中的 `21` 理解为 `margins`、`28` 理解为 `rectfill`、`4` 理解为 sixel、`52` 理解为 clipboard [tmux `tty-keys.c:1406-1470`]；ConPTY 会依据 `28` 改用 DECCRA [WT `getset.cpp:1044`]。
- 22：ANSI 彩色。
- 52：Emacs 看到后会启用 OSC 52 写入 [`xterm.el` `xterm--primary-da-handler`]。
- 实现 DECSLRM 之后，可以加 `21`？**不要加**：DEC 定义 21 是"水平滚动"（SL / SR），tmux 把它当作 margins 是 tmux 的启发式。实现 SL / SR 和 DECSLRM 后再评估。
- 实现矩形操作之后才能加 `28`；实现 sixel 之后才能加 `4`。

### 3.3 DA2：`CSI > c` / `CSI > 0 c`

**建议应答**：`CSI > 0 ; 2500 ; 0 c`。这是 [推断]，依据 vim 与 Emacs 的源码，需要用户确认（见第 2 个决策点的备注）。

逐项理由：
- vim [`term.c:5176-5390`]：
  - 版本 ≥ 141 时会用 XTGETTCAP 请求按键码（TCode 支持）；
  - ≥ 277 时设置 `ttymouse=sgr`（窗口超过 223 列时必须）；
  - ≥ 279 时认为可以查询光标样式（我们支持 DECRQSS `SP q`）；
  - ≥ 2500 时启用下划线颜色 `t_8u`。
- Emacs [`xterm.el:793-850`]：只有首参数为 1 或 65 且版本 > 2000 时才会把版本降到 200，**首参数用 0 正好避开**。于是 ≥ 242 时查询 OSC 10 / 11 背景色，≥ 216 时启用 modifyOtherKeys，≥ 203 时启用 OSC 52 写入。
- iTerm2 的注释正是这样分析的，并选择了 2500 [`VT100Output.m:1131-1170`]。
- tmux 只根据首参数 'M' / 'T' / 'U' 识别 mintty / tmux / urxvt，不受影响。
- 备选：`>1;277;0c`（WezTerm 的保守选择）。代价是 vim 的下划线颜色和光标样式探测被关闭。

### 3.4 DA3：`CSI = c`

应答 `DCS ! | 00000000 ST`（与 xterm、Ghostty、WezTerm 相同）。
- 不用它做身份标识。VTE 用 `~VTE`，iTerm2 用 `iTrm` 的十六进制 [terminal-wg issue #7；`VT100Output.m:1177`]，但没有主流程序依赖这个应答。

### 3.5 XTVERSION：`CSI > q` / `CSI > 0 q`

- 应答 `DCS > | TCode <semver> ST`，例如 `ESC P > | TCode 0.9.0 ESC \`。参数不为 0 时不应答 [xterm.js `InputHandler.ts:1772-1777`]。
- 格式参照 iTerm2 和 Ghostty：名称 + 空格 + 版本。只用 ASCII 可打印字符。
- 理由：这是**最可靠的终端识别方式，通过 SSH 也能用**（"XTVERSION is the most reliable, supported by 21 terminals" [quast-2026]）。tmux 按前缀匹配一张已知终端表；不认识的名字不会出错，只是拿不到默认特性，需要依赖 DA1 或 `terminal-features` [tmux `tty-features.c:593-730`]。

### 3.6 XTGETTCAP：`DCS + q <hex>[;<hex>…] ST`

- 每个名字单独应答一条 `DCS 1 + r <hexname>=<hexvalue> ST`。无值的布尔能力写成 `DCS 1 + r <hexname> ST`（kitty [`terminfo.py:529-537`]）。未知名字应答 `DCS 0 + r <hexname> ST`。
- xterm 原文说"An invalid name … ends processing of the list"[ctlseqs §DCS + q]，kitty 和 WezTerm 则对每个名字逐一应答。**TCode 逐一应答**，大多数客户端（neovim `vim.tty.query`）都能处理。
- 能力表（编译期常量，不读系统 terminfo）：
  - `TN` / `name`：**与 TERM 一致**，即 `xterm-256color`（§7）。
  - `Co` / `colors`：`256`。
  - `RGB`：`8`（Ghostty）。
  - `Tc`：布尔。
  - `Su`、`Smulx=\E[4:%p1%dm`、`Setulc=\E[58:2::%p1%{65536}%/%d:%p1%{256}%/%{255}%&%d:%p1%{255}%&%d%;m`、`Ss=\E[%p1%d q`、`Se=\E[0 q`、`Ms=\E]52;%p1%s;%p2%s\007`、`Sync=\E[?2026%?%p1%{1}%-%tl%eh%;`、`BE` / `BD` / `PS` / `PE`（括号粘贴）、`fe` / `fd`、`XM`、`xm`、`E3`。
  - 所有按键能力（`kcuu1` 等）必须**与 §4 的实际编码一致**。
- 依据：
  - neovim 用 XTGETTCAP 查询 `Tc`、`RGB`、`setrgbf`、`setrgbb` 来决定 `termguicolors` [`defaults.lua:1008-1035`]；
  - vim 在 DA2 版本 ≥ 141 时请求按键码；
  - ucs-detect 用 `Ms` 判断 OSC 52（Ghostty 实测数据）。
- 带参数的字符串按 terminfo 源码形式返回（`%p1%d` 原样），无参数的字符串返回转义后的字节。这是 Ghostty 的做法 [`terminfo/Source.zig:95-115`]。

### 3.7 DSR / CPR

| 请求 | 应答 | 说明 |
|---|---|---|
| `CSI 5 n` | `CSI 0 n` | |
| `CSI 6 n` | `CSI r ; c R` | 1 起算。**DECOM 置位时相对滚动区** [ghostty `cursorReportPos`]。处于 pending-wrap 时报告最后一列（光标实际所在位置）[推断：xterm 行为]。ucs-detect 正是靠 CPR 测量宽度，所以宽度策略必须与 CPR 一致 |
| `CSI ? 6 n`（DECXCPR） | `CSI ? r ; c ; 1 R` | xterm 版本带页号 1 [ctlseqs DSR DEC-specific] |
| `CSI ? 15 n` / `? 25 n` / `? 26 n` / `? 55 n` / `? 56 n` | `? 13 n`（无打印机）/ `? 20 n` / `? 27;1;0;0 n` / `? 53 n` / `? 57;0 n` | 低优先级 |
| `CSI ? 996 n` | `CSI ? 997 ; 1|2 n` | 明暗主题 [2031] |
| `CSI ? 998 n` | `CSI ? 999 ; 1|2 n` | 可见性 [2033]（Tier 2） |

### 3.8 DECRPM：`CSI [?]Ps $ p`

**模式状态表**：

| 应答 Pm | 模式 |
|---|---|
| 1 / 2（可切换） | ANSI 模式 4、20；DEC 私有模式 1、6、7、12、25、45、47、66、67、69、1000、1002、1003、1004、1006、1007、1016、1047、1048、1049、2004、2026、2027、2031、2048，以及 Windows 下的 9001 |
| 4（永久复位） | 2（VT52）、3（DECCOLM，不改尺寸）、1001、1005、1015、80（sixel，未实现前） |
| 3（永久置位） | 无 |
| 0（不认识） | 其他全部 |

ANSI 模式 2（KAM）、12（SRM）按 xterm 应答：SRM 永久置位 3（Ghostty 默认 true [`modes.zig:364`]）。

### 3.9 DECRQSS：`DCS $ q Pt ST`

| Pt | 应答示例 |
|---|---|
| `m` | `DCS 1 $ r 0;1;38:2::255:0:0 m ST`（用冒号写法输出当前 SGR）。neovim 用"先设 SGR 再用 DECRQSS 读回"的办法探测真彩色，**能识别 `48:2:` 前缀，能接受可选的空颜色空间** [`defaults.lua:1037-1085`]。所以**颜色应答必须用 `38:2::r:g:b` 或 `48:2::r:g:b` 形式** |
| `r` | `DCS 1 $ r 1;24 r ST` |
| `s` | `DCS 1 $ r 1;80 s ST`（仅在 DECLRMM 置位时） |
| `SP q` | `DCS 1 $ r 2 SP q ST` |
| `" q` | `DCS 1 $ r 0 " q ST` |
| 其他 | `DCS 0 $ r ST` |

### 3.10 OSC 查询的应答

- `OSC 4 ; n ; ?`、`OSC 10/11/12 ; ?`：应答 `OSC 4 ; n ; rgb:RRRR/GGGG/BBBB` 或 `OSC 1x ; rgb:RRRR/GGGG/BBBB`，用 16 位十六进制（Ghostty 的 `osc-color-report-format` 默认 16-bit）。**结束符沿用请求的结束符**。
- `OSC 52 ; c ; ?`：按 §8.1。
- `OSC 1337 ; ReportCellSize`、`ReportVariable`：不应答（Tier 2）。

### 3.11 XTWINOPS 报告与 2048

| 请求 | 应答 |
|---|---|
| `CSI 14 t` | `CSI 4 ; 文本区高 px ; 宽 px t` |
| `CSI 16 t` | `CSI 6 ; 格高 ; 格宽 t` |
| `CSI 18 t` | `CSI 8 ; rows ; cols t` |
| `CSI 19 t` | 同 18（屏幕即窗口） |

- **`CSI 20 t` 和 `CSI 21 t`（图标名、标题）默认不应答**（§8.2）。
- 像素值用**物理像素**（DPI 缩放后），与 kitty、Ghostty 一致 [推断]。
- `CSI 11 t` 应答 `CSI 1 t`。`CSI 13 t` 默认不应答（属于信息泄露类）。

---

## 4. 键盘编码规格

### 4.1 Legacy（xterm PC 风格，默认）

**修饰键参数** [ctlseqs §PC-Style Function Keys]：值 = 1 + 位掩码，Shift = 1，Alt = 2，Ctrl = 4，Meta = 8。

| 键 | 无修饰（普通） | 无修饰（应用模式 DECCKM / DECKPAM） | 带修饰 |
|---|---|---|---|
| ↑ ↓ → ← | `CSI A/B/C/D` | `SS3 A/B/C/D` | `CSI 1;m A`（带修饰时**一律用 CSI**） |
| Home / End | `CSI H` / `CSI F` | `SS3 H` / `SS3 F` | `CSI 1;m H/F` |
| Insert / Delete / PgUp / PgDn | `CSI 2~` `CSI 3~` `CSI 5~` `CSI 6~` | 同左 | `CSI n;m ~` |
| F1–F4 | `SS3 P/Q/R/S` | 同左 | `CSI 1;m P/Q/R/S`（xterm：带修饰时 SS3 改为 CSI）。**F3 带修饰的 `CSI 1;m R` 与 CPR 冲突**，ctlseqs 在 DSR 一节中专门提到这一点。kitty 协议的 F3 改用 `CSI 13~` [kitty-kbd] |
| F5–F12 | `CSI 15~ 17~ 18~ 19~ 20~ 21~ 23~ 24~` | 同左 | `CSI n;m ~` |
| Shift+Tab | `CSI Z` | | |
| Enter | `CR` | 小键盘 Enter 在 DECKPAM 下发 `SS3 M` | Alt+Enter 发 `ESC CR` |
| 小键盘 | 数字 / 运算符字符 | DECKPAM 下发 `SS3 j..y`（见 [ctlseqs VT220-Style Function Keys]） | |
| Esc | `ESC` | | |

**Backspace / Delete**：
- Backspace 默认发 **DEL 0x7F**。Ctrl+Backspace 发 **BS 0x08**，Alt+Backspace 发 `ESC 7F`。
  - 依据：kitty 表 [kitty-kbd §Legacy functional keys：Backspace 0x7f，Ctrl 0x8]；WT 的 Backspace 在 DECBKM 复位时发 DEL、Ctrl 反转 [`terminalInput.cpp:909-921`]；Ghostty 的 terminfo 为 `kbs=^?`。
  - 支持 `?67` DECBKM（置位时 Backspace 发 BS）。
- **注意系统差异**：[本机] macOS 自带的 `xterm-256color` 条目是 `kbs=^H`（ncurses 2015 版），而终端实际发 0x7F。readline 和 zsh 以 `stty erase`（通常是 `^?`）为准，所以一般没问题。只依赖 terminfo `kbs` 的程序可能出错。这是所有使用 xterm-256color 的终端共同的已知问题（ncurses 的 `xterm+kbs` 注释："people who cannot agree on what the backspace key should send"）。**TCode 跟随主流，发 0x7F**。
- Delete 键发 `CSI 3~`（`?1037` 控制是否改发 DEL，默认不改）。

**Ctrl 映射**：按 kitty 给出的表 [kitty-kbd §Legacy ctrl mapping]：
- `Ctrl+a..z` → 1..26；`Ctrl+@`、`Ctrl+Space`、`Ctrl+2` → 0；`Ctrl+[`、`Ctrl+3` → 27；`Ctrl+\`、`Ctrl+4` → 28；`Ctrl+]`、`Ctrl+5` → 29；`Ctrl+^`、`Ctrl+6`、`Ctrl+~` → 30；`Ctrl+_`、`Ctrl+/`、`Ctrl+7` → 31；`Ctrl+8`、`Ctrl+?` → 127。
- 表外的 ASCII 键不受 Ctrl 影响。

**Alt / Meta**：
- **Alt 的默认行为是加 ESC 前缀**（对应 xterm 的 `?1036` metaSendsEscape / `?1039` altSendsEscape，二者默认都置位）。**不使用"置第 8 位"的方式**（`?1034` 默认关），因为在 UTF-8 下会产生非法字节。
- macOS 上 Option 默认用于输入字符（例如 Option+b 得到 ∫）。提供 `option_as_alt = left|right|both|none` 配置，默认值可以像 Ghostty 一样按键盘布局决定：美式布局默认开 [ghostty `Config.zig` `macos-option-as-alt`]。
- 组合后不产生可打印字符的情况（如 Option+Ctrl+a）一律按 Alt 处理。

**AltGr（Windows）**：Windows 上 AltGr 表现为 LeftCtrl + RightAlt。WT 的判断办法：带有效图形码点时视为 AltGr；否则在 50 ms 窗口内用时间差区分"真 Ctrl" [`terminalInput.cpp:364-395`]。TCode 在 Avalonia 层得到文本输入事件时，**优先使用 TextInput 的文本**；只有没有文本输入时才走按键编码。这样 AltGr 输入的字符不会被误判为 Ctrl+Alt 组合。

**IME**：组字（preedit）期间不发送任何字节；提交（commit）时把文本以 UTF-8 发出。如果处于括号粘贴模式，**提交文本不加括号粘贴包裹**，它不是粘贴 [推断：Ghostty 的 `paste.Source.text` 也与剪贴板粘贴分开处理]。

### 4.2 modifyOtherKeys（xterm）

[ctlseqs §Alt and Meta Keys；modkeys]

| 设置 | 效果 |
|---|---|
| `CSI > 4 ; 0 m` | 关闭 |
| `CSI > 4 ; 1 m` | Shift / Ctrl 的常规组合照旧；其他修饰组合（Alt 等）按 `CSI 27 ; m ; code ~` 编码，例如 Alt+Tab 为 `CSI 27;3;9~` |
| `CSI > 4 ; 2 m` | 所有带修饰的键都编码，例如 Shift+Tab 为 `CSI 27;2;9~`，Ctrl+Shift+a 为 `CSI 27;6;65~`… |

- 应答格式默认用 xterm 的 `CSI 27;m;k ~`。`formatOtherKeys=1`（`CSI > 4 ; 1 f`）时改为 `CSI k ; m u`。
- `CSI > 4 m`（省略值）恢复默认，`CSI > 4 n` 禁用。查询 `CSI ? 4 m` 应答 `CSI > 4 ; v m`。
- **必须实现**：
  - vim 的默认 keyprotocol 对 `xterm*` 使用 `mok2` [vim `optiondefs.h:1584`]；
  - neovim 在 kitty 协议探测失败时发 `CSI >4;2m` [`tui.c:316-330`]；
  - Codex 在 tmux（csi-u 格式）下会发 `CSI >4;2m` [`keyboard_modes.rs:270-282`]；
  - Emacs 在 DA2 版本 ≥ 216 时初始化 modifyOtherKeys。
- 与 kitty 协议的优先级：**kitty 标志不为 0 时使用 kitty 编码**，否则 modifyOtherKeys 生效，否则 legacy。win32-input-mode 优先级最高（§4.5）。这与 WT（`terminalInput.cpp` 中 kitty 优先于 win32）的顺序有所不同；在 Windows ConPTY 场景下按 §4.5 处理。

### 4.3 应用光标键与小键盘模式

- 只有在**无修饰**时，DECCKM / DECKPAM 才改变编码 [kitty-kbd：在 cursor key mode 下 "used only when no modifiers are present"]。
- terminfo 中 `smkx=\E[?1h\E=`、`rmkx=\E[?1l\E>`，所以 ncurses 程序会同时开启两者，并且期望 `kcuu1=\EOA`、`khome=\EOH`、`kend=\EOF`。
- NumLock 开时小键盘数字发数字字符（`?1035` 的语义）。

### 4.4 kitty 键盘协议

[kitty-kbd 全文]

**控制序列**：
- 设置：`CSI = flags ; mode u`。mode 1 = 整体设置（默认），2 = 按位置位，3 = 按位清除。
- 查询：`CSI ? u` → `CSI ? flags u`。
- 入栈：`CSI > flags u`（flags 省略时为 0）。出栈：`CSI < n u`（n 默认 1）。
- **主屏和 alt 屏各有一个独立的栈**。栈上限 **8**（Ghostty [`kitty/key.zig:9`]、kitty [`screen.h:173`]、WT [`terminalInput.hpp:187`] 都是 8）。栈满时淘汰最旧的一项；栈出空时 flags 归零。
- RIS 清空两个栈。

**flags**：

| 位 | 名称 | 效果 |
|---|---|---|
| 1 | disambiguate | Esc、Alt+键、Ctrl+键、Ctrl+Alt+键、Shift+Alt+键都改用 `CSI code;mods u`；小键盘键单独编码（PUA 57399…）。**Enter / Tab / Backspace 保持 legacy 编码**，方便程序崩溃后用户还能在 shell 里敲 `reset` |
| 2 | report event types | 修饰字段带 `:1` 按下、`:2` 重复、`:3` 释放。Enter / Tab / Backspace 不报释放，除非同时开了 8 |
| 4 | report alternate keys | `code:shifted:base-layout`。base-layout 键是 PC-101 布局下的键（例如俄语布局 Ctrl+С 的 base 为 `c`） |
| 8 | report all keys as escapes | 所有按键（含纯文本、Enter、Tab、Backspace 和修饰键本身）都编码成转义序列 |
| 16 | report associated text | 第 3 个字段带文本码点（冒号分隔），只在与 8 同时开启时有意义 |

**编码细节**：
- key code 取**未按 Shift 时的码点**（如 a 是 97，不是 65）。
- 修饰值 = 1 + 位掩码：shift 1、alt 2、ctrl 4、super 8、hyper 16、meta 32、caps_lock 64、num_lock 128。
- 功能键使用规范中的表：方向键 `CSI 1;m A`；F1 `CSI 1;m P` 或 `CSI 11~`；F3 只能用 `CSI 13~`；F13 及以后、媒体键、左右修饰键用 PUA 码 57344–63743。
- 修饰键自身的事件（如 LEFT_SHIFT 57441）只在 flag 8 下报告。按下修饰键时，该修饰键的位要包含在本次事件里；释放时要清除。
- **探测**：应用发 `CSI ? u` 加 `CSI c`。TCode 必须按顺序先应答 `CSI ? flags u`，再应答 DA1。

**各程序的实际使用**：
- neovim：`CSI >3u`（1 | 2）[`tui.c:316-325`]。
- Codex：基础为 1 | 4；在 Ghostty / iTerm2 上不加 2，原因是"can leak shortcut release events"[`keyboard_modes.rs:242-259`]；入栈用 `CSI >5u` / `CSI >7u`。
- Claude Code：靠"回应 kitty 键盘查询"获得 Shift+Enter 等能力（changelog："terminals that answer the kitty keyboard query (such as foot and Alacritty 0.16+) now get Shift+Enter"）。
- **教训**：开启 flag 2 时，**TCode 自身消费的快捷键（例如 Cmd+C 复制）不得把释放事件发给应用**。按下事件被终端拦截时，对应的释放事件也要一并拦截 [推断，源自 Codex 的注释]。

### 4.5 Windows：ConPTY 与 win32-input-mode

**ConPTY 启动时会向宿主终端发送** `\x1b[c`（DA1）、`\x1b[?1004h`、`\x1b[?9001h`，退出时发 `?1004l`、`?9001l` [WT `host/VtIo.cpp:197-204,251-260`]。ConPTY 会解析我们的 DA1 应答，并据此决定是否使用 DECCRA 等功能 [`getset.cpp:1044`；`InputStateMachineEngine.hpp:52-76`]。

**win32-input-mode（`?9001`）编码**：`CSI Vk ; Sc ; Uc ; Kd ; Cs ; Rc _` [wt-4999；`terminalInput.cpp:541-563`]：

| 字段 | 含义 | 省略时默认 |
|---|---|---|
| Vk | 虚拟键码 | 0 |
| Sc | 扫描码 | 0 |
| Uc | UTF-16 码元的十进制值 | 0 |
| Kd | 按下 1 / 释放 0 | 0 |
| Cs | `dwControlKeyState` 位掩码 | 0 |
| Rc | 重复次数 | 1 |

- 一次按键要发送修饰键按下、主键按下、主键释放、修饰键释放**完整的 4 个或更多事件**。
- 依据：WezTerm 在 Windows 上默认 `allow_win32_input_mode = true`，原因是"best compatibility with win32 console applications such as Far Manager" [wezterm `docs/config/key-encoding.md`]。
- **ConPTY 内部当前强制关闭 KKP 转换**（`ForceDisableKittyKeyboardProtocol(true)`，TODO GH#19847 [`VtIo.cpp:125-131`]），并且在 ConPTY 下忽略 `CSI = u` 等序列 [`adaptDispatch.cpp:2079-2103`]。
- 所以在 Windows 下，**宿主终端只有支持 9001，原生控制台程序（PowerShell / PSReadLine、cmd、Far）才能收到 Ctrl+Space、Shift+Enter、修饰键单独按下等事件** [wt-4999 §Abstract 列出的问题]。
- 要注意的是，WSL / ssh 内的 Linux TUI 发出的 kitty 键盘请求，经过 ConPTY 后不会传到我们这里。ConPTY 会把 win32 事件重新编码成该程序请求的 VT 格式（spec 中 "WT → conpty → wsl" 的场景）。
- **TCode 规格（Windows）**：
  - 支持 `?9001`。置位时**所有键盘输入**都按 win32 格式发送（包括 IME 提交的字符：Vk = 0 或 VK_PACKET，Uc = 码元）。
  - Avalonia 的 KeyEventArgs 要还原出 Vk、扫描码、`dwControlKeyState`（含 NUMLOCK_ON、ENHANCED_KEY 等位）。这需要 Win32 原生消息信息，属于 AOT 安全的 P/Invoke 范围，由平台层负责（参见 05 号报告）。
  - 焦点事件（1004）照常发送。
- 非 Windows 平台：DECRPM 对 9001 应答 0（不认识）。

---

## 5. 鼠标编码规格

[ctlseqs §Mouse Tracking 全文]

**跟踪模式**（同一时间只有一个生效，后设置的覆盖先设置的）：

| 模式 | 报告内容 |
|---|---|
| `?9` X10 | 只报告按下；不带修饰键 |
| `?1000` | 按下和释放（滚轮没有释放事件） |
| `?1001` 高亮跟踪 | **不支持**，需要应用配合，否则 xterm 会挂起。DECRPM 应答 4 |
| `?1002` | 再加上**按住按钮时的移动**，且只在进入新格子时报告 |
| `?1003` | 所有移动 |

**编码模式**（与跟踪模式正交）：

| 模式 | 格式 | 结论 |
|---|---|---|
| 默认 X10 | `CSI M Cb Cx Cy`，三个值都 +32 编码成单字节 | 坐标上限 223（255 − 32）。超出时**不发送该事件**（xterm 不发）[推断] |
| `?1005` UTF-8 | 把 Cx、Cy（及 Cb）按 UTF-8 编码，上限 2015 | 会混淆非 UTF-8 应用。xterm.js 已移除。**TCode：解析后忽略，DECRPM 应答 4** |
| `?1006` SGR | `CSI < Cb ; Px ; Py M`（按下 / 移动）/ `m`（释放） | Cb 不加 32；释放时保留按钮号。**主力编码** |
| `?1015` urxvt | `CSI Cb ; Px ; Py M`，Cb 加 32 | 与 DL（`CSI M`）冲突，ctlseqs 明确不推荐。xterm.js 已移除。TCode 忽略 |
| `?1016` SGR-Pixels | 同 1006，但 Px、Py 为像素坐标 | 1 起算还是 0 起算：xterm 以文本区左上角为 (1,1) [推断]。TCode 跟随 Ghostty / kitty 的实现，测试时用 kitty 的 `kitten show-key`、ucs-detect 校准 |

**Cb 位定义**：
- 低 2 位：0 左键，1 中键，2 右键，3 释放（只在非 SGR 编码中使用）。
- +4 Shift，+8 Meta（Alt），+16 Ctrl。
- +32 移动。
- +64：滚轮。按钮 4 / 5（上 / 下滚）→ 64 / 65；按钮 6 / 7（左 / 右）→ 66 / 67。
- +128：按钮 8–11。
- 无按钮的移动（1003）：Cb = 35，即 32 + 3。

**交互规则**：
- **Shift 覆盖**：鼠标跟踪开启时，按住 Shift 拖动执行终端自己的选择，不报告给应用（xterm 的 `XTSHIFTESCAPE` 默认 0 即"shift-key overrides mouse protocol"[ctlseqs CSI > Ps s]）。macOS 上也可以用 Option 作为覆盖键（iTerm2 惯例）[推断]。
- **滚轮**：
  - 跟踪开启时发送滚轮事件；
  - 未开启跟踪、处于 alt 屏且 `?1007` 置位时，转成方向键（按 DECCKM 选 `CSI A` 或 `SS3 A`），每格 3 行 [推断：各终端常用值]；
  - 其他情况滚动 scrollback。
- **触控板高精度滚动**要累积成整格事件后再发送，避免一次发出几十个事件。
- 坐标钳制在网格范围内。窗口外的释放事件按最后位置报告。
- **聚焦点击**：点击用于激活窗口时，该次点击不报告给应用 [推断：各终端的常见做法]。
- **焦点（1004）**：与鼠标模式独立，只在焦点状态真正变化时发送。ConPTY 依赖它。

---

## 6. Unicode 宽度与字形簇策略

### 6.1 规范依据

**UAX #11** [UAX11]：
- East_Asian_Width 有 6 个取值：A / F / H / Na / W / N，最终归为窄或宽。
- **明确说明该属性"is not intended for use by modern terminal emulators without appropriate tailoring"**。
- 歧义宽度字符在无法确定上下文时"should be treated as narrow characters by default"。
- 私用区字符默认为 A；CJK 区段内未分配的码点为 W。
- 带 Emoji_Presentation 的字符为 W；区域指示符例外，归为 N。
- 变体序列可以用来解决歧义宽度（§4.3）。

**UAX #29** [UAX29]：使用扩展字形簇（EG，含 GB9a / GB9b / GB9c）。
- GB9c（Unicode 18 修订）：`\p{InCB=Linker} \p{InCB=Extend}* × \p{InCB=Consonant}`。
- GB11：emoji ZWJ 序列。
- GB12 / 13：区域指示符成对组合。
- 规范说字形簇长度没有上限，"There is no limit on the size of a grapheme cluster"，所以**实现必须自行设上限**（§6.4）。

**Mode 2027** [2027 draft]：
- 置位后终端必须按 UTS #29 划分字形簇，每个字形簇占一个格子（或宽字符的两格）。
- emoji 一律占 2 格；ZWJ 序列显示为一个整体，宽 2。
- VS16 把宽度提升为 2，可能因此触发自动换行；VS15 只改变显示方式，**不改变宽度**。
- 光标移入的格子在写入时整体覆盖。
- DECSTR / RIS 复位此模式。

**wcwidth 规范** [wcwidth-spec v0.9.3]：
- 宽度 0：Default_Ignorable（软连字符 U+00AD 和 U+115F 除外）、Mn、Me、Cf、ZWJ 及其后被消耗的码点、成对区域指示符的第二个、Hangul Jungseong U+1160–11FF。
- 宽度 2：EAW 为 W / F、区域指示符、单独出现的 Fitzpatrick 肤色符、**VS16 跟在窄字符后时 +1**。
- 宽度 1：**VS15 跟在宽字符后时 −1**（规范注明存在争议，见 issue #211）。
- Mc 把字形簇宽度封顶为 2；"Any grapheme cluster width is limited to 2 cells since 0.8.0"。

**行业现状** [quast-2026；ucs-detect data 2026-09]：

| 终端（版本） | 2027 | WIDE | ZWJ | VS16 | VS15 | 语言 |
|---|---|---|---|---|---|---|
| Ghostty 1.3.1 | 置位且可切换 | 100% | 100% | 100% | 100% | 83/85 |
| WezTerm 20260716 | 永久置位 | 95% | 100% | 50% | 0% | 61/85 |
| WT 1.24 | 永久置位 | 98% | 100% | 100% | 0% | 106/118 |
| kitty 0.48.2 | 不认识 | 98% | 100% | 98% | 100% | 88/118 |
| iTerm2 3.7.3 | 永久复位 | 98% | 17% | 100% | 0% | 98/118 |
| xterm.js 6.1-beta（VS Code） | 不认识 | 97% | 1% | 50% | 0% | 98/118 |
| Alacritty | 不认识 | 95% | 1% | 50% | 0% | 63/85 |
| xterm 411 | 不认识 | 95% | 1% | 50% | 0% | 65/85 |

"Even well-meaning terminals who report to support … 2027 have varying interpretations" [ucs-detect README]。

**Unicode TTWG**：TTWG 的结论是"application and terminal have to have the same understanding of how many cells a given piece of text will occupy"，计划制定独立于字体的测量规则，但**截至 2026-10 尚未发布标准** [TTWG L2/25-263；quast-2026]。

### 6.2 与 shell / 程序 wcwidth 不一致的问题

**核心矛盾**：shell 和 readline 用 libc 的 `wcwidth` 逐码点计算宽度，而终端按字形簇显示。二者不一致时，光标会错位，行编辑出现残影。

[本机] macOS libc `wcwidth` 实测结果：

| 码点 | 结果 | 说明 |
|---|---|---|
| U+2764 ❤ | 1 | 后接 VS16 时终端显示为 2 格 |
| U+1F1E6（区域指示符） | 1 | wcwidth-spec 规定为 2 |
| U+1F3FB 肤色符 | 2 | 按字形簇规则应为 0 |
| U+FE0F VS16、U+200D ZWJ | 0 | |
| U+2500 线框、U+00B7（A 类） | 1 | |

结论：同一个 ZWJ 家庭 emoji，shell 认为宽 6–8 格，2027 终端显示为 2 格。

各终端的处理方式：
- Ghostty：`grapheme-width-method = unicode`（默认）或 `legacy`；程序显式开启 2027 时强制使用 `unicode` [`Config.zig:515`]。注释承认默认的 unicode 模式"may result in cursor-desync issues with some programs (such as shells)"。
- fish：提供 `fish_emoji_width`（默认 2）和 `fish_ambiguous_width` 两个变量，让用户与终端对齐 [fish 4.x 文档]。
- iTerm2：`OSC 1337 ; UnicodeVersion=8|9` 用来切换宽度表 [iterm2-esc]。

### 6.3 TCode 建议策略

1. **两套宽度模式**：
   - **兼容模式（默认，2027 复位）**：逐码点计算宽度，组合字符（宽度 0）附着在前一个格子上；**VS16 不改变宽度**，**ZWJ 不合并**（每个 emoji 各占 2 格）；区域指示符各宽 1 或 2，按 wcwidth 表。
   - **字形簇模式（2027 置位）**：完整 UAX #29（EG）划分 + 上面 wcwidth-spec 的规则：VS16 → 2，VS15 → 1，ZWJ 序列 → 2，旗帜 → 2，单个字形簇封顶 2。
   - 依据：主流 shell（bash / readline、zsh）仍逐码点计算；neovim 会**主动探测并开启 2027**（`tui.c:254-258`：模式处于复位状态时会置位它），fish 4 和 Claude Code 自己处理字形簇。默认选兼容模式，能最大限度避免 shell 光标错位；需要的程序可以自己开启。**这是决策点**（见第 2 个决策），备选是像 Ghostty 一样默认使用 unicode。
2. **宽度表随 Unicode 版本更新**。在 NativeAOT 下由源码生成器从 UCD 18.0 的 `EastAsianWidth.txt`、`emoji-data.txt`、`DerivedCoreProperties.txt`、`GraphemeBreakProperty.txt`、`IndicSyllabicCategory`（InCB）生成静态表，**不依赖 .NET 运行时的 `StringInfo`**，因为其 Unicode 版本跟随运行时、不可控 [推断]。
3. **歧义宽度**：默认窄（1），符合 UAX #11 的建议，与 WezTerm、WT 默认一致（`treat_east_asian_ambiguous_width_as_wide = false`；WT `compatibility.ambiguousWidth = Narrow`）。提供"宽"选项，供使用 CJK 遗留软件的用户，并提示需要与 shell / locale 同时调整。私用区默认 1；Nerd Font 图标靠字体溢出显示，宽度仍算 1。
4. **CPR 必须与网格一致**：宽度测量完全以 TCode 的网格为准，ucs-detect 正是靠 CPR 来测的。
5. **DECRPM 2027**：可切换，应答 1 或 2。
6. **渲染与宽度解耦**：字体测得的字形宽度不改变格子宽度。宽度为 1 的格子里放了宽字形时，允许溢出到右侧格子，或按比例缩放（属于渲染层，参见 04 号报告）。

### 6.4 字形簇存储上限

- 每个格子的字形簇码点数设上限。Ghostty 是 64 [`page.zig:92`]，超出的码点静默丢弃。TCode 取 **32 个码点**，能覆盖最长的 ZWJ emoji（约 10 个码点）和梵文合字，又能防止 `e + 10000 个组合符` 一类攻击 [推断]。
- 兼容模式下组合符同样受此上限约束。

---

## 7. TERM / terminfo 策略

### 7.1 各终端的做法与 SSH 问题

| 终端 | 默认 TERM | SSH 问题与对策 |
|---|---|---|
| Ghostty | 自带 terminfo 时用 `xterm-ghostty`，找不到时退回 `xterm-256color`；`COLORTERM=truecolor`；`TERM_PROGRAM=ghostty`；`TERM_PROGRAM_VERSION`；会清除 `VTE_VERSION` [`termio/Exec.zig:~640-660,750-755`] | 远端没有条目时报 "unknown terminal type"。对策：① 用 `infocmp -x xterm-ghostty \| ssh host tic -x -` 手动安装；② `SetEnv TERM=xterm-256color`（会丢失样式下划线等）；③ shell integration 的 `ssh-env`（改 TERM 为 xterm-256color 并 SendEnv COLORTERM / TERM_PROGRAM*）和 `ssh-terminfo`（首次连接时自动执行 `tic`，结果缓存）[ghostty-terminfo；`Config.zig` shell-integration-features；`cli/ssh.zig`]。名字带 `xterm-` 前缀是因为很多程序检查 TERM 中是否含 "xterm"；ncurses 6.5-20241228 起已收录该条目。sudo 会过滤 `TERMINFO` |
| kitty | `xterm-kitty` | 同样的问题；对策是 `kitten ssh`（自动复制 terminfo 并启用 shell integration），或用 `env_keep += "TERM TERMINFO"` 处理 sudo [kitty `docs/faq.rst`] |
| WezTerm | **默认 `xterm-256color`**；`wezterm` 条目需要用户手动安装后改配置 [wezterm `docs/config/lua/config/term.md`] | 默认配置天然兼容 SSH；代价是 Smulx 等能力需要程序自行探测 |
| Windows Terminal | ConPTY；Linux 侧通常是 `xterm-256color` | ncurses 中有 `ms-terminal` 条目，但未被普遍使用 [ncurses] |
| VS Code / xterm.js | `xterm-256color`，`TERM_PROGRAM=vscode` | |
| tmux 内部 | `tmux-256color` 或 `screen-256color` | tmux 通过 DA1 / DA2 / XTVERSION、`CSI ? 2026 $ p` 和 OSC 10 / 11 识别外层终端 [`tty.c:413-430`]；默认 `terminal-features = "xterm*:clipboard:ccolour:cstyle:focus:title"` [`options-table.c:606`]。**TERM 匹配 `xterm*` 时，tmux 默认认为外层支持 OSC 52、光标颜色、光标样式、焦点和标题** |

### 7.2 程序如何探测特性（按可靠度排序）

1. **主动查询**：XTVERSION（最可靠 [quast-2026]）、DA1 / DA2、DECRQM（2026、2027、2031、2048、69）、XTGETTCAP、`CSI ? u` + DA1、DECRQSS 回读 SGR、OSC 10 / 11。
2. **环境变量**：`TERM_PROGRAM` / `TERM_PROGRAM_VERSION`（neovim 用它识别 WezTerm 版本 [`tui.c:525-551`]；VS Code 文档建议据此发送 633）；`COLORTERM=truecolor|24bit`（neovim 据此直接开启 termguicolors [`defaults.lua:1014-1018`]）。**这些变量默认不会通过 SSH 传递**，需要 `SendEnv` / `AcceptEnv`。
3. **TERM / terminfo**：vim 用 `keyprotocol` 按 TERM 名选择 kitty 协议或 mok2；neovim 按 terminfo 族名判断 `xterm-kitty`、`xterm-ghostty` [`tui.c:2180-2205`]。

### 7.3 TCode 推荐策略

**本地（启动系统 shell）**：
- `TERM=xterm-256color`，`COLORTERM=truecolor`，`TERM_PROGRAM=TCode`（建议与 XTVERSION 名称一致），`TERM_PROGRAM_VERSION=<semver>`。
- 清除继承来的 `VTE_VERSION`、`KITTY_WINDOW_ID`、`WT_SESSION`、`ITERM_SESSION_ID`、`TERMINFO` 等可能误导程序的变量（Ghostty 的做法）[推断 + `Exec.zig`]。
- 设置 `LANG` / `LC_CTYPE` 保证 UTF-8（参见 05 号报告）。
- **v1 不发布自定义 terminfo**。理由：
  - (a) xterm-256color 在所有系统上都有，SSH、sudo、容器都开箱即用；
  - (b) 现代能力（样式下划线、同步输出、kitty 键盘、真彩色）已经可以通过 §7.2 的查询方式探测，neovim、Codex、Claude Code、fish、tmux 都会主动查询；
  - (c) Ghostty 和 kitty 为自有 terminfo 付出了大量 SSH / sudo / tcell 兼容成本（Ghostty 注释提到 tcell 要求 terminfo 主名必须等于 TERM [`terminfo/ghostty.zig:6-16`]）。
- **代价**：xterm-256color 没有 `Smulx` / `Setulc` / `Sync` / `Su` / `Tc`，只看 terminfo 的程序（例如未做探测的 ncurses 应用）用不到这些能力。可以用 XTGETTCAP 部分弥补，vim 和 neovim 会查询。
- **v2 可选**：提供 `xterm-tcode` 条目（`use=xterm-256color` + 扩展能力），作为用户主动选择的选项。参照 Ghostty `ssh-terminfo` 的办法自动安装。

**SSH（以后内建 SSH 客户端时）**：
- 远端 `TERM=xterm-256color`。通过 SSH 的 env 请求发送 `COLORTERM`、`TERM_PROGRAM`、`TERM_PROGRAM_VERSION`；远端拒绝时静默跳过。
- 远端 shell integration（OSC 7 / 133）需要用户的远端 rc 文件支持，不注入脚本 [推断]。
- **所有查询应答在 SSH 下照常工作**，这也是"不依赖 terminfo、依赖查询"的主要理由。
- 远端发来的 OSC 7 / OSC 8 `file://` 带远端主机名，**不得当作本地路径打开**（§8.3、§8.5）。

**tmux 兼容性**：
- 在 TCode 里运行 tmux 时，tmux 会查询 XTVERSION。名字 "TCode" 不在 tmux 的表中，会落到 DA1 + `xterm*` 的默认特性：clipboard、ccolour、cstyle、focus、title，再加上 2026 探测到的 sync。RGB 需要用户设置 `terminal-features` 或 `Tc`。
- **建议**：TCode 文档提示 `set -as terminal-features ',xterm-256color:RGB:hyperlinks:usstyle:extkeys'`。长期可以向 tmux 提交 PR，把 TCode 加入 `tty_default_features` 表 [推断]。
- tmux 内部的 TUI 需要 `allow-passthrough` 才能把 OSC 9 / 1337 等透传给 TCode（Claude Code changelog："reach the outer terminal when running inside tmux with `set -g allow-passthrough on`"）。

---

## 8. 安全规格

总纲来自 [dgl-2023]：
- 严格按状态机解析；
- UTF-8 下不实现 C1；
- **默认关闭会回显可变内容的查询**；
- 应答不得包含攻击者可控的文本或控制字符；
- 对字体、剪贴板操作加限制或询问；
- 所有解析都做边界检查；
- 交给外部程序的值必须正确转义。

已知 CVE 涉及：DECRQSS 回显（iTerm2、mintty、SwiftTerm）、OSC 50 字体查询回显（xterm、mintty）、标题回报（ConEmu CVE-2022-46387 / 2023-39150）、OSC 9;9 cwd 引号注入（WT + WSL CVE-2022-44702）、OSC 8 序列未正确终止（less CVE-2022-46663）。

### 8.1 OSC 52 剪贴板

| 操作 | 默认 | 依据 |
|---|---|---|
| 写 | 允许；单次 8 MiB 上限；**每次写入在 UI 上给出提示** | Ghostty 默认写允许、读询问 [`Config.zig:2458-2459`]；kitty 默认 `write-clipboard write-primary read-clipboard-ask read-primary-ask`，并注明关闭读确认"is a security risk … even the ones running on a remote server via SSH can read your clipboard"[`definition.py:3194-3207`]；Alacritty 默认 OnlyCopy，理由是读剪贴板"can be easily abused while not providing significant benefits"[`alacritty.5.scd:618-626`]；WezTerm 和 WT 忽略读 |
| 读 | **询问**（按会话 + 来源记忆"本会话允许"；SSH 远端单独询问），可以设为拒绝 | 同上 |

- 读请求在等待用户确认期间**不能阻塞输出解析**。应答稍后再写入；拒绝时**不应答**。Ghostty 的同类处理是"Requests are ignored if denied"[推断]。
- 写入内容超过上限或不是合法 base64 时，忽略且不清空剪贴板。

### 8.2 标题回报与其他回显类查询

| 查询 | 默认 |
|---|---|
| `CSI 21 t`（标题）、`CSI 20 t`（图标名） | **关闭**。Ghostty `title-report = false`，注释写明"can expose sensitive information at best and enable arbitrary code execution at worst"[`Config.zig:2513`]；WezTerm 需要 `enable_title_reporting`；xterm 默认 disallowed GetWinTitle / GetIconTitle [xterm-man]。开启时，应答中的标题要去掉所有 C0 / C1 控制符并截断 |
| OSC 50（字体查询） | 不支持 |
| DECRQSS | 只应答 §3.9 列出的设置，内容完全由内部状态格式化，**绝不回显请求文本**（iTerm2 和 mintty 的 CVE 都是因为把请求内容放进了应答） |
| XTGETTCAP | 应答只包含 hex 编码的名字（与请求相同）和编译期常量值，安全 |
| DECRQCRA（区域校验和） | 默认关闭（信息泄露 [dgl-2023 §Information leaks]），只在测试模式开启（§9） |
| OSC 1337 `ReportVariable`、`RequestUpload` | 不支持 |
| ENQ answerback | 永远为空 |

### 8.3 超链接（OSC 8）欺骗

- 默认**普通点击不打开链接**，必须 Cmd / Ctrl + 点击 [osc8 §Security 的建议]。
- 悬停时**显示真实 URI**（状态栏或 tooltip），并高亮同一 id 的所有格子。
- 协议白名单：http、https、mailto、file（本机）。其他协议（含自定义 URI 协议）需要确认对话框 [osc8：custom URI handler 可能存在漏洞]。
- 链接文本看起来像 URL，但与实际 URI 的主机不同（典型的钓鱼手法）时，**在确认框中醒目提示** [推断]。
- `file://host/…`：只有 host 为空、`localhost` 或本机 `gethostname()` 时才在本地打开；否则不打开同名本地文件 [osc8 §file:// URIs and the hostname]。
- 长度上限：URI 8 KiB、id 256 字节；超出则丢弃该链接属性，但照常显示文本。
- 不对 URI 做任何 shell 拼接。打开时调用系统 API（macOS `NSWorkspace`、Windows `ShellExecute` 只传 URL、Linux `xdg-open` 以参数数组传递），参见 05 号报告。

### 8.4 粘贴注入

- **过滤**：所有文本插入（粘贴、拖放）都把以下字节替换成空格：NUL、BS、ENQ、EOT、**ESC**、DEL，以及 tty 特殊字符 ^C、^\、^U、^Z、^Q、^S、^W、^V、^R、^O。这与 xterm `disallowedPasteControls` 默认值 `BS,DEL,ENQ,EOT,ESC,NUL,STTY` 一致 [xterm-man]，列表直接取自 Ghostty 从 xterm 源码复制的版本 [ghostty `input/paste.zig` `encode` 的 strip 表]。替换掉 ESC 后，**粘贴内容中的 `ESC[201~` 就无法提前结束括号粘贴**。
- **安全检查**：
  - 括号粘贴模式开启时：内容若包含 `\x1b[201~`，视为不安全；
  - 未开启时：内容若包含 `\r` 或 `\n`，视为不安全；
  - 不安全时弹出确认（显示前几行预览）。
  - 依据：Ghostty `isSafe` / `isSafeWith` [`input/paste.zig:180-199`]；`clipboard-paste-protection = true`、`clipboard-paste-bracketed-safe = true` 为默认值。
- 未开启括号粘贴时，`\n` 转换为 `\r`（xterm 行为）[ghostty paste.zig 注释]。
- 也可以提供"总是确认多行粘贴"选项，供更谨慎的用户使用。
- Agent 场景（TCode 特有）：Agent 工具向终端写入输入时**也走同一套过滤**，并且永远不走括号粘贴之外的原始写入 [推断：TCode 设计需要；与 05 号报告一致]。

### 8.5 其他输入信道

| 信道 | 要求 |
|---|---|
| OSC 7、OSC 9;9、OSC 633 P Cwd、OSC 1337 CurrentDir | 只用于 UI（新标签页继承目录、显示）。用作新进程工作目录前要验证是本机的绝对路径且存在；**不拼进任何 shell 命令行**（WT 的 CVE-2022-44702）。OSC 7 不触发 DNS 查询（dgl 提到 OSC 7 可以造成 DNS 泄露） |
| 标题 | 去除控制字符，并截断长度 |
| 通知（OSC 9、777、99） | 限制频率；内容只当纯文本处理 |
| kitty 图形 `t=f/t/s` | 如果实现：只读普通文件，拒绝 /proc、/sys、/dev 等路径，所有失败返回同一个错误码（防止探测文件是否存在）[kitty-gfx §The transmission medium]。**远端会话（SSH）一律禁止 f / t / s 方式** |
| DoS 防护 | §1.5 的长度上限；kitty 键盘栈 8；标题栈 10；REP / ICH / IL 等参数钳到屏幕尺寸；SU 大参数要先钳制（kitty 注释提到 `CSI 2147483647 S` 会导致忙循环）；字形簇码点上限 32；超链接表数量上限（如 64 K 项）与 LRU 回收 |
| 写回 PTY 的总量 | 应答速率限制。例如程序以 1 MB/s 发 DA1，应答写入不能阻塞解析线程，也不能无限缓冲。设应答队列上限（如 1 MiB），超出时丢弃 [推断] |

---

## 9. 测试计划

### 9.1 套件

| 套件 | 性质 | 用法 |
|---|---|---|
| **vttest** 2.7（20251205） | 交互式、菜单驱动，覆盖 VT100–VT520、xterm 扩展、鼠标、字符集、键盘 | `vttest -l` 记录日志，`-c` 回放（键盘和鼠标测试仍需人工）[vttest.1]。用于人工验收和录制回放：在 TCode 中运行 vttest，同时录制 PTY 输出流，用作 §9.2 的黄金样本。**不要用合成的 GUI 输入去驱动**（遵守项目的 UI 验证约定） |
| **esctest2**（George Nachman / Thomas Dickey） | 全自动，80 个测试文件，"xterm, but without bugs in George's opinion" | 依赖 **DECRQCRA** 读格子内容（`escutil.AssertScreenCharsInRectEqual` 逐格求校验和）、`CSI 18 t` 读尺寸、CPR、DECRQSS、XTGETTCAP（`Co`）。`--expected-terminal` 只认 xterm / iTerm2；`--max-vt-level`。**做法**：TCode 提供"测试模式"开关，开启 DECRQCRA 和 XTWINOPS 全集；用无头 PTY 宿主（不需要 GUI）把 esctest 当子进程运行，CI 中跑 `--expected-terminal=xterm`，失败的用例维护一份 known-bugs 列表。这一项**只读运行 Python 脚本，没有构建步骤** |
| **ucs-detect** 2.3.x | 通过 CPR 测宽度，并探测 2026 / 2027 / 2031 / 2048 / 5522、kitty 键盘、OSC 52、XTGETTCAP、DECRQSS 等，输出 YAML | 每个发布版本跑一次 `ucs-detect --save-yaml`，与 Ghostty 等的 data/*.yaml 对照；目标是 WIDE / ZWJ / VS16 / VS15 在 2027 模式下 ≥ 98%。可以把结果提交给上游 data 目录 |
| xterm.js 回归素材 | `test/fixtures/escape_sequence_files/*.in` + `.text`（80×25，由 xterm 生成期望值），约 80 对，覆盖 CUP / IND / RI / DECSTBM / IRM / alt screen / SGR / vttest / vim | MIT 许可，可以直接作为黄金样本（需要按 NOTES 处理末尾空行）[xterm.js `test/fixtures/escape_sequence_files/NOTES`] |
| Alacritty ref tests | `alacritty_terminal/tests/ref/<name>/{alacritty.recording, grid.json, size.json, config.json}`，45 个（含 tmux_htop、vim_simple_edit、zsh_tab_completion、vttest_*） | Apache-2.0 / MIT。recording 就是原始字节流；grid.json 是期望网格（需要写转换器读成我们的快照格式）。很适合作为"真实 TUI 字节流 → 网格"的黄金样本 |
| iTerm2 feature-reporting 脚本 | `aliasing.sh`、`parser_unknown.sh` 等 | 解析器健壮性 [iterm2-feat §Parsing] |
| terminal-wg | freedesktop terminal-wg/specifications（2019 年以后基本停滞，仓库只有模板）；issue 清单（37 个）可以作为功能雷达：#2 同步更新、#4 语义提示、#7 终端识别、#20 OSC 7、#29 进度条、#36 emoji 宽度 | 只作参考 |

### 9.2 自动化回归架构

1. **解析层单测**：给定字节 → 期望的动作序列（print、execute、csi(params, subparams, intermediates, final)、osc(...)）。对照 alacritty/vte、Ghostty `Parser.zig` 测试里的边界用例：
   - 参数溢出（`"1;".repeat(MAX+1)`）；
   - 冒号 SGR `\x1b[38:2:255:0:255;1m`；
   - CSI 被 ESC 打断（`\x1b[3;1\x1b[?1049h`）；
   - OSC 用 BEL / ST 结束；
   - UTF-8 在 chunk 边界截断；
   - 解码得到的 C1 被忽略；原始 0x9B 输出 U+FFFD。
2. **屏幕模型快照**：用 `.in`（原始字节）+ `.snap`（文本 + 每格属性 + 光标 + 模式 + 滚动区 + 应答字节的规范化文本）构成黄金样本对，覆盖：
   - xterm.js 与 Alacritty 的素材（转换后入库，注明来源和许可）；
   - 自行录制的真实程序：用 `script -q` / asciinema 在 TCode 或其他终端里录制 vim 打开文件并编辑、htop 刷新 3 帧、tmux 分屏、less 翻页、fzf 过滤、Claude Code 和 Codex 一轮对话（**不发起真实的模型调用，可以用各自的离线 / mock 模式或录制回放**）；
   - 宽度：ucs-detect 的 `docs/ucs_example_files` 素材。
   - 快照在 Windows / macOS / Linux 上必须**逐字节一致**，确保与平台无关。
3. **应答测试**：断言查询序列的应答字节，例如 DA1、`CSI ? u` + DA1 的顺序、DECRPM 全表、XTGETTCAP、DECRQSS 回读 SGR（模拟 neovim 的探测）。
4. **键盘编码表测试**：用表格驱动（键 × 修饰 × 模式（legacy / DECCKM / mok1 / mok2 / kitty 各 flag / win32））断言输出字节。期望值来自 [ctlseqs]、[kitty-kbd] 中的表格，以及 WT `terminalInput` 单测（`src/terminal/adapter/ut_adapter/kittyKeyboardProtocol.cpp`）。
5. **鼠标编码表测试**：按钮 × 修饰 × 编码 × 坐标边界（223 / 2015 / 大于 2015）。
6. **Fuzz**：SharpFuzz 或自写随机生成器，加上结构化变异（在合法序列中插入 ESC / CAN / C1 / 超长参数 / 超长 OSC）。不变量：不抛异常、内存有上限、网格结构一致（宽字符的两半成对、光标在范围内）、`Feed` 之后 `IsGround` 可达。
7. **端到端（无头）**：真实 PTY + 真实 shell（bash / zsh / pwsh），驱动脚本只向 PTY **写字节**，不合成 GUI 事件，然后对网格做快照断言。渲染层另用 Avalonia 无头快照测试（遵守项目约定：不合成输入，不截取屏幕区域）。
8. **一致性对照（可选）**：把同一份 `.in` 交给 libghostty-vt 或 alacritty_terminal，结果作为第二参考，差异需要人工裁定。只作离线对照工具，不进入产品。

### 9.3 验收门槛（建议）

- esctest2 在 xterm 模式下：Tier 0 / Tier 1 相关测试文件通过率 ≥ 95%，剩余失败项列入 known-bugs。
- vttest 菜单 1–8、11（xterm 扩展）人工验收通过。
- ucs-detect：2027 模式下 WIDE / ZWJ / VS16 / VS15 ≥ 98%；兼容模式下与 xterm 行为一致。
- 真实 TUI 回放快照全部稳定；fuzz 运行 1 小时以上无失败。

---

## 附录 A：控制序列速查（TCode 实现清单）

ESC 序列：
- `ESC 7` / `8`、`ESC D` / `E` / `H` / `M`、`ESC c`、`ESC =` / `>`
- `ESC ( ) * +` 加 `0` / `A` / `B`
- `ESC # 8`
- `ESC N` / `O`（SS2 / SS3）、`ESC n` / `o` / `~` / `}` / `|`
- `ESC SP F` / `G`（忽略）、`ESC %`（忽略）
- `ESC \`（ST）

CSI 序列：
- `@ A B C D E F G H I J K L M P S T X Z ^ \` a b c d e f g h l m n r s t u`
- `? J`、`? K`、`? W`、`? h`、`? l`、`? n`、`? s`、`? r`、`? $ p`、`$ p`
- `! p`、`SP q`、`" q`、`> c`、`= c`、`> q`
- `> m`、`? m`（XTMODKEYS）、`> f`（XTFMTKEYS）
- `= u`、`? u`、`> u`、`< u`
- `# {`、`# }`（Tier 2）、`$ x / z / v`（Tier 2）、`' } / ~`（Tier 2）
- `* y`（测试模式）、`48 t`（应答方向）

OSC：
- `0 1 2 4 7 8 9 10 11 12 52 104 110 111 112 133 633 1337(CurrentDir|SetMark)`

DCS：
- `$ q`、`+ q`；sixel `q`（Tier 2）；未知 DCS 跳过

APC：
- `G`（Tier 2）；其他跳过

## 附录 B：主流 TUI 的启动查询（用于编写回归脚本）

| 程序 | 启动时发出（摘录） | 来源 |
|---|---|---|
| neovim | `?2004h`；DECRQM 69 / 2026 / 2027 / 2031 / 2048；`ESC[0m ESC[4:3m DCS $qm ST`（样式下划线探测）；`CSI ? u` + `CSI c`；`OSC 11;?` + `CSI 5n`；之后发 `>3u` 或 `>4;2m` | `tui.c:300-353,575-605` |
| tmux（外层） | `CSI c`、`CSI > c`、`CSI > q`、`CSI ? 2026 $ p`、`OSC 10;?`、`OSC 11;?` | `tty.c:413-430` |
| vim | `CSI > c`（t_RV）；版本 ≥ 141 时发 XTGETTCAP 请求；kitty 类 TERM 发 `CSI ? u`；xterm 类发 mok2 | `term.c` |
| Emacs | `CSI > c`；DA1 带 52 时启用 OSC 52 写；版本 ≥ 242 时发 OSC 10 / 11 | `xterm.el:793-860` |
| Codex | CPR、OSC 10 / 11、DA1、DA2、`CSI ? u`（单次 250 ms 超时）；括号粘贴、焦点、alternate scroll 1007、kitty `>5u` / `>7u` 或 mok2 | `terminal_probe.rs`、`tui.rs:245-266`、`keyboard_modes.rs` |
| Claude Code | kitty 键盘查询、2026、OSC 8、OSC 9;4、OSC 7501、OSC 52、标题；在 Windows 上处理 win32-input-mode 状态 | `CHANGELOG.md`（多条，见正文） |
