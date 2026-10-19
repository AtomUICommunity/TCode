# 终端调研 05：与系统终端行为一致 + 用户配置

> 调研日期：2026-10-16。范围：默认 shell 的探测与启动、环境变量、shell 集成、键盘、鼠标与选择、粘贴安全、外观默认值、生命周期、可访问性，以及 `settings.json` 中 `terminal` 节的草案。
>
> 标注：`[实]` = 已在源码、本机或官方文档中核实；`[推]` = 推断，实现前需要验证。源码引用的格式为 `前缀 路径:行号`，前缀与 commit 的对应关系见 §0。

## 0. 调研对象与版本

| 前缀 | 对象 | 版本 / commit | 说明 |
|---|---|---|---|
| VSC | microsoft/vscode | `4765bd3`（2026-10-16） | 终端相关目录的 sparse 克隆 |
| XT | xtermjs/xterm.js | `991857a`（2026-10-13） | VS Code 终端的内核 |
| WT | microsoft/terminal（Windows Terminal） | `44b440b`（2026-10-13） | |
| GH | ghostty-org/ghostty | `b3dc3d7`（2026-10-15） | |
| WZ | wezterm/wezterm | `e2a98e1`（2026-10-10） | |
| AL | alacritty/alacritty | `5421470`（2026-10-16） | |
| IT | gnachman/iTerm2 | `5794785`（v20261016-nightly，3.7） | |
| VTE | GNOME/vte | `a5c88ec`（2026-10-16） | GNOME Terminal 与 Ptyxis 的内核 |
| GT | GNOME/gnome-terminal | `1079728`（3.97.1，GTK4 版） | |
| PX | GNOME/ptyxis | `ecc97d3`（50.2） | Fedora / Ubuntu 新版的默认终端 |
| KO | KDE/konsole | `6ab4bb0`（2026-10-13） | |
| AV | AvaloniaUI/Avalonia | `8973430`（master，12.2.999） | TKCode 当前用 12.1.4，与 AtomUI 6.2.6 的最低版本一致 |
| LOGIN | apple-oss-distributions/system_cmds `login/login.c` | `408bba7453` | macOS 的 login(1) |
| local | 本机 macOS 26.6.2（25G83），Terminal.app 2.15（470.2） | | 只读命令：`ps`、`strings`、`plutil`、`cat /etc/*` |

## 结论摘要

1. **默认 shell 取账户里登记的登录 shell。** 顺序为 `getpwuid()->pw_shell` → `$SHELL` → `/bin/sh`。系统终端（login(1)、VTE、Ptyxis）以及 WezTerm 和桌面启动的 Ghostty 都以 passwd 为准，因为 `chsh` 之后 `$SHELL` 可能过期。
2. **按平台决定是否启动 login shell。**
   - macOS 启动 **login shell**：Terminal.app、iTerm2、Ghostty、Alacritty 都借助 `/usr/bin/login`，VS Code 用 `-l` 参数。原因是从 Dock 启动的 GUI 程序只有 `PATH=/usr/bin:/bin:/usr/sbin:/sbin`，而 path_helper 和 Homebrew 的 PATH 只在 login shell 中设置。
   - Linux 启动**非 login 的交互式 shell**：GNOME Terminal、Ptyxis、Konsole、VS Code、Ghostty、Alacritty 都是这样，只有 WezTerm 例外。
   - Windows 跟随 Windows Terminal：默认用 PowerShell 7（按 WT 的规则挑选最佳安装），没有时用 Windows PowerShell。另外探测 cmd、WSL、Git Bash、VS 开发者命令行和 MSYS2/Cygwin。
3. **环境变量。**
   - 以 TKCode 进程的原始环境为基础，不使用 Agent 的 PATH 快照。
   - 剔除 TKCode 内部变量、启动器/桌面会话注入的变量、其他终端遗留的变量，以及 `SHLVL`。
   - 设置 `TERM=xterm-256color`（仅 Unix）、`COLORTERM=truecolor`、`TERM_PROGRAM=TKCode` 和 `TERM_PROGRAM_VERSION`。
   - macOS 上 `LANG` 缺失时按**系统**区域生成（Terminal.app 和 iTerm2 的做法），而不是像 VS Code 那样按应用界面语言生成。按界面语言生成会让中文界面用户的命令输出也变成中文。
4. **Shell 集成默认自动注入。**
   - VS Code、Ghostty、kitty 默认注入；Windows Terminal、WezTerm、iTerm2 默认不注入。
   - 建议默认开启，注入方式：zsh 用 ZDOTDIR，bash 用 `--init-file` 模拟启动文件，fish 用 XDG_DATA_DIRS，pwsh 用 `-NoExit -Command`。
   - 协议用 OSC 133 + OSC 7，命令行文本用带 nonce 的 OSC 633;E。
   - 没有集成时，退化为 Windows Terminal 的"按回车即打标记"启发式。
5. **键盘：哪些键归应用、哪些键交给 shell。**
   - macOS：Cmd 组合键一律归应用（Terminal.app/iTerm2 的惯例），Ctrl 和 Option 交给 shell。
   - Windows/Linux：Ctrl+Shift 组合键归应用，单独的 Ctrl 和 Alt 交给 shell。
   - Windows：Ctrl+C 在有选区时复制，Ctrl+V 粘贴，这与 WT 新装时的默认一致。
   - **冲突：** TKCode 的 ⌘K（命令面板）与 macOS 终端惯例的 ⌘K 清屏冲突。Win/Linux 上 TKCode 的全局键 Ctrl+K/N/B 由窗口的 Tunnel 处理器拦截，会抢走 readline 的 kill-line、下一条历史、光标左移，需要用户决策（§4.6、§11）。
6. **鼠标。**
   - 右键：Windows 有选区时复制、否则粘贴；macOS 选中单词并弹出上下文菜单；Linux 弹出上下文菜单。
   - Linux 上选中的文本自动进入 PRIMARY，中键粘贴 PRIMARY。**Avalonia 只在 master 分支（预计 12.2）的 X11 后端上支持 PRIMARY**，TKCode 当前用的 12.1.4 还没有。
7. **粘贴。**
   - 多行警告默认 `auto`：只在 bracketed paste 关闭且内容含换行时警告，WT、VS Code、Ghostty 都是这样。
   - 始终过滤 ESC 等控制字符，并把 LF 转成 CR；单行粘贴去掉末尾换行。
   - 拖放文件时按 shell 种类转义路径。
8. **外观。**
   - 字体、字号、光标闪烁跟随系统。macOS 用系统等宽字体 `.SF NS Mono`，回退 Menlo；Windows 用 Cascadia Mono 12pt；Linux 从 portal 读 `monospace-font-name`。
   - Avalonia 不提供光标闪烁频率和系统提示音 API，需要分平台 P/Invoke。
   - ANSI 调色板跟随 TKCode 的明暗主题。
9. **生命周期。**
   - 关闭确认：Unix 上用 `tcgetpgrp(pty) ≠ shell` 判断是否有前台程序，再结合忽略列表决定是否确认。
   - shell 正常退出时关闭标签；非 0 退出时保留标签并显示退出码（对应 Terminal.app 的 "Close if the shell exited cleanly" 和 WT 的 `automatic`）。
   - 重启后恢复标签和 cwd，回滚内容可选恢复。
10. **可访问性：** Avalonia 没有公开的 UIA Text pattern provider，只有 interop 定义。只能用 `LiveSetting` 播报新输出，再加一个 VS Code 那样的"可访问缓冲区"文本视图兜底。

---

## 1. 默认 shell 的探测与启动

### 1.1 macOS：系统终端怎么做

| 终端 | shell 从哪里来 | 启动命令 | login shell | 来源 |
|---|---|---|---|---|
| Terminal.app 2.15 | 目录服务。login(1) 按 `pw_shell` exec，并据此设置 `SHELL` | 进程树为 `Terminal → login -pf token → -zsh`。二进制中有 `-pf`、`-pfl`、`-pfq`、`-pfql` 和 `exec -la %@ %@` 等字符串，推测指定工作目录时用 `login -pfl <user> /bin/bash -c "exec -la <name> <shell>"` | 是（argv0 为 `-zsh`） | local `ps` / `strings` [实]；参数含义 [推]；LOGIN:954-955、1025-1053 [实] |
| Terminal.app 设置 "Shells open with" | "Default login shell" 或 "Command (complete path)" | 自定义命令同样经 login(1) 启动，因此仍是 login shell | 是 | https://support.apple.com/en-us/guide/terminal/trmlstrtup/2.15/mac/26 [实] |
| iTerm2 | OpenDirectory 的 `UserShell`（相当于 `dscl . read /Users/$USER UserShell`） | `/usr/bin/login -f[q]pl $USER <bundle>/ShellLauncher --launch_shell`。ShellLauncher 以 argv0 `-<basename>` 调用 execvp；有 `~/.hushlogin` 时加 `q` | 是 | IT sources/Settings/Profiles/ITAddressBookMgr.m:782-811；sources/Tasks/shell_launcher.c:1-70；sources/System/iTermOpenDirectory.m:15-53 [实] |
| Ghostty | 从桌面启动时用 `getpwuid_r` 的 `pw_shell`；只在"像是从命令行启动"（`TERM_PROGRAM` 非空等）时才用 `$SHELL` | `/usr/bin/login [-q] -flp <user> /bin/bash --noprofile --norc -c "exec -l <shell>"` | 是 | GH src/termio/Exec.zig:1883-1960；src/config/Config.zig:4817-4885 [实] |
| Alacritty | `$SHELL` → passwd | `/usr/bin/login -flp <user> /bin/zsh -fc "exec -a -<name> <shell>"`，有 `.hushlogin` 时用 `-qflp` | 是 | AL alacritty_terminal/src/tty/unix.rs:141-191 [实] |
| WezTerm | 启动时先 unset `SHELL`（`chsh` 后可能过期），再查 passwd | 不经过 login，只把 argv0 设为 `-<basename>` | 是 | WZ env-bootstrap/src/lib.rs:236-240；pty/src/cmdbuilder.rs:526-535 [实] |
| VS Code | `$SHELL` → `os.userInfo().shell` → `sh`；`/bin/false` 改为 `/bin/bash` | 默认 profile 中 zsh/bash/fish 带 `-l`；没有匹配 profile 时，zsh/bash 用 `--login` | 是 | VSC src/vs/base/node/shell.ts:29-62；src/vs/platform/terminal/common/terminalPlatformConfiguration.ts:287-311；src/vs/workbench/contrib/terminal/browser/terminalProfileResolverService.ts:238-270 [实] |

**为什么 macOS 一定要启动 login shell**

- **GUI 程序的环境很少。** 从 Dock 启动的程序（本机实测 VS Code 的进程环境）只有：
  - `PATH=/usr/bin:/bin:/usr/sbin:/sbin`、`SHELL=/bin/zsh`；
  - HOME、USER、LOGNAME、TMPDIR、SSH_AUTH_SOCK；
  - `__CFBundleIdentifier`、`XPC_SERVICE_NAME`、`XPC_FLAGS`、`COMMAND_MODE`。

  **没有 `LANG`，也没有 `TERM`** [实 local `ps -wwE`]。
- **PATH 的补全只发生在 login shell 中。**
  - `/etc/zprofile` 调用 `/usr/libexec/path_helper`。它把 `/etc/paths` 和 `/etc/paths.d/*` 排在前面，原有 PATH 中的条目移到末尾 [实 `man 8 path_helper` + 本机实测]。
  - zsh 只在 login shell 中读取 `/etc/zprofile` 和 `~/.zprofile`；Homebrew 推荐的 `eval "$(brew shellenv)"` 也写在 `~/.zprofile` 中。
- **macOS 26 的 `/etc/zprofile` 还会补 LANG：** `if [ -z "$LANG" ]; then export LANG=C.UTF-8; fi` [实 local]。
- **login(1) 的行为** [实 LOGIN 与 `man 1 login`]：
  - `-p` 保留环境；
  - 不带 `-l` 时 chdir 到 home（LOGIN:937-951）；
  - 按 `pw_shell` 设置 `SHELL`（LOGIN:954-955）；
  - PATH 只在未设置时才设为 `/usr/bin:/bin`（LOGIN:974-976）；
  - 检查 home 下的 `.hushlogin`（LOGIN:671-676），没有就打印 "Last login: …" 和 motd。
- **Ghostty 选择 login(1) 而不是只改 argv0 的理由**（GH src/termio/Exec.zig:1883-1927）：
  - 只把 argv0 改成 `-zsh` 时，`getlogin()` 返回值不对，`SHELL` 也不会被设置；
  - 用 `-l` 可以避免 login 切换到 home，再用 bash 的 `exec -l` 让目标 shell 成为 login shell；
  - 用 bash 而不用 zsh 来 exec，是因为实测快约 2 倍。
- **VS Code 的额外处理：**
  - 启动时执行 `$SHELL -i -l -c` 解析一次 shell 环境（超时 10 秒，`application.shellEnvironmentResolutionTimeout`），终端从这个环境继承（VSC src/vs/platform/shell/node/shellEnv.ts:102-160）[实]；
  - 因为 path_helper 会打乱顺序，另用 `VSCODE_PATH_PREFIX` 在启动文件执行后补回扩展要求的 PATH 前缀（VSC src/vs/platform/terminal/node/terminalEnvironment.ts:279-313）[实]。

### 1.2 Linux：系统终端怎么做

| 终端 | shell 从哪里来 | login shell | 来源 |
|---|---|---|---|
| VTE `vte_get_user_shell()` | 只用 `getpwuid(getuid())->pw_shell` | — | VTE src/vtegtk.cc:3226-3235 [实] |
| GNOME Terminal | 子进程环境中的 `$SHELL`（须可执行且非 setuid）→ `pw_shell` → 固定列表（bash/zsh/tcsh/ksh/csh/sh） | profile 键 `login-shell` 默认 `false`；为 true 时 argv0 前加 `-` | GT src/eggshell.cc:58-93；src/terminal-screen.cc:1849-1870 [实] |
| Ptyxis | ptyxis-agent 在**宿主机**上用 `getpwuid`。Flatpak 版通过 `flatpak-spawn --host` 把 agent 跑在宿主机上，注释原话是 "whatever our sandbox tells us is a lie" | `login-shell=false`；为 true 时追加 `-l`。因为要穿过容器和 Flatpak 层，无法控制 argv0 | PX agent/ptyxis-agent-impl.c:352-427；src/ptyxis-client.c:244-264、710-713；src/ptyxis-util.c:76-96 [实] |
| Konsole | profile 的 `Command` 取 `$SHELL`；运行时依次为 profile 指定的程序 → `pw_shell` → `$SHELL` → `/bin/sh` | 否 | KO src/profile/Profile.cpp:236-262；src/session/Session.cpp:509-535 [实] |
| Ghostty | 同 §1.1 | 否：`/bin/sh -c "<shell>"`，Flatpak 下加 `-l` | GH src/termio/Exec.zig:2009-2019 [实] |
| Alacritty | `$SHELL` → passwd | 否 | AL alacritty_terminal/src/tty/unix.rs:161-164 [实] |
| WezTerm | passwd | **是**（argv0 `-`），属于例外 | WZ pty/src/cmdbuilder.rs:526-535 [实] |
| VS Code | `$SHELL` → `userInfo()` | 否：Linux 的默认 profile 不带参数 | VSC terminalPlatformConfiguration.ts:341-362 [实] |

**沙箱与打包格式**

| 场景 | 系统终端/同类应用的做法 | 来源 |
|---|---|---|
| Flatpak | 用 `/.flatpak-info` 判断是否在 Flatpak 中。通过 `flatpak-spawn --host --env=K=V --directory= --forward-fd=N` 在宿主机上启动（需要 `talk-name=org.freedesktop.Flatpak`），环境变量要逐个显式传入。shell 用宿主机上的 `getent passwd` 获取（Ghostty、Ptyxis、Konsole 都是这样） | PX agent/ptyxis-run-context.c:1163-1216；GH src/os/passwd.zig:51-80；KO src/session/Session.cpp:467-487 [实] |
| VS Code Flatpak 版 | 让用户手动配置 profile：`/usr/bin/flatpak-spawn --host --env=TERM=xterm-256color bash` | https://github.com/flathub/com.visualstudio.code [实] |
| Snap | VS Code 剔除 `SNAP*`；WezTerm 剔除 `SNAP*` 和 `LD_LIBRARY_PATH`；Ghostty 过滤掉 snap 路径 | VSC src/vs/base/common/processes.ts:109-114；WZ env-bootstrap/src/lib.rs:14-51；GH src/apprt/gtk/class/surface.zig:1606-1636 [实] |
| AppImage | WezTerm 剔除 `APPIMAGE`、`APPDIR`、`OWD`、`ARGV0` | WZ env-bootstrap/src/lib.rs:14-51 [实] |

**工作目录**

- GNOME Terminal 新开窗口时用 home（GT src/terminal-screen.cc:1389-1392）。
- 新标签继承当前目录时，GNOME Terminal 和 Ptyxis **只用 OSC 7** 的结果（`preserve-working-directory='safe'`，只在前台是 shell 时才继承）；Konsole 先用 OSC 7，没有时读 `/proc/<pid>/cwd`（GT src/terminal-screen.cc:1783-1801；PX src/ptyxis-client.c:723-748；KO src/session/Session.cpp:300-311）[实]。

### 1.3 Windows：系统终端与 VS Code 怎么做

**系统默认终端。** Windows 11 22H2 起，Windows Terminal 是系统默认的命令行宿主：所有命令行程序都在 WT 中打开（https://devblogs.microsoft.com/commandline/windows-terminal-is-now-the-default-in-windows-11/ ，2022-10-18）[实]。

**Windows Terminal 的默认 profile**

- `defaults.json` 中写的是 Windows PowerShell `{61c54bbd-…}`（WT src/cascadia/TerminalSettingsModel/defaults.json:3）。
- 但首次运行时，WT 会把 `defaultProfile` 改为生成的 "PowerShell"，即最佳的 pwsh 安装；没有 pwsh 才回退 Windows PowerShell（CascadiaSettingsSerialization.cpp:274-295、1264-1289）[实]。
- **结论：新装的 WT 在装有 pwsh 时默认用 pwsh。**

**Windows Terminal 的动态 profile 生成器**

生成顺序为 PowerShellCore → WSL → Azure → VisualStudio → SSH（CascadiaSettingsSerialization.cpp:222-229）[实]。

| 生成器 | 如何发现 | 命令行 | 起始目录 | 来源（TSM = src/cascadia/TerminalSettingsModel） |
|---|---|---|---|---|
| PowerShell 7 | `%ProgramFiles%\PowerShell\<ver>\pwsh.exe`（含 x86/ARM 目录）；Store/MSIX 的 `%LOCALAPPDATA%\Microsoft\WindowsApps\Microsoft.PowerShell[Preview]_8wekyb3d8bbwe\pwsh.exe`；`%USERPROFILE%\.dotnet\tools\pwsh.exe`；`%USERPROFILE%\scoop\shims\pwsh.exe`。**排名**：主版本高者优先；同版本时 Store > Scoop > dotnet > 传统安装 > WOW > Preview。第一名命名为 "PowerShell" | `"<路径>\pwsh.exe"` | `%USERPROFILE%` | TSM/PowershellCoreProfileGenerator.cpp:52-135、150-346 [实] |
| Windows PowerShell | 静态条目 | `%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe` | `%USERPROFILE%` | TSM/defaults.json:38-56 [实] |
| cmd | 静态条目 | `%SystemRoot%\System32\cmd.exe` | `%USERPROFILE%` | TSM/defaults.json:57-75 [实] |
| WSL | 读注册表 `HKCU\…\Lxss\{guid}\DistributionName`，**不运行 wsl.exe**。跳过 `docker-desktop*`、`rancher-desktop*` 和 `Modern=1` 的发行版 | `wsl.exe -d <name>` | `~`（build ≥ 19041） | TSM/WslDistroGenerator.cpp:19-101、205-209 [实] |
| Azure Cloud Shell | 只在官方构建（ClientID 非 "0"）中启用 | `connectionType` | — | src/cascadia/TerminalConnection/AzureConnection.cpp:73-76 [实] |
| VS Developer Command Prompt / PowerShell | 通过 COM 接口 `ISetupConfiguration2`（vswhere 的底层）发现；旧版本实例默认隐藏 | DevCmd：`cmd.exe /k "<inst>\Common7\Tools\VsDevCmd.bat" -startdir=none -arch=x64 -host_arch=x64`；DevShell：`pwsh/powershell -NoExit -Command "&{Import-Module …DevShell.dll; Enter-VsDevShell <id> -SkipAutomaticLocation …}"` | VS 安装目录 | TSM/VsSetupConfiguration.cpp:13-30；VsDevCmdGenerator.cpp:39-67；VsDevShellGenerator.cpp:11-89 [实] |
| SSH | 在 OpenSSH 目录中找 ssh.exe；解析 `%ProgramData%\ssh\ssh_config` 和 `~\.ssh\config`，每个带 HostName 的 Host 生成一个 "SSH - <host>" profile | `"<ssh.exe>" <host>` | 继承 | TSM/SshHostGenerator.cpp:13-167；src/features.xml:105-111 [实] |
| Git Bash | **没有内置生成器**。Git for Windows 自己安装 JSON fragment | — | — | [实]（grep 无结果）/ fragment [推] |

**VS Code 的探测逻辑**

- **默认 shell：** 第一个找到的 PowerShell（VSC src/vs/base/node/shell.ts:65-70）。查找顺序（VSC src/vs/base/node/powershell.ts:250-305）[实]：
  1. `ProgramFiles\PowerShell\<n>\pwsh.exe`；
  2. 另一位数的 Program Files；
  3. MSIX；
  4. `~\.dotnet\tools`；
  5. Preview（同样依次查 Program Files、MSIX、另一位数）；
  6. scoop（`~\scoop\apps\pwsh\current`）；
  7. Windows PowerShell。

| 自动探测的 profile | 路径 / 参数 | 来源（VSC src/vs/platform/terminal/node/terminalProfiles.ts） |
|---|---|---|
| PowerShell | 按上述顺序 | :96-101、338-345 |
| Windows PowerShell | `%windir%\System32\…\powershell.exe`；32 位进程运行在 64 位系统上时用 `Sysnative`（否则 PSReadLine 不可用） | :80-85、102-106 |
| Git Bash | 先找 PATH 上的 `git.exe`，取其上两级目录；再查 `ProgramW6432`、`ProgramFiles`、`ProgramFiles(X86)`、`LocalAppData\Program`。每个候选目录依次试 `Git\bin\bash.exe`、`Git\usr\bin\bash.exe`、`usr\bin\bash.exe`；最后查 scoop 的 `git` / `git-with-openssh`。参数 `--login -i` | :107-111、286-336 |
| Command Prompt | `System32\cmd.exe` | :112-116 |
| Cygwin | `%HOMEDRIVE%\cygwin64\bin\bash.exe`、`%HOMEDRIVE%\cygwin\bin\bash.exe`，参数 `--login`，**标记为 unsafe**（普通用户可以在 C:\ 下创建这些目录，首次使用前要确认） | :117-124 |
| MSYS2 | `%HOMEDRIVE%\msys64\usr\bin\bash.exe`，参数 `--login -i`，环境 `CHERE_INVOKING=1`（保持当前目录），unsafe | :125-134 |
| Cmder | `cmd.exe /K %CMDER_ROOT%\vendor\bin\vscode_init.cmd` | :135-142 |
| WSL | 要求 build ≥ 19041；运行 `wsl.exe -l -q`（强制 UTF-16LE，`WSL_UTF8=0`，超时 1 秒），跳过 `docker-desktop*`；参数 `-d <distro>` | :87-89、347-389 |
| Unix 平台 | 读 `/etc/shells`，每行一个 profile | :401-440 |

VS Code 还单独提供 **`terminal.integrated.agentHostProfile.*`**：AI Agent 工具使用的 shell 可以与交互终端分开配置（VSC terminalPlatformConfiguration.ts:171-192）[实]。这与 TKCode 的现状一致：Agent 的 `ShellResolver` 在 Windows 上优先 Git Bash，交互终端则应跟随系统。

### 1.4 VS Code 在各平台的默认 shell 与启动参数（汇总）

| 平台 | 可执行文件 | 参数 | 注入 shell 集成后的参数 |
|---|---|---|---|
| macOS | `$SHELL` | zsh/bash/fish 用 `-l` | zsh：`-il` 并设置 ZDOTDIR；bash：`--init-file <脚本>` 并设 `VSCODE_SHELL_LOGIN=1`（由脚本模拟 login）；fish：`-l --init-command 'source …'`；pwsh：`-l -noexit -command '. …'` |
| Linux | `$SHELL` | 无 | zsh：`-i` 并设置 ZDOTDIR；bash：`--init-file`；fish：`--init-command`；pwsh：`-noexit -command` |
| Windows | 第一个 PowerShell | 无 | pwsh/powershell：`-noexit -command 'try { . "<脚本>" } catch {}'`；Git Bash：`--init-file` |

来源：VSC src/vs/platform/terminal/node/terminalEnvironment.ts:52-277、327-368 [实]。如果用户自己写了无法识别的参数，就不注入（返回 `UnsupportedArgs`）。

### 1.5 TKCode 建议默认

| 项 | 建议 | 依据 |
|---|---|---|
| Unix 上的 shell 来源 | `getpwuid(getuid())->pw_shell`（存在且可执行）→ `$SHELL` → macOS 用 `/bin/zsh`、Linux 用 `/bin/sh` | login(1)、VTE、Ptyxis、WezTerm、桌面启动的 Ghostty 都以 passwd 为准，`$SHELL` 可能在 chsh 后过期。**不要**沿用 Agent `ShellResolver` 中"只接受 bash/zsh"的限制，fish 和 nu 用户也要能直接用 |
| macOS 启动方式 | **方案 A（推荐）：** `/usr/bin/login [-q] -flp <user> /bin/bash --noprofile --norc -c 'exec -l <shell>'`，有 `~/.hushlogin` 时加 `-q`；cwd 由 TKCode 在 fork 后自己 chdir（`-l` 让 login 不切到 home）。**方案 B：** 直接 exec shell，argv0 设为 `-zsh`（WezTerm 的做法） | A 与 Terminal.app、iTerm2、Ghostty、Alacritty 一致：有 utmp 记录，`getlogin()` 正确，会显示 "Last login"。B 省去 setuid 的 login，但这三点都和系统终端不同。见 §11 决策 2 |
| macOS 进程树 | 方案 A 下 PTY 的直接子进程是 root 所有的 `login`，shell 是孙进程。判断前台进程和 cwd 时用 `tcgetpgrp(master)` 和会话首进程，不要依赖直接子进程的 pid | [推]；Ghostty 也提到 login 包装会吞掉退出码（GH src/Surface.zig:1277-1353） |
| Linux 启动方式 | 非 login 的交互式 shell；用户设置 `loginShell: true` 时把 argv0 设为 `-<basename>`（GNOME Terminal 的做法） | GNOME Terminal、Ptyxis、Konsole、VS Code、Ghostty、Alacritty 一致 |
| Windows 默认 | 最佳 pwsh（采用 WT 的排名规则）→ Windows PowerShell | WT 首次运行的默认值，与 VS Code 一致 |
| Windows 自动探测 | pwsh（各安装形态分别列出）、Windows PowerShell、cmd、Git Bash（VS Code 的路径表）、WSL（WT 的读注册表方式，不用启动 `wsl.exe`）、MSYS2/Cygwin（VS Code 的路径，标记 unsafe，首次使用前确认）；VS 开发者命令行可以放到后续版本 | 结合 WT 与 VS Code。读注册表避免了 `wsl.exe -l` 唤醒 WSL 的开销 [推] |
| Agent 与交互终端分开 | `terminal.*` 只影响交互终端；Agent 继续用 `ShellResolver`（非交互、`-c`） | VS Code 的 `agentHostProfile` 也是这样分开的 |
| 起始目录 | 当前项目根目录；新标签/分屏继承当前标签的 cwd，来源依次为 OSC 7（shell 集成）→ Linux `/proc/<pid>/cwd` → macOS `proc_pidinfo(PROC_PIDVNODEPATHINFO)` [推] → 项目根目录 | VS Code（工作区根目录，`splitCwd: inherited`）、GNOME Terminal、Konsole |
| 环境基础 | 使用 TKCode **启动时的原始环境**，不用 `ExecutableLocator.ChildPath()` 生成的 PATH。在 macOS 上由 login shell 自己重建 PATH | 与 Terminal.app 一致；也避免了 VS Code 遇到的 path_helper 重排问题 |
| 跟随"系统终端的用户设置"（可选，以后做） | 读取 WT `settings.json` 的 `defaultProfile`、Terminal.app 偏好中的 Shell、GNOME Terminal 默认 profile 的 `custom-command` | 能更彻底地"与系统终端一致"，但这些都是非公开格式，先不做 |

---

## 2. 环境变量

### 2.1 各终端设置的变量

| 变量 | Terminal.app | iTerm2 | Windows Terminal | VTE / GNOME Terminal / Ptyxis | Konsole | VS Code | Ghostty | WezTerm | Alacritty |
|---|---|---|---|---|---|---|---|---|---|
| `TERM` | profile 的 "Declare terminal as"，默认 `xterm-256color` [推] | 新 profile 为 `xterm-256color` | **不设** | `xterm-256color` | `xterm-256color` | Unix 上 `xterm-256color`（node-pty `name`），Windows 不设 | 找到资源目录时 `xterm-ghostty` 加 `TERMINFO=<res>/terminfo`，否则 `xterm-256color` | `xterm-256color` | 有 terminfo 时 `alacritty`，否则 `xterm-256color` |
| `COLORTERM` | 有 `truecolor` 字符串 [推] | `truecolor` | 不设 | `truecolor`（VTE 强制设置，不允许覆盖） | `truecolor` | `truecolor` | `truecolor` | `truecolor` | `truecolor` |
| `TERM_PROGRAM` / `_VERSION` | `Apple_Terminal` / 版本 [推] | `iTerm.app` / 版本 | 不设 | 不设 | 不设（设 `KONSOLE_VERSION`） | `vscode` / 版本 | `ghostty` / 版本 | `WezTerm` / 版本 | 不设 |
| 会话 id | `TERM_SESSION_ID` | `ITERM_SESSION_ID`、`TERM_SESSION_ID`、`ITERM_PROFILE` | `WT_SESSION`、`WT_PROFILE_ID` | `GNOME_TERMINAL_SCREEN/SERVICE`；Ptyxis 设 `PTYXIS_PROFILE` | `KONSOLE_DBUS_*`、`SHELL_SESSION_ID` | — | `GHOSTTY_SURFACE_ID` | `WEZTERM_PANE` | `ALACRITTY_WINDOW_ID` |
| `LANG` / `LC_*` | "Set locale environment variables on startup"：设为 `<lang>_<REGION>.<enc>`，失败时设 `LC_CTYPE` [推] | 默认自动：按首选语言 × 国家地区猜测并验证，失败时设 `LC_CTYPE`；另设 `LC_TERMINAL=iTerm2` | 不设 | 不设 | `LANGUAGE=` 只在未设置时设 | `detectLocale: auto`：LANG 缺失或不是 UTF-8 时，**按 VS Code 界面语言**生成 | 桌面启动时移除 `LANGUAGE` | macOS 上 LANG 缺失时按 NSLocale 生成 | macOS 上 locale 无效时设 `LC_CTYPE` |
| 其他 | — | `COLORFGBG`、`TERMINFO_DIRS`、`TERM_FEATURES` | 把 `WSLENV` 前置 `WT_SESSION:WT_PROFILE_ID:` 和 profile 中的键 | VTE：`VTE_VERSION`、`PWD`、`TERMINFO_DIRS`；GNOME Terminal：`NCURSES_NO_UTF8_ACS=1` | `COLORFGBG`、`WINDOWID`、`PROFILEHOME` | `VSCODE_*`（shell 集成用） | `GHOSTTY_RESOURCES_DIR`、`GHOSTTY_BIN_DIR`（并加入 PATH）、`WINDOWID` | `WSLENV` 追加 `TERM:COLORTERM:TERM_PROGRAM:TERM_PROGRAM_VERSION` | `WINDOWID` |

来源 [除标 推 的格子外均为实]：
- Terminal.app：local `strings`
- iTerm2：IT sources/PTYSession/PTYSession.m:3316-3443；sources/Locale/iTermLocaleGuesser.swift:87-140；ITAddressBookMgr.m:550
- WT：src/cascadia/TerminalConnection/ConptyConnection.cpp:58-151
- VTE：src/spawn.cc:252-293
- GNOME Terminal：src/terminal-screen.cc:1951-1959
- Ptyxis：src/ptyxis-client.c:667-673
- Konsole：src/profile/Profile.cpp:71；src/session/Session.cpp:596-620
- VS Code：src/vs/workbench/contrib/terminal/common/terminalEnvironment.ts:62-71、98-108、110-180（第 16 行导入的 `language` 是界面语言）；src/vs/platform/terminal/node/terminalProcess.ts:148-155
- Ghostty：src/termio/Exec.zig:634-756
- WezTerm：config/src/config.rs:1606-1622；env-bootstrap/src/lib.rs:118-158
- Alacritty：alacritty_terminal/src/tty/mod.rs:100-150；alacritty/src/macos/locale.rs:13-40

### 2.2 各终端从继承环境中剔除的变量

| 终端 | 剔除内容 | 来源 |
|---|---|---|
| GNOME Terminal（最全） | **按名称：** COLORFGBG、COLORTERM、COLUMNS、LINES、DEFAULT_COLORS、DESKTOP_STARTUP_ID、XDG_ACTIVATION_TOKEN、GIO_LAUNCHED_DESKTOP_FILE(_PID)、INVOCATION_ID、JOURNAL_STREAM、NOTIFY_SOCKET、LISTEN_FDS/FDNAMES/PID、MAINPID、MANAGERPID、WATCHDOG_*、EXIT_CODE/STATUS、SERVICE_RESULT、PIDFILE、LC_TERMINAL、MLTERM、PWD、REMOTE_ADDR/PORT、**SHLVL**、STY、TERM、TERMCAP、TMUX、TMUX_PANE、VTE_VERSION、WINDOWID、WT_SESSION、WT_PROFILE、WCWIDTH_CJK_LEGACY、GJS_DEBUG_*，以及代理变量（之后按 GSettings 重新加入）。**按前缀：** GNOME_TERMINAL_、ALACRITTY_、CONTOUR_、FOOT_、GHOSTTY_、ITERM2_、KITTY_、KONSOLE_、MC_、MINTTY_、MOSH_、PUTTY_、RXVT_、**TERM_**、TERMINAL_、URXVT_、WEZTERM_、XTERM_、ZUTTY_ | GT src/terminal-client-utils.cc:211-305，在 src/terminal-screen.cc:1934-1945 应用 [实] |
| VTE 库本身 | 不剔除任何变量（VTE 的测试程序复制了 GNOME Terminal 的列表） | VTE src/spawn.cc；src/app/app.cc:261-320 [实] |
| Ptyxis | 非沙箱时整个环境原样传入；Flatpak 下只传白名单 | PX agent/ptyxis-session-container.c:107-124 [实] |
| Konsole | 不剔除 | [实]（源码中没有 unsetenv） |
| VS Code | `ELECTRON_*`；`VSCODE_*`（PORTABLE、SHELL_LOGIN、ENV_REPLACE/APPEND/PREPEND 除外）；`SNAP*`；`GDK_PIXBUF_*`。macOS 上把 `VSCODE_NODE_OPTIONS` 还原为 `NODE_OPTIONS` | VSC src/vs/base/common/processes.ts:104-126；terminalEnvironment.ts:278-295 [实] |
| iTerm2 | 只剔除 `SHLVL`、`NSZombieEnabled`、`MallocStackLogging`；`__CFBundleIdentifier` 和 `XPC_*` 保留；`TERM_SESSION_ID` 覆盖为新值 | IT sources/Tasks/PTYTask.m:648、678 [实] |
| Ghostty | 始终剔除 `VTE_VERSION`、`GHOSTTY_LOG`。macOS：剔除 `GHOSTTY_MAC_LAUNCH_SOURCE`，桌面启动时剔除 `LANGUAGE`，在 Xcode 中运行时剔除 `DYLD_*`/`__XPC_*`。GTK：剔除 `GDK_DEBUG`、`GDK_DISABLE`、`GSK_RENDERER`、`GIO_LAUNCHED_DESKTOP_FILE*`、`DBUS_STARTER_*`、`INVOCATION_ID`、`JOURNAL_STREAM`、`NOTIFY_SOCKET` | GH src/termio/Exec.zig:756；src/apprt/embedded.zig:1218-1238；src/apprt/gtk/class/surface.zig:1606-1636 [实] |
| WezTerm | `SHELL`、`WINDOWID`、`VTE_VERSION`、AppImage 和 Snap 相关变量 | WZ env-bootstrap/src/lib.rs:14-51、226-240 [实] |
| Alacritty | `XDG_ACTIVATION_TOKEN`、`DESKTOP_STARTUP_ID` | AL alacritty_terminal/src/tty/unix.rs:239-241 [实] |
| Windows Terminal | 不剔除，而是**从注册表重建**（`compatibility.reloadEnvironmentVariables` 默认 true）：读 HKLM/HKCU Environment 和 Volatile Environment，Path 按"系统;用户"拼接。安装程序修改 PATH 后，新标签马上能看到 | WT src/cascadia/TerminalConnection/ConptyConnection.cpp:296-316；src/inc/til/env.h:237-270、446-476 [实] |

### 2.3 TKCode 建议

**设置的变量**

| 变量 | 值 | 平台 | 依据 |
|---|---|---|---|
| `TERM` | `xterm-256color` | macOS、Linux。Windows 不设，WSL 会自己设 | 所有系统终端都用这个值。**不要自定义 TERM**：自定义值需要随应用分发 terminfo，以后接 SSH 时远端也没有对应条目（Ghostty 为此专门做了 `ssh-terminfo`） |
| `COLORTERM` | `truecolor` | 全部 | 全部终端 |
| `TERM_PROGRAM` / `TERM_PROGRAM_VERSION` | `TKCode` / 版本号 | 全部 | VS Code、Ghostty、WezTerm、iTerm2；方便 CLI 工具识别宿主。**不要**设为 `Apple_Terminal`，否则会触发 `/etc/zshrc_Apple_Terminal` 的会话保存逻辑 |
| `WSLENV` | 追加 `TERM_PROGRAM:TERM_PROGRAM_VERSION:COLORTERM`，以及 profile `env` 中的键 | Windows | WT、WezTerm |
| `LANG` | 见下方"locale"一条 | macOS | Terminal.app、iTerm2 |
| 终端会话 id（可选） | `TKCODE_TERMINAL_ID=<uuid>` | 全部 | 类似 `WT_SESSION`，供以后 Agent 或 CLI 定位"当前终端"使用。**不确定是否需要**，可以先不加 |

**locale（macOS）**

- `setLocaleVariables: auto` 为默认：`LANG` 未设置或不是 UTF-8 时，按 `NSLocale` 的语言和地区生成 `xx_YY.UTF-8`，并检查 `/usr/share/locale/<name>` 存在；不存在时只设 `LC_CTYPE=UTF-8`。
- 这与 Terminal.app 和 iTerm2 一致。**不用 TKCode 界面语言**生成，与 VS Code 的做法不同。
- Linux 和 Windows 不改 locale。

**剔除的变量**

1. **TKCode 自身的变量：** 剔除 `TKCODE_*`，只保留 `TKCODE_HOME`，因为它是面向用户的配置。剔除的例子有 `TKCODE_AGENTHOST_DIR`、`TKCODE_FAKE_PROVIDER`、`TKCODE_EXTERNAL_RUNTIMES`、`TKCODE_DISABLE_*`、`TKCODE_*_PATH`。同时**不要**注入 `TKCODE_AGENT=1`：这个变量是 `BashTool` 给 Agent 命令设置的（src/TKCode.Agent.Coding/Tools/Shell/BashTool.cs:204-207），交互终端不应带上。规则与 VS Code 剔除 `VSCODE_*` 相同。
2. **启动方式注入的 .NET/IDE 变量** [推]：NativeAOT 程序没有宿主，不会自己往环境里加 `DOTNET_*`。环境中出现这些变量，只能是用户或启动它的工具设置的。因此**不要一刀切地剔除 `DOTNET_*`**：用户设置的 `DOTNET_ROOT`、`DOTNET_CLI_TELEMETRY_OPTOUT` 等应当保留。只剔除开发期由启动器注入的变量：
   - `dotnet run` / `watch`：`DOTNET_LAUNCH_PROFILE`、`DOTNET_WATCH*`、`DOTNET_MODIFIABLE_ASSEMBLIES`；
   - MSBuild：`MSBuild*`、`MSBUILD*`；
   - 性能分析器：`DOTNET_STARTUP_HOOKS`、`CORECLR_*`、`COR_ENABLE_PROFILING`/`COR_PROFILER*`，否则任何 dotnet 子进程都会挂上分析器。
3. **桌面会话与启动器的变量：**
   - Linux：`DESKTOP_STARTUP_ID`、`XDG_ACTIVATION_TOKEN`、`GIO_LAUNCHED_DESKTOP_FILE(_PID)`、`INVOCATION_ID`、`JOURNAL_STREAM`、`NOTIFY_SOCKET`、`LISTEN_*`、`MANAGERPID`、`WATCHDOG_*`、`DBUS_STARTER_*`；
   - Snap：`SNAP*`；AppImage：`APPIMAGE`、`APPDIR`、`OWD`、`ARGV0`；
   - macOS：`__CFBundleIdentifier`、`XPC_SERVICE_NAME`、`XPC_FLAGS`。这几个描述的是 TKCode 自己，不是 shell。iTerm2 保留它们，所以这里属于取舍 [推]。
4. **其他终端遗留的变量**（TKCode 从别的终端里启动时会出现）：照搬 GNOME Terminal 的列表，并补上：
   - 前缀 `ITERM_`（`ITERM_SESSION_ID`、`ITERM_PROFILE`）、`VSCODE_`、`WT_PROFILE_ID`、`LC_TERMINAL_VERSION`、`TERM_FEATURES`；
   - 指向其他终端资源目录的 `TERMINFO`/`TERMINFO_DIRS` [推]，例如 Ghostty 设置的 `TERMINFO`。
5. **`SHLVL`、`PWD`、`OLDPWD`：** 剔除，这样新 shell 的 SHLVL 从 1 开始（GNOME Terminal、iTerm2 的做法）。
6. **Windows 环境刷新：** 默认像 WT 一样从注册表重建环境（`reloadEnvironment: true`），用户安装软件修改 PATH 后，新终端不需要重启 TKCode 就能生效。

---

## 3. Shell 集成

### 3.1 各终端的做法

| 终端 | 默认是否注入 | 注入方式 | 协议 | 来源 |
|---|---|---|---|---|
| VS Code | **自动**（`shellIntegration.enabled: true`） | **bash：** `--init-file <脚本>`，脚本自己 source `~/.bashrc`；login 时依次 source `/etc/profile`、`~/.bash_profile`/`~/.bash_login`/`~/.profile`。<br>**zsh：** 在 `${tmpdir}/${user}-${app}-zsh`（权限 0o1700）复制 `.zshrc`、`.zprofile`、`.zshenv`、`.zlogin`，设 `ZDOTDIR` 和 `USER_ZDOTDIR`；脚本中切回用户的 ZDOTDIR 并 source 用户文件，同时设 `HISTFILE=$USER_ZDOTDIR/.zsh_history`。<br>**fish：** `--init-command 'source …'`。<br>**pwsh：** `-noexit -command '. …'`。<br>Windows 支持 pwsh 和 Git Bash。用户参数无法识别时跳过 | OSC 633（A–I、E 命令行、P 属性、EnvJson…）、133、1337、7、9;9；`VSCODE_NONCE` 防止程序输出伪造命令行 | VSC src/vs/platform/terminal/node/terminalEnvironment.ts:52-368；src/vs/workbench/contrib/terminal/common/scripts/shellIntegration-bash.sh:30-68；shellIntegration-env.zsh、shellIntegration-rc.zsh；src/vs/platform/terminal/common/xterm/shellIntegrationAddon.ts:39-322 [实] |
| Ghostty | **自动**（`shell-integration = detect`） | **bash：** 改为 `bash --posix`，设 `ENV=<res>/…/ghostty.bash`；脚本执行 `set +o posix` 后按 login/非 login 自己 source 启动文件。**跳过 macOS 的 `/bin/bash` 3.2**。<br>**zsh：** `ZDOTDIR=<资源目录>`，原值存入 `GHOSTTY_ZSH_ZDOTDIR`，由 `.zshenv` 还原（要求 zsh ≥ 5.1）。<br>**fish / elvish / nushell：** 前置 `XDG_DATA_DIRS`（nushell 另加 `--execute`）。<br>功能默认值：cursor、title、path 开；sudo、ssh-env、ssh-terminfo 关 | OSC 133 A/B/C/D/P（带 `aid=`、`cl=line`）、OSC 7、OSC 2 | GH src/termio/shell_integration.zig:137-948；src/shell-integration/bash/ghostty.bash:22-278；src/config/Config.zig:2915-2940、8899-8906 [实] |
| kitty | **自动**，所有功能默认开启 | zsh 用 ZDOTDIR；fish 用 XDG_DATA_DIRS；bash 用 POSIX 模式 + `ENV`；设 `KITTY_SHELL_INTEGRATION`，读取后由脚本 unset | OSC 133 等 | https://sw.kovidgoyal.net/kitty/shell-integration/ [实] |
| iTerm2 | **不注入**（"Load shell integration automatically" 默认 NO）；可以从菜单安装，或用 curl 安装脚本 | 开启后与 Ghostty 相同：zsh 用 ZDOTDIR（原值存入 `IT2_ORIG_ZDOTDIR`），bash 用 `--posix` + `ENV`，fish 用 XDG_DATA_DIRS；同样跳过 `/bin/bash` 3.2 | OSC 133、OSC 7、OSC 1337（SetUserVar、ShellIntegrationVersion…） | IT sources/ShellIntegration/ShellIntegrationInjection.swift:36-465；iTermProfilePreferences.m:1208；Resources/shell_integration/iterm2_shell_integration.zsh:37-129 [实] |
| WezTerm | **不注入**；`wezterm.sh` 需要用户 source，Fedora/Debian/Arch 的软件包会放到 `/etc/profile.d` | — | OSC 7、133、1337 SetUserVar | WZ docs/shell-integration.md:1-35；assets/shell-integration/wezterm.sh [实] |
| Windows Terminal | **不注入**；文档让用户自己修改 PROMPT/PS1。`autoMarkPrompts` 默认 true：在主屏幕按回车时，把当前行标为提示符 | 无 | 解析 OSC 133、7、9;9、9;12、633（只用 Completions）、1337 SetMark | WT src/cascadia/TerminalCore/Terminal.cpp:740-770；src/terminal/parser/OutputStateMachineEngine.hpp:209-232；https://learn.microsoft.com/en-us/windows/terminal/tutorials/shell-integration [实] |
| VTE / GNOME Terminal | 发行版把 `/etc/profile.d/vte.sh` 装好，但它只在 login shell 或发行版的 bashrc source profile.d 时才生效（Fedora 会，Debian/Ubuntu 的非 login bash 通常不会 [推]） | vte.sh：只对交互式 bash/zsh 生效，并且要求 `VTE_VERSION` ≥ 3405 | OSC 7、OSC 0、OSC 133 A/B/C/D、OSC 666 termprops；Ctrl+Shift+←/→ 在提示符之间跳转 | VTE src/vte.sh.in:16-176；src/vteseq.cc:1826-1854 [实] |
| Terminal.app | 系统自带，`TERM_PROGRAM=Apple_Terminal` 时触发：`/etc/zshrc` 第 74 行 source `/etc/zshrc_$TERM_PROGRAM` | `update_terminal_cwd`（在 precmd 中发 OSC 7）；Resume Support：每个会话一份 `~/.zsh_sessions/$TERM_SESSION_ID.*` 历史与状态 | OSC 7；**提示符标记靠按键**：按回车、Ctrl-C、Ctrl-D 时自动给当前行打标记，不用 OSC 133 | local `/etc/zshrc_Apple_Terminal`:14-44、96-252；https://support.apple.com/guide/terminal/use-marks-and-bookmarks-trml135fbc26/2.15/mac/27 [实] |
| Konsole | 不注入 [推] | — | 解析 OSC 133 A/N/P、B、C、D、L；`SemanticHints` | KO src/Vt102Emulation.cpp:1194-1215 [实] |

### 3.2 Shell 集成提供的功能

| 功能 | 需要的序列 | 已有的实现 |
|---|---|---|
| 命令边界与装饰（行首的成功/失败标记） | 133 A/B/C/D | VS Code `decorationsEnabled: both`、iTerm2 的蓝/红标记、WT 滚动条上的标记、Konsole semantic hints |
| 退出码 | 133;D;<code> | 同上；Ghostty 的命令完成通知 |
| 当前目录 | OSC 7 / 633;P;Cwd / 9;9 | 新标签继承 cwd、标签标题、解析相对路径的链接 |
| 命令行文本 | 633;E（带 nonce） | VS Code 的命令历史与 quick fix |
| 跳到上一条/下一条命令 | 133 A | VS Code Ctrl/Cmd+↑/↓（VSC terminalActions.ts:872-925）；Terminal.app Cmd+↑/↓（基于标记）；iTerm2 Cmd+Shift+↑/↓；Ghostty macOS Cmd+↑/↓ 与 Cmd+Shift+↑/↓、其他平台 Ctrl+Shift+↑/↓；VTE Ctrl+Shift+←/→；WT `scrollToMark` 默认没有绑定键 |
| 选中/复制上一条命令的输出 | 133 C/D | VS Code copyLastCommandOutput、WT selectOutput、iTerm2 "Select Output of Last Command" |
| 按住 Alt/Option 单击移动光标 | 133（知道当前在输入区） | VS Code `altClickMovesCursor`、Ghostty `cursor-click-to-move`、Terminal.app 和 iTerm2 的 Option-click |
| 在提示符处关闭不需要确认 | 133 | Ghostty `confirm-close-surface`（GH src/Surface.zig:975-993） |
| 窗口不在前台时，长命令完成后通知 | 133 C/D 或 OSC 666 | Ptyxis（命令耗时 ≥ 250 ms 且窗口不活跃时）、Ghostty `notify-on-command-finish`（默认 never）、iTerm2 Cmd-Opt-A |
| 粘性滚动（固定显示当前命令行） | 633 | VS Code stickyScroll |

### 3.3 风险与对策

| 风险 | 说明 | 对策 |
|---|---|---|
| 改变启动文件的读取方式 | ZDOTDIR 和 `--init-file` 都改变了 shell 读 rc 的路径；如果 `/etc/zshenv` 自己设置了 ZDOTDIR，zsh 注入会失效（GH src/shell-integration/README.md）；HISTFILE 的默认位置会跟着 ZDOTDIR 变 | 照搬 VS Code 和 Ghostty 的还原逻辑，并显式设置 HISTFILE；设置页说明注入方式；每个 profile 都可以单独关闭 |
| macOS 的 `/bin/bash` 是 3.2 | `ENV` + `--posix` 的方式在 3.2 上不可用，Ghostty 和 iTerm2 都直接跳过 | bash 改用 VS Code 的 `--init-file` 方式并模拟 login 启动文件 [推：需要实测 3.2] |
| 用户自定义参数 | 无法判断是否还能注入 | 与 VS Code 一样，参数无法识别时跳过，并在界面上显示"未启用 shell 集成"的原因 |
| prompt 主题冲突 | 异步刷新的主题（p10k instant prompt、starship）可能吃掉标记；复杂的 `PROMPT_COMMAND` | kitty 把钩子挪到 `precmd_functions` 末尾；VS Code 文档单独说明了复杂 PROMPT_COMMAND 的情况 |
| 集成丢失 | `exec zsh`、tmux、ssh 之后就没有集成了 | 退化为 WT 的回车启发式；以后接 SSH 时可以考虑 Ghostty 的 ssh-env 做法 |
| 伪造序列 | 程序输出可以伪造 133/633，诱导用户"重新运行"恶意命令行 | 命令行文本只接受带 nonce 的 633;E（VS Code `VSCODE_NONCE`） |
| 环境变量泄漏 | 注入用的变量会被子进程继承 | 脚本读取后立即 unset（VS Code、Ghostty、kitty 都这样做） |
| 临时目录被篡改 | VS Code 把 zsh 文件复制到 /tmp，所以要设置 sticky 位 | 采用 Ghostty 的做法，让 ZDOTDIR 直接指向应用包内的只读资源目录，不复制到 /tmp |

### 3.4 TKCode 建议

- **默认开启自动注入**：zsh、bash、fish、pwsh，以及 Windows 上的 Git Bash。nushell 和 elvish 可以以后加。
  - 依据：TKCode 定位类似 VS Code，命令导航、退出码、cwd 继承、关闭确认、以后让 Agent 读取命令输出，都依赖 shell 集成。
  - 代价：会改变 shell 的启动方式。见 §11 决策 3。
- **注入方式：**
  - zsh：ZDOTDIR 指向只读资源目录（Ghostty 的做法）；
  - bash：`--init-file` 模拟启动文件（VS Code 的做法，兼容 bash 3.2）；
  - fish：用 `XDG_DATA_DIRS` 加载 `vendor_conf.d`，不改参数；
  - pwsh：`-NoExit -Command ". '<script>'"`。
- **TKCode 自己的脚本发出：** 133 A/B/C/D（带 `aid=`）、OSC 7、带 nonce 的 633;E、633;P;Cwd。**解析时兼容：** 其他脚本（iTerm2、WezTerm、vte.sh、starship）发出的 133、1337 CurrentDir/SetMark、9;9。
- **没有集成时：** 用 WT 的 `autoMarkPrompts` 启发式（在主屏幕按回车即打标记），让上一条/下一条命令的跳转仍然可用。
- **状态可见：** 在标签悬停提示中显示是否已启用以及失败原因（VS Code 有同样的提示），并提供一键关闭。
- **Agent 在用户终端里运行命令时**（以后的功能）：参考 VS Code 的 `VSCODE_PREVENT_SHELL_HISTORY`，用 `HISTCONTROL=ignorespace` 避免 Agent 的命令写进用户的 shell 历史。

---

## 4. 键盘约定

### 4.1 复制、粘贴等常用键

| 动作 | Terminal.app | iTerm2 | Windows Terminal | GNOME Terminal / VTE | Ptyxis | Konsole | VS Code | Ghostty |
|---|---|---|---|---|---|---|---|---|
| 复制 | ⌘C | ⌘C | `ctrl+shift+c`、`ctrl+insert`、有选区时 `enter`；**新装时 settings.json 还绑定 `ctrl+c`**，没有选区就发 ^C | Ctrl+Shift+C；VTE 内置 Ctrl+Insert | Ctrl+Shift+C | Ctrl+Shift+C、Ctrl+Insert | macOS ⌘C；Win/Linux Ctrl+Shift+C；**Windows 有选区时 Ctrl+C 复制并清除选区** | macOS ⌘C；其他平台 Ctrl+Shift+C、Ctrl+Insert（`performable:`，没有选区就交给程序） |
| 粘贴 | ⌘V | ⌘V | `ctrl+shift+v`、`shift+insert`，新装时还有 `ctrl+v` | Ctrl+Shift+V；VTE：**Shift+Insert 粘贴 PRIMARY**，Ctrl+Shift+Insert 粘贴 CLIPBOARD | Ctrl+Shift+V | Ctrl+Shift+V；**Shift+Insert 粘贴 CLIPBOARD**，Ctrl+Shift+Insert 粘贴 PRIMARY | macOS ⌘V；Windows Ctrl+V 和 Ctrl+Shift+V；Linux Ctrl+Shift+V；**Linux Shift+Insert 粘贴 PRIMARY** | macOS ⌘V；其他平台 Ctrl+Shift+V、Shift+Insert |
| 查找 | ⌘F、⌘G | ⌘F | Ctrl+Shift+F | Ctrl+Shift+F | Ctrl+Shift+F | Ctrl+Shift+F | ⌘F / Ctrl+F | — |
| 清屏 | **⌘K**（Clear to Start），⌘L 清到上一个标记 | **⌘K**，⇧⌘K 清回滚 | Ctrl+Shift+K | — | — | — | **macOS ⌘K**（优先级被刻意调高，权重 +1） | **macOS ⌘K** |
| 切换标签 | ⌘1…⌘9 | ⌘1…⌘9 | Ctrl+Alt+1…9、Ctrl+Tab（defaults.json:752-754） | Alt+1…0、Ctrl+PgUp/PgDn | 未调研 | 未调研 | 未调研 | macOS ⌘1…⌘8；其他平台 Alt+1…8（`performable:`）（Config.zig:7043-7095） |

来源 [实]：
- Terminal.app：https://support.apple.com/en-us/guide/terminal/trmlshtcts/2.15/mac/26
- iTerm2：IT sources/MainMenu/Base.lproj/MainMenu.xib:814、1073-1104
- WT：src/cascadia/TerminalSettingsModel/defaults.json:722-804、userDefaults.json:24-28；src/cascadia/TerminalControl/ControlInteractivity.cpp:228-249
- VTE：src/vte.cc:5243-5370
- GNOME Terminal：src/org.gnome.Terminal.gschema.xml:399-435
- Ptyxis：src/org.gnome.Ptyxis.gschema.xml.in:404-482
- Konsole：src/session/SessionController.cpp:680-824
- VS Code：src/vs/workbench/contrib/terminalContrib/clipboard/browser/terminal.clipboard.contribution.ts:248-325；terminalActions.ts:1354-1368
- Ghostty：src/config/Config.zig:6673-6722、7140-7145

### 4.2 macOS 特有：Option、Cmd 与行内编辑键

| 行为 | Terminal.app | iTerm2 | Ghostty | VS Code | 其他 |
|---|---|---|---|---|---|
| Option 作为 Meta | 一个开关 "Use Option as Meta key"（`useOptionAsMetaKey`），默认关闭 [推]，⌘⌥O 临时切换 | 左右 Option **分别设置**：Normal / Meta / Esc+，默认都是 Normal | `macos-option-as-alt`：未设置 / true / false / left / right。**未设置时只在 US 和 US International 布局下视为 Alt**，并在每次按键时按当前布局判断 | `macOptionIsMeta: false` | WezTerm：左 Option 作 Alt，右 Option 用来输入组合字符；Alacritty：`option_as_alt: None` |
| Option+←/→ | 内置键表映射为 ESC b / ESC f | "Natural Text Editing" 预设中映射为 Esc+b / Esc+f | ESC b / ESC f | ESC b / ESC f | |
| ⌘←/→、⌘⌫、⌥⌫ | — | 预设：^A/^E、^U、ESC DEL | ^A/^E、^U | ^A/^E、^U、^W | |
| Backspace | 发 DEL（^?），可选 "Delete sends Control-H" | DEL | | DEL；Ctrl+Backspace 发 ^H（XT src/common/input/Keyboard.ts:86） | VTE/GNOME Terminal 用 `ascii-delete`；Konsole 用 `\x7f` |
| Option 单击移动光标 | 有 | 有（`OptionClickMovesCursor`） | `cursor-click-to-move` | `altClickMovesCursor: true` | |

来源 [实]：
- Terminal.app：local `plutil -p Terminal.app/…/keyMappings.plist`；Apple 快捷键页
- iTerm2：IT iTermProfilePreferences.m:1243-1254；sources/Keyboard/iTermStandardKeyMapper.m:283-319；plists/PresetKeyMappings.plist:321；iTermPreferences.m:902
- Ghostty：src/config/Config.zig:3495-3527、7383-7420；src/input/keyboard.zig:22-40
- VS Code：src/vs/workbench/contrib/terminalContrib/sendSequence/browser/terminal.sendSequence.contribution.ts:185-262；XT src/common/input/Keyboard.ts:333
- WezTerm：config/src/config.rs:462-468、1699-1703

### 4.3 Windows 特有：AltGr、Ctrl+C、输入模式

- **AltGr**（WT）：
  - LeftCtrl+RightAlt 按下时跳过快捷键匹配（src/cascadia/TerminalControl/TermControl.cpp:1776-1790）；
  - Ctrl+Alt 组合能产生可打印字符时，按 AltGr 处理；
  - LeftCtrl 与 RightAlt 按下时间相差超过 50 ms，才认为用户真的按了 Ctrl（src/terminal/input/terminalInput.cpp:364-398）；
  - `altGrAliasing: false` 关闭这种推断（src/cascadia/TerminalCore/Terminal.cpp:662-673）[实]。
- **Ctrl+C：** 在 WT 中复制动作在没有选区时返回"未处理"，按键交给 shell，于是发出 ^C（ControlInteractivity.cpp:228-249；src/cascadia/TerminalApp/AppActionHandlers.cpp:547-556）[实]。这就是"有选区复制、否则中断"的实现方式，与 Ghostty 的 `performable:` 是同一个思路。
- **win32-input-mode：** WT 支持，VS Code 默认关闭（`enableWin32InputMode: false`，VSC src/vs/workbench/contrib/terminal/common/terminalConfiguration.ts:612-620）。

### 4.4 死键与输入法

- **iTerm2：** `PTYTextView` 实现了 `NSTextInputClient`（`setMarkedText`、`firstRectForCharacterRange`），按键先经过 `interpretKeyEvents`；功能键以及 Option 作为 Meta/Esc+ 时绕过 Cocoa 的文本输入（IT sources/TerminalView/PTYTextView.m:5503-5553、5774；iTermStandardKeyMapper.m:486-511）[实]。Terminal.app 也绘制 marked text [实 strings]。
- **TKCode：**
  - 必须接入 Avalonia 的文本输入（IME）客户端：在光标处绘制预编辑文本，并上报光标矩形，让候选窗口跟随光标；
  - 可打印字符一律走 TextInput 事件，不从 KeyDown 自己合成字符。这样死键、AltGr、中文输入法都能正确工作 [推：需要在三个平台上用 headless 测试加真机验证]。
  - 中文用户用输入法的比例很高，这一项必须做。

### 4.5 kitty 键盘协议

| 终端 | 默认 |
|---|---|
| VS Code | `enableKittyKeyboardProtocol: true`（VSC terminalConfiguration.ts:605-611） |
| WT | `compatibility.kittyKeyboardMode: true` |
| Konsole | `KittyKeyboardEnabled=true` |
| WezTerm | `enable_kitty_keyboard=false` |

- 程序要先通过 `CSI > u` 申请才会启用，所以默认开启对普通 shell 没有影响。Claude Code、Codex 一类 CLI 靠它区分 Shift+Enter 等按键。
- 建议**支持并默认开启**。

### 4.6 与应用全局快捷键的冲突

**现有方案**

- **VS Code：**
  - `commandsToSkipShell`：这些命令的快捷键不交给 shell；有一份很长的默认列表，加 `-` 前缀可以从默认列表中移除某项；
  - `sendKeybindingsToShell: false`：设为 true 时大部分快捷键都交给 shell；
  - `allowChords: true`：组合键序列的前缀键（如 Ctrl+K）会被工作台拿走，这是有名的痛点；
  - `allowMnemonics: false`。

  （VSC terminalConfiguration.ts:46-50、434-457；src/vs/workbench/contrib/terminal/common/terminal.ts:503+）
- **Ghostty：** `performable:`（只有动作能执行时才吞掉按键，例如有选区才复制）、`unconsumed:`、`all:`、`global:`（GH src/config/Config.zig:1740-1800）。
- **WT：** 用 `unbound` 或 id 为 null 的绑定把键还给 shell；用 `sendInput` 发送字面输入。
- **macOS 系统终端：** Cmd 键都是菜单快捷键，归应用；其余键交给 shell。
- **GNOME Terminal：** `shortcuts-enabled` 总开关，`menu-accelerator-enabled` 控制 F10。

**TKCode 建议的按键路由规则**

| 平台 | 归 TKCode（应用或终端命令） | 交给 shell |
|---|---|---|
| macOS | 所有 ⌘ 组合键 | Ctrl、Option、Fn 组合键，以及无修饰键 |
| Windows / Linux | Ctrl+Shift+字母、TKCode 登记的 F 键；Windows 上有选区时的 Ctrl+C（"可执行才吞掉"），Windows 的 Ctrl+V | 单独的 Ctrl+字母、Alt+字母（AltGr 产生的字符作为文本输入） |
| 用户调整 | `terminal.keyboard.skipShell`：加入或移除（`-` 前缀）命令 id；`sendKeybindingsToShell` 总开关 | |

**TKCode 自身的冲突**（见 §11 决策 1）

TKCode 现在在窗口级用 **Tunnel** 路由拦截全局快捷键（src/TKCode.Desktop/Views/MainWindow.axaml.cs:159），修饰键在 macOS 上是 ⌘，在其他平台上是 **Ctrl**（同文件:457）。绑定了以下几个键（同文件:509-541）[实]：

| 快捷键 | 动作 | 冲突 |
|---|---|---|
| ⌘/Ctrl+K | 命令面板 | macOS：所有终端惯例的 ⌘K 是清屏；Win/Linux：Ctrl+K 是 readline 的 kill-line |
| ⌘/Ctrl+N | 新会话 | Win/Linux：Ctrl+N 是 readline 的下一条历史 |
| ⌘/Ctrl+B | 切换侧栏 | Win/Linux：Ctrl+B 是 readline 的光标左移，也是 tmux 的默认前缀键 |
| ⌘/Ctrl+, | 打开设置 | |
| ⌘/Ctrl+Shift+N | 新项目 | |
| ⌘/Ctrl+Shift+E | 切换资源管理器 | |

- **问题：** 终端控件获得焦点时，这些键会在到达终端之前就被窗口吃掉。所以 Win/Linux 上必须让这个 Tunnel 处理器知道"焦点在终端里"：Ctrl+字母交给 shell，只保留 Ctrl+Shift 系和 macOS 的 ⌘ 系。
- **另外要确定：** ⌘1…9（Terminal.app 用来切换标签）、⌘W、⌘T、⌘F 在终端获得焦点时的含义。

---

## 5. 鼠标与选择

### 5.1 系统终端怎么做

| 行为 | Terminal.app | iTerm2 | Windows Terminal | VTE / GNOME Terminal / Ptyxis | Konsole | VS Code（xterm.js） | Ghostty |
|---|---|---|---|---|---|---|---|
| 双击选词的规则 | 不可配置 | **单词字符** `/-+\~_.`（另有正则模式） | **分隔符** `` /\()"'-.,:;<>~!@#$%^&*\|+=[]{}~?│``（开头是空格） | **单词字符** `-#%&+,./=?@\_~·`，加上 Unicode 字母和数字 | **单词字符** `:@-./_~?&=%+#` | **分隔符** `` ()[]{}',"`─‘’“”\|``（开头是空格） | **分隔符** `` \t '"│`\|:;,()[]{}<>$``（含空格） |
| 三击 | 选中一行 | 选中整个换行前的逻辑行 | 一行 | 一行 | 整行 | 一行 | 一行 |
| 矩形选择 | Option+拖拽 | ⌘⌥+拖拽 | Alt+拖拽 | **Ctrl+拖拽** | Ctrl+Alt+拖拽 | Alt+拖拽 | macOS Alt；其他平台 Ctrl+Alt |
| 绕过程序的鼠标上报 | 按住 Fn [推] | 按住 Option | Shift | Shift | — | 非 macOS 用 Shift；macOS 用 Option，但要开启 `macOptionClickForcesSelection` | Shift（`mouse-shift-capture=false`） |
| 选中即复制 | 否 | **是**（`SelectionCopiesText`） | 否（`copyOnSelect: false`） | **总是复制到 PRIMARY** | 总是复制到 X11 selection；`AutoCopySelectedText=false` 控制是否同时进 CLIPBOARD | 否 | Linux 复制到 primary，其他平台不复制 |
| 中键 | — | — | — | 粘贴 PRIMARY（`gtk-enable-primary-paste`） | 粘贴 X11 selection | Linux 由 Electron 粘贴 selection | 粘贴 primary |
| 右键 | 上下文菜单 | 上下文菜单 | **有选区时复制并清除选区，否则粘贴**（`rightClickContextMenu: false`） | 上下文菜单（按住 Ctrl/Alt 除外） | 上下文菜单 | **macOS 选中单词并弹出菜单；Windows 复制或粘贴；Linux 弹出菜单** | 上下文菜单 |
| 打开链接 | ⌘+双击 | ⌘+单击 | Ctrl+单击（优先于程序的鼠标模式） | Ctrl+单击 | 不支持直接单击打开 | Ctrl/⌘+单击 | macOS ⌘、Linux Ctrl + 悬停 |
| 自动识别 | URL；⇧⌘+双击选中完整路径或 URL | URL；semantic history 把 `path:line` 交给编辑器 | 只识别 `https?\|ftp\|file://` URL，**不识别裸路径** | URL、email、`file://`、VoIP、news/man | URL | URL 和本地路径，含二十多种 `path:line:col` 写法 | URL 和路径 |
| OSC 8 | 支持 | 支持，⌘单击打开 | 支持 | VTE 默认关闭，GNOME Terminal/Ptyxis 开启 | `AllowEscapedLinks=false` | 支持，不在白名单中的 scheme 会询问 | `link-osc8=true` |

来源 [除标 推 外均为实]：
- Terminal.app：Apple 快捷键页；https://support.apple.com/guide/terminal/trmlc69728a5/mac
- iTerm2：IT iTermPreferences.m:740-745、899-909；sources/…/PTYMouseHandler.m:29、588-600、796；iTermSemanticHistoryPrefsController.m:114
- WT：defaults.json:12-16；src/cascadia/TerminalControl/ControlCore.cpp:2103-2153；ControlInteractivity.cpp:266-370、749-768；src/cascadia/TerminalCore/Terminal.cpp:540-560、1422-1428
- VTE：src/vte.cc:107、5741-5750、7280-7598；src/widget.cc:1926-1934；src/vtegtk.cc:2152-2154
- GNOME Terminal：src/terminal-screen.cc:264-275、879、2242-2310；src/terminal-regex.hh:53-156
- Konsole：src/profile/Profile.cpp:162-184；src/terminalDisplay/TerminalDisplay.cpp:1388、2597
- VS Code：terminalConfiguration.ts:148-161、371-392、512-544；src/vs/workbench/contrib/terminalContrib/links/browser/terminalLinkParsing.ts:75-110
- xterm.js：XT src/browser/services/SelectionService.ts:437-447、607-612
- Ghostty：src/config/Config.zig:745-771、948-985、1454-1492、2524-2570；src/surface_mouse.zig:98-103

### 5.2 Avalonia 对 PRIMARY 选区的支持

- master 分支的 commit `ac7c57a1b`（"[X11] Implement support for PRIMARY selection (#21849)"，2026-09-14）新增了以下内容：
  - X11 后端分别为 CLIPBOARD 和 PRIMARY 创建 `X11ClipboardImpl`（AV src/Avalonia.X11/X11Platform.cs:86-105）；
  - 公开 API `TopLevel.TryGetClipboard(ClipboardType.PrimarySelection)`（AV src/Avalonia.Controls/TopLevel.cs:546-557；src/Avalonia.Base/Input/Platform/ClipboardType.cs:6-18）；
  - TextBox 已经用它实现中键粘贴（AV src/Avalonia.Controls/TextBox.cs:2150-2173）[实]。
- **12.1.4 中没有这个文件**，所以至少要等 12.2 [实，GitHub API 返回 404]；具体发布版本 [推]。
- **Wayland 后端**（`Avalonia.Wayland`，12.1 起有稳定的 NuGet 包，需要通过 `UseWayland()` 主动启用）**没有 primary selection**，即没有实现 `zwp_primary_selection`（AV src/Avalonia.Wayland/WaylandPlatform.cs:71-84）[实]。
- `UsePlatformDetect` 在 Linux 上仍然只加载 X11（AV src/Avalonia.Desktop/AppBuilderDesktopExtensions.cs:30-50）[实]。

**对 TKCode 的影响**

- 停留在 12.1.x 时，Linux 的选中即复制和中键粘贴只能退化为"TKCode 进程内的 PRIMARY"：只在 TKCode 自己的控件之间生效。
- 要与其他程序互通，需要升级到 12.2，并且只在 X11/XWayland 下有效。

### 5.3 TKCode 建议默认

| 项 | macOS | Windows | Linux | 依据 |
|---|---|---|---|---|
| 选词分隔符（`wordSeparators`，统一用"分隔符"表示） | iTerm2 单词字符 `/-+\~_.` 的补集：`` !"#$%&'()*,:;<=>?@[]^`{\|}`` 加空白和 `│` | WT 原样 `` /\()"'-.,:;<>~!@#$%^&*\|+=[]{}~?│`` | VTE 单词字符的补集：`` !"$'()*:;<>[]^`{\|}`` 加空白和 `│` | 各平台跟随系统终端（macOS 的 Terminal.app 不可配置，取 iTerm2 作为替代）。统一用"分隔符"，与 VS Code、WT 的写法一致，方便用户迁移设置 [补集为推导值] |
| 三击 | 选中换行前的整条逻辑行 | 同左 | 同左 | iTerm2 |
| 矩形选择 | Option+拖拽 | Alt+拖拽 | Ctrl+拖拽 | Terminal.app / WT / VTE |
| 绕过鼠标上报 | Shift（另外考虑 Fn [推]） | Shift | Shift | WT、VTE、Ghostty；macOS 上 Option 已经用于矩形选择 |
| 选中即复制 | 否 | 否 | 进 PRIMARY（12.2 之前只在进程内）；不进 CLIPBOARD | Terminal.app / WT / VTE |
| 中键 | 无 | 无 | 粘贴 PRIMARY | VTE |
| 右键 | 选中单词并弹出上下文菜单 | 有选区时复制并清除选区，否则粘贴 | 上下文菜单 | VS Code 的分平台默认值（与 WT、GNOME Terminal 一致） |
| 打开链接的修饰键 | ⌘+单击 | Ctrl+单击 | Ctrl+单击 | iTerm2、Ghostty / WT / GNOME Terminal |
| 路径识别 | 识别 `path:line:col`（采用 VS Code 的写法表），在 **TKCode 编辑区**打开；相对路径按 shell 集成上报的 cwd 解析 | 同左 | 同左 | VS Code、iTerm2 semantic history |
| OSC 8 | 悬停时显示真实目标。`http`、`https`、`mailto` 直接打开；`file://` 无主机时在 TKCode 中打开，**有主机时拒绝**；可执行扩展名需要确认；其他 scheme 弹窗询问，可选"总是允许"并写入 settings | 同左 | 同左 | WT src/cascadia/TerminalApp/TerminalPage.cpp:3270-3423；VS Code terminalLinkManager.ts:116-148 |
| Alt/Option 单击移动光标 | 开（需要 shell 集成） | 开 | 开 | VS Code、Terminal.app、Ghostty |

---

## 6. 粘贴安全

### 6.1 系统终端怎么做

| 终端 | 多行警告 | 控制字符处理 | bracketed paste | 末尾换行与空白 | 其他 |
|---|---|---|---|---|---|
| Windows Terminal | `warning.multiLinePaste: automatic`：**只在程序没有开启 bracketed paste、且内容含 CR/LF 时**警告；`warning.largePaste: true`：超过 5 KiB 时警告 | `FilterStringForPaste`：LF/CRLF 转为 CR，去掉除 HT/CR 外的所有 C0/C1 控制字符（包括 ESC） | 开启 DECSET 2004 时包裹 `ESC[200~ … ESC[201~` | `trimPaste: true`：没有 bracketed paste 时去掉单行内容末尾的空白 | — |
| VS Code | `enableMultiLinePasteWarning: auto`：bracketed paste 开启时不警告；pwsh 自己的 readline 处理粘贴时也不警告 | 在 bracketed paste 内把 ESC 替换成 `␛`（U+241B）；LF 转为 CR | `ignoreBracketedPasteMode: false` | — | — |
| Ghostty | `clipboard-paste-protection: true`：**没有 bracketed paste 且含换行**时警告；内容中含 `ESC[201~` 时**无论如何**都警告 | 始终把 NUL、BS、ENQ、EOT、ESC、DEL、^C ^\ ^U ^Z ^Q ^S ^W ^V ^R ^O 替换为空格（与 xterm 一致）；没有 bracketed paste 时 `\n` 转为 `\r` | `clipboard-paste-bracketed-safe: true` | `clipboard-trim-trailing-spaces: true` | OSC 52：读取时询问，写入时允许 |
| iTerm2 | **只要含 `\r`/`\n` 就弹窗**："OK to paste N lines at shell prompt?"；只有一行且以换行结尾时提供 "Paste Without Newline" | Advanced Paste 中可以去掉控制字符、转换换行 | `KEY_ALLOW_PASTE_BRACKETING: YES` | — | OSC 52 写入默认关闭，读取每次询问 |
| Terminal.app | 无 [推] | — | 支持；内容中含结束标记时**直接拒绝粘贴** | — | Ctrl-⌘-V 粘贴转义后的文本 |
| VTE / GNOME Terminal / Ptyxis | **无** | `pastify`：C0（HT/CR/LF 除外）、DEL、C1 替换为 Unicode 控制图形字符；LF 和 CRLF 转为 CR | 支持 | — | — |
| Konsole | 超过 8000 字符时警告；含不可见控制字符时让用户选择"去掉后粘贴"或"全部粘贴" | 见左 | 支持 | — | — |
| WezTerm | 无 | 去掉内嵌的 `ESC[200~`/`ESC[201~` | 开启 bracketed paste 时不转换换行 | `canonicalize_pasted_newlines`：Windows 转为 CRLF，其他平台转为 CR | — |

来源 [实]：
- WT：src/cascadia/TerminalApp/TerminalPage.cpp:3132-3266；src/types/utils.cpp:631-680、1109-1132
- VS Code：terminalConfiguration.ts:163-172、665-669；XT src/browser/Clipboard.ts:13-57
- Ghostty：src/config/Config.zig:2448-2513；src/Surface.zig:6254-6273；src/input/paste.zig:51-199
- iTerm2：sources/…/iTermPasteHelper.m:748-870；iTermPreferences.m:742、760
- Terminal.app：local `strings`
- VTE：src/pastify.cc:43-110
- Konsole：src/terminalDisplay/TerminalDisplay.cpp:2444-2532
- WezTerm：term/src/config.rs:100-125；term/src/terminalstate/mod.rs:943-965

### 6.2 拖放文件的路径转义

| 终端 | 做法 |
|---|---|
| VS Code | 按 shell 类型分别处理（VSC src/vs/workbench/contrib/terminal/common/terminalEnvironment.ts:316-380；src/vs/platform/terminal/common/terminalEnvironment.ts:14-59）：<br>• 所有 shell：先去掉 C0/C1 控制字符。<br>• **pwsh/powershell**：路径含空格或 `'` 时写成 `& '<路径中的 ' 替换为 ''>'`。<br>• **bash/zsh/sh/Git Bash**：用单引号包住；路径含单引号时写成 `'\''`；同时含单引号和双引号时用 `$'…'`。<br>• **fish**：有专门的规则。<br>• **Git Bash**：先把 `\` 转成 `/`。<br>• **WSL**：用 wslpath 转换。<br>• **cmd**：路径含空格时加双引号。 |
| GNOME Terminal / Ptyxis | 每个路径或 URI 用 `g_shell_quote`（单引号）处理，以空格连接，作为粘贴发送；Ptyxis 还把 document portal 路径映射回宿主机路径（GT src/terminal-screen.cc:2438-2452；PX src/ptyxis-terminal.c:567-608） |
| Konsole | `KShell::quoteArg` |
| Terminal.app | 从 Finder 拖入时写入完整路径，空格等字符用反斜杠转义 [推] |
| iTerm2 | 反斜杠转义 `` \ ()"&'!$<>;\|*?[]#`{}^+=@~ ``、制表符和换行；单引号包裹默认关闭（IT NSStringITerm.m:161-163、3249-3255；iTermPreferences.m:912） |
| Ghostty | macOS 用反斜杠转义，并走粘贴保护；GTK 版 `ShellEscapeWriter` 转义后每个路径**以 `\n` 结尾**（GH src/os/shell.zig:82-139） |
| WezTerm | `quote_dropped_files`：Unix 默认 `SpacesOnly`，Windows 默认 `Windows` |

### 6.3 TKCode 建议

- **`paste.multiLineWarning: auto`**：bracketed paste 关闭且内容含换行时警告；内容中含 `ESC[201~` 时一律警告（Ghostty）。警告对话框提供"粘贴为单行"。
- **始终过滤：** 去掉 C0/C1 控制字符（保留 HT），LF/CRLF 转为 CR；在 bracketed paste 内把 ESC 替换为 `␛`（VS Code/xterm.js）。
- **`paste.trimTrailingNewline: true`**：没有 bracketed paste 时，单行粘贴去掉末尾的换行和空白（WT `trimPaste`、iTerm2 的 "Paste Without Newline"）。
- **大段粘贴警告**默认关闭；用 `paste.largePasteWarning` 设置字节阈值，WT 是 5 KiB，Konsole 是 8000 字符。
- **拖放路径：**
  - 按**当前 shell 种类**转义，采用 VS Code 的 `preparePathForShell` 规则，但 pwsh 不加 `& ` 前缀：拖放只是插入路径，不应把它变成调用。
  - macOS 上默认用反斜杠转义（Terminal.app、iTerm2 的观感），Linux 用单引号（`g_shell_quote`）。
  - 多个文件以空格连接，**末尾不加换行**。
  - 拖放走同一套粘贴保护和过滤。
- **OSC 52：** 写剪贴板默认允许，读剪贴板默认询问（Ghostty；iTerm2 更严格）。

---

## 7. 外观默认值

### 7.1 字体与字号

| 平台 | 系统终端的默认 | 同类应用 | TKCode 默认（"跟随系统"的解析结果） |
|---|---|---|---|
| macOS | Terminal.app（macOS 26）的默认 profile 是 **"Clear Dark"，字体 SF Mono Terminal 12**；"Basic" 是 SF Mono Terminal 11。**SF Mono Terminal 字体装在 Terminal.app 包内，其他程序用不了** [实 local plutil]。iTerm2：Monaco 12 [实] | VS Code：Menlo 12px（VSC src/vs/editor/common/config/fontInfo.ts:222-236；terminalConfiguration.ts:43）；Ghostty：内置 JetBrains Mono 13；Alacritty：Menlo 11.25 | 系统等宽字体 **`.SF NS Mono`**（`/System/Library/Fonts/SFNSMono.ttf`，族名以点开头，属于隐藏字体 [实 local mdls]）**12pt**；如果 Skia 按族名解析不到，回退 **Menlo 12** [推：需要 headless 快照验证] |
| Windows | WT：**Cascadia Mono 12pt**（WT src/inc/DefaultSettings.h:29-31） | VS Code：Consolas 14px；Alacritty：Consolas | **Cascadia Mono 12pt**，没有安装时回退 Consolas。Win10 上不装 WT 可能没有 Cascadia [推] |
| Linux | GNOME：`org.gnome.desktop.interface monospace-font-name` 默认 **'Adwaita Mono 11'**（gsettings-desktop-schemas master）；GNOME Terminal `use-system-font=true`；Ptyxis 通过 **org.freedesktop.portal.Settings** 读取系统字体，失败时回退 "Monospace 11"（PX src/ptyxis-application.c:693-745）；Konsole 用 `QFontDatabase::systemFont(FixedFont)`，即 kdeglobals 的 `fixed` | VS Code：'Droid Sans Mono', monospace，再回退 DejaVu Sans Mono / Ubuntu Mono | 先从 portal 读 `monospace-font-name`（GNOME），再读 kdeglobals 的 `[General] fixed`（KDE），最后用 fontconfig 的 `monospace` 别名。**Avalonia 的 portal 设置只读取颜色**（`color-scheme`、`accent-color`），字体需要 TKCode 自己通过 D-Bus 读取 [实] |

- **单位：** 字号统一按"点"（pt）配置，与 WT、Ghostty、GNOME 一致，再按平台换算成 DIP：
  - macOS：1pt = 1 DIP（AppKit point）；
  - Windows/Linux：1pt = 96/72 DIP。

  不然同样写 12，在 macOS 和 Windows 上会差 1/3 [推]。
- **CJK 回退：** 等宽字体一般没有汉字，需要显式设置回退链（macOS 苹方、Windows 微软雅黑、Linux Noto Sans CJK），并保证宽字符占 2 格 [推]。

### 7.2 光标与闪烁

| 终端 | 形状 | 闪烁 | 闪烁频率的来源 |
|---|---|---|---|
| Terminal.app | 方块 [推] | 关 [推] | — |
| iTerm2 | 方块 | 关；程序也不能修改闪烁（`ALLOW_CHANGE_CURSOR_BLINK=NO`） | 固定 0.5 s |
| Windows Terminal | **竖线 bar** | **跟随系统** | `SM_CARETBLINKINGENABLED` 和 `GetCaretBlinkTime()`，超过 10 s 视为不闪；失焦时隐藏光标（WT src/cascadia/TerminalCore/terminalrenderdata.cpp:41-51；ControlCore.cpp:2564-2567） |
| VTE / GNOME Terminal / Ptyxis | 方块 | **跟随系统**（`cursor-blink-mode=system`） | GtkSettings 的 `gtk-cursor-blink`、`-time`、`-timeout`。GNOME 默认 `cursor-blink=true`、一个周期 `cursor-blink-time=1200` ms、`cursor-blink-timeout=10` s，即 10 秒不操作后停止闪烁（VTE src/widget.cc:2149-2183；src/vte.cc:8754-8763） |
| Konsole | 方块 | 关 | `QApplication::cursorFlashTime()/2` |
| VS Code | 方块，失焦时显示 `outline` | 关 | — |
| Ghostty | 方块 | 开（`cursor-style-blink` 为 null 时也服从 DEC mode 12） | 固定 600 ms |

- **macOS 的系统闪烁参数：** `NSTextInsertionPointBlinkPeriodOn`/`Off` 是**未公开**的 NSGlobalDomain 键，默认不存在；iTerm2 读不到时回退 0.56 s（IT MultiCursorTextView.swift:107-121）[实]。
- **Avalonia：**
  - `TextBox.CaretBlinkInterval` 固定 500 ms（AV src/Avalonia.Controls/TextBox.cs:122-123）；
  - Win32 的 `GetCaretBlinkTime` 只声明了 P/Invoke，没有使用（AV src/Windows/Avalonia.Win32/Interop/UnmanagedMethods.cs:1322）；
  - `IPlatformSettings` 中没有闪烁频率（AV src/Avalonia.Base/Platform/IPlatformSettings.cs:13-63）[实]。
- **TKCode 建议：**
  - 形状：macOS 和 Linux 用方块，Windows 用竖线；
  - 闪烁默认 `system`：Windows 读 Win32 API；Linux 读 portal 的 `cursor-blink`/`cursor-blink-time`/`cursor-blink-timeout`（KDE 读 kdeglobals 的 `CursorBlinkRate` [推]）；**macOS 不闪烁**（Terminal.app、iTerm2 的默认）；
  - 系统开启"减少动态效果"时不闪烁，与 settings.md 中的 `reduceMotion` 联动；
  - 失焦时显示空心方框（VS Code）。

### 7.3 响铃

| 终端 | 默认 |
|---|---|
| Terminal.app | 选项：Audible、Visual、Only when sound is muted、Badge app and window Dock icons、Bounce app icon when in background、Continue bouncing（默认值未核实） |
| iTerm2 | 播放声音；在标签上显示铃铛图标（Visual Bell）；发送通知 |
| Windows Terminal | `bellStyle: audible`，播放 `bellSound` 或系统的 SystemHand 声音；`window` 闪烁、`taskbar` 闪任务栏需要用户自己开启 |
| VTE / GNOME Terminal | `audible-bell=true`（`gtk_widget_error_bell`）；VTE 没有视觉响铃 |
| Ptyxis | 声音和视觉闪烁都开；窗口不在前台时设置 urgency hint |
| Konsole | `BellMode=NotifyBell` |
| VS Code | 视觉响铃关闭；声音由 `accessibility.signals.terminalBell` 控制 |
| Ghostty | `bell-features`：attention 开、标题前加 🔔 开；system/audio/border 关 |

来源 [实]：local strings；IT iTermProfilePreferences.m:1212-1226；WT src/cascadia/TerminalApp/TerminalPaneContent.cpp:255-295；VTE src/vte.cc:5143-5146；PX src/ptyxis-window.c:1095-1108；VSC terminalConfiguration.ts:424-433；GH src/config/Config.zig:3265-3318。

**TKCode 建议：**

- `bell.sound: system`：系统终端都默认播放声音。实现：macOS `NSBeep`，Windows `MessageBeep(MB_ICONHAND)`（对应 WT 的 SystemHand），Linux 用桌面提示音或 X11 bell。**Avalonia 没有提供提示音 API**，需要 P/Invoke [推]。
- `bell.visual: true`：在标签上显示铃铛标记（iTerm2、VS Code、Ghostty）。
- `bell.attention: true`：只在 TKCode 窗口不在前台时请求注意，macOS 让 Dock 图标跳动、Windows 闪任务栏、Linux 设 urgency hint（Terminal.app 的 Bounce 选项、Ptyxis、Ghostty attention）。
- 1 秒内的重复响铃合并处理（VS Code `bellDuration` 1000；VTE 两次之间至少隔 100 ms）。

### 7.4 调色板与最小对比度

| 终端 | 调色板跟随明暗 | 最小对比度 |
|---|---|---|
| VS Code | 跟随工作台主题 | **`minimumContrastRatio: 4.5`**（WCAG AA）。算法：WCAG 相对亮度的对比度；不够时前景色每次按 10% 调亮或调暗，直到达标（XT src/common/Color.ts:296-361、379-384） |
| Windows Terminal | `colorScheme` 可以写成 dark/light 一对；默认 Campbell | `adjustIndistinguishableColors: automatic`：**只在 Windows 高对比度模式下**生效。算法：转到 Oklab，ΔE < 0.5 时只调整 L（WT src/types/ColorFix.cpp:167-210；src/renderer/base/RenderSettings.cpp:214-222） |
| Ghostty | `theme = light:X,dark:Y` 跟随桌面 | `minimum-contrast=1`（关）。不足时前景色直接改成纯白或纯黑，emoji 和图片除外（GH src/terminal/color.zig:647-680） |
| iTerm2 | — | 默认 0（关）。按亮度 0.30R+0.59G+0.11B 计算差值，调整到目标值（IT sources/Categories/NSColor+iTerm.m:586-743） |
| Ptyxis | `palette='gnome'`，分 Light 和 Dark 两段，跟随 libadwaita | — |
| GNOME Terminal | `use-theme-colors=true` | — |
| WezTerm | — | `text_min_contrast_ratio=None` |

**TKCode 建议：**

- ANSI 16 色**分别为 TKCode 的暗色和亮色主题各定一套**，跟随 `theme`（system/dark/light），用户可以覆盖。
- `minimumContrastRatio` 默认 **4.5**（采用 VS Code 的 WCAG 算法），原因是亮色主题下 ANSI 黄色、青色在白底上看不清。这一点与 WT 和 Ghostty 的"默认不改色"不同，可以调成 1 关闭（§11 决策 7）。
- 系统开启高对比度或"增强对比度"时自动提高到 7。

### 7.5 回滚行数与 Unicode 宽度

| 项 | Terminal.app | iTerm2 | WT | GNOME Terminal / Ptyxis | Konsole | VS Code | Ghostty | WezTerm | Alacritty |
|---|---|---|---|---|---|---|---|---|---|
| 回滚 | 不限 [推] | 1000 | 9001 | **10000** | **10000** | 1000 | 50 MB（按字节） | 3500 | 10000 |
| 东亚模糊宽度字符 | "Unicode East Asian Ambiguous characters are wide" 选项 | 默认窄 | `compatibility.ambiguousWidth: narrow` | `cjk-utf8-ambiguous-width: narrow` | — | — | 无此选项 | `treat_east_asian_ambiguous_width_as_wide=false` | — |
| 宽度算法 | — | Unicode 9 | `textMeasurement: graphemes` | — | — | `unicodeVersion: 11` | `grapheme-width-method=unicode`（mode 2027） | `unicode_version=9` | — |

**TKCode 建议：**

- 回滚默认 **10000 行**（Linux 系统终端的默认值，与 WT 的 9001 接近）。macOS 上不跟随 Terminal.app 的"不限"，避免内存无限增长。
- 宽度按 grapheme 计算，支持 mode 2027（与 WT、Ghostty 一致）。
- 模糊宽度字符默认**窄**（所有系统终端都是）。中文旧软件的用户可以改为 `wide`。

---

## 8. 生命周期

### 8.1 关闭确认

| 终端 | 默认 | 如何判断"有程序在运行" | 来源 |
|---|---|---|---|
| Terminal.app | "Ask before closing" 有三个选项：Always / Never / "Only if there are processes other than the login shell and:" + 进程列表 | 除登录 shell 外的进程，排除列表中的进程；默认列表推测为 `screen`、`tmux` | https://support.apple.com/guide/terminal/change-profiles-shell-settings-trmlshll/mac [实]；默认列表 [推，依据 strings 相邻] |
| iTerm2 | `KEY_PROMPT_CLOSE: Never`；退出整个程序时确认（`PromptOnQuit: YES`） | 遍历进程树，跳过 login；`login`、`iTerm2`、`ShellLauncher` 和 argv0 以 `-` 开头的进程不算；用户的忽略列表迁移后为 rlogin/ssh/slogin/telnet | IT PTYSession.m:3125-3211；ProfileModel.m:470-475 [实] |
| GNOME Terminal | `confirm-close=true` | `tcgetpgrp(pty_fd)` 不等于 shell 的 pid，没有忽略列表 | GT src/terminal-screen.cc:2934-2970 [实] |
| Ptyxis | `prompt-on-close=true` | `tcgetpgrp` ≠ shell，且前台进程**不是 shell**（sh/bash/dash/zsh/fish/tcsh/csh/tmux，加上 `/etc/shells` 中的条目） | PX agent/ptyxis-process-impl.c:58-296 [实] |
| Konsole | 确认 | 前台进程名不等于 `$SHELL` 的 basename；关闭窗口时，有 2 个以上标签也确认 | KO src/session/SessionController.cpp:1143-1175 [实] |
| Windows Terminal | `warning.confirmOnClose: automatic` | **不检查进程**，只在关闭含多个标签或分屏的窗口时确认 | WT src/cascadia/TerminalApp/TerminalPage.cpp:2442-2480 [实] |
| VS Code | `confirmOnKill: editor`（只对编辑区里的终端）；`confirmOnExit: never` | 用 `listProcesses(pid)` 取进程树；只有一个子进程且在 `ignoreProcessNames`（starship、oh-my-posh、bash、zsh）中时不算 | VSC src/vs/platform/terminal/node/childProcessMonitor.ts:95-137；terminalPlatformConfiguration.ts:414-428 [实] |
| Ghostty | `confirm-close-surface: true` | **光标在提示符处（OSC 133）时不确认**；没有 shell 集成时总是确认 | GH src/Surface.zig:975-993 [实] |
| WezTerm | `window_close_confirmation=AlwaysPrompt` | 进程树中的进程名不全在 `skip_close_confirmation_for_processes_named`（bash、sh、zsh、fish、tmux、nu、cmd.exe、pwsh.exe、powershell.exe）中时确认 | WZ mux/src/localpane.rs:561-625 [实] |

**TKCode 建议：**

- **默认 `confirmClose: whenRunning`：**
  - macOS/Linux：用 `tcgetpgrp(master_fd)` 与 shell 的进程组比较，前台进程名再与忽略列表比较（Ptyxis 的做法）；
  - 有 shell 集成时，光标在提示符处就直接视为空闲（Ghostty）；
  - Windows 没有 `tcgetpgrp`，改用 Toolhelp32 取进程树，再按忽略列表判断（VS Code、WezTerm）。
- **忽略列表默认值：** 各种 shell（取自 `/etc/shells` 的 basename，加上 sh/bash/zsh/fish/nu/pwsh/powershell/cmd）、`login`、starship、oh-my-posh。tmux/screen **算作在运行**：关闭标签会断开会话中的程序，Terminal.app 列表中的 tmux 是可以去掉的。
- **退出 TKCode 或关闭窗口时：** 只要有任何终端在运行，就汇总确认一次（iTerm2 `PromptOnQuit`）。
- **与 WT 的差异：** Windows 上也默认确认，比 WT 保守，因为 TKCode 的终端标签与会话混在一起，更容易误关。

### 8.2 标签标题

| 终端 | 做法 |
|---|---|
| WT | 起始标题为 `tabTitle` 或 profile 名称；OSC 0/2 覆盖，`suppressApplicationTitle` 可以禁止；空的 OSC 标题恢复起始标题（WT src/cascadia/TerminalCore/TerminalApi.cpp:89-100） |
| VS Code | `tabs.title: ${process}`；`tabs.description: ${task}${separator}${local}${separator}${cwdFolder}` |
| Konsole | 本地 `%d : %n`（目录 : 程序），远程 `(%u) %H` |
| VTE | OSC 0 和 2 都只设置窗口标题，标题栈深度 8 |
| Ghostty | OSC 0/2，shell 集成把 cwd 或当前命令写进标题 |

**TKCode 建议：**

- 标题优先级：OSC 标题 → 前台进程名 → cwd 的目录名；允许用户关闭程序设置标题（`allowApplicationTitle`）。
- 标签副标题显示 cwd。
- 不响应标题查询（title report 默认关闭，Ghostty `title-report=false`、WezTerm 同样关闭，防止注入）。

### 8.3 shell 退出之后

| 终端 | 行为 |
|---|---|
| Terminal.app | "When the shell exits" 有三个选项：Close the window / **Close if the shell exited cleanly** / Don't close the window；打印 `[Process completed]` 或 `[Process exited - exit code N]` [实 Apple 文档 + strings；默认值未核实] |
| Windows Terminal | `closeOnExit: automatic`：退出码为 0 时关闭；否则显示 `[process exited with code N (0x…)]`，提示 "You can now close this terminal with Ctrl+D, or press Enter to restart."（ConptyConnection.cpp:517-549；ControlCore.cpp:536-553）[实] |
| GNOME Terminal / Ptyxis | `exit-action='close'`；另有 hold（信息栏显示退出状态，带"重新启动"）和 restart 两个选项 [实] |
| VS Code | 直接关闭；退出码非 0 且最后一次输入不是 Ctrl+D 时，弹通知 "The terminal process terminated with exit code: N"（`showExitAlert`）；启动失败时可以关闭 shell 集成后重试（VSC terminalInstance.ts:1760-1812、2905-2950）[实] |
| Ghostty | 启动后 250 ms 内退出视为异常并显示错误；否则直接关闭（`wait-after-command=false`）[实] |
| iTerm2 | 新 profile 默认 Close；选 "No Action" 时提供 Restart [实] |

**TKCode 建议：**

- **默认 `onExit: closeOnCleanExit`**（与 WT `automatic` 和 Terminal.app 的 "Close if the shell exited cleanly" 一致）：
  - 退出码为 0：关闭标签；
  - 非 0 或被信号终止：保留标签，显示 `[进程已退出，退出码 N]`，按 Enter 重新启动，按 Ctrl+D 或 Esc 关闭；
  - 启动后很快（约 250 ms 内）失败：显示启动命令、错误信息，并提供"关闭 shell 集成后重试"（Ghostty + VS Code）。
- macOS 用 login 包装时，退出码可能被 login 改写，需要实测 [推]。

### 8.4 会话恢复

| 终端 | 恢复什么 |
|---|---|
| Terminal.app | 依靠系统的窗口恢复，"Restore text when reopening windows" 恢复屏幕文本，可以限制行数，并在恢复的文本后插入书签；`/etc/zshrc_Apple_Terminal` 按 `TERM_SESSION_ID` 恢复每个会话的历史和状态；系统设置中开启 "Close windows when quitting an application" 时不恢复 |
| iTerm2 | 进程运行在常驻的 multiserver 守护进程里，崩溃、强制退出、升级后都**还活着**，但 ⌘Q 会结束它们；`restoreWindowContents=YES` |
| Windows Terminal | `firstWindowPreference: persistedLayoutAndContent`：把 VT 流保存为 `buffer_<guid>.txt`，启动时回放，并在末尾显示 `[Restored <日期>]` 横幅（WT src/cascadia/WindowsTerminal/WindowEmperor.cpp:1387-1440；ControlCore.cpp:1900-2003）[实] |
| VS Code | `enablePersistentSessions: true`：ptyHost 让进程在窗口重新加载后继续存活；关闭应用后 `persistentSessionReviveProcess: onExit`，恢复最近 `persistentSessionScrollback: 100` 行并显示 "History restored"，然后启动新进程（VSC src/vs/platform/terminal/node/ptyService.ts:265）[实] |
| Ptyxis | `restore-session=true`：恢复标签、profile、容器、cwd、标题，**不恢复回滚内容** [实 key / 推 无回滚] |
| GNOME Terminal | 不恢复 [推] |

**TKCode 建议：**

- 默认恢复：终端标签、所用 profile、cwd、标题。
- 回滚内容恢复默认最近 **1000 行**（介于 VS Code 的 100 行和 WT、Terminal.app 的全量之间），在内容后插入"以下为恢复的内容 / 新会话"分隔线（WT 和 Terminal.app 都有这样的标记）。
- 进程本身不恢复，启动新 shell。
- 恢复数据存在 `~/.tkcode/state.json` 旁的单独目录中，**可能含敏感输出**，文件权限设为 0600，并允许关闭。
- **架构问题（§11 决策 5）：** PTY 放在 Agent Host 进程中时，界面进程重启后终端还活着（相当于 VS Code 的 ptyHost），Agent 以后读取终端也更方便。

---

## 9. 可访问性

| 终端 | 方式 | 来源 |
|---|---|---|
| Windows Terminal | XAML 的 `TermControlAutomationPeer` 暴露 **UIA Text pattern**（按字符、单词、行、文档取范围）；新输出用 `RaiseNotificationEvent(ActionCompleted, …, "TerminalTextOutput")` 播报，每块 1000 字符（SAPI 的上限）；回显的按键字符不重复播报 | WT src/cascadia/TerminalControl/TermControlAutomationPeer.cpp:176-259；src/types/ScreenInfoUiaProviderBase.cpp:89-170；src/renderer/uia/UiaRenderer.cpp:75-259 [实] |
| VS Code | xterm.js 的 screen reader mode：行容器 `role=list`，加 `aria-live=assertive` 的 live region；**可访问缓冲区**（Alt+F2）以只读编辑器的形式显示终端内容，可以用屏幕阅读器逐行浏览；命令之间可以跳转 | XT src/browser/AccessibilityManager.ts:36-142；VSC src/vs/workbench/contrib/terminalContrib/accessibility/browser/terminal.accessibility.contribution.ts:279-368 [实] |
| macOS Terminal.app | 实现了 NSAccessibility 文本区域；VoiceOver 能读出变化的内容，但用户反馈自动朗读不稳定 | iTerm2 issue #12920 [推] |
| iTerm2 | `NSAccessibilityTextAreaRole`；发送 ValueChanged/SelectedTextChanged；新输出用 `NSAccessibilityAnnouncementRequestedNotification` 播报 | IT sources/Accessibility/iTermTextViewAccessibilityHelper.m:321-322；PTYTextView.m:3183-3425 [实] |
| VTE（GTK4） | 实现 `GtkAccessibleText`；Ptyxis 默认关闭（`enable-a11y=false`，"experimental"） | VTE src/vteaccess-gtk4.cc:417-545；PX src/org.gnome.Ptyxis.gschema.xml.in:223-227 [实] |
| Konsole | `QAccessibleTextInterface` | KO src/widgets/TerminalDisplayAccessible.h:27 [实] |

**Avalonia 的现状（commit 8973430）：**

- `src/Avalonia.Controls/Automation/Provider/` 中只有 Value、RangeValue、Toggle、Invoke、Scroll、Selection、ExpandCollapse 等 provider，**没有 ITextProvider**。Win32 自动化层只有 `ITextProvider`/`ITextRangeProvider` 的 interop 定义，没有实现。
- 支持 `AutomationProperties.LiveSetting`，Win32 上会发出 UIA LiveRegionChanged（AV src/Avalonia.Controls/Automation/AutomationProperties.cs:234-243；src/Windows/Avalonia.Win32.Automation/AutomationNode.cs:267-298）。
- Linux 有 AT-SPI 服务，其 Text 接口由 `IValueProvider.Value` 驱动（AV src/Avalonia.FreeDesktop.AtSpi/Handlers/AtSpiTextHandler.cs:293）[实]。

**TKCode 建议：**

1. **第一步：**
   - 终端控件实现 `IValueProvider`：只读，值为可见区域的文本，供 AT-SPI Text 和 UIA Value 使用；
   - 新输出去抖后通过 `LiveSetting=Polite` 的节点播报，回显的按键字符要去掉（WT 的做法）；
   - 提供 VS Code 那样的"可访问缓冲区"：一个只读文本视图，键盘可以浏览全部回滚内容并按命令跳转。
2. **第二步（可选）：** 给 Avalonia 贡献 Text pattern，或者在 Win32 和 macOS 上为终端控件做自定义的原生 peer。工作量大，先不做。
3. **检测屏幕阅读器：** 开启时自动启用播报，关闭最小对比度之外的装饰性动画（VS Code 的 `editor.accessibilitySupport`）。

---

## 10. 配置方案草案（`settings.json` 的 `terminal` 节）

### 10.1 设计原则（沿用 docs/settings.md）

1. **省略即跟随系统。** 字段省略或写 `"system"` 时使用 §1–§9 的"跟随系统"解析结果；写出具体值才固定。设置页显示解析后的实际值。
2. **profile 与 providers 写法相同。** 键与自动探测到的 profile id 相同时，写下的字段覆盖探测值；键不同时是自定义 profile；`"enabled": false` 隐藏。
3. **宽容解析。** 支持 JSONC，字段名不区分大小写，未知字段给出警告；`env` 的值支持 `{env:VAR}`，写 `null` 表示删除该变量。
4. **热重载。**
   - 外观、键盘、鼠标、粘贴、响铃等字段**立即**作用于已打开的终端；
   - 启动类字段（profiles、env、cwd、shellIntegration、term、setLocaleVariables）只影响**新建**的终端。设置页提示"重启终端后生效"，相当于 VS Code 的 `environmentChangesRelaunch`。
5. **只管交互终端。** Agent 的 `bash` 工具不受影响（相当于 VS Code 中 `agentHostProfile` 与 `defaultProfile` 分开）。

### 10.2 示例

```jsonc
{
  "terminal": {
    // 新终端使用的 profile。"system"（默认）：macOS/Linux 用账户的登录 shell；
    // Windows 用 PowerShell 7，没有时用 Windows PowerShell。可以按平台分别指定。
    "defaultProfile": { "windows": "git-bash" },

    "profiles": {
      // ① 覆盖自动探测的 profile：只写要改的字段
      "pwsh": { "args": ["-NoLogo"] },
      // ② 隐藏探测结果
      "cmd": { "enabled": false },
      // ③ 自定义 profile
      "node-dev": {
        "name": "Node 开发",
        "command": "/bin/zsh",            // 绝对路径，或者 PATH 中的名字
        "args": [],
        "loginShell": true,               // macOS 默认 true，Linux 默认 false
        "cwd": "~/work/web",
        "env": { "NODE_ENV": "development", "DEBUG": null },
        "shellIntegration": true,
        "platforms": ["macos", "linux"]
      }
      // ④ 以后：SSH
      // "prod": { "type": "ssh", "host": "prod-1", "user": "deploy", "port": 22 }
    },

    "cwd": "project",                     // project | home | 绝对路径
    "newTabCwd": "inherit",               // inherit | project | home
    "env": { "EDITOR": "code --wait", "HTTPS_PROXY": "{env:CORP_PROXY}" },
    "setLocaleVariables": "auto",         // auto | on | off，只在 macOS 上生效

    "font": { "family": "system", "size": "system", "lineHeight": 1.0, "ligatures": false },
    "cursor": { "style": "system", "blink": "system", "unfocusedStyle": "outline" },
    "colors": { "minimumContrastRatio": 4.5, "boldIsBright": false /*, "dark": {…}, "light": {…} */ },
    "scrollback": 10000,

    "keyboard": {
      "macOptionAsMeta": "none",          // none | left | right | both
      "ctrlCCopies": "system",            // system = Windows 开，其他平台关；只在有选区时生效
      "skipShell": ["-terminal.clear"],   // 加入或移除（"-" 前缀）"不交给 shell"的命令
      "sendKeybindingsToShell": false,
      "kittyKeyboardProtocol": true,
      "altGrAliasing": true               // 只在 Windows 上生效
    },
    "selection": { "wordSeparators": "system", "copyOnSelect": "system", "trimTrailingWhitespace": true },
    "mouse": { "rightClick": "system", "middleClick": "system", "altClickMovesCursor": true },
    "links": { "detectFilePaths": true, "allowedSchemes": ["http", "https", "mailto", "file"] },
    "paste": { "multiLineWarning": "auto", "trimTrailingNewline": true, "largePasteWarning": 0 },
    "bell": { "sound": "system", "visual": true, "attention": true },
    "shellIntegration": { "enabled": true, "decorations": true },
    "confirmClose": "whenRunning",        // whenRunning | always | never
    "ignoreProcesses": ["starship", "oh-my-posh"],   // 追加到内置列表
    "onExit": "closeOnCleanExit",         // closeOnCleanExit | close | hold
    "restore": { "sessions": true, "scrollbackLines": 1000 },
    "unicode": { "ambiguousWidth": "narrow" },        // narrow | wide
    "rendering": { "gpu": "auto", "customGlyphs": true, "smoothScrolling": false },
    "accessibility": { "announceOutput": "auto" }     // auto | on | off
  }
}
```

### 10.3 字段表

**启动**

| 字段 | 类型 | 默认 | 跟随系统 / 固定 | 依据 |
|---|---|---|---|---|
| `defaultProfile` | string，或 `{macos,linux,windows}` | `system` | 跟随系统 | §1.5：passwd 中的 shell；Windows 用最佳 pwsh（WT 首次运行的默认值） |
| `profiles.<id>` | object | 自动探测 | 跟随系统 | 内置 id：<br>• Unix：`system`，以及 `/etc/shells` 中每项的 basename（`zsh`、`bash`、`fish`、`nu`…）<br>• Windows：`pwsh`、`pwsh-preview`、`powershell`、`cmd`、`git-bash`、`wsl:<发行版>`、`msys2`、`cygwin`<br>（取自 WT 生成器和 VS Code 的探测逻辑） |
| `profiles.<id>.command` / `args` | string / string[] | 探测值 | — | VS Code `path`/`args`，WT `commandline` |
| `profiles.<id>.loginShell` | bool | macOS 为 true，Linux 为 false | 跟随系统 | Terminal.app、iTerm2 / GNOME Terminal、Ptyxis。macOS 上的实现方式见 §11 决策 2 |
| `profiles.<id>.cwd` / `env` / `shellIntegration` / `enabled` / `name` / `icon` / `platforms` | — | 继承全局值 | — | WT、VS Code 的 profile 字段 |
| `profiles.<id>.type` | `local` \| `ssh` | `local` | 固定 | 为 SSH 预留：WT 的 SSH 生成器读取 `~/.ssh/config` |
| `cwd` | `project` \| `home` \| 路径 | `project` | 固定 | VS Code 用工作区根目录 |
| `newTabCwd` | `inherit` \| `project` \| `home` | `inherit` | 固定 | VS Code `splitCwd: inherited`、GNOME Terminal `preserve-working-directory`、Ghostty |
| `env` | object，或 `{macos,linux,windows}` | `{}` | — | VS Code `env.*`、WT `environment`；`null` 表示删除（WT 做不到删除） |
| `term` | string | `xterm-256color` | 固定 | §2.3 |
| `setLocaleVariables` | `auto` \| `on` \| `off` | `auto` | 跟随系统（只在 macOS 上生效） | Terminal.app、iTerm2；VS Code `detectLocale` |
| `reloadEnvironment` | bool | true（只在 Windows 上生效） | — | WT `compatibility.reloadEnvironmentVariables` |

**外观**

| 字段 | 类型 | 默认 | 跟随系统 / 固定 | 依据 |
|---|---|---|---|---|
| `font.family` | string \| string[] | `system` | 跟随系统 | §7.1：`.SF NS Mono` → Menlo / Cascadia Mono → Consolas / portal `monospace-font-name` |
| `font.size` | number（pt） | `system` | 跟随系统 | macOS 12（Terminal.app Clear Dark）/ Windows 12（WT）/ Linux 取系统字号（Adwaita Mono 11） |
| `font.lineHeight` / `letterSpacing` / `weight` / `weightBold` / `ligatures` | — | 1.0 / 0 / normal / bold / false | 固定 | VS Code |
| `font.fallback` | string[] | 按平台的 CJK 字体 | 跟随系统 | [推] |
| `cursor.style` | `system` \| `block` \| `bar` \| `underline` | `system` | 跟随系统 | macOS 和 Linux 用 block（Terminal.app、VTE），Windows 用 bar（WT） |
| `cursor.blink` | `system` \| bool | `system` | 跟随系统 | Windows：GetCaretBlinkTime（WT）；GNOME：cursor-blink（VTE）；macOS：不闪（Terminal.app、iTerm2） |
| `cursor.unfocusedStyle` | `outline` \| `none` \| `same` | `outline` | 固定 | VS Code `cursorStyleInactive` |
| `colors.dark` / `colors.light` | 调色板 | TKCode 内置 | 跟随 `theme` | Ghostty `light:X,dark:Y`、WT 的 dark/light 方案对、Ptyxis |
| `colors.minimumContrastRatio` | number | 4.5（高对比度下为 7） | 固定 | VS Code；WT 和 Ghostty 默认不改色（§11 决策 7） |
| `colors.boldIsBright` | bool | false | 固定 | GNOME Terminal / Ptyxis `bold-is-bright=false`；VS Code 默认 true，WT `intenseTextStyle: bright`，三者不一致 |
| `scrollback` | int | 10000 | 固定 | GNOME Terminal、Ptyxis、Konsole、Alacritty |
| `unicode.ambiguousWidth` | `narrow` \| `wide` | `narrow` | 固定 | 所有系统终端 |
| `rendering.gpu` / `customGlyphs` / `smoothScrolling` | — | auto / true / false | 固定 | VS Code；`smoothScrolling` 在 `reduceMotion` 为真时强制关闭 |

**键盘**

| 字段 | 类型 | 默认 | 跟随系统 / 固定 | 依据 |
|---|---|---|---|---|
| `keyboard.macOptionAsMeta` | `none` \| `left` \| `right` \| `both` | `none` | 固定 | Terminal.app 默认关；iTerm2 左右分别为 Normal；VS Code false。Ghostty 的"按键盘布局自动判断"作为以后的 `auto` 选项 |
| `keyboard.ctrlCCopies` | `system` \| bool | `system`（Windows 为 true） | 跟随系统 | WT 新装默认（有选区复制，否则发 ^C） |
| `keyboard.skipShell` | string[] | `[]`（叠加在内置列表上） | — | VS Code `commandsToSkipShell` |
| `keyboard.sendKeybindingsToShell` | bool | false | 固定 | VS Code |
| `keyboard.kittyKeyboardProtocol` | bool | true | 固定 | VS Code、WT、Konsole |
| `keyboard.altGrAliasing` | bool | true | 固定 | WT |
| `keyboard.backspace` | `del` \| `ctrlH` | `del` | 固定 | 所有终端（Terminal.app 的 "Delete sends Control-H" 默认关） |

**鼠标、链接、粘贴、响铃**

| 字段 | 类型 | 默认 | 跟随系统 / 固定 | 依据 |
|---|---|---|---|---|
| `selection.wordSeparators` | string | `system` | 跟随系统 | §5.3：WT / iTerm2 补集 / VTE 补集 |
| `selection.copyOnSelect` | `system` \| `off` \| `primary` \| `clipboard` \| `both` | `system`（Linux 为 primary，其他平台为 off） | 跟随系统 | VTE / Terminal.app / WT；取值沿用 Ghostty |
| `selection.trimTrailingWhitespace` | bool | true | 固定 | WT `trimBlockSelection` |
| `mouse.rightClick` | `system` \| `menu` \| `selectWordAndMenu` \| `copyPaste` \| `paste` \| `none` | `system` | 跟随系统 | VS Code 的分平台默认值 |
| `mouse.middleClick` | `system` \| `pastePrimary` \| `paste` \| `none` | `system`（Linux 为 pastePrimary） | 跟随系统 | VTE |
| `mouse.altClickMovesCursor` | bool | true | 固定 | VS Code、Terminal.app |
| `mouse.bypassReportingModifier` | 修饰键 | `shift` | 固定 | WT、VTE、Ghostty |
| `links.detectUrls` / `detectFilePaths` | bool | true / true | 固定 | VS Code |
| `links.allowedSchemes` | string[] | `http`、`https`、`mailto`、`file`，加上 TKCode 自己的 scheme | 固定 | VS Code `allowedLinkSchemes` |
| `paste.multiLineWarning` | `auto` \| `always` \| `never` | `auto` | 固定 | WT、VS Code、Ghostty |
| `paste.trimTrailingNewline` | bool | true | 固定 | WT `trimPaste` |
| `paste.largePasteWarning` | int（字节，0 = 关） | 0 | 固定 | WT 为 5 KiB |
| `paste.ignoreBracketedPasteMode` | bool | false | 固定 | VS Code |
| `bell.sound` | `system` \| `off` \| 文件路径 | `system` | 跟随系统 | 系统终端都默认播放声音 |
| `bell.visual` / `bell.attention` | bool | true / true | 固定 | iTerm2、Ghostty、Ptyxis |
| `clipboard.oscWrite` / `oscRead` | `allow` \| `ask` \| `deny` | `allow` / `ask` | 固定 | Ghostty |

**Shell 集成与生命周期**

| 字段 | 类型 | 默认 | 跟随系统 / 固定 | 依据 |
|---|---|---|---|---|
| `shellIntegration.enabled` | bool | true | 固定 | VS Code、Ghostty（§11 决策 3） |
| `shellIntegration.decorations` | bool | true | 固定 | VS Code `decorationsEnabled` |
| `shellIntegration.notifyOnFinish` | `never` \| `unfocused` | `unfocused`，超过 10 秒的命令 | 固定 | Ptyxis、Ghostty、iTerm2 [阈值为推] |
| `confirmClose` | `whenRunning` \| `always` \| `never` | `whenRunning` | 跟随系统（macOS/Linux）；Windows 比 WT 保守 | §8.1 |
| `ignoreProcesses` | string[] | 追加到内置列表 | — | VS Code `ignoreProcessNames`、WezTerm、Terminal.app |
| `onExit` | `closeOnCleanExit` \| `close` \| `hold` | `closeOnCleanExit` | 固定 | WT `automatic`、Terminal.app |
| `title.allowApplicationTitle` | bool | true | 固定 | WT `suppressApplicationTitle: false` |
| `restore.sessions` / `scrollbackLines` | bool / int | true / 1000 | 固定 | WT、VS Code、Terminal.app |
| `accessibility.announceOutput` | `auto` \| `on` \| `off` | `auto`（检测到屏幕阅读器时开） | 跟随系统 | WT、VS Code |

---

## 11. 需要用户决策的点

| # | 问题 | 选项 | 建议 |
|---|---|---|---|
| 1 | **终端获得焦点时的快捷键归属**：macOS ⌘K 在 Terminal.app、iTerm2、Ghostty、VS Code 中都是清屏，在 TKCode 中是命令面板；Win/Linux 上 TKCode 的全局键是 Ctrl+K/N/B（MainWindow.axaml.cs:457-541），会抢走 readline 的 kill-line、下一条历史、光标左移（Ctrl+B 还是 tmux 的前缀键） | A：终端获得焦点时 ⌘K 清屏，命令面板改用 ⌘⇧P/⌘P；Win/Linux 上终端获得焦点时 Ctrl+字母一律交给 shell，TKCode 的全局键改用或增加 Ctrl+Shift 版本（命令面板 Ctrl+Shift+P，与 WT 相同）。B：⌘K 和 Ctrl+K 始终打开命令面板，清屏改为 ⌘⇧K 或 ⌘L，用户可以在 `keyboard.skipShell` 中去掉。C：终端中按 ⌘K 时先打开命令面板，命令面板中提供"清屏" | **A**：与 macOS 所有终端以及 VS Code 一致；Win/Linux 上不抢 shell 的 Ctrl 键，与 WT、GNOME Terminal 的 Ctrl+Shift 约定一致 |
| 2 | **macOS 启动 login shell 的方式** | A：`/usr/bin/login -flp <user> /bin/bash --noprofile --norc -c 'exec -l <shell>'`（Terminal.app、iTerm2、Ghostty、Alacritty）；B：argv0 设为 `-zsh`（WezTerm），或者 `-l` 参数（VS Code） | **A**：显示 "Last login"，有 utmp 记录，`getlogin()` 正确，与系统终端一致。代价是 login 以 root 运行、多一层进程，退出码要实测。用户可以用 `~/.hushlogin` 去掉 "Last login" |
| 3 | **shell 集成是否默认自动注入** | A：默认开（VS Code、Ghostty、kitty）；B：默认关，用户手动开启或 source（WT、iTerm2、WezTerm） | **A**：命令导航、退出码、cwd 继承、关闭确认、以后 Agent 读取命令输出都靠它。必须保证可以一键关闭，并显示失败原因 |
| 4 | Windows 默认 shell | A：最佳 pwsh → Windows PowerShell（WT、VS Code）；B：Git Bash 优先，与 Agent 的 `ShellResolver` 一致；C：读取 WT 的 `defaultProfile` | **A**；C 可以作为以后的增强 |
| 5 | PTY 放在哪个进程 | A：Desktop 进程；B：Agent Host 进程（相当于 VS Code 的 ptyHost，界面重启后终端还活着，Agent 也能直接访问） | 倾向 **B**，但会影响 Agent Host 的生命周期设计，需要和架构文档一起决定 |
| 6 | Linux 的 PRIMARY 选区 | A：等 Avalonia 12.2 后升级（只支持 X11/XWayland）；B：12.1.x 期间只做进程内的 PRIMARY | 先 **B**，12.2 发布后再升级 |
| 7 | 最小对比度默认值 | A：4.5（VS Code）；B：1，即不改色（WT、Ghostty、iTerm2） | **A**：亮色主题下 ANSI 黄色、青色的可读性问题明显 |
| 8 | macOS LANG 的来源 | A：系统区域（Terminal.app、iTerm2）；B：TKCode 界面语言（VS Code） | **A**：中文界面的用户不一定希望命令输出也变成中文 |
| 9 | 回滚内容恢复 | 不恢复 / 最近 N 行 / 全部 | 最近 1000 行，可以关闭；恢复文件可能含敏感输出 |
| 10 | 新标签的 cwd | 继承当前标签 / 项目根目录 | 继承当前标签（VS Code splitCwd、Ghostty） |

---

## 12. 来源

### 源码仓库（均为浅克隆，只读）

| 仓库 | commit | 主要文件 |
|---|---|---|
| https://github.com/microsoft/vscode | `4765bd3` | `src/vs/base/node/shell.ts`、`src/vs/base/node/powershell.ts`、`src/vs/base/common/processes.ts`、`src/vs/platform/shell/node/shellEnv.ts`、`src/vs/platform/terminal/node/{terminalProfiles,terminalEnvironment,terminalProcess,childProcessMonitor,ptyService}.ts`、`src/vs/platform/terminal/common/{terminal,terminalPlatformConfiguration,terminalEnvironment}.ts`、`src/vs/platform/terminal/common/xterm/shellIntegrationAddon.ts`、`src/vs/workbench/contrib/terminal/common/{terminalConfiguration,terminalEnvironment,terminal}.ts`、`src/vs/workbench/contrib/terminal/common/scripts/*`、`src/vs/workbench/contrib/terminal/browser/{terminalProfileResolverService,terminalInstance,terminalActions}.ts`、`src/vs/workbench/contrib/terminalContrib/{clipboard,sendSequence,links,accessibility,notification}/…`、`src/vs/editor/common/config/fontInfo.ts` |
| https://github.com/xtermjs/xterm.js | `991857a` | `src/common/Color.ts`、`src/common/input/Keyboard.ts`、`src/browser/Clipboard.ts`、`src/browser/services/SelectionService.ts`、`src/browser/AccessibilityManager.ts` |
| https://github.com/microsoft/terminal | `44b440b` | `src/cascadia/TerminalSettingsModel/*`（defaults.json、userDefaults.json、各生成器、MTSMSettings.h）、`src/cascadia/TerminalConnection/ConptyConnection.cpp`、`src/cascadia/TerminalControl/*`、`src/cascadia/TerminalApp/*`、`src/cascadia/TerminalCore/*`、`src/terminal/input/terminalInput.cpp`、`src/types/{ColorFix,utils}.cpp`、`src/renderer/uia/UiaRenderer.cpp`、`src/inc/til/env.h`、`doc/cascadia/profiles.schema.json` |
| https://github.com/ghostty-org/ghostty | `b3dc3d7` | `src/termio/Exec.zig`、`src/termio/shell_integration.zig`、`src/shell-integration/*`、`src/config/Config.zig`、`src/Surface.zig`、`src/input/paste.zig`、`src/os/{passwd,shell}.zig`、`src/terminal/color.zig` |
| https://github.com/wezterm/wezterm | `e2a98e1` | `env-bootstrap/src/lib.rs`、`pty/src/cmdbuilder.rs`、`config/src/config.rs`、`mux/src/{domain,localpane}.rs`、`wezterm-gui/src/{commands,inputmap}.rs`、`assets/shell-integration/wezterm.sh` |
| https://github.com/alacritty/alacritty | `5421470` | `alacritty_terminal/src/tty/{unix,mod,windows/mod}.rs`、`alacritty/src/config/*`、`extra/man/*.scd` |
| https://github.com/gnachman/iTerm2 | `5794785` | `sources/Settings/Profiles/ITAddressBookMgr.m`、`sources/Tasks/{shell_launcher.c,PTYTask.m}`、`sources/PTYSession/PTYSession.m`、`sources/Settings/iTermProfilePreferences.m`、`sources/…/iTermPreferences.m`、`sources/ShellIntegration/ShellIntegrationInjection.swift`、`sources/Keyboard/iTermStandardKeyMapper.m`、`sources/TerminalView/PTYTextView.m`、`sources/Categories/NSColor+iTerm.m` |
| https://gitlab.gnome.org/GNOME/vte | `a5c88ec` | `src/{vtegtk,vte,spawn,widget,pastify,vteseq,vteaccess-gtk4}.cc`、`src/vte.sh.in`、`src/vtedefines.hh` |
| https://gitlab.gnome.org/GNOME/gnome-terminal | `1079728` | `src/terminal-screen.cc`、`src/terminal-client-utils.cc`、`src/eggshell.cc`、`src/org.gnome.Terminal.gschema.xml` |
| https://gitlab.gnome.org/GNOME/ptyxis | `ecc97d3` | `src/ptyxis-client.c`、`agent/*`、`src/org.gnome.Ptyxis.gschema.xml.in`、`src/ptyxis-application.c` |
| https://invent.kde.org/utilities/konsole | `6ab4bb0` | `src/profile/Profile.cpp`、`src/session/{Session,SessionController}.cpp`、`src/terminalDisplay/TerminalDisplay.cpp`、`src/Vt102Emulation.cpp` |
| https://github.com/AvaloniaUI/Avalonia | `8973430` | `src/Avalonia.X11/X11Platform.cs`、`src/Avalonia.Base/Input/Platform/ClipboardType.cs`、`src/Avalonia.Controls/{TopLevel,TextBox}.cs`、`src/Avalonia.Base/Platform/IPlatformSettings.cs`、`src/Avalonia.Controls/Automation/*`、`src/Windows/Avalonia.Win32.Automation/*`、`src/Avalonia.Wayland/WaylandPlatform.cs` |
| https://github.com/apple-oss-distributions/system_cmds（login/login.c） | `408bba7453` | login(1) 的 `-p`/`-f`/`-l`、SHELL、PATH、hushlogin |

### 文档与网页

- Apple Terminal 使用手册 2.15：
  - https://support.apple.com/en-us/guide/terminal/trmlstrtup/2.15/mac/26
  - https://support.apple.com/guide/terminal/change-profiles-shell-settings-trmlshll/mac
  - https://support.apple.com/guide/terminal/use-marks-and-bookmarks-trml135fbc26/2.15/mac/27
  - https://support.apple.com/en-us/guide/terminal/trmlshtcts/2.15/mac/26
  - https://support.apple.com/guide/terminal/trmlc69728a5/mac
- iTerm2：
  - https://iterm2.com/documentation-shell-integration.html
  - https://iterm2.com/documentation-escape-codes.html
  - https://iterm2.com/documentation-restoration.html
- kitty：https://sw.kovidgoyal.net/kitty/shell-integration/
- Windows Terminal：
  - https://learn.microsoft.com/en-us/windows/terminal/tutorials/shell-integration
  - https://learn.microsoft.com/en-us/windows/terminal/customize-settings/startup （说"不保存内容"，已过时）
  - https://devblogs.microsoft.com/commandline/windows-terminal-is-now-the-default-in-windows-11/
- GNOME gsettings-desktop-schemas：https://gitlab.gnome.org/GNOME/gsettings-desktop-schemas/-/raw/master/schemas/org.gnome.desktop.interface.gschema.xml.in
- VS Code Flatpak：https://github.com/flathub/com.visualstudio.code
- Avalonia.Wayland NuGet：https://www.nuget.org/packages/Avalonia.Wayland
- man 页：`login(1)`、`path_helper(8)`

### 本机核实（macOS 26.6.2，只读）

- `ps`：Terminal.app 与 VS Code 的进程树和环境
- `strings`：Terminal.app 二进制中的字符串
- `plutil -p`：Terminal.app 的 `Initial Settings/settings.plist`、`keyMappings.plist`
- `cat`：`/etc/zprofile`、`/etc/zshrc`、`/etc/zshrc_Apple_Terminal`、`/etc/bashrc_Apple_Terminal`、`/etc/paths`、`/etc/paths.d/*`
- `/usr/libexec/path_helper -s`
- `mdls`：`/System/Library/Fonts/SFNSMono.ttf` 的字体族名（`.SF NS Mono`）
- `launchctl print gui/501`
