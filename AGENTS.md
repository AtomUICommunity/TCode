# AGENTS.md

Guidance for coding agents and new contributors. The "what" lives in the code and docs; this file is the non-obvious rules. Verify against the code when in doubt.

## Stack

- Desktop AI agent client: Avalonia 12 + AtomUI 6 on .NET 10 (`global.json` pins 10.0.300, `latestFeature`). Single solution `TKCode.slnx` — add new projects to it.
- Everything must stay NativeAOT-safe: `TKCode.Desktop`, `TKCode.AgentHost`, and `TKCode.Terminal.Host` set `PublishAot=true`; all libraries are `IsAotCompatible`.

## Setup (first build fails without it)

1. `git submodule update --init` — pulls `external/StreamMark`.
2. `tools/streammark/pack.sh` — packs `StreamMark.*` into `packages/streammark` (the local NuGet feed in `nuget.config`). Needs a Rust toolchain once, for the RaTeX native lib. After bumping the submodule, keep `StreamMarkVersion` in `Directory.Packages.props` in sync.
3. Terminal work only: Zig 0.16.0 exactly — do not use a package-manager Zig. `tools/terminal/build-ghostty.sh` auto-installs the pinned version via `tools/terminal/install-zig.sh`.

## Commands

| Purpose | Command |
|---|---|
| Build | `dotnet build TKCode.slnx` |
| Run app | `dotnet run --project src/TKCode.Desktop` |
| All tests | `dotnet test TKCode.slnx` |
| One project / one test | `dotnet test tests/<Project>` / `--filter "FullyQualifiedName~<Name>"` |
| AOT publish | `dotnet publish src/TKCode.Desktop -c Release -r <rid>` — the log must contain zero `warning IL####` (CI fails on any) |
| macOS app bundle | `tools/package_macos_app.sh osx-arm64` → `artifacts/TKCode.app` |
| Smoke-test a published build | `artifacts/publish/<rid>/TKCode --smoke --out=frame.png` |
| Regenerate README screenshots | `tools/readme-screenshots.sh` → `docs/images/` |
| Memory/CPU benchmark | `tools/mem-bench.sh [rid]` (opens real windows, ~25 s per scenario) |

Terminal native libraries (only for `TKCode.Terminal.*` / `native/` work):

- `tools/terminal/build-ghostty.sh [rid]` builds `libghostty-vt` into `artifacts/native/<rid>/`; `native/tkcodepty/build.sh [rid]` builds the Unix PTY lib + spawn helper.
- MSBuild picks the libs up via `native/TerminalNative.targets`; a build without them only warns — run the scripts if terminal tests fail to load natives.

## Layout

- `src/TKCode.Ai` → `TKCode.Agent` → `TKCode.Agent.Coding`: headless core (wire adapters, agent loop, coding session/tools). No UI references, ever.
- `src/TKCode.Rpc.Contracts`: hand-written JSON-RPC 2.0 over stdio (replaced StreamJsonRpc, which breaks under AOT). `src/TKCode.AgentHost`: session-host process. `src/TKCode.Config`: settings shared by Host and Desktop.
- `src/TKCode.Runtimes`: external agent runtimes (Claude Code, Codex, ACP). `src/TKCode.Mcp`: MCP client.
- `src/TKCode.Terminal.*`: Core (libghostty-vt bindings/facade), Pty, Protocol, Host (the `tkcode-ptyhost` process), Avalonia view.
- `src/TKCode.Desktop`: the Avalonia app. `native/`: Zig/C sources and toolchain pins. `tools/`: build/gen/package scripts. `bench/`: memory harness. `docs/`: architecture and design docs. `external/StreamMark`: submodule, do not commit changes from this repo into it casually.

## Hard rules

- **AOT:** JSON only via source-generated `JsonSerializerContext` contexts; build request bodies with `JsonNode` (no generic `JsonArray.Add<T>`). No reflection, `Activator`, dynamic proxies, or runtime codegen. Vet every new NuGet package for AOT compatibility and confirm a zero-warning publish.
- **AtomUI** comes from NuGet only; never copy or modify its source; theme via public tokens/theme XML. `WithDefaultTheme` does not exist in 6.2.5 — use `WithInitialTheme` / `IThemeManager`.
- **Central package versions** in `Directory.Packages.props` only; no `Version` attributes on PackageReferences.
- **Localization:** the single source of truth is `src/TKCode.Desktop/Localization/strings.tsv` (key, en-US, zh-CN). Regenerate with `python3 -I tools/gen_strings.py`; never hand-edit `TKCodeLangResourceKind.g.cs` or the `.xlf` files.
- **Generated assets:** provider icons (`tools/gen_provider_icons.py` → `Icons/ProviderIconPaths.g.cs`) and the model catalog (`tools/gen_catalog.py` → `TKCode.AgentHost/Catalog/catalog.json`) are generated — edit the inputs, rerun the scripts.

## Protocol constraints (see docs/architecture.md, docs/protocols/)

- Conversation history is append-only; the system prompt and tool set freeze at session start; changes are expressed by appending `SystemMessage`s. Compaction (summary + new turn) is the only history replacement and happens only between model requests.
- Streams never throw: every failure ends as an `ErrorEvent` (`StopReason` `Error`/`Aborted`), keeping partial content.
- Implement adapters strictly per the specs in `docs/protocols/`; replay provider-opaque data (thinking signatures, reasoning items) verbatim.
- Host's stdout is reserved for JSON-RPC frames — Host logs go to stderr.
- Adding an RPC method touches four places: the interface, `RpcMethods`, `RpcJsonContext`, and the proxy + dispatcher (docs/architecture.md §5).

## Tests

- `tests/TKCode.Desktop.Tests` and `TKCode.Terminal.Avalonia.Tests` render real components headlessly (Avalonia.Headless + Skia) and write PNGs to `snapshots/` (or `$TKCODE_SNAPSHOT_DIR`). Do not drive the real desktop or synthesize OS input.
- Useful env vars: `TKCODE_HOME` (redirects `~/.tkcode`), `TKCODE_AGENTHOST_DIR`, `TKCODE_TEST_HOST_EXE` (point RPC e2e tests at an AOT-published Host), `TKCODE_PTYHOST_DIR` (AOT pty host), `TKCODE_DIFF_SEEDS` / `TKCODE_FUZZ_MINUTES` / `TKCODE_PTY_STRESS` (long nightly sweeps, skipped unless set).

## CI

- `release.yml` (on `v*` tags): builds StreamMark native libs and packs NuGet → builds terminal native libs → per-platform AOT publish with the zero-warning gate → smoke tests → signed manifest → draft GitHub Release.
- `terminal.yml` (on terminal/native paths): native libs for all RIDs → tests on 3 platforms → esctest2 → AOT `tkcode-ptyhost` + `--self-test`.

## Pitfalls

- Restore/build fails on `StreamMark.*` → you skipped the setup steps above.
- The Desktop build auto-copies the Agent Host into `agenthost/` and the pty host into `ptyhost/` in its output; publishing AOTs both into the same subfolders. Run the app binary, not the hosts directly.
- Local builds stay version `0.0.0-dev` (no update checks); release CI overrides `-p:Version=<tag without v>`.
- Linux AOT binaries link the builder's glibc; the native floor is glibc 2.28 (CI verifies).
- Some scripts mentioned in `docs/terminal/12-build-and-ci.md` (`build-native.sh`, `verify-native.sh`) do not exist yet — trust the files in `tools/`, not the doc.
