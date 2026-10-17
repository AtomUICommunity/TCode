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
| E | **完成** | 随包 ConPTY：`native/ConPty.targets`（不用包自带 targets，见 12 §5.5），CI 检查布局、自检断言 Bundled；Windows 前台进程：`ProcessTree`（Toolhelp32 + `GetProcessTimes` 防 pid 复用，排除 conhost / OpenConsole），纯函数部分跨平台测试；PTY 压力测试 T10–T14（`PtyStressTests`，默认 CI 规模，`TCODE_PTY_STRESS=1` 按文档规模，每晚运行） |
| F | **完成** | `.grid` 快照格式（`TCode.Terminal.Core.Testing.GridSnapshot`）与 asciicast 读取（`Cast`）；黄金样本：xterm.js 76 个 + Alacritty 45 个（`tools/terminal/fixtures/convert-fixtures.py`），每个夹具三项断言（上游参照文本、`.grid` 回归锁定、分块不变性）；fuzz：上游 AFL++ 语料 PR 子集 694 个 + 结构化生成器 20 种子 × 1 MiB，不变量含增量 = 全量、宽字符成对、快照往返；esctest2 驱动 `tools/terminal/EscTestRunner`（一致性测试模式 `TerminalCoreOptions.Conformance`），CI 作业以 known-failures 为门禁 |

阶段 D 的实测：
- 行选区（`select_line`）止于最后一个非空格列，不是整行宽度；视图按选区区间绘制即可，不要自行补齐。
- 选区与搜索匹配都是未跟踪的 grid ref，只在下一次终端变更前有效，所以视口区间在 `CaptureFrame` 内换算，不跨线程传递 grid ref。
- 超链接 id 是捕获器内部编号：两个捕获器（例如差分测试的增量 / 全量）对同一地址可能给出不同 id，比较时按地址比较。

阶段 E 的实测：
- macOS 内核在输出队列满、写端阻塞时会重复输出 ONLCR 插入的 `\r`（`y\r\r\n`），不经本库的 Python 慢读者同样复现；显示上无差别。T10 单独计数这类字节，其余任何偏差都算失败。
- 背压：消费方暂停 2 s 后积压约 263–266 KiB（上限 256 KiB + 一次 64 KiB 读取）。
- 吞吐：1 GiB 约 23–34 MiB/s（macOS arm64），与 Python 直接读 PTY 相同，见 03 §14 T11。
- fd 基线要在首个会话之后取：退出监视器、线程池等进程级单例在首次使用时创建，不算泄漏。

阶段 F 的实测：
- **参照文本**：121 个夹具中 96 个与上游参照（xterm / Alacritty 的实际输出）完全一致；25 个登记在 `Fixtures/known-differences.txt`。其中 8 个是 Alacritty 参照本身与 xterm 不同（不支持 DECSCA、忽略 DECCOLM）；其余是 libghostty 与 xterm 的差异，最集中的一类是**带多个参数的光标移动**（`CSI 3;5 A`：xterm 取第一个参数，libghostty 忽略整个序列，6 个夹具），其次是未实现的 VPB、SR/SL、DECIC/DECDC。
- xterm.js 的参照经由 tty 生成，转换时必须按 ONLCR 把 `\n` 换成 `\r\n`；Alacritty 的 `grid.json` 是环形缓冲（`inner[0]` 为最底行），制表符本身存在起始格。
- **快照往返**（fuzz 不变量）发现 libghostty 快照不保存两项状态：mode 2026（同步输出，临时状态，可接受）与直接用 `CSI ? 12 h/l` 设置的光标闪烁（经 DECSCUSR 设置的会保存）。后者影响 pty host 重连后的光标闪烁，列为 overlay 候选（§5 第 11 项）。
- **esctest2**（macOS arm64，与 Ghostty 同一 esctest 提交）：387 通过、42 个 xterm 已知缺陷、138 失败；通过率 387 / 525 = 73.7%。失败中 44 个是查询没有应答（OSC 5 特殊颜色、若干 DECDSR），其余为应答值或屏幕内容不同；分类见 `EscTest/known-failures.txt`。11 §1.1.4 的"Tier 0/1 相关文件通过率 ≥ 95%"尚未按 Tier 拆分统计，留到 M3 与 10 §7 的 Tier 表对照后再评估。
- esctest 偶发整轮失败（启动时第一个应答丢失，之后全部超时），驱动会整轮重跑一次；新增失败会单独重跑，单独能通过的只记为不稳定。

### M2 pty host（约 3–4 周）

按 [04](04-pty-host.md) §17 的 7 步：协议与客户端 → host 核心（监听、认证、actor、日志、流控）→ attach 快照与多客户端 → 发现、启动、心跳、宽限期 → 持久化与 revive → 受限令牌与 `command/*` 接口 → 升级并存。外加 04 §18.1 采纳的补充（BaseEnvironment、集成等级、`setPersist` 等）。

验收：Desktop 被杀后重启，标签和内容完全恢复；host 崩溃后 Desktop 显示"会话已丢失"并能在原目录重开。

#### M2 实施状态（2026-10-17）

| 步骤 | 状态 | 内容 |
|---|---|---|
| 1 协议与客户端 | **完成** | `TCode.Terminal.Protocol`：12 字节帧头、`Envelope`（`Utf8JsonReader` 扫描，不建 JsonDocument）、源生成 `PtyHostJsonContext`、HMAC 握手、`PtyConnection`（读 / 写线程、控制优先 + 数据轮转、Ping）、`PtyHostClient` / `PtySessionHandle` / `IPtySessionSink` |
| 2 host 核心 | **完成** | `PtyHostServer`（监听、对端 uid 校验、认证超时、按方法分派）、会话 actor（命令与输出块同一队列）、`OutputLog`（64 KiB 记录的环、二分查找）、流控（256 / 64 KiB，4 块在途，慢订阅者 10 s 后 resync） |
| 3 attach | **完成** | 原子 attach（取 S、编码快照、登记游标）、`resumeFrom`、VT 格式、多客户端、尺寸所有者（最近获得焦点者） |
| 4 发现与启动 | **完成** | `PtyHostPaths` / `PtyHostLocator`（生命周期锁判活、start.lock 串行化、READY 行）、Unix 经 `tcode-pty-spawn --detach`、Windows `CreateProcessW` + breakaway（含 Job 回退）、`HostLifetime`（宽限期只计清醒时间、到期保留有程序在运行的会话、空 host 60 s 退出）、SIGTERM / SIGHUP、`HostLog` |
| 5 持久化 | **完成** | `PersistenceStore`（0700 / 0600、原子写入、meta 最后写）、裁剪快照 + `tail.vt` 回退、定时保存只写有变化的会话、`onQuit` 三种模式、SIGTERM 1.5 s 内保存、revive 推入回滚 + 分隔文字 + 干净屏幕 |
| 6 受限令牌 | **部分** | `auth/mintToken` / `revoke`、按权限列拒绝、agent 只能访问 agent 会话与共享会话、shell 环境去掉 `TCODE_PTYHOST_*`。`command/*` 的语义属于 09，随 M5 实现；AgentHost 的环境变量注入属于 Desktop 集成（M4） |
| 7 升级并存 | **完成（基础）** | 客户端的 ghosttyBuildId 与 host 不同 → 连接标为外来构建：attach 强制 VT 格式、create 返回 `HostDraining` 并使 host 进入排空、最后一个会话关闭后退出。主版本不同时的引导子集流程由 Desktop 实现（M4） |

验证：`TCode.Terminal.Protocol.Tests` 11 个、`TCode.Terminal.Host.Tests` 28 个（进程内 host + 真实客户端 + 真实 PTY；发现类用例启动真实 host 进程，CI 中另用 AOT 发布的 host 跑一遍）；AOT 发布 0 个 IL 警告。

尚未做（列入 M3 / M4 或待验证）：
- Windows 命名管道仍用 .NET 的 `CurrentUserOnly`，04 §2.1 要求的 `PIPE_REJECT_REMOTE_CLIENTS` 与 `FILE_FLAG_FIRST_PIPE_INSTANCE`（自己调 `CreateNamedPipeW`）未做；客户端侧的服务端 SID 校验依赖 `CurrentUserOnly`。
- Windows 关机时的隐藏窗口（`WM_ENDSESSION`，§12.3）未做；当前只靠定时保存兜底。
- `session/foregroundChanged` 轮询、`session/inputOverflow`（8 MiB 输入上限）、环在空闲 60 s 后缩小、按回滚大小预估编码耗时（18.1 第 10 条）未做。
- profile 探测与 shell 集成注入（`profiles/list`、`shellIntegration`）属于 `TCode.Terminal.Shell`（M4）；当前 `session/create` 只支持显式命令与系统默认 shell。

M2 中的实测与设计调整：
- **受限令牌的握手**：serverProof 原本只用完整令牌计算，持受限令牌的 AgentHost 无法验证 host。改为 hello 带上非秘密的 `tokenId`，host 用对应令牌计算 serverProof、认证时只验证这一个令牌；未知 tokenId 用随机密钥作答，不泄露哪些 id 存在（04 §2.4 已同步）。
- **create 带 attach 的顺序**：会话 id 在应答前未知，客户端在读线程上收到应答时（`IPtyConnectionHandler.OnResponse`）同步登记 sink 并调用 `OnAttached`；host 在应答排队之后才把订阅挂到写线程上，所以快照块不会先于应答。
- **裁剪粒度**：libghostty 的回滚上限按页生效，`scrollbackLines = 100` 实际保留约 330 行（≥ N，符合 §10.3）。
- **宽限期到期后**：保留下来的会话在程序结束后才关闭，host 每 2 s 复查一次；全部关闭后以 `orphanTimeout` 退出。

### M3 视图（约 4–5 周）

| 任务 | 文档 |
|---|---|
| `TerminalPainter`、R3 宿主、行缓存、cell 度量、字体回退、彩色 emoji、sprite、下划线样式、光标、选区、搜索高亮、最小对比度 | [05](05-rendering.md) |
| 按键路由、KeyDown / TextInput 分工、9001、IME、鼠标上报、选区手势、剪贴板、粘贴确认、链接、搜索栏 | [06](06-input.md) |
| 可访问性 v1：AutomationPeer、播报、可访问缓冲视图 | 06 §10 |
| 27 个截图用例 | 05 §13 |

验收：截图测试通过；基准 S1、S3、S4、S5 达标；输入延迟自动测量部分达标。

#### M3 实施状态（2026-10-17，首轮对接）

本轮把终端接进了桌面端的工作台标签（"终端"工具每次新建一个会话，在项目目录启动）。

| 项 | 状态 | 说明 |
|---|---|---|
| 桌面端接入 | **完成** | `TCode.Desktop` 引用 `TCode.Terminal.Avalonia` / `Protocol`；pty host 按 AgentHost 的方式复制到 `ptyhost/`（开发构建）并 AOT 发布到 `ptyhost/`（`PublishPtyHost`）。`PtyHostService` 负责发现 / 启动 / 配置当前构建的 host |
| 会话管道 | **完成** | `TerminalSession`：UI 侧核心只在一个工作线程上使用（写入、记录、选区、滚动、捕获帧都经同一队列）；帧在 UI 线程上合并为最新一帧；每批处理完 Ack；resize 只在 Record 到达后生效（01 §3.2） |
| 标签与生命周期 | **完成** | `TerminalTabViewModel`：启动、退出后按 Enter 重开、host 断开后在相同目录重开、关闭时有程序运行则保留标签并提示。`Workbench` 的终端标签不再是占位 |
| 键盘 | **完成（基础）** | `TerminalView` 获得焦点并转发 KeyDown / KeyUp / TextInput；物理键映射（`TerminalKeys`，有单测）；编码交给 `InputEncoder`，模式取自工作线程的缓存。⌘C / Ctrl+Shift+C 复制（有选区时），⌘V / Ctrl+Shift+V 粘贴，⌘K 清屏（macOS） |
| 粘贴 | **完成** | bracketed paste 编码（2004）；包含换行等不安全内容先进入确认栏（[TerminalTabView](../../src/TCode.Desktop/Views/TerminalTabView.axaml)），确认后以 allowUnsafe 发送，取消则丢弃 |
| 指针 | **完成（基础）** | 单击清除选区，拖动选区，双击选词，三击选行；程序开启鼠标模式时编码鼠标报告（Shift 绕过）；滚轮：鼠标模式上报、备用屏转方向键（1007）、否则滚动回滚 |
| 链接 | **完成（基础）** | ⌘/Ctrl+点击打开 OSC 8 链接，仅 http / https / mailto |
| 剪贴板写入 | **完成** | OSC 52 写入转到系统剪贴板（按 `oscWrite` 策略） |
| 剪贴板读取 | **未做** | `ui/clipboardRead` 直接拒绝（需要询问界面） |
| 输入法（IME） | **完成（基础）** | `TerminalView` 提供 `TextInputMethodClient`：光标矩形（候选窗口位置）、preedit 支持；平台推送的组合文字画在光标处、带下划线，提交后经 TextInput 发送，失去焦点时中止组合。验证：组合文字的绘制与清除、宽字符对齐的 headless 快照；真实系统输入法未在本机验证 |
| 搜索栏 | **完成** | ⌘F（macOS）/ Ctrl+Shift+F 打开，浮在终端右上角；搜索词变化即时搜索（UI 侧核心，不经 host）；"当前/总数"；Enter / Shift+Enter 或箭头切换；Esc 关闭并清除高亮。高亮来自帧里的 `SearchHighlights`。验证：计数与切换的集成测试（真实会话）、关闭后高亮清除、headless 截图 |
| 关闭运行中的会话 | **完成** | 有程序在运行时 host 不关闭，标签显示确认栏，确认后强制关闭并移除标签 |
| 验证 | **完成（部分）** | 键映射单测（23 项）；会话管道集成测试 6 项（真实 host + 真实 PTY）；视图 headless 快照（状态层、颜色、宽字符、反色）；AOT 发布 0 个 IL 警告，冒烟 `TCode --smoke` 在发布二进制中启动终端并看到 shell 输出 |

本轮发现并修掉的问题：
- **helper 拒绝只有 argv0 的启动**（`tcode-pty-spawn` 的参数个数检查为 `argc < 5`，默认 shell 不带参数时被拒，errno 22）。冒烟在 AOT 二进制中发现，已修并加回归测试。
- **host 的应答与事件竞态**：会话瞬间退出时，退出通知会排在 create 的应答之前发出，客户端丢弃了它。host 改为应答排队之后才启动输出泵，协议上保证"应答先于该会话的任何数据 / 事件"。

尚未做（M3 剩余，按优先级）：
- 剪贴板读取确认（OSC 52 read，需要询问界面）；
- 可访问性（AutomationPeer）；
- 通知、铃声、进度的界面呈现（事件已到达，未显示）；
- 装饰（下划线样式、超链接悬停）、彩色 emoji、box drawing 的 sprite、字形行缓存；
- 27 个截图用例中目前只有 2 个（05 §13）；
- 外观设置（字体、字号、调色板随明暗切换）与 `terminal` 设置节（M4）。

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
| 11 | M1 回放与 fuzz 发现的 libghostty 与 xterm 的差异是否用 overlay 修正：①带多个参数的光标移动（CUU/CUD/CUF/CUB/HPR/VPR）取第一个参数；②快照保存 mode 12；③DEC 特殊图形字符集的 `_` 显示为空白 | ①②做 overlay（改动小，①直接影响"与系统终端一致"），同时向上游提议题；③先向上游确认再定 | M3 |

## 6. 与终端无关、可单独处理的现有问题

| 问题 | 位置 | 处理 |
|---|---|---|
| Codex 命令输出只显示最后一个片段 | `CodexSession.cs:427` 把单个片段当作进度结果，`SessionViewModel.cs:353` 又把每次进度当作完整结果 | 进度改传累计的 `command.Output`；随时可修 |
| BashTool 子进程继承 `SIGPIPE=SIG_IGN`（`trap - PIPE` 对 bash 无效） | `BashTool.cs` | 改用 `tcode-pty-spawn --exec` 启动（[03](03-pty.md) §12），随 M1 完成 |
| `PeerDirectory` 创建 socket 目录时没有设为 0700，`/tmp` 回退目录不检查所有者 | `src/TCode.AgentHost/Peers/PeerDirectory.cs` | 单独修复 |
| 打包脚本的 macOS 最低版本写的是 11.0，.NET 10 要求 15.0 | `tools/package_macos_app.sh` | 单独修复 |
| Linux AOT 发布需要在 glibc 2.28 容器中进行，否则在 RHEL 8 上运行不了 | 发布流程 | 随 CI 建设一起处理（12） |
