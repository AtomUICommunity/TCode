# Codex app-server 协议规格（TCode 实现用）

> 版本基线：**codex-cli 0.162.1**（用户本机安装版本）。字段、枚举、必填性一律以该版本生成的 JSON Schema 为准；较新源码（2026-10-16 main，commit `e790ce2`）只用来解释行为，凡是只在新源码中出现、0.162.1 schema 里没有的内容都单独标注。
> 用途：TCode（C#/.NET 10 NativeAOT）把 `codex app-server` 作为子进程，通过 stdio 上的 JSON-RPC 驱动 Codex Agent：握手、线程/轮次管理、流式渲染、审批。
> 每条结论都标了出处。标为 [Probe] 的条目是本机对 0.162.1 实测（只做了 `initialize` / `model/list` / `account/read` 等只读调用，没有启动模型轮次）；标为 [Zeron] 的条目是生产驱动里沉淀的经验，不是官方承诺。

## 0. 来源

| 代号 | 来源 |
|---|---|
| [Schema] | `codex app-server generate-json-schema` 在 0.162.1 下的输出（**不带** `--experimental`）：`scratchpad/specs/codex-schema-0.162.1/`，包括 `ClientRequest.json`、`ServerRequest.json`、`ServerNotification.json`、`ClientNotification.json`、`codex_app_server_protocol.schemas.json`（v1 + 通用类型）、`codex_app_server_protocol.v2.schemas.json`（v2 类型），以及 `v1/`、`v2/` 下的逐类型文件 |
| [Schema-exp] | 同一版本带 `--experimental` 生成的 schema（`scratchpad/specs/codex-schema-0.162.1-exp/`），只用来对比哪些字段属于实验性 |
| [Src] | `openai/codex` 源码（较新）：`codex-rs/app-server-protocol/src/rpc.rs`、`src/protocol/common.rs`、`src/protocol/v2/*.rs`；`codex-rs/app-server/src/{message_processor.rs, bespoke_event_handling.rs, outgoing_message.rs, transport.rs, error_code.rs, server_request_error.rs, lib.rs, request_processors/*.rs}`；`codex-rs/app-server-transport/src/transport/{mod.rs, stdio.rs}` |
| [README] | `codex-rs/app-server/README.md`（新版只保留增量说明：shutdown 错误、线程删除等） |
| [PySDK] | `sdk/python/src/openai_codex/{client.py, _message_router.py, errors.py, _initialize_metadata.py, _sandbox.py, _approval_mode.py}`：官方参考客户端 |
| [Zeron] | zeron 的生产驱动 `crates/harness/src/codex/{mod.rs, normalize.rs, catalog.rs}`（对 0.146–0.159 做过实测） |
| [Docs] | https://learn.chatgpt.com/docs/app-server （2026-10-16 抓取） |
| [Probe] | 本机 `codex-cli 0.162.1` 实测，见各处说明 |

---

## 1. 进程与传输

### 1.1 启动

- 命令：`codex app-server`。默认就是 `--listen stdio://`（等价于 `--stdio`），显式写上 `--listen stdio://` 也可以 [Probe `codex app-server --help`；PySDK `client.py:start`]。
- 全局配置覆盖写在子命令**之前或之后**都可以：`codex app-server -c key=value`、`--enable <feature>`、`--disable <feature>`、`--strict-config`。值按 TOML 解析，解析失败时当作字面字符串 [Probe help]。TCode 优先用 `thread/start.config`（§3.1）按线程覆盖，只在需要进程级设置时才用 `-c`。
- 子进程 `cwd`：设成会话 cwd，但线程实际使用的工作目录以 `thread/start.cwd` 为准（§3.1）。
- 环境变量：
  - `CODEX_HOME`：配置/会话/凭据目录，默认 `~/.codex`。`initialize` 的响应会回报实际值 [Schema `InitializeResponse.codexHome`]。
  - `RUST_LOG`：stderr 日志级别（tracing `EnvFilter`），不设时只输出 ERROR [Src `app-server/src/lib.rs:717-732`；Probe]。
  - `LOG_FORMAT=json`：stderr 改为 JSON 行日志 [Src `lib.rs:158,435`]。不设时日志带 ANSI 颜色码 [Probe]。
- 可执行文件发现：与其他运行时相同（PATH，以及 `/opt/homebrew/bin`、`/usr/local/bin`）[Zeron `mod.rs:83-94`]。版本探测用 `codex --version`，输出 `codex-cli 0.162.1` [Probe]。

### 1.2 帧格式（stdio）

- **换行分隔的 JSON（JSONL）**：一行一条消息，UTF-8 编码，以 `\n` 结尾。服务端用 `serde_json::to_string` 写出后追加 `'\n'`，所以输出里不会有未转义的换行 [Src `app-server-transport/src/transport/stdio.rs:138-141`]。
- 服务端按行读 stdin（`std::io::stdin().lock().lines()`），**没有长度上限**；客户端读 stdout 也不能设行长上限，`aggregatedOutput`、diff、`turns` 历史都可能有几 MB [Src `stdio.rs`]。
- **没有 `"jsonrpc":"2.0"` 字段**：服务端不发送，也不要求客户端发送。源码原文：`We do not do true JSON-RPC 2.0, as we neither send nor expect the "jsonrpc": "2.0" field.` [Src `app-server-protocol/src/rpc.rs:1-2`；Docs]。反序列化时多出的未知字段会被忽略，但 TCode **不发送**这个字段。
- 消息类型按字段区分（`untagged`）[Src `rpc.rs`；Schema `JSONRPCMessage.json`]：

| 形状 | 含义 |
|---|---|
| `{"id", "method", "params"?, "trace"?}` | 请求（双向都有） |
| `{"method", "params"?}` | 通知（双向都有） |
| `{"id", "result"}` | 成功响应 |
| `{"id", "error": {"code", "message", "data"?}}` | 错误响应 |

- **服务端通知的信封上多一个 `emittedAtMs` 字段**（Unix 毫秒），与 `method`/`params` 同级。JSON Schema 里没有它，但线上每条通知都带 [Probe：`{"method":"remoteControl/status/changed","params":{…},"emittedAtMs":1792127431282}`；Src `outgoing_message.rs:904-909`、`transport/mod.rs:328-352`]。服务端发出的请求和响应不带这个字段。
- 客户端发出的通知没有 `params` 时**省略**这个键，例如 `{"method":"initialized"}` [Schema `ClientNotification.json`：`InitializedNotification` 只有 `method`；PySDK `notify`]。
- **请求 id**：`string | int64` [Schema `RequestId.json`]。两个方向的 id 空间相互独立。服务端发起的请求 id 是**整数，按进程从 0 开始递增** [Src `outgoing_message.rs:332-333`]。TCode 发出的请求用自增整数 id（从 1 开始），并按「是否有 `method`」区分「服务端请求」和「对我方请求的响应」，不能只看 id。
- **非法 JSON 行会被静默丢弃**：服务端只在 stderr 记一条 `Failed to deserialize JSONRPCMessage`，不回 `-32700` [Src `transport/mod.rs:214-229`；Probe]。所以一条写坏的请求永远不会有响应，客户端必须给每个请求设超时。
- 可选字段 `trace: {traceparent, tracestate}`（W3C Trace Context）[Schema `JSONRPCRequest.json`]，TCode 不使用。

### 1.3 并发与顺序

- **响应可能乱序**：请求是并发处理的。实测中 `id:6` 的 `Already initialized` 先于 `id:2` 的 `model/list` 返回 [Probe]。客户端必须按 id 匹配响应。
- 同一线程内的通知按事件发生顺序发出，但**请求的响应与通知之间没有顺序保证**：
  - `turn/start` 的响应和 `turn/started` 通知谁先到都有可能 [Zeron `mod.rs` `TurnRouter`；PySDK `_message_router.py` 会在 `turn/start` 响应到达前缓冲该线程的通知]。
  - `thread/start` 的实现是先发响应再发 `thread/started`，但客户端不能依赖这一点 [Src `request_processors/thread_processor.rs:1655-1664`]。
- 一个进程可以有**多个线程**。除了少数全局通知（`account/*`、`configWarning`、`model/list` 相关等），每条通知的 `params` 都带 `threadId`（`thread/started` 的线程 id 在 `params.thread.id`），客户端按线程分派 [Schema；Zeron `normalize.rs:652-666`]。
- **子 Agent 线程**（多 Agent 模式）的通知也在同一条流上，`threadId` 是子线程的。如果误把子线程的 `turn/completed` 当成父线程的，就会提前结束父轮次。按 `threadId` 严格过滤，未知线程的通知不能用来结束当前轮次 [Zeron `mod.rs` main loop、`normalize.rs:668-735`]。

### 1.4 背压

- 入站队列容量是 128。队列满时，新的**请求**直接得到错误 `-32001 "Server overloaded; retry later."`（通知和响应不会被拒绝，而是排队等待）[Src `transport/mod.rs:24,241-269`；Docs]。
- 客户端遇到 `-32001` 时用指数退避加抖动重试，参考值：最多 3 次，初始 0.25 s，上限 2 s [PySDK `client.py:request_with_retry_on_overload`、`errors.py:100-106`]。

### 1.5 stderr

- stderr 只有日志，**不是协议的一部分**。必须持续读取（否则管道写满后服务端会阻塞），保留最近 N 行（建议 200 行），用于进程意外退出时的错误信息 [Zeron `mod.rs:808-816`、`crash_message`；PySDK `_stderr_tail`]。
- 默认格式带 ANSI 颜色码 [Probe]。TCode 启动时设 `LOG_FORMAT=json`，或者在展示前去掉 ANSI 序列。

### 1.6 关闭

- stdio 是单客户端模式：stdin 收到 EOF 时连接关闭，服务端随即清理线程并退出 [Src `lib.rs:795,1173-1194`]。空闲时实测 0.07 s 内以退出码 0 结束 [Probe]。
- EOF 或 SIGTERM（Unix）都会启动一个 **45 s 看门狗**，超时后 `exit(1)` [Src `stdio.rs:168-182`]。stdio 模式下 SIGTERM 的效果等同于 EOF [Src `stdio.rs:start_stdio_connection`]。
- 服务端进入优雅关闭阶段后，新的请求会得到 `-32600`，`data: {"reason":"serverShuttingDown"}` [README「Shutdown admission errors」；Src `error_code.rs:server_draining_error`]。
- TCode 的关闭顺序（§10.3）：关 stdin → 等待 ≤5 s → SIGTERM（仅 Unix）→ 等待 ≤2 s → 杀进程树。

---

## 2. 握手

每条连接先发一次 `initialize` 请求，再发 `initialized` 通知，然后才能调用其他方法 [Docs；PySDK `initialize`]。

### 2.1 `initialize`

请求 params（`InitializeParams`）[Schema]：

| 字段 | 类型 | 说明 |
|---|---|---|
| `clientInfo.name` | string，必填 | 必须是合法的 HTTP header 值，否则返回 `-32600 Invalid clientInfo.name` [Src `initialize_processor.rs`]。它会成为 **originator**，也就是 `userAgent` 的前缀和线程的 `originator`。TCode 固定用 `"tcode"`。 |
| `clientInfo.title` | string \| null | 展示名，用 `"TCode"`。 |
| `clientInfo.version` | string，必填 | TCode 版本号。 |
| `capabilities` | object \| null | 见 §2.3。 |

响应 `InitializeResponse`（0.162.1 的 4 个字段全部必填）[Schema `v1/InitializeResponse.json`；Probe]：

```json
{"id":1,"result":{"userAgent":"tcode_probe/0.162.1 (Mac OS 26.6.2; arm64) vscode/1.142.0 (tcode_probe; 0.0.1)","codexHome":"/Users/token/.codex","platformFamily":"unix","platformOs":"macos"}}
```

- `userAgent` 的格式是 `<clientInfo.name>/<codex 版本> (<OS>) <终端/IDE 信息> (<clientInfo.name>; <clientInfo.version>)`。**前缀是我们自己的 client name，不是 `codex`**。Codex 版本取第一个 `/` 到第一个空格之间的部分 [Probe；PySDK `_initialize_metadata.py:_split_user_agent`]。
- 较新的服务端多了一个 `serverInfo: {name, version}`（0.162.1 没有）。解析时优先用 `serverInfo`，没有时再从 `userAgent` 里取 [PySDK `client.py:initialize`]。

### 2.2 `initialized`

```json
{"method":"initialized"}
```

服务端目前只把它记入日志 [Src `message_processor.rs:747-751`]，但官方协议要求发送 [Docs]。

### 2.3 Capabilities（`InitializeCapabilities`）[Schema]

| 字段 | 类型 | TCode 取值 | 说明 |
|---|---|---|---|
| `experimentalApi` | bool（默认 false） | **false** | 为 true 时，可以调用实验方法和字段，也会收到实验通知。为 false 时：调用实验方法或字段返回 `-32600 "<descriptor> requires experimentalApi capability"`；**实验通知直接不发**；`item/commandExecution/requestApproval` 中的实验字段（例如 `availableDecisions`）被剥离 [Src `message_processor.rs:984-988`、`transport.rs:104-200`；Docs]。本规格以 stable surface 为准，fixture 也是在 false 下构造的。 |
| `optOutNotificationMethods` | string[] \| null | 见下 | 按**完整方法名**屏蔽通知，不支持通配符，未知名字会被忽略，不影响请求、响应和错误 [Docs；Src `transport.rs:104-127`]。 |
| `requestAttestation` | bool | 不设 | 设了才会收到 `attestation/generate` 请求。 |
| `mcpServerOpenaiFormElicitation` / `extensions` | — | 不设 | MCP 扩展。 |
| `explicitGatewayOauth` | bool | 不设 | 网关 OAuth。 |

建议的 `optOutNotificationMethods`（TCode 不消费、量大或者纯噪声）：
`remoteControl/status/changed`、`thread/realtime/outputAudio/delta`、`thread/realtime/transcript/delta`、`fuzzyFileSearch/sessionUpdated`、`item/fileChange/outputDelta`（已弃用，服务端不再发送）、`command/exec/outputDelta`。**不能**屏蔽 §5 渲染所需的任何方法，也不能屏蔽 `serverRequest/resolved`。

> 关于 experimentalApi：zeron 一直设为 true [Zeron `mod.rs:1144`]，它也因此依赖了实验字段。TCode 先走 stable surface。将来要开启时，必须同时做好两件事：重新对比 [Schema-exp]，并容忍新字段和新通知。

### 2.4 握手错误 [Src `message_processor.rs:981`、`initialize_processor.rs:72,145`；Probe]

| 情形 | 错误 |
|---|---|
| 握手前调用任何方法 | `-32600 "Not initialized"` |
| 同一连接上第二次 `initialize` | `-32600 "Already initialized"` |
| 未知 method | `-32600 "Invalid request: unknown variant \`bogus/method\`, expected one of …"`（**注意是 -32600，不是 -32601**） |

---

## 3. 线程（thread）

线程对应一个持久化的 Codex 会话（rollout）。线程 id 是 UUIDv7 字符串；`thread.sessionId` 是会话树的 id，根线程的 sessionId 等于线程 id [Schema `Thread`；Docs「Use `thread.sessionId` rather than deriving the session from the thread id」]。TCode 的 NativeSessionId 用 **`thread.id`**（`thread/resume` 需要的就是它）。

### 3.1 `thread/start` → `ThreadStartParams`（全部可选）[Schema]

| 字段 | 类型 | TCode 用法 |
|---|---|---|
| `cwd` | string \| null | 会话工作目录，必须是**绝对路径**。不传时用服务端进程 cwd。 |
| `model` | string \| null | 取 `model/list` 结果里的 `model` 字段（§8.1）。 |
| `modelProvider` | string \| null | 不传，跟随配置。 |
| `approvalPolicy` | `AskForApproval` \| null | §7.1。 |
| `approvalsReviewer` | `"user"` \| `"auto_review"` \| `"guardian_subagent"` \| null | 不传（默认 `user`，由用户审批）。 |
| `sandbox` | `SandboxMode` \| null | **kebab-case 字符串**：`read-only` \| `workspace-write` \| `danger-full-access`（§7.2）。 |
| `config` | `map<string, any>` \| null | 按线程覆盖 `config.toml`。key 是点分路径（与 `-c` 一致，例如 `"model_reasoning_summary"`、`"features.xxx"`、`"mcp_servers.<name>.enabled"`），value 是 JSON 值 [Zeron `mod.rs:1083-1118`]。 |
| `baseInstructions` | string \| null | **替换** Codex 内置的基础系统提示词。TCode 不传，否则会丢掉 Codex 自带的工具使用说明。 |
| `developerInstructions` | string \| null | 追加一条 developer 消息。TCode 的附加系统提示放在这里。 |
| `ephemeral` | bool \| null | true 表示不落盘（不能在进程外恢复）。只用于临时任务，例如生成标题 [Zeron `mod.rs:1086-1091`]。 |
| `serviceTier` | string \| null | 线程默认的服务档位（§8.1 `serviceTiers[].id`）。 |
| `serviceName` | string \| null | 统计用，可不传。 |
| `personality` | — | 已弃用，不传。 |
| `sessionStartSource` | `"startup"` \| `"clear"` \| null | 不传。 |
| `threadSource` | string \| null | 统计用，不传。 |

**仅实验 API 可用（0.162.1 stable 不可用）**：`dynamicTools`、`permissions`、`environments`、`historyMode`、`runtimeWorkspaceRoots`、`experimentalRawEvents`、`projectId` 等 [Schema-exp 与 Schema 对比]。

### 3.2 `ThreadStartResponse` [Schema]

必填：`thread`、`model`、`modelProvider`、`cwd`、`approvalPolicy`、`approvalsReviewer`、`sandbox`。可选：`reasoningEffort`、`serviceTier`、`instructionSources`（已加载的 AGENTS.md 等文件路径）、`disabledPluginIds`。

- **`sandbox` 在响应里是 `SandboxPolicy` 对象**（`{"type":"workspaceWrite",…}`），而请求里是 kebab-case 字符串，两边形状不同（§7.2）。
- `model` 和 `reasoningEffort` 是**实际生效值**，用它们修正 TCode 显示的模型。

响应之后会有一条 `thread/started {thread}` 通知 [Src `thread_processor.rs:1655-1664`]。

### 3.3 `thread/resume` → `ThreadResumeParams`

必填 `threadId`。其余字段与 `thread/start` 相同（`model`、`cwd`、`approvalPolicy`、`sandbox`、`config`、`baseInstructions`、`developerInstructions`、`serviceTier` 等，用作恢复时的覆盖），另加 `excludeTurns: bool` [Schema]。

- 线程已在本进程运行时，服务端直接重新挂接；否则从磁盘加载 rollout [Schema 描述]。
- 响应 `ThreadResumeResponse` 比 start 多了 `collaborationMode?`、`turnsBackwardsCursor?`、`itemsBackwardsCursor?`。`thread.turns` 会填充历史（除非 `excludeTurns: true`）[Schema]。
- 恢复失败（rollout 不存在、不属于当前 CODEX_HOME 等）时返回错误响应。TCode 的处理：**回退为 `thread/start`**，再通过 `IRuntimeHost.OnNativeSessionId` 报告新的 id [Zeron `mod.rs:1178-1196`]。
- TCode 自己保存了消息历史，所以 resume 时设 `excludeTurns: true`，避免传回大量历史数据。

### 3.4 `thread/fork` → `ThreadForkParams`

必填 `threadId`。可选 `lastTurnId`（fork 到该轮为止，包含该轮；该轮不能处于进行中）、`ephemeral`、`excludeTurns`，以及与 start 相同的覆盖字段。响应结构同 `ThreadStartResponse` [Schema；Docs]。

### 3.5 `thread/list`、`thread/read`

- `thread/list`（`ThreadListParams`）：`cursor`、`limit`、`sortKey`（`created_at` \| `updated_at` \| `recency_at` \| `section_position`，**snake_case**）、`sortDirection`（`asc` \| `desc`，默认 desc）、`cwd`（一个或多个精确匹配的路径）、`archived`、`searchTerm`、`modelProviders`、`sourceKinds`、`useStateDbOnly` [Schema]。
  - 响应：`{data: Thread[], nextCursor?: string|null, backwardsCursor?}`。`nextCursor: null` 表示没有更多数据。`data` 中的 `Thread.turns` 恒为 `[]` [Schema `Thread.turns` 描述]。
  - `-32603` 只表示这次查询失败（例如状态 DB 暂不可用），**不代表**已经到底，可以重试 [README「Thread list exclusions」]。
  - `sourceKinds` 不传时只返回交互式来源的线程。TCode 列出自己创建的线程时传 `["appServer"]`。
- `thread/read`（`{threadId, includeTurns?}`）→ `{thread}`。`includeTurns: true` 时带上历史 turns 和 items [Schema]。

### 3.6 多线程、订阅、卸载

- 一个 app-server 进程可以承载多个线程，连接对自己 start/resume/fork 的线程**自动订阅**。
- `thread/unsubscribe {threadId}` → `{status: "notLoaded"|"notSubscribed"|"unsubscribed"}`：停止接收该线程的通知 [Schema]。
- 线程被卸载时会收到 `thread/closed {threadId}`。
- TCode 的选择：**一个 TCode 会话对应一个 app-server 进程、一个主线程**，与其他运行时保持一致，进程崩溃时互不影响。子 Agent 线程按 §1.3 过滤。

### 3.7 `Thread` 对象（0.162.1）[Schema]

必填：`id`、`sessionId`、`preview`、`ephemeral`、`projectId`（必填但可以为 null）、`modelProvider`、`createdAt`、`updatedAt`（**Unix 秒**）、`status`、`cwd`、`cliVersion`、`source`、`turns`。

可选/可空：`model`、`reasoningEffort`、`name`、`path`、`forkedFromId`、`parentThreadId`、`agentNickname`、`agentRole`、`gitInfo`、`originator`、`recencyAt`、`threadSource`、`historyMode`（`legacy`\|`paginated`）、`section`、`sectionEnteredAt`。

- `status: ThreadStatus` = `{"type":"notLoaded"}` \| `{"type":"idle"}` \| `{"type":"systemError"}` \| `{"type":"active","activeFlags":["waitingOnApproval"|"waitingOnUserInput", …]}`。
- `source: SessionSource` = `"cli"|"vscode"|"exec"|"appServer"|"unknown"` \| `{"custom":string}` \| `{"subAgent":…}`。

> 新版源码的 `Thread` 还有 `environments`、`extra`、`canAcceptDirectInput` 等字段，0.162.1 schema 里没有。**一律忽略未知字段。**

---

## 4. 轮次（turn）

### 4.1 `turn/start` → `TurnStartParams` [Schema]

| 字段 | 类型 | 说明 |
|---|---|---|
| `threadId` | string，必填 | |
| `input` | `UserInput[]`，必填 | 见下。 |
| `model` | string \| null | 覆盖**本轮及之后各轮**（下同，「sticky」）。 |
| `effort` | `ReasoningEffort` \| null | 推理档位（§7.4），sticky。 |
| `summary` | `"auto"\|"concise"\|"detailed"\|"none"` \| null | 推理摘要，sticky。**TCode 每轮都发 `"auto"`**。用户配置没打开摘要时，不发这个字段就收不到任何 `summaryTextDelta`，界面会长时间没有反馈 [Zeron `mod.rs:1270-1275`]。 |
| `approvalPolicy` | `AskForApproval` \| null | sticky。 |
| `sandboxPolicy` | `SandboxPolicy` \| null | **对象形状**（§7.2），sticky。注意线程级用的是 `sandbox` 字符串。 |
| `cwd` | string \| null | sticky。 |
| `serviceTier` | string \| null | sticky。**省略表示不变，显式 `null` 表示清除**（double-option 语义）[Src `v2/turn.rs:259-263`]。 |
| `serviceTierForTurn` | string \| null | 只对本轮生效；`"default"` 表示标准速度。 |
| `outputSchema` | JSON Schema | 只对本轮生效，约束最终回答的格式。 |
| `clientUserMessageId` | string \| null | 客户端给这条用户消息分配的 id，会出现在 `userMessage` item 的 `clientId` 中。 |
| `approvalsReviewer`、`personality`、`disabledPluginIds`、`toolOutput`、`parentTurnId`、`rootTurnId`、`turnTrigger` | — | TCode 不用。`toolOutput` 要求 `input` 为空数组 [Docs]。 |

`UserInput`（按 `type` 区分）[Schema]：

| type | 字段 |
|---|---|
| `text` | `text: string`，`text_elements?: TextElement[]`（**snake_case**；`TextElement = {byteRange:{start,end}, placeholder?}`） |
| `image` | `url: string` 或 `fileId: string` 二选一，`detail?: "auto"\|"low"\|"high"\|"original"` |
| `localImage` | `path: string`，`detail?` |
| `audio` / `localAudio` | `url` / `path` |
| `skill` / `mention` | `name`、`path` |

TCode 把文本附件拼进 `text`；图片附件（已保存在 `~/.tcode/attachments`，提示词里带路径）作为 `image` 传入，`url` 为 base64 data URL。只有模型的 `inputModalities` 包含 `"image"` 时才允许附图（§8.1）。

响应 `TurnStartResponse = {turn: Turn}`，其中 `turn.status = "inProgress"`，`items = []` [Schema；Docs]。

**轮次进行中又发 `turn/start`**：服务端会把它当作对活动轮次的 steer，并返回同一个 turn id（schema 原文：`turnTrigger` "Ignored when this request steers an already-active turn"）[Schema `TurnStartParams.turnTrigger`；Zeron `TurnRouter.adopt_started`]。TCode 不依赖这个行为，需要插入消息时显式使用 `turn/steer`。

### 4.2 `turn/steer` → `{threadId, expectedTurnId, input, clientUserMessageId?}` → `{turnId}`

- 把输入追加到**正在进行**的轮次。不会产生新的 `turn/started`，也不能带 model、cwd、sandbox 等覆盖 [Docs；Schema]。
- 失败（全部是 `-32600`）[Src `turn_processor.rs:1100-1182`]：
  - `"no active turn to steer"`（轮次刚好结束）
  - ``"expected active turn id `X` but found `Y`"``
  - `"cannot steer a review turn"` / `"cannot steer a compact turn"`，`data` 为 `TurnError`，`codexErrorInfo = {"activeTurnNotSteerable":{"turnKind":"review"|"compact"}}`
  - `"input must not be empty"`
- TCode 的处理：失败时 `SteerAsync` 返回 false，由调用方排队到下一轮 [Zeron `mod.rs:1797-1820`]。成功时以 `clientId == clientUserMessageId` 的 `userMessage` item 出现在流中作为确认点，在这个位置插入用户消息，而不是在响应到达时插入 [Zeron `mod.rs:1452-1469`]。

### 4.3 `turn/interrupt` → `{threadId, turnId}` → `{}`

- **响应会延迟**：服务端先记下请求，等核心真正中止（TurnAborted）后才回 `{}`，随后发出 `turn/completed`，其中 `status: "interrupted"` [Src `turn_processor.rs:1621-1680`、`bespoke_event_handling.rs:1208-1223`]。
- 错误（`-32600`）：`"no active turn to interrupt"`（轮次已结束，或者服务端还没登记这个轮次，例如在 `turn/started` 之前就中断）、`"expected active turn id {turnId} but found {active}"`、`"thread not found: …"`、`"invalid thread id: …"`。
- 竞态：在 `turn/start` 响应之后、`turn/started` 之前发出的中断，可能得到 "no active turn"，而轮次照常跑完 [Zeron `mod.rs` `TurnRouter.announced` 注释，0.159.3 实测]。TCode 的处理：如果还没收到 `turn/started`，就记下「待中断」，等 `turn/started` 到达时再发送。
- `turnId` 传空串表示中断线程启动过程（startup interrupt），会立即返回 `{}`。TCode 不使用。

### 4.4 `Turn` 对象 [Schema]

```text
Turn { id: string; items: ThreadItem[]; status: "inProgress"|"completed"|"interrupted"|"failed";
       error?: TurnError|null; itemsView?: "notLoaded"|"summary"|"full";
       rootTurnId?: string|null; startedAt?: int64|null; completedAt?: int64|null  // Unix 秒
       durationMs?: int64|null }
TurnError { message: string; codexErrorInfo?: CodexErrorInfo|null; additionalDetails?: string|null;
            misalignment?: {detailedExplanation?, errorType?, reviewTarget?, steer?}|null }
```

- `turn/started` 中 `items` 为空。`turn/completed` 中**不要依赖** `items`：新版源码在正常完成时只放最后一条 agentMessage（`itemsView: "summary"`），其他情况为空（`"notLoaded"`）[Src `bespoke_event_handling.rs:1326-1353`]。渲染一律基于 item 通知。
- `CodexErrorInfo` 有两种形状 [Schema]：
  - 字符串：`contextWindowExceeded`、`sessionBudgetExceeded`、`usageLimitExceeded`、`rateLimitExceeded`、`flexUnavailable`、`serverOverloaded`、`cyberPolicy`、`misalignmentPolicyViolation`、`tooManyDenials`、`internalServerError`、`unauthorized`、`badRequest`、`threadRollbackFailed`、`sandboxError`、`other`
  - 单键对象：`{"httpConnectionFailed":{"httpStatusCode":int|null}}`、`{"responseStreamConnectionFailed":{…}}`、`{"responseStreamDisconnected":{…}}`、`{"responseTooManyFailedAttempts":{…}}`、`{"activeTurnNotSteerable":{"turnKind":"review"|"compact"}}`
  - 解析时两种形状都要支持，遇到未知字符串或未知键时归为 `other`。

---

## 5. 通知（渲染相关）

### 5.1 一览（params 字段均来自 0.162.1 schema）

| method | params | TCode 处理 |
|---|---|---|
| `thread/started` | `{thread}` | 记录线程。 |
| `thread/status/changed` | `{threadId, status: ThreadStatus}` | 可选：`activeFlags` 含 `waitingOnApproval` 时显示「等待审批」。 |
| `turn/started` | `{threadId, turn}` | 确认活动轮次 id，处理待发的中断。 |
| `turn/completed` | `{threadId, turn}` | **唯一的轮次终止帧**，见 §5.6。 |
| `item/started` | `{threadId, turnId, item, startedAtMs}` | 打开内容块或工具调用（§5.3）。 |
| `item/completed` | `{threadId, turnId, item, completedAtMs}` | 以其中的 item 为**权威终态**。 |
| `item/agentMessage/delta` | `{threadId, turnId, itemId, delta}` | 文本增量。 |
| `item/reasoning/summaryPartAdded` | `{threadId, turnId, itemId, summaryIndex}` | 摘要分段，每段都会发，包括 index 0。 |
| `item/reasoning/summaryTextDelta` | `{threadId, turnId, itemId, summaryIndex, delta}` | 思考摘要增量。 |
| `item/reasoning/textDelta` | `{threadId, turnId, itemId, contentIndex, delta}` | 原始推理文本（只有部分模型或配置会产生）。 |
| `item/commandExecution/outputDelta` | `{threadId, turnId, itemId, delta}` | 命令输出片段。每个片段单独做 UTF-8 lossy 解码，多字节字符跨片段时会出现 `U+FFFD`，以 `aggregatedOutput` 为准 [Src `event_mapping.rs:438-446`]。 |
| `item/commandExecution/terminalInteraction` | `{threadId, turnId, itemId, processId, stdin}` | 向交互式终端写入的内容，可显示为输出。 |
| `item/fileChange/patchUpdated` | `{threadId, turnId, itemId, changes: FileUpdateChange[]}` | 更新补丁预览。 |
| `item/fileChange/outputDelta` | — | **已弃用，服务端不再发送** [Schema 描述]。 |
| `item/mcpToolCall/progress` | `{threadId, turnId, itemId, message}` | 工具进度文本。 |
| `item/plan/delta` | `{…, itemId, delta}` | 实验性计划流，不能保证拼接结果等于最终 item。 |
| `turn/plan/updated` | `{threadId, turnId, explanation?, plan: [{step, status: "pending"\|"inProgress"\|"completed"}]}` | 整份清单替换（update_plan 工具）。 |
| `turn/diff/updated` | `{threadId, turnId, diff}` | 本轮累计的 unified diff（每次都是全量）。 |
| `thread/tokenUsage/updated` | `{threadId, turnId, tokenUsage: {total, last, modelContextWindow?}}` | §5.5。 |
| `error` | `{threadId, turnId, willRetry, error: TurnError}` | §5.6。 |
| `warning` | `{threadId?, message}` | 显示为非致命提示。 |
| `guardianWarning` | `{threadId, message}` | 同上。 |
| `configWarning` | `{summary, details?, path?, range?}` | 记日志或显示一次。 |
| `deprecationNotice` | `{summary, details?}` | 记日志。 |
| `model/rerouted` | `{threadId, turnId, fromModel, toModel, reason: "highRiskCyberActivity"}` | 提示用户，并修正显示的模型。 |
| `serverRequest/resolved` | `{threadId, requestId}` | §6.1。 |
| `thread/closed` | `{threadId}` | 线程已卸载。 |
| `account/updated`、`account/rateLimits/updated`、`mcpServer/startupStatus/updated`、`skills/changed`、`thread/name/updated`、`hook/*` 等 | — | TCode 首版忽略。 |

**规则：未知 method 一律忽略（记一条 debug 日志）**，这样 Codex 升级新增通知时不会出错 [Zeron `mod.rs:1758-1760`]。

已不存在的旧通知：`turn/failed`、`turn/aborted`（zeron 仍兼容这两个），以及 `error` 的平铺形状 `params.message`（0.146 之前）。**0.162.1 只用 `turn/completed` 表示终态，`error` 一律是嵌套形状** [Schema `ServerNotification.json` 中没有前两者；Zeron `mod.rs:1644,1694,1742`]。TCode 可以把 `turn/failed`、`turn/aborted` 当作 `turn/completed` 的别名来容错，但不能依赖它们。

### 5.2 Item 生命周期规则

1. 每个 item 先有 `item/started`，再有若干增量，最后是 `item/completed`。增量里的 `itemId` 等于 `item.id` [Docs]。
2. **`item/completed` 中的 item 是权威值**，增量只用于预览。completed 的文本与已收到的增量不一致时，以 completed 为准（TCode `TurnWriter.EndBlockAsync(finalText)` 已经支持）。
3. **有的 item 永远等不到 `item/completed`**：轮次被中断时，正在执行的前台命令会被杀掉，不发 completed；审批被 turnTransition 取消的命令也一样 [Zeron `mod.rs` `prune_at` 注释；Src `bespoke_event_handling.rs:2039`]。所以收到 `turn/completed` 时，客户端必须把本轮仍然打开的块和工具调用全部关闭。
4. **后台命令可能跨轮次**：一个 `commandExecution`（例如 dev server）的 `item/completed` 可能在若干轮之后、进程退出时才到达，而且带的是**原来那一轮**的 `turnId` [Zeron `mod.rs` `open_commands` 注释，0.159.3 实测]。TCode 收到已结束轮次的 item 时，只更新工具状态，不重新打开轮次。
5. 一轮可能有**多条 `agentMessage`**（commentary 前言加 final answer），它们之间的增量没有分隔符。每条 agentMessage 必须是独立的文本块，否则会渲染成「…等待。Beta 的…」这样连在一起的句子 [Zeron `mod.rs:1490-1503`]。
6. 一个 reasoning item 包含多个摘要段（`summaryIndex`），段与段之间也没有分隔符。切换到新段（或新的 reasoning item）时补 `"\n\n"`，否则 Markdown 标题会粘在一起，变成 `**one****two**` [Zeron `normalize.rs:37-95`]。
7. 带审批的命令：`item/started`（`status: "inProgress"`）**先于** `item/commandExecution/requestApproval` 到达 [Docs；Src `bespoke_event_handling.rs:671-790`：`start_command_execution_item` 在 `send_request` 之前]。fileChange 也一样 [Docs]。即便如此，客户端仍要容忍审批请求先到：此时按 `itemId` 先建一个占位的工具调用。
8. `userMessage` item 是我方输入的回显，不要重复渲染。只把 `clientId` 匹配到某个 steer 的那条当作 steer 确认点（§4.2）。

### 5.3 `ThreadItem` 变体（0.162.1，按 `type` 区分）[Schema]

| type | 必填字段 | 主要可选字段 | 说明 |
|---|---|---|---|
| `userMessage` | `id`, `content: UserInput[]` | `clientId` | 用户输入回显。 |
| `agentMessage` | `id`, `text` | `phase: "commentary"\|"partial_answer"\|"final_answer"\|null`、`memoryCitation`、`delivery: "async"`、`questions` | 助手文本。`phase` 可能是 null（表示未知），started 时通常还没有。 |
| `reasoning` | `id` | `summary: string[]`、`content: string[]` | 思考摘要与原始推理。 |
| `plan` | `id`, `text` | — | 实验性。 |
| `commandExecution` | `id`, `command: string`, `cwd`, `commandActions: CommandAction[]`, `status` | `aggregatedOutput`、`exitCode`、`durationMs`、`processId`、`source: "agent"\|"userShell"\|"unifiedExecStartup"\|"unifiedExecInteraction"`、`pluginId`、`scriptPath` | `command` 是 shell 拼接后的**单个字符串**（例如 `"/bin/zsh -lc 'npm test'"`）。`status`：`inProgress\|completed\|failed\|declined`。 |
| `fileChange` | `id`, `changes: FileUpdateChange[]`, `status` | — | `FileUpdateChange = {path, kind, diff}`；**`kind` 是对象**：`{"type":"add"}` \| `{"type":"delete"}` \| `{"type":"update","move_path":string\|null}`（**snake_case**）。`status` 同上。 |
| `mcpToolCall` | `id`, `server`, `tool`, `arguments: any`, `status` | `result: {content: any[], structuredContent?, _meta?}`、`error: {message}`、`durationMs`、`mcpAppUi`、`pluginId` | `status`：`inProgress\|completed\|failed`。 |
| `dynamicToolCall` | `id`, `tool`, `arguments`, `status` | `contentItems`、`success`、`durationMs`、`namespace` | 只在注册了 dynamicTools（实验 API）时出现。 |
| `webSearch` | `id`, `query` | `action: {type:"search",query?,queries?}\|{type:"openPage",url?}\|{type:"findInPage",url?,pattern?}\|{type:"other"}`、`results` | |
| `imageView` | `id`, `path` | — | 模型查看本地图片。 |
| `imageGeneration` | `id`, `result`, `status: string` | `savedPath`、`revisedPrompt`、`failure` | `result` 可能是巨大的 base64 字符串，**不要日志、不要转发**，只使用 `savedPath` [Zeron `normalize.rs:275-276`]。 |
| `collabAgentToolCall` | `id`, `tool`, `status`, `senderThreadId`, `receiverThreadIds`, `agentsStates` | `prompt`、`model`、`reasoningEffort` | 多 Agent 工具调用。 |
| `subAgentActivity` | `id`, `agentPath`, `agentThreadId`, `kind: started\|interacted\|interrupted\|completed` | `model`、`reasoningEffort` | |
| `functionCallOutput` | `id`, `name`, `output` | `namespace` | |
| `hookPrompt` | `id`, `fragments: [{hookRunId, text}]` | — | |
| `sleep` | `id`, `durationMs` | — | |
| `enteredReviewMode` / `exitedReviewMode` | `id`, `review` | — | |
| `contextCompaction` | `id` | — | 上下文已压缩，可显示一条提示。 |

`CommandAction`：`{type:"read",command,name,path}` \| `{type:"listFiles",command,path?}` \| `{type:"search",command,query?,path?}` \| `{type:"unknown",command}`。只用于友好展示，是尽力而为的解析结果。

### 5.4 推理与文本增量

- 推理摘要的顺序：`item/started{reasoning}` → 对每个段依次发 `summaryPartAdded{summaryIndex:i}` 和若干 `summaryTextDelta{summaryIndex:i}` → `item/completed{reasoning, summary:[…]}` [Src `core/src/session/turn.rs:3165-3215`；`codex-api/src/sse/responses.rs:487-493`]。
- 只有请求了摘要（`summary != "none"`，且模型支持）才会出现 `summaryTextDelta`。`item/reasoning/textDelta` 是原始推理，大多数 OpenAI 模型不会发送。两种都收到时，TCode 只渲染摘要。
- 有些 reasoning item 只有 started 和 completed，没有任何增量，completed 时 `summary` 为空。这种情况不要渲染空的思考块。

### 5.5 Usage

```text
ThreadTokenUsage { total: TokenUsageBreakdown; last: TokenUsageBreakdown; modelContextWindow?: int64|null }
TokenUsageBreakdown { inputTokens; cachedInputTokens; outputTokens; reasoningOutputTokens; totalTokens;
                      cacheWriteInputTokens?  /* 0.162.1 中可选 */ }
```

- 每次模型请求（sampling）完成后，就发一次 `thread/tokenUsage/updated`，**一轮里可能有多次**（每次工具调用后都会再请求模型），而且都在 `turn/completed` 之前 [Src `core/src/session/turn.rs:3085-3107`、`bespoke_event_handling.rs:handle_token_count_event`]。
- `last` 是**最近一次请求**的用量，`total` 是**线程累计**用量 [Zeron `normalize.rs:125-141`]。语义同 OpenAI：`inputTokens` **已经包含** `cachedInputTokens`，`outputTokens` **已经包含** `reasoningOutputTokens`，`totalTokens = inputTokens + outputTokens` [Src `protocol/src/protocol.rs:2368-2387,2557-2568`]。
- 整轮用量 = 轮末的 `total` − 轮初的 `total`。轮初基线取本轮之前最后一次收到的 `total`。恢复后的线程还没有基线时，用本轮第一条通知的 `total − last` 作为基线。**不要**把各次 `last` 简单相加：同一个 TokenCount 在压缩或恢复时可能被重复发送。
- 换算成 TCode `Usage`（`Input` 不含缓存）：`Input = max(0, inputTokens − cachedInputTokens − (cacheWriteInputTokens ?? 0))`，`CacheRead = cachedInputTokens`，`CacheWrite = cacheWriteInputTokens ?? 0`，`Output = outputTokens`，`Reasoning = reasoningOutputTokens`。（`cacheWriteInputTokens` 按 OpenAI 语义也视为包含在 `inputTokens` 内。）
- 上下文占用 = `last.totalTokens / modelContextWindow` [Src `protocol.rs:tokens_in_context_window`；Zeron `normalize.rs:143-162`]。

### 5.6 错误与终止

- `error` 通知，`willRetry: true`：瞬时错误，Codex 会自动重试，例如 `"Reconnecting... 1/5"`、`codexErrorInfo = {"responseStreamDisconnected":…}`。**不要结束轮次**，只显示一条状态提示 [Src `bespoke_event_handling.rs:1058-1075`]。
- `error` 通知，`willRetry: false`：致命错误。服务端会把它记为本轮的 last_error，随后 `turn/completed` 的 `status` 为 `"failed"`，`turn.error` 与这条通知的 `error` 相同 [Src `bespoke_event_handling.rs:1034-1057,1509-1538,1624-1640`；Docs]。
- `turn/completed.turn.status` 的映射：
  - `completed` → 正常结束；
  - `interrupted` → 中止（`turn.error` 一般为 null；Guardian 熔断时可能带 `tooManyDenials`）[README「Guardian circuit-breaker errors」]；
  - `failed` → 错误。`turn.error` 可能缺失，这时用最后一条 `willRetry:false` 的 error，再没有就用 `"Codex turn failed"` [Zeron `mod.rs:1576-1581`]。
- 进程在轮次进行中意外退出（stdout EOF）时，按错误结束本轮，错误信息里附上 stderr 尾部 [Zeron `mod.rs:1955-1966`]。

### 5.7 典型顺序（简单文本轮次，详见附录 C-a）

```text
C→ turn/start
S→ (turn/start 响应 与 turn/started 先后不定)
S→ thread/status/changed{active}
S→ item/started{userMessage} → item/completed{userMessage}
S→ item/started{reasoning} → summaryPartAdded{0} → summaryTextDelta* → summaryPartAdded{1} → … → item/completed{reasoning}
S→ item/started{agentMessage} → item/agentMessage/delta* → item/completed{agentMessage}
S→ thread/tokenUsage/updated
S→ turn/completed{status:"completed"}
S→ thread/status/changed{idle}
```

---

## 6. 服务端请求（审批与提问）

### 6.1 通用规则

- 服务端请求的形状是 `{"id": <int>, "method": …, "params": {…}}`，**没有** `emittedAtMs`。TCode 必须回 `{"id": <同一 id>, "result": {…}}` 或 `{"id", "error"}`。
- **每个服务端请求都必须得到回应**，否则 Agent 会一直阻塞（线程状态停在 `active` + `waitingOnApproval`）。界面关闭或会话销毁时，TCode 要对所有未决请求回 decline 或 cancel。
- **`serverRequest/resolved {threadId, requestId}`**：请求已被回答**或被清除**时发出 [Src `thread_lifecycle.rs:1019-1041`、`bespoke_event_handling.rs:1651,1738,1814,1940,1987`]。TCode 收到后立即关闭对应的审批 UI（例如另一个客户端已经回答，或者轮次已经结束）。
- **轮次结束（完成或中断）时，服务端会清除本线程所有未决请求**：内部给回调一个带 `data.reason = "turnTransition"` 的错误，然后发出 `serverRequest/resolved`。这时**客户端不会收到任何错误消息**，只能靠 `serverRequest/resolved`（可能**晚于** `turn/completed` 到达）得知请求已失效。之后再回复会被忽略 [Src `outgoing_message.rs:221-236,604-637`、`server_request_error.rs`]。
- 客户端回 error，或者 `result` 无法反序列化时：command 和 fileChange 审批都按**拒绝**处理（`denied("approval request failed")`，命令 item 的 status 为 `failed`）；`requestUserInput` 按空答案处理 [Src `bespoke_event_handling.rs:1642-1680,1931-2060`]。所以「回 error」不能代替 cancel。
- `params.threadId` 和 `params.turnId` 都会给出。审批 UI 用 `itemId` 关联到 `item/started` 创建的工具调用。

### 6.2 `item/commandExecution/requestApproval`

params（`CommandExecutionRequestApprovalParams`）。必填：`threadId`、`turnId`、`itemId`、`startedAtMs` [Schema]。

| 字段 | 说明 |
|---|---|
| `itemId` | 等于 `commandExecution` item 的 id（也就是 call_id）。 |
| `approvalId` | 普通 shell 审批时为 null。zsh-exec-bridge 子命令审批时不为 null，这时同一个 `itemId` 下可能有多个审批。 |
| `kind` | `"command"` \| `"writeStdin"`（向已有终端写入），缺省为 command。 |
| `command` / `cwd` / `commandActions` | 待执行的命令。网络审批时三者为 null。 |
| `reason` | 模型给出的理由，例如申请网络访问。 |
| `networkApprovalContext` | `{host, protocol: "http"\|"https"\|"socks5Tcp"\|"socks5Udp"}`，托管网络审批时出现。 |
| `proposedExecpolicyAmendment` | `string[]`：建议的命令前缀规则。 |
| `proposedNetworkPolicyAmendments` | `[{host, action: "allow"\|"deny"}]`。 |
| `environmentId` | 执行环境。 |
| ~~`availableDecisions`~~ | 实验字段，experimentalApi=false 时被剥离 [Src `transport.rs:181-200`；Schema-exp]。 |

response `{decision: CommandExecutionApprovalDecision}`：

| decision | 效果 |
|---|---|
| `"accept"` | 执行这一次。 |
| `"acceptForSession"` | 执行，并且本会话内相同的审批键不再询问。 |
| `{"acceptWithExecpolicyAmendment":{"execpolicy_amendment":["npm","test"]}}` | 执行，并持久化命令前缀规则（**键名是 snake_case**）。 |
| `{"applyNetworkPolicyAmendment":{"network_policy_amendment":{"host":"…","action":"allow"\|"deny"}}}` | 持久化网络规则。`deny` 时命令 item 以 `declined` 结束。 |
| `"decline"` | 拒绝，**Agent 继续本轮**（模型会收到「rejected by user」）。命令 item 以 `status:"declined"` 结束。 |
| `"cancel"` | 拒绝，并且**立即中断本轮**（ReviewDecision::Abort）。命令 item 以 `declined` 结束，之后 `turn/completed` 的 status 为 `interrupted`。 |

[Schema；Src `bespoke_event_handling.rs:1973-2093`]

### 6.3 `item/fileChange/requestApproval`

params：`{threadId, turnId, itemId, startedAtMs, reason?, grantRoot?}` [Schema]。

- **params 中没有 `changes`**。要展示的补丁内容来自此前 `item/started` 和 `item/fileChange/patchUpdated` 中 `itemId` 相同的 `fileChange` item [Schema；Zeron 的 `approval_question` 试图读取 `params.changes`，实际总是空的]。
- `grantRoot`（UNSTABLE）表示 Agent 请求对该根目录在本会话内放开写权限。

response `{decision: "accept"|"acceptForSession"|"decline"|"cancel"}`，语义同 §6.2。decline 后 fileChange item 以 `status:"declined"` 结束 [Schema；Src `bespoke_event_handling.rs:1921-1929`]。

### 6.4 `item/permissions/requestApproval`

params：`{threadId, turnId, itemId, startedAtMs, cwd, permissions: {fileSystem?, network?}, reason?, environmentId?}`。

- `fileSystem = {read?: string[], write?: string[], entries?: [{path: {type:"path",path}|{type:"glob_pattern",pattern}|{type:"special",value}, access: "read"|"write"|"deny"}], globScanMaxDepth?}`
- `network = {enabled?: bool}`
- [Schema]

response `{permissions: GrantedPermissionProfile, scope?: "turn"|"session", strictAutoReview?: bool|null}`：

- 允许：把请求中的 `permissions` 原样（或者一个子集）放进 `permissions`，`scope` 设为 `"turn"`（本轮）或 `"session"`。
- 拒绝：`{"permissions":{}}`（空授权）。
- `strictAutoReview: true` 只能和 `scope:"turn"` 一起用，否则服务端会把授权降为空 [Src `bespoke_event_handling.rs:1890-1918`]。

### 6.5 `item/tool/requestUserInput`（实验性质，但在 stable schema 中）

params：`{threadId, turnId, itemId, isBlocking: bool, questions: ToolRequestUserInputQuestion[], autoResolutionMs?(已弃用)}`。

`ToolRequestUserInputQuestion = {id, header, question, isOther?, isSecret?, options?: [{label, description}]|null}`。

response：`{answers: {<question.id>: {answers: string[]}}}`。答案的 key 用**线上的 question id**，选项题回填 `label` [Schema；Zeron `mod.rs:2066-2090`]。用户取消时回 `{"answers":{}}`。

### 6.6 `mcpServer/elicitation/request`

params：`{threadId, serverName, turnId?}`，再加上下面几种 mode 之一 [Schema]：

- `{"mode":"form","message","requestedSchema": McpElicitationSchema, "_meta"?}`
- `{"mode":"url","message","url","elicitationId","_meta"?}`
- `{"mode":"openai/form"|"openaiForm",…}`（扩展，未声明对应能力时不会出现）

response：`{action: "accept"|"decline"|"cancel", content?: any, _meta?: any}`。accept 时 `content` 是符合 `requestedSchema` 的对象。TCode 首版：url 模式在浏览器中打开链接后回 accept；form 模式用 §6.8 的通用确认框，accept 时回 `content: {}`；其他情况回 decline。

### 6.7 其他服务端请求

| method | 何时出现 | TCode 回应 |
|---|---|---|
| `item/tool/call`（DynamicToolCall） | 只有通过实验 API 注册了 `dynamicTools` 才会出现 | `{"success":false,"contentItems":[{"type":"inputText","text":"TCode does not provide dynamic tools"}]}` |
| `account/chatgptAuthTokens/refresh` | 只在 `chatgptAuthTokens` 外部认证模式下出现 | error `-32601` |
| `attestation/generate` | 只有声明了 `requestAttestation` 才会出现 | error `-32601` |
| `applyPatchApproval`、`execCommandApproval` | v1 旧 API，v2 线程不会发送 | error `-32601` |
| 未知 method | — | error `-32601 "unsupported method: <m>"` [Zeron `mod.rs:2099`] |

### 6.8 映射到 TCode 审批（`IRuntimeHost.RequestApprovalAsync`）

`RuntimeApprovalRequest` 的构造：

| Codex 请求 | ToolCallId | ToolName | Kind | Title | Reason | ArgumentsJson | CanAllowForSession |
|---|---|---|---|---|---|---|---|
| commandExecution（kind=command） | `itemId` | `"shell"` | `Command` | `command`（网络审批时为 `"联网：<host>"`） | `reason` | `{"command","cwd","commandActions"}` | true |
| commandExecution（kind=writeStdin） | `itemId` | `"shell"` | `Command` | `"向终端写入"` | `reason` | 同上 | true |
| fileChange | `itemId` | `"apply_patch"` | `FileChange` | 来自 item 的路径列表 | `reason` | `{"changes":[…]}`（从 item 中取） | true |
| permissions | `itemId` | `"request_permissions"` | `Tool` | 权限摘要 | `reason` | `params.permissions` | true |
| mcpServer/elicitation | `"elicitation:"+requestId` | `serverName` | `Tool` | `message` | — | params | false |

`ApprovalOutcome` → 线上响应：

| TCode | commandExecution | fileChange | permissions | elicitation |
|---|---|---|---|---|
| AllowOnce | `"accept"` | `"accept"` | `{permissions: <请求值>, scope:"turn"}` | `{action:"accept", content:{}}` |
| AllowForSession | `"acceptForSession"` | `"acceptForSession"` | `{permissions: <请求值>, scope:"session"}` | 同 AllowOnce |
| Deny | `"decline"` | `"decline"` | `{permissions:{}}` | `{action:"decline"}` |
| Cancel | `"cancel"` | `"cancel"` | `{permissions:{}}`，随后发送 `turn/interrupt` | `{action:"cancel"}` |

- `ApprovalDecision.Message`（拒绝理由）**无法传给 Codex**，协议里没有这个字段，只能在 TCode 本地显示。
- `TurnOptions.AutoApprove = true` 时，TCode 不再发审批请求，而是把 `approvalPolicy` 设为 `"never"`（§7.3），从源头上不产生审批。偶尔还是收到审批请求时，自动回 `accept` [Zeron `mod.rs:2101-2104`；PySDK 默认 handler]。
- 用户中止轮次（取消令牌）时，**不要**逐个回答未决审批，直接发 `turn/interrupt`，服务端会自己清除这些请求（§6.1）。宿主侧 `RequestApprovalAsync` 的取消令牌要同时触发，以关闭 UI。

---

## 7. 策略与枚举

### 7.1 `AskForApproval` [Schema]

| 值 | 含义 |
|---|---|
| `"untrusted"` | 只有「受信任」的只读命令自动执行，其他命令都要审批。 |
| `"on-request"` | 由模型决定何时申请提权（越出沙箱、联网等）。这是 Codex 的默认交互模式。 |
| `"never"` | 从不询问。越出沙箱的操作直接失败，失败结果回传给模型。 |
| `{"granular":{"sandbox_approval":bool,"rules":bool,"mcp_elicitations":bool,"request_permissions"?:bool,"skill_approval"?:bool}}` | 细粒度开关（键名为 **snake_case**）。TCode 不使用。 |

注意：0.162.1 已经**没有** `"on-failure"`。

### 7.2 沙箱：`SandboxMode`（线程级字符串）与 `SandboxPolicy`（轮次级对象）[Schema]

| SandboxMode（`thread/start.sandbox`） | SandboxPolicy（`turn/start.sandboxPolicy` 和各响应的 `sandbox`） |
|---|---|
| `"read-only"` | `{"type":"readOnly","networkAccess"?:bool}` |
| `"workspace-write"` | `{"type":"workspaceWrite","writableRoots"?:string[],"networkAccess"?:bool,"excludeTmpdirEnvVar"?:bool,"excludeSlashTmp"?:bool}` |
| `"danger-full-access"` | `{"type":"dangerFullAccess"}` |
| — | `{"type":"externalSandbox","networkAccess"?:"restricted"\|"enabled"}` |

不传 `sandbox` 时使用 config.toml 的 `sandbox_mode`。没有配置时：项目已信任则为 `workspace-write`，否则为 `read-only` [PySDK `_sandbox.py` 注释]。

### 7.3 TCode 预设

| TCode 档位 | `approvalPolicy` | `sandbox`（thread）/ `sandboxPolicy`（turn） |
|---|---|---|
| 只读 | `on-request` | `read-only` / `{"type":"readOnly"}` |
| 默认（Agent） | `on-request` | `workspace-write` / `{"type":"workspaceWrite"}` |
| 自动批准（AutoApprove） | `never` | `workspace-write` / `{"type":"workspaceWrite","networkAccess":true}` |
| 完全访问 | `never` | `danger-full-access` / `{"type":"dangerFullAccess"}` |

`thread/start` 时发送一次 `approvalPolicy` 和 `sandbox`。之后**每次 `turn/start`** 都按当前的 `TurnOptions` 发送 `approvalPolicy` 和 `sandboxPolicy`，因为这些覆盖是 sticky 的，显式发送可以避免沿用上一轮的残留设置 [Zeron `mod.rs:1262-1290`]。

### 7.4 `ReasoningEffort`

0.162.1 中它是**开放字符串**（`minLength: 1`），**不是枚举** [Schema `ReasoningEffort`]。每个模型支持的取值以 `model/list` 的 `supportedReasoningEfforts[].reasoningEffort` 为准。实测 0.162.1 返回 `low, medium, high, xhigh, max, ultra` [Probe]。

TCode 的 `ThinkingLevel` → effort：Minimal/Low → `low`，Medium → `medium`，High → `high`，XHigh → `xhigh`，Max → `max`。目标值不在模型支持列表中时，取支持列表里**最接近且不高于**目标的那一档；列表为空时不发送 `effort`。`Off` 映射为 `"none"`（仅当模型声明支持时），否则取支持列表的最低档，同时把 `summary` 设为 `"none"` [Zeron `catalog.rs:to_effort`：Codex 在启用默认工具时拒绝 `minimal`]。

### 7.5 `ReasoningSummary`

`"auto"|"concise"|"detailed"|"none"` [Schema]。

---

## 8. 模型与账号

### 8.1 `model/list` → `{cursor?, limit?, includeHidden?}` → `{data: Model[], nextCursor?}`

分页：带上 `cursor` 循环请求，直到 `nextCursor` 为 null 或空串。还要防止服务端返回重复的 cursor 导致死循环 [Zeron `mod.rs:300-327`]。

`Model` 的必填字段 [Schema；Probe]：

| 字段 | 说明 |
|---|---|
| `id` | 目录 id。 |
| `model` | **传给 `thread/start.model` 和 `turn/start.model` 的值**。实测与 `id` 相同，但以 `model` 为准 [Zeron `parse_model_list_page`]。 |
| `displayName` | 显示名，例如 `"GPT-6.1-Sol"`。 |
| `description` | 描述。 |
| `hidden` | 是否不出现在默认选择器中。TCode 过滤掉 `hidden:true` 的模型（请求时 `includeHidden:false`）。 |
| `isDefault` | 默认模型，只有一个为 true。TCode 把它排到列表最前。 |
| `supportedReasoningEfforts` | `[{reasoningEffort, description}]`。 |
| `defaultReasoningEffort` | string。 |

可选字段：`inputModalities`（`"text"|"image"|"audio"`，**缺失时视为 `["text","image"]`** [Docs]）、`serviceTiers: [{id, name, description}]`（实测有 `{"id":"priority","name":"Fast"}`、`{"id":"ultrafast",…}`；TCode 的 FastMode 对应 `serviceTier:"priority"`，前提是该模型提供这一档）、`defaultServiceTier`、`additionalSpeedTiers`（已弃用）、`upgrade` / `upgradeInfo`（推荐升级到的模型）、`availabilityNux`、`supportsPersonality`（已弃用，恒为 false）、`modelSpecialty`、`multiAgentVersion`、`availableAccessPrograms`。

- 托管的模型提供方不满足当前要求时，`model/list` 返回 `-32600`，提示需要重启 Codex [README「Model catalog provider requirements」]。
- `ProbeAsync` 的流程：启动一个短生命周期的 app-server，依次调用 `initialize`、`initialized`、`model/list`（翻完所有页）、`account/read`，然后关闭 stdin。整个过程**不会**启动模型轮次 [Zeron `discover_models`；Probe]。

### 8.2 `account/read` → `{refreshToken?: bool}` → `{account: Account|null, requiresOpenaiAuth: bool}`

`Account` 的三种形状 [Schema]：
- `{"type":"apiKey"}`
- `{"type":"chatgpt","email":string|null,"planType":PlanType}`
- `{"type":"amazonBedrock","usesCodexManagedCredentials"?:bool}`

判断规则：
- `requiresOpenaiAuth == false`：当前模型提供方是**不需要 OpenAI 认证的自定义提供方**，这时 `account` 为 null 也算正常 [Src `account_processor.rs:1145-1155`；Probe：本机返回 `{"account":null,"requiresOpenaiAuth":false}`]。
- `requiresOpenaiAuth == true && account == null`：未登录。`RuntimeStatus` 设为 Error，提示用户在终端执行 `codex login`。TCode 不代为登录。

其他说明：
- `AuthMethod` 显示 `"ChatGPT (<planType>)"`、`"API key"` 或 `"Bedrock"`。
- 实测响应里还有一个 schema 之外的 `workspaceRouting: null` 字段 [Probe]，忽略。
- 第一次调用 `account/read` 会触发一条 `account/updated` 通知 [Zeron `mod.rs:1149-1158`]，忽略。
- `refreshToken` 固定传 false。

---

## 9. JSON-RPC 错误码 [Src `app-server/src/error_code.rs`；PySDK `errors.py:86-109`；Probe]

| code | 含义 | 典型 message | TCode 处理 |
|---|---|---|---|
| `-32600` | 无效请求。**最常见**：未初始化、重复初始化、未知 method、线程不存在、轮次状态不符、实验字段缺少能力、服务端正在关闭（`data.reason="serverShuttingDown"`） | `"Not initialized"`、`"Invalid request: unknown variant …"`、`"thread not found: …"`、`"no active turn to steer"` | 按场景处理，不自动重试（serverShuttingDown 除外：重启进程）。 |
| `-32601` | 方法不存在（少数显式拒绝的路径） | — | 视为不支持。 |
| `-32602` | 参数无效 | — | 属于实现缺陷，记录日志。 |
| `-32603` | 内部错误 | `"failed to interrupt turn: …"` | 列表类请求可以重试，其他情况作为错误返回给调用方。 |
| `-32001` | 过载 | `"Server overloaded; retry later."` | 指数退避重试（§1.4）。 |
| `-32700` | 解析错误 | — | 服务端**不会**返回它，坏行直接丢弃（§1.2）。 |

服务端内部取消审批时使用的错误（code 为 -32603，`data.reason="turnTransition"`）不会发给客户端（§6.1）。

---

## 10. 取消、中断与关闭

### 10.1 轮次取消（`RunTurnAsync` 的取消令牌）

1. 如果已经收到 `turn/started`，发送 `turn/interrupt {threadId, turnId}`；否则设置「待中断」标志，收到 `turn/started` 后再发送（§4.3）。
2. 继续读取消息流，直到收到本轮的 `turn/completed`（预期 `status:"interrupted"`）。期间 `turn/interrupt` 的响应 `{}` 会先于或紧挨着 `turn/completed` 到达。
3. 等待上限 **10 s**。超时则视为进程卡死：杀掉进程，本轮按 Aborted 结束，下一轮通过 `thread/resume` 恢复 [Zeron `interrupt_grace` / `kill_grace`]。
4. 本轮仍然打开的块和工具调用一律关闭，工具结果标为 isError，文本为「已中断」。未决审批由服务端清除，TCode 只需要关闭 UI（§6.1）。

### 10.2 审批中的 Cancel

用户在审批框里选择 Cancel 时，回复 `decision:"cancel"` 即可，服务端会中断本轮。不需要再发 `turn/interrupt`（permissions 例外，见 §6.8）。

### 10.3 进程关闭（`IRuntimeSession.DisposeAsync`）

1. 如果有活动轮次，先按 §10.1 中断（等待上限 3 s）。
2. 关闭 stdin（EOF），等待退出，上限 5 s。
3. Unix：发送 SIGTERM，再等 2 s。Windows：直接进入下一步。
4. 杀掉进程树。注意：Agent 启动的后台命令（例如 dev server）是 app-server 的子孙进程，也要一并清理 [Zeron `torn_down` / `descendants`]。

---

## 11. 映射到 TCode 事件

TCode 的 `AgentStarted` 和 `AgentEnded` 由 `ExternalAgent` 发出。`MessageStarted` / `MessageEnded` 由 `TurnWriter` 在打开或关闭「草稿助手消息」时自动产生。驱动只调用 `TurnWriter` 的方法。

| Codex | TurnWriter 调用 | 产生的 SessionEvent |
|---|---|---|
| `turn/start` 发出（`RunTurnAsync` 开始） | — | `agentStarted`（ExternalAgent）、用户消息的 `messageStarted` |
| `turn/started` | 记录 turnId | — |
| `item/started{userMessage}`，`clientId` 匹配某个 steer | `UserMessageAsync(steer)` | `messageEnded`（草稿）、`messageStarted(user)` |
| `item/started{reasoning}` | 延迟到第一个增量时再 `BeginBlockAsync(Thinking)` | `messageStarted(assistant)`（如果还没有草稿） |
| `summaryPartAdded{i>0}` / `summaryTextDelta` / `textDelta` | `AppendAsync(block, 分隔符 + delta)` | `thinkingDelta` |
| `item/completed{reasoning}` | `EndBlockAsync(block, string.Join("\n\n", summary))`（summary 为空且从没打开过块时跳过） | `thinkingDelta`（补差） |
| `item/started{agentMessage}` | `BeginBlockAsync(Text)` | `messageStarted`（如果需要） |
| `item/agentMessage/delta` | `AppendAsync` | `textDelta` |
| `item/completed{agentMessage}` | `EndBlockAsync(block, item.text)` | `textDelta`（补差） |
| `item/started{commandExecution}` | `ToolCallAsync(item.id, "shell", {"command","cwd","commandActions"})` | `toolCallStarted` + `toolCallDelta` + `toolCallEnded` |
| 命令开始执行（下列情况中最先发生的：approvalPolicy 为 never 时的 item/started、我方回复 accept 类决定、第一个 outputDelta、item/completed） | `ToolStartedAsync` | `toolExecutionStarted` |
| `item/commandExecution/outputDelta` / `terminalInteraction` | `ToolProgressAsync(id, "shell", delta)` | （进度） |
| `item/completed{commandExecution}` | `ToolCompletedAsync(id, "shell", aggregatedOutput ?? 已累积的输出, isError: status≠completed ‖ exitCode≠0, durationMs)`；declined 时结果文本为「用户拒绝执行」 | `messageEnded`（草稿）、`toolExecutionEnded`、toolResult 消息 |
| `item/started{fileChange}` | `ToolCallAsync(id, "apply_patch", {"changes"})` | toolCall* |
| `item/fileChange/patchUpdated` | `ToolProgressAsync(id, "apply_patch", diff 摘要)` | （进度） |
| `item/completed{fileChange}` | `ToolStartedAsync`（如果还没发）+ `ToolCompletedAsync`（completed → 「已修改 N 个文件」+ diff；declined → isError，「用户拒绝修改」；failed → isError） | `toolExecutionStarted/Ended` |
| `item/started{mcpToolCall}` | `ToolCallAsync(id, $"mcp__{server}__{tool}", arguments)` + `ToolStartedAsync` | toolCall*、`toolExecutionStarted` |
| `item/mcpToolCall/progress` | `ToolProgressAsync(message)` | — |
| `item/completed{mcpToolCall}` | `ToolCompletedAsync(text = result.content 中所有 text 拼接，或 error.message；isError = status≠completed)` | `toolExecutionEnded` |
| `item/started/completed{webSearch}` | `ToolCallAsync(id,"web_search",{"query"})`，completed 时 `ToolCompletedAsync(query/action 摘要)` | toolCall*、toolExecution* |
| `item/started/completed{imageView, dynamicToolCall, collabAgentToolCall, imageGeneration}` | 与 webSearch 相同，作为通用工具展示（imageGeneration 只用 `savedPath`） | 同上 |
| `item/completed{contextCompaction}` | `TextAsync("（上下文已压缩）")`，或者只做界面提示 | `textDelta` |
| `turn/plan/updated` | 首版不映射（可选：显示为会话级计划面板） | — |
| `turn/diff/updated` | 保存为本轮 diff（供「查看改动」使用） | — |
| `thread/tokenUsage/updated` | 按 §5.5 计算本轮增量，`SetUsage(usage)` | （随 `messageEnded` 携带） |
| `error{willRetry:true}` | 只显示状态提示 | — |
| `error{willRetry:false}` | 记录 lastError | — |
| `turn/completed` | 先关闭所有未结束的工具调用（`ToolCompletedAsync(isError:true, "已中断")`），然后调用 `CompleteAsync(stop, errorMessage)` | `messageEnded`（最后一条带用量和 stopReason）；`agentEnded` 由 ExternalAgent 在队列空时发出 |

`turn.status` → `StopReason`：`completed` → `Stop`；`interrupted` → `Aborted`；`failed` → `Error`（errorMessage = `turn.error.message`，没有时用 lastError，再没有时用 `"Codex turn failed"`）；`contextWindowExceeded` 也按 `Error` 处理（TCode 没有单独的状态）。Codex 的轮次不会以 `ToolUse` 结束，因为工具都在 Codex 内部执行。

`RuntimeCapabilities`：`SupportsSteering = true`、`SupportsResume = true`、`SupportsModelSwitch = true`（每次 `turn/start` 都带 model）、`StreamsToolArguments = false`（工具参数在 item/started 时一次给全）。

---

## 12. 实现必须满足（Checklist）

**传输**
1. [ ] 用 `codex app-server` 启动（stdio）。stdin、stdout、stderr 全部重定向，stderr 持续读取，保留尾部 200 行。环境变量设 `LOG_FORMAT=json`。
2. [ ] 写入：每条消息序列化为单行 JSON 后加 `\n`，**不带 `jsonrpc` 字段**，写入要串行化（加锁）。没有 params 的通知省略 `params`。
3. [ ] 读取：按 `\n` 分帧，不设行长上限，UTF-8 解码。遇到无法解析的行时记日志并跳过，不中断会话。
4. [ ] 按形状分派消息：有 `method` 和 `id` 是服务端请求；只有 `method` 是通知；有 `id` 和 `result`/`error` 是响应。请求 id 同时支持数字和字符串。忽略信封上的 `emittedAtMs`。
5. [ ] 我方请求用自增整数 id，按 id 匹配响应（响应**可能乱序**）。每个请求都设超时（默认 30 s；`turn/interrupt` 10 s）。
6. [ ] `-32001` 用指数退避重试（最多 3 次）。`-32600` 且 `data.reason=="serverShuttingDown"` 时重启进程。
7. [ ] stdout EOF 或进程退出：所有未完成的请求失败，活动轮次以 Error 结束，错误信息附上 stderr 尾部。

**握手与线程**
8. [ ] `initialize`：`clientInfo {name:"tcode", title:"TCode", version}`，`capabilities {experimentalApi:false, optOutNotificationMethods:[…]}`；然后发 `{"method":"initialized"}`。从 `serverInfo.version` 或 `userAgent` 中解析出 Codex 版本，并且只在 ≥ 0.162 时启用本规格。
9. [ ] 新会话发 `thread/start {cwd(绝对路径), model, approvalPolicy, sandbox, developerInstructions?, config?}`；恢复会话发 `thread/resume {threadId, excludeTurns:true, …}`，失败时回退到 `thread/start` 并上报新 id。NativeSessionId 取 `thread.id`。
10. [ ] 用 `ThreadStartResponse` 的 `model` 和 `reasoningEffort` 修正显示。`sandbox` 字段按 `SandboxPolicy` 对象解析。
11. [ ] 按 `threadId` 过滤通知，其他线程（包括子 Agent 线程）的 `turn/*` 不能影响当前轮次。

**轮次**
12. [ ] `turn/start` 每次都带 `threadId`、`input`、`model`、`effort`（在模型支持列表内）、`summary:"auto"`、`approvalPolicy`、`sandboxPolicy`（对象）。FastMode 时加 `serviceTier:"priority"`，关闭时发显式 `null`。
13. [ ] 活动轮次 id 同时接受 `turn/start` 响应和 `turn/started`，以先到的为准。已经结束的 turn id 不能再变回活动状态。
14. [ ] 轮次只以本线程、本 turnId 的 `turn/completed` 结束。遇到旧的或重复的 completed 时忽略。
15. [ ] steer 使用 `turn/steer {threadId, expectedTurnId, clientUserMessageId, input}`，失败时返回 false。在 `clientId` 匹配的 `userMessage` item 处插入用户消息。
16. [ ] 中断：收到 `turn/started` 之后才发 `turn/interrupt`，之前则延后发送。发出后等待 `turn/completed`，上限 10 s，超时杀进程。

**渲染**
17. [ ] 以 `item/completed` 为权威值，用增量做预览。每个 agentMessage 和 reasoning item 都是独立的块。摘要切换段落时补 `\n\n`。
18. [ ] 支持缺少 `item/completed` 的 item：`turn/completed` 时关闭所有打开的块和工具调用。轮次结束后到达的 `commandExecution` completed 只更新工具状态。
19. [ ] `ThreadItem` 按 `type` 分派，未知 type 忽略。`fileChange.changes[].kind` 按对象解析（`move_path` 为 snake_case）。`commandExecution.command` 是字符串。
20. [ ] 命令输出：用 `outputDelta` 做实时预览，结果以 `aggregatedOutput` 为准，失败条件是 `exitCode≠0` 或 `status≠completed`。
21. [ ] 不记录也不转发 `imageGeneration.result`。
22. [ ] 用量按 §5.5 计算：整轮用量等于 `total` 的差值，`Input` 要扣除缓存部分，`Reasoning = reasoningOutputTokens`。上下文占用 = `last.totalTokens / modelContextWindow`。
23. [ ] `error` 通知：`willRetry:true` 只做提示；`willRetry:false` 记为错误信息。`CodexErrorInfo` 同时支持字符串和单键对象两种形状。
24. [ ] 未知通知、未知字段、实验字段一律忽略，不能抛异常。

**审批**
25. [ ] 每个服务端请求都必须回应：已知审批类按 §6.8 映射回复，未知 method 回 `-32601`。会话销毁时对所有未决请求回 decline 或 cancel。
26. [ ] fileChange 审批的补丁内容从同一 `itemId` 的 item 中取，因为 params 里没有 `changes`。
27. [ ] 收到 `serverRequest/resolved` 就关闭对应的审批 UI，之后不再回复；这条通知可能晚于 `turn/completed` 到达。
28. [ ] 不要用 error 响应代替拒绝：明确拒绝回 `decline`，中止回 `cancel`。
29. [ ] AutoApprove 时用 `approvalPolicy:"never"`。偶尔仍收到审批请求时，自动回 `accept`。

**探测与关闭**
30. [ ] `ProbeAsync` 只调用 `initialize`、`model/list`（翻完所有页，过滤 hidden，默认模型排最前）、`account/read`，不调用 `thread/start`。
31. [ ] 账号状态：`requiresOpenaiAuth && account==null` 时视为未登录。
32. [ ] 关闭顺序：中断活动轮次 → 关 stdin → 等 5 s → SIGTERM（仅 Unix）→ 等 2 s → 杀进程树。
33. [ ] 附录 C 的全部夹具作为单元测试：回放 `S>` 行，断言 TCode 写出的 `C>` 行（JSON 语义相等，忽略 key 顺序）和产生的事件序列。

---

## 附录 A：与常见认知不同的地方（以及版本差异）

1. **不是标准 JSON-RPC 2.0**：线上没有 `"jsonrpc":"2.0"`；未知 method 返回 `-32600` 而不是 `-32601`；坏 JSON 行被静默丢弃，不回 `-32700`。
2. **服务端通知的信封多了 `emittedAtMs`**，schema 中没有。
3. **响应会乱序**；`turn/start` 的响应与 `turn/started` 没有先后保证。
4. **`userAgent` 以我方的 `clientInfo.name` 开头**，不是 `codex/…`。
5. **同一个概念有两种线上形状**：线程级 `sandbox` 是 kebab-case 字符串，轮次级 `sandboxPolicy` 和响应里的 `sandbox` 是 camelCase 的 `type` 对象。
6. **camelCase 协议里混着 snake_case**：`UserInput.text_elements`、`PatchChangeKind.move_path`、`acceptWithExecpolicyAmendment.execpolicy_amendment`、`applyNetworkPolicyAmendment.network_policy_amendment`、`AskForApproval.granular.*`、`ThreadSortKey` 的取值（`created_at`…）、`ApprovalsReviewer` 的取值（`auto_review`…）。
7. **`ReasoningEffort` 是开放字符串**，不是枚举。0.162.1 实测有 `max`、`ultra` 等新档位。
8. **fileChange 审批的 params 不含 `changes`**，补丁要从 item 中取。zeron 读取 `params.changes` 的代码在 0.162.1 上取到的永远是空。
9. **`turn/interrupt` 的响应会延迟**到核心真正中止之后才发。
10. **轮次结束时，未决审批被服务端静默清除**，客户端只会收到 `serverRequest/resolved`，没有错误。
11. **对审批回 error 等同于拒绝**（命令 item 标记为 failed），不是取消。
12. **中断或取消时，部分 item 没有 `item/completed`**；后台命令的 completed 可能在若干轮之后才到。
13. `turn/failed`、`turn/aborted` 以及平铺的 `error.message` 在 0.162.1 中已经不存在，终止只看 `turn/completed`。
14. 在 0.162.1 中，`item/tool/requestUserInput` 虽然标注为 EXPERIMENTAL，却属于 stable schema；而 `availableDecisions` 是实验字段，experimentalApi=false 时会被剥离（官方文档却把它列为常规字段）。
15. 不显式传 `summary:"auto"` 时，大多数配置下不会有任何推理增量，界面会「静默思考」几分钟。
16. 实测的 `account/read` 响应里有 schema 之外的 `workspaceRouting` 字段；新版源码的 `Thread` 也多了 `environments`、`extra` 等字段。所以必须宽松解析。
17. `-32001` 过载错误不只出现在 WebSocket 模式，stdio 在入站队列（容量 128）满时也会返回。

## 附录 B：未覆盖或暂不使用的方法

登录流程（`account/login/*`、`account/logout`）、`thread/compact/start`、`review/start`、`thread/rollback|revert`、`thread/turns/list`、`thread/items/list`、`thread/archive|delete|name/set|metadata/update`、`thread/goal/*`、`thread/realtime/*`、`command/exec*`、`fs/*`、`skills/*`、`plugin/*`、`app/*`、`marketplace/*`、`mcpServer/*`（状态与 OAuth）、`config/*`、`experimentalFeature/*`、`fuzzyFileSearch`、`feedback/upload`、`windowsSandbox/*`、`hooks/list`、dynamicTools 及其他实验 API。需要时以 0.162.1 schema 中对应的 `*Params.json` / `*Response.json` 为准，并重新与 [Schema-exp] 对比。

---

## 附录 C：测试夹具

格式：每行一条消息。`C> ` 表示 TCode → app-server（写入 stdin），`S> ` 表示 app-server → TCode（从 stdout 读出）。前缀之后是这条消息的**精确线上 JSON**（单行、无 `jsonrpc` 字段；通知带 `emittedAtMs`）。测试回放 `S>` 行，断言 TCode 写出的 `C>` 行在 JSON 语义上相等。

所有夹具都用 0.162.1 schema（stable，加上「对象不允许额外属性」的严格模式）逐行校验过：请求和通知的 `params`、响应的 `result` 都对照对应类型；`initialize` 时 `experimentalApi:false`。夹具中的 id、时间戳、路径均为虚构；`emittedAtMs` 单调递增，`startedAtMs` 和 `completedAtMs` 与之一致。服务端请求 id 按进程从 0 开始。

需要注意：
- (b) 中 `turn/started` **先于** `turn/start` 的响应到达，用来测试 §1.3 的竞态。
- (d) 中命令 item **没有** `item/completed`，审批请求 `id:0` 没有被回复，而是由 `serverRequest/resolved` 在 `turn/completed` **之后**清除。驱动必须在 `turn/completed` 时关闭这个工具调用，并且之后不再回复请求 0。
- (e) 包含一次可重试错误、一次最终失败（对象形状的 `codexErrorInfo`），以及同一线程上第二轮的立即失败（字符串形状的 `codexErrorInfo`）。
- (f) 是请求级错误与探测流程（未初始化、重复初始化、线程不存在、`model/list` 分页、已登录账号）。

### C-a 握手 + thread/start + 带推理摘要和用量的文本轮次（`a-text-turn.jsonl`）

```text
C> {"id":1,"method":"initialize","params":{"clientInfo":{"name":"tcode","title":"TCode","version":"0.1.0"},"capabilities":{"experimentalApi":false,"optOutNotificationMethods":["remoteControl/status/changed"]}}}
S> {"id":1,"result":{"userAgent":"tcode/0.162.1 (Mac OS 26.6.2; arm64) xterm-256color (tcode; 0.1.0)","codexHome":"/Users/dev/.codex","platformFamily":"unix","platformOs":"macos"}}
C> {"method":"initialized"}
C> {"id":2,"method":"thread/start","params":{"cwd":"/Users/dev/acme-app","model":"gpt-6.1-sol","approvalPolicy":"on-request","sandbox":"workspace-write","developerInstructions":"You are running inside TCode. Prefer concise answers.","config":{"model_reasoning_summary":"auto"}}}
S> {"id":2,"result":{"thread":{"id":"019a6f1e-2b3c-7d4e-8f50-6a7b8c9d0e1f","sessionId":"019a6f1e-2b3c-7d4e-8f50-6a7b8c9d0e1f","forkedFromId":null,"parentThreadId":null,"preview":"","ephemeral":false,"projectId":null,"modelProvider":"openai","model":"gpt-6.1-sol","reasoningEffort":"medium","createdAt":1792127440,"updatedAt":1792127440,"recencyAt":1792127440,"status":{"type":"idle"},"path":null,"cwd":"/Users/dev/acme-app","cliVersion":"0.162.1","originator":"tcode","source":"appServer","threadSource":null,"agentNickname":null,"agentRole":null,"gitInfo":null,"name":null,"turns":[]},"model":"gpt-6.1-sol","modelProvider":"openai","serviceTier":null,"cwd":"/Users/dev/acme-app","instructionSources":["/Users/dev/acme-app/AGENTS.md"],"approvalPolicy":"on-request","approvalsReviewer":"user","sandbox":{"type":"workspaceWrite","writableRoots":[],"networkAccess":false,"excludeTmpdirEnvVar":false,"excludeSlashTmp":false},"reasoningEffort":"medium"}}
S> {"method":"thread/started","params":{"thread":{"id":"019a6f1e-2b3c-7d4e-8f50-6a7b8c9d0e1f","sessionId":"019a6f1e-2b3c-7d4e-8f50-6a7b8c9d0e1f","forkedFromId":null,"parentThreadId":null,"preview":"","ephemeral":false,"projectId":null,"modelProvider":"openai","model":"gpt-6.1-sol","reasoningEffort":"medium","createdAt":1792127440,"updatedAt":1792127440,"recencyAt":1792127440,"status":{"type":"idle"},"path":null,"cwd":"/Users/dev/acme-app","cliVersion":"0.162.1","originator":"tcode","source":"appServer","threadSource":null,"agentNickname":null,"agentRole":null,"gitInfo":null,"name":null,"turns":[]}},"emittedAtMs":1792127440037}
C> {"id":3,"method":"turn/start","params":{"threadId":"019a6f1e-2b3c-7d4e-8f50-6a7b8c9d0e1f","input":[{"type":"text","text":"这个仓库是做什么的？","text_elements":[]}],"effort":"medium","summary":"auto"}}
S> {"id":3,"result":{"turn":{"id":"019a6f1e-3c4d-7e5f-9061-7b8c9d0e1f20","rootTurnId":"019a6f1e-3c4d-7e5f-9061-7b8c9d0e1f20","items":[],"itemsView":"notLoaded","status":"inProgress","error":null,"startedAt":1792127440,"completedAt":null,"durationMs":null}}}
S> {"method":"turn/started","params":{"threadId":"019a6f1e-2b3c-7d4e-8f50-6a7b8c9d0e1f","turn":{"id":"019a6f1e-3c4d-7e5f-9061-7b8c9d0e1f20","rootTurnId":"019a6f1e-3c4d-7e5f-9061-7b8c9d0e1f20","items":[],"itemsView":"notLoaded","status":"inProgress","error":null,"startedAt":1792127440,"completedAt":null,"durationMs":null}},"emittedAtMs":1792127440074}
S> {"method":"thread/status/changed","params":{"threadId":"019a6f1e-2b3c-7d4e-8f50-6a7b8c9d0e1f","status":{"type":"active","activeFlags":[]}},"emittedAtMs":1792127440111}
S> {"method":"item/started","params":{"threadId":"019a6f1e-2b3c-7d4e-8f50-6a7b8c9d0e1f","turnId":"019a6f1e-3c4d-7e5f-9061-7b8c9d0e1f20","item":{"type":"userMessage","id":"0d1e2f30-4152-4637-8899-aabbccddeeff","clientId":null,"content":[{"type":"text","text":"这个仓库是做什么的？","text_elements":[]}]},"startedAtMs":1792127440112},"emittedAtMs":1792127440148}
S> {"method":"item/completed","params":{"threadId":"019a6f1e-2b3c-7d4e-8f50-6a7b8c9d0e1f","turnId":"019a6f1e-3c4d-7e5f-9061-7b8c9d0e1f20","item":{"type":"userMessage","id":"0d1e2f30-4152-4637-8899-aabbccddeeff","clientId":null,"content":[{"type":"text","text":"这个仓库是做什么的？","text_elements":[]}]},"completedAtMs":1792127440149},"emittedAtMs":1792127440149}
S> {"method":"item/started","params":{"threadId":"019a6f1e-2b3c-7d4e-8f50-6a7b8c9d0e1f","turnId":"019a6f1e-3c4d-7e5f-9061-7b8c9d0e1f20","item":{"type":"reasoning","id":"rs_0a1b2c3d4e5f6071","summary":[],"content":[]},"startedAtMs":1792127440186},"emittedAtMs":1792127440186}
S> {"method":"item/reasoning/summaryPartAdded","params":{"threadId":"019a6f1e-2b3c-7d4e-8f50-6a7b8c9d0e1f","turnId":"019a6f1e-3c4d-7e5f-9061-7b8c9d0e1f20","itemId":"rs_0a1b2c3d4e5f6071","summaryIndex":0},"emittedAtMs":1792127440197}
S> {"method":"item/reasoning/summaryTextDelta","params":{"threadId":"019a6f1e-2b3c-7d4e-8f50-6a7b8c9d0e1f","turnId":"019a6f1e-3c4d-7e5f-9061-7b8c9d0e1f20","itemId":"rs_0a1b2c3d4e5f6071","summaryIndex":0,"delta":"**Inspecting the"},"emittedAtMs":1792127440216}
S> {"method":"item/reasoning/summaryTextDelta","params":{"threadId":"019a6f1e-2b3c-7d4e-8f50-6a7b8c9d0e1f","turnId":"019a6f1e-3c4d-7e5f-9061-7b8c9d0e1f20","itemId":"rs_0a1b2c3d4e5f6071","summaryIndex":0,"delta":" repository layout**\n\nThe user wants"},"emittedAtMs":1792127440235}
S> {"method":"item/reasoning/summaryPartAdded","params":{"threadId":"019a6f1e-2b3c-7d4e-8f50-6a7b8c9d0e1f","turnId":"019a6f1e-3c4d-7e5f-9061-7b8c9d0e1f20","itemId":"rs_0a1b2c3d4e5f6071","summaryIndex":1},"emittedAtMs":1792127440246}
S> {"method":"item/reasoning/summaryTextDelta","params":{"threadId":"019a6f1e-2b3c-7d4e-8f50-6a7b8c9d0e1f","turnId":"019a6f1e-3c4d-7e5f-9061-7b8c9d0e1f20","itemId":"rs_0a1b2c3d4e5f6071","summaryIndex":1,"delta":" a summary; README.md"},"emittedAtMs":1792127440265}
S> {"method":"item/reasoning/summaryTextDelta","params":{"threadId":"019a6f1e-2b3c-7d4e-8f50-6a7b8c9d0e1f","turnId":"019a6f1e-3c4d-7e5f-9061-7b8c9d0e1f20","itemId":"rs_0a1b2c3d4e5f6071","summaryIndex":1,"delta":" describes the app."},"emittedAtMs":1792127440284}
S> {"method":"item/completed","params":{"threadId":"019a6f1e-2b3c-7d4e-8f50-6a7b8c9d0e1f","turnId":"019a6f1e-3c4d-7e5f-9061-7b8c9d0e1f20","item":{"type":"reasoning","id":"rs_0a1b2c3d4e5f6071","summary":["**Inspecting the repository layout**\n\nThe user wants"," a summary; README.md describes the app."],"content":[]},"completedAtMs":1792127440321},"emittedAtMs":1792127440321}
S> {"method":"item/started","params":{"threadId":"019a6f1e-2b3c-7d4e-8f50-6a7b8c9d0e1f","turnId":"019a6f1e-3c4d-7e5f-9061-7b8c9d0e1f20","item":{"type":"agentMessage","id":"msg_0a1b2c3d4e5f6072","text":"","phase":null,"memoryCitation":null},"startedAtMs":1792127440358},"emittedAtMs":1792127440358}
S> {"method":"item/agentMessage/delta","params":{"threadId":"019a6f1e-2b3c-7d4e-8f50-6a7b8c9d0e1f","turnId":"019a6f1e-3c4d-7e5f-9061-7b8c9d0e1f20","itemId":"msg_0a1b2c3d4e5f6072","delta":"这是一个"},"emittedAtMs":1792127440381}
S> {"method":"item/agentMessage/delta","params":{"threadId":"019a6f1e-2b3c-7d4e-8f50-6a7b8c9d0e1f","turnId":"019a6f1e-3c4d-7e5f-9061-7b8c9d0e1f20","itemId":"msg_0a1b2c3d4e5f6072","delta":"基于 Avalonia 的"},"emittedAtMs":1792127440404}
S> {"method":"item/agentMessage/delta","params":{"threadId":"019a6f1e-2b3c-7d4e-8f50-6a7b8c9d0e1f","turnId":"019a6f1e-3c4d-7e5f-9061-7b8c9d0e1f20","itemId":"msg_0a1b2c3d4e5f6072","delta":"桌面 Agent 客户端。"},"emittedAtMs":1792127440427}
S> {"method":"item/completed","params":{"threadId":"019a6f1e-2b3c-7d4e-8f50-6a7b8c9d0e1f","turnId":"019a6f1e-3c4d-7e5f-9061-7b8c9d0e1f20","item":{"type":"agentMessage","id":"msg_0a1b2c3d4e5f6072","text":"这是一个基于 Avalonia 的桌面 Agent 客户端。","phase":"final_answer","memoryCitation":null},"completedAtMs":1792127440464},"emittedAtMs":1792127440464}
S> {"method":"thread/tokenUsage/updated","params":{"threadId":"019a6f1e-2b3c-7d4e-8f50-6a7b8c9d0e1f","turnId":"019a6f1e-3c4d-7e5f-9061-7b8c9d0e1f20","tokenUsage":{"total":{"inputTokens":12850,"cachedInputTokens":11264,"outputTokens":412,"reasoningOutputTokens":256,"totalTokens":13262,"cacheWriteInputTokens":0},"last":{"inputTokens":12850,"cachedInputTokens":11264,"outputTokens":412,"reasoningOutputTokens":256,"totalTokens":13262,"cacheWriteInputTokens":0},"modelContextWindow":272000}},"emittedAtMs":1792127440501}
S> {"method":"turn/completed","params":{"threadId":"019a6f1e-2b3c-7d4e-8f50-6a7b8c9d0e1f","turn":{"id":"019a6f1e-3c4d-7e5f-9061-7b8c9d0e1f20","rootTurnId":"019a6f1e-3c4d-7e5f-9061-7b8c9d0e1f20","items":[{"type":"agentMessage","id":"msg_0a1b2c3d4e5f6072","text":"这是一个基于 Avalonia 的桌面 Agent 客户端。","phase":"final_answer","memoryCitation":null}],"itemsView":"summary","status":"completed","error":null,"startedAt":1792127440,"completedAt":1792127444,"durationMs":4210}},"emittedAtMs":1792127440538}
S> {"method":"thread/status/changed","params":{"threadId":"019a6f1e-2b3c-7d4e-8f50-6a7b8c9d0e1f","status":{"type":"idle"}},"emittedAtMs":1792127440540}
```

### C-b 命令执行需要审批 → accept → 输出增量 → 完成（`b-command-approval-accept.jsonl`）

```text
C> {"id":1,"method":"initialize","params":{"clientInfo":{"name":"tcode","title":"TCode","version":"0.1.0"},"capabilities":{"experimentalApi":false,"optOutNotificationMethods":["remoteControl/status/changed"]}}}
S> {"id":1,"result":{"userAgent":"tcode/0.162.1 (Mac OS 26.6.2; arm64) xterm-256color (tcode; 0.1.0)","codexHome":"/Users/dev/.codex","platformFamily":"unix","platformOs":"macos"}}
C> {"method":"initialized"}
C> {"id":2,"method":"thread/start","params":{"cwd":"/Users/dev/acme-app","model":"gpt-6.1-sol","approvalPolicy":"on-request","sandbox":"workspace-write","developerInstructions":"You are running inside TCode. Prefer concise answers.","config":{"model_reasoning_summary":"auto"}}}
S> {"id":2,"result":{"thread":{"id":"019a6f20-1a2b-7c3d-8e4f-5a6b7c8d9e0f","sessionId":"019a6f20-1a2b-7c3d-8e4f-5a6b7c8d9e0f","forkedFromId":null,"parentThreadId":null,"preview":"","ephemeral":false,"projectId":null,"modelProvider":"openai","model":"gpt-6.1-sol","reasoningEffort":"medium","createdAt":1792127440,"updatedAt":1792127440,"recencyAt":1792127440,"status":{"type":"idle"},"path":null,"cwd":"/Users/dev/acme-app","cliVersion":"0.162.1","originator":"tcode","source":"appServer","threadSource":null,"agentNickname":null,"agentRole":null,"gitInfo":null,"name":null,"turns":[]},"model":"gpt-6.1-sol","modelProvider":"openai","serviceTier":null,"cwd":"/Users/dev/acme-app","instructionSources":["/Users/dev/acme-app/AGENTS.md"],"approvalPolicy":"on-request","approvalsReviewer":"user","sandbox":{"type":"workspaceWrite","writableRoots":[],"networkAccess":false,"excludeTmpdirEnvVar":false,"excludeSlashTmp":false},"reasoningEffort":"medium"}}
S> {"method":"thread/started","params":{"thread":{"id":"019a6f20-1a2b-7c3d-8e4f-5a6b7c8d9e0f","sessionId":"019a6f20-1a2b-7c3d-8e4f-5a6b7c8d9e0f","forkedFromId":null,"parentThreadId":null,"preview":"","ephemeral":false,"projectId":null,"modelProvider":"openai","model":"gpt-6.1-sol","reasoningEffort":"medium","createdAt":1792127440,"updatedAt":1792127440,"recencyAt":1792127440,"status":{"type":"idle"},"path":null,"cwd":"/Users/dev/acme-app","cliVersion":"0.162.1","originator":"tcode","source":"appServer","threadSource":null,"agentNickname":null,"agentRole":null,"gitInfo":null,"name":null,"turns":[]}},"emittedAtMs":1792127440037}
C> {"id":3,"method":"turn/start","params":{"threadId":"019a6f20-1a2b-7c3d-8e4f-5a6b7c8d9e0f","input":[{"type":"text","text":"运行测试","text_elements":[]}],"effort":"medium","summary":"auto"}}
S> {"method":"turn/started","params":{"threadId":"019a6f20-1a2b-7c3d-8e4f-5a6b7c8d9e0f","turn":{"id":"019a6f20-2b3c-7d4e-9f50-6a7b8c9d0e1f","rootTurnId":"019a6f20-2b3c-7d4e-9f50-6a7b8c9d0e1f","items":[],"itemsView":"notLoaded","status":"inProgress","error":null,"startedAt":1792127440,"completedAt":null,"durationMs":null}},"emittedAtMs":1792127440074}
S> {"id":3,"result":{"turn":{"id":"019a6f20-2b3c-7d4e-9f50-6a7b8c9d0e1f","rootTurnId":"019a6f20-2b3c-7d4e-9f50-6a7b8c9d0e1f","items":[],"itemsView":"notLoaded","status":"inProgress","error":null,"startedAt":1792127440,"completedAt":null,"durationMs":null}}}
S> {"method":"thread/status/changed","params":{"threadId":"019a6f20-1a2b-7c3d-8e4f-5a6b7c8d9e0f","status":{"type":"active","activeFlags":[]}},"emittedAtMs":1792127440111}
S> {"method":"item/started","params":{"threadId":"019a6f20-1a2b-7c3d-8e4f-5a6b7c8d9e0f","turnId":"019a6f20-2b3c-7d4e-9f50-6a7b8c9d0e1f","item":{"type":"userMessage","id":"1e2f3041-5263-4748-99aa-bbccddeeff00","clientId":null,"content":[{"type":"text","text":"运行测试","text_elements":[]}]},"startedAtMs":1792127440112},"emittedAtMs":1792127440148}
S> {"method":"item/completed","params":{"threadId":"019a6f20-1a2b-7c3d-8e4f-5a6b7c8d9e0f","turnId":"019a6f20-2b3c-7d4e-9f50-6a7b8c9d0e1f","item":{"type":"userMessage","id":"1e2f3041-5263-4748-99aa-bbccddeeff00","clientId":null,"content":[{"type":"text","text":"运行测试","text_elements":[]}]},"completedAtMs":1792127440149},"emittedAtMs":1792127440149}
S> {"method":"item/started","params":{"threadId":"019a6f20-1a2b-7c3d-8e4f-5a6b7c8d9e0f","turnId":"019a6f20-2b3c-7d4e-9f50-6a7b8c9d0e1f","item":{"type":"agentMessage","id":"msg_1b2c3d4e5f607182","text":"","phase":null,"memoryCitation":null},"startedAtMs":1792127440186},"emittedAtMs":1792127440186}
S> {"method":"item/agentMessage/delta","params":{"threadId":"019a6f20-1a2b-7c3d-8e4f-5a6b7c8d9e0f","turnId":"019a6f20-2b3c-7d4e-9f50-6a7b8c9d0e1f","itemId":"msg_1b2c3d4e5f607182","delta":"我先运行"},"emittedAtMs":1792127440209}
S> {"method":"item/agentMessage/delta","params":{"threadId":"019a6f20-1a2b-7c3d-8e4f-5a6b7c8d9e0f","turnId":"019a6f20-2b3c-7d4e-9f50-6a7b8c9d0e1f","itemId":"msg_1b2c3d4e5f607182","delta":"测试套件。"},"emittedAtMs":1792127440232}
S> {"method":"item/completed","params":{"threadId":"019a6f20-1a2b-7c3d-8e4f-5a6b7c8d9e0f","turnId":"019a6f20-2b3c-7d4e-9f50-6a7b8c9d0e1f","item":{"type":"agentMessage","id":"msg_1b2c3d4e5f607182","text":"我先运行测试套件。","phase":"commentary","memoryCitation":null},"completedAtMs":1792127440269},"emittedAtMs":1792127440269}
S> {"method":"item/started","params":{"threadId":"019a6f20-1a2b-7c3d-8e4f-5a6b7c8d9e0f","turnId":"019a6f20-2b3c-7d4e-9f50-6a7b8c9d0e1f","item":{"type":"commandExecution","id":"call_Qm9vX2NtZF8wMDE","command":"/bin/zsh -lc 'npm test'","cwd":"/Users/dev/acme-app","processId":null,"source":"agent","status":"inProgress","commandActions":[{"type":"unknown","command":"npm test"}],"aggregatedOutput":null,"exitCode":null,"durationMs":null},"startedAtMs":1792127440306},"emittedAtMs":1792127440306}
S> {"method":"thread/status/changed","params":{"threadId":"019a6f20-1a2b-7c3d-8e4f-5a6b7c8d9e0f","status":{"type":"active","activeFlags":["waitingOnApproval"]}},"emittedAtMs":1792127440307}
S> {"id":0,"method":"item/commandExecution/requestApproval","params":{"threadId":"019a6f20-1a2b-7c3d-8e4f-5a6b7c8d9e0f","turnId":"019a6f20-2b3c-7d4e-9f50-6a7b8c9d0e1f","itemId":"call_Qm9vX2NtZF8wMDE","startedAtMs":1792127440307,"approvalId":null,"environmentId":null,"kind":"command","reason":"Run the project's test suite (needs network access to the local registry)","networkApprovalContext":null,"command":"/bin/zsh -lc 'npm test'","cwd":"/Users/dev/acme-app","commandActions":[{"type":"unknown","command":"npm test"}],"proposedExecpolicyAmendment":["npm","test"],"proposedNetworkPolicyAmendments":null}}
C> {"id":0,"result":{"decision":"accept"}}
S> {"method":"serverRequest/resolved","params":{"threadId":"019a6f20-1a2b-7c3d-8e4f-5a6b7c8d9e0f","requestId":0},"emittedAtMs":1792127441207}
S> {"method":"thread/status/changed","params":{"threadId":"019a6f20-1a2b-7c3d-8e4f-5a6b7c8d9e0f","status":{"type":"active","activeFlags":[]}},"emittedAtMs":1792127441208}
S> {"method":"item/commandExecution/outputDelta","params":{"threadId":"019a6f20-1a2b-7c3d-8e4f-5a6b7c8d9e0f","turnId":"019a6f20-2b3c-7d4e-9f50-6a7b8c9d0e1f","itemId":"call_Qm9vX2NtZF8wMDE","delta":"\n> acme-app@1.0.0 test\n> vitest run\n\n"},"emittedAtMs":1792127441508}
S> {"method":"item/commandExecution/outputDelta","params":{"threadId":"019a6f20-1a2b-7c3d-8e4f-5a6b7c8d9e0f","turnId":"019a6f20-2b3c-7d4e-9f50-6a7b8c9d0e1f","itemId":"call_Qm9vX2NtZF8wMDE","delta":" ✓ src/math.test.ts (3 tests) 4ms\n\n Test Files  1 passed (1)\n      Tests  3 passed (3)\n"},"emittedAtMs":1792127442328}
S> {"method":"item/completed","params":{"threadId":"019a6f20-1a2b-7c3d-8e4f-5a6b7c8d9e0f","turnId":"019a6f20-2b3c-7d4e-9f50-6a7b8c9d0e1f","item":{"type":"commandExecution","id":"call_Qm9vX2NtZF8wMDE","command":"/bin/zsh -lc 'npm test'","cwd":"/Users/dev/acme-app","processId":null,"source":"agent","status":"completed","commandActions":[{"type":"unknown","command":"npm test"}],"aggregatedOutput":"\n> acme-app@1.0.0 test\n> vitest run\n\n ✓ src/math.test.ts (3 tests) 4ms\n\n Test Files  1 passed (1)\n      Tests  3 passed (3)\n","exitCode":0,"durationMs":1184},"completedAtMs":1792127442340},"emittedAtMs":1792127442340}
S> {"method":"turn/diff/updated","params":{"threadId":"019a6f20-1a2b-7c3d-8e4f-5a6b7c8d9e0f","turnId":"019a6f20-2b3c-7d4e-9f50-6a7b8c9d0e1f","diff":""},"emittedAtMs":1792127442345}
S> {"method":"item/started","params":{"threadId":"019a6f20-1a2b-7c3d-8e4f-5a6b7c8d9e0f","turnId":"019a6f20-2b3c-7d4e-9f50-6a7b8c9d0e1f","item":{"type":"agentMessage","id":"msg_1b2c3d4e5f607183","text":"","phase":null,"memoryCitation":null},"startedAtMs":1792127442382},"emittedAtMs":1792127442382}
S> {"method":"item/agentMessage/delta","params":{"threadId":"019a6f20-1a2b-7c3d-8e4f-5a6b7c8d9e0f","turnId":"019a6f20-2b3c-7d4e-9f50-6a7b8c9d0e1f","itemId":"msg_1b2c3d4e5f607183","delta":"3 个测试"},"emittedAtMs":1792127442405}
S> {"method":"item/agentMessage/delta","params":{"threadId":"019a6f20-1a2b-7c3d-8e4f-5a6b7c8d9e0f","turnId":"019a6f20-2b3c-7d4e-9f50-6a7b8c9d0e1f","itemId":"msg_1b2c3d4e5f607183","delta":"全部通过。"},"emittedAtMs":1792127442428}
S> {"method":"item/completed","params":{"threadId":"019a6f20-1a2b-7c3d-8e4f-5a6b7c8d9e0f","turnId":"019a6f20-2b3c-7d4e-9f50-6a7b8c9d0e1f","item":{"type":"agentMessage","id":"msg_1b2c3d4e5f607183","text":"3 个测试全部通过。","phase":"final_answer","memoryCitation":null},"completedAtMs":1792127442465},"emittedAtMs":1792127442465}
S> {"method":"thread/tokenUsage/updated","params":{"threadId":"019a6f20-1a2b-7c3d-8e4f-5a6b7c8d9e0f","turnId":"019a6f20-2b3c-7d4e-9f50-6a7b8c9d0e1f","tokenUsage":{"total":{"inputTokens":28432,"cachedInputTokens":25344,"outputTokens":410,"reasoningOutputTokens":180,"totalTokens":28842},"last":{"inputTokens":15120,"cachedInputTokens":14080,"outputTokens":96,"reasoningOutputTokens":32,"totalTokens":15216},"modelContextWindow":272000}},"emittedAtMs":1792127442502}
S> {"method":"turn/completed","params":{"threadId":"019a6f20-1a2b-7c3d-8e4f-5a6b7c8d9e0f","turn":{"id":"019a6f20-2b3c-7d4e-9f50-6a7b8c9d0e1f","rootTurnId":"019a6f20-2b3c-7d4e-9f50-6a7b8c9d0e1f","items":[{"type":"agentMessage","id":"msg_1b2c3d4e5f607183","text":"3 个测试全部通过。","phase":"final_answer","memoryCitation":null}],"itemsView":"summary","status":"completed","error":null,"startedAt":1792127440,"completedAt":1792127446,"durationMs":6620}},"emittedAtMs":1792127442539}
S> {"method":"thread/status/changed","params":{"threadId":"019a6f20-1a2b-7c3d-8e4f-5a6b7c8d9e0f","status":{"type":"idle"}},"emittedAtMs":1792127442541}
```

### C-c 文件修改审批 → decline（`c-filechange-approval-decline.jsonl`）

```text
C> {"id":1,"method":"initialize","params":{"clientInfo":{"name":"tcode","title":"TCode","version":"0.1.0"},"capabilities":{"experimentalApi":false,"optOutNotificationMethods":["remoteControl/status/changed"]}}}
S> {"id":1,"result":{"userAgent":"tcode/0.162.1 (Mac OS 26.6.2; arm64) xterm-256color (tcode; 0.1.0)","codexHome":"/Users/dev/.codex","platformFamily":"unix","platformOs":"macos"}}
C> {"method":"initialized"}
C> {"id":2,"method":"thread/start","params":{"cwd":"/Users/dev/acme-app","model":"gpt-6.1-sol","approvalPolicy":"on-request","sandbox":"read-only","developerInstructions":"You are running inside TCode. Prefer concise answers.","config":{"model_reasoning_summary":"auto"}}}
S> {"id":2,"result":{"thread":{"id":"019a6f22-3c4d-7e5f-8061-7b8c9d0e1f20","sessionId":"019a6f22-3c4d-7e5f-8061-7b8c9d0e1f20","forkedFromId":null,"parentThreadId":null,"preview":"","ephemeral":false,"projectId":null,"modelProvider":"openai","model":"gpt-6.1-sol","reasoningEffort":"medium","createdAt":1792127440,"updatedAt":1792127440,"recencyAt":1792127440,"status":{"type":"idle"},"path":null,"cwd":"/Users/dev/acme-app","cliVersion":"0.162.1","originator":"tcode","source":"appServer","threadSource":null,"agentNickname":null,"agentRole":null,"gitInfo":null,"name":null,"turns":[]},"model":"gpt-6.1-sol","modelProvider":"openai","serviceTier":null,"cwd":"/Users/dev/acme-app","instructionSources":["/Users/dev/acme-app/AGENTS.md"],"approvalPolicy":"on-request","approvalsReviewer":"user","sandbox":{"type":"workspaceWrite","writableRoots":[],"networkAccess":false,"excludeTmpdirEnvVar":false,"excludeSlashTmp":false},"reasoningEffort":"medium"}}
S> {"method":"thread/started","params":{"thread":{"id":"019a6f22-3c4d-7e5f-8061-7b8c9d0e1f20","sessionId":"019a6f22-3c4d-7e5f-8061-7b8c9d0e1f20","forkedFromId":null,"parentThreadId":null,"preview":"","ephemeral":false,"projectId":null,"modelProvider":"openai","model":"gpt-6.1-sol","reasoningEffort":"medium","createdAt":1792127440,"updatedAt":1792127440,"recencyAt":1792127440,"status":{"type":"idle"},"path":null,"cwd":"/Users/dev/acme-app","cliVersion":"0.162.1","originator":"tcode","source":"appServer","threadSource":null,"agentNickname":null,"agentRole":null,"gitInfo":null,"name":null,"turns":[]}},"emittedAtMs":1792127440037}
C> {"id":3,"method":"turn/start","params":{"threadId":"019a6f22-3c4d-7e5f-8061-7b8c9d0e1f20","input":[{"type":"text","text":"把 README 的标题改成 Acme","text_elements":[]}],"effort":"medium","summary":"auto"}}
S> {"id":3,"result":{"turn":{"id":"019a6f22-4d5e-7f60-9172-8c9d0e1f2031","rootTurnId":"019a6f22-4d5e-7f60-9172-8c9d0e1f2031","items":[],"itemsView":"notLoaded","status":"inProgress","error":null,"startedAt":1792127440,"completedAt":null,"durationMs":null}}}
S> {"method":"turn/started","params":{"threadId":"019a6f22-3c4d-7e5f-8061-7b8c9d0e1f20","turn":{"id":"019a6f22-4d5e-7f60-9172-8c9d0e1f2031","rootTurnId":"019a6f22-4d5e-7f60-9172-8c9d0e1f2031","items":[],"itemsView":"notLoaded","status":"inProgress","error":null,"startedAt":1792127440,"completedAt":null,"durationMs":null}},"emittedAtMs":1792127440074}
S> {"method":"thread/status/changed","params":{"threadId":"019a6f22-3c4d-7e5f-8061-7b8c9d0e1f20","status":{"type":"active","activeFlags":[]}},"emittedAtMs":1792127440111}
S> {"method":"item/started","params":{"threadId":"019a6f22-3c4d-7e5f-8061-7b8c9d0e1f20","turnId":"019a6f22-4d5e-7f60-9172-8c9d0e1f2031","item":{"type":"userMessage","id":"2f304152-6374-4859-aabb-ccddeeff0011","clientId":null,"content":[{"type":"text","text":"把 README 的标题改成 Acme","text_elements":[]}]},"startedAtMs":1792127440112},"emittedAtMs":1792127440148}
S> {"method":"item/completed","params":{"threadId":"019a6f22-3c4d-7e5f-8061-7b8c9d0e1f20","turnId":"019a6f22-4d5e-7f60-9172-8c9d0e1f2031","item":{"type":"userMessage","id":"2f304152-6374-4859-aabb-ccddeeff0011","clientId":null,"content":[{"type":"text","text":"把 README 的标题改成 Acme","text_elements":[]}]},"completedAtMs":1792127440149},"emittedAtMs":1792127440149}
S> {"method":"item/started","params":{"threadId":"019a6f22-3c4d-7e5f-8061-7b8c9d0e1f20","turnId":"019a6f22-4d5e-7f60-9172-8c9d0e1f2031","item":{"type":"fileChange","id":"call_UGF0Y2hfMDAy","status":"inProgress","changes":[{"path":"/Users/dev/acme-app/README.md","kind":{"type":"update","move_path":null},"diff":"@@ -1,3 +1,3 @@\n-# acme-app\n+# Acme\n \n A desktop agent client.\n"}]},"startedAtMs":1792127440186},"emittedAtMs":1792127440186}
S> {"method":"item/fileChange/patchUpdated","params":{"threadId":"019a6f22-3c4d-7e5f-8061-7b8c9d0e1f20","turnId":"019a6f22-4d5e-7f60-9172-8c9d0e1f2031","itemId":"call_UGF0Y2hfMDAy","changes":[{"path":"/Users/dev/acme-app/README.md","kind":{"type":"update","move_path":null},"diff":"@@ -1,3 +1,3 @@\n-# acme-app\n+# Acme\n \n A desktop agent client.\n"}]},"emittedAtMs":1792127440188}
S> {"method":"thread/status/changed","params":{"threadId":"019a6f22-3c4d-7e5f-8061-7b8c9d0e1f20","status":{"type":"active","activeFlags":["waitingOnApproval"]}},"emittedAtMs":1792127440189}
S> {"id":0,"method":"item/fileChange/requestApproval","params":{"threadId":"019a6f22-3c4d-7e5f-8061-7b8c9d0e1f20","turnId":"019a6f22-4d5e-7f60-9172-8c9d0e1f2031","itemId":"call_UGF0Y2hfMDAy","startedAtMs":1792127440189,"reason":null,"grantRoot":null}}
C> {"id":0,"result":{"decision":"decline"}}
S> {"method":"serverRequest/resolved","params":{"threadId":"019a6f22-3c4d-7e5f-8061-7b8c9d0e1f20","requestId":0},"emittedAtMs":1792127442589}
S> {"method":"thread/status/changed","params":{"threadId":"019a6f22-3c4d-7e5f-8061-7b8c9d0e1f20","status":{"type":"active","activeFlags":[]}},"emittedAtMs":1792127442590}
S> {"method":"item/completed","params":{"threadId":"019a6f22-3c4d-7e5f-8061-7b8c9d0e1f20","turnId":"019a6f22-4d5e-7f60-9172-8c9d0e1f2031","item":{"type":"fileChange","id":"call_UGF0Y2hfMDAy","status":"declined","changes":[{"path":"/Users/dev/acme-app/README.md","kind":{"type":"update","move_path":null},"diff":"@@ -1,3 +1,3 @@\n-# acme-app\n+# Acme\n \n A desktop agent client.\n"}]},"completedAtMs":1792127442593},"emittedAtMs":1792127442593}
S> {"method":"item/started","params":{"threadId":"019a6f22-3c4d-7e5f-8061-7b8c9d0e1f20","turnId":"019a6f22-4d5e-7f60-9172-8c9d0e1f2031","item":{"type":"agentMessage","id":"msg_2c3d4e5f60718293","text":"","phase":null,"memoryCitation":null},"startedAtMs":1792127442630},"emittedAtMs":1792127442630}
S> {"method":"item/agentMessage/delta","params":{"threadId":"019a6f22-3c4d-7e5f-8061-7b8c9d0e1f20","turnId":"019a6f22-4d5e-7f60-9172-8c9d0e1f2031","itemId":"msg_2c3d4e5f60718293","delta":"好的，"},"emittedAtMs":1792127442653}
S> {"method":"item/agentMessage/delta","params":{"threadId":"019a6f22-3c4d-7e5f-8061-7b8c9d0e1f20","turnId":"019a6f22-4d5e-7f60-9172-8c9d0e1f2031","itemId":"msg_2c3d4e5f60718293","delta":"我不会修改 README.md。"},"emittedAtMs":1792127442676}
S> {"method":"item/completed","params":{"threadId":"019a6f22-3c4d-7e5f-8061-7b8c9d0e1f20","turnId":"019a6f22-4d5e-7f60-9172-8c9d0e1f2031","item":{"type":"agentMessage","id":"msg_2c3d4e5f60718293","text":"好的，我不会修改 README.md。","phase":"final_answer","memoryCitation":null},"completedAtMs":1792127442713},"emittedAtMs":1792127442713}
S> {"method":"thread/tokenUsage/updated","params":{"threadId":"019a6f22-3c4d-7e5f-8061-7b8c9d0e1f20","turnId":"019a6f22-4d5e-7f60-9172-8c9d0e1f2031","tokenUsage":{"total":{"inputTokens":9800,"cachedInputTokens":8704,"outputTokens":140,"reasoningOutputTokens":64,"totalTokens":9940},"last":{"inputTokens":9800,"cachedInputTokens":8704,"outputTokens":140,"reasoningOutputTokens":64,"totalTokens":9940},"modelContextWindow":272000}},"emittedAtMs":1792127442750}
S> {"method":"turn/completed","params":{"threadId":"019a6f22-3c4d-7e5f-8061-7b8c9d0e1f20","turn":{"id":"019a6f22-4d5e-7f60-9172-8c9d0e1f2031","rootTurnId":"019a6f22-4d5e-7f60-9172-8c9d0e1f2031","items":[{"type":"agentMessage","id":"msg_2c3d4e5f60718293","text":"好的，我不会修改 README.md。","phase":"final_answer","memoryCitation":null}],"itemsView":"summary","status":"completed","error":null,"startedAt":1792127440,"completedAt":1792127445,"durationMs":5120}},"emittedAtMs":1792127442787}
S> {"method":"thread/status/changed","params":{"threadId":"019a6f22-3c4d-7e5f-8061-7b8c9d0e1f20","status":{"type":"idle"}},"emittedAtMs":1792127442789}
```

### C-d 审批未决时 turn/interrupt（`d-turn-interrupt.jsonl`）

```text
C> {"id":1,"method":"initialize","params":{"clientInfo":{"name":"tcode","title":"TCode","version":"0.1.0"},"capabilities":{"experimentalApi":false,"optOutNotificationMethods":["remoteControl/status/changed"]}}}
S> {"id":1,"result":{"userAgent":"tcode/0.162.1 (Mac OS 26.6.2; arm64) xterm-256color (tcode; 0.1.0)","codexHome":"/Users/dev/.codex","platformFamily":"unix","platformOs":"macos"}}
C> {"method":"initialized"}
C> {"id":2,"method":"thread/start","params":{"cwd":"/Users/dev/acme-app","model":"gpt-6.1-sol","approvalPolicy":"on-request","sandbox":"workspace-write","developerInstructions":"You are running inside TCode. Prefer concise answers.","config":{"model_reasoning_summary":"auto"}}}
S> {"id":2,"result":{"thread":{"id":"019a6f24-5e6f-7071-8283-9d0e1f203142","sessionId":"019a6f24-5e6f-7071-8283-9d0e1f203142","forkedFromId":null,"parentThreadId":null,"preview":"","ephemeral":false,"projectId":null,"modelProvider":"openai","model":"gpt-6.1-sol","reasoningEffort":"medium","createdAt":1792127440,"updatedAt":1792127440,"recencyAt":1792127440,"status":{"type":"idle"},"path":null,"cwd":"/Users/dev/acme-app","cliVersion":"0.162.1","originator":"tcode","source":"appServer","threadSource":null,"agentNickname":null,"agentRole":null,"gitInfo":null,"name":null,"turns":[]},"model":"gpt-6.1-sol","modelProvider":"openai","serviceTier":null,"cwd":"/Users/dev/acme-app","instructionSources":["/Users/dev/acme-app/AGENTS.md"],"approvalPolicy":"on-request","approvalsReviewer":"user","sandbox":{"type":"workspaceWrite","writableRoots":[],"networkAccess":false,"excludeTmpdirEnvVar":false,"excludeSlashTmp":false},"reasoningEffort":"medium"}}
S> {"method":"thread/started","params":{"thread":{"id":"019a6f24-5e6f-7071-8283-9d0e1f203142","sessionId":"019a6f24-5e6f-7071-8283-9d0e1f203142","forkedFromId":null,"parentThreadId":null,"preview":"","ephemeral":false,"projectId":null,"modelProvider":"openai","model":"gpt-6.1-sol","reasoningEffort":"medium","createdAt":1792127440,"updatedAt":1792127440,"recencyAt":1792127440,"status":{"type":"idle"},"path":null,"cwd":"/Users/dev/acme-app","cliVersion":"0.162.1","originator":"tcode","source":"appServer","threadSource":null,"agentNickname":null,"agentRole":null,"gitInfo":null,"name":null,"turns":[]}},"emittedAtMs":1792127440037}
C> {"id":3,"method":"turn/start","params":{"threadId":"019a6f24-5e6f-7071-8283-9d0e1f203142","input":[{"type":"text","text":"清理构建产物","text_elements":[]}],"effort":"medium","summary":"auto"}}
S> {"id":3,"result":{"turn":{"id":"019a6f24-6f70-7182-9394-ae0f1f304253","rootTurnId":"019a6f24-6f70-7182-9394-ae0f1f304253","items":[],"itemsView":"notLoaded","status":"inProgress","error":null,"startedAt":1792127440,"completedAt":null,"durationMs":null}}}
S> {"method":"turn/started","params":{"threadId":"019a6f24-5e6f-7071-8283-9d0e1f203142","turn":{"id":"019a6f24-6f70-7182-9394-ae0f1f304253","rootTurnId":"019a6f24-6f70-7182-9394-ae0f1f304253","items":[],"itemsView":"notLoaded","status":"inProgress","error":null,"startedAt":1792127440,"completedAt":null,"durationMs":null}},"emittedAtMs":1792127440074}
S> {"method":"thread/status/changed","params":{"threadId":"019a6f24-5e6f-7071-8283-9d0e1f203142","status":{"type":"active","activeFlags":[]}},"emittedAtMs":1792127440111}
S> {"method":"item/started","params":{"threadId":"019a6f24-5e6f-7071-8283-9d0e1f203142","turnId":"019a6f24-6f70-7182-9394-ae0f1f304253","item":{"type":"userMessage","id":"30415263-7485-496a-bbcc-ddeeff001122","clientId":null,"content":[{"type":"text","text":"清理构建产物","text_elements":[]}]},"startedAtMs":1792127440112},"emittedAtMs":1792127440148}
S> {"method":"item/completed","params":{"threadId":"019a6f24-5e6f-7071-8283-9d0e1f203142","turnId":"019a6f24-6f70-7182-9394-ae0f1f304253","item":{"type":"userMessage","id":"30415263-7485-496a-bbcc-ddeeff001122","clientId":null,"content":[{"type":"text","text":"清理构建产物","text_elements":[]}]},"completedAtMs":1792127440149},"emittedAtMs":1792127440149}
S> {"method":"item/started","params":{"threadId":"019a6f24-5e6f-7071-8283-9d0e1f203142","turnId":"019a6f24-6f70-7182-9394-ae0f1f304253","item":{"type":"reasoning","id":"rs_3d4e5f60718293a4","summary":[],"content":[]},"startedAtMs":1792127440186},"emittedAtMs":1792127440186}
S> {"method":"item/reasoning/summaryPartAdded","params":{"threadId":"019a6f24-5e6f-7071-8283-9d0e1f203142","turnId":"019a6f24-6f70-7182-9394-ae0f1f304253","itemId":"rs_3d4e5f60718293a4","summaryIndex":0},"emittedAtMs":1792127440197}
S> {"method":"item/reasoning/summaryTextDelta","params":{"threadId":"019a6f24-5e6f-7071-8283-9d0e1f203142","turnId":"019a6f24-6f70-7182-9394-ae0f1f304253","itemId":"rs_3d4e5f60718293a4","summaryIndex":0,"delta":"**Planning cleanup**"},"emittedAtMs":1792127440216}
S> {"method":"item/completed","params":{"threadId":"019a6f24-5e6f-7071-8283-9d0e1f203142","turnId":"019a6f24-6f70-7182-9394-ae0f1f304253","item":{"type":"reasoning","id":"rs_3d4e5f60718293a4","summary":["**Planning cleanup**"],"content":[]},"completedAtMs":1792127440253},"emittedAtMs":1792127440253}
S> {"method":"item/started","params":{"threadId":"019a6f24-5e6f-7071-8283-9d0e1f203142","turnId":"019a6f24-6f70-7182-9394-ae0f1f304253","item":{"type":"agentMessage","id":"msg_3d4e5f60718293a5","text":"","phase":null,"memoryCitation":null},"startedAtMs":1792127440290},"emittedAtMs":1792127440290}
S> {"method":"item/agentMessage/delta","params":{"threadId":"019a6f24-5e6f-7071-8283-9d0e1f203142","turnId":"019a6f24-6f70-7182-9394-ae0f1f304253","itemId":"msg_3d4e5f60718293a5","delta":"我将删除 dist/ "},"emittedAtMs":1792127440313}
S> {"method":"item/agentMessage/delta","params":{"threadId":"019a6f24-5e6f-7071-8283-9d0e1f203142","turnId":"019a6f24-6f70-7182-9394-ae0f1f304253","itemId":"msg_3d4e5f60718293a5","delta":"目录。"},"emittedAtMs":1792127440336}
S> {"method":"item/completed","params":{"threadId":"019a6f24-5e6f-7071-8283-9d0e1f203142","turnId":"019a6f24-6f70-7182-9394-ae0f1f304253","item":{"type":"agentMessage","id":"msg_3d4e5f60718293a5","text":"我将删除 dist/ 目录。","phase":"commentary","memoryCitation":null},"completedAtMs":1792127440373},"emittedAtMs":1792127440373}
S> {"method":"item/started","params":{"threadId":"019a6f24-5e6f-7071-8283-9d0e1f203142","turnId":"019a6f24-6f70-7182-9394-ae0f1f304253","item":{"type":"commandExecution","id":"call_cm1fZGlzdF8wMDM","command":"/bin/zsh -lc 'rm -rf dist'","cwd":"/Users/dev/acme-app","processId":null,"source":"agent","status":"inProgress","commandActions":[{"type":"unknown","command":"rm -rf dist"}],"aggregatedOutput":null,"exitCode":null,"durationMs":null},"startedAtMs":1792127440410},"emittedAtMs":1792127440410}
S> {"method":"thread/status/changed","params":{"threadId":"019a6f24-5e6f-7071-8283-9d0e1f203142","status":{"type":"active","activeFlags":["waitingOnApproval"]}},"emittedAtMs":1792127440411}
S> {"id":0,"method":"item/commandExecution/requestApproval","params":{"threadId":"019a6f24-5e6f-7071-8283-9d0e1f203142","turnId":"019a6f24-6f70-7182-9394-ae0f1f304253","itemId":"call_cm1fZGlzdF8wMDM","startedAtMs":1792127440411,"approvalId":null,"environmentId":null,"kind":"command","reason":null,"networkApprovalContext":null,"command":"/bin/zsh -lc 'rm -rf dist'","cwd":"/Users/dev/acme-app","commandActions":[{"type":"unknown","command":"rm -rf dist"}],"proposedExecpolicyAmendment":null,"proposedNetworkPolicyAmendments":null}}
C> {"id":4,"method":"turn/interrupt","params":{"threadId":"019a6f24-5e6f-7071-8283-9d0e1f203142","turnId":"019a6f24-6f70-7182-9394-ae0f1f304253"}}
S> {"id":4,"result":{}}
S> {"method":"turn/completed","params":{"threadId":"019a6f24-5e6f-7071-8283-9d0e1f203142","turn":{"id":"019a6f24-6f70-7182-9394-ae0f1f304253","rootTurnId":"019a6f24-6f70-7182-9394-ae0f1f304253","items":[],"itemsView":"notLoaded","status":"interrupted","error":null,"startedAt":1792127440,"completedAt":1792127447,"durationMs":7340}},"emittedAtMs":1792127440414}
S> {"method":"serverRequest/resolved","params":{"threadId":"019a6f24-5e6f-7071-8283-9d0e1f203142","requestId":0},"emittedAtMs":1792127440415}
S> {"method":"thread/status/changed","params":{"threadId":"019a6f24-5e6f-7071-8283-9d0e1f203142","status":{"type":"idle"}},"emittedAtMs":1792127440417}
```

### C-e 轮次失败：可重试错误 → 最终错误 → turn/completed(failed)（`e-turn-failed.jsonl`）

```text
C> {"id":1,"method":"initialize","params":{"clientInfo":{"name":"tcode","title":"TCode","version":"0.1.0"},"capabilities":{"experimentalApi":false,"optOutNotificationMethods":["remoteControl/status/changed"]}}}
S> {"id":1,"result":{"userAgent":"tcode/0.162.1 (Mac OS 26.6.2; arm64) xterm-256color (tcode; 0.1.0)","codexHome":"/Users/dev/.codex","platformFamily":"unix","platformOs":"macos"}}
C> {"method":"initialized"}
C> {"id":2,"method":"thread/start","params":{"cwd":"/Users/dev/acme-app","model":"gpt-6.1-sol","approvalPolicy":"on-request","sandbox":"workspace-write","developerInstructions":"You are running inside TCode. Prefer concise answers.","config":{"model_reasoning_summary":"auto"}}}
S> {"id":2,"result":{"thread":{"id":"019a6f26-7071-7283-8495-a6b7c8d9e0f1","sessionId":"019a6f26-7071-7283-8495-a6b7c8d9e0f1","forkedFromId":null,"parentThreadId":null,"preview":"","ephemeral":false,"projectId":null,"modelProvider":"openai","model":"gpt-6.1-sol","reasoningEffort":"medium","createdAt":1792127440,"updatedAt":1792127440,"recencyAt":1792127440,"status":{"type":"idle"},"path":null,"cwd":"/Users/dev/acme-app","cliVersion":"0.162.1","originator":"tcode","source":"appServer","threadSource":null,"agentNickname":null,"agentRole":null,"gitInfo":null,"name":null,"turns":[]},"model":"gpt-6.1-sol","modelProvider":"openai","serviceTier":null,"cwd":"/Users/dev/acme-app","instructionSources":["/Users/dev/acme-app/AGENTS.md"],"approvalPolicy":"on-request","approvalsReviewer":"user","sandbox":{"type":"workspaceWrite","writableRoots":[],"networkAccess":false,"excludeTmpdirEnvVar":false,"excludeSlashTmp":false},"reasoningEffort":"medium"}}
S> {"method":"thread/started","params":{"thread":{"id":"019a6f26-7071-7283-8495-a6b7c8d9e0f1","sessionId":"019a6f26-7071-7283-8495-a6b7c8d9e0f1","forkedFromId":null,"parentThreadId":null,"preview":"","ephemeral":false,"projectId":null,"modelProvider":"openai","model":"gpt-6.1-sol","reasoningEffort":"medium","createdAt":1792127440,"updatedAt":1792127440,"recencyAt":1792127440,"status":{"type":"idle"},"path":null,"cwd":"/Users/dev/acme-app","cliVersion":"0.162.1","originator":"tcode","source":"appServer","threadSource":null,"agentNickname":null,"agentRole":null,"gitInfo":null,"name":null,"turns":[]}},"emittedAtMs":1792127440037}
C> {"id":3,"method":"turn/start","params":{"threadId":"019a6f26-7071-7283-8495-a6b7c8d9e0f1","input":[{"type":"text","text":"解释 src/main.ts","text_elements":[]}],"effort":"medium","summary":"auto"}}
S> {"id":3,"result":{"turn":{"id":"019a6f26-8182-7394-95a6-b7c8d9e0f102","rootTurnId":"019a6f26-8182-7394-95a6-b7c8d9e0f102","items":[],"itemsView":"notLoaded","status":"inProgress","error":null,"startedAt":1792127440,"completedAt":null,"durationMs":null}}}
S> {"method":"turn/started","params":{"threadId":"019a6f26-7071-7283-8495-a6b7c8d9e0f1","turn":{"id":"019a6f26-8182-7394-95a6-b7c8d9e0f102","rootTurnId":"019a6f26-8182-7394-95a6-b7c8d9e0f102","items":[],"itemsView":"notLoaded","status":"inProgress","error":null,"startedAt":1792127440,"completedAt":null,"durationMs":null}},"emittedAtMs":1792127440074}
S> {"method":"thread/status/changed","params":{"threadId":"019a6f26-7071-7283-8495-a6b7c8d9e0f1","status":{"type":"active","activeFlags":[]}},"emittedAtMs":1792127440111}
S> {"method":"item/started","params":{"threadId":"019a6f26-7071-7283-8495-a6b7c8d9e0f1","turnId":"019a6f26-8182-7394-95a6-b7c8d9e0f102","item":{"type":"userMessage","id":"41526374-8596-4a7b-8ccd-deeff0011223","clientId":null,"content":[{"type":"text","text":"解释 src/main.ts","text_elements":[]}]},"startedAtMs":1792127440112},"emittedAtMs":1792127440148}
S> {"method":"item/completed","params":{"threadId":"019a6f26-7071-7283-8495-a6b7c8d9e0f1","turnId":"019a6f26-8182-7394-95a6-b7c8d9e0f102","item":{"type":"userMessage","id":"41526374-8596-4a7b-8ccd-deeff0011223","clientId":null,"content":[{"type":"text","text":"解释 src/main.ts","text_elements":[]}]},"completedAtMs":1792127440149},"emittedAtMs":1792127440149}
S> {"method":"error","params":{"threadId":"019a6f26-7071-7283-8495-a6b7c8d9e0f1","turnId":"019a6f26-8182-7394-95a6-b7c8d9e0f102","willRetry":true,"error":{"message":"Reconnecting... 1/5","codexErrorInfo":{"responseStreamDisconnected":{"httpStatusCode":null}},"additionalDetails":"stream disconnected before completion: error sending request for url (https://chatgpt.com/backend-api/codex/responses)"}},"emittedAtMs":1792127442249}
S> {"method":"error","params":{"threadId":"019a6f26-7071-7283-8495-a6b7c8d9e0f1","turnId":"019a6f26-8182-7394-95a6-b7c8d9e0f102","willRetry":false,"error":{"message":"unexpected status 503 Service Unavailable: upstream connect error","codexErrorInfo":{"responseTooManyFailedAttempts":{"httpStatusCode":503}},"additionalDetails":null}},"emittedAtMs":1792127451249}
S> {"method":"turn/completed","params":{"threadId":"019a6f26-7071-7283-8495-a6b7c8d9e0f1","turn":{"id":"019a6f26-8182-7394-95a6-b7c8d9e0f102","rootTurnId":"019a6f26-8182-7394-95a6-b7c8d9e0f102","items":[],"itemsView":"notLoaded","status":"failed","error":{"message":"unexpected status 503 Service Unavailable: upstream connect error","codexErrorInfo":{"responseTooManyFailedAttempts":{"httpStatusCode":503}},"additionalDetails":null},"startedAt":1792127440,"completedAt":1792127451,"durationMs":11240}},"emittedAtMs":1792127451286}
S> {"method":"thread/status/changed","params":{"threadId":"019a6f26-7071-7283-8495-a6b7c8d9e0f1","status":{"type":"idle"}},"emittedAtMs":1792127451288}
C> {"id":4,"method":"turn/start","params":{"threadId":"019a6f26-7071-7283-8495-a6b7c8d9e0f1","input":[{"type":"text","text":"继续","text_elements":[]}],"effort":"medium","summary":"auto"}}
S> {"id":4,"result":{"turn":{"id":"019a6f26-9293-74a5-a6b7-c8d9e0f10213","rootTurnId":"019a6f26-9293-74a5-a6b7-c8d9e0f10213","items":[],"itemsView":"notLoaded","status":"inProgress","error":null,"startedAt":1792127440,"completedAt":null,"durationMs":null}}}
S> {"method":"turn/started","params":{"threadId":"019a6f26-7071-7283-8495-a6b7c8d9e0f1","turn":{"id":"019a6f26-9293-74a5-a6b7-c8d9e0f10213","rootTurnId":"019a6f26-9293-74a5-a6b7-c8d9e0f10213","items":[],"itemsView":"notLoaded","status":"inProgress","error":null,"startedAt":1792127440,"completedAt":null,"durationMs":null}},"emittedAtMs":1792127451325}
S> {"method":"thread/status/changed","params":{"threadId":"019a6f26-7071-7283-8495-a6b7c8d9e0f1","status":{"type":"active","activeFlags":[]}},"emittedAtMs":1792127451362}
S> {"method":"item/started","params":{"threadId":"019a6f26-7071-7283-8495-a6b7c8d9e0f1","turnId":"019a6f26-9293-74a5-a6b7-c8d9e0f10213","item":{"type":"userMessage","id":"52637485-96a7-4b8c-9dde-eff001122334","clientId":null,"content":[{"type":"text","text":"继续","text_elements":[]}]},"startedAtMs":1792127451363},"emittedAtMs":1792127451399}
S> {"method":"item/completed","params":{"threadId":"019a6f26-7071-7283-8495-a6b7c8d9e0f1","turnId":"019a6f26-9293-74a5-a6b7-c8d9e0f10213","item":{"type":"userMessage","id":"52637485-96a7-4b8c-9dde-eff001122334","clientId":null,"content":[{"type":"text","text":"继续","text_elements":[]}]},"completedAtMs":1792127451400},"emittedAtMs":1792127451400}
S> {"method":"error","params":{"threadId":"019a6f26-7071-7283-8495-a6b7c8d9e0f1","turnId":"019a6f26-9293-74a5-a6b7-c8d9e0f10213","willRetry":false,"error":{"message":"You've hit your usage limit. Try again later.","codexErrorInfo":"usageLimitExceeded","additionalDetails":null}},"emittedAtMs":1792127452040}
S> {"method":"turn/completed","params":{"threadId":"019a6f26-7071-7283-8495-a6b7c8d9e0f1","turn":{"id":"019a6f26-9293-74a5-a6b7-c8d9e0f10213","rootTurnId":"019a6f26-9293-74a5-a6b7-c8d9e0f10213","items":[],"itemsView":"notLoaded","status":"failed","error":{"message":"You've hit your usage limit. Try again later.","codexErrorInfo":"usageLimitExceeded","additionalDetails":null},"startedAt":1792127440,"completedAt":1792127440,"durationMs":690}},"emittedAtMs":1792127452077}
S> {"method":"thread/status/changed","params":{"threadId":"019a6f26-7071-7283-8495-a6b7c8d9e0f1","status":{"type":"idle"}},"emittedAtMs":1792127452079}
```

### C-f 请求级错误与探测（`f-request-errors.jsonl`）

```text
C> {"id":1,"method":"thread/start","params":{"cwd":"/Users/dev/acme-app"}}
S> {"error":{"code":-32600,"message":"Not initialized"},"id":1}
C> {"id":2,"method":"initialize","params":{"clientInfo":{"name":"tcode","title":"TCode","version":"0.1.0"}}}
S> {"id":2,"result":{"userAgent":"tcode/0.162.1 (Mac OS 26.6.2; arm64) xterm-256color (tcode; 0.1.0)","codexHome":"/Users/dev/.codex","platformFamily":"unix","platformOs":"macos"}}
C> {"method":"initialized"}
C> {"id":3,"method":"initialize","params":{"clientInfo":{"name":"tcode","version":"0.1.0"}}}
S> {"error":{"code":-32600,"message":"Already initialized"},"id":3}
C> {"id":4,"method":"turn/interrupt","params":{"threadId":"019a6f28-0000-7000-8000-000000000000","turnId":"019a6f28-0000-7000-8000-000000000001"}}
S> {"error":{"code":-32600,"message":"thread not found: 019a6f28-0000-7000-8000-000000000000"},"id":4}
C> {"id":5,"method":"model/list","params":{"limit":1,"includeHidden":false}}
S> {"id":5,"result":{"data":[{"id":"gpt-6.1-sol","model":"gpt-6.1-sol","upgrade":null,"upgradeInfo":null,"availabilityNux":null,"displayName":"GPT-6.1-Sol","description":"Latest workhorse model for coding and everyday work.","modelSpecialty":null,"hidden":false,"supportedReasoningEfforts":[{"reasoningEffort":"low","description":"Fast responses with lighter reasoning"},{"reasoningEffort":"medium","description":"Balances speed and reasoning depth for everyday tasks"},{"reasoningEffort":"high","description":"Greater reasoning depth for complex problems"},{"reasoningEffort":"xhigh","description":"Extra high reasoning depth for complex problems"}],"defaultReasoningEffort":"low","inputModalities":["text","image"],"supportsPersonality":false,"additionalSpeedTiers":["fast"],"serviceTiers":[{"id":"priority","name":"Fast","description":"2x speed, increased usage"}],"defaultServiceTier":null,"isDefault":true}],"nextCursor":"eyJvZmZzZXQiOjF9"}}
C> {"id":6,"method":"account/read","params":{"refreshToken":false}}
S> {"id":6,"result":{"account":{"type":"chatgpt","email":"dev@example.com","planType":"pro"},"requiresOpenaiAuth":true}}
```
