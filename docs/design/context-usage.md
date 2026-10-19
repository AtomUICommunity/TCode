# 上下文容量卡片与悬停卡片组件

状态：已实现（2026-10-19）
关联文档：[compaction.md](compaction.md) §3–§4（上下文计量与阈值）、[architecture.md](../architecture.md) §5（RPC）、§7.3（控件）

## 1. 目标

- 输入框下方的上下文占用环，悬停时显示一张卡片：已用 / 预算、分段占用条、按来源的分类占比、整个会话的平均缓存命中率。之前这里只是一条 ToolTip 文本。
- 卡片用一组可复用的组件搭出来（悬停行为、卡片外壳、统计行、分段条），之后其他悬停说明也用这一套。

## 2. 已确认的决策（用户，2026-10-19）

| 维度 | 决策 |
|---|---|
| 组件形态 | 行为与外壳分离：`HoverCard` 只管悬停弹出，`InfoCard` 只管卡片外观；外壳也能放进点击弹层 |
| 数据来源 | Host 估算，卡片打开时通过 `session/contextUsage` 拉取一次（打开期间用量变化再拉） |
| 分类 | 系统工具、消息、系统提示词、技能、MCP 工具、其他（六类）；AGENTS.md 与模式说明计入系统提示词 |
| 位置 | 占用环保持原位（输入框下方一行的最右侧） |

## 3. 组件（`TKCode.Desktop/Controls`，主题在 `Styling/Controls.axaml`）

- **`HoverCard`**（`ContentControl`）：`Content` 是触发内容，`Card` 是卡片。
  - 指针停留 `OpenDelay`（默认 300ms）后打开；离开后 `CloseDelay`（默认 150ms）内移进卡片就保持打开；按下触发内容立即打开。
  - `IsOpen` 默认双向绑定，视图模型据此在打开时刷新数据。触发内容隐藏或离开可视树时关闭。
  - 模板里是 `motion:Popover` + `ZoomHost`（满足 UI 缩放的约定）。卡片四周留出阴影空间 `ShadowMargin`，弹层偏移按 `Placement` 换算掉这部分留白，再留出 `Gap`（默认 6），让可见的卡片边缘与触发内容对齐。
  - 阴影留白比 `Gap` 大，弹层窗口会盖住一部分触发内容。macOS 上弹层是另一个窗口：它一出现，主窗口就报告指针“离开”触发内容，而指针不动时弹层窗口不报告“进入”。如果按“离开”关闭，卡片会关掉、指针回到触发内容、再打开，反复闪烁。所以：
    - 留白是透明但可命中的，指针在弹层窗口任何位置都算在卡片上；
    - 触发内容报告离开时，指针位置仍在触发内容里就忽略；
    - 卡片打开期间监听主窗口的指针移动：移到触发内容和卡片以外才关闭（已在等待关闭时不重新计时）。
  - `Popup` 暴露模板里的弹层，测试把它放到窗口浮层上截图。
- **`InfoCard`**（`ContentControl`）：`Title`、`Extra`（标题右侧的摘要）、`Content`、`Footer`（有值时上方加分隔线）。背景、描边、圆角、投影与弹层菜单（`Border.menu`）同一套。
- **`CardStatRow`**：可选色点、标签（次要文字色）、右对齐数值（主要文字色）。色点用 `Marker`（画刷）或 `MarkerKey`（资源键，随主题切换）。
- **`SegmentedBar`**：`Segments` 是 `BarSegment(Value, BrushKey)` 列表，`Value` 为占整条轨道的比例。
  - 段与段之间留 2px 空隙，透出卡片底色，不画描边。剩余部分是轨道，与填充之间同样留空隙。
  - 填充太窄、放不下各段时（例如占用 2.6%），整段画成一个圆点 / 短条，用占比最大的那段的颜色。
  - 颜色在渲染时按资源键取，主题切换后重画。

## 4. 数据

### 4.1 `session/contextUsage`

`ContextUsageDto(Tokens, Breakdown?, CacheHitRate?)`：

- `Breakdown`：`ContextBreakdownDto(SystemPrompt, SystemTools, McpTools, Skills, Messages, Other)`，各类之和等于 `Tokens`。外部运行时为 null：Host 不知道它们的系统提示与工具定义，卡片只显示总量与命中率，并说明没有分类明细。
- `CacheHitRate`：整个会话（含压缩掉的消息）的 `Σ缓存读取 / Σ(未缓存 + 读取 + 写入)`，按 token 加权。没有任何缓存读写时为 null，显示“—”。

### 4.2 分类估算（`TKCode.Agent.Coding/ContextUsage.cs`）

按约 4 字符 / token（`ContextAccounting.EstimateText`）分别估算：

| 来源 | 归类 |
|---|---|
| 冻结的开头系统提示的各节（`SystemPromptTracker.InitialSections`） | `skills` 节 → 技能；`mcp` 节 → MCP 工具；其余（identity、tools、rules、environment、project_context、mode、cwd）→ 系统提示词 |
| 工具定义（名称 + 描述 + 参数 schema） | `SkillTool` → 技能；名称以 `mcp__` 开头 → MCP 工具；其余 → 系统工具 |
| 会话中途追加的系统提示补丁（`SystemMessage`） | 用 `SystemPromptBuilder.PatchParts` 按节名拆回，归类同上 |
| `skill` 工具的结果、用户消息里的 `<skill>` 块 | 技能 |
| 其他消息 | 消息 |

估算之后，按最近一次成功回复的真实用量（`ContextAccounting.ContextTokens`：最后一次 usage 加之后新增内容的估算）归一：

- 估算少于真实值：差额计入“其他”（工具协议的系统开销、消息格式、分词差异、图片）。
- 估算多于真实值：各类按比例缩小。
- 还没有真实用量：直接用估算值。

### 4.3 卡片上的显示

- 外部运行时只在一轮的最后一条助手消息上给整轮用量，中间消息的用量是全 0。Desktop 忽略全 0 的用量，不用它覆盖上一次的值，否则一轮里占用环和卡片会反复消失。
- 外部运行时的“已用”目前是整轮所有 API 调用的累计（例如 Claude 的 `result.usage`），会远大于真实的上下文占用；按协议规格应取最后一次 API 调用的输入侧用量（claude-code-stream-json.md §8.5），尚未修正。

- 标题右侧：`已用/预算 (比例)`，与占用环同一组数据（最近一次回复的 tokens / 会话的上下文预算）。数字中文按万 / 亿缩写，英文按 K / M 缩写。
- 分段条：按上下文的实际顺序叠放（提示词 → 系统工具 → MCP → 技能 → 消息 → 其他），总长等于占用比例。
- 图例：按占比从大到小排列，省略占比为 0 的类别。比例是占**已用上下文**的比例，保留一位小数。
- 底栏：平均缓存命中率；接近自动压缩阈值时加一行提示（图标 + 文字，与占用环同色）。

## 5. 配色

颜色跟着类别走，不随排名变化。用 dataviz 校验脚本按叠放顺序检查相邻对，背景用卡片底色（亮 `#FFFFFF`、暗 `#1C1C1E`）：

| 类别 | 亮色 | 暗色 |
|---|---|---|
| 系统提示词 | `#6250D6` | `#9085E9` |
| 系统工具 | `#EB6834` | `#D95926` |
| MCP 工具 | `#1BAF7A` | `#199E70` |
| 技能 | `#EDA100` | `#C98500` |
| 消息 | `#2A78D6` | `#3987E5` |
| 其他 | `#A8A8AC` | `#6E6E72` |

- 相邻对的色觉缺陷区分度最低 9.1（亮）/ 8.4（暗），正常视觉最低 17.6 以上。
- 亮色下青、黄、灰低于 3:1，图例行都有文字标签，满足补偿要求。
- “其他”有意用中性灰，不参与色相饱和度下限检查。

资源键 `TKCode.Context*` 在 `Styling/Palette.axaml`。

## 6. 测试

- `tests/TKCode.Agent.Tests/ContextUsageTests.cs`：系统提示节、工具、补丁、skill 正文的归类；归一（差额计入其他、按比例缩小、出错的回复不算真实用量）；缓存命中率加权；补丁拆分。
- `tests/TKCode.Desktop.Tests/ContextUsageCardTests.cs`：数字缩写；视图模型在打开时请求、图例排序、分段顺序与总长、外部运行时；`HoverCard` 的延迟打开、移进卡片保持、离开关闭、按下立即打开以及卡片与触发内容的对齐；弹层盖住触发内容时不闪烁（模拟主窗口报告离开、弹层窗口不报告进入）；外部运行时中间消息的全 0 用量不覆盖上一次的值；真实 Host（Fake 模型）下悬停占用环显示分类，截图 `context-card-dark` / `context-card-light`。
