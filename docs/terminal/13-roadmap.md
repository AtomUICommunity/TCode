# TCode 终端：路线图与验收

> 状态：设计稿 v1（2026-10-17）。本文把 01–12 中的实施拆分、spike 和待决问题汇总为一份按里程碑排列的计划。工期是**单人全职的粗略估计** [推断]，M0 完成后按实测修订。

## 1. 共同的完成标准

每个里程碑都要满足以下几条才算完成：

1. **AOT**：`dotnet publish -c Release -r <rid>`（Desktop 和 tcode-ptyhost）0 个 IL2xxx/IL3xxx 警告，并且实际运行过发布后的二进制（[12](12-build-and-ci.md)）。
2. **平台**：在 macOS arm64、Windows x64、Linux x64 三台机器上跑过本里程碑的测试，其余 RID 至少在 CI 中构建并加载成功。
3. **测试**：[11](11-testing-benchmarks.md) §5 中本里程碑对应的测试全部进入 CI。
4. **文档**：实现中偏离设计的地方，要回写到对应的设计文档里。

## 2. 依赖关系

```
M0 Spike ──▶ M1 核心 + PTY ──▶ M2 pty host ──▶ M3 视图 ──▶ M4 集成 ──▶ M5 Agent
                                                   │                      │
                                                   └──▶ M6 R1 + 图形 ◀─────┘
                                                                M4 ──▶ M7 SSH 一期
```

M3 的视图可以先接**回放连接**开发（[01](01-architecture.md) §6.5 的 `ITerminalViewSession`），不必等 M2 完成；所以 M2 与 M3 可以部分并行。

## 3. 里程碑

### M0 Spike（约 2 周）

目标：把所有"能不能做"的问题在写正式代码之前验证掉。

| 任务 | 文档 | 通过标准 |
|---|---|---|
| libghostty-vt 在 6 个 RID 上构建（Zig 0.16.0）、加载、通过一个回放用例；Windows x64 反汇编确认没有 AVX | [02](02-core-ghostty.md) S1、[12](12-build-and-ci.md) §4 | 6 个 RID 的 CI 全绿 |
| overlay O1–O4 能打上并通过上游测试；O1 后 `ModePacked` 的位宽与快照编解码 | 02 §2.4、S13 | 补丁可应用，快照往返通过 |
| 绑定生成器原型（ClangSharp）；按值传结构体的 ABI 往返；`terminal_get_multi` 行为 | 02 S2、S3、S15 | 6 个 RID 往返测试通过 |
| libtcodepty + helper 在 4 个 Unix RID 上构建；`tcpty_spawn` 往返；ConPTY 往返（DA1 握手） | [03](03-pty.md) | 三个平台上 `echo` 往返成功 |
| macOS login shell：argv0 `-zsh` 直接启动（不经 login），断言退出码和 PATH | 03 §8.3、07 §3.2 | `exit 3` 得到退出码 3；Homebrew 路径在 PATH 中 |
| `Control.Render` 画出一个静态网格（含宽字符、box drawing） | [05](05-rendering.md) | 截图测试通过 |
| AOT 二进制中运行 `vim` 并退出 | 11 §5 | 人工 + 冒烟 |
| 建立 CI（TCode 目前没有 CI；需要先解决 StreamMark 本地源的问题） | 12 §7 | `ci.yml` 跑通 |

#### M0 实施状态（2026-10-17）

| 任务 | 状态 | 说明 |
|---|---|---|
| libghostty-vt 构建 | **完成（本机）**：osx-arm64 构建并通过全部测试；linux-x64、win-arm64 从 macOS 交叉编译成功；6 个 RID 待 CI 验证 | `tools/terminal/build-ghostty.sh`；Zig 0.16.0 由 `tools/terminal/install-zig.sh` 按 `native/toolchain.json` 安装并校验 SHA-256。linux-x64 剥离后约 2.2 MB，glibc 最高 2.27。**win-arm64 开启 SIMD 也能编译**（12 号文档的担忧未出现，运行时待验证） |
| overlay O1–O4 | **完成** | `native/ghostty/overlays/0001`（O1 9001、O2 停用 5522）、`0002`（O3 延后应答 + 终止符、O4 modifyOtherKeys）；macOS 的导出列表追加 `_tcode_ghostty_*`。C 检查（`native/ghostty/test/overlay_check.c`）13/13，C# 测试覆盖同样的行为；O1 后快照往返保留 9001（S13） |
| 绑定 | **M0 手写子集**（约 40 个函数），生成器（S3）推迟到 M1 | `TCode.Terminal.Core/Interop`；结构体布局用 `ghostty_type_json()` 清单逐字段校验。注意 `DisableRuntimeMarshalling` 下 `Marshal.SizeOf/OffsetOf` 测的是封送布局，不能用于校验 |
| Unix PTY | **完成**：libtcodepty（spawn、管道模式 spawn、gather 读、wait、resize、退出监视、信号、进程信息）+ helper（PTY / `--exec` / `--exec-cwd` / `--detach`） | `native/tcodepty/build.sh`；macOS clang，Linux `zig cc`（glibc 2.28、helper musl 静态）；`-Werror` 通过。C 冒烟测试 9/9；C# 测试 11/11（控制终端、cwd、像素 resize、退出码、信号、SIGPIPE 复位、前台进程、挂断关闭、vim） |
| Windows ConPTY | **代码完成，未运行**（本机无 Windows） | `WindowsPtyConnection`：绝对路径加载随包 conpty.dll，否则回退 kernel32；读线程先于 CreateProcess；Close 在独立线程；每会话 Job。待 CI 验证；随包 conpty.dll / OpenConsole.exe 的打包放到 M1 |
| macOS login 退出码 | **取消**（2026-10-17 决定不经 login） | — |
| `Control.Render` 静态网格 | **完成** | `TCode.Terminal.Avalonia.TerminalView`（R3）：背景合并、按格 GlyphRun（x 对齐网格）、逐码点回退字体、四种光标；截图 `terminal-static-grid.png` 已人工检查（颜色、粗体、truecolor、CJK 宽字符、box drawing、光标）。`CaptureFrame` 暂为逐格读取，增量算法在 M1 |
| AOT 运行 vim | **完成（osx-arm64）** | `tcode-ptyhost --self-test`：AOT 发布 0 个 IL 警告，二进制 1.8 MB；自检覆盖核心解析、O1、快照往返、PTY 往返与退出码、vim 进入备用屏并 `:q!` 退出 |
| CI | **工作流已写好，未推送**（`.github/workflows/terminal.yml`，actionlint 通过） | 只构建终端项目，不依赖 StreamMark。全仓库 CI 需要 StreamMark 子模块指向包含当前 `StreamMarkVersion` 的已推送提交 |
| StreamMark 子模块 | **完成** | `external/StreamMark`（074f7ff）；`nuget.config` 改为 `packages/streammark/`；`tools/streammark/pack.sh [源码目录] [版本]` |

M0 中发现、需要后续处理的：

- Zig 0.16 的 HTTP 客户端经本机 HTTP 代理时报 `HttpConnectionClosing`；`tools/terminal/prefetch-zig-deps.sh` 用 curl / git 预取依赖（CI 不需要）。
- 快照要求开启续传跟踪（`CONTINUATION_MAX_BYTES`），否则在转义序列中途编码会返回 `INVALID_VALUE`；`TerminalCoreOptions.ContinuationMaxBytes` 默认 8 MiB，恢复时同时设置解码器的 `RETAIN_CONTINUATION`。
- libghostty 的版本字符串是 libghostty-vt 自己的 `0.1.0-dev`，不是 Ghostty 应用版本；`ghosttyBuildId` 依靠库文件 SHA-256 区分构建。
- 空屏时 formatter 返回空指针（已处理）。

### M1 核心与 PTY（约 4–5 周）

| 任务 | 文档 |
|---|---|
| `TCode.Terminal.Core`：151 个函数的绑定、门面 API、`FrameSnapshot` 复制算法、`InputModes`、键盘 / 鼠标 / 粘贴编码、选区、搜索、formatter、CommandTracker | 02 |
| 差分测试（增量 vs 全量）；核对 RoyalTerminal 修过的问题中在 HEAD 仍存在的 6 个 | 02 §15 |
| esctest2 驱动进 CI；黄金样本回放；fuzz 子集 | 11 |
| `TCode.Terminal.Pty`：Unix（spawn、gather 读线程、写线程、退出监视、关闭语义、前台进程、cwd、管道模式启动）与 Windows（ConPTY、Job、宽度标志、W1–W12 对策） | 03 |
| PTY 压力测试（每晚）：`cat` 1 GB、50 个并发会话、快速开关、resize 风暴、`yes \| head` | 03 §14 |

验收：Tier 0 全部通过（[10](10-protocol-compat.md) §7.1）；ASCII 解析吞吐达到实测修订后的目标。

#### M1 实施状态（2026-10-17）

| 阶段 | 状态 | 内容 |
|---|---|---|
| A | **完成** | 绑定生成器（`tools/terminal/ghostty_bindgen.py`，类型取自 `ghostty_type_json()`，函数取自头文件）；增量 `CaptureFrame`（只重建脏行、按内容哈希复用行对象）；差分测试；overlay O5（漏标脏行） |
| B | **完成** | `ITerminalEffects`（剪贴板、通知、进度、标题、cwd…）；核心应答的查询（DA1/DA2/XTVERSION/尺寸/配色）；OSC 133/633 `CommandTracker` |
| C | **完成** | `InputModes`；按键 / 鼠标 / 焦点编码；粘贴安全检查与 bracketed paste |
| D | **完成** | 选区（范围、矩形、词、行、全部、命令输出）与选区文本；视口滚动（顶 / 底 / 行 / 指定行 / 按提示符）；搜索（`TerminalSearch`，视口高亮在捕获时换算）；OSC 8 超链接解析进帧（`FrameSnapshot.Links` + `ResolvedStyle.HyperlinkId`，同一地址跨帧 id 不变，上限 65535）。帧新增 `Selection` / `Search` / `Links`，仅选区或搜索变化也会产生新帧 |
| E | 待做 | 随包 ConPTY（conpty.dll + OpenConsole.exe）；Toolhelp32 前台进程；PTY 压力测试 |
| F | 待做 | esctest2 驱动与黄金样本回放进 CI |

阶段 D 的实测：
- 行选区（`select_line`）止于最后一个非空格列，不是整行宽度；视图按选区区间绘制即可，不要自行补齐。
- 选区与搜索匹配都是未跟踪的 grid ref，只在下一次终端变更前有效，所以视口区间在 `CaptureFrame` 内换算，不跨线程传递 grid ref。
- 超链接 id 是捕获器内部编号：两个捕获器（例如差分测试的增量 / 全量）对同一地址可能给出不同 id，比较时按地址比较。

### M2 pty host（约 3–4 周）

按 [04](04-pty-host.md) §17 的 7 步：协议与客户端 → host 核心（监听、认证、actor、日志、流控）→ attach 快照与多客户端 → 发现、启动、心跳、宽限期 → 持久化与 revive → 受限令牌与 `command/*` 接口 → 升级并存。外加 04 §18.1 采纳的补充（BaseEnvironment、集成等级、`setPersist` 等）。

验收：Desktop 被杀后重启，标签和内容完全恢复；host 崩溃后 Desktop 显示"会话已丢失"并能在原目录重开。

### M3 视图（约 4–5 周）

| 任务 | 文档 |
|---|---|
| `TerminalPainter`、R3 宿主、行缓存、cell 度量、字体回退、彩色 emoji、sprite、下划线样式、光标、选区、搜索高亮、最小对比度 | [05](05-rendering.md) |
| 按键路由、KeyDown / TextInput 分工、9001、IME、鼠标上报、选区手势、剪贴板、粘贴确认、链接、搜索栏 | [06](06-input.md) |
| 可访问性 v1：AutomationPeer、播报、可访问缓冲视图 | 06 §10 |
| 27 个截图用例 | 05 §13 |

验收：截图测试通过；基准 S1、S3、S4、S5 达标；输入延迟自动测量部分达标。

### M4 集成（约 3 周）

| 任务 | 文档 |
|---|---|
| 工作台：`TerminalTabViewModel`、多标签、分屏树、关闭确认、标题、退出后的行为 | [01](01-architecture.md) §7、[07](07-shell-profiles-config.md) §6 |
| `MainWindow` 快捷键路由（D6）；命令面板 ⌘⇧P / Ctrl+Shift+P | 06 §2 |
| profile 探测（三个平台）、启动命令、环境变量策略 | 07 §2–§4 |
| shell 集成脚本（bash、zsh、fish、pwsh；改编 VS Code 的 MIT 脚本，不拷贝 GPL 脚本）、注入与失败提示 | 07 §5 |
| `settings.json` 的 `terminal` 节、Schema、热重载、设置页 | 07 §7、§8 |
| 主题映射（AtomUI → `Terminal.*`）；链接打开到工作台 | 05 §11、06 §8 |
| 更新 `TerminalDescription` 文案和 [architecture.md](../architecture.md) 路线图 | 00 |

验收：[11](11-testing-benchmarks.md) §1.6 的"与系统一致"手工清单在三个平台上完成。

### M5 Agent（约 3 周）

按 [09](09-agent-integration.md) §17 的 8 步：命令跟踪器 → TerminalBroker 与 BashTool 双路径（含子进程环境清理）→ 交互提示检测与中断 → Agent 终端标签 → 迷你终端与 Codex 输出修复 → 共享权限 → ACP `auth.terminal` / `terminal/*`（默认关）→ 测试与 AOT 冒烟。

验收：09 §16 的端到端测试（Fake 模型）全部通过。

### M6 R1 + Kitty 图形（约 3 周）

| 任务 | 文档 |
|---|---|
| 按 05 §4 的 7 步从 R3 迁到 R1（保留 R2 作开关）；research/04 §11 的 spike | 05 |
| Kitty 图形：托管 PNG 解码器、内存上限、绘制与 z 序；重连时图像丢失的提示 | 02 §11、05 |

验收：S12（终端洪流 + StreamMark 流式输出）时终端帧率不受 UI 线程负载影响；Kitty 图形截图测试通过。

### M7 SSH 一期（约 1–2 周）

按 [08](08-ssh.md) 第一期：主机列表（`~/.ssh/config` 的 Host / Include，按需 `ssh -G`）、ssh profile、Windows `ssh.exe` 探测、`SendEnv`、连接失败（255）的提示。

验收：在 CI 的 OpenSSH 容器中通过 ProxyJump 连接；手工验证 FIDO 密钥和 keyboard-interactive（由系统 ssh 在终端里提示）。

**合计**：约 23–29 周（单人）[推断]。M2 与 M3 并行时可以缩短 3–4 周。

## 4. 之后（v1 之外）

| 项 | 说明 |
|---|---|
| SSH 二期 | Tmds.Ssh 的 SFTP 与远程文件树；先向上游提 PR 补 3 个公开 API 缺口（[08](08-ssh.md)） |
| 远程 shell 集成 | kitty 式 tty bootstrap 或 Ghostty 式脚本上传，默认关 |
| 完整无障碍 | 向 Avalonia 贡献跨平台 `ITextProvider`，或者写平台专用的 peer |
| Linux 系统 PRIMARY 选区 | 等 Avalonia 12.2 |
| 升级 .NET 11 | 用 `UnixProcessStartArguments` 回调替换自研的退出监视（[research/02](research/02-pty.md) §2.5） |
| 托管核心 | 只在 libghostty 路线遇到无法绕过的问题时，适配 XTerm.NET 实现同一门面 |

## 5. 待决问题汇总

设计阶段留下的问题。1–5 已于 2026-10-17 决定；6–10 仍待决，每项都写了建议。

| # | 问题 | 建议 | 何时必须决定 |
|---|---|---|---|
| 1 | macOS login 包装会吞掉 shell 退出码 | **已决定（2026-10-17）：放弃 login 包装**，改用 argv0 `-zsh` | — |
| 2 | DA2 应答 | **已决定：`>1;10;0c`**（与 Ghostty 一致；vim 宽屏鼠标需 `ttymouse=sgr`，见 10 §2） | — |
| 3 | 原生库编译模式 | **已决定：ReleaseFast**（与 Ghostty 一致）；fuzz 与差分测试用 ReleaseSafe 构建 | — |
| 4 | 异常断开超过宽限期后，正在运行程序的会话 | **已决定：保留**，直到程序结束或达到 `keepAliveMaxHours` | — |
| 5 | `terminal` 设置节的容错方式 | **已决定：字段级容错**（错误字段回退默认值并警告）；M4 时在 settings.md 写明这一节的例外 | — |
| 6 | Agent 只读查询（`ls`、`git status` 一类）在 `auto` 模式下走进程模式还是可见终端（[09](09-agent-integration.md) §18） | 走进程模式，减少刷屏 | M5 |
| 7 | Agent 命令超时的默认行为：中断还是转入后台（09 §18） | 中断，与现有 bash 工具一致 | M5 |
| 8 | 是否向 Tmds.Ssh 上游提 PR 补 API 缺口 | 是，在 SSH 二期开始前 | SSH 二期 |
| 9 | 每晚基准是否需要固定硬件的自托管 runner | 需要；没有的话基准只作参考，不作门禁 | M1 |
| 10 | 是否允许开发者录制一次真实的 Claude Code / Codex 会话作为测试夹具入库（CI 中不调用模型） | 允许，录制前清理掉密钥和私有内容 | M3 |

## 6. 与终端无关、可单独处理的现有问题

| 问题 | 位置 | 处理 |
|---|---|---|
| Codex 命令输出只显示最后一个片段 | `CodexSession.cs:427` 把单个片段当作进度结果，`SessionViewModel.cs:353` 又把每次进度当作完整结果 | 进度改传累计的 `command.Output`；随时可修 |
| BashTool 子进程继承 `SIGPIPE=SIG_IGN`（`trap - PIPE` 对 bash 无效） | `BashTool.cs` | 改用 `tcode-pty-spawn --exec` 启动（[03](03-pty.md) §12），随 M1 完成 |
| `PeerDirectory` 创建 socket 目录时没有设为 0700，`/tmp` 回退目录不检查所有者 | `src/TCode.AgentHost/Peers/PeerDirectory.cs` | 单独修复 |
| 打包脚本的 macOS 最低版本写的是 11.0，.NET 10 要求 15.0 | `tools/package_macos_app.sh` | 单独修复 |
| Linux AOT 发布需要在 glibc 2.28 容器中进行，否则在 RHEL 8 上运行不了 | 发布流程 | 随 CI 建设一起处理（12） |
