<div align="center">

<img src="logo.png" alt="TCode" width="128" />

# TCode

**基于 Avalonia 与 AtomUI 构建的桌面 AI Agent 客户端。**

简体中文 · [English](README.md)

![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![Avalonia](https://img.shields.io/badge/Avalonia-12.1-8B44AC)
![NativeAOT](https://img.shields.io/badge/NativeAOT-ready-success)
![Status](https://img.shields.io/badge/status-early%20development-orange)

</div>

TCode 是一个用于协作 AI 编程 Agent 的原生桌面客户端。Agent 循环运行在独立的 Agent Host 进程中，通过三种线协议与模型通信，每个会话都以只追加的 JSONL 文件保存在本地。整个客户端按 NativeAOT 发布设计。

> **状态：** 早期开发阶段。协议适配器、Agent 循环、会话存储、Host 进程和桌面界面都已有实现，但还不是完整的产品，接口和文件格式仍可能变化。

## 功能特性

- **以项目 / 会话为中心的界面。** 暗色优先的视觉设计，支持亮色及跟随系统；界面中英双语。
- **命令面板（⌘K）**：多级页面、可搜索的项目和会话列表，支持键盘导航。
- **多厂商、多模型。** 内置厂商目录（每个厂商只需一个 API Key），也支持自定义 OpenAI 兼容端点；设置文件修改后自动热重载。
- **三种协议适配器：** OpenAI Chat Completions、OpenAI Responses、Anthropic Messages，支持思考 / 推理内容的原样回放，并在厂商支持时启用快速模式。
- **内置编码工具：** `read`、`write`、`edit`、`ls`、`glob`、`grep`、`bash`（支持后台任务）、任务清单、`web_fetch`、`web_search`，以及多 Agent 协作。
- **审批流程。** 工具调用经过权限策略判定：工作区内的只读操作直接放行，其余操作可以先向界面询问。
- **可恢复的会话。** 会话以 JSONL 树的形式保存在 `~/.tcode/sessions/`，可以列出、重新打开并继续。
- **流式 Markdown**，由 StreamMark 提供（代码高亮、Mermaid、LaTeX）。

## 架构

```
TCode.Desktop ──(JSON-RPC 2.0，经 stdio)──▶ TCode.AgentHost
     │                                            │
     └──▶ TCode.Rpc.Contracts ◀───────────────────┤
                                                  ▼
                                        TCode.Agent.Coding   会话、工具、系统提示、AGENTS.md
                                                  ▼
                                           TCode.Agent       Agent 循环、工具契约、事件
                                                  ▼
                                            TCode.Ai         统一消息模型、流式事件、协议适配器
```

| 项目 | 职责 |
|---|---|
| `src/TCode.Ai` | 统一消息模型、流式事件、OpenAI / Anthropic 适配器 |
| `src/TCode.Agent` | Agent 循环、工具抽象、审批钩子、steer / followUp 队列 |
| `src/TCode.Agent.Coding` | 编码会话、JSONL 会话存储、系统提示构建、内置工具 |
| `src/TCode.Rpc.Contracts` | Host 与界面之间的接口和 DTO，精简的 JSON-RPC 2.0 实现 |
| `src/TCode.AgentHost` | 独立进程：托管多个会话，并把 Agent 事件翻译为 RPC 通知 |
| `src/TCode.Config` | 设置类型与解析，Host 和桌面端共用 |
| `src/TCode.Runtimes` | 外部 Agent 运行时的基础代码（见路线图） |
| `src/TCode.Desktop` | Avalonia 应用：窗口、主题、视图、ViewModel、Host 连接 |

依赖方向严格单向。`TCode.Ai`、`TCode.Agent`、`TCode.Agent.Coding` 不依赖任何 UI，并标记为 `IsAotCompatible`。

## 快速开始

### 环境要求

- [.NET 10 SDK](https://dotnet.microsoft.com/download)（版本在 `global.json` 中锁定，滚动策略为 `latestFeature`）
- 同级目录 `../StreamMark` 下的 **StreamMark** 源码。`StreamMark.*` 包从本地源读取（`nuget.config` 中指定的 `../StreamMark/artifacts/packages`），需要先打包：

  ```bash
  # 在 StreamMark 源码目录中执行
  dotnet pack -o artifacts/packages
  ```

- AtomUI（`AtomUI.Desktop.Controls`）直接从 nuget.org 引用，无需源码。

### 构建与运行

```bash
dotnet build TCode.slnx
dotnet run --project src/TCode.Desktop
```

桌面端构建时会把 Agent Host 复制到输出目录的 `agenthost/` 子目录中，应用在需要时自动启动它。

### 运行测试

```bash
dotnet test TCode.slnx
```

桌面端测试使用 Avalonia.Headless + Skia 渲染真实组件，把 PNG 截图写入测试输出目录下的 `snapshots/`，也可以用 `TCODE_SNAPSHOT_DIR` 指定目录。

### 发布（NativeAOT）

桌面端和 Agent Host 都以 `PublishAot=true` 发布。macOS 上可以直接生成 .app 包：

```bash
tools/package_macos_app.sh osx-arm64        # 输出到 artifacts/TCode.app
```

或直接发布：

```bash
dotnet publish src/TCode.Desktop -c Release -r osx-arm64
```

对发布产物做冒烟测试（以无头平台运行真实界面，使用 Fake 模型）：

```bash
artifacts/publish/osx-arm64/TCode --smoke --out=frame.png
```

## 配置

设置文件位于 `~/.tcode/settings.json`（JSONC 格式，附带自动生成的 JSON Schema，编辑器可以补全）。设置 `TCODE_HOME` 环境变量可以改用其它目录。文件修改后会热重载；如果写入的内容有错误，程序保留上一次正确的配置，并在设置界面中显示错误。

```jsonc
{
  "$schema": "./settings.schema.json",
  "model": "anthropic/claude-opus-5-5",   // <厂商>/<模型>
  "language": "zh-CN",                     // zh-CN | en-US
  "theme": "system",                       // system | dark | light

  "providers": {
    // 内置厂商：只需要 Key
    "anthropic": { "apiKey": "{env:ANTHROPIC_API_KEY}" },

    // 自定义的 OpenAI 兼容端点
    "my-gateway": {
      "name": "My Gateway",
      "api": "chat",                       // chat | responses | messages
      "baseUrl": "https://gw.example.com/v1",
      "apiKey": "{env:MY_GATEWAY_KEY}",
      "models": { "qwen3-coder": { "contextWindow": 262144, "reasoning": true } }
    }
  }
}
```

API Key 可以写明文、`{env:VAR}` 或 `$VAR`。在界面中保存时，文件权限会设为 `0600`。完整规格见 [docs/settings.md](docs/settings.md)。

## 文档

| 文档 | 内容 |
|---|---|
| [docs/architecture.md](docs/architecture.md) | 技术决策、分层、RPC、界面与主题、NativeAOT 约束 |
| [docs/settings.md](docs/settings.md) | `settings.json` 格式与合并规则 |
| [docs/design/builtin-agent-tools.md](docs/design/builtin-agent-tools.md) | 内置 Agent 的系统提示与工具设计 |
| [docs/design/external-agent-runtimes.md](docs/design/external-agent-runtimes.md) | Claude Code、Codex、ACP 运行时的规划 |
| [docs/design/motion.md](docs/design/motion.md) | 动效规范 |
| [docs/protocols/](docs/protocols/) | 适配器实现所依据的线协议规格 |

## 路线图

- 用录制的真实流量加固三个协议适配器。
- 完成内置的 write / edit / bash 工具及其审批界面。
- 外部运行时：Claude Code（stream-json）、Codex（`app-server`）以及通用 ACP Agent，复用 TCode 的审批流程。
- 上下文压缩、会话分支与 fork、集成终端、变更 diff 视图。

## 参与贡献

项目趋于稳定后欢迎提交 Issue 和 Pull Request。请注意以下约定：

- **NativeAOT。** `TCode.Ai`、`TCode.Agent`、`TCode.Agent.Coding`、`TCode.Desktop`、`TCode.AgentHost` 必须保持 AOT 兼容：使用源生成的 JSON 上下文，不使用反射或运行时代码生成。AOT 发布应当做到 0 个 IL / AOT 警告。
- **UI 组件。** 通过 NuGet 使用 AtomUI 及其公开的主题 Token，不复制或修改 AtomUI 源码。
- **文案。** 界面文字的唯一来源是 `src/TCode.Desktop/Localization/strings.tsv`。用 `tools/gen_strings.py` 重新生成目录，不要手改生成文件。

## 许可证

TCode 基于 [Apache License 2.0](LICENSE) 开源。
