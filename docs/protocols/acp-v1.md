# Agent Client Protocol (ACP) v1 客户端协议规格（TKCode 实现用）

> 版本基线：ACP schema **v1**（`agent-client-protocol-schema` 1.11.0，2026-10-09；仓库 commit `5ab891a`，2026-10-16 克隆）。
> 用途：TKCode（C#/.NET 10，NativeAOT）在 Agent Host 内作为 ACP **Client**，通过 stdio 启动并驱动原生支持 ACP 的 Agent（Gemini CLI、GitHub Copilot CLI、Cursor、OpenCode、Qwen Code、Kimi CLI、Goose 等），对应设计文档 [external-agent-runtimes.md](../design/external-agent-runtimes.md) 中的 `AcpRuntime`。
> 编写日期：2026-10-16。方法名、字段名、枚举值一律保留英文原文。所有结论都标了出处；标「待核实」的条目没有在一手资料里确认，实现时必须做容错，不要硬依赖。

## 0. 来源与可信度标注

| 标记 | 来源 |
|---|---|
| **[Schema]** | 规范仓库 `schema/v1/schema.json`（权威）与 `schema/v1/meta.json`（方法名表）。本地路径：`/private/tmp/claude-501/-Users-token-code-AtomUI-TKCode/441895dd-b473-4eec-9848-b8a86c1a67e3/scratchpad/research/acp-agent-client-protocol/repo/`（下文简称 `repo/`）。线上：`https://github.com/agentclientprotocol/agent-client-protocol/blob/main/schema/v1/schema.json` |
| **[Unstable]** | `repo/schema/v1/schema.unstable.json`、`meta.unstable.json`：未稳定的扩展，Agent 可能发送，Client 只能宽松读取 |
| **[Doc:xxx]** | `repo/docs/protocol/v1/xxx.mdx`，即 `https://agentclientprotocol.com/protocol/v1/xxx` |
| **[RFD:xxx]** | `repo/docs/rfds/xxx.mdx`；**[RFD-v2:xxx]** 为 `repo/docs/rfds/v2/xxx.mdx` |
| **[CL]** | `repo/CHANGELOG.md`（schema 发布记录，用于判断某特性何时稳定） |
| **[Registry]** | ACP Registry 快照 `/private/tmp/claude-501/-Users-token-code-AtomUI-TKCode/441895dd-b473-4eec-9848-b8a86c1a67e3/scratchpad/research/acp-registry/registry.json`（`https://cdn.agentclientprotocol.com/registry/v1/latest/registry.json`） |
| **[zeron]** | zeron 的 ACP Client 实现：`scratchpad/research/zeron/crates/harness/src/acp/mod.rs`、`normalize.rs`，以及 `docs/research/acp.md`。用于交叉核对真实 Agent 的怪癖 |
| **[本机]** | 在本机只读检查得到：`cursor-agent` 2026.08.11-e8db854 的安装包（`~/.local/share/cursor-agent/versions/2026.08.11-e8db854/*.js`，只做 grep，未启动 ACP 会话）、`cursor-agent --version/--help` 的输出、VS Code Copilot Chat 扩展放在 PATH 上的 `copilot` 包装脚本 |
| **[待核实]** | 一手资料没有覆盖，取自经验或推断 |

稳定性时间线（[CL]）：`session/list` 与 `session_info_update` 在 0.11.1 稳定；`session/resume`、`session/close` 在 0.12.2；`logout` 在 0.13.3；`additionalDirectories` 在 0.13.5；`messageId`、`usage_update`、`session/delete` 在 0.13.6；1.0.0 定版；`model_config` 分类在 1.1.0；`$/cancel_request` 在 1.2.0；`boolean` 配置项在 1.3.0；elicitation 与 terminal 认证在 1.7.0（schema-v1.21.0，2026-08-20，“stabilize elicitation (#1779)”）；tool call `name` 在 1.8.0；`notice` 与 compaction 在 1.11.0。旧 Agent 可能不发送这些较新的字段，TKCode 必须把它们全部当作可选。

---

## 1. 角色与方法总表

ACP 基于 JSON-RPC 2.0。**Client 启动 Agent 子进程**；双方都可以发请求和通知 [Doc:overview]。TKCode 只扮演 Client。

### 1.1 Client → Agent（TKCode 发出）

| 方法 | 类型 | 前置能力 | TKCode 用法 |
|---|---|---|---|
| `initialize` | 请求 | — | **必须**，连接上的第一条消息 |
| `authenticate` | 请求 | `authMethods` 中有非 terminal 类型的方法 | 收到 `-32000` 后按 §4 处理 |
| `session/new` | 请求 | 基线 | **必须** |
| `session/load` | 请求 | `agentCapabilities.loadSession: true` | 恢复会话的后备方案（§5.3） |
| `session/resume` | 请求 | `sessionCapabilities.resume` 非 null | 恢复会话的首选方案（§5.3） |
| `session/list` | 请求 | `sessionCapabilities.list` 非 null | 第一阶段不用（设计：只显示 TKCode 自己的会话） |
| `session/close` | 请求 | `sessionCapabilities.close` 非 null | 关闭会话时调用；没有该能力就直接结束进程 |
| `session/delete` | 请求 | `sessionCapabilities.delete` 非 null | 不用 |
| `session/prompt` | 请求 | 基线 | **必须**，一轮对话 |
| `session/cancel` | **通知** | 基线 | **必须**，中断当前轮次 |
| `session/set_config_option` | 请求 | Agent 返回了 `configOptions` | 切换模型、思考深度、模式等（§10） |
| `session/set_mode` | 请求 | Agent 返回了 `modes` | 旧接口，只在没有 `category:"mode"` 配置项时使用（§10.5） |
| `logout` | 请求 | `agentCapabilities.auth.logout` 非 null | 不用（TKCode 不管理凭据） |
| `$/cancel_request` | 通知 | — | 放弃 `session/prompt` 以外的挂起请求时发送（§9.2） |

`session/set_model` 不在 v1 schema 中（unstable 接口，已于 2026-06-01 删除 [RFD:updates]），但仍有 Agent 在用，见 §10.6。

### 1.2 Agent → Client（TKCode 必须处理）

| 方法 | 类型 | TKCode 处理 |
|---|---|---|
| `session/update` | 通知 | **必须**，全部变体见 §7 |
| `session/request_permission` | 请求 | **必须**，映射到 TKCode 审批（§8） |
| `fs/read_text_file`、`fs/write_text_file` | 请求 | 不声明能力，收到就回 `-32601`（§12） |
| `terminal/create`、`terminal/output`、`terminal/release`、`terminal/wait_for_exit`、`terminal/kill` | 请求 | 同上 |
| `elicitation/create` | 请求 | 不声明能力，收到就回 `-32602`（§11） |
| `elicitation/complete` | 通知 | 忽略 |
| `$/cancel_request` | 通知 | 取消对应的 Agent→Client 请求（§9.2） |
| 以 `_` 开头的扩展方法、其它未知方法 | 请求 / 通知 | 请求回 `-32601`，通知忽略（§12） |

---

## 2. 传输与分帧（stdio）

### 2.1 规则 [Doc:transports]

- Client 以子进程方式启动 Agent。Agent 从 **stdin** 读消息，向 **stdout** 写消息。
- 消息必须是 **UTF-8**。
- 每条消息是一个 JSON-RPC 请求、通知或响应，**以 `\n` 分隔，消息内部不得包含换行**。
- **没有 `Content-Length` 头**。这一点与 TKCode 内部 UI↔Host 的 `JsonRpcConnection`（Content-Length 分帧）不同，必须使用按行分帧模式（设计文档 §9 第 2 步）。
- Agent 可以往 **stderr** 写任意 UTF-8 日志，Client 可以采集、转发或忽略。
- Agent 不得向 stdout 写非 ACP 内容；Client 不得向 stdin 写非 ACP 内容。
- 关闭：Client 关闭 stdin 并结束子进程。

### 2.2 TKCode 写入要求

1. 每条消息用紧凑格式序列化（`WriteIndented = false`），后面追加一个 `\n`（只用 LF，不用 CRLF），然后 flush。System.Text.Json 会把字符串里的换行转义成 `\n`，所以紧凑输出天然不含裸换行；不要使用会输出裸控制字符的自定义编码器。
2. 只有一个写者：所有写操作走单写者队列，保证一条消息完整写出后才写下一条。
3. 发出的请求 `id` 用 `long`，每个连接从 0 开始递增。不发送 `id: null` 的请求。
4. 不发送 JSON-RPC 批量数组（v1 没有定义批量；v2 才会遵循 JSON-RPC 批量规则 [RFD-v2:overview]）。

### 2.3 TKCode 读取要求

1. 以 `\n` 切分。切出的行如果以 `\r` 结尾，去掉 `\r`。空行和纯空白行跳过。
2. **单行没有长度上限**：图片、diff、`rawOutput` 都可能让一行达到数 MB。用可增长的缓冲区（`PipeReader`），设一个保护上限（建议 64 MiB），超过时判定连接损坏。
3. 无法解析为 JSON 的行：写进诊断日志（截断到 1 KB），**跳过，不断开连接**。有些 CLI 在启动时会往 stdout 打印横幅或警告，这是对规范的违反，但 Client 应该能活下来 [待核实：具体哪些 Agent 会这样做]。连续出现大量（例如 100 行）无法解析的行时，判定连接损坏。
4. 一行是 JSON 数组时（为 v2 做准备），按顺序逐个处理数组元素。
5. 消息分类：
   - 有 `method` 且有 `id` → 请求；
   - 有 `method` 没有 `id` → 通知；
   - 没有 `method`、有 `id`，且有 `result` 或 `error` → 响应。
   - 其它形状：记录日志后丢弃。
6. 收到的请求 `id` 可以是整数或字符串（[Schema] `RequestId`：`null | int64 | string`）。回复时**原样回传**（保留原始 `JsonElement`），不要转换类型。
7. **两个方向的 id 空间相互独立**：Agent 发来的请求 `id: 0` 与 TKCode 自己发出的 `id: 0` 没有关系（夹具 B、C、D 就是这样）。
8. 响应按 `id` 匹配本端挂起的请求；找不到对应请求的响应，记录日志后丢弃。

### 2.4 顺序保证（重要）

**所有入站消息必须在同一个读循环里严格按到达顺序处理，包括完成挂起请求的响应。**

原因：`session/prompt` 的响应标志着轮次结束，而它之前的所有 `session/update` 必须先被应用。如果响应走「挂起表 → 另一个线程完成 Task」，而通知走另一条队列，就会出现 `agentEnded` 早于最后几个增量的情况。zeron 为这个问题修了两次：`session/load` 返回后要冲刷队列，轮次结束前要先排空排队的更新 [zeron:docs/research/acp.md「Ordering hazard」，mod.rs `request_draining`]。

实现建议：
- 读循环把每条消息按顺序投递到每个会话的事件处理器，响应也作为一条「事件」进入同一序列，由处理器在处理到它时才完成对应的 `TaskCompletionSource`。
- Agent 发来的**请求**（例如权限请求）不能阻塞读循环：在顺序中登记这个请求（例如创建待审批项），然后异步等待用户，回复通过写队列发出。

### 2.5 进程管理

| 项 | 要求 |
|---|---|
| 启动 | 直接 exec 可执行文件，参数按 §17 的表格，**不经过 shell**、不使用 `npx` / `uvx` |
| 工作目录 | 设为会话的 `cwd`（协议上以 `session/new.cwd` 为准，但有些 Agent 会读进程 cwd）[Doc:session-setup「Working Directory」] |
| 环境变量 | 继承 Host 的环境；`PATH` 最前面加上可执行文件所在目录（让 `#!/usr/bin/env node` 一类的脚本在 GUI 启动时也能运行，见设计文档 §4）；叠加 Registry 条目中的 `env`（例如关闭自动更新） |
| stderr | **必须持续读取**，否则管道写满会让 Agent 卡死。只保留最后约 64 KB 的环形缓冲，脱敏后用于错误提示 |
| 握手超时 | `initialize` 设超时（建议 60 秒；zeron 用 120 秒，因为有的 Agent 冷启动很慢 [zeron:acp.md]）。超时就结束进程并报错 |
| 其它超时 | `session/prompt` **不设超时**。轮次结束只以响应为准，不用静默计时器之类的启发式判断（zeron 已因此废弃 quiet 计时器 [zeron:acp.md「Pending prompts and silence」]） |
| 关闭 | 有 `sessionCapabilities.close` 就先发 `session/close`；然后关闭 stdin；等 2 秒；对进程组发 SIGTERM；再等 3 秒；发 SIGKILL。Windows 用 Job Object |
| 进程意外退出 | 所有挂起请求以「连接断开」失败；进行中的轮次以 Error 结束（如果此前已经发出 `session/cancel`，则以 Aborted 结束）；把 stderr 尾部附在错误信息里。EOF 紧跟在最后一个响应之后属于正常结束 [zeron:acp.md] |

**一个会话一个进程**：ACP 允许在同一连接上建多个会话，但 TKCode 采用「每个活跃会话一个 Agent 进程」（设计文档 §3），这样隔离更好，中断时可以直接结束进程。连接层仍然要按 `sessionId` 校验和路由。

---

## 3. 初始化：`initialize`

### 3.1 请求

字段（[Schema] `InitializeRequest`）：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `protocolVersion` | `uint16` | 是 | Client 支持的**最新**主版本。TKCode 发 `1` |
| `clientCapabilities` | `ClientCapabilities` | 否 | 省略的能力一律视为**不支持** [Doc:initialization] |
| `clientInfo` | `Implementation` | 否（SHOULD） | `{name, title?, version}`，其中 `name` 和 `version` 必填 |
| `_meta` | object | 否 | 扩展元数据 |

TKCode 发送的确切内容：

```json
{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":1,"clientCapabilities":{"fs":{"readTextFile":false,"writeTextFile":false},"terminal":false,"elicitation":{"form":{}},"auth":{"terminal":false},"session":{"configOptions":{"boolean":{}},"notices":{}}},"clientInfo":{"name":"tkcode","title":"TKCode","version":"<TKCode 版本号>"}}}
```

`clientInfo.name` 会被部分 Agent 用作宿主标识，例如 Cursor 把它放进 `x-cursor-host-app-name` 请求头 [本机]。固定使用 `tkcode`。

### 3.2 每项能力的取舍与理由

| 能力 | TKCode 值 | 理由 |
|---|---|---|
| `fs.readTextFile` / `fs.writeTextFile` | `false` | ① 这组方法存在的意义是让 Agent 读到编辑器里**未保存的缓冲区**、并让编辑器跟踪改动 [Doc:file-system]；TKCode 不是编辑器，磁盘就是唯一真相，Agent 自己的文件工具读写结果完全相同。② 声明后，TKCode 就要自己实现路径边界检查、权限和并发写入，等于给 Agent 开了一个绕过其自身权限系统的后门。③ ACP v2 草案删除了整个 `fs/*` 方法组 [RFD-v2:overview]。④ zeron 同样拒绝 [zeron:mod.rs `initialize_params`] |
| `terminal` | `false` | ① 声明后 Agent 会把命令交给 Client 执行（`terminal/create` 等 5 个方法），TKCode 就要负责沙箱、输出缓冲、超时和进程树清理，这些本该是 Agent 自己的职责。② 不声明时，Agent 用自己的 shell 工具执行，并通过 `session/request_permission` 请求批准，TKCode 的审批照样生效。③ v2 草案删除了 Client 执行终端的接口，改为 Agent 自有、只读展示的终端输出 [RFD-v2:overview]。副作用：`ToolCallContent` 里的 `terminal` 变体不应出现（§7.5） |
| `auth.terminal` | `false` | 设为 `true` 表示 Client 能在交互式终端里重现 Agent 的启动命令 [Doc:authentication]。TKCode 第一阶段没有内嵌终端，而且设计上不经手登录。等内嵌终端（Iciclecreek.Avalonia.Terminal）上线后再改为 `true`（§4.3） |
| `elicitation` | 省略 | 省略或 `null` 表示不支持；**注意 `{}` 也表示不支持任何模式**，与 MCP 不同 [Doc:elicitation]。TKCode 的 `input/request` 提问面板完成之前不声明（§11） |
| `session.configOptions.boolean` | `{}` | 允许 Agent 发 `type:"boolean"` 的配置项。TKCode 渲染为开关即可，成本很低；不声明的话 Agent 可能直接隐藏这类选项 [Doc:session-config-options] |
| `session.notices` | `{}` | 不声明时，Agent 可能把通知当作普通 Agent 消息发送，污染对话记录 [Doc:prompt-turn「Session Notices」]。声明后 TKCode 用 toast 显示，不写入转录 |
| `session.compaction` | 省略 | 需要实现按 ID 寻址的压缩实体（补丁、重放语义），第一阶段不做。不声明时 Agent 不得发送 `compaction_update` / `compaction_summary_chunk`，可以改用普通消息说明 |
| `_meta` | 省略 | 不声明任何厂商扩展（例如 Devin 的 `cognition.ai/subagentSupport` [zeron]） |

### 3.3 响应

字段（[Schema] `InitializeResponse`）：

| 字段 | 类型 | 默认值 | 说明 |
|---|---|---|---|
| `protocolVersion` | `uint16` | 必填 | Agent 选定的版本 |
| `agentCapabilities` | `AgentCapabilities` | 见下 | 解析失败时取默认值 |
| `authMethods` | `AuthMethod[]` | `[]` | 解析失败的条目跳过 |
| `agentInfo` | `Implementation` \| null | — | 用于界面显示名称和版本 |
| `_meta` | object | — | 厂商扩展（例如 `_meta.steering.supported` [zeron]），TKCode 忽略 |

`AgentCapabilities`：

| 路径 | 类型 | 默认 | 含义 / TKCode 用法 |
|---|---|---|---|
| `loadSession` | bool | `false` | 是否支持 `session/load` |
| `promptCapabilities.image` | bool | `false` | prompt 可以带 `image` 块 |
| `promptCapabilities.audio` | bool | `false` | prompt 可以带 `audio` 块（TKCode 不用） |
| `promptCapabilities.embeddedContext` | bool | `false` | prompt 可以带 `resource` 块 |
| `mcpCapabilities.http` | bool | `false` | `mcpServers` 可以用 HTTP 传输 |
| `mcpCapabilities.sse` | bool | `false` | SSE 传输（MCP 已废弃，TKCode 不用） |
| `sessionCapabilities.list` / `resume` / `close` / `delete` / `additionalDirectories` | object \| null | 省略 | **非 null 对象（包括 `{}`）表示支持**；省略或 `null` 表示不支持 |
| `auth.logout` | object \| null | 省略 | 同上 |
| `_meta` | object | — | 厂商自定义能力 |

基线：所有 Agent 都必须支持 `session/new`、`session/prompt`、`session/cancel`、`session/update`，以及 prompt 中的 `text` 和 `resource_link` 内容块 [Doc:initialization]。

### 3.4 版本协商

- Client 发自己支持的最新版本；Agent 支持该版本就原样返回，否则返回它支持的最新版本 [Doc:initialization]。
- TKCode 收到的 `protocolVersion` **不等于 1** 时：关闭连接，界面提示「该 Agent 使用 ACP v{N}，TKCode 目前只支持 v1」。不要尝试按 v1 继续通信。
- 能力的新增不算破坏性变更，所以同为 v1 的 Agent 之间能力差异很大，一切以能力字段为准。

### 3.5 真实 Agent 的初始化响应示例

Cursor 2026.08.11（[本机] 安装包 `2996.index.js`）：`loadSession:true`、`mcpCapabilities:{http:true,sse:true}`、`promptCapabilities:{audio:false,embeddedContext:false,image:true}`、`sessionCapabilities:{list:{}}`（没有 resume / close），`authMethods:[{id:"cursor_login",name:"Cursor Login",description:"Authenticate using existing Cursor login credentials. Run 'cursor-agent login' first if not logged in."}]`。

---

## 4. 认证

### 4.1 认证方法 [Schema `AuthMethod`；Doc:authentication]

`authMethods` 的每个条目都有 `id`、`name`，可选 `description`、`_meta`。按 `type` 区分：

| `type` | 结构 | Client 的做法 |
|---|---|---|
| 省略（即 `agent`） | `{id, name, description?}` | 调用 `authenticate {methodId}`，由 Agent 自己完成登录（可能复用已有凭据，也可能打开浏览器） |
| `"terminal"` | `{id, name, description?, type:"terminal", args?: string[], env?: {string:string}}` | 用**同一个 Agent 程序和基础启动配置**，追加 `args`、覆盖 `env`，在交互式终端里运行；退出码 0 表示成功；然后**重新连接并重新 `initialize`**。**不得**对 terminal 方法调用 `authenticate` |
| 其它未知值 | — | 跳过该条目 |

Agent 只有在 Client 声明 `auth.terminal: true` 时才可以返回 terminal 方法。TKCode 声明为 `false`，所以第一阶段不应收到 terminal 方法；收到了也只按 §4.3 给出提示，不自动执行。

### 4.2 `authenticate`

```json
{"jsonrpc":"2.0","id":2,"method":"authenticate","params":{"methodId":"cursor_login"}}
{"jsonrpc":"2.0","id":2,"result":{}}
```

- 参数：`methodId`（必填）、`_meta`。成功返回空对象（可能带 `_meta`）。
- 成功后，创建会话等受认证保护的请求不再返回 `auth_required`。

### 4.3 TKCode 的认证策略

设计文档的约束：**TKCode 不提供登录、不读写凭据，直接复用 CLI 已有的登录状态**。因此：

1. **不在 `initialize` 后主动调用 `authenticate`**。直接发 `session/new`。
2. `session/new`（或 `session/load` / `session/resume` / `session/prompt`）返回错误码 **`-32000`（Authentication required）** 时，进入认证处理：
   - `authMethods` 中恰好有一个 agent 类型方法，且该方法属于「复用已有凭据、不弹出交互」的已知白名单时，自动调用一次 `authenticate`，成功后重试原请求一次。白名单目前只有 Cursor 的 `cursor_login`：Cursor 的 `session/new` 在未认证时会报错，提示先运行 `cursor-agent login`，再用 `cursor_login` 调用 `authenticate` [本机]。其它方法（例如 Gemini 的 `oauth-personal`）可能会打开浏览器，[待核实] 不自动调用。
   - 否则在界面显示「需要登录」：列出每个 agent 类型方法的 `name` / `description`，由用户点击后调用 `authenticate`；同时显示通用提示「请在终端运行 `<可执行文件> login`（或该 CLI 的登录命令），完成后点击重试」。
   - terminal 类型方法：显示可复制的命令行 `<可执行文件> <args...>`，以及需要设置的 `env`，由用户在自己的终端运行，完成后 TKCode 重启 Agent 进程并重新初始化。等 TKCode 有了内嵌终端，再改为声明 `auth.terminal:true` 并按 §4.1 的流程自动执行。
3. `authenticate` 自身失败：把 `error.message` 显示给用户，不再自动重试。
4. `logout`：TKCode 不调用。即使需要，也必须先确认 `agentCapabilities.auth.logout` 非 null [Doc:authentication]。
5. 会话中途收到 `-32000`（例如凭据过期）：结束当前轮次（Error），显示登录提示；用户重新登录后，重启进程并用 `session/resume` / `session/load` 恢复。

---

## 5. 会话生命周期

### 5.1 `session/new`

请求（[Schema] `NewSessionRequest`）：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `cwd` | string | 是 | **绝对路径**。会话的主工作目录，也是相对路径的基准，与 Agent 进程从哪里启动无关 |
| `mcpServers` | `McpServer[]` | **是** | 必须发送，没有就发 `[]` |
| `additionalDirectories` | string[] | 否 | 只有 `sessionCapabilities.additionalDirectories` 非 null 时才能发；每项必须是绝对路径 |
| `_meta` | object | 否 | |

`McpServer`（TKCode 自己的工具以 MCP 服务器的形式交给 Agent 时使用，设计文档 §8）：

| 变体 | 判别 | 必填字段 |
|---|---|---|
| stdio（所有 Agent 必须支持） | **没有 `type` 字段** | `name`、`command`（可执行文件**绝对路径**）、`args: string[]`、`env: [{name, value}]`（四个都必填，空就发 `[]`） |
| http | `type:"http"` | `name`、`url`、`headers: [{name, value}]`；需要 `mcpCapabilities.http` |
| sse | `type:"sse"` | 同 http；需要 `mcpCapabilities.sse`；已废弃，TKCode 不用 |

响应（[Schema] `NewSessionResponse`）：

| 字段 | 类型 | 说明 |
|---|---|---|
| `sessionId` | string | 必填，不透明 |
| `configOptions` | `SessionConfigOption[]` \| null | 会话配置项（§10），解析失败的条目跳过 |
| `modes` | `SessionModeState` \| null | 旧的模式接口（§10.5） |
| `_meta` | object | |
| `models`（**schema 外**） | `{currentModelId, availableModels:[{modelId, name, description?}]}` | 已删除的 unstable 模型接口，部分 Agent 仍然发送（§10.6） |

**真实怪癖：`session/new` 响应之前就可能到达该会话的通知。** 例如 Agent 在创建会话时立即推送 `available_commands_update` 或 `config_option_update`，而此时 TKCode 还不知道 `sessionId`。处理规则：读循环收到未知 `sessionId` 的 `session/update` 时，如果本连接上有挂起的 `session/new` / `session/load` / `session/resume`，就按 `sessionId` 暂存（上限 64 条）；响应到达后，对匹配的会话按原顺序回放这些暂存通知。zeron 也是这样做的，原因是丢掉它们可能会漏掉唯一一次「新模型上线」的通知 [zeron:mod.rs `request_draining`]。夹具 F 覆盖这个场景。Cursor 是在响应之后用 `setTimeout` 推送命令列表的 [本机]。

### 5.2 `session/load`（需要 `loadSession: true`）

请求：`{sessionId, cwd, mcpServers, additionalDirectories?, _meta?}`，其中 `sessionId`、`cwd`、`mcpServers` 必填。

语义 [Doc:session-setup]：
- Agent **必须**先用 `session/update` 通知重放**整段**对话（`user_message_chunk`、`agent_message_chunk`、`tool_call` 等），全部发完后才响应。
- 响应：`{configOptions?, modes?, _meta?}`。
- 实时通知（`notice`）不重放。

TKCode 的处理：
- TKCode 自己的 JSONL 才是转录的唯一来源，所以**重放的消息类和工具类更新一律不渲染、不写盘**。
- 重放期间的**状态类**更新（`config_option_update`、`available_commands_update`、`current_mode_update`、`session_info_update`、`usage_update`）照常应用。
- 响应到达后，按 §2.4 先处理完它之前的所有通知，再进入「就绪」状态。之后才到达的 `user_message_chunk` 等不再属于重放。

### 5.3 `session/resume`（需要 `sessionCapabilities.resume`）

请求：`{sessionId, cwd, mcpServers?, additionalDirectories?, _meta?}`（[Schema] 中只有 `sessionId`、`cwd` 必填，TKCode 仍然总是发送 `mcpServers`）。

语义：恢复上下文，**不重放历史**，准备好后响应 `{configOptions?, modes?, _meta?}`。

**TKCode 的恢复顺序**（设计文档 §7）：
1. 有 `resume` 能力 → `session/resume`。
2. 否则有 `loadSession` → `session/load`，丢弃重放内容。
3. 都没有，或调用失败 → `session/new`，界面提示「Agent 端上下文已丢失，新会话不包含之前的对话」。

已知失败情形：Devin 在旧进程仍持有会话时，`session/load` 返回 `-32015 "already open in another process"` [zeron:acp.md]。另一个怪癖：被取消的轮次会在 Devin 的历史里留下一条没有回复的用户消息，恢复后的下一轮可能同时回答新旧两个问题 [zeron:acp.md]。

`cwd` 必须与原会话一致（TKCode 只在同一个 cwd 下恢复）。`additionalDirectories` 每次都要发完整列表，省略不会恢复之前的值 [Doc:session-setup]。

### 5.4 `session/list`（需要 `sessionCapabilities.list`）

第一阶段不使用，这里记下规格以备后用：
- 请求：`{cwd?, cursor?, _meta?}`，全部可选；`cwd` 是绝对路径过滤条件。
- 响应：`{sessions: SessionInfo[], nextCursor?}`。`SessionInfo = {sessionId, cwd, additionalDirectories?, title?, updatedAt?(ISO 8601), _meta?}`。
- 没有 `nextCursor` 表示结束；游标不透明，不要解析或持久化。
- Cursor 不支持分页，传 `cursor` 会返回 invalid params [本机]。

### 5.5 `session/close`（需要 `sessionCapabilities.close`）

请求 `{sessionId}`，响应 `{}`。Agent 必须先像收到 `session/cancel` 一样取消进行中的工作，再释放资源。会话不存在时可以报错，TKCode 忽略这个错误。TKCode 在关闭会话或退出时调用，然后按 §2.5 结束进程。

### 5.6 `session/delete`

不使用（删除的是 Agent 侧的历史，TKCode 不管理）。

---

## 6. 轮次：`session/prompt`

### 6.1 请求

```json
{"jsonrpc":"2.0","id":2,"method":"session/prompt","params":{"sessionId":"sess_7f3a9c","prompt":[{"type":"text","text":"..."}]}}
```

| 字段 | 类型 | 必填 |
|---|---|---|
| `sessionId` | string | 是 |
| `prompt` | `ContentBlock[]` | 是 |
| `_meta` | object | 否 |

**ACP 没有逐轮的模型、温度、系统提示参数**。模型和思考深度只能通过 §10 的配置项设置。

### 6.2 `ContentBlock`（判别字段 `type`）[Schema；Doc:content]

ContentBlock 与 MCP 的结构相同，所有变体都可以带 `annotations`（`{audience?: ("assistant"|"user")[], lastModified?: string, priority?: number}`）和 `_meta`。

| `type` | 必填字段 | 可选字段 | 在 prompt 中使用的前提 |
|---|---|---|---|
| `text` | `text` | `annotations` | 基线 |
| `image` | `data`（base64）、`mimeType` | `uri`、`annotations` | `promptCapabilities.image` |
| `audio` | `data`（base64）、`mimeType` | `annotations` | `promptCapabilities.audio` |
| `resource_link` | `uri`、`name` | `mimeType`、`title`、`description`、`size`(int64)、`annotations` | 基线 |
| `resource` | `resource`：`{uri, text, mimeType?}`（文本）或 `{uri, blob(base64), mimeType?}`（二进制） | `annotations` | `promptCapabilities.embeddedContext` |

TKCode 把用户消息映射为 prompt 的规则：
1. 文本 → 一个 `text` 块。
2. 图片 → `image` 块；Agent 不支持时**不发送**，并在界面提示「该 Agent 不支持图片」。
3. 引用的文件（@ 提及、附件）：
   - 支持 `embeddedContext` 且是小于 256 KB 的文本文件 → `resource`（`uri` 为 `file://` 绝对路径，带 `text`）。这是规范推荐的上下文传递方式 [Doc:content]。
   - 否则 → `resource_link`（`uri` 为 `file://` 绝对路径，`name` 为文件名，尽量带 `mimeType` 和 `size`）。
4. 斜杠命令：就是普通文本，以 `/name 参数` 开头 [Doc:slash-commands]。命令列表来自 `available_commands_update`（§7.9）。
5. 发出的 prompt 必须严格遵守 promptCapabilities [Doc:prompt-turn]。

### 6.3 同一会话同时只能有一个轮次

在上一个 `session/prompt` 的响应到达之前，不要发下一个 `session/prompt`。真实 Agent 会拒绝重叠的 prompt（报 Invalid request）[zeron:acp.md]。TKCode 的 steer / followUp 队列在 ACP 下都按**轮次边界**投递：等当前轮次结束后，作为下一个 `session/prompt` 发出（ACP v1 没有标准的中途插话接口；`_session/steering` 是厂商扩展 [zeron]，TKCode 不用）。

### 6.4 响应与 `stopReason`

响应（[Schema] `PromptResponse`）：`{stopReason, _meta?}`。

| `stopReason` | 含义 [Doc:prompt-turn] | TKCode `StopReason` | 说明 |
|---|---|---|---|
| `end_turn` | 模型结束回答，不再请求工具 | `Stop` | |
| `max_tokens` | 达到 token 上限 | `Length` | 提示用户输出被截断 |
| `max_turn_requests` | 单轮内模型请求次数超限 | `Stop` | 附带提示「Agent 达到单轮请求上限，可以发送"继续"」 |
| `refusal` | Agent 拒绝继续 | `Error` | 错误文案「Agent 拒绝继续」；zeron 同样当作错误 [zeron:mod.rs `stop_outcome`] |
| `cancelled` | Client 取消了轮次 | `Aborted` | |
| 其它未知值（例如 unstable / v2 的 `error`） | — | `Error` | 文案里带上原始值 |

**Agent 的取消必须返回 `cancelled`，而不是错误**：规范要求 Agent 捕获中止时抛出的异常 [Doc:prompt-turn「Cancellation」]。但 TKCode 仍要容错：已经发出 `session/cancel` 后收到任何错误响应，一律按 `Aborted` 处理，不显示错误。

**逐轮用量（unstable）**：schema.unstable 在 `PromptResponse` 上增加了 `usage: {totalTokens, inputTokens, outputTokens, thoughtTokens?, cachedReadTokens?, cachedWriteTokens?}`（RFD end-turn-token-usage，仍处于草案阶段）[Unstable]。如果存在就读取。Grok 把 `inputTokens` / `outputTokens` 放在响应的 `_meta` 里 [zeron:mod.rs `usage_from_response`]。都没有时，本轮不报逐轮用量。

### 6.5 错误响应

`session/prompt` 返回 JSON-RPC 错误时：结束轮次（`agentEnded`，`StopReason.Error`），错误信息带上 `code`、`message`，以及非 null 的 `data`（zeron 修过一个丢弃 `error.data` 导致看不到原因的 bug [zeron:acp.md]）。`-32000` 转到 §4.3；`-32800` 按 `Aborted` 处理。

### 6.6 轮次结束的唯一标准

**只以 `session/prompt` 的响应作为轮次结束。** 已完成的工具结果、部分输出、`usage_update` 都不能证明轮次已结束（模型可能还要再发请求）[zeron:acp.md「Pending prompts and silence」]。

---

## 7. `session/update` 全部变体

通知参数（[Schema] `SessionNotification`）：`{sessionId, update: SessionUpdate, _meta?}`。`update.sessionUpdate` 是判别字段。v1 稳定 schema 定义了以下 14 种（[Schema] `SessionUpdate`），这就是完整集合 [Doc:prompt-turn]：

| `sessionUpdate` | 载荷类型 | 稳定版本 | TKCode 处理 |
|---|---|---|---|
| `user_message_chunk` | `ContentChunk` | 1.0 前 | 实时轮次中忽略；重放时丢弃 |
| `agent_message_chunk` | `ContentChunk` | 1.0 前 | `textDelta` |
| `agent_thought_chunk` | `ContentChunk` | 1.0 前 | `thinkingDelta` |
| `tool_call` | `ToolCall` | 1.0 前 | `toolCallStarted` |
| `tool_call_update` | `ToolCallUpdate` | 1.0 前 | 合并补丁，驱动工具生命周期事件 |
| `plan` | `Plan` | 1.0 前 | 计划 / 待办面板 |
| `available_commands_update` | `AvailableCommandsUpdate` | 1.0 前 | 斜杠命令列表 |
| `current_mode_update` | `CurrentModeUpdate` | 1.0 前 | 旧模式接口 |
| `config_option_update` | `ConfigOptionUpdate` | 1.0 前 | 替换配置项 |
| `session_info_update` | `SessionInfoUpdate` | 0.11.1 | 会话标题 |
| `usage_update` | `UsageUpdate` | 0.13.6 | `usage`（上下文用量） |
| `notice` | `Notice` | 1.11.0 | toast，不进转录 |
| `compaction_update` | `CompactionUpdate` | 1.11.0 | 未声明能力，不应出现；出现则忽略 |
| `compaction_summary_chunk` | `CompactionSummaryChunk` | 1.11.0 | 同上 |

未知的 `sessionUpdate`（包括 unstable 的 `plan_update`、`plan_removed` 等）：**忽略**，写调试日志。`sessionId` 与本会话不符的通知：忽略（§5.1 的暂存情形除外）。

会话更新**不局限于轮次进行中**：Agent 可以在会话创建后、轮次之间随时推送命令列表、配置变化、标题、通知等 [Doc:prompt-turn]。轮次之外到达的消息类更新（例如 Agent 自主继续工作的输出），TKCode 应创建一个没有对应 prompt 的「Agent 自发」轮次来展示 [待核实：哪些 Agent 会这样做]。

### 7.1 `ContentChunk`：`user_message_chunk` / `agent_message_chunk` / `agent_thought_chunk`

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `content` | `ContentBlock` | 是 | 一个内容块（通常是 `text`） |
| `messageId` | string \| null | 否 | 不透明。相同 `messageId` 的块属于同一条消息；`messageId` 变化表示新消息 [Doc:prompt-turn「Message IDs」] |
| `_meta` | object | 否 | |

处理规则：
- `agent_message_chunk`：
  - `text` → `textDelta`（追加）；
  - `image` → 作为图片附件显示；
  - `resource_link` / `resource` → 作为链接或附件卡片显示；
  - `audio` → 忽略。
- `agent_thought_chunk`：只取 `text` → `thinkingDelta`，其它类型忽略。ACP 的思考内容只用于显示，**没有签名，也不需要回放**（ACP 下 Agent 自己管理历史）。
- `user_message_chunk`：
  - 实时轮次中，这通常是 Agent 回显的 TKCode 自己发出的 prompt → 忽略（zeron 同样忽略 [zeron:normalize.rs]）；
  - `session/load` 重放中 → 丢弃（§5.2）。
- 文本块直接拼接，**不要**在块之间插入空格或换行。

### 7.2 `tool_call`（[Schema] `ToolCall`）

| 字段 | 类型 | 必填 | 默认 | 说明 |
|---|---|---|---|---|
| `toolCallId` | string | 是 | — | 会话内唯一 |
| `title` | string | 是 | — | 给人看的描述，例如 `"Edit src/config.json"`。Cursor 等 Agent 在参数到达前会先给通用标题（如 `Read File`），或把命令用 Markdown 反引号包起来 [zeron:normalize.rs `is_placeholder_title`]，**只用于显示，不要当成参数解析** |
| `name` | string \| null | 否 | — | 1.8.0 起：工具的程序名，例如 `read_file`。不透明信息，不代表授权；首次报告后通常不再变化 |
| `kind` | `ToolKind` | 否 | `other` | 见 §7.4 |
| `status` | `ToolCallStatus` | 否 | `pending` | 见 §7.6 |
| `content` | `ToolCallContent[]` | 否 | `[]` | 见 §7.5；解析失败的条目跳过 |
| `locations` | `ToolCallLocation[]` | 否 | `[]` | `{path(绝对), line?(uint32，从 1 开始), _meta?}`，用于「跟随 Agent」 |
| `rawInput` | 任意 JSON | 否 | — | 工具的原始输入。省略和 `null` 都表示没有值 |
| `rawOutput` | 任意 JSON | 否 | — | 工具的原始输出 |
| `_meta` | object | 否 | — | 厂商信息，例如 Grok 的 `x.ai/tool` [zeron] |

同一个 `toolCallId` 再次出现 `tool_call` 时，按**更新**（upsert）处理，不要新建。有些 Agent 只发一个已经是终态（`completed` / `failed`）的 `tool_call`，结果直接内联，不再发后续更新 [zeron:normalize.rs]。

### 7.3 `tool_call_update`（[Schema] `ToolCallUpdate`）

除 `toolCallId` 外全部可选；只携带变化的字段 [Doc:tool-calls]。**合并规则**（逐字段）：

| 字段 | 省略 | `null` | 有值 |
|---|---|---|---|
| `title`、`kind`、`status` | 不变 | 不变 | 替换 |
| `name` | 不变 | 不变（v1 无法清除 name） | 替换 |
| `content` | 不变 | 不变 | **替换整个集合**（不是追加！[Schema] 原文 "Replace the content collection"） |
| `locations` | 不变 | 不变 | **替换整个集合** |
| `rawInput`、`rawOutput` | 不变 | 不变 | 替换 |
| `_meta` | 不变 | 不变 | 替换 |

注意：夹具 B 的 `completed` 更新省略了 `content`，因此首个 `tool_call` 里的 diff 必须保留。如果某个更新只带一段文本 `content`，前面的 diff 就被替换掉了；TKCode 在把工具结果写入转录时，应当另外保留曾经出现过的最后一个 diff 作为「变更记录」，避免界面上的 diff 被冲掉 [zeron 采用了类似做法：只有出现新的形状信息时才刷新调用]。

容错：
- `tool_call_update` 引用了**从未出现过的 `toolCallId`**：当作新的 tool call 创建，`title` 依次回退为 `name`、`toolCallId`。Cursor 的提问会使用从未声明过的 `toolCallId`（如 `<id>_q0`）[本机]。
- 只有 `toolCallId` 的更新：合法，没有变化。

### 7.4 `ToolKind`

| 值 | 含义 | TKCode 用途 |
|---|---|---|
| `read` | 读文件或数据 | 图标；只读 |
| `edit` | 修改文件 | 图标；有 diff 时展示变更 |
| `delete` | 删除 | |
| `move` | 移动 / 重命名 | |
| `search` | 搜索 | 只读 |
| `execute` | 执行命令或代码 | 终端图标；`rawInput.command` 常见，但不保证 |
| `think` | 内部推理 / 规划 | 只读 |
| `fetch` | 获取外部数据 | |
| `switch_mode` | 切换会话模式 | 常伴随权限请求（退出计划模式）[Doc:session-modes] |
| `other` | 其它（默认） | |

未知值按 `other` 处理。`kind` 只是 UI 提示，不能用来做安全判断（审批由 Agent 通过权限请求发起）。

### 7.5 `ToolCallContent`（判别字段 `type`）

| `type` | 字段 | 说明 |
|---|---|---|
| `content` | `content: ContentBlock`（必填） | 普通内容：文本、图片、资源 |
| `diff` | `path`（必填，绝对路径）、`oldText`（string \| null；**null 或省略表示新文件**）、`newText`（必填） | 文件修改。是整文件（或片段）的前后文本，不是 unified diff；TKCode 用 DiffPlex 自己计算差异 |
| `terminal` | `terminalId`（必填） | 引用一个通过 `terminal/create` 创建的终端。TKCode 没有声明 `terminal` 能力，**不应出现**；出现时显示「终端输出不可用」占位，不报错 |

未知 `type` 的条目跳过。所有变体都可以带 `_meta`。

### 7.6 `ToolCallStatus` 与生命周期

| 值 | 含义 |
|---|---|
| `pending` | 尚未开始：参数还在流式生成，或在等待批准 |
| `in_progress` | 执行中 |
| `completed` | 成功完成 |
| `failed` | 出错。用户拒绝权限后，Agent 通常也把工具标为 `failed`（夹具 C） |

v1 **没有** `cancelled` 状态（v2 草案新增了 [CL 1.6.0 unstable-v2]）。收到未知状态值时，保留原值用于显示，生命周期按「不变」处理；值为 `cancelled` 时按取消处理。

### 7.7 `plan`（[Schema] `Plan`）

`{entries: PlanEntry[]}`，每个条目 `{content, priority: "high"|"medium"|"low", status: "pending"|"in_progress"|"completed", _meta?}`。**每次都是完整列表，Client 必须整体替换** [Doc:agent-plan]。计划没有 ID，TKCode 用一个固定的合成 ID（例如 `acp-plan`）刷新同一个计划卡片 [zeron:normalize.rs]。未知的 priority / status 值按 `medium` / `pending` 显示。

### 7.8 `available_commands_update`

`{availableCommands: [{name, description, input?: {hint}, _meta?}]}`。整体替换命令列表，用于输入框的 `/` 补全。`name` 不带斜杠。

### 7.9 `current_mode_update`

`{currentModeId}`。更新旧模式接口的当前模式（§10.5）。

### 7.10 `config_option_update`

`{configOptions: SessionConfigOption[]}`，**完整的配置状态**，整体替换（§10）。常见原因：计划阶段结束后切换模式、因限流回退到其它模型 [Doc:session-config-options]。

### 7.11 `session_info_update`

`{title?: string|null, updatedAt?: string|null, _meta?}`。补丁语义：省略表示不变，`null` 表示清除，有值表示替换 [Doc:session-list]。TKCode 用 `title` 作为会话标题的候选（仅当用户没有手动命名时）。

### 7.12 `usage_update`

`{used: uint64, size: uint64, cost?: {amount: double, currency: string(ISO 4217)} | null, _meta?}`。

- `used` / `size`：**当前会话上下文**的 token 占用和窗口大小，不是本轮的输入输出 token。
- `cost`：会话**累计**费用。
- TKCode 映射为 `usage` 事件中的「上下文用量」和「累计费用」字段，不要把它累加到逐轮用量里（zeron 明确不把它当作逐轮 token [zeron:acp.md]）。

### 7.13 `notice`

`{severity, title, description?, _meta?}`。
- `severity`：开放枚举，已知 `info` / `warning` / `error`；`_` 开头的是扩展值，其它未知值保留，按通用样式显示。
- `title`：非空纯文本。
- 只有声明了 `session.notices` 后才会收到。
- 处理方式：显示为 toast，不写入转录，不影响轮次状态；即使 `error` 也**不代表请求失败**。
- 通知没有 ID，也没有生命周期 [Doc:prompt-turn「Session Notices」]。

### 7.14 `compaction_update` / `compaction_summary_chunk`

TKCode 不声明 `session.compaction`，Agent 不得发送 [Doc:prompt-turn「Session Compaction」]。出现时忽略并记录日志。后续实现时的要点：
- `compactionId` 是实体 ID；
- `status` 取值为 `in_progress` / `completed` / `failed` / `cancelled`；
- `summary` 为 `ContentBlock[]`，`null` 或 `[]` 表示清除；
- `compaction_summary_chunk` 追加摘要内容。

---

## 8. 权限请求：`session/request_permission`

### 8.1 结构 [Schema；Doc:tool-calls]

请求参数：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `sessionId` | string | 是 | |
| `toolCall` | `ToolCallUpdate` | 是 | 要批准的工具调用。**按 §7.3 的规则把它当作补丁合并**；可能只有 `toolCallId`（夹具 D），也可能带 `title` / `rawInput` / `content`（夹具 C） |
| `options` | `PermissionOption[]` | 是 | `{optionId, name, kind, _meta?}` |

`PermissionOptionKind`：

| `kind` | 含义 |
|---|---|
| `allow_once` | 只允许这一次 |
| `allow_always` | 允许并记住（记住的范围由 Agent 决定：可能是本会话，也可能永久写入 Agent 配置） |
| `reject_once` | 只拒绝这一次 |
| `reject_always` | 拒绝并记住 |

响应结果 `outcome`（判别字段 `outcome`）：

```json
{"outcome":{"outcome":"selected","optionId":"allow-once"}}
{"outcome":{"outcome":"cancelled"}}
```

- `selected`：必须带 Agent 提供的某个 `optionId`，**原样回传，绝不能自己编造**。
- `cancelled`：表示轮次被取消。

### 8.2 映射到 TKCode 审批

TKCode 审批对话框有三个按钮：**允许一次 / 始终允许 / 拒绝**（设计文档 §6 称为「允许一次 / 本会话允许 / 拒绝」）。只有当 `options` 是**规范形状**时才使用三按钮模式。规范形状指：每种 kind 最多出现一次，并且所有 kind 都是已知的 4 种之一。

| TKCode 按钮 | 选择的选项 | 选项不存在时 |
|---|---|---|
| 允许一次 | `kind == allow_once` | 隐藏按钮 |
| 始终允许 | `kind == allow_always` | 隐藏按钮。按钮文案用该选项的 `name`，旁边注明「由 Agent 记住」 |
| 拒绝 | `kind == reject_once`；没有则用 `reject_always` | 都没有时，按钮仍然显示，点击后回复 `cancelled`（不会被当作批准） |

非规范形状时，使用「选项列表」模式：按原顺序把每个选项的 `name` 渲染成按钮，返回用户选择的那个 `optionId`。以下情况都属于非规范形状：
- 同一 kind 出现多次：例如 Codex 适配器在每次执行审批时发**两个** `allow_always`（「本会话允许」和「允许以 xxx 开头的命令」）[zeron:acp.md]；Cursor 的「提问」是若干个 `allow_once` 加一个 `reject_once` 的 Skip（optionId 为 `__ask_question_skip__`），`toolCall.kind` 为 `other`，问题文本放在 `toolCall.content` 中 [本机]。
- 出现未知 kind：zeron 见过 `kind:"other"` 的选项 [zeron:mod.rs 测试]。

无论哪种模式，对话框都显示：
- `toolCall.title`；
- `toolCall.content` 里的文本或 diff；
- `rawInput` 的摘要，例如 `execute` 显示命令、`edit` 显示 diff。

### 8.3 自动放行与安全规则

- **默认弹出审批**。会话级「自动放行」开启时，自动选择 `allow_once`；没有时选 `allow_always`。**优先 `allow_once`**，避免在 Agent 侧留下持久的放行规则。zeron 优先 `allow_always`，是因为它全程无人值守，这一点 TKCode 不照搬。
- 非规范形状的请求（问题类）**永不自动回答**，始终弹出。
- UI 没有响应、连接断开、超时（建议 10 分钟）、对话框被关闭时，**一律拒绝**：
  - 有 `reject_once` 时选它；
  - 否则有 `reject_always` 时**不选**它（避免持久化），改为回复 `cancelled`；
  - 否则回复 `cancelled`。
- 请求的 `sessionId` 不是本连接上的活跃会话时，立即回复 `cancelled` [zeron:mod.rs `handle_server_request_live`]。
- 每个请求**只回复一次**。多个权限请求可能同时挂起，各自独立处理。
- 回复之后，工具调用的后续状态以 Agent 的 `tool_call_update` 为准。TKCode 不要因为用户点了「允许」就自行把状态改为 `in_progress`。

---

## 9. 取消

### 9.1 `session/cancel`（轮次取消）[Doc:prompt-turn「Cancellation」]

```json
{"jsonrpc":"2.0","method":"session/cancel","params":{"sessionId":"sess_7f3a9c"}}
```

这是一个**通知**，没有响应。TKCode 的中断流程（`InterruptAsync`）：
1. 发送 `session/cancel`。
2. 本地立即把本轮所有未结束的工具调用标为「已取消」（规范：SHOULD），发出相应的结束事件（§15.3）。
3. 对本会话**所有挂起的** `session/request_permission` 回复 `{"outcome":{"outcome":"cancelled"}}`（规范：MUST），并关闭对应的审批对话框。不要等待 `$/cancel_request`。
4. 继续正常处理之后到达的 `session/update`。规范允许 Agent 在响应之前继续发送内容和工具更新，Client 也应该接受 [Doc:prompt-turn]。对于本地已标为取消的工具，后续更新只更新显示内容，**不再改变其终态**。
5. 等待 `session/prompt` 的响应，预期为 `stopReason: "cancelled"`，映射为 `Aborted`。
6. 宽限期：10 秒内没有响应时，结束进程（SIGTERM → SIGKILL），轮次以 `Aborted` 结束；下一次 prompt 前重启进程，并按 §5.3 恢复会话。

zeron 的做法相同：先 `session/cancel`，再等待完成，必要时升级为结束进程 [zeron:mod.rs 文件头注释]。

### 9.2 `$/cancel_request`（通用请求取消，1.2.0 稳定）[Doc:cancellation]

```json
{"jsonrpc":"2.0","method":"$/cancel_request","params":{"requestId":5}}
```

- 由**发出请求的一方**发送，`requestId` 是被取消请求的 `id`。
- 接收方可以忽略（`$/` 开头的通知都允许忽略）；如果处理，**必须**仍然对原请求给出一个响应：一个有效结果，或错误 `-32800`。
- **注意拼写**：ACP 用的是 `$/cancel_request`，参数为 `requestId`。TKCode 内部 UI↔Host 的 RPC 用的是 `$/cancelRequest`，两者不能混用。

TKCode 作为**发送方**：
- 取消 `session/prompt` 一律用 `session/cancel`，不用 `$/cancel_request`。
- 放弃其它挂起请求时（例如用户在 `session/load` 重放期间关闭会话、`session/set_config_option` 等待太久），可以发 `$/cancel_request`，然后仍然等待响应或直接结束进程。

TKCode 作为**接收方**（Agent 取消自己发给 TKCode 的请求）：
- 对应 `session/request_permission`：关闭对话框，回复 `{"outcome":{"outcome":"cancelled"}}`。
- 对应其它请求：回复错误 `{"code":-32800,"message":"Request cancelled"}`。
- `requestId` 未知或已回复：忽略。

---

## 10. 会话配置：config options、modes、models

### 10.1 `SessionConfigOption`（[Schema]；Doc:session-config-options）

公共字段：`id`（必填）、`name`（必填）、`description?`、`category?`、`_meta?`。按 `type` 判别：

| `type` | 字段 | 说明 |
|---|---|---|
| `select` | `currentValue: string`（值的 id）、`options` | `options` 是平铺数组 `[{value, name, description?, _meta?}]`，**或**分组数组 `[{group, name, options:[...], _meta?}]`。同一个数组里两种形式不会混用 |
| `boolean` | `currentValue: bool` | 只有在 Client 声明 `session.configOptions.boolean` 后才会出现 |
| 其它未知值 | — | **忽略该选项**，Agent 会继续使用默认值 |

解析分组还是平铺：看数组第一个元素有没有 `group` 字段。数组为空时视为平铺。

`category`（开放枚举，只用于 UX，**不得用于判断正确性**）：

| 值 | 含义 | TKCode 控件 |
|---|---|---|
| `mode` | 会话模式（审批级别、计划模式等） | 输入框旁的模式选择器 |
| `model` | 模型 | 模型选择器 |
| `model_config` | 与模型相关的参数（上下文长度、快速模式等） | 放在模型选择器的同一个弹出面板里 |
| `thought_level` | 思考 / 推理深度 | TKCode 的 `ThinkingLevel` 选择器 |
| `_` 开头 | 厂商自定义 | 通用「Agent 设置」面板 |
| 省略或其它未知值 | — | 通用「Agent 设置」面板 |

数组顺序就是 Agent 期望的优先级。同一分类有多个选项时，第一个占据主要位置 [Doc:session-config-options]。

### 10.2 `session/set_config_option`

请求（[Schema] `SetSessionConfigOptionRequest`）：

```json
{"sessionId":"...","configId":"model","value":"flash-2"}
{"sessionId":"...","configId":"fast","type":"boolean","value":true}
```

- `select`：`value` 是某个选项的 `value`。**不发** `type` 字段。
- `boolean`：必须带 `"type":"boolean"`，`value` 为 bool。
- 响应：`{configOptions: SessionConfigOption[]}`，是**完整状态**，可能带有连带变化（例如换模型后思考深度的可选值变了，夹具 E）。TKCode 整体替换本地状态。
- 可以在轮次进行中调用 [Doc:session-config-options]。
- 出错时：恢复 UI 上的原值，显示错误信息。

### 10.3 TKCode 的配置应用策略

1. `session/new` / `load` / `resume` 的响应里有 `configOptions` 时，作为权威状态保存，并通过 SessionEvent 推送给 UI。
2. 用户在 TKCode 中为该运行时选择的模型和思考深度：
   - 如果对应分类的选项存在、目标值在可选值内、且不等于 `currentValue`，就调用 `set_config_option`；
   - 值已经是当前值时跳过；
   - 值不存在时保留 Agent 的默认值，并提示用户。
   - 思考深度、模式等辅助选项设置失败只记录日志、不阻断；**用户显式指定的模型**设置失败时，在发送 prompt 之前报错。zeron 的做法也是如此：用错误的模型继续跑，比报错更糟 [zeron:mod.rs]。
3. `ThinkingLevel` → `thought_level`：按值的 id，其次按 `name`（不区分大小写）匹配 `off/none/minimal/low/medium/high/xhigh/max`，找不到就按顺序就近选择（与 `ThinkingLevels.ChooseEffort` 一致）。有的 Agent 不提供 `thought_level`，而是把深度编码在模型 id 里（例如 Devin 的 `claude-5-fable-high`）[zeron:acp.md]，这种情况下 TKCode 不显示思考深度选择器。
4. 模型列表发现：启动一个短时进程，执行 `initialize` → `session/new`，读取 `category:"model"` 的选项（zeron 的探测方式 [zeron:acp.md]），然后有 `close` 能力就关闭会话，再结束进程。结果按「可执行文件路径 + 版本」缓存。**副作用**：会在 Agent 的会话历史里留下一个空会话。
5. **优先使用 config options**：Agent 同时提供 `configOptions` 和 `modes` 时，只用 `configOptions` [Doc:session-config-options]。同时提供 `configOptions` 中的 model 选项和旧的 `models` 时，只用前者。原因：codex-acp 在旧接口里把「模型 × 深度」展开成很多行，而 `configOptions` 里是基础模型 id 加上单独的 `thought_level` [zeron:acp.md]。

### 10.4 Agent 主动修改

`config_option_update`（§7.10）推送完整状态，TKCode 整体替换。用户正在编辑的控件以新状态为准。

### 10.5 旧接口：session modes（仍是 v1 稳定接口，v2 中删除）

- 会话响应中的 `modes: {currentModeId, availableModes: [{id, name, description?}]}`。
- 切换：`session/set_mode {sessionId, modeId}` → `{}`。
- Agent 主动切换：`current_mode_update {currentModeId}`。
- **只在没有 `category:"mode"` 配置项时使用**。Cursor 同时发送 `modes` 和 `configOptions` [本机]。

### 10.6 旧接口：session models（unstable，已从 schema 删除）

2026-06-01 从协议中删除：「The never-stabilized `session/set_model` API and related session model response fields have been removed」[RFD:updates]。但截至本文，仍有 Agent 在发送：Cursor 2026.08.11 的会话响应同时带 `models` 和 `configOptions`，其 `unstable_setSessionModel` 内部转调 `setSessionConfigOption` [本机]；Grok Build 只提供 `models`，必须用 `session/set_model` 切换 [zeron:mod.rs]。

形状（取自 zeron 的测试与调用代码，**schema 中已经没有，宽松解析**）：
- 响应字段：`"models": {"currentModelId": "grok-4.6", "availableModels": [{"modelId": "grok-4.6", "name": "Grok 4.6", "description"?: "..."}]}`；
- 请求：`session/set_model {"sessionId": "...", "modelId": "grok-4.5"}`，响应为 `{}`。

TKCode 规则：**只在没有 `category:"model"` 配置项时**才使用这个接口。`session/set_model` 返回 `-32601` 时，标记该 Agent「不支持在线切换模型」。

---

## 11. Elicitation（结构化提问）

1.7.0 起稳定 [CL]。Agent 通过 `elicitation/create` 向用户索取结构化信息，分 `form` 和 `url` 两种模式 [Doc:elicitation]。

**已实现第二阶段（2026-10-18，docs/design/ask-user.md §8）**：声明 `{"elicitation":{"form":{}}}`，form 请求交给 MCP 表单卡片。规范要求 Agent 不得请求 Client 没有声明的模式 [Doc:elicitation]。如果仍然收到：
- 其他模式（`url`）的 `elicitation/create`：回复 `{"code":-32602,"message":"elicitation mode not supported"}`（规范：请求未声明的模式时，返回 -32602 Invalid params）；
- `elicitation/complete` 通知：忽略。

补充：能力对象里某个模式的键存在且不为 null 才算声明了该模式，所以 `"elicitation":{}` 表示**不支持任何模式**（与 MCP 不同）。form 属性的类型：`string`（`title`、`minLength`、`maxLength`、`pattern`、`format`、`default`、`enum` 或 `oneOf:[{const,title}]`）、`number` / `integer`（`minimum`、`maximum`、`default`）、`boolean`（`default`）、`array` 多选（`items` 为 `{type:"string",enum}` 或 `{anyOf:[{const,title}]}`，加 `minItems`、`maxItems`、`default`）。`content` 的值可以是 string、int、number、bool 或 string[]，`content` 本身可省略或为 null。

第二阶段的约定：
- 只声明 `{"elicitation":{"form":{}}}`。
- **不声明 `url`**：URL 模式要求做到以下几点，TKCode 暂不具备：
  - 显示完整 URL 并取得同意；
  - 在 Agent 和模型都无法观察的安全上下文中打开；
  - 对 Punycode 域名告警。
- form 请求：
  - 作用域：`{sessionId, toolCallId?}`（会话级）或 `{requestId}`（请求级）；
  - 字段：`mode:"form"`、`message`、`requestedSchema`（扁平对象 schema，属性类型为 string / number / integer / boolean / 枚举单选 / 多选）。
- 响应：
  - `{"action":"accept","content":{...}}`：提交前在客户端按 schema 校验，有默认值时预填；
  - `{"action":"decline"}`：用户明确拒绝；
  - `{"action":"cancel"}`：用户关闭了对话框。
- 超时或断开时回复 `cancel`。

---

## 12. 其它 Agent → Client 请求

| 收到 | 回复 |
|---|---|
| `fs/read_text_file`、`fs/write_text_file` | `{"code":-32601,"message":"Method not found"}`（没有声明，Agent 本不该调用 [Doc:file-system]） |
| `terminal/*` | 同上 |
| 以 `_` 开头的扩展请求 | `-32601` [Doc:extensibility] |
| 厂商的非下划线扩展请求，例如 Cursor 的 `cursor/ask_question`、`cursor/create_plan`、`cursor/update_todos`、`cursor/task`、`cursor/generate_image` [本机；线上是否带 `_` 前缀待核实] | `-32601`。Cursor 的 `cursor/ask_question` 收到 -32601 后会退回到 §8.2 所述的「权限请求形式的提问」[本机] |
| 其它未知请求 | `-32601` |
| 未知通知（包括以 `_` 开头的） | 忽略 [Doc:extensibility] |

**必须尽快回复**：不回复会让 Agent 永远等待。

---

## 13. 错误处理

### 13.1 错误对象与错误码

`{code: int32, message: string, data?: any}`（[Schema] `Error`；注意 `Error` **没有** `_meta`）。

| code | 名称 | TKCode 处理 |
|---|---|---|
| `-32700` | Parse error | 记录；说明 TKCode 发出的 JSON 有问题，属于 bug |
| `-32600` | Invalid request | 显示错误。常见原因：在一个轮次进行中又发了 prompt |
| `-32601` | Method not found | 可选方法不可用：标记该能力不可用，降级处理（例如 `set_model` 不可用） |
| `-32602` | Invalid params | 显示错误；对 `set_config_option`，恢复原值 |
| `-32603` | Internal error | 显示错误（带 `data`） |
| `-32800` | Request cancelled | 对 prompt 按 Aborted 处理；其它请求视为已取消 |
| `-32000` | Authentication required | §4.3 |
| `-32002` | Resource not found | 例如 `session/load` 的会话不存在 → 回退到 `session/new` |
| 其它 | — | 显示 `message`，例如 Devin 的 `-32015`（会话被另一个进程占用）→ 回退到 `session/new` |

### 13.2 通用规则

1. 错误信息展示格式：`{agentInfo.title ?? name}: {message}（code {code}）`，有非 null 的 `data` 时附上它的 JSON（截断到 2 KB）。
2. 通知不会有响应，也不能回复错误 [Doc:overview]。
3. 处理 Agent 请求时 TKCode 内部出现异常：回复 `-32603`，不让异常穿透读循环。
4. 进程退出或管道断开：按 §2.5 处理，错误信息附 stderr 尾部。
5. TKCode 自己的约定：**流永不抛异常**（architecture §3.2），所有失败都编码为 `agentEnded` 的 Error 或 Aborted。

---

## 14. 宽松解析规则

ACP 的 Rust schema 给很多字段标注了宽松解析：
- `x-deserialize-default-on-error`：值格式错误时取默认值；
- `x-deserialize-skip-invalid-items`：数组中格式错误的条目跳过。

1.10.2 起，Rust SDK 对格式错误的列表条目「静默跳过」[CL；Schema]。TKCode 必须做到同等宽松：

1. **未知字段**：一律忽略，绝不报错。所有对象都可能出现新字段。
2. **`_meta`**：
   - 任何对象上都可能出现，可能为 `null`；
   - 原样保存在原始 `JsonElement` 中，但不解释（§7.2 提到的厂商键只用于日志和调试）；
   - TKCode 发出的 `_meta` 不要在根级添加自定义键。规范保留 `traceparent`、`tracestate`、`baggage` [Doc:extensibility]。
3. **未知判别值**：
   - `sessionUpdate` 未知 → 忽略整条更新；
   - `ContentBlock.type`、`ToolCallContent.type` 未知 → 跳过该块；
   - `SessionConfigOption.type` 未知 → 跳过该选项；
   - `AuthMethod.type` 未知 → 跳过该方法；
   - `McpServer` 只由 TKCode 发出，不涉及。
4. **未知枚举值**：
   - `ToolKind` → `other`；
   - `ToolCallStatus` → 保持之前的状态；
   - `PlanEntryPriority` / `PlanEntryStatus` → 用默认值显示；
   - `NoticeSeverity`、`SessionConfigOptionCategory` → 保留原字符串，按通用方式处理；
   - `PermissionOptionKind` → 进入「选项列表」模式，**永不视为批准**；
   - `StopReason` → Error。
   - v2 将统一采用「`_` 开头为扩展，其它为 ACP 保留」的开放枚举规则 [RFD-v2:enum-variant-extension]，以上做法与之兼容。
5. **判别字段位置不固定**：`sessionUpdate`、`type`、`outcome` 不一定是对象的第一个键（例如 Agent 可能先写 `toolCallId` 再写 `sessionUpdate`）。System.Text.Json 的多态反序列化默认要求判别字段在最前面。处理方式：
   - **方案一（推荐）**：先解析为 `JsonElement`，用 `TryGetProperty` 读取判别字段，再用源生成的 `JsonTypeInfo` 把同一个 `JsonElement` 反序列化为具体类型。这种方式对 AOT 安全，而且能对未知判别值跳过。
   - **方案二**：在 `JsonSerializerOptions` 上启用 `AllowOutOfOrderMetadataProperties = true`（.NET 9+），配合 `[JsonPolymorphic]`。但未知判别值仍会抛异常，需要额外的回退逻辑，因此不推荐。
6. **`null` 与省略**：
   - 补丁类字段（§7.3、§7.11）区分「省略」「`null`」「有值」三种情况。DTO 不能只用 `T?` 表达，必须保留「是否出现」：用 `JsonElement?`，或者 `Optional<T>` 包装加自定义转换器。
   - 能力对象：`null` 与省略都表示不支持，`{}` 表示支持。用 `JsonElement.ValueKind == Object` 判断。
7. **数值**：
   - `used` / `size` 是 `uint64`，用 `long` 接收（不会超过 `long.MaxValue`），溢出时截断；
   - `line` 是 `uint32`；
   - `protocolVersion` 是 `uint16`；
   - 请求 `id` 可能是整数或字符串。
8. **布尔能力字段格式错误**（例如 `"loadSession":"yes"`）→ 取默认值 `false`。
9. **字符串内容不做任何假设**：`sessionId`、`toolCallId`、`messageId`、`optionId` 都是不透明字符串，不要解析，也不要改变大小写。
10. **重复的 `tool_call`**：按 upsert 处理（§7.2）。
11. **非 JSON 行**：§2.3。

---

## 15. TKCode 事件映射

### 15.1 总表

| TKCode 事件 | ACP 来源 | 规则 |
|---|---|---|
| `agentStarted` | TKCode 写出 `session/prompt` 后 | 每轮一次 |
| `messageStarted` | 当前没有打开的消息时，收到第一个 `agent_message_chunk` / `agent_thought_chunk` / `tool_call` | 见 §15.2 |
| `thinkingDelta` | `agent_thought_chunk`（`text`） | 带 `contentIndex` |
| `textDelta` | `agent_message_chunk`（`text`） | 带 `contentIndex` |
| `toolCallStarted` | 首次见到某个 `toolCallId`（`tool_call`，或未知 id 的 `tool_call_update` / 权限请求） | 携带 `toolCallId`、`name ?? title`、`kind`、`title`、`locations` |
| `toolCallDelta` | `rawInput` 首次出现或发生变化 | ACP **没有参数流式增量**（设计文档 §5）。事件载荷是完整的 `rawInput` JSON，标记为「整体替换」，不是追加片段 |
| `toolCallEnded` | 状态第一次离开 `pending`（变为 `in_progress` / `completed` / `failed`），或本地取消，或轮次结束时仍为 `pending` | 每个工具一次 |
| `toolExecutionStarted` | `status: in_progress` | 如果直接从 `pending` 跳到终态，就在 `toolExecutionEnded` 之前**补发**一次，保证成对出现 |
| `toolExecutionEnded` | `status: completed` / `failed`，或本地取消 | 携带：最终的 `content`（文本、图片、diff）、`rawOutput`、`isError = (status == failed)`、`rejected`（该工具的权限请求被拒绝时为 true）、`cancelled` |
| `messageEnded` | 见 §15.2 | |
| `agentEnded` | `session/prompt` 响应（及在它之前的所有通知处理完毕后） | `StopReason` 按 §6.4；错误按 §6.5 |
| `usage` | `usage_update`（上下文 used / size、累计 cost）；`PromptResponse.usage` 或 `_meta`（逐轮 token，unstable） | 两类数据分字段存放，不要互相累加 |
| 计划 / 待办 | `plan` | 整体替换 |
| 会话状态（非 AgentEvent，走 SessionEvent） | `available_commands_update`、`config_option_update`、`current_mode_update`、`session_info_update`、`notice` | 推送到 UI 的会话状态 |
| 审批（`tool/approve`） | `session/request_permission` | §8，对应关系见 §15.4 |

### 15.2 消息与内容块边界（状态机）

ACP 没有消息开始和结束帧，TKCode 按以下规则生成，保证结果确定、可测试：

1. **打开消息**：当前轮次没有打开的消息时，第一个 `agent_message_chunk`、`agent_thought_chunk` 或 `tool_call` 到达，发出 `messageStarted`。
2. **内容块**：消息内部维护 `contentIndex`，从 0 开始。连续的同类更新（thought → thought，text → text）追加到当前块。类别切换（thought ↔ text，或遇到新的 tool call）时，`contentIndex` 加 1，开始新块。每个 tool call 单独占一个块。
3. **切换到新消息**（先发 `messageEnded`，再发 `messageStarted`）。满足以下任一条件即切换：
   - (a) 当前消息已经包含至少一个 tool call，此时又到达 `agent_message_chunk` 或 `agent_thought_chunk`。这表示工具执行完后，模型开始了新的一步。
   - (b) 到达的 `agent_message_chunk` 带非 null 的 `messageId`，与当前消息中最近一个非 null 的 Agent 消息 `messageId` 不同，并且当前消息已经有正文。
   - `agent_thought_chunk` 的 `messageId` 只记录，不用于判断边界，因为各 Agent 对思考的 `messageId` 用法不一致 [待核实]。
4. **关闭消息**：`session/prompt` 响应到达时，如果有打开的消息，先 `messageEnded`，再 `agentEnded`。
5. 工具执行事件（`toolExecution*`）不属于消息内容，不影响消息边界。

### 15.3 工具生命周期（每个 `toolCallId` 的状态机）

```
(未见) --tool_call/未知id的update/权限请求--> Pending   [toolCallStarted；有 rawInput 则 toolCallDelta]
Pending --rawInput 变化--> Pending                      [toolCallDelta（整体替换）]
Pending --in_progress--> Running                        [toolCallEnded, toolExecutionStarted]
Pending --completed/failed--> Done                      [toolCallEnded, toolExecutionStarted(补发), toolExecutionEnded]
Running --completed/failed--> Done                      [toolExecutionEnded]
Pending/Running --本地取消(§9.1)--> Done(cancelled)       [必要时补 toolCallEnded / toolExecutionStarted, 再发 toolExecutionEnded(cancelled)]
Pending/Running --轮次结束仍未终结--> Done(aborted)        [同上，结果文本 "Operation aborted"]
Done --任何后续更新--> Done                              [只更新 content / rawOutput 的显示，不再发生命周期事件]
```

### 15.4 TKCode 审批与 ACP 权限的对应

| TKCode 审批结果 | ACP 回复 |
|---|---|
| 允许一次 | `{"outcome":{"outcome":"selected","optionId":<allow_once 的 optionId>}}` |
| 始终允许（本会话允许） | `selected` + `allow_always` 的 `optionId` |
| 拒绝 | `selected` + `reject_once` 的 `optionId`；没有就用 `reject_always`（仅限用户明确点击时）；都没有就回复 `cancelled` |
| 选项列表模式中选择了某项 | `selected` + 该项的 `optionId` |
| 用户中断轮次 | `{"outcome":{"outcome":"cancelled"}}` |
| 超时、断开、对话框被关闭 | 有 `reject_once` 就选它，否则回复 `cancelled`（§8.3） |
| 会话级自动放行 | `allow_once`，没有就 `allow_always`；问题类请求不自动回答 |

---

## 16. v2 草案的差异与预留

v2 处于 **Active 草案**阶段（[RFD-v2:overview]；`schema/v2/` 的 `meta.json` 中 version 为 2）。TKCode 现在始终发送 `protocolVersion: 1`，v2 Agent 按版本协商会回落到 v1，或者返回 2（这时 TKCode 断开连接，见 §3.4）。为了将来能平滑加入 v2，现在就应遵守下表中的「现在的准备」。

| v2 变化 | 对 TKCode 的影响 | 现在的准备 |
|---|---|---|
| `initialize` 中的 `clientCapabilities` / `agentCapabilities` 合并为 `capabilities`；`clientInfo` / `agentInfo` 合并为必填的 `info`；`sessionCapabilities` 改为 `session`，`promptCapabilities` / `mcpCapabilities` 改为 `session.prompt` / `session.mcp`；能力标记统一为对象（`{}` 表示支持） | 初始化需要按版本分别编写 | 能力解析集中在一个 `AcpCapabilities` 归一化对象里，不要在各处直接读 JSON |
| 认证改为 `auth/login`、`auth/logout`；只要 `authMethods` 非空，Agent 就必须实现这两个方法 | 方法名变化 | 认证调用经过版本适配层 |
| 删除 `session/load`；`session/resume` 增加可选的 `replayFrom` 游标，统一「重放」与「不重放」两种恢复；`session/list`、`resume`、`close` 成为基线 | 恢复逻辑变化 | 恢复逻辑写成策略对象（§5.3 的三级回退） |
| 删除 modes（`modes`、`set_mode`、`current_mode_update`），模型和模式只通过 config options 提供 | — | 已经优先使用 config options（§10.3） |
| 删除 Client 侧的 fs 和 terminal；新增 Agent 自有、只读展示的终端输出（终端更新携带 `command`、`cwd`、快照和退出码；输出块是 base64 字节） | 新增终端展示 | 已不声明 fs/terminal；工具结果模型预留「终端输出」类型 |
| `tool_call` 与 `tool_call_update` 合并为单一的 upsert；新增按条目追加的 tool-call content chunk；工具与计划条目新增 `cancelled` 状态 | 工具更新语义变化 | 内部已按 upsert 处理（§7.2）；状态枚举预留 `Cancelled` |
| `plan` 改为按条目的 `plan_update`（有 `planId`，以及 items / markdown / file 等变体），新增 `plan_removed` | 计划面板 | 计划模型按「ID + 条目」设计 |
| `Diff` 的 `oldText` / `newText` 改为 diff file states：可选的 `git_patch`（所有路径均为绝对路径，不带 `a/` `b/` 前缀），加上 add / delete / modify / move / copy 等结构化文件操作 | diff 渲染 | 渲染层接受「前后文本」或「unified patch」两种输入 |
| 权限请求增加必填的 `title`，以及可选的 `subject`（`tool_call` / `command` 等）；结果 `outcome` 将成为开放枚举 | 审批 UI | 审批 DTO 不要假设一定有 `toolCall`；未知 outcome 永不视为批准 |
| 新增整条消息更新（`user_message`、`agent_message`、`agent_thought`，按 `messageId` upsert，替换整条内容），消息块的 `messageId` 变为必填 | 消息边界可以明确得到 | §15.2 的边界判断封装在可替换的组件里 |
| **新的 prompt 生命周期**：`session/prompt` 在插入用户消息后**立即**返回 `{messageId}`，轮次结束改由 `state_update`（idle，带 `stopReason`，可以是 `error`，附带 JSON-RPC 错误对象）通知；`session/prompt` 返回 JSON-RPC 错误则一定表示消息没有插入 | **轮次结束不再由 prompt 响应表示** | 核心状态机只消费内部的 `TurnEnded` 事件，由 v1 / v2 适配层分别产生，不要把「prompt 响应 = 轮次结束」写死在 UI 或会话逻辑里 |
| 支持 JSON-RPC 批量请求和通知 | 读取端 | §2.3 第 4 条已经支持数组 |
| MCP 服务器配置必须带 `type`（包括 `"stdio"`）；删除 SSE；stdio 变为显式能力 `session.mcp.stdio` | `mcpServers` 构造 | 构造逻辑集中在一处 |
| 开放枚举：`_` 开头为扩展，未知的非下划线值为 ACP 将来的值；通知和展示类的标签联合保留原始载荷 | 解析器 | 已按 §14 做到 |

---

## 17. 可直接启动的 ACP Agent（Registry 摘录）

### 17.1 发现与自动启用规则

TKCode **只自动启用「可执行文件已安装、能直接 exec」的 Agent**，不运行 `npx` / `uvx`，也不下载 Registry 里的压缩包（设计文档 §2、§4）。

1. 按设计文档 §4 的顺序查找可执行文件：环境变量覆盖 → 进程 PATH → 登录 shell 的 PATH 快照 → 已知目录（`~/.local/bin`、`/opt/homebrew/bin`、`/usr/local/bin`、npm 全局前缀、`~/.opencode/bin` 等）。
2. 解析符号链接后再判断。
3. **排除 VS Code 的 Copilot 包装脚本**。本机 PATH 中的 `copilot` 实际是：
   - 路径：`~/Library/Application Support/Code/User/globalStorage/github.copilot-chat/copilotCli/copilot`；
   - 内容：一个 sh 脚本，以 `ELECTRON_RUN_AS_NODE=1` 运行 VS Code 的 Code Helper，执行 `copilotCLIShim.js` [本机]；
   - 行为：在 PATH 中查找真正的 `copilot`；找不到时，会**向 stdout 打印** `Install GitHub Copilot CLI? ['y/N']` 等文字，并通过 readline 读 stdin，用户同意后调用 npm、brew 或 curl 安装脚本。
   这会直接破坏 ACP 的 stdout，而且可能触发安装。规则：解析后的路径位于 `*/globalStorage/github.copilot-chat/copilotCli/` 下时跳过，继续查找真正的 `copilot`。
4. 版本探测：`<exe> --version`。stdin 接 `/dev/null`，超时 10 秒，只取第一行。不要用 `<exe> acp --help` 一类的命令探测：对于把未知参数当作 prompt 的 CLI，这会**启动一次真实的对话**。
5. 首次使用时才启动 ACP 进程；探测模型列表按 §10.3 第 4 条执行。

### 17.2 Registry 条目分类

Registry 快照共 42 个条目（A 类 19 个、B 类 16 个、C 类 7 个）。命令列取自 `distribution.binary.<平台>.cmd/args` 或 `distribution.npx/uvx.args`。「直接启动命令」一列是 TKCode 在 PATH 上找到对应可执行文件后实际 exec 的命令。

**A 类：Registry 以原生二进制分发，可以直接启动（自动启用候选）**

| Registry id | 名称 | 版本 | 可执行文件（PATH 上的名字） | 直接启动命令 | 备注 |
|---|---|---|---|---|---|
| `cursor` | Cursor | 2026.10.14 | `cursor-agent`（安装器还会建 `agent` 链接） | `cursor-agent acp` | 隐藏子命令，`--help` 中不列出；本机 2026.08.11 的安装包中确认存在 `command("acp",{hidden:!0})` [本机]。认证见 §4.3 |
| `opencode` | OpenCode | 1.19.1 | `opencode` | `opencode acp` | |
| `goose` | goose | 1.54.0 | `goose` | `goose acp` | |
| `kimi` | Kimi CLI | 1.52.0 | `kimi` | `kimi acp` | Registry 没有 darwin-x86_64 构建 |
| `devin` | Devin | 3000.11.92 | `devin` | `devin acp` | `session/load` 的怪癖见 §5.3 |
| `kilo` | Kilo | 7.8.12 | `kilo` | `kilo acp` | 同时提供 npx |
| `junie` | Junie | 3659.4.0 | `junie`（macOS 上为 `junie.app/Contents/MacOS/junie`） | `junie --acp=true` | |
| `mistral-vibe` | Mistral Vibe | 2.26.1 | `vibe-acp` | `vibe-acp` | 独立的 ACP 二进制，没有参数 |
| `cortex-code` | Cortex Code | 1.0.73 | `cortex` | `cortex acp serve` | |
| `stakpak` | Stakpak | 0.3.88 | `stakpak` | `stakpak acp` | |
| `vtcode` | VT Code | 0.96.14 | `vtcode` | `vtcode acp` | |
| `harn` | Harn | 0.10.167 | `harn` | `harn serve acp` | |
| `kimchi` | Kimchi | 1.15.1 | `kimchi` | `kimchi --mode acp` | |
| `crow-cli` | crow-cli | 0.1.24 | `crow-cli` | `crow-cli acp` | |
| `poolside` | Poolside | 1.0.16 | 压缩包内为 `pool-<os>-<arch>`；安装后的名字待核实（可能是 `pool`） | `pool acp` | |
| `amp-acp` | Amp | 0.10.0 | `amp-acp` | `amp-acp` | 是 Amp 的 ACP 桥接二进制，不是 `amp` 本体 |
| `corust-agent` | Corust Agent | 0.6.0 | `corust-agent-acp` | `corust-agent-acp` | |
| `sigit` | siGit Code | 1.7.0 | `sigit` | `sigit` | 没有参数；同时提供 npx |
| `antigravity-acp` | Google Antigravity | 1.4.0 | `agy_acp_server.par` | `agy_acp_server.par`（Linux 加 `--uid=`） | 一般不在 PATH 上，只在用户配置了路径时启用 |

**B 类：本身就是原生支持 ACP 的 CLI，但 Registry 只给出 npx / uvx 分发。** 用户已全局安装该 CLI（npm -g、brew 或官方安装脚本）、PATH 上有对应可执行文件时，可以直接启动，不经过 npx。可执行文件名称除注明外，均为 [待核实]。

| Registry id | 名称 | 包 | 直接启动命令 | 备注 |
|---|---|---|---|---|
| `gemini` | Gemini CLI | `@google/gemini-cli@0.64.0` | `gemini --acp` | 早期版本的参数是 `--experimental-acp` [待核实]。版本低于 Registry 时，先试 `--acp` |
| `github-copilot-cli` | Copilot | `@github/copilot@1.0.98` | `copilot --acp` | 安装方式：npm、`brew install copilot-cli`、`gh.io/copilot-install` 脚本（取自本机 VS Code 包装脚本中的安装逻辑）。**注意 §17.1 第 3 条** |
| `qwen-code` | Qwen Code | `@qwen-code/qwen-code@0.25.2` | `qwen --acp` | Registry 额外带了 `--experimental-skills` 参数，可以照加 |
| `grok-build` | Grok Build | `@xai-official/grok@1.0.60` | `grok agent stdio` | zeron 已验证 [zeron]；只支持旧的 `models` 接口（§10.6） |
| `auggie` | Auggie CLI | `@augmentcode/auggie@0.36.0` | `auggie --acp` | 环境变量 `AUGMENT_DISABLE_AUTO_UPDATE=1` |
| `cline` | Cline | `cline@3.0.71` | `cline --acp` | |
| `codebuddy-code` | Codebuddy Code | `@tencent-ai/codebuddy-code@2.168.0` | `codebuddy --acp` | |
| `factory-droid` | Factory Droid | `droid@0.242.1` | `droid exec --output-format acp-daemon` | 环境变量 `DROID_DISABLE_AUTO_UPDATE=true`、`FACTORY_DROID_AUTO_UPDATE_ENABLED=false` |
| `qoder` | Qoder CLI | `@qoder-ai/qodercli@0.2.14` | `qodercli --acp` | |
| `bob` | Bob | `@ibm/bob@2.0.8` | `bob acp` | |
| `minimax-code` | MiniMax Code | `@minimax-ai/code@0.2.7` | `<bin> acp` | 可执行文件名未知 |
| `nova` | Nova | `@compass-ai/nova@1.1.54` | `<bin> acp` | 同上 |
| `dimcode` | DimCode | `dimcode@0.5.18` | `dimcode acp` | |
| `dirac` | Dirac | `dirac-cli@0.5.21` | `dirac --acp` | |
| `minion-code` | Minion Code | uvx `minion-code@0.1.44` | `minion-code acp` | |
| `fast-agent` | fast-agent | uvx `fast-agent-acp==0.10.1` | `fast-agent-acp -x` | Registry 的环境变量 `FAST_AGENT_MODEL=codexplan` |

**C 类：适配器或只能通过 npx 运行的包，TKCode 不启用**

- `claude-acp`（`@agentclientprotocol/claude-agent-acp`）、`codex-acp`（`@agentclientprotocol/codex-acp`）：Claude 和 Codex 走原生协议（设计文档 §2）。
- `pi-acp`、`deepagents`（`deepagents-acp`）、`autohand`（`@autohandai/autohand-acp`）、`glm-acp-agent`、`agoragentic-acp`（`agoragentic-mcp --acp`）：只有 npx 形式的适配器，没有独立安装的 CLI。

### 17.3 本机现状（2026-10-16）

- `cursor-agent` → `~/.local/share/cursor-agent/versions/2026.08.11-e8db854/cursor-agent`（bash 脚本 + 自带 node）。可以自动启用：`cursor-agent acp`。
- `copilot` → 只有 VS Code 的包装脚本，PATH 上**没有**真正的 Copilot CLI → **不启用**。启用后包装脚本会在 stdout 上打印安装提示。
- 没有找到 `gemini`、`qwen`、`opencode`、`goose`、`kimi`。`~/.opencode/bin` 在 PATH 中，但目录不存在。
- 设计文档 §9 第 5 步计划「先接 Gemini CLI 和 Copilot CLI」，但本机两者都没有原生安装。开发和联调建议先用 Cursor，再用假 Agent 脚本加附录 A 的夹具覆盖其它场景。

---

## 18. 实现必须满足（检查清单）

**传输**
1. 使用 stdio、UTF-8、**按 `\n` 分帧**，没有 Content-Length。写入是紧凑 JSON 加 `\n`，通过单写者队列；读取时去掉行尾的 `\r`，跳过空行，单行不设硬性上限（保护上限 64 MiB）。
2. 无法解析为 JSON 的 stdout 行要记录并跳过，不断开连接。stderr 必须持续读取，保留尾部用于错误提示。
3. 入站消息（包括响应）在同一个读循环中**严格按序**处理。`agentEnded` 必须在 `session/prompt` 响应之前的所有 `session/update` 都处理完之后才发出。
4. 两个方向的请求 id 空间相互独立；Agent 请求的 `id` 原样回传（整数或字符串）。
5. 每个 Agent 发来的请求都必须且只能回复一次。不认识的方法回复 `-32601`，未声明的 elicitation 回复 `-32602`，未知通知忽略。

**初始化与认证**
6. `initialize` 发送 `protocolVersion:1`、`clientCapabilities = {fs:{readTextFile:false, writeTextFile:false}, terminal:false, auth:{terminal:false}, session:{configOptions:{boolean:{}}, notices:{}}}`，以及 `clientInfo{name:"tkcode", title, version}`。不声明 elicitation 和 compaction。
7. 返回的 `protocolVersion ≠ 1` 时断开连接并提示用户。能力对象中 `null` 与省略都表示不支持，`{}` 表示支持。
8. 不主动调用 `authenticate`。收到 `-32000` 后按 §4.3 处理：只有白名单方法（`cursor_login`）自动调用一次；绝不对 `terminal` 类型的方法调用 `authenticate`；不调用 `logout`。

**会话**
9. `session/new` 的 `cwd` 必须是绝对路径，`mcpServers` 必须发送（没有就是 `[]`）。stdio MCP 服务器必须带齐 `name`、`command`（绝对路径）、`args`、`env` 四个字段，并且**不带 `type`**。`additionalDirectories` 只在 Agent 声明能力后发送。
10. 会话响应之前到达的同 Agent 通知要按 `sessionId` 暂存，等响应到达后按原顺序回放。
11. 恢复顺序：`session/resume` → `session/load`（丢弃重放的消息和工具，保留状态更新）→ `session/new`（提示上下文丢失）。每一级都要先检查能力。
12. 同一会话同时只能有一个 `session/prompt`。steer / followUp 在轮次边界作为下一次 prompt 发出。

**Prompt 与更新**
13. prompt 内容块严格遵守 `promptCapabilities`：`text` 和 `resource_link` 是基线；`image` / `audio` / `resource` 需要对应能力。
14. 轮次只以 `session/prompt` 响应结束。`stopReason` 的映射：`end_turn`→Stop，`max_tokens`→Length，`max_turn_requests`→Stop+提示，`refusal`→Error，`cancelled`→Aborted，未知值→Error。不使用任何静默计时器。
15. 实现 §7 的全部 14 种 `session/update`。未知变体、未知内容类型、未知配置项类型都跳过，不报错。
16. 实时轮次中的 `user_message_chunk` 忽略。`agent_thought_chunk` 只显示，不回放。
17. `tool_call` 按 upsert 处理。`tool_call_update` 逐字段合并：`content` 和 `locations` **整体替换**；省略和 `null` 都表示不变。未知 `toolCallId` 的更新新建工具。
18. 工具事件严格成对：`toolCallStarted` → `toolCallEnded` → `toolExecutionStarted` → `toolExecutionEnded`，缺失的环节按 §15.3 补发。已结束的工具不再发生命周期事件。
19. `plan`、`available_commands_update`、`config_option_update` 每次都整体替换；`session_info_update` 区分省略、`null`、有值。
20. `usage_update` 是上下文用量和累计费用，不能当作逐轮 token 累加。逐轮 token 只取 `PromptResponse.usage` 或 `_meta`（unstable，可能不存在）。

**权限与取消**
21. 权限请求：规范形状（每种 kind 最多一个）时映射到「允许一次 / 始终允许 / 拒绝」三个按钮；否则按选项列表渲染。`optionId` 必须原样回传，不得编造。
22. 默认弹出审批。自动放行时优先选 `allow_once`。问题类请求永不自动回答。UI 超时、断开或关闭时一律拒绝（`reject_once`，没有就回复 `cancelled`），绝不默认放行。
23. 中断时：发 `session/cancel` → 本地把未结束的工具标为取消 → 对所有挂起的权限请求回复 `cancelled` → 继续接收更新 → 等待 `stopReason:"cancelled"`。10 秒内没有响应就结束进程。发出 cancel 后收到的错误响应按 Aborted 处理。
24. `$/cancel_request`（注意不是 `$/cancelRequest`）：收到时关闭对应的审批对话框并回复 `cancelled`，其它请求回复 `-32800`。不用它来取消 prompt。

**配置**
25. 优先使用 `configOptions`：`model` → 模型选择器，`thought_level` → ThinkingLevel，`mode` → 模式，`model_config` 放在模型面板，其余放进通用面板，未知 `type` 的选项忽略。
26. `set_config_option` 中，select 发 `{configId, value}`，boolean 发 `{configId, type:"boolean", value}`。响应是完整状态，整体替换；失败时恢复原值。
27. 只有在没有对应分类的配置项时，才回退到 `modes` + `session/set_mode`，或 schema 之外的 `models` + `session/set_model`（宽松解析，遇到 `-32601` 就降级）。

**解析**
28. 判别字段（`sessionUpdate`、`type`、`outcome`）可能不在对象的第一个位置：先解析为 `JsonElement` 再分派，保证 NativeAOT 安全。
29. 未知字段一律忽略；`_meta` 保存但不解释；数组中格式错误的条目跳过；布尔能力字段格式错误时取 `false`。

**发现与进程**
30. 只自动启用 PATH 上能直接 exec 的 A 类或 B 类可执行文件，不运行 `npx` / `uvx`，不下载 Registry 压缩包。必须排除 VS Code 的 Copilot 包装脚本。版本只用 `--version` 探测（stdin 接 `/dev/null`，超时 10 秒）。
31. 关闭流程：有能力时先 `session/close` → 关闭 stdin → SIGTERM（进程组）→ SIGKILL。Windows 用 Job Object。进程意外退出时，所有挂起请求失败，进行中的轮次以 Error（已取消的以 Aborted）结束，并附上 stderr 尾部。

**测试**
32. 附录 A 的夹具全部作为单元测试：`>` 行与 TKCode 实际写出的消息按 **JSON 语义相等**（不比较键顺序和空白）逐条比对；`<` 行喂给 TKCode；最后断言 TKCode 产生的事件序列与「期望事件」一致。

---

## 附录 A. 测试夹具

### A.0 格式

- 每行一条消息，行首两个字符表示方向：
  - `> `：TKCode（Client）→ Agent，即 TKCode **必须写出**的消息；
  - `< `：Agent → TKCode，即测试**注入**的消息。
- 去掉行首两个字符后，剩下的部分是一条完整的 ACP 消息，可以原样作为 stdio 的一行。
- 所有消息都已用 `schema/v1/schema.json` 逐条校验：请求按 `x-method` 找到对应的 `*Request` / `*Notification` 定义，响应按配对请求找到 `*Response` 定义，错误按 `Error` 定义；并检查没有 schema 未定义的字段名（`rawInput` / `rawOutput` 内部除外）。
- 夹具中的固定值：
  - TKCode 的请求 id 从 0 开始；Agent 的请求 id 也从 0 开始（独立空间）；
  - `clientInfo.version` 固定为 `"0.1.0"`，测试时注入同样的版本号；
  - 工作目录为 `/Users/dev/project`，会话 id 为 `sess_7f3a9c`。
- 「期望事件」中，`agentStarted` 等是 §15 中的 TKCode 事件；括号内是关键载荷。`[UI]` 表示 Host 发给 UI 的请求或状态（`tool/approve`、SessionEvent 状态），不属于 AgentEvent。

### A.1 夹具 A：initialize → session/new → prompt（思考 + 正文）→ end_turn

```text
> {"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":1,"clientCapabilities":{"fs":{"readTextFile":false,"writeTextFile":false},"terminal":false,"elicitation":{"form":{}},"auth":{"terminal":false},"session":{"configOptions":{"boolean":{}},"notices":{}}},"clientInfo":{"name":"tkcode","title":"TKCode","version":"0.1.0"}}}
< {"jsonrpc":"2.0","id":0,"result":{"protocolVersion":1,"agentCapabilities":{"loadSession":true,"promptCapabilities":{"image":true,"audio":false,"embeddedContext":true},"mcpCapabilities":{"http":true,"sse":false},"sessionCapabilities":{"list":{},"resume":{},"close":{}},"auth":{}},"authMethods":[{"id":"oauth-personal","name":"Log in with Google","description":"Sign in with your Google account"}],"agentInfo":{"name":"example-agent","title":"Example Agent","version":"1.4.2"}}}
> {"jsonrpc":"2.0","id":1,"method":"session/new","params":{"cwd":"/Users/dev/project","mcpServers":[]}}
< {"jsonrpc":"2.0","id":1,"result":{"sessionId":"sess_7f3a9c"}}
< {"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_7f3a9c","update":{"sessionUpdate":"available_commands_update","availableCommands":[{"name":"init","description":"Analyze the project and create an AGENTS.md file"},{"name":"review","description":"Review pending changes","input":{"hint":"optional focus area"}}]}}}
> {"jsonrpc":"2.0","id":2,"method":"session/prompt","params":{"sessionId":"sess_7f3a9c","prompt":[{"type":"text","text":"What does the main function in src/main.rs do?"},{"type":"resource_link","uri":"file:///Users/dev/project/src/main.rs","name":"main.rs","mimeType":"text/x-rust"}]}}
< {"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_7f3a9c","update":{"sessionUpdate":"agent_thought_chunk","content":{"type":"text","text":"The user is asking about the entry point. "}}}}
< {"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_7f3a9c","update":{"sessionUpdate":"agent_thought_chunk","content":{"type":"text","text":"The attached file shows main() wiring config and server."}}}}
< {"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_7f3a9c","update":{"sessionUpdate":"agent_message_chunk","messageId":"msg_01","content":{"type":"text","text":"`main` parses the CLI arguments, "}}}}
< {"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_7f3a9c","update":{"sessionUpdate":"agent_message_chunk","messageId":"msg_01","content":{"type":"text","text":"loads `config.toml`, and starts the HTTP server on port 8080."}}}}
< {"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_7f3a9c","update":{"sessionUpdate":"usage_update","used":18342,"size":200000,"cost":{"amount":0.0123,"currency":"USD"}}}}
< {"jsonrpc":"2.0","id":2,"result":{"stopReason":"end_turn"}}
```

期望事件：
1. 初始化完成：协议版本 1；能力 `loadSession=true`、`image=true`、`embeddedContext=true`、`resume`、`list`、`close`；一个 agent 类型的认证方法（不调用）。
2. `[UI]` 会话状态：命令列表 `init`、`review`（`review` 带输入提示）。
3. `agentStarted`（写出 id=2 的 prompt 之后）
4. `messageStarted`
5. `thinkingDelta(contentIndex=0, "The user is asking about the entry point. ")`
6. `thinkingDelta(contentIndex=0, "The attached file shows main() wiring config and server.")`
7. ``textDelta(contentIndex=1, "`main` parses the CLI arguments, ")``
8. ``textDelta(contentIndex=1, "loads `config.toml`, and starts the HTTP server on port 8080.")``
9. `usage(contextUsed=18342, contextSize=200000, cumulativeCost=0.0123 USD)`
10. `messageEnded`
11. `agentEnded(StopReason.Stop)`

### A.2 夹具 B：tool_call（kind=edit，diff）→ 权限请求 → 允许一次 → completed

```text
> {"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":1,"clientCapabilities":{"fs":{"readTextFile":false,"writeTextFile":false},"terminal":false,"elicitation":{"form":{}},"auth":{"terminal":false},"session":{"configOptions":{"boolean":{}},"notices":{}}},"clientInfo":{"name":"tkcode","title":"TKCode","version":"0.1.0"}}}
< {"jsonrpc":"2.0","id":0,"result":{"protocolVersion":1,"agentCapabilities":{"loadSession":true,"promptCapabilities":{"image":true,"audio":false,"embeddedContext":true},"mcpCapabilities":{"http":true,"sse":false},"sessionCapabilities":{"list":{},"resume":{},"close":{}},"auth":{}},"authMethods":[{"id":"oauth-personal","name":"Log in with Google","description":"Sign in with your Google account"}],"agentInfo":{"name":"example-agent","title":"Example Agent","version":"1.4.2"}}}
> {"jsonrpc":"2.0","id":1,"method":"session/new","params":{"cwd":"/Users/dev/project","mcpServers":[]}}
< {"jsonrpc":"2.0","id":1,"result":{"sessionId":"sess_7f3a9c"}}
> {"jsonrpc":"2.0","id":2,"method":"session/prompt","params":{"sessionId":"sess_7f3a9c","prompt":[{"type":"text","text":"Turn on debug mode in src/config.json"}]}}
< {"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_7f3a9c","update":{"sessionUpdate":"agent_message_chunk","messageId":"msg_01","content":{"type":"text","text":"I'll enable debug mode in `src/config.json`."}}}}
< {"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_7f3a9c","update":{"sessionUpdate":"tool_call","toolCallId":"call_001","name":"edit_file","title":"Edit src/config.json","kind":"edit","status":"pending","locations":[{"path":"/Users/dev/project/src/config.json","line":2}],"rawInput":{"path":"/Users/dev/project/src/config.json","old_string":"\"debug\": false","new_string":"\"debug\": true"},"content":[{"type":"diff","path":"/Users/dev/project/src/config.json","oldText":"{\n  \"debug\": false\n}\n","newText":"{\n  \"debug\": true\n}\n"}]}}}
< {"jsonrpc":"2.0","id":0,"method":"session/request_permission","params":{"sessionId":"sess_7f3a9c","toolCall":{"toolCallId":"call_001","title":"Edit src/config.json","kind":"edit","status":"pending"},"options":[{"optionId":"allow-once","name":"Allow","kind":"allow_once"},{"optionId":"allow-always","name":"Always allow edits","kind":"allow_always"},{"optionId":"reject-once","name":"Reject","kind":"reject_once"}]}}
> {"jsonrpc":"2.0","id":0,"result":{"outcome":{"outcome":"selected","optionId":"allow-once"}}}
< {"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_7f3a9c","update":{"sessionUpdate":"tool_call_update","toolCallId":"call_001","status":"in_progress"}}}
< {"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_7f3a9c","update":{"sessionUpdate":"tool_call_update","toolCallId":"call_001","status":"completed","rawOutput":{"success":true}}}}
< {"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_7f3a9c","update":{"sessionUpdate":"agent_message_chunk","messageId":"msg_02","content":{"type":"text","text":"Debug mode is now enabled."}}}}
< {"jsonrpc":"2.0","id":2,"result":{"stopReason":"end_turn"}}
```

期望事件：
1. `agentStarted`
2. `messageStarted`
3. ``textDelta(0, "I'll enable debug mode in `src/config.json`.")``
4. `toolCallStarted(call_001, name="edit_file", kind=edit, title="Edit src/config.json", locations=[config.json:2])`
5. `toolCallDelta(call_001, rawInput={path, old_string, new_string}, 整体替换)`
6. `[UI] tool/approve`：三按钮模式，三个选项都可用，并显示 diff。用户选择「允许一次」→ TKCode 写出 `selected / allow-once`。
7. `toolCallEnded(call_001)`
8. `toolExecutionStarted(call_001)`
9. `toolExecutionEnded(call_001, completed, diff 保留（该更新省略了 content）, rawOutput={success:true}, isError=false)`
10. `messageEnded`：规则 §15.2-3(a)，当前消息已有 tool call，又来了正文；同时 `messageId` 从 `msg_01` 变成了 `msg_02`
11. `messageStarted`
12. `textDelta(0, "Debug mode is now enabled.")`
13. `messageEnded`
14. `agentEnded(StopReason.Stop)`

### A.3 夹具 C：权限被拒绝

```text
> {"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":1,"clientCapabilities":{"fs":{"readTextFile":false,"writeTextFile":false},"terminal":false,"elicitation":{"form":{}},"auth":{"terminal":false},"session":{"configOptions":{"boolean":{}},"notices":{}}},"clientInfo":{"name":"tkcode","title":"TKCode","version":"0.1.0"}}}
< {"jsonrpc":"2.0","id":0,"result":{"protocolVersion":1,"agentCapabilities":{"loadSession":true,"promptCapabilities":{"image":true,"audio":false,"embeddedContext":true},"mcpCapabilities":{"http":true,"sse":false},"sessionCapabilities":{"list":{},"resume":{},"close":{}},"auth":{}},"authMethods":[{"id":"oauth-personal","name":"Log in with Google","description":"Sign in with your Google account"}],"agentInfo":{"name":"example-agent","title":"Example Agent","version":"1.4.2"}}}
> {"jsonrpc":"2.0","id":1,"method":"session/new","params":{"cwd":"/Users/dev/project","mcpServers":[]}}
< {"jsonrpc":"2.0","id":1,"result":{"sessionId":"sess_7f3a9c"}}
> {"jsonrpc":"2.0","id":2,"method":"session/prompt","params":{"sessionId":"sess_7f3a9c","prompt":[{"type":"text","text":"Clean the build output and rebuild"}]}}
< {"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_7f3a9c","update":{"sessionUpdate":"agent_message_chunk","messageId":"msg_01","content":{"type":"text","text":"I'll remove the build directory first."}}}}
< {"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_7f3a9c","update":{"sessionUpdate":"tool_call","toolCallId":"call_002","name":"run_shell_command","title":"rm -rf build","kind":"execute","status":"pending","rawInput":{"command":"rm -rf build","cwd":"/Users/dev/project"}}}}
< {"jsonrpc":"2.0","id":0,"method":"session/request_permission","params":{"sessionId":"sess_7f3a9c","toolCall":{"toolCallId":"call_002","title":"rm -rf build","kind":"execute","status":"pending","rawInput":{"command":"rm -rf build","cwd":"/Users/dev/project"}},"options":[{"optionId":"allow-once","name":"Allow once","kind":"allow_once"},{"optionId":"allow-always","name":"Always allow rm","kind":"allow_always"},{"optionId":"reject-once","name":"Reject","kind":"reject_once"}]}}
> {"jsonrpc":"2.0","id":0,"result":{"outcome":{"outcome":"selected","optionId":"reject-once"}}}
< {"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_7f3a9c","update":{"sessionUpdate":"tool_call_update","toolCallId":"call_002","status":"failed","content":[{"type":"content","content":{"type":"text","text":"The user rejected this command."}}]}}}
< {"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_7f3a9c","update":{"sessionUpdate":"agent_message_chunk","messageId":"msg_02","content":{"type":"text","text":"Understood, I will not delete the build directory."}}}}
< {"jsonrpc":"2.0","id":2,"result":{"stopReason":"end_turn"}}
```

期望事件：
1. `agentStarted`
2. `messageStarted`
3. `textDelta(0, "I'll remove the build directory first.")`
4. `toolCallStarted(call_002, name="run_shell_command", kind=execute, title="rm -rf build")`
5. `toolCallDelta(call_002, rawInput={command:"rm -rf build", cwd:"/Users/dev/project"})`
6. `[UI] tool/approve`：三按钮模式，显示命令。用户选择「拒绝」→ TKCode 写出 `selected / reject-once`。
7. `toolCallEnded(call_002)`
8. `toolExecutionStarted(call_002)`：补发，因为状态从 `pending` 直接跳到了 `failed`
9. `toolExecutionEnded(call_002, failed, isError=true, rejected=true, content="The user rejected this command.")`
10. `messageEnded`
11. `messageStarted`
12. `textDelta(0, "Understood, I will not delete the build directory.")`
13. `messageEnded`
14. `agentEnded(StopReason.Stop)`

### A.4 夹具 D：权限挂起时 session/cancel → cancelled

```text
> {"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":1,"clientCapabilities":{"fs":{"readTextFile":false,"writeTextFile":false},"terminal":false,"elicitation":{"form":{}},"auth":{"terminal":false},"session":{"configOptions":{"boolean":{}},"notices":{}}},"clientInfo":{"name":"tkcode","title":"TKCode","version":"0.1.0"}}}
< {"jsonrpc":"2.0","id":0,"result":{"protocolVersion":1,"agentCapabilities":{"loadSession":true,"promptCapabilities":{"image":true,"audio":false,"embeddedContext":true},"mcpCapabilities":{"http":true,"sse":false},"sessionCapabilities":{"list":{},"resume":{},"close":{}},"auth":{}},"authMethods":[{"id":"oauth-personal","name":"Log in with Google","description":"Sign in with your Google account"}],"agentInfo":{"name":"example-agent","title":"Example Agent","version":"1.4.2"}}}
> {"jsonrpc":"2.0","id":1,"method":"session/new","params":{"cwd":"/Users/dev/project","mcpServers":[]}}
< {"jsonrpc":"2.0","id":1,"result":{"sessionId":"sess_7f3a9c"}}
> {"jsonrpc":"2.0","id":2,"method":"session/prompt","params":{"sessionId":"sess_7f3a9c","prompt":[{"type":"text","text":"Run the full test suite"}]}}
< {"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_7f3a9c","update":{"sessionUpdate":"agent_message_chunk","messageId":"msg_01","content":{"type":"text","text":"Running the full test suite."}}}}
< {"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_7f3a9c","update":{"sessionUpdate":"tool_call","toolCallId":"call_003","title":"cargo test --workspace","kind":"execute","status":"pending","rawInput":{"command":"cargo test --workspace"}}}}
< {"jsonrpc":"2.0","id":0,"method":"session/request_permission","params":{"sessionId":"sess_7f3a9c","toolCall":{"toolCallId":"call_003"},"options":[{"optionId":"allow-once","name":"Allow once","kind":"allow_once"},{"optionId":"reject-once","name":"Reject","kind":"reject_once"}]}}
> {"jsonrpc":"2.0","method":"session/cancel","params":{"sessionId":"sess_7f3a9c"}}
> {"jsonrpc":"2.0","id":0,"result":{"outcome":{"outcome":"cancelled"}}}
< {"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_7f3a9c","update":{"sessionUpdate":"tool_call_update","toolCallId":"call_003","status":"failed","content":[{"type":"content","content":{"type":"text","text":"Cancelled by user."}}]}}}
< {"jsonrpc":"2.0","id":2,"result":{"stopReason":"cancelled"}}
```

注意第 9、10 行：用户点击「停止」后，TKCode **先**写出 `session/cancel` 通知，**再**对挂起的权限请求（Agent 的 id=0）回复 `cancelled`。两行的相对顺序是本夹具的断言点。权限请求里的 `toolCall` 只有 `toolCallId`，这是合法的。

期望事件：
1. `agentStarted`
2. `messageStarted`
3. `textDelta(0, "Running the full test suite.")`
4. `toolCallStarted(call_003, kind=execute, title="cargo test --workspace")`
5. `toolCallDelta(call_003, rawInput={command:"cargo test --workspace"})`
6. `[UI] tool/approve` 打开 → 用户中断 → 对话框关闭（不是用户点击了拒绝）
7. 本地取消（随 `session/cancel` 立即发生）：`toolCallEnded(call_003)` → `toolExecutionStarted(call_003)`（补发）→ `toolExecutionEnded(call_003, cancelled)`
8. 之后的 `tool_call_update(failed, "Cancelled by user.")`：工具已是终态，只更新显示内容，**不发**生命周期事件
9. `messageEnded`
10. `agentEnded(StopReason.Aborted)`

### A.5 夹具 E：config options（分组的模型选择、思考深度、布尔项）+ set_config_option

```text
> {"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":1,"clientCapabilities":{"fs":{"readTextFile":false,"writeTextFile":false},"terminal":false,"elicitation":{"form":{}},"auth":{"terminal":false},"session":{"configOptions":{"boolean":{}},"notices":{}}},"clientInfo":{"name":"tkcode","title":"TKCode","version":"0.1.0"}}}
< {"jsonrpc":"2.0","id":0,"result":{"protocolVersion":1,"agentCapabilities":{"loadSession":true,"promptCapabilities":{"image":true,"audio":false,"embeddedContext":true},"mcpCapabilities":{"http":true,"sse":false},"sessionCapabilities":{"list":{},"resume":{},"close":{}},"auth":{}},"authMethods":[{"id":"oauth-personal","name":"Log in with Google","description":"Sign in with your Google account"}],"agentInfo":{"name":"example-agent","title":"Example Agent","version":"1.4.2"}}}
> {"jsonrpc":"2.0","id":1,"method":"session/new","params":{"cwd":"/Users/dev/project","mcpServers":[]}}
< {"jsonrpc":"2.0","id":1,"result":{"sessionId":"sess_7f3a9c","configOptions":[{"id":"mode","name":"Approval Mode","description":"Controls when the agent asks for permission","category":"mode","type":"select","currentValue":"default","options":[{"value":"default","name":"Default","description":"Ask before edits and commands"},{"value":"auto_edit","name":"Auto Edit","description":"Apply edits without asking"},{"value":"plan","name":"Plan","description":"Read-only planning"}]},{"id":"model","name":"Model","category":"model","type":"select","currentValue":"pro-2","options":[{"group":"recommended","name":"Recommended","options":[{"value":"pro-2","name":"Pro 2","description":"Most capable"},{"value":"flash-2","name":"Flash 2","description":"Fast and low cost"}]},{"group":"legacy","name":"Legacy","options":[{"value":"pro-1","name":"Pro 1"}]}]},{"id":"reasoning","name":"Thinking","category":"thought_level","type":"select","currentValue":"high","options":[{"value":"low","name":"Low"},{"value":"medium","name":"Medium"},{"value":"high","name":"High"}]},{"id":"fast","name":"Fast Mode","description":"Lower latency at higher cost","category":"model_config","type":"boolean","currentValue":false}]}}
> {"jsonrpc":"2.0","id":2,"method":"session/set_config_option","params":{"sessionId":"sess_7f3a9c","configId":"model","value":"flash-2"}}
< {"jsonrpc":"2.0","id":2,"result":{"configOptions":[{"id":"mode","name":"Approval Mode","description":"Controls when the agent asks for permission","category":"mode","type":"select","currentValue":"default","options":[{"value":"default","name":"Default","description":"Ask before edits and commands"},{"value":"auto_edit","name":"Auto Edit","description":"Apply edits without asking"},{"value":"plan","name":"Plan","description":"Read-only planning"}]},{"id":"model","name":"Model","category":"model","type":"select","currentValue":"flash-2","options":[{"group":"recommended","name":"Recommended","options":[{"value":"pro-2","name":"Pro 2","description":"Most capable"},{"value":"flash-2","name":"Flash 2","description":"Fast and low cost"}]},{"group":"legacy","name":"Legacy","options":[{"value":"pro-1","name":"Pro 1"}]}]},{"id":"reasoning","name":"Thinking","category":"thought_level","type":"select","currentValue":"medium","options":[{"value":"low","name":"Low"},{"value":"medium","name":"Medium"}]},{"id":"fast","name":"Fast Mode","description":"Lower latency at higher cost","category":"model_config","type":"boolean","currentValue":false}]}}
> {"jsonrpc":"2.0","id":3,"method":"session/set_config_option","params":{"sessionId":"sess_7f3a9c","configId":"fast","type":"boolean","value":true}}
< {"jsonrpc":"2.0","id":3,"result":{"configOptions":[{"id":"mode","name":"Approval Mode","description":"Controls when the agent asks for permission","category":"mode","type":"select","currentValue":"default","options":[{"value":"default","name":"Default","description":"Ask before edits and commands"},{"value":"auto_edit","name":"Auto Edit","description":"Apply edits without asking"},{"value":"plan","name":"Plan","description":"Read-only planning"}]},{"id":"model","name":"Model","category":"model","type":"select","currentValue":"flash-2","options":[{"group":"recommended","name":"Recommended","options":[{"value":"pro-2","name":"Pro 2","description":"Most capable"},{"value":"flash-2","name":"Flash 2","description":"Fast and low cost"}]},{"group":"legacy","name":"Legacy","options":[{"value":"pro-1","name":"Pro 1"}]}]},{"id":"reasoning","name":"Thinking","category":"thought_level","type":"select","currentValue":"medium","options":[{"value":"low","name":"Low"},{"value":"medium","name":"Medium"}]},{"id":"fast","name":"Fast Mode","description":"Lower latency at higher cost","category":"model_config","type":"boolean","currentValue":true}]}}
< {"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_7f3a9c","update":{"sessionUpdate":"config_option_update","configOptions":[{"id":"mode","name":"Approval Mode","description":"Controls when the agent asks for permission","category":"mode","type":"select","currentValue":"auto_edit","options":[{"value":"default","name":"Default","description":"Ask before edits and commands"},{"value":"auto_edit","name":"Auto Edit","description":"Apply edits without asking"},{"value":"plan","name":"Plan","description":"Read-only planning"}]},{"id":"model","name":"Model","category":"model","type":"select","currentValue":"flash-2","options":[{"group":"recommended","name":"Recommended","options":[{"value":"pro-2","name":"Pro 2","description":"Most capable"},{"value":"flash-2","name":"Flash 2","description":"Fast and low cost"}]},{"group":"legacy","name":"Legacy","options":[{"value":"pro-1","name":"Pro 1"}]}]},{"id":"reasoning","name":"Thinking","category":"thought_level","type":"select","currentValue":"medium","options":[{"value":"low","name":"Low"},{"value":"medium","name":"Medium"}]},{"id":"fast","name":"Fast Mode","description":"Lower latency at higher cost","category":"model_config","type":"boolean","currentValue":true}]}}}
```

期望事件和状态：
1. 会话状态：4 个配置项。
   - `mode`：select，3 个值，当前 `default`；
   - `model`：分组 select，`recommended` 组里有 `pro-2`、`flash-2`，`legacy` 组里有 `pro-1`，当前 `pro-2`；
   - `reasoning`：thought_level，当前 `high`；
   - `fast`：model_config 类布尔项，当前 `false`。
   - UI 映射：`model` → 模型选择器（分两组显示）；`reasoning` → ThinkingLevel；`mode` → 模式选择器；`fast` → 模型面板中的开关。
2. 用户选择 `flash-2` → TKCode 写出 `{configId:"model", value:"flash-2"}`（**不带** `type`）。
3. 响应整体替换本地状态：`reasoning` 的可选值变为 `low` / `medium`，当前值变为 `medium`。UI 的 ThinkingLevel 选项随之更新。
4. 用户打开 Fast Mode → TKCode 写出 `{configId:"fast", type:"boolean", value:true}`。
5. 响应整体替换：`fast=true`。
6. Agent 主动推送 `config_option_update`：`mode` 变为 `auto_edit`。整体替换后，UI 模式选择器显示 Auto Edit。
7. 全程不产生 AgentEvent（没有轮次）。

### A.6 夹具 F：响应之前到达的通知 + 旧版 modes / set_mode

```text
> {"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":1,"clientCapabilities":{"fs":{"readTextFile":false,"writeTextFile":false},"terminal":false,"elicitation":{"form":{}},"auth":{"terminal":false},"session":{"configOptions":{"boolean":{}},"notices":{}}},"clientInfo":{"name":"tkcode","title":"TKCode","version":"0.1.0"}}}
< {"jsonrpc":"2.0","id":0,"result":{"protocolVersion":1,"agentCapabilities":{"loadSession":true,"promptCapabilities":{"image":true,"audio":false,"embeddedContext":true},"mcpCapabilities":{"http":true,"sse":false},"sessionCapabilities":{"list":{},"resume":{},"close":{}},"auth":{}},"authMethods":[{"id":"oauth-personal","name":"Log in with Google","description":"Sign in with your Google account"}],"agentInfo":{"name":"example-agent","title":"Example Agent","version":"1.4.2"}}}
> {"jsonrpc":"2.0","id":1,"method":"session/new","params":{"cwd":"/Users/dev/project","mcpServers":[]}}
< {"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_b20e41","update":{"sessionUpdate":"available_commands_update","availableCommands":[{"name":"compress","description":"Summarize the conversation to save context"}]}}}
< {"jsonrpc":"2.0","id":1,"result":{"sessionId":"sess_b20e41","modes":{"currentModeId":"ask","availableModes":[{"id":"ask","name":"Ask","description":"Request permission before making any changes"},{"id":"code","name":"Code","description":"Write and modify code with full tool access"}]}}}
> {"jsonrpc":"2.0","id":2,"method":"session/set_mode","params":{"sessionId":"sess_b20e41","modeId":"code"}}
< {"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_b20e41","update":{"sessionUpdate":"current_mode_update","currentModeId":"code"}}}
< {"jsonrpc":"2.0","id":2,"result":{}}
```

期望：
1. 第 4 行的 `available_commands_update` 到达时，`sess_b20e41` 还未知。因为 `session/new`（id=1）仍在挂起，所以暂存这条通知。
2. 第 5 行响应到达后，确定会话为 `sess_b20e41`，回放暂存的通知：命令列表为 `compress`。
3. 响应中没有 `configOptions`，只有 `modes`，因此回退到旧的模式接口：当前模式为 `ask`，可选 `ask` / `code`。
4. 用户切换到 Code → TKCode 写出 `session/set_mode {modeId:"code"}`。
5. `current_mode_update` 先于响应到达：应用 `currentModeId = code`。响应 `{}` 到达后，状态保持 `code`。

### A.7 夹具 G：auth_required → authenticate（白名单方法）→ 重试 session/new

```text
> {"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":1,"clientCapabilities":{"fs":{"readTextFile":false,"writeTextFile":false},"terminal":false,"elicitation":{"form":{}},"auth":{"terminal":false},"session":{"configOptions":{"boolean":{}},"notices":{}}},"clientInfo":{"name":"tkcode","title":"TKCode","version":"0.1.0"}}}
< {"jsonrpc":"2.0","id":0,"result":{"protocolVersion":1,"agentCapabilities":{"loadSession":true,"promptCapabilities":{"image":true,"audio":false,"embeddedContext":false},"mcpCapabilities":{"http":true,"sse":true},"sessionCapabilities":{"list":{}}},"authMethods":[{"id":"cursor_login","name":"Cursor Login","description":"Authenticate using existing Cursor login credentials. Run 'cursor-agent login' first if not logged in."}],"agentInfo":{"name":"example-agent","version":"1.0.0"}}}
> {"jsonrpc":"2.0","id":1,"method":"session/new","params":{"cwd":"/Users/dev/project","mcpServers":[]}}
< {"jsonrpc":"2.0","id":1,"error":{"code":-32000,"message":"Authentication required"}}
> {"jsonrpc":"2.0","id":2,"method":"authenticate","params":{"methodId":"cursor_login"}}
< {"jsonrpc":"2.0","id":2,"result":{}}
> {"jsonrpc":"2.0","id":3,"method":"session/new","params":{"cwd":"/Users/dev/project","mcpServers":[]}}
< {"jsonrpc":"2.0","id":3,"result":{"sessionId":"sess_c91d07"}}
```

期望：
1. 初始化后**不**主动认证。
2. `session/new` 返回 `-32000`。`authMethods` 中恰好有一个 agent 类型方法 `cursor_login`，而且它在白名单内，因此自动写出 `authenticate {methodId:"cursor_login"}`。
3. 认证成功后，用**新的请求 id**（3）重试一次 `session/new`，得到 `sess_c91d07`。
4. 如果 `authenticate` 返回错误，或者重试后仍然返回 `-32000`，则停止自动处理，显示 §4.3 的登录提示。不会进入循环。
