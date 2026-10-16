# TCode 终端：Agent 集成（09）

> 状态：设计稿 v1（2026-10-16）。上位文档：[01-architecture.md](01-architecture.md)（D3、D8、§3、§6.4、§8、§9、§10 M5）。pty host 的 IPC 帧格式与消息全集见 [04-pty-host.md](04-pty-host.md)；本文定义 **Agent 需要的语义**，§10.4 逐条对应 04 §4.3–4.5 的消息，并列出需要 04 补充的字段。连接参数与受限令牌采用 04 §13.2。
>
> 范围：内置 Agent 的 `bash` 工具在可见终端中执行命令；AgentHost 内的 `TerminalBroker`；Agent 访问终端的权限；ACP `auth.terminal` / `terminal/*`；Codex 命令输出的终端化显示；会话记录（transcript）中命令输出的展示；端到端测试。

**标注约定**

- **[已核实]**：已对照下列来源（本仓库源码、上游源码的指定提交、官方文档）核对。上游源码引用格式为 `仓库@提交 路径:行号`。
- **[推断]**：基于已核实事实的设计推理，实现前需要实测或评审。
- 上游版本：VS Code `microsoft/vscode@4765bd3`（2026-10-16）、Ghostty `ghostty-org/ghostty@b3dc3d7`（2026-10-15）、Zed `zed-industries/zed@6b218b8`（2026-10-16）。

---

## 0. 结论摘要

| # | 主题 | 结论 |
|---|---|---|
| 1 | bash 工具的模式 | 新增设置 `agent.shell.mode`：`process`（现有实现）/ `terminal`（可见终端）/ `auto`（**默认**）。`auto` 在"pty host 可用 + 有 UI 连接 + 不是子 Agent 的并行批次"时用可见终端，否则用进程模式（§2.3） |
| 2 | 终端数量 | **每个 Agent 会话（含子 Agent）一个持久的"Agent 终端"**，前台命令依次复用；忙碌（等待输入、超时转后台）时临时再开一个；后台命令每条一个独立的"命令会话"（§3） |
| 3 | 执行策略 | 沿用 VS Code 的三档：`rich`（OSC 133 A/B/C/D + 带 nonce 的 OSC 633）直接在 shell 中键入命令；`basic`（只有部分 133）键入命令，靠 D / 回到提示符 / 空闲判断结束；`none` **不键入命令**，改为"每条命令一个 PTY 会话"（`exec`，进程退出码精确）。哨兵方案只保留给以后的远程 shell（§4） |
| 4 | 策略运行位置 | 命令执行器放在 **pty host 内**（贴着镜像核心，空闲检测不跨进程）；AgentHost 的 `TerminalBroker` 只做策略、超时、结果格式化、后台任务、权限（§10） |
| 5 | 输出 | 交给模型的永远是**纯文本**：镜像核心的 formatter 按 `CommandRecord` 的输出区间取文本（已去 ANSI、`\r` 进度条已折叠成最终状态）。截断规则与现有 `OutputCapture` 相同（尾部 2000 行 / 50 KB，完整内容写日志文件）（§5） |
| 6 | 超时 / 中断 | 默认与进程模式一致：超时即中断。升级顺序：`^C` → 2 s → `^C` → 2 s → SIGTERM 前台进程组 → 2 s → SIGKILL 前台进程组 → 关闭整个会话。可选"超时转后台"（§6） |
| 7 | 交互提示 | 执行中输出静止 ≥ 3 s 时检查光标行与 termios（Unix：ECHO 关闭 = 密码提示）；命中就提前返回 `waiting_for_input`，终端标签亮起"需要输入"，用户可以直接在 Agent 终端里输入（接管）。v1 **不给模型写 stdin 的工具**（§7） |
| 8 | 审批 | **不变**：`DescribePermission` / `PermissionPolicy.Evaluate` / `tool/approve` 一字不改；审批弹窗只多一行"将在 Agent 终端中运行"（§8） |
| 9 | 后台任务 | `run_in_background` 在终端模式下创建独立的命令会话，仍然注册到 `BackgroundTasks`，仍然写日志文件、仍然注入 `<task-notification>`；`task_stop` = 中断升级（§9） |
| 10 | 权限 | Agent 只能操作自己创建的会话；读取用户终端需要用户在标签菜单中"共享给 Agent"；共享只读、按 Agent 会话授权、会话结束即失效。pty host 按客户端角色做第二道检查（§11） |
| 11 | ACP `auth.terminal` | 实现并声明 `true`：用户点击"在终端中登录"后，在内嵌终端中运行"同一 Agent 程序 + 方法的 args/env"，退出码 0 即成功，然后重启 Agent 进程并重新 `initialize`（§12.1） |
| 12 | ACP `terminal: true` | **实现但默认不声明**（设置 `agents.acp.clientTerminal: false`）。理由：v2 草案已删除；声明后 TCode 要替 Agent 承担输出缓冲、截断、进程树清理；TCode 集成的 ACP Agent 都自带 shell 工具。实现成本在 broker 落地后约 2–3 人日（§12.2） |
| 13 | Codex | `outputDelta` / `terminalInteraction` 改用**只读迷你终端**（回放连接）渲染；同时修复现有"进度只显示最后一个片段"的问题。放在 M5 末尾，约 3–4 人日（§13） |
| 14 | 会话记录 | 终端模式与 Codex/ACP 的命令输出用只读迷你终端显示（视口外不建核心）；进程模式仍显示纯文本。模型看到的始终是纯文本（§14） |
| 15 | 连接与持久化 | AgentHost 用 Desktop 签发的受限令牌连接 pty host；host 未运行时经 Host→UI 的 `terminal/ptyHostEndpoint` 获取（04 §13.2）。**确认 Agent 终端默认不持久化**（`persist: false`），退出 TCode 时不保存快照，需要回看时用会话记录中的日志与 `.vt`（§3.3） |

---

## 1. 现状与约束

### 1.1 现有 bash 工具 [已核实]

| 项 | 现状 | 来源 |
|---|---|---|
| 进程 | `ProcessStartInfo` + 重定向 stdout/stderr，stdin 立即关闭；`ShellResolver` 选 shell（Unix 只接受 bash/zsh；Windows 依次 Git Bash → pwsh） | `src/TCode.Agent.Coding/Tools/Shell/BashTool.cs:204-230`；`ShellResolver.cs` |
| 环境 | `PAGER=cat`、`GIT_PAGER=cat`、`GIT_TERMINAL_PROMPT=0`、`TCODE_AGENT=1` | `BashTool.cs:222-226` |
| 信号 | 子进程继承 .NET 的 `SIGPIPE=SIG_IGN`；`trap - PIPE` 对 bash 无效（03 §12 实测）。修复：Unix 上经 `tcode-pty-spawn --exec` 启动 shell，exec 前复位全部信号（03 §12）。终端模式的 shell 由 helper 启动，天然没有这个问题 | 03 §12 |
| 超时 | 默认 120 s，最大 600 s；超时杀进程树，结果附"timed out" | `BashTool.cs:15-16,95-115` |
| 输出 | 完整内容流式写 `~/.tcode/tmp/<session>/bash-*.log`；交给模型保留尾部 2000 行或 50 KB；被截断时附日志路径；未截断时删除日志 | `OutputCapture.cs:7-13`；`BashTool.cs:132-160` |
| 进度 | 每 250 ms 推一次"截至目前的输出快照"（`ToolExecutionUpdated.Text`，最多 16 K 字符），UI 用它**替换**结果文本 | `BashTool.cs:239-262`；`src/TCode.Desktop/ViewModels/SessionViewModel.cs:351-357` |
| 后台 | `run_in_background` → `BackgroundTasks.Start("bash", …, stop)`，结束时 `Complete` → 会话注入 `<task-notification>`；`task_stop` 调用 stop 委托 | `BashTool.cs:84-97,166-182`；`Tools/BackgroundTasks.cs`；`TaskStopTool.cs` |
| 审批 | `DescribePermission` 返回 `ToolAccess.Execute + Command`；`PermissionPolicy.Evaluate`：只读白名单放行，其余在 Ask 档询问，可"本会话允许此命令前缀" | `BashTool.cs:58-62`；`src/TCode.Agent.Coding/Workspace/PermissionPolicy.cs:75-82` |
| 并发 | `ToolConcurrency.Exclusive`：同一批调用串行 | `BashTool.cs:56` |
| 宿主能力注入 | `ToolContext.Services`（`ToolServices` record）注入网页抓取、子 Agent 协调、MCP 等宿主能力 | `src/TCode.Agent.Coding/Tools/ToolServices.cs:7-22` |

### 1.2 外部运行时 [已核实]

- ACP：`initialize` 声明 `terminal:false`、`auth.terminal:false`（`src/TCode.Runtimes/Acp/AcpSession.cs:94-96`）；`ToolCallContent` 的 `terminal` 显示为 `[terminal output unavailable]`（同文件 `:812`）；`-32000` 时只自动调用白名单方法，否则提示用户自己去终端登录（`:195-220`）。
- Codex：`item/commandExecution/outputDelta` 把**片段**交给 `ToolProgressAsync`（`src/TCode.Runtimes/Codex/CodexSession.cs:423-429`），而 UI 把每次进度文本当作**完整结果**替换（`SessionViewModel.cs:351-357`）。**结论：Codex 命令执行中界面只显示最后一个片段**，这是现存缺陷，§13 一并修复。`terminalInteraction` 未处理。

### 1.3 必须遵守的上位决策

- **D3**：终端会话只在 `tcode-ptyhost`。AgentHost 不持有 PTY，只是 pty host 的第二个客户端。因此"可见终端模式"的一切 PTY 操作都经 IPC 到 pty host。
- **D8**：Agent 与终端打通属于 v1 范围（M5）。
- **01 §6.4**：Agent 订阅者使用"按需读取"，**不订阅原始字节流，不参与流控**。本文的流式进度据此改为轮询 `command/output`（§5.4）。
- **01 §9**：命令边界依靠 OSC 133；无 shell 集成时退回进程模式或空闲检测 + 哨兵。本文在此基础上把 `none` 档具体化为 `exec`（§4.4），与 §9 的"退回进程模式"等价但保持可见。
- **研究 05 §10 第 5 条**：交互终端的 `terminal` 配置**不影响** Agent 的 bash 工具（VS Code 的 `agentHostProfile` 与 `defaultProfile` 分离）。因此 Agent 终端使用自己的 profile 设置（§2.2）。

---

## 2. bash 工具的两种模式

### 2.1 模式定义

| 模式 | 执行方式 | 用户可见 | 有 TTY | cwd / 环境变量跨调用保留 | 退出码来源 |
|---|---|---|---|---|---|
| `process` | 现有实现：`shell -c <cmd>`，管道重定向，stdin 关闭 | 只在会话记录中 | 否 | 否 | 进程退出码（精确） |
| `terminal` | 命令在 pty host 中的 Agent 终端里执行（§4 的策略之一） | 是："Agent 终端"标签 | 是 | `rich`/`basic`：是；`exec`：否 | OSC 133;D（`rich`/`basic`）或进程退出码（`exec`） |

### 2.2 设置项（`settings.json` 的 `agent.shell` 节）

> `docs/settings.md` 目前没有 `agent` 节，落地时新增（另一个会话正在改 `TCode.Config`，届时合并）。热重载：只影响**之后**的工具调用。

| 键 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `agent.shell.mode` | `auto` \| `terminal` \| `process` | `auto` | 见 §2.3 |
| `agent.shell.profile` | string \| null | null | Agent 终端使用的 shell。null = 沿用 `ShellResolver` 的选择（Unix bash/zsh，Windows Git Bash → pwsh），**不使用**交互终端的默认 profile（研究 05 §10）。可以填 `terminal.profiles` 中的 id |
| `agent.shell.onTimeout` | `interrupt` \| `background` | `interrupt` | 超时后中断（与进程模式一致），或转成后台任务（VS Code 的做法）（§6） |
| `agent.shell.reveal` | `never` \| `onInput` \| `always` | `onInput` | Agent 终端标签何时自动前置：从不 / 需要输入时 / 每条命令。标签本身总是创建，只是不抢焦点 |
| `agent.shell.idleInputSeconds` | number | 3 | 执行中输出静止多少秒后做"是否在等输入"检查（§7） |
| `agent.shell.preventHistory` | bool | true | Agent 键入的命令不写入用户的 shell 历史（§2.5） |
| `agents.acp.clientTerminal` | bool | false | 是否向 ACP Agent 声明 `terminal:true`（§12.2） |

### 2.3 `auto` 的判定

逐条检查，第一条命中即用 `process`；全部不命中用 `terminal`：

| # | 条件 | 原因 |
|---|---|---|
| 1 | pty host 不可用（启动失败、握手失败、版本 / 原生库哈希不匹配） | 退化，不让工具失败。结果末尾附一行 `[ran without terminal: <原因>]` 供排查（不发给模型，放 Details） |
| 2 | 当前没有 UI 客户端连接 AgentHost（无头运行、`--self-test`、外部脚本驱动） | 可见终端没有观众；进程模式更确定 |
| 3 | 调用来自**子 Agent**，且父会话也在执行命令 | 避免同时冒出多个终端标签；子 Agent 单独执行时仍可用终端 |
| 4 | 命令被判定为"非交互的纯查询"（`ReadOnlyCommands.IsReadOnly` 为真，例如 `ls`、`git status`、`rg`） | 这类命令只读、很快、输出给模型用，放进终端只会刷屏。**[推断]** 需要 dogfood 后确认是否保留这一条 |
| 5 | Windows 上 `agent.shell.profile` 解析为 `cmd.exe` | cmd 没有 shell 集成，且 `exec` 档对 cmd 的引号规则与 Git Bash 不同；保持现有行为 |

`terminal` 档（显式设置）跳过 2–4 条，只在第 1 条时退化。`process` 档永远走现有实现，**`BashTool` 现有代码保留不动**，两条路径通过 `ToolServices.Terminal` 是否为 null 切换（§10.1）。

**为什么默认 `auto` 而不是 `process`** [推断]：D8 把"Agent 与终端打通"定为 v1 目标，可见性和"卡住时可接管"是主要卖点；确定性的风险由三点控制：①输出永远经 formatter 转成纯文本；②没有 shell 集成就不键入命令（`exec`）；③PAGER / 编辑器 / 交互提示被环境变量关掉（§2.5）。如果 M5 dogfood 发现模型行为退化，把默认值改成 `process` 只是一行设置。

### 2.4 两种模式对模型可见的差异

工具描述（`BashTool.Description`）按当前生效的模式生成：

| 描述片段 | `process` | `terminal`（rich/basic） | `terminal`（exec） |
|---|---|---|---|
| 进程模型 | "Each call runs in a fresh process: `cd` and environment variables do not persist" | "Commands run in a persistent terminal session: `cd` and exported variables persist between calls. The working directory is reported after each command." | 同 `process` |
| stdin | "Avoid interactive commands; stdin is closed." | "Avoid interactive commands. If a command waits for input, the call returns early and the user may answer in the terminal." | 同左 |
| 输出 | 不变 | 不变（纯文本，无颜色码） | 不变 |

**模式在一个会话中途变化时**（例如 pty host 崩溃后退化），下一条工具结果附一行说明 `"[Note: commands now run in a fresh process each time; cd no longer persists]"`，避免模型依赖已失效的 cwd。

### 2.5 Agent 终端的环境

环境的基础是 **Desktop 的 BaseEnvironment**：Desktop 在启动之初抓取原始环境，经 `host/configure` 发给 pty host（01 §3.3、07 §4）；`LaunchBuilder` 在它之上叠加 profile 与集成变量。**Agent 终端不使用 AgentHost 自己的进程环境**（其中有 `TCODE_PTYHOST_*` 等内部变量，且可能被 AgentHost 修改过）。由于 Agent 会话是 agent 连接创建的，而 07 §4 写的是"使用发起请求的那个 Desktop 的 BaseEnvironment"，需要 04 / 07 明确：**agent 连接创建的会话使用最近一次由 full 连接发送的 BaseEnvironment**（见 §18）。

在此之上追加 / 覆盖：

| 变量 | 值 | 原因 / 来源 |
|---|---|---|
| `TCODE_AGENT` | `1` | 与进程模式一致（`BashTool.cs:226`） |
| `AI_AGENT` | `tcode` | 跨厂商约定，CLI 据此输出机器可读格式。VS Code 设置 `AI_AGENT` 与 `COPILOT_AGENT=1` [已核实 vscode@4765bd3 `chatAgentTools/browser/toolTerminalCreator.ts:151-160`] |
| `PAGER`、`GIT_PAGER` | `cat` | 防止分页器卡住；与进程模式一致 |
| `GIT_TERMINAL_PROMPT` | `0` | 与进程模式一致 |
| `GIT_EDITOR` | `:` | 防止 `git commit --amend`、`rebase` 打开编辑器（VS Code 同，`toolTerminalCreator.ts:163-167`） |
| `GIT_MERGE_AUTOEDIT` | `no` | 同上 |
| `DEBIAN_FRONTEND` | `noninteractive` | 防止 apt 弹出 TUI 对话框（VS Code 同） |
| `TCODE_PREVENT_HISTORY` | `1`（`preventHistory` 为 true 时） | TCode 的 shell 集成脚本读到后：bash 设 `HISTFILE=/dev/null` 或 `HISTCONTROL=ignorespace` 并在命令前加空格；zsh `setopt HIST_IGNORE_SPACE`；pwsh 设 `Set-PSReadLineOption -AddToHistoryHandler`。VS Code 的 `VSCODE_PREVENT_SHELL_HISTORY` 做法相同 [已核实 `toolTerminalCreator.ts:177-185`]；脚本细节由 07 号文档实现 |
| `TCODE_PTYHOST_*`、`TCODE_PTY_HELPER` 等内部变量 | **删除** | 防止终端中的命令拿到 pty host 的连接参数（§11.2）；host 构造环境时本来就剔除（04 §13.2 第 5 条），这里再列一次作为测试断言 |
| `TCODE_AGENT_ZSH_FIXUPS` | `1`（zsh） | 关闭 `!` 历史展开（双引号里的 `!` 会卡在 `dquote>`），开启 `INTERACTIVE_COMMENTS`。VS Code 同（`toolTerminalCreator.ts:187-192`） |

**不设置 `NO_COLOR`**：颜色对用户有价值，formatter 会去掉颜色码，模型不受影响。

---

## 3. Agent 终端：数量与复用

### 3.1 规则

| 对象 | 规则 |
|---|---|
| 前台命令 | 每个 Agent 会话（主会话、每个子 Agent 各自）维护一个**持久的 Agent 终端**（shell 会话），前台命令依次复用 |
| 复用前检查 | 会话已退出 → 新建；前一条命令仍在运行（等待输入或已转后台）→ 新建一个，旧的保留给用户；用户在空闲提示符处输入过字符 → 先发 `^U`（清除当前输入行，不会杀掉任何东西；VS Code basic/none 策略的做法 [已核实 `executeStrategy/basicExecuteStrategy.ts:207`、`noneExecuteStrategy.ts:84`]） |
| 并存上限 | 每个 Agent 会话最多 4 个 Agent 终端（含忙碌的），超过时最旧的空闲终端被关闭 [推断：数值待 dogfood] |
| 后台命令 | 每条后台命令一个独立的 **命令会话**（`session/create{kind: agent, launch{command: <shell>, args: [-c, <cmd>]}}`，直接 exec，不经交互 shell），退出即结束 |
| 子 Agent | 与父会话不共享终端（并行执行时会互相干扰）；终端标签标题前缀子 Agent 名 |
| 会话关闭 | Agent 会话关闭时（`BackgroundTasks.StopAllAsync` 的同一时机），pty host 关闭它的全部会话；标签保留为"已结束"状态，直到用户关闭（可以回看） |
| TCode 重启 | Agent 终端不保存 revive 数据（`persist: false`，即 04 §4.3 的默认值），理由见 §3.3 |

### 3.2 为什么不是"每条命令一个终端"

| 方案 | 优点 | 缺点 |
|---|---|---|
| 每会话一个持久终端（采用） | 与用户使用终端的方式一致；`cd`、`source venv/bin/activate`、`export` 跨调用保留，省掉模型的 `cd x &&` 前缀；标签少 | 前一条命令的副作用会影响后一条（例如误开的子 shell）；需要 shell 集成才能可靠判断边界 |
| 每条命令一个终端 | 退出码精确、互不干扰 | 一次会话几十个标签；shell 启动开销（zsh + 插件可能 200–800 ms）每条都付；cwd 不保留，与"可见终端"的初衷不符 |

折中：持久终端用于有 shell 集成的情况；没有 shell 集成时（§4.4）退回"每条命令一个命令会话"，但**仍复用同一个标签**（标签内依次显示多个会话的内容，见 §15），用户感知不到会话切换。

### 3.3 持久化：确认"Agent 终端默认不持久化"

04 §4.3 规定 `kind: agent` 的会话默认 `persist = false`（04 §19 第 11 条请 09 确认）。**本文确认这个默认值**，并补充两点：

| 情形 | 行为 | 理由 |
|---|---|---|
| UI 崩溃 / 重载 | Agent 终端照常运行（host 独立于 Desktop，D3）；Desktop 重连后按 `kind: agent` 重新建标签 | 这是 host 本身的保活，不涉及 revive |
| AgentHost 崩溃重启 | 会话不关；归属按 `agentSessionId` 找回（§10.5） | 同上 |
| 用户退出 TCode（`onQuit: revive`，默认） | Agent 终端**不保存快照**，随 host 退出关闭；`host/quitCheck` 默认不列出 Agent 终端（`includeAgent: false`），但正在运行命令的 Agent 终端要在退出确认里单独列一行"Agent 正在运行 N 条命令" | ① Agent 会话不会在重启后自动续跑，恢复出一个"死"的 Agent 终端只会让用户误以为 Agent 还在工作；② Agent 终端的输出可能很大（回滚设得大，§5.2），写盘代价高；③ 需要回看时，会话记录里已有每条命令的纯文本日志与 `.vt` 快照（§5.3、§14），信息不丢 |
| `onQuit: keepAlive` | Agent 终端与用户终端一样保留运行 | keepAlive 是用户明确要求"别停"，不区分会话种类 |
| 用户想保留某个 Agent 终端 | 标签菜单"转为普通终端"：Desktop 调 `session/setLabel` 并通过 `tags` 标记，之后 `persist` 按用户终端处理（**需要 04 支持在运行中修改 `persist`**，可以作为 `session/setLabel` 的附加字段） | 用户接管后（§7.2），这个终端事实上已是用户的 |

---

## 4. 执行策略

### 4.1 shell 集成等级的判定

在 pty host 的命令跟踪器（01 §3 "命令跟踪"）里，对每个 shell 会话维护 `IntegrationQuality`：

| 等级 | 判定条件 | 来源 / 说明 |
|---|---|---|
| `rich` | 收到带本会话 nonce 的 `OSC 633 ; P ; HasRichCommandDetection=True ; nonce=…`，**或**同一条命令依次收到 133 A、B、C、D（D 带退出码） | VS Code 以 `HasRichCommandDetection` 属性为准 [已核实 `toolTerminalCreator.ts:240-262`]。TCode 自己的集成脚本（07）必须发出这个属性和 nonce |
| `basic` | 收到过任意 133 序列，但不满足 rich | 第三方 prompt 主题、用户自装的集成脚本 |
| `none` | 会话启动后 `T_si` 内没有收到任何 133 | `T_si` 默认 5 s（本地）；VS Code 的同类超时可配置 [已核实 `toolTerminalCreator.ts:80-88`] |

实现要点：

- **libghostty-vt 解析 OSC 133，不解析 OSC 633**。133 通过 `GHOSTTY_TERMINAL_OPT_SEMANTIC_PROMPT` 回调给出 `kind`（PROMPT_START / INPUT_START / OUTPUT_START / COMMAND_END）、`has_exit_code`、`exit_code`、`command`（kitty 的 `cmdline=` 扩展）[已核实 ghostty@b3dc3d7 `include/ghostty/vt/terminal.h:1405-1500`]；633 不在库的 OSC 表里（`src/terminal/osc` 下无 633），会经 `GHOSTTY_TERMINAL_OPT_UNKNOWN_SEQUENCE` 回调交出原文 [已核实 `terminal.h:104,144-157`]。因此命令跟踪器 = 133 回调 + 未知序列回调中的 633 解析。
- **对 01 §6.1 的影响**：`ITerminalEffects` 需要增加一个 `UnknownOsc(ReadOnlySpan<byte> content)` 回调（见文末"待决问题"）。
- **等级只升不降**（VS Code `refreshShellIntegrationQuality` 同样只升级 [已核实 `toolTerminalCreator.ts:136-147`]）：在 `none` 会话里用户后来手动 source 了集成脚本，下一条命令就按新等级执行。
- **nonce**：每个会话启动时生成 128 bit 随机数，经环境变量 `TCODE_SI_NONCE` 交给集成脚本（脚本读取后立即 unset，研究 05 §3.3）。只有带正确 nonce 的 633 才算数；**不带 nonce 的 133;D 也接受为命令结束，但 Agent 结果里标注 `boundary: unverified`**——程序输出可以伪造 133;D 让命令"提前结束"，结果只是模型拿到不完整输出，没有安全后果；伪造 633;E 命令行才有安全后果（只用于显示，不执行，研究 03 §2.4）。

### 4.2 `rich`

```
前置：会话处于 Prompt 状态（最近事件是 133 A 或 B，且没有正在执行的命令）
1. 记录 baseline = 当前 Commands.Count；记录输出日志序号 seq0
2. 写入命令（§4.5）
3. 等待下列事件中最先发生的：
   a. 新的 CommandRecord 收到 D            → 完成（exitCode = D 的值；无值时 null）
   b. 会话进程退出                          → 完成（exitCode = 进程退出码，reason=processExit）
   c. 进入备用屏幕（DECSET 1049/1047/47）   → 返回 reason=alternateScreen（命令仍在运行，§7.3）
   d. 输入等待判定命中（§7）                → 返回 reason=waitingForInput（命令仍在运行）
   e. 超时 / 取消                           → §6
4. 取输出：§5
```

与 VS Code 的差异：VS Code 的 rich 策略在 `runCommand` 中可能先发 `^C` 清空提示符，需要额外过滤"上一条命令被 ^C 产生的 D"[已核实 `executeStrategy/richExecuteStrategy.ts:100-118`]。TCode 只在**确认 Prompt 状态**时键入，且清空输入用 `^U` 而不是 `^C`，所以不存在这个误判。

### 4.3 `basic`

步骤同 rich，结束判定换成（任一）：

1. 收到 D（可能没有退出码 → `exitCode: null`）；
2. 在 C 或 D 之后又收到 A（回到提示符），之后输出静止 `idle = 1 s`；VS Code `trackIdleOnPrompt` 的状态机（Initial → Prompt → Executing → PromptAfterExecuting）[已核实 `executeStrategy/executeStrategy.ts:175-332`]；
3. 兜底：执行态下输出静止 30 s，且光标行匹配常见提示符模式（`$`、`#`、`%`、`>`、`❯`、`PS C:\>` 结尾）——VS Code `detectsCommonPromptPattern` 的同一组正则 [已核实 `executeStrategy.ts:64-110`]。

`basic` 的输出可能夹带提示符文本（VS Code 文档化了同样的局限 [已核实 `basicExecuteStrategy.ts` 类注释]）。TCode 处理：用命令行文本在输出首行做一次"去回显"（与 VS Code `stripCommandEchoAndPrompt` 同义），末尾若是提示符行则去掉。

### 4.4 `none` → `exec`

没有 shell 集成时**不往 shell 里键入命令**，改为：

```
command/run { strategy: process, create: { kind: agent, agentSessionId, launch: { command: <shell>, args: [<-c 或 -Command>, <cmd>] }, cwd: <上一条命令结束时的 cwd 或工作区根>, env: §2.5, persist: false } }
```

- 在同一个 Agent 终端标签内显示（§15），用户看到的是"一条命令一段输出"；
- 退出码 = 进程退出码，精确；没有边界判定问题；
- 代价：cwd / 变量不跨调用保留——工具描述同步切换成 `process` 的措辞（§2.4）；
- 这就是 01 §9 所说的"退回进程模式"，但保留了 TTY 和可见性。

**为什么不用 VS Code 的 none 策略（空闲检测）或哨兵**：VS Code 的 none 策略只能等输出静止后猜测命令结束、拿不到退出码 [已核实 `noneExecuteStrategy.ts`]。哨兵（命令后追加 `; printf '\e]133;D;%s\a' $?`）在本地 shell 上可行，但会被回显到终端、对 `cmd.exe` 无效、命令里的 `exit`/`exec` 会让它永远不出现。TCode 能自己 spawn 进程，`exec` 严格更好。哨兵留给以后"在 ssh 会话里替 Agent 执行"的场景（不在 v1）。

### 4.5 写入命令

| 情况 | 写入方式 |
|---|---|
| 单行，≤ 1000 字节 | 原样写入 + `\r` |
| 多行或 > 1000 字节，shell 已开启 bracketed paste（模式 2004，从镜像核心的模式表读取） | `ESC[200~` + 命令 + `ESC[201~` + `\r`（命令中的 `ESC[201~` 先剔除，防止提前结束粘贴；与用户粘贴的检查相同，01 §8） |
| 多行或 > 1000 字节，且未开启 2004 | 把命令写入 `~/.tcode/tmp/<session>/cmd-<id>.sh`（pwsh：`.ps1`），键入 `. '<path>'`（pwsh：`. '<path>'`）。macOS 规范模式下单行上限 `MAX_CANON` = 1024 [推断：zle/readline 处于非规范模式时不受此限，但无行编辑器的 `sh` 受限] |
| `preventHistory` 且 shell 为 bash/zsh | 命令前加一个空格（配合 `HIST_IGNORE_SPACE` / `HISTCONTROL=ignorespace`） |

VS Code 在 macOS 或多行命令时强制使用 bracketed paste [已核实 `richExecuteStrategy.ts:178-179`]；TCode 的规则是它的超集。

### 4.6 与 VS Code 的对照

| 项 | VS Code | TCode |
|---|---|---|
| 等级 | rich / basic / none，按能力事件和 200 ms 窗口判定 | 同三档；`none` 改为 `exec` |
| 命令边界 | OSC 633（xterm.js 在渲染进程里解析） | OSC 133（libghostty，镜像核心，在 pty host）+ 633 nonce（未知序列回调） |
| 执行器位置 | 渲染进程（workbench） | pty host（贴着镜像核心） |
| 超时默认 | sync 模式默认不超时，超时后转后台 [已核实 `runInTerminalTool.ts:1892-1898,2233-2245`] | 默认 120 s 后中断（与现有进程模式一致），可选转后台 |
| 静默转交 | 输出静止 5 min 时把控制权交回模型 [已核实 `terminalChatAgentToolsConfiguration.ts:43`；`runInTerminalTool.ts:2193-2203`] | 不需要：超时已覆盖 |
| 输出上限 | 20000 字符，首 500 + 尾部 [已核实 `outputHelpers.ts:9-41`] | 沿用 TCode 现有的尾部 2000 行 / 50 KB + 日志文件 |
| 模型写 stdin | 有 `send_to_terminal` 工具 | v1 没有（§7.4） |

---

## 5. 输出读取

### 5.1 来源

镜像核心的 `CommandRecord`（01 §6.1 `TerminalCore.Commands`）：

```csharp
public sealed record CommandRecord(
    int Index,
    GridRef PromptStart, GridRef? InputStart, GridRef? OutputStart, GridRef? End,   // tracked grid ref（libghostty grid_ref_tracked）
    int? ExitCode, string? CommandLine, bool CommandLineVerified,                    // Verified = 来自带 nonce 的 633;E
    string? Cwd,                                                                      // OSC 7 / 633 P;Cwd，不可信，只用于显示与下一条 exec 的 cwd（需校验目录存在）
    long OutputStartSeq, long? EndSeq,                                                // 输出日志序号
    DateTimeOffset StartedAt, DateTimeOffset? EndedAt,
    bool OutputEvicted);                                                              // 输出起点已被挤出回滚
```

- 取文本：`TerminalCore.Format(new FormatRequest(Target.CommandOutput(index), TextFormat.Plain, Unwrap: true, TrimTrailingSpaces: true))`。底层是 `ghostty_terminal_select_output` 得到命令输出选区，再用 formatter 输出纯文本 [已核实 ghostty@b3dc3d7 `include/ghostty/vt/selection.h:846-866`；`include/ghostty/vt/formatter.h:135-226`]。
- 输出区间 = `OutputStart`（133 C）到 `End`（133 D）。`basic` 没有 C 时用"命令行所在行的下一行"作为起点。
- **formatter 的天然好处**：`\r` 刷新的进度条、`\b`、光标回退覆盖都已折叠成最终屏幕状态；颜色和 OSC 8 已去除。这比"对原始字节流去 ANSI"更接近用户看到的结果，也更省 token。

### 5.2 交给模型的文本

```
[Output truncated: showing the last lines of 48213. Full output: /Users/x/.tcode/tmp/<session>/bash-….log]   ← 仅截断时
<纯文本输出，或 "(no output)">

Command exited with code 2.                                             ← 非 0 时
[Current directory: /path/to/sub]                                       ← cwd 与工作区根不同时（rich/basic）
```

规则：

1. **截断**：与 `OutputCapture` 完全相同——保留尾部 2000 行或 50 KB（先到为准）[已核实 `OutputCapture.cs:7-13`]。复用 `OutputCapture.Tail()` 的算法，不另起一套。
2. **日志文件**：命令执行期间，broker 每 250 ms 用 `command/output { since: cursor }` 拉取增量纯文本并追加写入日志（§5.4），所以日志与模型看到的文本格式一致。截断时在结果首行给出路径（与现在相同），模型可以用 `read` 工具读完整内容。
3. **回滚溢出**：若 `OutputEvicted` 为真（命令输出超过镜像核心的回滚上限），日志文件仍然完整（增量是在溢出前拉走的），结果首行写 `[Output exceeded terminal scrollback; full output: <log>]`。**镜像核心回滚上限对 Agent 终端单独设大**：建议 50 000 行（libghostty 按字节计，约 50 MB 上限），因为 Agent 终端不参加 revive。
4. **非 UTF-8**：formatter 输出 UTF-8，非法字节已在解析阶段替换为 U+FFFD。
5. **备用屏幕内容不进入结果**：命令进入备用屏幕时只返回进入前的主屏输出 + 提示（§7.3）。

### 5.3 VT 版本（供会话记录渲染）

同一区间再用 `TextFormat.Vt` 导出一份**带样式的 VT 文本**（formatter 支持 VT 输出 [已核实 `formatter.h`]），写入 `bash-….vt`，路径放进 `CommandDetails`（§14）。它不交给模型，只用于会话记录里的只读迷你终端回放。上限 1 MB，超出时只保留尾部（UI 显示"已截断，打开日志查看全部"）。

### 5.4 流式进度

- broker 每 250 ms 调 `command/output { commandId, since: cursor, format: plain, maxBytes: 64 KiB }`，把新增文本追加到日志、更新内存尾部，并按现有约定推 `ToolExecutionUpdated.Text = 截至目前的尾部快照`（最多 16 K 字符，与现在一致）。
- 同一个 `ToolExecutionUpdated` 的 `DetailsJson` 带上 `{ terminalSessionId, commandId }`，Desktop 据此在会话记录里**直接 attach 到 pty host 会话**显示实时迷你终端（Desktop 本来就是 pty host 的客户端），而不是渲染纯文本快照（§14；注意 §14.2 的流控约束）。
- 不订阅原始字节：符合 01 §6.4；即使模型侧拉取停顿，也不会反压 PTY。

---

## 6. 超时、中断与取消

### 6.1 触发

| 触发 | 来源 |
|---|---|
| 超时 | `timeout` 参数（默认 120 s，最大 600 s，与进程模式相同） |
| 取消 | 用户停止本轮（`cancellationToken`） |
| `task_stop` | 后台任务 |

### 6.2 升级序列（建议由 pty host 的 `command/interrupt` 执行，见 §10.4；也可由 broker 用 `session/input` + `session/signal` + `session/kill` 编排）

| 步 | 动作 | 等待 | 说明 |
|---|---|---|---|
| 1 | 向 PTY 写 `0x03`（`^C`） | 2 s | 经线路规程变成 SIGINT 发给**前台进程组**，与用户按 Ctrl+C 完全相同；Windows 上 ConPTY 把它转成 `CTRL_C_EVENT`（输入未处于 VT 原始模式时）[推断：需在 M5 验证 ConPTY + pwsh/Git Bash 的行为] |
| 2 | 再写一次 `0x03` | 2 s | 部分程序第一次 ^C 只取消当前操作（npm、python REPL） |
| 3 | Unix：`killpg(tcgetpgrp(master), SIGTERM)`；Windows：跳过 | 2 s | 程序把 ISIG 关掉（raw 模式，如 vim、部分 TUI）时 ^C 不会产生信号，必须直接发信号 |
| 4 | Unix：`killpg(…, SIGKILL)`；Windows：结束 shell 以外的子进程树（ConPTY 下用 Job/快照枚举 shell 的子进程） | 1 s | |
| 5 | 关闭整个会话（SIGHUP 进程组 → 关闭 master → SIGKILL） | — | Agent 终端被标为"已结束"，下一条命令新建终端 |

- 每一步之后只要命令结束（D、回到提示符、或 `exec` 会话退出）就停止升级。
- `tcgetpgrp` 返回 shell 自己的 pgid 时（命令已结束，只是没收到 D），不发信号。
- 超时的工具结果：`Command timed out after 120 seconds and was interrupted (Ctrl+C).`；与进程模式措辞对齐，并说明停在第几步。

### 6.3 超时转后台（`agent.shell.onTimeout: background`）

不发 `^C`；把这条命令登记为后台任务（`BackgroundTasks.Start("bash", …)`，stop 委托 = §6.2），立刻返回 `"Command is still running after 120 s; continued in background as task b3. Output file: …"`；该 Agent 终端标为"忙碌"，下一条前台命令新建终端（§3.1）。完成时照常注入 `<task-notification>`。

---

## 7. 交互提示：检测与用户接管

### 7.1 检测（pty host 内，命令处于执行态时）

每当输出静止 `idleInputSeconds`（默认 3 s），执行一次检查；命中任一高置信条件即发 `command/inputNeeded` 通知：

| 信号 | 置信度 | 判定 | 来源 |
|---|---|---|---|
| termios：`ICANON` 开、`ECHO` 关 | 高（密码类） | Unix：`tcgetattr(master)` 读到的是从设备的 termios [推断：Linux 与 macOS 都应返回从设备设置，需在 libtcodepty 里实测]。`sudo`、`ssh`、`gpg` 读密码时都会关 ECHO | — |
| 光标行匹配 y/n | 高 | `(y/n)`、`[Y/n]`、`(yes/no)`、`[no/yes]` 等结尾 | VS Code `outputMonitor.ts:667-672` [已核实] |
| 光标行匹配密码提示 | 高（密码类） | `/password(?: for [^:]+)?:\s*$/i`，以及 passphrase、token、OTP、verification code | VS Code `outputMonitor.ts:619,688-692` [已核实] |
| 光标行匹配 "press any key"、`(END)`、`--More--` | 高 | 分页器（PAGER 被覆盖的程序仍可能自带） | VS Code `outputMonitor.ts:368-371,693-705` [已核实] |
| Linux：前台进程 `/proc/<pid>/wchan` 为 `n_tty_read` / `wait_woken` | 中 | 前台进程在读 tty | [推断：仅 Linux；只作为辅助] |
| 光标行以 `:`、`?`、`>` 结尾且前台进程不是 shell | 低 | 不单独触发，只在 30 s 静止后触发 | — |

**密码类**单独标记 `sensitive: true`。

### 7.2 发生后的行为

1. broker 立即结束本次工具调用，结果：

   ```
   <到目前为止的输出>
   
   [The command is waiting for input and is still running in the Agent terminal (session t2).
    The user has been asked to respond in the terminal. Do not retry the command.]
   ```

   `IsError = false`，Details 中 `status: "waitingForInput"`。命令**不被中断**；该终端转为"忙碌"，登记为后台任务（类型 `bash`，描述加"（等待输入）"），结束时注入 `<task-notification>`——这样用户在终端里回答之后，模型会在下一轮知道结果。
2. Desktop：Agent 终端标签显示"需要输入"徽标；`agent.shell.reveal = onInput`（默认）时前置标签但**不抢键盘焦点**；会话记录中的工具卡片显示"在终端中回答"按钮（聚焦该终端）；窗口不在前台时发系统通知。
3. `sensitive` 时文案明确"请直接在终端输入，TCode 不会把它发给模型"。
4. 用户在 Agent 终端输入任何内容 → pty host 发 `session/userInput { sessionId }`（只发事件，不带内容），broker 在后续结果中注明 `user typed input into the terminal`。**用户输入的内容永远不进入模型上下文**（终端回显的输出除外——密码不会回显）。

### 7.3 备用屏幕

命令进入备用屏幕（vim、less、htop、fzf……）时：立即返回 `[The command opened a full-screen program (alternate screen). It is still running in the Agent terminal; the user can interact with it. Avoid full-screen programs; use non-interactive flags.]`，并按 §7.2 登记为后台任务。VS Code 对备用屏幕也是立即返回 [已核实 `richExecuteStrategy.ts:181-193`]。

### 7.4 v1 不提供"模型写 stdin"的工具

VS Code 有 `send_to_terminal`。TCode v1 不提供，理由：①写入可能是对密码提示的回答，秘密会经过模型；②模型可以用非交互参数重跑（`-y`、`--yes`、`CI=1`）；③Codex 的 `writeStdin` 审批（`kind: writeStdin`，`docs/protocols/codex-app-server.md` §6.2）说明这类写入需要单独审批，UX 要另行设计。**以后要加时**：工具名 `terminal_send`，`ToolAccess.Execute`，审批标题"向终端写入：<内容>"，`sensitive` 提示期间直接拒绝。

---

## 8. 审批

- `BashTool.DescribePermission` 不变 → `PermissionPolicy.Evaluate` 不变 → `tool/approve`（`ToolApprovalRequest`）不变 [已核实 `PermissionPolicy.cs:75-82`；`src/TCode.Rpc.Contracts/Dtos.cs:242-253`]。只读白名单、"本会话允许此命令前缀"、AutoApprove / FullAccess 档位的语义与进程模式完全一致。
- **审批在键入命令之前完成**：broker 只在 `ExecuteAsync` 内被调用，而 `ExecuteAsync` 在审批钩子放行之后才运行。被拒绝的命令不会出现在终端里。
- 审批弹窗新增一行只读说明"将在 Agent 终端中运行"（从 `ToolApprovalRequest.ArgumentsJson` 无法得知模式，所以 `ToolPermission` 不变，改为在 `ToolApprovalRequest` 增加可选字段 `Target: "terminal" | "process"`；RPC 协议版本 +1。注意：另一个会话正在修改 `TCode.Rpc.Contracts`，合并时协调版本号）。
- **用户在 Agent 终端里自己输入的命令不经过审批**：那是用户本人的操作，与在自己的终端里输入相同。
- ACP 的 `terminal/create`（若启用，§12.2）：ACP Agent 在调用前会通过 `session/request_permission` 请求批准，TCode 的审批照常生效；`terminal/create` 本身不再二次询问（避免双重弹窗）[推断：需要用实际 Agent 验证它们是否总在 create 之前请求权限；若某 Agent 不请求，则对该 Agent 关闭 clientTerminal]。

---

## 9. 与后台任务（BackgroundTasks）的关系

| 项 | 进程模式（现状） | 终端模式 |
|---|---|---|
| 启动 | `Process` + 输出泵 | `TerminalBroker.StartBackgroundAsync(...)`（`kind: agent` 的命令会话，exec `shell -c cmd`） |
| 登记 | `BackgroundTasks.Start("bash", description, logPath, stop)` | 相同；`stop` = `command/interrupt` 升级序列 |
| 输出文件 | `OutputCapture` 写日志 | broker 每 250 ms 拉增量纯文本写同一格式的日志（§5.4）；另写 `.vt` |
| 完成 | `WaitForExitAsync` → `Complete(task, status, 摘要)` | `session/exited { exitCode, signal }` → 同一个 `Complete` 调用，摘要格式不变（"Exit code: … last 20 lines"） |
| 通知 | `<task-notification>` | 不变 |
| 可见 | 只在任务面板 | 任务面板 + Agent 终端标签（后台会话作为该标签的子页或单独的"任务终端"标签，§15） |
| 关闭会话 | `StopAllAsync` 杀进程 | `StopAllAsync` → broker 关闭该 Agent 会话的全部会话 |

`task_stop` 无需改动：它只调用 `BackgroundTasks.StopAsync(id)`。由等待输入 / 备用屏幕 / 超时转后台产生的"后台任务"（§6.3、§7.2）同样可以被 `task_stop` 停止。

---

## 10. TerminalBroker（AgentHost 内）

### 10.1 位置与依赖

```
TCode.Agent.Coding
  Tools/Shell/ITerminalExecutor.cs      ← 新接口（只用 BCL 类型），BashTool 依赖它
  Tools/ToolServices.cs                 ← 新增 ITerminalExecutor? Terminal { get; init; }
TCode.AgentHost
  Terminal/TerminalBroker.cs            ← 实现 ITerminalExecutor；ACP / auth.terminal 也用它
  Terminal/PtyHostEndpointProvider.cs   ← 环境变量 / Host→UI `terminal/ptyHostEndpoint`（§10.5）
  → TCode.Terminal.Protocol（PtyHostClient）
```

`TCode.Agent.Coding` 不引用任何 `TCode.Terminal.*` 项目（保持 01 §4 的依赖方向）：`BashTool` 只看到 `ITerminalExecutor`，`Services.Terminal == null` 时走现有进程路径。

### 10.2 C# API（设计稿）

```csharp
namespace TCode.Agent.Coding.Tools.Shell;

/// 宿主提供的"在可见终端中执行命令"的能力。线程安全；所有方法按 Agent 会话隔离。
public interface ITerminalExecutor
{
    /// 当前是否可用（pty host 已连接、有 UI 客户端）。BashTool 据此和 agent.shell.mode 选择路径。
    TerminalAvailability Availability { get; }

    /// 前台执行一条命令，直到完成、等待输入、进入备用屏幕、超时或取消。
    Task<TerminalCommandResult> RunAsync(TerminalCommandRequest request, IProgress<TerminalCommandProgress>? progress, CancellationToken ct);

    /// 以独立命令会话启动后台命令，立即返回。
    Task<TerminalBackgroundHandle> StartBackgroundAsync(TerminalCommandRequest request, CancellationToken ct);

    /// 关闭某个 Agent 会话的全部终端（会话关闭时调用）。
    Task CloseAllAsync(string agentSessionId);
}

public sealed record TerminalCommandRequest(
    string AgentSessionId, string ToolCallId, string Command, string Description,
    string WorkspaceRoot, TimeSpan Timeout, TimeoutBehavior OnTimeout, string LogPath);

public sealed record TerminalCommandProgress(string TailText, long TotalLines, string TerminalSessionId, string CommandId);

public sealed record TerminalCommandResult(
    TerminalCommandStatus Status,          // Completed / WaitingForInput / AlternateScreen / TimedOut / Interrupted / SessionLost
    int? ExitCode, string OutputText,      // 已按 §5.2 截断
    bool Truncated, string? LogPath, string? VtPath,
    string Strategy,                       // rich / basic / exec
    bool BoundaryVerified, string? Cwd,
    string TerminalSessionId, string CommandId,
    string? BackgroundTaskId);             // 等待输入 / 转后台时登记的任务

public sealed record TerminalBackgroundHandle(string TerminalSessionId, Task<TerminalExitInfo> Exited, Func<Task> StopAsync);
```

AgentHost 内部（ACP 用）的额外方法，不进接口：

```csharp
internal sealed partial class TerminalBroker
{
    // ACP terminal/*（§12.2）与 auth.terminal（§12.1）
    Task<string> CreateCommandSessionAsync(CommandSessionSpec spec, CancellationToken ct);  // 返回 sessionId
    Task<CommandOutputSnapshot> GetOutputAsync(string sessionId, int? byteLimit, CancellationToken ct);
    Task<TerminalExitInfo> WaitForExitAsync(string sessionId, CancellationToken ct);
    Task KillAsync(string sessionId);                      // 只结束进程，保留会话内容
    Task ReleaseAsync(string sessionId);                   // 结束并释放；UI 继续显示快照
    // 用户共享的终端（§11）
    Task<IReadOnlyList<SharedTerminalInfo>> ListSharedAsync(string agentSessionId);
    Task<string> ReadSharedAsync(string agentSessionId, string sessionId, SharedReadTarget target, int maxLines);
}
```

### 10.3 pty host 侧：CommandExecutor 的语义

01 §4 把"Agent 命令执行器"放在 `TCode.Terminal.Host`。它在会话 actor 线程上运行（独占镜像核心，01 §5.1），职责：

| 职责 | 细节 |
|---|---|
| 状态机 | 每个 shell 会话：`Starting → Prompt ⇄ Executing`，外加 `AltScreen`、`Exited`；输入是 133/633 事件、备用屏幕切换、输出到达、进程退出、计时器 |
| 一次只跑一条 | 同一会话已有 Agent 命令执行中时，`command/run` 返回错误 `busy` |
| 写入 | §4.5 规则；写之前若用户在提示符输入过字符则先 `^U` |
| 结束判定 | §4.2 / §4.3；`exec` 会话以进程退出为准 |
| 输入等待检测 | §7.1；只在 `Executing` 且输出静止时检查 |
| 中断 | §6.2 升级序列（每一步都等结束事件） |
| 输出 | `command/output` 直接调用镜像核心 formatter；`since` 游标 = "已返回的纯文本行数"（以命令输出区间内的行号计，软换行行合并后计数） |

### 10.4 与 04 号文档 IPC 消息的对应

04 号文档 §4 已经定义了消息全集（[04-pty-host.md](04-pty-host.md) §4.3–4.5）。下表列出 Agent 集成用到的消息、本文赋予的语义，以及**需要 04 补充的字段 / 消息**（标"需补充"）。名称以 04 为准。

**会话（04 §4.3）**

| 04 的消息 | Agent 集成中的用法与语义 |
|---|---|
| `session/create` | Agent 终端：`kind: agent`、`agentSessionId`、`profileId` = `agent.shell.profile` 解析结果（或 `launch{command,args}`）、`env` = §2.5、`shellIntegration: auto`、`persist: false`（§3.3）。`exec` 命令会话与后台命令：`launch{command: <shell>, args: [-c, <cmd>]}`。ACP `terminal/create`：`launch{command, args}`（§12.2）。**需补充**：`scrollbackBytes`（Agent 终端单独设大，§5.2 第 3 条）、`reveal: background\|focus`（登录终端需要聚焦，§12.1）、`role: shell\|command\|login` 元数据（可以放进 04 已有的 `tags`，由 Desktop 解释，不必改协议） |
| `host/sessionAdded`（H→C 通知，发给 full 连接） | Desktop 据此为 `kind: agent` 的会话建"Agent 终端"标签（§15） |
| `session/list` | broker 重连后找回自己的会话（按 `agentSessionId` 过滤） |
| `session/read` | `terminal_read` 工具读共享终端（§11）；`range: screen\|scrollback\|command` |
| `session/signal` | §6.2 第 3、4 步：`target: foreground`，`TERM` / `KILL`（Windows 只支持 INT / KILL，04 已说明） |
| `session/kill` | §6.2 第 5 步与 ACP `terminal/kill`：结束进程，**会话保留**（04 的语义正好满足 ACP"kill 后仍可 output / wait"） |
| `session/close` | ACP `terminal/release`、Agent 会话关闭；`force: true`（Agent 侧不弹确认，确认在 UI 关闭标签时由 Desktop 完成） |
| `session/share` | 只有 full（Desktop）可调用；`access: read`。v1 的 UI 不提供 `write`（§11）；撤销 = `access: none` |
| `session/input` | v1 中 broker **只用于**写入 `^U`、bracketed paste 包裹的命令文本由 host 的 `command/run` 完成；broker 本身不直接写用户输入 |
| `session/commandStarted` / `session/commandFinished`（事件） | broker 订阅后更新工具卡片；`commandFinished.exitCode` 即 133;D 的退出码 |
| `session/shellIntegration`（事件） | 注入状态。**需补充**：增加 `quality: rich\|basic\|none`（§4.1），或者新增 `session/integrationQuality` 事件，并且发送给 agent 连接（04 目前只发给 ui 订阅者） |
| `session/exited`（事件） | `exec` 会话、后台命令、ACP `wait_for_exit`、登录终端的完成信号 |

**命令（04 §4.5）**

| 04 的消息 | 本文语义 |
|---|---|
| `command/run {sessionId?, create?, commandLine, strategy, timeoutMs?, outputLimitBytes?}` | `strategy: auto` 时 host 按 §4.1 解析：`rich`/`basic` → 04 的 `shellIntegration`（在 shell 中键入命令，§4.2–4.5）；`none` → 04 的 `process`（用 `create` 新建一个命令会话执行，§4.4，即本文的 `exec`）。04 的 `idle`（VS Code none 策略）**v1 的 broker 不使用**，保留给以后的远程 shell（§4.4）。`timeoutMs` 由 broker 管理，传给 host 的值为 null（超时后的处理是 §6 的策略，属于 broker）。**需补充**：返回值中的 `strategy` 增加 `quality: rich\|basic`；参数增加 `preventHistory`、`idleInputSeconds` |
| `command/output {sessionId, commandId, cursor?, maxBytes, format}` → `{text, cursor, truncated, done}` | §5.1 / §5.4：broker 每 250 ms 以上次的 `cursor` 续读纯文本（`cursor` 不透明）；`format: vt` 用于导出 §5.3 的 `.vt`。ACP 的 `outputByteLimit` 截断（从头部、字符边界）由同一方法完成，04 已写明。**需补充**：返回 `evicted: bool`（命令输出起点已被挤出回滚，§5.2 第 3 条） |
| `command/wait {sessionId, commandId, timeoutMs}` → `{exitCode?, signal?, timedOut, durationMs}` | 长轮询。**需补充**：`state: finished\|waitingForInput\|alternateScreen\|running`、`sensitive`、`boundaryVerified`、`cwd`（§4.2 第 3 步的 c、d，§7） |
| —（**需补充**）`command/interrupt {sessionId, commandId, upTo: ctrlC\|sigterm\|sigkill\|close}` → `{stoppedAt}` | §6.2 的升级序列在 host 内执行（每一步都要等 133;D 或回到提示符，放在 host 才不需要跨进程往返）。也可以由 broker 用 `session/input`(`\x03`) + `session/signal` + `session/kill` 自己编排，代价是每一步多一次往返、判断"已结束"要再调 `command/wait`——可以接受，所以这条是"建议补充"而不是必需 |
| —（**需补充**）事件 `command/inputNeeded {commandId, sensitive, promptLine}` | §7.1 的检测在 host 内完成（要读 termios 和镜像核心的光标行）。发给该会话的 agent 连接与 ui 订阅者（标签徽标） |
| —（**需补充**）事件 `session/userInput {sessionId}` | 用户（ui 连接的 `Input` 帧）向 `kind: agent` 的会话输入时发给 owner 连接，不带内容；同一命令内只发一次（§7.2 第 4 条） |

### 10.5 连接、令牌与重连

采用 04 §13.2 的方案（替代本文初稿中"AgentHost 自己读 `ptyhost.json` 并自行启动 host"的做法）：

| 步 | 做法 | 对 Agent 集成的影响 |
|---|---|---|
| 1 | Desktop 启动 AgentHost 时，若 host 已在运行，就用 `auth/mintToken{scope:"agent"}` 签发**受限令牌**，经 `TCODE_PTYHOST_ENDPOINT` / `_TOKEN` / `_INSTANCE` / `_PROTOCOL` 环境变量传入 | broker 启动时直接连接 |
| 2 | host 尚未运行，或认证失败（host 重启过、令牌已吊销）时，AgentHost 通过现有 JSON-RPC 向 UI 发 Host→UI 请求 `terminal/ptyHostEndpoint`；Desktop 按需启动 host、签发新令牌并返回同样四个字段 | **没有 UI 连接时拿不到终端**——与 §2.3 第 2 条（无 UI 时走进程模式）一致。broker 的 `Availability` 在请求未完成时为 `Connecting`，`auto` 模式下本次调用直接走进程模式，不等待 |
| 3 | AgentHost 退出 / 重启时 Desktop `auth/revoke` 旧令牌 | 见下方"重连后的会话归属" |
| 4 | **AgentHost 必须从它启动的所有子进程环境中删除 `TCODE_PTYHOST_*`** | 包括 `BashTool` 进程模式的 `ProcessStartInfo`（`BashTool.cs:204-227` 需要增加 `info.Environment.Remove(...)`）、外部 Agent CLI、MCP server；终端模式下由 host 在构造 shell 环境时删除（04 §13.2 第 5 条） |

`terminal/ptyHostEndpoint` 在 `RpcMethods` 中登记（另一个会话正在修改 `TCode.Rpc.Contracts`，合并时协调协议版本号）。断线后 broker 按 0.2 s 起、上限 5 s 的指数退避重连，每次重连都重新走第 2 步。

**重连后的会话归属（需要 04 确认）**：04 把 agent 会话的 owner 记为 `agent:<tokenId>`（§4.3）。AgentHost 重启后令牌是新的，按 tokenId 判断归属会让旧的 Agent 终端变成"无主"。本文的建议：

- 归属以 `(scope = agent, agentSessionId)` 判断，而不是 tokenId：AgentHost 重启后，只要会话恢复了相同的 `agentSessionId`，就能重新拿回它的 Agent 终端（`session/list` 按 `agentSessionId` 过滤）；
- 令牌吊销时，正在运行命令的 Agent 终端**不杀**：Desktop 把标签标为"Agent 已断开"，用户可以继续使用或关闭；broker 重连后，若该 Agent 会话仍存在，就把这些终端当作"忙碌"处理（不再往里键入命令），新的命令开新终端。

---

## 11. 权限

### 11.1 规则

| 操作 | Agent 自己创建的会话 | 用户共享的会话 | 其他会话 |
|---|---|---|---|
| 列出 | ✔ | ✔（只显示共享给本 Agent 会话的） | ✘ 不可见 |
| 运行命令 / 写入 | ✔（经 §8 审批） | ✘（v1 不支持） | ✘ |
| 读取（屏幕、回滚、命令输出） | ✔ | ✔ | ✘ |
| 中断 / 关闭 | ✔ | ✘ | ✘ |

- **共享**：终端标签右键菜单"共享给 Agent…"→ 选择目标 Agent 会话（默认当前会话）→ UI 调 `session/share`。标签上显示"已共享"图标；再次点击可撤销。共享**只读**、只对选定的 Agent 会话有效，Agent 会话关闭、终端关闭或用户撤销时失效。共享关系只在 pty host 内存中，不持久化。
- **读取工具**：新增内置工具 `terminal_read { terminal_id, target: "last_command" | "screen" | "scrollback", max_lines? }`。只有当前 Agent 会话存在共享终端时才出现在工具列表（与 MCP 动态工具同样的机制）。`DescribePermission` 返回 `ToolAccess.Read`（无路径 → 放行），因为"共享"本身就是用户的授权。
- **组合器附件**：用户在输入框用"@终端"把某个终端的"最近一条命令 + 输出"附进消息——这是用户主动提供上下文，不需要共享授权（与附加文件相同）。
- Agent 终端的标签菜单不提供"共享"（本来就可读）。

### 11.2 连接参数不得泄漏给 Agent 执行的任何程序

AgentHost 从环境变量拿到 pty host 的端点与受限令牌（04 §13.2；01 §3.3 已写入）。**这些变量必须从 AgentHost 启动的所有子进程环境中剔除**，否则 Agent 执行的任意命令（`bash` 进程模式、MCP server、外部 Agent CLI 及其工具调用）都能直接连上 pty host，绕过 broker 的全部检查与 §8 的审批：

| 子进程 | 剔除位置 |
|---|---|
| `BashTool` 进程模式 | `BashTool.Start` 的 `ProcessStartInfo.Environment`（`BashTool.cs:204-227`），删除所有 `TCODE_PTYHOST_` 前缀的变量 |
| MCP server（stdio） | MCP 客户端启动进程处（另一个会话正在实现 MCP，需要同步这条规则） |
| 外部 Agent CLI（Claude Code、Codex、ACP Agent） | `TCode.Runtimes` 的进程启动处（`src/TCode.Runtimes/Process/`） |
| `ProcessRunner`（rg、git） | 同样剔除（这些程序不会滥用，但统一规则更简单） |
| 终端模式下的 shell | pty host 构造环境时剔除（04 §13.2 第 5 条），本文 §2.5 再列一次 |

实现建议：在 AgentHost 启动时把这些变量读入内存后**立即从自身进程环境中删除**（`Environment.SetEnvironmentVariable(name, null)`），这样之后任何 `ProcessStartInfo` 继承的环境天然不含它们；再在上表各处加一条测试断言（启动 `env` / `cmd /c set`，输出中不得出现 `TCODE_PTYHOST_`）。Agent 的文件工具也应把 `$TCODE_HOME/run/` 列为敏感路径（读取总要审批，04 §14.1），否则 Agent 可以直接读 `ptyhost-*.json` 中的完整令牌。

### 11.3 双重检查

1. **AgentHost（TerminalBroker）**：按 `agentSessionId` 记录自己创建的 `sessionId` 集合；工具参数里的 `terminal_id` 必须在"自己创建 ∪ 共享给本会话"之内。子 Agent 使用自己的 `agentSessionId`，看不到父会话的终端（父会话的共享也不继承）。
2. **pty host**（04 §2.4、§4、§13.2）：AgentHost 使用 Desktop 签发的**受限令牌**，认证后 `scope = agent`。受限连接只能写入 / 中断 / 关闭自己创建的 `kind: agent` 会话；对用户会话只能在存在 `session/share` 授权时调用 `session/read`；`session/share`、`host/*`、`persistence/*` 只接受 full 连接（Desktop）。这是纵深防御：令牌对同一用户的任意进程都可得，所以它不是安全边界（真正的边界是 01 §8 的"仅当前用户"），但能防止 AgentHost 内的逻辑错误越权。

### 11.4 TCode 审批流程如何覆盖终端命令

| 路径 | 审批 |
|---|---|
| 内置 Agent `bash`（终端模式） | §8：与进程模式相同的 `tool/approve` |
| 内置 Agent `terminal_read` | 不弹审批；前提是用户已共享 |
| ACP Agent 的 `terminal/create` | ACP `session/request_permission` → TCode 审批（`docs/protocols/acp-v1.md` §8） |
| ACP `auth.terminal` | 用户点击按钮即视为同意；运行的是 Agent 自己的可执行文件 |
| Codex | Codex 自己执行命令，`item/commandExecution/requestApproval` → TCode 审批（不变） |
| 用户在任何终端里输入 | 不审批 |

---

## 12. ACP

### 12.1 `auth.terminal: true`

规范要点 [已核实 https://agentclientprotocol.com/protocol/authentication，2026-10-16 读取]：Client 只有能"在交互式终端中重现所配置的 Agent 启动方式"时才声明；`type: "terminal"` 的方法给出 `args`、`env`；Client 用**同一个 Agent 程序**，追加 `args`，用 `env` 覆盖同名变量，在交互终端中运行，等待退出；**退出码 0 = 成功**，非 0、无退出码、取消 = 失败；之后**重新连接并重新 initialize**；不得对 terminal 方法调用 `authenticate`。

TCode 流程：

```
1. initialize 声明 auth.terminal = true（AcpSession.cs:96 改为 true）。
2. session/new（或 resume/load/prompt）返回 -32000：
   - authMethods 中有 type:"terminal" 的方法 → 界面"需要登录"卡片增加按钮「在终端中登录：<name>」
     （仍然不自动执行：登录需要用户在场；符合现有"不主动发起"的原则，acp-v1.md §4.3）。
3. 用户点击 → AgentHost.TerminalBroker.CreateCommandSessionAsync：
     file  = 与当前 ACP 进程相同的可执行文件（AcpAgentInfo.Executable 解析后的绝对路径）
     args  = 基础启动参数 + method.args      ← 见下方"待核实"
     env   = 基础启动环境，method.env 覆盖同名变量
     cwd   = 会话工作区
     kind  = agent，agentSessionId = "login:<runtimeId>"（不对应任何 Agent 会话），tags = { role: login }，persist = false
   Desktop 收到 host/sessionAdded，看到 tags.role = login，就把它当作普通终端标签打开并聚焦（不加 Agent 标记）。
4. 等待 session/exited：
   - exitCode == 0 → 关闭当前 ACP 进程，重新启动并 initialize，然后重试第 2 步失败的那个请求一次；
     登录标签保留，显示"登录成功，可关闭"。
   - 其他 → 卡片显示"登录未完成（退出码 N）"，标签保留供用户查看输出，可再次点击重试。
   - 用户关闭标签 → 视为取消（失败）。
5. 不设超时（登录可能需要浏览器往返）。
```

- **待核实**：规范说"同一个 Agent 程序"并"追加 args"，但没有说明基础参数（如 `gemini --acp` 中的 `--acp`）是否保留。字面理解是保留（`gemini --acp <method.args>`）。实现时在 `AcpAgents` 表中加一个可选字段 `TerminalAuthDropsBaseArgs`，按 Agent 实测结果逐个设置。Zed 的实现把命令、args、env 组成 `SpawnInTerminal` 任务，`use_new_terminal: true` [已核实 zed@6b218b8 `crates/acp_thread/src/connection.rs:73-92`]，基础参数由各 Agent 连接自行决定。
- 登录终端的 `agentSessionId` 是 `login:` 前缀的保留值，broker 不把它暴露给任何工具：内置 Agent 看不到它，ACP Agent 也读不到它的输出。（04 的 `kind` 只有 `user | agent`；如果 04 愿意增加 `kind: system`，这里改用它更清楚。）
- 失败路径不影响其他会话；同一 Agent 的其他会话在下次请求时自然拿到新的登录状态。

### 12.2 是否声明 `terminal: true`

**建议：实现，但默认不声明**（`agents.acp.clientTerminal: false`，设置页标为实验功能）。

| 因素 | 分析 |
|---|---|
| v2 草案 | 删除了 Client 侧 terminal，改为 Agent 自有、只读的终端输出（`docs/protocols/acp-v1.md` §16）[已核实]。v1 的投入在 v2 上不可复用（v2 只需要"只读回放"，§13 的组件就够） |
| TCode 集成的 Agent | 21 个 ACP Agent（`src/TCode.Runtimes/Acp/AcpAgents.cs:25-45`）都有自己的 shell 工具；不声明时它们自己执行并经 `request_permission` 请求审批，审批照样生效（`acp-v1.md` §3 表）[已核实] |
| 声明后的责任 | Client 负责执行、输出缓冲与截断、进程树清理、release 后继续显示 [已核实 https://agentclientprotocol.com/protocol/terminals] |
| 收益 | ACP Agent 的命令出现在用户可见的终端里、可接管交互提示 |
| 实现成本 | broker 与 pty host 已具备 command 会话、输出、等待、kill；5 个方法只是映射：约 2–3 人日（含测试）[推断] |
| 风险 | Agent 可能在 `terminal/create` 之前不请求权限（§8 末尾）；命令以 TCode 的环境运行，可能与 Agent 自己的沙箱策略不一致 |

结论：成本低，所以实现并保留开关；但默认值遵循 `acp-v1.md` 的既定决策（不声明），等确有 Agent 因此获益且验证了权限行为后再按 Agent 打开（`AcpAgents` 表加 `ClientTerminal: true`）。

**5 个方法到 pty host 的映射**（启用时）：

| ACP 方法 | 参数 / 结果 | TCode 实现 |
|---|---|---|
| `terminal/create` | `{sessionId, command, args?, env?: [{name,value}], cwd?, outputByteLimit?}` → `{terminalId}`，**立即返回** | `session/create { kind: agent, agentSessionId: "acp:<ACP 会话 id>", launch: {command, args}, env: 基础环境 + env（覆盖同名）, cwd: cwd ?? 会话 cwd（必须是绝对路径，否则 -32602）, label: "<Agent> 命令", persist: false }`。`terminalId` = TCode 生成的不透明 id，映射到 pty host `sessionId`，并记录 `outputByteLimit` |
| `terminal/output` | `{sessionId, terminalId}` → `{output, truncated, exitStatus?: {exitCode, signal}}` | `command/output { sessionId, format: plain, tail: true, maxBytes: outputByteLimit ?? 1 MiB }`；`truncated = truncatedHead \|\| evicted`；进程已退出时附 `exitStatus` |
| `terminal/wait_for_exit` | → `{exitCode, signal}` | 等待 `session/exited`；ACP 请求被取消（`$/cancel_request`）时停止等待，不杀进程 |
| `terminal/kill` | 结束命令但**不释放**；之后仍可 output / wait | `session/kill`（04：SIGHUP → 2 s → SIGKILL，**会话保留**，正好满足"kill 后仍可 output / wait"） |
| `terminal/release` | 若仍在运行则结束，然后释放；之后 id 失效；已嵌入工具调用的终端**应继续显示** | `session/close { keepContent: true }`；先导出 VT 快照（§5.3）交给 Desktop 持久显示；删除 id 映射 |

截断语义（ACP 规范 [已核实]）："超出 `outputByteLimit` 时从**输出开头**截断，必须在字符边界截断"。`command/output` 以 UTF-8 字节计数，从头部丢弃，再向后跳过续字节（`10xxxxxx`）到下一个字符起点；建议同时对齐到下一个换行，便于阅读（规范只要求字符边界，允许保留的少于上限）。**注意**：TCode 的"输出"是镜像核心格式化后的文本（不是原始字节），进度条等会被折叠——这对 Agent 更友好；Zed 也是从终端网格取文本 [推断：未核实 Zed 的具体实现]。

其他规则：

- 每个 ACP 会话最多 16 个未 release 的终端；超出时 `terminal/create` 返回错误。ACP 会话关闭 / Agent 进程退出时，release 该会话的全部终端。
- `env` 合并而非替换（规范未说明，见 [已核实] 的同一页"Gaps"）[推断]。

### 12.3 `ToolCallContent` 中的 `terminal`

- 启用 `clientTerminal` 时：`{type:"terminal", terminalId}` 渲染为**只读迷你终端**（§14 的同一组件），实时 attach 到对应的 pty host 会话；release 后改为显示导出的 VT 快照。规范要求 release 后继续显示 [已核实]。
- 未启用时（默认）：Agent 不应发送此类内容；万一出现，仍显示占位"终端输出不可用"（保持 `AcpSession.cs:812` 的现有行为）。
- `ResultText()` 中给模型 / 历史的文本：启用时改为该终端的纯文本输出（截断到 2000 行 / 50 KB）。

---

## 13. Codex：命令输出改用只读终端视图

### 13.1 现状与目标

- 现状：`outputDelta` 是 Codex 侧 UTF-8 lossy 解码后的字符串片段（多字节字符跨片段时出现 U+FFFD，以 `aggregatedOutput` 为准）[已核实 `docs/protocols/codex-app-server.md:304`]；`terminalInteraction { processId, stdin }` 是写给交互式进程的输入（`:305`），未处理；进度显示有 §1.2 的缺陷。
- 目标：命令卡片内显示只读迷你终端，按到达顺序回放输出；颜色、进度条、`\r` 覆盖都正确显示。

### 13.2 设计

| 部件 | 做法 |
|---|---|
| 连接 | `ReplayTerminalViewSession : ITerminalViewSession`（01 §6.5），在 **Desktop 进程内**，不经 pty host。数据来源是会话事件流；`SendInput` 丢弃（只读） |
| 数据 | CodexSession 不再把片段当作完整文本：`ToolProgressAsync` 改为携带增量标记（`ToolExecutionUpdated` 增加 `Append: bool` 字段，或 Details 中 `{"outputDelta": "..."}`）；Desktop 端按 itemId 累积 |
| 尺寸 | Codex 不告知 PTY 尺寸 → 列数取卡片宽度对应的列数（最少 80），创建后**固定不变**（换行由 Codex 侧程序决定，事后改列数会让 TUI 输出错位）[推断：Codex unified exec 的 PTY 默认尺寸待核实] |
| `terminalInteraction` | **不喂给终端**（PTY 回显已经包含在输出里 [推断]，重复喂会出现两次）；在卡片底部显示一行"已向进程输入：<stdin 摘要>"，内容 ≤ 80 字符 |
| 完成 | `item/completed` 的 `aggregatedOutput` 与已回放内容不一致时（丢片段、U+FFFD），以 `aggregatedOutput` 重放一次（清空核心后整体 write） |
| 历史 | 会话记录持久化的仍是纯文本 `aggregatedOutput`（不变）；重新打开历史时用它回放 |

### 13.3 工作量与阶段

| 项 | 估计 [推断] |
|---|---|
| `ReplayTerminalViewSession`（测试里已需要一个回放实现，01 §6.5，可复用） | 0.5 天 |
| 会话记录中的迷你终端项（虚拟化、最大高度、展开 / 复制 / 在终端中打开） | 1.5 天（与 §14 共用） |
| CodexSession 增量语义 + RPC 字段 + 修复替换缺陷 | 0.5 天 |
| Headless 截图测试（彩色输出、跨片段 UTF-8、`\r` 进度条） | 1 天 |
| 合计 | **3–4 人日** |

阶段：依赖 M3（`TerminalView` 可用），不依赖 pty host。放在 **M5 末尾**；其中"修复进度只显示最后一个片段"的缺陷与终端无关，可以立即单独修。

---

## 14. 会话记录中命令输出的展示

### 14.1 建议

| 来源 | 显示 | 原因 |
|---|---|---|
| 内置 bash，进程模式 | **纯文本**（现状） | 没有 TTY，程序通常不输出颜色；已有的显示足够 |
| 内置 bash，终端模式 | **只读迷你终端** | 输出本来就带颜色和光标控制；纯文本会丢失进度条的最终状态以外的信息并显示乱码风险 |
| Codex `commandExecution` | 只读迷你终端（§13） | 同上 |
| ACP `terminal` 内容 | 只读迷你终端（§12.3） | 规范要求实时显示 |
| ACP / Claude Code 普通文本工具结果 | 纯文本 | 不变 |

模型看到的永远是纯文本（§5.2）；迷你终端只是显示层。

### 14.2 迷你终端组件（`TranscriptTerminalView`）

- 内部是 `TerminalView`（`TCode.Terminal.Avalonia`），`Session` 为三种之一：
  1. **Live**：attach 到 pty host 会话（`ToolExecutionUpdated.DetailsJson` 中的 `terminalSessionId`），只订阅该命令的日志区间（从 `OutputStartSeq` 起）——Desktop 是 UI 角色客户端，可以订阅原始字节；
  2. **Snapshot**：命令结束后，读取 `CommandDetails.VtPath` 指向的 VT 文本回放（§5.3）；
  3. **Replay**：Codex / ACP 的事件流。
- 默认高度最多 12 行，可展开到 40 行；超过时内部滚动；工具栏：复制纯文本、在终端中打开（仅 Live / Agent 终端）、展开。
- **虚拟化**：只有进入视口的卡片才创建核心（每个核心有原生内存）；离开视口 2 屏后释放核心，只保留 VT 文本。会话记录里同时存在的核心数上限 8 [推断：数值待测]。
- 回滚上限 2000 行；Snapshot 文本超过 1 MB 时只回放尾部。
- **Live 模式受流控约束**：Live 迷你终端是 Desktop 连接上的一个 View 订阅者，参与 01 §6.4 / 04 §7 的流控——它不 Ack，会话就会被拖住（最多 10 s，之后该订阅者被降级并要求 resync）。所以迷你终端离开视口时必须 `session/detach`，而不是只停止绘制；重新进入视口时用 `snapshot: vt` 重新 attach。同一会话已经在 Agent 终端标签中显示时，迷你终端改用 Snapshot 模式定时刷新（每 500 ms 调 `command/output{format: vt}`），避免同一会话出现两个 View 订阅者。
- 字体与主题与终端标签一致（`Terminal.*` 资源键）。
- 无障碍：AutomationPeer 暴露纯文本（与 `TerminalView` 的可访问缓冲视图相同）。

### 14.3 RPC / 持久化变化

- `CommandDetails` 增加 `TerminalSessionId?`、`CommandId?`、`VtPath?`、`Strategy?`、`BoundaryVerified?`（只进 Details，不发给模型）[已核实现有字段：`src/TCode.Agent.Coding/Tools/ToolDetails.cs:48`]。
- `.vt` 与 `.log` 一样放在 `~/.tcode/tmp/<session>/`，随会话清理；历史回放时文件不存在则退回纯文本。

---

## 15. Desktop 侧呈现

| 元素 | 规则 |
|---|---|
| Agent 终端标签 | 收到 `host/sessionAdded`（`kind: agent`）时在工作台创建标签：图标带 Agent 标记，标题"<会话标题> · Agent"（子 Agent 加前缀），**不抢焦点**。只在该 Agent 会话所在的工作台出现 |
| 标签内容 | 持久 shell 会话直接显示；`exec` 模式的多个命令会话在同一标签内**按时间顺序拼接**显示（每段上方一行分隔：命令行 + 退出码），实现方式是标签持有一个会话列表，当前会话可交互，之前的会话以只读快照显示 |
| 状态徽标 | 运行中（转圈）/ 需要输入（黄点，§7.2）/ 已结束 |
| 用户接管 | 标签可以直接输入；首次在 Agent 正在运行命令时输入，标签顶部提示"你正在向 Agent 的命令输入"（不阻止） |
| 关闭 | 关闭 Agent 终端标签 = 关闭该会话；若命令仍在运行，确认后中断（§6.2），broker 收到 `session/exited` 后让工具调用以 `SessionLost` 结束 |
| 共享 | 用户终端标签右键"共享给 Agent…"（§11.1） |
| 登录终端 | ACP 登录（§12.1）以普通终端标签显示并聚焦，标题"<Agent> 登录" |

---

## 16. 测试计划

原则：全部用 **Fake / Scripted 模型**驱动（`tests/TCode.Agent.Tests/ScriptedWireApi.cs` 按脚本发出工具调用；`src/TCode.Ai/Fake/FakeWireApi.cs` 用于 UI 冒烟），**不发起真实模型调用**；不合成 GUI 输入，"用户输入"一律以 UI 角色客户端向 pty host 写字节来模拟（遵守项目的 UI 验证约定）。

### 16.1 测试位置

| 项目 | 内容 |
|---|---|
| `tests/TCode.Terminal.Host.Tests` | CommandExecutor 的状态机与策略（真实 PTY + 真实 shell），协议级 |
| `tests/TCode.Agent.Tests` | `BashTool` + 测试用 `ITerminalExecutor`（连接进程内启动的 pty host）+ ScriptedWireApi 的端到端 |
| `tests/TCode.AgentHost.Tests` | TerminalBroker：权限、发现与重连、ACP 映射 |
| `tests/TCode.Runtimes.Tests` | ACP fixture（假的 ACP Agent 脚本）：`terminal/*`、`auth.terminal`；Codex fixture 的增量语义 |
| `tests/TCode.Desktop.Tests` | Agent 终端标签、迷你终端、"需要输入"徽标的 Headless 截图 |

### 16.2 用例

| # | 场景 | 断言 |
|---|---|---|
| A1 | rich：`echo hi; (exit 3)` | 输出 `hi`；exitCode 3；`IsError`；Strategy=rich；BoundaryVerified |
| A2 | 状态保持：`mkdir sub && cd sub` 然后 `pwd` | 第二次输出以 `/sub` 结尾；结果附 `[Current directory: …]` |
| A3 | 进度条：`printf 'a\r b\r  c\n'` | 模型文本为 `  c`（formatter 折叠）；VT 快照回放后截图一致 |
| A4 | 大输出：`seq 1 300000` | 结果尾部截断 + 日志路径；日志首行 `1`、末行 `300000`（即使超过回滚上限） |
| A5 | 超时：`sleep 100`，timeout 1 s | 第 1 步 `^C` 即结束；终端可复用；结果文案 |
| A6 | 忽略 SIGINT：`trap '' INT; sleep 100` | 升级到 SIGTERM / SIGKILL；`stoppedAt` 正确 |
| A7 | raw 模式程序：`python3 -c "import tty,sys;tty.setraw(0);sys.stdin.read(1)"` | `^C` 无效，SIGTERM 生效 |
| A8 | 等待输入：`read -p "Continue? (y/n) " x; echo got=$x` | 3 s 内返回 `WaitingForInput`；登记后台任务；UI 客户端写 `y\r` 后任务完成，通知内容含 `got=y` |
| A9 | 密码：`read -s -p "Password: " p` | `sensitive=true`（termios ECHO 关）；结果文案不含"发给模型"的引导 |
| A10 | 备用屏幕：`less /etc/hosts` | 立即返回 `AlternateScreen`；`task_stop` 后会话回到提示符 |
| A11 | 无集成：`agent.shell.profile` 指向未注入的 `/bin/sh` | 等级 `none` → `exec`；退出码精确；工具描述切换为 fresh-process 措辞 |
| A12 | basic：集成脚本只发 133 A/D（测试夹具脚本） | 以 D 结束；无退出码时 exitCode=null |
| A13 | 伪造边界：命令输出中打印 `\e]133;D;0\a` 后 `sleep 1; echo tail` | 结果标 `BoundaryVerified=false`（rich 下应以带 nonce 的 633 为准，不受伪造影响） |
| A14 | 后台：`run_in_background` + `sleep 1; echo done` | 立即返回任务 id；完成后注入 `<task-notification>`；日志含 `done` |
| A15 | `task_stop` 后台任务 | 状态 stopped；会话已结束 |
| A16 | 取消本轮 | `^C` 发出；工具以取消结束 |
| A17 | pty host 不可用（指向不存在的 host） | `auto` 退化为进程模式，结果正确；Details 记录原因 |
| A18 | pty host 中途被杀 | 进行中的调用以 `SessionLost` 结束；下一条命令自动重连或退化 |
| A19 | 审批 | 终端模式与进程模式产生的 `ToolApprovalRequest` 除 `Target` 外逐字段相等；拒绝时终端中没有任何输入 |
| A20 | 权限 | agent 角色对 user 会话 `command/run`、`session/share` 均被拒；共享后 `terminal_read` 可读，撤销后不可读；子 Agent 读不到父会话的终端 |
| A23 | 环境隔离：终端模式与进程模式中执行 `env`（Windows `cmd /c set`），以及测试 MCP server 打印自身环境 | 输出中没有 `TCODE_PTYHOST_` 前缀的变量 |
| A24 | SIGPIPE（进程模式与终端模式） | `bash -c 'yes \| head -1; echo ${PIPESTATUS[0]}'` 输出 `141`（03 §14 T3/T19） |
| A21 | 历史 | `preventHistory` 下 zsh/bash 的 HISTFILE 中没有 Agent 命令 |
| A22 | Windows | pwsh（rich）与 Git Bash（rich）各跑 A1、A2、A5、A8 |
| C1 | ACP `terminal/*`（fixture Agent） | create 立即返回；output 的 `outputByteLimit` 截断在 UTF-8 字符边界（样本含中文与 emoji）；kill 后 output 仍可用；release 后 id 失效且 UI 仍显示快照 |
| C2 | ACP `auth.terminal`（fixture：首次 `session/new` 返回 -32000 + terminal 方法；登录脚本 `exit 0`） | 声明 `auth.terminal:true`；用户动作后运行登录命令（args/env 正确合并）；重启并重新 initialize；原请求重试成功；`exit 1` 时显示失败且不重启 |
| C3 | Codex 增量（fixture：带 ANSI 颜色、跨片段的多字节字符、`\r` 进度） | 进度累积而非替换（修复 §1.2 缺陷）；完成时以 `aggregatedOutput` 校正；迷你终端截图 |
| D1 | Desktop：收到 `host/sessionAdded(kind: agent)` | 工作台出现 Agent 终端标签，不抢焦点（截图） |
| D2 | Desktop：`command/inputNeeded` | 标签徽标与工具卡片按钮（截图） |
| D3 | Desktop：迷你终端 Live → Snapshot 切换 | 前后截图一致 |
| S1 | AOT 冒烟 | `TCode --smoke` 中加入：Fake 模型发出一次 bash 调用，在终端模式下执行 `echo tcode-smoke`，检查工具结果与 Agent 终端标签（12 号文档 aot-smoke 作业） |

---

## 17. M5 内的实施拆分

| 步 | 内容 | 依赖 |
|---|---|---|
| 1 | pty host：命令跟踪器（133 回调 + 633 未知序列）、IntegrationQuality、`command/*` 消息 | M2、07 的集成脚本发出 nonce 与 `HasRichCommandDetection` |
| 2 | `ITerminalExecutor` + TerminalBroker + 受限令牌 / `terminal/ptyHostEndpoint`（§10.5）；BashTool 双路径；子进程环境清理；`agent.shell.*` 设置 | 1 |
| 3 | 交互提示检测、中断升级、后台任务对接 | 2 |
| 4 | Desktop：Agent 终端标签、徽标、接管提示 | M4 |
| 5 | 迷你终端（Live/Snapshot/Replay）+ Codex 增量修复 | M3 |
| 6 | 权限：共享菜单、`terminal_read`、pty host 角色检查 | 2、4 |
| 7 | ACP `auth.terminal`；`terminal/*`（开关，默认关） | 2 |
| 8 | 测试 §16 全部进 CI；AOT 冒烟 | 全部 |

---

## 18. 待决问题（需评审）

1. **`auto` 第 4 条**（只读查询走进程模式）是否保留——它减少刷屏，但让"可见终端"看起来不完整。
2. **超时默认值**：`interrupt`（与现状一致）还是 VS Code 式 `background`。
3. **04 需补充的消息与字段**（§10.4 中标"需补充"的各项）：集成等级 `quality`、`command/wait` 的 `state`、`command/output` 的 `evicted`、`command/inputNeeded`、`session/userInput`，以及建议性的 `command/interrupt`；agent 会话归属改按 `agentSessionId`（§10.5）；运行中修改 `persist`（§3.3）。
4. **01 §6.1 `ITerminalEffects`** 需要 `UnknownOsc` 回调以支持 OSC 633（§4.1）。
5. **ACP 终端登录的基础参数**是否保留（§12.1"待核实"）。
6. **termios ECHO 检测**在 macOS 上 `tcgetattr(master)` 是否返回从设备设置（§7.1），M5 第 3 步先做 spike。
7. **agent 连接创建的会话使用哪份 BaseEnvironment**（§2.5）：建议为最近一次由 full 连接发送的那份，需要 04 / 07 写明。

---

## 来源

- 本仓库：`src/TCode.Agent.Coding/Tools/Shell/BashTool.cs`、`OutputCapture.cs`、`TaskStopTool.cs`、`Tools/BackgroundTasks.cs`、`Tools/ToolServices.cs`、`Tools/ToolDetails.cs`、`Workspace/PermissionPolicy.cs`；`src/TCode.Runtimes/Acp/AcpSession.cs`、`Acp/AcpAgents.cs`、`Codex/CodexSession.cs`、`TurnWriter.cs`；`src/TCode.Rpc.Contracts/Dtos.cs`、`SessionEvents.cs`；`src/TCode.Desktop/ViewModels/SessionViewModel.cs`；`docs/design/builtin-agent-tools.md` §6、§10；`docs/design/external-agent-runtimes.md`；`docs/protocols/acp-v1.md` §1、§4、§7.5、§12、§16；`docs/protocols/codex-app-server.md` §5、§6.2。
- VS Code `microsoft/vscode@4765bd3`：`src/vs/workbench/contrib/terminalContrib/chatAgentTools/browser/executeStrategy/{executeStrategy,richExecuteStrategy,basicExecuteStrategy,noneExecuteStrategy}.ts`、`toolTerminalCreator.ts`、`outputHelpers.ts`、`tools/runInTerminalTool.ts`、`tools/monitoring/outputMonitor.ts`、`common/terminalChatAgentToolsConfiguration.ts`。
- Ghostty `ghostty-org/ghostty@b3dc3d7`：`include/ghostty/vt/terminal.h`（effects 表、semantic prompt 事件、unknown sequence）、`selection.h`（`ghostty_terminal_select_output`）、`formatter.h`。
- Zed `zed-industries/zed@6b218b8`：`crates/acp_thread/src/connection.rs:73-92`。
- ACP 官方文档（2026-10-16 读取）：https://agentclientprotocol.com/protocol/terminals 、https://agentclientprotocol.com/protocol/authentication 。
- 研究文档：[research/06](research/06-ssh-transport.md) §7.2–7.4；[research/03](research/03-vt-protocol.md) §2.4；[research/05](research/05-platform-behavior-config.md) §3、§10。
