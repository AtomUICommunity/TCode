# TCode 终端：现状分析

> 日期：2026-10-16。本文只描述仓库里已有的东西和缺口，不做选型；选型依据见 [research/](research/)。

## 1. 结论

TCode 目前**没有终端**：

- 没有 PTY；
- 没有 VT 解析或屏幕模型；
- 没有渲染控件；
- 没有终端配置。

能看到的只有工作台"+"菜单里的一个"终端"占位标签。和终端相关的现有代码都属于 Agent 的**非交互**命令执行（`shell -c`、重定向管道），它和交互式终端是两条不同的路径，不能直接复用为终端。

## 2. 已有代码清单

| 位置 | 内容 | 与终端的关系 |
|---|---|---|
| [WorkbenchViewModel.cs](../../src/TCode.Desktop/ViewModels/WorkbenchViewModel.cs) | `WorkbenchTabKind.Terminal`；`AddTool` 用 `PlaceholderTabViewModel` 打开占位标签 | `AddTool` 按 Kind 查找已有标签，**同一种工具只能开一个标签**。多终端需要改成按实例管理 |
| [MainWindow.axaml:692](../../src/TCode.Desktop/Views/MainWindow.axaml#L692) | `PlaceholderTabViewModel` 的 DataTemplate：图标 + 标题 + 描述 + "即将推出" | 终端控件接入后，换成专门的 `TerminalTabViewModel` 模板 |
| [strings.tsv:78](../../src/TCode.Desktop/Localization/strings.tsv#L78) | `TerminalDescription`："终端将基于 Iciclecreek.Avalonia.Terminal（XTerm.NET + Porta.Pty）实现，并与 Agent 的 bash 工具共享会话" | 早期设想，未经调研，需要按最终选型改写 |
| [architecture.md §8](../architecture.md) | 路线图："终端（Iciclecreek.Avalonia.Terminal）" | 同上 |
| [MainWindowSnapshotTests.cs:123](../../tests/TCode.Desktop.Tests/MainWindowSnapshotTests.cs#L123) | 截图测试会打开终端占位标签 | 终端控件接入后需要专门的截图测试 |
| [ShellResolver.cs](../../src/TCode.Agent.Coding/Tools/Shell/ShellResolver.cs) | Agent 用的 shell：Unix 取 `$SHELL`，但**只接受 bash / zsh**，否则 `/bin/bash` → `/bin/sh`；Windows 依次是 Git Bash → pwsh → Windows PowerShell，并带 `-NoProfile -NonInteractive` | 这套选择是为"让模型稳定执行命令"设计的，和"与系统终端一致"的要求相反：fish / nushell 用户会被换成 bash，Windows 默认也不是 PowerShell。交互式终端需要另写一套 profile 探测 |
| [BashTool.cs](../../src/TCode.Agent.Coding/Tools/Shell/BashTool.cs) | `Process` + 重定向 stdin/stdout/stderr，设置 `PAGER=cat`、`GIT_TERMINAL_PROMPT=0`、`TCODE_AGENT=1` | 没有 TTY，程序会关闭颜色和交互。"与 bash 工具共享会话"如果要做，需要另行设计（见 §4） |
| [ProcessRunner.cs](../../src/TCode.Agent.Coding/Processes/ProcessRunner.cs) | rg、git 等辅助命令的运行与进程树终止 | 无关；但说明 UI 和 Host 两个进程里都在用 `System.Diagnostics.Process`。自己管理 PTY 子进程时，要考虑与 .NET 运行时的 SIGCHLD 处理互相影响（见 research/02） |
| [ToolPresentation.cs](../../src/TCode.Desktop/ViewModels/ToolPresentation.cs) | 把 `bash` / `exec_command` / `run_terminal_cmd` / `terminal` 等工具名归为 Command 类 | 将来"在终端中打开 / 重新运行"的入口 |
| [AcpSession.cs:94](../../src/TCode.Runtimes/Acp/AcpSession.cs#L94) | ACP `initialize` 声明 `terminal:false`、`auth.terminal:false`；`ToolCallContent` 中的 `terminal` 显示为"[terminal output unavailable]" | [acp-v1.md](../protocols/acp-v1.md) §4.3 写明：等内嵌终端上线后，把 `auth.terminal` 改为 `true`，让需要在终端中登录的 Agent 可以在 TCode 内完成登录 |
| [CodexSession.cs:830](../../src/TCode.Runtimes/Codex/CodexSession.cs#L830) | Codex 的 `writeStdin` 审批显示为"Write to terminal"；只处理了 `item/commandExecution/outputDelta`（[第 423 行](../../src/TCode.Runtimes/Codex/CodexSession.cs#L423)），`terminalInteraction` 没有处理 | Codex 的交互式命令输出（含 ANSI）目前按纯文本处理 |
| [JsonRpcConnection.cs:82](../../src/TCode.Rpc.Contracts/JsonRpcConnection.cs#L82) | UI ↔ Host 是 stdio 上按行分帧的 JSON-RPC，发送队列是**无界** `Channel` | 如果把 PTY 放到 Host 进程，终端字节流走这条通道会有两个问题：JSON 里的 base64 开销，以及没有背压（`cat` 大文件时队列会无限增长）。见 research/06 |
| [settings.md](../settings.md) | `~/.tcode/settings.json`（JSONC + Schema + 热重载） | 终端配置应作为其中的 `terminal` 节，沿用同样的宽容解析和热重载规则 |

## 3. 缺口

| 层 | 需要的能力 | 现状 |
|---|---|---|
| 进程 / PTY | Unix：openpty + 控制终端 + 安全启动子进程；Windows：ConPTY；resize、退出码、杀进程树、前台进程与 cwd 查询 | 无 |
| 终端核心 | VT 解析状态机、主 / 备用屏幕、回滚、reflow、字符集、模式、应答（DA / CPR / DECRQM…）、Unicode 宽度与 grapheme | 无 |
| 渲染 | 等宽网格自绘、字体回退、宽字符 / emoji、box drawing、光标、选区、搜索高亮、HiDPI | 无。团队有 Avalonia 自绘的经验：[CodeView](../../src/TCode.Desktop/Controls/CodeView.cs)，以及 StreamMark |
| 输入 | 键盘编码（legacy / Kitty）、IME 组合输入、鼠标上报、粘贴（bracketed paste）、与全局快捷键（⌘K 等）的冲突处理 | 无 |
| 系统一致性 | 按平台探测用户真正的 shell 与 profile（WSL / Git Bash / pwsh…）、login shell、环境变量、各平台的复制粘贴与 Option 键习惯、默认字体 | 只有 Agent 用的 `ShellResolver`，目标不同 |
| 配置 | profiles、字体、光标、回滚、键位、响铃、shell 集成… | 无 |
| 工作台 | 多个终端标签、分屏、标题跟随前台进程 / OSC、关闭确认 | 每种工具只能开一个占位标签 |
| 传输抽象 | 本地 PTY、SSH、WSL、`docker exec` 等共用一个连接接口 | 无 |
| 测试 | 字节流回放 → 屏幕快照断言；vttest / esctest；吞吐基准；截图测试 | 无 |

## 4. 与终端相关的后续集成点

这些不属于终端组件本身，但会影响它的接口设计，要在设计阶段预留：

1. **ACP 终端登录**：`auth.terminal:true` 之后，需要以"指定命令 + 参数 + 环境变量"启动一个终端，并拿到退出码（[acp-v1.md](../protocols/acp-v1.md) §4）。终端 API 需要支持"运行一条命令并等待它退出"，而不只是启动交互式 shell。
2. **Agent 命令在可见终端中运行**：占位文案中"与 bash 工具共享会话"的设想。可以做到的程度取决于 PTY 放在哪个进程（UI / AgentHost / 独立 pty host），见 research/06。
3. **路径与链接**：终端输出中的 `path:line:col` 需要在 TCode 工作台中打开。所以链接识别要有可插拔的 provider，不能写死成打开浏览器。
4. **SSH**：远程终端以后要能与远程文件树（SFTP）共用一条连接。所以传输层不能假设"连接 = 本地进程"。
5. **NativeAOT**：终端库的每个依赖都要通过 AOT 发布检查（0 个 IL 警告），并实际运行一次发布后的二进制。
