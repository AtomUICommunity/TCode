# TCode 终端：构建与 CI（12）

> 状态：设计稿 v1（2026-10-17）。上位文档：[01-architecture.md](01-architecture.md)（D1、D3、D9，§4 项目划分，§8 原生库签名，§10 每个里程碑"AOT 发布 0 警告并运行二进制"）。相关：[04-pty-host.md](04-pty-host.md) §1.1（`ptyhost/` 发布布局、`ghostty-build.txt`、`--self-test`）、[06-input.md](06-input.md) §3.7（需要 overlay 补丁登记模式 9001）、[07-shell-profiles-config.md](07-shell-profiles-config.md)（libtcodepty 的 `tcode_get_account`）、[11-testing-benchmarks.md](11-testing-benchmarks.md)（各测试作业的内容）。
>
> 本文规定：本地开发环境；`native/` 与 `tools/terminal/` 的目录结构；一条命令的本地构建；MSBuild 如何拾取原生产物；libghostty-vt、libtcodepty、tcode-pty-spawn 的构建；ConPTY 的引用与发布布局；pty host 的打包、签名与公证；CI 工作流；升级流程；故障排查。

**标注约定**：**[已核实]** = 对照源码、官方下载索引、上游 CI 或本机核对过；**[推断]** = 设计推理，需要在 M0 实测。上游版本：Ghostty `b3dc3d794513c11633e7c3347935693957744e83`（`b3dc3d7`，2026-10-15）、RoyalTerminal `01833b8`（2026-10-09）、StreamMark 本地仓库 `.github/workflows/ci.yml`。

---

> **M0 实际落地（2026-10-17）**：已有的脚本是 `tools/terminal/install-zig.sh`、`tools/terminal/build-ghostty.sh [rid] [--optimize …]`、`tools/terminal/prefetch-zig-deps.sh`（代理环境下预取 Zig 依赖）、`native/tcodepty/build.sh [rid]`、`tools/streammark/pack.sh`；MSBuild 拾取规则在 `native/TerminalNative.targets`；CI 是 `.github/workflows/terminal.yml`。本文中的一键脚本 `build-native.sh`、`verify-native.sh`、ConPTY 打包与签名流程尚未实现，按本文在 M1 补齐。

## 0. 结论摘要

| 主题 | 结论 |
|---|---|
| TCode 现有 CI | **没有**：仓库根目录没有 `.github/`（2026-10-16 检查）[已核实]。本文的工作流是 TCode 的第一个 CI，需要同时覆盖现有的 .NET 测试 |
| 工具链 | .NET SDK 10.0.300+（`global.json` 已锁定，`rollForward: latestFeature`）[已核实]；**Zig 精确为 0.16.0**（官方 tarball + SHA-256 校验；本机 Homebrew 装的是 0.17.0，不可用）；Linux 的 libtcodepty 与 helper 用 `zig cc`（不需要 gcc），macOS 用 Apple clang（03 §11.1）；Windows 上 libghostty 的 `x86_64-windows-msvc` 目标需要 VS 2022 Build Tools（MSVC + Windows SDK）；CMake 不需要 |
| 一条命令 | `tools/terminal/build-native.sh [rid]` / `build-native.ps1 -Rid <rid>`：只构建当前（或指定）RID；按"上游提交 + overlay 补丁 + Zig 版本 + 构建参数 + 脚本版本"的哈希做缓存；产物在 `artifacts/native/<rid>/` |
| MSBuild | 仿照 StreamMark：根目录 `Directory.Build.targets` 按使用方的 RID 把 `artifacts/native/<rid>/` 中的文件平铺复制到输出与发布目录；`TCode.Terminal.Core` / `.Pty` 另以 `runtimes/<rid>/native/` 打包，留给将来拆成独立仓库（D2） |
| 动态 vs 静态 | **libghostty-vt：v1 全部动态**（Debug 与 AOT 同一条路径）；**libtcodepty：按 03 §11.5，ptyhost 的 Unix AOT 发布静态链接 `.a`，Debug / 测试用动态库**；libghostty 静态链接作为 M6 之后的可选优化 |
| libghostty-vt | 每个 RID 显式 `-Dtarget`（禁止本机 CPU 推断），`-Doptimize=ReleaseFast -Dstrip=true`，从 git 检出构建以便 `build_info` 带上提交号；Linux 目标写成 `*-linux-gnu.2.28`（RHEL 8）；win-x64 先用 RoyalTerminal 验证过的标量构建，同时跑 SIMD 构建的 CPU 兼容性验证；win-arm64 用 `aarch64-windows-gnu` 且 `-Dsimd=false` |
| libtcodepty + helper | 4 个 Unix RID，构建以 03 §11 为准：Linux `zig cc -target *-linux-gnu.2.28`，helper 用 musl 完全静态；macOS Apple clang；约 690 行 C（03 §1）；Windows 不需要 |
| ConPTY | `Microsoft.Windows.Console.ConPTY` **1.25.260930003**，只由 `TCode.Terminal.Host` 直接引用，`ConptyRequires{x64,ARM64}Host=true`；布局 `ptyhost\conpty.dll` + `ptyhost\{x64,arm64}\OpenConsole.exe`（03 §11.3–11.4），发布后校验 |
| Linux AOT | AOT 主程序链接发布机的 glibc → Linux 发布在 `almalinux:8`（glibc 2.28）容器中进行（§5.3） |
| macOS 下限 | 以 .NET 10 的下限为准（**15.0**，03 §11.1 引 supported-os.md）；`LSMinimumSystemVersion` 从 11.0 提高到 15.0（§6.2） |
| 打包 | 采用 04 §1.1 的 `ptyhost/` 子目录；macOS 包内 Mach-O 文件留在 `Contents/MacOS/**`，非 Mach-O 文件（`ghostty-build.txt`）必须移出（§6.2）；签名由内向外，不用 `--deep` |
| CI 作业 | `native-ghostty ×6` → `native-tcodepty ×4` → `build-test ×3` → `esctest` → `aot-smoke ×3` → `bench-pr`；每晚：`nightly-stress`、`bench-nightly`、`fuzz-long`、ARM 平台测试 |

---

## 1. 本地开发环境

### 1.1 必需工具

| 工具 | 版本 | macOS | Linux | Windows |
|---|---|---|---|---|
| .NET SDK | 10.0.300 及以上同一特性带（`global.json`）[已核实] | 官方安装包 / `dotnet-install.sh` | 同左 | 官方安装包 / `winget install Microsoft.DotNet.SDK.10` |
| NativeAOT 前置 | — | Xcode 命令行工具（`xcode-select --install`） | `clang`、`zlib1g-dev`（.NET NativeAOT 的链接需要）[推断：按 .NET 官方 AOT 先决条件] | VS 2022 Build Tools："使用 C++ 的桌面开发"工作负载（MSVC 链接器 + Windows SDK） |
| **Zig** | **0.16.0（精确）** | 见 §1.2 | 见 §1.2 | 见 §1.2 |
| Git | 任意现代版本 | 系统自带 | 包管理器 | Git for Windows（同时提供 Git Bash，用于 shell 集成测试） |
| Python 3 | ≥ 3.9 | 系统 / Homebrew | 包管理器 | `winget install Python.Python.3.12` | 
| PowerShell 7 | 可选（Windows 上运行 `.ps1`、测试 pwsh 集成） | `brew install powershell` | 包管理器 | `winget install Microsoft.PowerShell` |

- Python 只用于：运行 esctest2（11 §1.1.4）、`tools/gen_*.py` 等现有脚本。
- **gcc 不是构建原生库的必需项**：libghostty 由 Zig 构建；Linux 的 libtcodepty 与 helper 用 `zig cc`（可以精确指定 glibc 2.28 与 musl 静态）。macOS 的 libtcodepty 与 helper 按 03 §11.1 用 Apple clang（Xcode 命令行工具，NativeAOT 本来就需要）。
- **CMake 不需要**。上游提供的 CMake 包装内部也是调用 zig [已核实 research/01 §4.1.4]，对我们没有额外价值。

### 1.2 安装 Zig 0.16.0

Zig 0.16.0 于 2026-04-13 发布，0.17.0 于 2026-10-01 发布；Ghostty `b3dc3d7` 的 `build.zig.zon` 写明 `minimum_zig_version = "0.16.0"` [已核实]；RoyalTerminal 记录"需要恰好 0.16.0，升级到 0.17 需要协调的 ABI 和行为评审"[已核实 research/01 §4.1.4]。**包管理器给的通常是最新版**（本机 `brew` 安装的是 0.17.0 [已核实 `zig version`]），所以统一用官方 tarball：

| 平台 | 文件（`https://ziglang.org/download/0.16.0/` 下）[已核实 `https://ziglang.org/download/index.json`] |
|---|---|
| macOS arm64 | `zig-aarch64-macos-0.16.0.tar.xz` |
| macOS x64 | `zig-x86_64-macos-0.16.0.tar.xz` |
| Linux x64 | `zig-x86_64-linux-0.16.0.tar.xz` |
| Linux arm64 | `zig-aarch64-linux-0.16.0.tar.xz` |
| Windows x64 | `zig-x86_64-windows-0.16.0.zip` |
| Windows arm64 | `zig-aarch64-windows-0.16.0.zip` |

安装脚本 `tools/terminal/install-zig.sh` / `install-zig.ps1`：

1. 从 `native/toolchain.json`（见 §2）读取版本与各平台 SHA-256（值取自 `index.json` 的 `shasum` 字段，脚本入库时一并写入）；
2. 下载到 `~/.cache/tcode/zig/0.16.0/`（Windows：`%LOCALAPPDATA%\tcode\zig\0.16.0\`），校验 SHA-256，解压；
3. 不修改 PATH。构建脚本按以下顺序找 zig：环境变量 `TCODE_ZIG` → 上述缓存目录 → PATH 中的 `zig`（**必须** `zig version` 输出 `0.16.0`，否则报错并提示运行安装脚本）。

也可以用 `mise use zig@0.16.0` 或 `zigup 0.16.0`，只要 `zig version` 正确即可。

### 1.3 各平台的额外要求

| 平台 | 要求 |
|---|---|
| macOS | Xcode 命令行工具（`codesign`、`install_name_tool`、`otool`、`lipo`）。libghostty 的 macOS 目标需要 Apple SDK，**只能在 macOS 上构建**（上游注释："lib-vt requires macOS runner for macOS/iOS builds because it requires the `apple_sdk` path"）[已核实 Ghostty `.github/workflows/test.yml:825`] |
| Linux | `patchelf`（可选，规范化 soname）、`binutils`（`objdump`、`strip`，用于校验 glibc 符号版本与剥离符号） |
| Windows | VS 2022 Build Tools（MSVC v143 + Windows 11 SDK）；`x86_64-windows-msvc` 目标需要能找到 MSVC 头文件与库，交叉编译不可行（上游注释："doesn't work yet, we need a way to find msvc libc/c++ headers"）[已核实 `test.yml:617-619`]，所以 Windows 原生库在 Windows 上构建。MinGW **不需要**：`aarch64-windows-gnu` 目标使用 Zig 自带的 mingw-w64 |

---

## 2. 目录结构

```
native/
  toolchain.json                     Zig 版本与各平台 tarball 的 SHA-256；esctest2 的 pin
  ghostty/
    ghostty-upstream.json            上游 pin（§4.1）
    overlay/                         overlay 补丁（*.patch），按文件名顺序应用；每个补丁附 README 段落说明原因与上游 issue
      0001-win32-input-mode-9001.patch   06 §3.7：在模式表登记 9001（若采纳）
    build-flags.json                 各 RID 的 zig 参数（§4.3），脚本与 CI 共用，单一来源
    THIRD_PARTY_NOTICES.md           Ghostty 及其依赖（simdutf、Highway、Wuffs、uucode）的许可证
  tcodepty/                          布局以 03-pty.md §1 为准
    include/tcodepty.h
    src/tcpty_{spawn,io,exit,proc}.c   合计约 690 行
    helper/tcode-pty-spawn.c           PTY / --exec / --detach 三种模式
    build.sh  check-glibc.sh           03 §11.1；build-native.sh 调用它
    exports.txt                        导出符号白名单，CI 用 nm 校验
tools/terminal/
  install-zig.sh / install-zig.ps1
  build-native.sh / build-native.ps1 一条命令的本地构建（§3）
  verify-native.sh / verify-native.ps1  产物校验（导出符号、glibc 版本、CPU 指令、体积、签名）
  package-macos.sh                   扩展现有 tools/package_macos_app.sh：放置 ptyhost/、签名、公证（§6.2）
  sign-windows.ps1
  gen-bench-data/                    11 §3.3
  fixtures/                          11 §1.1.1 的素材转换器
  record/                            11 §1.5.1 的录制脚本
  EscTestRunner/                     11 §1.1.4（.NET 控制台项目）
artifacts/                           （.gitignore）
  native/<rid>/                      原生产物（MSBuild 从这里拾取）
  native/<rid>/.stamp                缓存键
  native-symbols/<rid>/              未剥离的调试符号（.dSYM / .debug / .pdb）
  src/ghostty-<sha>/                 上游检出（只读，构建在独立的 zig cache 中进行）
```

---

## 3. 一条命令的本地构建

### 3.1 用法

```bash
# 构建当前机器的 RID（osx-arm64 等），首次约 2–5 分钟，之后命中缓存秒级返回
tools/terminal/build-native.sh

# 指定 RID（只允许本平台可以构建的 RID：macOS 可以构建 osx-*、linux-*；Linux 可以构建 linux-*；Windows 构建 win-*）
tools/terminal/build-native.sh linux-arm64

# 强制重建；只构建某一部分
tools/terminal/build-native.sh --force --only ghostty
```

```powershell
tools\terminal\build-native.ps1 -Rid win-x64
```

之后 `dotnet build` / `dotnet test` / `dotnet publish -r <rid>` 自动拾取产物（§3.4）。没有产物时，引用原生库的项目在构建时给出明确错误："artifacts/native/<rid>/ 中没有 libghostty-vt，请先运行 tools/terminal/build-native.sh <rid>"（Directory.Build.targets 中的 `Error` 任务，仅对需要原生库的可执行项目生效）。

### 3.2 脚本步骤

```
1. 解析 RID（默认取本机：uname -s/-m → osx|linux × x64|arm64；Windows 由 ps1 处理）
2. 找到 zig 0.16.0（§1.2），否则失败
3. 计算缓存键 key = sha256(
       ghostty-upstream.json 的 commit
     ‖ overlay/*.patch 的内容（按文件名排序）
     ‖ build-flags.json 中该 RID 的参数
     ‖ zig 版本
     ‖ tcodepty/*.c|*.h 的内容
     ‖ 脚本自身的版本号）
   artifacts/native/<rid>/.stamp == key 且文件齐全 → 直接返回
4. libghostty-vt（§4）：
   a. 检出：artifacts/src/ghostty-<sha>/ 不存在时
      git init && git remote add origin <url> && git fetch --depth 1 origin <commit> && git checkout FETCH_HEAD
      （从 git 检出而不是下载 tarball：Ghostty 的构建用 git 计算版本号中的提交号 [已核实 src/build/Config.zig:283-339]）
   b. 校验 HEAD == pin 的 commit
   c. 应用 overlay：每个补丁先 `git apply --check`，再 `git apply`；任一补丁失败即报错（不静默跳过——与 StreamMark 的"已应用则跳过"不同，因为这里每次都从干净检出开始）
   d. zig build -Demit-lib-vt <build-flags.json 中该 RID 的参数> --prefix artifacts/obj/ghostty/<rid>
      ZIG_GLOBAL_CACHE_DIR 保持默认（~/.cache/zig），依赖包（simdutf、highway、wuffs、uucode）在这里缓存
   e. 规范化文件名（§4.4），剥离符号到 artifacts/native-symbols/<rid>/
5. libtcodepty + tcode-pty-spawn（仅 Unix RID，§5）
6. 写 artifacts/native/<rid>/ghostty-build.txt（04 §2.4：版本字符串 + 库文件 SHA-256 前 16 位）
7. verify-native（§4.6）
8. 写 .stamp
```

### 3.3 产物

```
artifacts/native/osx-arm64/
  libghostty-vt.dylib
  libtcodepty.dylib                  Debug / 测试
  libtcodepty.a                      ptyhost 的 AOT 发布（DirectPInvoke，03 §11.5）
  tcode-pty-spawn
  ghostty-build.txt
  .stamp
artifacts/native/win-x64/
  ghostty-vt.dll
  ghostty-build.txt
  .stamp
```

ConPTY 的 `conpty.dll` / `OpenConsole.exe` 不在这里：它们来自 NuGet 包（§5.3）。

### 3.4 MSBuild 如何拾取

**仓库内（项目引用）**：仿照 StreamMark 根目录的 `Directory.Build.targets`——"SDK 不会把 RuntimeIdentifier 传给被引用项目，所以在使用方自己的构建中按其目标 RID 复制原生库" [已核实 `/Users/token/code/StreamMark/Directory.Build.targets`]。TCode 新增根目录 `Directory.Build.targets`：

```xml
<Project>
  <!-- 终端原生库：只对声明了 TCodeNeedsTerminalNative=true 的可执行项目生效
       （TCode.Desktop、TCode.Terminal.Host、各终端测试项目、基准、EscTestRunner）。 -->
  <PropertyGroup Condition="'$(TCodeNeedsTerminalNative)' == 'true'">
    <_TermRid>$(RuntimeIdentifier)</_TermRid>
    <_TermRid Condition="'$(_TermRid)' == ''">$(NETCoreSdkPortableRuntimeIdentifier)</_TermRid>
    <_TermNativeDir>$(MSBuildThisFileDirectory)artifacts\native\$(_TermRid)\</_TermNativeDir>
  </PropertyGroup>
  <ItemGroup Condition="'$(TCodeNeedsTerminalNative)' == 'true' and Exists('$(_TermNativeDir)')">
    <None Include="$(_TermNativeDir)*" Exclude="$(_TermNativeDir).stamp;$(_TermNativeDir)*.a"
          Link="%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest"
          CopyToPublishDirectory="PreserveNewest" Visible="false" />
  </ItemGroup>
  <Target Name="TCodeCheckTerminalNative" BeforeTargets="Build"
          Condition="'$(TCodeNeedsTerminalNative)' == 'true' and !Exists('$(_TermNativeDir)ghostty-build.txt')">
    <Error Text="artifacts/native/$(_TermRid)/ 中没有终端原生库。请先运行 tools/terminal/build-native.sh $(_TermRid)（Windows：tools\terminal\build-native.ps1 -Rid $(_TermRid)）。" />
  </Target>
</Project>
```

要点：

- `Link="%(Filename)%(Extension)"` 把文件**平铺**到输出目录根，`LibraryImport("ghostty-vt")` 按默认探测路径（应用目录）即可找到。不使用 `runtimes/<rid>/native/` 子目录：那是 NuGet 包内的布局，发布时 SDK 才会平铺；项目内直接平铺更简单，Debug 与发布一致。
- **Desktop 的 Debug 构建不带 RID**：用 `NETCoreSdkPortableRuntimeIdentifier`（例如 `osx-arm64`）。
- 可执行位：MSBuild 的 `Copy` 在 Unix 上保留源文件权限 [推断：.NET SDK 的 Copy 任务使用 `File.Copy`，在 Unix 上保留 mode]；`verify-native` 与发布后的校验步骤再检查一次 `tcode-pty-spawn` 的 `+x`（研究 02 §4 U13）。
- `ptyhost/` 子目录：Desktop 的 `CopyPtyHost` / `PublishPtyHost` 目标（04 §1.1，仿照现有 `CopyAgentHost` / `PublishAgentHost` [已核实 `src/TCode.Desktop/TCode.Desktop.csproj:36-54`]）把 `TCode.Terminal.Host` 的输出复制 / 发布到 `ptyhost/`；Host 项目本身声明 `TCodeNeedsTerminalNative=true`，所以 `ptyhost/` 中自带 libghostty、helper，以及（仅 Debug / 测试构建）libtcodepty 动态库。Desktop 根目录再有一份 libghostty（UI 侧核心使用），两份来自同一文件，字节相同（04 §1.1）。

**将来拆成独立仓库（D2）时的 NuGet 布局**：`TCode.Terminal.Core.csproj` 中

```xml
<None Include="$(TermNativeRoot)*/libghostty-vt.*;$(TermNativeRoot)*/ghostty-vt.dll"
      Pack="true" PackagePath="runtimes/%(RecursiveDir)native/%(Filename)%(Extension)" />
```

与 StreamMark.RaTeX 相同 [已核实 `src/StreamMark.RaTeX/StreamMark.RaTeX.csproj:20-22`]。v1 不发布 NuGet 包，这一段只用于 `dotnet pack` 的冒烟检查。

### 3.5 Debug 用动态库，AOT 用静态还是动态

| 方案 | 做法 | 优点 | 缺点 |
|---|---|---|---|
| A 全部动态（**采用**） | Debug 与 AOT 都加载 `libghostty-vt.{dylib,so}` / `ghostty-vt.dll` | 一条路径、一份产物；与 StreamMark RaTeX、RoyalTerminal 一致；04 的 `ghosttyBuildId` 直接对文件做哈希；Windows 两个架构都没有 ABI 问题 | 多两个文件（每份约 2 MB，Desktop 根目录与 `ptyhost/` 各一份）；每个都要签名；加载一次 `dlopen` |
| B AOT 静态链接 | `<DirectPInvoke Include="ghostty-vt" />` + `<NativeLibrary Include="libghostty-vt-static.a" />`；Debug 仍用动态库 | 少文件；P/Invoke 直接调用（略快）；不需要单独签名 dylib | ①Debug 与发布走两条路径，问题只在发布时出现；②Windows：静态库必须与 MSVC 链接器兼容，`win-x64` 需 `x86_64-windows-msvc`（可以），`win-arm64` 只能 `aarch64-windows-gnu` 构建（§4.3），gnu 的 `.a` 交给 MSVC `link.exe` 有 compiler_rt / 符号名风险 [推断]；③静态库中打包了 compiler_rt 与 ubsan_rt（上游注释）[已核实 research/01 §4.1.4]，可能与 .NET 运行时的静态库冲突 [推断]；④Desktop 与 ptyhost 各嵌一份，`ghosttyBuildId` 改为编译期常量；⑤SIMD 构建需要 libc（上游注释："Setting simd to false will force a pure static build that doesn't even require libc"）[已核实 `example/c-vt-static/build.zig:21-25`]，链接时要补齐系统库 |
| C 混合 | libtcodepty 静态、libghostty 动态 | libtcodepty 很小、只依赖 libc，静态链接风险低；少一个需要签名的文件 | 03 §11.5 已对 ptyhost 的 Unix AOT 发布采用 |

**结论**：libghostty-vt 采用 A；libtcodepty 采用 C（03 §11.5，§5.2）。M6 之后若要减少 libghostty 的文件数，先做 research/04 §11 第 6 条的 spike（静态链接 + `DirectPInvoke` + 跑 S1/S4），只在 5 个非 win-arm64 的 RID 上启用 B。

---

## 4. libghostty-vt 构建

### 4.1 pin 与校验

`native/ghostty/ghostty-upstream.json`：

```jsonc
{
  "repository": "https://github.com/ghostty-org/ghostty.git",
  "commit": "b3dc3d794513c11633e7c3347935693957744e83",   // 2026-10-15，research 全部基于它
  "commitDate": "2026-10-15",
  "zig": "0.16.0",
  "versionString": "1.3.2-tcode",                        // 传给 -Dversion-string 的前缀；脚本追加 +<短提交号>.<overlay 哈希前 8 位>
  "overlaySha256": "…",                                  // overlay/*.patch 串接后的 SHA-256，CI 校验
  "expected": {                                          // 构建后的期望导出（防止上游 API 悄悄变化）
    "exportsFile": "native/ghostty/exports-b3dc3d7.txt",
    "minExports": 200
  }
}
```

校验：

- 检出后 `git rev-parse HEAD` 必须等于 `commit`（防止 `fetch` 拿到别的对象）；
- overlay 内容哈希必须等于 `overlaySha256`（防止有人改了补丁却没有更新 pin）；
- Zig 依赖包由 `build.zig.zon` 中的内容哈希保证完整性（Zig 包管理器按 `.hash` 校验下载内容）[已核实 `build.zig.zon` 中各依赖都带 `.hash`]；
- `-Demit-lib-vt` 只拉取 lib-vt 用到的惰性依赖 [推断：按 `b.lazyDependency` 的用法]；
- 构建后用 `nm` / `dumpbin /exports` 导出 `ghostty_*` 符号列表，与 `exports-b3dc3d7.txt` 比较：缺少符号即失败，多出符号只警告（上游新增 API）。
- `-Dversion-string=1.3.2-tcode+b3dc3d7.<overlay8>`：使 `ghostty_build_info(VERSION_STRING / VERSION_BUILD)` 带上我们的构建标识 [推断：需确认 lib-vt 的 build_info 读取的是 `version` 还是 `lib_version`（`src/build/Config.zig:276-350` 中两者分开定义）；若是后者，改传 `-Dlib-version-string`]。

### 4.2 overlay 补丁

做法沿用 RoyalTerminal 的思路（构建时对干净检出打补丁，不改 submodule，每个补丁校验原文件）[已核实 research/01 §4.1.7]，但用标准 `git apply`：

- 每个补丁只做一件事，文件名带序号；补丁头部注释写明：原因、对应的上游 issue / PR、上游合并后删除的条件；
- 当前已知候选：`0001-win32-input-mode-9001.patch`（06 §3.7、§末待决：libghostty 模式表中没有 9001）；
- 研究 01 §4.1.7 列出的 RoyalTerminal 修正（脏标记等）在 `b3dc3d7` 上是否仍存在**未核实**；M1 的差分测试（11 §1.1.2）发现问题时，优先向上游提交，必要时临时加补丁。

### 4.3 6 个 RID 的命令行

公共参数：`zig build -Demit-lib-vt -Doptimize=ReleaseFast -Dstrip=true -Dversion-string=<见 4.1>`。

| RID | runner | 额外参数 | 说明 |
|---|---|---|---|
| `osx-arm64` | macOS arm64 | `-Dtarget=aarch64-macos` | 需要 Apple SDK，只能在 macOS 上构建 [已核实 上游注释]。最低系统版本由上游定为 macOS 13.0 [已核实 `src/build/Config.zig:789-797`] |
| `osx-x64` | macOS（arm64 或 Intel 均可） | `-Dtarget=x86_64-macos` | 在 arm64 runner 上交叉编译即可（RoyalTerminal 的做法）[已核实 RoyalTerminal `release.yml:69-71`]；测试仍需在 Intel runner（`macos-15-intel`）上运行 |
| `linux-x64` | Ubuntu x64 | `-Dtarget=x86_64-linux-gnu.2.28` | glibc 2.28 = RHEL 8（01 §1）。显式 target 使 CPU 模型为基线 x86-64，而不是 runner 的本机 CPU |
| `linux-arm64` | Ubuntu arm64 | `-Dtarget=aarch64-linux-gnu.2.28` | 也可以在 x64 上交叉编译；测试需要 arm64 runner |
| `win-x64` | Windows x64 | `-Dtarget=x86_64-windows-msvc -Dcpu=x86_64-vzeroupper -Dsimd=false`（**初始**） | 见下文"Windows x64 的 CPU 基线" |
| `win-arm64` | Windows（x64 或 arm64） | `-Dtarget=aarch64-windows-gnu -Dsimd=false` | RoyalTerminal 的注释："Zig 0.16.0's Windows ARM64 stdlib stack-trace code fails to compile when Ghostty's vendored SIMD archive is enabled" [已核实 RoyalTerminal `release.yml:262-268`]。aarch64 的 msvc 目标同样需要 MSVC 头文件，未验证，所以用 gnu |

这些参数写在 `native/ghostty/build-flags.json` 中，脚本与 CI 都从这里读，**不在 YAML 中重复**。

**Windows x64 的 CPU 基线 / SIMD 问题**：

- 事实：RoyalTerminal 发现默认构建可能在启动阶段就执行 AVX 指令，在不支持 AVX 的虚拟机或模拟环境中直接崩溃；发布版改用 `-Dcpu=x86_64-vzeroupper -Dsimd=false`，并在 CI 中反汇编确认没有 AVX [已核实 research/01 §4.1.4；RoyalTerminal `release.yml:269-278` 的注释："x86_64-windows-msvc alone still allows LLVM/third-party SIMD code to leave VEX/AVX instructions in startup paths"]。RoyalTerminal 在 **Linux** 上也加了 `-Dsimd=false`，注释写的原因是"runner-native Ghostty build can SIGILL … on another machine with the same RID" [已核实 `release.yml:166-176`]——那是不带 `-Dtarget` 的本机构建引起的。
- 代价：`-Dsimd=false` 关闭 simdutf / Highway 路径，ASCII 解析会明显变慢（Ghostty devlog：SIMD ASCII 处理比标量快 7.32×）[已核实 research/04 §2.9]，与 01 §1 的"≥ 500 MB/s"目标冲突。
- TCode 的做法：
  1. **Linux / macOS**：显式 `-Dtarget` + **保留 SIMD**。Highway 与 simdutf 都以运行时 CPU 检测分派 [推断：需确认 Ghostty 的 Highway 构建是否启用动态分派]，基线 target 保证非 SIMD 代码不含高级指令。验证见 §4.6 的"CPU 兼容性"。
  2. **win-x64**：发布物初始使用标量构建（与 RoyalTerminal 一致，风险已知且可接受）；CI 并行构建一份 `-Dsimd=true` 的版本，跑同样的 CPU 兼容性验证与 S1 基准。验证连续通过后，把发布物切换为 SIMD 构建（只改 `build-flags.json`）。
  3. **win-arm64**：标量，直到 Zig 升级解决编译问题。

### 4.4 文件名规范化

| 平台 | 上游产物 | 规范化后 | 处理 |
|---|---|---|---|
| macOS | `libghostty-vt.<major>.<minor>.<patch>.dylib`（及符号链接）[已核实 `src/build/GhosttyLibVt.zig:395-402`] | `libghostty-vt.dylib` | `cp -L`；`install_name_tool -id @rpath/libghostty-vt.dylib`（改了 Mach-O 头之后签名失效，脚本随即 `codesign -s - -f` 做临时签名，正式签名在打包时进行）；`dsymutil` 生成的 `.dSYM` 移到 `native-symbols/` |
| Linux | `libghostty-vt.so.<ver>`（及 `.so.0`、`.so` 符号链接）[已核实 上游 CI 检查 `zig-out/lib/libghostty-vt.so.0.1.0`] | `libghostty-vt.so` | `cp -L`；可选 `patchelf --set-soname libghostty-vt.so`（.NET 按完整路径 `dlopen`，soname 不影响加载，只影响以后有人链接它）；`objcopy --only-keep-debug` 后 `strip --strip-unneeded` |
| Windows | `ghostty-vt.dll`（`zig-out/bin/`）[已核实 上游 CI 从缓存中查找 `ghostty-vt.dll`] | 不变 | `.pdb`（若生成）移到 `native-symbols/` |

`LibraryImport("ghostty-vt")` 在三个平台分别探测 `libghostty-vt.dylib`、`libghostty-vt.so`、`ghostty-vt.dll`（.NET 的默认名称变换）。

### 4.5 体积目标

| RID | 目标（剥离后） | 依据 |
|---|---|---|
| osx-arm64 / osx-x64 | ≤ 2.5 MB | RoyalTerminal 0.6.3：1.94 / 2.07 MB（含其扩展）[已核实 research/01 §4.1.4] |
| win-x64 / win-arm64 | ≤ 2.5 MB | RoyalTerminal：1.72 / 1.74 MB（标量）[已核实 同上] |
| linux-x64 / linux-arm64 | ≤ 2.5 MB | RoyalTerminal 的 Linux 包约 9.7 MB，`file` 显示"with debug_info, not stripped"[已核实 同上]——说明 Linux 必须显式剥离；剥离后应与其他平台相当 [推断] |

`verify-native` 中超过目标 20% 即失败，提示检查是否漏剥离或上游新增了大依赖。

### 4.6 产物校验（`verify-native`）

| 检查 | 方法 | 失败条件 |
|---|---|---|
| 导出符号 | `nm -gU`（macOS）/ `nm -D --defined-only`（Linux）/ `dumpbin /exports`（Windows） | 缺少 `exports-<sha>.txt` 中的符号 |
| 不依赖 libc++ | `otool -L` / `ldd` / `dumpbin /dependents` | 出现 `libc++`、`libc++abi`、`libstdc++`（上游 CI 有同样断言）[已核实 `test.yml:464-469`] |
| glibc 基线 | `objdump -T libghostty-vt.so \| grep -o 'GLIBC_[0-9.]*' \| sort -V \| tail -1` | > `GLIBC_2.28` |
| macOS 最低版本 | `otool -l` 中 `LC_BUILD_VERSION` 的 `minos` | > 应用的 `LSMinimumSystemVersion`（15.0） |
| 体积 | `stat` | §4.5 |
| 已剥离 | `file`（Linux 不含 "not stripped"） | — |
| CPU 兼容性（x64） | ①反汇编扫描：`objdump -d` 中出现 `ymm`/`zmm` 寄存器的函数列表，与 SIMD 分派函数白名单比较（非白名单函数中出现即失败）；②运行时：在 **Intel SDE**（`sde64 -wsm --`，模拟没有 AVX 的 Westmere）下运行 `TCode.Terminal.Core.Tests` 的一个子集（加载库 + 回放 10 个夹具） | ①白名单外出现 AVX；②SIGILL / 非法指令异常 [推断：SDE 可在 Linux 与 Windows runner 上运行] |
| 构建标识 | 加载库调用 `ghostty_build_info(VERSION_STRING)`，应包含 `tcode+<短提交号>`；`SIMD` 字段与 `build-flags.json` 一致 | 不一致 |
| 签名（打包后） | `codesign --verify --strict` / `signtool verify /pa` | 失败 |

---

## 5. libtcodepty、tcode-pty-spawn 与 ConPTY

> 本节以 [03-pty.md](03-pty.md) §1、§11 为准（源文件布局、编译命令、签名、发布布局、ConPTY 引用、静态链接决定）。这里只记录它们在一条命令构建与 CI 中的落点，以及与 libghostty 的差异。

### 5.1 libtcodepty 与 helper（4 个 Unix RID）

源码布局（03 §1）：`native/tcodepty/include/tcodepty.h`、`src/tcpty_{spawn,io,exit,proc}.c`（合计约 690 行，01 估计的 300 行偏低）、`helper/tcode-pty-spawn.c`（PTY / `--exec` / `--detach` 三种模式，约 85 行）、`build.sh`、`check-glibc.sh`。`tools/terminal/build-native.sh --only tcodepty` 只是调用 `native/tcodepty/build.sh <rid>` 并把产物放到 `artifacts/native/<rid>/`。

| 产物 | linux-x64 / linux-arm64 | osx-arm64 / osx-x64 |
|---|---|---|
| 编译器 | `zig cc -target <arch>-linux-gnu.2.28`（与 libghostty 共用同一个 Zig 0.16.0，任何 Linux 主机都能交叉编译两个架构） | Apple clang（`-arch arm64` / `-arch x86_64`，arm64 runner 上一次产出两个架构） |
| `libtcodepty.so` / `.dylib` | 动态库，glibc 基线 2.28；`close_range`、`pidfd_open` 只经 `syscall()` 调用（03 §11.1） | `-install_name @rpath/libtcodepty.dylib` |
| `libtcodepty.a` | `zig ar rcs`，供 AOT 静态链接 | `libtool -static` |
| `tcode-pty-spawn` | **`zig cc -target <arch>-linux-musl -static`：完全静态**，不依赖目标机的 glibc（03 §3、§11.1） | clang，动态链接 libSystem（macOS 不支持完全静态） |
| 最低系统 | glibc 2.28（`check-glibc.sh` 校验） | `-mmacosx-version-min` = **TCode 的 macOS 下限**（§6.2：.NET 10 的下限 15.0；03 §11.1 写的 11.0 应随之提高） |

- 编译选项：`-O2 -fPIC -fvisibility=hidden -D_GNU_SOURCE -Wall -Wextra -Werror`；Linux CI 另跑一遍 `-fsanitize=address,undefined` 的测试构建（03 §11.1）。
- 本文 §1.1 原先写"Unix 上全部用 `zig cc`、不需要 clang"：macOS 改为按 03 使用 Apple clang（Xcode 命令行工具本来就是 NativeAOT 的前置条件，不增加依赖）。
- `verify-native --only tcodepty`：导出符号白名单、`check-glibc.sh`、helper 的可执行位、Linux helper 为静态（`file` 输出含 "statically linked"）。

**helper 的另一个用途**：03 §12 实测 `trap - PIPE` 对 bash 无效，BashTool（进程模式）的 SIGPIPE 修复改为经 `tcode-pty-spawn --exec` 启动 shell。所以 helper 也要能被 AgentHost 找到：Desktop 启动 AgentHost 时注入 `TCODE_PTY_HELPER=<ptyhost/tcode-pty-spawn 的绝对路径>`，后备路径 `<AgentHost 目录>/../ptyhost/tcode-pty-spawn`。打包时不需要再复制一份。

### 5.2 AOT 下 libtcodepty 静态链接，libghostty 动态链接

03 §11.5 的决定：**tcode-ptyhost 的 Release AOT 发布在 Unix 上用 `<DirectPInvoke Include="libtcodepty" />` + `<NativeLibrary Include=".../libtcodepty.a" />` 静态链接；Debug、JIT 测试使用动态库**。这与 §3.5 对 libghostty 的"全部动态"不冲突（§3.5 的方案 C）：libtcodepty 小、只依赖 libc、没有 compiler_rt，静态链接风险低；libghostty 的风险点（Windows ABI、compiler_rt、`ghosttyBuildId` 按文件哈希）都不适用于它。所以 Unix 发布目录的 `ptyhost/` 中**没有** `libtcodepty.*`。

`artifacts/native/<rid>/` 中同时有 `.so`/`.dylib` 与 `.a`；§3.4 的 `Directory.Build.targets` 排除 `*.a`（与 StreamMark 相同 [已核实 StreamMark `Directory.Build.targets`]），`.a` 只由 Host 项目的 `NativeLibrary` 项引用。

### 5.3 Linux AOT 二进制的 glibc 基线

03 §11.5 指出：NativeAOT 可执行文件链接的是**发布机器**的 glibc。`ubuntu-latest`（24.04，glibc 2.39）上发布的 `TCode`、`TCode.AgentHost`、`tcode-ptyhost` 在 RHEL 8（glibc 2.28）上无法启动，与 01 §1 的平台要求冲突。原生库用 `zig cc -target …gnu.2.28` 解决了自己的部分，但 AOT 主程序必须另行处理：

| 方案 | 做法 | 评价 |
|---|---|---|
| A（**采用**） | Linux 的发布作业在 `almalinux:8` 容器（glibc 2.28）中运行：容器内安装 .NET SDK（`dotnet-install.sh`）与 `clang`、`zlib-devel`，执行 `dotnet publish -r linux-x64` | 简单直接；arm64 用 `ubuntu-24.04-arm` runner + `almalinux:8` 的 arm64 镜像 |
| B | .NET 官方的交叉构建方式：`mcr.microsoft.com/dotnet-buildtools/prereqs` 的 crossdeps 镜像 + rootfs sysroot，`-p:SysRoot=…` | 更接近 dotnet/runtime 自己的做法，但配置复杂 |
| C | 只在 Ubuntu 上发布，声明最低 glibc 为 runner 的版本 | 违背 01 §1，不采用 |

Avalonia / SkiaSharp / HarfBuzz 等第三方原生库自带的 glibc 要求也要一并检查（`objdump -T`）[推断：SkiaSharp 的 Linux 原生库通常以较老的 glibc 构建，需实测]。CI：`aot-smoke` 的 linux-x64 行改为容器内发布（§7.3），另在 `almalinux:8` 容器中运行 `--self-test`（03 §14 的 test-rhel8）。

### 5.4 Windows：不需要我们自己的原生代码

Windows 侧全部是固定参数的 Win32 API，C# `LibraryImport` 即可 [已核实 research/02 §5 第 4 条]。

### 5.5 ConPTY NuGet 的引用与发布布局

采用 03 §11.4：

- `Microsoft.Windows.Console.ConPTY` **1.25.260930003**（2026-10-02，MIT）[已核实 research/02 §3.4]，**只在 `TCode.Terminal.Host` 中直接引用**（包内 `build/` targets 只对直接引用生效，经 ProjectReference 传递会丢失 OpenConsole，而 conpty.dll 找不到 OpenConsole 时会静默回退到 System32 的 conhost [已核实 03 §11.4 引 Porta.Pty.targets]）；`TCode.Terminal.Pty` 不引用它。
- 设置 `ConptyRequiresx64Host=true`、`ConptyRequiresARM64Host=true`（属性名取自 Porta.Pty 的用法），得到 03 §11.3 的布局：`ptyhost\conpty.dll`、`ptyhost\x64\OpenConsole.exe`、`ptyhost\arm64\OpenConsole.exe`（x64 包两份都带，arm64 包只带 arm64）。conpty.dll 的查找顺序是"同目录 → `<本机架构>\OpenConsole.exe` → 静默回退 conhost"[已核实 research/02 §3.4]，所以 x64 构建运行在 arm64 Windows 上时会用 arm64 的 OpenConsole。
- 这些属性在 NativeAOT 发布中是否生效是 03 的 spike S8；不生效时用 `GeneratePathProperty="true"` + 显式 `<None Include="$(PkgMicrosoft_Windows_Console_ConPTY)\build\native\runtimes\x64\OpenConsole.exe" Link="x64\OpenConsole.exe" CopyToPublishDirectory="PreserveNewest" />` 兜底。
- **发布后校验**（`check-layout`）：上述三个文件都在；`tcode-ptyhost --self-test` 报告 `ConPty=Bundled` 及加载路径，不是随包的就失败。
- `conpty.dll` 与 `OpenConsole.exe` 来自同一个包，天然成对升级（§8.2）；二者都是微软签名，TCode 不重新签名。

---

## 6. pty host 的发布与打包

### 6.1 发布布局（采用 04 §1.1）

```
<发布目录>/
  TCode[.exe]                          Desktop（AOT）
  libghostty-vt.* / ghostty-vt.dll     UI 侧核心
  ghostty-build.txt
  （Avalonia / Skia / HarfBuzz / StreamMark RaTeX 等现有原生库）
  agenthost/
    TCode.AgentHost[.exe] …            现有
  ptyhost/
    tcode-ptyhost[.exe]
    libghostty-vt.* / ghostty-vt.dll   与根目录的字节相同
    ghostty-build.txt
    （libtcodepty 在 Unix AOT 发布中静态链接进 tcode-ptyhost，不单独存在；Debug 构建中有 .so/.dylib）
    tcode-pty-spawn                    Unix，0755
    conpty.dll                         Windows
    x64/OpenConsole.exe  arm64/OpenConsole.exe   Windows（x64 包两份，arm64 包只有 arm64；03 §11.3）
```

`dotnet publish src/TCode.Desktop -c Release -r <rid>` 一次产出全部内容（`PublishAgentHost` 与 `PublishPtyHost` 两个 AfterTargets）。

### 6.2 macOS `.app`

现有 `tools/package_macos_app.sh` 把整个发布目录复制到 `Contents/MacOS/`，`LSMinimumSystemVersion` 为 11.0，没有签名步骤 [已核实]。终端上线后改为：

**位置**：

| 文件 | 位置 | 说明 |
|---|---|---|
| `TCode`、现有 dylib、`libghostty-vt.dylib` | `Contents/MacOS/` | 与现在一致 |
| `agenthost/`、`ptyhost/` 中的 Mach-O 文件（可执行文件、dylib、helper） | `Contents/MacOS/agenthost/`、`Contents/MacOS/ptyhost/` | 04 §1.1 的选择。Apple 推荐 helper 放 `Contents/Helpers/` 或 `Contents/MacOS/` [推断：TN2206 的推荐]；放在 `Contents/MacOS` 的子目录中、且子目录只含 Mach-O 时，`codesign` 可以逐个签名 [推断：需在 M2 用公证实测] |
| `ghostty-build.txt`（两处） | **移到 `Contents/Resources/ghostty-build.txt`** | `Contents/MacOS` 下不能有非代码文件，否则签名 / 公证失败或被当作需要单独签名的代码 [推断：Apple 的 "Placing content in a bundle" 规则]。host 与 Desktop 读取时按"可执行文件目录 → `../Resources`（macOS 包内）"的顺序查找。**需要 04 的作者确认**这一改动（见待决问题） |
| AOT 产生的 `.dSYM`、`.pdb`、`.json` | 不进入包 | 发布后脚本删除；符号另行归档 |

`LSMinimumSystemVersion` 改为 **15.0**：这是 .NET 10 官方支持的最低 macOS 版本（03 §11.1 引 dotnet/core `supported-os.md`），比 libghostty-vt 的 13.0 [已核实 `Config.zig:789-797`] 更高，是实际的下限。旧值 11.0 在 macOS 11–14 上不受支持。libtcodepty / helper 的 `-mmacosx-version-min` 同步为 15.0；libghostty 保持上游的 13.0 即可（低于应用下限无害）。

**签名顺序**（由内向外，**不用 `--deep`**；全部使用 Developer ID Application 证书、`--options runtime`（hardened runtime）、`--timestamp`）：

```bash
ID="Developer ID Application: … (TEAMID)"
S="codesign --force --timestamp --options runtime --sign $ID"
# 1. 所有 dylib（最内层）
find TCode.app/Contents/MacOS -name '*.dylib' -exec $S {} \;
# 2. helper 与辅助可执行文件（它们 exec 用户 shell，自身不需要额外 entitlements）
$S TCode.app/Contents/MacOS/ptyhost/tcode-pty-spawn
$S --entitlements tools/terminal/entitlements/ptyhost.plist  TCode.app/Contents/MacOS/ptyhost/tcode-ptyhost
$S --entitlements tools/terminal/entitlements/agenthost.plist TCode.app/Contents/MacOS/agenthost/TCode.AgentHost
# 3. 主可执行文件
$S --entitlements tools/terminal/entitlements/app.plist TCode.app/Contents/MacOS/TCode
# 4. 整个包
$S --entitlements tools/terminal/entitlements/app.plist TCode.app
codesign --verify --strict --deep --verbose=2 TCode.app      # 验证可以用 --deep
# 5. 公证
ditto -c -k --keepParent TCode.app TCode.zip
xcrun notarytool submit TCode.zip --keychain-profile tcode-notary --wait
xcrun stapler staple TCode.app
spctl --assess --type execute --verbose TCode.app
```

Entitlements：

- NativeAOT 不生成 JIT 代码，**不需要** `com.apple.security.cs.allow-jit` / `allow-unsigned-executable-memory` [推断：需用 hardened runtime 实测 Avalonia + Skia 的 AOT 二进制]；
- 所有 dylib 同一 Team ID 签名，**不需要** `disable-library-validation`；
- 终端会运行用户的任意程序，这些程序访问"文稿"、"桌面"等受保护目录时，系统以 TCode 为"负责进程"弹出授权提示——`Info.plist` 需要 `NSDesktopFolderUsageDescription`、`NSDocumentsFolderUsageDescription`、`NSDownloadsFolderUsageDescription`、`NSRemovableVolumesUsageDescription` 等说明文字。host 脱离 Desktop 后的归属问题见 04 §18 第 7 条 [推断]；
- 不启用 App Sandbox（终端不可能在沙箱中工作）。

### 6.3 Windows 签名

- `signtool sign /fd sha256 /tr <RFC3161 时间戳> /td sha256 /a`：`TCode.exe`、`agenthost\TCode.AgentHost.exe`、`ptyhost\tcode-ptyhost.exe`、`ghostty-vt.dll`（两份）、其他我们构建的 DLL；
- 不重新签名 `conpty.dll`、`OpenConsole.exe`（微软签名）以及已有签名的第三方 DLL（`signtool verify /pa` 通过的跳过）；
- 脚本：`tools/terminal/sign-windows.ps1`，证书来自 CI 机密（Azure Trusted Signing 或 PFX）；没有证书时（PR 构建）跳过签名但仍执行 `verify` 之外的检查。

### 6.4 Linux

- 发布物为 tar.gz（以后再考虑 AppImage / deb / rpm）。打包时 `chmod 0755`：`TCode`、`agenthost/TCode.AgentHost`、`ptyhost/tcode-ptyhost`、`ptyhost/tcode-pty-spawn`；**tar 保留权限**（`tar --mode` 不覆盖）；zip 不用于 Linux（会丢权限）。
- 运行时自检：host 启动时检查 helper 可执行，否则报出明确错误（研究 02 §4 U13；RoyalTerminal 在运行时补设 `UserExecute` [已核实 research/02]——TCode 选择报错而不是静默修复，因为包被改动本身就是问题）。

---

## 7. CI 工作流

### 7.1 作业总览

参照 StreamMark：`native-*` 按 RID 矩阵在原生 runner 上构建并上传工件 → `build-test` 与 `aot-smoke` 下载对应 RID 的工件 [已核实 `/Users/token/code/StreamMark/.github/workflows/ci.yml`]。

```
                ┌─────────────────────────┐
push / PR ──▶   │ native-ghostty ×6       │──┐
                │ native-tcodepty ×4      │──┤ artifacts: native-<rid>
                └─────────────────────────┘  │
                         ┌───────────────────┼──────────────────────────┐
                         ▼                   ▼                          ▼
                 build-test ×3        aot-smoke ×3                esctest (linux-x64)
                 (L1–L5, Agent E2E)   (Desktop + ptyhost AOT,     (报告 + 新增失败阻断)
                                       0 IL 警告, --smoke,
                                       --self-test)
                         │
                         ▼
                 bench-pr (linux-x64，base vs head)

nightly (cron) ──▶ native ×6 ──▶ test-arm (linux-arm64, win-arm64, osx-x64)
                                 nightly-stress (PTY 压力, fuzz-long 1h)
                                 bench-nightly (S1–S15, JIT+AOT)
```

| 作业 | 矩阵 | 触发 | 时长目标 |
|---|---|---|---|
| `native-ghostty` | 6 RID（见 §7.3 的 runner） | 每次 push / PR（缓存命中时 < 1 min） | 冷构建 < 10 min |
| `native-tcodepty` | linux（构建 2 个 linux RID）、macOS（构建 2 个 osx RID） | 同上 | < 2 min |
| `build-test` | linux-x64、osx-arm64、win-x64 | 每次 | < 20 min |
| `esctest` | linux-x64 | 每次 | < 5 min |
| `aot-smoke` | linux-x64、osx-arm64、win-x64 | 每次 | < 15 min |
| `bench-pr` | linux-x64 | PR（可通过 label `skip-bench` 跳过） | < 15 min |
| `test-arm` | linux-arm64、win-arm64、osx-x64 | 每晚 | — |
| `nightly-stress` | linux-x64、osx-arm64、win-x64 | 每晚 | < 90 min |
| `bench-nightly` | linux-x64、osx-arm64 | 每晚 | < 120 min |

### 7.2 缓存策略

| 缓存 | 键 | 路径 |
|---|---|---|
| Zig 工具链 | `zig-0.16.0-${{ runner.os }}-${{ runner.arch }}` | `~/.cache/tcode/zig`（Windows：`%LOCALAPPDATA%\tcode\zig`） |
| Zig 包缓存（Ghostty 依赖） | `zigpkg-${{ hashFiles('native/ghostty/ghostty-upstream.json') }}` | `~/.cache/zig/p`（Windows：`%LOCALAPPDATA%\zig\p`）。**不缓存** Zig 的本地编译缓存 `.zig-cache`（RoyalTerminal 的注释：恢复的 Windows 缓存引用了不存在的 `build.exe`，因此关闭了缓存）[已核实 RoyalTerminal `release.yml:251-254`] |
| 原生产物 | `native-<rid>-<缓存键>`（与 `.stamp` 相同，由 `build-native.sh --print-key` 输出） | `artifacts/native/<rid>`。命中时跳过整个构建 |
| NuGet | `nuget-${{ runner.os }}-${{ hashFiles('Directory.Packages.props', '**/*.csproj') }}` | `~/.nuget/packages` |
| 基准数据集 | `benchdata-${{ hashFiles('tools/terminal/gen-bench-data/sources.json') }}` | `artifacts/bench-data` |
| esctest2 | `esctest-${{ hashFiles('native/toolchain.json') }}` | `artifacts/src/esctest2` |

现有依赖的注意事项：`nuget.config` 中 StreamMark 通过**本地 NuGet 源**引用 [已核实 `Directory.Packages.props` 的注释"经本地 NuGet 源引用，见 nuget.config"]。CI 上没有这个本地源——**在 TCode 的 CI 能运行之前，StreamMark 的包必须发布到可访问的源**（GitHub Packages 或 nuget.org），或者在 CI 中先检出 StreamMark 并 `dotnet pack` 到本地目录。列入待决问题。

### 7.3 工作流 YAML 骨架

`.github/workflows/ci.yml`：

```yaml
name: CI

on:
  push:
    branches: [main]
  pull_request:
  workflow_dispatch:

env:
  DOTNET_NOLOGO: true
  DOTNET_CLI_TELEMETRY_OPTOUT: true
  TCODE_ZIG_VERSION: 0.16.0

concurrency:
  group: ci-${{ github.ref }}
  cancel-in-progress: ${{ github.event_name == 'pull_request' }}

jobs:
  # libghostty-vt：6 个 RID。参数来自 native/ghostty/build-flags.json，YAML 只给出 RID 与 runner。
  native-ghostty:
    strategy:
      fail-fast: false
      matrix:
        include:
          - { rid: osx-arm64,   os: macos-latest }
          - { rid: osx-x64,     os: macos-latest }        # 在 arm64 上交叉编译
          - { rid: linux-x64,   os: ubuntu-latest }
          - { rid: linux-arm64, os: ubuntu-24.04-arm }
          - { rid: win-x64,     os: windows-latest }
          - { rid: win-arm64,   os: windows-latest }      # aarch64-windows-gnu 交叉编译
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v4
      - name: Cache key
        id: key
        shell: bash
        run: echo "key=$(tools/terminal/build-native.sh --print-key ${{ matrix.rid }} --only ghostty)" >> "$GITHUB_OUTPUT"
      - uses: actions/cache@v4
        id: cache
        with:
          path: artifacts/native/${{ matrix.rid }}
          key: native-ghostty-${{ matrix.rid }}-${{ steps.key.outputs.key }}
      - uses: actions/cache@v4
        if: steps.cache.outputs.cache-hit != 'true'
        with:
          path: |
            ~/.cache/tcode/zig
            ~/.cache/zig/p
            ~/AppData/Local/tcode/zig
            ~/AppData/Local/zig/p
          key: zig-${{ env.TCODE_ZIG_VERSION }}-${{ runner.os }}-${{ runner.arch }}-${{ hashFiles('native/ghostty/ghostty-upstream.json') }}
      - name: Install Zig
        if: steps.cache.outputs.cache-hit != 'true'
        shell: bash
        run: tools/terminal/install-zig.sh          # Windows 上由 Git Bash 执行；脚本内部区分平台
      - name: Build (Unix)
        if: steps.cache.outputs.cache-hit != 'true' && runner.os != 'Windows'
        run: tools/terminal/build-native.sh ${{ matrix.rid }} --only ghostty
      - name: Build (Windows)
        if: steps.cache.outputs.cache-hit != 'true' && runner.os == 'Windows'
        shell: pwsh
        run: tools/terminal/build-native.ps1 -Rid ${{ matrix.rid }} -Only ghostty
      - name: Verify
        shell: bash
        run: tools/terminal/verify-native.sh ${{ matrix.rid }} --only ghostty
      - uses: actions/upload-artifact@v4
        with:
          name: native-ghostty-${{ matrix.rid }}
          path: artifacts/native/${{ matrix.rid }}/
          if-no-files-found: error
      - uses: actions/upload-artifact@v4
        with:
          name: symbols-ghostty-${{ matrix.rid }}
          path: artifacts/native-symbols/${{ matrix.rid }}/
          if-no-files-found: ignore

  # x64 的 CPU 兼容性：在 Intel SDE（无 AVX 的 Westmere）下加载库并回放夹具
  native-cpu-compat:
    needs: native-ghostty
    strategy:
      fail-fast: false
      matrix:
        include:
          - { rid: linux-x64, os: ubuntu-latest }
          - { rid: win-x64,   os: windows-latest }
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - uses: actions/download-artifact@v4
        with: { name: native-ghostty-${{ matrix.rid }}, path: artifacts/native/${{ matrix.rid }} }
      - name: Run under SDE -wsm
        shell: bash
        run: tools/terminal/cpu-compat.sh ${{ matrix.rid }}   # 下载 SDE（pin 版本 + SHA-256），运行 Core.Tests 的 CpuCompat 子集

  # libtcodepty + tcode-pty-spawn：4 个 Unix RID，两个 runner 各构建两个
  native-tcodepty:
    strategy:
      fail-fast: false
      matrix:
        include:
          - { rids: "linux-x64 linux-arm64", os: ubuntu-latest, name: linux }
          - { rids: "osx-arm64 osx-x64",     os: macos-latest,  name: osx }
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v4
      - uses: actions/cache@v4
        with:
          path: ~/.cache/tcode/zig
          key: zig-${{ env.TCODE_ZIG_VERSION }}-${{ runner.os }}-${{ runner.arch }}
      - run: tools/terminal/install-zig.sh
      - name: Build
        run: for rid in ${{ matrix.rids }}; do tools/terminal/build-native.sh "$rid" --only tcodepty; tools/terminal/verify-native.sh "$rid" --only tcodepty; done
      - uses: actions/upload-artifact@v4
        with:
          name: native-tcodepty-${{ matrix.name }}
          path: artifacts/native/
          if-no-files-found: error

  build-test:
    needs: [native-ghostty, native-tcodepty]
    strategy:
      fail-fast: false
      matrix:
        include:
          - { rid: linux-x64, os: ubuntu-latest,  pty: linux }
          - { rid: osx-arm64, os: macos-latest,   pty: osx }
          - { rid: win-x64,   os: windows-latest, pty: "" }
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - uses: actions/cache@v4
        with:
          path: ~/.nuget/packages
          key: nuget-${{ runner.os }}-${{ hashFiles('Directory.Packages.props', '**/*.csproj') }}
      - uses: actions/download-artifact@v4
        with: { name: native-ghostty-${{ matrix.rid }}, path: artifacts/native/${{ matrix.rid }} }
      - uses: actions/download-artifact@v4
        if: matrix.pty != ''
        with: { name: native-tcodepty-${{ matrix.pty }}, path: artifacts/native }
      - name: Restore executable bits                    # upload-artifact 不保留权限
        if: runner.os != 'Windows'
        run: chmod +x artifacts/native/*/tcode-pty-spawn
      - name: Shells for integration tests
        if: runner.os == 'Linux'
        run: sudo apt-get install -y zsh fish
      - run: dotnet build TCode.slnx -c Release
      - name: Test
        run: dotnet test TCode.slnx -c Release --no-build -- --filter-not-trait "Category=FuzzLong" --filter-not-trait "Category=Stress"
        env:
          TCODE_SNAPSHOT_DIR: ${{ github.workspace }}/artifacts/snapshots
      - uses: actions/upload-artifact@v4
        if: always()
        with:
          name: test-results-${{ matrix.rid }}
          path: |
            artifacts/snapshots/
            artifacts/test-results/
          if-no-files-found: ignore

  esctest:
    needs: [native-ghostty, native-tcodepty]
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - uses: actions/download-artifact@v4
        with: { name: native-ghostty-linux-x64, path: artifacts/native/linux-x64 }
      - uses: actions/download-artifact@v4
        with: { name: native-tcodepty-linux, path: artifacts/native }
      - run: chmod +x artifacts/native/*/tcode-pty-spawn
      - name: Fetch esctest2 (pinned)
        run: tools/terminal/fetch-esctest.sh                # native/toolchain.json 中的提交与 SHA-256
      - name: Run
        run: dotnet run -c Release --project tools/terminal/EscTestRunner -- --out artifacts/esctest --known tests/TCode.Terminal.Core.Tests/EscTest/known-failures.txt
      - name: Summary
        if: always()
        run: cat artifacts/esctest/summary.md >> "$GITHUB_STEP_SUMMARY"
      - uses: actions/upload-artifact@v4
        if: always()
        with: { name: esctest-log, path: artifacts/esctest/ }

  # NativeAOT：发布 Desktop（含 agenthost/ 与 ptyhost/），0 个 IL 警告；运行 --smoke 与 --self-test
  aot-smoke:
    needs: [native-ghostty, native-tcodepty]
    strategy:
      fail-fast: false
      matrix:
        include:
          - { rid: linux-x64, os: ubuntu-latest,  pty: linux, exe: "", container: "almalinux:8" }   # glibc 2.28（§5.3）
          - { rid: osx-arm64, os: macos-latest,   pty: osx,   exe: "", container: "" }
          - { rid: win-x64,   os: windows-latest, pty: "",    exe: ".exe", container: "" }
    runs-on: ${{ matrix.os }}
    container: ${{ matrix.container }}
    steps:
      - name: Container prerequisites
        if: matrix.container != ''
        run: dnf install -y git tar xz clang zlib-devel libicu fontconfig findutils which   # NativeAOT 链接 + 无头 Skia 的字体
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - uses: actions/download-artifact@v4
        with: { name: native-ghostty-${{ matrix.rid }}, path: artifacts/native/${{ matrix.rid }} }
      - uses: actions/download-artifact@v4
        if: matrix.pty != ''
        with: { name: native-tcodepty-${{ matrix.pty }}, path: artifacts/native }
      - if: runner.os != 'Windows'
        run: chmod +x artifacts/native/*/tcode-pty-spawn
      - name: Publish
        shell: bash
        run: |
          dotnet publish src/TCode.Desktop -c Release -r ${{ matrix.rid }} -o artifacts/aot 2>&1 | tee publish.log
          if grep -E "warning IL[0-9]+" publish.log; then
            echo "::error::NativeAOT publish produced IL warnings"
            exit 1
          fi
      - name: Layout check
        shell: bash
        run: tools/terminal/check-layout.sh artifacts/aot ${{ matrix.rid }}   # ptyhost/ 文件齐全、可执行位、conpty + x64/arm64 OpenConsole、两份 libghostty 字节相同、Linux 上所有 ELF 的 GLIBC_ ≤ 2.28
      - name: ptyhost self-test        # 启动 shell、echo、校验输出、resize、快照往返
        shell: bash
        run: ./artifacts/aot/ptyhost/tcode-ptyhost${{ matrix.exe }} --self-test
      - name: AgentHost self-test
        shell: bash
        run: ./artifacts/aot/agenthost/TCode.AgentHost${{ matrix.exe }} --self-test
      - name: Desktop smoke            # 无头：Fake 模型一轮 + 打开终端标签执行 echo + Agent 终端模式 bash（09 §16 S1）
        shell: bash
        run: ./artifacts/aot/TCode${{ matrix.exe }} --smoke --out=artifacts/smoke.png
      - name: Host E2E tests against AOT binaries
        shell: bash
        run: dotnet test tests/TCode.Terminal.Host.Tests -c Release --filter "Category=AotE2E"
        env:
          TCODE_TEST_PTYHOST_EXE: ${{ github.workspace }}/artifacts/aot/ptyhost/tcode-ptyhost${{ matrix.exe }}
          TCODE_TEST_HOST_EXE: ${{ github.workspace }}/artifacts/aot/agenthost/TCode.AgentHost${{ matrix.exe }}
      - uses: actions/upload-artifact@v4
        if: always()
        with: { name: smoke-${{ matrix.rid }}, path: artifacts/smoke.png, if-no-files-found: ignore }

  bench-pr:
    if: github.event_name == 'pull_request' && !contains(github.event.pull_request.labels.*.name, 'skip-bench')
    needs: [native-ghostty]
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with: { fetch-depth: 0 }
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - uses: actions/cache@v4
        with:
          path: artifacts/bench-data
          key: benchdata-${{ hashFiles('tools/terminal/gen-bench-data/sources.json') }}
      - name: A/B（base 与 head 在同一台机器上）
        run: tools/terminal/bench-ab.sh "${{ github.event.pull_request.base.sha }}" HEAD --profile pr
      - run: cat artifacts/bench-ab/summary.md >> "$GITHUB_STEP_SUMMARY"
      - uses: actions/upload-artifact@v4
        with: { name: bench-pr, path: artifacts/bench-ab/ }
```

`.github/workflows/nightly.yml`（骨架）：

```yaml
name: Nightly
on:
  schedule: [{ cron: "17 3 * * *" }]
  workflow_dispatch:
jobs:
  native:
    uses: ./.github/workflows/native.yml        # 把 native-ghostty / native-tcodepty 抽成可复用工作流，ci.yml 与 nightly.yml 共用
  test-arm:
    needs: native
    strategy:
      fail-fast: false
      matrix:
        include:
          - { rid: linux-arm64, os: ubuntu-24.04-arm }
          - { rid: win-arm64,   os: windows-11-arm }
          - { rid: osx-x64,     os: macos-15-intel }
    runs-on: ${{ matrix.os }}
    steps: [ "…与 build-test 相同，外加 aot-smoke 的发布与自检…" ]
  nightly-stress:
    needs: native
    strategy: { matrix: { os: [ubuntu-latest, macos-latest, windows-latest] } }
    runs-on: ${{ matrix.os }}
    steps: [ "dotnet test … --filter-trait Category=Stress", "dotnet test … --filter-trait Category=FuzzLong（仅 Linux，1 h）" ]
  bench-nightly:
    needs: native
    strategy: { matrix: { os: [ubuntu-latest, macos-latest] } }
    runs-on: ${{ matrix.os }}
    steps: [ "dotnet run -c Release --project tests/TCode.Terminal.Benchmarks -- --profile nightly --runtimes net10.0 nativeaot10.0 --exporters json github",
             "推送结果到 bench-history 分支；回退 > 10% 时开 issue" ]
```

runner 标签（`ubuntu-24.04-arm`、`windows-11-arm`、`macos-15-intel`、`macos-latest`）与 StreamMark 当前使用的一致 [已核实 StreamMark `ci.yml`]。

---

## 8. 升级流程

### 8.1 Ghostty 升级

| 步 | 内容 |
|---|---|
| 1 | 选目标提交（优先 tag 或经过上游 CI 全绿的提交）；阅读两次 pin 之间 `include/ghostty/vt/*.h` 的 diff（API 标为 work-in-progress，不稳定 [已核实 research/04 §0.8]），以及 `test/fuzz-libghostty`、`test/esctest` 的变化 |
| 2 | 更新 `ghostty-upstream.json`（commit、date、versionString）；逐个重放 overlay：`git apply --check`，失败的补丁重写或删除（上游已修复时） |
| 3 | 本地 `build-native.sh --force`；重新生成 `exports-<sha>.txt`，审阅新增 / 删除的符号；同步 C# 绑定（`TCode.Terminal.Core/Interop/Ghostty`） |
| 4 | 若 `minimum_zig_version` 变化 → 先走 §8.3 |
| 5 | 运行全部测试；黄金样本若有变化，逐个确认是上游行为修正还是回退（不允许不看就 `TCODE_UPDATE_GOLDEN`）；esctest known-failures 更新 |
| 6 | 快照格式：libghostty 快照 v1 **不保证二进制兼容** [已核实 04 §18 第 4 条]。升级后 revive 依赖 `tail.vt` 回退；在测试中用旧版本生成的 `snapshot.bin` 验证回退路径 |
| 7 | `bench-pr` 自动标记"原生库版本变更"，人工审阅吞吐变化 |
| 8 | PR 描述列出：上游提交范围、API 变化、补丁变化、黄金样本变化、基准对比 |

### 8.2 ConPTY 升级

1. 读 `Microsoft.Windows.Console.ConPTY` 新版本的发行说明（以及对应的 Windows Terminal 发行说明）；
2. 只改 `Directory.Packages.props` 中的版本——`conpty.dll` 与 `OpenConsole.exe` 来自同一个包，天然成对（WezTerm 曾因只升级其一导致 pwsh 崩溃 [已核实 research/02 §3.4]）；
3. 在 Windows 10 1809（最低支持，01 §1）虚拟机、Windows 11 x64、Windows 11 arm64 上跑 L2 ConPTY 组与 L5b；
4. 检查 `dwFlags` 定义是否变化（0x8 / 0x10 曾被重新定义为字宽模式 [已核实 research/02 §3.2]）；
5. 预览版（如 1.26.x-preview）不进入发布分支。

### 8.3 Zig 升级

Zig 版本由 Ghostty 决定：只有当 pin 的 Ghostty 提交要求新版本时才升级（升级 Zig 的同时必然升级 Ghostty）。

1. 更新 `native/toolchain.json`（版本 + 6 个 tarball 的 SHA-256，取自 `https://ziglang.org/download/index.json`）；
2. 重新评估 `build-flags.json`：win-arm64 的 `-Dsimd=false` 原因是 Zig 0.16.0 的标准库问题（§4.3），新版本可能已修复；`zig cc` 的 glibc 目标写法是否变化；
3. Linux 的 libtcodepty 与 musl 静态 helper 用同一个 Zig 编译（macOS 用 Apple clang，不受影响），跑 L2 全部用例（03 §14）；
4. 清空 CI 的 Zig 缓存（缓存键含版本号，自动失效）；
5. 在开发者文档中提醒更新本地 Zig（构建脚本会因版本不符直接报错）。

---

## 9. 故障排查

| 现象 | 原因 | 处理 |
|---|---|---|
| `build-native` 报 "zig 0.17.0 is not 0.16.0" | PATH 中是 Homebrew 的新版 | 运行 `tools/terminal/install-zig.sh`，或设置 `TCODE_ZIG` |
| `error: unable to find Visual Studio installation` / 找不到 MSVC 头文件（win-x64） | 没有 VS Build Tools，或在 Linux / macOS 上尝试构建 `*-windows-msvc` | 在 Windows 上安装"使用 C++ 的桌面开发"工作负载；msvc 目标不能交叉编译 [已核实 上游注释] |
| win-arm64 构建在标准库的 stack-trace 代码处编译失败 | Zig 0.16.0 + SIMD 归档的已知问题 | 确认 `build-flags.json` 中有 `-Dsimd=false` [已核实 RoyalTerminal 注释] |
| macOS 目标报 Apple SDK 路径错误 | 在 Linux 上构建 osx RID，或 `xcode-select` 指向了错误位置 | macOS 目标只能在 macOS 上构建；`sudo xcode-select -s /Applications/Xcode.app` |
| `DllNotFoundException: ghostty-vt` | `artifacts/native/<rid>/` 为空，或 Debug 构建的 RID 与产物 RID 不同（例如 Rosetta 下运行 x64 SDK） | 运行 `build-native.sh <rid>`；`dotnet --info` 检查 RID |
| macOS：`Library not loaded … image not found` 或 `code signature invalid` | 改过 install name 后没有重新签名；hardened runtime 下 dylib 的 Team ID 不同 | `codesign -s - -f`（开发）；打包时全部用同一证书签名 |
| Linux 发布物在 RHEL 8 上报 `GLIBC_2.3x not found` | 某个原生库链接了新版 glibc 符号（忘了 `.2.28` 后缀，或用了系统 gcc） | `verify-native` 的 glibc 检查；所有 Unix 原生代码都用 `zig cc -target …-gnu.2.28` |
| 用户机器上 SIGILL / `0xC000001D`（非法指令） | 原生库包含 runner CPU 的指令（构建时没有显式 `-Dtarget`），或 SIMD 代码未做运行时分派 | 检查 `build-flags.json`；`native-cpu-compat` 作业必须通过；win-x64 退回标量构建 |
| Windows 终端行为异常（OSC 丢失、resize 整屏重画、清屏后旧内容回来） | 加载了 System32 的 conhost，而不是随包的 OpenConsole | `--self-test` 输出加载路径；检查 `ptyhost\OpenConsole.exe` 是否存在（conpty.dll 找不到时静默回退 [已核实 research/02 §3.4]） |
| Linux：AOT 二进制在 RHEL 8 上报 `GLIBC_2.3x not found`（原生库检查通过） | AOT 主程序在新 glibc 的 runner 上发布 | 在 `almalinux:8` 容器中发布（§5.3） |
| Unix：`posix_spawn` 返回 EACCES，或子进程立即退出码 126 | `tcode-pty-spawn` 丢失可执行位（工件下载、zip 打包） | CI 中 `chmod +x`；Linux 用 tar.gz；`check-layout.sh` 检查 |
| AOT 发布出现 `IL2026` / `IL3050` | 新代码用了反射或运行时封送 | 改为 `LibraryImport` + `DisableRuntimeMarshalling`、源生成 JSON（01 §1 的约束）；不允许用 `NoWarn` 压制 |
| `esctest` 大量失败且集中在某一类 | 未开启 DECRQCRA 校验和选项，或屏幕不是 80×25 | 检查 `TestMode` 是否设置 `XT_CHECKSUM_REPORT` 与 `XT_CHECKSUM_EXTENSION = 23` [已核实 `terminal.h:2471-2505`] |
| CI 上 `dotnet restore` 找不到 StreamMark 包 | `nuget.config` 指向本地源 | 见 §7.2 末尾，需要发布 StreamMark 包 |
| overlay 补丁 `git apply --check` 失败 | 上游改动了补丁涉及的代码 | 按 §8.1 第 2 步重写补丁；确认上游是否已修复 |
| `zig build` 下载依赖失败（网络受限） | Ghostty 的依赖从 `deps.files.ghostty.org` 与 GitHub 下载 [已核实 `build.zig.zon`] | 预先 `zig build --fetch` 填充 `~/.cache/zig/p`；CI 中缓存该目录 |

---

## 10. 待决问题与发现的问题

1. **TCode 目前没有任何 CI**（无 `.github/`）。本文的 `ci.yml` 同时承担现有项目的测试；StreamMark 依赖本地 NuGet 源，CI 跑起来之前必须先解决（§7.2）。
2. **`LSMinimumSystemVersion` = 11.0 低于 .NET 10 的下限（15.0）与 libghostty-vt 的下限（13.0）**（§6.2）；01 §1 的平台表没有写 macOS 最低版本，建议补上 15.0。
2a. **Linux AOT 主程序的 glibc 基线**：在 Ubuntu runner 上发布的 AOT 二进制无法在 RHEL 8 上运行，必须在 glibc 2.28 容器中发布（§5.3）；这同样影响现有的 Desktop 与 AgentHost。
3. **win-x64（以及 win-arm64）可能使用标量构建**，与 01 §1"ASCII ≥ 500 MB/s"的性能目标冲突（§4.3）；需要决定是放宽该 RID 的目标，还是以 CPU 兼容性验证为前提使用 SIMD 构建。
4. **04 §1.1 把 `ghostty-build.txt` 放在 `Contents/MacOS/` 下**，会影响 macOS 签名 / 公证；建议移到 `Contents/Resources/`（§6.2），需要 04 的作者确认。
5. `-Dversion-string` 与 `-Dlib-version-string` 哪一个进入 lib-vt 的 `build_info`（§4.1），M0 确认。
5a. **03 §14 另设计了一个 `.github/workflows/terminal-pty.yml`**（test-rhel8、ASan 构建、macos-26 runner、Windows 降级路径）。建议并入本文的 `ci.yml` / `nightly.yml`（PTY 用例本来就在 `build-test` 中运行；ASan 与 Windows 降级矩阵放进 nightly），避免两套原生构建作业。
6. 01 §4 的目录结构中 `native/ghostty/` 写的是"构建脚本"，本文把脚本放在 `tools/terminal/`，`native/` 只放 pin、补丁与 C 源码——与 01 不矛盾，但建议 01 的注释同步。

---

## 来源

- 本仓库：`global.json`、`Directory.Build.props`、`Directory.Packages.props`、`nuget.config` 的用法注释、`src/TCode.Desktop/TCode.Desktop.csproj:36-54`、`src/TCode.AgentHost/TCode.AgentHost.csproj`、`tools/package_macos_app.sh`、`src/TCode.Desktop/SmokeMode.cs`、`src/TCode.AgentHost/SelfTest.cs`；仓库根目录无 `.github/`（2026-10-16 检查）。
- StreamMark（本地）：`.github/workflows/ci.yml`、`native/build.sh`、`native/build.ps1`、`Directory.Build.targets`、`src/StreamMark.RaTeX/StreamMark.RaTeX.csproj`。
- Ghostty `ghostty-org/ghostty@b3dc3d7`：`build.zig.zon`（`minimum_zig_version`、依赖哈希）、`src/build/Config.zig`（`simd`、`strip`、`version-string`、`osVersionMin`）、`src/build/GhosttyLibVt.zig`（产物命名、静态库处理）、`example/c-vt-static/build.zig`、`.github/workflows/test.yml`（`build-libghostty-vt*`、`test-lib-vt-pkgconfig`、`test-esctest`）。
- RoyalTerminal `royalapplications/RoyalTerminal@01833b8`：`.github/workflows/release.yml`（Windows / Linux 的 zig 参数与注释）。
- Zig 下载索引：https://ziglang.org/download/index.json （2026-10-16 读取：0.16.0 发布于 2026-04-13）。
- 研究文档：[research/01](research/01-emulator-cores.md) §4.1.4–4.1.7；[research/02](research/02-pty.md) §3.2、§3.4、§4、§5、§9；[research/04](research/04-rendering-performance.md) §0.8、§2.9、§11；[04-pty-host.md](04-pty-host.md) §1.1、§2.4、§15、§18。
