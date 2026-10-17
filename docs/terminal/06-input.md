# TKCode 终端：输入（键盘、IME、鼠标、选区、剪贴板、粘贴、链接、搜索、可访问性）

> 状态：设计稿 v1（2026-10-16）。上游约束见 [01-architecture.md](01-architecture.md)（D6 快捷键、§3.2 状态一致性、§5.2 线程、§6.5 TerminalView）；依据见 [research/05](research/05-platform-behavior-config.md) §4–§6、§9，[research/03](research/03-vt-protocol.md) §4–§6，[research/04](research/04-rendering-performance.md) §6。渲染侧见 [05-rendering.md](05-rendering.md)。
>
> **来源标注**：**[已核实]** = 在只读源码中读到（Avalonia tag `12.1.3`/`8eeda4f`，libghostty-vt `b3dc3d7`，Windows Terminal `44b440b`，TKCode `dfd3657`），给出 `文件:行`；**[推断]** = 设计决定或推导，未经运行验证。

## 0. 结论摘要

| 主题 | 决定 |
|---|---|
| 按键路由 | `MainWindow` 的 Tunnel 处理器在焦点位于终端时**不处理**按键；`TerminalView` 在自己的 KeyDown 里抛出 `KeyRouting`，由 Desktop 的 `TerminalKeyRouter` 按平台键表决定"归 TKCode 执行"还是"归 shell 编码" |
| 键表 | macOS：⌘ 组合全部归 TKCode，⌘K 清屏；Win/Linux：Ctrl+字母交给 shell，TKCode 用 Ctrl+Shift；命令面板全平台增加 ⌘⇧P / Ctrl+Shift+P（D6） |
| KeyDown / TextInput | 可打印文本（含 AltGr、死键、Option 字符、IME 提交）走 TextInput；其余在 KeyDown 编码并设 `Handled`。KeyDown 一旦 `Handled`，Win32 与 macOS 都不会再产生文本 [已核实] |
| 编码 | UI 线程持有 `GhosttyKeyEncoder` / `GhosttyMouseEncoder`，选项来自 actor 单独发布的 `InputModes`（不受 mode 2026 扣快照影响），输入路径零线程跳转 |
| win32-input-mode 9001 | libghostty-vt **不支持**（模式表里没有 9001 [已核实]）→ 需要 overlay 补丁登记该模式；编码由 TKCode 自己实现，Win32 原始键信息用 `Win32Properties.AddWndProcHookCallback` 取得 |
| IME | `TextInputMethodClient`（`SupportsPreedit=true`），预编辑自绘，列宽用 `ghostty_unicode_grapheme_width` 预测，`CursorRectangle` 跟随预编辑插入符 |
| 鼠标 / 选区 | 上报编码在 UI 线程；选区手势、自动滚动交给 actor 上的 libghostty selection gesture 状态机（15 ms tick，与 Ghostty 相同） |
| 剪贴板 / 粘贴 | 复制默认纯文本；粘贴走 `ghostty_terminal_paste`，`GHOSTTY_REJECTED` → 确认框 → `allow_unsafe` 重试 |
| Linux PRIMARY | Avalonia 12.1.3 没有 [已核实]，先做进程内 PRIMARY，接口留给 12.2 替换 |
| 可访问性 v1 | `TerminalAutomationPeer`（`IValueProvider` + `IScrollProvider`）、LiveSetting 播报、可访问缓冲视图（⌥F2 / Alt+F2） |

## 1. 模块

| 类型 | 项目 | 职责 |
|---|---|---|
| `TerminalKeyRouter` / `TerminalKeymap` | TKCode.Desktop | 平台键表、`skipShell`、命令执行（§2） |
| `TerminalInputController` | TKCode.Terminal.Avalonia | KeyDown / KeyUp / TextInput 决策、修饰键左右跟踪、吞掉被拦截按键的释放事件（§3） |
| `KeyInputMapper` | 同上 | Avalonia `KeyEventArgs` → `KeyInput`（§3.4） |
| `PlatformKeyboard`（Win32 / macOS / X11 三个实现） | 同上 | 取"去掉 Option / Alt 后的字符"、Win32 VK / 扫描码 / 控制键状态（§3.5、§3.6） |
| `Win32InputModeEncoder` | TKCode.Terminal.Core | `CSI Vk;Sc;Uc;Kd;Cs;Rc _` 编码（§3.7） |
| `TerminalImeClient` | TKCode.Terminal.Avalonia | IME 客户端（§4） |
| `MouseController` / `SelectionController` | 同上 | 上报、滚轮、手势、右键 / 中键（§5） |
| `ITerminalClipboard` / `PrimarySelection` | 同上 | 剪贴板抽象、进程内 PRIMARY（§6） |
| `PasteController` / `PathQuoter` | 同上 | 粘贴流程、拖放路径转义（§7） |
| `LinkDetector` | 同上 | URL、文件路径、OSC 8 识别与悬停（§8） |
| `SearchController` + `SearchBar` | 同上 | 搜索 UI 与工作线程 tick（§9） |
| `TerminalAutomationPeer` / `OutputAnnouncer` / `AccessibleBufferView` | 同上 | 可访问性（§10） |

## 2. 按键路由

### 2.1 Avalonia 中的事件顺序（已核实）

1. Window 上的 Tunnel 处理器先收到 KeyDown：TKCode 的 `MainWindow` 在这里处理全局快捷键 [已核实 `MainWindow.axaml.cs:159` `AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel)`；`:463-541`]。
2. 焦点元素（`TerminalView`）的 KeyDown。
3. 若 KeyDown 未 `Handled`，平台再产生 TextInput：
   - Win32：`WM_KEYDOWN` 被 Handled 后 `_ignoreWmChar = true`，随后的 `WM_CHAR` 被丢弃；`< 0x20` 的 `WM_CHAR` 一律丢弃；IME 组合中不产生 TextInput [已核实 `WindowImpl.AppWndProc.cs:261-275, 975-988`]。`WM_SYSCHAR`（Alt+字母）**没有**处理分支，所以 Alt+字母永远没有 TextInput [已核实：该文件无 `WM_SYSCHAR` 分支]。
   - macOS：KeyDown 先于输入法上下文触发；`handled` 为真时直接返回，不交给 `NSTextInputContext`，也不产生文本 [已核实 `AvnView.mm:798-814`]。组合进行中或"可能开始组合"的按键，`Key` 被替换为 `ImeProcessed`，但 `PhysicalKey` / `KeySymbol` 保留真实值 [已核实 `AvnView.mm:772-796`]。

结论：**谁在 KeyDown 里设 `Handled`，谁就要负责这个键的全部输出**；不设就会收到 TextInput。

### 2.2 方案

```
Window Tunnel：MainWindow.OnWindowKeyDown
   ├─ 命令面板 / 设置页打开时的既有逻辑（焦点不在终端，不受影响）
   └─ 焦点在 TerminalView 内（e.Source 是 TerminalView 或其后代以外的终端元素）→ 直接 return，不处理任何键
TerminalView（自身的 Tunnel 处理器，保证先于模板内子元素）
   ├─ Key == ImeProcessed 或预编辑非空 → 不处理（交给 IME）
   ├─ 修饰键本身 → 记录左右；kitty flag 8 时编码；return
   ├─ 抛出 KeyRouting(TerminalKeyRoutingEventArgs)
   │     Desktop.TerminalKeyRouter：Chord → 命令 id → 是否在 skipShell 集合 → 命令是否"可执行"(performable)
   │     → 执行，并设 args.Route = TKCode
   ├─ Route == TKCode → e.Handled = true；记住 PhysicalKey 以吞掉其 KeyUp（§2.6）
   └─ 否则 → TerminalInputController 编码（§3）
```

为什么不在 Window 的 Tunnel 里直接查表：Window 不知道终端当前能否执行某个命令（例如"有选区才复制"、"备用屏下 ⌘K 不清屏"），而 `TerminalView` 又不应该知道 TKCode 的命令。`KeyRouting` 事件让两者各管一半，和 Ghostty 的 `performable:`（只有动作能执行时才吞键）同义 [已核实 research/05 §4.6]。

```csharp
public sealed class TerminalKeyRoutingEventArgs : EventArgs
{
    public KeyEventArgs Key { get; }
    public KeyChord Chord { get; }              // 规范化后的组合：物理修饰键 + Key/PhysicalKey
    public ITerminalCommandContext Terminal { get; }   // HasSelection、IsAlternateScreen、HasShellIntegration、IsSearchOpen……
    public TerminalKeyRoute Route { get; set; } = TerminalKeyRoute.Shell;   // Shell | TKCode
}
```

命令面板打开时焦点在面板的搜索框里，不在终端，所以面板的 ↑↓↵ 处理不受影响。

### 2.3 macOS 键表（终端获得焦点时）

规则：所有 ⌘ 组合归 TKCode；Ctrl、Option、Fn、无修饰交给 shell（research/05 §4.6）。`⌘Q / ⌘H / ⌘M` 由系统菜单处理，不经过视图 [推断]。

| 按键 | 命令 id | 行为 |
|---|---|---|
| ⌘C | `terminal.copy` | 有选区时复制；无选区时吞掉（不发给 shell，与 Terminal.app 一致） |
| ⌘V | `terminal.paste` | 粘贴剪贴板（§7） |
| ⌘A | `terminal.selectAll` | |
| ⌘F | `terminal.find` | 打开搜索栏，选区为单行时预填 |
| ⌘G / ⇧⌘G | `terminal.findNext` / `terminal.findPrevious` | 搜索栏打开时 |
| **⌘K** | `terminal.clear` | 清屏与回滚（§2.5）；备用屏中不可执行 → 交给 shell |
| ⌘⇧P | `workbench.commandPalette` | 新增；终端外 ⌘K 仍打开面板 |
| ⌘T | `terminal.new` | 在当前终端组新开标签 |
| ⌘W | `terminal.closePane` | 关闭当前分屏，最后一个分屏时关闭标签（运行中程序按 `confirmClose` 确认） |
| ⌘D / ⇧⌘D | `terminal.splitRight` / `terminal.splitDown` | iTerm2 惯例 |
| ⌘[ / ⌘] | `terminal.focusPreviousPane` / `focusNextPane` | iTerm2 |
| ⌥⌘←↑→↓ | `terminal.focusPane.{left,up,right,down}` | iTerm2 |
| ⌘1…⌘9 | `terminal.selectTab.N` | 终端组内第 N 个标签（⌘9 = 最后一个），Terminal.app 惯例 |
| ⇧⌘[ / ⇧⌘] | `terminal.previousTab` / `nextTab` | |
| ⌘+ / ⌘- / ⌘0 | `terminal.fontSize.{increase,decrease,reset}` | |
| ⌘↑ / ⌘↓ | `terminal.scrollToPreviousPrompt` / `NextPrompt` | 需要 OSC 133；没有时按"上 / 下一页" |
| ⌘Home / ⌘End、⌘PgUp / ⌘PgDn | `terminal.scrollToTop/Bottom`、`scrollPageUp/Down` | |
| ⌘← / ⌘→ / ⌘⌫ | `terminal.sendText`：`^A` / `^E` / `^U` | Ghostty / VS Code 同样映射（research/05 §4.2）；kitty 键盘标志非 0 时不映射，原样编码 |
| ⌥← / ⌥→ | 发送 `ESC b` / `ESC f` | 仅 `macOptionAsMeta=none` 且 kitty 标志为 0；Terminal.app / Ghostty 同 |
| ⌥F2 | `terminal.accessibleView` | §10.3 |
| ⌘N / ⌘B / ⌘, / ⇧⌘N / ⇧⌘E | TKCode 全局（新会话、侧栏、设置、新项目、资源管理器） | 与现有一致 |
| Ctrl+任意、⌥+任意（上面未列）、F1–F12、无修饰 | shell | |

### 2.4 Windows / Linux 键表

规则：Ctrl+Shift 组合与登记的 F 键归 TKCode；单独的 Ctrl+字母、Alt+字母交给 shell；AltGr 产生的字符永远是文本（research/05 §4.6）。

| 按键 | 命令 id | Windows | Linux |
|---|---|---|---|
| Ctrl+Shift+C、Ctrl+Insert | `terminal.copy` | ✓ | ✓ |
| **Ctrl+C** | `terminal.copyIfSelection` | `keyboard.ctrlCCopies=system` 默认开：有选区时复制并清除选区，否则发 `^C`（WT 新装默认 [已核实 research/05 §4.3]） | 默认关（发 `^C`） |
| Ctrl+Shift+V | `terminal.paste` | ✓ | ✓ |
| Ctrl+V | `terminal.paste` | ✓（WT 新装默认） | 交给 shell（`^V` literal-next） |
| Shift+Insert | 粘贴 | 剪贴板 | PRIMARY（VTE、VS Code） |
| Ctrl+Shift+Insert | 粘贴剪贴板 | — | ✓ |
| Ctrl+Shift+A | `terminal.selectAll` | ✓ | ✓ |
| Ctrl+Shift+F | `terminal.find` | ✓ | ✓ |
| F3 / Shift+F3、Enter / Shift+Enter（搜索栏内） | 下一个 / 上一个 | ✓ | ✓ |
| Ctrl+Shift+K | `terminal.clear` | ✓（WT） | ✓ |
| Ctrl+Shift+P | `workbench.commandPalette` | 新增 | 新增 |
| Ctrl+Shift+T / Ctrl+Shift+W | 新标签 / 关闭分屏 | ✓ | ✓ |
| Ctrl+Shift+D / Ctrl+Shift+Alt+D | 向右 / 向下分屏 | ✓ | ✓ |
| Alt+←↑→↓ | 切换分屏焦点 | ✗（交给 shell：readline 的 Alt+← 等） | ✗ |
| Ctrl+Shift+Alt+←↑→↓ | 切换分屏焦点 | ✓ | ✓ |
| Ctrl+Tab / Ctrl+Shift+Tab | 下一个 / 上一个标签 | ✓（WT） | ✓ |
| Ctrl+Alt+1…9 | 选择标签 | ✓（WT）；AltGr 字符优先（德语布局 AltGr+7 = `{`，不会触发） | — |
| Alt+1…9 | 选择标签 | — | ✓（GNOME Terminal / Ghostty） |
| Ctrl+= / Ctrl+- / Ctrl+0 | 字号 | ✓（WT / VTE） | ✓ |
| Shift+PgUp / Shift+PgDn | 回滚翻页 | ✓ | ✓ |
| Ctrl+Shift+↑ / ↓ | 回滚一行 | ✓（WT） | ✓ |
| Ctrl+Shift+Home / End | 回滚到顶 / 底 | ✓（WT） | ✓ |
| Ctrl+Shift+PgUp / PgDn | 上 / 下一个 prompt | ✓（Ghostty） | ✓ |
| Alt+F2 | `terminal.accessibleView` | ✓ | ✓ |
| Ctrl+, / Ctrl+Shift+N / Ctrl+Shift+E | TKCode 全局 | ✓ | ✓ |
| **Ctrl+K / Ctrl+N / Ctrl+B** | TKCode 全局键 | **交给 shell**（readline kill-line、下一条历史、光标左移 / tmux 前缀） | 同左 |

### 2.5 ⌘K / Ctrl+Shift+K 清屏

按 01 §3.2，清屏是日志记录 `Clear`，不能在 UI 侧核心本地执行：

1. `terminal.clear` 的可执行条件：当前不在备用屏（快照 `Modes.AlternateScreen`）；否则返回"不可执行"，按键交给 shell（Ghostty 同样规则：备用屏下清屏会破坏程序对光标的认知 [已核实 Ghostty `Termio.zig:597-606`、`Surface.zig:5322-5336`]）。
2. 视图调用会话的"请求清屏"（`ITerminalViewSession` 需补方法，见文末），host 在日志中追加 `Clear{history:true}`，镜像核心与 UI 侧核心在同一位置执行。
3. 语义（Ghostty `clearScreen`）：清除回滚；光标不在 prompt 时只擦除光标行以上；在 prompt 时整屏清除并由 host 向 PTY 写一个 FF（`0x0C`）让 shell 重画 prompt；同时删除 Kitty 图片。具体实现属于 [04-pty-host.md](04-pty-host.md)。
4. 视图清除本地选区与搜索结果（这两者只在 UI 侧，01 §3.2 规则 4）。

### 2.6 被拦截按键的释放事件

kitty 键盘协议开启 flag 2（报告事件类型）时，程序会收到释放事件。TKCode 消费的按键（如 ⌘C），其释放事件也必须拦截，否则程序会收到"只有释放没有按下"的事件（research/03 §4.4 末尾的教训，源自 Codex 注释）。`TerminalInputController` 维护 `HashSet<PhysicalKey> _swallowed`：路由为 TKCode 时加入；对应 KeyUp 到达时移除并 `Handled`，不编码。焦点离开时清空。

### 2.7 设置项语义

| 设置 | 语义 |
|---|---|
| `terminal.keyboard.skipShell: string[]` | 修改"终端获得焦点时仍归 TKCode 的命令"集合。默认集合 = §2.3 / §2.4 表中归 TKCode 的全部命令。`"-terminal.clear"` 表示从集合移除（⌘K 交给 shell）；`"workbench.toggleSidebar"` 表示加入（Win/Linux 上 Ctrl+B 也归 TKCode）。语义同 VS Code `commandsToSkipShell`（[已核实 research/05 §4.6]） |
| `terminal.keyboard.sendKeybindingsToShell: bool`（默认 false） | 为 true 时，默认集合缩小为"最小集"：命令面板、复制、粘贴、⌥F2/Alt+F2；其余全部交给 shell。用户在 `skipShell` 里**显式加入**的命令仍归 TKCode |
| 优先级 | 用户 `skipShell` 的显式加入 / 移除 > `sendKeybindingsToShell` > 平台默认 |
| 不支持 | 多键序列（chord prefix），避免 VS Code `allowChords` 的前缀键吞键问题 |

`TerminalKeymap` 是 Desktop 的数据表（平台 × 命令 → `KeyChord[]`），同一张表也用于命令面板里显示快捷键。

## 3. KeyDown 与 TextInput 的分工

### 3.1 判定算法

```text
OnKeyDown(e):
  1. e.Key == ImeProcessed 或 preedit 非空          → 返回（不 Handled）
  2. 更新修饰键左右状态；若按的是修饰键本身：
       kitty flags & 8 → 编码按下事件，Handled；否则返回
  3. KeyRouting → Route == TKCode                     → Handled，加入 _swallowed，返回
  4. 计算 printable = KeySymbol 非空 且 首字符 ≥ 0x20 且 ≠ 0x7F
  5. 以下情况走"文本路径"（不 Handled，等 TextInput）：
       a. Windows：Ctrl 与 Alt 同时按下 且 printable 且 altGrAliasing      （AltGr）
       b. macOS：Option 按下、无 Ctrl/⌘、printable、且该侧 Option 不作 Meta  （⌥ 字符、死键）
       c. 无 Ctrl / Alt(Meta) / Super、printable、kitty flags 不含 8           （普通字符、Shift 字符、死键）
       d. 9001 模式下的可打印键：见 §3.7（先记下 VK/扫描码，等 TextInput）
  6. 其余：KeyInput ← 映射（§3.4）；n = encoder.Encode(KeyInput)；
       n > 0 → SendInput，Handled；n == 0（例如无修饰的修饰键）→ 不 Handled
  7. 发出输入前：清除选区（可配置），视口滚到底（有输入即回到底部，同所有终端）

OnKeyUp(e):
  _swallowed 中 → 移除，Handled，返回
  kitty flags & 2 → 编码释放事件（Enter/Tab/Backspace 只在 flag 8 时报释放，由编码器处理）
  9001 → 编码释放事件（§3.7）

OnTextInput(e):
  9001 → 每个 UTF-16 码元编码为按下事件（§3.7）
  否则 → UTF-8 直接 SendInput（IME 提交不加 bracketed paste，research/03 §4.1）
```

关键点：

- 第 5 步保证死键、AltGr、Option 字符、IME 都由平台合成字符，TKCode 不自己合成（research/05 §4.4）。
- **Backspace、Enter、Tab、Esc 一律在 KeyDown 编码并 Handled**：Win32 上 Ctrl+Backspace 会产生 `WM_CHAR 0x7F`（≥0x20 不被过滤），不 Handled 会重复发送；macOS 上这些键的 KeySymbol 是控制字符 [已核实 `AvnView.mm:692-698` 注释]，第 4 步已排除。
- Windows Alt+字母：`WM_SYSKEYDOWN` 的 KeySymbol 来自 `MapVirtualKey(MAPVK_VK_TO_CHAR)`（[已核实 `WindowImpl.AppWndProc.cs:1506-1511`]），字母为大写、不含 Shift 效果，并且没有 TextInput。所以 Alt 组合必须在 KeyDown 编码，基础字符由 §3.5 求出。

### 3.2 Backspace / Delete 与常用键

| 键 | 无修饰 | 说明（编码由 libghostty 编码器完成） |
|---|---|---|
| Backspace | `0x7F` | DECBKM（mode 67）置位时发 `0x08`（编码器选项 `BACKARROW_KEY_MODE` [已核实 `encoder.h:114`]）；`keyboard.backspace=ctrlH` 时反转 |
| Ctrl+Backspace | `0x08` | |
| Alt+Backspace | `ESC 0x7F` | macOS 需 Option 作 Meta，或 ⌥⌫ 映射（§2.3 未列，按 Option 字符路径：⌥⌫ 在 US 布局不产生字符 → 编码为 Alt+Backspace） |
| Delete（含 macOS fn+⌫） | `CSI 3~` | |
| Enter | `CR` | Alt+Enter `ESC CR`；kitty disambiguate 下 Shift+Enter `CSI 13;2u`（Claude Code / Codex 靠它，research/03 §4.4） |
| Tab / Shift+Tab | `HT` / `CSI Z` | |
| Esc | `ESC` | kitty flag 1：`CSI 27u` |
| 方向键 / Home / End | `CSI A..D/H/F`；DECCKM 下 `SS3` | 带修饰一律 CSI（research/03 §4.1） |

### 3.3 macOS Option 作为 Meta

- 设置 `keyboard.macOptionAsMeta: none | left | right | both`（默认 `none`，research/05 §10.3）。
- 左右区分：Avalonia 的 `KeyModifiers` 不分左右，但修饰键本身会产生 KeyDown / KeyUp（macOS 由 `flagsChanged` 合成 [已核实 `AvnView.mm:605-659`]），`PhysicalKey` 为 `AltLeft` / `AltRight`。`TerminalInputController` 自己维护四个修饰键的左右按下状态，填入 `GhosttyMods` 的 `*_SIDE` 位 [已核实 `event.h:46-76`]，并设置编码器 `MACOS_OPTION_AS_ALT`（`FALSE/TRUE/LEFT/RIGHT` [已核实 `encoder.h:62-76`]）。
- Option 作 Meta 时，需要"不带 Option 的字符"作为 `utf8`（编码器要求传入未经 Ctrl/Meta 变换的字符 [已核实 `event.h:430-447`]）。Avalonia 的 `KeySymbol` 已经包含 Option 效果（`UCKeyTranslate` 带 `optionKey` [已核实 `KeyTransform.mm:421-439`]），所以由 `PlatformKeyboard.Mac` 用 `PhysicalKey → kVK 键码`反查表 + 自己的 `UCKeyTranslate`（`LibraryImport` Carbon：`TISCopyCurrentKeyboardLayoutInputSource`、`TISGetInputSourceProperty`、`UCKeyTranslate`、`LMGetKbdType`）去掉 Option 重新求字符 [推断：AOT 安全的 P/Invoke]。

### 3.4 `KeyInput` → libghostty 键编码器

`KeyInput`（TKCode.Terminal.Core，01 §6.1 `EncodeKey(in KeyInput)` 的参数）：

| 字段 | 类型 | 来源 | 写入 libghostty |
|---|---|---|---|
| `Action` | Press / Repeat / Release | KeyDown / KeyUp；Repeat = 该 `PhysicalKey` 已在按下集合中（Avalonia `KeyEventArgs` 没有重复标志 [已核实 `KeyEventArgs.cs:8-76`]）；Win32 用 lParam bit 30（§3.6） | `ghostty_key_event_set_action` |
| `Key` | `TerminalKey`（W3C code 枚举） | `KeyEventArgs.PhysicalKey`。Avalonia 的 `PhysicalKey` 与 `GhosttyKey` 都基于 W3C UI Events code，按名一一映射 [已核实 `PhysicalKey.cs`、`event.h:78-273`] | `set_key` |
| `Mods` | Shift/Ctrl/Alt/Super/CapsLock/NumLock + 左右位 | `KeyModifiers` + 自己跟踪的左右状态；`Meta`(⌘/Win) → Super | `set_mods` |
| `ConsumedMods` | 同上 | 产生 `Utf8` 时被布局"用掉"的修饰键：Shift（字符与不带 Shift 的字符不同）、Option（未作 Meta 且产生了字符） | `set_consumed_mods` |
| `Utf8` | 字符串 | §3.5 的"基础字符"：当前布局下含 Shift、不含 Ctrl/Alt/Meta 的字符；控制字符与 macOS 功能键 PUA 一律传空 [已核实 `event.h:430-447`] | `set_utf8` |
| `UnshiftedCodepoint` | uint | 同一物理键不按 Shift 的字符（kitty key code 用未 Shift 的码点，research/03 §4.4） | `set_unshifted_codepoint` |
| `Composing` | bool | 预编辑非空 | `set_composing` |
| Win32 附加：`VirtualKey`、`ScanCode`、`Extended`、`ControlKeyState`、`RepeatCount` | | §3.6 | 只给 `Win32InputModeEncoder` |

编码器选项（UI 线程持有一个 `GhosttyKeyEncoder`，每次 `InputModes` 变化时 `setopt`，不调用 `setopt_from_terminal`——那需要终端句柄，只能在 actor 线程）[已核实 `encoder.h:89-114, 167-186`]：

| 选项 | 来自 `InputModes` |
|---|---|
| `CURSOR_KEY_APPLICATION` | DECCKM（mode 1） |
| `KEYPAD_KEY_APPLICATION` | DECKPAM（mode 66 / `ESC =`） |
| `IGNORE_KEYPAD_WITH_NUMLOCK` | mode 1035（默认置位） |
| `ALT_ESC_PREFIX` | mode 1036（默认置位） |
| `MODIFY_OTHER_KEYS_STATE_2` | `CSI > 4 ; 2 m` |
| `KITTY_FLAGS` | 当前屏幕 kitty 标志栈顶 |
| `MACOS_OPTION_AS_ALT` | 设置项（不来自终端） |
| `BACKARROW_KEY_MODE` | mode 67 |

`InputModes` 由视图核心 actor 在每批 `vt_write` 后比较、变化才发布（`Volatile` 交换），**不随 `FrameSnapshot` 发布**：快照在 mode 2026 期间最多被扣 1 s、视图不可见时不生成（01 §5.2），用快照里的 `Modes` 编码会读到过期模式。字段：DECCKM、DECKPAM、1035、1036、modifyOtherKeys、kitty 标志、67、鼠标跟踪模式与编码、1007、备用屏、2004、1004、2027、9001、`CursorAtPrompt`（`GHOSTTY_TERMINAL_DATA_CURSOR_AT_PROMPT` [已核实 `terminal.h:2934`]）。

### 3.5 求"基础字符"

| 平台 | `KeySymbol` 的性质 | 取 `Utf8` / `UnshiftedCodepoint` |
|---|---|---|
| macOS | 含 Shift / CapsLock / Option，不含 Ctrl / ⌘ [已核实 `KeyTransform.mm:421-427`] | 无 Option：直接用；Option 作 Meta：`UCKeyTranslate` 去掉 Option（§3.3）；Unshifted：去掉 Shift 再译一次 |
| Windows `WM_KEYDOWN` | `ToUnicodeEx` + 当前键盘状态；Ctrl 组合产生控制字符时被过滤为 null [已核实 `KeyInterop.cs:520-548`] | 自己调用 `ToUnicodeEx(vk, sc, 只含 Shift/CapsLock 的状态, flags = 0x4 不改变键盘状态)` [推断：Avalonia 同样用 0x4，`KeyInterop.cs:523`] |
| Windows `WM_SYSKEYDOWN` | `MapVirtualKey` 结果：无 Shift、字母大写 [已核实 `KeyInterop.cs:503-512`] | 同上（用 `ToUnicodeEx` 而不是 `MapVirtualKey`） |
| Linux X11 | 来自 keysym；Ctrl 下的行为未核实 [推断] | `KeySymbol` 可打印则用；否则按 `Key`（布局相关虚拟键）取字母 / 数字；再不行用 US 布局表 |

### 3.6 Win32 原始按键信息

Avalonia 不在 `KeyEventArgs` 中暴露 VK、扫描码和重复次数。TerminalView 附加到窗口时调用 `Win32Properties.AddWndProcHookCallback(topLevel, hook)`（[已核实 `Win32Properties.cs:41,83`；钩子在 Avalonia 的 `WndProc` 之前同步调用 `WindowImpl.cs:1043-1054`]），在钩子里记录最近一条 `WM_KEYDOWN / WM_SYSKEYDOWN / WM_KEYUP / WM_SYSKEYUP` 的 `wParam`（VK）与 `lParam`（扫描码 bits 16–23、扩展位 24、重复次数 bits 0–15、之前状态 bit 30），不设 `handled`。随后同一线程上触发的 KeyDown 读取这条记录。`ControlKeyState` 用 `GetKeyState` 求（消息处理期间反映该消息时刻的状态）：

| 位 | 值 | 来源 |
|---|---|---|
| RIGHT_ALT_PRESSED | 0x0001 | `GetKeyState(VK_RMENU)` |
| LEFT_ALT_PRESSED | 0x0002 | `VK_LMENU` |
| RIGHT_CTRL_PRESSED | 0x0004 | `VK_RCONTROL` |
| LEFT_CTRL_PRESSED | 0x0008 | `VK_LCONTROL` |
| SHIFT_PRESSED | 0x0010 | `VK_SHIFT` |
| NUMLOCK_ON | 0x0020 | `VK_NUMLOCK` 切换位 |
| SCROLLLOCK_ON | 0x0040 | `VK_SCROLL` 切换位 |
| CAPSLOCK_ON | 0x0080 | `VK_CAPITAL` 切换位 |
| ENHANCED_KEY | 0x0100 | lParam bit 24 |

钩子只在 Windows 注册；其他平台此路径为空。

### 3.7 win32-input-mode（9001）

**libghostty-vt 现状**：模式表里没有 9001 [已核实 `src/terminal/modes.zig:362-416`]，未知 CSI 也不会交给 `UNKNOWN_SEQUENCE` 回调（只报 APC / OSC [已核实 `terminal.h:616-636`]）。所以 TKCode 无法从核心得知 ConPTY 发来的 `CSI ? 9001 h`。方案：在 `native/ghostty` 的 overlay 补丁里给模式表加一项 `win32_input = 9001`（默认关，两份核心用同一构建，DECRQM 应答随之正确）。补丁打不上时的退路：UI 侧 actor 在 `vt_write` 前对字节流做一个只识别 `CSI ? 9001 h/l` 的小状态机 [推断]。这是对 01 / 02 的补充（见文末）。

**优先级**：WT 在 kitty 标志非 0 时优先 kitty，否则 9001 优先 [已核实 WT `terminalInput.cpp:224-234`]。ConPTY 当前强制关闭 KKP 转换（research/03 §4.5），所以 Windows 本地会话里实际总是 9001。TKCode 采用 WT 的顺序。

**编码**：`CSI Vk ; Sc ; Uc ; Kd ; Cs ; Rc _`，省略值取默认（Vk/Sc/Uc/Kd/Cs = 0，Rc = 1）（research/03 §4.5）。流程照搬 WT（[已核实 WT `Terminal.cpp:604-760`]）：

| 事件 | 处理 |
|---|---|
| 修饰键按下 / 释放 | 立即编码（Kd=1/0），Uc=0 |
| 非字符键（方向、F 键、Enter、Tab、Esc、Backspace，或 Alt 单独按下的组合） | 立即编码按下，Uc = `ToUnicodeEx` 结果（可能为 0），`Handled` |
| 产生字符的键按下（含死键、AltGr） | **不**编码、不 Handled；记下 `(Vk, Sc, Cs)` 为"待配对" |
| TextInput（平台已合成的字符，含死键组合结果） | 每个 UTF-16 码元编码一个按下事件：Vk / Sc 取"待配对"记录（没有时 Vk 取 `VkKeyScanEx(ch)` 的低字节，Sc 由 `MapVirtualKey` 求；IME 提交没有对应按键时 Vk=`VK_PACKET`(0xE7)，Sc=0），Uc=码元 |
| 任意键释放 | 编码释放（Kd=0），Uc 用 `ToUnicodeEx` 求 |
| 被 TKCode 拦截的键 | 按下与释放都不发 |

Rc 取 lParam 的重复次数（一般为 1）。焦点事件（1004）照常发。非 Windows 平台不注册钩子，9001 置位时也按普通路径编码 [推断：非 Windows 上没有程序会请求 9001]。

## 4. IME

### 4.1 客户端实现

```csharp
sealed class TerminalImeClient(TerminalView view) : TextInputMethodClient
{
    public override Visual TextViewVisual => view;
    public override bool SupportsPreedit => true;            // 预编辑自己画（05 §9.6）
    public override bool SupportsSurroundingText => false;   // 终端没有可编辑的上下文
    public override string SurroundingText => string.Empty;
    public override TextSelection Selection { get => default; set { } }
    public override Rect CursorRectangle => view.ImeCaretRect();   // DIP，相对 TextViewVisual
    public override void SetPreeditText(string? text, int? cursorPos) => view.SetPreedit(text, cursorPos);
    public void NotifyCaretMoved() => RaiseCursorRectangleChanged();
}
// TerminalView：AddHandler(TextInputMethodClientRequestedEvent, (_, e) => e.Client = _ime);
```

API 与签名 [已核实 `TextInputMethodClient.cs:5-117`；`InputElement.cs:134-137`]。只有 `SupportsPreedit=true` 时平台才把组合串交给客户端（research/04 §6.2）。

| 项 | 规则 |
|---|---|
| 预编辑期间 | 不写 PTY；KeyDown 的 `ImeProcessed` 一律放行；`KeyInput.Composing=true` |
| 提交 | 经 TextInput 到达 → 普通文本路径（不加 bracketed paste）；清空预编辑 |
| 取消 | `SetPreeditText(null)` 或空串 → 清空叠加层 |
| 焦点离开 | 清空预编辑（平台会取消组合） |
| `TextInputOptions` | `ContentType=Normal`、`Multiline=false`、`ShowSuggestions=false`；快照 `Cursor.PasswordInput` 为真时 `IsSensitive=true`（[已核实 `TextInputOptions.cs:30-264`]，桌面平台是否有效果未验证） |

### 4.2 预编辑列宽预测

预编辑叠加层要按"程序提交后终端实际占用的列数"排版，否则提交瞬间会跳动：

- 把预编辑字符串转成码点数组，按当前 `InputModes.GraphemeClusterMode`（2027）：
  - 开：循环调用 `ghostty_unicode_grapheme_width(cps + i, len - i, &w)`，累加；每个 grapheme 记录 1 或 2 列；
  - 关：逐码点 `ghostty_unicode_codepoint_width`，宽度 0 的码点并入前一个 grapheme。
- 这两个函数是纯函数、无分配、线程安全 [已核实 `unicode.h:69, 70-144`]，UI 线程直接调用（通过 `TKCode.Terminal.Core.TerminalUnicode` 静态包装）。
- 结果 `CellWidths[]` 放进 `OverlayState.Preedit`，渲染见 05 §9.6。
- 歧义宽度设置（`unicode.ambiguousWidth`）由核心决定；若核心配置为 wide，预测时对 East Asian Ambiguous 码点改为 2（与核心同一张表）[推断：需与 02 的宽度配置一致]。

### 4.3 候选框定位

- `CursorRectangle`：有预编辑时为预编辑插入符所在格的矩形；否则为光标格矩形（DIP，相对 `TerminalView`）。高度用整格高，Windows IMM 和 macOS 都把候选框放在矩形下方。
- 光标格变化（快照 `Cursor` 位置变化、视口滚动、字号变化）以及预编辑插入符移动时调用 `NotifyCaretMoved`，每帧最多一次。
- 预编辑叠加层左移（05 §9.6）时，矩形跟随叠加层的实际位置。

## 5. 鼠标

### 5.1 上报

UI 线程持有 `GhosttyMouseEncoder`，选项来自 `InputModes`：`EVENT`（跟踪模式 none / X10 / normal / button / any）、`FORMAT`（X10 / UTF-8 / SGR / urxvt / SGR-pixels）、`SIZE`（屏幕像素、cell 像素、内边距）、`ANY_BUTTON_PRESSED`、`TRACK_LAST_CELL`（按格去重）[已核实 `mouse/encoder.h:35-125`]。坐标用设备像素（与 `TerminalSize.CellWidth/CellHeight` 一致），encoder 负责换算成格与 SGR-pixels。

| 规则 | 说明 |
|---|---|
| 绕过上报 | 按住 `mouse.bypassReportingModifier`（默认 Shift，WT / VTE / Ghostty）时，鼠标事件交给 TKCode（选区、链接），不上报 |
| 上报内容 | 按下、释放、拖动（button / any 模式）、移动（any 模式）、滚轮（按钮 4–7） |
| 捕获 | 按下时 `e.Pointer.Capture(this)`，拖出控件仍能收到移动与释放；坐标由 encoder 钳制在网格内 |
| 激活窗口的点击 | 窗口在按下前 50 ms 内刚获得激活时，该次按下与对应释放都不上报 [推断：各终端常见做法，research/03 §5] |
| 链接点击 | ⌘/Ctrl+单击链接优先于上报（WT 规则，§8.3） |
| OSC 22 指针形状 | 快照 `MouseShape` 映射到 Avalonia `Cursor`；默认 `Ibeam`；悬停链接时 `Hand` |

### 5.2 滚轮

```text
OnPointerWheelChanged(e):
  rows = 累积(e.Delta.Y × linesPerNotch)，取整数部分，余数留到下次（触控板高精度滚动）
  if 跟踪模式 ≠ none 且未按绕过修饰键   → 每行上报一个按钮 4/5 事件（水平 6/7）
  elif 备用屏 且 mode 1007 置位          → 每行编码一次 ↑/↓（按 DECCKM 选 CSI / SS3）
  else                                    → actor.ScrollViewport(-rows)（回滚）
```

- mode 1007（alternate scroll）在 libghostty 中默认置位 [已核实 `modes.zig:394` `.default = true`]，所以 less、man 等程序在备用屏下滚轮可用。
- `linesPerNotch`：Windows 读 `SPI_GETWHEELSCROLLLINES`，其他平台 3 [推断]。Avalonia `PointerWheelEventArgs.Delta` 的单位在各平台的精确定义需实测（[已核实只有 `Delta` 属性 `PointerWheelEventArgs.cs:5`]）。
- 平滑滚动开启时，回滚分支改为把亚行位移交给渲染（05 §10.6）。

### 5.3 选区手势（libghostty selection gesture）

手势状态机在 actor 上（`ghostty_selection_gesture_event` 需要终端句柄，且必须与终端修改串行 [已核实 `selection.h:54-60, 632-636`]）。UI 线程只发命令：

```csharp
readonly record struct SelectionGestureCommand(
    GestureEventType Type,            // Press / Drag / Release / AutoscrollTick
    int ViewportCol, int ViewportRow, // actor 用 ghostty_terminal_grid_ref 转成 GridRef
    PixelPoint SurfacePosition,       // 设备像素
    ulong TimeNs, bool Rectangle);
```

actor 每次调用前设置的事件选项 [已核实 `selection.h:480-536`]：

| 选项 | 值 |
|---|---|
| `TIME_NS` | `Stopwatch` 纳秒；不设则只有单击行为 |
| `REPEAT_INTERVAL_NS` | `IPlatformSettings.GetDoubleTapTime(PointerType.Mouse)`（[已核实 `IPlatformSettings.cs:34`]）；libghostty 默认为 0，必须设置 [已核实 `selection_gesture.zig:81`] |
| `REPEAT_DISTANCE` | `GetDoubleTapSize(PointerType.Mouse)` 换成设备像素 |
| `WORD_BOUNDARY_CODEPOINTS` | `selection.wordSeparators` 的平台默认（research/05 §5.3） |
| `BEHAVIORS` | 单击 = CELL，双击 = WORD，三击 = LINE（换行前的整条逻辑行，iTerm2 / research/05 §5.3） |
| `RECTANGLE` | 矩形选择修饰键按下：macOS Option、Windows Alt、Linux Ctrl（research/05 §5.3） |
| `GEOMETRY` | 列数、cell 宽、左内边距、表面高度（设备像素） |
| `VIEWPORT` | 自动滚动 tick 时的指针视口坐标 |

- 结果选区用 `GHOSTTY_TERMINAL_OPT_SELECTION` 安装为终端当前选区（[已核实 `terminal.h:2148-2160`]），下一帧快照带出 `Selection`。
- **自动滚动**：拖动事件后读 `GHOSTTY_SELECTION_GESTURE_DATA_AUTOSCROLL`（none / up / down）；非 none 时 actor 启动 15 ms 定时器，每次发 `AUTOSCROLL_TICK`，直到变回 none 或释放（Ghostty `selection_scroll_ms = 15` [已核实 `termio/Thread.zig:41`]）。
- 程序开启鼠标跟踪时，只有按住绕过修饰键才做选区。
- Option/Alt+单击移动光标（`mouse.altClickMovesCursor`，默认开）：仅当 `CursorAtPrompt` 为真且点击位置在光标所在的逻辑行内；计算列差，用键编码器发送对应次数的 ←/→ [推断：VS Code / Terminal.app 的行为；Ghostty 的 `cursor-click-to-move` 同理]。macOS 上 Option+拖动是矩形选择，只有"单击无拖动"才移动光标。
- macOS 深按（Force Touch）：Avalonia 不提供 → 不支持 `DEEP_PRESS`。

### 5.4 右键与中键

| 平台 | 右键（`mouse.rightClick=system`） | 中键（`mouse.middleClick=system`） |
|---|---|---|
| macOS | 指针不在现有选区内时先选中单词，然后弹出上下文菜单 | 无 |
| Windows | 有选区：复制并清除选区；无选区：粘贴 | 无 |
| Linux | 上下文菜单 | 粘贴 PRIMARY（§6.3） |

（research/05 §5.3，VS Code 的分平台默认值。）程序开启鼠标跟踪且未按绕过修饰键时，右键 / 中键上报给程序。

上下文菜单项（由 Desktop 通过 `TerminalView.ContextRequested` 提供 AtomUI 菜单，视图只给出上下文）：复制、复制为 HTML、粘贴、粘贴选区（Linux）、全选、清屏、查找、向右 / 向下分屏、打开链接 / 复制链接地址（指针在链接上）、复制命令输出（有 OSC 133）、可访问缓冲视图。

## 6. 剪贴板

### 6.1 复制

| 项 | 规则 |
|---|---|
| 文本 | actor `GetSelectionText(selection, TextFormat.Plain)`：软换行的行拼接（`unwrap=true`），`selection.trimTrailingWhitespace`（默认 true）去掉行尾空白（`trim` [已核实 `selection.h:235-246` 格式化选项]）；矩形选区每行一段 |
| 换行符 | Windows `\r\n`，其他 `\n` |
| 写入 | `TopLevel.Clipboard.SetTextAsync`（[已核实 `TopLevel.cs:544`、`ClipboardExtensions.cs:165`]） |
| HTML | 默认不写；`terminal.copyAsHtml` 命令同时写纯文本 + HTML：`DataTransferItem` 加 `DataFormat.Text` 与平台格式（[已核实 `DataFormat.cs:185` `CreateStringPlatformFormat`]）——Windows `"HTML Format"`（CF_HTML，需拼接带偏移的头）、macOS `"public.html"`、Linux `"text/html"` [推断：平台格式名]；HTML 由 formatter 生成（01 §6.1 `Format`） |
| 复制后 | Windows Ctrl+C / 右键复制：清除选区（WT）；⌘C / Ctrl+Shift+C：保留选区 |

### 6.2 copyOnSelect

`selection.copyOnSelect: system | off | primary | clipboard | both`，默认 Linux `primary`，其他 `off`（research/05 §10.3）。在选区手势**释放**时写入（拖动中不写），选区为空不写。

### 6.3 Linux PRIMARY（进程内）

- Avalonia 12.1.3 没有 `ClipboardType` / `PrimarySelection`，`TopLevel` 只有 `Clipboard` [已核实：源码中无 `PrimarySelection`；`TopLevel.cs:544`]；X11 PRIMARY 在 master 上才有，Wayland 后端没有（research/05 §5.2）。
- `ITerminalClipboard` 抽象：`SetTextAsync(ClipboardTarget, string)`、`GetTextAsync(ClipboardTarget)`。`ClipboardTarget.Primary` 的 12.1.x 实现是 `InProcessPrimarySelection`（静态单例，保存最后一次选中的文本），只在 TKCode 自己的控件之间生效。Desktop 可以让 `CodeView` 等控件也写入它。
- 升级到带 PRIMARY 的 Avalonia 后，只替换这一个实现（`TopLevel.TryGetClipboard(ClipboardType.PrimarySelection)`）。
- OSC 52 的 `p` / `s` 目标映射到同一个 PRIMARY（`GHOSTTY_CLIPBOARD_LOCATION_PRIMARY/SELECTION` [已核实 `terminal.h:690-700`]）。

### 6.4 OSC 52

- 写：由**镜像核心**的 `ClipboardWrite` 触发，host 发 `session/clipboardWrite` 给该会话的主视图（[04](04-pty-host.md) §11）；Desktop 按 `clipboard.oscWrite`（默认 `allow`）处理，`ask` 时弹出确认，显示前 200 字符。UI 侧核心的同一回调忽略，避免多个视图重复写入。
- 读：同样由镜像核心触发，经 `clipboard/readRequest` / `clipboard/readResponse` 往返（01 §3.2 规则 6）。默认 `ask`。库原生不支持"以后补发应答"，依赖 overlay O3（[02](02-core-ghostty.md)）。

## 7. 粘贴

### 7.1 流程

```text
Paste(source = Clipboard | Primary | Drop | Command):
  text ← 剪贴板 / PRIMARY / 拖放结果
  if text 为空 → 返回
  预处理：
    trimTrailingNewline（默认 true）：bracketed paste 未开启 且 去掉末尾 \r?\n 后是单行 → 去掉末尾换行
    largePasteWarning（字节，0 = 关）：超过阈值 → 确认
    multiLineWarning = always：含换行就确认（即使 bracketed）
    含 "\x1b[201~" → 一律确认（Ghostty 规则）
  r ← actor.EncodePaste(text, allowUnsafe: multiLineWarning == never)
  if r == Rejected → 确认框（显示行数与前 5 行）：
        [粘贴] → EncodePaste(text, allowUnsafe: true)
        [粘贴为单行] → 把换行替换为空格后 EncodePaste(…, allowUnsafe: true)
        [取消]
  SendInput(r.Bytes)（host 写线程按 4 KiB 分块，01 §5.1）；视口滚到底
```

- `EncodePaste` 在 actor 上调用 `ghostty_terminal_paste`（[已核实 `paste.h:187-189`]）：开启 bracketed paste（2004）时包裹，否则 `\n → \r`；不安全控制字节替换为空格；可能注入命令的内容（未包裹时的换行 / 回车，包裹时的结束标记）返回 `GHOSTTY_REJECTED`，不写任何东西 [已核实 `paste.h:42-50, 165-183`]。`multiLineWarning=auto`（默认）正好等于这个规则（research/05 §6.3）。
- **输出通道**：`ghostty_terminal_paste` 的结果通过 `write_pty` 回调分块输出 [已核实 `paste.h:37-41`]，而 01 §3.1 规定 UI 侧核心的 `write_pty` 是空实现。actor 必须在调用期间打开"捕获作用域"：回调把字节写进 `PasteResult` 的缓冲，调用结束后关闭；其他时间仍丢弃。
- **mode 5522（kitty 粘贴事件）**：需要安装 `clipboard_read` 回调才会触发 [已核实 `paste.h:16-28, 87-93`]。程序随后用 OSC 5522 读取剪贴板时，请求会到达**镜像核心**，而一次性密码授权记录在 UI 侧核心——两份核心状态不一致。v1 **不**在 UI 侧核心安装 `clipboard_read`，粘贴事件关闭（见文末）。
- `paste.ignoreBracketedPasteMode=true`：改用不读终端状态的 `ghostty_paste_encode`（不包裹）[已核实 `paste.h:243`]。
- 来源：键盘 / 菜单 / 中键粘贴用 `GHOSTTY_PASTE_SOURCE_CLIPBOARD`；拖放用 `GHOSTTY_PASTE_SOURCE_TEXT` [已核实 `paste.h:76-85`]。IME 提交不走粘贴。

### 7.2 拖放

- `DragDrop.SetAllowDrop(view, true)`；`DragOver`：数据含 `DataFormat.File` 或 `DataFormat.Text` 时 `DragEffects=Copy`（[已核实 `DragDrop.cs`、`DragEventArgs.cs`、`DataFormat.cs:34-46`]）。
- 文件：`IStorageItem.TryGetLocalPath()`，按 shell 种类转义，多个路径以空格连接，**末尾不加换行**，然后走 §7.1（安全检查一样适用）。
- shell 种类来自 `TerminalViewOptions.ShellKind`（Desktop 按 profile 填，01 §6.3 `ShellKind`）。

| shell | 规则（research/05 §6.2–6.3，VS Code `preparePathForShell`） |
|---|---|
| bash / zsh / sh（macOS） | 反斜杠转义 `` \ ()"&'!$<>;\|*?[]#`{}^+=@~ ``、空格、制表符（Terminal.app / iTerm2 观感） |
| bash / zsh / sh（Linux） | 单引号包裹，内部 `'` 写成 `'\''`（`g_shell_quote`） |
| fish | 单引号包裹，内部 `\` → `\\`、`'` → `\'` |
| pwsh / powershell | 含空格或 `` '`$&(){}[];,@# `` 等时单引号包裹，`'` 写成 `''`；**不加** `& ` 前缀 |
| cmd | 含空格或 `&()[]{}^=;!'+,`~` 时双引号包裹 |
| Git Bash | `C:\a\b` → `/c/a/b`，再按 bash（Linux 规则） |
| WSL | `C:\a\b` → `/mnt/c/a/b`，再按 bash（Linux 规则） |
| nu | 不含反引号时用反引号包裹，否则 `r#'…'#` |
| 其他 / 未知 | 按平台默认 posix 规则；Windows 按 cmd |
| 所有 | 先去掉 C0 / C1 控制字符 |

- 拖入纯文本：按 §7.1 粘贴（`SOURCE_TEXT`）。

## 8. 链接

### 8.1 来源

| 来源 | 识别 |
|---|---|
| OSC 8 | 快照 cell 的超链接 id → `Hyperlinks` 表的 URI（需补进快照，见 05 文末） |
| URL | `[GeneratedRegex]`（AOT 安全）识别 `https?`、`ftp`、`file`、`mailto`、`ssh`、`git` 等 scheme；去掉末尾 `.,;:!?'"`，括号按配对保留（`(…)` 在 URL 内部成对时保留结尾的 `)`） |
| 文件路径 | 绝对路径（`/a/b`、`~/a`、`C:\a`、`\\server\share`）、相对路径（`./a`、`../a`、`a/b.ext`）加位置后缀 |

位置后缀（取 VS Code `terminalLinkParsing.ts` 的常用子集 [已核实 research/05 §5.1 来源]）：

| 形式 | 示例 |
|---|---|
| `:line` / `:line:col` / `:line.col` | `src/a.cs:12:5` |
| `(line)` / `(line,col)` / `(line, col)` | `a.cs(12,5)`（MSBuild） |
| `[line]` / `[line, col]` | |
| `", line N"` | `File "a.py", line 3`（Python） |
| ` line N` / `:line N` | `a.txt line 3` |
| `#Lline` | `a.cs#L12` |

### 8.2 检测与验证

- 时机：指针悬停时只检测指针所在的**逻辑行**（拼接软换行行），结果按 `(RowId, Version)` 缓存；不在每帧全屏扫描。
- 相对路径的基准目录：该行所属命令的 cwd（OSC 133 命令记录里的 cwd），否则当前 OSC 7 cwd，否则会话初始 cwd。
- 存在性验证在线程池执行，结果缓存 5 s；路径不存在则不显示为链接（避免把普通词当路径）。
- OSC 7 的主机名不是本机（ssh 会话）时，不做文件路径识别；URL 照常。

### 8.3 悬停与激活

| 项 | 规则 |
|---|---|
| 悬停 | 下划线（05 §8.5：无下划线加 single，已有 single 改 double）+ 指针 `Hand` + 工具提示：OSC 8 显示真实地址；检测到的链接显示"⌘/Ctrl+单击打开" |
| 激活修饰键 | macOS ⌘，Windows / Linux Ctrl（research/05 §5.3） |
| 与鼠标上报 | 修饰键 + 单击优先于程序的鼠标上报（WT） |
| 键盘 | v1 不提供键盘遍历链接（v2） |

```csharp
public sealed class TerminalLinkActivatedEventArgs : EventArgs
{
    public TerminalLinkKind Kind { get; }      // Url | File | Hyperlink（OSC 8）
    public string Target { get; }              // 原始目标（URI 或路径文本）
    public string? DisplayText { get; }        // 屏幕上的文字（OSC 8 时可能与 Target 不同）
    public string? ResolvedPath { get; }       // File：已按 cwd 解析的绝对路径
    public int? Line { get; } public int? Column { get; }
    public KeyModifiers Modifiers { get; }
    public bool Handled { get; set; }
}
```

Desktop 处理：URL 按 `links.allowedSchemes` 交给 `TopLevel.Launcher`；OSC 8 先确认真实地址（显示文字与地址不一致时必须确认）；`file://` 无主机在 TKCode 工作台打开，有主机拒绝；文件路径在工作台打开到行列（01 §7）。

## 9. 搜索

### 9.1 UI

- `SearchBar`：`TerminalView` 模板中的浮层（右上角），含输入框、"k / n"、上一个 / 下一个、关闭。样式由宿主主题提供。
- 打开：`terminal.find`；选区为单行时预填并选中。
- 匹配规则：libghostty 搜索按字节精确匹配，只有 ASCII 字母不区分大小写 [已核实 `search.h:31-34`]。v1 不提供正则、全词、区分大小写开关。
- 键：Enter / ⌘G / F3 = 向更旧的内容（`GHOSTTY_SEARCH_OPT_SELECT_NEXT`，从屏幕底部往回滚方向 [已核实 `search.h` OPT_SELECT_NEXT 注释]）；Shift+Enter / ⇧⌘G / Shift+F3 = 向更新的内容（`SELECT_PREV`）；Esc 关闭并把焦点还给终端。
- 计数显示 `SELECTED_INDEX + 1` / `TOTAL_MATCHES`（index 0 是最新的匹配 [已核实 `search.h` DATA_SELECTED_INDEX 注释]）。
- 关闭时若有选中的匹配，把它安装为终端选区，⌘C 可以直接复制。

### 9.2 线程

libghostty 搜索把工作分成两类 [已核实 `search.h:36-52, 104-117`]：`feed` / `set(NEEDLE)` / `select` 需要独占终端（只能在 actor）；`tick` / `get` 只碰搜索自己的内存，可以在其他线程。同一个 `GhosttySearch` 的调用必须串行。所以采用"所有权交接"：

```text
UI：输入变化（50 ms 去抖）→ actor.SetNeedle(text)
actor：ghostty_search_set(NEEDLE) → feed → status
   RUNNING → 把 search 句柄交给 SearchWorker（标记 owner = worker），actor 之后不再碰它
SearchWorker（线程池）：循环 tick，每轮预算 2 ms，直到 FEED_REQUIRED 或 COMPLETE → 交回 actor
actor（收回后）：get(TOTAL_MATCHES, SELECTED_INDEX, VIEWPORT_MATCHES)
   → 换算视口坐标（ghostty_terminal_point_from_grid_ref）→ 写进下一帧 FrameSnapshot.SearchHighlights
   → 向 UI 发 SearchStatus(k, n, running)
   FEED_REQUIRED → 再 feed，再交给 worker
终端有新输出（search 处于 actor 手中时）：每 100 ms 最多 feed 一次，保持视口匹配最新
select next/prev：actor 调 set(SELECT_NEXT/PREV)（会先追上终端，并按 SCROLL_IF_NEEDED 滚动视口）
```

- 匹配跨主屏 / 备用屏切换保留（[已核实 `search.h:20-26`]），vim 进出不会重启回滚搜索。
- 匹配结果只在下一次修改终端前有效，所以只在 actor 上、feed 之后读取并立即换算（[已核实 `search.h:63-71`]）。
- 关闭搜索：`set(NEEDLE, null)` 释放终端内的跟踪状态，然后 `ghostty_search_free`。

## 10. 可访问性 v1

### 10.1 `TerminalAutomationPeer`

Avalonia 12.1.3 公开的 provider 只有 Value、RangeValue、Toggle、Invoke、Scroll、Selection、ExpandCollapse 等，没有 `ITextProvider` [已核实 `Avalonia.Controls/Automation/Provider/` 目录]。

| 成员 | 实现 |
|---|---|
| ControlType | `Document`；Name = 终端标题（OSC 0/2 或 profile 名） |
| `IValueProvider` | `IsReadOnly = true`；`Value` = 视口纯文本（UI 线程从最新快照的行生成，上限 64 KiB）；`SetValue` 抛 `InvalidOperationException`（[已核实接口 `IValueProvider.cs`]）。Linux AT-SPI 的 Text 接口由 `IValueProvider.Value` 驱动（research/05 §9） |
| `IScrollProvider` | 只支持纵向：`VerticallyScrollable`、`VerticalScrollPercent`（由 `ScrollbarState`）、`VerticalViewSize`、`Scroll(ScrollAmount)` → `ScrollViewport` 行 / 页、`SetScrollPercent` → 绝对滚动 [已核实接口 `IScrollProvider.cs`] |
| 变化通知 | 视口内容变化且已检测到辅助技术时，节流 500 ms `RaisePropertyChangedEvent(Value)`（[已核实 `AutomationPeer.cs:590`]） |

### 10.2 输出播报

- 隐藏的播报元素：`TerminalView` 模板里一个 1×1、`Opacity=0`、不可命中测试、`AutomationProperties.LiveSetting=Polite` 的元素（[已核实 `AutomationProperties.cs:239`]）。Win32 上 peer 的 Name 变化且 LiveSetting 非 Off 时触发 UIA `LiveRegionChanged`；macOS 透传 LiveSetting（research/04 §6.3）。`IsVisible=false` 的元素可能不进自动化树，所以用透明而不是隐藏 [推断：需在三个平台验证]。
- 内容：actor 在每批输出后报告"新完成的行"（光标离开的行，以及滚出视口的行）的绝对行号区间；UI 线程每 500 ms 汇总，向 actor 请求这些行的纯文本（`Format`）。
- 去掉回显：维护最近 1 s 内用户键入字符的队列，新输出以这些字符开头时去掉（xterm.js `_charsToConsume` 思路，research/04 §6.3）。
- 超过 20 行时只播报"输出了 N 行"（xterm.js 上限 20 行）。
- 开关：`accessibility.announceOutput: auto | on | off`；`auto` = 检测到屏幕阅读器时开。Avalonia 没有屏幕阅读器检测 API [已核实：源码中无相关 API]，由 Desktop 分平台检测：Windows `SystemParametersInfo(SPI_GETSCREENREADER)`；macOS `NSWorkspace.isVoiceOverEnabled`；Linux D-Bus `org.a11y.Status.ScreenReaderEnabled` [推断]。

### 10.3 可访问缓冲视图（⌥F2 / Alt+F2）

- 与 VS Code Accessible View 相同（research/05 §9）：打开一个只读 `TextBox`（自带平台文本无障碍，屏幕阅读器可逐字、逐行浏览），内容是回滚 + 屏幕的纯文本（actor `Format`，上限最近 10,000 行），插入符放在末尾。
- 有 OSC 133 时：Ctrl+↑ / Ctrl+↓（macOS ⌥↑ / ⌥↓）在命令之间跳转；提供"跳到命令"列表（命令行文本 + 退出码）。
- Esc 关闭并把焦点还给终端；打开时不实时刷新，重新打开才更新（避免屏幕阅读器位置跳动）。

### 10.4 其他

| 项 | 规则 |
|---|---|
| 键盘陷阱 | 终端会吃掉 Tab；离开终端的方式：⌘⇧P / Ctrl+Shift+P 命令面板、⌘B 等全局键（macOS），以及 Ctrl+Shift+E（Win/Linux）。这些都在默认 skipShell 中，`sendKeybindingsToShell=true` 时命令面板仍保留 |
| 焦点可见 | `Terminal.FocusBorder` 焦点环（05 §11.1） |
| 高对比度 | 最小对比度自动提高到 7（05 §9.7） |
| 屏幕阅读器开启时 | 关闭光标与文字闪烁 |
| v2 | UIA TextPattern / NSAccessibility 文本（需要给 Avalonia 贡献 `ITextProvider` 或写平台专用 peer，research/04 §10 决策 3） |

## 11. 焦点上报

GotFocus / LostFocus，以及窗口 `Activated` / `Deactivated`：模式 1004 置位时 `ghostty_focus_encode`（纯函数 [已核实 `focus.h:64`]）编码后 `SendInput`，只在状态真正变化时发送。ConPTY 依赖它（research/03 §5）。失焦同时清空修饰键左右状态与 `_swallowed`。

## 12. 测试计划

### 12.1 自动化

| 套件 | 内容 | 方式 |
|---|---|---|
| `KeyEncodingTests`（Core.Tests） | 表驱动：`(KeyInput, InputModes) → 期望字节`。覆盖 legacy 全表（research/03 §4.1）、DECCKM / DECKPAM、modifyOtherKeys 2、kitty 标志 1/2/4/8/16 组合（含释放、重复、修饰键自身）、Backspace 各变体与 DECBKM、Option 作 Meta 左 / 右 | 直接调用编码器，不经 UI |
| `Win32InputModeTests` | 修饰键按下 / 释放、非字符键、"待配对 + TextInput"、死键组合、IME 提交（VK_PACKET）、`ControlKeyState` 各位、被拦截键不发送 | 纯函数 + 伪造的钩子记录 |
| `KeyDecisionTests`（Avalonia.Tests） | §3.1 判定表：AltGr、Option 字符、Alt+字母（Win32 SYSKEY）、ImeProcessed、预编辑中、kitty flag 8 | 构造 `KeyEventArgs` 调用 `TerminalInputController` |
| `KeyRouterTests`（Desktop.Tests） | 三平台键表、`skipShell` 增删、`sendKeybindingsToShell`、performable（无选区 Ctrl+C → shell；备用屏 ⌘K → shell）、释放事件吞掉 | 纯逻辑 |
| `MouseEncodingTests` | 5 种跟踪 × 5 种编码 × 按钮 / 修饰键、绕过修饰键、1007、滚轮累积、坐标钳制、SGR-pixels 像素坐标 | 编码器 + 控制器 |
| `SelectionReplayTests` | 回放固定屏幕内容 + 手势序列 → 期望选中文本：三平台单词分隔符、三击逻辑行、矩形、自动滚动 tick、软换行拼接、行尾空白 | Core 回放 |
| `PasteTests` | 拒绝矩阵（bracketed 开 / 关 × 换行 / 结束标记 / 控制字符）、单行去尾换行、捕获作用域、拖放转义表（每种 shell 的空格、引号、`$`、中文、`'` 与 `"` 并存） | Core + 纯函数 |
| `LinkDetectorTests` | URL 末尾标点、括号配对、各位置后缀形式、相对路径解析、远程 cwd 关闭路径识别、OSC 8 | 纯函数 |
| `SearchTests` | 所有权交接、计数与 k/n、上一个 / 下一个环绕、备用屏切换保留、输出期间增量 feed | Core |
| `PreeditWidthTests` | 语料（CJK、emoji ZWJ、组合符、韩文字母组合）在 2027 开 / 关下：预测列宽 = 写入核心后光标位移 | Core |
| `AutomationTests` | `Value` 内容与长度上限、`IScrollProvider` 百分比、播报节流与回显去除、超过 20 行摘要 | Headless |
| 截图测试 | 预编辑、选区、搜索高亮、链接悬停（05 §13 T15–T17、T21） | Headless |

UI 级测试只在进程内调用控制器或使用 Avalonia Headless 的输入 API，不使用操作系统级的合成输入或屏幕区域截图。

### 12.2 必须手工验收的部分

| 区域 | 清单 |
|---|---|
| IME（macOS） | 简体拼音、繁体注音、日文罗马字、韩文 2-Set：预编辑位置随光标；候选框跟随；提交后列宽不跳；Esc 取消；⌘K 等快捷键在组合中不触发；预编辑在行尾时左移 |
| IME（Windows） | 微软拼音、微软日文 IME、韩文 IME：同上；`ImeProcessed` 不产生多余字符 |
| IME（Linux） | fcitx5 + 拼音、ibus + 拼音：同上 |
| 死键 | Windows US-International、macOS ABC Extended、French：`'`+`e` → `é`；死键 + 空格 → 原字符 |
| AltGr | Windows 德语（AltGr+Q = `@`、AltGr+7 = `{`）、波兰语程序员布局；`altGrAliasing=false` 后 Ctrl+Alt+Q 发给程序 |
| Option | `macOptionAsMeta` 四个取值；⌥B 在 none 时输入 `∫`、left 时左 ⌥ 发 `ESC b` |
| kitty 键盘 | nvim（`CSI >3u`）、Claude Code 的 Shift+Enter、Codex；⌘C 拦截后程序不收到释放事件 |
| win32-input-mode | PowerShell 7 + PSReadLine：Ctrl+Space 补全、Shift+Enter、Ctrl+Backspace；Far Manager；cmd 下中文输入 |
| 鼠标 | htop / mc / vim `mouse=a` 的点击、拖动、滚轮；Shift 绕过；SGR-pixels（`kitten show-key` / 自写探针）；HiDPI 坐标 |
| 选区与剪贴板 | 三平台双击 / 三击 / 矩形；Windows 右键复制 / 粘贴；Linux 中键粘贴 PRIMARY（进程内） |
| 粘贴 | 多行确认、粘贴为单行、含 `ESC[201~` 的内容；大段粘贴（10 MB）不卡 UI |
| 拖放 | Finder / Explorer / Nautilus 拖入含空格、引号、中文的路径到 zsh、bash、fish、pwsh、cmd、Git Bash、WSL |
| 链接 | URL、`file:line:col` 在工作台打开到行；OSC 8 真实地址确认 |
| 屏幕阅读器 | Windows NVDA 与讲述人、macOS VoiceOver、Linux Orca：新输出播报（不重复读回显）、可访问缓冲视图逐行浏览与命令跳转、`IScrollProvider` 滚动 |
| 键盘陷阱 | 只用键盘能离开终端（命令面板、全局键） |

---

## 发现的架构问题 / 待决问题（供修订 01）

> 2026-10-17：以下各项均已并入 01-architecture.md（第 6 项的通道见 01 §3.2 规则 6 与 04 §11）。

1. **9001 不在 libghostty-vt 的模式表里**（§3.7）：D9 要求 v1 实现 win32-input-mode，而核心无法跟踪 `CSI ? 9001 h`；需要在 `native/ghostty` overlay 补丁里登记该模式（或在 actor 里另做字节流嗅探）。
2. **输入编码不能用 `FrameSnapshot.Modes`**（§3.4）：01 §5.2 允许"UI 线程直接用快照里的模式编码"，但快照在 mode 2026 下最多扣 1 s、视图不可见时不生成。建议 actor 单独发布 `InputModes`。
3. **`ITerminalViewSession` 缺少请求方法**：发起 `Clear`（§2.5）与 `SetColors`（05 §11.1）的日志记录请求。
4. **UI 侧核心的 `write_pty` 不能完全为空**（§7.1）：`ghostty_terminal_paste` 通过它输出，actor 需要"粘贴捕获作用域"。
5. **kitty 粘贴事件（mode 5522）与双核心冲突**（§7.1）：授权记录在 UI 侧核心，程序的读取请求到达镜像核心。v1 关闭。
6. **OSC 52 读取的应答通道未定义**（§6.4）：镜像核心需要向 UI 请求剪贴板内容并等待用户确认，01 / 04 需要定义 host → UI 的请求与回传。
