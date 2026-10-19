# 输入框：按会话保存草稿与 ↑↓ 历史输入

对应 [issue #11](https://github.com/AtomUICommunity/TKCode/issues/11)「会话输入框优化建议」。输入框本身的设计见 [composer.md](composer.md)。

## 1. 目标

1. 各会话的输入框内容互相独立：在 a 会话里输入的内容，不会出现在 b 会话（包括“新对话”空状态）；切回来时还在，重启应用后也还在。
2. ↑ ↓ 可以在输入框里切换到历史输入（当前会话已发送过的消息），再按 ↓ 回到正在写的草稿。

## 2. 已确认的决策（用户，2026-10-20）

| 问题 | 决定 |
|---|---|
| 历史输入的范围 | 只取当前会话已发送的消息；不做跨会话、跨项目的历史 |
| 草稿是否落盘 | 落盘，重启应用后恢复 |

## 3. 现状（问题的根因）

- 整个应用只有一个 `ConversationViewModel`，它持有**一份** `Composer`（`ComposerDocument`）、**一份** `Attachments`（图片附件）和一份 `Mcp` 选中的提示词（`ConversationViewModel.cs`）。空状态居中的输入框和停靠在底部的输入框是两个 `ComposerView`，共用这个 ViewModel。
- 切换会话只走 `Activate()` / `NewChat()`，两者都不碰这三份状态，所以草稿原样出现在别的会话里。
- `CompletionViewModel` 在构造时绑定了这个 `Composer` 实例，`ComposerEditor.Document` 也绑定它。因此**不换文档实例**，切换时替换内容。
- 编辑器里 ↑ ↓ 只做光标纵向移动（`ComposerEditor.OnKeyDown`）。补全列表打开时，↑ ↓ 已由 `ComposerView.OnInputKeyDown`（隧道阶段）先截走，历史功能必须排在它之后。
- 没有任何“输入历史”的存储。已发送的消息只存在会话转录里：`SessionViewModel.Items` 中的 `UserMessageItemViewModel`，打开会话时只加载最近 20 轮，更早的靠 `LoadEarlierHistoryAsync()` 分页。

## 4. 草稿

### 4.1 数据：`ComposerDraft`

一个不可变的快照，包含输入框里属于“这一个会话”的全部状态：

| 字段 | 说明 |
|---|---|
| `Segments` | 文字与 tag（`ComposerDocument.Segments`） |
| `Images` | 图片附件（路径、类型、编号）；内存里直接持有 `ImageAttachmentViewModel`，缩略图不用重新解码 |
| `NextImageNumber` | 下一张图片的编号（`_nextImageNumber`），否则恢复后新贴的图会与已有 tag 编号冲突 |
| `McpPrompt` | 选中的 MCP 提示词与已填的参数值（输入框上方的卡片） |

`IsEmpty`：没有文字 / tag、没有图片、没有 MCP 提示词。空草稿不保存。

光标不存：编辑器对“不是自己发起的文档修改”（`SetContent`）会把光标放到末尾（composer.md §17），恢复后光标在末尾。撤销记录同样不保留（`SetContent` 会清掉）。

### 4.2 草稿的归属与切换

- 草稿的键：`Active?.SessionId ?? "new"`。`"new"` 是“新对话”空状态的草稿，整个应用一份（不分项目）。会话 ID 是 32 位十六进制（`Guid.CreateVersion7().ToString("N")`），可以直接当文件名。
- 唯一的切换点是 `ConversationViewModel.OnActiveChanged(SessionViewModel? oldValue, SessionViewModel? newValue)`（CommunityToolkit 生成的双参数重载，与现有的 `OnActiveChanged(value)` 并存）。`Activate`、`NewChat`、删除会话后回到空状态、`Disconnected` 都经过这里，不用在每个调用点各写一遍。
- 切换在这一个同步方法里完成，不含 `await`：
  1. `Capture()`：把当前输入框状态存到 `DraftStore[旧键]`（空则移除）。
  2. `Apply(DraftStore[新键])`：`Composer.SetContent`、`Attachments` 重建、`_nextImageNumber`、`Mcp` 恢复；没有草稿则全部清空。
  3. 重置补全：`Completion.Host = null` 并关闭弹层。否则切换到一个以 `@` / `/` 结尾的草稿时，上一个获得焦点的输入框还挂着，会立刻弹出补全；输入框下次获得焦点时（`ComposerView` 的 `GotFocus` 已有）会重新挂上。
- 应用草稿期间用 `_applyingDraft` 守卫：`Composer.Changed` / `Attachments.CollectionChanged` 触发的“标记草稿已改动”在守卫内不执行，避免刚读出来的草稿又被写回磁盘。

### 4.3 发送与失败恢复

- 发送时先 `ComposerText = ""`、`Attachments.Clear()`，这一步清掉的是**发送时所属**的草稿，不受后面的 `await` 影响。
- 现有的失败恢复（`SendAsync` 的 `catch`）直接写回当前输入框，在 `await CreateSessionAsync` / `PromptAsync` 期间用户若已切走会话，草稿会被恢复到错误的会话。改为：
  - 发送开始时记下 `ownerKey = DraftKey`；
  - 从空状态发送并成功创建了会话后，`ownerKey` 变为这个新会话（之后 `PromptAsync` 失败，草稿回到这个新会话里，与现在一致）；
  - 失败时：`DraftKey == ownerKey` 就像现在一样直接恢复到输入框；否则写入 `DraftStore[ownerKey]`，该草稿此时已有新内容时，把失败的内容放在前面、空一行接上原有内容，不覆盖。
- 在空状态发送、`CreateSessionAsync` 等待期间用户又在空输入框里打了字：这些字属于“新对话”草稿，`Activate(live)` 触发切换时会被 `Capture` 到 `"new"`，新会话的输入框是空的。这是想要的结果，不用特别处理。
- 排队消息（`QueuedPrompts`）本来就按会话保存，不变。

### 4.4 落盘：`DraftStore`

新增 `Services/DraftStore.cs`，在 `Program.ConfigureServices` 里注册为单例，由 `ConversationViewModel` 注入。

- **位置**：`~/.tkcode/drafts/<键>.json`（`"new.json"` 与各会话一个文件）。不放进 `state.json`：它每次 `Save()` 都整份重写，草稿的写入频率会拖累它。
- **格式**（源生成 `JsonSerializerContext`，`DraftJsonContext`；AOT 要求）：

  ```json
  {
    "version": 1,
    "updatedAt": "2026-10-20T10:00:00Z",
    "segments": [{ "kind": "text", "text": "看看 " }, { "kind": "file", "value": "src/a.cs", "label": "a.cs" }],
    "images": [{ "path": "…/attachments/2026-10/….png", "mimeType": "image/png", "number": 2 }],
    "nextImageNumber": 3,
    "mcpPrompt": { "server": "x", "name": "y", "arguments": { "k": "v" } }
  }
  ```

  `segments` 复用 `PromptSegmentDto` 与 `ComposerSegments`；`ComposerSegments.ToDto` 只有文字时返回 null，草稿需要一个“总是返回列表”的版本。Image tag 的 `label` 是本地化的（“图片 #2”），恢复时按编号重新生成，不信任存下的文字。
- **写入**：任何改动（文字、附件、MCP 提示词）都标记对应键为脏；最后一次改动后 800 ms 写入（防抖）；写临时文件再改名，保证中途退出不会留下半个文件；写盘放在 `Task.Run`，同一个键的写入串行。草稿变空时删除文件。
- **退出时落盘**：`App.axaml.cs` 的 `desktop.ShutdownRequested`（现在只释放 `AgentHostConnection`）里同步 `Flush()` 所有脏草稿。关到托盘不是退出，防抖写入照常工作。
- **读取**：启动后在后台一次性读入 `drafts/` 下所有文件到内存字典（文件很小），随后切换草稿只读内存，UI 线程上没有磁盘 I/O。读完后若当前键的输入框仍然是空的，就应用该键的草稿（覆盖“启动时就在空状态”和“很快就打开了某个会话”两种情况）。不阻塞首帧：和现有的 `InitializeAsync` 一样用 `DispatcherPriority.Background` 排队。
- **清理**：
  - 删除会话（`DeleteSessionAsync`）时删除对应草稿；
  - 草稿最多保留 3 天（用户指定，`DraftStore.MaxAge`）：从最后一次修改（`updatedAt`）算起。启动读取时删除超过 3 天的草稿文件；应用一直开着时，读取到过期的草稿也当作不存在并删除。不能拿会话列表去比对“孤儿”草稿：会话列表是分页加载的（`HasMoreSessions`），没加载出来的会话不等于不存在；
  - 分叉会话（`ForkSessionAsync`）不复制草稿。
- **图片附件文件**：附件目录（`~/.tkcode/attachments`）里的文件本来就不会自动清理，草稿引用它们没有新问题。恢复时某个文件已不存在：丢掉这张图片，同时删掉文字里对应的 Image tag。
- **MCP 提示词恢复**：存的是 `server` + `name` + 参数值；恢复时从 MCP 目录（`McpComposerViewModel` 的 `GetCatalogAsync`）找回 `McpPromptDto`，目录没加载好或找不到就丢弃这张卡片，不阻塞草稿的其余部分。
- **隐私**：草稿是明文，与会话的 JSONL 同在 `~/.tkcode`，信任边界相同。

## 5. 历史输入（↑ ↓）

### 5.1 数据来源

只取当前会话，不新增存储，也不做跨会话历史：从 `Active.Items` 里取 `UserMessageItemViewModel`，由新到旧。

不包含：
- 通知、压缩摘要等不是用户输入的条目（它们不是 `UserMessageItemViewModel`）；
- 排队中尚未发出的消息（可以在队列里直接编辑）；
- `/compact`、`/new` 这类只在界面里执行、不作为消息发出的命令；
- MCP 提示词卡片（它不在消息的 `Segments` 里）。

连续相同的条目（文字、tag、图片都相同）只算一条。

### 5.2 触发规则

放在 `ComposerView.OnInputKeyDown` 里，**排在补全列表那一段之后**（补全打开时 ↑ ↓ 仍然是移动高亮），Shift+Tab 之前。

共同前提：没有修饰键（Shift 是扩展选区，Cmd/Ctrl+↑↓ 是跳到开头 / 末尾）、没有选区、没有输入法预编辑（`Input.HasPreedit`）。

| 按键 | 条件 | 动作 |
|---|---|---|
| ↑ | 还没开始浏览：`CaretIndex == 0`（输入框为空时一定满足）。已经在浏览：光标在第一行 | 翻到更早的一条；没有更早的就什么都不做、也不吃掉按键 |
| ↓ | 正在浏览历史 **且** 光标在最后一行 | 翻到更新的一条；越过最新一条时恢复浏览前的草稿 |

“第一行 / 最后一行”指折行后的显示行（`ComposerEditor.IsCaretOnFirstLine` / `IsCaretOnLastLine`）。按键没有满足条件时不处理，交给编辑器做光标移动，所以：

- **还没开始浏览时要求光标在开头**：草稿不为空、光标在中间时，第一次 ↑ 先把光标移到开头（现有行为，macOS 习惯），第二次 ↑ 才翻历史。不会因为误按一下 ↑ 就顶掉正在写的内容。
- **已经在浏览时放宽到第一 / 最后一行**：这时草稿已经暂存，不会丢。否则翻进去之后光标在开头，想往回翻 ↓ 要先按一下把光标移到末尾，换方向多按一次，不自然。
- 翻出来的是多行消息时，光标放在开头（↑ 翻入）或末尾（↓ 翻入），所以继续按同一个方向键可以连续翻；想在消息内部上下移动，也还是 ↑ ↓，到了第一 / 最后一行再按一次才会翻页。
- 光标位置由 `ConversationViewModel` 在翻页后通过 `Completion.Host.SetCaret` 设置（与补全确认放置光标走同一个入口，只有获得焦点的输入框是 Host）。

备选：还没开始浏览时也改成“光标在第一行就翻历史”（Claude Code、Codex 的做法），代价是误按会替换草稿（↓ 仍可恢复）。换掉 `InputHistory.ShouldRecallOlder` 里的一个条件即可。

### 5.3 浏览状态：`InputHistory`

新增 `ViewModels/InputHistory.cs`，一个纯 C# 类（不依赖 Avalonia，可以直接单元测试），每个 `ConversationViewModel` 一个。

- 状态：`Index`（-1 表示没在浏览）、`Stash`（开始浏览时的 `ComposerDraft`）、正在显示的那一条消息。
- 浏览期间列表可能变化：排队的消息自动发出（新条目出现在最新的一端），或翻页加载了更早的（出现在最旧的一端）。每一步都按“正在显示的那一条”在新列表里重新定位 `Index`，而不是按数量增减推算——两种变化的位置相反，按数量推算会错位（翻页的测试发现过）。
- 开始浏览（第一次 ↑）：`Stash = Capture()`，`Index = 0`。
- 应用一条历史：`_applyingDraft` 守卫内把条目转成 `ComposerDraft`（见 §5.5）再 `Apply`，**不**写入 `DraftStore`。
- 恢复：`Index` 退到 -1 时 `Apply(Stash)`，结束浏览。
- 以下情况结束浏览并丢掉 `Stash`：
  - 用户编辑了内容（`Composer.Changed` 不在守卫内，或附件 / MCP 提示词变化）——此时显示的是“改过的历史消息”，它成为当前草稿；
  - 发送；
  - 切换会话。
- **浏览中的草稿归属**：浏览期间输入框里的是历史消息，不是用户的草稿。所以 `Capture()`（切换会话、防抖落盘、退出时 `Flush`）在浏览中返回 `Stash`，而不是输入框的当前内容。否则：浏览中切走会话，会把翻出来的历史消息存成这个会话的草稿，真正的草稿丢了。

### 5.4 翻页

`Items` 只含最近加载的几轮。↑ 翻到已加载部分的最早一条、且 `Active.HasEarlierHistory` 时，调用现有的 `LoadEarlierHistoryAsync()` 并在完成后继续翻（期间 `IsLoadingEarlier` 为真，不重复请求；完成时如果浏览状态没变就自动前进一步）。

翻页期间再按 ↑ 只是吞掉按键（`_pagingHistory`），加载完成时如果用户没有编辑输入框、也没有切换会话（`_historyVersion` 没变）才继续翻；重试时不再允许翻页，避免加载一直失败时无限重试。

转录滚动：`TranscriptAdapter` 在插入用户消息时会让 `MainWindow` 把转录滚到底部（`UserMessageAdded`，本意是发送新消息后回到底部），翻页插入的是更早的用户消息，同样会触发。从输入框翻历史时用户本来就在输入框附近，转录通常已经在底部，所以没有处理；如果用户先向上滚动阅读、再在输入框里翻历史，转录会被拉回底部。

### 5.5 把消息还原成可编辑的草稿

`HistoryEntry.FromUserMessage(UserMessageItemViewModel)` → `ComposerDraft`（纯函数）：

- 有 `Segments`（含 tag 的消息）：直接用，tag 都能还原（文件、文件夹、command、skill、MCP 资源、附件文件、Image）。
- 没有 `Segments`：文字取 `DisplayText`，其中的图片占位符 `[图片 #N]` 换成 Image tag。
- 图片：`UserMessageItemViewModel.Images` 按它们在文字里出现的顺序对应 Image tag，**重新编号为 1..n**（`Segments` 里保留的是发送时的原始编号，而 `Images` 是按顺序编号的，两者不能直接按编号对应）；没有出现在文字里的图片追加在末尾；文件已不存在的丢掉，连同 tag。
- `NextImageNumber = n + 1`。

## 6. 文件改动

| 文件 | 改动 |
|---|---|
| `ViewModels/ComposerDraft.cs`（新） | 快照记录；`Capture()` / `Apply()` 的实现放在 `ConversationViewModel`（它同时持有三份状态） |
| `ViewModels/InputHistory.cs`（新） | 浏览状态机、`ShouldRecall`、`HistoryEntry.FromUserMessage` |
| `Services/DraftStore.cs`（新） | 内存字典、防抖落盘、启动读取、清理、`Flush`；`DraftJsonContext` |
| `ViewModels/ConversationViewModel.cs` | `DraftKey`；`OnActiveChanged` 双参数重载做切换；`SendAsync` 的所属键与失败恢复；`DeleteSessionAsync` 删除草稿；脏标记订阅 |
| `ViewModels/McpComposerViewModel.cs` | 提示词与参数值的 `Capture()` / `Restore()`（现有的 `Take` / `Restore` 只管发送失败） |
| `ViewModels/Completion/CompletionViewModel.cs` | 切换草稿时重置 Host 与弹层（如果现有的 `Dismiss()` 不够用就加一个 `Reset()`） |
| `Views/ComposerView.axaml.cs` | `OnInputKeyDown` 里的 ↑ ↓ 分支、翻页后放置光标 |
| `Controls/Composer/ComposerEditor.cs` | 无需改动：`CaretIndex`、`HasSelection`、`HasPreedit` 已是公开成员 |
| `Program.cs`、`App.axaml.cs` | 注册 `DraftStore`；`ShutdownRequested` 里 `Flush()` |
| `docs/design/composer.md` | 在文首链接到本文 |

不新增界面文字，所以不改 `strings.tsv`。不新增设置项。

## 7. 实施阶段

| 阶段 | 内容 | 可独立验证 |
|---|---|---|
| D1 | `ComposerDraft` + 内存中的按会话切换 + 发送失败的所属键 | 是（issue 第一条的核心） |
| D2 | `DraftStore`：落盘、启动读取、清理、退出 `Flush`、MCP 提示词恢复 | 是 |
| D3 | `InputHistory` + 键盘分支 + 翻页 | 是（issue 第二条） |
| D4 | 文档、AOT 发布验证 | — |

## 8. 测试

均为无头测试（`tests/TKCode.Desktop.Tests`），不驱动真实桌面、不合成系统输入；按键用路由事件。

- `ComposerDraftTests`（纯模型）：文字 + tag + 图片 + MCP 提示词的 `Capture` / `Apply` 往返；JSON 往返（包括 Image tag 的 label 重新生成）；图片文件不存在时连同 tag 丢弃；空草稿不落盘并删除旧文件；超过 3 天的草稿启动时被清理、读取时被忽略；半个文件（损坏的 JSON）被忽略而不是让启动失败。
- `SessionDraftTests`（经由 `Program.ConfigureServices` 与假 provider，参照 `PromptQueueTests`）：a 里输入 → 切到 b 是空的 → 切回 a 还在；“新对话”有自己的草稿；发送只清掉本会话的草稿；`CreateSessionAsync` 等待期间切走再失败，草稿回到所属会话；删除会话后草稿文件消失；用同一个 `TKCODE_HOME` 新建一套服务，草稿被恢复；切换后补全弹层是关闭的。
- `InputHistoryTests`：顺序与去重；↑ 到顶不再前进；↓ 越过最新一条恢复浏览前的草稿（含附件与 MCP 提示词）；浏览中编辑则结束浏览；浏览中切换会话，存下的是 `Stash` 不是历史消息；`ShouldRecall` 的条件表（空输入框、光标在中间、有选区、预编辑中、补全打开、↓ 未在浏览）；含 command / 文件 tag / 图片的消息还原，旧的无 `Segments` 消息还原；翻页（用合成的 `HistoryMessageDto`，分两页，`LoadEarlierHistory` 后继续前进）。
- 不需要新的快照：界面外观没有变化。

## 9. 风险与待确认

1. **↑ 的触发规则**（§5.2）：还没开始浏览时用“光标在开头”而不是“光标在第一行”，是为了不顶掉正在写的草稿，代价是非空草稿要按两次；已经在浏览时放宽到第一 / 最后一行。用户没有明确确认过这条规则。
2. **编辑浏览中的历史消息会丢掉原来的草稿**（§5.3）：用户是有意改动，但原草稿不可找回。如果要保护，可以在编辑后仍保留 `Stash`，让再次 ↓ 越过最新一条时恢复它；目前没做，因为那时“正在浏览”的含义会变得模糊。
3. **翻页会把转录滚到底部**（§5.4）：见上；只在用户先向上滚动阅读、再在输入框里翻历史时才看得出来。
4. **历史里不含排队消息、MCP 提示词卡片**（§5.1）：有意从简，后续可补。

## 10. 不做的事

- 跨会话 / 跨项目 / 落盘的输入历史（用户选择仅当前会话）。
- 撤销记录与光标位置的保留。
- Alt+↑ 等备用快捷键、历史搜索（Ctrl+R）。
