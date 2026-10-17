# Model Context Protocol (MCP) 客户端协议规格（TKCode 实现用）

> 版本基线：MCP **2026-07-28**（当前稳定版），兼容 **2025-11-25** 及更早的旧版（按 initialize 握手协商）。
> 用途：TKCode 的 Agent Host 作为 MCP **Client**，把 MCP 服务器的工具、资源、提示词接入内置 Agent。设计见 [design/mcp.md](../design/mcp.md)。
> 实现：官方 C# SDK `ModelContextProtocol.Core` 2.2.0。本文的作用有两个：一是界定 SDK 负责什么、TKCode 负责什么；二是列出 TKCode 必须自己遵守的规则。
> 编写日期：2026-10-16。方法名、字段名一律保留英文原文。

## 0. 来源

| 标记 | 来源 |
|---|---|
| **[Spec:x]** | `https://modelcontextprotocol.io/specification/2026-07-28/x`，源文件为 `github.com/modelcontextprotocol/modelcontextprotocol` 的 `docs/specification/2026-07-28/x.mdx` |
| **[Changelog]** | `…/2026-07-28/changelog`，**[Deprecated]** 为 `…/2026-07-28/deprecated` |
| **[Legacy]** | `…/specification/2025-11-25/…`（旧版：initialize 握手、`Mcp-Session-Id`） |
| **[SDK]** | `github.com/modelcontextprotocol/csharp-sdk` v2.2.0 源码与 v2.0.0 发布说明 |
| **[实测]** | 2026-10-16 在 osx-arm64 上用 SDK 写的 AOT 测试客户端与测试服务器做的验证（见 §11） |
| **[CC]** | Claude Code 文档 `code.claude.com/docs/en/mcp`（参考它的配置与界面行为） |
| **[Cursor] / [VSCode] / [Windsurf]** | 各客户端的 MCP 配置文档（见 §10） |

---

## 1. 版本与时代

| 术语 | 含义 | 来源 |
|---|---|---|
| Modern | 2026-07-28 及以后的版本。**没有握手**：每个请求在 `_meta` 中携带 `io.modelcontextprotocol/protocolVersion`、`clientCapabilities`（必填）和 `clientInfo`（建议填） | [Spec:basic/versioning]、[Spec:basic/index#meta] |
| Legacy | 2025-11-25 及更早的版本。先发 `initialize`，再发 `notifications/initialized`，能力在握手时协商；HTTP 下有 `Mcp-Session-Id` 会话 | [Legacy] |
| Dual-era | 两种时代都支持。**TKCode 属于这一类**（由 SDK 实现） | [Spec:basic/versioning] |

**时代判定**（SDK 自动完成，[SDK] v2.0.0 发布说明「discovery-first negotiation」）：
- **stdio**：先用首选的 modern 版本发 `server/discover`。返回 `DiscoverResult`，说明对方是 modern；返回已知的 modern 错误（如 `-32022 UnsupportedProtocolVersion`），按 `data.supported` 换一个版本重试，**不回退**；返回其他错误或超时，说明对方是 legacy，改走 `initialize`。**不能**只凭某一个错误码判断要不要回退 [Spec:basic/transports/stdio#backward-compatibility]。
- **HTTP**：先发一个 modern 请求。收到 `400` 时检查响应体：是已知的 modern JSON-RPC 错误就留在 modern；否则回退到 `initialize`。如果 POST 返回 400/404/405 并且响应体不是已知的 modern 错误，再尝试已弃用的 HTTP+SSE：发 GET，等待 `endpoint` 事件 [Spec:basic/transports/streamable-http#backward-compatibility]。
- 判定结果是**服务器的属性**：stdio 在进程存活期间缓存，HTTP 按 origin 缓存 [Spec:basic/versioning]。

**2026-07-28 相对 2025-11-25 的主要变化**（对客户端有影响的部分，[Changelog]）：
1. 去掉协议级会话和 `Mcp-Session-Id`，去掉 initialize 握手；新增 `server/discover`（服务器必须实现）。
2. 服务器发起的请求（`roots/list`、`sampling/createMessage`、`elicitation/create`）改为 **MRTR**：服务器返回 `resultType:"input_required"` 和 `inputRequests`，客户端带上 `inputResponses`（以及原样带回的 `requestState`）**用新的请求 id** 重发原请求（§6）。
3. 所有结果都有 `resultType` 字段；字段缺失时视为 `"complete"`。不认识的值视为无效结果。
4. 变化通知改为由 `subscriptions/listen` 长请求下发；去掉 HTTP 的 GET 流和 `resources/subscribe`。
5. 去掉 `ping`、`logging/setLevel` 和 `notifications/roots/list_changed`；日志级别改为按请求设置（`_meta` 中的 `io.modelcontextprotocol/logLevel`）。
6. 去掉 SSE 断点续传（`Last-Event-ID`）。流断开时，进行中的请求丢失，客户端**必须用新的 id 重新发起**。
7. 列表类结果带 `ttlMs` / `cacheScope` 缓存提示（§4.4）。
8. HTTP 请求必须带 `Mcp-Method` / `Mcp-Name` 请求头，并支持 `x-mcp-header` 参数头（§3.3）。
9. 资源不存在的错误码由 `-32002` 改为 `-32602`；客户端仍应把 `-32002` 当作资源不存在。
10. **弃用**（[Deprecated]，最早 2027-07-28 之后移除）：Roots、Sampling、Logging、Dynamic Client Registration（推荐改用 Client ID Metadata Documents）、HTTP+SSE 传输。

**TKCode 的选择**：请求时用 SDK 默认的最高版本，由 SDK 负责向下协商；协商出的版本（`McpClient.NegotiatedProtocolVersion`）在设置页显示。

## 2. 消息与错误码

- JSON-RPC 2.0。请求的 `id` 必须是 string 或 integer，不能是 `null`，在未完成的请求之间不能重复 [Spec:basic/index]。
- 错误码 [Spec:basic/index#error-codes]：

| 码 | 含义 | TKCode 处理 |
|---|---|---|
| `-32700`、`-32600`～`-32603` | JSON-RPC 标准错误 | `tools/call` 收到时作为工具错误交给模型（见 §4.2） |
| `-32020` HeaderMismatch | HTTP 请求头与请求体不一致 | 重新执行 `tools/list`，然后重试一次（SDK 的 `McpHeaderExtractor` 负责生成请求头） |
| `-32021` MissingRequiredClientCapability | 缺少客户端能力（`data.requiredCapabilities`） | 作为工具错误交给模型，并在设置页记一条日志 |
| `-32022` UnsupportedProtocolVersion | `data.supported` / `data.requested` | 由 SDK 换版本重试 |
| `-32002`（旧版） | 资源不存在 | 与 `-32602` 同样处理 |
| `-32042`（只在 2025-11-25 中有） | URL elicitation required | 由 SDK 处理 |

- `-32000`～`-32019` 是旧版自定义的区段，除 `-32002` 外不能假定含义；本地产生的错误（超时等）不能和对端返回的错误码混淆 [Spec:basic/index#error-codes]。

## 3. 传输

### 3.1 stdio [Spec:basic/transports/stdio]

- 客户端把服务器作为子进程启动；每行一条 JSON-RPC 消息，消息内**不能有换行**；stdout 只能输出 MCP 消息；stderr 是日志，**不能当作出错的信号**。
- Modern 版本中服务器不会在 stdout 上发请求；与某个请求相关的通知（`notifications/progress`、`notifications/message`）和该请求共用同一条通道。
- 取消：发送 `notifications/cancelled {requestId, reason?}`。
- **关闭顺序**：先关闭子进程的 stdin，等它退出；超时后强制结束（POSIX 先 SIGTERM 再 SIGKILL，Windows 用 TerminateProcess 或 Job Object）。SDK 的 `StdioClientTransportOptions.ShutdownTimeout` 默认 5s。
- **意外退出**：客户端**应当**重启服务器。协议是无状态的，进行中的请求直接丢失，可以重试；`subscriptions/listen` 订阅需要重新建立。
- 规范**不建议**把单个会话或对话的生命周期作为 stdio 进程的生命周期 [Spec:basic/index#statelessness]，这一点支持 TKCode 采用 Host 共享连接池的做法。

SDK 对应：`StdioClientTransport(new StdioClientTransportOptions { Command, Arguments, WorkingDirectory, EnvironmentVariables, InheritEnvironmentVariables = true, ShutdownTimeout, StandardErrorLines = line => … })`。

### 3.2 Streamable HTTP（Modern）[Spec:basic/transports/streamable-http]

- 只有一个 MCP 端点，只接受 POST。每条消息单独一个 POST，`Accept: application/json, text/event-stream`。
- 发请求时，服务器回 `application/json`（单个对象）或 `text/event-stream`（该请求独占的 SSE 流：先是相关通知，最后是响应）。客户端**必须**两种都支持。
- 发通知时，服务器回 `202`，没有响应体。
- **取消**：关闭该请求的 SSE 响应流就是取消，不需要再发 `notifications/cancelled`。
- SSE 中以 `:` 开头的注释行是保活信号，必须忽略。不支持 `Last-Event-ID`。
- 请求头：`MCP-Protocol-Version`（必须与 `_meta` 中的版本一致）、`Mcp-Method`、`Mcp-Name`（`tools/call` / `resources/read` / `prompts/get` 时必填）。值不是安全 ASCII 时，用 `=?base64?…?=` 编码。
- 服务器不支持请求的版本时回 `400` + `-32022`；不认识方法时回 `404` + `-32601`；请求头与请求体不一致时回 `400` + `-32020`。

### 3.3 `x-mcp-header`（客户端**必须**支持）

工具 `inputSchema` 中，属性上的 `"x-mcp-header": "Name"` 表示要把该参数的值放进 `Mcp-Param-Name` 请求头。约束如下：
- 名字是 token 语法，在同一个 schema 中不区分大小写地唯一；
- 只能标在 integer、string 或 boolean 类型的属性上，不能是 number；
- 只能经由 `properties` 链静态到达。

违反约束的工具**必须从 `tools/list` 结果中剔除**，并记一条警告。这些由 SDK 的 `McpHeaderExtractor` 完成；TKCode 只需要在设置页的工具列表中显示“已剔除：原因”（如果 SDK 暴露了这个信息，否则写进日志）。

### 3.4 旧版 HTTP（2025-03-26～2025-11-25）[Legacy]

- 服务器可以通过 `Mcp-Session-Id` 分配会话：后续请求都要带上这个头；收到 404 时重新 `initialize`；结束时发 DELETE。
- 客户端可以用 GET 打开一条独立的 SSE 流，接收服务器主动发出的请求和通知；流可以凭 `Last-Event-ID` 续传。
- 服务器可以在 SSE 中直接发 `elicitation/create` 等请求。

以上全部由 SDK 处理（`HttpClientTransportOptions.TransportMode = AutoDetect`、`EnableStandaloneGetStream = true`、`MaxReconnectionAttempts = 5`）。

### 3.5 已弃用的 HTTP+SSE（2024-11-05）

`TransportMode = AutoDetect` 时，POST 失败会回退到这种传输；也可以显式指定 `Sse`。配置中的 `type: "sse"` 映射为 `HttpTransportMode.Sse`。

## 4. 服务器功能

### 4.1 工具：`tools/list` [Spec:server/tools]

- 支持分页：`params.cursor` 和 `nextCursor`。`nextCursor` 为空串也表示还有下一页，只有字段缺失才表示结束；游标不透明，不能解析 [Spec:server/utilities/pagination]。SDK 的 `ListToolsAsync()` 会自动翻完所有页。
- `Tool` 的字段：
  - `name`：建议 1～128 字符，取值 `[A-Za-z0-9_.-]`，区分大小写，**只在单个服务器内唯一**。
  - `title?`、`description`、`icons?`。
  - `inputSchema`：必填，不能为 `null`；默认 JSON Schema 2020-12；必须是对象 schema。v2.0 的 SDK 收到缺少 `inputSchema` 的工具时会抛 `JsonException`。
  - `outputSchema?`、`annotations?`、`_meta?`。
- `annotations`（`ToolAnnotations`）：`title`、`readOnlyHint`、`destructiveHint`、`idempotentHint`、`openWorldHint`。**它们只是提示**：除非服务器可信，客户端必须把它们视为不可信 [Spec:server/tools#tool]、[SDK] `ToolAnnotations` 注释。
- **命名冲突**：聚合多个服务器时**应当**给工具名加服务器前缀；`serverInfo.name` 不保证唯一，不能用它区分服务器 [Spec:server/tools#tool-names]。TKCode 用配置中的服务器键作前缀（见设计文档 §5）。
- 服务器**应当**按确定的顺序返回工具，便于客户端缓存和保持 prompt cache 命中 [Changelog minor 3]。

### 4.2 工具：`tools/call`

- 请求：`{name, arguments}`；需要进度时带 `_meta.progressToken`。
- 结果：`content[]`、`structuredContent?`（任意 JSON）、`isError?`。内容块类型：
  - `text`；
  - `image {data(base64), mimeType}`；
  - `audio {data, mimeType}`；
  - `resource_link {uri, name, description?, mimeType?}`；
  - `resource {resource:{uri, mimeType?, text | blob}}`。
  - 以上都可以带 `annotations {audience?: ("user"|"assistant")[], priority?: 0..1, lastModified?}`。
- 返回 `structuredContent` 的工具**应当**同时在 TextContent 中给出序列化的 JSON（向后兼容）。
- **两类错误**：
  - 协议错误（JSON-RPC error：未知工具、请求格式错误、服务器内部错误）：客户端**可以**交给模型；
  - 执行错误（`isError: true`）：客户端**应当**交给模型，让它自行纠正 [Spec:server/tools#error-handling]。
- 客户端**应当**：对敏感操作请用户确认；调用前向用户展示工具输入，防止数据外泄；把结果交给模型之前做校验；为工具调用设置超时；记录调用以便审计 [Spec:server/tools#security-considerations]。
- 结果可能是 `InputRequiredResult`（MRTR，§6），由 SDK 透明处理。

### 4.3 变化通知：`subscriptions/listen` [Spec:basic/patterns/subscriptions]

- Modern：客户端发一个长时间挂起的请求 `subscriptions/listen {notifications:{toolsListChanged?, promptsListChanged?, resourcesListChanged?, resourceSubscriptions?: string[]}}`。服务器先回 `notifications/subscriptions/acknowledged`（其中给出同意的子集），之后在这个流上推送 `notifications/tools/list_changed` 等通知；每条通知的 `_meta["io.modelcontextprotocol/subscriptionId"]` 都等于该请求的 id。
- 取消订阅：HTTP 下关闭流，stdio 下发 `notifications/cancelled`。服务器主动结束时回一个 `resultType:"complete"` 的响应；连接突然断开时没有这个响应，客户端**可以**重连。
- Legacy：服务器声明了 `listChanged` 能力后，直接发送通知（HTTP 走 GET 流）。
- **SDK 2.2.0 的客户端没有封装 `subscriptions/listen`**（只有服务器端实现）。TKCode 的做法：
  - 用 `McpSession.SendRequestAsync(new JsonRpcRequest { Method = "subscriptions/listen", … })` 加长期的 CancellationToken 自己发这个请求，再用 `RegisterNotificationHandler("notifications/tools/list_changed", …)` 接收通知；
  - Legacy 服务器只需要注册通知处理器；
  - 订阅失败（服务器不支持）时不报错，退回到 TTL 和每轮检查（§4.4）。
  - [待实测]：在 HTTP 上，SDK 会不会把长请求 SSE 流中的通知分发给已注册的处理器。实现时要用测试服务器验证；验证不通过就只靠 TTL。

### 4.4 缓存提示 [Spec:server/utilities/caching]

- `server/discover`、`tools/list`、`prompts/list`、`resources/list`、`resources/templates/list` 和 `resources/read` 的 complete 结果中**必须**带 `ttlMs` 与 `cacheScope`（`public` 或 `private`）。
- `ttlMs` 缺失、为 0 或为负数时，都视为立即过期。TTL **不是轮询间隔**；如果确实要轮询，必须加抖动和退避。
- 收到对应的 `list_changed` 通知时，缓存立即失效。
- 经 MRTR 重试得到的结果（请求中带 `inputResponses` / `requestState`）**不能**缓存。
- TKCode 只用 TTL 判断要不要在每轮开始前重新 `tools/list`，不做跨认证上下文的共享缓存。

### 4.5 资源 [Spec:server/resources]

- 能力：`resources {listChanged?, subscribe?}`。
- `resources/list`（分页）返回 `Resource {uri, name, title?, description?, mimeType?, size?, icons?, annotations?}`。
- `resources/templates/list` 返回 `ResourceTemplate {uriTemplate (RFC 6570), name, …}`。
- `resources/read {uri}` 返回 `contents[]`，每项为 `{uri, mimeType?, text}` 或 `{uri, mimeType?, blob(base64)}`；可能有多项，例如读取一个目录。
- 资源不存在时返回 `-32602`（旧版为 `-32002`）。服务器不能用空的 `contents` 表示资源不存在。
- 资源由**应用控制**：怎样纳入上下文由宿主决定（例如 @ 选择器）。
- `https://` 资源可以由客户端直接抓取，TKCode 首版仍然统一走 `resources/read`。

### 4.6 提示词 [Spec:server/prompts]

- 能力：`prompts {listChanged?}`。
- `prompts/list`（分页）返回 `Prompt {name, title?, description?, arguments?: {name, description?, required?}[], icons?}`。
- `prompts/get {name, arguments: {string: string}}` 返回 `{description?, messages: {role: "user"|"assistant", content: ContentBlock}[]}`。
- 提示词由**用户控制**，典型形式是斜杠命令。
- 错误：名称不存在或缺少参数时返回 `-32602`。

### 4.7 补全（completion）

`completion/complete` 为提示词参数和资源模板参数提供自动补全。首版**不实现**，参数改用普通输入框。

### 4.8 日志（已弃用）

`notifications/message` 只在请求的 `_meta` 中带了 `io.modelcontextprotocol/logLevel` 时才会发送。TKCode **不请求**服务器日志：stdio 的 stderr 已经能看到日志；HTTP 服务器的日志不在客户端的职责范围内。

## 5. 客户端能力

| 能力 | TKCode 首版 | 理由 |
|---|---|---|
| `elicitation {form:{}, url:{}}` | **声明** | 用户已选择支持 elicitation 表单。URL 模式只需要“确认后用系统浏览器打开”，代价很小 |
| `roots` | 不声明 | 已弃用 [Deprecated]；需要工作目录的服务器通过 args、cwd 或 env 拿到 |
| `sampling` | 不声明 | 已弃用 [Deprecated]；还会涉及计费和模型选择方面的风险 |
| `extensions`（Apps / Tasks） | 不声明 | MCP Apps 需要渲染 HTML 沙箱，Tasks 是扩展包，都留到以后 |

### 5.1 Elicitation [Spec:client/elicitation]

- 请求 `elicitation/create`：Modern 版本放在 `InputRequiredResult.inputRequests` 中，Legacy 版本由服务器直接发请求，SDK 把两者统一交给 `McpClientHandlers.ElicitationHandler`。
- **form 模式**（`mode` 缺失时也按 form 处理）：参数为 `message` 和 `requestedSchema`。schema 只能是**扁平对象**，属性限于以下类型：
  - `string`：`minLength`、`maxLength`、`format` ∈ {email, uri, date, date-time}、`default`；
  - `number` / `integer`：`minimum`、`maximum`、`default`；
  - `boolean`：`default`；
  - 单选枚举：`enum`，或 `oneOf[{const, title}]`；
  - 多选枚举：`type: array` 加 `items.enum`，或 `items.anyOf[{const, title}]`，可带 `minItems` / `maxItems`。
- 结果为 `{action: "accept" | "decline" | "cancel", content?}`：
  - `accept`：form 模式带上 `content`，URL 模式不带；
  - `decline`：用户明确拒绝；
  - `cancel`：用户关闭了窗口或没有作出选择。
- **客户端必须**：
  - 清楚显示是哪个服务器在请求；
  - 提供拒绝和取消；
  - form 模式下允许用户在提交前检查并修改；
  - URL 模式下显示完整 URL 并突出显示域名，征得同意后才能打开；不能预取 URL；用不让客户端或 LLM 看到页面内容的方式打开（系统浏览器）；对 Punycode 给出警告；
  - 不把 form 中的任何文本渲染成可点击的链接。
- 服务器不应通过 form 索取密码或 API Key；但客户端仍要把 form 的内容当作不可信输入。
- URL 模式的 `accept` 只表示用户同意打开链接，不表示交互已经完成；完成后由服务器在重试时判断。客户端**应当**提供“重试或取消原请求”的手动控件。

## 6. MRTR [Spec:basic/patterns/mrtr]

- 只有 `tools/call`、`resources/read`、`prompts/get` 可能返回 `InputRequiredResult {resultType:"input_required", inputRequests?, requestState?}`。
- 客户端处理 `inputRequests`，然后**用新的 JSON-RPC id** 重发原请求，带上 `inputResponses`；`requestState` 必须**原样**带回，不能检查或修改；服务器没给 `requestState` 时也不能带。
- SDK 的 `McpClientImpl` 自动完成这个循环：调用 `ResolveInputRequestsAsync`，由它调用 `ElicitationHandler`；循环有次数上限，超过后抛 `McpException`。
- 用户长时间不处理 elicitation 时，这次工具调用会一直挂起，所以 TKCode 的 elicitation 等待要受工具调用的取消控制（Abort）。

## 7. 进度、取消与超时

- **进度** [Spec:basic/patterns/progress]：请求的 `_meta.progressToken` 必须在所有活动请求中唯一；`notifications/progress {progressToken, progress (递增), total?, message?}`。SDK 的 `CallToolAsync(name, args, IProgress<ProgressNotificationValue>)` 会自动生成 token。TKCode 把进度映射为 `ToolExecutionUpdated`。
- **取消** [Spec:basic/patterns/cancellation]：HTTP 下关闭响应流，stdio 下发 `notifications/cancelled`；取消之后到达的响应应当忽略。SDK 根据 CancellationToken 自动处理。
- **超时**：应当为每个请求设置超时；收到进度通知时**可以**重置计时，但必须有上限。TKCode 的取值：启动或连接 30s（`HttpClientTransportOptions.ConnectionTimeout` 与 `McpClientOptions.InitializationTimeout`）；工具调用默认 10 分钟、可配置；收到进度时延长，但不超过上限。

## 8. 授权（HTTP）[Spec:basic/authorization/*]

stdio 服务器**不走** OAuth，凭据从环境变量取 [Spec:basic/index#auth]。HTTP 服务器的流程如下（SDK 的 `ClientOAuthProvider` 已实现，[实测]通过）：

1. 不带令牌发请求，收到 `401` 和 `WWW-Authenticate: Bearer resource_metadata="…", scope="…"`。
2. 获取 Protected Resource Metadata（RFC 9728）：优先用请求头中的 `resource_metadata`；没有时依次尝试 `/.well-known/oauth-protected-resource/<路径>` 和 `/.well-known/oauth-protected-resource`。从 `authorization_servers` 中选择授权服务器。
3. 发现授权服务器的元数据：
   - issuer 带路径时依次尝试 `/.well-known/oauth-authorization-server/<路径>`、`/.well-known/openid-configuration/<路径>`、`<路径>/.well-known/openid-configuration`；
   - 不带路径时依次尝试 `/.well-known/oauth-authorization-server`、`/.well-known/openid-configuration`。
   - 文档中的 `issuer` 必须与构造 URL 时用的 issuer **完全一致**，否则拒绝。
4. **PKCE**：元数据中缺少 `code_challenge_methods_supported`，或其中不含 `S256`，就拒绝继续。SDK v2.0 起强制执行。
5. 客户端注册，按优先级：
   - 预注册的 `client_id`（配置中的 `oauth.clientId`）；
   - CIMD（授权服务器声明了 `client_id_metadata_document_supported`，**并且**客户端在 HTTPS 上托管了元数据文档；TKCode 暂无托管地址，首版不用）；
   - DCR（`registration_endpoint`，已弃用但仍是主要的兜底方式；必须带 `application_type: "native"`，SDK 会自动推断）；
   - 让用户手动填写。
6. 选择 scope：`WWW-Authenticate` 中有 `scope` 时用它；否则用 PRM 的 `scopes_supported`；两者都没有时不发 `scope` 参数。授权服务器声明了 `offline_access` 时追加它（SDK 已实现）。
7. 打开系统浏览器进行授权。请求带 `code_challenge` 和 `resource`（**RFC 8707，必须带**，值为 MCP 服务器的规范 URI，不带末尾斜杠），并记录期望的 issuer 和 `state`。
8. 回调地址必须是 `http://127.0.0.1:<端口>/…` 或 `localhost`，用回环监听器接收。按 RFC 9207 校验 `iss`：
   - 授权服务器声明支持 `iss` 但响应中没有：拒绝；
   - 响应中有 `iss`：做简单字符串比较，不同就拒绝；
   - `iss` 不匹配时，不能显示响应里的 error 内容。
   - 同时校验 `state`。
9. 用授权码换取令牌：带 `code_verifier` 和 `resource`。之后每个请求都带 `Authorization: Bearer`，令牌**不能**放在 URL 查询串中。
10. 刷新：令牌过期或收到 401 时，用 refresh token 刷新；refresh token 必须保密存储；不能假定授权服务器一定会发 refresh token。
11. **Step-up**：收到 `403` 加 `error="insufficient_scope", scope="…"` 时，取已请求 scope 与挑战中 scope 的并集重新授权，重试次数要有限制。SDK v2.0 起，挑战没有带来新 scope 时抛 `McpException`。
12. **绑定**：客户端凭据（client_id / secret）按授权服务器的 `issuer` 保存，不能拿到其他授权服务器使用；授权服务器变化后必须重新注册。

SDK 对应：`HttpClientTransportOptions.OAuth = new ClientOAuthOptions { RedirectUri, ClientId?, ClientSecret?, Scopes?, DynamicClientRegistration = new() { ClientName = "TKCode", … }, TokenCache = <ITokenCache>, AuthorizationCallbackHandler = (ctx, ct) => … }`。`TokenContainer` 带 `ClientId`、`ClientSecret`、`AuthorizationServer`，用 `[JsonSerializable(typeof(TokenContainer))]` 的源生成上下文做持久化（[实测]，AOT 下可用）。

**静态凭据**：不支持 OAuth 的服务器在配置的 `headers` 中写 `Authorization: Bearer {env:TOKEN}` 即可。规范没有规定这种方式，但各客户端都支持；这时**不启用** OAuth 流程。

## 9. 安全要点（客户端侧）

- **本地命令执行**：stdio 服务器就是在本机任意执行命令。项目级 `.mcp.json` 必须经过用户信任确认才能启动（参考 [CC] 的项目作用域审批）；从其他客户端导入的配置同样要求用户确认。
- **令牌直通**：只把授权服务器为该 MCP 服务器颁发的令牌发给它；不同服务器的令牌不能混用。
- **Annotations 不可信**：`readOnlyHint` 等标注不能作为免审批的依据（与用户决策一致，见设计文档 §7）。
- **工具输出不可信**：工具描述和工具结果都可能包含提示注入。设置页要能查看每个工具的描述原文；结果要截断（见设计文档）。
- **SSRF**：授权发现中的 URL 来自服务器，SDK 负责相关校验；TKCode 不额外打开服务器给出的 URL，elicitation 的 URL 模式必须先经用户确认。
- **图标**：只接受 https 或 data URI，不带凭据，同源，并按魔数校验图片类型。首版**不渲染**服务器提供的图标，统一显示默认图标。

## 10. 各客户端的配置格式（导入用）

规范**没有**定义配置文件格式，下面是事实上的各种格式。

| 客户端 | 位置 | 顶层键 | 远程服务器字段 | 变量语法 | 来源 |
|---|---|---|---|---|---|
| Claude Desktop | macOS `~/Library/Application Support/Claude/claude_desktop_config.json`；Windows `%APPDATA%\Claude\claude_desktop_config.json` | `mcpServers` | 只支持 stdio（远程服务器通过 Connectors 配置） | 无 | modelcontextprotocol.io/docs/develop/connect-local-servers |
| Claude Code | 项目 `.mcp.json`；用户 / 本地 `~/.claude.json`（顶层 `mcpServers` 为用户级，`projects.<path>.mcpServers` 为本地级） | `mcpServers` | `type: "http" \| "sse" \| "ws"` + `url`、`headers`、`headersHelper`、`oauth {clientId, callbackPort, authServerMetadataUrl, scopes}` | `${VAR}`、`${VAR:-default}` | [CC] |
| Cursor | `~/.cursor/mcp.json`、`<项目>/.cursor/mcp.json` | `mcpServers` | `url`（没有 `type` 时按 http 处理）、`headers`；stdio 支持 `envFile` | `${env:NAME}`、`${userHome}`、`${workspaceFolder}`、`${workspaceFolderBasename}`、`${pathSeparator}` / `${/}` | [Cursor] cursor.com/docs/mcp |
| VS Code / Copilot | `<项目>/.vscode/mcp.json`；用户配置目录下的 `mcp.json` | **`servers`** + `inputs[]` | `type: "http" \| "sse"`、`url`、`headers`；stdio 支持 `cwd`、`envFile`、`sandbox` | `${input:id}`（运行时提示用户输入）、`${env:VAR}`、`${workspaceFolder}`、`${userHome}` | [VSCode] code.visualstudio.com/docs/copilot/reference/mcp-configuration |
| Windsurf | `~/.codeium/windsurf/mcp_config.json`（旧）；新版 `~/.config/devin/mcp_config.json`（Windows 为 `%APPDATA%\devin\`） | `mcpServers` | **`serverUrl`** 或 `url`、`headers`、`disabledTools` | `${env:VAR}`、`${file:path}` | [Windsurf] docs.windsurf.com |
| Copilot CLI | `~/.copilot/mcp-config.json`；项目 `.mcp.json`、`.github/mcp.json` | `mcpServers` | `type: "local" \| "http" \| "sse"`、`url`、`headers`、`tools: ["*"]` | `${VAR}` | docs.github.com/copilot/how-tos/copilot-cli |
| Kiro / Cline / Roo | `.kiro/settings/mcp.json` 等 | `mcpServers` | `url`；附加 `disabled`、`autoApprove`、`alwaysAllow`、`disabledTools` | 各不相同 | kiro.dev/docs/mcp |
| LM Studio | `~/.lmstudio/mcp.json` | `mcpServers` | 同 Cursor | 同 Cursor | lmstudio.ai/docs/app/mcp |
| Gemini CLI | `~/.gemini/settings.json`、`.gemini/settings.json` | `mcpServers` | **`httpUrl`**（Streamable HTTP）与 `url`（SSE）区分；`cwd`、`timeout`、`trust`、`includeTools`、`excludeTools` | `$VAR`、`${VAR}` | github.com/google-gemini/gemini-cli docs/tools/mcp-server.md |
| Codex | `~/.codex/config.toml` | TOML `[mcp_servers.<name>]` | `command`、`args`、`env`、`cwd`、`url`、`bearer_token_env_var`、`http_headers`、`env_http_headers`、`enabled`、`enabled_tools`、`disabled_tools`、`startup_timeout_sec`、`tool_timeout_sec` | 无（通过 `*_env_var` 字段引用环境变量） | github.com/openai/codex docs/config.md |
| opencode | `opencode.json(c)` | `mcp` | `{type: "local", command: string[], environment, enabled}` / `{type: "remote", url, headers, oauth, enabled}` | `{env:VAR}`、`{file:path}` | opencode.ai/docs/mcp-servers |
| Zed | `~/.config/zed/settings.json` | `context_servers` | `{command: {path, args, env}}`（旧格式）、`{command, args, env}`（新格式）、`{url, headers}` | 无 | zed.dev/docs/ai/mcp |

**判定传输类型时的常见规则**：
- 有 `command` 就是 stdio；
- 有 `url` / `serverUrl` / `httpUrl` 就是远程：
  - `type` 为 `sse`（或 Gemini 的 `url`）时走 SSE；
  - 否则走 Streamable HTTP（TKCode 用 `AutoDetect`，HTTP 不通时会自动回退到 SSE）。
- Claude Code 把带 `url` 但没有 `type` 的条目当作配置错误，TKCode 导入时则宽松地按 http 处理。

## 11. SDK 实测（2026-10-16，osx-arm64，.NET 10）

- 包：`ModelContextProtocol.Core` 2.2.0。传递依赖只有 `Microsoft.Extensions.AI.Abstractions` 10.8.3、`Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.10 和 `Microsoft.Extensions.Logging.Abstractions` 10.0.10。
- 测试客户端覆盖 stdio、Streamable HTTP、AutoDetect、显式 SSE 和 OAuth，用 `PublishAot` 加 `TrimmerSingleWarn=false` 发布，**IL/AOT 警告为 0**。二进制约 12.9 MB；只用 stdio 的客户端约 8.4 MB。
- AOT 二进制全部通过的场景：
  - stdio；
  - Streamable HTTP：无状态的 2026-07-28 服务器，以及强制协商 2025-11-25 的有状态服务器（拿到了 `Mcp-Session-Id`）；
  - AutoDetect 回退到旧版 SSE，以及显式 SSE；
  - 进度通知与取消；
  - 端点不可达时，以 `HttpRequestException` 失败；
  - OAuth：PRM 发现、AS 元数据、DCR、PKCE S256、回环回调（带 `iss` 和 `state`）、自定义 `ITokenCache` 持久化、令牌过期后自动刷新、重启后复用 refresh token 不再打开浏览器。
- 已知注意点：
  - v2.0 起会给 Roots / Sampling / Logging API 报 `MCP9005`，`AuthorizationRedirectDelegate` 报 `MCP9007`；TKCode 不使用这些 API。
  - 改用 `AuthorizationCallbackHandler`，它返回 `AuthorizationResult {Code, State, Iss}`。
