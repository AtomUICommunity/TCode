# TKCode 终端：总体架构

> 状态：设计稿 v1（2026-10-16）。依据见 [README.md](README.md) 的决策表和 [research/](research/)。本文定义各层的职责、进程划分、线程模型和层间接口。各子系统的实现细节见 02–13 号文档。

## 1. 目标与约束

| 类别 | 要求 |
|---|---|
| 完整性 | 完整的终端模拟器：vim、tmux、htop、less、fzf、neovim、Claude Code、Codex 等 TUI 都能正常工作；Tier 0 和 Tier 1 协议全部支持（[10-protocol-compat.md](10-protocol-compat.md)） |
| 系统一致 | 默认 shell、login 方式、环境变量、键鼠约定、字体、光标、响铃都跟随所在平台的系统终端；所有偏离系统的行为都可以配置（[07-shell-profiles-config.md](07-shell-profiles-config.md)） |
| 性能 | 输入回显路径上不加任何延迟；ASCII 解析目标 ≥ 500 MB/s（libghostty-vt 的 SIMD 构建。这是目标值，调研中只有相对数据，没有绝对出处，M0 实测后修订；Windows 可能只能用标量构建，即 win-x64 的 AVX 兼容问题和 win-arm64 在 Zig 0.16 下开启 SIMD 无法编译，目标另按实测设定，见 [12](12-build-and-ci.md)）；渲染每帧 p99 ≤ 2 ms；终端洪流不能拖慢 Agent UI；空闲时 CPU ≈ 0（[11-testing-benchmarks.md](11-testing-benchmarks.md)） |
| NativeAOT | 所有新增项目都要求 `IsAotCompatible=true`，发布时 0 个 IL2xxx/IL3xxx 警告；只用 `LibraryImport` + `DisableRuntimeMarshalling`，不用反射；JSON 只用源生成 |
| 平台 | win / linux / osx × x64 / arm64 共 6 个 RID；Windows 最低 10 1809（17763）；macOS 最低 15.0（.NET 10 的下限）；Linux 支持 RHEL 8（内核 4.18、glibc 2.28），因此 Linux 的 AOT 发布必须在 glibc 2.28 的容器中进行（这一点同样适用于现有的 Desktop 和 AgentHost） |
| 会话保活 | UI 崩溃、重载、升级后，终端会话仍在运行，可以重新连接，屏幕和回滚内容完整 |
| Agent | Agent 可以在用户看得见的终端里运行命令、读取命令输出；ACP 登录可以在内嵌终端中完成 |
| SSH | 第一阶段：交互终端在本地 PTY 里运行系统 `ssh`；第二阶段：用 Tmds.Ssh 支持 SFTP 和远程文件树 |

## 2. 已确定的决策

| # | 决策 | 来源 |
|---|---|---|
| D1 | 终端核心使用 **libghostty-vt**（Ghostty 的 C API），自己维护 C# 绑定，pin 上游 commit，CI 构建 6 个 RID，**不做托管保底核心**。C# 门面接口与 libghostty 解耦，保留以后更换核心的可能 | 用户 |
| D2 | 组织形式：**TKCode 仓库内的独立项目** `src/TKCode.Terminal.*`。终端库不依赖 TKCode 的其他项目，以后可以拆成独立仓库 | 用户 |
| D3 | **一开始就做独立的 pty host 进程**（`tkcode-ptyhost`）。终端会话不放在 UI 进程，也不放在 AgentHost | 用户 |
| D4 | SSH 采用混合方案：交互终端用系统 `ssh`（默认不加 `-t`，只在有远程命令时加，以尊重用户的 `RequestTTY` 配置）；以后 SFTP 用 Tmds.Ssh | 用户 |
| D5 | 渲染：先用 `Control.Render`（R3）做出可用版本，再迁到 `CompositionCustomVisualHandler` 在渲染线程绘制（R1） | 用户 |
| D6 | 终端获得焦点时按系统终端惯例分配快捷键：macOS 上 ⌘ 组合键归 TKCode，⌘K 清屏；Windows / Linux 上 Ctrl+字母交给 shell，TKCode 用 Ctrl+Shift。命令面板增加 ⌘⇧P / Ctrl+Shift+P | 用户 |
| D7 | shell 集成默认自动注入，可以关闭，注入失败时显示原因 | 用户 |
| D8 | v1 范围：多标签与分屏、会话保活与恢复、Agent 与终端打通、Kitty 图形协议。**不做** sixel | 用户 |
| D9 | macOS 的 login shell 用 argv0 `-zsh` 形式直接启动，**不经 `/usr/bin/login`**（login 父进程总是 `exit(0)`，会吞掉 shell 的退出码；2026-10-17 用户决定放弃 login 包装，代价是没有 utmp 记录和 "Last login"）；Unicode 宽度在 macOS / Linux 上默认兼容模式，程序开启 2027 后切换到字形簇模式；**Windows 上固定为字形簇模式**（ConPTY 的宽度在创建时就固定，不响应 2027）；Windows 随应用分发 `conpty.dll` + `OpenConsole.exe`，v1 实现 win32-input-mode；`TERM=xterm-256color`，不发布自定义 terminfo | 采纳调研推荐（可推翻） |

## 3. 进程划分

```
┌──────────────────── TKCode.Desktop（UI 进程） ────────────────────┐
│ TerminalView × N（每个视图一个 UI 侧核心：libghostty-vt）           │
│ 标签 / 分屏 · 快捷键路由 · 设置 · 链接 → 工作台                      │
│        │ PtyHostClient（控制面 + 数据面，同一条 IPC 连接）           │
└────────┼─────────────────────────────────────────────────────────┘
         │ Unix domain socket / Windows named pipe（仅当前用户、令牌认证）
┌────────┴──────────── tkcode-ptyhost（独立 AOT 进程） ───────────────┐
│ SessionManager                                                    │
│  └ Session × N                                                    │
│     ├ ITerminalConnection（本地 PTY：Unix spawn helper / ConPTY）  │
│     ├ 镜像核心：无头 libghostty-vt（权威状态、查询应答、快照）       │
│     ├ 输出日志：带序号的字节流 + 订阅者（每个订阅者独立 ack 流控）   │
│     └ 命令跟踪：OSC 133 边界、退出码、cwd                           │
│ Persistence：退出时写快照，下次启动恢复内容（revive）               │
└────────┬─────────────────────────────────────────────────────────┘
         │ 同一套 IPC 协议（第二个客户端）
┌────────┴──────────── TKCode.AgentHost ─────────────────────────────┐
│ bash 工具（可见终端模式）· ACP auth.terminal / terminal/* · 读取命令输出 │
└──────────────────────────────────────────────────────────────────┘
```

### 3.1 为什么核心状态有两份（UI 侧核心 + 镜像核心）

pty host 里的每个会话都有一个**无头镜像核心**，它是该会话的**权威状态**。UI 进程里的每个 `TerminalView` 再有一个自己的核心，只用来渲染和处理交互。两者接收完全相同的字节流，按相同的顺序处理，所以状态相同。

| 职责 | 由谁负责 | 原因 |
|---|---|---|
| 查询应答（DA、CPR、DECRQM、XTVERSION、OSC 10/11 颜色查询、2048 尺寸报告…） | **镜像核心**；UI 侧核心的 `write_pty` 回调被丢弃 | 程序启动时就会查询（ConPTY 启动时会发 DA1 并等待应答），这时可能还没有任何 UI 连接；如果两份核心都应答，程序会收到重复的应答 |
| 重连时恢复屏幕和回滚 | 镜像核心的 `ghostty_snapshot_encode` | 快照包含解析到一半的输入，可以从字节流的任意位置精确续传 |
| Agent 读取屏幕、命令输出 | 镜像核心（formatter、OSC 133 语义区、`select_output`） | Agent 读取时 UI 不一定开着 |
| 退出前保存内容（revive） | 镜像核心 | 同上 |
| 渲染、选区、搜索、视口滚动、按键 / 鼠标 / 焦点编码、IME 列宽预测 | **UI 侧核心** | 这些都是高频交互，不能每次都跨进程往返 |

代价：每个字节解析两次（分别在两个进程里并行进行），回滚内存也占两份。libghostty-vt 的 SIMD 解析很快（目标 500 MB/s 量级，待 M0 实测），镜像核心可以更积极地压缩回滚（[02-core-ghostty.md](02-core-ghostty.md) §7）。VS Code 的 ptyHost 也采用同样的结构（headless xterm + 渲染端 xterm）。

**备选方案及不选的原因**：只在 pty host 里解析，把渲染增量（脏行）发给 UI（WezTerm mux 的做法）。这样只解析一遍，但选区、搜索、按键编码都要跨进程往返；而且 libghostty 的 render state 只能在持有终端的进程里使用，UI 侧就得自己实现一套行缓存。

### 3.2 状态一致性规则

两份核心要保持一致，以下规则必须遵守：

1. **只能通过日志改变状态。** 会改变终端状态的操作只有以下几种：PTY 输出、resize、reset、`SetColors`、`Clear`（⌘K 清屏），以及 host 自己追加的输出（例如 revive 恢复的内容和分隔文字）。它们都由 pty host 写入**同一条带序号的输出日志**，客户端按日志顺序执行，不能在本地抢先执行（记录类型的完整定义见 [04](04-pty-host.md)）。
   - 例：UI 请求 resize → host 执行 `TIOCSWINSZ` / `ResizePseudoConsole`，同时在日志里追加一条 `Resize` 记录 → 镜像核心和所有客户端都在这条记录处执行 resize。UI 发出请求到收到这条记录之间大约 1 ms，这段时间里视图按旧尺寸显示（Ghostty 本身也会把 resize 合并 25 ms）。
2. **影响状态的配置两边一致**：初始尺寸、像素尺寸、回滚上限、宽度模式、调色板默认值、Kitty 图形开关和上限。这些都由 host 在 `session/attach` 的应答中下发，UI 侧核心按此创建。
3. **两边使用同一个 libghostty-vt 构建**：pty host 和 Desktop 一起发布，加载同一个 RID 目录下的同一个原生库。`build_info` 中没有哈希，所以握手时校验 `ghosttyBuildId` = 版本字符串 + 库文件的 SHA-256。例外：升级后仍在运行的旧 host（keepAlive 会话）构建不同，这时改用 VT 格式 attach（由旧 host 的镜像核心用 formatter 输出 VT 序列重建屏幕），可能出现少量显示偏差（[04](04-pty-host.md) §1.5）。
4. 只影响 UI 的选项，例如视口位置、选区、搜索，只在 UI 侧核心修改，不需要同步。
5. **调色板**：OSC 4/10/11 的查询应答依赖调色板，所以主题切换时 UI 先通知 host（`session/setColors`），host 在日志中追加 `SetColors` 记录，两边在同一位置更新默认颜色。
6. **需要用户参与的应答由 host 转发给 UI**：OSC 52 读剪贴板由镜像核心触发，host 向该会话的主视图发 `clipboard/readRequest`（附请求 id），UI 询问用户后回 `clipboard/readResponse`，host 再写回 PTY。没有 UI 连接时一律拒绝。libghostty 在回调返回时如果没有应答，会自动回一个空剪贴板，所以"以后补发"依赖 overlay **O3**（[02](02-core-ghostty.md)）；O3 打不上时退回到回调内同步等待（有超时）。OSC 52 写同样只由镜像核心触发。
7. **依赖 UI 侧状态的协议在 v1 关闭**：Kitty 粘贴事件（mode 5522）的授权记录在 UI 侧核心，程序的读取请求却到达镜像核心，两者无法对应，所以 v1 不启用。
8. **v1 需要四个小 overlay 补丁**（[02](02-core-ghostty.md)，打在 pin 住的上游 commit 上，升级时逐个检查能否去掉）：
   - O1 登记 win32-input-mode（9001），libghostty 的模式表里没有它，未知 CSI 也不进回调；
   - O2 停用 Kitty 粘贴事件 5522（安装了剪贴板读取回调后，库会把 5522 报告为已识别）；
   - O3 OSC 52 读取允许延后应答；
   - O4 导出 modifyOtherKeys 的状态，供 `InputModes` 使用。
   任一补丁打不上时的退路见 02 §17。
9. **已知限制**：快照不包含 Kitty 图像，重新连接后已显示的图像会丢失；Windows 的宽度"锁定"只是把 2027 的默认值设为置位，程序仍可以复位它。

### 3.3 进程生命周期

| 事件 | 行为 |
|---|---|
| 首次打开终端（或 Agent 首次需要终端） | Desktop 按需启动 `tkcode-ptyhost`（脱离 Desktop 的 Job / 进程组，Desktop 崩溃时 host 不受影响），通过 IPC 握手，并在 `host/configure` 中发送 Desktop 启动时的原始环境（BaseEnvironment）：host 是长期进程，它自己的环境会过期，新会话的环境以 Desktop 最近一次发送的为准。AgentHost 通过环境变量拿到连接地址和受限令牌；**这些变量必须从 AgentHost 启动的所有子进程（BashTool、MCP 服务器、外部 Agent CLI）的环境中剔除**，否则 Agent 执行的任何命令都能直接连上 pty host |
| UI 崩溃、重载、升级重启 | host 不受影响；新的 Desktop 找到正在运行的 host（运行时目录中的 `ptyhost-<build>.json`），重新连接，用快照恢复每个标签。无客户端连接的宽限期默认 **30 分钟**，超时后的处理见下一行 |
| 宽限期到期 | 没有前台程序在运行的会话按"退出 TKCode"处理；**有前台程序在运行的会话继续保留**，直到程序结束或达到 `keepAliveMaxHours`（2026-10-17 用户决定） |
| 用户正常退出 TKCode | 有程序在运行时汇总确认一次（`keepAlive` 模式不结束进程，不需要确认）；然后按 `terminal.persistence.onQuit` 处理：<br>**`revive`（默认）**：每个会话写一份快照（裁剪到尾部 N 行，默认 1000 行；libghostty 快照格式不保证跨版本兼容，所以同时保存一份 `tail.vt` 作为回退）→ SIGHUP / 关闭 → host 退出；下次启动时恢复标签、cwd 和内容，并启动新 shell（iTerm2、VS Code 的做法）<br>`keepAlive`：host 保留所有会话继续运行，下次启动时直接重新连接（类似 tmux）<br>`close`：全部关闭，不保存 |
| host 崩溃 | Desktop 检测到断开 → 所有终端标签显示"会话已丢失"，并提供"在相同目录重新打开"（使用最近一次的 revive 快照，host 每 30 s 增量保存一次）。Desktop 不会自动重启正在运行的程序 |
| 系统关机、注销 | 与正常退出相同，但不弹确认 |

pty host 按"用户 + `TKCODE_HOME` + 构建"区分：正常情况下只有一个 host，多个 Desktop 窗口（以后会有）共用它；升级后新旧两个构建的 host 可能短暂并存，旧 host 在其会话全部结束后退出（[04](04-pty-host.md) §1.5）。Agent 创建的会话默认不持久化。

## 4. 项目划分

```
src/
  TKCode.Terminal.Core/          终端核心门面（无 UI）：TerminalCore、RenderState 读取、键盘/鼠标/粘贴编码、
                                选区、搜索、快照、formatter；Interop/Ghostty（LibraryImport 原始声明）；
                                托管 PNG 解码器（供 Kitty 图形使用）；runtimes/<rid>/native/libghostty-vt.*
  TKCode.Terminal.Pty/           ITerminalConnection 与本地 PTY：Unix（libtkcodepty + tkcode-pty-spawn）、
                                Windows（ConPTY，随包 conpty.dll + OpenConsole.exe）；进程信息查询
  TKCode.Terminal.Shell/         profile 探测（各平台）、启动命令构造（login、argv0）、环境变量策略、
                                shell 集成脚本（嵌入资源）与注入、ssh 主机列表
  TKCode.Terminal.Protocol/      pty host IPC 协议：帧格式、控制消息（源生成 JSON）、流控；PtyHostClient
  TKCode.Terminal.Host/          tkcode-ptyhost 可执行项目：SessionManager、镜像核心、输出日志、命令跟踪、
                                持久化、Agent 命令执行器
  TKCode.Terminal.Avalonia/      TerminalView 控件：Painter、行缓存、字体与 sprite、输入 / IME / 鼠标、
                                选区手势、搜索栏、可访问性；只依赖 Avalonia（不依赖 AtomUI）；
                                资源键 Terminal.*（颜色、字体）
  TKCode.Desktop/                集成：TerminalTabViewModel、分屏、快捷键路由、settings 的 terminal 节、
                                AtomUI 主题 → Terminal.* 映射、链接打开、Agent 终端展示
native/
  ghostty/                      上游 pin（ghostty-upstream.json）、构建脚本、overlay 补丁（如有）
  tkcodepty/                     libtkcodepty（约 690 行 C）与 tkcode-pty-spawn（约 85 行 C，PTY / --exec / --detach 三种模式）
tools/terminal/                 原生库构建脚本、EscTestRunner（esctest2 驱动）、夹具录制工具
tests/
  TKCode.Terminal.Core.Tests/    回放 → 网格断言、增量 vs 全量差分、esctest2 驱动、fuzz 回归
  TKCode.Terminal.Pty.Tests/     真实 PTY 往返、resize、退出码、SIGPIPE、关闭语义、ConPTY 握手
  TKCode.Terminal.Shell.Tests/   profile 探测（假文件系统 / 注册表）、启动命令构造、环境变量策略、shell 集成注入（CI 中真实启动各 shell）
  TKCode.Terminal.Host.Tests/    协议、流控、重连快照、持久化、多客户端
  TKCode.Terminal.Avalonia.Tests/ 视图单元测试：按键编码表、路由、选区手势、行缓存
  TKCode.Desktop.Tests/          TerminalView 截图测试（Headless + Skia）
  TKCode.Terminal.Benchmarks/    BenchmarkDotNet：解析吞吐、快照成本、渲染帧时间
```

依赖方向（单向）：

```
TKCode.Desktop ──▶ TKCode.Terminal.Avalonia ──▶ TKCode.Terminal.Core
      │                                              ▲
      ├──▶ TKCode.Terminal.Protocol ◀── TKCode.Terminal.Host ──▶ TKCode.Terminal.Pty
      │                                      │                      
TKCode.AgentHost ──▶ TKCode.Terminal.Protocol  └──▶ TKCode.Terminal.Shell ──▶ TKCode.Terminal.Pty
```

- `TKCode.Terminal.*` 不引用任何 `TKCode.*` 非终端项目，也不引用 AtomUI。TKCode 的设置、主题、本地化通过 Desktop 的适配层注入。
- `TKCode.Terminal.Protocol` 只依赖 BCL，Desktop 和 AgentHost 都引用它。
- 只有 `TKCode.Terminal.Avalonia` 依赖 Avalonia。

## 5. 线程模型

### 5.1 pty host 中的一个会话

| 线程 | 职责 |
|---|---|
| 读线程（专用 `Thread`） | 从 PTY 读取。Unix：非阻塞 fd + `poll`，循环读到 EAGAIN、凑满 64 KiB 或用完 3 ms（gather，弥补 macOS 每次只能读约 1 KiB 的限制）。Windows：阻塞 `ReadFile`。读到的数据块交给会话 actor |
| 会话 actor（专用 `Thread`，或每个会话一个串行队列） | 独占镜像核心：`vt_write` → 产生的应答直接排进写队列；把数据块追加到输出日志并唤醒订阅者；处理命令（resize、写入、kill、快照、Agent 查询） |
| 写线程（专用 `Thread`） | 从 channel 取数据写入 PTY；遇到 EAGAIN 就 poll `POLLOUT`；大段粘贴按 4 KiB 分块 |
| 退出监视（进程级一个线程） | Linux：pidfd + epoll（内核 < 5.3 时，每个会话一个阻塞的 `waitpid(pid)` 线程）；macOS：kqueue `EVFILT_PROC`；Windows：`RegisterWaitForSingleObject` |
| IPC 写线程（每个客户端一个） | 控制帧优先，数据帧按订阅者 ack 流控发送 |

**背压**：读线程使用 4 块 × 64 KiB 的缓冲池。镜像核心解析完一个数据块并把它追加到日志后，这块缓冲才归还。日志对每个订阅者都有未确认上限（§6.4），任何订阅者超过上限时，该会话暂停读取 PTY，从而反压子进程。例外：没有活跃 UI 订阅者时（UI 已断开），只受镜像核心的速度限制，日志只保留最近的环形窗口。

### 5.2 UI 进程中的一个 TerminalView

| 线程 | 职责 |
|---|---|
| IPC 读线程（每个 host 连接一个） | 解帧，把数据帧分派到对应视图的核心 actor 队列 |
| 视图核心 actor（专用 `Thread`） | 独占 UI 侧核心：`vt_write`（`write_pty` 回调为空）→ 按需执行 `render_state_update` → 只复制脏行，生成 `FrameSnapshot` → 发布（`Volatile` 交换 + Generation 加一）；处理 UI 命令（视口滚动、选区、搜索、按键编码），处理完每块数据后回 ack |
| UI 线程 | 布局、键盘 / IME / 鼠标、选区手势、AutomationPeer；R3 阶段每帧在 `Render` 中读取最新快照并绘制 |
| 渲染线程（R1 阶段，Avalonia 合成器，所有窗口共用） | `CompositionCustomVisualHandler` 读取最新快照并绘制；**从不等锁** |

要点：

- 按键路径：UI 线程 KeyDown → 用 actor 单独发布的 **`InputModes`** 编码（DECCKM、DECKPAM、Kitty flags、9001、鼠标模式等）→ 编码结果直接通过 IPC 写给 host。**输入路径上没有任何合并定时器。** `InputModes` 每处理完一个数据块就发布，不受 mode 2026 或视图不可见的影响；不能用 `FrameSnapshot.Modes`，因为 mode 2026 期间快照最多会被扣住 1 s，读到的模式可能已经过期。
- 视图不可见（标签在后台）时，视图核心仍然解析（保持状态最新），但不生成快照，也不请求帧。
- 光标闪烁由 UI 线程在相位边界唤醒一次渲染，不在渲染线程逐帧计时，以保证空闲 CPU ≈ 0。
- mode 2026 期间不发布快照，超时 1 s。

## 6. 层间接口

以下是各层之间的约定。签名是设计稿，实现时可以调整名称，但职责划分不变。

### 6.1 核心门面（TKCode.Terminal.Core）

```csharp
namespace TKCode.Terminal.Core;

/// 一个终端状态机。非线程安全：只能由其所属的 actor 线程调用。
public sealed class TerminalCore : IDisposable
{
    public static TerminalCore Create(in TerminalCoreOptions options, ITerminalEffects effects);

    // —— 改变状态（只能按输出日志的顺序调用）——
    public void Write(ReadOnlySpan<byte> data);
    public void Resize(TerminalSize size);                 // 见下方 TerminalSize
    public void SetDefaultColors(in TerminalPalette palette);
    public void Reset();

    // —— 查询 / 交互（只在 UI 侧使用）——
    public void ScrollViewport(ViewportScroll scroll);     // 按行、按页、到顶 / 底、到上 / 下一个 prompt
    public bool CaptureFrame(FrameSnapshotBuilder builder);// render state 更新 + 只复制脏行；无变化时返回 false
    public SelectionHandle Select(SelectionRequest request);   // 两点、词、行、全部、命令输出
    public string? GetSelectionText(SelectionHandle selection, TextFormat format);
    public TerminalSearch CreateSearch(string query, SearchOptions options);

    // —— 输入编码（读取当前模式，不改变状态）——
    public int EncodeKey(in KeyInput key, Span<byte> destination);
    public int EncodeMouse(in MouseInput mouse, Span<byte> destination);
    public int EncodeFocus(bool focused, Span<byte> destination);
    public PasteResult EncodePaste(ReadOnlySpan<char> text, bool allowUnsafe, IBufferWriter<byte> destination);

    // —— 镜像侧 ——
    public void EncodeSnapshot(IBufferWriter<byte> destination, SnapshotOptions options);
    public static TerminalCore Restore(ReadOnlySequence<byte> snapshot, in TerminalCoreOptions options, ITerminalEffects effects);
    public string Format(FormatRequest request);           // 纯文本 / VT / HTML；整屏、回滚、某条命令的输出
    public IReadOnlyList<CommandRecord> Commands { get; }  // OSC 133 解析出的命令边界（A/B/C/D 与退出码）
    public void CompressIdle(TimeSpan budget);
}

/// 副作用回调。在调用 Write 的线程上同步触发，实现方不能阻塞，也不能重入 TerminalCore。
public interface ITerminalEffects
{
    void WriteToPty(ReadOnlySpan<byte> reply);             // 镜像核心：写回 PTY。UI 侧核心：丢弃查询应答，
                                                           // 但 EncodePaste 期间开启捕获作用域，收集粘贴编码结果
    void Bell();
    void TitleChanged(string title);
    void WorkingDirectoryChanged(string uri);              // OSC 7 等，不可信输入
    void ClipboardWrite(ClipboardTarget target, ReadOnlySpan<byte> data);
    ClipboardReadResult ClipboardRead(ClipboardTarget target);  // 默认拒绝；需要询问时返回"待定"，以后补发应答
    void Notification(string? title, string body);         // OSC 9 / 777
    void Progress(ProgressState state, int? percent);      // OSC 9;4
    void SemanticPrompt(in SemanticPromptEvent e);         // OSC 133
    void UnknownOsc(int command, ReadOnlySpan<byte> payload);   // libghostty 不解析的 OSC，例如 633（命令行与 nonce，用于 Agent 命令跟踪）
    void RenderHold(bool hold);                            // mode 2026
}
```

快照格式（UI 侧 actor 产出，渲染方只读）：

```csharp
public sealed class FrameSnapshot
{
    public long Generation;
    public TerminalSize Size;
    public RowSnapshot[] Rows;           // 未变化的行与上一帧共享同一对象
    public CursorState Cursor;           // 位置、形状、可见、闪烁、是否在宽字符上
    public TerminalPalette Palette;      // 已处理 reverse video（DECSCNM）
    public TerminalModes Modes;          // DECCKM、DECKPAM、2004、1004、鼠标跟踪与编码、Kitty flags、9001……
    public SelectionSpans? Selection;
    public ScrollbarState Scrollbar;     // 总行数、视口偏移、视口行数
    public ImagePlacements? Images;      // Kitty 图形的放置位置（像素矩形 + 图片 id + 版本）
    public SearchHighlights? Search;     // 搜索匹配与当前匹配
    public HyperlinkTable? Links;        // 超链接 id → URI（OSC 8）
    public RowSnapshot[]? Overscan;      // 视口上下的额外行（平滑滚动）
    public MouseShape PointerShape;      // OSC 22
}

public sealed class RowSnapshot
{
    public ulong RowId; public int Version;   // RowId 来自 libghostty 的行 ID，跨滚动不变
    public ulong[] Cells;                    // 我们自己的 8 字节 cell 格式（不直接暴露 GhosttyCell 位布局）
    public ResolvedStyle[] Styles;           // 本行去重后的样式：fg / bg / 下划线颜色与样式、属性、超链接 id
    public GraphemeTable? Graphemes;         // 稀疏：列 → 码点序列
    public RowFlags Flags;                   // 软换行、prompt 行、语义区
}
```

渲染层只依赖 `FrameSnapshot`，不依赖 libghostty 的任何类型。

`TerminalSize` 在所有层（核心、连接、IPC）中含义统一：

```csharp
/// 像素是**单个 cell** 的尺寸；整块区域像素（TIOCSWINSZ 的 ws_xpixel / ws_ypixel）= 列数 × CellWidth、行数 × CellHeight，由需要的一方计算。
public readonly record struct TerminalSize(int Columns, int Rows, int CellWidth, int CellHeight);
```

### 6.2 连接（TKCode.Terminal.Pty）

采用 [research/06](research/06-ssh-transport.md) §6 的接口草案：

```csharp
namespace TKCode.Terminal.Pty;

public interface ITerminalConnection : IAsyncDisposable
{
    string Kind { get; }                                 // "local-pty" | "replay" | 以后的 "ssh" ……
    ConnectionCapabilities Capabilities { get; }         // Resize、PixelSize、Signals、ForegroundProcess、WorkingDirectory
    PipeReader Output { get; }                           // 消费方 AdvanceTo 之前，读线程最多再读一个池的数据（背压）
    ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken ct = default);
    ValueTask ResizeAsync(TerminalSize size, CancellationToken ct = default);
    ValueTask<bool> SignalAsync(TerminalSignal signal, CancellationToken ct = default);  // HUP / INT / TERM / KILL
    Task<TerminalExit> Exited { get; }                   // 退出码或信号；未知时两者都为空
    ValueTask<ForegroundInfo?> GetForegroundAsync(CancellationToken ct = default);       // 前台进程名 / pid / 是否就是 shell
    ValueTask<string?> GetWorkingDirectoryAsync(CancellationToken ct = default);
}

public sealed record PtyLaunch(
    string File, IReadOnlyList<string> Arguments, string? Argv0,   // Argv0 = "-zsh" 这类 login 形式
    string? WindowsCommandLine,                                     // Windows：原样命令行（cmd.exe 等不按 CommandLineToArgvW 解析的程序）
    string? WorkingDirectory,
    IReadOnlyList<KeyValuePair<string, string>> Environment,        // 完整环境块（策略由 TKCode.Terminal.Shell 决定）
    TerminalSize InitialSize,
    PtyWidthMode WidthMode, bool AmbiguousWide);                    // Windows：映射为 ConPTY 的 GLYPH_WIDTH_* / AMBIGUOUS_IS_WIDE

// ITerminalConnection 另有：bool TryWriteReply(ReadOnlySpan<byte>)（镜像核心的应答，非阻塞，排在输入之后先进先出）、
// ValueTask ClearBufferAsync()（Windows：ClearPseudoConsole）、ValueTask CloseAsync(TimeSpan grace)（SIGHUP → 超时 SIGKILL / Job），
// 以及启动警告（例如 conpty.dll 降级为系统 conhost）。完整定义见 03。
// Exited 在"输出读到 EOF，或进程退出后输出静默超时"之后完成：后台进程仍持有 slave 时读不到 EOF。
```

### 6.3 Shell 与 profile（TKCode.Terminal.Shell）

```csharp
public interface IProfileProvider { IReadOnlyList<TerminalProfile> Detect(); }  // 每个平台一个实现

public sealed record TerminalProfile(
    string Id, string Name, string Command, IReadOnlyList<string> Arguments,
    bool LoginShell, ShellKind Kind,                       // Bash / Zsh / Fish / Pwsh / PowerShell / Cmd / Nu / Wsl / Ssh / Other
    string? Icon, ProfileSource Source);                   // Detected / Settings
// 完整字段（PathStyle、RawCommandLine、Environment、RequiresTrust、WslDistribution、Ssh 等）
// 与异步探测接口（vswhere、wsl.exe 需要异步）见 07 §1.2。

public static class LaunchBuilder
{
    /// profile + 设置 + 项目上下文 → PtyLaunch（包括 login shell 的 argv0、环境变量增删、shell 集成注入）
    public static LaunchPlan Build(TerminalProfile profile, LaunchContext context);
}
```

### 6.4 IPC 协议（TKCode.Terminal.Protocol）

完整规格见 [04-pty-host.md](04-pty-host.md)。要点：

- **传输**：
  - Unix：`$TKCODE_HOME/run/ptyhost-<build>.sock`，所在目录权限 0700（路径超出 `sun_path` 上限时回退，见 04）；
  - Windows：命名管道，ACL 只允许当前用户，并设置 `PIPE_REJECT_REMOTE_CLIENTS`；
  - 先校验对端 uid / SID，再做 **HMAC 双向挑战应答**（令牌保存在权限 0600 的 `ptyhost-<build>.json` 中，从不在连接上传输，防止管道抢注者拿到令牌），然后握手协议版本和 `ghosttyBuildId`。
- **帧**：`[u32 长度][u8 类型][u8 标志][u16 保留][u32 会话 id][负载]`。类型分为三种：
  - 控制请求 / 应答 / 通知：负载是源生成 JSON（与现有 RPC 风格一致）；
  - 数据输出 `Output`：负载是 `[u64 日志序号][原始字节]`；
  - 数据输入 `Input`：原始字节。
- **一条连接、两个优先级**：每个客户端的发送端有两个队列，控制帧优先；数据帧单帧不超过 64 KiB，所以控制消息最多被延迟一个数据帧。
- **流控（每个会话、每个订阅者独立）**：
  - host 记录"已发送未确认"的字节数，超过 **256 KiB** 就暂停这个订阅者；
  - 客户端的核心每处理完 **32 KiB** 回一次 `Ack(序号)`；
  - 所有订阅者都低于 **64 KiB** 时恢复。
  - 慢订阅者最多拖住会话 **10 s**；超时后这个订阅者降级，等它跟上后用快照重新同步（resync）。数据不会丢，因为镜像核心始终是完整的。Agent 订阅者用"按需读取"模式，不订阅原始字节，所以不参与流控（§9）。
- **重连**：`session/attach` 返回 `{快照, 快照对应的日志序号}`，之后的 `Output` 帧从这个序号开始，不重不漏。快照分两段发送：先发 READY 段（可以立即渲染），再增量发送更早的回滚。

### 6.5 UI 控件（TKCode.Terminal.Avalonia）

```csharp
public sealed class TerminalView : Control   // R3：重写 Render；R1：挂载 CompositionCustomVisual
{
    public ITerminalViewSession? Session { get; set; }       // 由宿主提供：输入输出走 IPC，或者在测试里直接接回放
    public TerminalViewOptions Options { get; set; }         // 字体、光标、选区、鼠标、粘贴、链接、可访问性……
    public event EventHandler<TerminalLinkActivatedEventArgs>? LinkActivated;   // URL、文件路径（path:line:col）、OSC 8
    public event EventHandler<TerminalKeyRoutingEventArgs>? KeyRouting;         // 宿主决定按键归 TKCode 还是归 shell
    public void Copy(); public void Paste(); public void Clear(); public void Find(); public void ScrollToPrompt(int delta);
}

/// 视图与宿主之间的会话：Desktop 用 PtyHostClient 实现，测试用回放实现。
public interface ITerminalViewSession
{
    TerminalCoreOptions CoreOptions { get; }
    void Start(ITerminalViewSink sink);                       // 宿主把输出日志中的数据 / resize / 颜色记录推给 sink
    void SendInput(ReadOnlySpan<byte> bytes);
    void RequestResize(TerminalSize size);
    void Ack(long sequence);
    void RequestClear();                                      // ⌘K：由 host 追加 Clear 记录
    void SetColors(in TerminalPalette palette);               // 主题切换：由 host 追加 SetColors 记录
    void AnswerClipboardRead(long requestId, ClipboardReadResult result);   // 回应 host 转来的 OSC 52 读取请求
}
```

`TerminalView` 不知道 pty host 的存在，也不知道 TKCode 的设置格式。

## 7. 与 TKCode 的集成点

| 位置 | 改动 |
|---|---|
| [WorkbenchViewModel.cs](../../src/TKCode.Desktop/ViewModels/WorkbenchViewModel.cs) | 新增 `TerminalTabViewModel`。`AddTool(Terminal)` 改为每次都新建一个终端，不再按 Kind 去重；标签支持分屏（`TerminalGroupViewModel` 持有一棵分屏树） |
| [MainWindow.axaml.cs](../../src/TKCode.Desktop/Views/MainWindow.axaml.cs) `OnWindowKeyDown` | 焦点在终端里时，先询问终端的按键路由（D6）；命令面板新增 ⌘⇧P / Ctrl+Shift+P |
| `docs/settings.md` + `TKCode.Config` | 新增 `terminal` 节（[07](07-shell-profiles-config.md) §7），沿用 JSONC、宽容解析、热重载 |
| 主题 | `TKCode.Desktop` 把 AtomUI token 和 TKCode 调色板映射到 `Terminal.*` 资源键（与 StreamMark 的 `MarkdownResourceAlias` 做法相同） |
| 链接 | `LinkActivated` → 文件路径在工作台打开（含行号）；URL 交给系统打开；OSC 8 打开前确认真实地址 |
| AgentHost | 新增 `TerminalBroker`，连接 pty host，为 bash 工具的可见终端模式、ACP `auth.terminal` / `terminal/*` 提供服务（[09](09-agent-integration.md)） |
| ACP | `auth.terminal:true`；`terminal:true` 作为可配置能力（v2 草案删除了它，见 09） |
| 文案 | `TerminalDescription` 和 [architecture.md](../architecture.md) 的路线图改为本文的方案 |

## 8. 安全边界

| 边界 | 规则 |
|---|---|
| IPC | 仅当前用户：Unix socket 所在目录 0700，Windows 管道 ACL 只允许当前用户，再加令牌认证；拒绝远程客户端 |
| 程序 → 终端 | OSC 52 读剪贴板默认询问；标题回报默认关闭；OSC 8 链接打开前显示真实地址；OSC 7 cwd 当作不可信输入（只用于继承目录，并校验目录存在）；DECRQSS 应答不回显请求内容（[10](10-protocol-compat.md) §6） |
| 用户 → 程序 | 粘贴前检查 `ESC[201~` 和控制字符；多行粘贴警告（可配置） |
| Agent → 终端 | Agent 只能操作它自己创建的终端，或者用户明确共享给它的终端；在终端里执行命令前走 TKCode 的审批；Agent 读取用户终端的内容需要用户授权（[09](09-agent-integration.md) §11） |
| 持久化 | revive 快照可能包含敏感输出：文件权限 0600，可以关闭，保存的行数有上限 |
| 原生库 | conpty.dll 只按绝对路径加载；libghostty-vt 和 libtkcodepty 与应用一起签名（macOS 需要公证） |
| 许可证 | Ghostty、kitty 的 shell 集成脚本是 GPLv3，不能拷贝进 TKCode（Apache-2.0）；只能参考思路自写，或改编 VS Code 的 MIT 脚本（07 §5） |

## 9. Agent 使用终端的方式（概要）

完整设计见 [09-agent-integration.md](09-agent-integration.md)。

- AgentHost 是 pty host 的另一个客户端。它的 `TerminalBroker` 可以创建会话、写入、等待命令结束、读取命令输出、kill。
- **命令边界**：依靠 shell 集成的 OSC 133（A/B/C/D + 退出码）。镜像核心的 `Commands` 列表记录每条命令的输出区间，可以用 formatter 取出纯文本。
- **没有 shell 集成时**（注入失败、用户关闭、远程 shell）：退回"进程模式"，即现有的 `shell -c` 执行方式，或者用空闲检测 + 哨兵（VS Code 的 basic / none 策略）。
- **用户可见**：Agent 创建的终端在工作台中显示为"Agent 终端"标签（带 Agent 标记）。用户可以接管输入，例如命令卡在交互提示时。

## 10. 实施里程碑（概要）

详见 [13-roadmap.md](13-roadmap.md)。每个里程碑都要求：AOT 发布 0 个警告，并且运行过 AOT 二进制。

| 里程碑 | 内容 | 验收 |
|---|---|---|
| M0 Spike（1–2 周） | libghostty-vt 在 6 个 RID 上构建并加载；绑定最小子集；Unix spawn helper + ConPTY 往返；`Control.Render` 画出静态网格 | 6 个 RID 的 CI 全绿；AOT 二进制能运行 `vim` 并退出 |
| M1 核心与 PTY | 核心门面、差分测试、esctest2 进 CI；PTY 全功能（退出码、关闭语义、前台进程、cwd） | Tier 0 通过；PTY 压力测试通过 |
| M2 pty host | IPC 协议、流控、镜像核心、重连快照、持久化 | Desktop 被杀后重启，标签和内容完全恢复 |
| M3 视图 | R3 渲染、字体回退、sprite、光标、选区、搜索、IME、鼠标、可访问性 v1 | 截图测试；输入延迟与吞吐达到目标 |
| M4 集成 | 标签与分屏、快捷键路由、设置、主题、链接、shell 集成脚本、profile 探测 | 三个平台上完成"与系统一致"的检查清单 |
| M5 Agent | bash 工具可见终端模式、ACP `auth.terminal` / `terminal/*`、命令输出读取 | Agent 端到端测试（Fake 模型） |
| M6 R1 + 图形 | 渲染线程绘制、Kitty 图形协议 | 与 Agent UI 同时运行时，终端帧率不受影响 |
| M7 SSH 一期 | 主机列表、ssh profile、TERM 降级 | 通过 ProxyJump、FIDO 密钥连接成功 |
