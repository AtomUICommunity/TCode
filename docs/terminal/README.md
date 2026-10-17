# TCode 终端组件

> 状态：设计文档 v1 完成；**M0 实施基本完成**（2026-10-17，见 [13-roadmap.md](13-roadmap.md) 的 M0 实施状态）。决策见 [01-architecture.md](01-architecture.md) §2；待你拍板的问题汇总在 [13-roadmap.md](13-roadmap.md) §5。

## 目录

| 文档 | 内容 |
|---|---|
| [00-current-state.md](00-current-state.md) | 仓库现状：只有占位标签页；现有 shell 代码与终端的关系；缺口 |
| [01-architecture.md](01-architecture.md) | **总体架构**：决策、进程划分（UI / pty host / AgentHost）、双核心一致性、项目划分、线程模型、层间接口 |
| [02-core-ghostty.md](02-core-ghostty.md) | 核心：libghostty-vt 的 pin、绑定、门面 API、快照、差分测试 |
| [03-pty.md](03-pty.md) | PTY：libtcodepty + spawn helper、ConPTY、退出监视、关闭语义 |
| [04-pty-host.md](04-pty-host.md) | pty host 进程与 IPC 协议：帧、控制消息、流控、重连、持久化 |
| [05-rendering.md](05-rendering.md) | 渲染：Painter、行缓存、字体与 sprite、R3 → R1 |
| [06-input.md](06-input.md) | 输入：按键路由、IME、鼠标、选区、剪贴板、粘贴、链接、搜索、可访问性 |
| [07-shell-profiles-config.md](07-shell-profiles-config.md) | profile 探测、启动命令、环境变量、shell 集成、`terminal` 设置 |
| [08-ssh.md](08-ssh.md) | SSH：一期系统 ssh，二期 Tmds.Ssh |
| [09-agent-integration.md](09-agent-integration.md) | Agent：可见终端中的 bash、ACP、Codex 输出展示 |
| [10-protocol-compat.md](10-protocol-compat.md) | 协议支持范围、应答、TERM、宽度、安全 |
| [11-testing-benchmarks.md](11-testing-benchmarks.md) | 测试分层与基准 |
| [12-build-and-ci.md](12-build-and-ci.md) | 构建文档：工具链、原生库、打包签名、CI |
| [13-roadmap.md](13-roadmap.md) | 里程碑与验收 |
| [research/01-emulator-cores.md](research/01-emulator-cores.md) | 终端模拟核心选型：libghostty-vt、XTerm.NET、alacritty_terminal、wezterm-term、libvterm、xterm.js、纯 C# 自研 |
| [research/02-pty.md](research/02-pty.md) | 跨平台 PTY：posix_spawn + helper、控制终端、与 .NET 的 SIGCHLD 共存、ConPTY 全集与陷阱、现有 .NET 库评估 |
| [research/03-vt-protocol.md](research/03-vt-protocol.md) | 协议规格：解析器状态机、Tier 0/1/2 功能矩阵、查询应答、键盘与鼠标编码、Unicode 宽度、TERM、安全、测试 |
| [research/04-rendering-performance.md](research/04-rendering-performance.md) | 缓冲区结构、Avalonia 12 绘制路径、字体字形、线程模型、IME 与可访问性、性能目标、基准计划 |
| [research/05-platform-behavior-config.md](research/05-platform-behavior-config.md) | 与系统终端一致：默认 shell、环境变量、shell 集成、键鼠约定、外观默认值、生命周期；`terminal` 配置草案 |
| [research/06-ssh-transport.md](research/06-ssh-transport.md) | SSH 接入方式、.NET SSH 库（含 AOT 实测）、传输抽象接口草案、进程架构、JSON-RPC 承载终端数据的实测代价 |

## 调研结论汇总

### 1. 分层

无论最终怎么选，组件都分成以下五层，层与层之间只通过接口交互：

```
┌──────────────────────── 宿主（TCode.Desktop） ────────────────────────┐
│ 工作台标签 / 分屏 · profile 与 settings.json · 快捷键路由 · 链接打开      │
└──────────────────────────────────┬────────────────────────────────────┘
┌──────────────── 视图层（Avalonia，自绘） ───────────────────────────────┐
│ TerminalView：网格渲染 · 光标 / 选区 / 搜索高亮 · 键盘与 IME · 鼠标 · 无障碍 │
└──────────────────────────────────┬──── 只读快照（脏行） ─────────────────┘
┌──────────────── 终端核心（与 UI 无关） ─────────────────────────────────┐
│ VT 解析 · 屏幕 / 回滚 / reflow · 模式与应答 · 键盘 / 鼠标 / 粘贴编码 · 搜索   │
└──────────────────────────────────┬──── 字节流 ─────────────────────────┘
┌──────────────── 连接（ITerminalConnection） ───────────────────────────┐
│ 本地 PTY（Unix posix_spawn + helper；Windows ConPTY） · SSH · WSL · 回放 │
└───────────────────────────────────────────────────────────────────────┘
```

### 2. 已经比较明确、不需要大的取舍的结论

| 主题 | 结论 | 来源 |
|---|---|---|
| Unix PTY | 不在托管代码里 fork。用 posix_spawn 加一个小 helper（macOS 必须用 helper 才能拿到控制终端），再用一个约 300 行的 C shim 承接 `ioctl` / `fcntl` 等可变参数函数（Apple arm64 上直接 P/Invoke 会静默传错参数） | 02 §2、§4 |
| 子进程回收 | 只能用 `waitpid(pid)` 回收自己的子进程，不能 `waitpid(-1)`；退出通知 Linux 用 pidfd，macOS 用 kqueue；子进程启动前把所有信号恢复为默认（.NET 会让子进程继承 `SIGPIPE=SIG_IGN`） | 02 §2.5 |
| Windows | ConPTY；新版会原样透传程序输出；关闭时必须继续读输出管道并在另一个线程关闭，否则旧系统上会死锁；ConPTY 启动时会发 DA1，我们必须回应 | 02 §3 |
| 默认 shell | 用 `getpwuid` 取账户登记的 shell，`$SHELL` 只作后备；macOS 起 login shell，Linux 起非 login shell；Windows 和 Windows Terminal 一样优先 pwsh。Agent 的 `ShellResolver` 不复用 | 05 §1 |
| TERM | `xterm-256color` + `COLORTERM=truecolor` + `TERM_PROGRAM=TCode`；v1 不发布自定义 terminfo，靠 XTVERSION / XTGETTCAP 等查询应答补充能力 | 03 §7、05 §2 |
| 解析器 | 以 Williams 的 DEC 状态机为骨架，加冒号子参数；UTF-8 下不执行 C1；参数和字符串长度设上限 | 03 §1 |
| 线程 | 每个终端一个读线程 + 一个核心线程；读缓冲满了就阻塞读取（背压）；渲染只读快照，从不等锁 | 04 §0.3 |
| 交互延迟 | 输入回显路径上不加任何合并延迟；大量输出时才合并到每帧一次 | 04 §0.3 |
| 传输数据 | 终端字节流不走现有 JSON-RPC（base64 + 无界队列 + 队头阻塞）；跨进程时使用独立的二进制通道和确认式流控 | 06 §7.3 |
| 安全 | OSC 52 读剪贴板要询问；标题回报默认关闭；粘贴时检查 `ESC[201~`；OSC 8 链接打开前显示真实地址 | 03 §8 |
| 测试 | 字节流回放 → 网格快照断言；esctest2 进 CI；vttest、ucs-detect 人工验收；fuzz；Headless 截图测试；BenchmarkDotNet 基准 | 03 §9、04 基准计划 |

### 3. 决策（2026-10-16 已确定）

| # | 问题 | 选项 | 调研推荐 → **结果** |
|---|---|---|---|
| 1 | 终端核心 | A 纯 C# 自研（约 5–7 人月）；A′ 基于 XTerm.NET（纯 C#，单人维护，无逐行脏标记）；B libghostty-vt + 自维护绑定（API 未冻结、Zig 工具链、要 pin commit）；E 以 B 为主，托管核心保底 | B → **libghostty-vt（用户选择）** |
| 2 | 组织方式 | 独立仓库（像 StreamMark：核心 + 视图 + PTY + 主题包，经 NuGet 引用）；或者 TCode 仓库内的独立项目 | 独立库 → **TCode 仓库内独立项目（用户选择）** |
| 3 | 终端会话所在进程 | v1 在 UI 进程（UI 退出 shell 就结束）；一开始就做独立的 pty host（可重连、可保活，工作量大一档）；AgentHost（不推荐：它会崩溃重启） | v1 在 UI 进程，核心不依赖 UI，以后可迁出 → **一开始就做独立 pty host（用户选择）** |
| 4 | SSH | A 系统 ssh 跑在本地 PTY；B 托管库（Tmds.Ssh 最接近系统行为，AOT 0 警告）；C 混合：交互用系统 ssh，SFTP 等用 Tmds.Ssh | C → **混合（用户选择）** |
| 5 | 渲染路径 | `Control.Render`（UI 线程，简单）；`CompositionCustomVisualHandler`（渲染线程，Agent 流式输出时不抢 UI 线程） | 先 Render 做 MVP，再迁到渲染线程 → **先 Render 再迁渲染线程（用户选择）** |
| 6 | 终端获得焦点时的快捷键 | macOS 的 ⌘K 是清屏还是命令面板；Windows / Linux 上的 Ctrl+K/N/B 交给 shell 还是 TCode | ⌘K 清屏，命令面板另配 ⌘⇧P；Win/Linux 上 Ctrl+字母交给 shell → **按系统终端惯例（用户选择）** |
| 7 | shell 集成 | 默认自动注入（VS Code、Ghostty）或默认关闭（Windows Terminal、iTerm2） | 默认开，可关，失败时显示原因 → **默认开（用户选择）** |
| 8 | macOS 启动方式 | 通过 `/usr/bin/login -flp` 启动（和 Terminal.app 完全一致）；或者只把 argv0 改成 `-zsh` | login → **argv0 `-zsh`，不经 login（2026-10-17 用户决定：login 会吞掉 shell 退出码）** |
| 9 | Unicode 宽度 | 默认兼容（按码点）+ 程序开启 2027 后按字形簇；或者默认按字形簇 | 默认兼容 → 默认兼容（采纳推荐） |
| 10 | Windows | 是否随应用分发 `conpty.dll` + `OpenConsole.exe`（每种架构约 1.2 MB）；win32-input-mode 是否 v1 必做 | 都分发；win32-input-mode 做 → 分发；做 win32-input-mode（采纳推荐） |
| 11 | v1 范围 | 图形协议（Kitty graphics / sixel）、多标签 / 分屏、会话恢复、Agent 在可见终端中运行命令、ACP `terminal:true` | 图形与 Agent 共享终端放到 v1 之后 → **全部进入 v1：多标签分屏、保活恢复、Agent 打通、Kitty 图形；不做 sixel（用户选择）** |

## 另外发现的问题

- `BashTool` 启动的子进程会继承 .NET 的 `SIGPIPE=SIG_IGN`，所以 `yes | head` 这类管道的表现和系统终端不同（02 §2.5）。这和终端无关，可以单独修。
- `TerminalDescription` 文案和 [architecture.md](../architecture.md) 的路线图仍然写着 Iciclecreek.Avalonia.Terminal，选型确定后要更新。

### 4. 设计阶段的后续决定（2026-10-17）

| 问题 | 决定 |
|---|---|
| macOS login 包装 | 放弃，改用 argv0 `-zsh`（login 父进程总是 `exit(0)`） |
| DA2 应答 | `>1;10;0c`（与 Ghostty 一致） |
| libghostty 编译模式 | ReleaseFast（与 Ghostty 一致） |
| 异常断开超过宽限期后仍有程序在运行的会话 | 保留，直到程序结束或达到 `keepAliveMaxHours` |
| `terminal` 设置的容错方式 | 字段级容错 |

其余待决问题见 [13-roadmap.md](13-roadmap.md) §5。
