# Claude Code CLI stream-json 双向流与 stdio 控制协议规范（TKCode 实现用）

> 适用：TKCode（C#/.NET 10 NativeAOT）的 `ClaudeRuntime` 直接启动用户本机的 `claude` 可执行文件，用 `-p --input-format stream-json --output-format stream-json` 双向收发 NDJSON，并通过 stdio 控制通道（`control_request` / `control_response`）完成握手、审批、中断和在线改配置。不使用任何官方 SDK，也不引入 Node。
> 编写日期：2026-10-16。基线版本：Claude Code **2.1.301**（本机 `/Users/token/.npm-global/bin/claude`）。字段名、帧类型、subtype、错误文本一律保留英文原文。
> 设计背景见 [../design/external-agent-runtimes.md](../design/external-agent-runtimes.md)。

## 0. 来源与可信度标注

控制协议**没有公开文档**。官方文档只覆盖 stdout 的一部分帧和 CLI 参数；控制通道的线上格式来自官方 SDK 源码和类型定义。本文每条规则都标注来源：

| 标记 | 来源 |
|---|---|
| **[Doc:…]** | 官方文档 `https://code.claude.com/docs/en/<page>.md`，2026-10-16 抓取：`headless`、`cli-reference`、`authentication`、`setup`、`agent-sdk/overview`、`agent-sdk/typescript`、`agent-sdk/python`、`agent-sdk/streaming-output`、`agent-sdk/user-input`、`agent-sdk/sessions`、`agent-sdk/permissions`、`agent-sdk/streaming-vs-single-mode` |
| **[Py:…]** | 官方 Python Agent SDK `anthropics/claude-agent-sdk-python` v0.2.168（commit `aada024`，2026-10-15），路径相对 `src/claude_agent_sdk/`。主要读了 `_internal/query.py`、`_internal/transport/subprocess_cli.py`、`_internal/message_parser.py`、`_internal/client.py`、`types.py`。**这是控制协议事实上的线上规格** |
| **[TS:sdk.d.ts]** | 官方 TypeScript Agent SDK `@anthropic-ai/claude-agent-sdk` **0.3.301** 的 `sdk.d.ts`（npm registry 标注 `claudeCodeVersion: 2.1.301`，与本机 CLI 同版本）。类型注释非常详细，很多语义以它为准 |
| **[zeron:…]** | zeron 的生产级 Claude 驱动 `crates/harness/src/claude/{mod.rs, wire.rs, normalize.rs, catalog.rs, discovery.rs}` 和 `docs/research/harness.md`。注释里标了"verified live against 2.1.2xx"的条目视为实测 |
| **[CLI:help]** | 本机 `claude --version` / `claude --help` / `claude auth status` 的输出（2.1.301） |
| **[CLI:bin]** | 对本机 2.1.301 可执行文件做 `strings` 后检索到的源码片段（只读，没有运行任何模型轮次）。能确认隐藏参数、枚举值和帧的构造代码 |
| **[推断]** | 以上来源都不能直接证实，根据相邻证据推断。实现时按"宽松解析"处理，并用录制的真实 JSONL 校正 |

夹具（附录 A）里的每一帧都标了哪些字段已核实、哪些是推断。

---

## 1. 总体模型

```
TKCode.AgentHost (ClaudeRuntime)
   │  stdin : NDJSON  ──▶  user 消息、control_request（TKCode 发起）、control_response（回答 CLI）
   │  stdout: NDJSON  ◀──  system / stream_event / assistant / user / result / rate_limit_event /
   │                        command_lifecycle / control_request（CLI 发起）/ control_response（回答 TKCode）/ …
   │  stderr: 文本      ◀──  诊断日志，只保留尾部若干行用于报错
   ▼
claude -p --input-format stream-json --output-format stream-json --verbose …
```

- **一个进程 = 一个会话**。进程在 stdin 关闭之前一直存活，可以跑任意多个轮次（turn）。stdin EOF 后 CLI 跑完当前工作再退出。[Py:query.py `_track_task_lifecycle` 注释 "the CLI in stream-json mode only exits on stdin EOF"；zeron:mod.rs `StdinMsg::Close`]
- stdout 上混着三类流量：**对话帧**（system/stream_event/assistant/user/result…）、**CLI 发起的控制请求**（`can_use_tool` 等，必须回答）、**对 TKCode 控制请求的应答**。都按行分帧，用顶层 `type` 分派。
- **一个轮次以且仅以一个 `result` 帧结束**。[TS:sdk.d.ts `SDKResultMessage` "The CLI emits exactly one result message per turn … treat it as the turn-complete signal"] 但 `result` 之后还可能有信息性帧（task 通知、`session_state_changed`、`command_lifecycle completed`），也可能紧接着开始下一个轮次（排队的消息、后台任务唤醒），见 §9。

---

## 2. 启动进程

### 2.1 可执行文件定位

沿用设计文档 §4 的查找顺序（`TKCODE_CLAUDE_PATH` → PATH → 登录 shell PATH 快照 → 已知目录）。补充事实：

- npm 安装的包里 `bin/claude.exe` 本身就是**原生可执行文件**（macOS 上也叫 `claude.exe`，约 250 MB），`~/.npm-global/bin/claude` 是指向它的符号链接。[CLI:本机 `ls -la`]
- Python SDK 的已知位置列表：`~/.npm-global/bin/claude`、`/usr/local/bin/claude`、`~/.local/bin/claude`、`~/node_modules/.bin/claude`、`~/.yarn/bin/claude`、`~/.claude/local/claude`；Windows 只认 `~/.local/bin/claude.exe`。[Py:transport/subprocess_cli.py `_find_cli`]
- **Windows 拒绝启动 `.cmd`/`.bat`**（npm 的 `claude.cmd` shim）：cmd.exe 会重新解析命令行，参数里的元字符可以注入命令。必须找到原生 `claude.exe`。[Py:subprocess_cli.py `_reject_windows_batch_cli`]

### 2.2 TKCode 使用的 argv

固定部分（每个会话都带）：

```
claude
  -p
  --input-format stream-json
  --output-format stream-json
  --verbose
  --include-partial-messages
  --replay-user-messages
  --permission-prompt-tool stdio
  --thinking-display summarized
  --forward-subagent-text
```

可选部分（按会话配置追加）：

```
  --model <alias|id>              # 例：opus、claude-opus-5-5、claude-opus-5-5[1m]
  --effort <low|medium|high|xhigh|max>
  --permission-mode <default|acceptEdits|plan|auto|dontAsk|bypassPermissions>
  --session-id=<uuid>             # 新建会话：TKCode 预先生成 UUID
  --resume=<session-id>           # 恢复会话（与 --session-id 互斥，除非同时带 --fork-session）
  --fork-session                  # 恢复时分叉出新 ID
  --mcp-config <json-or-path>     # TKCode 自己的 MCP 服务器
  --settings <json-or-path>       # 例：{"alwaysThinkingEnabled":true}
  --setting-sources <user,project,local>
  --add-dir <dir> …
  --append-system-prompt <text>
```

逐项说明：

| 参数 | 说明 | 来源 |
|---|---|---|
| `-p` / `--print` | 非交互模式。`--input-format`、`--output-format`、`--include-partial-messages` 都只在 `-p` 下生效。**Python SDK 实际上不传 `-p`**（只传 `--output-format stream-json --verbose … --input-format stream-json`），CLI 在 stdout 非 TTY 时同样进入非交互模式；TKCode 显式传 `-p`，与 zeron 一致 | [CLI:help]；[Py:subprocess_cli.py `_build_command`]；[zeron:mod.rs `launch_args`] |
| `--verbose` | **必填**。`-p` 配 `--output-format=stream-json` 而没有 `--verbose` 时，CLI 向 stderr 打印 `Error: When using --print, --output-format=stream-json requires --verbose` 并以 1 退出 | [CLI:bin]；[zeron:mod.rs 注释] |
| `--include-partial-messages` | 输出 `stream_event`（原始 Messages API 流事件），这是 textDelta/thinkingDelta/工具参数增量的唯一来源 | [CLI:help]；[Doc:headless §Stream responses] |
| `--replay-user-messages` | 把 stdin 收到的 user 消息回显到 stdout（`isReplay:true`），用于确认 CLI 已收到。要求两个 format 都是 stream-json | [CLI:help]；[Doc:cli-reference] |
| `--permission-prompt-tool stdio` | **隐藏参数**（`--help` 不显示）。值 `stdio` 是特殊值：权限询问走控制通道的 `can_use_tool`，而不是某个 MCP 工具。Python SDK 在设置了 `can_use_tool` 回调时自动传这个值。不传的话，`-p` 模式下需要询问的操作会被直接拒绝 | [CLI:bin `permissionPromptTool:({permissionPromptTool:e})=>e==="stdio"?…`、`.hideHelp()`]；[Py:types.py `_configure_can_use_tool`]；[zeron:mod.rs "validated live against 2.1.228"]；[Doc:headless "In a -p run with no host, these requests are denied"] |
| `--thinking-display summarized` | **隐藏参数**，取值 `summarized` / `omitted` / `highlights`。新模型默认不给可读的 thinking 文本，要显示思考内容必须传 `summarized` | [CLI:bin `f7e=["summarized","omitted","highlights"]`]；[Py:subprocess_cli.py `--thinking-display`]；[zeron:mod.rs] |
| `--forward-subagent-text` | 子 Agent 的 text/thinking 块也作为带 `parent_tool_use_id` 的 assistant/user 帧转发（默认只转发 tool_use/tool_result）。≥ 2.1.211。也可以在 `initialize` 里传 `forwardSubagentText:true` | [CLI:help]；[Doc:headless §Follow subagent messages]；[Py:query.py `initialize`] |
| `--model` | 别名（`opus`/`sonnet`/`haiku`/`fable`/`default`）或完整 ID；1M 上下文用 `<model>[1m]` 后缀 | [CLI:help]；[zeron:mod.rs `model_arg`] |
| `--effort` | `low`/`medium`/`high`/`xhigh`/`max`；文档还列了 `ultracode`（≥ 2.1.203）。不支持 `xhigh` 的模型由 TKCode 降到 `max`（zeron 的做法） | [CLI:help]；[Doc:cli-reference]；[zeron:catalog.rs `to_effort`] |
| `--permission-mode` | `--help` 列出 `acceptEdits, auto, bypassPermissions, manual, dontAsk, plan`。`manual` 是 `default` 的别名（≥ 2.1.200），两者都可用；内部值是 `default`，`system/init.permissionMode` 报告 `default`。**必须显式传**：不传时会话的起始模式可能是 `auto` | [CLI:help]；[Doc:cli-reference]；[CLI:bin `function Fh(e){return e==="manual"?"default":e}`]；[Doc:headless "which can be auto, so pass the one you want"] |
| `--session-id=<uuid>` | 必须是合法 UUID，否则 `Error: Invalid session ID. Must be a valid UUID.`；已存在时 `Error: Session ID <id> is already in use.`；与 `--resume`/`--continue` 同用必须加 `--fork-session` | [CLI:help]；[CLI:bin] |
| `--resume=<id>` | 用 `--flag=value` 形式：`--resume` 的值是可选的，两段式写法里以 `-` 开头的值会被解析成新参数，造成参数注入 | [Py:subprocess_cli.py 注释]；[zeron:mod.rs] |
| `--mcp-config` | 可接 JSON 字符串或文件路径，与 settings 里的 MCP 配置**合并**（加 `--strict-mcp-config` 才排他）。`-p` 下 CLI 会等待 pending 的服务器连接（最多 `MCP_TIMEOUT`，默认 30s，≥ 2.1.221） | [Doc:cli-reference]；[zeron:mod.rs `mcp_config_arg`] |
| `--settings` | JSON 字符串或文件路径，覆盖同名键 | [Doc:cli-reference] |
| `--setting-sources` | 逗号分隔 `user,project,local`。**不传时加载全部来源**（与用户平时的 Claude Code 一致）；TKCode 默认不传 | [Doc:cli-reference]；[Py:subprocess_cli.py 只在非 None 时传] |
| `--system-prompt` | **TKCode 不传**。注意 Python SDK 在 `system_prompt=None` 时会传 `--system-prompt ""`，等于清空 Claude Code 自带的系统提示词；TKCode 要保留 Claude Code 的默认行为，只在需要时用 `--append-system-prompt` | [Py:subprocess_cli.py `_build_command`] |
| `--bare` | 不推荐默认使用：会跳过 hooks、skills、CLAUDE.md、插件、MCP 自动发现，且**只认 `ANTHROPIC_API_KEY`/apiKeyHelper，不读 OAuth/钥匙串**，订阅用户会无法认证 | [CLI:help]；[Doc:headless] |
| `--permission-prompts none` | 无人值守时让所有需要询问的请求自动拒绝（≥ 2.1.259）。TKCode 的"自动放行"开关不用它，用 `bypassPermissions` | [CLI:help]；[Doc:headless] |

`--dangerously-skip-permissions` 等价于 `--permission-mode bypassPermissions`。[Doc:cli-reference] zeron 自动放行时两者都传；TKCode 只传 `--permission-mode bypassPermissions` 即可。

### 2.3 环境变量

以继承的环境为基础，做如下调整：

| 变量 | 处理 | 说明 / 来源 |
|---|---|---|
| `CLAUDECODE` | **删除** | 防止 TKCode 本身从 Claude Code 终端启动时，子进程误判为嵌套会话。[Py:subprocess_cli.py `inherited_env = {k: v … if k != "CLAUDECODE"}`] |
| `CLAUDE_CODE_ENTRYPOINT` | 设为 `sdk-cli`（或不设） | 不设时，CLI 在非交互模式下自己设为 `sdk-cli`。合法值里 `sdk-cli`/`sdk-ts`/`sdk-py` 都归类为 SDK；**不要冒充 `sdk-py`/`sdk-ts`**（Python SDK 设 `sdk-py`） [CLI:bin `a.set("CLAUDE_CODE_ENTRYPOINT",e?"sdk-cli":"cli")`；Py:subprocess_cli.py] |
| `CLAUDE_CODE_SDK_READS_SESSION_STATE` | 设为 `1` | 让 CLI 输出带 `sdk_host_only:true` 的 `system/session_state_changed` 帧（`running` / `requires_action` / `idle`），用来判断整次运行是否真正空闲（§9.4）。[Py:subprocess_cli.py `_SDK_READS_SESSION_STATE_ENV`；CLI:bin] |
| `PWD` | 设为会话 cwd | 与 `ProcessStartInfo.WorkingDirectory` 一致。[Py:subprocess_cli.py] |
| `PATH` | 把可执行文件所在目录放在最前 | 设计文档 §4 |
| `CLAUDE_AGENT_SDK_VERSION` | 不设 | 这是 SDK 的自报版本，TKCode 不是 SDK |
| `CLAUDE_CODE_PRINT_BG_WAIT_CEILING_MS` | 不设 | stdin 关闭后等待后台工作的上限，默认 600000ms，`0` 表示不限。[Doc:headless；Py:query.py] |
| `CLAUDE_CONFIG_DIR` | 不改 | 若用户设置了它，会话存储在 `$CLAUDE_CONFIG_DIR/projects/` 下 [Doc:agent-sdk/sessions] |

TKCode 不读写任何凭据环境变量（`ANTHROPIC_API_KEY` 等），原样继承。

### 2.4 进程参数（.NET）

- `RedirectStandardInput/Output/Error = true`，`UseShellExecute = false`，`WorkingDirectory = cwd`。
- **编码**：`StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)`。Windows 上默认编码可能写 BOM，CLI 会把第一行解析失败。`StandardOutputEncoding = StandardErrorEncoding = UTF8`。最好直接操作 `BaseStream` 并自己做 UTF-8 编解码。
- POSIX 上用新进程组启动（`setsid`），便于整组发信号；Windows 用 Job Object（设计文档 §8）。
- stderr 必须持续读取（否则管道写满会阻塞 CLI），只保留尾部若干行，脱敏后用于错误提示。[Py:subprocess_cli.py `_handle_stderr`；zeron:mod.rs `StderrTail`]

---

## 3. 传输与分帧

### 3.1 NDJSON

- 每行一个 JSON 对象，以 `\n` 结尾，UTF-8。读取端按 `\n` 切分，每行先 `Trim()`（去掉 CRLF 的 `\r`）。[Py:subprocess_cli.py `_parse_stdout_line`]
- **空行跳过**；**不以 `{` 开头的行跳过并记 debug 日志**（某些构建会往 stdout 打 `[SandboxDebug] …` 之类的非 JSON 行）。[Py:subprocess_cli.py `_parse_stdout_line` (#347)]
- 以 `{` 开头但解析失败的行：Python SDK 视为致命错误（完整分帧后不可能被后续数据补全）；zeron 选择跳过。TKCode：记录错误并**跳过该行**，不中断会话；进程退出时缓冲区里残留的半行直接丢弃。[Py；zeron:mod.rs]
- **未知 `type`、未知 `subtype`、未知字段一律忽略**。新版本 CLI 经常加帧类型（见 §8.10）。[Py:message_parser.py "Forward-compatible: skip unrecognized message types"；zeron:wire.rs `Frame::Other`]

### 3.2 大行

- 单行可能非常大：`Read` 读取图片后的 `tool_result` 带 base64 图片、`tool_use_result` 带整个文件内容、`initialize` 应答带全部命令与模型列表。
- Python SDK 默认单行上限 1 MiB（超限抛错），这个值对 TKCode 太小。TKCode 使用流式读取（`PipeReader` 或手写的分块拼接，不要用单次固定缓冲），单行上限设为 **64 MiB**；超过上限时丢弃该行并报协议错误，不终止进程。[Py:subprocess_cli.py `_DEFAULT_MAX_BUFFER_SIZE`、`_LineFramer`]
- 分块读取时**绝不能**对块做 trim：块边界可能落在 JSON 字符串内部。[Py:subprocess_cli.py `_LineFramer` 注释]

### 3.3 写入

- 所有 stdin 写入经过**单一写入者**串行化（一个 Channel + 一个写任务）：user 消息、TKCode 的控制请求、对 CLI 控制请求的应答共用同一个管道，交错写入会破坏行。[Py:subprocess_cli.py `_write_lock`；zeron:mod.rs `stdin_writer`]
- 每条写 `json + "\n"` 后立即 `Flush`。
- 写入失败（EPIPE，进程已退出）：容忍并记录，转入"进程已退出"处理，不抛给 UI。[zeron:mod.rs `stdin_writer`]
- JSON 序列化用 System.Text.Json 源生成器（AOT 安全）；输出紧凑格式，字符串里的换行必须转义（默认如此）。

### 3.4 stdin 必须保持打开

- **整个会话期间 stdin 不能关闭。** 关闭 stdin 后：CLI 跑完当前轮次和后台工作就退出；之后的 `can_use_tool`、hook 回调、SDK MCP 请求都会在 CLI 侧失败（"Stream closed"），即需要审批的工具会被拒绝。[Py:query.py `_has_bidirectional_needs`、`wait_for_result_and_end_input` 注释 (#1088)；设计文档 §6]
- 关闭 stdin 只在拆除会话时做（§10.3）。

---

## 4. 控制协议信封

控制通道是对称的：任一方都可以发起请求，另一方必须且只能回答一次。

### 4.1 请求

```json
{"type":"control_request","request_id":"<发起方生成，在其在途请求中唯一>","request":{"subtype":"<subtype>", "...":"..."}}
```

- `request_id` 由发起方选择。Python SDK 用 `req_{计数}_{8位hex}`；zeron 用 `zeron_initialize`、`zeron_stop_1` 等。CLI 发起的请求 ID 由 CLI 生成，TKCode 原样回填。[Py:query.py `_send_control_request`；TS:sdk.d.ts `SDKControlRequest`]
- TKCode 发起的 ID 用 `tkcode_<用途>_<序号>`，便于日志检索。

### 4.2 应答

成功：

```json
{"type":"control_response","response":{"subtype":"success","request_id":"<原 ID>","response":{ /* 按请求 subtype 而定；仅确认时为 {} 或缺省 */ }}}
```

失败：

```json
{"type":"control_response","response":{"subtype":"error","request_id":"<原 ID>","error":"<可读描述>"}}
```

- CLI 的错误应答可能带 `error_code` 字段。[CLI:bin `function tB(e,n,t){return{type:"control_response",response:{subtype:"error",request_id:e,error:n,...t!==void 0&&{error_code:t}}}}`]
- `initialize` 的成功应答在信封层（与 `response` 并列）带 `pending_permission_requests` 和 `pending_user_dialog_requests`：本进程已发出、尚未解决的 `can_use_tool` / `request_user_dialog` 请求（≥ 2.1.268 总是带，可能为空数组）。全新进程里总是空的；TKCode 不做重连，可忽略，但解析时要容忍。[TS:sdk.d.ts `ControlResponse`]
- 等待应答要有超时：`initialize` 60 秒（MCP 服务器启动可能很慢；Python SDK 取 `max(CLAUDE_CODE_STREAM_CLOSE_TIMEOUT, 60s)`），其他请求 60 秒。超时视为失败，但不杀进程。[Py:query.py `_send_control_request`、client.py]
- 进程退出或读循环出错时，所有在途请求立即以错误完成，不要等超时。[Py:query.py `_read_messages` except 分支]

### 4.3 取消

```json
{"type":"control_cancel_request","request_id":"<要撤回的请求 ID>"}
```

- 任一方都可以撤回**自己发起**的在途请求（典型场景：轮次被中断后，CLI 撤回尚未回答的 `can_use_tool`）。发送方立即停止等待，之后到达的该 ID 的应答被忽略；对取消本身**没有应答**。[TS:sdk.d.ts `SDKControlCancelRequest`]
- TKCode 收到后：关闭对应的审批对话框，**不再发送应答**（Python SDK 取消处理任务、不写应答）。[Py:query.py `control_cancel_request` 分支和 `_handle_control_request` 的 cancelled 分支]

### 4.4 未知 subtype

- CLI 发来 TKCode 不认识的 `control_request.subtype`：回 `subtype:"error"`，`error` 写 `Unsupported control request subtype: <x>`。不能不回——CLI 会一直阻塞等待。[Py:query.py `_handle_control_request` else 分支]
- 例外：`request_user_dialog` 只会发给在 `initialize.supportedDialogKinds` 声明过该 kind 的客户端；收到未声明的 kind **不要回答**（error 应答会被丢弃，绝不能回 `{behavior:"cancelled"}`）。TKCode 不声明任何 kind，正常不会收到。[TS:sdk.d.ts `SDKControlRequestUserDialogRequest`]

---

## 5. TKCode → CLI 的控制请求

### 5.1 `initialize`（必发，进程启动后的第一条 stdin 行）

TKCode 发送：

```json
{"type":"control_request","request_id":"tkcode_init_1","request":{"subtype":"initialize","hooks":null}}
```

可选字段（[TS:sdk.d.ts `SDKControlInitializeRequest`]；[Py:query.py `initialize`]）：

| 字段 | TKCode 用法 |
|---|---|
| `hooks` | `null`（TKCode 不注册 SDK 侧 hook；Python SDK 无 hook 时也发 `null`） |
| `forwardSubagentText` | 已用 `--forward-subagent-text` 参数，可不传；两者等价 |
| `perTaskStopAffordance` | **不传**。声明后，开放输入会话上的 `interrupt` 只结束当前轮次、保留后台子 Agent/工作流（需要 UI 提供逐任务的 `stop_task`）；不声明时 interrupt 会连同后台 Agent 一起停掉（后台 shell 两种情况都保留）。TKCode 首版语义是"停止 = 全部停止"。zeron 声明了它 [zeron:wire.rs `initialize_request_line`，"verified live against 2.1.285"] |
| `title` | 可传会话标题；传了就跳过自动生成标题，恢复已有会话时无效 |
| `agents`、`skills`、`appendSystemPrompt`、`systemPrompt`、`jsonSchema`、`excludeDynamicSections`、`systemPromptSnapshot`、`promptSuggestions`、`supportedDialogKinds`、`sdkMcpServers`… | 首版不用 |

成功应答 `response.response` 的字段（[TS:sdk.d.ts `SDKControlInitializeResponse`]；[zeron:catalog.rs、mod.rs `parse_initialize_commands`]）：

| 字段 | 类型 | 用途 |
|---|---|---|
| `commands` | `{name, description, argumentHint, aliases?, builtin?}[]` | 斜杠命令列表（不带 `/`）。`argumentHint` 可能为空串 |
| `agents` | `{name, description, model?}[]` | 可用子 Agent |
| `output_style` / `available_output_styles` | string / string[] | 输出风格 |
| `models` | `ModelInfo[]` | 模型目录：`value`（传给 `--model`/`set_model` 的值）、`resolvedModel?`（别名解析后的真实 ID）、`displayName`、`description`、`supportsEffort?`、`supportedEffortLevels?`、`supportsAdaptiveThinking?`、`supportsFastMode?`、`supportsAutoMode?`。`value:"default"` 那一行是默认模型 |
| `account` | `{email?, organization?, subscriptionType?, tokenSource?, apiKeySource?, apiProvider?}` | 账号信息（`apiProvider`：`firstParty`/`bedrock`/`vertex`/`foundry`/`anthropicAws`/`anthropicGoogleCloud`/`mantle`/`gateway`） |
| `claude_code_version` | string? | 运行轮次的进程版本 |
| `fast_mode_state` / `fast_mode_disabled_reason` | 可选 | |
| `hooks_applied`、`plugins_applied`、`sdk_mcp_manifests_parked`、`unavailable_models`、`current_model`… | 可选 | 忽略 |

要点：

- `initialize` 应答通常是进程的**第一行 stdout**（之前可能有 SessionStart hook 帧，解析时不要假设），在任何 API 请求之前到达，不触发模型轮次；所以可以起一个短命进程只做 `initialize` 来发现模型和命令（zeron 超时 10 秒，结果缓存 2 分钟，按凭据和可执行文件身份失效）。[zeron:mod.rs `probe_initialize`、discovery.rs]
- 探测进程读到应答后直接关闭 stdin 并按 §10.3 拆除。
- **会话进程里，先等 `initialize` 应答，再写第一条 user 消息**（Python SDK 的顺序）。zeron 是连写两行不等待，也能工作；等待的好处是：失败时能拿到明确的错误，而不是一个莫名的 result。[Py:client.py `await query.initialize()` 之后才写 prompt]
- 恢复会话失败（ID 不存在等）时，CLI 可能在 `initialize` 应答之前就输出错误 `result` 并退出；等待 `initialize` 的逻辑必须同时监听 result 帧和进程退出。[Py:query.py "an `initialize` still in flight when the CLI reports an error result during startup (e.g. a refused resume)"]

### 5.2 `interrupt`（结束当前轮次，进程保留）

```json
{"type":"control_request","request_id":"tkcode_int_3","request":{"subtype":"interrupt"}}
```

- 可选 `cancel_queued:true`：同时取消所有已排队但未开始的带 uuid 的 user 消息（每条发终态 `cancelled` 的 `command_lifecycle`，并列在应答的 `cancelled` 里）。由 `system/init.capabilities` 中的 `interrupt_cancel_queued_v1` 声明；旧 CLI 忽略此字段。TKCode 的"停止"按钮发 `cancel_queued:true`（停止 = 连同排队消息一起停）。[TS:sdk.d.ts `SDKControlInterruptRequest`；zeron:wire.rs `stop_turn_request_line`]
- 成功应答（`interrupt_receipt_v1`，≥ 2.1.205）：`{"still_queued":[<uuid>…]}`，带 `cancel_queued:true` 时还有 `"cancelled":[<uuid>…]`。更早的 CLI 回空的成功应答。[TS:sdk.d.ts `SDKControlInterruptResponse`]
- **顺序**：正常中断时，应答先于被中断轮次的 `result` 写出；轮次在中断处理中崩溃时，错误 result 可能先到。TKCode 不能依赖二者的相对顺序。[TS:sdk.d.ts]
- 被中断的轮次照常输出一个 `result`（见 §8.5.3）。**空闲时（没有轮次在跑）发 interrupt，只有成功应答，没有 result**——TKCode 收到应答后若 3 秒内没有 result，就视为该次停止已结算。[zeron:mod.rs `STOP_SETTLE`]
- 中断时若有 `can_use_tool` 在等审批，CLI 会发 `control_cancel_request` 撤回它（§4.3）。

### 5.3 `set_permission_mode`

```json
{"type":"control_request","request_id":"tkcode_mode_1","request":{"subtype":"set_permission_mode","mode":"acceptEdits"}}
```

`mode` ∈ `default`/`acceptEdits`/`bypassPermissions`/`plan`/`dontAsk`/`auto`。成功应答为空对象。切换到 `bypassPermissions` 需要启动时允许（`--allow-dangerously-skip-permissions` 或以该模式启动），否则返回错误；会话级"自动放行"开关若要在线切换，启动时加 `--allow-dangerously-skip-permissions`。[TS:sdk.d.ts `SDKControlSetPermissionModeRequest` "requires allowDangerouslySkipPermissions"；Doc:cli-reference] 模式变化也会出现在 `system/status.permissionMode` 和下一个 `system/init.permissionMode`。

### 5.4 `set_model`

```json
{"type":"control_request","request_id":"tkcode_model_2","request":{"subtype":"set_model","model":"claude-sonnet-5"}}
```

`model` 省略、为 `null` 或 `"default"` 时恢复会话默认模型。对后续轮次生效。[TS:sdk.d.ts `SDKControlSetModelRequest`；Py:query.py `set_model`；zeron:mod.rs 在线改模型]

### 5.5 `apply_flag_settings`（在线改 effort 等）

```json
{"type":"control_request","request_id":"tkcode_settings_3","request":{"subtype":"apply_flag_settings","settings":{"effortLevel":"high"}}}
```

替换"命令行参数层"的设置（只影响本会话，不写文件）。值为 `null` 表示去掉覆盖、回落到用户自己的设置。zeron 用它在线改 `effortLevel`、`fastMode`、`alwaysThinkingEnabled`、`ultracode`（"verified live on 2.1.286 through get_settings"）。[TS:sdk.d.ts `SDKControlApplyFlagSettingsRequest`；zeron:mod.rs `flag_settings`]

### 5.6 其他可用请求（首版可不实现）

| subtype | 字段 | 用途 | 来源 |
|---|---|---|---|
| `set_max_thinking_tokens` | `max_thinking_tokens?: number\|null`、`thinking_display?: "summarized"\|"omitted"\|"highlights"\|null` | 在线改 thinking | [TS] |
| `get_context_usage` | — | 上下文用量分类明细 | [TS；Py] |
| `mcp_status` / `mcp_reconnect {serverName}` / `mcp_toggle {serverName, enabled}` | 注意 `serverName` 是驼峰 | MCP 管理 | [Py:types.py] |
| `stop_task {task_id}` | | 停止单个后台任务 | [Py；TS] |
| `cancel_async_message {message_uuid}` | | 撤回一条已排队的 user 消息 | [TS] |
| `rewind_files {user_message_id}` | | 文件回退，需要 `CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING=true` | [Py] |
| `list_models`、`get_settings`、`background_tasks`、`reload_skills`… | | | [TS] |

---

## 6. CLI → TKCode 的控制请求

### 6.1 `can_use_tool`（审批，核心）

```json
{"type":"control_request","request_id":"<CLI 生成>","request":{
  "subtype":"can_use_tool",
  "tool_name":"Bash",
  "input":{"command":"rm -rf build","description":"Remove build output"},
  "tool_use_id":"toolu_01…",
  "permission_suggestions":[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"rm -rf build"}],"behavior":"allow","destination":"localSettings"}],
  "blocked_path":null,
  "decision_reason":"…", "decision_reason_type":"…",
  "title":"…", "display_name":"…", "description":"…",
  "agent_id":"…",
  "suppress_always_allow_rule":false, "default_to_no":false, "requires_user_interaction":false,
  "classifier_approvable":true, "matched_ask_rule":{"source":"…","tool_name":"…","rule_content":"…"},
  "mcp_server":{…}
}}
```

字段（[TS:sdk.d.ts `SDKControlPermissionRequest`]；[Py:types.py `SDKControlPermissionRequest`]）：

| 字段 | 必有 | 说明 |
|---|---|---|
| `tool_name`、`input`、`tool_use_id` | 是 | `tool_use_id` 对应 assistant 帧里 `tool_use.id`，用它关联 UI 上的工具卡片 |
| `permission_suggestions` | 否 | `PermissionUpdate[]`，CLI 建议的"以后不再询问"规则（§15） |
| `blocked_path` | 否 | 触发询问的路径 |
| `decision_reason` / `decision_reason_type` | 否 | 为什么需要询问；`decision_reason` **可能含 ANSI 转义，显示前要清洗** |
| `title` / `display_name` / `description` | 否 | 宿主可直接展示的文案 |
| `agent_id` | 否 | 子 Agent 发起的请求 |
| `suppress_always_allow_rule` | 否 | 为 true 时**不要**提供"本会话/永久允许"选项 |
| `default_to_no` | 否 | 为 true 时默认焦点放在"拒绝" |
| `requires_user_interaction` | 否 | 审批卡片本身就是交互面（如 `AskUserQuestion`、计划审阅），不能提供一键批准 |

要点：

- **CLI 会一直阻塞等待应答**，没有超时。[Doc:agent-sdk/user-input "The callback can stay pending indefinitely"] 每个 `can_use_tool` 都必须得到应答或被 `control_cancel_request` 撤回。
- 读循环不能被审批阻塞：收到请求后交给后台任务去等 UI，读循环继续处理后续帧（期间可能有子 Agent 帧、`tool_progress`、取消请求）。[zeron:mod.rs `handle_control_request` 用 `tokio::spawn`；Py:query.py `_spawn_control_request_handler`]
- 自动放行的调用（allow 规则、`acceptEdits`、`bypassPermissions` 等）**不会**发 `can_use_tool`。[Doc:agent-sdk/user-input]
- `AskUserQuestion` **总是**走 `can_use_tool`：它是提问而不是审批，TKCode 走 `input/request`，用户回答后以 allow 回复，`updatedInput` = 原 `input` 加 `answers`（键为问题文本，值为所选选项的 `label`；多选为 label 数组或用 `", "` 连接）。[Doc:agent-sdk/user-input；zeron:mod.rs `updated_input_with_answers`] 细节见下面的 §6.1.1。

#### 6.1.1 `AskUserQuestion` 的完整约定（CLI 2.1.302，2026-10-18 核对；TKCode 实现见 docs/design/ask-user.md §8）

- **启用条件**：`-p` 模式下只有设置了 `--permission-prompt-tool` 且不为 `none` 时才启用（TKCode 传 `stdio`）。子 Agent 里不可用。[Doc:agent-sdk/user-input Limitations；CLI 包]
- **请求**：`can_use_tool` 带 `requires_user_interaction: true`。工具的 `checkPermissions` 总是返回 `ask`，所以任何权限模式（包括 `bypassPermissions`）下都会发来。
- **input**：`{questions: [{question, header, options: [{label, description, preview?}], multiSelect}], metadata?}`。限制：1–4 个问题、每题 2–4 个选项、`header` 不超过 12 字符、问题文本互不相同、同一题内 label 互不相同。模型被告知不要自己加“Other”。
  - `preview` 只在设置了 `CLAUDE_CODE_QUESTION_PREVIEW_FORMAT=markdown|html` 时出现。`CLAUDE_CODE_ENTRYPOINT` 以 `sdk-` 开头时没有默认值，TKCode 用 `sdk-cli`，所以**没有 preview**。
  - 扩展形态（`CLAUDE_CODE_QUESTION_EXTENDED`：顶层 `title`，每题 `kind: choice|text|number` 等）和 `CLAUDE_CODE_QUESTION_OPTIONAL_DESCRIPTIONS` TKCode 都**不开启**。
- **回答**：`{"behavior":"allow","updatedInput":{…原 input…,"answers":{"<问题原文>":"<值>"}},"toolUseID":…}`。
  - 值：单选为 label；多选用 `", "` 连接（label 本身含 `", "` 或 `"` 时加引号，与 CLI 的编码一致；直接传数组时 CLI 会用 `","` 连接，没有空格）；用户自己输入的文本原样作为值。
  - 还可以带 `annotations`（`{<问题>: {notes, preview}}`）、`response`（用一段自由文字代替回答）、`followUp`。TKCode 不发送这些字段。
  - 本地 stdio 会话里非空 `updatedInput` 直接替换 input；托管 / 远程会话的严格模式只接受新增 `answers`、`annotations`、`response`、`followUp`，其他改动会被拒绝。TKCode 只追加 `answers`，两种模式都能通过。
- **拒绝回答**：`{"behavior":"deny","message":…}`，message 作为工具错误交给模型。TKCode 使用 ask-user.md §5.4 的拒绝文本。
- **模型看到的结果**：`Your questions have been answered: "Q"="A", …. You can now continue with these answers in mind.`；有不属于选项的答案时是 `The user answered: …. Read the answers carefully …`；全部未答时是 `The user did not answer the questions.`。
- **超时**：设置项 `askUserQuestionTimeout`（`60s`/`5m`/`10m`/`never`，默认 `never`）。触发时结果带 `afkTimeoutMs`。TKCode 不设置。

应答格式（`response.response`）：

```json
{"behavior":"allow","updatedInput":{…},"updatedPermissions":[…],"toolUseID":"toolu_01…","decisionClassification":"user_temporary"}
{"behavior":"deny","message":"用户拒绝了这次操作","interrupt":false,"toolUseID":"toolu_01…","decisionClassification":"user_reject"}
```

| 字段 | 说明 | 来源 |
|---|---|---|
| `behavior` | `allow` / `deny` | [TS:`PermissionResult`] |
| `updatedInput` | allow 时**总是带上**（未修改时回传原 `input`）。CLI 收到空对象或缺省时回落到原输入，但旧版 CLI 曾要求必填；Python SDK 和 zeron 都总是带 | [Py:query.py；zeron:wire.rs `allow_response`；CLI:bin `e.updatedInput&&Object.keys(e.updatedInput).length>0?e.updatedInput:…`] |
| `updatedPermissions` | allow 时可选，`PermissionUpdate[]`，用于"本会话允许"。其中的 `setMode` 更新会直接切换会话权限模式 | [TS；CLI:bin `findLast((T)=>T.type==="setMode")`] |
| `message` | deny 时必填，模型会看到它并可能换个做法 | [Doc:agent-sdk/user-input] |
| `interrupt` | deny 时可选，true 表示拒绝的同时中止整个轮次 | [Py；CLI:bin "SDK permission prompt deny+interrupt … abort()"] |
| `toolUseID` | 可选，回填请求的 `tool_use_id` | [TS] |
| `decisionClassification` | 可选：`user_temporary`（允许一次）、`user_permanent`（总是允许）、`user_reject`（拒绝），只用于遥测；不填时 CLI 保守推断 | [TS:`PermissionDecisionClassification`] |

TKCode 内部处理审批出错（例如 UI 断开）时：回 **`behavior:"deny"`** 的成功应答（设计文档 §6 的安全规则），而不是 error 应答。

**`ExitPlanMode`（计划审批，docs/design/plan-mode.md §15）**：与 `AskUserQuestion` 一样不是一键审批（`requires_user_interaction`），TKCode 不看权限档位，交给计划审批面板。

- input：计划正文在 `plan`，计划文件在 `planFilePath`（CLI 在 2.1.285 起保证发给 SDK 回调的是同一条回复里刚写入的最新计划 [CHANGELOG 2.1.285]）。**字段名尚未用真实 CLI 抓帧核实**；TKCode 先取 `plan`，没有时读 `planFilePath`，都没有就 deny。
- 批准：`allow`，`updatedInput` 为原 input（用户编辑过时替换 `plan`，并写回 `planFilePath`），`updatedPermissions: [{"type":"setMode","mode":<default|bypassPermissions>,"destination":"session"}]` 让 CLI 离开 plan [Doc:permission-modes "Approving a plan exits plan mode and switches the session to the permission mode each approve option describes"]。
- 继续完善 / 关闭面板：`deny`，`message` 为反馈或"暂不批准，结束本轮"的说明，`interrupt: false`；CLI 留在 plan。
- 模式同步：CLI 自己改变权限模式时（模型调用 `EnterPlanMode`、审批后的 `setMode`），`system/status.permissionMode` 与之后的 `system/init.permissionMode` 会报告新值；TKCode 只把与已应用值不同的报告当作 CLI 侧的变化，进入或离开 `plan` 时同步自己的模式。`EnterPlanMode` 不需要权限（[Doc:tools-reference]），不会经过 `can_use_tool`。

### 6.2 `hook_callback`

`{"subtype":"hook_callback","callback_id":"hook_0","input":{…},"tool_use_id":"…"}`。只有在 `initialize.hooks` 里注册过回调 ID 才会出现。TKCode 不注册，收到即按 §4.4 回 error。应答体是 hook 输出对象（键名 `async`、`continue` 为原样英文）。[Py:query.py]

### 6.3 `mcp_message`

`{"subtype":"mcp_message","server_name":"…","message":<JSON-RPC>}`，应答 `{"mcp_response":<JSON-RPC 响应>}`；通知类消息也要回 `{"mcp_response":{"jsonrpc":"2.0","result":{}}}`。只在 `--mcp-config` 里有 `type:"sdk"` 的服务器时出现。TKCode 把自己的工具做成 stdio MCP 服务器传给 `--mcp-config`，不用 SDK MCP，因此收到即回 error。[Py:query.py `_handle_sdk_mcp_request`]

### 6.4 `elicitation` / `request_user_dialog`

MCP 服务器的 elicitation 请求和工具驱动的对话框。首版：`elicitation` 回 error（等同于取消）；`request_user_dialog` 见 §4.4。[TS:sdk.d.ts]

---

## 7. stdin 上的 user 消息

### 7.1 形状

```json
{"type":"user","message":{"role":"user","content":[{"type":"text","text":"重构这个函数"}]},"parent_tool_use_id":null,"uuid":"3f9a6c1e-2b7d-4e8a-9c05-1d2e3f4a5b61"}
```

| 字段 | 必填 | 说明 | 来源 |
|---|---|---|---|
| `type` | 是 | `"user"` | [Py；zeron] |
| `message` | 是 | Messages API 的 `MessageParam`：`role:"user"`，`content` 为字符串或 content block 数组（`text`、`image`、`document`…） | [TS:`SDKUserMessage`] |
| `parent_tool_use_id` | 是 | 主会话消息填 `null` | [Py；zeron] |
| `uuid` | 推荐 | 客户端生成的 UUID。**带了 uuid 才会有 `command_lifecycle` 帧**，result 也会回显 `user_message_uuid`，这是 TKCode 把输出和发送关联起来的键 | [TS；CLI:bin `command_lifecycle` 描述 "Commands enqueued without a uuid … emit no lifecycle events"；zeron] |
| `priority` | 否 | `now` / `next` / `later`，见 §7.3 | [TS；zeron:wire.rs] |
| `session_id` | 否 | 可省略。Python SDK 填 `""` 或 `"default"`，CLI 不依赖它 | [Py:client.py] |
| `shouldQuery` | 否 | `false` 时只追加到转录，不触发轮次，合并进下一条会触发轮次的消息 | [TS] |
| `client_composed` | 否 | `true` 时文本原样投递：不展开 `@path`、不分派斜杠命令，并跳过轮次开始时的附件注入（嵌套 CLAUDE.md、提醒等）。≥ 2.1.248。TKCode 用户手打的消息**不设**；TKCode 程序生成的消息才设 | [TS；Py:subprocess_cli.py `VERBATIM_PROMPTS_MINIMUM_CLAUDE_CODE_VERSION`] |
| `pasted_content` / `inline_pastes` | 否 | 粘贴内容标注 | [TS] |

斜杠命令（`/compact`、`/review` 等）直接作为文本发送，CLI 会展开。[Doc:headless 命令支持说明]

### 7.2 图片

图片作为 content block 放在文本**之前**：

```json
{"type":"user","uuid":"…","parent_tool_use_id":null,"message":{"role":"user","content":[
  {"type":"image","source":{"type":"base64","media_type":"image/png","data":"iVBORw0KGgo…"}},
  {"type":"text","text":"这个报错是什么意思？"}]}}
```

- 支持 `image/png`、`image/jpeg`、`image/gif`、`image/webp`；`data` 是裸 base64，不带 `data:` 前缀。
- 单张原始字节超过 5 MB 不内联，改为在文本里给出路径，让 Agent 用 Read 工具读取。媒体类型先看扩展名，再看魔数。
- 来源：[zeron:wire.rs `content_blocks`（"verified against the real CLI"）、mod.rs `MAX_INLINE_IMAGE_BYTES`、`image_media_type`]

### 7.3 轮次进行中再发消息（steering）

- 任何时候都可以写 user 消息，CLI 放进命令队列。
- `priority`（[zeron:wire.rs `steer_message_line_with_images`，"verified against CLI 2.1.280"]；[TS]）：
  - `next`：在下一个步骤边界（一次工具结果之后）并入当前轮次，不中止正在运行的工具。
  - `now`：立即停止正在流式输出的文本或 thinking，被中断的轮次输出自己的 `result`，然后新轮次处理这条消息。**`now` 也会中止正在运行的 MCP 工具调用**（"The tool call was interrupted before a result was received"）。
  - `later`：当前轮次结束后作为新轮次处理。
  - 省略：CLI 默认行为 [推断：与 `next` 相同，在步骤边界并入]。
- TKCode 规则（照搬 zeron）：有工具在运行（已见 `tool_use`、未见对应 `tool_result`）时用 `next`，否则用 `now`。
- 并入当前轮次的消息与该轮次共用一个 `result`，`result.user_message_uuids` 会列出该轮次消费的全部 uuid。[TS:`SDKResultError.user_message_uuids`]

### 7.4 回显（`--replay-user-messages`）

CLI 收到 stdin 的 user 消息后在 stdout 回显：

```json
{"type":"user","message":{…原样…},"parent_tool_use_id":null,"session_id":"…","uuid":"<TKCode 发送时的 uuid>","isReplay":true,"timestamp":"…"}
```

- `isReplay:true` 是区分标志，`uuid` 必有。[TS:`SDKUserMessageReplay`]
- TKCode 用它（或 `command_lifecycle`）确认消息已被 CLI 接收。回显帧**不能**被当成工具结果或新的用户输入处理。
- 被更晚的消息取代而从未开始的消息可能不会被回显；回显一条消息意味着它之前写入的消息也都已被接收。[zeron:mod.rs 注释 "superseded steers are never replayed themselves"]

---

## 8. stdout 帧

所有对话帧都带 `uuid`（帧自身 ID）和 `session_id`。下面按 `type` 分节。

### 8.1 `system` 帧

用 `subtype` 分派。已知 subtype：

| subtype | 内容 | TKCode 处理 | 来源 |
|---|---|---|---|
| `init` | 会话元数据，见 §8.1.1 | 更新会话信息；**每个轮次开始时都会再发一次** | [TS；Doc:headless；zeron:normalize.rs] |
| `api_retry` | `attempt`、`max_retries`、`retry_delay_ms`、`error_status`（无 HTTP 响应时为 null）、`error`（错误类别）、`no_response?{waited_ms, retry_wait_ms}` | 显示"正在重试 (n/max)"，不结束轮次 | [Doc:headless §Handle API retries；TS:`SDKAPIRetryMessage`] |
| `status` | `status: "requesting"\|"compacting"\|null`、`permissionMode?`、`compact_result?`、`compact_error?` | 状态栏；`permissionMode` 变化时同步 UI | [TS；CLI:bin `stream_request_start → status:"requesting"`] |
| `compact_boundary` | `compact_metadata{trigger, pre_tokens, post_tokens?…}` | 在转录里插入"已压缩"分隔 | [TS] |
| `session_state_changed` | `state: idle\|running\|requires_action`，`sdk_host_only?:true` | 见 §9.4 | [TS；Py:query.py；CLI:bin] |
| `task_started` / `task_progress` / `task_updated` / `task_notification` | 后台任务（子 Agent、后台 shell、工作流）生命周期；`task_id`、`tool_use_id`、`task_type`（`local_agent`/`local_bash`/`local_workflow`…）、`status` | 子 Agent 卡片状态；后台 shell 状态 | [Py:message_parser.py、query.py；zeron:normalize.rs] |
| `background_tasks_changed` | `tasks[]`，**替换语义**（完整集合，不是增量） | 后台任务指示器 | [TS；zeron:mod.rs] |
| `hook_started` / `hook_progress` / `hook_response` | SessionStart/Setup hook 总是输出；其他 hook 需 `--include-hook-events` | 忽略或写日志 | [Doc:headless、cli-reference；Py] |
| `permission_denied` | 未经询问就被自动拒绝的调用（规则、dontAsk、分类器） | 在工具卡片上标"被策略拒绝" | [Doc:headless；TS] |
| `thinking_tokens` | `estimated_tokens`、`estimated_tokens_delta` | 可做思考进度 | [TS] |
| `commands_changed` | `commands[]`，替换语义 | 刷新斜杠命令 | [TS] |
| `notification`、`model_fallback`、`model_refusal_fallback`、`model_refusal_no_fallback`、`plugin_install`、`control_request_progress`、`informational`… | | 显示或忽略 | [TS；CLI:bin] |

#### 8.1.1 `system/init`

```json
{"type":"system","subtype":"init","cwd":"/Users/dev/proj","session_id":"…","tools":["Task","Bash","Read","Edit","…"],
 "mcp_servers":[{"name":"tkcode","status":"connected"}],"model":"claude-opus-5-5","permissionMode":"default",
 "slash_commands":["compact","review","…"],"apiKeySource":"none","claude_code_version":"2.1.301","output_style":"default",
 "agents":["general-purpose","Explore","Plan"],"skills":[],"plugins":[],"capabilities":["interrupt_receipt_v1","interrupt_cancel_queued_v1"],
 "fast_mode_state":"off","uuid":"…"}
```

- 必有：`session_id`、`model`、`cwd`、`tools`、`mcp_servers[{name,status,source?}]`、`permissionMode`、`slash_commands`、`apiKeySource`（`ANTHROPIC_API_KEY`/`apiKeyHelper`/`/login managed key`/`none`；订阅登录为 `none`）、`claude_code_version`、`output_style`、`skills`、`plugins`。[TS:`SDKSystemMessage`；Py:types.py `SystemInitData`]
- 可选：`agents`、`betas`、`terminal_slash_commands`、`plugin_errors`、`fast_mode_state`、`effort`、`view_mode`、`capabilities`（≥ 2.1.205）。
- **`capabilities` 用于特性检测**，不要比较版本号；开放集合，忽略不认识的值。已知：`interrupt_receipt_v1`、`interrupt_cancel_queued_v1`、`mcp_read_resource_v1`…[TS；Doc:headless]
- 在流中通常是**每个轮次的第一帧**（之前可能有 `plugin_install`、SessionStart hook 帧）。[Doc:headless §Read session metadata]
- **同一进程会重复发 init**：每个轮次一次，包括后台子 Agent 完成后的唤醒轮次（`session_id` 相同）。TKCode 只把**第一次** init 当作会话建立；之后的 init 只用于刷新 `model`、`permissionMode`、`tools` 等元数据，不能重置转录。[zeron:normalize.rs `saw_init` "live-verified against 2.1.228"]

### 8.2 `stream_event`

```json
{"type":"stream_event","event":{ /* 一个原始 Messages API 流事件 */ },"parent_tool_use_id":null,"session_id":"…","uuid":"…"}
```

- `event` 就是 Messages API SSE 的 data 对象：`message_start`、`content_block_start`、`content_block_delta`（`text_delta` / `thinking_delta` / `signature_delta` / `input_json_delta` / `citations_delta`）、`content_block_stop`、`message_delta`、`message_stop`（以及可能的 `ping`）。各事件字段与 [anthropic-messages.md](anthropic-messages.md) §6 完全相同。[Doc:agent-sdk/streaming-output；TS:`SDKPartialAssistantMessage`]
- **只有主会话有 stream_event**，`parent_tool_use_id` 总是 `null`；子 Agent 不转发 token 级增量。[Doc:agent-sdk/streaming-output；zeron:normalize.rs "The 2.1.x wire streams NO tagged partial deltas (live-verified)"] TKCode 仍然按非 null 的 `parent_tool_use_id` 归属处理（防御性）。
- 每个 API 请求一组 `message_start … message_stop`；一个轮次有多次工具往返时就有多组。
- 可选字段：`ttft_ms`（首个事件）、`user_message_uuid` / `user_message_uuids`（轮次第一个非 ping 事件）。[TS]
- `thinking_delta` 可能是空串并带 `estimated_tokens`（display 为 omitted 时）。空 thinking 不产生 thinkingDelta。[zeron:normalize.rs 测试夹具]
- **流被切断**（中断、断网）时：≥ 2.1.290 仍然发出 `message_stop`；被切断的 text/thinking 块有 `content_block_stop`，**被切断的 tool_use 块没有**——`message_stop` 到达时还开着的工具块，其输入视为不完整。[Doc:agent-sdk/streaming-output §Handle a stream that's cut off]

### 8.3 `assistant`

```json
{"type":"assistant","message":{"id":"msg_01…","type":"message","role":"assistant","model":"claude-opus-5-5","content":[{"type":"text","text":"…"}],"stop_reason":null,"stop_sequence":null,"usage":{…}},
 "parent_tool_use_id":null,"session_id":"…","uuid":"…"}
```

- **每个完成的非空 content block 发一个 assistant 帧**，`content` 里只有这一个块；同一次 API 响应的多个块共享 `message.id`。这些帧上 `stop_reason` 为 `null`、`usage` 不是最终值。[TS:`SDKAssistantMessage`；Doc:agent-sdk/streaming-output §Message flow]
- 开启 partial messages 时，**某个块的 assistant 帧先于该块的 `content_block_stop` 到达**：

  ```
  stream_event message_start
  stream_event content_block_start (text)
  stream_event content_block_delta …
  assistant    (完整 text 块)
  stream_event content_block_stop
  stream_event content_block_start (tool_use)
  stream_event content_block_delta (input_json_delta) …
  assistant    (完整 tool_use 块，input 已解析为对象)
  stream_event content_block_stop
  stream_event message_delta
  stream_event message_stop
  … 工具执行 …
  ```

  [Doc:agent-sdk/streaming-output §Message flow]
- 块类型：`text`、`thinking`（`thinking`、`signature`）、`redacted_thinking`、`tool_use`（`id`、`name`、`input`）、`server_tool_use`、`advisor_tool_result` 等。未知块类型保留原始 JSON。[Py:message_parser.py]
- 可选包装字段：`error`（见下）、`aborted:true`（被中断截断的消息）、`agent_id` / `subagent_type` / `task_description`（子 Agent 帧）、`user_message_uuid(s)`、`supersedes`（拒答回退时替换的帧 uuid，收到后撤下这些帧）、`request_id`、`timestamp`。[TS]
- **`error` 字段**：API 失败时 CLI 合成一个 assistant 帧，`message.model` 为 `"<synthetic>"`，文本是可读错误，`error` 为错误类别：`authentication_failed`、`oauth_org_not_allowed`、`account_on_hold`、`verification_required`、`billing_error`、`rate_limit`、`overloaded`、`invalid_request`、`model_not_found`、`server_error`、`unknown`、`max_output_tokens`、`cloud_credential_error`。这类失败往往**不**以 error 类 result 出现，必须单独显示。[TS:`SDKAssistantMessageError`；CLI:bin `Ti="<synthetic>"`；zeron:normalize.rs `assistant_error_text`]
- 子 Agent 帧（`parent_tool_use_id` 非 null）：默认只有 tool_use 块；开了 `--forward-subagent-text` 才有 text/thinking 块。子 Agent 的第一帧是一个 user 帧，携带驱动它的 prompt。[Doc:headless §Follow subagent messages]

### 8.4 `user`

stdout 上的 user 帧有四种来源：

1. **工具结果**（主会话）：`content` 是 `tool_result` 块数组，包装层带 `tool_use_result`：

   ```json
   {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_01…","type":"tool_result","content":"…","is_error":false}]},
    "parent_tool_use_id":null,"session_id":"…","uuid":"…","timestamp":"…",
    "tool_use_result":{"stdout":"…","stderr":"","interrupted":false,"isImage":false}}
   ```

   - `tool_result.content` 是给模型看的内容（字符串或 block 数组，可能含图片）；`is_error` 可能缺省（视为 false）。
   - `tool_use_result` 是工具的**结构化输出**，形状按工具而定（Bash：`stdout`/`stderr`/`interrupted`/`isImage`…；Agent/Task：子 Agent 的最终报告和统计；被移到后台的调用是 `{detachedToolCall:true}`）。UI 渲染优先用它。**不一定是对象**：工具出错时可能是字符串（如 `"Error: File has not been read yet…"`），消费方必须先检查 `ValueKind`（0.1.2 曾因此在 UI 线程崩溃）。[TS:`SDKUserMessage.tool_use_result`；Py:message_parser.py]
   - zeron 丢弃了工具输出，TKCode **必须保留**（设计文档 §5）。
2. **子 Agent 的帧**（`parent_tool_use_id` 非 null）：它的工具结果，以及父 Agent 发给它的 prompt / 后续消息（text 块）。
3. **回显**（`isReplay:true`，§7.4）。
4. **合成标记**：`[Request interrupted by user]`、`[Request interrupted by user for tool use]`、以 `<system-reminder>` 开头的注入文本。它们不是用户输入，不能当成 steering 显示。按前缀匹配（至少有两种拼法）。[zeron:normalize.rs `is_synthetic_user_text`；CLI:bin 字符串] 也可能带 `isSynthetic:true`。[TS]

### 8.5 `result`

```json
{"type":"result","subtype":"success","is_error":false,"duration_ms":2841,"duration_api_ms":2610,"num_turns":1,
 "result":"…最终文本…","stop_reason":"end_turn","session_id":"…","total_cost_usd":0.0213,
 "usage":{…},"modelUsage":{"claude-opus-5-5":{…}},"permission_denials":[],
 "terminal_reason":"completed","uuid":"…"}
```

#### 8.5.1 字段

| 字段 | 说明 | 来源 |
|---|---|---|
| `subtype` | `success`、`error_during_execution`、`error_max_turns`、`error_max_budget_usd`、`error_max_structured_output_retries` | [TS；Py] |
| `is_error` | **与 subtype 独立**：API 失败结束的轮次是 `subtype:"success"` + `is_error:true`，`result` 里是错误文本，`errors` 为空 | [Py:query.py `_error_result_text` 注释；TS] |
| `result` | 仅 success：最终 assistant 文本（is_error 时是错误文本） | [TS] |
| `errors` | 仅 error_*：字符串数组。**可能含 `[ede_diagnostic] …` 前缀的内部诊断，不要显示给用户**，只写日志 | [Py；zeron:normalize.rs `is_internal_diagnostic`；CLI:bin `e7r`] |
| `stop_reason` | 最后一次 API 响应的 stop_reason，可能为 null | [TS] |
| `terminal_reason` | 轮次为什么结束：`completed`、`aborted_streaming`、`aborted_tools`、`max_turns`、`api_error`、`model_error`、`image_error`、`prompt_too_long`、`blocking_limit`、`rapid_refill_breaker`、`malformed_tool_use_exhausted`、`stop_hook_prevented`、`hook_stopped`、`tool_deferred`、`background_requested`、`budget_exhausted`、`structured_output_retry_exhausted`、`tool_deferred_unavailable`、`turn_setup_failed` | [TS:`TerminalReason`；CLI:bin 同名数组] |
| `api_error_status` | is_error 且 subtype 为 success 时，失败 API 调用的 HTTP 状态（≥ 2.1.110） | [Py:types.py] |
| `num_turns` | 本轮次内的模型往返次数 | [TS] |
| `duration_ms` / `duration_api_ms` | | |
| `usage` | **仅主循环、仅本轮次**（流式输入会话中按轮次计），不含子 Agent 和辅助调用 | [TS] |
| `modelUsage` | `{<model>: {inputTokens, outputTokens, cacheReadInputTokens, cacheCreationInputTokens, webSearchRequests, costUSD, contextWindow, maxOutputTokens, thinkingTokens?, canonicalModel?, provider?}}`。覆盖全部调用（含子 Agent、压缩），**在同一进程内跨轮次累计**（每个 result 是到目前为止的总数） | [TS；Py:types.py `ModelUsage`] |
| `total_cost_usd` | **同一进程内跨轮次累计**的估算费用；恢复/分叉的会话从转录里保存的总数继续；`/clear` 清零。是估算，不是账单 | [TS] |
| `permission_denials` | `[{tool_name, tool_use_id, tool_input}]`，权威的拒绝记录 | [TS；Doc:headless] |
| `queued_turn_count` | 结果产生时命令队列里还有几条用户发送；>0 表示后面还会有轮次（和 result） | [TS] |
| `user_message_uuid` / `user_message_uuids` | 本轮次回答的 user 消息 uuid（TKCode 发送时给的） | [TS] |
| `result_index` | 本进程内 result 的序号（从 0 开始）；出现跳号说明有 result 丢失 | [TS] |
| `origin` | 触发本轮次的消息来源；`{"kind":"task-notification"}` 表示后台任务唤醒的轮次，不是用户发起的 | [Py:types.py；TS] |
| `deferred_tool_use`、`structured_output`、`fast_mode_state`、`resume_reason`、`subagent_stats`… | 可选 | |

#### 8.5.2 用量语义（TKCode 的计算方式）

- 本轮次 token：取 `result.usage`（主循环）。显示"本轮次总消耗"时用 `modelUsage` 各模型之和的**差值**（本次 result 减上次 result）。
- 本轮次费用：`total_cost_usd` 差值。进程内第一个 result 的基准：新会话为 0；恢复的会话**第一个 result 已经包含之前的总数**，基准要取恢复前 TKCode 记录的最后值，没有记录时这一轮的费用显示为未知。[TS]
- 上下文占用（显示"已用多少上下文"）：取主会话最后一次 API 调用的 `input_tokens + cache_read_input_tokens + cache_creation_input_tokens`，来源是 `stream_event` 的 `message_start.message.usage`（输入侧）和 `message_delta.usage`（最终值）；上下文窗口大小取 `modelUsage[<当前模型>].contextWindow`。子 Agent 帧的 usage 不计入。[zeron:normalize.rs `ContextUsage`]

#### 8.5.3 被中断轮次的 result

被 `interrupt`（或 `now` 消息、deny+interrupt）中止的轮次照常输出一个 result，CLI 的实际报告方式：

- `terminal_reason` 为 `aborted_streaming`（流式阶段被打断）或 `aborted_tools`（工具阶段被打断）。**这是判断"被中断"的唯一可靠依据**。[Py:types.py `terminal_reason` 注释；CLI:bin `function TM(e){return e==="aborted_streaming"||e==="aborted_tools"}`]
- `subtype` 取决于中断时转录的最后一条消息 [CLI:bin `Ins`/`Mns`/`e7r`]：
  - 最后一条是 user 消息（通常是 `[Request interrupted by user]` 标记，或还没产生任何回复）→ **`subtype:"error_during_execution"`、`is_error:true`**，`errors` 为 `["[ede_diagnostic] result_type=user last_content_type=n/a stop_reason=null"]` 一类的诊断。zeron 的测试夹具里就是这个字符串。[zeron:normalize.rs 测试]
  - 最后一条是以 text/thinking 结尾的 assistant 消息，或全是 tool_result 的 user 消息 → `subtype:"success"`、`is_error:false`。
- 旧 CLI 没有 `terminal_reason`：TKCode 在"已发出 interrupt 且尚未结算"期间收到的 result 一律视为中断。[zeron:normalize.rs `normalize(frame, interrupted)`]
- 被中断轮次消费的 user 消息收到 `command_lifecycle` 的 `cancelled`。

### 8.6 `rate_limit_event`

```json
{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1792155600,"rateLimitType":"five_hour","overageStatus":"rejected","overageDisabledReason":"org_level_disabled","isUsingOverage":false},"uuid":"…","session_id":"…"}
```

- 只对 claude.ai 订阅用户，限额状态变化时发出。`status`：`allowed` / `allowed_warning` / `rejected`；`rateLimitType`：`five_hour`、`seven_day`、`seven_day_opus`、`seven_day_sonnet`、`seven_day_overage_included`、`overage`；`resetsAt` 为 Unix 秒。[TS:`SDKRateLimitInfo`；Py:message_parser.py]
- **只有在真正阻塞时才报错**：`status=="rejected"` 且 `overageStatus` 不是 `allowed`/`allowed_warning`，且 `isUsingOverage`、`overageInUse` 都不为 true。`allowed_warning` 只做提示（显示使用率 `utilization`）。[zeron:wire.rs `RateLimitInfo::blocks`]

### 8.7 `command_lifecycle`

```json
{"type":"command_lifecycle","command_uuid":"<TKCode 发送的 uuid>","state":"queued","uuid":"…","session_id":"…"}
```

- 只针对带 `uuid` 的 stdin user 消息。`state`：`queued`（进入队列）→ `started`（被某个轮次取用；`next` 消息在并入的步骤边界 started）→ 恰好一个终态：`completed`、`cancelled`（被撤回、被 `cancel_queued` 扫掉、或所在轮次被中断/失败）、`discarded`（会话结束时仍在队列）、`refused`。[CLI:bin schema 描述；zeron:wire.rs "verified live against CLI 2.1.286"]
- 与 result 的顺序：**开启新轮次的消息，`completed` 在该轮次 result 之后**；并入进行中轮次的消息，`completed` 在该轮次 result 之前。[CLI:bin schema 描述]
- 不严格成对：终态可能没有 `started`；内部入队的命令可能没有 `queued`。按"存在即更新"处理。
- `completed` 只表示消费它的轮次结束了，不表示 result 已送达。

### 8.8 控制帧

`control_request`（§6）、`control_response`（对 TKCode 请求的应答，§4.2）、`control_cancel_request`（§4.3）。

### 8.9 其他帧

| type | 说明 | 处理 |
|---|---|---|
| `tool_progress` | `tool_use_id`、`tool_name`、`elapsed_time_seconds`、`parent_tool_use_id`、`heartbeat?`、`task_id?`、`subagent_retry?` | 工具运行计时；首个 `tool_progress` 也可作为 toolExecutionStarted 的信号 [TS；CLI:bin] |
| `tool_use_summary` | `summary` | 可忽略 |
| `keep_alive` | 无字段 | 忽略 |
| `prompt_suggestion` | `--prompt-suggestions` 才有 | 忽略 |
| `auth_status` | `isAuthenticating`、`output[]`、`error?` | 记录 |
| `conversation_reset` | `new_conversation_id` | 记录；首版不支持 |
| `transcript_mirror` | 只在 `--session-mirror` 时出现 | 忽略 |
| 其他 | `active_goal`、`files_persisted`、`elicitation_complete`、`memory_recall`… | 忽略 |

来源：[TS:sdk.d.ts `SDKMessage` 联合类型]。

---

## 9. 轮次生命周期

### 9.1 启动握手

```
TKCode                                    CLI
  │ spawn(argv, env, cwd)                  │
  │ ── control_request initialize ───────▶ │
  │ ◀── control_response (success) ─────── │   ← 第一行 stdout，不触发 API
  │ ── user {uuid=U1} ───────────────────▶ │
  │ ◀── command_lifecycle U1 queued ────── │
  │ ◀── user (isReplay, uuid=U1) ───────── │
  │ ◀── system/init ────────────────────── │   ← 每个轮次开始时
  │ ◀── command_lifecycle U1 started ───── │
  │ ◀── stream_event / assistant / user … ─│
  │ ◀── result ─────────────────────────── │   ← 轮次结束
  │ ◀── command_lifecycle U1 completed ─── │
```

`queued` / 回显 / `init` / `started` 之间的相对顺序 [推断]，TKCode 不要依赖。`system/session_state_changed`（若开启）的 `running` 在轮次开始时、`idle` 在 result 前后到达。

### 9.2 轮次结束

- **以 `result` 帧为唯一的轮次结束信号**，不用静默计时器猜测。[设计文档 §5；zeron:mod.rs `deterministic_turn_end`]
- 收到 result 时，若 `queued_turn_count > 0`，或 TKCode 自己还有已写入但没有 `started`/终态的消息，则**下一个轮次会紧接着开始**；TKCode 仍对每个 result 发 agentEnded，再为下一个轮次发 agentStarted。（zeron 把这种 result 暂存为"steer 边界"，因为它的上层模型是"一条 prompt = 一次运行"；TKCode 的事件流按轮次划分，不需要暂存。）
- 进程在没有任何 result 的情况下退出：合成 agentEnded（状态 = 错误，错误信息 = 退出码 + stderr 尾部）。[zeron:mod.rs 结尾的 terminal bookkeeping]

### 9.3 后台任务唤醒轮次

后台子 Agent 或后台命令完成后，CLI 会**自己再跑一个轮次**来处理结果：一个新的 `system/init`（同一 `session_id`）、新的流事件、新的 `result`（`origin.kind == "task-notification"`），期间 TKCode 没有发任何消息。TKCode 把它当成一个普通的新轮次（agentStarted…agentEnded），UI 标注为"后台任务唤醒"。[zeron:mod.rs 头注释 "a fresh init (same session id, deduped) plus another result"；Py:types.py `origin`]

### 9.4 运行是否真正空闲

一个 result 只表示一个轮次结束，不表示整次运行结束：后台 Agent 可能还在跑，之后还会唤醒。TKCode 设了 `CLAUDE_CODE_SDK_READS_SESSION_STATE=1` 时，用 `system/session_state_changed` 判断：

- `running`：有工作在进行（含后台 Agent 存活、唤醒轮次待处理）。
- `requires_action`：在等宿主回答（如 `can_use_tool`）。
- `idle`：没有欠下的轮次。

用途：UI 的"会话忙碌"指示、决定拆除前是否需要等待。带 `sdk_host_only:true` 的帧是因为环境变量才发出的，不进入转录。[Py:query.py `_on_session_state`、`_maybe_end_run`；CLI:bin]

### 9.5 状态机（TKCode 侧）

```
Starting ──initialize ok──▶ Idle ──写入 user──▶ Pending ──首个轮次帧──▶ Running
Running ──result──▶ Idle（或立即进入下一个 Running）
Running ──发出 interrupt──▶ Stopping ──result / 应答后 3 秒无 result──▶ Idle
任意状态 ──stdout EOF / 进程退出──▶ Exited
```

"首个轮次帧"= 主会话的 `system/init`、`command_lifecycle started`、`stream_event`、`assistant` 中先到的那个。

---

## 10. 中断与拆除

### 10.1 停止当前轮次（进程保留）

1. 写 `{"subtype":"interrupt","cancel_queued":true}`（CLI 不支持 `interrupt_cancel_queued_v1` 时去掉该字段）。
2. 关闭所有挂起的审批对话框；之后收到的 `control_cancel_request` 照常处理。
3. 等待被中断轮次的 result（按 §8.5.3 映射为"已中断"）。CLI 应答后 3 秒内没有 result，就合成一个"已中断"的 agentEnded。[zeron:mod.rs `STOP_SETTLE`]
4. 停止后 3 秒内到达的、且其间没有任何新活动（TKCode 没写新消息、没有新的内容输出）的 result，是被停止轮次的迟到报告，丢弃。[zeron:mod.rs "late result of a stopped turn dropped"]

### 10.2 SIGTERM / SIGINT 语义

- SIGTERM：CLI 以 **143** 退出；正在进行的轮次**不输出 result**；会杀掉仍在运行的 Bash 进程树，运行 `SessionEnd` hook；等待审批时收到 SIGTERM，审批保持未回答。[Doc:headless §Stop a run with SIGTERM]
- 想让轮次正常结束：先发 `interrupt` 控制请求（或 SIGINT），再停进程。[Doc:headless]
- stdin 关闭时 CLI 会立即取消挂起的审批。[Doc:headless]

### 10.3 拆除会话

照搬 Python SDK `close()` 与 zeron 的组合：

1. 若有轮次在跑：发 `interrupt`，最多等 2 秒的 result。[zeron:mod.rs `interrupt_grace = 2s`]
2. 关闭 stdin（CLI 收到 EOF 后收尾退出；若有后台工作，它最多等 `CLAUDE_CODE_PRINT_BG_WAIT_CEILING_MS`）。
3. 等进程退出，最多 5 秒。
4. 向进程组发 SIGTERM，再等 5 秒（zeron 用 3 秒）。
5. SIGKILL。
6. 继续把 stdout 读到 EOF，确保最后的帧被处理；关闭 stderr 读取。

[Py:subprocess_cli.py `close()`：关 stdin → wait 5s → terminate → wait 5s → kill；zeron:mod.rs 中断升级 2s → SIGTERM → 3s → SIGKILL]

TKCode 进程自身退出时，要对所有存活的 CLI 子进程发 SIGTERM（Python SDK 的 atexit 处理），避免留下孤儿进程。[Py:subprocess_cli.py `_kill_active_children`]

### 10.4 退出码

- `0`：正常结束。
- `1`：最后一个 result 是 `is_error:true`（CLI 为了脚本使用者故意非零退出），或启动失败。这种情况下应以 result 里的错误为准，而不是笼统的"退出码 1"。[Py:query.py `_read_messages` except 分支注释]
- `143`：SIGTERM。

---

## 11. 会话、恢复与存储

- 会话 ID：`system/init.session_id`，每个 result 也带 `session_id`。新会话由 TKCode 用 `--session-id=<uuid>` 预先指定，免得等第一帧。[Doc:agent-sdk/sessions；CLI:help]
- 恢复：`--resume=<id>`。2.1.223 起 CLI 会在当前项目目录、其 git worktree、再到本机所有项目中查找该 ID；也接受 `.jsonl` 转录文件的绝对路径。会话文件必须在本机。[Doc:headless、cli-reference、agent-sdk/sessions]
- 分叉：`--resume=<id> --fork-session`，新的 `session_id` 从第一个 init/result 读取。[Doc:agent-sdk/sessions]
- 存储位置：`~/.claude/projects/<编码后的 cwd>/<session-id>.jsonl`（设置 `CLAUDE_CONFIG_DIR` 时在其 `projects/` 下）。编码方式：绝对路径中每个非字母数字字符替换为 `-`；超过 200 字符时截断并加哈希。TKCode **不解析**这些文件（设计文档 §7）。[Doc:agent-sdk/sessions]
- 恢复失败：stderr/result 文本为 `No conversation found with session ID: <id>`，进程以 1 退出。[CLI:bin；Py:query.py "a refused resume"]
- `--no-session-persistence` 让会话不落盘（无法恢复）；TKCode 不用。
- `system/init` 不会重放历史；恢复后的进程不会重发之前的 `task_started`（后台任务身份要靠自己记录）。[zeron:normalize.rs `for_resume`]
- 尽管 2.1.223 起可以跨目录查找，TKCode 仍只在原 cwd 下恢复（设计文档 §7）。

---

## 12. 认证与版本探测

### 12.1 认证：`claude auth status`

```
$ claude auth status          # 默认输出 JSON（--json），--text 为人类可读
{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","analyticsDisabled":false,
 "projectsDirectory":"/Users/x/.claude/projects","configDirectory":"/Users/x/.claude",
 "email":"…","orgId":"…","orgName":null,"subscriptionType":"max"}
```

- **退出码：已登录 0，未登录 1**。[Doc:cli-reference]
- `authMethod` ∈ `none`、`claude.ai`、`oauth_token`、`api_key`、`api_key_helper`、`third_party`。[Doc:cli-reference]
- `configDirectory` ≥ 2.1.268。[Doc:cli-reference]
- 其他字段（`apiProvider`、`email`、`orgId`、`subscriptionType`…）以本机 2.1.301 输出为准 [CLI:help]，解析时全部按可选处理。
- **隐私**：`email`、`orgId` 是个人信息，TKCode 只取 `loggedIn`、`authMethod`、`apiProvider`、`subscriptionType`，不落盘、不上传。
- 凭据优先级（用于向用户解释"当前用的是哪个凭据"）：云厂商（`CLAUDE_CODE_USE_BEDROCK/VERTEX/FOUNDRY`）> `ANTHROPIC_AUTH_TOKEN` > `ANTHROPIC_API_KEY` > apiKeyHelper > `CLAUDE_CODE_OAUTH_TOKEN` > Anthropic profile > `/login` 订阅。[Doc:authentication §Authentication precedence]
- 运行时的认证失败不会体现在 `auth status` 上，而是以合成 assistant 帧（`error:"authentication_failed"`）+ is_error 的 result 出现（§13）。
- 未登录时 TKCode 只提示用户在终端运行 `claude` 登录，TKCode 不代为登录（设计文档 §2）。

### 12.2 版本

- `claude --version` 输出 `2.1.301 (Claude Code)`；用正则 `^(\d+)\.(\d+)\.(\d+)` 取版本。超时 2 秒；失败不阻止使用。[CLI:help；Py:subprocess_cli.py `_check_claude_version`]
- 运行中的版本以 `system/init.claude_code_version` / `initialize` 应答的 `claude_code_version` 为准（CLI 会后台自动更新，探测结果可能过期）。
- **最低支持版本 2.1.290**（被切断的流也会发 `message_stop`）；**已验证版本 2.1.301**。低于最低版本时在 UI 上警告但仍允许运行。功能差异用 `capabilities` 判断，不比较版本。
- 相关版本门槛速查：

| 功能 | 最低版本 | 来源 |
|---|---|---|
| `capabilities`、`interrupt_receipt_v1` | 2.1.205 | [Py:types.py；zeron:harness.md] |
| `--forward-subagent-text` | 2.1.211 | [Doc:headless] |
| `--resume` 跨项目查找 | 2.1.223 | [Doc:cli-reference] |
| `client_composed` | 2.1.248 | [Py:subprocess_cli.py] |
| `--permission-prompts` | 2.1.259 | [Doc:headless] |
| `pending_permission_requests` 必带 | 2.1.268 | [TS] |
| `command_lifecycle` | ≤ 2.1.286（zeron 实测；更早版本未知） | [zeron:wire.rs] |
| cut stream 仍发 `message_stop` | 2.1.290 | [Doc:agent-sdk/streaming-output] |

---

## 13. 错误处理

| 情形 | 表现 | TKCode 处理 |
|---|---|---|
| 可重试的 API 错误 | `system/api_retry`（attempt/max_retries/retry_delay_ms/error_status/error） | 显示重试进度；轮次继续 |
| API 失败结束轮次 | 合成 assistant 帧（`model:"<synthetic>"`，`error` 字段）+ `result{subtype:"success", is_error:true, result:"<错误文本>", api_error_status}` | 显示错误；agentEnded(错误)。错误文本优先级：`errors[]` → `result` → 非 success 的 `subtype` → `API error (HTTP <status>)` → "unknown error" [Py:query.py `_error_result_text`] |
| 限额被拒 | `rate_limit_event`（§8.6）+ 通常还有 `error:"rate_limit"` 的合成 assistant 帧 | 显示窗口名和重置时间 |
| `error_max_turns` / `error_max_budget_usd` / `error_max_structured_output_retries` | `errors` 可能只有诊断 | 用固定文案兜底（"达到最大轮数"等）[zeron:normalize.rs `result_error_text`] |
| `error_during_execution` | 运行中异常；`errors` 可能全是 `[ede_diagnostic]` | 过滤诊断；剩余为空且 `terminal_reason` 为 aborted_* 时视为中断，否则显示"运行出错"兜底文案 |
| 启动失败（已知原因） | 零值的 `error_during_execution` result，`startup_failure_reason`（如 `cwd_unavailable`、`cli_version_too_old`），`errors` 与 stderr 同文；部分失败只在设置 `CLAUDE_CODE_STARTUP_FAILURE_RESULTS` 时才写 result | 显示原因 [TS:`SDKStartupFailureReason`] |
| 参数错误（`--verbose` 缺失、非法 session id 等） | 只有 stderr + 退出码 1，没有任何 stdout 帧 | 显示 stderr 尾部 |
| 进程崩溃 / 意外退出 | stdout EOF，无 result | 合成 agentEnded(错误)，附退出码和 stderr 尾部 |
| 非 JSON 行、损坏的 JSON 行 | §3.1 | 跳过并记日志 |
| 控制请求超时 | §4.2 | 失败但不杀进程；`initialize` 超时则拆除会话并报错 |
| `assistant.supersedes` | 拒答回退 | 撤下被替换的帧 |

---

## 14. TKCode 事件映射

### 14.1 映射表

| TKCode 事件 | 触发帧 | 关键字段 / 说明 |
|---|---|---|
| **agentStarted** | 空闲状态下主会话的第一个轮次帧（§9.5）；第一次 `system/init` 同时触发会话元数据更新 | `nativeSessionId = session_id`；`model`；`origin`（用户 / 后台唤醒）；关联的 user uuid 取 `command_lifecycle started` 或 stream_event 的 `user_message_uuid` |
| **messageStarted** | `stream_event` `message_start`（`parent_tool_use_id == null`） | `messageId = event.message.id`，`model = event.message.model` |
| **thinkingDelta** | `content_block_delta` 且 `delta.type == "thinking_delta"`，`thinking` 非空 | `text = delta.thinking`；`blockIndex = event.index` |
| （thinking 签名） | `signature_delta` | **赋值**而非追加；只用于审计，不显示 |
| **textDelta** | `content_block_delta` 且 `delta.type == "text_delta"` | `text = delta.text` |
| **toolCallStarted** | `content_block_start` 且 `content_block.type ∈ {tool_use, server_tool_use, mcp_tool_use}` | `toolCallId = content_block.id`，`name = content_block.name` |
| **toolCallDelta** | `content_block_delta` 且 `delta.type == "input_json_delta"` | 追加 `partial_json` 到该 index 的缓冲 |
| **toolCallEnded** | 该工具块的**完整 assistant 帧**或 `content_block_stop`，以先到者为准，只发一次 | 参数以 assistant 帧的 `input`（已解析对象）为准；只等到了 `content_block_stop` 时解析缓冲（空串视为 `{}`）。`message_stop` 时仍未结束的工具块：发 toolCallEnded(`incomplete=true`) |
| **toolExecutionStarted** | ① 对该 `tool_use_id` 回复了 `can_use_tool` allow；② 该工具没有审批请求时：首个 `tool_progress`，或包含它的消息的 `message_stop`（`stop_reason == "tool_use"`），以先到者为准 | 被 deny 的工具**不发**此事件 |
| **toolExecutionEnded** | 主会话 user 帧中的 `tool_result` 块（按 `tool_use_id` 关联） | `isError = is_error ?? false`；`content`（给模型的内容）；`structured = tool_use_result`；被拒绝时 `status = denied`（该 id 在 TKCode 的拒绝集合里，或出现在 result 的 `permission_denials`） |
| **messageEnded** | `stream_event` `message_stop` | `stopReason` 取之前 `message_delta.delta.stop_reason`；被中断时为 null |
| **usage** | ① `message_delta.usage`（每次 API 调用，用于上下文占用）；② `result`（本轮次 usage + `modelUsage`/`total_cost_usd` 的差值） | §8.5.2 |
| **agentEnded** | `result` | 状态映射见 §14.3；`resultText = result`；`sessionId` |
| 子 Agent 事件 | `parent_tool_use_id != null` 的 assistant/user（以及防御性的 stream_event）帧 | 所有事件带 `parentToolCallId = parent_tool_use_id`，**不混入父轮次**，也不影响父轮次的 messageId |

### 14.2 stream_event 与 assistant 帧的去重规则

同一内容会出现两次：一次是 stream_event 增量，一次是完整 assistant 帧。规则：

1. **以 stream_event 为主**。messageStarted/Ended、textDelta、thinkingDelta、toolCall* 都从 stream_event 产生。
2. 维护集合 `streamedMessageIds`：收到 `message_start` 时加入 `event.message.id`。
3. 收到主会话 assistant 帧时，查看 `message.id`：
   - **在集合中**：不再为其 text/thinking 块产生 delta。它的作用只有：(a) 为 `tool_use` 块触发 toolCallEnded（若该 `tool_use.id` 尚未结束），并提供权威的 `input`；(b) 用完整文本校正本地累积的块内容（以 assistant 帧为准，存进转录）；(c) 记录 thinking 的 `signature`；(d) 读取 `error`、`aborted`、`supersedes`。块的对应关系：`tool_use` 用 `id`；text/thinking 用"同一 `message.id` 内的出现顺序"与 `content_block_start` 的 index 顺序对齐（assistant 帧只含非空块，所以按类型 + 顺序匹配，不要直接用数组下标）。
   - **不在集合中**（没有流事件的消息：合成错误消息 `model:"<synthetic>"`、恢复时的回放、旧版本 CLI、子 Agent 帧）：由 assistant 帧合成完整事件序列：messageStarted → 每个块一次性的 textDelta/thinkingDelta（整段文本）或 toolCallStarted + toolCallEnded → 该 `message.id` 的最后一个块之后 messageEnded（下一个不同 `message.id` 的帧、user 帧或 result 到来时关闭）。
4. 每个 `tool_use.id` 的 toolCallStarted 和 toolCallEnded 各只发一次（用 `HashSet<string>` 记录）。
5. 子 Agent（`parent_tool_use_id != null`）的 assistant 帧一律走第 3 条的"不在集合中"分支，并带 `parentToolCallId`。开了 `--forward-subagent-text` 时 text 块是整段，按整段 textDelta 发出。
6. `user` 帧里的 `tool_result` 只产生 toolExecutionEnded；`isReplay:true` 的 user 帧和合成标记（§8.4）不产生任何对话事件。

### 14.3 result → agentEnded 状态

按顺序判断：

| 条件 | agentEnded.status |
|---|---|
| `terminal_reason ∈ {aborted_streaming, aborted_tools}`，或 TKCode 处于 Stopping 状态 | `interrupted` |
| `subtype == "success"` 且 `is_error == false` | `completed` |
| `subtype == "success"` 且 `is_error == true` | `error`（API 错误，带 `api_error_status`） |
| `subtype == "error_max_turns"` | `maxTurns` |
| `subtype == "error_max_budget_usd"` | `budgetExceeded` |
| 其他 `error_*` | `error` |

`result.session_id` 总是更新到会话头部的 `nativeSessionId`（分叉时会变）。

### 14.4 子 Agent 归属

- `parent_tool_use_id` 指向启动子 Agent 的 `Agent`/`Task`（或 `Skill`）工具调用 ID；嵌套子 Agent 指向它自己的父调用，由此可重建嵌套树。以 `/<skill>` 作为 prompt 启动的 fork 技能，`parent_tool_use_id` 以 `forked-command-` 开头。[Doc:headless §Follow subagent messages]
- 子 Agent 的结束：父会话的 `Agent` 工具的 `tool_result`（前台），或 `system/task_notification`（后台，`status`：`completed`/`failed`/`stopped`；`task_updated` 用 `killed`）。只有已知的 Agent 启动调用才按子 Agent 结算——后台 Bash 任务也用同样的 subtype，带的是 Bash 调用的 id。[zeron:normalize.rs `agent_spawn_tools`；Py:types.py `TERMINAL_TASK_STATUSES`]

---

## 15. 审批映射

TKCode 的 `tool/approve` 有三个选项，映射到 `can_use_tool` 应答：

| TKCode 选项 | 应答 `response.response` |
|---|---|
| **允许一次** | `{"behavior":"allow","updatedInput":<原 input>,"toolUseID":<tool_use_id>,"decisionClassification":"user_temporary"}` |
| **本会话允许** | `{"behavior":"allow","updatedInput":<原 input>,"updatedPermissions":<见下>,"toolUseID":…,"decisionClassification":"user_permanent"}` |
| **拒绝** | `{"behavior":"deny","message":"用户拒绝了这次操作。","toolUseID":…,"decisionClassification":"user_reject"}` |
| 拒绝并停止 | 同上，加 `"interrupt":true`（整个轮次中止，result 为 aborted） |
| UI 无响应 / 断开 / 会话关闭 | 回 `deny`，`message` 写明原因；**绝不默认放行** |

"本会话允许"的 `updatedPermissions` 构造：

1. 若 `permission_suggestions` 非空：逐条复制，把 `destination` **改写为 `"session"`**（CLI 的建议常是 `localSettings`，原样回传会写入 `.claude/settings.local.json`，影响以后的会话）。[Doc:agent-sdk/user-input §Approve and remember；TS:`PermissionUpdateDestination`]
2. 若为空：构造 `[{"type":"addRules","rules":[{"toolName":<tool_name>}],"behavior":"allow","destination":"session"}]`（不带 `ruleContent` = 整个工具）。对 Bash 这类按命令区分的工具，最好让用户确认范围；首版可只在有建议时提供"本会话允许"。
3. `suppress_always_allow_rule == true` 时**不提供**"本会话允许"选项。`default_to_no == true` 时默认焦点放在"拒绝"。`requires_user_interaction == true` 的请求不能一键批准，要打开对应的交互面板（如 `AskUserQuestion`、计划审阅 `ExitPlanMode`）。[TS]

`PermissionUpdate` 形状（[TS:sdk.d.ts]；[Py:types.py `PermissionUpdate.to_dict`]）：

```json
{"type":"addRules"|"replaceRules"|"removeRules","rules":[{"toolName":"Bash","ruleContent":"npm test"}],"behavior":"allow"|"deny"|"ask","destination":"session"}
{"type":"setMode","mode":"acceptEdits","destination":"session"}
{"type":"addDirectories"|"removeDirectories","directories":["/abs/path"],"destination":"session"}
```

`destination` ∈ `userSettings`、`projectSettings`、`localSettings`、`session`、`cliArg`。

自动放行模式下（会话级开关）：TKCode 用 `set_permission_mode` 切到 `bypassPermissions`（需启动时带 `--allow-dangerously-skip-permissions`），或重启进程。此时仍可能收到 `can_use_tool`（`AskUserQuestion`、需要用户交互的 MCP 工具），照常处理。

---

## 16. 实现必须满足（Checklist）

**启动**

- [ ] argv 至少包含 `-p --input-format stream-json --output-format stream-json --verbose --include-partial-messages --replay-user-messages --permission-prompt-tool stdio --thinking-display summarized`；`--permission-mode` 显式传入。
- [ ] `--resume`、`--session-id`、`--resume-session-at` 一律用 `--flag=value` 形式；会话 ID 先校验为 UUID。
- [ ] 不传 `--system-prompt`、不默认 `--bare`；`--setting-sources` 默认不传。
- [ ] 环境：删除 `CLAUDECODE`；`CLAUDE_CODE_ENTRYPOINT=sdk-cli`（不冒充 sdk-py/sdk-ts）；`CLAUDE_CODE_SDK_READS_SESSION_STATE=1`；`PWD=cwd`；PATH 前置可执行文件目录。
- [ ] Windows 上拒绝 `.cmd`/`.bat`，只启动原生 `claude.exe`。
- [ ] stdin 用无 BOM 的 UTF-8；stderr 持续读取，只保留脱敏后的尾部。
- [ ] 启动后第一行写 `initialize`，等到成功应答（60 秒超时，同时监听 result 和进程退出）后才写第一条 user 消息。

**传输**

- [ ] 按 `\n` 分帧，块不 trim、行 trim；跳过空行和非 `{` 开头的行；损坏的 JSON 行记日志后跳过。
- [ ] 单行上限 ≥ 64 MiB，流式拼接，不用固定小缓冲。
- [ ] 所有 stdin 写入经单一写入者串行化，每行写完立即 flush；EPIPE 容忍。
- [ ] 会话存活期间**绝不关闭 stdin**；只在拆除时关闭。
- [ ] 未知 `type`、`subtype`、字段、content block 类型全部忽略或原样保留，不报错。

**控制协议**

- [ ] TKCode 发起的请求按 `request_id` 关联应答；`subtype:"error"` 转为失败；60 秒超时；进程退出时在途请求立即失败。
- [ ] CLI 发起的每个 `control_request` 都有且只有一个应答（被 `control_cancel_request` 撤回的除外）；未知 subtype 回 error。
- [ ] 读循环不被审批阻塞；审批在后台等待 UI。
- [ ] 收到 `control_cancel_request` 时关闭对应对话框、不再应答。
- [ ] 支持 `interrupt`（按 capability 决定是否带 `cancel_queued:true`）、`set_model`、`set_permission_mode`、`apply_flag_settings`。

**审批**

- [ ] allow 应答总是带 `updatedInput`（未修改时回传原 input）。
- [ ] "本会话允许"把建议的 `destination` 改写为 `session`；`suppress_always_allow_rule` 时隐藏该选项；`default_to_no` 时默认拒绝。
- [ ] 任何异常（UI 断开、超时、会话关闭）都回 `deny`，绝不默认放行。
- [x] `AskUserQuestion` 走 `input/request`，以 allow + `updatedInput.answers`（键为问题文本，值为 label）回复；拒绝回答时 deny（§6.1.1）。
- [ ] `decision_reason` 显示前清洗 ANSI 转义。

**消息与事件**

- [ ] 每条 stdin user 消息带新生成的 `uuid` 和 `parent_tool_use_id:null`；图片作为 base64 image block 放在文本之前，单张 > 5 MB 改为路径引用。
- [ ] steering：有工具在运行时用 `priority:"next"`，否则 `"now"`。
- [ ] 以 stream_event 为主产生增量事件；assistant 帧按 §14.2 去重（`message.id` 集合 + `tool_use.id` 集合），没有流事件的消息由 assistant 帧合成事件。
- [ ] toolCallEnded 在完整 assistant 帧或 `content_block_stop` 中先到者触发且只触发一次；`message_stop` 时未关闭的工具块标记为不完整。
- [ ] `signature_delta` 赋值不追加；空的 `thinking_delta` 不产生 thinkingDelta。
- [ ] 保留工具输出：`tool_result.content` 和 `tool_use_result` 都要进转录。
- [ ] `isReplay:true` 的 user 帧、`[Request interrupted…`、`<system-reminder>` 前缀文本不当作用户消息。
- [ ] `parent_tool_use_id` 非 null 的帧带 `parentToolCallId`，不混入父轮次，不影响父轮次的 messageId。
- [ ] 第一次 `system/init` 建立会话；之后的 init 只刷新元数据，不重置转录。

**轮次与用量**

- [ ] 只以 `result` 判断轮次结束；不用静默计时器（唯一例外：空闲时 interrupt 应答后 3 秒无 result 视为已结算）。
- [ ] result 状态按 §14.3 映射；`terminal_reason` 为 `aborted_*` 即为中断，不论 `subtype`。
- [ ] `errors` 中的 `[ede_diagnostic]` 只写日志；为空时用固定文案兜底。
- [ ] `subtype:"success"` + `is_error:true` 当作错误处理；同时显示合成 assistant 帧的 `error` 类别。
- [ ] `total_cost_usd`、`modelUsage` 是进程内累计值，按差值计算本轮次；`result.usage` 只含主循环。
- [ ] 后台唤醒轮次（没有 TKCode 输入的新 init + result）作为新轮次处理。
- [ ] `rate_limit_event` 只在真正阻塞时报错（考虑 overage 字段）。
- [ ] 进程无 result 退出时合成 agentEnded(错误)，附退出码和 stderr 尾部；退出码 1 时优先显示最后一个 is_error result 的文本。

**中断与拆除**

- [ ] 停止：`interrupt` → 等 result（应答后 3 秒兜底）→ 丢弃停止后 3 秒内无新活动的迟到 result。
- [ ] 拆除：interrupt（若在跑，等 2 秒）→ 关 stdin → 等 5 秒 → 进程组 SIGTERM → 等 5 秒 → SIGKILL；Windows 用 Job Object。
- [ ] TKCode 退出时清理所有存活的 CLI 子进程。

**探测**

- [ ] `claude --version`（2 秒超时）解析版本；低于 2.1.290 警告；运行中以 `system/init.claude_code_version` 为准。
- [ ] `claude auth status` 以退出码判断是否登录，只读取 `loggedIn`、`authMethod`、`apiProvider`、`subscriptionType`，不落盘个人信息。
- [ ] 模型和命令发现用短命进程的 `initialize`（10 秒超时，缓存 2 分钟，凭据或可执行文件变化时失效），不触发模型轮次。
- [ ] 功能差异用 `system/init.capabilities` 判断。

**测试**

- [ ] 附录 A 的夹具逐行作为解析器和映射器的契约测试；另外用假 CLI 脚本回放它们，覆盖进程管理路径。
- [ ] 每次升级 CLI 基线时录制真实 JSONL，替换标注为 [推断] 的字段。

---

## 附录 A. 测试夹具

### A.0 格式约定

- 每行 = 2 字符方向前缀 + 一个完整 JSON 对象：
  - `> ` = TKCode 写入 CLI 的 stdin
  - `< ` = CLI 写到 stdout
- 测试代码去掉前 2 个字符后按行解析；代码块内不含其他内容。
- 时间、ID、token 数为虚构但自洽。所有夹具共用：
  - cwd `/Users/dev/proj`，模型 `claude-opus-5-5`，CLI 2.1.301。
  - 会话 ID `5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11`（(e2) 除外）。
- 可信度：
  - **已核实形状**（来源见正文对应章节）：control_request/response/cancel 信封；initialize 请求与应答的字段名；`can_use_tool` 请求与 allow/deny 应答；interrupt 请求与 `still_queued` 应答；user 消息（含 `uuid`、`priority`）；回显（`isReplay`）；`system/init`、`system/api_retry`、`system/status`、`system/session_state_changed` 的字段；`stream_event` 包装及内部的 Messages API 事件；每块一个 assistant 帧且先于 `content_block_stop`；`result` 字段及 `terminal_reason`；`rate_limit_event`；`command_lifecycle`；零值错误 result（(e2) 的形状逐字段对应 CLI 源码 `aH()`）。
  - **[推断]**：帧之间的确切交错顺序（尤其是 `command_lifecycle`、回显、`system/init`、`session_state_changed`、`rate_limit_event` 的相对位置）；`permission_suggestions` 的具体规则内容；`tool_use_result` 的具体内容；被拒绝工具的 `tool_result.content` 文本；中断后是否输出 `aborted:true` 的 assistant 帧和 `[Request interrupted by user]` user 帧；认证失败的错误文本、`terminal_reason:"api_error"`；`usage` 的具体数值和部分可选字段。
- 解析器必须对这些夹具产生 §14 规定的事件序列；对 [推断] 部分的顺序变化保持稳健（测试可以再加打乱顺序的变体）。

### A.1 (a) 握手 + system/init + 带 thinking 的纯文本回答

期望事件：agentStarted → messageStarted(msg_01A) → thinkingDelta×2 → textDelta×2 → messageEnded(end_turn) → usage → agentEnded(completed)。thinking 和 text 的 assistant 帧不产生重复 delta。

```jsonl
> {"type":"control_request","request_id":"tkcode_init_1","request":{"subtype":"initialize","hooks":null}}
< {"type":"control_response","response":{"subtype":"success","request_id":"tkcode_init_1","response":{"commands":[{"name":"compact","description":"Clear conversation history but keep a summary in context","argumentHint":"<optional custom summarization instructions>"},{"name":"review","description":"Review a pull request","argumentHint":""}],"agents":[{"name":"general-purpose","description":"General-purpose agent for researching complex questions and executing multi-step tasks"},{"name":"Explore","description":"Fast agent specialized for exploring codebases","model":"haiku"}],"output_style":"default","available_output_styles":["default","Explanatory","Learning"],"models":[{"value":"default","resolvedModel":"claude-opus-5-5","displayName":"Default (recommended)","description":"Opus 5.5 · Most capable for complex work","supportsEffort":true,"supportedEffortLevels":["low","medium","high","xhigh","max"],"supportsAdaptiveThinking":true,"supportsFastMode":true,"supportsAutoMode":true},{"value":"sonnet","resolvedModel":"claude-sonnet-5","displayName":"Sonnet","description":"Sonnet 5 · Best for everyday tasks","supportsEffort":true,"supportedEffortLevels":["low","medium","high","xhigh","max"],"supportsAdaptiveThinking":true},{"value":"haiku","resolvedModel":"claude-haiku-4-5-20251001","displayName":"Haiku","description":"Haiku 4.5 · Fastest for quick answers"}],"account":{"subscriptionType":"max","tokenSource":"claude.ai","apiKeySource":"none","apiProvider":"firstParty"},"claude_code_version":"2.1.301","fast_mode_state":"off"},"pending_permission_requests":[],"pending_user_dialog_requests":[]}}
> {"type":"user","message":{"role":"user","content":[{"type":"text","text":"用一句话解释什么是递归。"}]},"parent_tool_use_id":null,"uuid":"3f9a6c1e-2b7d-4e8a-9c05-1d2e3f4a5b61"}
< {"type":"command_lifecycle","command_uuid":"3f9a6c1e-2b7d-4e8a-9c05-1d2e3f4a5b61","state":"queued","uuid":"c0a80101-0000-4000-8000-000000000001","session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11"}
< {"type":"user","message":{"role":"user","content":[{"type":"text","text":"用一句话解释什么是递归。"}]},"parent_tool_use_id":null,"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","uuid":"3f9a6c1e-2b7d-4e8a-9c05-1d2e3f4a5b61","timestamp":"2026-10-16T08:00:00.120Z","isReplay":true}
< {"type":"system","subtype":"session_state_changed","state":"running","sdk_host_only":true,"uuid":"c0a80101-0000-4000-8000-000000000002","session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11"}
< {"type":"system","subtype":"init","cwd":"/Users/dev/proj","session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","tools":["Task","Bash","Glob","Grep","Read","Edit","Write","WebFetch","WebSearch","TodoWrite","AskUserQuestion","mcp__tkcode__open_file"],"mcp_servers":[{"name":"tkcode","status":"connected"}],"model":"claude-opus-5-5","permissionMode":"default","slash_commands":["compact","context","cost","init","review"],"apiKeySource":"none","claude_code_version":"2.1.301","output_style":"default","agents":["general-purpose","Explore","Plan"],"skills":[],"plugins":[],"capabilities":["interrupt_receipt_v1","interrupt_cancel_queued_v1"],"fast_mode_state":"off","uuid":"c0a80101-0000-4000-8000-000000000003"}
< {"type":"command_lifecycle","command_uuid":"3f9a6c1e-2b7d-4e8a-9c05-1d2e3f4a5b61","state":"started","uuid":"c0a80101-0000-4000-8000-000000000004","session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11"}
< {"type":"system","subtype":"status","status":"requesting","uuid":"c0a80101-0000-4000-8000-000000000005","session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11"}
< {"type":"stream_event","event":{"type":"message_start","message":{"model":"claude-opus-5-5","id":"msg_01AbCdEfGhIjKlMnOpQrStUv","type":"message","role":"assistant","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":3,"cache_creation_input_tokens":1520,"cache_read_input_tokens":14210,"output_tokens":1,"service_tier":"standard"}}},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80101-0000-4000-8000-000000000006","ttft_ms":812,"user_message_uuid":"3f9a6c1e-2b7d-4e8a-9c05-1d2e3f4a5b61","user_message_uuids":["3f9a6c1e-2b7d-4e8a-9c05-1d2e3f4a5b61"]}
< {"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1792155600,"rateLimitType":"five_hour","overageStatus":"rejected","overageDisabledReason":"org_level_disabled","isUsingOverage":false},"uuid":"c0a80101-0000-4000-8000-000000000007","session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11"}
< {"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80101-0000-4000-8000-000000000008"}
< {"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"用户想要一个简洁的定义，"}},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80101-0000-4000-8000-000000000009"}
< {"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"一句话即可。"}},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80101-0000-4000-8000-00000000000a"}
< {"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"EuYBCkQIBxgCKkBzZWNyZXQtc2lnbmF0dXJl"}},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80101-0000-4000-8000-00000000000b"}
< {"type":"assistant","message":{"model":"claude-opus-5-5","id":"msg_01AbCdEfGhIjKlMnOpQrStUv","type":"message","role":"assistant","content":[{"type":"thinking","thinking":"用户想要一个简洁的定义，一句话即可。","signature":"EuYBCkQIBxgCKkBzZWNyZXQtc2lnbmF0dXJl"}],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":3,"cache_creation_input_tokens":1520,"cache_read_input_tokens":14210,"output_tokens":1,"service_tier":"standard"}},"parent_tool_use_id":null,"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","uuid":"c0a80101-0000-4000-8000-00000000000c","user_message_uuid":"3f9a6c1e-2b7d-4e8a-9c05-1d2e3f4a5b61","user_message_uuids":["3f9a6c1e-2b7d-4e8a-9c05-1d2e3f4a5b61"]}
< {"type":"stream_event","event":{"type":"content_block_stop","index":0},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80101-0000-4000-8000-00000000000d"}
< {"type":"stream_event","event":{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80101-0000-4000-8000-00000000000e"}
< {"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"递归就是一个函数在定义中调用自身，"}},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80101-0000-4000-8000-00000000000f"}
< {"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"把问题拆成更小的同类问题，直到遇到可以直接回答的基本情况。"}},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80101-0000-4000-8000-000000000010"}
< {"type":"assistant","message":{"model":"claude-opus-5-5","id":"msg_01AbCdEfGhIjKlMnOpQrStUv","type":"message","role":"assistant","content":[{"type":"text","text":"递归就是一个函数在定义中调用自身，把问题拆成更小的同类问题，直到遇到可以直接回答的基本情况。"}],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":3,"cache_creation_input_tokens":1520,"cache_read_input_tokens":14210,"output_tokens":1,"service_tier":"standard"}},"parent_tool_use_id":null,"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","uuid":"c0a80101-0000-4000-8000-000000000011"}
< {"type":"stream_event","event":{"type":"content_block_stop","index":1},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80101-0000-4000-8000-000000000012"}
< {"type":"stream_event","event":{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"input_tokens":3,"cache_creation_input_tokens":1520,"cache_read_input_tokens":14210,"output_tokens":96}},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80101-0000-4000-8000-000000000013"}
< {"type":"stream_event","event":{"type":"message_stop"},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80101-0000-4000-8000-000000000014"}
< {"type":"result","subtype":"success","is_error":false,"duration_ms":2841,"duration_api_ms":2610,"ttft_ms":812,"num_turns":1,"result":"递归就是一个函数在定义中调用自身，把问题拆成更小的同类问题，直到遇到可以直接回答的基本情况。","stop_reason":"end_turn","session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","total_cost_usd":0.0213,"usage":{"input_tokens":3,"cache_creation_input_tokens":1520,"cache_read_input_tokens":14210,"output_tokens":96,"server_tool_use":{"web_search_requests":0,"web_fetch_requests":0},"service_tier":"standard"},"modelUsage":{"claude-opus-5-5":{"inputTokens":3,"outputTokens":96,"cacheReadInputTokens":14210,"cacheCreationInputTokens":1520,"webSearchRequests":0,"costUSD":0.0213,"contextWindow":1000000,"maxOutputTokens":128000}},"permission_denials":[],"terminal_reason":"completed","queued_turn_count":0,"result_index":0,"user_message_uuid":"3f9a6c1e-2b7d-4e8a-9c05-1d2e3f4a5b61","user_message_uuids":["3f9a6c1e-2b7d-4e8a-9c05-1d2e3f4a5b61"],"fast_mode_state":"off","uuid":"c0a80101-0000-4000-8000-000000000015"}
< {"type":"command_lifecycle","command_uuid":"3f9a6c1e-2b7d-4e8a-9c05-1d2e3f4a5b61","state":"completed","uuid":"c0a80101-0000-4000-8000-000000000016","session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11"}
< {"type":"system","subtype":"session_state_changed","state":"idle","sdk_host_only":true,"uuid":"c0a80101-0000-4000-8000-000000000017","session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11"}
```

### A.2 (b) 工具调用：input_json_delta → can_use_tool → 允许 → tool_result → 最终回答

前置：已完成 (a) 的握手（同一进程的第二个轮次，所以 `total_cost_usd`/`modelUsage` 是累计值：本轮次费用 = 0.0561 − 0.0213 = 0.0348）。

期望事件：agentStarted → messageStarted(msg_01B) → textDelta → toolCallStarted(toolu_01Bash) → toolCallDelta×2 → toolCallEnded(input 来自 assistant 帧) → messageEnded(tool_use) → [审批 UI：允许一次] → toolExecutionStarted → toolExecutionEnded(stdout) → messageStarted(msg_01C) → textDelta → messageEnded(end_turn) → usage → agentEnded(completed)。

```jsonl
> {"type":"user","message":{"role":"user","content":[{"type":"text","text":"列出 src 目录下的文件。"}]},"parent_tool_use_id":null,"uuid":"7c2e9b40-5a1f-4d63-8e07-9b3c2d1e0f72"}
< {"type":"command_lifecycle","command_uuid":"7c2e9b40-5a1f-4d63-8e07-9b3c2d1e0f72","state":"queued","uuid":"c0a80102-0000-4000-8000-000000000001","session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11"}
< {"type":"user","message":{"role":"user","content":[{"type":"text","text":"列出 src 目录下的文件。"}]},"parent_tool_use_id":null,"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","uuid":"7c2e9b40-5a1f-4d63-8e07-9b3c2d1e0f72","timestamp":"2026-10-16T08:01:00.050Z","isReplay":true}
< {"type":"system","subtype":"init","cwd":"/Users/dev/proj","session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","tools":["Task","Bash","Glob","Grep","Read","Edit","Write","WebFetch","WebSearch","TodoWrite","AskUserQuestion","mcp__tkcode__open_file"],"mcp_servers":[{"name":"tkcode","status":"connected"}],"model":"claude-opus-5-5","permissionMode":"default","slash_commands":["compact","context","cost","init","review"],"apiKeySource":"none","claude_code_version":"2.1.301","output_style":"default","agents":["general-purpose","Explore","Plan"],"skills":[],"plugins":[],"capabilities":["interrupt_receipt_v1","interrupt_cancel_queued_v1"],"fast_mode_state":"off","uuid":"c0a80102-0000-4000-8000-000000000002"}
< {"type":"command_lifecycle","command_uuid":"7c2e9b40-5a1f-4d63-8e07-9b3c2d1e0f72","state":"started","uuid":"c0a80102-0000-4000-8000-000000000003","session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11"}
< {"type":"stream_event","event":{"type":"message_start","message":{"model":"claude-opus-5-5","id":"msg_01BcDeFgHiJkLmNoPqRsTuVw","type":"message","role":"assistant","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":2,"cache_creation_input_tokens":210,"cache_read_input_tokens":15730,"output_tokens":1,"service_tier":"standard"}}},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80102-0000-4000-8000-000000000004","user_message_uuid":"7c2e9b40-5a1f-4d63-8e07-9b3c2d1e0f72","user_message_uuids":["7c2e9b40-5a1f-4d63-8e07-9b3c2d1e0f72"]}
< {"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80102-0000-4000-8000-000000000005"}
< {"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"我来列出 src 目录。"}},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80102-0000-4000-8000-000000000006"}
< {"type":"assistant","message":{"model":"claude-opus-5-5","id":"msg_01BcDeFgHiJkLmNoPqRsTuVw","type":"message","role":"assistant","content":[{"type":"text","text":"我来列出 src 目录。"}],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":2,"cache_creation_input_tokens":210,"cache_read_input_tokens":15730,"output_tokens":1,"service_tier":"standard"}},"parent_tool_use_id":null,"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","uuid":"c0a80102-0000-4000-8000-000000000007","user_message_uuid":"7c2e9b40-5a1f-4d63-8e07-9b3c2d1e0f72","user_message_uuids":["7c2e9b40-5a1f-4d63-8e07-9b3c2d1e0f72"]}
< {"type":"stream_event","event":{"type":"content_block_stop","index":0},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80102-0000-4000-8000-000000000008"}
< {"type":"stream_event","event":{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_01BashLsSrc000000000001","name":"Bash","input":{}}},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80102-0000-4000-8000-000000000009"}
< {"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"command\": \"ls -1 s"}},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80102-0000-4000-8000-00000000000a"}
< {"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"rc\", \"description\": \"List files in src\"}"}},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80102-0000-4000-8000-00000000000b"}
< {"type":"assistant","message":{"model":"claude-opus-5-5","id":"msg_01BcDeFgHiJkLmNoPqRsTuVw","type":"message","role":"assistant","content":[{"type":"tool_use","id":"toolu_01BashLsSrc000000000001","name":"Bash","input":{"command":"ls -1 src","description":"List files in src"}}],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":2,"cache_creation_input_tokens":210,"cache_read_input_tokens":15730,"output_tokens":1,"service_tier":"standard"}},"parent_tool_use_id":null,"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","uuid":"c0a80102-0000-4000-8000-00000000000c"}
< {"type":"stream_event","event":{"type":"content_block_stop","index":1},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80102-0000-4000-8000-00000000000d"}
< {"type":"stream_event","event":{"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"input_tokens":2,"cache_creation_input_tokens":210,"cache_read_input_tokens":15730,"output_tokens":58}},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80102-0000-4000-8000-00000000000e"}
< {"type":"stream_event","event":{"type":"message_stop"},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80102-0000-4000-8000-00000000000f"}
< {"type":"control_request","request_id":"0b7e4f2a-9c13-4e58-a6d1-3f2e1d0c9b8a","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"ls -1 src","description":"List files in src"},"permission_suggestions":[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"ls -1 src"}],"behavior":"allow","destination":"localSettings"}],"tool_use_id":"toolu_01BashLsSrc000000000001","description":"List files in src"}}
< {"type":"system","subtype":"session_state_changed","state":"requires_action","sdk_host_only":true,"uuid":"c0a80102-0000-4000-8000-000000000010","session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11"}
> {"type":"control_response","response":{"subtype":"success","request_id":"0b7e4f2a-9c13-4e58-a6d1-3f2e1d0c9b8a","response":{"behavior":"allow","updatedInput":{"command":"ls -1 src","description":"List files in src"},"toolUseID":"toolu_01BashLsSrc000000000001","decisionClassification":"user_temporary"}}}
< {"type":"system","subtype":"session_state_changed","state":"running","sdk_host_only":true,"uuid":"c0a80102-0000-4000-8000-000000000011","session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11"}
< {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_01BashLsSrc000000000001","type":"tool_result","content":"App.cs\nProgram.cs\nViews","is_error":false}]},"parent_tool_use_id":null,"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","uuid":"c0a80102-0000-4000-8000-000000000012","timestamp":"2026-10-16T08:01:04.310Z","tool_use_result":{"stdout":"App.cs\nProgram.cs\nViews","stderr":"","interrupted":false,"isImage":false,"noOutputExpected":false}}
< {"type":"stream_event","event":{"type":"message_start","message":{"model":"claude-opus-5-5","id":"msg_01CdEfGhIjKlMnOpQrStUvWx","type":"message","role":"assistant","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1,"cache_creation_input_tokens":96,"cache_read_input_tokens":15940,"output_tokens":1,"service_tier":"standard"}}},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80102-0000-4000-8000-000000000013"}
< {"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80102-0000-4000-8000-000000000014"}
< {"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"src 下有 App.cs、Program.cs 和 Views 目录。"}},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80102-0000-4000-8000-000000000015"}
< {"type":"assistant","message":{"model":"claude-opus-5-5","id":"msg_01CdEfGhIjKlMnOpQrStUvWx","type":"message","role":"assistant","content":[{"type":"text","text":"src 下有 App.cs、Program.cs 和 Views 目录。"}],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1,"cache_creation_input_tokens":96,"cache_read_input_tokens":15940,"output_tokens":1,"service_tier":"standard"}},"parent_tool_use_id":null,"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","uuid":"c0a80102-0000-4000-8000-000000000016"}
< {"type":"stream_event","event":{"type":"content_block_stop","index":0},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80102-0000-4000-8000-000000000017"}
< {"type":"stream_event","event":{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"input_tokens":1,"cache_creation_input_tokens":96,"cache_read_input_tokens":15940,"output_tokens":24}},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80102-0000-4000-8000-000000000018"}
< {"type":"stream_event","event":{"type":"message_stop"},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80102-0000-4000-8000-000000000019"}
< {"type":"result","subtype":"success","is_error":false,"duration_ms":6120,"duration_api_ms":3890,"num_turns":2,"result":"src 下有 App.cs、Program.cs 和 Views 目录。","stop_reason":"end_turn","session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","total_cost_usd":0.0561,"usage":{"input_tokens":3,"cache_creation_input_tokens":306,"cache_read_input_tokens":31670,"output_tokens":82,"server_tool_use":{"web_search_requests":0,"web_fetch_requests":0},"service_tier":"standard"},"modelUsage":{"claude-opus-5-5":{"inputTokens":6,"outputTokens":178,"cacheReadInputTokens":45880,"cacheCreationInputTokens":1826,"webSearchRequests":0,"costUSD":0.0561,"contextWindow":1000000,"maxOutputTokens":128000}},"permission_denials":[],"terminal_reason":"completed","queued_turn_count":0,"result_index":1,"user_message_uuid":"7c2e9b40-5a1f-4d63-8e07-9b3c2d1e0f72","user_message_uuids":["7c2e9b40-5a1f-4d63-8e07-9b3c2d1e0f72"],"uuid":"c0a80102-0000-4000-8000-00000000001a"}
< {"type":"command_lifecycle","command_uuid":"7c2e9b40-5a1f-4d63-8e07-9b3c2d1e0f72","state":"completed","uuid":"c0a80102-0000-4000-8000-00000000001b","session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11"}
< {"type":"system","subtype":"session_state_changed","state":"idle","sdk_host_only":true,"uuid":"c0a80102-0000-4000-8000-00000000001c","session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11"}
```

### A.3 (c) can_use_tool 拒绝

前置：已完成握手。用户选择"拒绝"。

期望事件：agentStarted → messageStarted → toolCallStarted(toolu_01Rm) → toolCallDelta → toolCallEnded → messageEnded(tool_use) → [审批 UI：拒绝] → toolExecutionEnded(status=denied, isError=true，**没有** toolExecutionStarted) → messageStarted → textDelta → messageEnded → agentEnded(completed)。`result.permission_denials` 列出被拒调用。

```jsonl
> {"type":"user","message":{"role":"user","content":[{"type":"text","text":"清理 build 目录。"}]},"parent_tool_use_id":null,"uuid":"9d4f1a62-3b8c-4e7d-a051-6c2b8e9f1d03"}
< {"type":"command_lifecycle","command_uuid":"9d4f1a62-3b8c-4e7d-a051-6c2b8e9f1d03","state":"queued","uuid":"c0a80103-0000-4000-8000-000000000001","session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11"}
< {"type":"user","message":{"role":"user","content":[{"type":"text","text":"清理 build 目录。"}]},"parent_tool_use_id":null,"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","uuid":"9d4f1a62-3b8c-4e7d-a051-6c2b8e9f1d03","timestamp":"2026-10-16T08:02:00.010Z","isReplay":true}
< {"type":"system","subtype":"init","cwd":"/Users/dev/proj","session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","tools":["Task","Bash","Glob","Grep","Read","Edit","Write","WebFetch","WebSearch","TodoWrite","AskUserQuestion","mcp__tkcode__open_file"],"mcp_servers":[{"name":"tkcode","status":"connected"}],"model":"claude-opus-5-5","permissionMode":"default","slash_commands":["compact","context","cost","init","review"],"apiKeySource":"none","claude_code_version":"2.1.301","output_style":"default","agents":["general-purpose","Explore","Plan"],"skills":[],"plugins":[],"capabilities":["interrupt_receipt_v1","interrupt_cancel_queued_v1"],"fast_mode_state":"off","uuid":"c0a80103-0000-4000-8000-000000000002"}
< {"type":"command_lifecycle","command_uuid":"9d4f1a62-3b8c-4e7d-a051-6c2b8e9f1d03","state":"started","uuid":"c0a80103-0000-4000-8000-000000000003","session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11"}
< {"type":"stream_event","event":{"type":"message_start","message":{"model":"claude-opus-5-5","id":"msg_01DeFgHiJkLmNoPqRsTuVwXy","type":"message","role":"assistant","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":2,"cache_creation_input_tokens":140,"cache_read_input_tokens":16100,"output_tokens":1,"service_tier":"standard"}}},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80103-0000-4000-8000-000000000004","user_message_uuid":"9d4f1a62-3b8c-4e7d-a051-6c2b8e9f1d03","user_message_uuids":["9d4f1a62-3b8c-4e7d-a051-6c2b8e9f1d03"]}
< {"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_01RmBuild0000000000002","name":"Bash","input":{}}},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80103-0000-4000-8000-000000000005"}
< {"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"command\": \"rm -rf build\", \"description\": \"Remove build output\"}"}},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80103-0000-4000-8000-000000000006"}
< {"type":"assistant","message":{"model":"claude-opus-5-5","id":"msg_01DeFgHiJkLmNoPqRsTuVwXy","type":"message","role":"assistant","content":[{"type":"tool_use","id":"toolu_01RmBuild0000000000002","name":"Bash","input":{"command":"rm -rf build","description":"Remove build output"}}],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":2,"cache_creation_input_tokens":140,"cache_read_input_tokens":16100,"output_tokens":1,"service_tier":"standard"}},"parent_tool_use_id":null,"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","uuid":"c0a80103-0000-4000-8000-000000000007","user_message_uuid":"9d4f1a62-3b8c-4e7d-a051-6c2b8e9f1d03","user_message_uuids":["9d4f1a62-3b8c-4e7d-a051-6c2b8e9f1d03"]}
< {"type":"stream_event","event":{"type":"content_block_stop","index":0},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80103-0000-4000-8000-000000000008"}
< {"type":"stream_event","event":{"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"input_tokens":2,"cache_creation_input_tokens":140,"cache_read_input_tokens":16100,"output_tokens":41}},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80103-0000-4000-8000-000000000009"}
< {"type":"stream_event","event":{"type":"message_stop"},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80103-0000-4000-8000-00000000000a"}
< {"type":"control_request","request_id":"5e8d2c71-0a4b-4f96-b3e2-7a1c9d8e6f05","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"rm -rf build","description":"Remove build output"},"permission_suggestions":[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"rm -rf build"}],"behavior":"allow","destination":"localSettings"}],"tool_use_id":"toolu_01RmBuild0000000000002","description":"Remove build output"}}
> {"type":"control_response","response":{"subtype":"success","request_id":"5e8d2c71-0a4b-4f96-b3e2-7a1c9d8e6f05","response":{"behavior":"deny","message":"用户拒绝了这次操作。","toolUseID":"toolu_01RmBuild0000000000002","decisionClassification":"user_reject"}}}
< {"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"用户拒绝了这次操作。","is_error":true,"tool_use_id":"toolu_01RmBuild0000000000002"}]},"parent_tool_use_id":null,"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","uuid":"c0a80103-0000-4000-8000-00000000000b","timestamp":"2026-10-16T08:02:05.700Z"}
< {"type":"stream_event","event":{"type":"message_start","message":{"model":"claude-opus-5-5","id":"msg_01EfGhIjKlMnOpQrStUvWxYz","type":"message","role":"assistant","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1,"cache_creation_input_tokens":70,"cache_read_input_tokens":16240,"output_tokens":1,"service_tier":"standard"}}},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80103-0000-4000-8000-00000000000c"}
< {"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80103-0000-4000-8000-00000000000d"}
< {"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"好的，我不会删除 build 目录。需要的话你可以手动清理。"}},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80103-0000-4000-8000-00000000000e"}
< {"type":"assistant","message":{"model":"claude-opus-5-5","id":"msg_01EfGhIjKlMnOpQrStUvWxYz","type":"message","role":"assistant","content":[{"type":"text","text":"好的，我不会删除 build 目录。需要的话你可以手动清理。"}],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1,"cache_creation_input_tokens":70,"cache_read_input_tokens":16240,"output_tokens":1,"service_tier":"standard"}},"parent_tool_use_id":null,"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","uuid":"c0a80103-0000-4000-8000-00000000000f"}
< {"type":"stream_event","event":{"type":"content_block_stop","index":0},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80103-0000-4000-8000-000000000010"}
< {"type":"stream_event","event":{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"input_tokens":1,"cache_creation_input_tokens":70,"cache_read_input_tokens":16240,"output_tokens":30}},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80103-0000-4000-8000-000000000011"}
< {"type":"stream_event","event":{"type":"message_stop"},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80103-0000-4000-8000-000000000012"}
< {"type":"result","subtype":"success","is_error":false,"duration_ms":7410,"duration_api_ms":3120,"num_turns":2,"result":"好的，我不会删除 build 目录。需要的话你可以手动清理。","stop_reason":"end_turn","session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","total_cost_usd":0.0842,"usage":{"input_tokens":3,"cache_creation_input_tokens":210,"cache_read_input_tokens":32340,"output_tokens":71,"server_tool_use":{"web_search_requests":0,"web_fetch_requests":0},"service_tier":"standard"},"modelUsage":{"claude-opus-5-5":{"inputTokens":9,"outputTokens":249,"cacheReadInputTokens":78220,"cacheCreationInputTokens":2036,"webSearchRequests":0,"costUSD":0.0842,"contextWindow":1000000,"maxOutputTokens":128000}},"permission_denials":[{"tool_name":"Bash","tool_use_id":"toolu_01RmBuild0000000000002","tool_input":{"command":"rm -rf build","description":"Remove build output"}}],"terminal_reason":"completed","queued_turn_count":0,"result_index":2,"user_message_uuid":"9d4f1a62-3b8c-4e7d-a051-6c2b8e9f1d03","user_message_uuids":["9d4f1a62-3b8c-4e7d-a051-6c2b8e9f1d03"],"uuid":"c0a80103-0000-4000-8000-000000000013"}
< {"type":"command_lifecycle","command_uuid":"9d4f1a62-3b8c-4e7d-a051-6c2b8e9f1d03","state":"completed","uuid":"c0a80103-0000-4000-8000-000000000014","session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11"}
```

### A.4 (d) 轮次中途 interrupt

#### (d1) 文本流式输出时中断

前置：已完成握手。TKCode 在第二个 text_delta 之后发 interrupt（带 `cancel_queued`）。

期望事件：agentStarted → messageStarted(msg_01F) → textDelta×2 → [发出 interrupt，进入 Stopping] → messageEnded(stopReason=null) → agentEnded(**interrupted**)。判断依据是 `terminal_reason:"aborted_streaming"`，与 `subtype:"error_during_execution"`/`is_error:true` 无关；`[ede_diagnostic]` 只写日志；`[Request interrupted by user]` user 帧不产生对话事件；`aborted:true` 的 assistant 帧只用于校正转录，不产生重复 delta。

```jsonl
> {"type":"user","message":{"role":"user","content":[{"type":"text","text":"详细介绍一下这个项目的架构。"}]},"parent_tool_use_id":null,"uuid":"a8e3c5d7-6f21-4b09-9d4e-2c7b1a0f3e94"}
< {"type":"command_lifecycle","command_uuid":"a8e3c5d7-6f21-4b09-9d4e-2c7b1a0f3e94","state":"queued","uuid":"c0a80104-0000-4000-8000-000000000001","session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11"}
< {"type":"user","message":{"role":"user","content":[{"type":"text","text":"详细介绍一下这个项目的架构。"}]},"parent_tool_use_id":null,"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","uuid":"a8e3c5d7-6f21-4b09-9d4e-2c7b1a0f3e94","timestamp":"2026-10-16T08:03:00.000Z","isReplay":true}
< {"type":"system","subtype":"init","cwd":"/Users/dev/proj","session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","tools":["Task","Bash","Glob","Grep","Read","Edit","Write","WebFetch","WebSearch","TodoWrite","AskUserQuestion","mcp__tkcode__open_file"],"mcp_servers":[{"name":"tkcode","status":"connected"}],"model":"claude-opus-5-5","permissionMode":"default","slash_commands":["compact","context","cost","init","review"],"apiKeySource":"none","claude_code_version":"2.1.301","output_style":"default","agents":["general-purpose","Explore","Plan"],"skills":[],"plugins":[],"capabilities":["interrupt_receipt_v1","interrupt_cancel_queued_v1"],"fast_mode_state":"off","uuid":"c0a80104-0000-4000-8000-000000000002"}
< {"type":"command_lifecycle","command_uuid":"a8e3c5d7-6f21-4b09-9d4e-2c7b1a0f3e94","state":"started","uuid":"c0a80104-0000-4000-8000-000000000003","session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11"}
< {"type":"stream_event","event":{"type":"message_start","message":{"model":"claude-opus-5-5","id":"msg_01FgHiJkLmNoPqRsTuVwXyZa","type":"message","role":"assistant","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":2,"cache_creation_input_tokens":120,"cache_read_input_tokens":16380,"output_tokens":1,"service_tier":"standard"}}},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80104-0000-4000-8000-000000000004","user_message_uuid":"a8e3c5d7-6f21-4b09-9d4e-2c7b1a0f3e94","user_message_uuids":["a8e3c5d7-6f21-4b09-9d4e-2c7b1a0f3e94"]}
< {"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80104-0000-4000-8000-000000000005"}
< {"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"这个项目分为三层："}},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80104-0000-4000-8000-000000000006"}
< {"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"桌面 UI、Agent Host 和"}},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80104-0000-4000-8000-000000000007"}
> {"type":"control_request","request_id":"tkcode_int_1","request":{"subtype":"interrupt","cancel_queued":true}}
< {"type":"control_response","response":{"subtype":"success","request_id":"tkcode_int_1","response":{"still_queued":[],"cancelled":[]}}}
< {"type":"assistant","message":{"model":"claude-opus-5-5","id":"msg_01FgHiJkLmNoPqRsTuVwXyZa","type":"message","role":"assistant","content":[{"type":"text","text":"这个项目分为三层：桌面 UI、Agent Host 和"}],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":2,"cache_creation_input_tokens":120,"cache_read_input_tokens":16380,"output_tokens":1,"service_tier":"standard"}},"parent_tool_use_id":null,"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","uuid":"c0a80104-0000-4000-8000-000000000008","aborted":true}
< {"type":"stream_event","event":{"type":"content_block_stop","index":0},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80104-0000-4000-8000-000000000009"}
< {"type":"stream_event","event":{"type":"message_stop"},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80104-0000-4000-8000-00000000000a"}
< {"type":"user","message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user]"}]},"parent_tool_use_id":null,"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","uuid":"c0a80104-0000-4000-8000-00000000000b","timestamp":"2026-10-16T08:03:02.900Z"}
< {"type":"result","subtype":"error_during_execution","is_error":true,"duration_ms":2950,"duration_api_ms":2700,"num_turns":1,"stop_reason":null,"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","total_cost_usd":0.0907,"usage":{"input_tokens":2,"cache_creation_input_tokens":120,"cache_read_input_tokens":16380,"output_tokens":18,"server_tool_use":{"web_search_requests":0,"web_fetch_requests":0},"service_tier":"standard"},"modelUsage":{"claude-opus-5-5":{"inputTokens":11,"outputTokens":267,"cacheReadInputTokens":94600,"cacheCreationInputTokens":2156,"webSearchRequests":0,"costUSD":0.0907,"contextWindow":1000000,"maxOutputTokens":128000}},"permission_denials":[],"errors":["[ede_diagnostic] result_type=user last_content_type=n/a stop_reason=null"],"terminal_reason":"aborted_streaming","queued_turn_count":0,"result_index":3,"user_message_uuid":"a8e3c5d7-6f21-4b09-9d4e-2c7b1a0f3e94","user_message_uuids":["a8e3c5d7-6f21-4b09-9d4e-2c7b1a0f3e94"],"uuid":"c0a80104-0000-4000-8000-00000000000c"}
< {"type":"command_lifecycle","command_uuid":"a8e3c5d7-6f21-4b09-9d4e-2c7b1a0f3e94","state":"cancelled","uuid":"c0a80104-0000-4000-8000-00000000000d","session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11"}
```

#### (d2) 等待审批时中断（CLI 撤回审批请求）

前置：已完成握手。工具块已结束，CLI 发出 `can_use_tool`，用户还没回答时按了停止。

期望事件：…toolCallEnded → messageEnded(tool_use) → [审批对话框打开] → [发出 interrupt] → 收到 `control_cancel_request` 后关闭对话框，**不发送应答** → toolExecutionEnded(status=cancelled，若有 tool_result) → agentEnded(interrupted，依据 `terminal_reason:"aborted_tools"`)。这个变体里 `subtype` 是 `success`（转录最后一条是全为 tool_result 的 user 消息），同样判为中断。

```jsonl
> {"type":"user","message":{"role":"user","content":[{"type":"text","text":"运行全部测试。"}]},"parent_tool_use_id":null,"uuid":"b2f7d9e1-4c3a-4a58-8f60-1e9d7c5b3a25"}
< {"type":"system","subtype":"init","cwd":"/Users/dev/proj","session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","tools":["Task","Bash","Glob","Grep","Read","Edit","Write","WebFetch","WebSearch","TodoWrite","AskUserQuestion","mcp__tkcode__open_file"],"mcp_servers":[{"name":"tkcode","status":"connected"}],"model":"claude-opus-5-5","permissionMode":"default","slash_commands":["compact","context","cost","init","review"],"apiKeySource":"none","claude_code_version":"2.1.301","output_style":"default","agents":["general-purpose","Explore","Plan"],"skills":[],"plugins":[],"capabilities":["interrupt_receipt_v1","interrupt_cancel_queued_v1"],"fast_mode_state":"off","uuid":"c0a80105-0000-4000-8000-000000000001"}
< {"type":"stream_event","event":{"type":"message_start","message":{"model":"claude-opus-5-5","id":"msg_01GhIjKlMnOpQrStUvWxYzAb","type":"message","role":"assistant","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":2,"cache_creation_input_tokens":90,"cache_read_input_tokens":16500,"output_tokens":1,"service_tier":"standard"}}},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80105-0000-4000-8000-000000000002"}
< {"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_01DotnetTest00000000003","name":"Bash","input":{}}},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80105-0000-4000-8000-000000000003"}
< {"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"command\": \"dotnet test\", \"description\": \"Run all tests\"}"}},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80105-0000-4000-8000-000000000004"}
< {"type":"assistant","message":{"model":"claude-opus-5-5","id":"msg_01GhIjKlMnOpQrStUvWxYzAb","type":"message","role":"assistant","content":[{"type":"tool_use","id":"toolu_01DotnetTest00000000003","name":"Bash","input":{"command":"dotnet test","description":"Run all tests"}}],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":2,"cache_creation_input_tokens":90,"cache_read_input_tokens":16500,"output_tokens":1,"service_tier":"standard"}},"parent_tool_use_id":null,"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","uuid":"c0a80105-0000-4000-8000-000000000005"}
< {"type":"stream_event","event":{"type":"content_block_stop","index":0},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80105-0000-4000-8000-000000000006"}
< {"type":"stream_event","event":{"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"input_tokens":2,"cache_creation_input_tokens":90,"cache_read_input_tokens":16500,"output_tokens":37}},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80105-0000-4000-8000-000000000007"}
< {"type":"stream_event","event":{"type":"message_stop"},"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","parent_tool_use_id":null,"uuid":"c0a80105-0000-4000-8000-000000000008"}
< {"type":"control_request","request_id":"6f9e3d82-1b5c-4a07-a4f3-8b2d0e9f7a16","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"dotnet test","description":"Run all tests"},"permission_suggestions":[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"dotnet test"}],"behavior":"allow","destination":"localSettings"}],"tool_use_id":"toolu_01DotnetTest00000000003","description":"Run all tests"}}
> {"type":"control_request","request_id":"tkcode_int_2","request":{"subtype":"interrupt","cancel_queued":true}}
< {"type":"control_cancel_request","request_id":"6f9e3d82-1b5c-4a07-a4f3-8b2d0e9f7a16"}
< {"type":"control_response","response":{"subtype":"success","request_id":"tkcode_int_2","response":{"still_queued":[],"cancelled":[]}}}
< {"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"[Request interrupted by user for tool use]","is_error":true,"tool_use_id":"toolu_01DotnetTest00000000003"}]},"parent_tool_use_id":null,"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","uuid":"c0a80105-0000-4000-8000-000000000009","timestamp":"2026-10-16T08:04:03.200Z"}
< {"type":"result","subtype":"success","is_error":false,"duration_ms":3300,"duration_api_ms":1900,"num_turns":1,"result":"","stop_reason":"tool_use","session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","total_cost_usd":0.0951,"usage":{"input_tokens":2,"cache_creation_input_tokens":90,"cache_read_input_tokens":16500,"output_tokens":37,"server_tool_use":{"web_search_requests":0,"web_fetch_requests":0},"service_tier":"standard"},"modelUsage":{"claude-opus-5-5":{"inputTokens":13,"outputTokens":304,"cacheReadInputTokens":111100,"cacheCreationInputTokens":2246,"webSearchRequests":0,"costUSD":0.0951,"contextWindow":1000000,"maxOutputTokens":128000}},"permission_denials":[],"terminal_reason":"aborted_tools","queued_turn_count":0,"result_index":4,"user_message_uuid":"b2f7d9e1-4c3a-4a58-8f60-1e9d7c5b3a25","user_message_uuids":["b2f7d9e1-4c3a-4a58-8f60-1e9d7c5b3a25"],"uuid":"c0a80105-0000-4000-8000-00000000000a"}
```

### A.5 (e) 错误结果

#### (e1) API 认证失败（`subtype:"success"` + `is_error:true`）

全新进程（握手见 (a)，此处省略 initialize）。凭据失效。

期望事件：agentStarted → messageStarted/textDelta/messageEnded（由**没有流事件**的合成 assistant 帧合成，文本为错误信息，标记 error=`authentication_failed`）→ agentEnded(**error**，错误文本取 `result`，`api_error_status:401`)。

```jsonl
> {"type":"user","message":{"role":"user","content":[{"type":"text","text":"你好"}]},"parent_tool_use_id":null,"uuid":"c4a1e8f3-7d26-4b9c-8e15-3f0a2b6d9c47"}
< {"type":"system","subtype":"init","cwd":"/Users/dev/proj","session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","tools":["Task","Bash","Glob","Grep","Read","Edit","Write","WebFetch","WebSearch","TodoWrite","AskUserQuestion"],"mcp_servers":[],"model":"claude-opus-5-5","permissionMode":"default","slash_commands":["compact","context","cost","init","review"],"apiKeySource":"ANTHROPIC_API_KEY","claude_code_version":"2.1.301","output_style":"default","agents":["general-purpose","Explore","Plan"],"skills":[],"plugins":[],"capabilities":["interrupt_receipt_v1","interrupt_cancel_queued_v1"],"uuid":"c0a80106-0000-4000-8000-000000000001"}
< {"type":"assistant","message":{"id":"c0a80106-0000-4000-8000-0000000000aa","model":"<synthetic>","role":"assistant","type":"message","content":[{"type":"text","text":"Invalid API key · Fix external API key"}],"stop_reason":"stop_sequence","stop_sequence":"","usage":{"input_tokens":0,"output_tokens":0,"cache_creation_input_tokens":0,"cache_read_input_tokens":0}},"parent_tool_use_id":null,"session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","uuid":"c0a80106-0000-4000-8000-000000000002","error":"authentication_failed"}
< {"type":"result","subtype":"success","is_error":true,"api_error_status":401,"duration_ms":412,"duration_api_ms":388,"num_turns":1,"result":"Invalid API key · Fix external API key","stop_reason":"stop_sequence","session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","total_cost_usd":0,"usage":{"input_tokens":0,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":0,"server_tool_use":{"web_search_requests":0,"web_fetch_requests":0},"service_tier":"standard"},"modelUsage":{},"permission_denials":[],"terminal_reason":"api_error","queued_turn_count":0,"result_index":0,"user_message_uuid":"c4a1e8f3-7d26-4b9c-8e15-3f0a2b6d9c47","user_message_uuids":["c4a1e8f3-7d26-4b9c-8e15-3f0a2b6d9c47"],"uuid":"c0a80106-0000-4000-8000-000000000003"}
```

#### (e2) 恢复不存在的会话（启动阶段的错误 result）

argv 带 `--resume=0d9b7e55-1c2a-4f3e-8b6d-5a4c3b2a1f00`。CLI 在 `initialize` 应答之前输出零值的 `error_during_execution` 并以 1 退出。

期望行为：等待 `initialize` 的逻辑因这个 result 立即失败；不发 agentStarted；向 UI 报"会话不存在：…"（文本取 `errors[0]`，不是 `[ede_diagnostic]`）；进程退出码 1 不再单独报错。

```jsonl
> {"type":"control_request","request_id":"tkcode_init_1","request":{"subtype":"initialize","hooks":null}}
< {"type":"result","subtype":"error_during_execution","duration_ms":0,"duration_api_ms":0,"is_error":true,"num_turns":0,"stop_reason":null,"session_id":"0d9b7e55-1c2a-4f3e-8b6d-5a4c3b2a1f00","total_cost_usd":0,"usage":{"output_tokens_details":{"thinking_tokens":0},"input_tokens":0,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":0,"server_tool_use":{"web_search_requests":0,"web_fetch_requests":0},"service_tier":"standard","cache_creation":{"ephemeral_1h_input_tokens":0,"ephemeral_5m_input_tokens":0},"inference_geo":"","iterations":[],"speed":"standard","fallback_credit":null},"modelUsage":{},"permission_denials":[],"uuid":"c0a80107-0000-4000-8000-000000000001","errors":["No conversation found with session ID: 0d9b7e55-1c2a-4f3e-8b6d-5a4c3b2a1f00"]}
```

(e2) 的 result 形状逐字段对应 CLI 源码中的零值错误 result 构造函数 [CLI:bin `function aH(e,n,t)`]；该函数是否用于 `--resume` 失败这一路径属于 [推断]（源码在此处调用的是另一个致命错误输出函数，可能只写 stderr 后退出）。解析器必须同时覆盖"有这个 result"和"只有 stderr + 退出码 1"两种情况。

---

## 附录 B. 与 zeron 的差异（TKCode 的取舍）

| 点 | zeron | TKCode |
|---|---|---|
| 工具审批 | 全部自动放行，只拦 `AskUserQuestion` | 默认弹审批（允许一次 / 本会话允许 / 拒绝） |
| 工具输出 | 丢弃（`output: None`） | 保留 `tool_result.content` 和 `tool_use_result` |
| `perTaskStopAffordance` | 声明（停止只结束轮次，保留后台 Agent） | 不声明（停止 = 全部停止），后续有逐任务停止 UI 时再声明 |
| 首条消息 | 与 `initialize` 连写，不等应答 | 等 `initialize` 应答 |
| "一条 prompt = 一次运行" | 用 `command_lifecycle` 暂存作为 steer 边界的 result | 事件按轮次划分，每个 result 都发 agentEnded |
| `--dangerously-skip-permissions` | 自动放行时与 `bypassPermissions` 同传 | 只用 `--permission-mode bypassPermissions` |
| `CLAUDE_CODE_SDK_READS_SESSION_STATE` | 不用 | 用（会话忙碌指示） |
