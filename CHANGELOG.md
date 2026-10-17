# Changelog

## 0.1.2

Renames the project from TCode to TKCode and reduces work while thinking blocks stream.

### Changed

- **Project renamed from TCode to TKCode.** This covers the solution, projects and namespaces, the pty and ghostty native libraries (`libtkcodepty`, `tkcode-pty-spawn`, `tkcode_ghostty.h`), the macOS bundle id (`com.tkcode.desktop`), and the GitHub repository (`AtomUICommunity/TKCode`).
- **Data directory and environment variables are renamed.** The data directory is now `~/.tkcode` and every `TCODE_*` variable is now `TKCODE_*` (for example `TKCODE_HOME`). Existing data in `~/.tcode` is not migrated automatically; move it with `mv ~/.tcode ~/.tkcode`.
- Streaming thinking blocks update their duration and token statistics at most every 500 ms, and count characters incrementally instead of rescanning the whole text on every refresh.
- StreamMark is updated to `0.1.0-alpha.19` (position- and history-independent fallback fonts; fixes Windows CI font handling).

**Full Changelog**: https://github.com/AtomUICommunity/TKCode/compare/v0.1.1...v0.1.2

## 0.1.1

Largest update since 0.1.0. Adds conversation compaction, an interactive question panel for the agent, file change tracking with revert, and a number of desktop UI and release-pipeline changes.

### Added

- **Conversation compaction.** When a session approaches the model's context limit, TKCode summarizes the history and continues the session from the summary. Compaction runs proactively before a request and reactively when the provider reports a context overflow. The summary replaces the earlier history as a whole; the rest of the session continues unchanged.
- **Agent questions (`ask_user`).** The agent can ask the user a structured question. The question appears in a panel that replaces the composer while it is pending; sending is blocked until the question is answered. External runtimes' own question tools (Claude Code, Codex, ACP) use the same panel.
- **Change tracking and diff view.** A new Changes tab lists files the agent modified, with a per-turn changes view and a diff view for each file.
- **Revert file changes.** Files changed by the agent can be reverted from the change views.
- **Built-in prompt commands.** `/commit` and `/init` are available as built-in slash commands.
- **Session file reading.** A dedicated session file reader is added to the coding session store.
- **Resizable explorer panel.** The explorer and workbench panels can be resized with a splitter.

### Changed

- Image attachments are reworked: attachment storage and handling in the session store were rewritten.
- Background task notifications are emitted when the set of running background tasks changes.
- Idle sessions can release their in-memory resources in the Agent Host.
- Tool calls are rendered as step views with a dedicated task orb, replacing the old task list.
- Terminal tabs use the updated terminal theme and the refreshed tab view.
- Settings schema and localization strings (English and Simplified Chinese) were updated for the new features.
- Shared line-diff logic moved from the coding tools into the core `TKCode.Agent` library so both the agent and the UI can use it.

### Removed

- The macOS installer package (`component.plist` and `postinstall`). macOS builds ship as the `.app` bundle and the dmg/zip artifacts.
- The standalone task list view, replaced by the step views and task orb.

### Release process

- Release versions come from the git tag only. Tagging `v0.1.1` builds `0.1.1`; the local build stays `0.0.0-dev`.
- The release CI creates a draft release for stable tags. Tags containing `-` are published as pre-releases.

**Full Changelog**: https://github.com/AtomUICommunity/TKCode/compare/v0.1.0...v0.1.1
