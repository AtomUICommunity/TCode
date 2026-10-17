<div align="center">

<img src="logo.png" alt="TCode" width="128" />

# TCode

**A desktop AI agent client built with Avalonia and AtomUI.**

[简体中文](README.zh-CN.md) · English

![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![Avalonia](https://img.shields.io/badge/Avalonia-12.1-8B44AC)
![NativeAOT](https://img.shields.io/badge/NativeAOT-ready-success)
![Status](https://img.shields.io/badge/status-early%20development-orange)

</div>

TCode is a native desktop client for working with AI coding agents. It runs its agent loop in a separate Agent Host process, talks to models through three wire protocols, and keeps every session as a local, append-only JSONL file. The whole client is designed to publish as a NativeAOT binary.

> **Status:** early development. The core pieces (protocol adapters, agent loop, session store, Host process, desktop UI) exist, but the app is not yet a finished product, and APIs and file formats may change.

## Features

- **Project- and session-centric UI.** Dark-first visual design with light and system-following themes; Chinese and English UI.
- **Command palette (⌘K)** with nested pages, searchable project and session lists, and keyboard navigation.
- **Multiple providers and models.** Built-in provider catalog with a single API key per provider, custom OpenAI-compatible endpoints, and hot-reloaded settings.
- **Three protocol adapters:** OpenAI Chat Completions, OpenAI Responses, and Anthropic Messages, with thinking / reasoning replay and fast mode where the provider supports it.
- **Coding tools for the built-in agent:** `read`, `write`, `edit`, `ls`, `glob`, `grep`, `bash` (with background tasks), task list tools, `web_fetch`, `web_search`, and multi-agent coordination.
- **Approval flow.** Tool calls go through a permission policy; read-only actions inside the workspace pass, anything else can ask the UI first.
- **Resumable sessions.** Sessions are stored as JSONL trees under `~/.tcode/sessions/` and can be listed, reopened, and continued.
- **Streaming Markdown** via StreamMark (code highlighting, Mermaid, LaTeX).

## Architecture

```
TCode.Desktop ──(JSON-RPC 2.0 over stdio)──▶ TCode.AgentHost
     │                                            │
     └──▶ TCode.Rpc.Contracts ◀───────────────────┤
                                                  ▼
                                        TCode.Agent.Coding   sessions, tools, system prompt, AGENTS.md
                                                  ▼
                                           TCode.Agent       agent loop, tool contract, events
                                                  ▼
                                            TCode.Ai         message model, streaming events, wire adapters
```

| Project | Responsibility |
|---|---|
| `src/TCode.Ai` | Unified message model, streaming events, OpenAI / Anthropic adapters |
| `src/TCode.Agent` | Agent loop, tool abstraction, approval hook, steer / follow-up queues |
| `src/TCode.Agent.Coding` | Coding session, JSONL session store, system prompt builder, built-in tools |
| `src/TCode.Rpc.Contracts` | Host ↔ UI interfaces and DTOs, minimal JSON-RPC 2.0 implementation |
| `src/TCode.AgentHost` | Standalone process that hosts sessions and translates agent events into RPC notifications |
| `src/TCode.Config` | Settings types and parsing, shared by Host and Desktop |
| `src/TCode.Runtimes` | Groundwork for external agent runtimes (see Roadmap) |
| `src/TCode.Desktop` | Avalonia application: window, theme, views, view models, Host connection |

Dependencies point one way. `TCode.Ai`, `TCode.Agent`, and `TCode.Agent.Coding` have no UI dependency and are marked `IsAotCompatible`.

## Getting started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (pinned in `global.json`, `latestFeature` roll-forward)
- A sibling checkout of **StreamMark** at `../StreamMark`. The `StreamMark.*` packages are read from a local feed (`../StreamMark/artifacts/packages`, set in `nuget.config`), so pack them first:

  ```bash
  # from the StreamMark checkout, next to this repository
  dotnet pack -o artifacts/packages
  ```

- AtomUI (`AtomUI.Desktop.Controls`) is consumed from nuget.org; no source checkout is needed.

### Build and run

```bash
dotnet build TCode.slnx
dotnet run --project src/TCode.Desktop
```

The desktop build copies the Agent Host into the output directory's `agenthost/` folder, and the app starts it on demand.

### Run the tests

```bash
dotnet test TCode.slnx
```

The Desktop tests render real components headlessly (Avalonia.Headless + Skia) and write PNG snapshots to the test output's `snapshots/` directory, or to `TCODE_SNAPSHOT_DIR` if set.

### Publish (NativeAOT)

Both the desktop app and the Agent Host are published with `PublishAot=true`. On macOS you can build an app bundle:

```bash
tools/package_macos_app.sh osx-arm64        # outputs artifacts/TCode.app
```

Or publish directly:

```bash
dotnet publish src/TCode.Desktop -c Release -r osx-arm64
```

Smoke test a published build (runs the real app headlessly against the Fake provider):

```bash
artifacts/publish/osx-arm64/TCode --smoke --out=frame.png
```

## Configuration

Settings live in `~/.tcode/settings.json` (JSONC, with a generated JSON Schema for editor completion). Set `TCODE_HOME` to use a different directory. The file is hot-reloaded; invalid edits keep the last good configuration and show the error in the settings view.

```jsonc
{
  "$schema": "./settings.schema.json",
  "model": "anthropic/claude-opus-5-5",   // <provider>/<model>
  "language": "en-US",                     // zh-CN | en-US
  "theme": "system",                       // system | dark | light

  "providers": {
    // Built-in provider: only the key is required
    "anthropic": { "apiKey": "{env:ANTHROPIC_API_KEY}" },

    // Custom OpenAI-compatible endpoint
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

API keys can be plain text, `{env:VAR}`, or `$VAR`. Saving from the UI sets the file mode to `0600`. See [docs/settings.md](docs/settings.md) for the full specification.

## Documentation

| Document | Contents |
|---|---|
| [docs/architecture.md](docs/architecture.md) | Technical decisions, layering, RPC, UI and theming, NativeAOT rules |
| [docs/settings.md](docs/settings.md) | `settings.json` format and merge rules |
| [docs/design/builtin-agent-tools.md](docs/design/builtin-agent-tools.md) | Built-in agent system prompt and tool design |
| [docs/design/external-agent-runtimes.md](docs/design/external-agent-runtimes.md) | Plans for Claude Code, Codex, and ACP runtimes |
| [docs/design/motion.md](docs/design/motion.md) | Motion guidelines |
| [docs/protocols/](docs/protocols/) | Wire protocol specs the adapters implement |

## Roadmap

- Harden the three protocol adapters against recorded provider traffic.
- Finish built-in write / edit / bash tools with an approval UI.
- External runtimes: Claude Code (stream-json), Codex (`app-server`), and generic ACP agents, reusing the TCode approval flow.
- Context compaction, session branching and fork, integrated terminal, diff view.

## Contributing

Issues and pull requests are welcome once the project settles. Please keep these rules in mind:

- **NativeAOT.** `TCode.Ai`, `TCode.Agent`, `TCode.Agent.Coding`, `TCode.Desktop`, and `TCode.AgentHost` must stay AOT-safe. Use source-generated JSON contexts and no reflection or runtime code generation. Check that an AOT publish produces zero IL/AOT warnings.
- **UI components.** Use AtomUI through NuGet and its public theme tokens. Do not copy or modify AtomUI source.
- **Strings.** UI text comes from `src/TCode.Desktop/Localization/strings.tsv`. Regenerate the catalogs with `tools/gen_strings.py`; do not hand-edit the generated files.

## License

TCode is licensed under the [Apache License 2.0](LICENSE).
