# OpenAI Responses API 协议规格（TKCode 实现用）

> 版本基线：2026-10-16 抓取的官方文档 + openai-python v3.31.0（commit `9998c1a`，2026-10-14）。
> 用途：TKCode（C#/.NET 10）以**无状态**方式（`store:false`，每轮全量回放 input，回放带 `encrypted_content` 的 reasoning item）调用 `POST /v1/responses` 流式接口。
> 本文所有结论都标了出处。标为「社区」的条目不是官方文档，只是经验性说明。

## 0. 来源

| 代号 | 来源 |
|---|---|
| [Ref-Create] | https://developers.openai.com/api/reference/resources/responses/methods/create （`.md` 版本） |
| [Ref-Stream] | https://developers.openai.com/api/reference/resources/responses/streaming-events （`.md` 版本） |
| [G-Reasoning] | https://developers.openai.com/api/docs/guides/reasoning |
| [G-FC] | https://developers.openai.com/api/docs/guides/function-calling |
| [G-Stream] | https://developers.openai.com/api/docs/guides/streaming-responses |
| [G-State] | https://developers.openai.com/api/docs/guides/conversation-state |
| [G-Cache] | https://developers.openai.com/api/docs/guides/prompt-caching |
| [G-Compact] | https://developers.openai.com/api/docs/guides/compaction |
| [G-Errors] | https://developers.openai.com/api/docs/guides/error-codes |
| [SDK] | `github.com/openai/openai-python` → `src/openai/types/responses/*.py`、`src/openai/_streaming.py`、`src/openai/_base_client.py`、`src/openai/lib/streaming/responses/_responses.py` |
| [OpenResp] | https://www.openresponses.org/specification （2026-04-24 版） |
| [社区] | https://community.openai.com/t/how-to-solve-badrequesterror-400-item-rs-of-type-reasoning-was-provided-without-its-required-following-item-error-in-responses-api/1151686 、 https://community.openai.com/t/one-potential-cause-of-item-rs-xx-of-type-reasoning-was-provided-without-its-required-following-item-error-stateless-using-agents-sdk/1370540 、 https://community.openai.com/t/store-false-breaks-multi-turn-computer-use/1375928 |

---

## 1. Endpoint、认证与请求头

- **Endpoint**：`POST https://api.openai.com/v1/responses` [Ref-Create]。第三方兼容服务器（Open Responses）也使用 `/v1/responses` 资源 [OpenResp]。
- **请求头**
  - `Authorization: Bearer <API_KEY>`：必填 [Ref-Create 示例；SDK `_client.py`]
  - `Content-Type: application/json`：必填
  - `OpenAI-Organization: <org_id>`、`OpenAI-Project: <project_id>`：可选，SDK 只在配置了对应值时才发送 [SDK `_client.py:659-660`]
  - 流式请求建议加 `Accept: text/event-stream`（SDK 的惯例，官方没有强制要求）
- **响应头**：`x-request-id` 用于日志和工单，SDK 会记录它 [SDK `_base_client.py`]。限流时可能带 `retry-after` 或 `retry-after-ms`。SDK 还会读取非标准头 `x-should-retry: true|false`，它的优先级高于状态码判断 [SDK `_base_client.py:822-925`]。
- **SDK 的重试规则**（可作为默认参考）：`x-should-retry` 优先；其次按状态码，408、409、429 和所有 ≥500 都重试；有 `Retry-After` 时至少等待它给出的时间，没有时用指数退避加抖动 [SDK；G-Errors]。
- **流式开关**：body 里设 `"stream": true` 后，响应变为 `Content-Type: text/event-stream` [OpenResp；Ref-Create]。

---

## 2. 请求体（Create params）

下表依据 [SDK `response_create_params.py`] 和 [Ref-Create] 整理。只列出 agent 客户端会用到的字段，其余字段见文末附录。

| 字段 | 类型 | 默认 | 说明与约束 |
|---|---|---|---|
| `model` | string | — | 例如 `gpt-6-astra`、`gpt-6.1-sol`、`gpt-5.6-*`、`gpt-5.5`。 |
| `input` | `string` 或 `InputItem[]` | — | 传字符串等价于一条 user 消息。agent 场景**一律传数组**。 |
| `instructions` | string \| null | null | 以 system/developer 身份插入的消息。与 `previous_response_id` 一起用时**不会继承**上一轮的 instructions。**不能**放 prompt cache breakpoint [G-Cache]。在 Response 对象中它的类型是 `string \| InputItem[] \| null`。 |
| `stream` | bool | false | 流式开关。 |
| `stream_options.include_obfuscation` | bool | true（服务端默认开启） | 开启后，delta 事件会带一个 `obfuscation` 随机填充字段，用来对抗侧信道攻击。设为 false 可以节省带宽。只能在 `stream:true` 时设置。 |
| `store` | bool \| null | **true** | 是否在服务端保存 response（≥30 天）。TKCode 必须显式发送 `false`。 |
| `previous_response_id` | string \| null | null | 服务端链式续接。不能与 `conversation` 同时使用。无状态模式下**不发送**。 |
| `conversation` | string \| `{id}` | null | Conversations API。TKCode 不使用。 |
| `include` | string[] | — | 取值见 §2.1。 |
| `reasoning` | object | — | 见 §2.2。 |
| `tools` | Tool[] | [] | 见 §2.3。 |
| `tool_choice` | 见 §2.4 | `"auto"` | |
| `parallel_tool_calls` | bool \| null | true | 设为 false 时，每轮**最多 0 或 1 个**工具调用 [G-FC]。修改它会使 prompt cache 失效 [G-Cache]。 |
| `max_output_tokens` | int \| null | null | 上限覆盖**可见输出 + reasoning tokens + 格式化 tokens**。达到上限时 `status=incomplete`，`incomplete_details.reason=max_output_tokens`，而且可能一个可见 token 都还没产出 [G-Reasoning]。官方建议起步时至少预留 25,000。 |
| `max_tool_calls` | int \| null | null | 只限制**内置工具**的总调用次数，不影响 function tool。 |
| `prompt_cache_key` | string \| null | null | 用来替代 `user` 字段，提高缓存命中率。见 §2.6。 |
| `prompt_cache_options` | `{mode, ttl, prewarm, comparison_response_id}` | — | GPT-5.6 及更新的模型才支持。见 §2.6。 |
| `prompt_cache_retention` | `"in_memory"` \| `"24h"` | 由模型和组织决定 | **已弃用**，改用 `prompt_cache_options.ttl`。gpt-5.5 及之后的模型只接受 `24h`。 |
| `safety_identifier` | string（≤128） | — | 稳定的终端用户标识，建议传哈希值。 |
| `service_tier` | `auto` \| `default` \| `flex` \| `scale` \| `priority` \| `fast` \| `ultrafast` | `auto` | 响应中会回填**实际使用**的 tier，可能与请求值不同。请求 `fast` 时响应显示为 `priority`。如果项目不允许该 tier，返回 400 [G-Errors]。 |
| `text` | `{format, verbosity}` | `{format:{type:"text"}}` | `verbosity`: `low` \| `medium`（默认）\| `high`。`format`: `text` \| `json_schema{name,schema,strict,description}` \| `json_object`。修改 format 或 verbosity 会使缓存失效。 |
| `truncation` | `"auto"` \| `"disabled"` | **`disabled`** | disabled 时如果超出上下文窗口，返回 **400**；auto 时从对话**开头**丢弃 item。 |
| `temperature` / `top_p` | float | 1 / 1 | 推理模型通常不支持或会忽略这两个参数，**建议不发送**。 |
| `top_logprobs` | int 0–20 | — | |
| `metadata` | map（≤16 对，key ≤64，value ≤512） | — | |
| `context_management` | `[{type:"compaction", compact_threshold:int}]` | — | 服务端自动压缩，见 §4.8。 |
| `background` | bool | — | 后台模式。TKCode 不使用。 |
| `user` | string | — | 已被 `safety_identifier` 和 `prompt_cache_key` 取代。 |
| `moderation`、`access_programs`、`prompt` | — | — | 当前不需要。 |

### 2.1 `include`

可选值 [SDK `response_includable.py`]：`file_search_call.results`、`web_search_call.results`、`web_search_call.action.sources`、`message.input_image.image_url`、`computer_call_output.output.image_url`、`code_interpreter_call.outputs`、**`reasoning.encrypted_content`**、`message.output_text.logprobs`。

> **与常见认知不同**：现行官方文档说明，在无状态模式（`store:false` 或 ZDR）下，reasoning item **默认**就带 `encrypted_content`。`include:["reasoning.encrypted_content"]` 属于「legacy，仍然接受，但不再需要」[G-Reasoning §Preserve reasoning without stored responses；SDK `response_reasoning_item.py` 注释]。
> **TKCode 的做法**：仍然发送 `include:["reasoning.encrypted_content"]`。官方 API 对它无副作用，而较旧或第三方兼容的服务端可能仍依赖它。

### 2.2 `reasoning`

[SDK `shared_params/reasoning.py`；G-Reasoning]

```jsonc
"reasoning": {
  "effort":  "none|minimal|low|medium|high|xhigh|max",  // 支持哪些值取决于模型
  "summary": "auto|concise|detailed",                   // 不设置就没有 summary
  "context": "auto|current_turn|all_turns",             // 新字段，见下
  "mode":    "standard|pro"                             // GPT-5.6 / GPT-6 支持；默认 standard
  // "generate_summary"：已弃用，改用 summary
}
```

- **effort 的模型差异**：GPT-6 Astra 设 `none` 会返回 HTTP 400。GPT-6.1 Sol 不支持 `none` 和 `minimal`，默认值是 `medium`。gpt-5.5 和 GPT-5.6 的默认值也是 `medium`（standard 和 pro 模式都一样）[G-Reasoning]。客户端应按模型配置可选值，不要写死。
- **summary**：`auto` 会选该模型最详细的 summarizer，目前大多数模型等价于 `detailed`。`concise` 支持 computer-use 模型和 gpt-5 之后的所有推理模型。使用最新模型的 summarizer 可能需要组织验证 [G-Reasoning]。
- **context**（对无状态回放最关键）：
  - `current_turn`：只渲染**当前轮**的 reasoning，不把更早轮次的 reasoning 渲染进下一次采样。**GPT-5.6 之前的模型默认是这个**。
  - `all_turns`：渲染所有可用且兼容的历史 reasoning。GPT-5.6 系列默认使用它，GPT-6.1 Sol 也支持。
  - `auto` 或不设置时使用模型默认值。Response 对象中的 `reasoning.context` 返回**实际生效**的值。
  - 只有请求能拿到历史 item 时 `all_turns` 才起作用。在无状态模式下，这意味着必须**完整回放历史 output item**。
- **同一工具链内（当前 user 消息之后）的 reasoning 一定要回放**，与 context 设置无关（见 §3.6）。

### 2.3 `tools`：function tool（扁平结构）

Responses 的 function tool 是**扁平**结构，不像 Chat Completions 那样包一层 `{"type":"function","function":{...}}` [SDK `function_tool_param.py`；G-FC]：

```json
{
  "type": "function",
  "name": "read_file",
  "description": "Read a UTF-8 text file from the workspace.",
  "parameters": {
    "type": "object",
    "properties": {
      "path":  { "type": "string" },
      "limit": { "type": ["integer", "null"] }
    },
    "required": ["path", "limit"],
    "additionalProperties": false
  },
  "strict": true
}
```

| 字段 | 类型 | 说明 |
|---|---|---|
| `type` | `"function"` | 必填 |
| `name` | string | 必填 |
| `parameters` | JSON Schema object \| null | 在 SDK 类型中标为 Required，但值可以是 null |
| `strict` | bool \| null | 在 SDK 类型中标为 Required，但值可以是 null |
| `description` | string \| null | 可选 |
| `defer_loading` | bool | 延迟加载，配合 `tool_search` 使用（gpt-5.4 及以上） |
| `output_schema` | object \| null | 描述字符串输出所编码的 JSON 值 |
| `allowed_callers` | `["direct","programmatic"]` | programmatic tool calling |
| `async` | bool | 新字段 |

**strict 规则** [G-FC §Strict mode]：
- `strict:true` 时，每个 object 都必须设 `additionalProperties:false`，并且 `properties` 中**所有**字段都要列进 `required`。可选字段写成 `"type":[...,"null"]`。不满足这些条件时，请求会被**拒绝**（400）。
- **不设置 `strict`**：Responses 会尝试把 schema 规范化成 strict。如果规范化失败，就回退到非 strict，此时响应里的 tool 显示 `strict:false`。（Chat Completions 默认是非 strict。）
- 想在 Responses 中明确使用非 strict，必须显式写 `strict:false`。
- 结论：TKCode 要么把 schema 转换成 strict 兼容形式并发送 `true`，要么显式发送 `false`。

其他工具类型（需要知道它们存在，TKCode 暂不使用）：`namespace`（`{type:"namespace",name,description,tools:[function|custom]}`）、`custom`（自由文本输入，可选 lark/regex 语法约束，输出为 `custom_tool_call`）、`tool_search`、`web_search`、`file_search`、`mcp`、`shell`、`apply_patch`、`computer` 等。

工具定义会被注入模型上下文并按 input token 计费。官方的软性建议是：一轮开始时可用的函数少于 20 个 [G-FC]。

### 2.4 `tool_choice`

[SDK `tool_choice_*.py`；G-FC]

- `"auto"`（默认）：可以调用 0 个、1 个或多个工具。
- `"required"`：至少调用 1 个。
- `"none"`：效果等同于没有传入任何函数。
- `{"type":"function","name":"read_file"}`：强制调用指定函数。
- `{"type":"allowed_tools","mode":"auto|required","tools":[{"type":"function","name":"read_file"}, ...]}`：在**不改变 `tools` 列表**的前提下限制可调用的工具子集，因此不破坏 prompt cache。
- 还有 `{"type":"custom","name"}`、`{"type":"mcp",...}`、`{"type":"shell"}`、`{"type":"apply_patch"}`、内置工具类型和 `{"type":"programmatic_tool_calling"}`。

### 2.5 一个完整的 TKCode 典型请求

```json
{
  "model": "gpt-5.6-sol",
  "stream": true,
  "store": false,
  "include": ["reasoning.encrypted_content"],
  "instructions": null,
  "input": [
    { "type": "message", "role": "developer",
      "content": [{ "type": "input_text", "text": "You are TKCode, a coding agent..." }] },
    { "type": "message", "role": "user",
      "content": [{ "type": "input_text", "text": "Fix the failing test." }] }
  ],
  "tools": [ { "type": "function", "name": "read_file", "description": "...",
               "parameters": { "...": "..." }, "strict": true } ],
  "tool_choice": "auto",
  "parallel_tool_calls": true,
  "reasoning": { "effort": "medium", "summary": "auto" },
  "text": { "verbosity": "medium" },
  "max_output_tokens": 32000,
  "prompt_cache_key": "tkcode:<session-id>",
  "truncation": "disabled"
}
```

### 2.6 Prompt caching 要点

[G-Cache]

- 缓存按**前缀**匹配，要求从开头到 breakpoint 的渲染内容完全一致。GPT-5.6 及以上模型的最小可缓存长度是 1,024 个可见 input token。在旧模型上，`cached_tokens` 按 128 的倍数向下取整。
- **会使缓存失效的改动**：修改断点之前的任何内容，包括编辑历史消息、原地追加内容；修改 `model`；修改 `tools`（名称、描述、schema、**顺序**）；修改 `parallel_tool_calls`、`text.format`、`reasoning.effort`（GPT-6 可以改用 `configuration_update` input item 来调整，不破坏前缀）、`text.verbosity`；compaction 或 truncation。
- 由此对 TKCode 的要求：tools 的序列化必须**确定性**（顺序和字段顺序稳定）；历史 item 必须原样回放，不能重新格式化；developer 提示放在最前面并保持稳定。
- `prompt_cache_key`：旧模型用它影响路由，单个 key 大约每分钟 15 个请求，超过后可能溢出到其他机器。GPT-5.6 及以上模型自动路由，这个 key 主要用于隔离缓存统计，防止缓存命中探测。
- `prompt_cache_options`（GPT-5.6 及以上）：`mode: implicit`（默认，自动放 1 个隐式 breakpoint，另外最多 3 个显式）或 `explicit`（最多 4 个显式；没有显式 breakpoint 时不缓存）；`ttl:"30m"` 是目前唯一可选值；`prewarm:true` 只预热缓存，不生成内容。显式 breakpoint 写在内容块上，形式为 `"prompt_cache_breakpoint":{"mode":"explicit"}`（`input_text`、`input_image`、`input_file` 都支持）。
- 计费：GPT-5.6 及以上模型写缓存按普通 input 的 1.25 倍计费，读缓存按 0.1 倍。

---

## 3. Input items（TKCode 必须发送或回放的结构）

`input` 数组的元素是一个按 `type` 区分的联合类型 [SDK `response_input_item_param.py`]。下面列出 TKCode 用到的类型。

### 3.1 user / developer / system 消息

有两种写法：EasyInputMessage 和 Message。

```json
{ "type": "message", "role": "user",
  "content": [
    { "type": "input_text",  "text": "What is in this image?" },
    { "type": "input_image", "image_url": "data:image/png;base64,....", "detail": "auto" }
  ] }
```

- EasyInputMessage [SDK `easy_input_message_param.py`]：`role` 取 `user|assistant|system|developer`；`content` 可以是 **string** 或内容数组；`type:"message"` 可省略；可选 `phase`（只对 assistant 有效）。
- Message [SDK]：`role` 取 `user|system|developer`；`content` 必须是数组；可选 `status`。
- 内容块：
  - `input_text`：`{type, text, prompt_cache_breakpoint?}`。
  - `input_image`：`{type, detail, image_url?, file_id?, prompt_cache_breakpoint?}`。`detail` 取 `low|high|auto|original`，默认 `auto`。在 Param 类型中 `detail` 标为 Required，**TKCode 要始终发送**。`image_url` 可以是完整 URL 或 base64 data URL。
  - `input_file`：`{type, file_data?(base64), file_id?, file_url?, filename?, detail?: auto|low|high}`。
- `developer` 和 `system` 指令的优先级高于 `user`。建议使用 `developer`。

### 3.2 assistant 消息（回放模型输出）

把上一轮 output 中的 message item **原样**回放 [SDK `response_output_message_param.py`]：

```json
{
  "type": "message",
  "id": "msg_abc",
  "role": "assistant",
  "status": "completed",
  "phase": "commentary",
  "content": [
    { "type": "output_text", "text": "I'll inspect the test first.", "annotations": [] }
  ]
}
```

- ResponseOutputMessageParam 的必填字段：`id`、`content`、`role:"assistant"`、`status`（`in_progress|completed|incomplete`）、`type:"message"`。
- 内容块：`output_text{text, annotations(必填,可为[]), logprobs?}` 或 `refusal{refusal}`。
- **`phase`**：取值 `"commentary"`（中间说明，例如工具调用前的 preamble）或 `"final_answer"`。对 gpt-5.3-codex 及之后的模型，「**所有 assistant 消息都要保留并回传 phase**，丢失会降低效果」[SDK 注释]。官方还提到，丢失 phase 会导致 preamble 被当成最终答案，从而过早停止 [G-Reasoning §phase]。不要给 user 消息设置 phase。
- 也可以用 EasyInputMessage 的写法 `{role:"assistant", phase, content:"..."}` 回放（官方 phase 示例就是这么写的），但 TKCode 应当原样回放 output item，以保持缓存前缀稳定。

### 3.3 `function_call`（回放模型发起的调用）

[SDK `response_function_tool_call_param.py`]

```json
{
  "type": "function_call",
  "id": "fc_67ca09c6bedc8190...",
  "call_id": "call_unLAR8MvFNptuiZK6K6HCy5k",
  "name": "read_file",
  "arguments": "{\"path\":\"src/a.cs\",\"limit\":null}",
  "status": "completed"
}
```

- **`id` 与 `call_id` 的区别**：`id`（`fc_...`）是 **item ID**，流式事件中的 `item_id` 就是它；`call_id`（`call_...`）是**调用关联 ID**，`function_call_output.call_id` 必须等于它。两者不能混用。
- 必填：`arguments`（**JSON 字符串**，不是对象）、`call_id`、`name`、`type`。可选：`id`、`status`、`namespace`、`caller`、`async`。
- `arguments` 要**原样**回放，不要解析后重新序列化（以免破坏缓存前缀，也避免改变模型看到的内容）。

### 3.4 `function_call_output`（工具结果）

[SDK `response_input_item_param.py:FunctionCallOutput`；Ref-Create]

```json
{ "type": "function_call_output", "call_id": "call_unLAR8MvFNptuiZK6K6HCy5k",
  "output": "{\"ok\":true,\"content\":\"...\"}" }
```

或者用多模态内容数组：

```json
{ "type": "function_call_output", "call_id": "call_...",
  "output": [
    { "type": "input_text",  "text": "Screenshot of the rendered page:" },
    { "type": "input_image", "image_url": "data:image/png;base64,...", "detail": "auto" }
  ] }
```

- `output` 可以是 `string`，也可以是 `(input_text|input_image|input_file)[]`。字符串格式由调用方自行决定（JSON、纯文本或错误描述都可以）。没有返回值的函数也要返回一个表示成功或失败的字符串 [G-FC]。
- `call_id` 在 SDK 和 Ref 中都标为 `optional/null`，但**实际上必须提供**，否则无法与调用配对。TKCode 必须始终发送。
- 可选：`id`、`status`、`name`、`namespace`、`caller`。
- **每个回放的 `function_call` 都必须有对应的 `function_call_output`**。即使用户取消或执行出错，也要回一条 output，例如 `"error: cancelled by user"`。（官方 [G-FC] 的流程是：先追加 output，再为**每个**调用追加一条 call_id 匹配的 function_call_output。「缺少 output 的 function_call 会导致 400 或行为异常」这一点是工程上的推断，文档没有明确写出。）

### 3.5 `reasoning`（回放推理 item）

[SDK `response_reasoning_item_param.py`]

```json
{
  "type": "reasoning",
  "id": "rs_6806bfca0b2481918a57...",
  "summary": [ { "type": "summary_text", "text": "**Planning the fix**\n\n..." } ],
  "encrypted_content": "gAAAAABo...",
  "status": "completed"
}
```

- 必填：`id`、`summary`（可以是 `[]`）、`type`。可选：`content`（`[{type:"reasoning_text", text}]`，原始推理文本，只有部分模型或兼容实现会返回）、`encrypted_content`、`status`。
- `encrypted_content` **必须取自 `response.output_item.done`**（或 `response.completed.response.output`）中的那个 item。`response.output_item.added` 中的值可能**不完整**。这一点在 store=false 或 ZDR 下尤其重要 [SDK 注释；Ref-Stream `response.output_item.added`]。
- `encrypted_content` 是不透明的数据，客户端不得解析或修改 [OpenResp]。

### 3.6 回放规则（无状态，store:false）

官方规则如下 [G-Reasoning §Keeping reasoning items in context / §Preserve reasoning without stored responses；G-State；G-FC；G-Compact]：

1. **原样保留每个 output item**（reasoning、message（含 phase）、function_call 等），按原顺序追加到历史，再追加 function_call_output 或新的 user 消息，然后整段回放。官方 Python 示例的写法是 `history.extend(item.model_dump() for item in first.output)`，Go 示例则是把 output 的 RawJSON 直接反序列化成 input 联合类型。也就是说，**output item 的 JSON 可以直接作为 input item 使用**。
2. **工具链内必须完整**：从「最后一条 user 消息」到「当前的 function_call_output」之间的所有 item（reasoning、function_call、function_call_output）都必须**一字不改**地回放。如果要裁剪上下文，只能裁剪这段区间之前的内容。
3. 最简单的做法是回放所有历史 reasoning item，服务端会忽略不相关的部分。
4. 对 `all_turns` 模型（GPT-5.6、GPT-6.1 Sol），回放历史轮次的 reasoning 会被真正渲染进上下文。对 `current_turn` 模型，历史轮次的 reasoning 不渲染，但回放也是安全的。
5. **跨模型族**：reasoning 只能在同一模型族内复用。例如 gpt-5.6-sol、terra、luna 之间可以互用，但 GPT-5.6 与 GPT-5.5 之间不行。切换模型族后，API 会**自动忽略**不兼容的 reasoning（即使设置了 `all_turns`）[G-Reasoning]。因此切换模型时**不需要**手动删除 reasoning item。
6. 有 compaction item 时（见 §4.8），可以丢弃**最新一个 compaction item 之前**的所有 item [G-Compact]。
7. **顺序和配对约束**（官方文档没有明说，来自社区报错，但 400 错误真实存在）：
   - 报错 `Item 'rs_…' of type 'reasoning' was provided without its required following item`：reasoning item 后面必须紧跟它在原 output 中的后继 item（message 或 function_call 等）。**不要单独删除 reasoning 后面的 item，也不要打乱顺序** [社区]。
   - 在 store=false 下，如果只回传 `rs_` 的 id 而没有完整内容（例如使用 `item_reference`），服务端会返回 404 或 not found，因为 item 没有被持久化 [社区]。因此**必须回放带 `encrypted_content` 的完整 item**，绝不能使用 `item_reference`。
   - 如果一个流被中断，只收到了 reasoning 而没有后继 item，那么回放这个孤立的 reasoning 会触发上面的 400 错误。**处理方式**：丢弃这个 response 中不完整的尾部 reasoning（未收到 `output_item.done` 的 item 一律不进历史），或者整轮重试。

### 3.7 其他可能遇到的 input item（了解即可）

- `item_reference {id}`：只在有状态模式下可用。
- `compaction {encrypted_content, id?}`：服务端压缩产物，**必须原样回放**。
- `compaction_trigger {}`：手动触发压缩，必须放在 input 的最后一项。
- `configuration_update {reasoning:{effort}}`：GPT-6 系列在对话中途修改 effort，同时保持缓存前缀不变。要放在下一条 user 消息之前 [G-Reasoning §Change reasoning mid-conversation]。
- `custom_tool_call` / `custom_tool_call_output`、`tool_search_call` / `tool_search_output`、`additional_tools`、`program` / `program_output`（`fingerprint` 必须原样回传）、`shell_call*`、`apply_patch_call*`、`mcp_*`、`computer_call*`、`local_shell_call*`、`image_generation_call`、`web_search_call`、`file_search_call`、`code_interpreter_call`。

---

## 4. 输出：Response 对象与 output items

### 4.1 Response 对象

[SDK `response.py`；Ref-Stream 示例]

| 字段 | 说明 |
|---|---|
| `id` | `resp_...` |
| `object` | `"response"` |
| `created_at` / `completed_at` | Unix 秒，**float**。`completed_at` 只在 completed 时有值。 |
| `status` | `completed` \| `failed` \| `in_progress` \| `cancelled` \| `queued` \| `incomplete` |
| `error` | `ResponseError \| null`，见 §6 |
| `incomplete_details` | `{reason: "max_output_tokens" \| "max_messages" \| "content_filter" \| "steered"} \| null` |
| `output` | `OutputItem[]`。**长度和顺序由模型决定**，不能假设第一个就是 message。 |
| `usage` | 见 §7，流式过程中为 null，只在终态事件中出现。 |
| `model`、`reasoning`（生效的 effort、summary、context、mode）、`service_tier`（实际使用的 tier）、`tools`（strict 回退后可能显示 `strict:false`）、`tool_choice`、`parallel_tool_calls`、`text`、`truncation`、`max_output_tokens`、`previous_response_id`、`instructions`、`metadata`、`prompt_cache_key`、`prompt_cache_options`、`prompt_cache_diagnostics`、`store`（文档示例中有，SDK 类型中没有） | 回显字段 |

`output_text` 是 **SDK 提供的便捷属性**（把所有 message 中的 output_text 拼接起来），**不是**线上协议字段。

### 4.2 Output item 类型（TKCode 关心的部分）

[SDK `response_output_item.py`]

| type | 字段 |
|---|---|
| `message` | `id, role:"assistant", status, content:[output_text\|refusal], phase?` |
| `function_call` | `id?, call_id, name, arguments(string), status?, namespace?, caller?, async?` |
| `reasoning` | `id, summary:[summary_text], content?:[reasoning_text], encrypted_content?, status?` |
| `compaction` | `id, encrypted_content, created_by?` |
| `custom_tool_call` | `id?, call_id, name, input(string), namespace?` |
| 其他 | `function_call_output`、`tool_search_call/output`、`additional_tools`、`program/program_output`、`web_search_call`、`file_search_call`、`computer_call(_output)`、`image_generation_call`、`code_interpreter_call`、`local_shell_call(_output)`、`shell_call(_output)`、`apply_patch_call(_output)`、`mcp_*` |

`output_text` 内容块：`{type:"output_text", text, annotations:[file_citation|url_citation|container_file_citation|file_path], logprobs?}`。`refusal` 内容块：`{type:"refusal", refusal}`。

**未知的 output item 类型**：必须容忍。回放时原样透传 JSON（或者确定它不需要回放时可以丢弃，但这样会破坏缓存前缀）。UI 层面忽略它们即可 [OpenResp §Extensions]。

---

## 5. 流式协议（SSE）

### 5.1 帧格式

- 标准 SSE 格式。每个事件由 `event: <type>` 和 `data: <单行 JSON>` 组成，以空行结束。`event:` 的值与 JSON 中的 `type` 相同 [Ref-Create §Streaming 示例；OpenResp]。
- 可能出现以 `:` 开头的注释行，例如官方示例中的 `: Intermediate response events omitted.`，必须忽略。
- 解析时以 **JSON 中的 `type` 为准**，不要依赖 `event:` 行（SDK 也是这样做的）。
- SDK 解码器遵循 WHATWG 规范：`data:` 可以有多行，用 `\n` 拼接；去掉冒号后的一个前导空格；`id:` 和 `retry:` 字段可以忽略；**事件大小没有上限**（`encrypted_content` 和 `response.completed` 可能非常大，不能用定长行缓冲区）[SDK `_streaming.py:SSEDecoder`]。
- **`[DONE]`**：OpenAI 官方示例的流以 `response.completed` 结束，**没有** `[DONE]`。SDK 收到以 `[DONE]` 开头的 data 时会结束读取。Open Responses 规定服务端「MAY」在终态事件后发送 `[DONE]`，客户端「MUST」能接受不带 `[DONE]` 的流 [OpenResp]。→ **以终态事件作为流结束的依据**，`[DONE]` 只当作可选信号。
- delta 事件可能带有 `obfuscation` 字段（随机字符串），**忽略即可**。所有事件都可能出现**未知字段**，同样忽略。

### 5.2 `sequence_number`

- 每个事件都有 `sequence_number:int`。官方示例从 **0** 开始，按 1 递增 [Ref-Create §Streaming]。
- 用途：排序和去重（例如 WebSocket 或断线续传场景）。在 HTTP SSE 中按到达顺序处理即可。建议发现序号倒退或重复时记录 warning，但**不要因此中断**流。

### 5.3 索引语义

- `output_index`：item 在 `response.output` 数组中的位置。所有 item 级和内容级事件都带这个字段。**以它作为累加 item 的键**。
- `item_id`：item 的 `id`，例如 `msg_…`、`fc_…`、`rs_…`。注意 function_call 的 `item_id` 是 `fc_…`，**不是** `call_id`。
- `content_index`：message 的 `content[]` 下标，适用于 output_text 和 refusal；也是 reasoning 的 `content[]`（reasoning_text）下标。
- `summary_index`：reasoning 的 `summary[]` 下标。
- SDK 注释提到：「Stream indexes can have gaps when a provider emits an empty added event」，并且 `output_item.added` 的 `item` 可能为 null [SDK `_responses.py`]。→ 用**字典**按 `output_index` 存放 item，不要用 List.Add；最终按 key 排序。

### 5.4 事件类型与载荷

[SDK `response_*_event.py`；Ref-Stream]

**生命周期事件**（载荷都是 `{type, response: Response, sequence_number}`）

| type | 含义 |
|---|---|
| `response.created` | 第一个事件。`status:"in_progress"`，`output:[]`，`usage:null`。 |
| `response.queued` | 排队中（`status:"queued"`）。 |
| `response.in_progress` | 生成中。 |
| `response.completed` | 终态，`status:"completed"`，带完整 `output` 和 `usage`。 |
| `response.incomplete` | 终态，`status:"incomplete"`，带 `incomplete_details.reason`。 |
| `response.failed` | 终态，`status:"failed"`，带 `response.error`。 |

**Output item 事件**

| type | 载荷 |
|---|---|
| `response.output_item.added` | `{output_index, item: OutputItem, sequence_number}`。item 处于初始态（`status:"in_progress"`；message 的 `content:[]`；function_call 的 `arguments:""` 已带 `name`、`id`、`call_id`；reasoning 的 `encrypted_content` **可能不完整**）。 |
| `response.output_item.done` | `{output_index, item: OutputItem, sequence_number}`。**这是该 item 的权威最终版本**。 |

**内容块事件**（用于 message，部分实现也用于 reasoning_text）

| type | 载荷 |
|---|---|
| `response.content_part.added` | `{item_id, output_index, content_index, part, sequence_number}`。`part` 是 `output_text{text:"",annotations:[],logprobs:[]}`、`refusal` 或 `reasoning_text` 之一。 |
| `response.content_part.done` | 同上，`part` 是完整内容。 |

**文本事件**

| type | 载荷 |
|---|---|
| `response.output_text.delta` | `{item_id, output_index, content_index, delta, logprobs:[], sequence_number}` |
| `response.output_text.done` | `{item_id, output_index, content_index, text, logprobs:[], sequence_number}` |
| `response.output_text.annotation.added` | `{item_id, output_index, content_index, annotation_index, annotation, sequence_number}` |
| `response.refusal.delta` | `{item_id, output_index, content_index, delta, sequence_number}` |
| `response.refusal.done` | `{item_id, output_index, content_index, refusal, sequence_number}` |

**函数调用事件**

| type | 载荷 |
|---|---|
| `response.function_call_arguments.delta` | `{item_id, output_index, delta, sequence_number}`（**没有** content_index） |
| `response.function_call_arguments.done` | `{item_id, output_index, arguments, sequence_number}`（**没有** name，name 要从 `output_item.added` 获取） |
| `response.custom_tool_call_input.delta/.done` | `{item_id, output_index, delta / input, sequence_number}` |

**推理事件**

| type | 载荷 |
|---|---|
| `response.reasoning_summary_part.added` | `{item_id, output_index, summary_index, part:{type:"summary_text",text:""}, sequence_number}` |
| `response.reasoning_summary_part.done` | `{item_id, output_index, summary_index, part:{type:"summary_text",text}, status?:"incomplete", sequence_number}`。生成被中断时 `status` 为 `incomplete`，正常结束时省略该字段。 |
| `response.reasoning_summary_text.delta` | `{item_id, output_index, summary_index, delta, sequence_number}` |
| `response.reasoning_summary_text.done` | `{item_id, output_index, summary_index, text, sequence_number}` |
| `response.reasoning_text.delta` | `{item_id, output_index, content_index, delta, sequence_number}`（原始推理文本，只有部分模型或兼容实现会发） |
| `response.reasoning_text.done` | `{item_id, output_index, content_index, text, sequence_number}` |

**其他事件**

| type | 说明 |
|---|---|
| `response.compaction.compacting` | `{item_id, output_index, sequence_number}`。表示压缩正在进行，最多每 30 秒发一次，不含内容。压缩 item 的最终 `encrypted_content` 在 `output_item.done` 中 [Ref-Stream 开头说明]。 |
| `error` | `{type:"error", code:string\|null, message, param:string\|null, sequence_number}` |
| 内置工具进度事件 | `response.web_search_call.*`、`response.file_search_call.*`、`response.code_interpreter_call*.*`、`response.image_generation_call.*`、`response.mcp_call*.*`、`response.mcp_list_tools.*`、`response.shell_call_command.*`、`response.shell_call_output_content.*`、`response.audio.*`。TKCode 暂时忽略。 |
| 未知类型 | 必须忽略，不能抛异常 [OpenResp]。 |

### 5.5 典型事件顺序

示例：一个推理模型，先给出 reasoning，再输出 commentary，然后发起两个并行的函数调用。

```
response.created (seq 0)
response.in_progress
response.output_item.added            output_index=0 item={type:reasoning,id:rs_1,summary:[]}
  response.reasoning_summary_part.added   summary_index=0
  response.reasoning_summary_text.delta*  summary_index=0
  response.reasoning_summary_text.done
  response.reasoning_summary_part.done
response.output_item.done             output_index=0 item={reasoning, summary:[...], encrypted_content:"..."}   ← 回放用这个
response.output_item.added            output_index=1 item={type:message,id:msg_1,status:in_progress,content:[],phase:"commentary"}
  response.content_part.added         content_index=0 part={output_text,text:""}
  response.output_text.delta*
  response.output_text.done
  response.content_part.done
response.output_item.done             output_index=1
response.output_item.added            output_index=2 item={type:function_call,id:fc_1,call_id:call_1,name:"read_file",arguments:""}
  response.function_call_arguments.delta*
  response.function_call_arguments.done
response.output_item.done             output_index=2
response.output_item.added            output_index=3 item={function_call fc_2 ...}
  ...
response.output_item.done             output_index=3
response.completed                    response.output=[rs_1,msg_1,fc_1,fc_2], usage={...}
```

Open Responses 规定的单个 item 内事件顺序为 `output_item.added → content_part.added → <type>.delta* → <type>.done → content_part.done → output_item.done` [OpenResp]。不同 item 之间**理论上可能交错**，所以一律按 `output_index` 路由事件。

### 5.6 权威数据的来源

1. **单个 item 的最终状态**：以 `response.output_item.done.item` 为准，包括 status、完整 arguments、annotations、refusal、`encrypted_content`、phase。delta 累加的结果只用于 UI 实时展示。
2. **整个 response 的最终状态**：以终态事件中的 `response` 为准，包括 status、usage、incomplete_details、error。
3. **`response.completed.response.output`** 通常是完整的 output 列表。但 SDK 的防御性实现说明它**可能为 null 或缺失**，这时要用各个 `output_item.done` 按 `output_index` 排序后重建（SDK 的注释是「Recover finalized items; deltas can omit final status, annotations, or refusals」）。SDK 还会校验 completed 中的 item 身份与 done 事件是否一致 [SDK `_responses.py:accumulate_event`]。
   - **TKCode 的策略**：用 `output_item.done` 的结果组成历史。如果 completed 中有 output，用它来校验或覆盖（同一个 id 时以 completed 为准）。如果 completed 没有 output，就用 done 的结果。
4. `response.failed` 和 `response.incomplete` 中的 `output` 也可能为空（官方示例就是 `output:[]`）。SDK 会用已收到的 done item（或者 added 后已累加的 item）补全。
5. `encrypted_content` **只能**从 `output_item.done`（或终态 response 的 output）中取，绝不能用 `added` 中的值。

### 5.7 流中断与错误

- 收到 `error` 事件：SDK 的做法是：如果 data 的 JSON 中有顶层 `error` 键，就抛出 APIError [SDK `_streaming.py`]。官方 schema 定义的 `error` 事件则是平铺结构 `{type:"error",code,message,param}`。**TKCode 两种结构都要兼容**：`type=="error"` 时读取 `message`、`code`、`param`；JSON 中出现顶层 `error` 对象时读取 `error.message`、`error.code`、`error.type`。
- Open Responses 规定：流式过程中出现错误后，「followed by `response.failed`」[OpenResp]。OpenAI 的实际行为可能是 `error` 之后直接断开。→ 收到 `error` 后，再等待一个终态事件，但要有超时，并以连接关闭为准。
- **流在没有终态事件的情况下结束（EOF）**：视为传输错误，可以重试。这一轮中未收到 `output_item.done` 的 item 不得写入历史（见 §3.6 第 7 条）。

---

## 6. 状态、停止原因与错误结构

### 6.1 映射到 TKCode 的 StopReason

| 终态事件 / `response.status` | 条件 | TKCode StopReason |
|---|---|---|
| `completed` | output 中有 `function_call`（或 custom_tool_call） | `ToolUse` |
| `completed` | 没有工具调用，message 中有 refusal 内容块 | `Refusal` |
| `completed` | 其他情况 | `EndTurn` |
| `incomplete` | `reason=max_output_tokens` | `MaxTokens`。如果没有任何 output_text，说明在推理阶段就耗尽了 [G-Reasoning]。 |
| `incomplete` | `reason=content_filter` | `ContentFilter` |
| `incomplete` | `reason=max_messages` | `MaxTokens` 或 `Other`（这个原因在文档中没有展开说明） |
| `incomplete` | `reason=steered` | 只在 WebSocket steering 中出现，HTTP 下不会遇到 |
| `failed` | `response.error` | `Error`（携带 code 和 message） |
| `cancelled` | — | `Cancelled`（只在 background 模式下出现） |

> 注意：Responses API **没有** `finish_reason` 字段。「是否调用了工具」只能通过检查 output 中有没有 `function_call` 来判断。`completed` 加上 function_call 就是正常的工具轮次。
> `incomplete` 时 output 中可能有 status 为 `incomplete` 的 message 或 function_call。被截断的 function_call 的 **arguments 可能不是合法 JSON**，不得执行。

### 6.2 `response.error`（Response 内）

[SDK `response_error.py`]

`{ code, message, param?: string|null, type?: string|null, misalignment?: {...} }`。

`code` 的取值包括：`server_error`、`rate_limit_exceeded`、`invalid_prompt`、`data_residency_mismatch`、`bio_policy`、`misalignment_policy_violation`、`vector_store_timeout`，以及一系列 `invalid_image*`、`image_*` 错误码。`misalignment` 包含 `detailed_explanation`、`error_type`（客户端必须能接受新增的取值）、`review_target`、`steer.message`。

### 6.3 HTTP 错误体（非 2xx，流开始之前）

`{"error": {"message", "type", "param", "code", "misalignment"?}}` [SDK `shared/error_object.py`]。

常见情况 [G-Errors]：
- 400：参数错误、schema 不合法、超出上下文（truncation=disabled 时）、配对错误。
- 401：认证失败。
- 403：地区不支持，或 Daybreak 权限不足。
- 404：store=false 时 previous_response_id 或 item 找不到。
- 429：限流，或 `credit_balance_exhausted`、`*_spend_limit_exceeded`、`slow_down`。
- 500：服务端错误。
- 503：`server_is_overloaded`。

有 `Retry-After` 时按它重试。Open Responses 的错误类型为 `server_error`(500)、`invalid_request`(400)、`not_found`(404)、`model_error`(500)、`too_many_requests`(429) [OpenResp]。

---

## 7. Usage

[SDK `response_usage.py`；G-Cache；G-Reasoning]

```json
"usage": {
  "input_tokens": 291,
  "input_tokens_details": { "cached_tokens": 0, "cache_write_tokens": 0 },
  "output_tokens": 23,
  "output_tokens_details": { "reasoning_tokens": 0 },
  "total_tokens": 314
}
```

- **`input_tokens` 已经包含** `cached_tokens` 和 `cache_write_tokens`。官方的计费公式是 `ordinary = input_tokens - cached_tokens - cache_write_tokens` [G-Cache]。**不要**再把 cached 加到 input 上。
- **`output_tokens` 已经包含** `reasoning_tokens`。
- `total_tokens = input_tokens + output_tokens`。
- `cache_write_tokens` 是新字段，只有 GPT-5.6 及以上模型会产生非零值（写缓存按 1.25 倍计费）。旧的或兼容的服务端可能不返回该字段，应当按 0 处理。
- 流式过程中 usage 只出现在终态事件（completed、incomplete、failed）的 `response.usage` 中，并且**可能为 null**（例如 failed 示例）。
- 即使用 `previous_response_id`，历史 token 也照常按 input 计费。无状态全量回放并不会更贵，缓存命中率才是关键。

---

## 8. 其他与 agent 相关的机制

### 8.1 Compaction（长会话）

[G-Compact；SDK]

- 服务端压缩：在请求中加 `"context_management":[{"type":"compaction","compact_threshold":200000}]`。渲染后的 token 数超过阈值时，服务端会在本轮 output 中产出一个 `{type:"compaction", id, encrypted_content}` item（流式中会有 `response.compaction.compacting` 进度事件，最终内容在 `output_item.done` 中）。在 `store=false` 下它对 ZDR 也是友好的。
- 无状态链式调用的做法：把 output（包括 compaction item）原样追加到历史。之后**可以丢弃最新 compaction item 之前的所有 item**，以减少延迟。
- 独立的 `POST /v1/responses/compact` 返回一个新的 input 窗口（不是 response id），下一轮直接把它当作 `input` 使用 [OpenResp]。
- `compaction_trigger` input item 用于手动触发压缩，必须放在 input 的最后一项。
- compaction 会使 prompt cache 失效。

### 8.2 WebSocket 模式、steering

存在 `response.create` / `response.steer` 等 WebSocket 事件，以及 `incomplete_details.reason="steered"`。HTTP SSE 不涉及这些，TKCode 第一版可以不实现。

---

## 9. Open Responses（第三方兼容服务器）要点

[OpenResp]

- 资源路径同样是 `/v1/responses`。SSE 中 `event:` 必须与 `type` 一致；服务端「SHOULD NOT」使用 `id:`；流以 `response.completed`、`failed` 或 `incomplete` 结束；`[DONE]` 可选。
- item 要求必须有 `id`、`type`、`status`。`incomplete` 是终态，并且必须是最后一个 item，同时会把整个 response 置为 incomplete。
- reasoning item 的 `content`、`summary`、`encrypted_content` 都是可选的，provider 可以任意组合。也就是说，第三方服务端可能直接通过 `reasoning_text` 发送原始推理文本，也可能根本不发 `encrypted_content`。
- 扩展 item 和事件必须带 provider 前缀，例如 `openai:web_search_call`、`acme:trace_event`。客户端**必须能忽略未知类型**。
- 错误类型为 `server_error`、`invalid_request`、`not_found`、`model_error`、`too_many_requests`。
- `allowed_tools` 由服务端强制执行。

---

## 10. 实现必须满足（Checklist）

**请求构造**
1. [ ] 显式发送 `"store": false`。服务端默认值是 **true**。
2. [ ] 不发送 `previous_response_id` 和 `conversation`，每轮回放完整的 `input`。
3. [ ] 发送 `include:["reasoning.encrypted_content"]`。官方现在会默认返回，但这样做对兼容实现更稳妥。
4. [ ] function tool 使用**扁平结构** `{type:"function",name,description,parameters,strict}`，不包 `function:{}`。
5. [ ] `strict` 必须显式设置：要么设为 `true` 并提供 strict 兼容的 schema（`additionalProperties:false`，所有字段都在 required 中，可选字段用 `["T","null"]`），要么设为 `false`。不设置时，服务端会静默尝试规范化。
6. [ ] tools 的顺序和 JSON 序列化必须确定、稳定。developer 提示放在最前面并保持不变，以保护 prompt cache 前缀。
7. [ ] 限制可用工具时优先使用 `tool_choice:{type:"allowed_tools",...}`，不要改动 `tools` 列表。
8. [ ] 推理模型不发送 `temperature` 和 `top_p`。`reasoning.effort` 的可选值按模型配置（例如 GPT-6 Astra 不支持 none，GPT-6.1 Sol 不支持 none 和 minimal）。
9. [ ] `max_output_tokens` 要把 reasoning 也算进去（建议 ≥25k 起步）。
10. [ ] 使用稳定的 `prompt_cache_key`（例如按会话设置）。
11. [ ] `input_image` 始终带上 `detail`。

**回放（历史管理）**
12. [ ] 历史只记录**已经收到 `output_item.done` 的 item**，内容取自 done 事件或终态 response，并按 `output_index` 排序。
13. [ ] output item 以**原始 JSON 原样**回放，包括 `id`、`status`、`phase`、`annotations`、`encrypted_content`、`arguments` 原字符串、未知字段和未知类型。不要重新格式化。
14. [ ] reasoning item 必须带上完整的 `encrypted_content`（取自 done），并保持它与后继 item 的相对顺序。绝不能孤立回放 reasoning，也不能使用 `item_reference`。
15. [ ] 每个 `function_call` 都必须有对应的 `function_call_output`，用 **`call_id`**（不是 `id`）关联。取消或失败时也要回传一个说明字符串。
16. [ ] 从最后一条 user 消息到最新的 function_call_output 之间的 item 一字不改。裁剪只能发生在这个区间之前，或者最新 compaction item 之前。
17. [ ] assistant 消息保留原有的 `phase`（`commentary` 或 `final_answer`）。user 消息不设置 phase。
18. [ ] 切换模型或模型族时，不需要手动删除 reasoning，服务端会忽略不兼容的部分。
19. [ ] 流中断或失败的那一轮：丢弃没有 done 的 item。如果 reasoning 的后继 item 缺失，就丢弃这个 reasoning（或整轮重试）。

**流解析**
20. [ ] SSE 解析器不限制行长或事件大小，支持多行 `data:`，忽略 `:` 注释行，以 JSON 中的 `type` 分派事件。
21. [ ] 终态事件是 `response.completed`、`response.incomplete`、`response.failed`。`[DONE]` 可选，收到时直接结束。EOF 时如果还没有收到终态事件，按传输错误处理。
22. [ ] 用字典按 `output_index` 累加 item，容忍索引空缺，以及 `item` 为 null 的 added 事件。
23. [ ] 函数参数按 `output_index` 拼接 delta。`arguments.done` 和 `output_item.done` 中的完整值为准。只在 done 之后解析 JSON。`incomplete` 状态下的函数调用不执行。
24. [ ] 函数名、`call_id`、`id` 都从 `output_item.added` 或 `output_item.done` 的 item 中获取，因为 arguments 事件里没有这些字段。
25. [ ] reasoning summary 按 `summary_index` 分段，reasoning_text 按 `content_index`，output_text 和 refusal 按 `content_index`。
26. [ ] 忽略未知事件类型、未知字段，以及 `obfuscation` 字段。
27. [ ] `error` 事件同时兼容平铺结构 `{code,message,param}` 和嵌套结构 `{error:{...}}`。
28. [ ] 即使 `response.completed.response.output` 缺失或为 null，也要能工作（用 done 的 item 重建）。

**结果与计量**
29. [ ] StopReason 依据 `status`、`incomplete_details.reason` 以及 output 中是否有 function_call 来判断（Responses 没有 finish_reason 字段）。
30. [ ] usage 中，`input_tokens` 已包含 cached 和 cache_write，`output_tokens` 已包含 reasoning。usage 可能为 null，`cache_write_tokens` 可能缺失。
31. [ ] HTTP 重试规则：先看 `x-should-retry`，然后对 408、409、429、5xx 重试。遵守 `retry-after-ms` 和 `retry-after`，否则用指数退避加抖动。
32. [ ] 记录 `x-request-id`。

---

## 附录 A：与常见认知不同的地方

1. `encrypted_content` 在 store=false 时**默认返回**，`include` 已经降为 legacy 写法（但仍然接受）。
2. 新增了 `reasoning.context`：GPT-5.6 系列默认使用 `all_turns`，会把**历史轮次**的 reasoning 渲染进上下文；旧模型默认 `current_turn`。模型族之间不能互用 reasoning，服务端会自动忽略不兼容的部分。
3. `phase`（`commentary` / `final_answer`）是 assistant 消息上的字段。gpt-5.3-codex 及之后的模型要求回放时保留它，否则 preamble 可能被当成最终答案。
4. 不设置 `strict` 时，Responses 会**自动尝试 strict**，失败时静默回退。
5. `function_call_output.call_id` 在类型定义中是 optional，但实际上必须提供。`output` 也可以是多模态数组。
6. `response.completed` 的 `output` 在 SDK 中被当作可能缺失来处理；`output_index` 可能有空缺。
7. usage 新增了 `cache_write_tokens`（GPT-5.6 及以上模型写缓存按 1.25 倍计费）；`input_tokens` 已经包含缓存部分。
8. `truncation` 默认值是 `disabled`，超出上下文时直接返回 400。
9. `prompt_cache_retention` 已弃用，改用 `prompt_cache_options`；修改 reasoning effort 会破坏缓存，GPT-6 可以改用 `configuration_update` item。
10. `incomplete_details.reason` 新增了 `max_messages` 和 `steered`；`ReasoningSummaryPartDone` 新增了 `status:"incomplete"`。
11. `service_tier` 新增了 `fast`（响应中显示为 `priority`）和 `ultrafast`。
12. `created_at` 和 `completed_at` 是 **float**。

## 附录 B：未覆盖或暂不使用的字段

`background`、`conversation`、`prompt`（模板）、`moderation`、`access_programs`、`top_logprobs`、`max_tool_calls`、内置工具（web_search、file_search、code_interpreter、image_generation、mcp、computer、shell、apply_patch、tool_search）、programmatic tool calling（`program` item）、WebSocket 模式与 steering、Conversations API。需要时请查阅 [Ref-Create] 和 SDK 中对应的类型文件。
