# TCode 架构设计

TCode 是基于 Avalonia + AtomUI 的桌面 AI Agent 客户端。视觉最初参考 Cursor 的 Agents 窗口；2026-10-16 起改为以项目 / 会话为中心的暗色风格（居中 Composer、径向光晕背景、单一紫色强调色、可搜索弹层、⌘K 命令面板），界面中英双语。

## 1. 已确定的技术决策

| 维度 | 决策 | 说明 |
|---|---|---|
| UI 组件库 | AtomUI.Desktop.Controls 6.2.5（NuGet） | 不引用 `../AtomUI` 源码；Avalonia 版本跟随 AtomUI（12.1.3），.NET 10 |
| 协议层 | 自研统一协议层，适配器直接基于 `HttpClient` + `SseParser` | 每种线协议一个适配器：Chat Completions / Responses / Messages；厂商差异用兼容配置描述 |
| Agent 运行时 | pi 式分层 + **独立 Agent Host 进程** | UI 与 Host 通过 stdio 上的 JSON-RPC 2.0 通信（自研 `JsonRpcConnection`，NativeAOT 安全） |
| UI 定制 | 主题 Token + 定向覆盖 | 自定义主题 XML → 少量 ControlTheme → TCode 复合控件 |
| MVVM / DI | CommunityToolkit.Mvvm + Microsoft.Extensions.Hosting | ReactiveUI 只是 AtomUI 的传递依赖，未启用 |
| 窗口 | Cursor 式融合标题栏 | macOS 红绿灯嵌入侧栏顶部；Windows/Linux 自绘窗口按钮 |
| 主题 | 暗色 + 亮色，支持跟随系统 | `TCodeDark` / `TCodeLight` 两套主题定义 |
| 会话存储 | append-only JSONL 树 | 与 pi 的会话格式思路一致 |

## 2. 工程结构与依赖方向

```
TCode.Desktop ──(JSON-RPC over stdio)──▶ TCode.AgentHost
     │                                         │
     └──▶ TCode.Rpc.Contracts ◀────────────────┤
                                               ▼
                                     TCode.Agent.Coding   会话、工具、系统提示、AGENTS.md
                                               ▼
                                         TCode.Agent      Agent 循环、工具契约、事件
                                               ▼
                                          TCode.Ai        统一消息模型、流式事件、协议适配器
```

依赖严格单向；`TCode.Ai` / `TCode.Agent` / `TCode.Agent.Coding` 不依赖任何 UI，并标记 `IsAotCompatible`。

| 项目 | 对应 pi | 职责 |
|---|---|---|
| `TCode.Ai` | pi-ai | `Message`/`ContentBlock` 统一模型；`LlmEvent` 流式事件；`IWireApi` 适配器契约与 `WireApiRegistry`；`FakeWireApi` |
| `TCode.Agent` | pi-agent-core | `Agent` 循环；`IAgentTool`；`AgentEvent`；steer / followUp 队列；`BeforeToolCall` 审批钩子 |
| `TCode.Agent.Coding` | pi-coding-agent | `AgentSession`；`SessionStore`（JSONL）；按节的系统提示；工作区与审批策略；内置工具（文件、搜索、bash/后台任务、任务清单、网页、子 Agent 与消息，见 [builtin-agent-tools.md](design/builtin-agent-tools.md)） |
| `TCode.Rpc.Contracts` | pi RPC 模式 | `IAgentHostServer`（UI→Host）、`IAgentHostClient`（Host→UI）、只含增量的 `SessionEvent` |
| `TCode.AgentHost` | — | 独立进程，托管多个会话，把 Agent 事件翻译为 RPC 通知 |
| `TCode.Mcp` | — | MCP 客户端（官方 C# SDK）：Host 内的共享连接池、把 MCP 工具适配为 `IAgentTool`、OAuth、资源与提示词；设计见 [design/mcp.md](design/mcp.md) |
| `TCode.Desktop` | — | Avalonia 应用：主题、窗口、视图与 ViewModel、Host 连接 |

## 3. 协议层（TCode.Ai）

### 3.1 统一消息模型

- 消息：`SystemMessage`、`UserMessage`、`AssistantMessage`、`ToolResultMessage`。
- 内容块：`TextContent`、`ThinkingContent`（含 `Signature` / `Redacted`）、`ImageContent`、`ToolCallContent`。
- 厂商的不透明数据**原样保存**，只在同一 (provider, api, model) 下回放：
  - Anthropic 的 thinking signature；
  - Responses 的 reasoning item（含 `encrypted_content`）和消息 `phase`；
  - 兼容厂商的 `reasoning_content` / `reasoning_details`。
- `Usage` 归一化：`Input` 不含缓存读写。OpenAI 的 `prompt_tokens` 要扣除缓存部分。

### 3.2 流式事件约定

```
Start → (Text|Thinking|ToolCall)Start → *Delta → *End … → Done | Error
```

- 不同内容块的事件可能交错，消费方按 `ContentIndex` 区分。
- **流永不抛异常**：失败与中止都以 `ErrorEvent` 收尾，`StopReason` 为 `Error` / `Aborted`，已生成的部分内容保留。
- `AssistantMessageStream` = Channel + `Task<AssistantMessage> Result`；适配器使用 `AssistantMessageBuilder` 推送事件。

### 3.3 必须遵守的协议约束（来自调研）

1. **历史只追加**：Anthropic 新模型的 preserved thinking 会校验历史前缀，修改历史会使后续思考块失效甚至返回 400。
   - 系统提示和工具集在会话开始时冻结；
   - 变化通过追加系统消息表达；
   - 压缩采用"摘要 + 新一轮"的简单形态。
2. **思考 / 推理原样回放**：
   - Anthropic：thinking 块带签名；
   - Responses：`store:false`，回放 reasoning item；
   - DeepSeek V4、Kimi 等：带工具时必须回传 `reasoning_content`。
3. **不依赖强制 `tool_choice`**：最新 Claude 模型不支持 `any` / `tool`。
4. **思考深度统一抽象为 `ThinkingLevel`**，由各适配器映射：
   - Anthropic：adaptive + `output_config.effort`；
   - Chat Completions：`reasoning_effort`；
   - Responses：`reasoning.effort`。
5. **工具参数片段流式透传**（`ToolCallDeltaEvent`），用于实时预览编辑和 diff。
6. **截断的工具调用不执行**：`StopReason == Length` 时返回错误结果，让模型重新发起。

### 3.4 协议适配器与规格

每个适配器都严格按 `docs/protocols/` 下的规格实现。规格基于官方文档和官方 SDK 生成的类型整理，每条都标注了来源。测试用的 SSE 夹具按规格中的事件序列构造，不是录制的真实流量。

| 适配器 | 规格 | 要点 |
|---|---|---|
| `OpenAIChatCompletionsApi` | [openai-chat-completions.md](protocols/openai-chat-completions.md) | 兼容厂商差异由 `CompatResolver.ResolveChat` 按 BaseUrl 推导默认值，可被模型配置覆盖；推理字段按厂商回传（`reasoning_content` / OpenRouter `reasoning_details`）；`length` 优先判定，截断的工具调用不执行 |
| `OpenAIResponsesApi` | [openai-responses.md](protocols/openai-responses.md) | 无状态：`store:false`；`output_item.done` 的原始 item 原样回放；末尾孤立的 reasoning 不回放 |
| `AnthropicMessagesApi` | [anthropic-messages.md](protocols/anthropic-messages.md) | thinking / effort 按模型族配置（`CompatResolver.ClaudeFamily`）；`tool_choice` 只用 `auto`；同角色合并且 `tool_result` 在前；第三方端点不发送 beta、缓存等 Anthropic 专有字段 |

公共基础设施在 `HttpWireApi`：
- 首字节之前，对 408、409、429（额度类除外）、5xx 和连接错误重试，遵守 `x-should-retry`、`retry-after-ms`、`retry-after`。
- 两次事件之间设空闲超时。
- 所有失败都编码进消息，不抛异常。

`ThinkingLevels.ChooseEffort` 按模型支持的 effort 取值就近选值。关闭思考在模型支持时发 `none`，否则取最低档；不靠省略字段表达关闭。

### 3.5 模型与 API Key 配置

完整规格见 [settings.md](settings.md)。要点：

- **配置文件**：`~/.tcode/settings.json`（JSONC，带 JSON Schema），可以手工编辑，也可以通过设置页修改。程序自己记录的状态放在 `state.json`。
- **内置目录**：裁剪后的 models.dev 快照（`tools/gen_catalog.py` → `TCode.AgentHost/Catalog/catalog.json`），再叠加 TCode 补充（`BuiltInCatalog`）。
  - 内置厂商只需要一个 Key，也可以不写，直接读取目录登记的环境变量；
  - 自定义厂商的模型只需要写 id。
- **Key 的写法**：明文、`{env:VAR}` 或 `$VAR`。界面保存后，文件权限设为 0600。
- **热重载**：Host 的 `SettingsWatcher` 以 500ms 防抖重新加载，并推送 `models/changed`；文件有错误时保留旧配置，并在设置页显示错误。
- **模型引用**：RPC 中一律使用 `厂商/模型` 形式的 Ref，避免不同厂商的同名模型冲突。
- **共享代码**：`TCode.Config` 提供配置类型、宽松解析、按节点写回和 Key 解析，Host 和桌面端共用。

## 4. Agent 运行时（TCode.Agent）

```
PromptAsync → AgentStart
  └ 循环：追加待处理消息 → TurnStart → 流式调用模型 → 追加助手消息
          ├ 有工具调用：先逐个预检（含审批），再并行执行 → 按源顺序追加结果
          ├ TurnEnd；取 steer 队列；有工具调用或 steer 就继续下一轮
  └ 即将停止时取 followUp 队列；为空则结束 → AgentEnd
```

- **监听器按订阅顺序 await**。`AgentSession` 的持久化监听器最先订阅，所以 `MessageEnd` 写盘后才会进入工具执行（屏障语义）。
- **中止**：`Abort()` 取消本轮的 `CancellationToken`。流以 `Aborted` 收尾，未开始的工具返回 "Operation aborted"。
- **工具并发**：预检（含审批）串行；执行时，批内有 `Exclusive` 工具就整批串行，否则并行。结果按源顺序追加。进度事件按报告顺序送达，并且先于结束事件。
- **审批**：`BeforeToolCall` 钩子收到工具描述的 `ToolPermission`。Host 按 `PermissionPolicy` 判定：工作区内的只读操作和只读命令放行，其余按权限档位决定是否通过 `tool/approve` 询问 UI。详见 [builtin-agent-tools.md](design/builtin-agent-tools.md) §10。
- **系统提示**：按节构建，开头的版本冻结。每批用户输入之前，如果某节有变化，就追加一条 `SystemMessage` 补丁。快照记录在 JSONL 的 `systemPrompt` 条目里，恢复会话时以它为准。

## 5. 进程模型与 RPC

- UI 按需启动 `agenthost/TCode.AgentHost` 子进程：构建时由 `CopyAgentHost` 目标复制到桌面端输出目录；发布时由 `PublishAgentHost` 目标以相同运行时标识 AOT 发布到 `agenthost/`。
- **RPC 实现**（`TCode.Rpc.Contracts`）：自研的精简 JSON-RPC 2.0，替换了 StreamJsonRpc，因为 StreamJsonRpc 的 `Attach<T>` 在 NativeAOT 下启动即抛出 `CreateProxy is not supported`。
  - `JsonRpcConnection`：Content-Length 分帧；发送走单写者队列，保证顺序；接收时，通知在读循环里按顺序分发，请求并发执行。
  - 取消：本端取消调用时发送 `$/cancelRequest`，远端取消对应的处理器并回复 -32800。
  - 错误：沿用标准错误码，远端异常以 `RpcRemoteException` 抛出；连接断开时，挂起的调用以 `RpcConnectionLostException` 失败。
  - `AgentHostServerProxy` / `AgentHostServerDispatcher`、`AgentHostClientProxy` / `AgentHostClientDispatcher` 是手写的代理和分发器，没有运行时代码生成。
  - `RpcJsonContext` 是全部载荷的源生成序列化上下文。
  - **新增 RPC 方法的步骤**：在接口中声明 → 在 `RpcMethods` 中加方法名（多参数时加一个 params 记录）→ 在 `RpcJsonContext` 中登记新的根 DTO → 代理和分发器各加一行。
- **stdout 专用于 JSON-RPC 帧**（Content-Length 分帧），Host 的日志全部写到 stderr。
- UI 关闭 stdin 后 Host 自动退出；Host 崩溃不影响 UI，下次调用时会重新拉起。
- 方法：

  | 方向 | 方法 |
  |---|---|
  | UI → Host | `host/initialize`、`models/list`、`session/create\|list\|open\|prompt\|abort\|setModel\|setOptions\|close` |
  | Host ↔ Host | `peer/list`、`peer/send`（本机 Unix socket，用于跨窗口的 `send_message` / `list_agents`） |
  | Host → UI | `session/event`（notification）、`tool/approve` |

- 事件只携带增量，并带单调递增的 `Seq`。
- 会话列表与恢复（协议版本 3）：
  - `session/list(cwd?)` 读取会话文件，返回标题、项目、模型、最近修改时间和运行状态。标题取第一条用户消息的首行；`cwd` 为 null 时列出全部项目。
  - `session/open(filePath)` 返回会话和当前分支的历史（`HistoryMessageDto`），UI 用它重建转录。同一文件已经打开时直接返回该会话，避免两个 Agent 同时写一个文件。
  - 恢复时，模型和参数取分支上最后一条 `modelChange` / `optionsChange`；记录的模型不在目录中时退回第一个可用模型。
- 会话参数（协议版本 2 起）：
  - `ModelDto` 带上模型能力：`ThinkingLevels`（只列出会按原样生效的档位，由 `ThinkingLevels.Available` 按协议与兼容配置推导）、`DefaultThinkingLevel`（优先 High）、`ContextWindows`（上限超过 200K 时额外提供 200K 档，默认取最小）、`SupportsFastMode`。
  - `session/create` 可带 `SessionOptionsDto`；`session/setOptions` 和 `session/setModel` 返回**生效后**的参数（不支持的值就近修正），UI 以返回值为准。
  - Fast mode：Anthropic 发送 `speed:"fast"` + `anthropic-beta: fast-mode-2026-02-01`（仅 Opus 5.5 / Opus 5 / Opus 4.8，且只在 Claude API 上可用）；OpenAI 发送 `service_tier:"fast"`。模型未声明 `supportsFastMode` 时适配器忽略该选项。
  - 上下文预算目前只用于计算占用比例（输入 + 缓存 + 输出 tokens / 预算），以后作为压缩阈值；各厂商没有对应的请求参数。

## 5.1 NativeAOT 约束（整个客户端必须支持 AOT）

- `TCode.Desktop` 和 `TCode.AgentHost` 都设置了 `PublishAot=true`，构建时 AOT 和裁剪分析器始终开启；库项目标记 `IsAotCompatible`。
- 验证基线（2026-10-16，osx-arm64）：两个可执行项目 AOT 发布均为 **0 个 IL/AOT 警告**（桌面端约 33 MB，Host 约 11 MB）。AOT 编译的 Host 通过 stdio 端到端测试：设置 `TCODE_TEST_HOST_EXE=<AOT Host 路径>` 后运行 `RpcEndToEndTests` 即可复验。
- 编码规则：
  - JSON 一律使用源生成的 `JsonSerializerContext`；构造请求体用 `JsonNode`，但不要调用泛型的 `JsonArray.Add<T>`。
  - 不使用反射、`Activator`、动态代理或运行时代码生成。
  - 新引入的 NuGet 包要先确认 AOT 兼容，并用 AOT 发布验证零警告。

## 6. 会话存储

- 位置：`~/.tcode/sessions/<cwd 编码>/<时间戳>_<sessionId>.jsonl`。
- 第一行是 `session` header；其余每行一个带 `id` / `parentId` 的条目（`message`、`modelChange`、`optionsChange`、`systemPrompt` 等），组成一棵只追加的树。
- 文件在写入第一条消息时才创建：此前追加的模型 / 参数变化先缓存在内存里，随第一条消息一起写入，所以空会话不会落盘。读取时跳过损坏的行。

## 7. 桌面端与样式

### 7.0 布局

- **侧栏**：项目筛选（全部项目 / 单个项目，弹层可搜索，并提供“新建项目…”）+ 会话列表。每行依次显示运行状态（点阵动画 / 灰点）、模型图标、项目首字母徽标、标题和相对时间。
- **主区**：
  - 空状态是居中的 Composer：上方是设备和项目，下方是检出和分支。
  - 会话中，标题栏显示模型图标、会话标题和“项目 @ 设备”；Composer 收成单行胶囊停靠在底部，下方是检出、分支和上下文占用环。
  - 背景是三层径向渐变光晕（`TCode.GlowMain`、`GlowAccent`、`GlowSidebar`），不依赖系统材质。
- **多会话**：`ConversationViewModel` 为每个打开的会话保留一个 `SessionViewModel`，Host 事件按 SessionId 分发，所以后台会话的转录和运行状态同样保持最新。
  - 等待输出时显示“思考中… Ns”。
  - 同一轮内工具调用前后的多条助手消息合并显示。
- **Markdown**：`Controls/MarkdownView` 是轻量渲染器，支持段落、标题、列表、引用、代码块、行内代码、粗体、斜体和链接，不支持表格和图片。
- **分支选择器**：选择其它分支时，先在弹层内确认，再执行 `git switch`。这个操作是安全的：git 会把未提交的修改带到新分支，如果修改会被覆盖则拒绝切换并显示错误。当前项目中有会话在运行时不允许切换。
- **命令面板（⌘K）**：
  - 根页列出命令（新对话、新建项目、切换主题 / 语言 / 侧栏 / 工作台）；输入搜索词时，还会列出匹配的会话。
  - 支持多级页面：“新建项目” → 设备页 → 文件夹选择框。面包屑可以返回上一页，搜索框为空时按 Backspace 也会返回。
  - 键盘：↑ ↓ 移动高亮，↵ 执行，Esc 返回或关闭。
- **全局快捷键**（Windows / Linux 上 ⌘ 换成 Ctrl）：⌘K 命令面板、⌘N 新对话、⇧⌘N 新建项目、⌘B 切换侧栏。
- **前进 / 后退**：在打开过的会话和新对话之间导航。
- **数据目录**：`TCODE_HOME` 覆盖 `~/.tcode`，Host 子进程继承该变量；`TCODE_AGENTHOST_DIR` 指定 Host 位置。两者都供测试使用。

### 7.1 主题

- **`Assets/Themes/TCodeDark|TCodeLight.theme.xml`**：AtomUI 主题定义，包含：
  - Seed：`ColorPrimary`、`FontSize=13` 等；
  - Map：`ColorBgContainer`、`ColorBgElevated`、`ColorBorder` 等；
  - 控件级 Token：`ToggleSwitch.SwitchColor`。
- 通过 `AvaloniaAssetThemeDefinitionResolver` 注册，`WithInitialTheme` 选择初始主题，运行时用 `IThemeManager.ApplyThemeAsync` 切换。
- **注意**：AtomUI README 中的 `WithDefaultTheme` 在 6.2.5 中不存在。

### 7.2 应用专属颜色

`Styling/Palette.axaml` 中的 `ThemeDictionaries` 定义了 Dark / Light 两套颜色，覆盖侧栏、Composer、磁贴等 AtomUI Token 管不到的表面。暗色取值来自参考截图的像素采样：

| 用途 | 色值 |
|---|---|
| 背景 | `#151515` |
| Composer | `#212121`，边框 `#3F3F3F` |
| 弹层 | `#181818`，边框 `#383838` |
| 开关 | `#40A167` |

### 7.3 控件

- `Styling/Controls.axaml` 为 TCode 复合控件提供 ControlTheme：行按钮、磁贴、标签页、胶囊、分割条。
- 通用按钮、输入框、开关、弹层使用 AtomUI 控件：`atom:Button`、`atom:TextArea`、`atom:LineEdit`、`atom:ToggleSwitch`、`atom:FlyoutHost`、`atom:Avatar`。
- 白色发送按钮直接用 `atom:Button Color="Default" Variant="Solid"`：暗色主题下它天然是白底深色字。
- 图标用 `Icons/LucideIcon`：Lucide 风格线性图标，基于 AtomUI `Icon`，可以直接作为 `atom:Button.Icon` 使用。

### 7.4 品牌图标

- 来源：LobeHub lobe-icons（`@lobehub/icons-static-svg@1.95.1`，MIT；商标归各品牌所有，只用于标识 Provider / 模型）。文件放在 `Assets/ProviderIcons/svg/`，说明见同目录 README。
- **单色图标**：由 `tools/gen_provider_icons.py` 转成路径数据（`Icons/ProviderIconPaths.g.cs`），用 `ProviderGlyph`（AtomUI `Icon` 子类）绘制，跟随前景色。
- **彩色图标**（`*-color.svg`，部分含渐变）：用 AtomUI 依赖中自带的 Svg.Controls.Avalonia 渲染。
- `ProviderLogo` 会优先使用彩色版，没有彩色版时退回单色。`ModelIcons` 负责把模型 ID / Provider ID 映射到图标键，规则移植自 lobe-icons 的 `modelConfig.ts`，按顺序匹配。
- 更新图标：替换 `svg/` 下的文件后重新运行生成脚本。

### 7.5 本地化

- 使用 AtomUI 的 XLIFF 语言目录。文案的唯一来源是 `Localization/strings.tsv`（key、en-US、zh-CN）。
- `tools/gen_strings.py` 根据这张表生成 `TCodeLangResourceKind.g.cs`、`en-US.xlf`、`zh-CN.xlf`。AtomUI 要求枚举成员与 XLIFF 单元一一对应，所以不要手改生成的文件。
- 引用方式：
  - AXAML：`{loc:TCodeLangResource Key}`，随语言切换自动刷新；
  - C#：`Strings.Get / Format`，读取当前语言的快照，需要刷新时订阅 `Strings.LanguageChanged`。
- 语言保存在 `desktop.json` 的 `language` 字段，启动时传给 `UseLanguages`。`ILanguageManager.ChangeLanguage` 会同时切换 AtomUI 内置控件的文案。

### 7.6 界面截图测试

- `tests/TCode.Desktop.Tests` 用 Avalonia.Headless + Skia 启动真实的 `App`（主题、调色板、语言目录），把关键组件渲染成 PNG（默认写到测试输出目录的 `snapshots/` 下，也可以用 `TCODE_SNAPSHOT_DIR` 指定）。
- `MainWindowSnapshotTests` 是端到端测试：它启动真实的 Host，用 Fake 模型跑完一轮带工具调用的对话，然后截取空状态、会话、思考展开和英文界面四张图。
- 无头平台没有原生弹出窗口，测试中弹层改用窗口的覆盖层渲染。
- 不要用合成鼠标事件或屏幕截图去操作真实桌面。

### 7.7 窗口

- 不显示 AtomUI 标题栏（`IsTitleBarVisible=False`）。
- **macOS**：在 code-behind 中恢复 `WindowDecorations.Full`，再用 `MacStandardWindowButtons` 把红绿灯移到侧栏顶部。
- **Windows / Linux**：在右上角自绘窗口按钮（`WindowDecorationProperties.ElementRole`）。
- 拖拽区域设为 `ElementRole="TitleBar"`；标题栏里可交互的按钮设为 `User`。

### 7.8 许可证

只通过公开的 Token、样式、主题定制 AtomUI，属于 LGPL 允许的"链接使用"。不要复制或修改 AtomUI 源码模板。

## 8. 路线图

| 阶段 | 内容 |
|---|---|
| 当前（壳子） | 三栏界面、双主题、Composer / 模型选择器 / 工作台 / 文件树、Agent 循环、JSONL 会话、Host 进程与 RPC、Fake Provider、只读工具 |
| 下一步 | 三个协议适配器（含兼容配置与录制 SSE 夹具测试）、模型目录与 `models.json`、API Key 设置页、流式 Markdown（LiveMarkdown.Avalonia）、跨模型交接（transform） |
| 之后 | write / edit / bash 工具与审批界面、压缩、会话列表 / 分支 / fork、终端（Iciclecreek.Avalonia.Terminal）、浏览器（Avalonia.Controls.WebView）、变更 diff（DiffPlex + AvaloniaEdit）、本地化（AtomUI XLIFF） |
