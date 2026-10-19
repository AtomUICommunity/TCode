# 07 · Shell、Profile 与配置（TKCode.Terminal.Shell + settings 的 `terminal` 节）

> 状态：设计稿 v1（2026-10-16）。上游：[01-architecture.md](01-architecture.md)（D4、D7、D9、§3.3、§6.3）、[research/05](research/05-platform-behavior-config.md)、[research/03](research/03-vt-protocol.md) §2.4/§7、[research/02](research/02-pty.md) §2.4/§2.9、[docs/settings.md](../settings.md)、`src/TKCode.Config`。SSH 部分见 [08-ssh.md](08-ssh.md)，Agent 使用终端见 09。
>
> 标注：**[实]** = 已在源码、官方文档或本机核实；**[推]** = 推断，实现前要验证；**[定]** = 本文做出的设计决定（可在评审中推翻）。
>
> 源码前缀沿用 research/05 §0：VSC = microsoft/vscode `4765bd3`，GH = ghostty `b3dc3d7`，WT = microsoft/terminal `44b440b`，IT = iTerm2 `5794785`，WZ = wezterm `e2a98e1`，OSSH = openssh-portable `6545e6a`。路径相对各仓库根目录。

> **已落地的子集（2026-10-19）**：`TKCode.Shell`（只依赖 BCL）探测 pwsh（最新稳定版 / 预览版）、Windows PowerShell、cmd、Git Bash 和 Unix 的 `/etc/shells`，规则同 §2.3 的 1–4 项（pwsh 另外只保留每类排名第一的安装，不产出 `pwsh@<变体>`）。`terminal.defaultProfile` 与 `agent.shell.profile` 只接受 `auto`、探测到的 id、程序名或绝对路径，经 `host/configure` 的 `DefaultShell` 下发给 pty host；`terminal.profiles`、WSL / MSYS2 / VS 开发者命令行、shell 集成注入仍属 M4。Windows 的 `system` 现在是 PowerShell 7 → Windows PowerShell → cmd。

## 0. 结论摘要

1. **所有探测与启动构造都在 pty host 里完成**（01 §6.3）。Desktop 只负责把 `settings.json` 的 `terminal` 节解析成一个与 TKCode.Config 无关的 DTO（放在 `TKCode.Terminal.Protocol`），通过 `host/configure` 推给 host；host 里的 `ProfileResolver` 把"探测结果 + 设置覆盖 + 自定义 profile"合并，`LaunchBuilder` 产出 `PtyLaunch`。
2. **探测**：Unix 以 `getpwuid(getuid())->pw_shell` 为准、`/etc/shells` 列出其余 shell、`$SHELL` 只作回退；Windows 采用 WT 的 pwsh 排名规则，另探测 Windows PowerShell、cmd、Git Bash、WSL（读注册表，失败才跑 `wsl.exe -l -q`）、MSYS2、Cygwin、VS 开发者命令行。冷探测预算：Unix < 5 ms，Windows < 150 ms（不含 vswhere，后者异步）。
3. **macOS 启动**（2026-10-17 用户决定，不经 `/usr/bin/login`）：spawn helper 直接 exec shell，argv0 为 `-zsh` 这样的 login 形式，PATH 由系统的 `/etc/zprofile`（path_helper）补齐；shell 是 PTY 的直接子进程，退出码准确（§3.2）。bash 例外：为了注入 shell 集成，用非 login 的 `--init-file` 并在脚本里模拟 login 启动文件。
4. **环境变量**：以 Desktop 启动时的原始环境为基础（不是 host 自己的环境，原因见 §4.1），剔除 `TKCODE_*`（包括 pty host 的连接地址和令牌）、其他终端遗留的变量、启动器变量、`SHLVL`；设置 `TERM=xterm-256color`、`COLORTERM=truecolor`、`TERM_PROGRAM=TKCode`、`TERM_PROGRAM_VERSION`、`TKCODE_TERMINAL_ID`；macOS 按**系统区域**补 `LANG`。合并顺序见 §4.6。
5. **Shell 集成**（D7，默认开）：bash 用 `--init-file`；zsh 用 ZDOTDIR，但只放一个 `.zshenv`，进来后立刻还原用户的 ZDOTDIR（Ghostty 的思路，HISTFILE 不受影响）；fish 用 `XDG_DATA_DIRS` 的 `vendor_conf.d`；pwsh 用 `-NoExit -Command`；nu 依赖 nu 自带的 OSC 133；cmd 用 `PROMPT` 做部分集成。发出 OSC 133 A/B/C/D（含退出码）、OSC 7、带 nonce 的 OSC 633;E，以及一条握手属性 `OSC 633;P;TKCodeShellIntegration=…`。10 秒内没有握手判为失败，并显示原因。
6. **脚本许可**：Ghostty 和 kitty 的 bash/zsh/fish 脚本是 **GPLv3**（文件头写明）[实]，TKCode 是 Apache-2.0，**不能拷贝**；VS Code 的脚本是 MIT，可以改编并保留版权声明。我们自己写脚本，只借鉴机制。
7. **`terminal` 节**：沿用 settings.md 的风格（省略即跟随系统、同 id 覆盖内置项、JSONC、宽容解析、500 ms 防抖热重载）。新增三点：
   - 平台覆盖块 `terminal.macos` / `windows` / `linux`，可以覆盖任意字段，不再使用"字符串或 `{macos,linux,windows}`"这种联合类型；
   - **字段级容错**：`terminal` 节中的错误只让该字段回到默认值、或只停用该 profile，不会让整个 settings.json 失效（厂商配置不能因为一个字号写错而失效）；
   - 新增 `persistence.*`（键名以 04-pty-host.md 为准：enabled、onQuit、scrollbackLines、orphanGraceMinutes、keepAliveMaxHours、saveIntervalSec，取代 research/05 的 `restore.*`）、`graphics.*`（Kitty 图形开关与内存上限）、`ssh.*`（由 08 定稿）。Agent 相关设置不放在 `terminal` 下，而是 09 §2.2 定义的顶层 `agent.shell.*`。

---

## 1. 范围、进程归属与接口

### 1.1 数据流

```
~/.tkcode/settings.json
   │  UserSettingsService（已有：FileSystemWatcher + 500 ms 防抖）
   ▼
TKCode.Desktop: TerminalConfig.Parse(JsonObject? terminal, Platform)      ← TKCode.Config（新增）
   │  → TerminalSettingsAdapter：映射为 Protocol DTO（不含 TKCode.Config 类型）
   ▼  host/configure { config, baseEnvironment, clientVersion }           ← TKCode.Terminal.Protocol
tkcode-ptyhost
   ├ ProfileResolver：ProfileProviders.Detect() ⊕ config.Profiles → ResolvedProfile[]
   ├ profiles/list → Desktop（"+" 菜单、设置页）；同时写磁盘缓存
   └ session/create { profileId, cwdRequest, purpose, extraEnv, size }
        → LaunchBuilder.Build(profile, LaunchContext) → LaunchPlan
        → ITerminalConnection（TKCode.Terminal.Pty）
```

- **依赖方向**（D2、01 §4）：`TKCode.Terminal.Shell` 只依赖 BCL 和 `TKCode.Terminal.Pty`（用其中的 `PtyLaunch`、`TerminalSize`）。`{env:VAR}` 展开、平台判断等都在 Shell 内实现，**不引用 TKCode.Config**。语法与 `McpVariables` 保持一致，两边共用一套测试向量（§9.1）[定]。
- **Desktop 不做探测**：Desktop 打开"+"菜单时，如果 host 还没启动，先显示磁盘缓存 `$TKCODE_HOME/cache/terminal-profiles.json`（host 写入），同时异步拉起 host 并刷新列表（§2.6）。

### 1.2 对 01 §6.3 接口的细化

01 §6.3 的签名保持不变，下面只做**向后兼容的扩充**（新增字段都有默认值）：

```csharp
namespace TKCode.Terminal.Shell;

public interface IProfileProvider
{
    /// 同步探测：只做文件、注册表这类廉价检查（预算见 §2.6）。
    IReadOnlyList<TerminalProfile> Detect();
}

/// 可选：需要运行外部进程的探测器（vswhere、wsl.exe 回退）。ProfileResolver 先用同步结果出菜单，再合并异步结果。
public interface IAsyncProfileProvider : IProfileProvider
{
    ValueTask<IReadOnlyList<TerminalProfile>> DetectSlowAsync(CancellationToken ct);
}

public sealed record TerminalProfile(
    string Id, string Name, string Command, IReadOnlyList<string> Arguments,
    bool LoginShell, ShellKind Kind, string? Icon, ProfileSource Source)
{
    // —— 以下为本文新增 ——
    public PathStyle PathStyle { get; init; } = PathStyle.Native;          // Native | Msys | Cygwin | Wsl：拖放路径转义、OSC 7 路径换算
    public string? RawCommandLine { get; init; }                           // 仅 Windows：映射为 03 §4 的 PtyLaunch.WindowsCommandLine，原样作为 lpCommandLine（cmd /k "…" 等，§3.4）
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = Empty; // profile 自带的环境（MSYS2 的 MSYSTEM 等）
    public string? WorkingDirectory { get; init; }                         // profile 固定的起始目录；null = 用全局规则
    public bool? ShellIntegration { get; init; }                           // null = 跟随全局
    public bool RequiresTrust { get; init; }                               // VS Code 的 unsafe：首次使用前确认（MSYS2、Cygwin）
    public string? Version { get; init; }                                  // 菜单副标题："7.6.1"、"Store"
    public bool Hidden { get; init; }                                      // 探测到但默认收在"更多"里（次要 pwsh 安装、VS 开发者命令行）
    public string? WslDistribution { get; init; }                          // Kind == Wsl 时
    public SshTarget? Ssh { get; init; }                                   // Kind == Ssh 时（08 §2.4）
    public IntegrationHint Integration { get; init; } = IntegrationHint.Auto; // Auto | Skip | AppendToPwshCommand（VS DevShell，§5.3.4）
}

public enum ProfileSource { Detected, Settings, DetectedOverridden }
public enum PathStyle { Native, Msys, Cygwin, Wsl }

public static class LaunchBuilder
{
    public static LaunchPlan Build(TerminalProfile profile, LaunchContext context);
}

public sealed record LaunchContext(
    LaunchPurpose Purpose,                         // Interactive | Agent | AcpAuth | Revive（09 使用后两者）
    TerminalSize InitialSize,
    PtyWidthMode WidthMode,
    IReadOnlyDictionary<string, string> BaseEnvironment, // §4.1
    ShellConfig Config,                            // host/configure 下发的 terminal 配置（已平台合并）
    string? ProjectRoot,
    CwdRequest Cwd,                                // Default | Inherit(sessionId) | Explicit(path)
    IReadOnlyDictionary<string, string?> ExtraEnvironment, // 调用方（Agent/ACP）追加；null = 删除
    bool? ShellIntegrationOverride,                // "关闭 shell 集成后重试"时为 false
    IShellPlatform Platform);                      // 可替换的系统访问：文件、注册表、账户、时钟（§9.1）

public sealed record LaunchPlan(
    PtyLaunch Launch,
    IntegrationPlan Integration,                   // Mode：Injected | NativeExpected | Partial | None；Reason；Nonce；ScriptVersion
    string DisplayCommand,                         // 错误界面显示的等价命令行（已脱敏：不含 nonce）
    IReadOnlyList<LaunchWarning> Warnings);        // 例如"PATH 前缀会被 path_helper 重排"
```

`Detect()` 保持同步是为了满足 01 的签名；Windows 上需要启动进程的部分放进 `IAsyncProfileProvider`（§2.6）。这是对 01 的补充，列在 §10。

---

## 2. Profile 探测

### 2.1 通用规则

**profile id**（同时是 `terminal.profiles` 的键，也写入 `state.json` 的标签恢复数据）[定]：

| 来源 | id 格式 | 例子 | 说明 |
|---|---|---|---|
| 虚拟 id | `system` | `system` | 不是独立条目，表示"账户登录 shell"（Unix）或"最佳 PowerShell"（Windows）。`defaultProfile` 的默认值 |
| Unix shell | shell 文件名 | `zsh`、`bash`、`fish`、`nu`、`pwsh` | 同名不同路径时，账户 shell（或 `/etc/shells` 中靠前的那个）用短名，其余用 `<名>@<绝对路径>`，例如 `bash@/opt/homebrew/bin/bash` |
| Windows PowerShell 7 | `pwsh`、`pwsh-preview`、`pwsh@<变体>` | `pwsh@store`、`pwsh@scoop`、`pwsh@dotnet`、`pwsh@x86`、`pwsh@path` | `pwsh` 始终指"排名第一的稳定版"，安装新版本后会指向新的那个（与 WT 的 "PowerShell" 语义一致），用户对 `pwsh` 的覆盖随之生效 |
| 其他 Windows 内置 | `powershell`、`cmd`、`git-bash`、`msys2`、`cygwin` | | |
| WSL | `wsl:<发行版名>` | `wsl:Ubuntu-24.04` | 名字原样保留；查找时不区分大小写 |
| VS 开发者命令行 | `vs-devcmd:<instanceId>`、`vs-devshell:<instanceId>` | `vs-devshell:1a2b3c4d` | instanceId 来自 vswhere |
| SSH | `ssh:<Host 别名>` | `ssh:prod-1` | 见 08 §2 |
| 自定义 | 用户任意写 | `node-dev` | 正则 `^[A-Za-z0-9][A-Za-z0-9._@:+-]{0,127}$`；不能以 `wsl:`、`ssh:`、`vs-` 开头，除非 `type`/`command` 明确给出（这时视为自定义 profile，只是借用了名字） |

**合并顺序**（`ProfileResolver`）：

1. 各平台探测器按 §2.3/§2.4 的顺序产出 `Detected` 列表；
2. `terminal.profiles.<id>` 的 id 与探测结果相同时，逐字段覆盖，`Source = DetectedOverridden`；`"enabled": false` 表示隐藏；
3. id 不同的是自定义 profile，必须有 `command`（或 `type: "ssh"` + `host`），否则该 profile 报错、停用，不影响其他（§7.5）；
4. `platforms` 字段不含当前平台的条目丢弃；
5. 按"探测顺序 → 自定义（按设置文件中的顺序）"排序；`Hidden` 的条目在菜单里收进"更多"。

**默认 profile 的解析**：`defaultProfile`（平台覆盖块优先）→ 找不到或已停用时记一条警告，回到 `system` → `system` 的解析见各平台小节 → 仍然失败时走 §2.7 的最终回退。

**`ShellKind` 的判定**：按可执行文件名（去掉 `.exe`，小写）映射：`bash`→Bash，`zsh`→Zsh，`fish`→Fish，`pwsh`/`pwsh-preview`→Pwsh，`powershell`→PowerShell，`cmd`→Cmd，`nu`→Nu，`wsl`→Wsl，`ssh`→Ssh，其他→Other。Kind 决定 argv0 形式（§3）和注入方式（§5）。用户可以在 profile 里写 `"kind"` 强制指定（例如 `/usr/local/bin/mybash` 实际是 bash）。

### 2.2 macOS 与 Linux

```
DetectUnix():
  account = tkcode_get_account()              // libtkcodepty：getpwuid_r(getuid()) → { name, home, shell }
  shells  = ParseEtcShells("/etc/shells")
  result  = []

  accountShell = account.shell
  if !IsUsable(accountShell):                // 空、不存在、不可执行、/usr/bin/false、*/nologin
      accountShell = env(SHELL) if IsUsable(env(SHELL)) else null     // 只作回退
  if accountShell == null:
      accountShell = macOS ? "/bin/zsh" : FirstUsable("/bin/bash", "/bin/sh")

  for path in [accountShell] ++ shells:      // 账户 shell 放第一个，即使它不在 /etc/shells 里
      if !IsUsable(path) or IsExcluded(path): continue
      real = realpath(path)
      if seen(real): continue                // Linux merged-usr：/bin/bash 与 /usr/bin/bash 指向同一个文件
      result += MakeProfile(path, isAccountShell: path == accountShell)
  return result

ParseEtcShells(file):
  逐行；去掉首尾空白；跳过空行与 '#' 开头的行；只接受以 '/' 开头的绝对路径；文件不存在时返回 []

IsExcluded(path): basename ∈ { rbash, git-shell, nologin, false, true, tmux, screen, scponly, rssh }
```

- **为什么以 passwd 为准**：login(1)、VTE、Ptyxis、WezTerm、从桌面启动的 Ghostty 都这样做；`chsh` 之后 `$SHELL` 会过期，直到用户重新登录桌面 [实 research/05 §1.1–1.2]。`$SHELL` 只在 passwd 不可用时使用（例如容器里 passwd 的 shell 字段是 `/sbin/nologin`）。
- **getpwuid 放在 C shim 里**：`struct passwd` 在 macOS 上多出 `pw_change`、`pw_class`、`pw_expire` 字段，布局与 glibc 不同，不适合在 C# 中镜像。`libtkcodepty` 增加 `tkcode_get_account(buf, len)`，返回以 NUL 分隔的 `name\0home\0shell\0` [定]。macOS 上 getpwuid 会经过 opendirectoryd，结果与 `dscl . read /Users/$USER UserShell` 相同 [推]。
- **`sh` 和 `dash` 也列出**，但排在后面（VS Code 也列出 `/etc/shells` 的每一项 [实 VSC src/vs/platform/terminal/node/terminalProfiles.ts:401-440]）。`tmux`、`screen` 在 Debian 的 `/etc/shells` 里，但它们不是 shell，排除；需要时用户可以写自定义 profile。
- **Linux Flatpak**（以后）：`/.flatpak-info` 存在时，账户 shell 要在宿主机上用 `flatpak-spawn --host getent passwd $USER` 查询，启动也要经 `flatpak-spawn --host`（Ptyxis、Ghostty 的做法 [实 research/05 §1.2]）。v1 不发布 Flatpak，`IsFlatpak()` 时只给出一条"不支持"的警告 [定]。
- **macOS 上命令的解析**：host 是从 Dock 启动的 Desktop 拉起的，`PATH` 只有 `/usr/bin:/bin:/usr/sbin:/sbin`。自定义 profile 写 `"command": "nu"` 这样的短名时，在 host 的 PATH 里找不到 Homebrew 的程序。解析顺序：绝对路径 → 基础环境的 PATH → `path_helper -s` 给出的 PATH（`/etc/paths` + `/etc/paths.d/*`）→ 常见目录 `/opt/homebrew/bin`、`/usr/local/bin`、`~/.cargo/bin`、`~/.local/bin`、`~/.nix-profile/bin`、`/nix/var/nix/profiles/default/bin`、`/run/current-system/sw/bin`。找到后以**绝对路径**写入 `PtyLaunch.File` [定]。原因：login 包装里的 bash 用 `--noprofile` 启动，此时 PATH 还没有被 login 文件补全，`exec` 找不到短名。

### 2.3 Windows

探测器按下表顺序运行。路径中的环境变量用 host 进程的值展开（`%ProgramFiles%` 等不会因用户而异）。

| # | 探测器 | 候选路径 / 方法 | 产出 | 来源 |
|---|---|---|---|---|
| 1 | PowerShell 7 | ① `%ProgramFiles%\PowerShell\<n>\pwsh.exe`（枚举子目录：纯数字为稳定版，`<n>-preview` 为预览版）；② `%ProgramFiles(x86)%\PowerShell\<n>\pwsh.exe`（WOW）；ARM64 系统上再查 `%ProgramFiles(Arm)%`；③ Store/MSIX 应用执行别名 `%LOCALAPPDATA%\Microsoft\WindowsApps\Microsoft.PowerShell_8wekyb3d8bbwe\pwsh.exe`、`…\Microsoft.PowerShellPreview_8wekyb3d8bbwe\pwsh.exe`；④ `%USERPROFILE%\.dotnet\tools\pwsh.exe`；⑤ Scoop：`%SCOOP%`（缺省 `%USERPROFILE%\scoop`）`\apps\pwsh\current\pwsh.exe`，其次 `\shims\pwsh.exe`；⑥ 最后在 PATH 中找 `pwsh.exe`（Chocolatey 等），按 realpath 去重 | `pwsh`、`pwsh-preview`、其余为 `pwsh@<变体>`（`Hidden`） | WT TSM/PowershellCoreProfileGenerator.cpp:52-135；VSC src/vs/base/node/powershell.ts:250-305 [实] |
| 2 | Windows PowerShell | `%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe` | `powershell` | WT TSM/defaults.json:38-56 [实] |
| 3 | cmd | `%SystemRoot%\System32\cmd.exe`（不用 `%ComSpec%`，防止被改写；只有找不到时才回退到 `%ComSpec%`） | `cmd` | WT defaults.json:57-75 [实] |
| 4 | Git Bash | ① 注册表 `HKLM\SOFTWARE\GitForWindows` 的 `InstallPath`（[推]：Git for Windows 安装程序写入）；② PATH 中 `git.exe` 上两级目录；③ `%ProgramW6432%`、`%ProgramFiles%`、`%ProgramFiles(x86)%`、`%LOCALAPPDATA%\Programs` 下的 `Git`；④ Scoop 的 `apps\git\current`、`apps\git-with-openssh\current`。每个根目录依次试 `bin\bash.exe`、`usr\bin\bash.exe`。**不要用 `git-bash.exe`**：它会启动 mintty 窗口 | `git-bash`，参数 `--login -i`，`PathStyle=Msys` | VSC terminalProfiles.ts:286-336 [实] |
| 5 | WSL | 见下方专门小节 | `wsl:<名>`，Kind=Wsl，`PathStyle=Wsl` | WT TSM/WslDistroGenerator.cpp:19-101 [实] |
| 6 | MSYS2 | `%HOMEDRIVE%\msys64\usr\bin\bash.exe`；参数 `--login -i`；环境 `MSYSTEM=UCRT64`（[推]：MSYS2 目前推荐的默认环境）、`CHERE_INVOKING=1`（保持起始目录） | `msys2`，`RequiresTrust=true`，`PathStyle=Msys` | VSC terminalProfiles.ts:125-134 [实] |
| 7 | Cygwin | `%HOMEDRIVE%\cygwin64\bin\bash.exe`、`%HOMEDRIVE%\cygwin\bin\bash.exe`；参数 `--login -i` | `cygwin`，`RequiresTrust=true`，`PathStyle=Cygwin` | VSC terminalProfiles.ts:117-124 [实] |
| 8 | VS 开发者命令行（异步） | `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe -all -prerelease -format json -utf8 -nologo`（超时 3 s），取 `instanceId`、`installationPath`、`displayName`、`installationVersion` | `vs-devcmd:<id>`、`vs-devshell:<id>`，`Hidden=true` | WT TSM/VsDevCmdGenerator.cpp:39-67、VsDevShellGenerator.cpp:11-89 [实]；用 vswhere 而不是 COM `ISetupConfiguration2`，避免在 AOT 里做 COM 互操作 [定] |

**pwsh 的排名**（照搬 WT）[实 WT PowershellCoreProfileGenerator.cpp:150-346]：

1. 主版本号高者优先；
2. 同版本时按安装形态：Store > Scoop > dotnet tool > 传统安装（Program Files）> WOW（x86）；
3. 预览版单独排名，第一名为 `pwsh-preview`。

版本号取目录名（`7`、`7-preview`）；Store 和 dotnet tool 无法从路径得到版本时，读文件版本资源（`FileVersionInfo.GetVersionInfo`，AOT 可用 [推]）；应用执行别名是 0 字节的 reparse point，读不到版本时按"Store 最新"处理 [推]。

**应用执行别名的检测**：`File.Exists` 对 `IO_REPARSE_TAG_APPEXECLINK` 返回 true [推]，`CreateProcessW` 也能直接启动它（WT 就是这样启动 Store 版 pwsh 的）[推]。实现时加一条集成测试（Windows CI 上从 Store 安装 pwsh 不可行，用 mock 的属性测试 + 人工验收）。

**cmd 与 VS 开发者命令行用 `RawCommandLine`**：

- VS DevCmd：`cmd.exe /k "<inst>\Common7\Tools\VsDevCmd.bat" -startdir=none -arch=<a> -host_arch=<a>`；
- VS DevShell：`<pwsh 或 powershell> -NoExit -Command "&{Import-Module '<inst>\Common7\Tools\Microsoft.VisualStudio.DevShell.dll'; Enter-VsDevShell <instanceId> -SkipAutomaticLocation -DevCmdArguments '-arch=<a> -host_arch=<a>'}"`，`Integration = AppendToPwshCommand`（§5.3.4）。

`<a>` 取 host 进程的架构（`RuntimeInformation.OSArchitecture`：`x64`、`arm64`）[实 WT VsDevCmdGenerator.cpp]。

#### WSL

WT 的做法是读注册表，不运行 `wsl.exe` [实 WT WslDistroGenerator.cpp:19-23、195-215]。VS Code 运行 `wsl.exe -l -q` [实 VSC terminalProfiles.ts:347-389]。我们以注册表为主，命令为回退 [定]：

```
DetectWsl():
  if OSBuild < 19041: 不探测（与 VS Code 一致；19041 起 wsl.exe 才支持 --cd 与 Linux 路径）
  wslExe = GetSystemDirectory() + "\wsl.exe"      // 绝对路径，防止 PATH 劫持（WT GH#11096）
  if !exists(wslExe): return []

  key = HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss
  if key 存在:
      for sub in key.SubKeyNames（形如 {guid}）:
          name = sub["DistributionName"] as string
          if name 为空: continue
          if name 以 "docker-desktop" 或 "rancher-desktop" 开头: continue
          // WT 还跳过 Modern=1 的发行版：那是给 WT fragment 机制让路 [推]；TKCode 不读 fragment，所以不跳过
          yield name（保持注册表枚举顺序；DefaultDistribution 的 guid 对应的发行版排第一）
  else:
      out = platform.Processes.RunAsync(wslExe, ["-l", "-q"], env: { WSL_UTF8: "0" }, timeout: 2s)   // 放进 DetectSlowAsync；经 §2.8 的外部命令执行器
      if out.ExitCode != 0: return []                // 没有发行版时 wsl.exe 打印提示并返回非 0
      text = UTF16LE.Decode(out.Stdout)              // 强制 WSL_UTF8=0，否则用户设置了 WSL_UTF8=1 时会变成 UTF-8（VS Code #276253）
      text = text.TrimStart('\uFEFF').Replace("\0", "")
      names = text.Split('\n').Select(l => l.TrimEnd('\r').Trim()).Where(l => l.Length > 0)
      同上过滤 docker-desktop / rancher-desktop
```

- **命令行**：`<System32>\wsl.exe -d <名>`；起始目录用 `--cd`（§3.4）。
- **为什么注册表优先**：`wsl.exe -l` 在某些版本上会唤醒 WSL 服务，冷启动可达数百毫秒；注册表读取不到 1 ms [推]。
- **图标**：按名字含 Ubuntu / Debian / openSUSE / Alpine / Fedora / Arch / kali 选 lobe 或内置图标，否则用通用 Linux 图标（VS Code `getWslIcon` 的思路）[实 VSC terminalProfiles.ts:391+]。

### 2.4 profile 的"信任"

`RequiresTrust`（MSYS2、Cygwin）：这些路径位于 `C:\` 根目录下，普通用户就能创建，可能被植入恶意程序 [实 VSC terminalProfiles.ts:117-134 的 unsafe 标记]。首次从这类 profile 启动时弹窗确认，显示完整路径；确认结果写入 `state.json` 的 `terminal.trustedProfiles`（按 `id + 路径` 记录，路径变了要重新确认）[定]。设置文件中显式写出的自定义 profile 视为已信任（用户自己写的）。

### 2.5 探测结果的展示字段

| 字段 | macOS / Linux | Windows |
|---|---|---|
| `Name` | `zsh`、`bash (Homebrew)`（路径在 `/opt/homebrew`、`/usr/local/Cellar` 下时） | `PowerShell`、`PowerShell 7 (Store)`、`Windows PowerShell`、`命令提示符`、`Git Bash`、`Ubuntu-24.04 (WSL)`、`MSYS2`、`Cygwin`、`Developer Command Prompt for VS 2026`、`Developer PowerShell for VS 2026` |
| `Icon` | 按 Kind 选 lobe-icons / 内置图标 | 同左 |
| `Version` | 不探测（避免运行 `--version`） | pwsh 的版本、`Store` / `Scoop` 等形态 |

名字走本地化资源（`strings.tsv`），由 Desktop 根据 id 前缀和 Kind 生成；host 只返回结构化字段 [定]。

### 2.6 耗时与缓存

| 步骤 | 预算 | 说明 |
|---|---|---|
| Unix 冷探测 | < 5 ms | 一次 getpwuid + 读 `/etc/shells` + 十几次 `stat`/`access` |
| Windows 同步部分 | < 150 ms | 约 40 次文件检查 + 2 次注册表读取 + PATH 查找；`FileVersionInfo` 只对 pwsh 候选读取 |
| Windows 异步部分 | vswhere ≤ 3 s 超时；`wsl.exe -l -q` ≤ 2 s 超时 | 后台运行，完成后推送 `profiles/changed`；外部命令一律经 §2.8 的执行器 |

**缓存策略** [定]：

1. host 内存中缓存 `ResolvedProfile[]` 和每个探测器的输入指纹：
   - Unix：`/etc/shells` 的 mtime + 账户 shell 字符串；
   - Windows：Lxss 键的最后写入时间、各候选路径存在与否的位图、PATH 字符串的哈希。
2. **刷新时机**：
   - host 启动；
   - 收到 `profiles/refresh`（设置页的"重新探测"按钮）；
   - `host/configure` 带来新的 profiles 覆盖时（只重新合并，不重新探测）；
   - Desktop 打开"+"菜单时，若缓存超过 30 s，就先返回缓存，同时后台重新探测；结果有变化时推送 `profiles/changed`（stale-while-revalidate）。
3. **磁盘缓存**：每次结果变化时，host 把它写到 `$TKCODE_HOME/cache/terminal-profiles.json`（0600，带 `hostVersion` 字段）。Desktop 在 host 尚未运行时读它来显示菜单，菜单项可以立即点击：点击后再启动 host，host 重新解析 profile id。
4. **AgentHost** 通过 `profiles/list` 读取，不单独缓存。

### 2.7 探测失败时的回退

| 情况 | 行为 |
|---|---|
| 某个探测器抛异常（注册表权限、路径非法） | 记日志，跳过该探测器，其他照常 |
| 列表为空 | Unix：合成一个 `sh` profile（`/bin/sh`）；Windows：合成 `cmd`（`%SystemRoot%\System32\cmd.exe`，没有时用 `%ComSpec%`）。**保证至少有一个 profile** |
| `defaultProfile` 指向不存在或已停用的 id | 设置页显示警告；新终端用 `system` |
| profile 的 `command` 在启动时已经不存在（例如卸载了 pwsh） | 启动失败横幅："找不到 `<路径>`"，提供"改用默认 profile"和"重新探测" |


### 2.8 外部命令的执行：不用 `System.Diagnostics.Process`

pty host 进程内**不使用 `System.Diagnostics.Process` 和 `System.Console`**：使用它们会让 .NET 安装 SIGCHLD 处理器并改动终端设置，与 host 自己的子进程回收冲突（03 §10 R5、04 的实现约束）。探测和启动构造中需要运行的外部命令有：

| 命令 | 平台 | 用途 |
|---|---|---|
| `wsl.exe -l -q` | Windows | WSL 回退探测（§2.3） |
| `vswhere.exe …` | Windows | VS 开发者命令行（§2.3） |
| `/usr/libexec/path_helper -s` | macOS | 非 login profile 的 PATH 修补、短名解析（§2.2、§4.5） |
| `ssh -V`、`ssh -G` | 全部 | 08 §2.1、§2.3 |

这些命令都经 `IShellPlatform.Processes`（§9.1）运行，host 中的实现是 `HelperProcessRunner` [定]：

- **Unix**：用 libtkcodepty 以**管道模式**（stdin 接 `/dev/null`，stdout/stderr 接管道，不分配 PTY）启动 `tkcode-pty-spawn --exec <file> <argv0> <args…>`。helper 复位全部信号后 execvp（03 §3、§12），子进程的回收走 host 自己的退出监视（`waitpid(pid)`，03 §6），不经过 .NET；
- **Windows**：按 04 的约束同样经 `tkcode-pty-spawn --exec`。如果 03 最终不为 Windows 提供 helper（03 §3 的 helper 是 Unix C 程序），则改为在 `TKCode.Terminal.Pty` 中直接用 `CreateProcessW` + 匿名管道 + `STARTUPINFOEX` 的 `PROC_THREAD_ATTRIBUTE_HANDLE_LIST`（只继承这两个管道句柄）+ `CREATE_NO_WINDOW` 实现，同样不使用 `Process`；
- 统一的约束：
  - 超时后杀死子进程（Unix `kill(SIGKILL)`；Windows `TerminateProcess`），并读完管道，避免僵尸进程和句柄泄漏；
  - stdout 上限 1 MiB，超出时截断并视为失败；
  - 环境使用 07 §4 的剔除规则后的 BaseEnvironment（不能把 pty host 的令牌带给外部命令）；
  - 输出按字节返回，由调用方解码（`wsl.exe` 是 UTF-16LE，其他是 UTF-8）。
- 03 目前的 C API 中只有 PTY 模式的 `tcpty_spawn`，**管道模式的启动接口需要 03 补充**（列入 §10）。

---

## 3. 启动命令的构造（LaunchBuilder）

### 3.1 流水线

```
Build(profile, ctx):
  1. 生效配置 = ctx.Config 中该 profile 的覆盖 ⊕ profile 自带字段
  2. 解析可执行文件为绝对路径（§2.2 的搜索顺序；Windows 用 SearchPathW 规则 + PATHEXT）
  3. 计算工作目录（§6.4），校验存在；不存在时回退到项目根目录 → home，并加一条 Warning
  4. 计算环境变量（§4.6 的第 1–7 步）
  5. 决定 shell 集成：IntegrationPlanner(profile.Kind, args, cfg, platform)（§5.3、§5.5）→ 修改 args 与 env（§4.6 第 8 步）
  6. 平台包装：macOS login（§3.2）| Linux argv0（§3.3）| Windows 命令行拼接（§3.4）
  7. 产出 PtyLaunch(File, Arguments, Argv0, WorkingDirectory, Environment, InitialSize, WidthMode) 和 IntegrationPlan
```

`LaunchBuilder` 是纯函数：所有系统访问都经过 `IShellPlatform`，测试时用假实现（§9.1）。

### 3.2 macOS：argv0 形式的 login shell（不经 login(1)）

> 2026-10-17 用户决定：**放弃 `/usr/bin/login` 包装**（原 D9）。原因是 login 父进程总是 `exit(0)`，shell 的退出码会丢失（03 §8.3 已核实 login.c:854-883），"shell 以非 0 退出则保留标签"的行为就无法实现。

**命令形态** [定]：spawn helper 直接 exec 目标 shell，argv0 设为 `-<name>`（例如 `-zsh`），shell 因此作为 login shell 启动。这是 WezTerm 的做法，也就是 bash 的 `exec -l` 的效果。

| 项 | 说明 |
|---|---|
| argv0 | `"-" + Path.GetFileName(shell)`，例如 `-zsh`、`-fish`、`-bash`（与 Terminal.app 的 argv0 相同） |
| PATH | zsh 和 bash 作为 login shell 会读 `/etc/zprofile`、`/etc/profile`，其中调用 `path_helper`，补齐 `/etc/paths` 与 `/etc/paths.d`（包括 Homebrew 写进去的路径）[实 research/05 §1.1]；fish 的 login 启动同样会处理 `/etc/paths`；其余 shell 按 §4.5 修补 |
| 工作目录 | 由 spawn helper 在 exec 前 chdir（03 §3） |
| 与 Terminal.app 的差异 | 没有 utmp/wtmp 记录（`who`、`last` 里看不到这个终端），`getlogin()` 返回的可能不是该终端的会话用户，也不显示 "Last login"。这三点对日常使用基本没有影响，换来的是**准确的退出码**和更简单的进程树 |
| 退出码 | PTY 的直接子进程就是 shell，`waitpid` 得到的就是 shell 的真实状态；不需要 03 的 `NOTE_EXITSTATUS` 方案 |
| 前台进程 / cwd | spawn 返回的 pid 就是 shell，其 pgid 等于 pid；cwd 兜底查询直接用这个 pid |

**按 Kind 区分**：

| Kind | File | argv | 说明 |
|---|---|---|---|
| Zsh、Fish、Nu、Other（sh、ksh、tcsh…） | shell 路径 | `[-<name>, args…]` | 真正的 login shell |
| Bash（注入 shell 集成时） | bash 路径 | `[bash, --init-file, <脚本>, args…]`，并设置 `TKCODE_SI_BASH_LOGIN=1` | argv0 以 `-` 开头时，bash 作为 login shell 会忽略 `--init-file`。所以改为非 login，由脚本模拟 login 的启动文件（VS Code 的方式，兼容 bash 3.2）[实 VSC shellIntegration-bash.sh:30-58] |
| Bash（不注入时） | bash 路径 | `[-bash, args…]` | |
| Pwsh | pwsh 路径 | `[pwsh, -Login, args…]` | pwsh 的 `-Login` **必须是第一个参数**，否则被忽略 [实 learn.microsoft.com about_Pwsh] |
| 非 shell 的自定义命令（`kind: "other"`，例如 `htop`） | 命令路径 | `[name, args…]` | 不加 `-`；PATH 按 §4.5 修补 |

**`loginShell: false`（用户关闭）**：argv0 为普通名字（`zsh`），PATH 按 §4.5 修补。

### 3.3 Linux

- **默认非 login 的交互式 shell**：直接 exec，参数不加 `-l`（GNOME Terminal、Ptyxis、Konsole、VS Code、Ghostty、Alacritty 一致 [实 research/05 §1.2]）。
- **`loginShell: true`**：`PtyLaunch.Argv0 = "-" + basename`（GNOME Terminal 的做法）。例外：bash 注入时改用 `TKCODE_SI_BASH_LOGIN=1`（同 §3.2 的原因）；pwsh 用 `-Login` 作为第一个参数。
- 不经 `/bin/sh -c` 包装（Ghostty 的 `.shell` 形式会包一层 sh [实 GH Exec.zig:2007-2019]）：我们的 profile 本来就是 argv 数组，不需要 shell 解析。

### 3.4 Windows

#### 命令行拼接（CommandLineToArgvW / MSVCRT 规则）

ConPTY 的 `CreateProcessW` 只接受一个字符串。参数数组按下列算法拼接 [实 learn.microsoft.com/cpp/c-language/parsing-c-command-line-arguments；Daniel Colascione 的 "Everyone quotes command line arguments the wrong way"]：

```
Join(file, args):
  // 第一个 token（程序名）的解析规则不同：不处理反斜杠转义，只认引号
  s = file 含空格或制表符 ? "\"" + file + "\"" : file      // file 不得含 '"'（Windows 文件名本来就不允许）
  for a in args: s += " " + QuoteArg(a)
  return s

QuoteArg(a):
  if a != "" and a 不含 { ' ', '\t', '\n', '\v', '"' }: return a
  r = "\""
  i = 0
  while i < len(a):
      n = 0
      while i < len(a) and a[i] == '\\': n++; i++
      if i == len(a):  r += '\\' × (2n); break            // 结尾的反斜杠要加倍，否则会转义收尾的引号
      if a[i] == '"':  r += '\\' × (2n + 1) + '"'
      else:            r += '\\' × n + a[i]
      i++
  return r + "\""
```

- `lpApplicationName` 传绝对路径，避免 `CreateProcess` 按空格逐段猜测可执行文件 [定]。
- **cmd.exe 与 `.bat`/`.cmd` 不按这套规则解析**：cmd 自己处理 `/c`、`/k` 后面的引号，并解释 `& | < > ^ %` 等元字符。CreateProcess 启动 `.bat` 时也会隐式经过 cmd（BatBadBut，CVE-2024-24576）[实]。因此：
  - 内置的 cmd 类 profile 使用 `RawCommandLine`，原样传入，不再拼接；
  - 用户自定义的 profile 可以写 `"commandLine": "…"`（仅 Windows）来提供原始命令行；
  - **来自 Agent 或 ACP 的参数**（09）如果目标是 cmd/.bat/.cmd，参数中出现 `& | < > ^ % ! " \r \n` 时拒绝启动，不尝试转义 [定]。
- **pwsh 的 `-Command` 字符串**：PowerShell 单引号字符串中，`'` 写成 `''`。我们构造的是 `-Command`，其中的路径用 `'<path 中的 ' 替换为 ''>'` 包裹，再整体用 `QuoteArg` 拼接。

#### WSL 的 `--cd`

| 起始目录来源 | 参数 | 说明 |
|---|---|---|
| 默认（没有项目） | `--cd ~` | WT 在 build ≥ 19041 时用 `~` [实 WT WslDistroGenerator.cpp:35-46、70-77] |
| 项目根目录（Windows 路径） | `--cd "C:\work\proj"` | wsl.exe 会转换为 `/mnt/c/work/proj` [推] |
| 从 WSL 标签继承（OSC 7 给出 Linux 路径，§6.4） | `--cd /home/u/src` | 要求 build ≥ 19041 |
| 用户在 profile 中写了 `cwd` | 原样传给 `--cd` | |

`lpCurrentDirectory` 同时设为一个存在的 Windows 目录（项目根目录或 `%USERPROFILE%`）。UNC 路径（`\\wsl.localhost\…`）不能作为 `lpCurrentDirectory`，否则 CreateProcess 会失败 [推]。

#### Git Bash / MSYS2 / Cygwin

- 注入脚本的路径用正斜杠形式：`C:/Users/u/.tkcode/shell-integration/…/tkcode.bash`。MSYS 运行时能识别这种形式 [推；VS Code 直接传 Windows 路径，同样可用]。
- `MSYSTEM`、`CHERE_INVOKING` 来自 profile 的 `Environment`，用户可以覆盖。
- Git Bash 的 `bin\bash.exe` 是一个包装程序，它会设置 `MSYSTEM=MINGW64` 和 PATH，然后启动 `usr\bin\bash.exe` [推]。所以不要直接用 `usr\bin\bash.exe`（只有 `bin\` 不存在时才用）。

### 3.5 用户参数与注入的兼容性

用户在 profile 里写了参数时，只有能确认注入安全，才会注入（VS Code 的做法，否则返回 `UnsupportedArgs`）[实 VSC terminalEnvironment.ts:330-380]：

| Shell | 允许注入的参数（不区分大小写） | 这些参数会阻止注入 |
|---|---|---|
| bash | 空；`-l`/`--login`（转为模拟 login）；`-i`/`--interactive`；`--noprofile`（模拟 login 时跳过 profile 文件） | `-c`、`--rcfile`/`--init-file`、`--norc`、`--posix`、`-s`、脚本文件 |
| zsh | 空；`-l`/`--login`；`-i` | `-c`、`-f`/`--no-rcs`、`-d`/`--no-globalrcs`、`-o …`、脚本文件 |
| fish | 空；`-l`/`--login`；`-i`；`--private`/`-P` | `-c`、`--no-config`/`-N`、`-C`/`--init-command` 已存在时仍可注入（fish 允许多个 `-C`）[推] |
| pwsh / powershell | 空；`-NoLogo`/`-nol`；`-Login`/`-l`；`-NoProfileLoadTime`；`-WorkingDirectory <p>` | `-Command`、`-c`、`-File`、`-f`、`-EncodedCommand`、`-NonInteractive`、`-NoExit`（已有）、`-ConfigurationName` |
| nu | 不注入（§5.3.5） | — |

被阻止时 `IntegrationPlan.Mode = None`，`Reason = UnsupportedArgs`，并在 UI 中说明（§5.5）。

---

## 4. 环境变量策略

### 4.1 基础环境从哪里来

- **问题**：pty host 是一个长期运行的进程，可以跨越 Desktop 的多次重启（01 §3.3 的 keepAlive、UI 崩溃重连）。如果用 host 自己的环境，那么用户换了一个环境启动 TKCode（例如从带 `nvm` 的终端里 `open -a TKCode`），新终端仍然会用第一次启动 host 时的环境。
- **决定** [定]：
  - Desktop 在 `Program.Main` 一开始（任何代码修改环境之前）抓取 `Environment.GetEnvironmentVariables()` 作为 **BaseEnvironment**，在 `host/configure` 中发给 host；
  - host 按客户端保存一份，`session/create` 时使用发起请求的那个 Desktop 的 BaseEnvironment；
  - AgentHost 发起的会话使用最近一次连接的 Desktop 的 BaseEnvironment（AgentHost 本来就是 Desktop 启动的，它自己的环境里还有 TKCode 内部变量）。
- **Windows 的环境刷新**（`reloadEnvironment`，默认 true）：在 BaseEnvironment 的基础上，用注册表中的值替换同名变量：
  - 读取 `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment`、`HKCU\Environment`、`HKCU\Volatile Environment`；
  - `REG_EXPAND_SZ` 用 `ExpandEnvironmentStringsW` 展开；
  - `Path` 按"系统;用户"拼接，其他变量用户值覆盖系统值；
  - 注册表中没有的变量（父进程临时设置的）保留。

  效果：用户安装软件修改 PATH 之后，新终端不需要重启 TKCode 就能看到。来源：WT `compatibility.reloadEnvironmentVariables` [实 WT ConptyConnection.cpp:296-316；src/inc/til/env.h:237-270、446-476]。WT 是否保留"注册表中没有的变量"[推]。

### 4.2 设置的变量

| 变量 | 值 | 平台 | 依据 |
|---|---|---|---|
| `TERM` | `xterm-256color`（`terminal.term` 可改） | macOS、Linux；Windows 上只对 Git Bash / MSYS2 / Cygwin 设置（MSYS 程序依赖 TERM）[推] | D9；research/03 §7.3 |
| `COLORTERM` | `truecolor` | 全部 | research/05 §2.3 |
| `TERM_PROGRAM` | `TKCode` | 全部。**不要**设为 `Apple_Terminal`，否则会触发 `/etc/zshrc_Apple_Terminal` 的会话保存逻辑 | research/05 §2.3；与 XTVERSION 的名称一致（research/03 §7.3） |
| `TERM_PROGRAM_VERSION` | Desktop 的语义化版本号，例如 `0.9.0` | 全部 | 同上 |
| `TKCODE_TERMINAL_ID` | 会话 uuid（与 pty host 的会话 id 一一对应，但不是 IPC 凭据） | 全部 | [定] 类似 `WT_SESSION`、`WEZTERM_PANE`。供以后的 `tkcode` CLI 或 Agent 定位"当前终端"；不含任何秘密。09 可以决定不要它 |
| `SHELL` | Unix：passwd 中的账户 shell（不是当前 profile 的 shell） | macOS 上 login(1) 本来就会这样设置；Linux 上顺便修正 chsh 之后的过期值 | LOGIN:954-955 [实]；WZ env-bootstrap/src/lib.rs:236-240 [实] |
| `LANG`（或 `LC_CTYPE`） | §4.4 | macOS | |
| `WSLENV` | §4.7 | Windows，Kind=Wsl | |
| 注入用的临时变量 `TKCODE_SI_*` | §5.3 | 注入时 | 脚本读取后立即 unset |

**不设置**：

- `TKCODE_AGENT`：这是 `BashTool` 给 Agent 命令设置的标记（src/TKCode.Agent.Coding/Tools/Shell/BashTool.cs）[实 research/05 §2.3]，交互终端不能带。Agent 创建的可见终端是否设置，由 09 通过 `LaunchContext.ExtraEnvironment` 决定。
- `COLUMNS`、`LINES`：程序应当通过 `TIOCGWINSZ` 取尺寸。

### 4.3 剔除的变量

按以下顺序检查，命中即剔除（名称比较：Unix 区分大小写，Windows 不区分）[定，汇总自 research/05 §2.2–2.3]：

| 类别 | 按名称 | 按前缀 |
|---|---|---|
| TKCode 内部 | — | `TKCODE_`（保留 `TKCODE_HOME`，因为它是面向用户的配置）。**特别注意**：01 §3.3 规定 AgentHost 通过环境变量拿到 pty host 的连接地址和令牌（例如 `TKCODE_PTYHOST_*`），这些变量**必须**不能进入用户 shell，否则 shell 里的任何程序都能连上 pty host 并操作所有终端 |
| 其他终端遗留 | `COLORFGBG`、`COLORTERM`、`TERM`、`TERMCAP`、`TERMINFO`、`DEFAULT_COLORS`、`LC_TERMINAL`、`LC_TERMINAL_VERSION`、`MLTERM`、`STY`、`TMUX`、`TMUX_PANE`、`VTE_VERSION`、`WINDOWID`、`WT_SESSION`、`WT_PROFILE_ID`、`WCWIDTH_CJK_LEGACY`、`TERM_SESSION_ID`、`TERM_FEATURES`、`KITTY_WINDOW_ID`、`KITTY_PID`、`KITTY_PUBLIC_KEY`、`KITTY_INSTALLATION_DIR` | `ALACRITTY_`、`CONTOUR_`、`FOOT_`、`GHOSTTY_`、`GNOME_TERMINAL_`、`ITERM_`、`ITERM2_`、`KITTY_`、`KONSOLE_`、`MC_`、`MINTTY_`、`MOSH_`、`PUTTY_`、`RXVT_`、`TERM_PROGRAM`（含 `_VERSION`，随后重新设置）、`TERMINAL_`、`URXVT_`、`VSCODE_`、`WEZTERM_`、`XTERM_`、`ZUTTY_`、`ELECTRON_` |
| 会话层级 | `SHLVL`、`PWD`、`OLDPWD`、`_`（bash 的上一条命令） | — |
| Linux 桌面与 systemd | `DESKTOP_STARTUP_ID`、`XDG_ACTIVATION_TOKEN`、`GIO_LAUNCHED_DESKTOP_FILE`、`GIO_LAUNCHED_DESKTOP_FILE_PID`、`INVOCATION_ID`、`JOURNAL_STREAM`、`NOTIFY_SOCKET`、`MANAGERPID`、`MAINPID`、`PIDFILE`、`EXIT_CODE`、`EXIT_STATUS`、`SERVICE_RESULT` | `LISTEN_`、`WATCHDOG_`、`DBUS_STARTER_`、`GDK_DEBUG`、`GSK_` |
| Snap / AppImage | `APPIMAGE`、`APPDIR`、`OWD`、`ARGV0` | `SNAP` |
| macOS 应用身份 | `__CFBundleIdentifier`、`XPC_SERVICE_NAME`、`XPC_FLAGS` | `__XPC_` |
| 开发期启动器 | `DOTNET_LAUNCH_PROFILE`、`DOTNET_MODIFIABLE_ASSEMBLIES`、`DOTNET_STARTUP_HOOKS`、`COR_ENABLE_PROFILING` | `DOTNET_WATCH`、`MSBuild`、`MSBUILD`、`CORECLR_`、`COR_PROFILER` |

说明：

- **`DOTNET_*` 不一刀切**：NativeAOT 程序没有宿主，不会自己写入 `DOTNET_*`；出现这些变量，只能是用户或启动工具设置的。所以只剔除开发期注入的那几个，`DOTNET_ROOT`、`DOTNET_CLI_TELEMETRY_OPTOUT` 等保留 [实 research/05 §2.3]。
- **`TERMINFO_DIRS` 保留**（NixOS、Homebrew ncurses 用户可能有意设置），只有当它指向其他终端的资源目录（路径中含 `Ghostty.app`、`kitty.app`、`/kitty/terminfo`、`WezTerm`）时才去掉那一段 [定]。
- **用户在 `terminal.env` 里显式写出的变量不受剔除影响**：合并时剔除在前，用户设置在后（§4.6）。

### 4.4 macOS 的 LANG

- **为什么要补**：从 Dock 启动的程序没有 `LANG` [实 research/05 §1.1]。macOS 26 的 `/etc/zprofile` 会在 `LANG` 为空时设为 `C.UTF-8` [实 local]，但只有 zsh login shell 会读它，bash 和 fish 不会；而且 `C.UTF-8` 会让程序的消息、日期格式都退回英文和 C 习惯。
- **规则**（`setLocaleVariables`，默认 `auto`）：

```
FixLocale(env):
  if setLocaleVariables == off: return
  if setLocaleVariables == auto and env.LANG 存在 and IsUtf8(env.LANG) and LocaleExists(env.LANG): return
  // 按系统区域，不按 TKCode 界面语言（research/05 决策 8）
  CFPreferencesAppSynchronize(kCFPreferencesAnyApplication)          // host 是长期进程，每次重新读取，避免缓存过期
  langs  = CFPreferencesCopyAppValue("AppleLanguages")   // ["zh-Hans-CN", "en-CN"]
  locale = CFPreferencesCopyAppValue("AppleLocale")      // "zh_CN" 或 "en_US@rg=cnzzzz"
  country = 先取 "@rg=" 中的地区（rg=cnzzzz → CN），否则取 locale 中 "_" 后面的部分
  candidates = []
  for l in langs, 直到并包括第一个两字母代码:            // iTerm2 的取法
      lang = l 去掉第一个 "-" 之后的部分（zh-Hans-CN → zh）
      candidates += lang + "_" + country + ".UTF-8"
  candidates += locale 去掉 "@…" 部分 + ".UTF-8"
  for c in candidates: if LocaleExists(c): env.LANG = c; return
  env.LC_CTYPE = "UTF-8"                                 // macOS 上 "UTF-8" 是有效的 LC_CTYPE 值
LocaleExists(name) = Directory.Exists("/usr/share/locale/" + name)
IsUtf8(v) = v 以 ".UTF-8" 或 ".utf8"（不区分大小写）结尾
```

  来源：iTerm2 `iTermLocaleGuesser`（候选列表与验证）[实 IT sources/Locale/iTermLocaleGuesser.swift:87-230]；Terminal.app 的 "Set locale environment variables on startup" [实 research/05 §2.1]。CoreFoundation 的调用用 `LibraryImport` 声明在 `TKCode.Terminal.Shell` 的 macOS 部分，不依赖 AppKit [定]。
- `on`：总是按上述规则覆盖 `LANG`。Linux 和 Windows 不处理 locale。

### 4.5 GUI 启动时 PATH 缺失

| 场景 | 处理 |
|---|---|
| macOS + login（默认） | login shell 的 `/etc/zprofile`（path_helper）和 `~/.zprofile` 会补全 PATH，**不做任何处理**。这与 Terminal.app 一致，也避免了 VS Code 遇到的 path_helper 重排问题 [实 research/05 §1.5] |
| macOS + `loginShell: false` | 启动前把 `path_helper -s` 的结果（`/etc/paths` + `/etc/paths.d/*`）与 BaseEnvironment 的 PATH 合并去重，前者在前 [定]。`~/.zprofile` 中的 Homebrew `shellenv` 不会执行，设置页在该开关旁注明 |
| macOS + 用户在 `terminal.env` 中设置了 PATH（例如前置 `~/bin`） | path_helper 会把原有 PATH 条目挪到后面，用户的前置项因此失效（VS Code issue #99878 的问题 [实 VSC terminalEnvironment.ts:279-313]）。如果用户值以 `{env:PATH}` 结尾，就把前缀部分放进 `TKCODE_SI_PATH_PREFIX`，由注入脚本在 login 文件执行完后重新前置；没有注入时给出 `LaunchWarning` |
| Linux | 桌面会话的 PATH 一般已经完整（显示管理器会 source `~/.profile`、systemd user 环境）。不处理 |
| Windows | `reloadEnvironment`（§4.1） |

### 4.6 合并顺序

```
env = BaseEnvironment                                      // 1. §4.1（Windows：再叠加注册表刷新）
env.RemoveWhere(StripRules)                                // 2. §4.3
FixLocale(env)                                             // 3. §4.4（macOS）
env.Set(TERM, COLORTERM, TERM_PROGRAM, TERM_PROGRAM_VERSION, TKCODE_TERMINAL_ID, SHELL)   // 4. §4.2
ApplyUserEnv(env, config.env)                              // 5. 全局 terminal.env（已经过平台覆盖块合并）
ApplyUserEnv(env, profile.Environment ⊕ profile 覆盖的 env) // 6. profile 级
ApplyUserEnv(env, ctx.ExtraEnvironment)                    // 7. 调用方（Agent / ACP / 恢复）
integration.Apply(env, args)                               // 8. 注入变量：读取第 7 步之后的 ZDOTDIR / XDG_DATA_DIRS 原值
WslEnv.Apply(env, profile)                                 // 9. §4.7
PathFixups(env)                                            // 10. §4.5（macOS 非 login）

ApplyUserEnv(env, map):
  for (k, v) in map:
     if v == null: env.Remove(k)                           // null = 删除（WT 做不到删除，VS Code 可以）
     else: env[k] = Expand(v, env: BaseEnvironment, projectRoot: ctx.ProjectRoot)
```

- **展开语法**与 `McpVariables` 一致（`{env:VAR}`、`${env:VAR}`、`${VAR}`、`${VAR:-default}`、整串 `$VAR`、`${workspaceFolder}`、`${userHome}`），实现位于 `TKCode.Terminal.Shell.EnvExpander` [定]。
  - 展开时**对照 BaseEnvironment**，而不是对照前几步修改后的环境，避免出现"用户值引用了被剔除的变量"这类不直观的行为；
  - 唯一的例外：写 `"PATH": "~/bin:{env:PATH}"` 时，`{env:PATH}` 取第 1 步的值（Windows 上是刷新后的值）。
- **大小写**：Windows 上键不区分大小写（`Path` 与 `PATH` 是同一个变量），合并时保留第一次出现的写法；Unix 区分大小写。
- **保护**：用户设置不能覆盖第 8 步写入的注入变量（第 8 步在后）；但用户可以通过 `env` 改写 TERM 等第 4 步的值。用户改写 TERM 后，设置页提示"非 xterm-256color 的 TERM 需要对应的 terminfo"。

### 4.7 WSLENV（Windows，Kind=Wsl）

WSL 只把 `WSLENV` 中列出的变量从 Windows 传进 Linux。在原值前面追加 `TERM_PROGRAM:TERM_PROGRAM_VERSION:COLORTERM:TKCODE_TERMINAL_ID`，再加上用户 `env` 中出现的键（不带标志位，按字符串原样传递）[实 WT ConptyConnection.cpp:58-151、WZ config.rs:1606-1622 的做法]。用户需要路径转换时，可以在 `WSLENV` 中自己写 `/p` 等标志。

---

## 5. Shell 集成

### 5.1 发出的序列

| 时机 | 序列 | 用途 | 来源 |
|---|---|---|---|
| 脚本加载完成（一次） | `OSC 633 ; P ; TKCodeShellIntegration=<脚本版本>,<shell>,<shell 版本> ST` | **握手**：表明是 TKCode 自己的脚本在工作，用来区分"注入成功"和"用户另外装了别的集成"（§5.5）。633;P 的未知属性会被其他终端忽略 | [定]；633;P 的格式 [实 research/03 §2.4] |
| 每次提示符之前 | `OSC 133 ; D ; <退出码> ST` | 上一条命令结束。只有之前发过 C 才发 D（第一个提示符不发） | research/03 §2.4 |
| 同上 | `OSC 7 ; file://<hostname><百分号编码的路径> ST` | 当前目录。Windows 上路径写成 `/C:/Users/…` | |
| 同上 | `OSC 133 ; A ; aid=<shell pid> ; cl=line ST` | 提示符开始。`aid` 用于区分嵌套 shell；`cl=line` 声明单行编辑器支持左右方向键（Alt+单击移动光标时使用，Tier 2） | Per Bothner 的 semantic prompt 规范 [实 research/03 §2.4] |
| 提示符结束、输入开始 | `OSC 133 ; B ST` | | |
| 命令执行前 | `OSC 633 ; E ; <转义后的命令行> ; <nonce> ST` | 精确的命令行文本。转义规则同 VS Code：`\` → `\\`，`;` 和 0x00–0x1F 写成 `\xHH` | VSC shellIntegration-bash.sh:151-190、shellIntegration.ps1:95-104 [实] |
| 同上，紧随其后 | `OSC 133 ; C ST` | 命令输出开始 | |
| 续行提示符（PS2，可选） | `OSC 133 ; P ; k=c ST` | 续行提示符，命令块高亮时排除 | |

终止符统一用 `ESC \`（ST），pwsh 5.1 和 cmd 用 BEL（`\a`），解析两种都接受 [定]。

**为什么既要 133 又要 633;E**：

- OSC 133 由镜像核心（libghostty-vt）直接解析，生成 `TerminalCore.Commands`（01 §6.1），用于命令边界、退出码、命令导航、选择命令输出；
- 133 只给出边界，**拿不到可信的命令行文本**。从屏幕上抠 B 到 C 之间的文本并不可靠：
  - 提示符可能有多行、右侧提示符；
  - 长命令会软换行；
  - zsh-autosuggestions 和 fish 的灰色建议文本也在这一区域里，会被当成命令；
  - 语法高亮插件会重绘输入行。
- **Agent 需要准确的命令行**（09）：
  - 读取"用户刚才在终端里运行了什么"作为上下文；
  - 在共享终端里执行命令时，核对 shell 实际执行的是不是 Agent 发出的那条（中间可能被用户编辑，或者被别名展开）；
  - 命令历史 UI、"重新运行"。
- **nonce 防伪造**：程序输出可以伪造 `633;E`，诱导用户或 Agent "重新运行"一条恶意命令。所以 host 只在 nonce 与本会话一致时，才把 E 的内容记为可信命令行；不一致时只当作"未知命令"（VS Code `VSCODE_NONCE` 的做法）[实 VSC terminalEnvironment.ts:86-88；research/03 §2.4]。
  - nonce 是每个会话 128 位的随机数，经 `TKCODE_SI_NONCE` 传给脚本；
  - 脚本存进非导出的 shell 变量后立即 unset，子进程看不到；
  - 运行在同一个 shell 进程里的代码（用户的 rc、插件）仍然可以读到它，这一点无法避免（VS Code 的 ps1 脚本也这样说明 [实 VSC shellIntegration.ps1:26-31]）。
- 命令行文本**只用于显示和 Agent 上下文**，不会自动执行，也不会写入 shell 历史（research/03 §2.4）。

### 5.2 解析侧：633 由 host 自己处理

libghostty 的 OSC 解析器有 `semantic_prompt`（133，含 kitty 的 `cmdline=`/`cmdline_url=` 选项），但**没有 633** [实 GH src/terminal/osc/parsers/ 目录中没有 633 的解析器]。方案 [定]：

- libghostty-vt 的 C API 会把不认识的序列经 `GHOSTTY_TERMINAL_OPT_UNKNOWN_SEQUENCE` 回调交出原文，133 则经 `GHOSTTY_TERMINAL_OPT_SEMANTIC_PROMPT` 回调给出 [实，09 §3 引用 ghostty `include/ghostty/vt/terminal.h:104,144-157,1405-1500`]。因此镜像核心的命令跟踪器 = 133 回调 + 在未知序列回调中解析 633（E、P），两者在同一次 `vt_write` 中按字节流顺序到达，天然有序；
- 本节只规定 **shell 侧发出什么**；解析与命令记录的结构以 09 为准；
- 备选（回调不可用时）：会话 actor 在 `vt_write` 之前用一个小状态机扫描 `ESC ] 633 ;`（向量化 `IndexOf(0x1B)` 跳过普通字节）。

### 5.3 各 shell 的注入机制

| Shell | 机制 | 改了什么 | 依据 |
|---|---|---|---|
| bash（含 macOS `/bin/bash` 3.2、Git Bash、MSYS2、Cygwin） | `--init-file <ver>/bash/tkcode.bash`；login 时设 `TKCODE_SI_BASH_LOGIN=1`，由脚本模拟 login 启动文件 | 只改参数 | VS Code [实 VSC terminalEnvironment.ts:335]；**不用** Ghostty 的 `--posix` + `ENV`：Apple 的 bash 3.2 禁用了这条路径，Ghostty 和 iTerm2 都直接跳过 `/bin/bash` [实 GH shell_integration.zig:144-158]；POSIX 模式下 HISTFILE 默认是 `~/.sh_history`，还要额外修正 [实 GH shell_integration.zig:391-406] |
| zsh | `ZDOTDIR=<ver>/zsh`（目录中只有 `.zshenv`）；用户原来的 ZDOTDIR 存入 `TKCODE_SI_ZDOTDIR` | 只改环境 | Ghostty 的单 `.zshenv` 方案 [实 GH src/shell-integration/zsh/.zshenv]。VS Code 复制 4 个文件到 `/tmp/<user>-<app>-zsh`（1700 权限）[实 VSC terminalEnvironment.ts:206-273]，需要处理临时目录被篡改的问题，不采用 |
| fish | 在 `XDG_DATA_DIRS` 前面加上 `<ver>/fish-data`，fish 启动时加载其中的 `fish/vendor_conf.d/tkcode.fish`；`TKCODE_SI_XDG_DIR` 记录要去掉的那一项，`TKCODE_SI_XDG_UNSET=1` 表示原来没有设置 | 只改环境 | Ghostty [实 GH shell_integration.zig:620-690；fish/vendor_conf.d]；VS Code 用 `--init-command`（改参数）[实 VSC terminalEnvironment.ts:336] |
| pwsh / Windows PowerShell | `-NoExit -Command "<加载脚本>"` | 只改参数 | VS Code [实 VSC terminalEnvironment.ts:330-332] |
| nu | 不注入，依赖 nu 自带的 OSC 133 / OSC 7 | — | §5.3.5 |
| cmd | 包装 `PROMPT` 环境变量（部分集成：A、B、无退出码的 D、OSC 9;9 cwd，没有 C 和 E） | 只改环境 | WT 的 shell 集成教程 [实 learn.microsoft.com/windows/terminal/tutorials/shell-integration] |
| WSL | v1 不注入 | — | §5.3.7 |
| ssh 等其他 | 不注入 | — | §5.8 |

#### 5.3.1 bash：`tkcode.bash` 大纲

```bash
# tkcode.bash — TKCode shell integration for bash (≥ 3.2). Auto-loaded via --init-file or sourced manually.
[[ $- == *i* ]] || return 0                          # 只处理交互式 shell
[[ -n ${__tkcode_si_loaded-} ]] && return 0           # 防止重复加载（手动 source + 自动注入）
__tkcode_si_loaded=1

# 1. 取出注入变量并清除（不导出给子进程）
__tkcode_nonce=${TKCODE_SI_NONCE-}; builtin unset TKCODE_SI_NONCE
if [[ ${TKCODE_SI_INJECTED-} == 1 ]]; then             # 由 --init-file 自动注入：我们负责读取启动文件
  builtin unset TKCODE_SI_INJECTED
  if [[ -n ${TKCODE_SI_BASH_LOGIN-} ]]; then           # 模拟 login：/etc/profile + 第一个存在的个人 profile
    builtin unset TKCODE_SI_BASH_LOGIN
    [[ -r /etc/profile ]] && builtin source /etc/profile
    for __tkcode_f in ~/.bash_profile ~/.bash_login ~/.profile; do
      [[ -r $__tkcode_f ]] && { builtin source "$__tkcode_f"; break; }
    done
    if [[ -n ${TKCODE_SI_PATH_PREFIX-} ]]; then PATH=$TKCODE_SI_PATH_PREFIX$PATH; fi
    builtin unset TKCODE_SI_PATH_PREFIX __tkcode_f
  else
    [[ -r ~/.bashrc ]] && builtin source ~/.bashrc
  fi
fi

# 2. 工具函数
__tkcode_osc() { builtin printf '\e]%s\e\\' "$1"; }
__tkcode_escape() { …逐字节：\ → \\，; 和 < 0x20 → \xHH；长度 ≥ 2000 时用 sed 的快速路径（VS Code 的做法）… }
__tkcode_osc7()  { …把 $PWD 百分号编码，主机名取 ${HOSTNAME}… }

# 3. preexec：DEBUG trap（bash 3.2 没有 PS0；PS0 的 $(…) 在子 shell 中执行，无法记录状态）
__tkcode_executing=0
__tkcode_in_prompt=0
__tkcode_preexec() {
  (( __tkcode_in_prompt )) && return                   # PROMPT_COMMAND 自身触发的 DEBUG 不算
  (( __tkcode_executing )) && return                   # 一行中的多条命令只报告一次
  __tkcode_executing=1
  local cmd
  cmd=$(builtin history 1 2>/dev/null)                # 优先用历史：得到完整的一行
  if [[ $cmd == "$__tkcode_last_hist" || -z $cmd ]]; then cmd=$BASH_COMMAND   # ignorespace/ignoredups 时历史不变，退回当前简单命令
  else __tkcode_last_hist=$cmd; cmd=${cmd#*[0-9]  }; fi
  __tkcode_osc "633;E;$(__tkcode_escape "$cmd");$__tkcode_nonce"
  __tkcode_osc "133;C"
}
# 与用户已有的 DEBUG trap 串联：先读出原 trap（VS Code 的 __vsc_get_trap 思路），在我们的函数之后执行它

# 4. precmd：放在 PROMPT_COMMAND 的最前面，先保存 $?
__tkcode_precmd() {
  local s=$?
  __tkcode_in_prompt=1
  if (( __tkcode_executing )); then __tkcode_osc "133;D;$s"; __tkcode_executing=0; fi
  __tkcode_osc7
  return $s                                           # 把退出码原样留给用户的 PROMPT_COMMAND
}
__tkcode_precmd_end() {                                # 放在 PROMPT_COMMAND 的最后：此时 PS1 已被 starship 等改写完毕
  __tkcode_wrap_ps1                                    # 若 PS1 不以我们的标记开头，就包上 \[\e]133;A;aid=$$;cl=line\e\\\] … \[\e]133;B\e\\\]
  __tkcode_in_prompt=0
}
# PROMPT_COMMAND：bash ≥ 5.1 支持数组形式，按数组插入首尾；更早的版本按字符串拼接 "__tkcode_precmd; <原值>; __tkcode_precmd_end"

# 5. 握手
__tkcode_osc "633;P;TKCodeShellIntegration=<ver>,bash,${BASH_VERSION}"
```

#### 5.3.2 zsh：`.zshenv` + `tkcode-integration.zsh`

```zsh
# <ver>/zsh/.zshenv —— 由于 ZDOTDIR 指向这里，zsh 在读取 /etc/zshenv 之后读取本文件
# 1. 立即还原 ZDOTDIR，之后的 .zprofile/.zshrc/.zlogin 都从用户自己的目录读取（HISTFILE 默认位置也就不受影响）
if [[ -n ${TKCODE_SI_ZDOTDIR+X} ]]; then 'builtin' 'export' ZDOTDIR=$TKCODE_SI_ZDOTDIR; 'builtin' 'unset' TKCODE_SI_ZDOTDIR
else 'builtin' 'unset' ZDOTDIR; fi
# 2. 加载用户自己的 .zshenv（它可能设置 fpath 等）
'builtin' 'typeset' __tkcode_f=${ZDOTDIR-$HOME}/.zshenv
[[ -r $__tkcode_f ]] && 'builtin' 'source' -- "$__tkcode_f"
# 3. 交互式 shell 才加载集成；要求 zsh ≥ 5.1，否则发出 633;P;TKCodeShellIntegrationError=ZshTooOld
if [[ -o interactive ]]; then 'builtin' 'source' -- "${${(%):-%x}:A:h}/tkcode-integration.zsh"; fi
'builtin' 'unset' __tkcode_f
```

- 所有内建命令都加引号，防止用户把 `source`、`unset` 等定义成别名（Ghostty 的 `.zshenv` 也这样做 [实]）。这里只借鉴机制，代码自己写，因为 Ghostty 的文件是 GPLv3（§5.9）。
- `tkcode-integration.zsh`：
  - 用 `add-zsh-hook precmd/preexec`；
  - `preexec` 的 `$1` 就是完整的命令行，所以 E 是精确的；
  - 第一次 precmd 时，把我们的 precmd 挪到 `precmd_functions` 末尾、preexec 挪到 `preexec_functions` 开头，保证 starship、p10k 等先设置好提示符；
  - A、B 用 `PS1=%{\e]133;A…\e\\%}$PS1%{\e]133;B\e\\%}` 包裹，每次 precmd 检查后重新包裹；
  - 第一次 precmd 时，如果有 `TKCODE_SI_PATH_PREFIX` 就前置到 PATH 并 unset（此时 `/etc/zprofile` 的 path_helper 已经执行过）。
- **已知限制**：如果 `/etc/zshenv` 自己设置了 ZDOTDIR，我们的 `.zshenv` 根本不会被读取 [实 GH src/shell-integration/README.md 的 Zsh 一节]。表现为注入超时；§5.5 对 zsh 超时给出这个提示。

#### 5.3.3 fish：`vendor_conf.d/tkcode.fish`

```fish
# 1. 无论是否交互，先还原 XDG_DATA_DIRS（只去掉我们加的那一项；原来没有设置就整体删除）
# 2. 不是交互式就结束
# 3. 用 --on-event fish_preexec（$argv[1] 是完整命令行）发 E 和 C；--on-event fish_postexec 记下 $status
# 4. 第一次 fish_prompt 事件时（此时用户 config.fish 已经执行完）：把原 fish_prompt 改名为 __tkcode_orig_fish_prompt，
#    定义新的 fish_prompt：先发 D、OSC 7、A，再调用原提示符，最后发 B
# 5. fish ≥ 4.0 自己会发 OSC 133 [推，需实测]：检测到 $version ≥ 4 时跳过 A/B/C/D，只发 E、OSC 7 和握手，避免重复
```

fish 的 `vendor_conf.d` 在用户 `config.fish` **之前**加载，所以还原 `XDG_DATA_DIRS` 发生在用户配置之前，用户配置看到的是原始值 [实 GH fish/vendor_conf.d 脚本的注释与结构]。fish 从哪个版本开始从 `XDG_DATA_DIRS` 读取 `vendor_conf.d` 需要确认 [推：3.x]，太旧时表现为注入超时。

#### 5.3.4 pwsh 与 Windows PowerShell：`tkcode.ps1`

**加载命令**（`-Command` 的内容）：

```powershell
# pwsh 7（Windows / macOS / Linux）
. '<ver>/pwsh/tkcode.ps1'
# Windows PowerShell 5.1：执行策略可能阻止加载，失败时报告原因，不能让 shell 启动失败
try { . '<ver>\pwsh\tkcode.ps1' } catch { [Console]::Write([char]27 + ']633;P;TKCodeShellIntegrationError=' + $_.FullyQualifiedErrorId + [char]7) }
```

- 参数：`[-Login（仅 macOS/Linux 的 login，必须第一个）] [用户的 -NoLogo 等] -NoExit -Command <上面的字符串>`。`-Command` 必须是最后一个参数 [实 about_Pwsh]。
- **profile 照常加载**：PowerShell 先加载 `$PROFILE`，再执行 `-Command`，然后 `-NoExit` 进入交互 [实 about_Pwsh：-NoProfile 才会跳过 profile]。所以我们的脚本在 oh-my-posh 等设置好 `prompt` 函数之后才包装它，正好是我们需要的顺序。
- **执行策略**：
  - Windows 客户端版本上，Windows PowerShell 5.1 的默认执行策略是 `Restricted`，dot-source 脚本文件会失败 [推：Windows 客户端版默认值]；pwsh 7 在 Windows 上默认是 `RemoteSigned`，本地文件可以运行。
  - **不绕过执行策略**：例如把脚本内容读出来用 `Invoke-Expression` 执行，在技术上可行，但企业可能用组策略设置了 AllSigned，绕过它等于违反管理员的意图 [定]。
  - 失败时通过上面的 `catch` 报告 `TKCodeShellIntegrationError=…`，UI 显示"执行策略阻止了 shell 集成脚本"，并给出说明链接。
  - 以后可以给脚本做 Authenticode 签名，让 AllSigned 环境也能加载（Restricted 仍然不行）。
- **脚本内容**（参考 VS Code 的 MIT 脚本改编）：
  - 受限语言模式（`$ExecutionContext.SessionState.LanguageMode -ne 'FullLanguage'`）下直接返回并报告原因；
  - nonce 存进 `$Global:__TKCodeState` 后清除 `$env:TKCODE_SI_NONCE`；
  - 包装 `prompt` 函数：先发 D（`$?` 与 `$LASTEXITCODE` 合成退出码，同 VS Code 的 `FakeCode` 逻辑），再发 OSC 7（只在 `$pwd.Provider.Name -eq 'FileSystem'` 时），然后 A + 原提示符 + B；
  - 包装 `PSConsoleHostReadLine`：拿到用户输入的命令行后发 E 和 C，再把命令行返回给宿主（VS Code 的做法），不改用户的 Enter 键绑定；
  - 5.1 不支持 `` `e ``，统一用 `[char]27`。
- **VS DevShell**（`Integration = AppendToPwshCommand`）：这类 profile 的 `-Command` 是我们自己构造的，所以把加载脚本的语句追加到 `Enter-VsDevShell …;` 之后，而不是因为"参数无法识别"而放弃注入 [定]。

#### 5.3.5 nu

nushell 自带 OSC 133 和 OSC 7 的支持（`$env.config.shell_integration`），在较新版本中默认开启 [推，需按版本实测]。Ghostty 对 nu 的注入只用来提供 sudo/ssh 等附加功能，不用来发 133 [实 GH src/shell-integration/README.md 的 Nushell 一节]。因此 [定]：

- 不注入；`IntegrationPlan.Mode = NativeExpected`；
- 收到 A 但没有握手时，状态显示为"Shell 自带集成"（§5.5），Agent 能用命令边界，但没有可信的命令行（E）；
- 以后可以按 Ghostty 的方式加 `XDG_DATA_DIRS` + `--execute 'use tkcode *'` 补上 E。

#### 5.3.6 cmd

在用户原有 `PROMPT` 的外面包一层（没有设置时，原值取 cmd 的默认 `$P$G`）：

```
PROMPT=$e]133;D$e\$e]9;9;$P$e\$e]133;A$e\<原 PROMPT>$e]133;B$e\
```

- cmd 在显示提示符时展开 `$e`（ESC）和 `$P`（当前路径）[实 WT 的教程]；
- cwd 用 OSC 9;9 而不是 OSC 7，因为 `$P` 是未经编码的 Windows 路径，放进 file URI 不合法；OSC 9;9 是 ConEmu/WT 的约定，research/05 §3.4 要求兼容解析；
- 能力有限：D 没有退出码，没有 C 和 E。命令跟踪器把"B 之后用户按下的回车"推断为 C（WT 的 `autoMarkPrompts` 启发式）；
- 状态显示为"部分集成"。Agent 在 cmd 终端里退回进程模式（01 §9）。
- Clink 用户：Clink 有自己的 OSC 133 支持 [推]。检测到 `CLINK_*` 环境变量或 AutoRun 中包含 clink 时，不改 PROMPT [定]。

#### 5.3.7 WSL

v1 不注入 [定]。原因：
- 我们启动的是 `wsl.exe`，不知道发行版里的默认 shell 是什么；
- 也无法把 Windows 上的脚本路径可靠地传进去。

用户可以在发行版的 `~/.bashrc` 里手动 source（§5.6；脚本在 `/mnt/c/Users/<u>/.tkcode/shell-integration/current/`）。以后的做法：通过 `WSLENV` 传入 `TKCODE_SI_*`，并用 `wsl.exe -d X -e bash --init-file /mnt/c/…`。缺点是这样会绕过用户在 WSL 里设置的默认 shell，所以要先查询默认 shell。

### 5.4 脚本的安装位置

```
$TKCODE_HOME/shell-integration/
  <hash12>/                    ← 自动注入使用；内容哈希（全部脚本的 SHA-256 前 12 位）作目录名
    VERSION                    ← { "scriptVersion": 3, "tkcodeVersion": "0.9.0" }
    bash/tkcode.bash
    zsh/.zshenv
    zsh/tkcode-integration.zsh
    fish-data/fish/vendor_conf.d/tkcode.fish
    pwsh/tkcode.ps1
  current/                     ← 同样内容的副本，供手动 source（§5.6）；升级时整体替换
```

- 脚本作为**嵌入资源**编进 `TKCode.Terminal.Shell`（01 §4）。host 启动时检查 `<hash12>/VERSION` 是否存在：
  - 不存在：写到临时目录 `<hash12>.tmp-<pid>`，再原子改名；
  - 已存在：不重写。
- **权限**：目录 0755、文件 0644，所有者为当前用户。`$TKCODE_HOME` 在用户 home 下，不像 VS Code 那样用 `/tmp`，所以不需要 sticky 位这类防护。
- **为什么用内容哈希而不是 TKCode 版本号**：脚本没有变化的版本升级不产生新目录。旧会话（keepAlive 的 host 跨升级继续运行）引用的目录不能删除。
- **清理**：host 启动时删除既不是当前哈希、也没有被任何运行中的 host（`run/ptyhost*.json` 里记录了各自的哈希）引用、并且超过 7 天的目录。
- **为什么不从应用包里直接引用**（Ghostty 用 `<app>/Contents/Resources`）：
  - 开发运行、便携版、Linux tarball 的路径各不相同；
  - macOS 应用升级时，旧路径会在 shell 运行期间被替换，导致 zsh 的 autoload 失败。

  嵌入资源加释放到用户目录，在所有形态下行为一致 [定]。

### 5.5 注入状态与失败检测

**状态机**（每个会话一个，由 host 的命令跟踪器维护，`session/integrationChanged` 通知客户端）：

```
Planned(mode)
  ├─ mode == None  → Inactive(reason)                                    // 配置关闭、不支持的 shell/参数、远程
  ├─ mode == Partial（cmd）→ 收到 A → Partial
  ├─ mode == NativeExpected（nu）→ 收到 A → Native ；10 s 超时 → Inactive(NoNativeIntegration)
  └─ mode == Injected → Pending
        ├─ 收到握手 633;P;TKCodeShellIntegration → Active(version)
        ├─ 收到 633;P;TKCodeShellIntegrationError=X → Failed(X)            // ExecutionPolicy、RestrictedLanguage、ZshTooOld…
        ├─ 收到 A 但没有握手 → Native（例如用户自己 source 了 iTerm2/WezTerm 的脚本，或者 /etc/zshenv 改写了 ZDOTDIR 但 vte.sh 在工作）
        ├─ shell 在握手前退出 → Failed(ShellExited)                      // 交给 §6.3 的"启动失败"处理
        └─ 10 s 内都没有 → Failed(Timeout)；之后再收到握手 → Active（晚到，例如 rc 很慢）
```

- **超时为什么取 10 s**：rc 文件里加载 nvm、conda、p10k 的用户，启动可能需要好几秒。计时从第一个输出字节开始，而不是从 spawn 开始，因为 macOS 的 login 需要先打印 "Last login" [定]。
- **失败原因与提示**：

| 原因 | 显示文案（中文界面） | 可执行的操作 |
|---|---|---|
| `Disabled` | 已在设置中关闭 | — |
| `UnsupportedShell` | 不支持为 `<shell>` 自动注入 | 查看手动方式 |
| `UnsupportedArgs` | profile 的参数 `<args>` 与注入方式冲突 | 编辑 profile |
| `Timeout`（zsh） | 10 秒内没有收到 shell 集成信号。可能是 `/etc/zshenv` 改写了 ZDOTDIR，或 zsh 版本低于 5.1 | 查看说明；对此 profile 关闭 |
| `Timeout`（其他） | 10 秒内没有收到 shell 集成信号，启动文件可能覆盖了提示符钩子 | 同上 |
| `ExecutionPolicy` | PowerShell 执行策略阻止了脚本 | 查看说明（含 `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned`，由用户自己决定是否执行） |
| `RestrictedLanguage` | PowerShell 处于受限语言模式 | — |
| `ScriptInstallFailed` | 无法写入 `$TKCODE_HOME/shell-integration` | 查看日志 |

- **UI**：
  - 标签悬停提示中有一行"Shell 集成：已启用（TKCode v3）/ Shell 自带 / 部分 / 未启用：<原因>"；
  - `Failed` 时，在终端顶部显示一条可关闭的信息条，**每个 profile 每个脚本版本只显示一次**，关闭记录写入 `state.json` 的 `terminal.integrationNoticeDismissed`；
  - 信息条按钮："查看原因"、"对此 profile 关闭 shell 集成"（写回 `profiles.<id>.shellIntegration: false`）、"不再提示"；
  - Agent 侧（09）在 `Failed` / `Inactive` 时显示"此终端将以进程模式执行命令"。
- **启动失败的重试**：shell 在 250 ms 内非 0 退出且注入过（§6.3）时，提供"关闭 shell 集成后重试"，以 `ShellIntegrationOverride=false` 重新启动（VS Code 的做法 [实 VSC terminalInstance.ts:2905-2950]）。

### 5.6 手动 source

自动注入关闭或失败时，用户可以在自己的 rc 文件里加载。所有片段都先检查 `TERM_PROGRAM`，因为 `TKCODE_*` 会被剔除，不能用来判断：

```bash
# ~/.bashrc（放在文件末尾，在 starship / oh-my-bash 之后）
[[ $TERM_PROGRAM == TKCode ]] && . "${TKCODE_HOME:-$HOME/.tkcode}/shell-integration/current/bash/tkcode.bash"
```
```zsh
# ~/.zshrc（末尾）
[[ $TERM_PROGRAM == TKCode ]] && . "${TKCODE_HOME:-$HOME/.tkcode}/shell-integration/current/zsh/tkcode-integration.zsh"
```
```fish
# ~/.config/fish/config.fish
test "$TERM_PROGRAM" = TKCode; and source (set -q TKCODE_HOME; and echo $TKCODE_HOME; or echo ~/.tkcode)/shell-integration/current/fish-data/fish/vendor_conf.d/tkcode.fish
```
```powershell
# $PROFILE
if ($env:TERM_PROGRAM -eq 'TKCode') { . "$(if ($env:TKCODE_HOME) { $env:TKCODE_HOME } else { "$HOME/.tkcode" })/shell-integration/current/pwsh/tkcode.ps1" }
```

- 手动加载时没有 nonce，E 被标记为"不可信命令行"：显示时带标记，Agent 只能当作参考 [定]。
- 脚本检测到 `TKCODE_SI_INJECTED` 未设置时，**不会**再去 source 启动文件（避免重复加载）。

### 5.7 对用户 rc 文件的影响与规避

| Shell | 影响 | 规避 |
|---|---|---|
| bash | `--init-file` 取代了 `~/.bashrc` 的自动读取；login 被模拟：`shopt -q login_shell` 为假，`$0` 不以 `-` 开头 | 脚本按 bash 的规则 source `/etc/profile`、`~/.bash_profile`/`~/.bash_login`/`~/.profile` 或 `~/.bashrc`。设置页说明"login 由 TKCode 模拟"。依赖 `login_shell` 判断的 rc 很少见 [推] |
| bash | DEBUG trap、`PROMPT_COMMAND` 被串联 | 保留用户原有的 trap 和 PROMPT_COMMAND，我们的函数放首尾，`$?` 原样传递 |
| bash | `HISTCONTROL=ignorespace/ignoredups` 时 `history 1` 拿不到当前命令 | 比较历史内容；没有变化时退回 `BASH_COMMAND`（只有第一条简单命令，E 标记为"可能不完整"）。**不修改用户的 HISTCONTROL** |
| bash / zsh / fish / pwsh | Agent 在共享终端中执行的命令进入用户历史 | 只对 `Purpose = Agent` 的终端或 Agent 注入的命令处理（09）：bash 在用户原值后追加 `ignorespace`，zsh `setopt HIST_IGNORE_SPACE`，fish 本来就不保存以空格开头的命令 [推]，pwsh 设置 PSReadLine 的 `AddToHistoryHandler`。Agent 发出的命令前加一个空格。用户自己的交互终端**不改动** |
| zsh | ZDOTDIR 被临时改写；HISTFILE 默认取 `$ZDOTDIR/.zsh_history` | `.zshenv` 第一行就还原 ZDOTDIR，后续文件和 HISTFILE 的默认值都与未注入时相同（VS Code 是事后改 HISTFILE [实 VSC shellIntegration-rc.zsh:18-23]，我们的方式不需要） |
| zsh | p10k 的 instant prompt 在 `.zshrc` 执行完之前就画出提示符 | 第一次 precmd 时才包装 PS1，第一个提示符可能缺少 A。命令跟踪器容忍这种情况 |
| fish | `XDG_DATA_DIRS` 被临时改写 | `vendor_conf.d` 一开始就还原，并精确区分"原来没有设置"和"原来是空" |
| pwsh | `prompt` 和 `PSConsoleHostReadLine` 被包装 | 只包装，不替换；用户之后重新定义 `prompt`（例如切换 oh-my-posh 主题）时，下一次 `PSConsoleHostReadLine` 会检测并重新包装 |
| 全部 | 注入变量泄漏给子进程 | 脚本读取后立即 unset；`TKCODE_SI_*` 也在 §4.3 的剔除规则内（嵌套的 TKCode 终端不会继承） |

### 5.8 远程与嵌套 shell

- **SSH profile 默认不注入**（08 §2.6）：远端没有我们的脚本，在远端写文件属于侵入行为。
- **本地 shell 中手动运行 `ssh`**：命令跟踪器看到的是一条正在执行的命令（C 之后），直到 ssh 退出才收到 D。远端如果自己装了 OSC 133 集成（例如 vte.sh），会产生**嵌套**的 A/B/C/D，其 `aid` 与本地不同。命令跟踪器按 `aid` 区分层级，Agent 只信任本会话 nonce 对应的那一层 [定，细节在 09]。
- `exec zsh`、tmux 等会导致集成丢失：没有握手的 A 视为 Native；完全没有标记时，退回 WT 的回车启发式（research/05 §3.4）。

### 5.9 脚本的许可

| 来源 | 许可 | 能否使用 |
|---|---|---|
| Ghostty `src/shell-integration/{bash,zsh,fish}` | GPLv3（文件头：基于 kitty，"this file is also distributed under GPLv3"）[实] | **不能拷贝**，只能参考机制（思想不受著作权保护） |
| kitty shell-integration | GPLv3 [实，同上] | 同上 |
| VS Code `shellIntegration-*.sh/.zsh/.fish/.ps1` | MIT [实 文件头] | 可以改编，保留版权声明，并在 THIRD-PARTY-NOTICES 中登记 |
| bash-preexec（Ghostty 引用） | MIT [推] | 可用；但我们的 DEBUG trap 方案用不到它 |

TKCode 仓库是 Apache-2.0（根目录 LICENSE）[实]。

---

## 6. 生命周期

### 6.1 关闭确认："有程序在运行"的判定

```
IsBusy(session):
  if session.Exited: return false
  if config.confirmClose == never: return false
  if config.confirmClose == always: return true
  t = session.Tracker                                   // shell 集成的状态
  if t.Active or t.Native:
      if t.State == Executing: return !IgnoredProcess(Foreground(session))   // C 之后、D 之前
      if t.State in (Prompt, Input): return false        // 光标在提示符处（Ghostty 的做法）
  return session.Platform == Windows ? BusyWindows(session) : BusyUnix(session)

BusyUnix(session):
  fg = tcgetpgrp(master)
  if fg == session.ShellPgid: return false               // ShellPgid：第一次收到 A 时记录的前台进程组；
                                                         //   没有集成时：spawn 后 500 ms 取 tcgetpgrp 作为近似（macOS 下是 login 之后的 shell）
  name = ProcessName(fg)                                 // 进程组首进程的 comm（Linux /proc/<pid>/comm；macOS proc_name），失败时取该组任一进程
  return !IgnoredProcess(name)

BusyWindows(session):
  tree = Toolhelp32Snapshot 中以 shell pid 为根的子树（校验子进程的创建时间晚于父进程，排除 pid 复用）
  tree -= { conhost.exe, OpenConsole.exe }
  return tree.Any(p => !IgnoredProcess(p.Name))
  // Kind == Wsl：Linux 侧的进程在 Windows 上不可见；没有集成时视为空闲（与 WT 一致：WT 根本不检查进程）

IgnoredProcess(name) = Normalize(name) ∈ (内置忽略列表 ∪ 用户追加) − 用户移除（"-name"）
Normalize：去掉 ".exe"，去掉开头的 "-"（login shell 的 argv0），小写；以 gitstatusd 开头的统一视为 gitstatusd
```

- **为什么集成状态优先**：`bash script.sh` 运行时，前台进程名是 `bash`，在忽略列表中，只看进程名会误判为空闲。shell 集成知道当前是"执行中"[定]。
- **内置忽略列表** [定，汇总 Ptyxis、VS Code、WezTerm、iTerm2：research/05 §8.1]：
  - shell：`sh`、`bash`、`zsh`、`fish`、`nu`、`dash`、`ksh`、`mksh`、`tcsh`、`csh`、`elvish`、`xonsh`、`pwsh`、`powershell`、`cmd`，以及 `/etc/shells` 中各项的文件名；
  - 包装进程：`login`、`wsl`、`wslhost`；
  - 提示符辅助程序：`starship`、`oh-my-posh`、`gitstatusd`。
- **不在列表中、因而算作"在运行"的**：`tmux`、`screen`、`ssh`、`mosh`、`vim` 等。
  - 关闭含 tmux 的标签会断开 tmux 客户端，tmux 会话本身不会丢；但 Terminal.app 默认列表里有 tmux 的做法我们不采用：用户可能运行的是 `tmux attach` 之外的东西，统一确认更安全；
  - 用户可以写 `"ignoreProcesses": ["tmux"]` 把它加进来。
- **SSH profile**：根进程是 `ssh`，远端的前台程序看不到。默认认为在运行（需要确认），因为关闭会结束远端 shell。用户可以把 `ssh` 加进忽略列表。
- **汇总确认**：
  - 关闭窗口或退出 TKCode 时，对所有 `IsBusy` 的会话汇总确认一次（iTerm2 `PromptOnQuit`）；
  - `persistence.onQuit == keepAlive` 时，退出不会结束进程，**不需要确认**（与 01 §3.3 的表述有出入，列入 §10）。
- `IsBusy` 是按需查询（关闭时调用），不轮询。

### 6.2 标题的优先级

```
TabTitle(session):
  1. 用户手动重命名的标题（标签右键 → 重命名；保存在会话状态中，revive 时恢复）
  2. 程序通过 OSC 0/2 设置的标题（title.allowApplicationTitle 为 true，且最近一次设置的不是空串；设置空串表示恢复默认）
  3. 有 shell 集成时：
       Executing → 命令名（E 的第一个词，去掉路径和常见前缀 sudo/env/time/nice）
       Prompt    → cwd 的目录名（home 显示为 "~"）
  4. 没有 shell 集成时：
       前台进程不是 shell（§6.1 的 fg 名不在 shell 列表中）→ 前台进程名
       否则 → cwd 目录名（OSC 7 → 进程 cwd 查询，节流到 ≤ 1 Hz）
  5. profile 的显示名
副标题（标签悬停 / 第二行）：cwd 的完整路径（home 缩写为 ~）；SSH 会话显示 user@host
```

- 我们的脚本**不发** OSC 2：标题由 TKCode 根据结构化信息计算。用户的提示符框架（oh-my-zsh 等）发出的 OSC 2 按第 2 级处理。
- 标题的查询应答（title report）默认关闭（01 §8，research/03 §8.2）。
- 远端主机（SSH）发出的 OSC 0/2 同样按第 2 级处理；控制字符和超长标题（> 256 字符）截断。

### 6.3 shell 退出之后（`onExit`）

| 取值 | 退出码 0 | 非 0 或被信号终止 |
|---|---|---|
| `closeOnCleanExit`（默认） | 关闭标签 | 保留标签，在末尾显示 `[进程已退出，退出码 N]` 或 `[进程被 SIGTERM 终止]`；Enter 重新启动（同一 profile、同一 cwd，新会话接在旧内容后面，中间插入分隔线）；Ctrl+D 或 Esc 关闭 |
| `close` | 关闭 | 关闭 |
| `hold` | 保留并显示 `[进程已完成]` | 同 `closeOnCleanExit` |

- 依据：WT `closeOnExit: automatic`、Terminal.app 的 "Close if the shell exited cleanly" [实 research/05 §8.3]。
- **启动即失败**：退出发生在启动后 **250 ms** 内（Ghostty 的阈值），或者在收到任何输出之前，且退出码非 0：
  - 不论 `onExit` 取什么值都保留标签，显示错误面板：等价命令行（`LaunchPlan.DisplayCommand`）、退出码、最后几行输出；
  - 按钮："重试"、"关闭 shell 集成后重试"（仅在注入过时）、"编辑 profile"、"改用默认 profile"。
- **SSH profile 的 255**：见 08 §2.8。
- **Agent 和 ACP 创建的终端**（09）：由调用方决定，通常是 `hold`，因为调用方要读取退出码和最终输出。

### 6.4 新标签与分屏的 cwd 继承

```
ResolveCwd(ctx):
  match ctx.Cwd:
    Explicit(p)  → p
    Default      → config.cwd: project → ctx.ProjectRoot ?? Home ; home → Home ; <path> → 展开 ~ 与变量后的路径
    Inherit(src) → if config.newTabCwd != inherit: 同 Default
                   c = InheritedCwd(src)
                   return c ?? Default
  最后：目录不存在 → Warning + 回退到 ProjectRoot → Home

InheritedCwd(src):
  if src.Kind == Ssh: return null                          // 远端路径不能用作本地目录
  u = src.Tracker.LastOsc7                                 // 最近一次 OSC 7
  if u != null and IsLocalHost(u.Host, src) and Directory.Exists(LocalPath(u, src)): return LocalPath(u, src)
  if src 的前台是 shell（§6.1）:                            // 只在 shell 处于前台时才信任进程 cwd（GNOME Terminal 的 "safe" 规则）
      p = Linux ? readlink(/proc/<shellPid>/cwd) : macOS ? proc_pidinfo(shellPid, PROC_PIDVNODEPATHINFO) : null
      if p != null and Directory.Exists(p): return p
  return null

IsLocalHost(h, src) = h ∈ { "", "localhost", 本机 hostname（大小写不敏感，去掉 .local）}
                      或 (src.Kind == Wsl 且 h == 该发行版的 hostname)       // WSL 内部的 hostname 与 Windows 不同
LocalPath(u, src)：百分号解码；Windows 上 "/C:/x" → "C:\x"；
                   src.PathStyle == Msys/Cygwin 时 "/c/x"、"/cygdrive/c/x" → "C:\x"；
                   src.Kind == Wsl 时保留 Linux 路径（只用于 WSL → WSL 的继承，见下）
```

- **跨类型继承**：
  - WSL → 同一发行版的 WSL：`--cd <Linux 路径>`；
  - WSL → 非 WSL：转换为 `\\wsl.localhost\<发行版>\<路径>`（Windows 11；Windows 10 是 `\\wsl$\`）[推]，只有目标 shell 是 pwsh 时才使用（cmd 不能把 UNC 路径作为当前目录）；其他情况回退到 Default；
  - 非 WSL → WSL：Windows 路径直接交给 `--cd`。
- **分屏**：使用源标签的 profile + 继承的 cwd（VS Code 的 `splitCwd: inherited`）。**新标签**使用默认 profile + 继承的 cwd。
- **OSC 7 是不可信输入**（01 §8）：只用于选择起始目录，并且必须校验目录存在；不用来打开文件，也不传给其他命令。
- **revive**（01 §3.3）：恢复时使用快照中记录的最后 cwd，规则同 `Explicit`（不存在时回退）。

---

## 7. settings.json 的 `terminal` 节

### 7.1 设计原则

沿用 [settings.md](../settings.md) §1：

1. **省略即跟随系统**：字段省略或写 `"system"` 时使用"跟随系统"的解析结果；写出具体值才固定。设置页显示解析后的实际值，例如"跟随系统（Cascadia Mono 12）"。
2. **profile 与 providers 写法相同**：键与探测到的 id 相同时是覆盖，否则是自定义；`"enabled": false` 表示隐藏。
3. **平台覆盖块** [定，新增]：`terminal.macos` / `terminal.windows` / `terminal.linux` 可以包含 `terminal` 的任意字段（平台块自身除外），在当前平台上**深合并**到基础值之上：
   - 对象逐键合并；
   - 数组整体替换；
   - `env` 中的 `null` 表示删除；
   - 这样就不再需要 research/05 草案里"字符串或 `{macos,linux,windows}`"那种联合类型（源生成 JSON 不好处理）。
4. **宽容解析，字段级容错** [定，新增]：
   - 未知字段：警告（同 settings.md）；
   - 某个字段类型或取值非法：警告，**该字段回到默认值**；
   - 某个 profile 缺少必填字段：只停用该 profile（相当于 settings.md 的"厂商错误"级别）；
   - `terminal` 不是对象：警告，整个节按默认处理。
   - **`terminal` 节永远不会造成"文件错误"**，除非 JSON 本身有语法错误。理由：字号写错不应该让模型和厂商配置也失效。
5. **热重载**：Desktop 的 `UserSettingsService` 已经在监听（500 ms 防抖），`Changed` 事件触发后重新解析并 `host/configure`。字段表的"热重载"列：
   - **立即**：作用于已经打开的终端；
   - **新终端**：只影响此后新建的终端。设置页在这类字段旁提示"对新终端生效"，并提供"重启此终端"的按钮（相当于 VS Code 的 `environmentChangesRelaunch`）。核心状态类字段（回滚上限、宽度模式、Kitty 图形上限等）必须两份核心一致（01 §3.2 规则 2），所以只能对新终端生效。
6. **只管交互终端**：Agent 的 `bash` 工具不受 `terminal.*` 影响（VS Code `agentHostProfile` 与 `defaultProfile` 分开的做法）；Agent 的设置在顶层 `agent.shell.*`（09 §2.2）。

### 7.2 示例

```jsonc
{
  "terminal": {
    // 新终端的 profile："system"（默认）= macOS/Linux 的账户登录 shell；Windows 的 PowerShell 7（没有时用 Windows PowerShell）
    "defaultProfile": "system",

    "profiles": {
      "pwsh": { "args": ["-NoLogo"] },                 // ① 覆盖探测到的 profile：只写要改的字段
      "cmd": { "enabled": false },                     // ② 隐藏
      "node-dev": {                                    // ③ 自定义
        "name": "Node 开发",
        "command": "/bin/zsh",                         // 绝对路径或 PATH 中的名字
        "args": [],
        "loginShell": true,                            // 默认：macOS true，其他 false
        "cwd": "~/work/web",
        "env": { "NODE_ENV": "development", "DEBUG": null },
        "shellIntegration": true,
        "icon": "nodejs",
        "platforms": ["macos", "linux"]
      },
      "prod": { "type": "ssh", "host": "prod-1" }      // ④ SSH（字段见 08 §2.4）
    },

    "cwd": "project",                                  // project | home | 绝对路径
    "newTabCwd": "inherit",                            // inherit | project | home
    "env": { "EDITOR": "code --wait", "HTTPS_PROXY": "{env:CORP_PROXY}" },
    "setLocaleVariables": "auto",                      // auto | on | off（只在 macOS 上生效）

    "font": { "family": "system", "size": "system", "lineHeight": 1.0, "ligatures": false },
    "cursor": { "style": "system", "blink": "system", "unfocusedStyle": "outline" },
    "colors": { "minimumContrastRatio": 4.5, "boldIsBright": false },
    "scrollback": 10000,
    "unicode": { "ambiguousWidth": "narrow", "widthMode": "compat" },

    "keyboard": { "macOptionAsMeta": "none", "ctrlCCopies": "system", "skipShell": [], "kittyKeyboardProtocol": true },
    "selection": { "wordSeparators": "system", "copyOnSelect": "system" },
    "mouse": { "rightClick": "system", "middleClick": "system", "altClickMovesCursor": true },
    "links": { "detectFilePaths": true, "allowedSchemes": ["http", "https", "mailto", "file"] },
    "paste": { "multiLineWarning": "auto", "trimTrailingNewline": true, "largePasteWarning": 0 },
    "bell": { "sound": "system", "visual": true, "attention": true },
    "clipboard": { "oscWrite": "allow", "oscRead": "ask" },

    "shellIntegration": { "enabled": true, "decorations": true, "notifyOnFinish": "unfocused" },
    "confirmClose": "whenRunning",                     // whenRunning | always | never
    "ignoreProcesses": ["-tmux"],                      // 追加到内置列表；"-" 前缀表示从内置列表中移除
    "onExit": "closeOnCleanExit",                      // closeOnCleanExit | close | hold
    "title": { "allowApplicationTitle": true },

    "persistence": {
      "onQuit": "revive",                              // revive | keepAlive | close（01 §3.3）
      "scrollbackLines": 1000,                         // revive 快照保存的尾部行数；0 = 只恢复标签与 cwd
      "orphanGraceMinutes": 30,                        // 没有任何 UI 连接时，host 等待多久后按"退出 TKCode"处理（只计清醒时间）
      "keepAliveMaxHours": null,                       // keepAlive 退出后会话最多保留多久；null = 不限制
      "saveIntervalSec": 30                            // 增量保存快照的间隔（防 host 崩溃）
    },
    "graphics": { "kitty": true, "kittyStorageLimitMB": 128 },
    "rendering": { "gpu": "auto", "customGlyphs": true, "smoothScrolling": false },
    "accessibility": { "announceOutput": "auto" },

    "ssh": { "enabled": true, "sendEnv": true },       // 见 08 §5

    // 平台覆盖块：只在对应平台生效，深合并到上面的值
    "windows": { "defaultProfile": "git-bash", "env": { "MSYS": "winsymlinks:nativestrict" } },
    "macos": { "font": { "size": 13 } }
  }
}
```

### 7.3 字段表

"系统/固定"列："跟随系统"表示默认值取决于平台或系统设置，"固定"表示所有平台默认值相同。

**启动**

| 字段 | 类型 | 默认 | 系统/固定 | 依据 | 热重载 |
|---|---|---|---|---|---|
| `defaultProfile` | string | `system` | 跟随系统 | §2.1；research/05 §1.5 | 新终端 |
| `profiles.<id>` | object | 探测结果 | 跟随系统 | §2、§7.4 | 新终端（`enabled`、`name`、`icon` 立即反映到菜单） |
| `cwd` | `project` \| `home` \| string | `project` | 固定 | VS Code 用工作区根目录 | 新终端 |
| `newTabCwd` | `inherit` \| `project` \| `home` | `inherit` | 固定 | VS Code `splitCwd: inherited`、GNOME Terminal、Ghostty（research/05 决策 10） | 立即（下次新建标签时生效） |
| `env` | object（值为 string \| null） | `{}` | — | VS Code `env.*`、WT `environment`；`null` = 删除 | 新终端 |
| `term` | string | `xterm-256color` | 固定 | D9 | 新终端 |
| `setLocaleVariables` | `auto` \| `on` \| `off` | `auto` | 跟随系统（只在 macOS 上生效） | §4.4 | 新终端 |
| `reloadEnvironment` | bool | `true` | 只在 Windows 上生效 | WT | 新终端 |

**外观**

| 字段 | 类型 | 默认 | 系统/固定 | 依据 | 热重载 |
|---|---|---|---|---|---|
| `font.family` | string \| string[] | `system` | 跟随系统：`.SF NS Mono` → Menlo / Cascadia Mono → Consolas / portal `monospace-font-name` | research/05 §7.1 | 立即 |
| `font.size` | number（pt）\| `system` | `system` | 跟随系统：macOS 12、Windows 12、Linux 取系统字号 | 同上 | 立即 |
| `font.lineHeight` / `letterSpacing` / `weight` / `weightBold` / `ligatures` | number / number / string / string / bool | 1.0 / 0 / normal / bold / false | 固定 | VS Code | 立即 |
| `font.fallback` | string[] | 按平台的 CJK 字体 | 跟随系统 | research/05 §7.1 | 立即 |
| `cursor.style` | `system` \| `block` \| `bar` \| `underline` | `system` | 跟随系统：macOS/Linux block，Windows bar | Terminal.app、VTE、WT | 立即 |
| `cursor.blink` | `system` \| bool | `system` | 跟随系统 | research/05 §7.2；`reduceMotion` 为真时不闪 | 立即 |
| `cursor.unfocusedStyle` | `outline` \| `none` \| `same` | `outline` | 固定 | VS Code | 立即 |
| `colors.dark` / `colors.light` | 调色板对象（`foreground`、`background`、`cursor`、`selectionBackground`、`ansi`[16]） | TKCode 内置 | 跟随 `theme` | Ghostty、WT、Ptyxis | 立即（经 `session/setColors`，01 §3.2 规则 5） |
| `colors.minimumContrastRatio` | number（1–21） | 4.5（系统高对比度时 7） | 固定 | VS Code（research/05 决策 7） | 立即 |
| `colors.boldIsBright` | bool | false | 固定 | GNOME Terminal | 立即 |
| `scrollback` | int（行，0–1000000） | 10000 | 固定 | GNOME Terminal、Konsole、Alacritty | 新终端（两份核心必须一致） |
| `unicode.ambiguousWidth` | `narrow` \| `wide` | `narrow` | 固定 | 所有系统终端 | 新终端（核心选项） |
| `unicode.widthMode` | `compat` \| `grapheme` | macOS/Linux：`compat`（程序开启 2027 后自动切换到字形簇模式）；**Windows：固定为 `grapheme`，设置值被忽略** | 跟随系统 | D9；Windows：ConPTY 的宽度在创建时就固定，对 DECSET 2027 不做任何处理，两份核心必须锁定为与 ConPTY 一致的 grapheme 模式，并忽略 2027 切换（03 §7.4） | 新终端（核心选项） |
| `rendering.gpu` / `customGlyphs` / `smoothScrolling` | `auto`\|`on`\|`off` / bool / bool | auto / true / false | 固定 | VS Code；`reduceMotion` 时强制关闭平滑滚动 | 立即 |

**键盘、鼠标、链接、粘贴、响铃、剪贴板**（取值与依据沿用 research/05 §10.3，热重载均为"立即"，以下几项除外）

| 字段 | 类型 | 默认 | 热重载 | 说明 |
|---|---|---|---|---|
| `keyboard.kittyKeyboardProtocol` | bool | true | 新终端 | 影响核心对 `CSI ? u` 的应答，两份核心必须一致 |
| `keyboard.win32InputMode` | bool | true（只在 Windows 上生效） | 新终端 | D9；ConPTY 启动标志 |
| `keyboard.macOptionAsMeta` / `ctrlCCopies` / `skipShell` / `sendKeybindingsToShell` / `altGrAliasing` / `backspace` | — | research/05 §10.3 | 立即 | |
| `selection.*`、`mouse.*`、`links.*`、`paste.*`、`bell.*`、`clipboard.*` | — | research/05 §10.3 | 立即 | |

**Shell 集成与生命周期**

| 字段 | 类型 | 默认 | 系统/固定 | 依据 | 热重载 |
|---|---|---|---|---|---|
| `shellIntegration.enabled` | bool | true | 固定 | D7 | 新终端 |
| `shellIntegration.decorations` | bool | true | 固定 | VS Code `decorationsEnabled` | 立即 |
| `shellIntegration.failureNotice` | bool | true | 固定 | §5.5 | 立即 |
| `shellIntegration.notifyOnFinish` | `never` \| `unfocused` | `unfocused` | 固定 | Ptyxis、Ghostty、iTerm2 | 立即 |
| `shellIntegration.notifyOnFinishSeconds` | int | 10 | 固定 | [推] | 立即 |
| `confirmClose` | `whenRunning` \| `always` \| `never` | `whenRunning` | 跟随系统（macOS/Linux）；Windows 比 WT 保守 | §6.1 | 立即 |
| `ignoreProcesses` | string[] | `[]`（叠加在内置列表上；`-name` 表示移除） | — | VS Code、WezTerm、Terminal.app | 立即 |
| `onExit` | `closeOnCleanExit` \| `close` \| `hold` | `closeOnCleanExit` | 固定 | §6.3 | 立即（作用于此后退出的会话） |
| `title.allowApplicationTitle` | bool | true | 固定 | WT | 立即 |

**会话保活**（01 §3.3）

| 字段 | 类型 | 默认 | 系统/固定 | 依据 | 热重载 |
|---|---|---|---|---|---|
| `persistence.onQuit` | `revive` \| `keepAlive` \| `close` | `revive` | 固定 | 01 §3.3；iTerm2、VS Code | 立即（下次退出时生效） |
| `persistence.scrollbackLines` | int（0–100000） | 1000 | 固定 | 01 §3.3；research/05 决策 9 | 立即（下次写快照时生效，包括 30 s 的增量保存） |
| `persistence.enabled` | bool | true | 固定 | 04 `host/configure` 的 `persistence.enabled`；false 时不写任何快照，`onQuit` 的 revive 退化为只恢复标签布局 | 立即 |
| `persistence.orphanGraceMinutes` | int（1–10080） | 30 | 固定 | 01 §3.3；04 §（宽限期）：只计清醒时间 | 立即（推送给 host） |
| `persistence.keepAliveMaxHours` | int \| null | null（不限制） | 固定 | 04：keepAlive 退出时不计宽限期，可选上限 | 立即 |
| `persistence.saveIntervalSec` | int（5–3600） | 30 | 固定 | 01 §3.3、04：增量保存间隔 | 立即 |

键名与语义以 [04-pty-host.md](04-pty-host.md) 为准（`host/configure` 的 `persistence{…}` 一一对应），本表只登记 settings.json 中的写法。**Agent 创建的会话默认不持久化**（01 §3.3、04），不受这些字段影响；是否允许持久化由 09 决定。快照文件权限 0600（01 §8）。`scrollbackLines: 0` 时只恢复标签、profile、cwd 和标题，不保存任何输出内容，适合在意敏感输出的用户。

**图形**（D8）

| 字段 | 类型 | 默认 | 依据 | 热重载 |
|---|---|---|---|---|
| `graphics.kitty` | bool | true | D8 | 新终端（核心选项） |
| `graphics.kittyStorageLimitMB` | int（0–4096） | 128 | Ghostty `image-storage-limit` 默认 320 MB，而且主屏和备用屏各一份 [实 GH src/config/Config.zig:2520-2522]。我们的镜像核心和 UI 侧核心各持有一份，最坏情况是 4 倍（2 个屏幕 × 2 份核心），所以取得更保守 [定]。镜像核心能否只保留元数据、不保留像素，由 02 决定 | 新终端（核心选项） |

**其他**

| 字段 | 说明 |
|---|---|
| `accessibility.announceOutput` | `auto` \| `on` \| `off`，默认 `auto`（检测到屏幕阅读器时开启）；立即生效 |
| `ssh.*` | 见 [08-ssh.md](08-ssh.md) §5 |
| Agent 相关 | **不在 `terminal` 节内**。由 [09](09-agent-integration.md) §2.2 定义为顶层的 `agent.shell.*`（`mode`、`profile`、`onTimeout`、`reveal`、`idleInputSeconds`、`preventHistory`）和 `agents.acp.clientTerminal`。其中 `agent.shell.profile` 可以引用 `terminal.profiles` 中的 id，此时 LaunchBuilder 按 `Purpose = Agent` 构造（§4.6 第 7 步追加调用方环境）；`preventHistory` 的实现见 §5.7 |
| `macos` / `windows` / `linux` | 平台覆盖块（§7.1 第 3 条）。不能嵌套 |

### 7.4 profile 字段（`terminal.profiles.<id>`）

| 字段 | 类型 | 探测到的 profile | 自定义 profile | 说明 |
|---|---|---|---|---|
| `name` | string | 可选 | 可选，默认为 id | 显示名称 |
| `command` | string | 可选（替换可执行文件） | **必填**（`type: ssh` 时不需要） | 绝对路径或 PATH 中的名字（§2.2 的搜索顺序） |
| `args` | string[] | 可选（**替换**探测到的参数） | 可选 | |
| `commandLine` | string | 可选 | 可选 | 只在 Windows 上生效：原始命令行，设置后忽略 `args`（§3.4） |
| `kind` | `bash` \| `zsh` \| `fish` \| `pwsh` \| `powershell` \| `cmd` \| `nu` \| `other` | 可选 | 可选，默认按文件名推断 | 决定 argv0 形式和注入方式 |
| `loginShell` | bool | 可选 | 可选 | 默认：macOS true，其他平台 false |
| `cwd` | string | 可选 | 可选 | 覆盖全局 `cwd`；支持 `~` 和变量 |
| `env` | object | 可选（与探测值逐键合并） | 可选 | 同全局 `env` |
| `shellIntegration` | bool | 可选 | 可选 | 覆盖全局开关 |
| `enabled` | bool | 可选 | 可选 | 默认 true |
| `icon` | string | 可选 | 可选 | lobe-icons 名或内置图标名 |
| `platforms` | (`macos`\|`linux`\|`windows`)[] | 不适用 | 可选 | 省略 = 所有平台 |
| `type` | `local` \| `ssh` | 不适用 | 可选，默认 `local` | `ssh` 的其余字段见 08 §2.4 |

### 7.5 校验与错误级别

| 级别 | 示例 | 影响 |
|---|---|---|
| 文件错误（沿用 settings.md §2.5） | JSON 语法错误 | 沿用上一次正确的配置 |
| profile 错误 [新增] | 自定义 profile 没有 `command`；`type` 非法；`platforms` 中有未知值 | 只停用该 profile；设置页的 profile 列表中显示红色标记和原因 |
| 字段警告 [新增] | `font.size: "big"`；`onExit: "never"`；`scrollback: -1` | 该字段回到默认值，在设置页"终端"顶部显示黄色提示，并写入日志 |
| 警告 | 未知字段 | 忽略 |

### 7.6 与 TKCode.Config 的集成方案（只写方案，本文不改代码）

注意：另一个会话正在给 TKCode.Config 加 MCP 配置，下面的改动要在它合入之后进行，并沿用它的模式（`McpServers` 以 `JsonObject` 原样保留，再逐项解析）。

| 文件 | 改动 |
|---|---|
| `src/TKCode.Config/Settings.cs` | `TKCodeSettings` 增加 `public JsonObject? Terminal { get; init; }`（原样保留，同 `McpServers`），注释指向 `TerminalConfig.Parse`。`SettingsJsonContext` 增加 `[JsonSerializable(typeof(TerminalSettings))]` |
| `src/TKCode.Config/TerminalSettings.cs`（新） | 反序列化用的记录类型，全部可空，与 settings.md 的写法一一对应：`TerminalSettings`、`TerminalProfileSettings`、`TerminalFontSettings`、`TerminalCursorSettings`、`TerminalColorsSettings`、`TerminalPaletteSettings`、`TerminalKeyboardSettings`、`TerminalSelectionSettings`、`TerminalMouseSettings`、`TerminalLinksSettings`、`TerminalPasteSettings`、`TerminalBellSettings`、`TerminalClipboardSettings`、`TerminalShellIntegrationSettings`、`TerminalPersistenceSettings`、`TerminalGraphicsSettings`、`TerminalSshSettings`、`JsonObject? Agent`（09 定稿前原样保留）。`font.size`、`cursor.blink` 这类"`system` 或具体值"的字段用 `JsonNode?` 接收，由 `TerminalConfig` 解释，避免自定义转换器 |
| `src/TKCode.Config/TerminalConfig.cs`（新） | `public static TerminalConfig Parse(JsonObject? terminal, TerminalPlatform platform, List<string> warnings)`：① 平台覆盖块的 JSON 级深合并；② 逐字段校验（枚举、范围），非法值记警告并回到默认；③ 逐个 profile 解析，错误只停用该项（同 `McpServerConfig.ParseServers`）；④ 产出规整后的 `TerminalConfig`（不含 `system` 以外的平台判断：`system` 由 host 或 Desktop 的平台层解析）。另提供 `static void Validate(JsonObject terminal, List<string> warnings)`，供 `SettingsFile.Parse` 调用 |
| `src/TKCode.Config/SettingsFile.cs` | `TopLevelKeys` 增加 `"terminal"`；在 `Parse` 中，当 `obj["terminal"]` 是对象时调用 `TerminalConfig.Validate(…, warnings)`，**只产生警告，不产生 errors**（§7.5）；不是对象时记警告。增加写回辅助方法：`JsonObject Terminal(JsonObject root)`、`JsonObject TerminalProfile(JsonObject root, string id)`、`void SetTerminal(JsonObject root, string dottedPath, JsonNode? value)`（例如 `"font.size"`，值为 null 时删除，空对象顺带删除） |
| `src/TKCode.Config/settings.schema.json` | 顶层 `properties` 增加 `"terminal": { "$ref": "#/definitions/terminal" }`（顶层 `additionalProperties: false`，**必须**加）。新增定义：`terminal`（含 `macos`/`windows`/`linux` → `#/definitions/terminalPlatformOverride`）、`terminalPlatformOverride`（与 `terminal` 相同但不含平台键，用 `allOf` 复用公共部分）、`terminalProfile`、`terminalPalette`、`terminalEnv`（`additionalProperties: { "type": ["string", "null"] }`）。所有枚举字段同时接受 `"system"`。`description` 用英文，与现有 schema 一致 |
| `src/TKCode.Config/SettingsSchema.cs` | 不变（继续写出嵌入的 schema） |
| `src/TKCode.Terminal.Protocol/TerminalHostConfig.cs`（新，不属于 TKCode.Config） | host 端使用的 DTO（源生成 JSON）。即 04 §（`host/configure`）中的 `HostSettingsDto`：本文的 profiles 覆盖对应其 `settingsProfiles[]`，`ignoreProcesses` 对应 `confirmIgnoreProcesses[]`，`persistence.*` 一一对应；另需增加 §4.1 的 `baseEnvironment` 和启动类字段（env、cwd、term、setLocaleVariables、shellIntegration、reloadEnvironment）。`TKCode.Desktop` 的 `TerminalSettingsAdapter` 负责 `TerminalConfig → TerminalHostConfig` 的映射；终端库因此不依赖 TKCode.Config（D2） |
| `src/TKCode.Desktop/Services/UserSettingsService.cs` | 增加 `TerminalConfig Terminal`（`Changed` 时重新解析）；增加 `SetTerminal(path, value)` 等写回方法，供设置页使用 |
| 测试 | 新建 `tests/TKCode.Config.Tests`（或沿用 `TKCode.AgentHost.Tests` 中 `ModelCatalogTests` 的位置）：`TerminalConfigTests`（平台合并、字段级容错、profile 级容错、JSONC）；`SchemaParityTests`：断言 `SettingsFile` 中的已知键集合与 schema 的 `properties` 一致，避免两边不同步（目前 schema 是手写的） |

### 7.7 docs/settings.md 需要补充的章节（大纲）

```
§1 设计原则
   + 第 6 条：terminal 节采用字段级容错（字段非法只回到默认值，profile 非法只停用该 profile）
   + 第 7 条：平台覆盖块（macos / windows / linux）
§2 格式
   示例中加一段最小的 "terminal"（defaultProfile + 一个 profile 覆盖 + font.size）
§2.1 顶层字段
   + terminal | object | 无 | 见 §2.7
§2.5 错误的级别
   + "profile 错误"与"字段警告"两行
§2.7 终端（terminal）——新增
   2.7.1 概览与原则（跟随系统、热重载的两类、只管交互终端）
   2.7.2 profiles：探测到的 id 一览（各平台）、覆盖与自定义、字段表、信任（MSYS2/Cygwin）
   2.7.3 启动：defaultProfile、cwd、newTabCwd、env（null 删除、变量展开、合并顺序摘要）、term、setLocaleVariables、reloadEnvironment
   2.7.4 外观：font、cursor、colors、scrollback、unicode、rendering
   2.7.5 键盘、鼠标与选择、链接、粘贴、响铃、剪贴板
   2.7.6 Shell 集成：开关、支持的 shell、失败原因、手动 source 的片段、对 rc 文件的影响
   2.7.7 生命周期：confirmClose、ignoreProcesses、onExit、title
   2.7.8 会话保活：persistence.*（含快照中可能有敏感输出的提示）
   2.7.9 图形：graphics.*
   2.7.10 SSH：ssh.*（链接到 docs/terminal/08-ssh.md 的用户部分）
   2.7.11 与 Agent 的关系：说明 agent.shell.* 是独立的顶层节（链接到 09 §2.2），以及 agent.shell.profile 引用 terminal.profiles 的规则
   2.7.12 平台覆盖块示例
§5 界面与写回
   + 左侧导航增加"终端"，子页见本文 §8
```

---

## 8. 设置页 UI 分组建议

左侧导航"通用 / 模型与厂商 / **终端** / 关于"。"终端"下分子页，每个字段旁显示"跟随系统（解析值）"或"已固定"，以及"对新终端生效"的标记。

| 子页 | 内容 |
|---|---|
| 常规 | 默认 profile（下拉，带图标和版本）；起始目录（项目 / home / 自定义）；新标签目录（继承 / 项目 / home）；关闭确认；shell 退出后；标题允许程序设置 |
| Profiles | 主从双栏（与"模型与厂商"一致）：左栏分"探测到的 / 自定义 / SSH 主机"三组，每项有启用开关和 shell 集成状态点；右栏为表单（命令、参数、login、cwd、env 表格、shell 集成、图标），"重新探测"按钮，"用此 profile 打开终端"按钮；探测到的 profile 显示"已覆盖的字段"并可逐项"恢复默认" |
| 外观 | 字体（族、字号、行高、连字、回退）；光标；配色（亮/暗两套，带预览）；最小对比度；粗体高亮；回滚行数；Unicode 宽度 |
| 键盘与鼠标 | Option 作为 Meta；Ctrl+C 复制；不交给 shell 的命令列表；Kitty 键盘协议；win32-input-mode；右键 / 中键行为；Alt 单击移动光标；选词分隔符；选中即复制 |
| 粘贴与链接 | 多行警告、大段粘贴警告、去掉末尾换行；链接识别、允许的 scheme；OSC 52 读 / 写 |
| Shell 集成 | 总开关；命令装饰；命令完成通知（阈值）；每种 shell 的支持情况表；手动 source 的片段（带"复制"按钮）；失败提示开关 |
| 会话与恢复 | 退出时（恢复内容 / 保持运行 / 全部关闭）；保存的行数；无连接宽限期；恢复标签；"清除已保存的终端内容"按钮 |
| 响铃与无障碍 | 声音、视觉、请求注意；朗读输出 |
| SSH | 见 08 §5 |
| Agent | 不在终端页内：放在"Agent"相关设置页，编辑顶层 `agent.shell.*`（09 §2.2）；终端页只放一个跳转链接 |
| 高级 | 环境变量（全局 env 表格，`null` 用"删除"开关表示）；TERM；locale；Windows 环境刷新；渲染（GPU、内置绘制字形、平滑滚动）；Kitty 图形与内存上限；"在 settings.json 中编辑"链接 |

写回规则沿用 settings.md §5：按 JSON 节点写回，只改动涉及的字段，注释会丢失（保存前提示）。平台相关的字段（例如在 Windows 上修改默认 profile）默认写入基础值；字段旁有"仅对此平台"复选框，勾选后写入平台覆盖块 [定]。

---

## 9. 测试计划

### 9.1 单元测试（`tests/TKCode.Terminal.Shell.Tests`，三平台都跑）

**可替换的系统访问**（AOT 友好，手写假实现，不用 Moq）：

```csharp
public interface IShellPlatform
{
    OSPlatformKind Os { get; }                    // MacOS | Linux | Windows
    int WindowsBuild { get; }
    Architecture OsArchitecture { get; }
    IFileProbe Files { get; }                     // Exists、IsExecutable、RealPath、ReadAllText、EnumerateDirectories、GetFileVersion、IsAppExecLink
    IRegistryReader? Registry { get; }            // Windows：OpenSubKey、GetValue、GetSubKeyNames、LastWriteTime
    IAccountInfo Account { get; }                 // Name、Home、Shell（替代 getpwuid）
    IProcessRunner Processes { get; }             // RunAsync(file, args, env, timeout) → (exit, stdout bytes, stderr)
    IMacLocale? Locale { get; }                   // AppleLanguages、AppleLocale
    IReadOnlyDictionary<string, string> HostEnvironment { get; }
}
```

| 测试组 | 用例 |
|---|---|
| Unix 探测 | passwd 为 zsh、`$SHELL` 为 bash（chsh 后过期）→ 默认 zsh；passwd 为 `/usr/bin/false` → 回退 `$SHELL`；`/etc/shells` 含注释、空行、相对路径、tmux、rbash；merged-usr 下 `/bin/bash` 与 `/usr/bin/bash` 去重；同名不同路径的 id（`bash@/opt/homebrew/bin/bash`）；`/etc/shells` 不存在；全部不可用 → 合成 `sh` |
| Windows 探测 | pwsh 排名（7.4 传统安装 vs 7.6 Store vs 7.6 scoop vs 7-preview）的各种组合与 id 分配；只有 Windows PowerShell；Git Bash 的四种来源与 `git-bash.exe` 排除；Lxss 注册表（含 docker-desktop、Modern=1、DefaultDistribution 排序）；没有 Lxss 键时的 `wsl.exe -l -q` 回退：UTF-16LE + BOM + `\0` + `\r\n`、退出码非 0、超时；build < 19041 不探测 WSL；MSYS2 与 Cygwin 标记 RequiresTrust；vswhere JSON 解析与超时 |
| 合并 | 覆盖探测字段、`enabled: false`、自定义缺 `command` 只停用该项、`platforms` 过滤、平台覆盖块深合并（对象合并、数组替换、env 中的 null） |
| LaunchBuilder（macOS） | 各 Kind 的 argv 快照（argv0 为 `-<name>`）；bash 注入时 argv0 不加 `-`、设置 `TKCODE_SI_BASH_LOGIN`；pwsh 的 `-Login` 在第一位；`loginShell: false` 时 PATH 合并 path_helper |
| LaunchBuilder（Windows） | `QuoteArg` 的向量（空串、空格、尾部反斜杠、内嵌引号、连续反斜杠后接引号，对照 CommandLineToArgvW 的往返结果——在 Windows CI 上用真实的 `CommandLineToArgvW` 解析验证）；cmd/VS DevCmd 使用 RawCommandLine；Agent 参数含 `&` 时拒绝；WSL 的 `--cd` 各种来源 |
| 环境 | 剔除规则（每个类别至少一例，包括 `TKCODE_PTYHOST_*`）；`TKCODE_HOME` 保留；用户 env 的 null 删除与覆盖顺序；`{env:VAR}` 展开对照 BaseEnvironment；Windows 键不区分大小写；WSLENV 追加；macOS LANG：已有 UTF-8 不改、`zh-Hans-CN` + `zh_CN` → `zh_CN.UTF-8`、locale 目录不存在 → `LC_CTYPE=UTF-8`、`@rg=` 地区 |
| 变量展开一致性 | 与 `McpVariables` 共用一份 JSON 测试向量文件（`tests/shared/variable-expansion.json`），两边的测试都读取它 |
| 注入计划 | §3.5 参数表中的每一行；zsh 已有 ZDOTDIR 时记入 `TKCODE_SI_ZDOTDIR`；fish 的 XDG_DATA_DIRS 原来未设置 / 为空 / 有值三种情况；nu → NativeExpected；cmd 已有 PROMPT 时的包裹 |
| 关闭确认 | 用假的进程信息源：Executing 状态下前台为 bash → 忙；Prompt 状态 → 空闲；没有集成时前台 vim → 忙；tmux 默认算忙，`ignoreProcesses: ["tmux"]` 后空闲；`-zsh` 归一化；Windows 子树中只有 starship → 空闲 |
| 标题、cwd 继承 | 优先级表的每一级；OSC 7 主机名校验（localhost、本机名、`.local`、WSL 发行版名、远程主机拒绝）；MSYS 路径换算；目录不存在时回退 |
| 设置解析（TKCode.Config.Tests） | §7.5 的三个级别；schema 与已知键一致（`SchemaParityTests`） |

### 9.2 注入脚本的集成测试（真实 shell + 真实 PTY，CI）

**测试方式**：`ShellIntegrationE2ETests` 通过 `LaunchBuilder` 加 `TKCode.Terminal.Pty` 真实启动 shell，用一个只解析 OSC 的小型扫描器（与 host 的 633 扫描器同一份代码）收集事件，**不依赖 UI**。每个 shell 跑同一段脚本：

```
等待握手 → 断言 A、B
输入 "echo hi\r"                 → 断言 E("echo hi", nonce 正确)、C、输出 "hi"、D;0、A、B
输入 "false\r"                   → 断言 D;1
输入 "cd /tmp\r"                 → 断言 OSC 7 的路径为 /tmp（macOS 上为 /private/tmp 的 realpath，按目录身份比较）
输入 "echo 'a;b\\c'\r"           → 断言 E 的转义与还原
输入 "env | grep TKCODE_SI_\r"    → 断言没有输出（注入变量不泄漏）
输入 "exit 3\r"                  → Linux/Windows 断言退出码 3；macOS 断言 03 的 NOTE_EXITSTATUS 拿到 3（spike S1 成功时），否则断言 code 与 signal 都为空（未知），并且 login 的退出码为 0
```

**rc 文件保真**（每个 shell 用一个临时 HOME）：

| 断言 | bash | zsh | fish | pwsh |
|---|---|---|---|---|
| 用户 rc 中定义的别名或函数可用 | `~/.bashrc` | `$ZDOTDIR/.zshrc`（自定义 ZDOTDIR）和 `~/.zshrc` | `config.fish` | `$PROFILE` |
| login 文件被读取（login 模式） | `~/.bash_profile` 设置的变量 | `.zprofile` | `status is-login` | — |
| 历史文件位置不变 | `HISTFILE` 未被改写 | 未设置 HISTFILE 时 `$HISTFILE == $ZDOTDIR/.zsh_history`（用户的 ZDOTDIR） | — | — |
| 环境还原 | — | `echo ${ZDOTDIR-unset}` 与注入前一致 | `XDG_DATA_DIRS` 与注入前完全一致（含"未设置"） | — |
| 用户已有钩子保留 | 已有 DEBUG trap 和 PROMPT_COMMAND 仍然执行 | 已有 precmd 仍然执行 | 已有 `fish_prompt` 内容出现 | oh-my-posh 风格的自定义 `prompt` 仍然输出 |

**CI 矩阵**（GitHub Actions）：

| Runner | Shell 与版本 |
|---|---|
| ubuntu-latest | bash 5.x、zsh 5.9、fish 3.x（apt）、fish 4.x（PPA 或官方 tarball）、pwsh 7（官方包）、nu（release 二进制，只断言 Native 状态） |
| ubuntu（RHEL 8 容器：`rockylinux:8`） | bash 4.4、zsh 5.5（验证老版本；zsh < 5.1 的路径用单元测试覆盖） |
| macos-latest | `/bin/bash` 3.2（`--init-file`）、Homebrew bash 5、`/bin/zsh`、fish（brew）、pwsh（brew）；端到端断言 shell 的非 0 退出码能传回 |
| windows-latest | pwsh 7、Windows PowerShell 5.1（先在测试进程作用域设置 `-ExecutionPolicy RemoteSigned`，另有一个用例保持 Restricted，断言收到 `TKCodeShellIntegrationError=…` 和 `Failed(ExecutionPolicy)`）、Git Bash（runner 自带 Git）、cmd（断言 PROMPT 部分集成：A、B、OSC 9;9） |

- **超时检测**：用一个故意在 `.zshenv` 之前覆盖 ZDOTDIR 的环境（模拟 `/etc/zshenv`：通过 `zsh -d` 以外的方式无法改系统文件，所以在容器测试中写 `/etc/zshenv`），断言 10 s 后进入 `Failed(Timeout)`。测试中把超时缩短为 2 s（`IShellPlatform` 提供时钟）。
- **AOT**：E2E 测试同时以 AOT 发布的 host 运行一次（01 的要求）。

### 9.3 人工验收清单（M4 "与系统一致"）

- macOS：新终端的 `ps -o args= -p $$` 显示 `-zsh`；`exit 3` 后标签保留并显示退出码 3；`echo $LANG` 与 Terminal.app 一致；Homebrew 程序在 PATH 中；
- Linux（GNOME、KDE 各一）：`echo $0` 为 `bash`（非 login）；`$SHELL` 与 passwd 一致；
- Windows：默认 pwsh 与 WT 的默认 profile 相同；WSL 发行版列表与 `wsl -l` 一致；安装软件修改 PATH 后新终端立即可见。

---

## 10. 待决问题与对 01 的补充

| # | 问题 | 本文的处理 | 需要谁决定 |
|---|---|---|---|
| 1 | 01 §6.3 的 `TerminalProfile` 缺少 `PathStyle`、`RawCommandLine`、`Environment`、`RequiresTrust`、`WslDistribution`、`Ssh` 等字段；`IProfileProvider.Detect()` 是同步的，但 vswhere 和 `wsl.exe` 回退需要异步 | §1.2 以向后兼容的方式扩充，并增加 `IAsyncProfileProvider` | 01 维护者确认 |
| 2 | 01 §6.2 的 `PtyLaunch` 只有参数数组；Windows 的 cmd / .bat 需要原始命令行 | 03 §4 已增加 `PtyLaunch.WindowsCommandLine`，本文的 `RawCommandLine` 映射到它 | 已解决 |
| 3 | 01 §3.3 规定 AgentHost 通过环境变量拿 pty host 的地址和令牌：这些变量会被 AgentHost 的 `BashTool` 子进程继承，Agent 执行的任何命令都能连上 pty host | 交互终端已经按 `TKCODE_*` 剔除；`BashTool` 也必须剔除，或者改用文件句柄或参数传递 | 04 / 09 |
| 4 | 01 §3.3 "用户正常退出 TKCode：有程序在运行时汇总确认一次"：在 `keepAlive` 模式下进程不会结束，不需要确认 | §6.1 按 keepAlive 跳过确认 | 01 维护者确认 |
| 5 | libghostty-vt 不解析 OSC 633 | 09 已核实未知序列回调可用，§5.2 改为在回调中解析；字节流扫描只作备选 | 已由 09 解决 |
| 6 | pty host 的环境是第一个 Desktop 的环境，跨 Desktop 重启会过期 | §4.1：Desktop 在 `host/configure` 中发送 BaseEnvironment | 04（IPC 协议需增加该字段） |
| 7 | 01 §4 的测试项目列表里没有 `TKCode.Terminal.Shell.Tests` | §9 建议新增 | 01 / 11 |
| 8 | ~~macOS login 包装会吞掉退出码~~ | 已解决：2026-10-17 用户决定不经 login，改用 argv0 `-zsh`（§3.2） | — |
| 13 | pty host 不能用 `System.Diagnostics.Process`，但 03 的 C API 只有 PTY 模式的启动；`tkcode-pty-spawn` 是 Unix 程序，04 却要求 Windows 上的 `wsl.exe -l` 也经它执行 | §2.8：需要 03 增加管道模式的启动接口，并明确 Windows 上的执行方式 | 03 / 04 |
| 14 | Windows 的宽度模式锁定为 grapheme（03 §7.4），与 D9 "默认兼容模式"不同 | §7.3 的 `unicode.widthMode` 在 Windows 上忽略设置值 | 01 维护者确认 |
| 9 | `exec -la` 的 `-a` 在 `/bin/bash` 3.2 上是否可用 | Terminal.app 的字符串说明它可用；CI 实测 | 实测 |
| 10 | fish ≥ 4 是否自带 OSC 133；nu 默认是否开启 OSC 133 | §5.3.3、§5.3.5 按"自带"设计，并在运行时检测 | 实测 |
| 11 | `TKCODE_TERMINAL_ID` 是否暴露 | 建议暴露（§4.2） | 09 |
| 12 | `terminal` 节采用字段级容错，与 settings.md 中"类型错误 = 文件错误"的规则不同 | §7.1 说明了理由；需要在 settings.md 中写明 | 用户 |

## 11. 来源

**源码**（只读克隆，commit 见文首）：

- VS Code：`src/vs/platform/terminal/node/terminalEnvironment.ts`（:52-280 注入逻辑，:330-380 参数表）、`src/vs/platform/terminal/node/terminalProfiles.ts`（:96-142、:286-336、:347-389、:401-440）、`src/vs/base/node/powershell.ts`（:240-310）、`src/vs/workbench/contrib/terminal/common/scripts/shellIntegration-bash.sh`（:1-60、:151-190、:250-360）、`shellIntegration-env.zsh`、`shellIntegration-profile.zsh`、`shellIntegration-login.zsh`、`shellIntegration-rc.zsh`（:1-40）、`shellIntegration.ps1`（:1-40、:95-140）
- Ghostty：`src/termio/Exec.zig`（:1870-2020）、`src/termio/shell_integration.zig`（:43-160、:299-410、:620-700、:779-830、:919-948）、`src/shell-integration/README.md`、`src/shell-integration/zsh/.zshenv`、`src/shell-integration/fish/vendor_conf.d/ghostty-shell-integration.fish`、`src/shell-integration/bash/ghostty.bash`（文件头许可）、`src/terminal/osc/parsers/semantic_prompt.zig`、`src/config/Config.zig`（:2520-2522）
- Windows Terminal：`src/cascadia/TerminalSettingsModel/WslDistroGenerator.cpp`（:15-110、:195-215）、`PowershellCoreProfileGenerator.cpp`、`VsDevCmdGenerator.cpp`、`VsDevShellGenerator.cpp`、`defaults.json`、`src/cascadia/TerminalConnection/ConptyConnection.cpp`、`src/inc/til/env.h`
- iTerm2：`sources/Locale/iTermLocaleGuesser.swift`（:80-230）
- 本仓库：`src/TKCode.Config/{Settings,SettingsFile,SettingsSchema,McpServerConfig,McpVariables,ConfigPaths}.cs`、`settings.schema.json`、`src/TKCode.Desktop/Services/UserSettingsService.cs`、`src/TKCode.AgentHost/SettingsWatcher.cs`、`LICENSE`（Apache-2.0）

**文档**：

- about_Pwsh（`-Login` 必须是第一个参数；`-Command` 必须是最后一个参数；`-NoExit`）：https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_pwsh
- 命令行参数解析规则：https://learn.microsoft.com/cpp/c-language/parsing-c-command-line-arguments
- WT shell 集成教程（cmd 的 PROMPT 写法）：https://learn.microsoft.com/windows/terminal/tutorials/shell-integration
- `man 1 login`、`man 8 path_helper`（macOS 26.6.2 本机）
- 其余结论引用 research/02、03、05 中已经标注的来源
