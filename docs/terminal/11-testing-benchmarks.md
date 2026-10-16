# TCode 终端：测试与基准（11）

> 状态：设计稿 v1（2026-10-16）。上位文档：[01-architecture.md](01-architecture.md)（§1 数字目标、§4 项目划分、§10 里程碑）。依据：[research/03](research/03-vt-protocol.md) §9（测试计划）、[research/04](research/04-rendering-performance.md) §7.3、§9（数字目标、基准计划）、[research/01](research/01-emulator-cores.md) §4.1.5（libghostty 的一致性测试）、[research/02](research/02-pty.md) §5、§9（PTY 验证与压力测试）。CI 作业的布局与 YAML 见 [12-build-and-ci.md](12-build-and-ci.md)。Agent 集成的端到端用例见 [09-agent-integration.md](09-agent-integration.md) §16。
>
> 本文规定：测试分几层、每层测什么、用什么夹具和文件格式、在 xunit v3 中怎么组织；基准项目的结构、场景 S1–S15 的数据集与测量方法、JIT/AOT 两种工具链、CI 门禁、端到端指标的采集；数字目标表与对应的测量方法。

**标注约定**：**[已核实]** = 对照源码、文档或本机实测核对过；**[推断]** = 设计推理或经验值，需要实测。上游版本：Ghostty `b3dc3d7`（2026-10-15）。

---

## 0. 结论摘要

| 主题 | 结论 |
|---|---|
| 分层 | L1 核心（回放 → 网格快照、增量 vs 全量差分、应答、编码表、esctest2、fuzz 语料回放）；L2 PTY（真实 PTY 往返、退出码、关闭语义、ConPTY）；L3 pty host 协议（握手、流控、重连快照、持久化、多客户端）；L4 视图截图（Avalonia Headless + Skia）；L5 端到端 TUI（录制字节流 → 网格快照，真实 PTY + shell 冒烟）；L6 手工（vttest、ucs-detect、"与系统一致"清单、Typometer） |
| 夹具格式 | 字节流用 **asciicast v2**（`.cast`），加一个 TCode 扩展事件码 `"ob"` 承载非 UTF-8 的原始字节；网格快照用自定义的纯文本格式 **`.grid`**（文本 + 样式表 + 行内属性游程 + 光标 / 模式），跨平台逐字节一致 |
| esctest2 | 自写无头驱动（C#，`tools/terminal/EscTestRunner`）：真实 PTY 中运行 esctest2，输出经镜像核心处理并回写应答；开启 libghostty 的 DECRQCRA 校验和选项（`GHOSTTY_TERMINAL_OPT_XT_CHECKSUM_REPORT = 44`）。CI 中只报告、不阻断，失败项进 known-failures 列表，**新增失败**才阻断 |
| fuzz | **不用 SharpFuzz**：它对托管 IL 插桩，而解析器在原生库里，插桩覆盖不到；NativeAOT 也不适用。改为①回放上游 AFL++ 语料（`test/fuzz-libghostty/corpus/*`）经过我们的 C# 门面；②自写的确定性结构化随机生成器（固定种子进 PR，长跑进每晚）；③不变量检查 |
| 基准 | `tests/TCode.Terminal.Benchmarks`（BenchmarkDotNet）。数据集由 `tools/terminal/gen-bench-data` 确定性生成到 `artifacts/bench-data/`，仓库只提交清单和 SHA-256。JIT 与 NativeAOT 两种工具链都跑。PR 跑短作业并做"同机 A/B"比较；每晚跑完整的 S1–S15 |
| 端到端指标 | 进程内 `System.Diagnostics.Metrics`（`Meter`）计数，`TCODE_TERMINAL_METRICS=<file>` 时由 `MeterListener` 写 JSON Lines；需要 `dotnet-counters` 时另做打开 `EventSourceSupport` 的诊断构建（NativeAOT 默认关闭 EventSource [推断：需核对当前 .NET 10 默认值]） |
| 延迟 | 自动化部分：内部分段时间戳（KeyDown → 写 PTY → 回显解析 → 快照发布 → 绘制结束）。端到端：Typometer **仅人工**在开发机上测量，不进 CI，不由 Agent 执行（遵守项目"不合成输入、不截屏幕区域"的约定） |

---

## 1. 测试分层

| 层 | 项目 | 运行时机 | 平台 | 里程碑 |
|---|---|---|---|---|
| L1 核心 | `tests/TCode.Terminal.Core.Tests` | 每次 PR | 3 平台（linux-x64、osx-arm64、win-x64） | M0 起，M1 完整 |
| L1b esctest2 | `tools/terminal/EscTestRunner` | 每次 PR（报告）+ 每晚 | linux-x64 | M1 |
| L1c fuzz 长跑 | `tests/TCode.Terminal.Core.Tests`（`[Trait("Category","FuzzLong")]`） | 每晚 1 h | linux-x64 | M1 |
| L2 PTY | `tests/TCode.Terminal.Pty.Tests` | 每次 PR | 3 平台 + 每晚 linux-arm64、win-arm64 | M0 往返，M1 完整 |
| L3 pty host | `tests/TCode.Terminal.Host.Tests` | 每次 PR | 3 平台 | M2 |
| L4 视图截图 | `tests/TCode.Desktop.Tests`（`Terminal*SnapshotTests`）、`tests/TCode.Terminal.Avalonia.Tests`（控件级，不依赖 Desktop） | 每次 PR | 3 平台 | M3 |
| L5 TUI 回放 | `tests/TCode.Terminal.Core.Tests/Tui/` | 每次 PR | 3 平台 | M1（夹具逐步补齐到 M4） |
| L5b 真实 shell 端到端 | `tests/TCode.Terminal.Host.Tests/Shell/` | 每次 PR | 3 平台 | M4 |
| L6 手工 | 本文 §7 清单 | 每个发布版本 | 3 平台实机 | M4 起 |
| Agent 端到端 | 见 09 §16 | 每次 PR | 3 平台 | M5 |

> `TCode.Terminal.Avalonia.Tests` 是新增项目（01 §4 的测试列表中只有 `TCode.Desktop.Tests` 承担截图）。理由：`TCode.Terminal.Avalonia` 不依赖 AtomUI 与 TCode（01 §4），它的截图测试不应依赖启动整个 TCode App；Desktop 侧只测集成（主题映射、标签、分屏）。列入待决问题。

### 1.1 L1 核心

#### 1.1.1 回放 → 网格断言（黄金样本）

```
tests/TCode.Terminal.Core.Tests/
  Fixtures/
    xtermjs/            *.cast + *.grid      xterm.js escape_sequence_files 转换而来（MIT）
    alacritty/          *.cast + *.grid      Alacritty ref tests 转换而来（Apache-2.0 / MIT）
    ucs/                *.cast + *.grid      ucs-detect docs/ucs_example_files
    tui/                *.cast + *.grid      自录：vim、htop、tmux、less、fzf、Claude Code、Codex（§1.5）
    replies/            *.cast + *.replies   查询应答
    THIRD_PARTY.md                            来源、许可、转换脚本版本
  Replay/GoldenTests.cs
```

- 每个夹具一对文件：`.cast`（输入字节流，§2.1）、`.grid`（期望网格，§2.2）。
- 测试：读 `.cast` → 按事件依次 `TerminalCore.Write` / `Resize` → `GridSnapshot.Capture(core)` → 与 `.grid` 逐字节比较；不一致时把实际结果写到 `artifacts/test-results/grid/<name>.actual.grid` 并输出 unified diff。
- **分块不变性**：同一夹具再按 1 字节、7 字节、4 KiB、随机块大小各回放一次，结果必须相同（覆盖 UTF-8 和转义序列跨块截断，research/03 §9.2 第 1 条）。
- **更新黄金样本**：`TCODE_UPDATE_GOLDEN=1 dotnet test --filter Category=Golden` 覆盖写回 `.grid`；PR 中黄金样本的变化需要人工审阅（CI 里永远不设这个变量）。
- 第三方素材的转换器在 `tools/terminal/fixtures/`：`convert-xtermjs.py`（`.in` + `.text` → `.cast` + `.grid`，按 xterm.js NOTES 处理末尾空行）、`convert-alacritty.py`（`alacritty.recording` + `grid.json` + `size.json` → `.cast` + `.grid`）。转换结果入库，转换器只在更新素材时运行。

#### 1.1.2 增量 vs 全量差分（render state 正确性）

研究 01 §4.1.7 记录了 RoyalTerminal 在 libghostty 上发现的多处"该标脏未标脏"问题，所以这一项是 D1 选型的必要配套。

```
对每个 .cast 夹具、每种分块方式：
  coreA：每写一块就 CaptureFrame（只复制脏行，01 §6.1），维护一份"增量拼出的帧"
  coreB：每写一块后重建一个新 RenderState 并全量复制（不依赖脏标记）
  断言：每一块之后，增量帧 == 全量帧（逐行比较 cells、styles、graphemes、flags、cursor）
```

- 差异报告给出第一处不一致的块序号、行号与两边的 `.grid` 片段。
- 已知上游缺陷：放进 `KnownDirtyBugs.cs`（夹具名 + 上游 issue 链接），在该处跳过并计数；上游修复后删除。
- 同样的差分也用于 fuzz 语料（§1.1.5）。

#### 1.1.3 应答与编码表

| 测试 | 内容 | 来源 |
|---|---|---|
| `Replies/*.cast` + `.replies` | 每个查询序列 → 期望的应答字节（DA1/DA2/DA3、XTVERSION、DECRQM 全表、DECRQSS 回读 SGR、XTGETTCAP、CPR、OSC 4/10/11、2048、`CSI ? u` 与 DA1 的顺序） | research/03 §3、§9.2 第 3 条；附录 B 中各 TUI 的启动查询 |
| `Encoding/KeyTable.cs` | 键 × 修饰 × 模式（legacy / DECCKM / DECKPAM / modifyOtherKeys 1/2 / Kitty 各 flag / win32-input-mode）→ 字节；表格驱动（`TheoryData`） | research/03 §4；期望值取自 xterm ctlseqs、Kitty 键盘协议文档、WT `kittyKeyboardProtocol.cpp` |
| `Encoding/MouseTable.cs` | 按钮 × 修饰 × 编码（X10 / SGR / UTF-8 / urxvt / SGR-pixels）× 坐标边界（223、2015、> 2015） | research/03 §5 |
| `Encoding/PasteTests.cs` | bracketed paste 包裹；`ESC[201~` 与控制字符过滤 | 01 §8 |

`.replies` 格式：每行一个"在第几个输入事件之后期望的应答"，字节以 C 风格转义写出（`\x1b[?62;22c`），便于审阅。

#### 1.1.4 esctest2

- 驱动：`tools/terminal/EscTestRunner`（.NET 控制台程序，引用 `TCode.Terminal.Core` 与 `TCode.Terminal.Pty`）。参照 Ghostty `test/esctest` 的做法 [已核实 ghostty@b3dc3d7 `test/esctest/README.md`、`src/main.zig`]：
  1. 在真实 PTY 中启动 `python3 esctest.py --expected-terminal=xterm --xterm-checksum=411 --xterm-reverse-wrap=411 --max-vt-level=5 --logfile=<out>`；
  2. 屏幕 80×25（esctest 每个测试前重置到这个尺寸）；
  3. 输出全部写入 `TerminalCore`；核心通过 `WriteToPty` 回写的应答写回 PTY；
  4. 开启 DECRQCRA 校验和应答：`GHOSTTY_TERMINAL_OPT_XT_CHECKSUM_REPORT`（默认关闭，因为程序可以借此探测屏幕内容）与 `GHOSTTY_TERMINAL_OPT_XT_CHECKSUM_EXTENSION = 23`（xterm 334+ 的算法）[已核实 `include/ghostty/vt/terminal.h:2471-2505`]；
  5. DA 应答为 VT520 级别；XTWINOPS 尺寸报告开启。
- 这两个选项只在 `TerminalCoreOptions.TestMode` 中开启，产品构建的门面不暴露。
- esctest2 本身：pin 一个提交（`native/ghostty/esctest.json` 中记录 URL + 提交 + SHA-256），CI 下载 tarball，只运行 Python 脚本，没有构建步骤。
- 结果处理：解析日志中的 `tests passed` 汇总与失败用例名 → 与 `tests/TCode.Terminal.Core.Tests/EscTest/known-failures.txt` 比较：
  - 新增失败 → 作业失败（PR 门禁）；
  - known-failures 中的用例通过了 → 作业警告，提示从列表中删除；
  - 汇总写入 GitHub 步骤摘要，完整日志作为工件上传。
- 已知会失败的类别（Ghostty 同样遇到）：DECSTR 没有完整实现导致的状态残留；终端不改变尺寸导致 XTWINOPS / DECCOLM 相关测试失败 [已核实 Ghostty esctest README]。TCode 的驱动可以真正执行 resize（我们有完整的连接层），所以第二类有机会通过。
- 验收门槛（research/03 §9.3）：xterm 模式下 Tier 0 / Tier 1 相关测试文件通过率 ≥ 95%。

#### 1.1.5 fuzz

**SharpFuzz 评估** [推断]：SharpFuzz 通过改写托管程序集的 IL 插入覆盖率探针（需要 .NET 8+，配合 AFL 或 libFuzzer）[已核实 https://github.com/Metalnem/sharpfuzz 页面；"改写 IL"一点页面未明说，按其依赖 Mono.Cecil / dnlib 推断]。对 TCode 的问题：①VT 解析与网格都在 libghostty（原生 Zig）里，IL 插桩看不到它的分支，覆盖率反馈几乎只来自门面的薄层；②插桩后的程序集以 JIT 运行，无法用于 NativeAOT。因此**不采用**。

采用的方案：

| 手段 | 内容 | 运行 |
|---|---|---|
| 上游语料回放 | 上游 `test/fuzz-libghostty/corpus/{stream,parser,osc}-{initial,cmin}` [已核实目录存在]。`stream` 语料的**第一个字节**是上游驱动用来选择 SIMD / 标量路径的选择位 [已核实 `test/fuzz-libghostty/README.md`]，回放时丢弃。每个文件按 1 字节、随机块各喂一次 | 每次 PR（cmin 子集，约数百个文件，< 30 s）；每晚全量 |
| 结构化随机生成器 | `Fuzz/VtGenerator.cs`：在合法序列模板（CSI、OSC、DCS、APC、SGR 冒号子参数、Kitty 图形、UTF-8 多字节、组合字符、ZWJ 序列）中随机插入 ESC / CAN / SUB / C1 / 超长参数 / 超长 OSC / 截断；穿插 `Resize`、`Reset`、视口滚动、选区、搜索、快照编码 → 解码 | PR：固定 20 个种子 × 1 MiB；每晚：随机种子 1 h，失败时打印种子 |
| 不变量 | 不抛异常、不崩溃；原生内存有上限（libghostty 分配器统计）；光标在范围内；宽字符两半成对；`EncodeSnapshot` → `Restore` 后网格相同；增量帧 == 全量帧（§1.1.2） | 每个输入 |
| 崩溃回归 | 发现的输入最小化后放进 `Fixtures/fuzz-regressions/*.bin`，永久回放 | 每次 PR |
| 上游 AFL++ | 不在我们的 CI 中运行（上游已经在跑 [已核实]）。升级 Ghostty 时检查上游 fuzz 目录的变更 | — |

原生崩溃会带走测试进程：fuzz 测试在**子进程**中运行（`tests/TCode.Terminal.Core.Tests` 以 `--fuzz-child <seed>` 启动自身），父进程收集退出码与 stderr，得到崩溃时可以复现的种子。

### 1.2 L2 PTY

`tests/TCode.Terminal.Pty.Tests`，全部使用真实 PTY 和真实进程，不 mock。**用例清单以 [03-pty.md](03-pty.md) §14（T1–T19、W-T1–W-T6）为准**，下表是分组概览与对应关系。

| 组 | 用例 | 依据 |
|---|---|---|
| 往返 | 启动 `/bin/sh -c 'printf hello'` / `cmd /c echo hello`，读到 `hello`；输入回显 | research/02 §9 M1 |
| 控制终端 | 子进程 `tty` 输出 `/dev/ttys*`/`/dev/pts/*`；`ps -o tpgid` 与 `tcgetpgrp(master)` 一致；macOS 上经 helper 获得控制终端 | research/02 §2.2–2.3 |
| 退出码 | `exit 7` → 7；`kill -TERM $$` → signal=TERM；早于 watch 注册就退出的进程（kqueue `ESRCH` 竞态）；RHEL 8 内核回退路径（`TCODE_PTY_FORCE_WAITPID=1` 强制） | research/02 §2.5 |
| 信号默认值 | `bash -c 'yes \| head -1; echo ${PIPESTATUS[0]}'` 输出 `141`（PTY 路径与 helper `--exec` 路径各一次；`trap - PIPE` 对 bash 无效，03 §12 实测） | 03 §14 T3、T19 |
| 与 .NET SIGCHLD 共存 | 同一进程中同时运行 `Process.Start` 与 PTY 子进程各 100 个，全部正确回收，`Process.ExitCode` 不受影响 | research/02 §1 第 3 条 |
| resize | `TIOCSWINSZ` 后子进程 `stty size` 与像素尺寸（`ws_xpixel = cols × cellWidth`，01 已改为每 cell 像素）正确 | 01 §6.1；04 §19 第 10 条 |
| 读模型 | macOS gather：`cat` 64 MiB 文件的读取块大小分布（应明显大于 1 KiB）；EOF / HUP 语义 | research/02 §2.7 |
| 关闭语义 | SIGHUP 进程组 → 2 s → SIGKILL；后台进程（`sleep 100 &` + `disown`）的处理；关闭后无僵尸（`waitpid` 检查） | research/02 §2.4 |
| fd 泄漏 | 子进程 `ls /proc/self/fd`（Linux）/ `lsof -p`（macOS）中除 0/1/2 外没有 master 或其他 fd | research/02 §4 U6 |
| 前台进程与 cwd | `sleep 5` 期间前台进程名为 `sleep`；`cd /tmp` 后 cwd | research/02 §2.9 |
| ConPTY | 随包 `conpty.dll` + `OpenConsole.exe` 被加载（检查加载路径，而不是 System32）；DA1 握手；`ClosePseudoConsole` 在旧系统路径上不死锁（关闭期间持续读输出）；`ReleasePseudoConsole`；`ClearPseudoConsole`；GLYPH_WIDTH 标志 | research/02 §3 |
| 压力（每晚） | `cat` 1 GB；并发打开 50 个会话；快速开关 500 次；TUI resize 风暴 | 03 §14 T11–T14 |
| helper | `--detach`（新会话、无控制终端、fd 只剩 0–2）、`--exec`；Linux helper 为 musl 静态 | 03 §14 T18、T19 |
| RHEL 8 | 在 `almalinux:8` 容器中运行 AOT 冒烟（glibc 2.28） | 03 §14 test-rhel8；12 §5.3 |

平台条件：xunit v3 的 `[Fact(SkipUnless = nameof(Platform.IsUnix))]` 或在测试体中 `Assert.SkipUnless(...)`（v3 支持动态跳过）[推断：以 xunit.v3 3.2.2 的 API 为准]。

### 1.3 L3 pty host 协议

`tests/TCode.Terminal.Host.Tests`。host 在测试进程内以库方式启动（`PtyHostServer.StartInProcess(options)`），另有一组用例启动**构建出的可执行文件**（`TCODE_PTYHOST_DIR`，与 `TCODE_AGENTHOST_DIR` 的做法相同 [已核实 `src/TCode.Desktop/Services/AgentHostConnection.cs:54-58`]）；设置 `TCODE_TEST_PTYHOST_EXE` 时改用 AOT 发布的二进制（与现有 `TCODE_TEST_HOST_EXE` 的做法相同 [已核实 `tests/TCode.Agent.Tests/RpcEndToEndTests.cs:13-18`]）。

| 组 | 用例 |
|---|---|
| 帧 | 12 字节头的编解码；超长帧、深层嵌套 JSON、未认证时超过 16 KiB 的帧都被拒绝并断开（04 §3、§14） |
| 握手与认证 | HMAC 双向挑战；错误令牌；受限令牌的 scope；`auth/revoke` 断开连接；对端 uid / SID 校验（在 CI 上只能测"同用户通过"） |
| 单例与发现 | 锁可得 = host 已死；陈旧 json 清理；`instanceId` 不一致拒绝；两个 Desktop 同时启动只产生一个 host |
| 流控 | 256 KiB 暂停读 PTY（用 `yes` 洪流 + 不 Ack 的客户端验证子进程 `write` 阻塞）；Ack 后 < 64 KiB 恢复；**慢订阅者 10 s 后降级并收到 `session/resync`**（01 §6.4 已改）；Agent 模式订阅不影响流控 |
| 重连 | attach 时 `S = head` 原子性：并发洪流中反复 attach / detach 1000 次，每次"快照 + Output(≥ S)"重建的网格 == 镜像核心网格（不重不漏） |
| 一致性 | Resize / SetColors / Reset / Clear 记录在两侧核心的同一位置生效：UI 侧核心网格 == 镜像核心网格（`.grid` 比较） |
| 多客户端 | 两个 UI 客户端 + 一个 agent 客户端；尺寸所有者切换；剪贴板读取只发给主视图 |
| 持久化 | revive 写入与恢复；裁剪到 N 行；`snapshot.bin` 不兼容时回退 `tail.vt`；文件权限 0600 / 目录 0700 |
| 生命周期 | 宽限期（用可注入的时钟）；`host/quit` 三种模式；host 被 SIGKILL 后 Desktop 侧"会话已丢失" |
| Agent 命令 | 09 §16 中 A1–A13 的协议级部分 |

### 1.4 L4 视图截图（Headless + Skia）

沿用 TCode 现有做法：`AppBuilder.UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })`，`TopLevel.CaptureRenderedFrame()` 截图，`TCODE_SNAPSHOT_DIR` 指定输出目录 [已核实 `tests/TCode.Desktop.Tests/HeadlessApp.cs`]。

- **输入来源**：`TerminalView.Session` 接一个 `ReplayTerminalViewSession`（读取 `.cast`），不启动 PTY，不合成任何键鼠事件（遵守项目约定）。
- **覆盖**：box drawing 与块元素的拼接（每个字号、1×/1.5×/2× 缩放）、Powerline 符号、光标四种形状与闪烁相位、宽字符与 emoji（含 ZWJ、VS16、旗帜）、下划线五种样式与颜色、删除线 / 上划线、超链接下划线、选区、搜索高亮、最小对比度、暗淡、反显、Kitty 图形放置（M6）。
- **比较方式**：像素比较不能跨平台逐字节一致（字体栅格化不同）。规则：
  1. 每个平台各自一套基线 PNG：`tests/TCode.Terminal.Avalonia.Tests/Baselines/<os>/<name>.png`；
  2. 比较采用"允许每像素 ΔE ≤ 2 且不同像素 ≤ 0.1%"的容差；
  3. **几何断言优先**：对 box drawing、光标、下划线这类程序化绘制的元素，直接断言像素坐标（例如"└ 的竖线在 cell 的第 x 列像素，且与上一行的 │ 连续"），这类断言跨平台成立；
  4. 字体：测试使用随测试项目分发的固定字体（JetBrains Mono，OFL），不依赖系统字体。
- **帧构建耗时**：同一组用例在 Headless 下记录 `Render` 的 CPU 耗时，作为 S4 的辅助数据（Headless 不代表 GPU 性能，research/04 §9.1）。
- **R1（`CompositionCustomVisual`）能否参与 `CaptureRenderedFrame`** 是 research/04 §11 第 7 条风险：M6 之前先写一个最小用例验证；不能截图时，R1 的截图测试改为直接调用 `TerminalPainter` 画到离屏 `SKSurface`。

### 1.5 L5 端到端 TUI 场景

#### 1.5.1 录制

在开发机上用 **TCode 自己的录制功能**（pty host 的调试开关 `TCODE_PTYHOST_RECORD=<dir>`：把每个会话的输出日志按 §2.1 格式落盘，包括 Resize 记录）或 `asciinema rec`（v2 格式）录制。录制脚本 `tools/terminal/record/*.sh` 记录了步骤，便于重录：

| 夹具 | 步骤（人工操作，录制 PTY 输出） | 尺寸 | 关注点 |
|---|---|---|---|
| `vim-edit` | `vim -u NONE -N sample.c`：移动、插入一行、`:set number`、分屏 `:vsp`、`:q!` | 100×30 | 滚动区、备用屏幕、DECSET 1049、光标形状 |
| `nvim-startup` | `nvim --clean`（产生 research/03 附录 B 的全部启动查询） | 100×30 | DECRQM、DECRQSS、Kitty 键盘查询、OSC 11 |
| `htop-3frames` | `htop` 刷新 3 次后 `q` | 120×40 | 全屏重画、颜色、2026 |
| `tmux-split` | `tmux -f /dev/null new` → 横竖分屏 → 每格运行 `ls --color` → 退出 | 120×40 | 滚动区、DECSLRM、外层查询 |
| `less-page` | `less -R` 一个带颜色的 2000 行文件，翻 5 页、搜索 | 100×30 | 备用屏幕、反显 |
| `fzf-filter` | `seq 1 5000 \| fzf`，输入 `42`，回车 | 100×30 | 非备用屏幕的局部重画、bracketed paste |
| `claude-code-turn` | Claude Code 启动 → 输入一句话 → 完成一轮 → `/exit` | 120×40 | Kitty 键盘、2026、OSC 8、OSC 9;4、宽字符 |
| `codex-turn` | Codex 启动 → 一轮 → 退出 | 120×40 | CPR、OSC 10/11、DA1/DA2、`CSI ? u`、1007 |
| `zsh-completion` | zsh 中 Tab 补全菜单 | 100×30 | 提示符重画 |

**Claude Code 与 Codex 的录制不得发起真实的模型调用**（研究 03 §9.2）：使用各自的离线 / mock 模式；若没有，录制者本人在本地完成一次录制即可（录制一次、入库永久回放，CI 中只回放字节流，不运行这两个程序）。录制文件入库前人工检查并删除路径、用户名、令牌等敏感内容。

#### 1.5.2 回放与快照

- 测试读 `.cast` 回放，在**若干关键点**（`.cast` 中的 `m` 标记事件，§2.1）各取一次 `.grid`，与期望比较。期望文件 `<name>.grid` 中按标记名分段。
- 同一夹具同时用于 §1.1.2 的差分、§1.4 的截图（取最后一个标记）和 §3 的基准（S4）。
- 跨平台：`.grid` 在三个平台上必须逐字节一致（research/03 §9.2 第 2 条）。

#### 1.5.3 真实 shell 冒烟（L5b）

`tests/TCode.Terminal.Host.Tests/Shell/`：真实 pty host + 真实 shell（bash、zsh、fish（若安装）、pwsh、Git Bash），只向 PTY **写字节**：

- shell 集成注入成功，OSC 133 A/B/C/D 与退出码到达，`CommandRecord` 正确；
- `echo $TERM $COLORTERM $TERM_PROGRAM`；
- 多行粘贴（bracketed paste）与 `ESC[201~` 注入防护；
- `exit` 后会话结束、退出码；
- macOS 上 login shell 的 argv0（`ps -o comm`）。

### 1.6 跨平台"与系统一致"检查清单（手工，L6）

每个发布版本在三台实机上执行一次，结果记录在发布说明的附件中。对照对象：macOS = Terminal.app / iTerm2；Windows = Windows Terminal；Linux = GNOME Terminal（GNOME）/ Konsole（KDE）。

| 类 | 检查项（预期：与对照终端相同） | 依据 |
|---|---|---|
| 启动 | 默认 shell 取自账户记录（`getpwuid`）；macOS login shell（`ps` 中 `-zsh`）；Linux 非 login；Windows 优先 pwsh；新标签继承 cwd | research/05 §1、D9 |
| 环境 | `TERM=xterm-256color`、`COLORTERM=truecolor`、`TERM_PROGRAM=TCode`；`LANG` 处理；PATH 与系统终端一致（macOS 的 `path_helper`） | research/05 §2 |
| 键盘 | macOS：⌘C/⌘V/⌘K（清屏）/⌘T/⌘W；Option 作 Meta 的默认值；Windows / Linux：Ctrl+字母交给 shell，Ctrl+Shift+C/V；Ctrl+C 中断；Alt+Backspace；Home/End/PgUp/PgDn | D6、research/05 §4 |
| 鼠标 | 双击选词规则、三击选行；Linux 中键粘贴 PRIMARY；右键菜单；Shift 强制本地选择 | research/05 §4 |
| 剪贴板 | 复制时去除软换行；粘贴多行警告 | 01 §8 |
| 外观 | 默认字体与字号；光标形状与闪烁；铃声（系统提示音 / 视觉） | research/05 §5 |
| 输入法 | 中文、日文 IME 的组合窗口位置与提交 | research/04 §6.2 |
| 生命周期 | 有程序运行时关闭标签确认；退出 TCode 的汇总确认；revive 恢复 | 01 §3.3 |
| 程序 | vim、tmux、htop、less、fzf、neovim、Claude Code、Codex 的日常操作 | 01 §1 |
| vttest | 菜单 1–8、11 人工通过 | research/03 §9.3 |
| ucs-detect | `ucs-detect --save-yaml`；2027 模式下 WIDE / ZWJ / VS16 / VS15 ≥ 98%；与 Ghostty 的 data/*.yaml 对照 | research/03 §9.1、§9.3 |
| 无障碍 | VoiceOver / Narrator / Orca 能读出新输出 | research/04 §6.3 |

---

## 2. 测试工具与文件格式

### 2.1 字节流：asciicast v2 + TCode 扩展

格式规范 [已核实 https://docs.asciinema.org/manual/asciicast/v2/]：NDJSON；首行为头（必填 `version: 2`、`width`、`height`；可选 `timestamp`、`command`、`title`、`env`、`theme`）；之后每行一个事件 `[time, code, data]`，`code` 为 `o`（输出）、`i`（输入）、`m`（标记）、`r`（尺寸，`"COLSxROWS"`）；**事件码表是开放的，读取方应忽略不认识的事件码**；`data` 必须是合法的 UTF-8 JSON 字符串。

问题：PTY 输出不一定是合法 UTF-8，且 UTF-8 字符可能跨块；asciicast 的字符串无法逐字节保真。TCode 的约定：

| 事件 | 含义 | 何时使用 |
|---|---|---|
| `o` | 输出，UTF-8 文本 | 块是合法 UTF-8 且不在字符中间截断 |
| `ob` | **TCode 扩展**：输出，`data` 为原始字节的 base64 | 其他情况（含跨块截断）。其他播放器会忽略它，所以这类文件只用 TCode 回放 |
| `r` | resize，`"COLSxROWS"`；TCode 扩展可选第二种写法 `"COLSxROWS@CWxCH"`（每 cell 像素，与 01 的 `TerminalSize` 定义一致） | |
| `m` | 标记，`data` 为快照点名称 | `.grid` 按标记分段 |
| `i` | 输入 | 只用于录制说明，回放时忽略（输入的效果已在输出中） |
| `xc` | **TCode 扩展**：`SetColors`，data 为 JSON 调色板 | 对应 pty host 日志的 SetColors 记录 |
| `xr` | **TCode 扩展**：`Reset`（`"hard"`/`"soft"`） | |

头部扩展字段（读取方忽略未知字段）：`"tcode": {"widthMode": "compat", "scrollback": 1000, "palette": "default-dark", "source": "xtermjs:vttest-2.in", "license": "MIT"}`。

时间戳：回放测试不使用时间（按顺序写入）；基准与 S4 的"按真实节奏回放"使用时间。

为什么不用 asciicast v3（2025-09 发布，使用相对时间间隔、新增 `x` 退出事件 [已核实 https://docs.asciinema.org/manual/asciicast/v3/]）：v3 与 v2 不兼容，`asciinema` 2.x 与很多现有素材是 v2；v2 足够。读取器同时支持 v3（多 10 行代码），写出统一为 v2。

### 2.2 网格快照：`.grid`

要求：纯文本、可读、可 diff、跨平台逐字节一致（LF、UTF-8、无 BOM）、与 libghostty 的内部布局无关（以 `FrameSnapshot` 为准，01 §6.1）。

```
tcode-grid 1
size 80x24 cell 9x18
cursor 12,3 shape=block visible=1 blink=0 wide=0
screen primary
modes decckm=0 deckpam=0 2004=1 1004=0 mouse=none kitty=0 2026=0 2027=0 1049=0 origin=0 autowrap=1 insert=0
margins top=0 bottom=23 left=0 right=79
title "vim sample.c"
cwd file://host/tmp
styles
  s0 default
  s1 fg=1 bold
  s2 fg=#ff8800 bg=default underline=curly ul-color=#00ff00
  s3 fg=4 link=1
links
  l1 id="" uri="https://example.com"
rows
  0 |$ ls --color                                                                   | s0
  1 |src  tests  README.md                                                          | s3:0-2 s0:3-4 s3:5-9 s0:10-79
  2 |中文测试 ✓                                                                      | s0  w:0,2,4,6
 14 |~                                                                              | s1:0 s0:1-79  wrap
scrollback 3
  -3 |old line 1                                                                    | s0
semantic
  prompt rows=0 input=0:2-13 output=1-1 exit=0
---- mark: after-insert
...
```

规则：

| 项 | 规则 |
|---|---|
| 行文本 | 两个 `\|` 之间是该行全部列，**不截去行尾空格**（列宽可见）；宽字符占两列，第二列写 `⁣`（不可见分隔符，以免显示错位），行末 `w:` 列出宽字符起始列 |
| 字形簇 | 多码点字形簇整体写在起始列；`g:` 列出起始列与码点（`g:5=U+1F468,U+200D,U+1F469`） |
| 不可打印 | 控制字符不会出现在网格中；空 cell 写空格，`e:` 列出"真正为空（从未写入）"的游程，与写入空格区分 |
| 样式 | 每帧一张去重的样式表 `sN`；行尾为游程 `sN:起-止`，整行同一样式时只写 `sN`；颜色：调色板索引写十进制，真彩写 `#rrggbb` |
| 软换行 | 行尾 `wrap` |
| 回滚 | 默认只写最后 `scrollback N` 行（N 由测试指定，默认 0）；行号为负 |
| 语义 | OSC 133 区间与退出码 |
| 多段 | 每个 `m` 标记一段，以 `---- mark: <name>` 分隔 |

实现：`TCode.Terminal.Core` 中的 `GridSnapshot.Capture(TerminalCore core, GridSnapshotOptions options)` → `string`，属于测试辅助，但放在主程序集的 `TCode.Terminal.Core.Testing` 命名空间（L3、L4、Agent 测试都要用，且必须 AOT 安全，`tcode-ptyhost --self-test` 也用它）。

### 2.3 在 xunit v3 中的组织

TCode 现有测试项目是 xunit v3 3.2.2 + `Microsoft.NET.Test.Sdk`，项目 `OutputType=Exe` [已核实 `Directory.Packages.props`、`tests/TCode.Desktop.Tests/TCode.Desktop.Tests.csproj`]。新增测试项目沿用同样配置。

| 机制 | 用法 |
|---|---|
| 数据驱动 | 夹具枚举：`public static TheoryData<string> Fixtures => FixtureCatalog.Enumerate("tui")`；`[Theory, MemberData(nameof(Fixtures))]`。每个夹具是一个独立的测试用例，失败时显示夹具名 |
| 分类 | `[Trait("Category", "Golden" \| "Diff" \| "Fuzz" \| "FuzzLong" \| "EscTest" \| "Pty" \| "Stress" \| "Snapshot")]`。PR 作业排除 `FuzzLong`、`Stress`；每晚全部运行。过滤：`dotnet test -- --filter-not-trait "Category=FuzzLong"`（xunit v3 原生命令行）[推断：以 xunit.v3 runner 实际参数为准] |
| 平台条件 | `Assert.SkipUnless(OperatingSystem.IsWindows(), "ConPTY")` 动态跳过 |
| 取消与超时 | 每个 PTY / host 用例使用 `TestContext.Current.CancellationToken` 并设 30 s 超时；超时时 dump 会话日志 |
| 夹具复制 | `Fixtures/**` 设为 `CopyToOutputDirectory=PreserveNewest`；`.cast`、`.grid` 在 `.gitattributes` 中设为 `-text`（禁止 CRLF 转换） |
| 并行 | L1 可并行；L2/L3/L4 设 `[assembly: CollectionBehavior(DisableTestParallelization = true)]`（与 `TCode.Desktop.Tests` 相同 [已核实 `HeadlessApp.cs:6`]），避免 PTY 与时钟竞争 |
| 工件 | 失败时写 `artifacts/test-results/<project>/`：actual `.grid`、diff、截图、host 日志；CI 统一上传 |

---

## 3. 基准

### 3.1 项目结构

```
tests/TCode.Terminal.Benchmarks/
  TCode.Terminal.Benchmarks.csproj      OutputType=Exe；BenchmarkDotNet；引用 Core、Protocol、Host（库方式）、Avalonia（R1/R3 绘制）
  Program.cs                            BenchmarkSwitcher；--data <dir>；--profile pr|nightly
  Config/
    JitAotConfig.cs                      两个 Job：Jit（默认 RyuJIT + 动态 PGO）、NativeAot（BDN 的 NativeAOT 工具链）
    PrConfig.cs                          ShortRun 风格：1 次启动、3 次预热、5 次迭代
  Core/
    ParseThroughputBenchmarks.cs         S1、S2、S3、S5、S6（关闭渲染：只 vt_write）
    SnapshotBenchmarks.cs                FrameSnapshot 发布（全脏 / 1–3 行脏）、EncodeSnapshot / Restore（10k / 100k 行）
    ReflowBenchmarks.cs                  S7 的核心部分
    MemoryBenchmarks.cs                  S9（自定义列：原生字节数，通过 libghostty 分配器统计）
    FormatterBenchmarks.cs               Agent 读取输出（09 §5）：1k / 50k 行命令输出的 plain 格式化
  Ipc/
    FrameCodecBenchmarks.cs              帧编解码
    HostThroughputBenchmarks.cs          S13：host → 客户端的端到端字节吞吐（进程内 socket 对）
    AttachBenchmarks.cs                  S14：attach 快照耗时
  Render/
    PainterBenchmarks.cs                 S4：200×60 全量重画、1–3 脏行（Headless Skia，离屏 SKSurface）
  E2E/                                   不用 BDN：独立的计时驱动，见 §3.5
    E2EDriver.cs                         启动 Desktop（--bench-mode），回放数据集，读 metrics 文件
```

- 数据集路径通过 `--data` 或环境变量 `TCODE_BENCH_DATA` 传入，默认 `artifacts/bench-data/`。
- 基准中的"关闭渲染"= 只调 `TerminalCore.Write`，不调 `CaptureFrame`。另有一组"开启渲染"= 每 8 ms（模拟 120 Hz）调用一次 `CaptureFrame` + Painter。
- 每个基准都加 `[MemoryDiagnoser]`；热路径基准另加 `[DisassemblyDiagnoser]`（只在 JIT 作业中有意义）。
- BenchmarkDotNet 的版本：使用支持 .NET 10 与 `nativeaot10.0` 运行时标识的版本，进入 `Directory.Packages.props` [推断：落地时选当时的最新稳定版]。

### 3.2 JIT 与 NativeAOT 两种工具链

产品发布的是 NativeAOT（项目约定），而 AOT 没有动态 PGO、默认指令集基线较低（research/04 §9.1、§11 第 5 条），所以两者都跑：

```
dotnet run -c Release --project tests/TCode.Terminal.Benchmarks -- --runtimes net10.0 nativeaot10.0 --filter '*Parse*'
```

- NativeAOT 作业：BDN 为每个基准类生成并发布一个 AOT 程序，首次运行较慢（每个类一次 ILC），所以 PR 作业只对 S1/S3/S5 跑 AOT。
- 指令集：AOT 作业另加一个 `IlcInstructionSet=native` 的变体，与默认基线对比，判断是否值得在发布时按 RID 指定 `IlcInstructionSet`（例如 x64 用 `x86-x64-v2`）。不过解析主路径在 libghostty（原生）中，托管侧的向量化主要影响 `FrameSnapshot` 复制与绘制；结论写回本文 [推断]。
- libghostty 本身的 SIMD 开关（`-Dsimd`）对吞吐影响很大：12 号文档中 win-x64 可能使用标量构建。基准报告必须注明所用原生库的 `ghostty_build_info(SIMD)` [已核实 `include/ghostty/vt/build_info.h:60`]。

### 3.3 场景与数据集

S1–S12 来自 research/04 §9.2；S13–S15 为本文新增（pty host 架构 D3 引入的路径）。

| # | 场景 | 数据集（生成方式） | 测量 | 通过线 |
|---|---|---|---|---|
| S1 | `cat` 大文件 | `ascii-src-100m.txt`：拼接 dotnet/runtime 与 ghostty 源码（固定提交的 tarball，按文件名排序拼接到 100 MiB）；`ascii-150m.txt`：生成器输出 150 MiB 可打印 ASCII，80 列换行 | BDN：解析吞吐（关闭渲染）；E2E：从 host 读到最后一字节到 UI 帧呈现的总耗时 | 核心 ≥ 500 MB/s（libghostty，SIMD 构建）；端到端 ≥ 关闭渲染时的 0.8×；150 MB ≤ 1 s |
| S2 | `seq` | `seq-10m.txt`：`seq 1 10000000` 的输出（约 78 MB），生成器内置，不调用系统 `seq` | 每行开销（滚动、换行） | ≥ 200 MB/s |
| S3 | 彩色密集 | vtebench `dense_cells`、`medium_cells`（运行 vtebench 生成器脚本一次、录下 stdout）；`truecolor-gradient.vt`（生成器） | SGR 解析与样式去重；分配 | ≥ 100 MB/s；稳态零分配 |
| S4 | 全屏刷新 | `tui/vim-edit`、`tui/htop-3frames`（§1.5，按真实时间回放）；vtebench `sync_medium_cells` | 每帧脏行数、绘制耗时 p50/p99、2026 期间无撕裂（帧序列中不出现半更新的帧） | 绘制 p99 ≤ 2 ms（200×60，2×）；1–3 脏行 ≤ 0.3 ms |
| S5 | Unicode | `ja-5m.txt`：青空文库公版作品拼接到 5.4 MiB（与 Ghostty devlog 的规模相当，公版、无许可问题）；`emoji-zwj.vt`（生成器：ZWJ 家庭、旗帜、肤色、VS16）；`combining.vt` | 宽度、字形簇、回退字体首帧不阻塞 | ≥ 100 MB/s |
| S6 | 光标移动 | vtebench `cursor_motion`、`scrolling_bottom_region`、`scrolling_top_region` | CSI 快路径 | 不低于同机 Alacritty |
| S7 | resize 与 reflow | 回滚 1 万 / 10 万行（`ascii-src` 前缀），80↔200 列每 16 ms 切换，持续 5 s | reflow 耗时、UI 帧时间、PTY 暂停时长 | 1 万行 ≤ 15 ms；10 万行 ≤ 150 ms；UI 不掉帧 |
| S8 | 输入延迟 | 不需要数据集；后台洪流用 `yes` | 自动：内部分段时间戳（§3.5）；人工：Typometer（§3.6） | 自身附加 p50 ≤ 1 ms；端到端均值 ≤ 对照组 + 2 ms |
| S9 | 内存 | 1 万 / 10 万行的纯文本、彩色（dense_cells 截取）、CJK（ja-5m 截取） | 托管堆、原生页、压缩后大小 | 每 1 万行 × 120 列 ≤ 10 MB，压缩后 ≤ 3 MB |
| S10 | 空闲 | 10 个终端空闲，光标闪烁，10 min | CPU、唤醒次数 | 合计 < 0.5% CPU |
| S11 | 背压 | `yes` 运行 30 s 后发 `^C`（写字节，不合成按键） | 内存是否有界；`^C` 到提示符出现的时间 | 内存平稳；≤ 100 ms |
| S12 | 与 Agent UI 共存 | 终端 S1 洪流 + StreamMark 流式渲染长 Markdown（TCode 现有的 Fake 模型流式输出） | 两边的帧时间 | 终端帧率不受 UI 线程负载影响（R1 阶段的验收，M6） |
| S13 | host → UI 吞吐（新增） | S1 数据经真实 pty host（`cat` 文件）→ IPC → UI 侧核心 | 吞吐、Ack 往返、暂停次数 | 不低于单进程核心吞吐的 0.7× [推断：目标值] |
| S14 | attach / 重连（新增） | 10 k / 100 k 行回滚的会话 | `session/attach` 到 READY 段可渲染的时间；全部回滚到达的时间 | READY ≤ 50 ms；10 k 行快照编码 < 20 ms（04 §18 第 1 条） |
| S15 | Agent 读取（新增） | 50 k 行命令输出 | `command/output` 的 plain 格式化耗时（09 §5.4 每 250 ms 调用） | ≤ 5 ms / 64 KiB [推断：目标值] |

**数据集的存放与生成**：

- 生成器：`tools/terminal/gen-bench-data/`（C# 单文件程序，`dotnet run`）。所有内容确定性生成（固定种子）；需要外部来源的（dotnet/runtime 与 ghostty 源码 tarball、青空文库文本、vtebench 仓库）按 `tools/terminal/gen-bench-data/sources.json` 中的 URL + SHA-256 下载并校验。
- 输出：`artifacts/bench-data/`（`.gitignore`），附 `manifest.json`（文件名、大小、SHA-256、生成器版本）。仓库只提交 `sources.json` 与期望的 `manifest.json`；生成后校验一致，否则报错。
- CI：数据集按 `manifest.json` 的哈希作为缓存键缓存（12 号文档 §7）。PR 短作业只用 ≤ 16 MiB 的子集（`--profile pr` 时生成器输出 `*-pr` 变体）。
- vtebench 生成器是 shell 脚本：在 Linux 上运行一次录下 stdout 即可，结果是确定的字节流；录下的文件也纳入 `manifest.json` [推断：需确认 vtebench 各脚本不含随机内容]。

### 3.4 CI 门禁

GitHub 托管 runner 的性能噪声通常在 ±10% 以上 [推断]，直接与历史基线 JSON 比较会频繁误报。做法：

| 作业 | 内容 | 判定 |
|---|---|---|
| **PR：`bench-pr`** | 在同一台 runner 上依次构建并运行 **base（目标分支的合并基）** 与 **head** 两个版本的 S1、S3、S5 核心吞吐、快照发布、帧编解码（JIT + AOT），短作业 | head 的中位数比 base 差 > 10%，且 BDN 报告的置信区间不重叠 → 失败；5–10% → 警告注释。改了原生库版本（Ghostty 升级）的 PR 自动标记"需要人工审阅基准"，不自动失败 |
| **每晚：`bench-nightly`** | 完整 S1–S11、S13–S15（JIT + AOT），Linux x64 与 macOS arm64 | 结果 JSON 上传为工件，并追加到 `bench-history` 分支的 `results/<date>/`；与 7 天中位数相比回退 > 10% 时开 issue |
| **手动：`bench-e2e`** | S4、S8 自动部分、S10、S11、S12（需要真实窗口与 GPU） | 在固定的自托管机器上运行（若有）；否则开发者本地运行 `tools/terminal/bench-e2e.sh` |

建议有条件时配一台固定硬件的自托管 runner（Apple Silicon Mac mini）专跑 `bench-nightly`，GitHub 托管 runner 只做 A/B。

### 3.5 端到端指标采集

**为什么不直接用 EventCounters**：`EventSource` / `EventCounters` 在 NativeAOT 中由 `EventSourceSupport` 特性开关控制，AOT 发布默认关闭 [推断：需核对 .NET 10 的默认值；Microsoft 文档的 trimming 选项列表中 `EventSourceSupport` 在 AOT 下默认 false]。打开它会增加体积和启动成本，所以产品构建不开。

方案：

1. 代码中只用 `System.Diagnostics.Metrics.Meter`（AOT 安全）：`Meter("TCode.Terminal")`，仪表：
   - `terminal.bytes_parsed`（Counter，按 view/mirror 标签）；
   - `terminal.snapshots_published`、`terminal.frames_rendered`（Counter）；
   - `terminal.render_ms`、`terminal.snapshot_us`、`terminal.input_latency_us`（Histogram；input 分段：`keydown→pty_write`、`pty_write→echo_parsed`、`echo_parsed→snapshot`、`snapshot→render_end`）；
   - `terminal.flow_paused`（Counter，host 侧）、`terminal.ack_rtt_us`（Histogram）；
   - `gc.gen0`（从 `GC.CollectionCount` 每秒采样）。
2. 设置环境变量 `TCODE_TERMINAL_METRICS=<path>` 时，进程内 `MeterListener` 每秒把聚合结果（计数、Histogram 的 p50/p95/p99）写成 JSON Lines；未设置时不注册监听器（零开销）。
3. 需要 `dotnet-counters` / `dotnet-trace` 实时观察时，用 `-p:EventSourceSupport=true` 构建诊断版（JIT 构建本身就支持）。
4. `E2EDriver` 启动 Desktop（`--bench-mode`：Headless 关闭，真实窗口，打开指定场景的终端标签），按数据集回放，读取 metrics 文件并输出汇总表。

### 3.6 Typometer 手工测量流程

Typometer 通过向目标窗口发送按键并截取屏幕区域来测量延迟（Java 程序）。它属于**人工**测量：由开发者在自己的机器上运行，**不进入 CI，也不由 Agent 执行**。

| 步骤 | 内容 |
|---|---|
| 1 环境 | 接电源、关闭省电与 True Tone / Night Shift、关闭其他应用；显示器原生刷新率记录在结果里；macOS 关闭"降低透明度"之外的视觉效果保持默认 |
| 2 对照组 | 同机安装 Ghostty、Alacritty、kitty、WezTerm；macOS 加 Terminal.app、iTerm2；Windows 加 Windows Terminal；字体、字号、窗口尺寸（100×30）一致 |
| 3 被测状态 | TCode 的终端标签单独占据窗口（工作台其他面板折叠）；shell 为 `sh`（避免提示符主题影响）；分别测 R3 与 R1 构建 |
| 4 测量 | Typometer 默认设置，每个终端 200 次按键 × 3 轮；第二组在后台运行 `yes > /dev/null`（S8"洪流"条件）——注意这条命令不输出到终端，用来测 CPU 争用；第三组在另一个 TCode 终端标签运行 `yes`（测本进程负载） |
| 5 记录 | min / avg / max / 标准差，写入 `docs/terminal/bench/typometer-<date>-<machine>.md`（表格 + 环境描述） |
| 6 判定 | TCode 均值 ≤ 同机 Alacritty 或 Ghostty 均值 + 2 ms（research/04 §7.3） |

---

## 4. 数字目标与测量方法

来源：01 §1、research/04 §0.7 与 §7.3（标"目标值"的均为 [推断：目标值]），以及 04 号文档的流控参数。

| 指标 | 目标 | 测量方法 | 场景 / 作业 |
|---|---|---|---|
| 核心解析吞吐，ASCII，关闭渲染 | ≥ 500 MB/s（libghostty SIMD 构建，01 §1）；标量构建另行记录，不设门槛（见待决问题） | BDN `ParseThroughputBenchmarks`，数据在内存中，只调 `Write` | S1 / bench-pr、nightly |
| dense SGR | ≥ 100 MB/s | 同上，vtebench `dense_cells` | S3 |
| Unicode / CJK | ≥ 100 MB/s | 同上，`ja-5m.txt` | S5 |
| 端到端 `cat`（开启渲染） | ≥ 关闭渲染的 0.8×；150 MB ASCII ≤ 1 s | E2EDriver：真实 host + Desktop，`cat` 数据集，`terminal.bytes_parsed` 与墙钟 | S1、S13 / bench-e2e |
| 渲染线程每帧（200×60，2×） | 全量重画 p99 ≤ 2 ms；1–3 脏行 ≤ 0.3 ms | BDN `PainterBenchmarks`（离屏 Skia）+ E2E `terminal.render_ms` Histogram | S4 |
| UI 线程每帧 | ≤ 0.1 ms（R1 阶段） | E2E：Avalonia `Dispatcher` 钩子计时 | S4、S12 |
| 快照发布 | 全脏 ≤ 50 µs；渲染线程等锁时间 = 0 | BDN `SnapshotBenchmarks`；E2E `terminal.snapshot_us` | S4 |
| 输入延迟（自身附加） | KeyDown → Present 提交 p50 ≤ 1 ms，p99 ≤ 1 帧 | `terminal.input_latency_us` 分段直方图（回显由真实 shell 产生；按键事件来自真实键盘，开发者手动打字——不合成输入） | S8 / 人工 |
| 输入延迟（端到端） | 均值 ≤ 同机 Alacritty / Ghostty + 2 ms | Typometer（§3.6） | S8 / 人工 |
| 回滚内存（每 1 万行 × 120 列） | ≤ 10 MB；压缩后 ≤ 3 MB | BDN `MemoryBenchmarks`：libghostty 分配器统计的原生字节 + 托管堆差值 | S9 |
| reflow | 1 万行 ≤ 15 ms；10 万行 ≤ 150 ms；不阻塞 UI 与渲染 | BDN `ReflowBenchmarks`；E2E 帧时间 | S7 |
| 空闲 CPU | 10 个空闲终端合计 < 0.5% | E2E：进程 CPU 时间差 / 墙钟，10 min；macOS 用 `powermetrics` 记录唤醒次数（人工） | S10 |
| GC | 热路径稳态零分配；洪流下 Gen0 ≤ 1 次/s | BDN `MemoryDiagnoser`（Allocated = 0 B）；E2E `gc.gen0` | S1、S3 |
| 背压 | 洪流时内存平稳；`^C` 到提示符 ≤ 100 ms | E2E：`yes` 30 s，记录 RSS 曲线；写 `\x03` 后到 133;A 的时间 | S11 |
| 流控参数 | 暂停阈值 256 KiB、恢复 64 KiB、Ack 32 KiB（04 §7.1）；慢订阅者最多拖住 10 s（01 §6.4） | L3 测试断言行为；S13 记录暂停次数与 Ack 往返 | S13 |
| attach | READY ≤ 50 ms；10 k 行快照编码 < 20 ms | BDN `AttachBenchmarks` | S14 |
| 与 Agent UI 共存 | 终端帧率不受 UI 线程负载影响 | E2E：S1 洪流 + Fake 模型流式 Markdown，比较有无 UI 负载时的 `terminal.frames_rendered` 速率 | S12 / M6 |
| esctest2 | Tier 0 / Tier 1 文件通过率 ≥ 95% | §1.1.4 | esctest |
| ucs-detect | 2027 模式下 WIDE / ZWJ / VS16 / VS15 ≥ 98% | 人工（§1.6） | L6 |
| fuzz | 1 h 无失败 | §1.1.5 | 每晚 |

---

## 5. 测试与里程碑的对应

| 里程碑（01 §10） | 必须通过的测试 |
|---|---|
| M0 Spike | 6 个 RID 加载 libghostty 并通过一个回放用例；PTY 往返（Unix helper + ConPTY）；AOT 二进制运行 `vim` 并退出（人工 + 一个 L5b 冒烟） |
| M1 核心与 PTY | L1 全部（黄金样本、差分、应答、编码表、fuzz PR 子集）；esctest 进 CI 且门槛达标；L2 全部（压力测试在每晚） |
| M2 pty host | L3 全部；`tcode-ptyhost --self-test` 在 aot-smoke 中运行 |
| M3 视图 | L4 全部；S1、S3、S4、S5 达标；S8 自动部分 |
| M4 集成 | L5b；§1.6 手工清单三平台完成 |
| M5 Agent | 09 §16 全部 |
| M6 R1 + 图形 | S12；Kitty 图形截图；R1 截图方式验证（research/04 §11 第 7 条） |

---

## 6. 待决问题

1. **win-x64 的 libghostty 可能使用标量构建**（12 号文档 §4.3），S1 的 500 MB/s 目标在该 RID 上是否放宽，或者要求 SIMD 构建通过 CPU 兼容性验证后再发布。
2. 是否新增 `tests/TCode.Terminal.Avalonia.Tests`（§1 表后的说明）；01 §4 的测试项目列表需要相应补充 `TCode.Terminal.Avalonia.Tests` 与 `tools/terminal/EscTestRunner`。
3. 有无固定硬件的自托管 runner 跑每晚基准（§3.4）。
4. Claude Code / Codex 录制夹具的来源：是否允许开发者本人录制一次真实会话入库（不在 CI 中调用模型）。

---

## 来源

- 本仓库：`Directory.Packages.props`；`tests/TCode.Desktop.Tests/HeadlessApp.cs`、`TCode.Desktop.Tests.csproj`；`tests/TCode.Agent.Tests/RpcEndToEndTests.cs`；`src/TCode.Desktop/Services/AgentHostConnection.cs`。
- Ghostty `ghostty-org/ghostty@b3dc3d7`：`test/esctest/README.md`、`test/esctest/src/main.zig`、`test/fuzz-libghostty/README.md` 与 `corpus/`、`include/ghostty/vt/terminal.h`（XT_CHECKSUM 选项）、`include/ghostty/vt/build_info.h`。
- asciicast 规范：https://docs.asciinema.org/manual/asciicast/v2/ 、https://docs.asciinema.org/manual/asciicast/v3/ （2026-10-16 读取）。
- SharpFuzz：https://github.com/Metalnem/sharpfuzz （2026-10-16 读取）。
- 研究文档：[research/01](research/01-emulator-cores.md) §4.1.5、§4.1.7；[research/02](research/02-pty.md) §2、§3、§5、§9；[research/03](research/03-vt-protocol.md) §9、附录 B；[research/04](research/04-rendering-performance.md) §0.7、§2.9、§7.3、§9、§11；[04-pty-host.md](04-pty-host.md) §7、§16、§18。
