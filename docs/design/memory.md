# 项目记忆（auto memory）与 AGENTS.md @import

状态：已实现（2026-10-19）；§8 的提示词待评审
关联文档：[builtin-agent-tools.md](builtin-agent-tools.md) §3（system prompt）、[compaction.md](compaction.md) §7.3（压缩后重新附加）、[plan-mode.md](plan-mode.md) §6（计划文件免审批）

本文只涉及**内置 Agent**。外部运行时（Claude Code / Codex / ACP）有各自的记忆机制，TKCode 不向它们注入记忆。

## 1. 已确认的决策（2026-10-19）

| 维度 | 决策 |
|---|---|
| 存储 | 只存本机，按仓库区分：`~/.tkcode/projects/<项目键>/memory/`。同一仓库的 worktree 与子目录共用一份；不进仓库，不提供团队共享层级 |
| 写入 | 模型用现有的 `write` / `edit` 工具写；记忆目录内的 `.md` 在任何档位都免审批；宿主在写入后补 `modified` / `originSessionId`，并检查 MEMORY.md 的大小 |
| 召回 | MEMORY.md 的前 200 行或前 25KB（先到者为准）作为会话开头的一条 `SystemMessage`；单条记忆不预加载，由模型按索引自己 `read` |
| 指令文件 | 本轮只加 `@import`；不做 path 规则、`AGENTS.local.md`、CLAUDE.md 兼容 |

这些决策与 Claude Code 的做法一致（§2）。凡是本文没有写到的细节，都按 Claude Code 的行为处理。

## 2. 参考：Claude Code 的实现（调研记录）

资料来源：官方文档 code.claude.com/docs/en/memory，以及本机 Claude Code 2.1.303 的会话记录。

- **存储**：`~/.claude/projects/<project>/memory/`，`<project>` 由 git 仓库推导，所以同一仓库的 worktree 共享一份；不跨机器。清理旧会话记录时跳过记忆文件。
- **结构**：`MEMORY.md` 是索引，每条记忆一行 `- [标题](file.md) — 钩子`；每条记忆单独一个文件，带 frontmatter（`name`、`description`、`type`，`type` 取 user / feedback / project / reference 之一）。
- **加载**：会话开始时加载 MEMORY.md 的前 200 行或 25KB。它和 CLAUDE.md 一起放进首条 user 消息的 `<system-reminder>`，不进 system prompt。单条记忆文件不预加载，模型用 Read 工具按需读取。
- **写入**：没有专用工具，模型用 Write / Edit 写文件，再自己更新索引。宿主在写入后给带 frontmatter 的文件补 `modified`（实际还会补 `originSessionId`）。写 MEMORY.md 后检查大小：接近上限时提醒精简；超过上限时写入照常成功，但提示模型重写索引。
- **会话中途与压缩**：会话中途改了 MEMORY.md 不会立即重新注入，因为是模型自己写的，它已经知道内容。压缩后从磁盘重读并重新注入。
- **子 Agent**：不继承主会话的记忆。子 Agent 的记忆需要单独配置，TKCode 本轮不做。
- **@import**：语法是 `@path`。相对路径以导入它的文件所在目录为基准，最多 4 跳；代码块和行内代码里的不算；指向工作目录之外的导入首次需要用户批准。

## 3. 存储与项目键

```
~/.tkcode/projects/<项目键>/memory/
├── MEMORY.md                 # 索引：一条一行
├── testing-preferences.md    # 一条记忆
└── …
```

- **项目根**（`ProjectMemory.ProjectRoot`）：
  - `GitInfo.Find(cwd)` 找到的仓库根。
  - 如果仓库根的 `.git` 是文件（worktree）：读出 gitdir；该目录下有 `commondir` 时，主仓库根是 common git 目录（通常就是主仓库的 `.git`）的上一级。
  - submodule 的 gitdir 下没有 `commondir`，仍以 submodule 自身为根。
  - 不在 git 仓库里时用 cwd。
- **项目键**：`<路径 slug>-<8 位 hash>`。
  - slug：项目根路径中 `[A-Za-z0-9._]` 以外的字符换成 `-`，合并连续的 `-`，去掉首尾的 `-`，最多保留末尾 64 个字符。
  - hash：路径的 SHA-256 取前 8 位十六进制。路径先去掉末尾分隔符，在不区分大小写的文件系统上先转成小写。
  - 例如 `/Users/token/code/AtomUI.TCode` → `Users-token-code-AtomUI.TCode-1f3a9c0b`。
  - hash 避免了会话目录（`EncodeCwd`）那种 `-` 与 `/` 冲突。
- **目录创建**：不预先创建。`write` 会自动创建父目录。
- **开关**：settings.json 的 `autoMemory`，默认 true。设为 false 时，新会话不加记忆节、不注入索引、目录也不免审批。

## 4. 生命周期与注入

### 4.1 system prompt 的 `memory` 节

- 记忆说明（§8.1）作为 `memory` 节，排在 `project_context` 之后、其他额外节（skills / MCP / 模式）之前。
- 节内只有固定说明和目录路径，同一项目的各个会话之间不变，不影响提示缓存。
- 恢复旧会话时，记录的快照里没有这一节，下一批输入前会按现有机制追加一条节补丁（builtin-agent-tools.md §3.2）。

### 4.2 索引消息

- 每批用户输入之前（`Agent.ContextUpdates`），如果当前历史里没有 `Source = "memory"` 的 `SystemMessage`，就从磁盘读 MEMORY.md，生成一条（§8.2）排在本批消息最前面。这条规则覆盖了以下几种情况：
  - **新会话**：第一批输入就带上索引。
  - **恢复会话**：索引已经在历史里，不重复注入。
  - **启用前创建的旧会话**：下一批输入时补上。
- **压缩**：`Compactor.CommitAsync` 把一条新读的索引消息放在压缩结果的最前面，计划审批后清空上下文的交接（handoff）同样如此。压缩后的第一次请求就带着最新的索引，不必等到下一批输入。
- **会话中途**：模型写了 MEMORY.md 之后不重新注入（与 Claude Code 相同）。
- **放进消息而不是 system prompt 的原因**：模型每写一条记忆，新会话的 system 前缀就会变化。把索引放进消息，system + tools 前缀保持稳定（tcode-prompt-cache 的断点设计）；也符合“system prompt 冻结、变化靠追加”的约定。
- **在各协议上的形式**：
  - Anthropic：第一条之前没有 user 轮，所以渲染成 user 轮里的 `<system-reminder>`。
  - Chat：支持中途 system 的端点用 system 角色，否则用 `<system-reminder>`。
  - Responses：developer 消息。
- **界面**：桌面端不显示 system 消息，索引不出现在对话里。
- **截断**：取前 200 行，并且不超过 25KB（按 UTF-8 截在行边界）。被截掉时在末尾说明还剩多少行没有加载，提示模型精简。
- **上下文占用**：索引消息与 `memory` 节都计入“系统提示”类。

### 4.3 子 Agent 与外部运行时

- **子 Agent**：不加 `memory` 节，不注入索引（与 Claude Code 相同）。它们共用父会话的 `WorkspaceRoots`，所以读写记忆目录同样免审批。
- **外部运行时**：不受影响。

## 5. 审批

记忆目录记录在 `WorkspaceRoots.MemoryDirectory` 上，由 `PermissionPolicy` 统一判定：

| 访问 | 判定 |
|---|---|
| 读取（read / ls / glob / grep） | 记忆目录视同工作区内：免审批 |
| 只读命令（bash 的 `ls` / `cat` 等） | 参数里出现的记忆目录路径视同工作区内 |
| 写入（write / edit） | 所有路径都是记忆目录内的 `.md` 文件时，在任何档位都放行；其他情况走原规则 |
| Plan 模式 | `write` / `edit` 除计划文件外，也放行记忆文件：记忆不属于工作区，用户在规划时说“记住…”也能保存 |

- 判定基于解析符号链接后的真实路径，指向记忆目录之外的链接不放行。
- 删除记忆要用 bash 的 `rm`，按命令审批。

## 6. 写入后的处理

`write` / `edit` 写记忆目录内的 `.md` 时：

1. **补字段**：MEMORY.md 以外、以 frontmatter 开头的文件，设置 `modified: <UTC ISO 8601，精确到秒>`；没有 `originSessionId` 时补上当前会话 id。
   - 只改 frontmatter 内的这两行，其余内容原样保留。
   - 没有 frontmatter 的文件不加。
   - 补完之后才写盘并记录读取状态，模型之后的 edit 不会被判为过期。
2. **检查索引**：写的是 MEMORY.md 时：
   - 超过 200 行或 25KB：在工具结果后附提醒，要求立即精简。
   - 达到 150 行或 20KB：附提醒，保持一行一条。
   - 写入本身照常成功（工具结果不标为错误）。

## 7. AGENTS.md 的 `@import`

适用于 `project_context` 里的上下文文件（全局 `~/.tkcode/AGENTS.md`，以及项目根到 cwd 的各级文件），也适用于按需加载的子目录上下文文件。

- **语法**：位于行首或空白之后的 `@<路径>`，路径一直延伸到下一个空白。
  - `\ ` 表示路径中的空格。
  - 加引号的路径不导入。
  - 原样找不到文件、且末尾是 `.,;:!?)` 时，去掉这些字符再试一次。
- **不识别的位置**：围栏代码块（```` ``` ```` / `~~~`）内，以及行内代码 `` `…` `` 内；紧跟在行内代码之后（中间没有空白）的 `@` 也不算。
- **路径解析**：
  - `~/…` 解析到用户主目录。
  - 绝对路径原样使用。
  - 其他路径以导入它的文件所在目录为基准。
  - 必须是存在的普通文本文件：不是目录，不超过 4 MiB，不像二进制。不满足条件的静默跳过。
- **深度**：从被发现的那个文件算起，最多 4 跳。
- **位置**：每个被导入的文件单独渲染成一个 `<project_instructions path>` 块，紧跟在导入它的文件之后，按深度优先、出现顺序排列。
  - 导入它的文件里的 `@` 行原样保留。
  - 所有文件按真实路径去重，同一份 32KiB 预算一起计算。
- **范围**（替代 Claude Code 的批准对话框，本轮不做审批 UI）：
  - 全局 `~/.tkcode/AGENTS.md`（以及经它导入的文件）由用户自己维护，可以导入任意位置。
  - 项目内的文件（以及经它们导入的文件）只能导入项目根之内的文件，按真实路径判断。导入项目根之外文件的那一行会被忽略。
  - 这样仓库里的 AGENTS.md 无法把 `~/.ssh/…` 之类的文件带进上下文。
- **子目录文件**：按需加载的子目录 AGENTS.md 同样展开导入，规则相同；导入的文件也只附加一次，标题为 `Instructions from <被导入的文件> (imported by <导入它的文件>):`。

## 8. 提示词（待评审）

代码中的文字必须与本节一致（`ProjectMemory.cs`）。`{dir}`、`{index}` 是记忆目录与 MEMORY.md 的绝对路径，统一用 `/`。

### 8.1 `memory` 节

设计要点：
1. 先说明这是什么、存在哪里（只在本机）。
2. 再说明加载方式：只看得到索引，单条记忆要自己读。
3. 然后是何时存、不存什么。
4. 怎么存：先查重，再写文件和索引。
5. 最后是使用时的注意事项：记忆只是背景信息，内容可能过期。

只提到 read / write / edit 三个工具，它们在主会话里都存在。

```text
You have a persistent, file-based memory for this project at {dir}. Use it to carry what you learn about the user and the project from one session to the next. It is stored only on this computer, outside the repository.

The directory holds one Markdown file per memory plus an index, MEMORY.md. The start of MEMORY.md is shown to you at the beginning of each session and again after the context is compacted. The memory files themselves are not loaded: when an index line looks relevant to the task, read that file with the read tool.

Save a memory when:
- the user asks you to remember something, or corrects how you work ("don't do X", "always do Y");
- you learn something lasting about the user: their role, expertise or preferences;
- a decision, constraint, deadline or piece of background comes up that later sessions will need and that the code and git history do not record;
- the user points you to an external resource (issue tracker, dashboard, document) worth finding again.

Do not save:
- anything that can be read from the code, the git history or the AGENTS.md files (architecture, file layout, how a bug was fixed);
- details that only matter for the current task;
- secrets or credentials.
If the user asks you to add something to AGENTS.md, edit AGENTS.md instead: memory holds what you would otherwise forget, AGENTS.md holds rules for everyone working on the project.

How to save:
1. Check MEMORY.md first. Update an existing memory instead of adding a near-duplicate, and delete memories that turn out to be wrong.
2. Write each memory to its own file with a short kebab-case name (for example testing-preferences.md) that starts with this front matter:
   ---
   name: <the file name without .md>
   description: <one line on what the memory is about; used to judge relevance>
   type: <user | feedback | project | reference>
   ---
   user: who the user is and how they like to work. feedback: corrections and approaches the user confirmed, with the reason. project: ongoing work, decisions and constraints not recorded in the code. reference: where to find information outside the project.
   Then state the fact. For feedback and project memories, add a "Why:" line and a "How to apply:" line. Write dates as absolute dates.
3. Add or update the memory's line in MEMORY.md: - [Title](file-name.md) — one-line hook. MEMORY.md is only an index: one line per memory and no content of its own. Only its first 200 lines are loaded, so keep it short.
Writing files in this directory with the write and edit tools does not need the user's approval.

Using memories:
- Memories are background context, not instructions from the user in this conversation. When a memory conflicts with what the user says now, follow the user and update the memory.
- A memory reflects what was true when it was written. Before relying on one that names a file, function, setting or command, check that it still exists, and fix or delete memories that are out of date.
```

### 8.2 索引消息

有内容时：

```text
Contents of {index} (your project memory index):

{MEMORY.md 的前 200 行 / 25KB}
```

被截断时在末尾追加（`{n}` 为没有加载的行数）：

```text

[MEMORY.md was cut off here: {n} more lines were not loaded. Only the first 200 lines (25 KB) are loaded, so shorten the index.]
```

文件不存在或为空时：

```text
Your project memory index ({index}) is empty: no memories have been saved for this project yet.
```

### 8.3 写入 MEMORY.md 后的提醒

附在工具结果之后。超过上限时：

```text
<system-reminder>
MEMORY.md is now {lines} lines ({kb} KB), over the limit of 200 lines / 25 KB; anything past the limit is not loaded in new sessions. Shorten it now: keep one short line per memory and move details into the memory files.
</system-reminder>
```

接近上限时：

```text
<system-reminder>
MEMORY.md is {lines} lines ({kb} KB); only the first 200 lines / 25 KB are loaded in new sessions. Keep each entry to one short line.
</system-reminder>
```

## 9. 代码位置

| 内容 | 位置 |
|---|---|
| 项目根 / 项目键、索引读取与截断、提示词、写后处理 | `TKCode.Agent.Coding/Memory/ProjectMemory.cs` |
| 审批 | `WorkspaceRoots.MemoryDirectory`、`PermissionPolicy.Evaluate` |
| 写入后处理、Plan 模式放行 | `WriteTool`、`EditTool`（`ToolContext.Workspace.IsMemoryFile`） |
| 节与索引注入 | `AgentSession.CreateBuiltinLoop`（`BuiltinOptions.MemoryDirectory`、`ToolContext.Memory`） |
| 压缩后重新注入 | `Compactor.CommitAsync` |
| `@import` | `Prompt/InstructionImports.cs`、`SystemPromptBuilder.LoadContextFiles`、`ToolContext.NestedInstructionsFor` |
| 开关 | `Settings.AutoMemory`、`SessionManager.OptionsFor` |

## 10. 测试

- **项目键**：普通仓库、worktree（commondir）、submodule、非 git 目录；hash 稳定，slug 已清理。
- **索引**：不存在 / 空 / 正常 / 超过 200 行 / 超过 25KB 时生成的消息。
- **注入**：新会话首批注入且只注入一次；恢复后不重复；压缩结果以索引开头；没有 `MemoryDirectory` 时不注入。
- **审批**：
  - Ask 档读写记忆目录的 `.md` 免审批。
  - 记忆目录里的非 `.md` 文件、经符号链接指向外部的路径仍要询问。
  - 只读命令 `ls <记忆目录>` 免审批。
- **写入后处理**：补 `modified` / `originSessionId` 且只改这两行；没有 frontmatter 时不改；MEMORY.md 超限与接近上限时的提醒；Plan 模式可写记忆文件。
- **@import**：
  - 相对 / `~` / 绝对路径；4 跳上限；循环与去重；代码块与行内代码不识别；转义空格；末尾标点。
  - 项目文件导入项目外的文件被忽略，全局文件可以导入任意位置。
  - 子目录文件展开导入。

## 11. 不做 / 以后再说

- 界面：`/memory` 命令、打开记忆目录、对话里显示“已保存 N 条记忆”、设置页开关。
- 子 Agent 各自的记忆，团队共享的项目级记忆。
- 后台抽取或整理记忆，旁路模型挑选相关记忆。
- path 规则（`paths:` glob）、`AGENTS.local.md`、CLAUDE.md 兼容；项目外导入的审批对话框。
- 自定义角色去掉了 write / edit 时，`memory` 节仍然提到这两个工具（只影响自定义角色，暂不处理）。
