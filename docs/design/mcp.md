# 内置 Agent 的 MCP 支持

状态：M1–M8 已实现（2026-10-17），与设计的差异见 §16
关联文档：协议规格 [protocols/mcp.md](../protocols/mcp.md)、[builtin-agent-tools.md](builtin-agent-tools.md)、[external-agent-runtimes.md](external-agent-runtimes.md)、[settings.md](../settings.md)

## 1. 目标

- 内置 Agent 可以使用 MCP 服务器提供的工具，并支持在对话中引用资源、使用提示词，以及处理服务器发起的 elicitation（表单 / URL）。
- 客户端配置：设置页可以增删改服务器、查看状态和日志、登录 OAuth、逐个启用或停用工具。
- MCP JSON：可以从剪贴板或文件导入其他客户端的配置，自动识别格式；可以扫描本机已有的配置一键导入；可以把 TCode 的配置导出为通用格式。
- 外部运行时（Claude Agent / Codex / ACP）通过 TCode 桥接同样可以使用这些服务器。
- 整个客户端保持 NativeAOT，发布时 0 个 IL/AOT 警告。

## 2. 已确认的决策（用户，2026-10-16）

| 维度 | 决策 |
|---|---|
| 协议层 | 官方 `ModelContextProtocol.Core` 2.2.0（已用实测确认 AOT 0 警告，stdio、HTTP、SSE 回退和 OAuth 全部通过；见规格 §11） |
| 配置位置 | 全局放在 `~/.tcode/settings.json` 的 `mcpServers`；项目级放在仓库内的 `.mcp.json`，首次使用前需要用户确认信任 |
| 导入 | 粘贴 JSON 自动识别格式、扫描本机一键导入、从文件导入，另外支持导出 |
| 首版范围 | 工具、stdio / HTTP、状态显示（以上是必做项），以及 OAuth 登录、外部运行时透传、Resources / Prompts、Elicitation 表单 |
| 连接生命周期 | Host 内的共享连接池：首次被会话用到时连接，空闲后关闭，崩溃后按退避策略重连 |
| 工具列表变化 | 同一轮内不变；在下一批用户输入之前，追加一段工具节补丁并替换工具定义 |
| 审批 | Ask 档默认询问，`readOnlyHint` 不作为放行依据；可以选择“本会话允许”，也可以在设置中持久设为“始终允许”（按服务器或按工具） |
| 设置页 | 新增“MCP”分区，采用主从双栏表单，风格与“模型与厂商”页一致 |
| 外部运行时 | 走 TCode 桥接：只注入一个 stdio MCP 服务器（AgentHost 的 `--mcp-bridge` 模式），由它转发到 Host 的连接池 |

## 3. 架构

```
TCode.Desktop ──JSON-RPC──▶ TCode.AgentHost
   设置页 / 导入 / 资源与提示词选择器       ├─ McpManager（连接池，每项对应一个服务器连接）
   elicitation 与 OAuth 弹窗                  │    ├─ McpConnection ─ ModelContextProtocol.Core（stdio / HTTP / OAuth）
                                              │    └─ McpAuthStore / McpTrustStore（~/.tcode/mcp/）
                                              ├─ 内置会话：McpToolSet → McpTool : IAgentTool
                                              └─ McpBridgeServer（UDS）◀── tcode-agenthost --mcp-bridge（stdio MCP 服务器）
                                                                              ▲ 由 Claude Agent / Codex / ACP 启动
TCode.Config：McpServerConfig、宽松解析、变量展开、导入导出（McpImport）—— Host 与 Desktop 共用，不依赖 SDK
```

- 新库 `src/TCode.Mcp`（`IsAotCompatible`）依赖 `TCode.Agent`、`TCode.Config` 和 `ModelContextProtocol.Core`，包含：连接、工具适配、结果转换、OAuth 存储、桥接协议的 DTO。Host 引用它；Desktop **不**引用 SDK。
- 配置类型与导入解析放在 `TCode.Config`：纯 .NET，Desktop 预览导入时不需要调用 RPC。
- SDK 版本统一写在 `Directory.Packages.props` 中；升级时重跑 AOT 发布和自检。

## 4. 配置

### 4.1 settings.json

```jsonc
"mcpServers": {
  // stdio：有 command 即为 stdio
  "filesystem": {
    "command": "npx",
    "args": ["-y", "@modelcontextprotocol/server-filesystem", "${workspaceFolder}"],
    "env": { "LOG_LEVEL": "info" }
  },
  // 远程：有 url 即为远程；type 省略或为 http 时用 Streamable HTTP（不通时自动回退到 SSE），写 sse 则强制使用 SSE
  "github": {
    "url": "https://api.githubcopilot.com/mcp/",
    "headers": { "Authorization": "Bearer {env:GITHUB_TOKEN}" }
  },
  // OAuth：不写 headers 中的 Authorization 时，服务器返回 401 就进入登录流程
  "linear": { "url": "https://mcp.linear.app/mcp", "oauth": { "scopes": "read write" } },
  "sentry": { "url": "…", "enabled": false }
}
```

| 字段 | 类型 | 说明 |
|---|---|---|
| `command` / `args` / `env` / `cwd` | string / string[] / object / string | stdio。`cwd` 默认为会话的 cwd；`env` 叠加在继承来的环境变量之上 |
| `envFile` | string | stdio：额外加载一个 dotenv 文件（兼容 Cursor 和 VS Code 的写法） |
| `type` | `stdio` \| `http` \| `sse` | 可以省略，按 `command` / `url` 推断 |
| `url` / `headers` | string / object | 远程 |
| `oauth` | `{clientId?, clientSecret?, callbackPort?, scopes?}` | 预注册的客户端和固定的回调端口；不写时用 DCR，端口随机 |
| `enabled` | bool | 默认 `true` |
| `disabledTools` | string[] | 不暴露给模型的工具（MCP 原名） |
| `alwaysAllow` | string[] | Ask 档免审批的工具，`"*"` 表示整个服务器 |
| `startupTimeout` / `toolTimeout` | 秒 | 默认分别为 30 和 600 |
| `name` | string | 显示名（可选） |

**服务器键**：限定为 `[A-Za-z0-9_-]{1,32}`，同时用作工具名前缀（§5.1）。导入时会自动规整，例如把 `.` 和空格替换成 `-`。

**变量**：所有字符串字段都会展开。支持的写法有：
- `{env:VAR}`、`$VAR`（与厂商 Key 的写法一致）；
- 为了直接复用其他客户端的配置，也接受 `${VAR}`、`${VAR:-default}`、`${env:VAR}`；
- `${workspaceFolder}` / `{workspaceFolder}`：会话的 cwd；`${userHome}`：主目录。

变量未设置时展开为空串，并在设置页显示警告。

### 4.2 项目级 `.mcp.json`

- 文件位于项目根（git 根，没有 git 时为 cwd），格式与 Claude Code 的 `.mcp.json` 相同（顶层 `mcpServers`，变量写法为 `${VAR}`），可以和团队共享。
- **信任**：项目服务器在用户确认之前**不会启动**。
  - 信任记录保存在 `~/.tcode/mcp/trust.json`，键为“项目路径 + 服务器名 + 配置指纹”；配置改变后需要重新确认。
  - 设置页和会话中的提示条都提供“信任 / 忽略”操作。
- **同名冲突**：项目级配置覆盖全局配置，设置页会标出“已被项目配置覆盖”。

### 4.3 运行期状态

| 文件 | 内容 | 权限 |
|---|---|---|
| `~/.tcode/mcp/auth.json` | OAuth：按服务器的规范 URL 分组保存 `TokenContainer`（access/refresh token、client_id/secret、issuer） | 0600 |
| `~/.tcode/mcp/trust.json` | 项目服务器的信任记录 | 0600 |

## 5. 内置 Agent 的工具

### 5.1 命名

- 模型看到的名字为 `mcp__<服务器键>__<工具名>`。
- 工具名中不属于 `[A-Za-z0-9_-]` 的字符替换为 `_`。
- 总长度上限 64（OpenAI 的限制）。超出时截断工具名部分，再加上 `_<6 位 hash>`。
- 同一服务器内规整后的名字冲突时，同样追加 hash。
- `McpTool` 记录原始名，调用时使用原始名。

### 5.2 `McpTool : IAgentTool`

| 成员 | 取值 |
|---|---|
| `Description` | 工具的 `description`，超过 2 KB 时截断。`title` 不同于 `name` 时，以“title — description”的形式写在开头 |
| `ParametersSchemaJson` | `inputSchema` 原样传入，只做两处保底：根节点没有 `type` 时补上 `"type":"object"`；去掉根节点的 `$schema`（部分厂商会报错）。保持非 strict 模式（规格 openai-chat-completions §strict） |
| `Label` | `Tool.Mcp`（UI 再用服务器名和工具 title 拼出步骤行） |
| `Concurrency` | `Parallel` |
| `DescribePermission` | 新增 `ToolAccess.External`：`Title = "<服务器> · <工具 title>"`，`Domain = 服务器键`，`Command = 原始工具名`。没有路径时，不参与工作区判定 |
| `ExecuteAsync` | `CallToolAsync(原始名, args, progress, ct)`，超时取 `toolTimeout`。连接断开时等待重连，最多等到 `startupTimeout`，然后重试**一次**（协议无状态，见规格 §3.1） |

### 5.3 结果转换

| MCP 内容 | 转换为 |
|---|---|
| `text` | `TextContent` |
| `image` | 模型支持图片时转为 `ImageContent`，否则写一行 `[image: mimeType, N KB omitted]` |
| `audio` | `[audio: mimeType omitted]` |
| `resource_link` | `[resource] name <uri> (mimeType)` |
| `resource`（text） | `<resource uri="…">…</resource>` |
| `resource`（blob） | 图片按 image 处理；其余写一行占位 |
| `structuredContent` | 只有在没有 text 内容时，才把它序列化成 JSON 文本 |

- `isError: true` 转为 `AgentToolResult.IsError`。JSON-RPC 错误转为 `Error("MCP error <code>: <message>")`。
- **截断**：总文本超过 100 000 字符时，完整内容写入 `~/.tcode/tmp/<会话>/mcp-<调用 id>.txt`，结果中只保留开头部分，并提示用 `read` 读取全文（与 bash 的长输出处理方式一致）。
- **Details**（只给 UI）：`{server, tool, title, arguments, isError, structured?, blocks:[{type, mimeType?, uri?, bytes?}]}`。
- **进度**：`notifications/progress` 转为 `ToolExecutionUpdated`，显示为“message (progress/total)”。

### 5.4 工具集随轮次更新

- 改为由 `AgentSession` 组合工具集：`Agent.Tools` 由只读改为可替换，**只在 `ContextUpdates` 回调中替换**，也就是每批用户输入之前；同一轮内始终不变。
- `SystemPromptTracker` 新增 `mcp` 节，内容为已连接服务器的列表：“服务器名：工具数”，有 `instructions` 时附上（每个服务器截断到 2 KB）。连接的服务器或工具集有变化时，追加一条 `SystemMessage` 补丁，复用现有的分节补丁机制。
- **第一轮**：会话创建时就开始连接（不阻塞）。第一条 prompt 发出前，最多等待 `startupTimeout`，同时显示“正在连接 MCP…”。超时的服务器不进入首轮的工具集，之后连上了，从下一轮开始生效。
- **变化检测**：每批用户输入之前比较 `(服务器, 工具名, schema hash, 描述 hash)` 的集合。下列情况会重新执行 `tools/list`：收到 `list_changed` 通知，或者缓存超过 `ttlMs`（没有 TTL 时每轮都取，但同一轮内只取一次）。
- `disabledTools` 中的工具和被 SDK 按 `x-mcp-header` 规则剔除的工具，都不进入工具集。
- **子 Agent**：继承父会话的 MCP 工具，共用同一个连接租约；审批规则与父会话相同。
- **工具数量**：设置页显示每个服务器的工具数和 schema 体积估算（按约 4 字符 1 token 计）。总数超过 80，或 schema 超过上下文的 10% 时，给出警告并建议停用部分工具。首版不做延迟加载。

## 6. 连接池（McpManager）

- **键**：
  - stdio：`服务器键 + 展开后配置的指纹`。配置中用到了 `${workspaceFolder}`，或者没写 `cwd` 时，指纹还包含会话的 cwd；
  - HTTP：`服务器键 + URL + headers 指纹`，与 cwd 无关。
- **租约**：会话创建时调用 `Acquire(cwd)`，得到它可用的连接集合；会话关闭时释放。没有租约 5 分钟后关闭连接：stdio 先关闭 stdin，再按 `ShutdownTimeout` 强制结束。
- **状态**：
  - `Disabled`：配置中关闭；
  - `Untrusted`：项目服务器尚未被信任；
  - `Connecting`；
  - `Connected`：附带工具、资源、提示词的数量，以及协商出的版本；
  - `NeedsAuth`；
  - `Failed`：附带错误信息，以及 stderr 的最后几行。
- **重连**：stdio 进程意外退出，或 HTTP 连接失败时，按 1s、2s、4s… 退避，最长 60s，连续失败 5 次后置为 `Failed`，等用户点击“重试”。会话不必等待重连：工具调用时连接不可用，就返回错误交给模型。
- **stderr**：每个连接保留最近 1000 行的环形缓冲，可以在设置页查看。
- **热重载**：`SettingsWatcher` 发现 `mcpServers` 有变化时，配置变了的连接关闭后重建，新增的服务器延迟到下次被用到时再连接，删除的服务器立即关闭。正在进行的工具调用不中断。项目 `.mcp.json` 由一个 FileSystemWatcher 按项目监听。
- **初始化**：`McpClientOptions { ClientInfo = {name:"TCode", version}, Capabilities = { Elicitation = {Form, Url} }, InitializationTimeout = startupTimeout }`。连上之后取 `tools/list`；服务器声明了相应能力时，再取 `prompts/list` 和 `resources/list`。
- **订阅变化**：服务器声明了 `listChanged` 时，modern 服务器发 `subscriptions/listen`（规格 §4.3，SDK 没有封装，用 `SendRequestAsync` 自己发），legacy 服务器直接注册通知处理器；不支持时只靠 TTL。

## 7. 审批

`PermissionPolicy` 新增 `ToolAccess.External` 分支：

| 档位 | 行为 |
|---|---|
| FullAccess / AutoApprove | 放行 |
| Ask | 命中 `alwaysAllow`（工具名或 `*`）就放行；命中本会话允许的键 `mcp:<服务器>/<工具>` 或 `mcp:<服务器>/*` 也放行；其余询问 |

- 审批弹窗显示服务器、工具 title 和完整参数（JSON 预览），以及工具的 annotations（只作提示，例如“服务器声明：只读”）。
- 弹窗选项：允许一次 / 本会话允许此工具 / 本会话允许此服务器 / **始终允许此工具**（写入 settings 的 `alwaysAllow`）/ 拒绝。
- 为此，`ToolApprovalRequest` 新增 `Scopes`（可选的“本会话允许”范围列表）和 `CanAllowAlways`；`ToolApprovalResponse` 新增 `Scope` 和 `Always` 字段。RPC 协议版本 +1。
- 子 Agent 的审批同样以父会话的名义发出（现有机制）。

## 8. Resources、Prompts 与 Elicitation

### 8.1 资源：@ 选择器

- 现在的 Composer 还没有 @ 选择器，首版新增：输入 `@` 时弹出可搜索的弹层，列出当前会话可用服务器的资源，按服务器分组。资源模板暂不支持参数填写，也不出现在列表中。
- 选中的资源显示为 Composer 中的一枚资源胶囊。发送时，Host 调用 `resources/read`：
  - 文本资源作为 `<mcp_resource server="…" uri="…">…</mcp_resource>` 文本块附在用户消息中；
  - 图片资源附为 `ImageContent`；
  - 超过 100 000 字符的内容截断，并注明已截断。
- RPC：`mcp/listResources(sessionId)` 和 `mcp/readResource`（由 `session/prompt` 携带资源引用、在 Host 内读取，Desktop 不经手内容）。

### 8.2 提示词：斜杠命令

- Composer 新增 `/` 命令弹层（首版只包含 MCP 提示词，以后可以加入内置命令），命令名为 `/<服务器>:<提示词>`。
- 提示词有参数时，在 Composer 上方展开参数表单（必填项带标记）。
- 提交后，Host 调用 `prompts/get`，结果按顺序转为一条用户消息：`user` 消息作为正文，`assistant` 消息用“[assistant] …”标注后并入正文，资源与图片按 §8.1 处理。
- RPC：`mcp/listPrompts(sessionId)`；`session/prompt` 携带提示词引用和参数，由 Host 展开。

### 8.3 Elicitation

- SDK 的 `ElicitationHandler` 收到请求后，Host 调用 Host→UI 的新请求 `mcp/elicit {sessionId?, server, mode, message, schema?, url?}`，返回 `{action, content?}`。
- 弹窗用审批面板的样式，标题为“<服务器> 请求信息”：
  - **form**：按规格 §5.1 渲染受限 schema，包括字符串（含 format 校验）、数字、布尔、单选和多选枚举，默认值预先填好。用户可以接受、拒绝或取消。不支持的 schema 只显示原文，并只允许拒绝。
  - **url**：显示完整 URL 并突出显示域名，Punycode 给出警告。只有用户点击“打开”后才用系统浏览器打开，返回 `accept`。
- 没有 UI 连接，或者请求所属的工具调用被中止时，返回 `cancel`。
- elicitation 来自连接池中的共享连接，但同一个请求只属于一次工具调用：Host 用 AsyncLocal 记下当前的会话 id，弹窗显示在对应的会话中；找不到会话时显示为全局弹窗。

## 9. OAuth

- **触发**：建立连接时不弹出浏览器。服务器返回 401 时，连接状态置为 `NeedsAuth`。用户在设置页或会话提示条中点击“登录”后，再发起交互式授权。
- **流程**：交给 SDK 的 `ClientOAuthOptions`（规格 §8）：
  - `RedirectUri = http://127.0.0.1:<端口>/callback`，端口取 `oauth.callbackPort`，没写时随机；
  - `DynamicClientRegistration.ClientName = "TCode"`；
  - `ClientId` / `ClientSecret` 来自配置；
  - `TokenCache` 由 `McpAuthStore` 实现，按服务器 URL 保存。
- **AuthorizationCallbackHandler**：Host 启动回环 `HttpListener`，再通过 Host→UI 的新请求 `mcp/openUrl` 让 Desktop 用系统浏览器打开授权 URL。之后等待回调（最长 5 分钟，用户可以取消），回调页显示“已完成，可以关闭此页”。最后返回 `{Code, State, Iss}`，`iss` 和 `state` 的校验由 SDK 完成。
- **登出**：删除该服务器的令牌，连接重建后状态变为 `NeedsAuth`。
- **静态 Authorization 头**：配置了这个头的服务器不走 OAuth。头部引用的令牌失效时，状态显示为 `Failed`（401），不提示登录。

## 10. 外部运行时：MCP 桥接

- AgentHost 新增启动模式 `--mcp-bridge`：作为 stdio MCP 服务器运行（使用 SDK 的服务器 API 和 stdio 服务器传输），通过 UDS 连接 Host 的 `McpBridgeServer`，把请求转发过去：
  - `tools/list` 返回该会话可用的全部 MCP 工具，名字为 `<服务器键>__<工具名>`，按 §5.1 规整，上限 64；
  - `tools/call` 转发给对应的连接；
  - 进度、elicitation 和 list_changed 原样透传。
- **身份**：Host 为每个外部会话生成一个随机令牌，放在环境变量 `TCODE_MCP_BRIDGE_TOKEN` 中，socket 路径放在 `TCODE_MCP_BRIDGE_SOCKET` 中（都不写进命令行参数）。桥接进程用令牌换取会话身份，未知令牌直接拒绝。
- **注入方式**（服务器名统一为 `tcode`）：
  - Claude Agent：`--mcp-config` 指向临时文件 `~/.tcode/tmp/<会话>/mcp.json`（0600，内容是 `{"mcpServers":{"tcode":{"command":"<agenthost>","args":["--mcp-bridge"],"env":{…}}}}`）；
  - Codex：`thread/start` 的 `config` 中写 `mcp_servers.tcode`，值为 `{command, args, env}`；
  - ACP：`session/new.mcpServers` 写成 `[{name:"tcode", command, args, env:[{name, value}]}]`。stdio 是 ACP 的基线能力，不依赖 `mcpCapabilities.http`。
- **审批**：
  - 桥接转发前，在 Host 侧按 §7 的策略判定（当前权限档位取自该外部会话），需要时询问；
  - 运行时自己的审批尽量放行 `tcode` 服务器，避免同一调用问两次：Claude 加 `--allowedTools mcp__tcode__*`；Codex 和 ACP 的做法待核实，查不到配置项时接受重复询问，并记录在案。
- **没有配置任何 MCP 服务器时不注入**桥接，外部运行时的行为与现在完全一样。
- `ToolPresentation`：把 `mcp__tcode__<服务器>__<工具>`（Claude）、Codex 的 `mcp__tcode__…` 和 ACP 的同类名字归为 MCP 类别，显示为“<服务器> · <工具>”。

## 11. 设置页“MCP”

- 左栏：
  - 搜索；
  - “全局”组和“项目：<当前项目名>”组；
  - 每行显示状态点（绿 Connected / 黄 Connecting / 灰 Disabled / 橙 NeedsAuth 或 Untrusted / 红 Failed）、名称、工具数和启用开关；
  - 底部有“添加服务器”“导入…”“导出”三个按钮。
- 右栏的详情：
  - **连接**：类型切换（本地命令 / 远程 URL）。本地命令填写命令、参数、工作目录、环境变量（键值表，值可以是 `{env:}` 引用，敏感值遮罩显示）；远程 URL 填写 URL 和请求头。另有“保存”和“测试连接”：测试会临时建立连接并执行 tools/list，不写文件。
  - **状态**：协商出的版本、服务器名和版本、`instructions`。状态为 `NeedsAuth` 时显示“登录”，已登录时显示“登出”；另有“重启”。
  - **工具**：逐个启用或停用的开关、title、描述（可展开查看原文）、annotations 标签（只读 / 破坏性 / 开放世界）、“始终允许”开关，以及 schema 体积。
  - **资源 / 提示词**：只读列表。
  - **日志**：stderr 环形缓冲，以及连接错误。
- 项目服务器显示“来自 .mcp.json”，编辑时写回该文件；尚未信任的项目服务器显示“信任 / 忽略”。
- **写回**：全局配置通过 `SettingsFile.Update` 按节点写回（会丢失注释，现有做法）；项目配置写回 `.mcp.json`。写完后调用 `ReloadSettingsAsync`。
- **会话提示**：会话中有服务器处于 `NeedsAuth` 或 `Untrusted` 状态时，Composer 上方显示一条可关闭的提示条，带“登录 / 信任”按钮。

## 12. 导入与导出

### 12.1 识别（`McpImport.Parse(text, hint?)`，放在 TCode.Config 中）

依次尝试以下格式：
1. **TOML**：出现 `[mcp_servers.` 时，按 Codex 格式解析。只实现 TOML 子集：表、点号键、字符串、字面量字符串、多行字符串、数组、内联表、布尔、整数。
2. **JSON / JSONC**：
   - 有 `mcpServers` 键：Claude、Cursor、Windsurf、Copilot CLI、Kiro、Cline 等的格式；
   - 有 `servers` 键（可能同时有 `inputs`）：VS Code；
   - 有 `mcp` 键，值是对象，且其中的条目带 `type: "local" | "remote"`：opencode；
   - 有 `context_servers` 键：Zed；
   - 有 `projects.*.mcpServers`：`~/.claude.json`，同时导入用户级和指定项目的本地级；
   - 是单个服务器对象（带 `command` 或 `url` / `serverUrl` / `httpUrl`）：要求用户填写名字；
   - 是裸的条目片段，形如 `"name": {...}`（复制时少了外层括号）：自动补上 `{}` 后重试。

### 12.2 字段映射（统一转为 `McpServerConfig`）

- **传输类型**：
  - `command` 为字符串数组时（opencode、Zed 新格式），第一项作为 command，其余作为 args；
  - `type: "local"` 映射为 stdio；
  - `serverUrl`（Windsurf）和 `httpUrl`（Gemini）映射为 `url` 加 `type: http`；Gemini 的 `url` 映射为 `type: sse`。
- **变量**：
  - `${env:X}`、`${X}`、`${X:-d}` 原样保留（TCode 能展开它们）；`{env:X}`（opencode）同样保留；
  - `${input:id}`（VS Code）在导入预览中列为待填项，用户填写明文值或环境变量名；
  - `${file:path}`（Windsurf）和 `{file:path}`（opencode）在预览中提示，可以展开为文件内容，或者改为环境变量。
- **Codex 专有字段**：
  - `bearer_token_env_var` 映射为 `headers.Authorization = "Bearer {env:VAR}"`；
  - `env_http_headers` 映射为 `headers.<H> = "{env:VAR}"`；
  - `enabled_tools` / `disabled_tools` 映射为 `disabledTools`（`enabled_tools` 需要连接后才能求补集，因此只在预览中提示）；
  - `startup_timeout_sec` / `tool_timeout_sec` 映射为对应的超时。
- **自动批准类字段**：`autoApprove` / `alwaysAllow`（Cline、Kiro、Roo）映射为 `alwaysAllow`，但**默认不勾选**，需要用户在预览中确认后才导入。
- `disabled: true` 映射为 `enabled: false`；`disabledTools` 原样保留。
- 不认识的字段丢弃，并在预览中列出（“以下字段不会导入”）。

### 12.3 扫描本机（`McpImport.Discover()`）

只读取已知路径（下表），不执行任何程序：

| 来源 | macOS | Windows | Linux |
|---|---|---|---|
| Claude Desktop | `~/Library/Application Support/Claude/claude_desktop_config.json` | `%APPDATA%\Claude\claude_desktop_config.json` | `~/.config/Claude/claude_desktop_config.json` |
| Claude Code | `~/.claude.json`（用户级和当前项目），`<项目>/.mcp.json` | 同左 | 同左 |
| Cursor | `~/.cursor/mcp.json`，`<项目>/.cursor/mcp.json` | 同左 | 同左 |
| VS Code | `~/Library/Application Support/Code/User/mcp.json`，`<项目>/.vscode/mcp.json` | `%APPDATA%\Code\User\mcp.json` | `~/.config/Code/User/mcp.json` |
| Windsurf | `~/.codeium/windsurf/mcp_config.json`，`~/.config/devin/mcp_config.json` | `%USERPROFILE%\.codeium\windsurf\…`，`%APPDATA%\devin\…` | 同 macOS |
| Codex | `~/.codex/config.toml`（以及 `CODEX_HOME`） | 同左 | 同左 |
| Gemini CLI | `~/.gemini/settings.json`，`<项目>/.gemini/settings.json` | 同左 | 同左 |
| opencode | `~/.config/opencode/opencode.json(c)`，`<项目>/opencode.json(c)` | 同左 | 同左 |
| Copilot CLI | `~/.copilot/mcp-config.json` | 同左 | 同左 |
| Zed | `~/.config/zed/settings.json` | `%APPDATA%\Zed\settings.json` | `~/.config/zed/settings.json` |
| LM Studio | `~/.lmstudio/mcp.json` | 同左 | 同左 |

扫描结果按来源分组，与已有配置相同的条目标记为“已存在”。

### 12.4 导入预览（共用一个对话框）

- 三个入口共用这个对话框：粘贴、选择文件、扫描本机。
- 每个条目可以勾选，显示名字（可以改名；与已有服务器重名时，默认改为 `name-2`，也可以选择覆盖）、类型、命令或 URL。
- 列出待填变量、被丢弃的字段和安全提示，例如“将在本机执行 `npx …`”。
- 目标可以选“全局（settings.json）”或“当前项目（.mcp.json）”。
- 导入**不会**自动信任或启动任何服务器：写入全局配置是用户自己的操作，视为已信任；写入项目配置也视为已信任，但会记录信任状态。

### 12.5 导出

- 可以选全部服务器或部分服务器，导出为通用格式 `{"mcpServers": {...}}`。stdio 条目带 `command`、`args`、`env`；远程条目带 `type`、`url`、`headers`。
- 默认原样保留 `{env:}` 等引用；也可以选择“展开为当前值”，此时会警告内容中包含密钥。
- 可以复制到剪贴板或保存为文件。

## 13. RPC 变化（协议版本 +1）

| 方向 | 方法 | 说明 |
|---|---|---|
| UI → Host | `mcp/list(cwd?)` | 服务器、状态、来源、工具、提示词和资源的数量，以及最近的错误 |
| UI → Host | `mcp/tools(server, cwd?)`、`mcp/log(server, cwd?)` | 详情页用的数据 |
| UI → Host | `mcp/test(config, cwd)` | 用未保存的配置临时连接并执行 tools/list |
| UI → Host | `mcp/restart`、`mcp/login`、`mcp/logout`、`mcp/trust(cwd, server, trusted)` | 操作 |
| UI → Host | `mcp/listResources(sessionId)`、`mcp/listPrompts(sessionId)` | Composer 的选择器 |
| UI → Host | `session/prompt` | `PromptRequest` 增加 `McpResources[]`、`McpPrompt?` |
| Host → UI | `mcp/changed`（notification） | 状态变化 |
| Host → UI | `mcp/elicit`、`mcp/openUrl`（request） | elicitation、OAuth 浏览器 |
| Host → UI | `tool/approve` | `ToolApprovalRequest` 增加 `Scopes`、`CanAllowAlways` |

## 14. 实施阶段

| 阶段 | 内容 | 验证 |
|---|---|---|
| M1 配置 | `TCode.Config` 中的 `McpServerConfig`、宽松解析、变量展开、settings schema 和 settings.md | 单元测试 |
| M2 连接与工具 | `TCode.Mcp`：McpManager、stdio 与 HTTP、状态、重连、stderr、`McpTool`、结果转换、截断、进度；`ToolAccess.External` 审批；`Agent.Tools` 可替换，以及 `mcp` 节补丁 | 测试用的 MCP 服务器（AOT，SDK 服务器端，放在 tests 中）跑端到端；**AOT 发布 0 警告，并在 AOT Host 中实际调用 MCP 工具** |
| M3 设置页 | MCP 分区（列表、详情、测试连接、工具开关、日志）和 RPC | headless 截图测试 |
| M4 导入导出 | `McpImport`：所有格式、TOML 子集、扫描、预览对话框、导出 | 每种格式一个夹具测试；截图测试 |
| M5 OAuth | AuthStore、回环监听、`mcp/openUrl`、登录与登出、NeedsAuth 提示条 | 测试 AS / RS（参考规格 §11 中实测用的服务器）端到端 |
| M6 项目配置与信任 | `.mcp.json`、TrustStore、提示条、写回 | 单元测试和截图测试 |
| M7 Elicitation、Resources、Prompts | `mcp/elicit` 弹窗、@ 资源选择器、/ 提示词命令 | 端到端测试和截图测试 |
| M8 外部运行时桥接 | `--mcp-bridge`、`McpBridgeServer`、三种运行时注入、审批与命名展示 | 用桩运行时测试桥接；用真实 CLI 跑一轮前**先征得用户同意** |

每个阶段完成后：AOT 发布 Desktop 和 Host，确认 0 个 IL/AOT 警告，并运行 Host 自检（自检中加入一次 MCP stdio 往返）。

## 15. 风险

| 风险 | 对策 |
|---|---|
| GUI 启动的应用拿不到 shell 的 PATH（macOS 从 Finder 启动），找不到 `npx` 或 `uvx` | 复用 `TCode.Runtimes` 的 `ExecutableLocator`（它已经处理了登录 shell 的 PATH）来解析 command；Windows 上的 `npx` / `.cmd` 用 `cmd /c` 启动 |
| SDK 2.x 还在快速迭代（2.0 有破坏性变更） | 固定版本号；升级前重跑 AOT 发布、自检和端到端测试 |
| `subscriptions/listen` 要自己发，而且 HTTP 下的通知分发未经验证 | 失败时退回到 TTL 和每轮检查（§5.4），不影响正确性 |
| 工具太多导致上下文膨胀 | 显示体积与警告，提供逐个工具的开关；以后再考虑延迟加载 |
| 工具描述和结果中的提示注入 | 默认需要审批，结果截断，可以查看描述原文 |
| 外部运行时重复审批 | 见 §10 |

## 16. 实现状态（2026-10-17）

| 阶段 | 实现位置 | 验证 |
|---|---|---|
| M1 配置 | `TCode.Config/McpServerConfig.cs`、`McpVariables.cs`（变量展开、dotenv、项目 `.mcp.json`）、`settings.schema.json` | `McpConfigTests` |
| M2 连接与工具 | `TCode.Mcp/McpConnection.cs`、`McpManager.cs`（连接池与 `McpSessionTools`）、`McpTool.cs`、`McpResultConverter.cs`、`McpToolNames.cs`；`Agent.Tools` / `SystemPrompt` 可在 `ContextUpdates` 中替换；`ToolAccess.External` 审批 | `McpConnectionTests`、`McpSessionTests`；AOT Host `--self-test`（设置 `TCODE_SELFTEST_MCP`）中实际执行 tools/list 与 tools/call |
| M3 设置页 | `McpSettingsView`、`McpSettingsViewModel`；RPC `mcp/list\|tools\|log\|test\|restart\|logout\|trust`、`mcp/changed`（协议版本 14） | `McpSettingsSnapshotTests` |
| M4 导入导出 | `TCode.Config/McpImport.cs`、`McpToml.cs`；导入、导出对话框 | `McpImportTests`（覆盖每种格式）、截图测试 |
| M5 OAuth | `TCode.Mcp/McpOAuth.cs`（`McpAuthStore`、回环回调）；`mcp/openUrl` | `McpHttpOAuthTests`：用模拟授权服务器跑完 PRM、DCR、PKCE、iss、刷新、登出全流程；确认后台连接不做动态注册 |
| M6 项目配置与信任 | `McpTrustStore`；设置页和 Composer 上方的“信任 / 忽略”提示 | `McpConnectionTests.ProjectServersRequireTrust`、`McpConversationSnapshotTests` |
| M7 Elicitation、资源、提示词 | `McpElicitationView`；`McpComposerViewModel`（@ 与 /）；`McpContext`；`mcp/elicit`、`mcp/catalog`；`PromptRequest.McpResources\|McpPrompt` | `McpContextTests`、`McpConversationSnapshotTests`（审批、表单、资源、提示词端到端） |
| M8 外部运行时桥接 | `AgentHost/McpBridge.cs`（`--mcp-bridge` 与 UDS 服务端）；`McpBridgeLaunch` 注入 Claude、Codex、ACP | `McpBridgeTests`（包括用 AOT Host 作为桥接进程）、`McpBridgeInjectionTests` |

AOT（osx-arm64）：Desktop 与 Host 发布均为 0 个 IL/AOT 警告。Host 约 26.9 MB，比加入 MCP 前多约 9 MB（SDK、M.E.AI.Abstractions，以及桥接用的服务端）。

**与设计的差异与待办**：
- **审批选项**：沿用现有审批卡片，只有“允许一次 / 本会话允许此工具 / 拒绝”。“本会话允许此服务器”和卡片上的“始终允许”尚未加入；“始终允许”目前在设置页的工具列表中勾选。
- **subscriptions/listen**：未实现。工具列表变化靠两种方式发现：legacy 服务器的 `list_changed` 通知，以及每轮开始前按 TTL 重新执行 tools/list（没有 TTL 的服务器每轮都会重新获取）。
- **桥接**：不转发进度通知；tools/list_changed 也不转发，运行时下次执行 tools/list 时才看到新工具。Codex 和 ACP Agent 对 `tcode` 服务器的工具是否也会各自审批，尚未在真实 CLI 上核实，可能出现重复询问；Claude 已用 `--allowedTools=mcp__tcode` 放行。
- **工具数量警告**：设置页显示每个工具的 schema 体积，没有“超过 80 个工具”的全局警告。
- **资源模板**：@ 选择器只列出 `resources/list` 的结果，不支持带参数的资源模板。
- **桌面端**的 Fake 模型在用户消息为 `mcp:<工具名> <JSON 参数>` 时直接调用该工具，仅供开发和测试。
