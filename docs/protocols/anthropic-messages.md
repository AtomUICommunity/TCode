# Anthropic Messages API 协议规范（TKCode 实现用）

> 适用：TKCode 直接用 `HttpClient` + SSE 实现 `POST /v1/messages`（不使用官方 SDK）。
> 编写日期：2026-10-16。字段名、事件名、错误类型一律保留英文原文。

## 0. 来源与可信度标注

本文每条规则尽量标注来源。标记含义如下：

| 标记 | 来源 |
|---|---|
| **[Skill:…]** | 随 Claude Code 2.1.300 分发的 `claude-api` skill（Anthropic 官方维护的离线参考），路径 `/private/tmp/claude-501/bundled-skills/2.1.300/46f84673a7c2f4fc76b9c51203e2ad4f/claude-api/`。主要读了 `curl/examples.md`、`shared/tool-use-concepts.md`、`shared/prompt-caching.md`、`shared/models.md`、`shared/error-codes.md`、`shared/agent-design.md`、`shared/model-migration.md`（包括 Opus 4.6/4.7/4.8/5/5.5、Sonnet 5/5.5、Haiku 5.5、Fable 5.1 各节）、`shared/anthropic-cli.md`、`shared/live-sources.md` |
| **[SDK:…]** | `anthropics/anthropic-sdk-python` v1.15.0（commit `bcd55fe`，2026-10-15）生成的类型与运行时代码，路径相对 `src/anthropic/`。这些类型由官方 OpenAPI spec 生成，可以视为线上 schema 的镜像 |
| **[3P:url]** | 第三方厂商的官方文档 |
| **[记忆·待核实]** | 官方文档无法自动抓取，本条取自训练知识。实现前建议人工打开官方页面确认 |

官方在线文档（`platform.claude.com/docs/...`）对自动抓取一律返回 HTTP 400，这次无法直接读取，只能用上面的离线 skill 和 SDK 源码交叉确认。人工核对时用这些 URL（来自 `shared/live-sources.md`）：

- Messages API 参考：`https://platform.claude.com/docs/en/api/messages`
- Streaming：`https://platform.claude.com/docs/en/build-with-claude/streaming`
- Tool use：`https://platform.claude.com/docs/en/agents-and-tools/tool-use/overview`
- Adaptive thinking：`https://platform.claude.com/docs/en/build-with-claude/adaptive-thinking`
- Extended thinking：`https://platform.claude.com/docs/en/build-with-claude/extended-thinking`
- Effort：`https://platform.claude.com/docs/en/build-with-claude/effort`
- Prompt caching：`https://platform.claude.com/docs/en/build-with-claude/prompt-caching`
- Errors：`https://platform.claude.com/docs/en/api/errors`
- Rate limits：`https://platform.claude.com/docs/en/api/rate-limits`
- Stop reasons：`https://platform.claude.com/docs/en/build-with-claude/handling-stop-reasons`
- Refusals：`https://platform.claude.com/docs/en/build-with-claude/refusals-and-fallback`

---

## 1. Endpoint、认证与请求头

### 1.1 Endpoint

```
POST {base_url}/v1/messages            # 默认 base_url = https://api.anthropic.com
POST {base_url}/v1/messages/count_tokens   # 计 token，入参与 messages 基本相同（无 stream / max_tokens）
GET  {base_url}/v1/models/{id}          # 查询模型 capabilities（context、max_tokens、thinking/effort 支持情况）
```

`base_url` 必须可配置。第三方兼容端点见第 12 节。[SDK:_client.py]

### 1.2 请求头

| Header | 值 | 说明 |
|---|---|---|
| `content-type` | `application/json` | 必填 |
| `anthropic-version` | `2023-06-01` | 必填。SDK 至今仍固定为这个值 [SDK:_constants.py `ANTHROPIC_API_VERSION`] |
| `x-api-key` | API key | API key 认证 |
| `authorization` | `Bearer <oauth access token>` | OAuth 认证。**必须同时**发送 `anthropic-beta: oauth-2025-04-20`；`/v1/messages` 不带这个 beta 会失败 [Skill:shared/anthropic-cli.md；SDK:lib/credentials/_constants.py `OAUTH_API_BETA_HEADER`] |
| `anthropic-beta` | 逗号分隔的 beta 名 | 使用 beta 特性时才发送。名字不存在或组织未开通时返回 **400**，消息为 ``Unexpected value(s) `<value>` for the `anthropic-beta` header.`` [Skill:shared/error-codes.md] |
| `accept` | `text/event-stream`（流式） | 建议在流式请求上发送 |

认证注意事项：

- `x-api-key` 和 `Authorization` **只能二选一**，两个都发会被 API 拒绝（401）。把 OAuth token 放进 `x-api-key` 也会 401。[Skill:shared/error-codes.md]
- 响应头 `request-id` 是请求 ID，要写进日志；错误体里也有 `request_id`。[SDK:_exceptions.py]

### 1.3 大小与数量限制

| 项 | 限制 | 来源 |
|---|---|---|
| `messages` 条数 | ≤ 100,000 | [SDK:types/message_create_params.py docstring] |
| 请求体大小 | 32 MB，超出返回 413 `request_too_large` | [Skill:shared/managed-agents-core.md 提到 "a request body over 32 MB -> 413"]；Messages API 同为 32 MB 属于 [记忆·待核实] |
| 上下文窗口 | 现役主力模型 1M；Haiku 4.5 为 200K | [Skill:shared/models.md] |
| `max_tokens` 上限 | 现役模型 128K；Haiku 4.5 为 64K | [Skill:shared/models.md] |
| 非流式 + 大 `max_tokens` | `max_tokens` 大于约 16K 时应使用流式，否则容易 HTTP 超时 | [Skill:model-migration.md "Stream for max_tokens > ~16K"] |
| 图片 | 单张最长边 2576px（Opus 4.7+、Sonnet 5+ 的高分辨率支持）；单请求图片数、单图字节上限（约 5 MB）、PDF 页数上限 | 最长边见 [Skill:model-migration.md]；其余 [记忆·待核实] |

---

## 2. 请求体字段

字段集合以 [SDK:types/message_create_params.py] 为准，beta 字段以 [SDK:types/beta/*] 为准。

### 2.1 顶层字段一览

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `model` | string | ✔ | 例如 `claude-opus-5-5`。只用文档列出的 ID，不要自己拼。不存在的模型和组织无权使用的模型都返回 404 `not_found_error` [Skill:models.md, error-codes.md] |
| `max_tokens` | int | ✔ | 输出上限，**thinking 也计入**。允许为 `0`，表示只预热缓存（见 8.6） |
| `messages` | MessageParam[] | ✔ | 见 2.3 |
| `system` | string \| TextBlockParam[] | | 顶层 system prompt。用 block 数组时可以加 `cache_control` |
| `stream` | bool | | TKCode 应**始终为 `true`** |
| `thinking` | ThinkingConfig | | 见第 5 节，规则按模型不同 |
| `output_config` | `{effort?, format?, task_budget?(beta)}` | | 见 2.6 |
| `tools` | ToolUnion[] | | 见 2.5 |
| `tool_choice` | ToolChoice | | 见 2.5，部分模型禁用 `any` 和 `tool` |
| `stop_sequences` | string[] | | 命中后 `stop_reason="stop_sequence"`，`stop_sequence` 返回命中的串 |
| `metadata` | `{user_id?: string}` | | 只能放不透明 ID（uuid 或 hash），**不要放 PII** |
| `cache_control` | `{type:"ephemeral", ttl?}` | | **顶层自动缓存**：自动在最后一个可缓存 block 上放断点（见第 8 节） |
| `service_tier` | `"auto"` \| `"standard_only"` | | priority 容量选择 |
| `speed` | `"standard"` \| `"fast"` | | fast mode。部分模型需要 `fast-mode-2026-02-01` beta [SDK] |
| `inference_geo` | string | | 推理地域 |
| `container` | object | | code execution 容器复用，TKCode 一般不用 |
| `diagnostics` | `{previous_message_id}` | | 缓存未命中诊断，需要配合 `cache-diagnosis-2026-04-07` beta [Skill:prompt-caching.md] |
| `temperature` / `top_p` / `top_k` | number | | **SDK v1.x 已从方法签名中删除**，API 只对旧模型还接受。新模型发送会 400，见 2.7 [SDK:MIGRATION.md] |

beta-only 字段（必须配合 `anthropic-beta`）：`context_management`、`mcp_servers`、`fallbacks`、`output_config.task_budget`、`thinking.block_binding`、`thinking.display:"updates"`，以及 `role:"system"` 消息上的 `clear_at` 和 `output_config`。

### 2.2 `system`

```json
"system": "You are TKCode..."
```
或
```json
"system": [
  {"type": "text", "text": "<稳定的核心 system prompt>", "cache_control": {"type": "ephemeral"}}
]
```

- **会话中不要修改顶层 `system`。** 修改会让整个 messages 缓存失效；在 Fable 5.1、Opus 5.5、Sonnet 5.5、Haiku 5.5 上还会让之后所有 thinking block 失效（新账户直接 400）。中途要追加指令，改用 mid-conversation system message（2.4）。[Skill:prompt-caching.md, model-migration.md]

### 2.3 `messages`、角色和交替规则

`MessageParam = { role: "user" | "assistant" | "system", content: string | ContentBlockParam[] }` [SDK:types/message_param.py]

（SDK 的 `messages` docstring 还写着“no system role”，这是过时文字；类型定义里 role 已经包含 `"system"`。）

规则如下：

1. `content` 为 string 时，等价于 `[{"type":"text","text":...}]`。
2. **第一条必须是 `user`。** 首条为 `assistant` 返回 400；首条为 `system` 也不行，初始 system prompt 用顶层 `system`。[Skill:error-codes.md, prompt-caching.md]
3. **user 和 assistant 交替。** 官方 SDK docstring 说连续的同角色消息会被合并成一个 turn [SDK:message_create_params.py]，但 error-codes 里也有 `roles must alternate` 的 400 示例。**TKCode 发送前必须自己合并相邻的同角色消息**，不要依赖服务端行为。
4. **Prefill（最后一条是 assistant）：** Opus 4.6+、Sonnet 4.6+、所有 Fable/Mythos、Haiku 5.5 上返回 400。TKCode **永远以 user（或 system）消息结尾**。唯一例外是 `pause_turn` 续写：原样回传以 server tool 结尾的 assistant 消息（见 7.3）。[Skill:model-migration.md]
5. `role:"system"` 消息（mid-conversation system messages）的规则见 2.4。

### 2.4 Mid-conversation system messages（`role:"system"`）

**支持的模型（无需 beta，GA）：** Opus 4.8、Opus 5、Opus 5.5、Fable 5、Fable 5.1、Mythos 5、Mythos 5.1、Sonnet 5.5、Haiku 5.5。**Sonnet 5 及更早的模型不支持**，返回 400 `role 'system' is not supported on this model`；这时降级为在 user turn 里加一个 `<system-reminder>` 文本块。[Skill:prompt-caching.md §Mid-conversation system messages, model-migration.md]

位置规则（违反返回 400）：

- 必须紧跟在 `role:"user"` 消息之后，或者紧跟在以 server-tool use 结尾的 `assistant` 消息之后。
- 必须是 `messages` 的**最后一项**，或者**后面紧跟 `assistant`**。
- 不能是 `messages[0]`。
- 只能放文本：`content` 为 string 或 text blocks。例外是 `tool_addition`/`tool_removal` 块（beta），以及只带 `output_config` 的空 content 消息（beta）。
- 措辞写成“陈述上下文”，不要写成“覆盖用户”的命令。

```json
"messages": [
  {"role": "user", "content": "..."},
  {"role": "assistant", "content": [ ... ]},
  {"role": "user", "content": [{"type":"tool_result","tool_use_id":"toolu_01","content":"ok"}]},
  {"role": "system", "content": "The user enabled auto-approve mode."}
]
```

可选的 beta 扩展：

| 能力 | beta header | 形状 | 约束 |
|---|---|---|---|
| 单轮提醒 | `mid-conversation-system-clear-at-2026-08-21` | `{"role":"system","clear_at":"next_user_message","content":"..."}` | `clear_at` 取 `"never"`（默认）或 `"next_user_message"`。只能是文本；**不能加 `cache_control`（400）**；后面紧跟 user 消息时 400。已经清除的副本**必须原样保留在历史里**，不计 input tokens [SDK:beta_message_param.py；Skill:model-migration.md] |
| 按消息调整 effort | `mid-conversation-output-config-2026-07-01` | `{"role":"system","content":[],"output_config":{"effort":"low"}}` | 从下一个 user turn 起生效。**不受上面的位置规则限制**，可以放在任意位置。支持 Fable 5.1、Mythos 5.1、Opus 5、Opus 5.5、Sonnet 5.5，以及 thinking 开启时的 Haiku 5.5。thinking 关闭时（`disabled` 或 `between_tools`）返回 400。content 和 output_config 都为空的消息会被拒绝 [SDK:beta_system_message_output_config_param.py] |
| 增删工具 | `mid-conversation-tool-changes-2026-07-01` | `content: [{"type":"tool_addition"\|"tool_removal","tool":{"type":"tool_reference","name":"..."}}]` | 要新增的工具必须事先在 `tools[]` 里声明并标上 `defer_loading:true`；`tool_removal` 必须紧挨 assistant 之前，或者放在最后 [Skill:tool-use-concepts.md] |

### 2.5 Content block 参数（入参）

以下形状均取自 [SDK:types/*_param.py]。`cache_control` 都是 `{"type":"ephemeral","ttl"?: "5m"|"1h"}`。

**text**
```json
{"type": "text", "text": "...", "cache_control": {...}?, "citations": [...]?}
```

**image**（source 三选一）
```json
{"type": "image", "source": {"type": "base64", "media_type": "image/png", "data": "<b64>"}}
{"type": "image", "source": {"type": "url", "url": "https://..."}}
{"type": "image", "source": {"type": "file", "file_id": "file_..."}}
```
`media_type` 只能取 `image/jpeg`、`image/png`、`image/gif`、`image/webp`。

在 preserved thinking 模型上，URL 图片在后续请求里返回的**字节**必须不变（服务端绑定的是字节）。跨 turn 引用时优先用 base64 或 `file_id`。[Skill:model-migration.md]

**document**
```json
{"type": "document",
 "source": {"type": "base64", "media_type": "application/pdf", "data": "..."}   // 或
           {"type": "text", "media_type": "text/plain", "data": "..."}          // 或
           {"type": "content", "content": "..." | [blocks]}                      // 或
           {"type": "url", "url": "..."} | {"type": "file", "file_id": "..."},
 "title": "...?", "context": "...?", "citations": {"enabled": true}?, "cache_control": {...}?}
```

**tool_use**（assistant 历史中回放用；必须与模型返回的内容完全一致）
```json
{"type": "tool_use", "id": "toolu_...", "name": "read_file", "input": {...},
 "caller": {...}?, "toolset_name": "...?", "cache_control": {...}?}
```

**tool_result**（user 消息中）
```json
{"type": "tool_result", "tool_use_id": "toolu_...",
 "content": "string" | [ text | image | document | search_result | tool_reference ... ],
 "is_error": true?, "toolset_name": "...?", "cache_control": {...}?}
```

**thinking / redacted_thinking**（assistant 历史中回放用）
```json
{"type": "thinking", "thinking": "<原样，可能是空串>", "signature": "<原样>"}
{"type": "redacted_thinking", "data": "<原样 opaque>"}
```
SDK 原文：“Thinking blocks must be passed back unmodified and in their original order; a modified block results in a 400 `invalid_request_error`.” [SDK:types/thinking_block_param.py]

**其他：** `search_result`、`server_tool_use`、`web_search_tool_result`、`web_fetch_tool_result`、`code_execution_tool_result`、`container_upload` 等。对 TKCode 的要求是：**无法识别的 assistant block 也必须原样回放**（按 `JsonElement` 透传）。

### 2.6 `tools`

自定义工具（`ToolParam`）[SDK:types/tool_param.py]：

| 字段 | 说明 |
|---|---|
| `name` | 必填。字符集 `^[a-zA-Z0-9_-]{1,64}$` [记忆·待核实] |
| `description` | 强烈建议填写。描述里写清“什么时候调用” [Skill:tool-use-concepts.md] |
| `input_schema` | 必填，JSON Schema，顶层必须是 `{"type":"object", ...}` |
| `strict` | bool。为 true 时保证参数符合 schema；schema 必须带 `additionalProperties:false`。支持模型：Fable/Mythos、Opus 4.8+、Sonnet 5+、Haiku 4.5/5.5 等 [Skill:tool-use-concepts.md §Structured Outputs] |
| `eager_input_streaming` | bool。流式时关闭服务端的参数缓冲和校验，见 6.5。`null` 表示默认（缓冲） |
| `cache_control` | 工具定义上也可以放断点 |
| `defer_loading` | 先声明但不加载，配合 tool search 或 `tool_addition` 使用 |
| `input_examples` | 示例输入 |
| `allowed_callers` | `direct` / `code_execution_*`，用于 programmatic tool calling |
| `type` | 可选 `"custom"` |

Anthropic 定义的 schema-less 工具（只写 `type`+`name`，**不能带 `input_schema`**）：`{"type":"bash_20250124","name":"bash"}`、`{"type":"text_editor_20250728","name":"str_replace_based_edit_tool"}`、`computer_toolset_20260801`、`browser_toolset_20260801` 等。server tools（`web_search_20260209`、`web_fetch_20260209`、`code_execution_20260521` 等）由服务端执行。[Skill:tool-use-concepts.md]

**工具列表要按 name 排序后确定性序列化，会话中不能变。** 工具在 prompt 最前面，任何变化都会让全部缓存失效；在 preserved thinking 模型上还会让所有 thinking block 失效。[Skill:prompt-caching.md]

### 2.7 `tool_choice`

| 值 | 语义 |
|---|---|
| `{"type":"auto"}`（默认） | 模型自行决定 |
| `{"type":"any"}` | 必须至少调用一个工具 |
| `{"type":"tool","name":"x"}` | 必须调用指定工具 |
| `{"type":"none"}` | 禁止调用工具 |

- `auto`、`any`、`tool` 都可以加 `disable_parallel_tool_use: true`：配合 `auto` 时表示“至多一个”调用，配合 `any`/`tool` 时表示“恰好一个”。[SDK:tool_choice_*_param.py]
- **以下模型对 `any` 和 `tool` 返回 400：** Fable 5.1、Mythos 5.1、Opus 5.5、Sonnet 5.5。错误为 `tool_choice: type "tool" and "any" are not supported for this model.`，`count_tokens` 和 Batches 同样适用。Fable 5 和 Opus 5 仍然接受。Haiku 5.5 接受，但强制调用时会跳过 thinking。[Skill:tool-use-concepts.md, model-migration.md]
- 老模型使用 manual extended thinking（`enabled`）时，`any`/`tool` 与 thinking 不兼容 [记忆·待核实]。Bedrock 上的 Sonnet 5 使用强制 tool_choice 时必须 `thinking: disabled`。
- **TKCode 策略：只发送 `auto` 或 `none`。** 需要强制调用时在 prompt 里明确要求，并检查实际是否调用。

### 2.8 `output_config`

```json
"output_config": {
  "effort": "low" | "medium" | "high" | "xhigh" | "max",
  "format": {"type": "json_schema", "schema": {...}},        // structured outputs
  "task_budget": {"type": "tokens", "total": 128000}          // beta task-budgets-2026-03-13，最小 20000
}
```
[SDK:output_config_param.py, beta_output_config_param.py；Skill:model-migration.md]

effort 默认值与支持情况（**以 `GET /v1/models/{id}` 的 `capabilities.effort` 为准**）：

| 模型 | 默认 effort | 说明 |
|---|---|---|
| Opus 5.5 | **medium** | 五档都支持；`max` 不封顶 |
| Haiku 5.5 | **medium** | 第一个支持 effort 的 Haiku |
| Opus 5、Opus 4.8、Opus 4.7、Opus 4.6、Sonnet 5.5（档位重新校准过）、Sonnet 5、Sonnet 4.6、Fable/Mythos | high | `xhigh` 只有 Opus 4.7+、Sonnet 5+、Fable 才有 |
| Opus 4.5 | high | 无 `xhigh` 和 `max` [记忆·待核实] |
| Sonnet 4.5、Haiku 4.5 | — | **发送 effort 返回 400** |

- 会话中修改顶层 `effort` 会让 messages 缓存失效；按消息调整 effort 的 beta 写法不会（2.4）。[Skill:prompt-caching.md]
- 显式设置为模型默认值，等价于不设置。
- `output_config.format`（structured outputs）与 citations 不兼容（400），也与 prefill 不兼容。

### 2.9 采样参数（`temperature` / `top_p` / `top_k`）

| 模型 | 规则 |
|---|---|
| Opus 4.7、4.8、5、5.5，Fable 5/5.1，Mythos | **发送任何一个都返回 400** |
| Sonnet 5、Sonnet 5.5 | 非默认值返回 400 |
| Haiku 5.5 | 只接受 `temperature=1` 和 `top_p=0.99`；任何 `top_k` 返回 400；`temperature` 和 `top_p` 同时发送返回 400 |
| 其他 Claude 4.x（Sonnet 4.5/4.6、Haiku 4.5、Opus 4.5/4.6） | `temperature` 范围 0–1；`temperature` 与 `top_p` 只能二选一，同时发送返回 400 |

[Skill:error-codes.md, model-migration.md；SDK:MIGRATION.md]

**TKCode 策略：默认不发送；只有用户显式配置，并且模型在允许列表里时才发送。**

---

## 3. 响应体（非流式 `Message`）

```json
{
  "id": "msg_...", "type": "message", "role": "assistant",
  "model": "claude-opus-5-5",
  "content": [ {thinking...}, {text...}, {tool_use...} ],
  "stop_reason": "end_turn",
  "stop_sequence": null,
  "stop_details": null,
  "container": null,
  "usage": {
    "input_tokens": 12, "output_tokens": 345,
    "cache_creation_input_tokens": 0, "cache_read_input_tokens": 51234,
    "cache_creation": {"ephemeral_5m_input_tokens": 0, "ephemeral_1h_input_tokens": 0},
    "output_tokens_details": {"thinking_tokens": 200},
    "server_tool_use": {"web_search_requests": 0, "web_fetch_requests": 0},
    "service_tier": "standard", "speed": "standard", "inference_geo": "..."
  },
  "input_transformations": [ ... ]      // 只有发送 thinking-binding-controls beta 时才有
}
```
[SDK:types/message.py, usage.py]

响应 content block 类型：`text`（可能带 `citations`）、`thinking{thinking,signature}`、`redacted_thinking{data}`、`tool_use{id,name,input,caller?,toolset_name?}`、`server_tool_use`、各类 `*_tool_result`、`container_upload`，以及 fallback 时出现的 `fallback` block。**按 `type` 分派，不要按位置读取。** 响应可能以一个或多个 thinking block 开头，`content[0]` 不一定是 text。[Skill:model-migration.md]

---

## 4. 工具调用循环规则

1. 响应 `stop_reason == "tool_use"` 时，把**完整的** `response.content` 原样追加为 assistant 消息，包括 thinking、redacted_thinking、text、tool_use 以及未知 block，顺序不能变。
2. **下一条消息必须是 user**，并且要为上一条 assistant 中的**每一个** `tool_use.id` 提供对应的 `tool_result`，缺任何一个都返回 400。
3. 并行调用的**所有**结果放在**同一条** user 消息里，不能拆成多条。[Skill:tool-use-concepts.md §Handling Tool Results]
4. 这条 user 消息里，**`tool_result` blocks 必须排在 content 数组最前面**，文本只能放在所有 tool_result 之后；文本放在前面会 400 [记忆·待核实，官方 tool-use 文档原则]。skill 明确建议把单轮提醒“作为 text block 放在 `tool_result` blocks 之后”[Skill:prompt-caching.md, model-migration.md]，说明**允许在 tool_result 之后跟文本**。
5. 执行失败时：`{"type":"tool_result","tool_use_id":"...","content":"<错误信息>","is_error":true}`。`content` 可以是 string，也可以是 block 数组，**数组里可以放图片**（截图类工具）。`is_error` 结果里放图片在 schema 层面合法 [SDK:tool_result_block_param.py]；是否有语义限制属于 [记忆·待核实]。
6. 用户拒绝执行时，也返回 `tool_result`（例如 `"User declined"`，可以加 `is_error:true`），**不能省略**。
7. `tool_use.input` 必须用 JSON 解析器处理。4.5+ 模型会保留字符串末尾的换行；4.6+ 可能出现 Unicode 或 `/` 转义。[Skill:model-migration.md]
8. **不能执行的情况：** `stop_reason == "max_tokens"` 且存在 tool_use（input 可能被截断）、`stop_reason == "refusal"`，或者解析或 schema 校验失败。处理方式：重试，或者回一个 `is_error` 的 tool_result，内容为 `{"INVALID_JSON": "<原始片段>"}`，这段要用 JSON 库序列化。[Skill:tool-use-concepts.md]
9. 使用 `auto` 时不保证会发生调用。业务上要求必须调用时，检查结果，没调用就重试。
10. computer toolset 的 `tool_result` 必须回传 `"toolset_name":"computer"`。[Skill:tool-use-concepts.md]

示例：
```json
[
  {"role":"user","content":"读取 README 并总结"},
  {"role":"assistant","content":[
    {"type":"thinking","thinking":"","signature":"EqQBCkgI..."},
    {"type":"tool_use","id":"toolu_01","name":"read_file","input":{"path":"README.md"}},
    {"type":"tool_use","id":"toolu_02","name":"list_dir","input":{"path":"."}}
  ]},
  {"role":"user","content":[
    {"type":"tool_result","tool_use_id":"toolu_01","content":"# TKCode ..."},
    {"type":"tool_result","tool_use_id":"toolu_02","content":"permission denied","is_error":true},
    {"type":"text","text":"<system-reminder>…</system-reminder>"}
  ]}
]
```

---

## 5. Thinking：配置、按模型的限制、回放规则

### 5.1 ThinkingConfig 形状 [SDK:types/thinking_config_*_param.py, beta/beta_thinking_config_adaptive_param.py]

```json
{"type": "adaptive", "display": "summarized" | "omitted" | "updates"(beta), "block_binding": {...}(beta)}
{"type": "enabled", "budget_tokens": 1024+, "display": "summarized" | "omitted"}   // manual extended thinking
{"type": "disabled"}
{"type": "between_tools"}                                                          // 仅 Sonnet 5.5
```

- `budget_tokens` 必须 ≥ 1024 且 **小于** `max_tokens`。
- thinking token 计入 `max_tokens` 和 `output_tokens`。`usage.output_tokens_details.thinking_tokens` 给出其中原始 reasoning 的部分。

### 5.2 按模型的行为（**发错直接 400**）

| 模型 | 省略 `thinking` 时 | `adaptive` | `enabled`+budget | `disabled` | `display` 默认 |
|---|---|---|---|---|---|
| **Opus 5.5** | adaptive（**始终开启**） | ✔ | **400** | **400**（任何 effort 下） | omitted |
| **Fable 5 / 5.1、Mythos 5 / 5.1** | adaptive（始终开启） | ✔ | **400** | **400** | omitted |
| **Opus 5** | adaptive | ✔ | **400** | 只在 effort ≤ high 时可用，`xhigh`/`max` 下 400 | omitted |
| **Sonnet 5.5** | adaptive | ✔ | **400** | **400**（改用 `between_tools`，只能在 effort ≤ high 下使用，且不能带其他 thinking 字段，也不能按消息调整 effort） | omitted |
| **Haiku 5.5** | adaptive | ✔ | **400** | 只在 effort ≤ high 时可用 | omitted |
| **Sonnet 5** | adaptive | ✔ | **400** | ✔ | omitted |
| **Opus 4.8 / 4.7** | **不思考** | ✔（必须显式开启） | **400** | ✔ | omitted |
| **Opus 4.6 / Sonnet 4.6** | 不思考 | ✔（推荐） | 已弃用但仍可用 | ✔ | summarized |
| **Opus 4.5、Sonnet 4.5、Haiku 4.5 及更早** | 不思考 | ✗（Haiku 4.5 只支持 manual） | ✔ | ✔ | summarized |

- `between_tools` 发给 Sonnet 5.5 以外的模型都返回 400：`"thinking.type.between_tools" is not supported for this model.`
- Opus 5.5 的错误文本：`"thinking.type.disabled" is not supported for this model. Use "thinking.type.adaptive" and "output_config.effort" to control thinking behavior.`

[Skill:curl/examples.md §Extended Thinking, error-codes.md, models.md, model-migration.md]

**TKCode 策略：** 按模型族建立能力表，或者运行时查询 `/v1/models/{id}` 的 `capabilities.thinking.types.*.supported` 和 `capabilities.effort.*`。新模型（Opus 5.5、Fable、Sonnet 5.5、Haiku 5.5）**省略 `thinking`**，或发送 `{"type":"adaptive","display":"summarized"}`；只用 effort 控制思考深度。

### 5.3 display 模式

- `omitted`：thinking block **照常返回**，但 `thinking` 字段是**空串**，只带 `signature`。流式时表现为一段长时间的沉默。
- `summarized`：返回可读的摘要（从来不是原始 CoT；Opus 5+ 和 Fable 从不返回原始思维）。
- `updates`（beta `thinking-display-updates-2026-08-18`，Fable 5/5.1、Mythos、Opus 5.5、Sonnet 5.5）：只返回工具调用之间的“进度更新”文本，reasoning 仍然隐藏。规则：**任何非空的 thinking block 都是进度更新**，渲染成状态行；空 block 不渲染。一次响应里可能有 0 个或多个，每次 tool_use 前至多一个。截断时最后一个 block 的文本可能恰好是 `This part of the response was interrupted before it finished.`
- **注意：** Opus 5.5、Sonnet 5.5、Fable 5.1 会把“工具调用之间的较长说明”放进 thinking block 而不是 text block。只渲染 text 的 UI 会长时间无输出，所以 TKCode 要渲染非空 thinking。
- `display` 只影响可见性，计费完全相同。**序列化时不能跳过空 thinking block。**

[Skill:model-migration.md §Opus 4.7 silent changes, Fable 5.1 §New API features 3, Opus 5.5 §Text between tool calls]

### 5.4 回放（round-trip）规则

1. **thinking 和 redacted_thinking block 必须逐字节原样回放**：`thinking` 文本（包括空串）、`signature`、`data` 都不能改；位置和顺序也不能变（在 assistant content 中的相对次序，以及跨 turn 的次序）。修改后返回 400 `Invalid signature in thinking block`。篡改签名**总是** 400，不受任何设置影响。[SDK:thinking_block_param.py；Skill:error-codes.md]
2. **工具循环中必须回放。** 带 tool_use 的 assistant turn 中的 thinking 必须连同 tool_result 一起回放。最简单、正确的做法是**始终回放全部历史 thinking**。较新的模型（Opus 4.5+、Sonnet 4.6+、Haiku 5.5、Fable）默认保留之前 turn 的 thinking，回放可以保持缓存命中。较早的模型会在普通 user 消息之后自动剥离之前 turn 的 thinking。[Skill:prompt-caching.md §Thinking blocks and the messages cache]
3. **不要自己剥离 thinking block。** 剥离可能触发顺序或签名相关的 400；跨模型时由服务端丢弃无法读取的 block（不计费）。[Skill:model-migration.md]
4. 以下由服务端处理，client 不需要做任何事。**跨模型绑定（model binding）：**
   - Fable 5.1 的 block 只有 Fable 5.1 和 Mythos 5.1 能读。
   - Opus 5.5 的 block 只有 Fable 5.1 和 Mythos 5.1 能读（仅 Claude API）。
   - Sonnet 5.5 的 block 没有其他模型能读。
   - Opus 5.5 能读 Opus 5、更早的 Opus、Sonnet、Haiku 以及 Haiku 5.5 的 block，读不了 Fable 和 Mythos 的。
   - 无法读取的 block 会被**静默丢弃**，请求照常成功，`input_tokens` 相应下降。
5. **preserved thinking：会话绑定（history-editing check）**。适用于 Fable 5.1、Opus 5.5、Sonnet 5.5、Haiku 5.5；Mythos 5.1 不做这项检查。
   - 签名会记录产生该 block 时的完整前缀：顶层 `system`、`tools` 集合、该 block 之前的全部 messages，并且链接到上一个 thinking block。
   - **2026-08-31 及之后创建的账户默认强制执行**：前缀被修改后再回放 block，返回 400：
     ```
     messages.5.content.0: Invalid `signature` in `thinking` block. The block is bound to a different conversation. Remove the block, or set `thinking.block_binding.prefix_mismatch_behavior` to "drop_block". ...
     ```
     同一个 body 重试永远失败，`count_tokens` 也返回同样的 400。
   - **会让之后所有 thinking 失效的操作：** 编辑、重排或删除较早的 turn（包括删除旧的 tool_result）；在较早的 turn 里注入下一次又删除的文本；重建 `system` 或 `tools`；从中间删除 thinking block；URL 图片返回了不同的字节。
   - **不会失效的操作：** 只追加历史（包括追加 `role:"system"` 消息、已清除的 `clear_at` 消息）；**从最前面**按顺序删除连续的 thinking block；只调整 `tools` 的顺序但内容不变（按 name 排序后作为集合绑定，待上线确认）；修改 `system`/`tools`/`messages` 以外的参数（`max_tokens`、`effort`、`tool_choice`、`metadata`）；增删 `cache_control`；server-side compaction 和 context editing。
   - **恢复方式：** ① 剥离历史中**全部** thinking 和 redacted_thinking block（保留 text 和 tool_use），然后重试一次；② 发送 beta `thinking-binding-controls-2026-08-01`，并设置 `thinking.block_binding.prefix_mismatch_behavior: "drop_block"`，服务端会丢弃第一个不匹配的 block **及之后所有的** thinking block，并在响应顶层 `input_transformations` 中报告 `{type:"thinking_dropped", path:"messages.i.content.j", reason:"prefix_binding_mismatch"|"model_binding_mismatch"|"organization_binding_mismatch"|"end_user_binding_mismatch"}`。不带 header 就发送 `block_binding`，返回 400 `block_binding: Extra inputs are not permitted`。`block_binding` 只能配合 `adaptive` 使用。
   - **compaction：** client 端只用“整段摘要替换”的 simple compaction，摘要之后不回放任何旧 thinking。“保留尾部”的 compaction 必须剥离保留 turn 中的 thinking 或使用 `drop_block`。**不要在 tool round 中间做 compaction。**

   [Skill:model-migration.md §Migrating to Claude Fable 5.1 from Claude Fable 5 Breaking change 2/3, §Opus 5.5 Breaking change 3, §Sonnet 5.5 Breaking change 3；SDK:beta_thinking_*]
6. **账户绑定：** Haiku 5.5 的 block，以及 Bedrock/Vertex 上 Sonnet 5.5 的 block，只在产生它的账户（或关联账户）内有效，其他账户会丢弃。
7. **跨厂商：** 来自 DeepSeek、Kimi 等第三方的 thinking block，签名对 Anthropic 无效。按“不可解密签名总是 400”这一规则推断，**切换到 Anthropic 时必须剥离其他厂商产生的 thinking block**；反过来切换时同理 [推断·待测试]。TKCode 应在每个 thinking block 上记录 `provider` 和 `model` 来源。

---

## 6. 流式（SSE）

### 6.1 帧格式

- 请求体中 `"stream": true`。HTTP 200 后响应 `content-type: text/event-stream`。
- 标准 SSE：每个事件由若干 `event: <name>` 和 `data: <json>` 行组成，以**空行**结束。行尾可能是 `\n`、`\r\n` 或 `\r`。以 `:` 开头的行是注释，要忽略。字段名后的第一个空格要去掉。可能出现 `id:` 或 `retry:`，忽略即可。[SDK:_streaming.py SSEDecoder]
- `data` 中 JSON 的 `type` 与 `event` 名相同。**以 JSON 中的 `type` 为准**；SDK 在 `type` 缺失时用 event 名补上。
- **未知事件类型必须忽略，不能报错**（版本策略允许新增事件；SDK 1.14 起把未知事件透传）。[SDK:CHANGELOG 1.14.0]

### 6.2 事件序列

```
message_start
  ( content_block_start(index=i)
    content_block_delta(index=i)*
    content_block_stop(index=i) )*      // i 从 0 递增
  ping*                                 // 可以出现在任意位置
message_delta                           // 带 stop_reason 和累计 usage
message_stop
```
如果出错，会在中途出现 `error` 事件，随后流结束。

### 6.3 各事件 payload [SDK:types/raw_*.py]

| 事件 | payload | 处理 |
|---|---|---|
| `message_start` | `{"type":"message_start","message":{id,type:"message",role:"assistant",model,content:[],stop_reason:null,stop_sequence:null,usage:{input_tokens,output_tokens,cache_creation_input_tokens,cache_read_input_tokens,...}, input_transformations?}}` | 初始化快照。`model` 是实际服务的模型（fallback 时可能不同） |
| `content_block_start` | `{"type":"content_block_start","index":i,"content_block":{...}}` | 在 `index` 处新建 block。初始形状：`text` 为 `{"type":"text","text":""}`；`thinking` 为 `{"type":"thinking","thinking":"","signature":""}`；`redacted_thinking` 为 `{"type":"redacted_thinking","data":"..."}`（**一次给完整，没有 delta**）；`tool_use` 和 `server_tool_use` 为 `{"type":"tool_use","id","name","input":{}}`；`*_tool_result` 一般一次给完整 |
| `content_block_delta` | `{"type":"content_block_delta","index":i,"delta":{...}}` | 见下表 |
| `content_block_stop` | `{"type":"content_block_stop","index":i}` | 收尾：tool_use 在这里把累积的 JSON 解析为 `input` |
| `message_delta` | `{"type":"message_delta","delta":{stop_reason,stop_sequence,stop_details,container},"usage":{output_tokens,input_tokens?,cache_creation_input_tokens?,cache_read_input_tokens?,server_tool_use?,output_tokens_details?}}` | 写入 stop 信息并**覆盖**累计 usage |
| `message_stop` | `{"type":"message_stop"}` | 消息完整 |
| `ping` | `{"type":"ping"}` | 忽略，可以用来刷新空闲超时计时器 |
| `error` | `{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}` | **HTTP 已经是 200，但流失败了。** 按错误类型处理（见第 9 节），已经收到的部分作废 |

delta 类型 [SDK:types/*_delta.py, lib/streaming/_messages.py accumulate_event]：

| delta.type | 字段 | 适用 block | 累积方式 |
|---|---|---|---|
| `text_delta` | `text` | text | 追加 |
| `input_json_delta` | `partial_json` | tool_use / server_tool_use | **拼接成字符串**，在 `content_block_stop` 时整体 `JSON.parse`。全程没有 delta 或拼接结果为空串时 input = `{}`。中间片段不是合法 JSON |
| `thinking_delta` | `thinking` | thinking | 追加 |
| `signature_delta` | `signature` | thinking | **赋值，不是追加**。在该 block 的 `content_block_stop` 之前送达 |
| `citations_delta` | `citation` | text | 追加到 `citations[]` |

### 6.4 usage 语义

- `message_start.message.usage`：`input_tokens`、`cache_creation_input_tokens`、`cache_read_input_tokens` 已经是最终值；`output_tokens` 只是一个很小的初始值。
- `message_delta.usage` 中的数字是**累计总数**，**直接覆盖，不能相加**。可选字段缺失时，保留 message_start 的值。[SDK:lib/streaming/_messages.py 注释原文："Usage counts on a message_delta are cumulative totals, so they overwrite rather than add"]
- **`input_tokens` 只是未命中缓存的那部分**。总 prompt 大小 = `input_tokens + cache_creation_input_tokens + cache_read_input_tokens`。[SDK:message.py；Skill:prompt-caching.md]
- `output_tokens` 包含 thinking（即使 display 为 omitted）。

### 6.5 工具输入的流式行为

- 默认情况下，服务端**先缓冲并校验每个参数**，再发出对应的 `input_json_delta`，所以大参数（例如整个文件内容）会出现长时间沉默。
- 在工具定义上设置 `"eager_input_streaming": true` 会关闭缓冲：片段立即到达，但**不再校验**，最终 JSON 可能非法或被截断。处理要求：严格解析 → 按 schema 校验 → 失败时不执行，回 `is_error` tool_result；同时检查 `stop_reason=="max_tokens"`。[Skill:tool-use-concepts.md §Eager input streaming]
- 旧的 header `fine-grained-tool-streaming-2025-05-14` 已经 GA，不需要再发；与 computer/browser toolset 同时发送会 400。
- **只对 Anthropic 官方端点发送 `eager_input_streaming`**。旧版 Bedrock 和某些代理会对未知字段返回 400。

### 6.6 流中断与恢复

- 网络中断或收到 `error` 事件时，最稳妥的做法是**整请求重试**（请求是幂等的生成；已显示的部分标记为作废）。
- 新模型不支持 prefill，不能用“回传部分 assistant 让模型续写”的方式恢复。如果一定要续写，加一条 user 消息说明“上次输出在 `[...]` 处中断，从那里继续”。[Skill:model-migration.md 4.6 prefill 替代表]
- 中断时**未完成的 tool_use 绝不执行**。

### 6.7 完整 SSE 示例（thinking + 工具）

```
event: message_start
data: {"type":"message_start","message":{"id":"msg_01","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":25,"cache_creation_input_tokens":0,"cache_read_input_tokens":48000,"output_tokens":1}}}

event: content_block_start
data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"需要先读取文件。"}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"EqQBCkgI..."}}

event: content_block_stop
data: {"type":"content_block_stop","index":0}

event: content_block_start
data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_01","name":"read_file","input":{}}}

event: content_block_delta
data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"path\": \"REA"}}

event: content_block_delta
data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"DME.md\"}"}}

event: content_block_stop
data: {"type":"content_block_stop","index":1}

event: ping
data: {"type":"ping"}

event: message_delta
data: {"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":89}}

event: message_stop
data: {"type":"message_stop"}
```

---

## 7. `stop_reason` 与处理方式

取值 [SDK:types/stop_reason.py]：`end_turn`、`max_tokens`、`stop_sequence`、`tool_use`、`pause_turn`、`refusal`、`model_context_window_exceeded`。在流中，`stop_reason` 在 `message_start` 里为 null，在 `message_delta` 里给出。

| stop_reason | 含义 | TKCode 处理 |
|---|---|---|
| `end_turn` | 自然结束 | turn 结束，等待用户 |
| `tool_use` | 发起了一个或多个工具调用 | 执行所有 tool_use，然后回传 tool_result（第 4 节） |
| `max_tokens` | 达到 `max_tokens` 或模型上限 | 已有 text 标记为截断。**存在 tool_use 时不执行**。可以提高 `max_tokens` 后重试，或者追加 user 消息要求“继续”。thinking 可能耗尽预算，导致只有 thinking 没有 text。`max_tokens:0` 的预热请求也返回这个值 |
| `stop_sequence` | 命中自定义停止序列 | 读取 `stop_sequence` |
| `pause_turn` | **server tool**（web_search、code_execution 等）的服务端循环达到上限（默认 10 次）后暂停 | 把 assistant content **原样**追加到历史，**不要添加 "continue" 之类的 user 消息**，直接再发请求，服务端检测到末尾的 `server_tool_use` 会自动续跑。设置最大续跑次数（例如 5）。只用 client tools 的 TKCode 一般遇不到，但必须处理 [Skill:tool-use-concepts.md] |
| `refusal` | 安全分类器拒绝，或者模型自己拒绝。**HTTP 仍是 200** | **先判断 `stop_reason`，再读 content。** content 可能为空（输出前就拒绝），也可能是部分输出（流中途拒绝），部分输出要丢弃。**本轮的 tool_use 一律不执行**（可能被截断）。`stop_details = {type:"refusal", category: "cyber"\|"bio"\|"frontier_llm"\|"reasoning_extraction"\|"general_harms"\|null, explanation: string\|null}`，只用于提示，**不要用它分支**。可选 server-side `fallbacks`（beta），见 [Skill:model-migration.md §refusal stop reason] |
| `model_context_window_exceeded` | 达到**上下文窗口**上限（不是 max_tokens） | 当前输出有效但不完整。触发 compaction 或摘要后继续 [Skill:model-migration.md 4.5+] |

补充：

- 进度 block 截断哨兵：在 `display:"updates"` 下，由 `max_tokens`、`model_context_window_exceeded` 或 `stop_sequence` 结束时，最后一个 thinking 的文本可能是 `This part of the response was interrupted before it finished.`。续写时原样回传 assistant turn，再追加 user 消息（如果有 tool_use，要附上对应的 tool_result）。
- server-side fallback 时 content 中可能出现 `{"type":"fallback","from":{"model"},"to":{"model"}}` block（作为普通的 `content_block_start`/`stop` 到达）。TKCode 不使用 fallbacks 时不会出现，但解析器要能容忍未知 block。

---

## 8. Prompt caching

### 8.1 基本机制

- **前缀匹配**：渲染顺序为 `tools` → `system` → `messages`。断点之前的字节完全一致才能命中；任何一个字节变化都会让其后的缓存全部失效。
- 在 block 上标记 `"cache_control": {"type":"ephemeral"}` 表示 5 分钟 TTL；加 `"ttl":"1h"` 表示 1 小时。
- **每个请求最多 4 个断点**，顶层自动缓存也占一个。超出返回 400。
- 断点可以放在：tools 定义、system text block、messages 中的 `text`、`image`、`document`、`tool_use`、`tool_result`（以及 `search_result`、`tool_reference`）。
- 断点**不能**放在：`clear_at` 的 system 消息（400）、thinking block、空 text block [记忆·待核实]。
- 混合 TTL 时，**1h 的断点必须在 5m 的断点之前**。
- **顶层 `cache_control`（自动缓存）**：自动放在最后一个可缓存 block 上，并随对话增长向后移动。显式断点已经用满 4 个时返回 400；最后一个 block 上的显式断点 TTL 与顶层不同时返回 400。部分平台不支持顶层自动缓存。

[Skill:prompt-caching.md §API reference, §Automatic vs explicit breakpoints；SDK:message_create_params.py]

### 8.2 最小可缓存长度（低于这个长度会静默不缓存，`cache_creation_input_tokens: 0`）

| 模型 | 最小 token 数 |
|---|---:|
| Opus 5.5、Opus 5、Fable 5/5.1、Mythos 5/5.1、Sonnet 5.5、Haiku 5.5 | 512 |
| Opus 4.8、Sonnet 5、Sonnet 4.6、Sonnet 4.5、Opus 4.1、Opus 4、Sonnet 4 | 1024 |
| Opus 4.7、Haiku 3.5 | 2048 |
| Opus 4.6、Opus 4.5、Haiku 4.5 | 4096 |

### 8.3 TKCode 推荐布局（agent loop）

1. 工具按 name 排序，确定性序列化（JSON key 顺序固定，不放时间戳或 UUID）。
2. 在**静态 system 的最后一个 block** 上放显式断点，同时缓存 tools 和 system。
3. 加顶层 `cache_control: {"type":"ephemeral"}`，让自动断点跟随对话尾部。
4. 动态信息（日期、模式、cwd 变化等）**不要写进 system**，改用追加的 `role:"system"` 消息；不支持该特性的模型，用 tool_result 之后的 text block。
5. 每个请求的 thinking 配置和 effort 保持固定。变化会让 messages 缓存失效，在部分模型上也会让 tools 和 system 的缓存失效。
6. 单个 turn 新增超过 20 个“位置”时，自动断点可能找不到上一次的缓存（20-block lookback 窗口；连续的 tool_use 或 tool_result 序列算作一个位置）。这种情况下每隔约 15 个位置补一个断点。

### 8.4 缓存失效层级 [Skill:prompt-caching.md §Invalidation hierarchy]

| 变更 | tools 缓存 | system 缓存 | messages 缓存 |
|---|:-:|:-:|:-:|
| 工具定义增删或重排 | ✗ | ✗ | ✗ |
| 切换模型 | ✗ | ✗ | ✗ |
| system 内容 | ✔ | ✗ | ✗ |
| `tool_choice`、图片 | ✔ | ✔ | ✗ |
| thinking 或 effort 变化 | 视模型而定 | 视模型而定 | ✗ |
| 消息内容 | ✔ | ✔ | ✗ |

（✔ = 保留，✗ = 失效）

### 8.5 usage 字段

| 字段 | 含义 |
|---|---|
| `cache_creation_input_tokens` | 本次写入缓存的 token 数（5m TTL 按 1.25 倍计价，1h 按 2 倍） |
| `cache_read_input_tokens` | 从缓存读取的 token 数（约 0.1 倍；Opus 5.5 为 0.05 倍；Fable 5.1 为 0.025 倍） |
| `cache_creation.ephemeral_5m_input_tokens` / `ephemeral_1h_input_tokens` | 写入量按 TTL 拆分 |
| `input_tokens` | **未缓存的剩余部分**，按全价计费 |

健康循环的特征：`cache_read` 逐轮增长，`cache_creation` 约等于“上一轮输出 + 新输入”，`input_tokens` 很小。如果 `cache_creation` 每轮都接近整个对话的大小，说明前缀被改写了。

### 8.6 其他

- `max_tokens: 0` 的预热请求：返回 `content: []`、`stop_reason: "max_tokens"`。不能与以下任一项同时使用（否则 400）：`stream:true`、`thinking.type:"enabled"`、`output_config.format`、`tool_choice` 为 `any`/`tool`、Batches。
- 缓存读取在大多数模型上不计入 input-token 速率限制。
- 缓存按 workspace 隔离，并且按模型隔离。

---

## 9. 错误

### 9.1 错误体

```json
{"type": "error",
 "error": {"type": "invalid_request_error", "message": "..."},
 "request_id": "req_011CSHoEeqs5C35K2UUqR7Fy"}
```
[SDK:types/shared/error_response.py；Skill:error-codes.md]

### 9.2 状态码与类型

| HTTP | `error.type` | 可重试 | 常见原因 |
|---|---|---|---|
| 400 | `invalid_request_error` | **否** | 参数或格式错误、模型不支持的参数（见 2.x、5.2）、角色顺序错误、tool_result 缺失、thinking 签名或绑定错误、未知 beta、ZDR 组织调用 Fable/Mythos |
| 401 | `authentication_error` | 否 | key 无效；x-api-key 与 Bearer 同时发送；OAuth token 放在 x-api-key 里 |
| 402 | `billing_error` | 否 | 计费问题 |
| 403 | `permission_error` | 否 | 组织或 workspace 无权限，或地区、身份验证限制 |
| 404 | `not_found_error` | 否 | 模型 ID 错误，或组织无权使用该模型（两种情况返回一样）；endpoint 错误 |
| 408 | — | 是（SDK 规则） | 请求超时 |
| 409 | — | 是（SDK 规则） | 锁冲突 |
| 413 | `request_too_large` | 否 | 请求体超过 32 MB |
| 429 | `rate_limit_error` | **是** | RPM、ITPM、OTPM 超限；读取 `retry-after` |
| 500 | `api_error` | **是** | 服务端内部错误 |
| 503 | — | 是 | 服务不可用 [SDK:_exceptions.py ServiceUnavailableError] |
| 504 | `timeout_error` | 是 | 网关超时 [SDK:types/shared/gateway_timeout_error.py] |
| 529 | `overloaded_error` | **是** | 过载 |

[Skill:error-codes.md；SDK:_client.py `_make_status_error`, _base_client.py `_should_retry`]

### 9.3 重试策略（照搬 SDK 行为）[SDK:_base_client.py, _constants.py]

- 响应头 `x-should-retry: true|false` **优先级最高**，以它为准。
- 否则以下情况重试：408、409、429、所有 ≥ 500（包括 529）、连接错误和超时。
- 等待时间：先读 `retry-after-ms`（毫秒），再读 `retry-after`（秒，可能是小数，也可能是 HTTP 日期）。都没有时用指数退避：`min(0.5 × 2^n, 8s)`，再加约 25% 的 jitter。SDK 默认最多重试 2 次。
- SDK 默认超时 600s，连接超时 5s。流式请求建议用**空闲超时**（无事件，包括 ping，超过 N 秒就判定断开），不要用总时长超时。
- 速率限制相关响应头：`anthropic-ratelimit-{requests,tokens,input-tokens,output-tokens}-{limit,remaining,reset}` [记忆·待核实]。
- **流中 `error` 事件**：已经收到 200 之后，流里出现 `event: error`（最常见的是 `overloaded_error`）。按 `error.type` 映射到上表决定是否重试（overloaded、api_error 可重试）。重试时整请求重发，丢弃已显示的部分。
- `refusal` **不是错误**（HTTP 200），不要自动用同一个 prompt 重试。

### 9.4 常见的模型相关 400（实现时要能在 UI 上给出清楚的提示）

- `tool_choice: type "tool" and "any" are not supported for this model.`
- `"thinking.type.disabled" is not supported for this model. ...`
- `"thinking.type.enabled" is not supported for this model. ...`
- `role 'system' is not supported on this model`
- ``messages.N.content.M: Invalid `signature` in `thinking` block. The block is bound to a different conversation. ...``
- `block_binding: Extra inputs are not permitted`
- ``Unexpected value(s) `x` for the `anthropic-beta` header.``
- `output_config.effort 'xhigh' is not supported when thinking is disabled on this model. ...`
- `In order to access this model, your organization or workspace must have data retention enabled.`（ZDR 组织调用 Fable/Mythos）

---

## 10. 完整请求示例

### 10.1 TKCode 典型的 agent loop 请求（Opus 5.5，流式）

```json
{
  "model": "claude-opus-5-5",
  "max_tokens": 64000,
  "stream": true,
  "thinking": {"type": "adaptive", "display": "summarized"},
  "output_config": {"effort": "medium"},
  "cache_control": {"type": "ephemeral"},
  "system": [
    {"type": "text", "text": "<TKCode 静态 system prompt>", "cache_control": {"type": "ephemeral"}}
  ],
  "tools": [
    {"name": "edit_file", "description": "...", "eager_input_streaming": true,
     "input_schema": {"type": "object", "properties": {"path": {"type": "string"}, "old": {"type": "string"}, "new": {"type": "string"}},
                      "required": ["path", "old", "new"]}},
    {"name": "read_file", "description": "...", "eager_input_streaming": true,
     "input_schema": {"type": "object", "properties": {"path": {"type": "string"}}, "required": ["path"]}}
  ],
  "tool_choice": {"type": "auto"},
  "metadata": {"user_id": "sha256:..."},
  "messages": [
    {"role": "user", "content": "修复 build"},
    {"role": "assistant", "content": [
      {"type": "thinking", "thinking": "…摘要…", "signature": "EqQB..."},
      {"type": "tool_use", "id": "toolu_01", "name": "read_file", "input": {"path": "src/a.cs"}}
    ]},
    {"role": "user", "content": [
      {"type": "tool_result", "tool_use_id": "toolu_01", "content": "…文件内容…"}
    ]},
    {"role": "system", "content": "Note: files under src/gen are generated; do not edit them."}
  ]
}
```

### 10.2 旧模型（Haiku 4.5，manual thinking）

```json
{"model": "claude-haiku-4-5", "max_tokens": 16000, "stream": true,
 "thinking": {"type": "enabled", "budget_tokens": 8000},
 "messages": [{"role": "user", "content": "..."}]}
```

---

## 11. Beta header 速查（TKCode 可能用到的）

| beta | 作用 |
|---|---|
| `oauth-2025-04-20` | 使用 OAuth Bearer 认证时必须发送 |
| `thinking-binding-controls-2026-08-01` | 启用 `thinking.block_binding` 和 `input_transformations` |
| `thinking-display-updates-2026-08-18` | 启用 `display:"updates"` |
| `mid-conversation-system-clear-at-2026-08-21` | 启用 system 消息的 `clear_at` |
| `mid-conversation-output-config-2026-07-01` | 启用按消息调整 effort |
| `mid-conversation-tool-changes-2026-07-01` | 启用 `tool_addition` 和 `tool_removal` |
| `task-budgets-2026-03-13` | 启用 `output_config.task_budget` |
| `compact-2026-01-12` / `compact-2026-09-04` | 服务端 compaction（threshold 方式和 on-demand 方式） |
| `context-management-2025-06-27` | context editing（清理旧 tool result 或 thinking） |
| `cache-diagnosis-2026-04-07` | 缓存未命中诊断 |
| `server-side-fallback-2026-06-01`（数组形式）/ `-2026-07-01`（`"default"` 形式） | refusal fallback |
| `fast-mode-2026-02-01` | `speed:"fast"` |

已经 GA、**不要再发送**的：`fine-grained-tool-streaming-2025-05-14`、`interleaved-thinking-2025-05-14`（adaptive 自带 interleaved thinking）、`effort-2025-11-24`、`token-efficient-tools-2025-02-19`、`output-128k-2025-02-19`。[Skill:model-migration.md §Beta Headers to Remove on 4.6]

---

## 12. Anthropic 兼容的第三方端点（简表，存在不确定性）

结论先说：这些端点都是“近似兼容”。TKCode 应为每个 provider 维护一个**能力描述**（认证方式、是否支持 cache_control、effort 取值集合、thinking 配置方式、是否支持 `role:"system"`、是否支持 document 和 redacted_thinking、是否可以发送 anthropic-beta 等），**不能**把 Anthropic 的高级特性原样发过去。

| 厂商 | Base URL | 认证 | 已知支持 | 已知忽略或不支持 | 来源 / 可信度 |
|---|---|---|---|---|---|
| **DeepSeek** | `https://api.deepseek.com/anthropic` | `x-api-key` | `max_tokens`、`stream`、`system`、`stop_sequences`、`temperature`（0–2）、`thinking`（**`budget_tokens` 被忽略**）、`output_config.effort`、`tool_choice` 的 auto/any/tool/none、`thinking` block、image（base64/url/file）、`metadata.user_id` | `anthropic-version` 和 `anthropic-beta`（`/messages` 上）被忽略；`cache_control`（任何位置）被忽略；`top_k` 被忽略；`disable_parallel_tool_use` 被忽略；**`tool_result.is_error` 被忽略**；`citations` 被忽略；**不支持 `document`、`redacted_thinking`、`search_result`**；`model` 名会被映射（`claude-opus*` 映射为 `deepseek-v4-pro`，其他映射为 `deepseek-flash`） | [3P:https://api-docs.deepseek.com/guides/anthropic_api] 较可靠。is_error 被忽略意味着错误信息要**写进 content 文本里** |
| **MiniMax** | `https://api.minimax.io/anthropic`（中国区域名可能不同，待核实） | 文档示例用 `Authorization: Bearer` | `temperature`（0–2，超出范围报错）、`top_p`、`tool_choice`、`tools`、`thinking`（adaptive/disabled，各模型行为不同：M3.1 上 disabled 返回 400，M2.x 上 disabled 被忽略）、`output_config.effort`（只对 M3.1）、thinking block 需要**原样回传**、image（M3 系列）、请求体 ≤ 64 MB | `top_k`、`stop_sequences`、`mcp_servers`、`context_management`、`container` 被忽略；文档**没有提到 `cache_control` 和 document** | [3P:https://platform.minimax.io/docs/api-reference/text-anthropic-api] 中等可靠 |
| **Moonshot Kimi** | `https://api.moonshot.ai/anthropic`（中国区 `.cn` 待核实） | `Authorization: Bearer` | 模型 `kimi-k3`；`system`；`stream`；`stop_sequences`（≤ 5 条，每条 ≤ 32 字节）；`tool_choice` 只有 `auto`/`any`/`none`（**没有 `tool`**）；**只支持顶层 `cache_control`**（messages 内的断点被忽略）；`output_config.effort` 取 `low`/`high`/`max`（默认 max，中途修改会破坏缓存）；`output_config.format`；thinking block 带 signature，需要原样回传；`output_tokens_details.thinking_tokens`；stop_reason 只有 end_turn/max_tokens/tool_use/refusal；最后一条为 assistant 时进入 Partial Mode 续写 | 请求侧 `thinking` 参数**没有文档**，思考深度由 effort 控制；document、temperature、top_k、anthropic-beta 均无文档 | [3P:https://platform.kimi.ai/docs/api/messages] 中等可靠 |
| **智谱 / Z.ai（GLM）** | `https://open.bigmodel.cn/api/anthropic`（国际版疑为 `https://api.z.ai/api/anthropic`，待核实） | `x-api-key` | 基础 messages 和流式 | 官方页面只说“某些场景下与 Claude 接口仍存在差异”，没有列出明细。第三方资料称 GLM 4.5+ 默认开启 thinking、部分型号不允许 disabled、需要回传 reasoning 内容 | [3P:https://docs.bigmodel.cn/cn/guide/develop/claude/introduction] 低可靠，需要实测 |

对 TKCode 的兼容建议：

1. 对第三方端点**默认不发送**：`anthropic-beta`、`eager_input_streaming`、`block_binding`、`role:"system"` 消息（统一降级为 user 文本）、`display:"updates"`、`clear_at`、`fallbacks`。
2. thinking block 带上来源标记（provider+model）。**跨 provider 切换时剥离对方产生的 thinking 和 redacted_thinking**；同 provider 内原样回放。
3. tool_result 出错时，在 content 文本里显式写出 “Error: …”，因为 DeepSeek 等会忽略 `is_error`。
4. 可以放心使用的 `cache_control` 只有 Anthropic 官方；Kimi 只支持顶层；DeepSeek 忽略；MiniMax 和智谱未知。
5. 流事件格式基本一致，但必须容忍字段缺失（例如 `cache_*` 为 null、没有 `signature_delta`）和未知事件。

---

## 13. 实现必须满足（Checklist）

**请求构建**

- [ ] 始终发送 `anthropic-version: 2023-06-01` 和 `content-type: application/json`。认证方式在 `x-api-key` 与 `Authorization: Bearer` 中**二选一**；使用 Bearer 时追加 `anthropic-beta: oauth-2025-04-20`。
- [ ] 多个 `anthropic-beta` 用逗号合并到一个 header 中；只发送当前模型和平台支持的 beta（未知 beta 会 400）。
- [ ] `model`、`max_tokens`、`messages` 必填；`max_tokens` 要为 thinking 留出余量（agent 长 turn 建议 32K–64K），并且不超过模型上限（128K，Haiku 4.5 为 64K）。
- [ ] 始终使用流式（`stream:true`）。
- [ ] `messages[0]` 必须是 user；发送前合并相邻的同角色消息；**不以 assistant 结尾**（`pause_turn` 续跑除外）。
- [ ] 按模型能力表决定 `thinking` 字段：Opus 5.5、Fable、Mythos **不能发送 disabled 或 enabled**；Sonnet 5.5 不能发送 disabled；Opus 4.7+ 和 Sonnet 5+ 不能发送 `budget_tokens`；`between_tools` 只能发给 Sonnet 5.5。
- [ ] Opus 5/Haiku 5.5 的 disabled 和 Sonnet 5.5 的 `between_tools` 只能与 effort ≤ high 组合。
- [ ] `budget_tokens` 必须 ≥ 1024 且小于 `max_tokens`（只在旧模型上使用）。
- [ ] effort 显式设置（不同模型默认值不同：Opus 5.5 和 Haiku 5.5 默认 medium，其余默认 high）；不要给 Sonnet 4.5 和 Haiku 4.5 发送 effort。
- [ ] 默认**不发送** `temperature`/`top_p`/`top_k`；即使发送，也只发其中一个，并且只发给允许的模型。
- [ ] `tool_choice` 只用 `auto` 或 `none`（Fable 5.1、Mythos 5.1、Opus 5.5、Sonnet 5.5 上 `any`/`tool` 会 400）。
- [ ] 工具按 name 排序并确定性序列化；会话中 `tools` 和 `system` 保持字节级不变；动态指令用追加的 `role:"system"` 消息，不支持的模型降级为 user 文本。
- [ ] `role:"system"` 消息遵守位置规则：跟在 user 之后、位于最后或其后紧跟 assistant、不能作为首条、只能放文本。
- [ ] 第一方端点上，流式请求的 client 工具设置 `eager_input_streaming:true`，并且做严格解析加 schema 校验。

**工具循环**

- [ ] assistant 的完整 content（包括未知 block）原样追加到历史。
- [ ] 下一条 user 消息为每一个 `tool_use.id` 提供 `tool_result`，全部放在同一条消息中，并且 tool_result 位于 content 数组最前面；额外文本放在最后。
- [ ] 失败时使用 `is_error:true` 并写明可读的错误信息；用户拒绝时也必须返回 tool_result。
- [ ] `stop_reason` 为 `max_tokens` 或 `refusal` 时，本轮的 tool_use **不执行**；JSON 解析或校验失败时，回 `is_error` 的 tool_result。

**Thinking**

- [ ] thinking 和 redacted_thinking block **逐字节原样回放**（包括空 `thinking` 字符串和完整的 `signature`/`data`），保持顺序；序列化时不跳过空 block。
- [ ] 历史只追加，不修改：不删除旧 tool_result、不改写之前的 turn、不注入后又删除提醒；compaction 采用“整段摘要替换”或服务端 compaction。
- [ ] 能识别 “bound to a different conversation” 这个 400：剥离全部 thinking 后重试一次（或启用 `drop_block` beta），并记录日志。
- [ ] UI 按 `type` 渲染；渲染非空 thinking（summarized 或 updates），容忍空 thinking；不假设 `content[0]` 是 text。
- [ ] thinking block 记录 provider 和 model；切换 provider 时剥离外来的 thinking block。

**流解析**

- [ ] SSE 解析器：按空行分帧，兼容 `\n`、`\r\n`、`\r`；忽略注释、`id`、`retry`；以 data JSON 中的 `type` 分派。
- [ ] 处理 message_start、content_block_start/delta/stop、message_delta、message_stop、ping、error；**忽略未知事件**；遇到未知 delta 或 block 类型时保留原始 JSON 用于回放。
- [ ] `input_json_delta` 拼接成字符串，在 `content_block_stop` 时整体解析；为空时视为 `{}`。
- [ ] `signature_delta` 是赋值，不是追加；`redacted_thinking` 在 start 事件里就已完整。
- [ ] `message_delta.usage` 是**累计值，覆盖不相加**；缺失的字段保留 message_start 的值。
- [ ] 总输入 token = `input_tokens + cache_creation_input_tokens + cache_read_input_tokens`；`output_tokens` 包含 thinking。
- [ ] 流中出现 `error` 事件时按错误类型处理（overloaded 和 api_error 可重试），丢弃部分输出；使用空闲超时检测断流。

**stop_reason 和错误**

- [ ] 七种 `stop_reason` 都有处理分支：`pause_turn` 原样续发且不加 user 消息、设置续跑上限；`refusal` 先判断再读 content、丢弃部分输出、`stop_details` 只用于展示；`model_context_window_exceeded` 触发 compaction。
- [ ] 重试：优先遵循 `x-should-retry`，否则重试 408、409、429、5xx、529 和网络错误；先读 `retry-after-ms`，再读 `retry-after`，没有时用指数退避（0.5s 起，最大 8s，加 jitter）；4xx（408、409、429 除外）不重试。
- [ ] 记录响应头 `request-id`，以及错误体的 `error.type`、`error.message`、`request_id`；把常见的模型相关 400 翻译成用户能看懂的提示。

**缓存**

- [ ] 在静态 system 的最后一个 block 上放显式断点，加上顶层自动缓存；断点总数不超过 4；1h TTL 的断点排在 5m 之前。
- [ ] 不在 `clear_at` 的 system 消息和 thinking block 上放 `cache_control`。
- [ ] 会话中固定 thinking 配置和 effort（需要调整时使用按消息调整 effort 的 beta）；同一会话不切换模型。
- [ ] 监控 `cache_read_input_tokens`；连续两个请求在相同前缀下读取为 0 时报警。

**第三方端点**

- [ ] 维护每个 provider 的能力描述；第三方端点默认不发送 beta 字段、`role:"system"` 消息、`eager_input_streaming` 和 `cache_control`（仅在 provider 声明支持时发送）。
- [ ] 错误信息同时写进 tool_result 文本中（DeepSeek 等会忽略 `is_error`）。
