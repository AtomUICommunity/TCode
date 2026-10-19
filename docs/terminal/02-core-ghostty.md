# TKCode 终端核心：libghostty-vt 绑定与 TKCode.Terminal.Core 设计

> 状态：设计稿 v1（2026-10-16）。上位文档：[01-architecture.md](01-architecture.md)（决策 D1–D9、§3 进程划分与一致性规则、§6.1 接口）。协议范围与应答字符串见 [10-protocol-compat.md](10-protocol-compat.md)。
> 范围：`src/TKCode.Terminal.Core`（门面 + Interop）、`native/ghostty`（上游 pin 与构建输入）、`tests/TKCode.Terminal.Core.Tests`（差分测试）。渲染、PTY、IPC 只在接口处涉及。
> 读者：实现 `TKCode.Terminal.Core` 的工程师。本文所有 API 名、枚举名保留原文。

## 0. 标注约定与来源

| 标记 | 含义 |
|---|---|
| `[H:file:行]` | **已核实**：ghostty `include/ghostty/vt/<file>` @ `b3dc3d7`（2026-10-15）。例：`[H:terminal.h:79-84]` |
| `[S:file:行]` | **已核实**：ghostty `src/terminal/<file>` @ `b3dc3d7` 的实现代码 |
| `[R01 §x]` `[R03 §x]` `[R04 §x]` | 调研报告 [research/01](research/01-emulator-cores.md)、[03](research/03-vt-protocol.md)、[04](research/04-rendering-performance.md) |
| `[A01 §x]` | [01-architecture.md](01-architecture.md) |
| `[RT]` | RoyalTerminal `native/ghostty-vt-extensions/README.md` @ `01833b8`（12 个修正补丁的说明） |
| `[DN]` | deblasis/libghostty-vt-dotnet @ `c6f23d8`（`ghostty-upstream.json`、`build/generate-bindings.sh`） |
| `[MH-0428]` | Mitchell Hashimoto, *Ghostty Is Leaving GitHub*（2026-04-28），https://mitchellh.com/writing/ghostty-leaving-github |
| **[推断]** | 基于已核实事实的设计推理，未实测 |
| **[待 spike]** | 必须在 M0/M1 用代码验证后才能定稿 |

---

## 1. 结论摘要

1. **绑定分三层**：`Interop`（从头文件生成的 `LibraryImport` 声明 + blittable 结构体）→ `Ghostty*` 安全句柄与小型包装 → `TerminalCore` 门面。渲染层、IPC 层只看见门面类型（`FrameSnapshot` 等），看不见任何 `Ghostty*` 类型 [A01 §6.1]。
2. **v1 用到 151 个 C 函数**（头文件共 208 个，含 5 个 wasm 专用）；清单见 §5。每帧 FFI 调用数约 `6 + 3×脏行 + 2×(每行不同样式数)`，200×60 全量重绘约 500 次调用，远低于调研时估计的 2.5 万次（因为整行用 `CELLS_RAW` 一次取出）[R01 §4.1.3][H:render.h:446-460]。**v1 不需要为了性能加 Zig shim**；功能缺口由 §2.4 的 overlay 和 §8.9 的可选 ext 导出解决。
3. **libghostty-vt 的 VT 处理不依赖回调的返回值，也不读时钟**：标题、pwd、语义区、2026 等状态在调用回调**之前**就已写入终端 [S:stream_terminal.zig:1871-1886,1973-1984][H:terminal.h:1529]；查询类回调只产生应答字节。所以"镜像核心挂全部 effects、UI 侧核心丢弃应答"不会让两份状态分叉 **[推断，依据代码阅读]**。这是 [A01 §3.1] 双核心方案成立的前提，差分测试要持续守住它（§15.4）。
4. **v1 带一个小的 overlay 补丁集**（§2.4，共 4 项，每项几行到几十行），用来补 libghostty 与 01 设计之间的缺口：
   - **O1 登记 mode 9001**（win32-input-mode）：模式表里没有它 [S:modes.zig:362-416]，未知 CSI 也不会进回调 [H:terminal.h:131-160]。追加到模式表末尾后，DECRQM、RIS、快照都自动正确。
   - **O2 停用 mode 5522**（Kitty 粘贴事件）：[A01 §3.2 规则 7] 要求 v1 关闭；但镜像核心为 OSC 52 读安装了 `clipboard_read`，库就会把 5522 报告为"可识别"[S:stream_terminal.zig:1908-1920]。把它在模式表里标成 `disabled`。
   - **O3 OSC 52 读允许"延后应答"**：库要求在回调返回前应答，否则自动回一个空剪贴板 [H:terminal.h:969-981][S:stream_terminal.zig:1072-1101]；这与 [A01 §3.2 规则 6] 的"host 转发给 UI、稍后写回"冲突。补丁让镜像核心可以不应答、由 host 稍后自己写 `OSC 52` 应答。
   - **O4 导出 modifyOtherKeys 状态**：[A01 §5.2] 的 `InputModes` 需要它，C API 没有 getter（只能经 `ghostty_key_encoder_setopt_from_terminal` 间接读取 [H:key/encoder.h:169-186]）。
   - 补丁打不上时的退路逐项写在 §2.4。
5. **其他与 01 / 04 需要对齐的点**（详见 §17）：
   - **Kitty 图像不在快照里** [S:snapshot/terminal.zig:238-242]。重连后 UI 侧核心没有图像，与镜像核心不一致（§11.5）。
   - **快照格式 v1 不承诺二进制兼容** [H:snapshot.h:112-113]：与 [04 §10] 一致，revive 保存裁剪后的快照 + `tail.vt` 回退（§9.4）。
   - **Windows 上 ConPTY 的字宽模式在创建时固定**，无法跟随程序运行时切换 2027；Windows 默认改为字形簇模式（§13.4，需用户确认，影响 D9）。
6. **RoyalTerminal 修过的 12 个问题，在 HEAD 上至少 6 个仍然存在**（代码中仍无对应 dirty 标记或边界检查），其余 6 个需要移植回归用例才能判断（§15.5）。增量渲染 vs 全量重建的差分测试是 M1 的硬性验收项。

---

## 2. 上游 pin 与升级

### 2.1 `native/ghostty/ghostty-upstream.json`

参考 [DN] 的同名文件（字段：repo、branch、commit、upstreamVersion、lastUpdated），扩展为：

```jsonc
{
  "schema": 1,
  "repo": "https://github.com/ghostty-org/ghostty.git",     // 主源
  "mirrors": [                                               // 主源不可用时按序尝试（§2.5）
    "https://<future-primary-host>/ghostty-org/ghostty.git"
  ],
  "commit": "b3dc3d794513c11633e7c3347935693957744e83",      // 40 位，唯一权威
  "branch": "main",                                          // 只用于"有多少新提交"的提示，构建不用
  "upstreamVersion": "1.3.2-dev",                            // build.zig.zon 的 .version
  "commitDate": "2026-10-15T14:12:20-07:00",
  "sourceSha256": "<git archive --format=tar <commit> 的 SHA-256>",
  "headersSha256": "<include/ghostty 目录按路径排序后逐文件 SHA-256 再整体 SHA-256>",
  "zig": { "version": "0.16.0", "sha256": { "macos-aarch64": "...", "linux-x86_64": "...", "windows-x86_64": "..." } },
  "build": {
    "optimize": "ReleaseFast",
    "args": ["-Demit-lib-vt"],
    "perRid": {
      "win-x64": ["-Dtarget=x86_64-windows-gnu", "-Dcpu=baseline", "-Dsimd=false"],   // §3.2
      "win-arm64": ["-Dtarget=aarch64-windows-gnu"],
      "osx-arm64": ["-Dtarget=aarch64-macos"], "osx-x64": ["-Dtarget=x86_64-macos"],
      "linux-x64": ["-Dtarget=x86_64-linux-gnu.2.28"], "linux-arm64": ["-Dtarget=aarch64-linux-gnu.2.28"]
    }
  },
  "typeManifestSha256": { "osx-arm64": "<ghostty_type_json() 输出的 SHA-256>", "...": "..." },
  "overlays": [                                              // 本地修正补丁（§2.3），没有就为空数组
    { "id": "dirty-zero-width-append", "file": "src/terminal/Terminal.zig",
      "originalSha256": "<打补丁前整文件哈希>", "patch": "overlays/0001-dirty-zero-width.patch",
      "upstreamIssue": null, "rtRef": "RT 第 1 项" }
  ],
  "abi": { "functionCount": 208, "boundFunctionCount": 151 },
  "reviewedBy": "<github id>",
  "updatedAt": "2026-10-16T00:00:00Z"
}
```

- `commit` 之外的哈希都是"防止静默漂移"的校验项。构建脚本先校验 `sourceSha256`，再应用 overlay（校验 `originalSha256`，不匹配就失败，与 [RT] 的做法相同），然后构建。
- `typeManifestSha256` 让 CI 能发现"同一 commit 在不同 Zig 补丁版本下类型布局变了"这类意外 **[推断]**。
- glibc 目标后缀 `.2.28` 对应 [A01 §1] 的 RHEL 8 下限 **[推断：Zig 支持 `-gnu.2.28` 形式的 glibc 版本目标]**。

### 2.2 升级流程与检查单

节奏：每月一次（与 [R01 §6] 建议一致），或者上游修复了我们 overlay 覆盖的问题时提前升级。一次升级是一个独立 PR，标题 `ghostty: <old7>..<new7>`。

| # | 步骤 | 工具 / 产物 | 通过条件 |
|---|---|---|---|
| 1 | 生成上游差异摘要 | `git log --oneline <old>..<new> -- include/ghostty/vt src/terminal`；`git diff --stat` | PR 描述里附上摘要，标出 `include/` 变化 |
| 2 | **头文件 API 差异** | `tools/GhosttyBindgen diff-headers`：比较两版本的函数签名、枚举值、结构体字段 | 列出新增 / 删除 / 签名变化。签名变化必须逐条处理 |
| 3 | **类型清单差异** | 在新旧两个库上分别运行 `ghostty_type_json()`，用 `tools/GhosttyBindgen diff-manifest` 比较 | 列出所有 offset、size、枚举值、packed 位偏移的变化；`GhosttyCell` 位布局变化必须更新 §6.5 的转换测试 |
| 4 | 重新生成绑定 | `tools/GhosttyBindgen generate`（§4.1） | 生成文件与提交的文件逐字一致，或差异已被审阅 |
| 5 | 布局自检 | `GhosttyLayoutTests`（§4.2）在 6 个 RID 上运行 | 全部结构体、枚举、`GhosttyCell` 位布局与清单一致 |
| 6 | overlay 复核 | 每个 overlay：上游是否已修？`originalSha256` 是否仍匹配？ | 已修复的删除；不匹配的重新审阅后更新 |
| 7 | 单元 + 回放测试 | `TKCode.Terminal.Core.Tests` | 全绿 |
| 8 | **差分测试**（§15） | 增量 vs 全量、镜像 vs UI、快照往返 | 0 差异；新增差异需定位到上游提交，并决定 overlay 还是等待上游 |
| 9 | esctest2 / ucs-detect 回归 | CI job | 通过数不低于上一版本；新失败项进 known-bugs 并说明原因 |
| 10 | 吞吐基准 | `TKCode.Terminal.Benchmarks` | 解析吞吐、帧捕获耗时回退不超过 10% |
| 11 | AOT 发布 + 运行二进制 | `dotnet publish -p:PublishAot=true` × 6 RID；运行 `tkcode-ptyhost --selftest` | 0 个 IL 警告；自检通过 |
| 12 | 更新 `ghostty-upstream.json` | 新 commit、哈希、`reviewedBy` | — |

快照兼容：升级后 pty host 和 Desktop 一起发布；`ghosttyBuildId` 不同的旧 host（keepAlive 会话）改用 VT 格式 attach（[04 §1.5、§8.5]）。revive 文件的 `snapshot.bin` 在构建不同时不用，改用 `tail.vt`（§9.4、[04 §10.5]）。

### 2.3 overlay（本地补丁）政策

- 默认**不打补丁**，问题先报上游。
- 只在满足以下全部条件时加 overlay：差分测试能稳定复现；影响用户可见的正确性或安全；上游 30 天内没有修复。
- overlay 以 patch 文件形式放在 `native/ghostty/overlays/`，构建时应用到源码副本，不修改 submodule / 下载的源码目录（[RT] 的做法）。
- 新增**导出函数**（§8.9 的功能缺口）放在独立的 `native/ghostty/ext/` Zig 包里，以 `tkcode_ghostty_*` 前缀导出，与上游符号隔离。构建方式沿用 [RT]：生成上游 Zig 根文件的副本，在末尾追加 `ext/*.zig` 的导出；不改上游目录。

### 2.4 v1 overlay 补丁计划（O1–O4）

上面"默认不打补丁"的例外：这 4 项是 01 设计决策（D9 的 win32-input-mode、规则 6、规则 7、`InputModes`）直接要求的，从 M0 起就有。每项都有退路，补丁打不上（例如上游重构导致 `originalSha256` 不匹配且短期无法改写）时启用退路，功能降级但不阻塞发布。

| # | 文件 | 改动 | 验证 | 退路 |
|---|---|---|---|---|
| **O1** | `src/terminal/modes.zig` | 在 `entries` **末尾**追加 `.{ .name = "win32_input", .value = 9001, .default_configurable = false }`。`setMode` 的 `else => {}` 分支已覆盖，无副作用；RIS 复位为 false；DECRQM 自动应答 1/2 | ① `CSI ? 9001 h` 后 `DATA_MODE(9001)` 为 true；② `CSI ? 9001 $ p` → `CSI ? 9001 ; 1 $ y`；③ RIS 后为 false；④ 快照往返保持 | 视图 actor 与会话 actor 在 `vt_write` 前运行托管扫描器 `DecPrivateModeScanner`（§13.5）；DECRQM 9001 只能答 0 |
| **O2** | `src/terminal/modes.zig` | `kitty_paste_events`（5522）条目加 `.disabled = true`（字段语义："Ghostty will not allow it to be set or queried" [S:modes.zig:332-335]）| DECRQM 5522 → 0；`CSI ? 5522 h` 后粘贴仍是 2004 包裹的文本 | 两份核心都不改：镜像核心对 DECRQM 5522 答"已识别"，程序可能以为有粘贴事件。UI 侧核心不装 `clipboard_read`，粘贴仍按文本发出 —— 字节层面一致，只是声明不准确 |
| **O3** | `src/terminal/stream_terminal.zig` `clipboardRead` | 新增终端级开关 `clipboard_read_deferred`（由 ext 导出 `tkcode_ghostty_set_clipboard_read_deferred(t, bool)` 设置）。开关打开时，回调返回而未调用 `reply` 的情况**不再**发送空剪贴板（跳过 [S:stream_terminal.zig:1096-1100] 的兜底应答）；并在 `GhosttyClipboardRead` 末尾追加 `terminator` 字段（sized struct 允许追加），供 host 稍后用同一终止符应答| 镜像核心回调不应答 → PTY 无输出；host 稍后写入的 `ESC ] 52 ; c ; <base64> <原终止符>` 被程序正确接收 | 回调内**同步**阻塞：IPC 询问 UI，最多等 30 s，超时回 `DENIED`（库随之发空剪贴板）。阻塞期间该会话暂停解析 |
| **O4** | ext（追加导出）| `bool tkcode_ghostty_modify_other_keys_2(GhosttyTerminal)`：读 `terminal.flags.modify_other_keys_2`（[S:Terminal.zig:114]；RoyalTerminal 有同名扩展 [RT]）| `CSI > 4 ; 2 m` 后为 true，`CSI > 4 ; 0 m`、RIS 后为 false | 按键改在视图 actor 上用 `setopt_from_terminal` 编码（多一次线程跳转，约 10–50 µs [R04 §5.1]）|

O1 的快照影响：快照把三套模式按 `ModePacked` 的位序写入 8 字节槽，当前占 43 位、43–63 位保留且"解码器忽略保留位"[S:snapshot/terminal.zig:164-215,474-476]。9001 追加在末尾占第 43 位：同一构建的两个进程之间往返正确；被未打补丁的解码器读取时只是丢掉该位。上游注释说"新增模式需要提升快照版本"，我们不改版本号，因为快照只在同一构建之间使用（[04 §8.5] 的跨构建 attach 走 VT 格式）**[待 spike：确认没有 comptime 断言限定 43 位]**。

dirty 漏标（§15.5 的 1、2、6、7）若上游 30 天内未修，再按同样格式加 O5–O8。已加入的：O5（行元数据变化漏标：OSC 133、wrap / wrap_continuation、残留 spacer_head）；O6（mode 2027 关闭时，零宽码点追加到前一个 cell 没有标脏，增量帧沿用旧行；每晚长跑 fuzz 发现，补丁 `0004-mark-row-dirty-on-zero-width-grapheme-append.patch`，一行 `cursorMarkDirty()`）。后续同类问题从 O7 起编号。

### 2.5 Ghostty 迁出 GitHub 的应对

已知事实 [MH-0428]：项目已决定离开 GitHub，目的地未公布，迁移是渐进的；**GitHub 上会保留当前 URL 的只读副本**；原因是 Issues / PR / Actions 的可靠性，不是 Git 本身。截至本文日期，上游提交仍在 GitHub 上进行（`b3dc3d7`，2026-10-15）。

| 风险 | 对策 |
|---|---|
| 主仓库地址变更 | 构建只认 `commit`，不认 URL。`repo` + `mirrors` 按序尝试；任何一个源拿到该 commit 且 `sourceSha256` 匹配即可 |
| 只读副本停止同步 | 升级时脚本比较各源的 `main` 头；发现主源变化时更新 `repo` 字段（单独的 PR） |
| 源码不可获取（离线、全部源失效） | CI 缓存：每次升级时把 `git archive` 结果上传到 TKCode 自己的 release 资产 `ghostty-src-<commit>.tar.gz`，作为最后一个 mirror |
| issue 跟踪迁移 | `overlays[].upstreamIssue` 存完整 URL，不存 `#1234` 这样的短号 |
| 依赖（Zig 包）托管变化 | `build.zig.zon` 的依赖由 Zig 按哈希获取；CI 设置 `ZIG_GLOBAL_CACHE_DIR` 并缓存，升级时预热 **[推断]** |
| 上游 CI 不再在 GitHub Actions 上运行 | 不影响我们：我们自己跑 6 RID 构建、测试和 esctest；上游 CI 只作参考 |

---

## 3. 原生构建（要点）

完整构建文档另写，这里只列影响核心设计的事实。

| 项 | 内容 | 来源 |
|---|---|---|
| 工具链 | Zig **0.16.0** 精确版本；`zig build -Demit-lib-vt` 同时产出动态库和静态库 | [R01 §4.1.4] |
| 产物 | `libghostty-vt.dylib` / `libghostty-vt.so` / `ghostty-vt.dll`；`[LibraryImport("ghostty-vt")]` 可以在三个平台上解析到 | [DN:NativeMethods.cs:7]（已核实） |
| 优化级别 | 发布用 `ReleaseFast`（与上游 Ghostty 一致）；差分测试和 fuzz 额外跑一套 `ReleaseSafe` 构建，让 Zig 的 `assert`、越界检查变成可见的 panic | **[推断]**；理由见 §15.5 第 4、8 项 |
| 动态链接 | v1 只用动态库，放在 `runtimes/<rid>/native/`。静态链接进 NativeAOT 留到以后（compiler_rt 冲突风险）| [R01 §6] |
| Windows x64 | RoyalTerminal 发现默认构建可能在启动阶段执行 AVX 指令，发布版改用基线 CPU 并关闭 SIMD | [R01 §4.1.4] |
| 构建信息 | `ghostty_build_info` 只提供版本字符串和 commit（`VERSION_BUILD`），**没有构建哈希**[H:build_info.h:84-125]；`ghostty_type_json()` 的清单含 `library_version`、`commit`、`dirty` 字段 [S:c/types.schema.json] | 已核实 |
| 握手标识 | `ghosttyBuildId` = `VERSION_STRING` + `"+sha256:"` + 原生库文件字节的 SHA-256（[A01 §3.2 规则 3]、[04 §10.2] 的格式，例如 `1.3.2-dev+sha256:6b0e…`）。由 `GhosttyRuntime` 在启动时计算一次（约 2 MB，毫秒级）。overlay 改变了库字节，所以自动体现在哈希里 | 已对齐 01 / 04 |
| 禁用 Glyph Protocol | Ghostty 自有的 APC 协议，默认开启 [H:terminal.h:2181-2190]；注册的字形不进快照，开启会导致重连后不一致。两份核心都设 `GLYPH_PROTOCOL=false` | 已核实 + 推断 |

---

## 4. 绑定层（`TKCode.Terminal.Core.Interop.Ghostty`）

### 4.1 生成方法

候选方案：

| 方案 | 做法 | 优点 | 缺点 |
|---|---|---|---|
| A 纯手写 | 手写约 151 个 `LibraryImport` 和约 60 个结构体 | 完全可控 | 升级时容易漏掉签名变化（[DN] 记录过：`ghostty_terminal_new` 去掉了按值传入的选项结构体，名字没变，"name-existence check" 仍然全绿，ABI 却已损坏） |
| B ClangSharp 直接生成 C# | `ClangSharpPInvokeGenerator` | 现成 | 生成的是 `DllImport` 风格；[DN] 踩过的坑：`vt.h` 只有 include，不传 `--traverse` 就什么都不生成且退出码为 0；退出码是诊断条数而不是成败 [DN:build/generate-bindings.sh] |
| **C ClangSharp 解析 + 自写发射器（采用）** | ClangSharp 以 XML 模式输出 AST 摘要（`--output-mode Xml`），自写 `tools/GhosttyBindgen` 读取 XML，按我们的规则发射 C# | 签名变化一定会体现在生成文件的 diff 里；发射规则（`LibraryImport`、函数指针、`nint` 句柄）统一 | 需要维护一个小工具（约 800 行） |

> `--output-mode Xml` 的确切参数名 **[待 spike]**；如果 XML 模式不可用，退路是 libclang 的 C API（`clang_visitChildren`）直接在工具里解析头文件，工作量相近。

`tools/GhosttyBindgen` 的规则：

| C 形态 | C# 形态 |
|---|---|
| `GHOSTTY_API T fn(args)` | `[LibraryImport("ghostty-vt", EntryPoint = "fn")] internal static partial T Fn(args);`，`[SuppressGCTransition]` 只加在 §5 标记为"纯函数、极快"的少数调用上 **[待 spike：确认这些函数不会回调、不会长时间运行]** |
| 不透明句柄 `typedef struct XImpl* X` | `nint`（Interop 层）；门面层用 SafeHandle 包装（§4.6） |
| `typedef enum GHOSTTY_ENUM_TYPED {...}` | `enum X : int`（头文件保证底层为 int [H:types.h:36-83]）；`*_MAX_VALUE` 哨兵不生成 |
| `typedef uint16_t GhosttyMode` 等整型别名 | 同宽整型，或 `readonly struct` 包装（`GhosttyMode` 用包装，带 `New(value, ansi)`） |
| 结构体 | `[StructLayout(LayoutKind.Sequential)]` blittable struct；含 `bool` 的字段保持 `bool`（启用 `DisableRuntimeMarshalling` 后 `bool` 按 1 字节处理） |
| 联合体（带 `_padding[n]`） | `[StructLayout(LayoutKind.Explicit, Size = N)]` + `[FieldOffset(0)]` 各臂 |
| 回调函数指针类型 | `delegate* unmanaged[Cdecl]<...>`；返回 `bool` 的回调在签名里用 `byte`，避免平台差异 |
| `size_t` / `uintptr_t` / `intptr_t` | `nuint` / `nuint` / `nint` |
| `static inline` 帮助函数（`ghostty_mode_new` 等）和宏（`GHOSTTY_INIT_SIZED`、`GHOSTTY_COLOR_PALETTE_MASK_*`） | 不是导出符号，在 C# 手写等价实现 [H:modes.h:118-144][H:types.h:351-353] |
| `wasm.h` 的 5 个函数 | 不生成 |

程序集级别：`[assembly: DisableRuntimeMarshalling]`；项目 `IsAotCompatible=true`、`AllowUnsafeBlocks=true`。

### 4.2 布局校验：`ghostty_type_json`

上游明确要求绑定"从清单读取 offset、size、对齐、枚举值、packed 位布局，不要硬编码，并在初始化时校验所需的描述符"；位布局"不承诺跨版本稳定" [H:types.h:355-410]。

| 时机 | 校验内容 | 失败时 |
|---|---|---|
| **单元测试**（每个 RID 的 CI） | 对 Interop 中每个结构体：`Unsafe.SizeOf<T>()` == 清单 `size`；每个字段的 `Marshal.OffsetOf`（测试里允许反射）== 清单 `offset`；每个枚举：C# 值 == 清单 `values`；`schema == 1`；`abi.pointer_size`、`endian` 与进程一致 | 测试失败 |
| **启动时**（Release 也做，成本 < 1 ms） | 只校验热路径依赖的部分：`GhosttyCell` 的 packed 描述（content_tag、content 各臂、style_id、wide、hyperlink、semantic_content 的 `lsb` / `width`）和 6 个 sized struct 的 `size`；解析结果存进 `GhosttyCellLayout`（§6.5） | 抛 `GhosttyAbiMismatchException`，pty host 拒绝启动会话，Desktop 显示"终端组件与原生库不匹配" |

启动时的 JSON 解析用 `Utf8JsonReader` 手写遍历（只取需要的节点），不用 `JsonSerializer`，保持 AOT 安全和零反射 **[推断]**。

`GhosttyCell` 在 HEAD 上的布局（只用于理解，代码一律从清单读取）[S:page.zig:2137-2160]：

| 位 | 字段 | 说明 |
|---|---|---|
| 0–1 | `content_tag` | 0 codepoint、1 codepoint_grapheme、2 bg_color_palette、3 bg_color_rgb |
| 2–25 | `content` | codepoint：低 21 位；palette：低 8 位；rgb：r、g、b 各 8 位（r 在最低位）|
| 26–41 | `style_id` | 页内样式 id，0 = 默认样式；**只在同一页内有意义** |
| 42–43 | `wide` | 0 narrow、1 wide、2 spacer_tail、3 spacer_head |
| 44 | `protected` | DECSCA |
| 45 | `hyperlink` | 有超链接 |
| 46–47 | `semantic_content` | 0 output、1 input、2 prompt |
| 48–63 | padding | |

### 4.3 sized struct

头文件里约 20 个结构体首字段是 `size_t size`，调用方用 `GHOSTTY_INIT_SIZED` 初始化 [H:types.h:325-353]。规则：

1. **我们传给库的**（如 `GhosttyRenderStateCursor`、`GhosttyFormatterTerminalOptions`、`GhosttyPaste`、`GhosttyTerminalMemoryUsage`）：一律 `new T { size = (nuint)Unsafe.SizeOf<T>() }`，由生成器为每个 sized struct 生成 `static T Create()`。
2. **库传给我们的**（回调参数，如 `GhosttyTerminalSemanticPrompt*`、`GhosttyClipboardRead*`、`GhosttyTerminalProgramStatus*`）：先读 `size`，**只访问 `offset + 字段大小 <= size` 的字段**。生成器为每个这样的结构体生成 `static bool HasField(T* p, int fieldOffset, int fieldSize)` 帮助方法。
3. 我们回复给库的（`GhosttyClipboardReadReply`、`GhosttyClipboardWriteReply`）：同规则 1。
4. "frozen layout" 的结构体（`GhosttyClipboardContent`、`GhosttyTerminalModeConfig`）没有 size 字段 [H:terminal.h:710,1928]。

pin 机制保证库和绑定来自同一 commit，所以 size 不匹配只可能是生成器或布局 bug，测试会捕获。

### 4.4 按值传递的结构体

以下函数按值传结构体：`ghostty_terminal_scroll_viewport`（24 B）、`ghostty_terminal_grid_ref` / `point_from_grid_ref`（`GhosttyPoint` 24 B）、`ghostty_terminal_select_output`（`GhosttyGridRef`）、`ghostty_formatter_terminal_new`（`GhosttyFormatterTerminalOptions`）、`ghostty_terminal_selection_format_*`、`ghostty_mouse_event_set_position` / `get_position`（两个 float，按值返回）。

`LibraryImport` 对 blittable 结构体按值传递由运行时处理各平台 ABI（Windows x64 大于 8 字节隐式传引用、SysV 大于 16 字节走内存、Apple arm64 HFA 规则）。**[待 spike]**：在 6 个 RID 上为每个按值函数写往返测试（例如 `ghostty_terminal_grid_ref` → `ghostty_terminal_point_from_grid_ref` 坐标一致；`set_position` → `get_position` 一致）。

### 4.5 回调：`[UnmanagedCallersOnly]` + GCHandle

```csharp
// 每个 TerminalCore 一个 GCHandle，作为 GHOSTTY_TERMINAL_OPT_USERDATA。
_self = GCHandle<TerminalCore>.Alloc(this);                       // .NET 9+ 泛型 GCHandle
Native.TerminalSet(_t, GhosttyTerminalOption.Userdata, (void*)GCHandle<TerminalCore>.ToIntPtr(_self));
Native.TerminalSet(_t, GhosttyTerminalOption.WritePty,
    (void*)(delegate* unmanaged[Cdecl]<nint, nint, byte*, nuint, void>)&Callbacks.WritePty);

static class Callbacks
{
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void WritePty(nint terminal, nint userdata, byte* data, nuint len)
    {
        var core = GCHandle<TerminalCore>.FromIntPtr(userdata).Target;
        try { core.OnWritePty(new ReadOnlySpan<byte>(data, checked((int)len))); }
        catch (Exception ex) { core.RecordCallbackFault(ex); }      // 绝不让异常穿过原生帧
    }
}
```

规则：

| 规则 | 说明 |
|---|---|
| 不抛异常 | `[UnmanagedCallersOnly]` 方法里的未处理异常会直接终止进程。所有回调体包 `try/catch`，异常记入 `core.CallbackFaults` 并写日志；`Write` 返回后如有故障，按严重程度决定是否把核心标为 `Faulted` |
| 借用数据立即复制 | 回调参数中的字符串、数组只在回调期间有效 [H:terminal.h:558-565,1885-1903] |
| 重入保护 | 进入回调时置 `_inCallback = true`；门面所有公开方法检查它，回调内调用公开方法抛 `InvalidOperationException`。门面内部的回调处理器可以调用**只读**的 `ghostty_terminal_get`、`grid_ref`、`grid_ref_track`、`render_state_update`（头文件只禁止重入 `vt_write` [H:terminal.h:79-84]，render hold 文档明确要求在回调里调用 `render_state_update` [H:terminal.h:1777-1783]） |
| 进程级回调（sys） | `GHOSTTY_SYS_OPT_DECODE_PNG`、`GHOSTTY_SYS_OPT_LOG` 是进程全局的 [H:sys.h:16-30]。`GhosttyRuntime.EnsureInitialized()` 在第一个 `TerminalCore.Create` 之前调用一次，userdata 传 `null`，处理函数必须线程安全（多个核心 actor 可能同时调用） |
| GCHandle 生命周期 | `Dispose` 中先 `ghostty_terminal_free`，再 `GCHandle.Free`；顺序反了会让最后一次回调拿到已释放的句柄 |

### 4.6 句柄与生命周期

| 原生对象 | 包装 | 释放函数 | 绑定终端？ |
|---|---|---|---|
| `GhosttyTerminal` | `TerminalHandle : SafeHandle` | `ghostty_terminal_free` | — |
| `GhosttyRenderState` | `RenderStateHandle` | `ghostty_render_state_free` | 否（只在 update 时借用）|
| `GhosttyRenderStateRowIterator` / `RowCells` | 普通 `IDisposable` 字段，由 `FrameCapturer` 持有 | `*_free` | 否 |
| `GhosttyKeyEncoder` / `GhosttyKeyEvent` / `GhosttyMouseEncoder` / `GhosttyMouseEvent` | 同上，由 `InputEncoder` 持有，复用 | `*_free` | 否 |
| `GhosttySearch` | `SearchHandle` | `ghostty_search_free` | **是**：free 必须与终端访问串行 [H:search.h:91-96] |
| `GhosttyFormatter` | 用完即释放（`using`）| `ghostty_formatter_free` | 是 |
| `GhosttyTrackedGridRef` | `TrackedRef` | `ghostty_tracked_grid_ref_free` | 是（每个都给终端变更增加簿记开销，要节制使用 [H:grid_ref.h:74-76]）|
| `GhosttySelectionGesture` | 由 `SelectionController` 持有 | `ghostty_selection_gesture_free(gesture, terminal)` | 是（free 需要传终端）|
| `GhosttySnapshotDecoder` | `RestoreSession` 持有 | `ghostty_snapshot_decoder_free` | 借用 READY 返回的终端 |
| `GhosttyKittyGraphicsPlacementIterator` | `FrameCapturer` 持有 | `*_free` | 数据只在终端未变更时有效 |

**终结器线程问题**：SafeHandle 的 `ReleaseHandle` 可能在终结器线程运行，而 libghostty 的终端不是线程安全的。规则：

1. "绑定终端"的子对象**不实现终结器释放**：它们由 `TerminalCore` 统一持有，`TerminalCore.Dispose()`（在 actor 线程）按"子对象 → 终端"的顺序释放。
2. 只有 `TerminalHandle` 自身有终结器兜底。终结器运行时整个 `TerminalCore` 已不可达，不存在并发访问；此时先释放仍挂着的子对象（search、tracked ref 允许在终端之前或之后释放 [H:types.h:192-198][H:grid_ref.h:85-87]），再释放终端。
3. Debug 构建中 `TerminalCore` 记录所属线程 id，每个公开方法断言 `Environment.CurrentManagedThreadId == _ownerThread`。

### 4.7 错误码映射

`GhosttyResult` 值见 [H:types.h:85-110]。

| 原生返回 | 门面行为 |
|---|---|
| `GHOSTTY_SUCCESS (0)` | 正常 |
| `GHOSTTY_OUT_OF_MEMORY (-1)` | 抛 `TerminalOutOfMemoryException`（派生自 `InsufficientMemoryException`）；`Write` 路径不会出现（`vt_write` 永不失败 [H:terminal.h:3085-3089]）|
| `GHOSTTY_INVALID_VALUE (-2)` | 视为**绑定或调用顺序错误**：抛 `GhosttyInvalidValueException(functionName)`。例外：`ghostty_snapshot_encode` 在"解析器未处于 ground 且未开启续传跟踪"时也返回它 [H:snapshot.h:309-312]，门面把它映射为 `SnapshotDeferred`（§9.2）|
| `GHOSTTY_OUT_OF_SPACE (-3)` | 门面内部按返回的所需大小扩容重试，不对外暴露 |
| `GHOSTTY_NO_VALUE (-4)` | `TryXxx` 返回 `false` / 可空返回 `null` |
| `GHOSTTY_IO_ERROR (-5)` | 我们的 writer/reader 回调返回了 `false`：抛 `IOException`，内含回调里捕获的原始异常 |
| `GHOSTTY_LIMIT_EXCEEDED (-6)` | 抛 `TerminalLimitExceededException`（快照或续传超过上限）|
| `GHOSTTY_REJECTED (-7)` | 不是异常：`PasteResult.RequiresConfirmation` [H:types.h:103-108] |
| 其他负值 | `GhosttyException(code, functionName)`（未来版本新增的错误码）|

辅助：`static void Check(GhosttyResult r, [CallerArgumentExpression] string expr = "")`，热路径用 `[MethodImpl(AggressiveInlining)]` 的 `if (r != 0) Throw(...)`，抛出部分放在不内联的方法里。

`GHOSTTY_TERMINAL_DATA_VT_PROCESSING_ERROR`（VT 处理中出现过非预期错误，不能清除）[H:terminal.h:2850-2864]：每次 `CaptureFrame` / 每 5 秒读一次，首次为 true 时写一条警告日志并上报遥测，不改变行为。

---

## 5. v1 函数清单

"用"列：M = 镜像核心（pty host）用，V = UI 侧核心（视图）用，T = 只在测试 / 工具中用。共 151 个。

### 5.1 terminal（18 个，全部使用）

| 函数 | 用 | 用途 |
|---|---|---|
| `ghostty_terminal_new` / `_free` | MV | 创建（只有列、行参数 [H:terminal.h:2986-2989]）、释放 |
| `ghostty_terminal_reset` | MV | 用户触发的"重置终端"（作为日志记录，两边同时执行）|
| `ghostty_terminal_resize` | MV | 列、行 + cell 像素；会 reflow、解除 2026、发 2048 报告 [H:terminal.h:3019-3048] |
| `ghostty_terminal_set` | MV | 全部选项与回调（§6.2、§7）|
| `ghostty_terminal_vt_write` | MV | 喂字节；永不失败 |
| `ghostty_terminal_vt_write_until_ground` | M | 快照前把流推进到 ground 的备用手段（§9.2）|
| `ghostty_terminal_continuation_write` / `_buf` / `_alloc` | MT | 续传字节；镜像核心编码快照时由库内部使用，测试中直接校验 |
| `ghostty_terminal_scroll_viewport` | V | 按行 / 页 / 顶 / 底 / 绝对行 / 按 prompt 滚动 |
| `ghostty_terminal_compression_activity` / `_compress` | MV | 回滚压缩调度（§7.3）|
| `ghostty_terminal_get` / `_get_multi` | MV | 读模式、光标、滚动条、标题、pwd、颜色、内存占用等 |
| `ghostty_terminal_grid_ref` / `_grid_ref_track` / `_point_from_grid_ref` | MV | 超链接解析、选区端点换算、命令边界锚点 |

### 5.2 render state（22 个，全部使用）

`ghostty_render_state_new/free/update/begin_update/end_update/clean/get/get_multi/set`，`ghostty_render_state_row_iterator_new/free/next/next_dirty`，`ghostty_render_state_row_get/get_multi/set`，`ghostty_render_state_row_cells_new/next/select/get/get_multi/free`。只在 V 用（镜像核心不渲染）。`begin_update`/`end_update` 在 v1 不用（actor 独占终端，没有锁可省），保留绑定供 R1 阶段评估。

### 5.3 screen / style / grid ref（15 个）

| 函数 | 用 | 用途 |
|---|---|---|
| `ghostty_cell_get` / `_get_multi` | T | 测试中逐字段解码，作为 §6.5 位运算转换的对照 |
| `ghostty_row_get` / `_get_multi` | V | 解码 render state 给出的 `GhosttyRow`（wrap、wrap_continuation、semantic_prompt、hyperlink、kitty placeholder）；行布局**不透明**，不能自己解位 [H:screen.h:44-53] |
| `ghostty_style_default` / `_is_default` | VT | 默认样式比较 |
| `ghostty_grid_ref_cell` / `_row` / `_graphemes` / `_hyperlink_uri` / `_style` | MVT | 超链接 URI（只有这个 API 能取到）、测试中的独立全量重建（§15.2）|
| `ghostty_tracked_grid_ref_free` / `_has_value` / `_point` / `_set` / `_snapshot` | MV | 命令边界锚点（§12）、选区锚点 |

### 5.4 键盘 / 鼠标 / 焦点编码（34 个）

| 函数 | 用 | 用途 |
|---|---|---|
| `ghostty_key_encoder_new/free/setopt/setopt_from_terminal/encode`（5）| V | 按键编码（UI 线程，选项来自 `InputModes` 逐项 `setopt`，§13.1）；`setopt_from_terminal` 只在 O4 退路中使用（它还会把 `MACOS_OPTION_AS_ALT` 重置为 false [H:key/encoder.h:177-179]）|
| `ghostty_key_event_new/free/set_action/set_key/set_mods/set_consumed_mods/set_composing/set_utf8/set_unshifted_codepoint`（9）| V | 构造按键事件（对象复用）；对应的 7 个 `get_*` 只在 T 用 |
| `ghostty_key_event_get_*`（7）| T | 测试 |
| `ghostty_mouse_encoder_new/free/setopt/setopt_from_terminal/reset/encode`（6）| V | 鼠标上报编码（UI 线程）；`EVENT`、`FORMAT`、`SIZE`、`ANY_BUTTON_PRESSED`、`TRACK_LAST_CELL` |
| `ghostty_mouse_event_new/free/set_action/set_button/clear_button/set_mods/set_position`（7）| V | 构造鼠标事件；`get_*` 3 个只在 T 用（不计入）|
| `ghostty_focus_encode` | V | `CSI I` / `CSI O`；调用前检查 1004 模式 |

### 5.5 paste（3 个）

`ghostty_terminal_paste`（V，§13.3）、`ghostty_paste_is_safe`（V，"总是确认多行粘贴"选项的预检）、`ghostty_paste_encode`（M：Agent 写入时按镜像核心的 2004 状态编码）。

### 5.6 selection（21 个，全部使用，V）

`ghostty_terminal_select_word/word_between/line/all/output`、`ghostty_terminal_selection_format_buf/format_alloc/adjust/order/ordered/contains/equal`、`ghostty_selection_gesture_new/free/reset/get/get_multi`、`ghostty_selection_gesture_event_new/free/set`、`ghostty_selection_gesture_event`。`select_output` 和 `selection_format_*` 也在 M 用（Agent 读命令输出）。

### 5.7 search（8 个，全部使用，V）

`ghostty_search_new/free/tick/feed/run/set/get/get_multi`。匹配规则：除 ASCII 字母不区分大小写外按字节精确匹配；覆盖主屏、alt 屏和回滚；`tick` 不碰终端，`feed` 需要独占终端 [H:search.h:20-96]。v1 在视图 actor 上按时间片驱动（§14.2）。

### 5.8 formatter（5 个，M + T）

`ghostty_formatter_terminal_new/format/format_buf/format_alloc/free`：输出 PLAIN / VT / HTML，可限定选区 [H:types.h:243-258][H:formatter.h]。用途：Agent 读屏、revive 保存（§9.4）、差分测试的规范化输出（§15.3）。

### 5.9 snapshot（12 个）

`ghostty_snapshot_encode`（M）、`_encode_buf` / `_encode_alloc`（T）、`ghostty_snapshot_decoder_new`（V）/ `_new_buf`（T）/ `_free` / `_set` / `_ready` / `_next` / `_decode`（T）/ `_get` / `_get_multi`。

### 5.10 kitty graphics（16 个，V；M 只用 `get(GENERATION)` 做内存统计）

`ghostty_kitty_graphics_get`、`_image`、`_image_get`、`_image_get_multi`、`_placement_iterator_new/free/set`、`_placement_next`、`_placement_get`、`_placement_get_multi`、`_placement_render_info`（主路径）、`_placement_rect` / `_pixel_size` / `_grid_size` / `_viewport_pos` / `_source_rect`（这 5 个只在 T 中与 `render_info` 对照）。

### 5.11 其余

| 组 | 函数 | 用 | 用途 |
|---|---|---|---|
| unicode（2）| `ghostty_unicode_codepoint_width`、`_grapheme_width` | V（UI 线程可直接调用：纯函数、线程安全 [H:unicode.h:63-66,138-140]）| IME 预编辑文本的列宽预测；可访问性文本 |
| sys（2）| `ghostty_sys_set` | MV | PNG 解码器、日志回调 |
| | `ghostty_sys_log_stderr` | T | 测试日志 |
| allocator（2）| `ghostty_alloc`、`ghostty_free` | MV | PNG 解码输出缓冲（必须用库给的 allocator 分配 [H:sys.h:126-135]）；释放 `*_alloc` 系列返回的缓冲 |
| 编码帮助（3）| `ghostty_mode_report_encode`、`ghostty_size_report_encode`、`ghostty_color_scheme_report_encode` | M | 主动发送的报告：主题切换时的 2031 通知（`CSI ? 997 ; 1/2 n`）；另外两个只在 T 中对照 |
| 颜色（5）| `ghostty_color_parse`、`_palette_default`、`_palette_generate`、`_perceived_luminance`、`_contrast` | V | 主题解析、生成 256 色、判断明暗、最小对比度 |
| 构建信息（1）| `ghostty_build_info` | MV | 日志、关于页、确认 `KITTY_GRAPHICS` 已编译进库 |
| 类型清单（1）| `ghostty_type_json` | MVT | §4.2 |

**不绑定**（v1）：`ghostty_osc_*`（8）、`ghostty_sgr_*`（9）—— 终端内部已解析，v1 无独立使用场景；`ghostty_color_parse_x11` / `_parse_palette_entry` / `_rgb_get` / `_luminance` / `_x11_names` / `_x11_name_count`（6）；`wasm.h`（5）；`ghostty_mouse_event_get_*`（3，测试也不需要）。合计 208 − 151 = 57。

---

## 6. 门面 API（`TKCode.Terminal.Core`）

### 6.1 类型总览

```
TerminalCore              一个终端状态机（单线程）
 ├─ TerminalCoreOptions   创建参数；其中 TerminalStateConfig 必须两边一致（§7.1）
 ├─ ITerminalEffects      副作用回调（§6.3）
 ├─ FrameCapturer         render state → FrameSnapshot（只在 View 角色创建）
 ├─ InputEncoder          key / mouse / focus 编码（View）
 ├─ SelectionController / TerminalSearch（View）
 ├─ CommandTracker        OSC 133 / 633 命令边界（Mirror）
 └─ RestoreSession        快照恢复（READY + 增量历史）
GhosttyRuntime            进程级初始化：加载库、校验清单、安装 sys 回调
```

### 6.2 `TerminalCore`

在 [A01 §6.1] 的基础上细化（标 ★ 的是相对 01 的修改，理由见 §17）：

```csharp
namespace TKCode.Terminal.Core;

public enum TerminalCoreRole { Mirror, View }

/// 与 A01 §6.1 一致：像素是**单个 cell** 的物理像素；整块区域 = 列数 × CellWidth、行数 × CellHeight。
/// 门面在调用 ghostty_terminal_resize 时检查范围（列、行 1..65535；像素 ≥ 0）。
public readonly record struct TerminalSize(int Columns, int Rows, int CellWidth, int CellHeight);

/// 影响终端状态的配置：两份核心必须完全一致（A01 §3.2 规则 2）。pty host 在 session/attach 中下发，
/// 并把 StableHash() 放进握手；Desktop 侧不一致则拒绝 attach。
public sealed record TerminalStateConfig(
    TerminalSize InitialSize,
    ScrollbackLimits Scrollback,                 // §10
    WidthPolicy Width,                           // Compat | Grapheme：设置 mode 2027 的复位默认值（§13.4）
    CursorShape DefaultCursorShape, bool DefaultCursorBlink,
    KittyGraphicsLimits KittyGraphics,           // §11.3
    bool ResizePullScrollback,                   // Windows ConPTY = false [H:terminal.h:2416-2439]
    string TerminfoName);                        // "xterm-256color"，影响 XTGETTCAP TN 应答

public sealed record TerminalCoreOptions(
    TerminalCoreRole Role,
    TerminalStateConfig State,
    TerminalPalette Palette, ColorScheme Scheme,  // 初始默认颜色；之后只能通过 SetDefaultColors 改
    ReplyProfile Replies,                         // DA1/DA2/DA3、XTVERSION、OSC52 是否声明（只有 Mirror 用到）
    int ContinuationMaxBytes,                     // Mirror 4 MiB；View 0（§9.2）
    bool TestMode = false);                       // 打开 DECRQCRA、XTWINOPS 全集，仅 esctest 用

public sealed class TerminalCore : IDisposable
{
    public static TerminalCore Create(TerminalCoreOptions options, ITerminalEffects effects);

    // —— 改变状态：只能按输出日志顺序调用（A01 §3.2 规则 1）——
    public void Write(ReadOnlySpan<byte> data);
    public void Resize(TerminalSize size);
    public void SetDefaultColors(in TerminalPalette palette, ColorScheme scheme);   // ★ 带上明暗，用于 996/2031
    public void Reset();
    public void Clear(ClearFlags flags);           // ★ 04 §6.2 的 Clear 记录（⌘K）；Screen / Scrollback，见 §6.6

    // —— 只读信息（两种角色都可用）——
    public TerminalSize Size { get; }
    public string Title { get; }                 // DATA_TITLE，已去除控制字符
    public string WorkingDirectoryRaw { get; }   // DATA_PWD 原文（OSC 7 是 file:// URI，OSC 9;9 / 1337 是路径）
    public TerminalModes Modes { get; }          // 一次 get_multi 读全
    public bool CursorAtPrompt { get; }          // DATA_CURSOR_AT_PROMPT
    public bool Win32InputMode { get; }          // ★ O1 后来自 DATA_MODE(9001)；退路为托管扫描器（§13.5）
    public InputModes ReadInputModes();          // ★ A01 §5.2：actor 每批 Write 后调用、变化才发布（§13.1）
    public TerminalMemoryUsage GetMemoryUsage(); // DATA_MEMORY_USAGE（会遍历所有页，不要每帧调用）

    // —— View 角色 ——
    public void ScrollViewport(ViewportScroll scroll);
    public void SetOverscan(ushort above, ushort below);
    public bool CaptureFrame(FrameSnapshotBuilder builder, in CaptureOptions options);  // §8
    public SelectionController Selection { get; }
    public TerminalSearch CreateSearch();
    public string? HyperlinkAt(int viewportRow, int column);                          // ★
    // 按键 / 鼠标 / 焦点编码不在 TerminalCore 上：UI 线程持有独立的 InputEncoder，选项来自 InputModes（§13.1）
    public PasteResult EncodePaste(in PasteRequest request, IBufferWriter<byte> output);  // UI 侧核心：开启 write_pty 捕获作用域（§13.3）

    // —— Mirror 角色 ——
    public void CompleteClipboardRead(in PendingClipboardRead request, ReadOnlySpan<byte> data, IBufferWriter<byte> reply); // ★ O3 延后应答（§14.3）
    public SnapshotResult EncodeSnapshot(IBufferWriter<byte> destination);             // ★ 可能返回 Deferred（§9.2）
    public static RestoreSession BeginRestore(ISnapshotSource source, TerminalCoreOptions options, ITerminalEffects effects); // ★
    public string Format(in FormatRequest request);
    public CommandTracker Commands { get; }
    public CompressionResult Compress(CompressionMode mode);   // §7.3
    public ulong CompressionActivity { get; }

    public void Dispose();
}
```

### 6.3 `ITerminalEffects`

01 的签名保留；以下是逐项说明和新增项。所有回调都在调用 `Write`（或 `Resize`、`Reset`、`Clear`、`EncodePaste`）的线程上同步触发，实现方**不能阻塞（只有 O3 退路下的 ClipboardRead 例外）、不能调用 `TerminalCore` 的任何成员**。

```csharp
public interface ITerminalEffects
{
    void WriteToPty(ReadOnlySpan<byte> reply);           // 应答字节（DA、CPR、DECRQM、颜色查询、2048……）
    void Bell();
    void TitleChanged(string title);
    void WorkingDirectoryChanged(string raw);            // 未解析原文；只当不可信输入（10 §6.5）
    ClipboardWriteResult ClipboardWrite(in ClipboardWriteRequest request);   // ★ 同步返回结果
    ClipboardReadDecision ClipboardRead(in ClipboardReadRequest request);    // ★ Mirror：返回 Deferred（O3）后由 host 转发 clipboard/readRequest；退路下同步阻塞（§14.3）
    void Notification(string title, string body);       // OSC 9 / 777
    void Progress(ProgressState state, int? percent);    // OSC 9;4
    void ProgramStatus(in ProgramStatusReport report);   // ★ OSC 7501（Claude Code 等会发送）
    void SemanticPrompt(in SemanticPromptEvent e);       // OSC 133（CommandTracker 已先更新）
    void RenderHold(bool hold);                          // mode 2026；FrameCapturer 已先处理
    void FullReset();                                    // ★ RIS 之后（标题、pwd 已清空，回调不会再单独通知）
    void UnknownOsc(int number, ReadOnlySpan<byte> payload, OscTerminator terminator);  // ★ 例如 OSC 633
    void ResizeWindowRequested(ushort rows, ushort columns);                            // ★ CSI 8 t，默认忽略
    void Win32InputModeChanged(bool enabled);            // ★ 9001 变化（O1 后由门面比较 DATA_MODE 触发）
}
```

DA、XTVERSION、尺寸查询、996 明暗查询**不走 effects**：它们的答案完全由核心自己的状态决定（`ReplyProfile`、最近一次 `Resize`、最近一次 `SetDefaultColors`），门面在内部回调里直接作答。这样两份核心对查询的"答案"天然相同，只是 UI 侧的应答被丢弃。

### 6.4 `FrameSnapshot` / `RowSnapshot`

```csharp
public sealed class FrameSnapshot                 // 发布后不可变；渲染线程只读
{
    public long Generation;
    public TerminalSize Size;
    public RowSnapshot[] Rows;                    // 视口行，长度 = Size.Rows，未变化的行与上一帧共享同一对象
    public RowSnapshot[]? Overscan;               // 视口上下的额外行（DATA_OVERSCAN 实际捕获数），ViewportY 为负 / ≥ Rows
    public SelectionSpans? Selection;             // 每个视口行 + overscan 行一个 { StartX, EndX }；不放进 RowSnapshot，便于行对象共享（§8.6）
    public CursorState Cursor;
    public TerminalPalette Palette;               // 已处理 DECSCNM（mode 5）：默认前景 / 背景已交换
    public TerminalModes Modes;                   // 只用于渲染与 UI 提示；按键编码用 InputModes（A01 §5.2）
    public ScrollbarState Scrollbar;              // total / offset / len（行），以及 ViewportActive
    public ImagePlacements? Images;               // §11.4
    public SearchHighlights? Search;              // 视口内的匹配区间 + 当前匹配（§8.6）
    public HyperlinkTable? Links;                 // 超链接 id → URI（§8.5）；ResolvedStyle.HyperlinkId 指向它
    public MouseShape PointerShape;               // OSC 22（DATA_MOUSE_SHAPE）
    public bool RenderHeld;                       // ★ 2026 期间被冻结的帧（调试用）
}

public sealed class RowSnapshot                   // 不可变，可跨帧共享
{
    public ulong RowId;                           // ★ 我们自己的 64 位 id，由 128 位 GhosttyRenderStateRowId 映射而来（§8.3）
    public int Version;                           // 同一 RowId 内容每变化一次加一
    public ulong ContentHash;                     // ★ Cells + Styles + Graphemes 的 XxHash3；渲染层的整形缓存用它做键
    public ulong[] Cells;                         // 8 字节 cell（§6.5），长度 = 列数
    public ResolvedStyle[] Styles;                // [0] 永远是默认样式
    public GraphemeTable? Graphemes;              // 稀疏：列 → 额外码点（不含基字符）
    public RowFlags Flags;                        // SoftWrapped、WrapContinuation、PromptLine、PromptContinuation、HasHyperlink、KittyPlaceholder
    public int ViewportY;                         // 负数 = 视口上方的 overscan 行
}

public readonly record struct ResolvedStyle(
    TerminalColor Foreground, TerminalColor Background, TerminalColor UnderlineColor,
    StyleFlags Flags,                            // Bold Italic Faint Blink Inverse Invisible Strikethrough Overline
    UnderlineStyle Underline,                    // None Single Double Curly Dotted Dashed（值同 GHOSTTY_SGR_UNDERLINE_*）
    int HyperlinkId);                            // 0 = 无；FrameSnapshot.Links 的键（§8.5）

/// 32 位：高 8 位为种类，低 24 位为值。种类 0 = 默认（使用调色板的默认前景/背景），
/// 1 = 调色板索引（低 8 位），2 = RGB（0xRRGGBB）。调色板颜色**不在捕获时解析**，
/// 由渲染层用 FrameSnapshot.Palette 解析，这样粗体变亮色、最小对比度等策略都在渲染层统一处理。
public readonly record struct TerminalColor(uint Raw);
```

### 6.5 8 字节 cell 格式（`TCell`）

我们自己的格式只服务渲染，与 `GhosttyCell` 解耦（上游位布局不受 ABI 保护 [H:render.h:456-458]）。

| 位 | 字段 | 说明 |
|---|---|---|
| 0–20 | `Codepoint` | 基字符码点；0 = 无文字（空格、只有背景色的格子、spacer）|
| 21–22 | `Width` | 0 Narrow、1 Wide、2 SpacerTail（不绘制）、3 SpacerHead（软换行行末的占位）|
| 23 | `HasGrapheme` | `Graphemes` 中有该列的额外码点 |
| 24–39 | `StyleIndex` | `Styles` 的下标；0 = 默认样式。行内最多 65 535 种，不可能溢出（列数 ≤ 65 535）|
| 40–41 | `Semantic` | 0 Output、1 Input、2 Prompt（OSC 133）|
| 42 | `Hyperlink` | 该格有超链接（`Styles[StyleIndex].HyperlinkId != 0` 的快速判断）|
| 43 | `KittyPlaceholder` | 码点为 U+10EEEE（Kitty 虚拟放置占位符，§11.4）|
| 44–63 | 保留 | 必须为 0；以后可用于搜索高亮等 |

`TCell` 提供 `static` 访问器（`TCell.Codepoint(ulong)` 等），全部是 `[AggressiveInlining]` 的移位和掩码。

**从 `GhosttyCell` 转换**（`CellConverter.ConvertRow`，每个脏行调用一次）：

```csharp
// L 是启动时从 ghostty_type_json 解析出的 GhosttyCellLayout（各字段 shift / mask）
for (int x = 0; x < cols; x++)
{
    ulong g = raw[x];
    uint tag  = (uint)(g >> L.TagShift) & L.TagMask;
    uint sid  = (uint)(g >> L.StyleShift) & 0xFFFF;
    uint wide = (uint)(g >> L.WideShift) & 3;
    bool link = ((g >> L.HyperlinkShift) & 1) != 0;
    uint sem  = (uint)(g >> L.SemanticShift) & 3;

    uint cp = 0; TerminalColor bgOverride = default; bool grapheme = false;
    switch (tag)
    {
        case 0: cp = (uint)(g >> L.CodepointShift) & 0x1FFFFF; break;                      // codepoint
        case 1: cp = (uint)(g >> L.CodepointShift) & 0x1FFFFF; grapheme = true; break;     // codepoint_grapheme
        case 2: bgOverride = TerminalColor.Palette((byte)(g >> L.PaletteShift)); break;     // bg_color_palette
        case 3: bgOverride = TerminalColor.Rgb((byte)(g >> L.RShift), (byte)(g >> L.GShift), (byte)(g >> L.BShift)); break;
    }

    int hyperlinkId = link ? hyperlinks.Resolve(viewportY, x) : 0;      // §8.5，同一 run 只解析一次
    int styleIndex = styles.Intern(sid, bgOverride, hyperlinkId);       // §8.4，sid != 0 时首次遇到才发 FFI
    if (grapheme) graphemes.Capture(x);                                 // §8.4

    outCells[x] = TCell.Pack(cp, wide, grapheme, styleIndex, sem, link, cp == 0x10EEEE);
}
```

- 只有背景色的格子（tag 2、3）在 Ghostty 里 `style_id` 为 0，背景色内联在 cell 中 [R04 §2.1.2]。我们把它合成为一个"默认样式 + 背景覆盖"的 `ResolvedStyle`，渲染层不需要区分。
- 纯 ASCII、无样式的行，循环体只有移位和一次 `styles.Intern(0, default, 0)` 的快速分支；可以用 `Vector256<ulong>` 先判断"整组 4 个 cell 的 tag、style_id、hyperlink 都为 0"再走快速路径（与 Ghostty `page.Mask` 的思路相同 [R04 §2.1.2]）**[推断：优化项，先写标量版]**。

### 6.6 `Clear`（⌘K）

[04 §6.2] 的 `Clear` 记录（`flags`：bit0 screen、bit1 scrollback）在两份核心的同一日志位置执行。C API 没有清屏 / 清回滚函数，门面用 `vt_write` 注入等价序列：scrollback → `CSI 3 J`；screen → `CSI H` + `CSI 2 J`。

- **只在解析器处于 ground 时注入**：注入前读 `DATA_VT_GROUND`，不在 ground 时注入会切断程序的半截序列。要求 host 只在镜像核心 `VT_GROUND == true` 的块边界追加 `Clear` 记录，否则推迟到下一个回到 ground 的块边界（⌘K 时 shell 通常处于 ground）。UI 侧核心在同一位置必然也处于 ground（字节相同），门面仍断言一次。
- 注入的字节不经过 PTY，`CSI 2/3 J` 也没有应答。
- 清屏后是否让 shell 重画提示符、ConPTY 是否需要 `ClearPseudoConsole`，由 04 / 06 决定 [R02 §3 W6]。

---

## 7. 镜像核心与 UI 侧核心的配置差异

### 7.1 必须一致的配置（`TerminalStateConfig`）

| 配置 | 原生选项 | 为什么必须一致 |
|---|---|---|
| 初始尺寸、像素尺寸 | `ghostty_terminal_new` + `resize` | 换行、reflow、2048 报告 |
| 回滚上限（行、字节）| `SCROLLBACK_MAX_LINES` / `_BYTES` [H:terminal.h:2213-2257] | 回滚裁剪点决定 `ED 3`、搜索结果、快照内容 |
| 宽度策略 | `MODE_DEFAULT`（mode 2027）[H:terminal.h:2308-2321] | 决定每个字符占几格 |
| 默认光标形状 / 闪烁 | `DEFAULT_CURSOR_STYLE` / `_BLINK` | `CSI 0 q` 之后的状态 |
| Kitty 图像存储上限、允许的传输介质 | `KITTY_IMAGE_STORAGE_LIMIT`、`KITTY_IMAGE_MEDIUM_*` | 淘汰顺序、文件读取结果都会改变状态（§11.3）|
| resize 是否从回滚拉回行 | `RESIZE_PULL_SCROLLBACK` | resize 结果不同 |
| Glyph Protocol | `GLYPH_PROTOCOL=false`（两边都关）| §3 |
| APC 上限 | `APC_MAX_BYTES`、`APC_MAX_BYTES_KITTY`（两边都用默认值）| 超限截断会改变图像是否成功 |
| 默认颜色 | `COLOR_*`（只通过 `SetColors` 日志记录修改）| OSC 4/10/11 查询答案、渲染 |

### 7.2 effects 的分工

原则：**会产生外部动作（写 PTY、写剪贴板、弹系统通知）的 effect 只在镜像核心生效**，因为它在没有 UI 时也必须工作，而且只应发生一次；**只影响画面的 effect 只在 UI 侧核心生效**，因为它和渲染时序绑定。

| 原生回调 | 镜像核心（pty host） | UI 侧核心（视图） |
|---|---|---|
| `WRITE_PTY` | 交给会话写队列，进入 PTY | **捕获作用域**：只在 `EncodePaste` 执行期间收集输出（[A01 §6.1]），其余一律丢弃。必须安装：`ghostty_terminal_paste` 没有 write_pty 会返回 INVALID_VALUE [H:paste.h:179-182] |
| `DEVICE_ATTRIBUTES`、`XTVERSION`、`SIZE`、`COLOR_SCHEME` | 由门面按 `ReplyProfile` / 当前尺寸 / 当前明暗作答 | 同样安装（答案被丢弃），保持代码路径一致 |
| `ENQUIRY` | 不安装（ENQ 不应答 [R03 §1.4]）| 不安装 |
| `BELL` | 不安装 | 视图：响铃动画 / 声音（按设置）|
| `TITLE_CHANGED` | 记录会话标题（持久化、Agent 列表、重连初值）| 更新标签页标题 |
| `PWD_CHANGED` | 记录会话 cwd（"在相同目录重新打开"、Agent）| 显示 |
| `CLIPBOARD_WRITE` | 按策略经 host 发 `session/clipboardWrite` 给主视图（[04 §11.2]），同步回复 SUCCESS（策略 deny 时 DENIED）| 不安装（Kitty 5522 写入会以 ENOSYS 拒绝，应答被丢弃）。[06 §6] 写成了「UI 侧核心触发、镜像核心忽略」，与 04 相反，本文按 04（§17）|
| `CLIPBOARD_READ` | O3 打开延后应答；回调登记请求后返回，host 发 `clipboard/readRequest` 给主视图（[A01 §3.2 规则 6]，§14.3）| 不安装（UI 侧核心的粘贴不会变成 5522 事件；O2 另外把 5522 整体停用）|
| `DESKTOP_NOTIFICATION` | 转发给 Desktop 进程（每个 Desktop 进程只显示一次，不按视图重复）| 不安装 |
| `PROGRESS_REPORT` | 记录（重连时下发初值）| 标签页进度条 |
| `PROGRAM_STATUS`（OSC 7501）| 按协议规则维护记录表（最多 256 条），转发给 Desktop / Agent | 不安装 |
| `SEMANTIC_PROMPT` | `CommandTracker`（§12）| 提示符装饰（命令块分隔、退出码徽标）|
| `RENDER_HOLD` | 不安装（不渲染）| `FrameCapturer`（§8.7）|
| `RESET` | 清空 CommandTracker、ProgramStatus 记录 | 清空视图侧缓存 |
| `UNKNOWN_SEQUENCE` + `UNKNOWN_MAX_BYTES=4096` | OSC 633（§12.3）| 不安装 |
| `RESIZE_WINDOW` | 不安装（v1 不允许程序改窗口尺寸）| 不安装 |
| `TITLE_REPORT` | `false`（默认关闭）| `false` |
| `XT_CHECKSUM_REPORT` | 仅 `TestMode` 时 `true`（esctest）| `false` |
| `TERMINFO_NAME` | `"xterm-256color"` | 同左 |
| `CONTINUATION_MAX_BYTES` | 4 MiB（快照需要，§9.2）| 0 |

### 7.3 回滚压缩策略

压缩只改变存储表示，不改变逻辑内容 [H:terminal.h:3278-3282]，所以两边可以不同。

| | 镜像核心 | UI 侧核心 |
|---|---|---|
| 触发 | `compression_activity` 令牌 1 s 不变 | 令牌 1 s 不变，**且**视口在底部、没有进行中的搜索 / 选区拖动 |
| 方式 | `INCREMENTAL`，每步之间让出 actor（每步后检查输入队列）| 同左 |
| 无订阅者时（UI 断开）| 再执行一次 `FULL` | — |
| 恢复快照 | — | 解码器 `COMPRESS_HISTORY=true` [H:snapshot.h:163-194] |
| 平台不支持 | `MEMORY_USAGE.compression_supported == false` 时跳过（`compress` 会返回 `UNSUPPORTED`）[H:terminal.h:466-473] | 同左 |

Ghostty 应用的参数是空闲 250 ms、步间隔 1 ms [R04 §2.1.4]。我们取 1 s 是为了避开交互中的短暂停顿 **[推断，基准测试后调整]**。

---

## 8. render state → FrameSnapshot

### 8.1 每帧流程（视图核心 actor）

```
CaptureFrame(builder):
  if (renderHeld && !holdExpired) return false                         // §8.7
  render_state_update(rs, t)                                           // 1
  render_state_get_multi(rs, [DIRTY, COLS, ROWS, CURSOR, OVERSCAN])    // 1
  terminal_get_multi(t, [SCROLLBAR, VIEWPORT_ACTIVE, MOUSE_SHAPE,
                         KITTY_KEYBOARD_FLAGS, ACTIVE_SCREEN, MODE×N]) // 1
  if dirty == FALSE && 选区/图像/光标都没变: return false
  if dirty == FULL || 尺寸变 || 调色板代数变:
      render_state_get(rs, COLORS)                                     // 1（含 256 色）
  render_state_get(rs, ROW_ITERATOR, it)                               // 1
  while row_iterator_next_dirty(it, &i):                               // 每脏行 1
      row_get_multi(it, [ID, VIEWPORT_Y, RAW, CELLS_RAW])              // 每脏行 1
      row_get_multi(rawRow, [WRAP, WRAP_CONTINUATION, SEMANTIC_PROMPT,
                             HYPERLINK, KITTY_VIRTUAL_PLACEHOLDER])    // 每脏行 1（ghostty_row_get_multi）
      需要样式或字形簇时: row_get(it, CELLS, cells)                     // 每行最多 1
      ConvertRow(...)                                                  // §6.5
  选区：§8.6；图像：§11.4
  render_state_clean(rs)                                               // 1
  builder.Publish()                                                    // Volatile 交换 + Generation++
```

**只能有一个 render state 绑定到一个终端**：`update` 会"消费"终端的 dirty 状态 [H:render.h:616-620]，第二个 render state 会漏掉变化。测试里的全量重建对照（§15.2）每次新建、只做一次 update，不受此影响。

### 8.2 脏行与"未变化的行"

- 全局 `DIRTY`：`FALSE` 跳过；`PARTIAL` 只遍历脏行；`FULL` 全部重建 [H:render.h:228-243]。
- 对 PARTIAL 中**未被访问的行**：沿用上一帧同一 `ViewportY` 位置的 `RowSnapshot` 对象。依据：render state 只在行脏、页脏或需要全量重建时才重建该位置的数据，否则保留上一次的内容 [S:render.zig:553-596,795-836]；所以对 render state 来说"不脏"就等于"与上一帧同位置相同"。这个推论的正确性完全依赖终端正确地标记 dirty —— 正是 RoyalTerminal 补丁修的那类问题，由 §15 的差分测试兜底。
- 当前上游的视口滚动会把所有行标为脏 [H:render.h:179-182]，所以滚动时每行都会重建 `RowSnapshot`；重建本身很便宜（复制 + 转换），昂贵的是渲染层整形，靠 `ContentHash` 避免（§8.3）。

### 8.3 RowId 与 Version

- Ghostty 的行 id 是 128 位（`bits[2]`，实现上是"页 serial + 页内行号"），永不复用，0 表示无 [H:render.h:283-302][S:render.zig:371-384]。
- 01 的 `RowSnapshot.RowId` 是 `ulong`。映射：每个核心一个 `Dictionary<(ulong, ulong), RowIdEntry>`，`RowIdEntry { ulong Id; int Version; ulong Hash; }`；新出现的 Ghostty id 分配自增 64 位 id。每帧结束后删除本帧未出现的条目（id 消失即永久失效）。
- 重建脏行时计算 `ContentHash`；与条目中的 `Hash` 相同时 `Version` 不变、并且**直接复用上一帧的 `RowSnapshot` 对象**（"保守标脏"导致的无效重建在这里被吸收）；不同则 `Version++`。
- 渲染层的行缓存键：`(ContentHash, FontGeneration, CellMetricsGeneration)`。它不依赖 `RowId`，因此滚动、同内容的行重复出现时都能命中 **[推断：比按 RowId 缓存命中率更高，代价是哈希计算约 0.1 µs/行]**。

### 8.4 样式与字形簇

- 样式：`CELLS_RAW` 里的 `style_id` 只在本行所在页内有意义。行内按 `style_id` 去重：第一次遇到某个非 0 的 `style_id` 时，`row_cells_select(x)` + `row_cells_get(STYLE)` 取出 `GhosttyStyle`，转换为 `ResolvedStyle`（`fg_color` / `bg_color` / `underline_color` 的 tag `NONE / PALETTE / RGB` 映射到 `TerminalColor` 的三种种类；`underline` 是 `GHOSTTY_SGR_UNDERLINE_*` 整数 [H:style.h:94-108][H:sgr.h:102-107]）。
- 行内最终的 `Styles[]` 键是 `(style_id, bgOverride, hyperlinkId)`，用一个 16 项的线性探测小表，超过后退回 `Dictionary`。
- 字形簇：`content_tag == codepoint_grapheme` 的格子，`row_cells_select(x)` + `row_cells_get_multi([GRAPHEMES_LEN, GRAPHEMES_BUF])`，缓冲区固定 64 个 `uint`（Ghostty 每格上限 64 个码点 [S:page.zig:92]），只存除基字符以外的码点。
- 不使用 `ROW_CELLS_DATA_FG_COLOR / BG_COLOR`：它们会把调色板解析成 RGB，且不处理粗体 [H:render.h:960-974]，与 §6.4 "颜色在渲染层解析"的约定冲突。

### 8.5 超链接

render state 只告诉我们"这格有超链接"（cell 的 hyperlink 位、行的 `HYPERLINK` 标记），**拿不到 URI**；URI 只能通过 `ghostty_terminal_grid_ref` + `ghostty_grid_ref_hyperlink_uri` 取得 [H:grid_ref.h:166-191]，也拿不到 OSC 8 的显式 `id=`。

- 捕获时：对带 `HYPERLINK` 标记的脏行，按"连续的 hyperlink 格子"分 run，每个 run 只解析一次：`ghostty_terminal_grid_ref({VIEWPORT, x, y})` → `ghostty_grid_ref_hyperlink_uri`。这发生在同一次 actor 处理内、终端未变更，grid ref 有效。
- 超链接表：核心内部维护 URI → `int HyperlinkId` 的驻留表（LRU 上限 4096 项，id 在核心生命周期内稳定）。每帧把本帧可见行用到的条目放进 `FrameSnapshot.Links`（id → URI，[A01 §6.1]），渲染层和悬停提示只读这张表。
- overscan 行的 `ViewportY` 为负或超出视口，用 `SCREEN` 坐标（`scrollbar.offset + viewportY`）解析 **[推断：SCREEN 坐标与 scrollbar offset 同一行空间，[H:terminal.h:316-318] 只明确了 ROW 滚动与 scrollbar 同空间，待测试确认]**。
- "悬停时高亮同一链接的所有格子"按 `HyperlinkId`（即 URI 相同）分组，跨行也成立；无法区分"URI 相同但 OSC 8 id 不同"的两个链接 —— 已知偏差，v1 接受。
- 01 的 `HyperlinkAt(row, col)`：直接用同一对 API 解析，供点击时取真实地址。

### 8.6 选区、光标、overscan

- **选区**：不逐行读 `ROW_DATA_SELECTION`。每帧读 `DATA_SELECTION`，用 `ghostty_terminal_point_from_grid_ref(…, SCREEN)` 把两个端点转成屏幕坐标，再按矩形 / 线性规则在托管代码里算出每行的 `SelectionSpan` —— 2–3 次调用代替每行 1 次，而且选区变化不需要让行对象失效。
- **光标**：`GHOSTTY_RENDER_STATE_DATA_CURSOR`（sized struct：是否在视口内、x、y、是否在宽字符尾、可见、闪烁、密码输入、形状）[H:render.h:510-549]。`password_input` 为 true 时，UI 显示密码锁图标并让 IME 进入密码模式 **[推断：libghostty 如何判定密码输入未查]**。
- **overscan**：平滑滚动（R1 阶段）才需要。`SetOverscan(above, below)` 设置后下一次 update 会全量重建 [H:render.h:411-417]。行迭代器先给出视口上方的行、再给视口行、再给下方的行 [H:render.h:108-114]；按 `ROW_DATA_VIEWPORT_Y` 分拣：0..Rows-1 进 `FrameSnapshot.Rows`，其余进 `FrameSnapshot.Overscan`。实际行数以 `DATA_OVERSCAN` 为准 [H:render.h:387-392]。
- **搜索高亮**：有活动的 `TerminalSearch` 时，每帧读 `GHOSTTY_SEARCH_DATA_VIEWPORT_MATCHES` 和 `SELECTED_MATCH`（都是 `GhosttySelection`），用与选区相同的端点换算方法生成 `FrameSnapshot.Search` 的每行区间 [H:search.h:42-60]。匹配只在下一次终端变更前有效，所以必须在本次捕获内换算完。
- **鼠标指针形状**：`DATA_MOUSE_SHAPE`（OSC 22）→ `FrameSnapshot.PointerShape`。

### 8.7 mode 2026（render hold）

- `RENDER_HOLD(held=true)` 回调里：立即执行一次 `CaptureFrame`（此时终端正是程序希望显示的那一帧 [H:terminal.h:1775-1783]），发布后记录 `holdStartedAt`，之后 `CaptureFrame` 直接返回 false。
- `held=false`：恢复正常捕获并立即发布一帧。
- 超时 1 s [R03 §2.2][A01 §5.2]：**不修改 mode 2026**，只是自己忽略 hold 继续发布（头文件允许"在显示实时内容更重要时忽略 hold"[H:terminal.h:1840-1843]）。不改模式是为了不让 UI 侧核心与镜像核心的模式状态分叉（例程用 `OPT_MODE` 复位 2026，那会改变终端状态）。
- 用户滚动、拖动选区时同样忽略 hold。

### 8.8 FFI 预算

| 场景 | 调用次数（每帧）| 估计耗时 **[推断：按每次 5–20 ns 的无封送调用]** |
|---|---|---|
| 无变化 | 3（update、两次 get_multi）| < 0.1 µs |
| 输入回显（1–2 个脏行，少量样式）| ≈ 12 | < 1 µs |
| 200×60 全量重绘，每行 3 种样式 | 6 + 60×3 + 60×3×2 ≈ 550 | 3–11 µs |
| 最坏：每格不同的 truecolor 样式（彩虹测试）| ≈ 6 + 180 + 12 000×2 ≈ 24 000 | 0.1–0.5 ms |
| 每格都是字形簇（emoji 墙）| ≈ 24 000 | 同上 |

[A01 §1] 的目标是渲染每帧 p99 ≤ 2 ms，快照发布 ≤ 50 µs [R04 §0.7]。除最后两种病态场景外都满足。

### 8.9 什么时候需要 Zig shim

**性能触发条件**（满足任一）：基准 S4（全屏 truecolor 动画）上 `CaptureFrame` p99 > 0.5 ms；或者 Windows / Linux arm64 上实测的单次调用开销 > 50 ns。shim 做法：导出 `tkcode_ghostty_row_export(iterator, out_cells, out_style_ids, out_styles, style_cap, out_graphemes, …)`，一次调用把整行（含去重样式表、字形簇）拷进调用方提供的平面数组（RoyalTerminal 的思路 [R01 §4.1.3]）。

**功能缺口**（与性能无关）。前两项已经由 §2.4 的 O1、O4 在 v1 解决；其余是 v1 接受的限制，以后集中放进 `native/ghostty/ext`：

| 缺口 | 影响 | shim 导出 |
|---|---|---|
| modifyOtherKeys 状态无 getter | `InputModes` 缺字段 | **v1：O4** `tkcode_ghostty_modify_other_keys_2`（RoyalTerminal 有同名扩展 [RT]）|
| mode 9001（win32-input-mode）未实现 | 无法跟踪 ConPTY 的 `CSI ? 9001 h` | **v1：O1** 登记进模式表 |
| Kitty 图像不进快照 | 重连后 UI 侧没有图像 | 图像导出 / 导入函数 |
| Kitty 动画无 tick API | 自动播放动画不动（`animationTick` 只被 Ghostty 自己的渲染器调用 [S:kitty/graphics_storage.zig:1057]）| `tkcode_ghostty_kitty_animation_tick(t, now_ms)` |
| 可见性（2033）无 setter | `CSI ? 998 n` 永远回答"可见"（`flags.visible` 默认 true [S:Terminal.zig:136]）| `tkcode_ghostty_visibility_set` |
| OSC 99 已解析但不交给 C API | kitty 通知协议不可用 | 回调桥（RoyalTerminal 做过 [RT]）|

v1 决策：M0 起带 O1–O4（§2.4）；性能 shim 不做，等基准结果；上表其余项 v1 不做。

---

## 9. 快照：编码与恢复

### 9.1 格式要点（已核实）

`"GHOSTSNP"` + u16 版本，后接带 CRC32C 的记录流：TERMINAL → SCREEN×n（含 PAGE）→ CONTINUATION → **READY** → HISTORY×n（PAGE 从新到旧）→ **FINISH** [H:snapshot.h:52-110]。READY 之后终端即可渲染和接收实时输入；`decoder_next` 每次前插一页历史，两次调用之间可以渲染、resize、喂实时数据 [H:snapshot.h:505-516]。

**快照包含**：尺寸、像素尺寸、滚动区、制表位、调色板（原值 + 覆盖）、pwd、标题、三套模式、光标默认值、鼠标状态、回滚上限、两个屏幕的页、光标、保存的光标、字符集、Kitty 键盘栈 [S:snapshot/terminal.zig:217-236]。
**不包含**：Kitty 图像与放置、Glyph 注册、选区、视口位置、dirty 状态、所有回调和"调用方本地"的配置（Unicode 宽度策略被列为"版本级或调用方要求"）[S:snapshot/terminal.zig:226-242]。

### 9.2 编码（镜像核心，`session/attach` 时）

1. 会话 actor 在两个数据块之间处理 attach 请求（此时没有进行中的 `vt_write`）。记下当前日志序号 `S`。
2. `ghostty_snapshot_encode(t, writer)`：writer 回调把字节追加到 `SegmentedBuffer`（64 KiB 段），**同时**按记录头（u16 tag、u32 len、u32 crc）切分；读到 READY 记录时，把此前的段标记为"READY 段可发送"。
3. 返回 `GHOSTTY_INVALID_VALUE` 且 `DATA_VT_GROUND == false`：说明解析器在序列中间而续传不可用（超过 `CONTINUATION_MAX_BYTES` 或开启跟踪前就已在序列中）[H:terminal.h:2276-2294]。处理：`SnapshotResult.Deferred`，host 继续处理下一个数据块后重试；连续 2 s 不成功则对 attach 方返回"稍后重试"。
4. IPC 按 [04 §8.2]：`Response{attachId, snapshotSeq=S, coreOptions, metadata}` 走控制队列；快照字节按 64 KiB 分成 `SnapshotChunk` 顺序发送（快照字节流本身就是 READY 在前，客户端解码器读到 READY 即可渲染，host 不需要自己切 READY 段 [04 §8.1]）；之后从位置 S 开始发 `Output` / 记录。上面第 2 步的记录头解析只用于统计，可以省略。

成本：编码是 O(全部回滚)，期间会话 actor 被占用、PTY 读取被反压。耗时 **[待 spike]**。注意 [04 §8.2] 的退路（编码超过 50 ms 就改用 `historyLines` 限制，按 §9.4 的方法裁剪）只能减小传输量和 UI 侧解码量，**减不了 actor 上的编码时间**：裁剪发生在完整编码之后。真正缩短 actor 占用需要上游提供"只编码最近 N 行"的接口，或者 ext 导出。

### 9.3 恢复（UI 侧，`RestoreSession`）

```csharp
public sealed class RestoreSession : IDisposable
{
    public TerminalCore Core { get; }          // READY 之后即可用
    public long HistoryRowsTotal { get; }      // DATA_HISTORY_ROWS_PRIMARY：READY 时就能定滚动条长度
    public bool PumpHistory(TimeSpan budget);  // 循环 decoder_next，返回是否还有剩余
}
```

1. `ghostty_snapshot_decoder_new(null, &dec, reader)`：reader 回调从 IPC 的"快照通道"阻塞读取（视图 actor 线程上，允许阻塞）。
2. `decoder_set(MAX_CONTINUATION_BYTES = 4 MiB)`、`decoder_set(COMPRESS_HISTORY = true)`；`RETAIN_CONTINUATION` 保持 false（UI 侧核心不需要再导出续传）。
3. `decoder_ready(dec, &t)` → 得到终端句柄。**此时把 `TerminalCoreOptions` 中除 `TerminalStateConfig` 外的选项、全部回调和 userdata 重新应用到这个终端**（快照不含回调与本地配置）。`TerminalStateConfig` 中的回滚上限来自快照本身，门面断言它与下发的配置一致。
4. 创建 render state，`CaptureFrame` 发布第一帧。
5. actor 主循环：优先处理 `Output` 帧（序号 > S）；空闲时 `PumpHistory(2 ms)`；`decoder_next` 返回 `NO_VALUE` 表示 FINISH，释放解码器。
6. 任一步出错：丢弃该核心，向 host 请求重新 attach（最多 3 次），仍失败则视图显示"无法恢复内容"并从当前序号开始以空屏继续。

### 9.4 退出保存（revive）：裁剪后的快照 + `tail.vt`

与 [04 §10] 一致：每个会话保存 `snapshot.bin`（裁剪到尾部 N 行，默认 1000）和 `tail.vt`（formatter 输出的 VT 文本）。原因：快照格式 v1 "尚无二进制兼容保证"[H:snapshot.h:112-113]，`ghosttyBuildId` 或快照信封版本不同时不能用 `snapshot.bin`，退回 `tail.vt`。

**裁剪**（快照 API 没有"只编码尾部 N 行"的选项 [H:snapshot.h:302-383]）。核心层提供 `SnapshotTrimmer.Trim(ReadOnlySequence<byte> full, int lines) → (byte[] snapshot, byte[] tailVt)`，在持久化工作线程上运行，不占会话 actor：

1. `ghostty_snapshot_decoder_new_buf` → `decoder_ready` 得到临时终端（不装任何回调，写入不会产生副作用）。`DATA_HISTORY_ROWS_PRIMARY` 给出完整历史行数。
2. 循环 `decoder_next`，累加 `DATA_PROGRESS_ROWS`（每页实际前插的行数）；累计 ≥ N（主屏）后停止。`PROGRESS_SCREEN` 为 alternate 的页不计数。
3. **先 `ghostty_snapshot_decoder_free`，再对临时终端编码**：头文件写明"放弃增量解码后，终端仍可使用，保留已恢复的历史"，并且解码器在 FINISH 之前借用该终端 [H:snapshot.h:434-445,485-487]。所以中途停止后，释放解码器即可正常 `ghostty_snapshot_encode`（这回答了 [04 §10.3] 的待验证项，仍需测试确认）**[待 spike]**。
4. 页粒度：历史按页前插，最后一页可能多出几百行；需要精确 N 行时，用 `SCROLLBACK_MAX_LINES = N` 设置临时终端再编码（设置会立即裁剪完整的历史页 [H:terminal.h:2250-2253]，仍是页粒度）。v1 接受页粒度的误差。
5. `tail.vt`：对同一临时终端 `ghostty_formatter_terminal_new(emit = VT, unwrap = false, trim = true, extra = { palette, screen.style, screen.hyperlink })`，选区为主屏最后 N 行到屏幕末尾。

**恢复**（新会话的镜像核心，[04 §10.5]）：`snapshot.bin` 可用 → `BeginRestore` 后一次 `PumpHistory` 到 FINISH，再按 §9.3 第 3 步重新应用选项与回调；否则把 `tail.vt` 作为 host 追加的 Output 写入。随后 resize、追加换行与分隔文字由 host 以 Output 记录完成。Kitty 图像不会恢复（§11.5）。

---

## 10. 回滚设置

libghostty 同时支持字节上限和行上限，"先到者生效"，两者都按页（约 400 KB）粒度裁剪，实际保留的行数几乎总是多于配置值 [H:terminal.h:2213-2257]。**必须显式设置**，因为 libghostty 的默认字节上限只有 10 KB [R04 §2.1.4]。

| 用户配置 `terminal.scrollback` | `SCROLLBACK_MAX_LINES` | `SCROLLBACK_MAX_BYTES` |
|---|---|---|
| 正整数 L（默认 10 000 [R05 §7.5]）| L | `max(4 MiB, L × 256 × 8 × 1.25)`，即按 256 列、样式开销 25% 估算的内存护栏。L=10 000 时约 25.6 MB |
| `0` | 0 | 0（字节上限 0 表示禁用回滚 [H:terminal.h:2228]）|
| `-1`（不限行数）| NULL（不限）| 50 MB（与 Ghostty 应用默认值相同 [R04 §2.1.4]）|

- 字节护栏的作用：宽终端或样式很多的输出（每页能放的行变少）不至于让 10 000 行变成几百 MB。行数配置面向用户，字节配置面向内存 [R04 §2.10]。
- 修改上限会立即裁剪 [H:terminal.h:2227-2229]。[04 §6.2] 的记录类型里没有"修改回滚上限"，所以 v1 规定：**设置修改只对新会话生效**，运行中的会话保持创建时的上限（两份核心因此始终一致）。若以后要即时生效，需在 04 增加 `SetScrollback` 记录。
- 进程级内存监控：pty host 每 30 s 读一次每个会话的 `MEMORY_USAGE`（会遍历所有页，不要更频繁）[H:terminal.h:2954-2967]，汇总到诊断页。

---

## 11. Kitty 图形

### 11.1 PNG 解码器的注入

libghostty 收到 PNG 格式（`f=100`）的图像时调用进程级的 `GHOSTTY_SYS_OPT_DECODE_PNG` 回调；不安装则拒绝 PNG [H:sys.h:118-173,204-213]。RGB / RGBA / 灰度、zlib 压缩（`o=z`）由库内部处理（Wuffs）[S:kitty/graphics_image.zig:636-675]。

| 方案 | 优点 | 缺点 | 结论 |
|---|---|---|---|
| **A 托管自写解码器 + `System.IO.Compression.ZLibStream`** | 零新依赖；内存安全；AOT 安全（BCL）；**镜像核心和 UI 侧核心用同一份代码，结果逐字节一致** | 约 600 行代码 + 测试；性能低于原生（但 PNG 在 kitty 中多是小图）| **采用** |
| B 借用 Wuffs（已编进 libghostty，`WUFFS_CONFIG__MODULE__PNG` 已定义 [S:pkg/wuffs/build.zig:5-16]）| 原生、快、Wuffs 本身内存安全 | 需要 Zig shim 导出一个 `decode_png` 并在构建里引用 Wuffs 模块 | 如果 M2 引入 `ext` 包，可以切换到 B（接口不变）|
| C SkiaSharp（RoyalTerminal 的做法）| 现成 | pty host 不应依赖 Skia 原生库（体积、AOT）；两个进程的解码器不同会破坏一致性 | 不采用 |
| D 第三方托管库（StbImageSharp、BigGustave 等）| 现成 | 引入依赖，需要逐个做 AOT 实测和安全审计 | 不采用 |

方案 A 的实现规格（`TKCode.Terminal.Core.Imaging.PngDecoder`，`static`、无状态、线程安全）：

1. 校验签名；逐块解析（长度 ≤ 剩余字节、CRC32 用 `System.IO.Hashing.Crc32` 校验）。支持 IHDR、PLTE、tRNS、IDAT、IEND；忽略其他辅助块（含 gAMA、iCCP，不做色彩管理，与终端常见实现一致 **[推断]**）。
2. 尺寸检查：宽、高 ≥ 1 且 ≤ 10 000；`width × height × 4` ≤ 单图上限（= `KittyGraphicsLimits.MaxImageBytes`，默认 64 MiB）。**解压前**按 IHDR 算出期望的解压长度，`ZLibStream` 读取时一旦超过就失败（防压缩炸弹）。
3. 去滤波：None / Sub / Up / Average / Paeth；位深 1/2/4/8/16；颜色类型 0/2/3/4/6；Adam7 隔行。16 位取高字节。
4. 输出：`ghostty_alloc(allocator, w*h*4)` 分配，直接写 RGBA（非预乘）；失败时 `ghostty_free` 已分配的部分并返回 false [H:sys.h:126-152]。
5. 回调外层 `try/catch`，任何异常都返回 false。
6. 测试：PngSuite 全套（含故意损坏的 x*.png）；fuzz（SharpFuzz）1 小时无异常、内存有界；与 `System.Drawing`-无关的参考值（预先用 libpng 生成的 RGBA 文件）逐字节比较。

### 11.2 v1 支持范围

| 功能 | v1 | 说明 |
|---|---|---|
| 直接传输（`t=d`）、分块、RGB/RGBA/PNG、zlib | ✅ | 库原生 |
| 文件（`t=f`）、临时文件（`t=t`）、共享内存（`t=s`）| ❌ 两边都关闭 | 临时文件和共享内存**读完即删除**，两份核心先后读取，后读的一方必然失败 → 状态分叉；`t=f` 有本地文件探测风险且 SSH 远端应禁止 [R03 §8.5]。libghostty 默认也只允许直接传输 [S:Terminal.zig:318-325] |
| 放置（`a=p`）、删除（`a=d`）、z 层（下 / 中 / 上）| ✅ | 渲染层按 `GhosttyKittyPlacementLayer` 三层绘制 |
| Unicode 占位符（虚拟放置，U+10EEEE）| ✅（需要自己解码）| C API 只告诉我们"是虚拟放置"，占位符格子的图像 id（前景色）、行列（变音符）解码由我们实现（移植 Ghostty `graphics_unicode.zig`）；cell 格式里有 `KittyPlaceholder` 位（§6.5）|
| 动画：客户端驱动换帧（`a=a,c=N`）| ✅ | 当前帧随 `DATA_PTR` 变化，`GENERATION` 会变 [H:kitty_graphics.h:402-406] |
| 动画：自动播放（`a=a,s=3`）| ❌ | 无 tick API（§8.9）|
| 查询 `a=q` | ✅ | 镜像核心应答 |

### 11.3 内存上限

| 项 | 默认 | 说明 |
|---|---|---|
| 每屏存储上限 `KITTY_IMAGE_STORAGE_LIMIT` | 64 MB（libghostty 默认 10 MB，Ghostty 应用 320 MB [S:Terminal.zig:307-316]）| 主屏和 alt 屏各一份 [H:terminal.h:2084-2095]；0 = 关闭协议。必须两边一致 |
| 单图上限 | 64 MiB（PNG 解码输出）| §11.1 |
| UI 进程的像素副本（§11.4）| 进程总计 256 MiB，LRU | 不影响终端状态，可以只在 UI 侧设置 |

双核心的代价：同一张图在镜像核心和 UI 侧核心各存一份解码后的 RGBA，UI 进程还有一份给渲染器的副本（以及 GPU 纹理）。64 MB 上限下最坏约 3–4 倍 **[推断]**。

### 11.4 捕获到 `FrameSnapshot.Images`

1. 每帧读 `KITTY_GRAPHICS` 句柄和 `GENERATION`；代数不变且帧不脏时沿用上一帧的 `ImagePlacements`。
2. 否则遍历放置：`placement_iterator_new`（复用）→ `kitty_graphics_get(PLACEMENT_ITERATOR)` → `placement_next` → `placement_get_multi([IMAGE_ID, PLACEMENT_ID, IS_VIRTUAL, Z])` → `kitty_graphics_image(id)` → `placement_render_info`（像素尺寸、格数、视口行列（可为负）、是否可见、源矩形）[H:kitty_graphics.h:445-487]。
3. 像素：`image_get_multi([WIDTH, HEIGHT, FORMAT, DATA_PTR, DATA_LEN, GENERATION])`。按 `(imageId, generation)` 查 `ImagePixelCache`；未命中时把数据复制成**预乘 BGRA**（Skia 原生格式）放进池化缓冲。`DATA_PTR` 返回 `NO_VALUE` 表示负载还在传输中，跳过 [H:kitty_graphics.h:391-394]。
4. `ImagePlacements` 引用缓存条目（引用计数）；渲染层按 `(imageId, generation)` 管理纹理。
5. 借用指针只在下一次终端变更前有效，所以复制必须在本次捕获内完成。

### 11.5 两份核心的一致性

| 情况 | 是否一致 | 处理 |
|---|---|---|
| 正常运行（两边收到同样字节）| 一致：同一解码器、同一上限、只允许直接传输 | — |
| UI 重连（快照恢复）| **不一致**：快照不含图像 [S:snapshot/terminal.zig:238-242]，镜像核心仍有图像 | v1：重连后已有图像不显示（占位符格子保留但无图）；之后新传输的图像正常。程序引用旧图像 id 的放置命令，镜像核心应答成功、UI 侧无图。在视图中显示一次"重连后部分图像不可用"的提示。根治需要 shim（§8.9）或上游支持 |
| 存储上限不同 | 会分叉（淘汰顺序不同）| `TerminalStateConfig` 强制一致 |
| PNG 解码失败 | 两边同样失败 | 同一解码器 |

---

## 12. OSC 133 命令跟踪（`CommandTracker`，镜像核心）

### 12.1 数据模型

```csharp
public sealed class CommandRecord
{
    public long Id;                       // 自增
    public CommandState State;            // Prompt → Input → Running → Finished | Abandoned
    public DateTimeOffset PromptAt, StartedAt, EndedAt;
    public int? ExitCode;                 // has_exit_code 为 false 时为 null（Windows 退出码可为负）[H:terminal.h:1492-1500]
    public string? CommandLine;           // OUTPUT_START 的 command（shell 发送并已解码）；633 E 需 nonce 匹配（§12.3）
                                          // 退出码来自 OSC 133 D，与 shell 进程本身的退出码无关：macOS 上 login 包装会吞掉 shell 的
                                          // 退出码（login 总是 exit(0)，计划用 kqueue NOTE_EXITSTATUS 取 shell pid 的退出状态，待 spike，见 03）
    public string? ErrorText;             // COMMAND_END 的 error
    public string? WorkingDirectory;      // 开始时的 cwd 原文
    public TrackedAnchor PromptStart, InputStart, OutputStart, OutputEnd;   // 跟踪型 grid ref
    public long OutputStartSeq, OutputEndSeq;   // 输出日志序号（数据块粒度）
}
```

### 12.2 事件处理

`SEMANTIC_PROMPT` 回调在终端已更新屏幕之后调用 [H:terminal.h:1529-1531]，此时光标位置就是事件发生的位置：

| 事件 | 动作 |
|---|---|
| `PROMPT_START`（`prompt_kind` = PRIMARY）| 若上一条仍为 Running：标记 Abandoned。新建记录，`PromptStart = track(cursor)`。重复的 PROMPT_START（resize 后重画）只更新锚点，不新建 [H:terminal.h:1522-1527] |
| `PROMPT_START`（RIGHT / CONTINUATION / SECONDARY）| 忽略（多行输入）|
| `INPUT_START` | `InputStart = track(cursor)` |
| `OUTPUT_START` | `OutputStart = track(cursor)`；`StartedAt`；`CommandLine = e.command`；`OutputStartSeq = 当前块序号` |
| `COMMAND_END` | `OutputEnd = track(cursor)`；`ExitCode`、`ErrorText`、`EndedAt`；`State = Finished`。若此前没有 OUTPUT_START（命令被取消）→ Abandoned [R03 §2.4] |
| `RESET` 回调 | 清空全部记录、释放锚点 |

- `track(cursor)`：`ghostty_terminal_grid_ref_track(t, {ACTIVE, CURSOR_X, CURSOR_Y})`（回调内只读访问，允许）。
- 上限：保留最近 1000 条记录；淘汰时释放 4 个跟踪引用（每个跟踪引用都会增加终端变更的簿记开销 [H:grid_ref.h:74-76]）。
- 锚点失效（被回滚裁剪）：`has_value == false`，该记录的输出标为"已不在回滚中"。
- 序号是数据块粒度：回调发生在 `vt_write` 中间，拿不到块内字节偏移。Agent 需要字节级精确输出时，用 `OutputStartSeq` 前一块到 `OutputEndSeq` 的原始字节，交给一个临时终端重放后再 format **[推断：备用路径]**。

### 12.3 OSC 633（VS Code）

libghostty 不实现 633，会通过 `UNKNOWN_SEQUENCE` 回调以 OSC 原文交给我们（需同时设置 `UNKNOWN_MAX_BYTES`）[H:terminal.h:131-197]。

- 镜像核心解析 `633;A/B/C/D[;exit]` 映射为同样的 CommandTracker 事件；`633;E;<cmdline>;<nonce>` 只在 nonce 等于本会话注入脚本时生成的 nonce 时采用 [R03 §2.4]；`633;P;Cwd=…` 作为 cwd 提示。
- **限制**：633 不会在网格上打语义标记，所以 `select_output`、按 prompt 滚动对只发 633 的 shell 无效。TKCode 自己的 shell 集成脚本（[A01] D7）**只发 OSC 133**（含 kitty 的 `cmdline=` 扩展），633 只用于兼容别人的脚本。

### 12.4 读取命令输出（Agent）

1. 首选：`ghostty_terminal_select_output(t, snapshot(OutputStart))` → `GhosttySelection` → `ghostty_terminal_selection_format_alloc(PLAIN, unwrap=true, trim=true)`。
2. 退路：用 `OutputStart`、`OutputEnd` 两个锚点构造线性选区再 format。
3. 输出上限：默认 256 KiB 文本，超出时保留头尾并在中间注明省略 **[推断：与 bash 工具现有截断策略保持一致，详见 09 号文档]**。

---

## 13. 输入编码

### 13.1 `InputModes` 与按键编码（UI 线程）

按 [A01 §5.2] 与 [06 §3.4]：视图核心 actor 每处理完一批 `Write` 调用一次 `ReadInputModes()`，与上一份比较，有变化才用 `Volatile` 交换发布。它**不**随 `FrameSnapshot` 发布（2026 期间快照最多被扣 1 s，视图不可见时不生成）。UI 线程持有自己的 `GhosttyKeyEncoder` / `GhosttyMouseEncoder`（不绑定终端），`InputModes` 变化时逐项 `setopt`，**不调用 `setopt_from_terminal`**（那需要终端句柄，只能在 actor 线程）。

`ReadInputModes()` 的实现（一次 `ghostty_terminal_get_multi` + 一次 O4 调用）：

| `InputModes` 字段 | 来源 | 对应编码器选项 |
|---|---|---|
| CursorKeyApplication（DECCKM ?1）| `DATA_MODE` | `KEY_ENCODER_OPT_CURSOR_KEY_APPLICATION` |
| KeypadApplication（?66）| `DATA_MODE` | `KEYPAD_KEY_APPLICATION` |
| IgnoreKeypadWithNumLock（?1035）| `DATA_MODE` | `IGNORE_KEYPAD_WITH_NUMLOCK` |
| AltEscPrefix（?1036）| `DATA_MODE` | `ALT_ESC_PREFIX` |
| BackarrowKeyMode（DECBKM ?67）| `DATA_MODE` | `BACKARROW_KEY_MODE` |
| ModifyOtherKeys2 | **O4** `tkcode_ghostty_modify_other_keys_2` | `MODIFY_OTHER_KEYS_STATE_2` |
| KittyFlags | `DATA_KITTY_KEYBOARD_FLAGS` | `KITTY_FLAGS` |
| MouseTracking（9/1000/1002/1003）、MouseFormat（1005/1006/1015/1016）| `DATA_MODE` × 8 | 鼠标编码器 `EVENT` / `FORMAT` |
| AlternateScroll（?1007）、AltScreen（`DATA_ACTIVE_SCREEN`）、BracketedPaste（?2004）、FocusEvent（?1004）、GraphemeCluster（?2027）| `DATA_MODE` / `DATA_ACTIVE_SCREEN` | UI 侧逻辑（滚轮、IME 列宽、焦点上报）|
| Win32Input（?9001）| **O1** 后 `DATA_MODE(9001)` | 平台层自己编码 `CSI Vk;Sc;Uc;Kd;Cs;Rc _` [R03 §4.5] |
| CursorAtPrompt | `DATA_CURSOR_AT_PROMPT` [H:terminal.h:2925-2934] | 06 的按键路由 |

`MACOS_OPTION_AS_ALT` 来自用户设置，不来自终端。`DATA_MODE` 是输入 / 输出型参数，`get_multi` 中可重复出现，每项指向不同的 `GhosttyTerminalModeConfig` [H:terminal.h:2899-2908] **[待 spike：确认 get_multi 允许重复键]**。

O4 不可用时的退路：按键改为在 actor 上编码（`setopt_from_terminal` 后编码，多一次线程跳转，约 10–50 µs [R04 §5.1]），`InputModes.ModifyOtherKeys2` 恒为 false 仅供显示。

### 13.2 鼠标与焦点

- 鼠标：UI 线程的编码器，`EVENT` / `FORMAT` 来自 `InputModes`，另设 `SIZE`（屏幕、cell、padding 的物理像素）、`ANY_BUTTON_PRESSED`、`TRACK_LAST_CELL`（1002 模式按格去重）[H:mouse/encoder.h]。
- 焦点：`InputModes.FocusEvent` 为 true 时 `ghostty_focus_encode`（纯函数，可在 UI 线程调用）。

### 13.3 粘贴

与 [A01 §6.1]、[06 §7] 一致：粘贴在 **UI 侧核心**上执行，`EncodePaste` 期间打开 `write_pty` 捕获作用域收集输出。

1. `EncodePaste` 是视图 actor 上的命令（需要终端句柄）：构造 `GhosttyPaste{ mimes = ["text/plain"], reader, source, allow_unsafe }`，调用 `ghostty_terminal_paste` [H:paste.h:170-187]；输出经 `write_pty` 分块进入捕获缓冲。
2. 返回 `GHOSTTY_REJECTED`：`PasteResult.RequiresConfirmation`，UI 弹确认框（预览前几行），确认后带 `allowUnsafe = true` 再调一次。
3. 一致性：UI 侧核心不装 `clipboard_read`，所以粘贴**不会**变成 5522 粘贴事件（库只在"5522 开启且安装了 clipboard_read"时才发事件 [H:paste.h:20-28]），也就不会在 UI 侧核心留下授权记录；O2 另外把 5522 停用，DECRQM 如实回答。
4. Agent 写入终端时没有 UI 侧核心：host 用 `ghostty_paste_encode`（不依赖终端的编码函数）按镜像核心当前的 2004 状态编码，**永远不**放行不安全内容 [R03 §8.4][H:paste.h:213-257]。

IME 提交的文本不是粘贴，作为普通输入字节发送 [H:paste.h:84-91]。

### 13.4 宽度模式（mode 2027）

libghostty 的 2027 是运行时模式：关闭时零宽字符直接附着到前一格、VS16 不改宽度、ZWJ 不合并（每个 emoji 各自计宽）；开启时按 UAX #29 分簇，VS16 → 2、VS15 → 1 [S:Terminal.zig:1286-1300,1475-1530][H:unicode.h:63-140]。`MODE_DEFAULT` 可设置复位默认值（2027 是"可配置默认"的模式）[S:modes.zig:406,494]。

| 平台 | `WidthPolicy` | 2027 默认 | ConPTY 标志 | 说明 |
|---|---|---|---|---|
| macOS / Linux | `Compat` | 复位 | — | D9：默认兼容，程序（neovim 等）开启后切换 |
| Windows（本地 ConPTY）| `Grapheme`（**锁定**）| **置位** | `PSEUDOCONSOLE_GLYPH_WIDTH_GRAPHEMES`（0x08）| 已写入 [A01]（D9 更新）：ConPTY 的字宽在创建时固定、忽略 2027，模拟器必须与之一致 [R02 §3 W5]。程序发 `CSI ? 2027 l` 时，libghostty 会照常复位该模式（两份核心一致），但 ConPTY 仍按字形簇布局 → 可能错位；v1 接受，见下 |
| Windows（SSH / WSL 走 ConPTY）| 同上 | 同上 | 同上 | 同上 |

- 歧义宽度：libghostty **没有**"歧义宽度按宽字符处理"的选项（`ghostty_unicode_codepoint_width` 的规则里没有 EAW=A 分支 [H:unicode.h:40-58]）。v1 不提供该设置；ConPTY 也不设 `AMBIGUOUS_IS_WIDE`。
- Windows 的"锁定"只在核心层面是默认值：libghostty 没有"永久置位"的开关，DECRQM 2027 会回答 1/2（可切换）而不是 3（永久置位，WT 的回答 [R03 §2.2]）。如果要严格锁定，需要再加一个 overlay（2027 在 Windows 构建中忽略复位）——不推荐，因为同一原生库服务所有会话；v1 接受上述偏差，在 10 号文档记录。
- IME 预编辑列宽：UI 线程直接调用 `ghostty_unicode_codepoint_width`（2027 关）或 `ghostty_unicode_grapheme_width`（2027 开），模式取 `InputModes.GraphemeCluster`。

### 13.5 win32-input-mode（9001）

**首选：O1**（§2.4）。登记后 `CSI ? 9001 h/l` 由 libghostty 正常处理，两份核心同步，DECRQM 如实应答，`InputModes.Win32Input` 从 `DATA_MODE` 读取，`ITerminalEffects.Win32InputModeChanged` 由门面在每批 `Write` 后比较状态触发。ConPTY 启动时发 `ESC[?9001h`、退出时 `?9001l` [R03 §4.5]。

**退路（O1 打不上时）**：`DecPrivateModeScanner`（约 120 行，托管）。在 `Write` 把字节交给 `vt_write` **之前**扫描，只识别 `ESC [ ? Pm h/l`（`Pm` 为分号分隔的参数列表）中的 9001，跨块保存部分状态；遇到其他序列立即回到空闲态；CAN / SUB / ESC 中断时丢弃。两份核心都运行它（相同字节 → 相同结论）。RIS 时复位。代价：DECRQM 9001 回答 0；快照不含该状态，attach 元数据需带上 `win32InputMode`。只在 Windows 会话启用。

---

## 14. 线程与重入规则

### 14.1 总则

| 规则 | 来源 |
|---|---|
| 一个 `TerminalCore` 只被一个线程（所属 actor）访问；libghostty 本身不建线程，单个终端不是线程安全的 | [R01 §3.3][A01 §5] |
| 回调在调用 `vt_write` / `resize` / `reset` / `paste` 的线程上同步执行；回调内禁止重入写入 | [H:terminal.h:79-84,3060-3063] |
| 借用指针（`DATA_TITLE` 字符串、Kitty 句柄、grid ref、render state 行数据）只在下一次终端变更前有效；门面绝不把它们存过一次调用 | [H:terminal.h:2649-2651][H:kitty_graphics.h:109-115] |
| UI 线程 / 渲染线程只读 `FrameSnapshot`（不可变）| [A01 §5.2] |
| 进程级 API（`ghostty_sys_set`、`ghostty_unicode_*`、`ghostty_type_json`、`ghostty_build_info`）可在任意线程调用；`sys_set` 只在启动时调用一次 | [H:sys.h:255-258][H:unicode.h:63-66] |

### 14.2 actor 内的调度

视图 actor 的一次循环：处理所有排队的 `Output` 块（每块后检查是否有高优先级命令：按键编码、视口滚动）→ 若渲染端设置了"想要帧"则 `CaptureFrame` → 空闲时：搜索 `feed` + `tick`（2 ms 时间片）、历史恢复 `PumpHistory`、压缩（§7.3）。搜索的 `tick` 理论上可以放到工作线程 [H:search.h:84-96]，v1 为简单起见留在 actor 上。

### 14.3 OSC 52 读：延后应答（O3）

[A01 §3.2 规则 6]：镜像核心触发，host 向主视图发 `clipboard/readRequest`，UI 询问用户后回 `clipboard/readResponse`，host 写回 PTY [04 §11.2]。libghostty 的原始语义是"回调返回前必须应答，否则自动回空剪贴板"[H:terminal.h:969-981][S:stream_terminal.zig:1096-1100]，所以需要 O3。

O3 打开后镜像核心的流程：

1. 回调里检查 `location`（标准 / primary / selection）和 OSC 52 的终止符（BEL 或 ST；库在 `ClipboardReadState` 里保存了它，但回调参数里没有 → **O3 补丁同时把 terminator 放进 `GhosttyClipboardRead` 末尾的新字段**，sized struct 允许追加 [H:terminal.h:969-975]）。
2. 策略 `deny` 或没有 UI 连接：不调用 `reply`、也不登记请求（O3 打开时等于不应答）。
3. 否则不调用 `reply`，返回；门面生成 `PendingClipboardRead{ id, location, terminator, seqAtRequest }` 交给 host。
4. UI 允许后，host 在会话 actor 上调用 `core.CompleteClipboardRead(pending, data, writer)`：编码 `ESC ] 52 ; <c|p|s> ; <base64> <终止符>` 放进 PTY 写队列。拒绝或 60 s 超时：不写任何东西。

拒绝时不应答与 [R03 §8.1]、[04 §11.2] 一致；库的默认（回空剪贴板）让程序不必等超时，但会和 host 稍后的真实应答重复，所以 O3 打开时一律不用。OSC 5522（kitty 剪贴板协议）的读取也会进同一个回调：v1 对它同步回 `UNSUPPORTED`（库按协议回 ENOSYS [H:terminal.h:913-916]），不转发给 UI。

O3 打不上时的退路：回调内同步阻塞——IPC 询问 UI，最多等 30 s，期间该会话暂停解析；超时回 `DENIED`（空应答）。这是 libghostty 文档认可的方式（"an embedder that needs to ask the user must block"[H:terminal.h:1044-1048]）。

### 14.4 回调中能做什么

| 允许 | 禁止 |
|---|---|
| `ghostty_terminal_get` / `get_multi`、`grid_ref`、`grid_ref_track`、`point_from_grid_ref` | `vt_write`、`vt_write_until_ground` |
| `ghostty_render_state_update`（仅 render hold 回调）| `resize`、`reset`、`set`（选项）、`paste`、`snapshot_encode` |
| 复制借用数据、入队消息 | 调用 `ITerminalEffects` 之外的门面公开方法（`_inCallback` 检查）|
| （仅 ClipboardRead）有超时的阻塞 IPC | 无超时的阻塞；访问其他会话的核心 |

---

## 15. 差分测试

### 15.1 目标

捕获三类问题：(a) 终端没有正确标记 dirty，导致增量渲染结果与真实状态不同；(b) render state 自身的复制错误；(c) 两份核心、快照往返之间的状态分叉。

### 15.2 增量 vs 全量（每帧）

```
DiffHarness(input bytes, chunker):
  t = new terminal(配置 = View 角色)
  inc = FrameCapturer(t)                         // 正常路径：一个长期存在的 render state
  for chunk in chunker(input):                   // 随机切块，含 1 字节切块、序列中间切块
      t.Write(chunk)
      a = inc.Capture()                          // 增量
      b = FullRebuild(t)                         // 新建 render state → update 一次（必然 FULL）→ 转换 → 释放
      AssertEqual(Normalize(a), Normalize(b), chunkIndex, byteOffset)
      每 N 块: c = GridRefRebuild(t)             // 不经过 render state：逐格 grid_ref_cell/style/graphemes
               AssertEqual(Normalize(b), Normalize(c))
```

- `Normalize`：把 `FrameSnapshot` 展开为"每格（码点 + 额外码点、宽度、ResolvedStyle 全字段、语义、超链接 URI）+ 行标记 + 光标 + 调色板"；`RowId`、`Version`、`Generation` 不比较（它们本来就随路径不同）。
- 比较 a 与 b 抓的是 (a) 类问题（漏标 dirty）；比较 b 与 c 抓的是 (b) 类问题。
- 新建的 render state 不会干扰增量那一个：顺序是"写入 → 增量 update（消费 dirty）→ 新建 update（全量）"，两次 update 之间没有写入。
- 切块策略：固定 4 KiB；随机 1–64 字节；**逐字节**（小语料）；在每个 ESC、每个多字节 UTF-8 中间各切一次。RoyalTerminal 第 1 项问题（`K` 与 U+0301 之间有一次干净的帧）只有在两个字符之间做一次 capture 才能复现。
- 失败时自动最小化：对块序列做 ddmin（删块、合并块），输出最小复现的 `.in` 文件 + 首个差异格。

### 15.3 其他一致性对照

| 对照 | 做法 | 抓什么 |
|---|---|---|
| 镜像 vs UI | 两个终端（Mirror / View 配置）喂同样字节；每 K 块比较 `Format(VT, 全部 extra)` 输出与全量捕获 | effects 配置差异导致的分叉（§1 第 3 点的不变量）|
| 快照往返 | 任意时刻 encode → decode（含逐页 `next` 期间继续喂字节）→ 与原终端比较全量捕获和 formatter 输出 | 快照遗漏的状态、续传错误（RoyalTerminal 第 5 项）|
| 两种切块 | 同一输入用两种切块方式喂两个终端，结果必须相同 | 跨块解析状态错误 |
| 宽度 | ucs-detect 语料在两种宽度策略下各跑一次 | 2027 切换 |

### 15.4 语料

1. RoyalTerminal 12 项问题的最小复现（§15.5，逐项写成用例）。
2. xterm.js `escape_sequence_files`（约 80 对）、Alacritty ref tests（45 个，含 tmux_htop、vim）[R03 §9.1]。
3. 自录的真实程序：vim、htop、tmux 分屏、less、fzf、Claude Code / Codex 一轮对话（离线 / 回放）。
4. fuzz：结构化变异（在合法序列中插入 ESC / CAN / C1 / 超长参数 / 超长 OSC / 半截 UTF-8），以及专门的"dirty 压力"生成器：随机光标移动 + 宽字符 + 零宽字符 + OSC 133 + 软换行边界写入 + 滚动区 + DECSTBM。
5. CI：每个 PR 跑 1–3（约 2 分钟）；夜间跑 fuzz 30 分钟，使用 `ReleaseSafe` 原生库。

### 15.5 RoyalTerminal 12 项修正在 HEAD（`b3dc3d7`）上的状态

RoyalTerminal pin 在 `b40acce5`，以 overlay 方式修正了 12 处 [RT]。逐项检查当前代码：

| # | 问题（[RT] 原文概括）| HEAD 上的代码 | 状态 |
|---|---|---|---|
| 1 | 2027 关闭时追加零宽字符，没有标记光标行 dirty，render state 只显示基字符 | `print` 的 width==0 分支直接 `appendGrapheme(prev, c)`，前面没有 `cursorMarkDirty`（2027 开启的分支有）[S:Terminal.zig:1475-1530]；`Page.appendGrapheme` 只设 `row.grapheme`，不设 dirty [S:page.zig:1562-1591] | **仍存在**（代码阅读）|
| 2 | 覆盖软换行宽字符的任一半时，清除了上一行的 spacer_head，但上一行没标 dirty | `writeCell` 的 `.wide` / `.spacer_tail` 分支把 `cursorCellEndOfPrev()` 改成 narrow，没有标记上一行 [S:Terminal.zig:1681-1712] | **仍存在**（代码阅读）|
| 3 | Kitty 动画删除帧时 `current_index` 下标差一 | `removed_idx = number - 2` 与包含根帧的 `current_index`（0 = 根帧 [S:kitty/graphics_animation.zig:95-98]）比较 [S:kitty/graphics_storage.zig:1546-1560] | **仍存在**（代码阅读）|
| 4 | 淘汰请求应按实际占用而不是准入上限计算（RGB→RGBA 提升后可超限）| `evictImageExcept` 仍有 `assert(req <= self.total_limit)`；`reserveAnimationBytes` 加了保护，`addImage` 路径没有 [S:kitty/graphics_storage.zig:298-304,1003-1025] | **疑似仍存在**；ReleaseFast 下 assert 变为未定义行为 |
| 5 | APC 之后切换到 C1 引导的 DCS/CSI/OSC 时，续传导出未规范化 | `stream_continuation.zig` 未见对应规范化逻辑 | **未能确定**，用 §15.3 快照往返 + APC/C1 语料验证 |
| 6 | OSC 133 和隐式换行续行改了行的语义标记，没有标 dirty | `cursorSetSemanticContent` 改 `page_row.semantic_prompt` 无 dirty [S:Screen.zig:2948-2980]；换行续行处同样 [S:Terminal.zig:1822,2229,2384][S:Screen.zig:3842,3878] | **仍存在**（代码阅读）|
| 7 | `cursorResetWrap` 清除下一行的 wrap_continuation，没有标 dirty | [S:Screen.zig:1639-1664] 无 dirty | **仍存在**（代码阅读）|
| 8 | bitmap 分配器大块分配越界读 | 大块路径在 `while (rem > 64)` 之后直接读 `bitmaps[i]`，`i` 可能等于 `bitmaps.len`（例如单字中请求 65 块）[S:bitmap_allocator.zig:286-322] | **仍存在**（代码阅读）；超链接、字形簇分配可触发 |
| 9 | PageList 非 reflow 克隆失败后的行重置与 pin 重映射 | 涉及多段错误路径 | **未核实**，需移植 [RT] 的回归用例 |
| 10 | 光标页样式扩容时恢复的超链接引用泄漏 / 空指针 | 同上 | **未核实** |
| 11 | scroll-clear 裁剪后光标 pin 已迁移，旧页内 id 失效 | 同上 | **未核实** |
| 12 | 非 reflow 加宽时新页连一行都放不下导致无限分配 | 同上 | **未核实**（[RT] 自述该补丁"已编写但未构建运行"）|

处理：

- 1、2、6、7 是"漏标 dirty"，用户可见（显示旧内容）。M1 必须有对应的失败用例；若上游 30 天内未修，按 §2.3 加 overlay（每处都只是加一行 `markDirty`）。同时向上游报告。
- 3、4、8 是 Kitty / 分配器的健壮性问题：3 只影响动画显示；4、8 在 ReleaseFast 下是未定义行为，属于**安全问题**，优先报上游；在上游修复前，fuzz 和差分测试使用 ReleaseSafe 构建以便发现。
- 5、9–12：移植 [RT] 的回归用例（MIT 许可，注明来源）后再判断。

---

## 16. 风险与 spike 清单

| # | 风险 / 待验证项 | 验证方法 | 阶段 |
|---|---|---|---|
| S1 | 6 RID 构建与加载；Windows x64 无 AVX 指令 | CI 矩阵 + 反汇编检查 | M0 |
| S2 | 按值传递结构体在 6 RID 上的 ABI | §4.4 往返测试 | M0 |
| S3 | ClangSharp XML 输出能否覆盖全部头文件 | 生成器原型 | M0 |
| S4 | `[SuppressGCTransition]` 用在哪些函数上安全且有收益 | BenchmarkDotNet，对比有无 | M1 |
| S5 | 每帧 FFI 成本（§8.8）在 Windows arm64、Linux arm64 上的实测 | Benchmarks S1/S4 | M1 |
| S6 | 50 MB 回滚的快照编码耗时、解码耗时 | 基准 | M2 |
| S7 | ~~ReleaseSafe 与 ReleaseFast 的吞吐差距~~（已决定 ReleaseFast，不再需要） | — | — |
| S8 | Windows：ConPTY `GLYPH_WIDTH_GRAPHEMES` + 2027 默认置位下，pwsh / cmd / WSL 中 emoji、CJK 光标是否对齐；程序复位 2027 后的错位程度 | 手工 + 回放 | M1 |
| S13 | O1：追加 9001 后 `ModePacked` 变为 44 位，确认 snapshot 编解码与上游测试没有限定 43 位的 comptime 断言 | 构建 + 快照往返 | M0 |
| S14 | O3：延后应答 + `GhosttyClipboardRead.terminator` 追加字段；host 稍后写入的应答被 neovim / tmux 正确接收 | 回放 + 手工 | M2 |
| S15 | `terminal_get_multi` 是否允许重复的 `DATA_MODE` 键（§13.1）| 单元测试 | M0 |
| S16 | revive 裁剪：中途释放解码器后再编码（§9.4 第 3 步）| 单元测试 | M2 |
| S9 | OSC 52 读阻塞期间的用户体验（30 s 上限是否合适）| 手工 | M4 |
| S10 | 托管 PNG 解码器的性能（1920×1080 PNG）与 fuzz | 基准 + fuzz | M6 |
| S11 | SCREEN 坐标与 scrollbar offset 是否同一行空间（§8.5、§8.6）| 单元测试 | M1 |
| S12 | 差分测试本身的开销（每块全量重建）能否在 CI 时间预算内 | 计时 | M1 |
| R1 | 上游 API 变动频繁（近 90 天 `include/ghostty/vt` 82 次提交 [R01 §4.1.1]）| 每月升级 + §2.2 检查单 | 持续 |
| R2 | 上游迁出 GitHub 期间源不可用 | §2.5 | 持续 |
| R3 | dirty 漏标类问题（§15.5）| 差分测试 + overlay | M1 起 |
| R4 | ReleaseFast 下 Zig 断言变为未定义行为，被恶意输出触发 | 报上游；fuzz 与差分测试用 ReleaseSafe 构建尽早发现 | 持续 |
| R5 | 快照格式在升级间不兼容 | 只用于同构建进程间；revive 带 `tail.vt` 回退（§9.4）| — |
| R6 | overlay O1–O4 在上游升级时失效 | §2.2 第 6 步；每项都有退路（§2.4）| 持续 |

---

## 17. 与 01 / 04 / 06 的差异及待决问题

本文没有修改其他文档。按协调方更新后的 01（D9、规则 1–8、`InputModes`、`TerminalSize`、`ghosttyBuildId`）和 04 §8、§10 对齐后，仍需处理的差异：

| # | 位置 | 现状 | 本文结论 | 依据 |
|---|---|---|---|---|
| 1 | 01 §6.1 `ClipboardRead` 注释"需要询问时返回待定"；04 §11.2"返回待定 → 稍后写回" | libghostty 原生不支持"待定"：回调返回而不应答，库会自动回空剪贴板，之后 host 再写会产生两次应答 | 需要 overlay **O3**；退路是回调内同步阻塞。01 规则 6 的流程不变 | [S:stream_terminal.zig:1072-1101]、§14.3 |
| 2 | 01 规则 7 "5522 在 v1 不启用" | 镜像核心为 OSC 52 读安装了 `clipboard_read`，库会把 5522 报告为已识别、并允许程序打开它 | overlay **O2** 把 5522 标为 disabled；退路下只是 DECRQM 声明不准确，字节一致 | [S:stream_terminal.zig:1908-1920]、§2.4 |
| 3 | 01 §5.2 / 06 §3.4 的 `InputModes` 含 modifyOtherKeys | C API 没有读取 modifyOtherKeys 的接口 | overlay **O4**；退路：按键回到 actor 上编码 | §13.1 |
| 4 | 01 规则 8（9001）| — | 采用 overlay **O1**（具体计划见 §2.4），退路为托管扫描器 | §13.5 |
| 5 | 06 §6 "OSC 52 写由 UI 侧核心触发、镜像核心忽略" | 与 04 §11 "效果由镜像核心产生、UI 侧核心忽略"相反 | 按 04：镜像核心产生，host 发 `session/clipboardWrite` 给主视图 | §7.2 |
| 6 | 04 §8.2 "编码超过 50 ms 就改用 `historyLines` 裁剪" | 裁剪在完整编码之后，不减少会话 actor 的占用时间 | 该退路只减小传输与 UI 解码；actor 时间需上游接口或 ext | §9.2 |
| 7 | 04 §10.3 "中途停止解码的终端能否再编码"待验证 | 头文件说明放弃增量解码后终端可继续使用 | 先 `decoder_free` 再编码，列为 S16 | [H:snapshot.h:434-445]、§9.4 |
| 8 | 04 §6.2 记录类型 | 没有"修改回滚上限" | v1 回滚设置只对新会话生效 | §10 |
| 9 | 04 §6.2 `Clear` | C API 没有清屏函数 | 门面注入 `CSI 3 J` / `CSI H CSI 2 J`，要求 host 只在镜像核心处于 ground 时追加该记录 | §6.6 |
| 10 | 01 §6.1 `Restore(ReadOnlySequence<byte>)` | 一次性恢复 | 增量的 `BeginRestore(ISnapshotSource) → RestoreSession`（与 04 §8.2 的 `decoder_next` 循环一致）| §9.3 |
| 11 | 01 §3.1 "重连时恢复屏幕和回滚" | Kitty 图像不在快照里 | 重连后已有图像在 UI 侧丢失，v1 接受并提示 | §11.5 |
| 12 | 01 §6.1 `RowId` 注释"来自 libghostty 的行 ID" | libghostty 行 id 为 128 位 | 门面映射为 64 位自增 id，另加 `ContentHash` | §8.3 |
| 13 | 01 §1、04 §7.1 "libghostty 解析约 500 MB/s" | 调研只有相对数据（SIMD 比标量快 7.3 倍），没有 MB/s 实测 [R01 §3.4] | 改为"待基准测试确定"，04 的流控参数推算以此为前提 | — |
| 14 | D9 Windows 宽度锁定为字形簇 | libghostty 没有"永久置位"，程序仍能复位 2027，DECRQM 回答 1/2 而不是 3 | v1 接受；严格锁定需要额外 overlay，不推荐 | §13.4 |

待决问题：

1. ~~DA2 应答~~：已决定 `>1;10;0c`（2026-10-17，见 [10](10-protocol-compat.md) §2）。
2. ~~pty host 的原生库用 ReleaseFast 还是 ReleaseSafe~~：已决定发布用 **ReleaseFast**（2026-10-17，与 Ghostty 一致）；fuzz 和差分测试继续用 ReleaseSafe 构建。
3. **是否以后引入 `ext` 解决 §8.9 余下的缺口**（重连丢图、动画自动播放、2033、OSC 99）。

---

## 18. 来源

| 键 | 来源 |
|---|---|
| H | ghostty-org/ghostty @ `b3dc3d794513c11633e7c3347935693957744e83`（2026-10-15）`include/ghostty/vt/*.h`、`include/ghostty/vt.h` |
| S | 同一 commit 的 `src/terminal/`：`Terminal.zig`、`Screen.zig`、`page.zig`、`render.zig`、`stream_terminal.zig`、`stream_continuation.zig`、`modes.zig`、`bitmap_allocator.zig`、`kitty/graphics_storage.zig`、`kitty/graphics_animation.zig`、`kitty/graphics_image.zig`、`snapshot/{main,terminal,screen}.zig`、`c/types.schema.json`；`pkg/wuffs/build.zig` |
| RT | royalapplications/RoyalTerminal @ `01833b8`：`native/ghostty-vt-extensions/README.md`、`src/RoyalTerminal.GhosttySharp/GhosttySys.cs` |
| DN | deblasis/libghostty-vt-dotnet @ `c6f23d8`：`ghostty-upstream.json`、`build/generate-bindings.sh`、`build/generate-bindings.rsp`、`build/check-binding-symbols.sh`、`src/Ghostty.Vt/Native/NativeMethods.cs` |
| MH-0428 | https://mitchellh.com/writing/ghostty-leaving-github（2026-04-28）|
| R01/R03/R04/R05 | 本目录 `research/` 下的调研报告 |
