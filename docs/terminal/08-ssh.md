# 08 · SSH

> 状态：设计稿 v1（2026-10-17）。上游：[01-architecture.md](01-architecture.md) D4、§6.2、§10 M7；[research/06](research/06-ssh-transport.md)；[research/03](research/03-vt-protocol.md) §7；[07-shell-profiles-config.md](07-shell-profiles-config.md)（profile、环境变量、shell 集成、`terminal` 节）；[04-pty-host.md](04-pty-host.md)（IPC、会话）。
>
> 标注：**[实]** = 已在源码、官方文档或本机核实；**[推]** = 推断，实现前要验证；**[定]** = 本文的设计决定。
>
> 源码版本：OSSH = openssh-portable `6545e6a`（2026-10-16）；TMDS = Tmds.Ssh `e186178`（2026-10-14，发布版 0.26.0）；GH = ghostty `b3dc3d7`；ZED = zed `6b218b8`；WT = microsoft/terminal `44b440b`。

## 0. 结论摘要

1. **第一期 = 本地 PTY 里运行系统 `ssh`**（D4）。TCode 不写任何 SSH 协议代码，只做三件事：
   - 从 `~/.ssh/config` 生成主机列表（`ssh:<别名>` profile）；
   - 构造 `ssh` 命令行；
   - 处理连接失败（退出码 255）的界面。

   认证、主机密钥确认、FIDO 触摸、ControlMaster 复用全部由 OpenSSH 在终端里自己完成，行为与系统终端完全一致。
2. **TERM 不需要 `SetEnv`**：本地 TERM 已经是 `xterm-256color`，ssh 会把它放进 `pty-req` 发给服务端 [实 ssh_config(5)]。另外：
   - **不要**像 Ghostty 那样传 `-o SetEnv=TERM=…`。`SetEnv` 是"第一次出现的那组生效"，命令行上的 `-o SetEnv` 会**整组替换**用户 config 里的 `SetEnv` [实 OSSH readconf.c:1962-1989]；
   - 用 `-o SendEnv=COLORTERM` 等传递 `COLORTERM`、`TERM_PROGRAM`、`TERM_PROGRAM_VERSION`。`SendEnv` 是追加语义 [实 OSSH readconf.c:1936-1960]，服务端不接受时被静默丢弃（env 请求的 want_reply=0）[实 OSSH clientloop.c:2706]。上游 sshd 默认不接受任何变量；Debian/Ubuntu 默认接受 `COLORTERM`。
3. **askpass 第一期不接**：ssh 运行在 PTY 里，所有提示都直接出现在终端中。Zed 需要 askpass，是因为它的 master 连接没有 TTY [实 ZED crates/askpass]；我们没有这种场景。
4. **第二期**：引入 Tmds.Ssh，只用于 SFTP 文件树和 Agent 的远程只读操作。
   - 配置**不交给 Tmds.Ssh 的解析器**（它遇到 `ProxyCommand`、`SetEnv` 等关键字会直接抛异常 [实 research/06 §2.3]），而是先运行 `ssh -G <别名>` 拿到 OpenSSH 的最终结果，再自己映射到 `SshClientSettings`；
   - 无法映射的情况（ProxyCommand、HostKeyAlias、keyboard-interactive、FIDO 密钥文件、PKCS#11）整体判为"不兼容"，文件树显示原因，终端不受影响；
   - Tmds.Ssh 的几个公开 API 缺口已经核实：`Proxy` 只有 internal 抽象成员，外部无法实现 ProxyCommand；`SshAgentCredentials(address)` 是 internal，无法指定 `IdentityAgent`；没有基于 Stream 的 `SftpClient`。这些计划通过上游 PR 解决。
5. **第二期的交互终端仍然用系统 ssh**，不换成 Tmds.Ssh 的通道：一致性的价值远大于"少一次认证"。只提供一个可选的 `managed` 模式，给 Windows 上只能用密码登录的主机用（Windows 没有 ControlMaster，否则要输两次密码）。

---

## 1. 范围

| 期 | 交付 | 里程碑 |
|---|---|---|
| 一期 | 主机列表；`ssh` profile（探测得到的 + settings 中自定义的）；`ssh` 可执行文件探测与缺失提示；`SendEnv`；255 退出码的界面；与 ControlMaster 共存；安全规则；CI 容器测试 | M7（01 §10） |
| 二期 | Tmds.Ssh 引入与 AOT 验证；`ssh -G` → `SshClientSettings` 的映射；known_hosts 界面；SFTP 连接池；远程文件树；可选的 `managed` 终端模式；Tmds.Ssh 上游 PR | 文件树功能上线时 |
| 以后 | 远程 shell 集成（§2.7）；ControlMaster 预热；Windows 上的 askpass | — |

不做：内置 SSH 客户端替代系统 ssh（research/06 的路线 B）；mosh；串口。

---

## 2. 第一期：系统 ssh

### 2.1 ssh 可执行文件的探测

| 平台 | 候选（按顺序） | 依据 |
|---|---|---|
| macOS | `/usr/bin/ssh` | 系统自带 |
| Linux | 基础环境 PATH 中的 `ssh`；然后 `/usr/bin/ssh`、`/usr/local/bin/ssh` | 发行版包 `openssh-client(s)` |
| Windows | ① `%SystemRoot%\System32\OpenSSH\ssh.exe`（"可选功能"安装）；② `%ProgramFiles%\OpenSSH\ssh.exe`（MSI 安装）；③ `%ProgramFiles(x86)%\OpenSSH\ssh.exe`；④ PATH 中的 `ssh.exe`；⑤ Git for Windows 自带的 `<Git>\usr\bin\ssh.exe` | ①–③ [实 WT SshHostGenerator.cpp:18-24、44-58]；④⑤ [定] |
| 全部 | `terminal.ssh.path` 设置了就只用它 | [定] |

- **Git for Windows 的 ssh 排在最后**，并在界面上标注"MSYS 版本"：
  - 它是 MSYS 程序，`~` 指向 MSYS 的 home；
  - 它不使用 Windows 的 `\\.\pipe\openssh-ssh-agent`，所以 Windows 的 ssh-agent 服务、1Password 的 agent 都对它无效 [实 research/06 §3]；
  - 与 Win32-OpenSSH 混用时，用户会遇到"终端里能连上，文件树连不上"这类不一致。
- **外部命令的执行**：`ssh -V`、`ssh -G` 都在 pty host 中运行，必须经 07 §2.8 的执行器（`tcode-pty-spawn --exec` + 管道），不能用 `System.Diagnostics.Process`（03 §10 R5、04）。
- **版本检查**：探测到后运行一次 `ssh -V`（版本信息输出到 stderr，例如 `OpenSSH_10.3p1, LibreSSL 3.3.6`、`OpenSSH_for_Windows_9.5p1, LibreSSL 3.8.2`），结果按路径和 mtime 缓存。
  - 版本低于 7.3 时，`Include` 和 `ProxyJump` 不可用，主机列表仍然生成，但在设置页提示 [推：7.3 引入 Include/ProxyJump]；
  - `ssh -G` 需要 6.8 以上 [推]。
- **找不到 ssh 时**：主机列表为空，设置页的 SSH 子页和"+"菜单的 SSH 分组显示一条提示：
  - Windows："未找到 OpenSSH 客户端。可以在 设置 → 系统 → 可选功能 中添加"OpenSSH 客户端"，或在管理员 PowerShell 中运行 `Add-WindowsCapability -Online -Name OpenSSH.Client~~~~0.0.1.0`。" 附带"重新检测"按钮。命令只显示给用户，**TCode 不自动执行**（需要管理员权限）。
  - Linux："未找到 ssh。请通过发行版的包管理器安装 openssh-client（Debian/Ubuntu）或 openssh-clients（Fedora/RHEL）。"
  - macOS 不会出现这种情况。
- 探测在 pty host 中完成（`SshProfileProvider : IProfileProvider`，07 §1.2），与本地 shell 的探测共用缓存和刷新机制（07 §2.6）。

### 2.2 主机列表：解析 `~/.ssh/config`

**读哪些文件**：

| 平台 | 用户配置 | 系统配置 |
|---|---|---|
| macOS / Linux | `~/.ssh/config` | `/etc/ssh/ssh_config` |
| Windows（Win32-OpenSSH） | `%USERPROFILE%\.ssh\config` | `%ProgramData%\ssh\ssh_config` [实 WT SshHostGenerator.cpp:26-27] |

`terminal.ssh.configFile` 设置了时只读这个文件（对应 `ssh -F`），并且所有 ssh 命令都加 `-F <file>`。

**解析器**（`SshConfigScanner`，只为了列出主机名，不解释其他关键字）[定]：

```
Scan(file, isUserConfig, depth):
  if depth > 16: 警告并停止                    // 与 OpenSSH 的 READCONF_MAX_DEPTH 相同 [实 OSSH readconf.c:2629]
  for line in ReadLines(file)（去掉 UTF-8 BOM，兼容 CRLF）:
      line = 去掉首尾空白；空行和 '#' 开头的行跳过
      (keyword, args) = 拆分：关键字后面可以是空白或 '='（"Host=foo" 也合法）；参数支持双引号
      switch lower(keyword):
        "host":
            for pattern in args:
                if pattern 含 '*' 或 '?'，或以 '!' 开头: continue          // 通配符与否定不生成条目
                yield HostEntry(alias: pattern, file, line, block: 当前块)
        "match": 进入 Match 块（不生成条目；块内的 HostName 等不归属任何别名）
        "include":
            for spec in args:
                path = ExpandTilde(ExpandEnv(spec))                        // ssh_config(5)：支持 ~、环境变量；%token 不展开
                if !IsAbsolute(path): path = (isUserConfig ? "~/.ssh/" : "/etc/ssh/") + path
                for f in Glob(path) 按字典序: Scan(f, isUserConfig, depth + 1)
        "hostname", "user", "port", "proxyjump", "remotecommand", "requesttty":
            记录到当前 Host 块（只用于展示和 §2.4 的提示；连接时以 ssh 自己的解析为准）
```

- 依据：`Include` 的相对路径规则、glob、按字典序处理、可以出现在 Host/Match 块内 [实 OSSH ssh_config.5:288-310]。
- **块内的 Include 无条件展开**：列主机时我们不知道它所在的块会不会匹配。这样可能多列出一些主机，但不会漏掉。
- **去重**：ssh 会把命令行上的主机名转成小写再匹配 [推]，所以别名不区分大小写去重，保留第一次出现的写法。
- **与 WT 的差异**：WT 只列出带有 `HostName` 的 Host 块 [实 WT SshHostGenerator.cpp:94-131]。我们列出所有非通配的别名，因为 `Host build-01` 加上 DNS 就能直接连接，不一定写 HostName。
- **隐藏的主机** [定]：常见 git 托管站点的 Host 块只用来配置密钥，连上去也拿不到 shell。默认隐藏下列别名（可以在 `terminal.ssh.hiddenHosts` 中修改）：
  - `github.com`、`gist.github.com`、`ssh.github.com`；
  - `gitlab.com`、`altssh.gitlab.com`；
  - `bitbucket.org`、`altssh.bitbucket.org`；
  - `ssh.dev.azure.com`、`vs-ssh.visualstudio.com`；
  - `codeberg.org`、`git.sr.ht`。
- **排序**：先按 `state.json` 中记录的最近使用时间，没有记录的按文件中出现的顺序。
- **缓存**：记录所有读过的文件（包括 Include 进来的）的路径和 mtime；"+"菜单打开时检查 mtime，变了就重新扫描。一般一次扫描不到 5 ms。
- **known_hosts 不用来生成列表**：其中的主机名通常是哈希过的（`|1|salt|hash`），而且大量是 git 托管站点。

### 2.3 `ssh -G`：什么时候用，怎么用

`ssh -G <别名>` 会在完成 Host/Match 解析后打印**最终配置**并退出，不发起连接 [实 OSSH ssh.1:276-283]。

**注意**：它会**执行** `Match exec` 里的命令（解析 Match 块就要执行），`CanonicalizeHostname` 也可能触发 DNS 查询 [推]。因此 [定]：

- **不在生成列表时批量运行**：100 个主机就会执行 100 次用户的 `Match exec` 命令；
- 只在以下时机按需运行（单个主机，结果缓存到 config 文件的 mtime 改变为止）：
  1. 设置页打开某个 SSH 主机的详情（显示解析后的 HostName、User、Port、ProxyJump、IdentityFile）；
  2. 第二期建立 Tmds.Ssh 连接之前（§3.2）；
  3. 一期连接前的"预检"——默认关闭，见 §2.4；
- **运行方式**：
  - `argv = [ssh, (-F file)?, -G, --, <别名>]`；
  - 环境同 07 §4（剔除 `TCODE_*` 等）；
  - 经 07 §2.8 的执行器运行；stdin 接 `/dev/null`，超时 5 s；
  - stdout 按行解析为 `key value`：关键字是小写的；`identityfile`、`certificatefile`、`sendenv`、`setenv`、`localforward` 等可以出现多次，收集成列表；
- **退出码非 0**（通常是 255，config 有语法错误）：把 stderr 显示在主机详情中，例如 "`/Users/u/.ssh/config line 12: Bad configuration option: foo`"。

### 2.4 ssh profile 的形态

**探测得到的**（`SshProfileProvider`）：

```csharp
new TerminalProfile(
    Id: "ssh:" + alias, Name: alias, Command: sshPath,
    Arguments: BuildSshArgs(target, config),         // 见下
    LoginShell: false, Kind: ShellKind.Ssh, Icon: "ssh", Source: ProfileSource.Detected)
{
    Ssh = new SshTarget(Destination: alias, User: null, Port: null, ConfigFile: cfg.ConfigFile),
    ShellIntegration = false,                        // §2.6
    PathStyle = PathStyle.Native,
}
```

**settings 中自定义的**（`terminal.profiles.<id>`，`"type": "ssh"`）：

| 字段 | 类型 | 映射为 | 说明 |
|---|---|---|---|
| `host` | string，**必填** | 目标 | `~/.ssh/config` 中的别名或主机名 |
| `user` | string | `-l <user>` | |
| `port` | int | `-p <port>` | |
| `identityFile` | string | `-i <path>` | 展开 `~` |
| `jump` | string | `-J <spec>` | ProxyJump |
| `args` | string[] | 插在目标之前 | 其他 ssh 选项，例如 `["-A"]` |
| `remoteCommand` | string | 目标之后的命令；同时加 `-t` | 例如 `"tmux new -A -s main"` |
| `sendEnv` | bool | 覆盖全局 `terminal.ssh.sendEnv` | |
| `name`、`icon`、`enabled`、`env`、`platforms` | — | 同 07 §7.4 | `env` 作用于本地的 ssh 进程（例如 `SSH_AUTH_SOCK`） |

探测得到的 `ssh:<别名>` 也可以用同样的字段覆盖，例如 `"ssh:prod-1": { "remoteCommand": "tmux new -A -s main" }`。

**命令行构造**：

```
BuildSshArgs(t, cfg):
  args = []
  if cfg.ConfigFile: args += ["-F", cfg.ConfigFile]
  if sendEnv: args += ["-o", "SendEnv=COLORTERM", "-o", "SendEnv=TERM_PROGRAM", "-o", "SendEnv=TERM_PROGRAM_VERSION"]
  if t.User: args += ["-l", t.User]
  if t.Port: args += ["-p", t.Port]
  if t.IdentityFile: args += ["-i", t.IdentityFile]
  if t.Jump: args += ["-J", t.Jump]
  args += t.ExtraArgs
  if t.RemoteCommand: args += ["-t"]
  args += ["--", t.Destination]                     // "--" 之后不再解析选项：防止以 "-" 开头的主机名被当作选项
  if t.RemoteCommand: args += [t.RemoteCommand]     // 作为一个参数；远端由用户的登录 shell 解析
  return args
```

- **默认不加 `-t`**（与 01 D4 "`ssh -t`"的字面写法不同，列入 §8）：
  - 没有远程命令时，只要本地 stdin 是终端（PTY 正是），ssh 就会自动请求 PTY（`RequestTTY auto`）；
  - 强制 `-t` 会覆盖用户 config 中的 `RequestTTY no`，这不符合"与系统一致"。
  - 有 `remoteCommand` 时才加 `-t`，否则远端不会分配 PTY。
- **校验目标** [定]：`host`、`user`、`jump` 不能包含空白和控制字符，`host`、`user` 不能以 `-` 开头。不合法时该 profile 报错、停用（07 §7.5）。虽然 argv 不经过 shell，`--` 也已经挡住了选项注入，但用户名或主机名以 `-` 开头基本都是配置错误。
- **本地进程的环境**：同 07 §4 的规则（TERM、COLORTERM、TERM_PROGRAM 等都设置好）。
  - `SSH_AUTH_SOCK` 来自 BaseEnvironment。macOS 上它由 launchd 提供；Linux 上由桌面会话提供；Windows 的 Win32-OpenSSH 默认使用命名管道，不需要这个变量 [实 research/06 §3]。
  - **不设置 `SSH_ASKPASS*`**（§2.8）。
- **macOS 不经 login 包装**：ssh 不是 shell（07 §3.2 的"非 shell 命令"），直接由 spawn helper 启动。`loginShell` 对 ssh profile 无意义，设置页不显示这个开关。这样还有一个好处：login 包装会吞掉退出码（login 恒 `exit(0)`，03 §8.3），而 ssh profile 不经 login，所以 §2.8 依赖的 255 退出码在 macOS 上也能可靠拿到 [定]。
- **起始目录**：本地 cwd 对 ssh 没有意义，使用 home（避免 ssh 在不存在的目录中启动报错）。新标签从 ssh 标签继承 cwd 时不继承远端路径（07 §6.4）。
- **标签标题**（07 §6.2）：没有远端 OSC 标题时显示别名；副标题显示 `user@host:port`（来自 `ssh -G` 的缓存，没有缓存时只显示别名）。

### 2.5 远端的 TERM、COLORTERM 与 TERM_PROGRAM

| 变量 | 怎么到达远端 | 结论 | 依据 |
|---|---|---|---|
| `TERM` | ssh 在 `pty-req` 中携带本地的 `$TERM`。"TERM 在请求 PTY 时总会发送" | 本地已经是 `xterm-256color`（07 §4.2），远端无需任何处理。**不加** `SetEnv TERM=…` | [实 OSSH ssh_config.5:1905-1908；research/06 §4] |
| `COLORTERM` | `SendEnv`，要求服务端 `AcceptEnv` | Debian/Ubuntu 的默认 sshd_config 含 `AcceptEnv LANG LC_* COLORTERM NO_COLOR` [实 research/06 §4]；RHEL、macOS 和上游默认都不接受 → 远端程序回退到 256 色或主动查询 | [实 sshd_config(5)："The default is not to accept any environment variables"] |
| `TERM_PROGRAM`、`TERM_PROGRAM_VERSION` | `SendEnv` | 绝大多数服务器都不接受，被静默丢弃 | 同上 |
| `LANG`、`LC_*` | macOS 的默认 ssh_config 已经写了 `SendEnv LANG LC_*` [实 research/06 §4] | 与系统 ssh 相同，TCode 不额外处理 | |

**为什么 `SendEnv` 安全而 `SetEnv` 不安全**：

| 关键字 | 合并语义 | 在命令行上用 `-o` 传入的后果 |
|---|---|---|
| `SendEnv` | **追加**（源码注释："appends to list; doesn't respect first-match-wins"）；用 `-VAR` 可以从列表中删除 | 在用户配置的基础上追加，无副作用 [实 OSSH readconf.c:1936-1960] |
| `SetEnv` | **第一次出现的那组整体生效**（`found = options->num_setenv == 0`） | 命令行参数先于配置文件处理，所以用户 config 里的所有 `SetEnv` 都会失效 [实 OSSH readconf.c:1962-1989]。Ghostty 的 `ssh-env` 正是这样做的 [实 GH src/cli/ssh.zig:290-303]，这是一个副作用 |

**服务端拒绝时**：env 请求的 want_reply 是 0 [实 OSSH clientloop.c:2706]，客户端不会收到错误，也不会打印任何内容。用户看不到任何报错，正是我们想要的效果。

**没有 COLORTERM 时的真彩色**：程序仍然可以用 DECRQSS 或 XTGETTCAP 主动查询，这些查询经过 ssh 原样往返，由本地的镜像核心应答（research/03 §7.2–7.3）。

**`terminal.ssh.sendEnv` 默认 true** [定]。关闭它的理由只有一个：某些网络设备的 SSH 服务端遇到 env 请求会断开连接 [推]。

### 2.6 远端的 shell 集成

- **默认不注入**（07 §5.8）：
  - 远端没有我们的脚本，自动写入远端文件属于侵入行为；
  - 远端可能根本不是 POSIX shell（网络设备、Windows OpenSSH 服务端的 cmd）。
- **一期的效果**：
  - 本地命令跟踪器看到的是一条"正在执行"的 ssh 命令（对 ssh profile 来说，整个会话就是这一条）；
  - 远端如果自己配置了 OSC 133（vte.sh、远端安装的 iTerm2/WezTerm 脚本、fish 4、nushell），镜像核心照样解析，状态为 `Native`（07 §5.5）。这时命令导航可用，但没有 nonce，也就没有可信的命令行，Agent 只能把它当作参考；
  - 远端发来的 OSC 7 带有远端主机名，**不能**当作本地路径（07 §6.4；research/03 §7.3）；
  - 用户可以在远端手动 source 我们的脚本：设置页提供"复制安装命令"，内容是把 `current/<shell>/…` 的脚本内容通过 heredoc 写到远端 `~/.tcode/shell-integration/`，再加一行 rc 片段。这个命令由用户自己粘贴执行。
- **以后的自动方案**（v2，按主机选择开启：`"ssh:prod-1": { "shellIntegration": true }`）：

| 方案 | 做法 | 优点 | 缺点 |
|---|---|---|---|
| A. kitty 式 tty 引导 | `ssh -t host '<一段 POSIX sh 引导脚本>'`：脚本关闭回显，用一次性口令向终端请求数据；TCode 在本地识别这个请求（一种私有 OSC/DCS），把 base64 的 tar（集成脚本 + 版本号）写进 PTY；脚本解包到 `~/.cache/tcode-si/<hash>/` 后，`exec` 用户的登录 shell，并按 07 §5.3 的方式注入（ZDOTDIR 等通过 `env` 设置，不依赖 AcceptEnv） | 只用一条连接、一次认证；Windows 本地也能用（不需要 ControlMaster）；不依赖 AcceptEnv | 只适用于 POSIX 远端；会占用 RemoteCommand（与用户自己的 RemoteCommand 冲突）；需要在 TCode 的 PTY 输出路径上加一个握手识别器 [实 research/06 §5 引用 kitty 文档] |
| B. Ghostty 式 ControlMaster 复制 | 首次连接时经临时 ControlMaster 运行 `mkdir -p … && cat > …` 写入脚本，本地缓存"已安装"；之后用 `ssh -t host 'exec env … $SHELL -l'` | 实现简单 | Windows 上没有 ControlMaster，会认证两次；需要远端可写 [实 GH src/cli/ssh.zig:480-560 的 terminfo 安装流程] |

  **建议 A** [定]：Windows 是 TCode 的一等平台，B 在 Windows 上体验差。A 的识别器需要 01 的镜像核心或会话 actor 支持"私有握手序列"，到时在 04 中增加。

### 2.7 askpass：要不要接到 UI

**Zed 的做法** [实 ZED crates/askpass/src/askpass.rs:34-60、225-405；research/06 §1]：

- Zed 为每次连接建立一个临时目录，里面有 Unix socket 和 askpass 脚本（Windows 上直接用 Zed 的 cli.exe）；
- 设置 `SSH_ASKPASS=<脚本>` 和 `SSH_ASKPASS_REQUIRE=force`。ssh 需要密码、口令或确认时会运行这个脚本，脚本通过 socket 把提示发给 Zed，Zed 用对话框收集输入；
- **前提**：Zed 的 master 连接是 `ssh -N`，**没有 TTY**。没有 askpass，ssh 就无法提示用户。

**TCode 一期不需要它** [定]：

| 场景 | 有没有 TTY | 提示出现在哪里 |
|---|---|---|
| 交互终端（ssh profile、用户手动运行 ssh） | 有（我们的 PTY） | 终端里，与 Terminal.app 等完全一致：密码、密钥口令、OTP、`Are you sure you want to continue connecting (yes/no/[fingerprint])?`、FIDO 的 "Confirm user presence for key …" |
| `ssh -V`、`ssh -G` | 不需要 | 不会认证，没有提示 |

- 设置 askpass 反而会改变行为：`SSH_ASKPASS_REQUIRE=force` 会让交互终端也弹出对话框。所以交互终端**不设置** askpass，用户自己在环境中设置的 `SSH_ASKPASS*` 原样保留（这是用户的选择）。
- **什么时候需要**：TCode 在后台运行没有 TTY 的 ssh 时（以后的 ControlMaster 预热、§3.4 中二期回退路径里的后台 master），要按 Zed 的方式实现：
  - 每次连接一个 0700 临时目录，放 Unix socket 和一个 askpass 入口；
  - 入口是 TCode 的 AOT 可执行文件，以 `--askpass <socket>` 模式运行；
  - 对话框必须显示主机名和 ssh 给出的原始提示文本，并标明"由 ssh 请求"（防钓鱼）；
  - OpenSSH 8.4 之前还要求设置 `DISPLAY` 才会使用 askpass [推]；
  - Win32-OpenSSH 是否支持 `SSH_ASKPASS` 需要实测 [推]。

### 2.8 连接失败（退出码 255）的界面

**事实**：

- "ssh exits with the exit status of the remote command or with 255 if an error occurred" [实 OSSH ssh.1:1579-1582]；
- ssh 会把错误信息直接打印到终端里，例如：
  - `ssh: Could not resolve hostname …`
  - `Connection refused`
  - `Permission denied (publickey)`
  - `Host key verification failed.`
  - `client_loop: send disconnect: Broken pipe`

**判定** [定]（ssh profile 的会话退出时）：

| 条件 | 状态 | 显示 |
|---|---|---|
| 退出码 255，且会话时长 < 30 s 或从未出现过远端输出 | **连接失败** | 横幅："无法连接到 `prod-1`（ssh 退出码 255）。错误信息见上方输出。" |
| 退出码 255，会话已经运行了一段时间 | **连接中断** | 横幅："与 `prod-1` 的连接已断开。" |
| 其他非 0 退出码 | 远端 shell 的退出码 | 07 §6.3 的通用处理 |
| 0 | 正常退出 | 按 `onExit`（默认关闭标签） |

"远端输出"的判断：收到过 OSC 133 A，或者在 ssh 认证提示之后有超过 N 字节的输出——两者都是近似，所以横幅措辞保守，不下结论，只让用户看上方的输出。

- **按钮**：
  - "重新连接"（Enter 键）：在同一标签中启动新会话，旧内容保留，中间插入分隔线（07 §6.3）；
  - "详细模式重试"：同样的命令加 `-v`，用于诊断；
  - "编辑主机"：打开设置页对应的 profile。对于探测得到的主机，按钮名改为"打开 ~/.ssh/config"，用 TCode 的编辑器打开文件并定位到 `HostEntry.line`；
  - "复制命令"：复制等价的命令行（`LaunchPlan.DisplayCommand`）。
- **onExit 对 255 无效**：即使用户设置 `onExit: close`，也不自动关闭，因为用户需要看到错误信息 [定]。
- **常见错误的提示**（只匹配 ssh 自己打印的固定英文前缀，作为横幅中的附加说明，不替代原文）：

| 输出中包含 | 附加说明 |
|---|---|
| `Host key verification failed` / `REMOTE HOST IDENTIFICATION HAS CHANGED` | 主机密钥与 known_hosts 不一致。如果确认是服务器重装，可以运行 `ssh-keygen -R <host>`（TCode 不自动执行） |
| `Permission denied (publickey` | 服务器只接受公钥认证；检查 ssh-agent 是否运行、`IdentityFile` 是否正确 |
| `Could not resolve hostname` | 主机名无法解析；检查 `HostName` |
| `Too many authentication failures` | agent 中的密钥过多，超过了服务器的 `MaxAuthTries`；可以在该主机的配置中设置 `IdentitiesOnly yes`（research/06 §3） |

匹配失败时只显示通用横幅。远端的输出可以伪造这些字符串，但我们只是附加说明，不采取任何动作，所以没有风险。

### 2.9 与 ControlMaster 的关系

| 问题 | 结论 |
|---|---|
| TCode 会不会主动开启 ControlMaster | **不会**（一期）。用户在 config 中写了 `ControlMaster auto` + `ControlPath` + `ControlPersist`，系统 ssh 自然会复用：第二个标签连接同一主机时不需要再认证 |
| 第一个标签就是 master 时关闭它 | 没有 `ControlPersist` 时，master 会等所有共享会话结束后才退出，标签关闭会被延迟。我们的关闭确认（07 §6.1）把 `ssh` 视为"在运行"，会弹出确认 |
| `ControlPersist` 让 master 转入后台 | master 进程脱离我们的会话，继续存活。关闭标签时我们发 SIGHUP 给进程组，它已经不在进程组里了，所以不受影响。这符合用户在 config 中表达的意图 |
| Windows | Win32-OpenSSH 不支持 ControlMaster（依赖 Unix socket 与文件描述符传递）[实 research/06 §1 Zed 行] |
| pty host `keepAlive` 模式 | 会话保持，与 ControlMaster 无关 |
| 第二期的 Tmds.Ssh | 无法复用 OpenSSH 的 master：PROTOCOL.mux 是私有协议 [实 research/06 §3]。所以文件树会独立认证一次（§3.4、§3.6） |
| 状态显示（可选） | Unix 上可以运行 `ssh -O check <别名>`（只在 `ssh -G` 显示配置了 `controlpath` 时），在主机列表中显示"已连接"标记 [实 research/06 §1 Zed 的做法]。二期再做 |

### 2.10 第一期的安全规则

| 风险 | 规则 |
|---|---|
| 选项注入 | argv 直接传给 `execve`，不经过 shell；目标前面加 `--`；主机名、用户名不能以 `-` 开头，不能包含空白和控制字符（§2.4） |
| `ssh -G` 执行 `Match exec` | 只在用户的明确操作时运行，不批量运行（§2.3）。配置文件是用户自己的，执行其中的命令与用户运行 ssh 的效果相同 |
| `terminal.ssh.path` 指向任意程序 | settings.json 是用户自己的文件（0600）。**以后如果加入项目级配置，不允许项目级配置设置 `ssh.path`、`profiles.*.command`**（同 VS Code 的"受限模式"思路）[定] |
| 远端输出伪造 OSC | OSC 7 和 OSC 8 中的远端 `file://` 不当作本地路径（07 §6.4）；OSC 52 读剪贴板默认询问（01 §8）；伪造的 `633;E` 没有 nonce，不被信任（07 §5.1） |
| Agent 操作 SSH 终端 | 在 SSH 终端里执行命令的影响在远端，审批必须明确显示"远程主机 `prod-1`"。默认要求逐条审批，不适用"本地只读命令免审批"的规则 [定，细节由 09 定] |
| revive 快照中的远端输出 | 与本地会话相同：0600、行数上限、可以关闭（`persistence.*`，07 §7.3） |
| Agent forwarding | TCode 从不添加 `-A`；用户在 config 或 profile 的 `args` 中自己开启 |
| 凭据 | TCode 一期不接触任何密码或口令（都在 ssh 进程里）；不在命令行中传递密码；`sshpass` 一类的做法不支持 |

---

## 3. 第二期：Tmds.Ssh 用于 SFTP 与远程文件树

### 3.1 引入时机与 AOT 验证

**时机**：远程文件树或 Agent 远程只读工具（列目录、读文件）立项时引入，不早于 M7。一期只用系统 ssh，不引用 Tmds.Ssh。

**验证清单**（每次升级 Tmds.Ssh 版本都要跑一遍，写进 CI）：

1. 在 `samples/SshAotSmoke`（新，不随产品发布）中 `PublishAot`，6 个 RID 的发布都必须 **0 条 IL2xxx/IL3xxx 警告**。research/06 的本机实测为 0.26.0 osx-arm64：0 警告、7.2 MB [实 research/06 §2.1]；
2. 运行发布出来的二进制，连接 CI 中的 OpenSSH 容器（§5），完成：ed25519 公钥认证、经 agent 认证、ProxyJump、SFTP 列目录/读/写、`exec` 获取退出码；
3. 检查依赖：Tmds.Ssh 0.26.0 只依赖 `Microsoft.Extensions.Logging.Abstractions` [推，以 nuspec 为准]；新增依赖需要重新评估；
4. pin 精确版本（`PackageReference Version="[0.26.0]"`），升级时审查 diff，重点看 `SshConfigParser`、认证和主机密钥部分。

**放在哪里** [定]：

- 新项目 `src/TCode.Terminal.Ssh`，只依赖 Tmds.Ssh 和 BCL，不依赖其他 TCode 项目（D2）。内容：
  - `SshConfigDump`（`ssh -G` 的运行与解析，一期就需要，届时从 `TCode.Terminal.Shell` 移过来）；
  - `SshSettingsMapper`；
  - `SshConnectionPool`；
  - `SshChannelConnection : ITerminalConnection`（§3.6）。
- **运行在 pty host 中**：
  - 文件树（Desktop）和 Agent 的远程操作（AgentHost）都是 pty host 的客户端，同一个主机只需要认证一次；
  - Desktop 重启不会断开 SFTP 连接；
  - 代价是 04 的协议要增加 `remote/*` 一组消息（控制帧传元数据，数据帧传文件内容），并沿用同样的流控。
  - 列入 §8 待决问题。

### 3.2 `ssh -G` → `SshClientSettings` 的映射

**原则** [定]：

- **不让 Tmds.Ssh 解析任何配置文本**：用 `new SshClientSettings()` 直接构造，不用 `SshConfigSettings`。Tmds.Ssh 的解析器遇到 `ProxyCommand`、`SetEnv`、`Tag`、`AddressFamily`、`ControlPath` 会抛 `NotSupportedException`，遇到 `Match exec` 抛 `NotImplementedException` [实 research/06 §2.3；TMDS src/Tmds.Ssh/SshConfigParser.cs:738、917、961]。`ssh -G` 的输出又包含**所有**关键字（含默认值），喂给它肯定失败。
- `ssh -G` 已经完成了 `Host`/`Match`（包括 `Match exec`）、`Include`、`CanonicalizeHostname` 和大部分 `%` token 的展开，这正是我们需要 OpenSSH 代劳的部分。
- 每个关键字归入三类之一：**映射**（按下表转换）、**忽略**（对 SFTP 无影响）、**不兼容**（我们无法实现其语义）。出现任何"不兼容"的关键字且取值不是默认值时，整个主机判为不兼容，走 §3.4 的回退，**不尝试部分连接**。

| `ssh -G` 关键字 | 类别 | `SshClientSettings` | 说明 |
|---|---|---|---|
| `hostname` | 映射 | `HostName` | |
| `user` | 映射 | `UserName` | |
| `port` | 映射 | `Port` | |
| `connecttimeout` | 映射 | `ConnectTimeout` | `none` → Tmds.Ssh 的默认值 |
| `serveraliveinterval` / `serveralivecountmax` | 映射 | `KeepAliveInterval` / `KeepAliveCountMax` | 0 表示关闭 |
| `tcpkeepalive` | 映射 | `TcpKeepAlive` | |
| `compression` | 映射 | `EnableCompression` | |
| `batchmode` | 映射 | `NonInteractive` | |
| `userknownhostsfile` | 映射 | `UserKnownHostsFilePaths` | 空格分隔的多个路径，展开 `~`；`/dev/null` 表示不使用 |
| `globalknownhostsfile` | 映射 | `GlobalKnownHostsFilePaths` | 同上 |
| `hashknownhosts` | 映射 | `HashKnownHosts` | |
| `stricthostkeychecking` | 映射 | `HostAuthentication` 委托的行为（§3.5） | `yes`、`accept-new`、`no`/`off`、`ask` |
| `identityfile`（多个） | 映射 | `Credentials` 中逐个加 `PrivateKeyCredential(path, passphrasePrompt)` | 只加入存在且为 `openssh-key-v1` 格式的文件（Tmds.Ssh 只支持这种格式 [实 research/06 §2.1]）；PEM/PPK 格式记一条"已跳过"的诊断。`-sk` 类型的密钥见下 |
| `certificatefile`（多个） | 映射 | `CertificateCredential(cert, PrivateKeyCredential(对应的私钥))` | |
| `identitiesonly` | 映射 | 为 `yes` 时**不加入** `SshAgentCredentials` | 例外：`IdentityFile` 指向 `.pub` 而私钥不在本地（1Password 等 agent 托管密钥的典型写法）→ 需要"只用 agent 中的这把钥匙"，Tmds.Ssh 不支持 → **不兼容** |
| `identityagent` | 映射 / 不兼容 | 值为 `none` → 不加 agent；`SSH_AUTH_SOCK` 或与环境中的 `SSH_AUTH_SOCK` 相同 → `new SshAgentCredentials()`；**其他路径 → 不兼容** | `SshAgentCredentials(string address)` 是 internal [实 TMDS SshAgentCredentials.cs:9-25]，无法指定 agent 地址（上游 PR，§3.7） |
| `pubkeyauthentication` / `passwordauthentication` / `kbdinteractiveauthentication` / `gssapiauthentication` / `preferredauthentications` | 映射 | 决定 `Credentials` 中包含哪些凭据以及顺序 | 默认顺序 `gssapi-with-mic,hostbased,publickey,keyboard-interactive,password`；password → `PasswordCredential(prompt)` |
| `gssapidelegatecredentials` | 映射 | `KerberosCredential(delegateCredential: …)` | |
| `requiredrsasize` | 映射 | `MinimumRSAKeySize` | |
| `proxyjump` | 映射 | `Proxy = Proxy.Chain(SshProxy(…) × N)` | 每一跳 `[user@]host[:port]` 分别运行 `ssh -G -l user -p port host` 并**递归**映射为 `SshClientSettings`，再用 `new SshProxy(settings)` [实 TMDS SshProxy.cs:23；Proxy.cs:18]。任何一跳不兼容 → 整体不兼容 |
| `kexalgorithms` / `hostkeyalgorithms` / `ciphers` / `macs` / `pubkeyacceptedalgorithms` / `casignaturealgorithms` | 映射 | 对应的 `AlgorithmList`：OpenSSH 的最终列表与 Tmds.Ssh 支持的列表求交集，保持 OpenSSH 的顺序 | 交集为空 → 不兼容，原因写明"服务器或配置要求的算法 X 不被托管实现支持"。Tmds.Ssh 只实现安全算法，没有 ssh-rsa（SHA-1），加密只有 aes-gcm 和 chacha20-poly1305 [实 research/06 §2.1] |
| `forwardagent` | 忽略 | 强制 `ForwardAgent = Off` | SFTP 不需要转发 agent；安全考虑（§4） |
| `forwardx11` / `forwardx11trusted` | 忽略 | 强制 Off | |
| `sendenv` / `setenv` | 忽略（SFTP）；映射（managed 终端，§3.6） | `EnvironmentVariables` | managed 终端：`setenv` 的值，加上本地环境中匹配 `sendenv` 模式的变量 |
| `requesttty` / `remotecommand` / `sessiontype` / `stdinnull` / `forkafterauthentication` / `escapechar` / `loglevel` / `visualhostkey` / `fingerprinthash` / `obscurekeystroketiming` / `channeltimeout` / `localforward` / `remoteforward` / `dynamicforward` / `exitonforwardfailure` / `gatewayports` | 忽略 | — | 对 SFTP 没有影响 |
| `controlmaster` / `controlpath` / `controlpersist` | 忽略 | — | 无法复用（§2.9） |
| `updatehostkeys` / `verifyhostkeydns` / `checkhostip` | 忽略 | — | 不实现；只影响 known_hosts 的维护 |
| `canonicalizehostname` 等 | 忽略 | — | `ssh -G` 已经应用到 `hostname` 上 [推] |
| `addressfamily` | 忽略（`any`）；不兼容（`inet`/`inet6`） | — | Tmds.Ssh 不支持 [实 research/06 §2.3] |
| `proxycommand` | **不兼容**（不为 `none` 时） | — | `Proxy` 的抽象成员 `ConnectToProxyAndForward` 是 internal [实 TMDS Proxy.cs:9-11]，外部无法实现（上游 PR，§3.7） |
| `hostkeyalias` / `knownhostscommand` | **不兼容**（设置了时） | — | Tmds.Ssh 按实际主机名查 known_hosts，结果与 OpenSSH 不同，会造成"终端信任、文件树不信任"或者反过来 |
| `bindaddress` / `bindinterface` | **不兼容**（设置了时） | — | |
| `pkcs11provider` / `securitykeyprovider`（非默认） | **不兼容** | — | |
| `identityfile` 是 `id_*_sk` 类的 FIDO 密钥 | **不兼容** | — | Tmds.Ssh 不支持 sk 私钥文件，经 agent 签名也不行（源码中没有 sk 算法名）[实 research/06 §3]（上游 PR，§3.7） |
| 其他未知关键字 | 忽略并记录诊断 | — | OpenSSH 新版本增加的关键字 |

**解析注意事项**：

- `ssh -G` 输出的路径中可能还留着 `~` [推]，映射时统一展开；`%d`、`%u` 等 token 在 OpenSSH 10.x 中已经展开 [推]，映射器遇到残留的 `%` 时判为不兼容，不自己实现 token 规则。
- `ssh -G` 的结果按"主机别名 + 所有 config 文件的 mtime"缓存。
- 结果中的 `user` 一定有值（默认是本地用户名）。

### 3.3 认证缺口与回退

| 缺口 | 现状 | 处理 |
|---|---|---|
| keyboard-interactive（Duo、Google Authenticator、PAM 的 OTP） | Tmds.Ssh 没有实现（`kbdinteractivedevices` 的注释是 "not supported"）[实 research/06 §2.1] | ① 向上游提交 RFC 4256 的实现（协议很小，维护者活跃）；② 合入之前：服务器只提供 keyboard-interactive 时，认证失败 → 文件树显示"此主机需要交互式认证，托管连接暂不支持" |
| FIDO（`sk-ssh-ed25519@openssh.com`、`sk-ecdsa-sha2-nistp256@openssh.com`） | 私钥文件需要 libfido2；经 agent 签名需要库认识 sk 公钥算法，目前不认识 | ① 上游 PR：增加 sk 公钥算法名与签名格式，让 agent 中的 sk 密钥可用（触摸由 agent 驱动）；② 磁盘上的 sk 密钥文件**不支持**（不引入 libfido2 的 6 RID 原生依赖） |
| ProxyCommand | 无法实现（§3.2） | 上游 PR：公开一个基于 `Func<CancellationToken, ValueTask<Stream>>` 的 `StreamProxy`；TCode 用它运行 ProxyCommand 并把子进程的 stdin/stdout 作为 Stream |
| `IdentityAgent` 指定路径 | 无法指定 | 上游 PR：公开 `SshAgentCredentials(string address)` |
| PEM / PPK 私钥 | 不支持 | 提示用户 `ssh-keygen -p -f <key>` 转换为 OpenSSH 格式（只显示命令） |
| 以上任何一项不满足 | — | **回退** |

**回退方案** [定]：

| 平台 | 回退 |
|---|---|
| macOS / Linux | **系统 ssh + ControlMaster + SFTP over stdio**：<br>① 在一个**可见的** PTY 标签中运行 `ssh -M -S <TCODE_HOME/run/ssh/<hash>.sock> -o ControlPersist=10m -N <别名>`，用户在终端里完成 OTP 或 FIDO 触摸（与一期的体验相同）；<br>② 之后运行 `ssh -S <sock> -s <别名> sftp`（`-s` 表示请求 subsystem），把子进程的 stdin/stdout 当作 SFTP 通道；<br>③ 在这个 Stream 上运行一个**最小的 SFTP v3 客户端**：Tmds.Ssh 的 `SftpClient` 只能通过 `SshClient` 创建 [实 TMDS SftpClient.cs:117-147]。可以向上游提议"基于 Stream 的 SftpClient"，否则自己实现一个只读子集（opendir/readdir/stat/open/read/close，约 800 行）。<br>socket 目录 0700；TCode 退出时 `ssh -O exit` |
| Windows | 没有 ControlMaster。回退为"此主机的文件树不可用：原因 X。终端连接不受影响。" |

### 3.4 known_hosts 的界面

Tmds.Ssh 读写的是与 OpenSSH **相同的** known_hosts 文件（`UserKnownHostsFilePaths`），所以信任关系在系统 ssh 和 TCode 之间是共享的 [实 research/06 §2.1]：在终端里接受过的主机，文件树不再询问；反之亦然。

`HostAuthentication` 委托收到 `KnownHostResult`（`Trusted` / `Revoked` / `Changed` / `Unknown`）[实 TMDS KnownHostResult.cs] 和 `ConnectionInfo`（含主机密钥）：

| 结果 | `StrictHostKeyChecking` | 行为 |
|---|---|---|
| `Trusted` | 任意 | 通过 |
| `Unknown` | `ask`（默认） | 弹出对话框（见下）；对话框打开期间调用 `SuspendConnectTimeout()`，关闭后 `ResumeConnectTimeout()` [实 TMDS HostAuthentication.cs:47-54] |
| `Unknown` | `accept-new` | 不询问，接受并写入 known_hosts |
| `Unknown` | `yes` | 拒绝：显示"known_hosts 中没有该主机，并且配置了 StrictHostKeyChecking yes" |
| `Unknown` | `no` / `off` | 接受，**不写入** |
| `Changed` | 任意 | **拒绝，不提供覆盖按钮**（与 OpenSSH 相同）。显示记录的指纹和收到的指纹、known_hosts 的文件和行号，以及说明"如果服务器确实重装过，请运行 `ssh-keygen -R <host>`"。即使是 `no`/`off` 也拒绝：OpenSSH 在这种情况下会禁用密码认证并继续，我们选择更严格的做法 [定] |
| `Revoked` | 任意 | 拒绝，显示"该主机密钥已被吊销（@revoked）" |
| 任何结果，且 `IsNonInteractive`（自动重连、rekey） | — | 不弹窗；`Unknown` 视为拒绝 [实 TMDS HostAuthentication.cs:33-41 的要求] |

**首次连接对话框**：

```
首次连接 prod-1（10.0.3.17:22）
主机密钥类型：ssh-ed25519
SHA256 指纹：SHA256:Xq2…9kE              [复制]
（可选：随机艺术图，等宽字体）
将写入：~/.ssh/known_hosts（哈希形式：是）

请通过可信渠道核对指纹后再继续。
   [取消]   [仅本次连接]   [信任并保存]
```

- 指纹格式与 `ssh-keygen -lf` 一致（SHA256 + base64，去掉末尾的 `=`）；
- "信任并保存"用 `UpdateKnownHostsFileAfterAuthentication = true` 写入：只有认证也成功之后才写入，避免把一个认证失败的服务器的密钥留在文件里 [实 TMDS SshClientSettings.cs:420]；
- 对话框只能由 Desktop 显示：host 通过 IPC 请求 `ssh/hostKeyPrompt`。没有 UI 客户端时（例如只有 AgentHost 在连接）按"非交互"处理并拒绝。

### 3.5 远程终端在第二期是否也换成 Tmds.Ssh 的通道

**建议：不换，交互终端继续用系统 ssh；`managed` 模式作为按主机开启的选项** [定]。

| 维度 | 系统 ssh（一期方案） | Tmds.Ssh 通道 |
|---|---|---|
| 配置一致性 | 100%（ProxyCommand、Match exec、IdentityAgent、FIDO、keyboard-interactive、`~.` 转义、ControlMaster、`LocalForward`） | 子集，不兼容的主机连不上 |
| 认证次数 | 终端与文件树各认证一次（有 agent 或密钥时用户感觉不到）；Unix 上可以通过 ControlMaster 只认证一次 | 终端与文件树共用一条连接 |
| 退出信息 | 远端退出码；255 有歧义 | 精确的 `exit-status` / `exit-signal`（`GetExitStatusAsync`）[实 TMDS RemoteProcess.cs:249-266、592] |
| 主机密钥与密码界面 | 在终端里（文本） | 结构化对话框 |
| 断线检测 | ssh 自己的 `ServerAlive*` | 可控的 KeepAlive；可以提示"已断开，重新连接" |
| 端口转发 | 用户的 config 与 `-L` 照常工作 | 要自己实现 |
| 实现成本 | 0（已经完成） | `SshChannelConnection` + 提示 UI + 测试 |

理由：

1. 第二期的价值在文件树。终端已经"与系统一致"，替换它只会带来回归；
2. 用户最常见的 ssh 配置（`ProxyCommand` 走跳板、1Password 的 `IdentityAgent`、公司的 OTP）恰恰是 Tmds.Ssh 不支持的部分；
3. **managed 模式的真正用处**：Windows 上，只能用密码登录的主机会在终端和文件树中各要求输入一次密码（Windows 没有 ControlMaster）。对这些主机，允许用户设置 `"ssh:legacy-box": { "transport": "managed" }`，让终端也走文件树的那条连接。只有在该主机映射结果为"兼容"时，这个选项才可用。

### 3.6 `ITerminalConnection` 的 SSH 实现草案

运行在 pty host 中，替换本地 PTY 作为会话的连接；镜像核心、输出日志、快照、命令跟踪都不受影响（01 §3）。

```csharp
namespace TCode.Terminal.Ssh;

/// managed 模式的远程终端：一条已认证的 SshClient 上的一个 session 通道（pty-req + shell）。
public sealed class SshChannelConnection : ITerminalConnection
{
    private readonly SshConnectionPool.Lease _lease;     // 引用计数：与文件树共用 SshClient
    private readonly RemoteProcess _process;
    private readonly Pipe _output = new(new PipeOptions(pauseWriterThreshold: 256 * 1024, resumeWriterThreshold: 64 * 1024));
    private readonly TaskCompletionSource<TerminalExit> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Kind => "ssh";
    public ConnectionCapabilities Capabilities => ConnectionCapabilities.Resize | ConnectionCapabilities.PixelSize | ConnectionCapabilities.Signals;
    // 没有 ForegroundProcess、WorkingDirectory：远端进程不可见，cwd 只能来自 OSC 7（而且是远端路径）
    public PipeReader Output => _output.Reader;
    public Task<TerminalExit> Exited => _exited.Task;

    public static async ValueTask<SshChannelConnection> OpenAsync(
        SshConnectionPool pool, SshHostKey key, SshTerminalLaunch launch, CancellationToken ct)
    {
        var lease = await pool.AcquireAsync(key, ct);                 // 必要时建立连接（含主机密钥 / 口令提示）
        var options = new ExecuteOptions
        {
            AllocateTerminal = true,
            TerminalType = launch.Term,                               // "xterm-256color"
            TerminalSize = ToTmds(launch.InitialSize),
            // TerminalSettings 使用 Tmds.Ssh 的默认值：VERASE=0x7F、VINTR=^C、IUTF8 等 [实 research/06 §2.3]
        };
        foreach (var (k, v) in launch.Environment) options.EnvironmentVariables[k] = v;   // SetEnv + 匹配 SendEnv 的变量 + COLORTERM 等
        var process = launch.RemoteCommand is { } cmd
            ? await lease.Client.ExecuteAsync(cmd, options, ct)
            : await lease.Client.ExecuteShellAsync(options, ct);
        var c = new SshChannelConnection(lease, process);
        c.StartPump();
        return c;
    }

    // TerminalSize 中的像素是**单个 cell** 的尺寸（01 §6.2）；pty-req / window-change 要的是整个区域的像素
    private static Tmds.Ssh.TerminalSize ToTmds(TerminalSize s) =>
        new(s.Columns, s.Rows, s.Columns * s.CellWidth, s.Rows * s.CellHeight);

    private async void StartPump()  // 实现时改为显式的 Task 并记录异常
    {
        try
        {
            while (true)
            {
                var mem = _output.Writer.GetMemory(16 * 1024);
                var (isError, n) = await _process.ReadAsync(mem, mem);   // 有 PTY 时 stderr 也进入同一个 tty，极少出现扩展数据
                if (n == 0) break;                                       // EOF
                _output.Writer.Advance(n);
                var flush = await _output.Writer.FlushAsync();           // 背压：消费者不读，这里就停下，
                if (flush.IsCompleted) break;                            // Tmds.Ssh 在读取时才补通道窗口 → 远端被压住 [实 research/06 §2.3]
            }
            var status = await _process.GetExitStatusAsync();
            _exited.TrySetResult(new TerminalExit(
                ExitCode: status.ExitSignal is null ? status.ExitCode : null,
                Signal: status.ExitSignal));
        }
        catch (SshConnectionException ex)                               // 断线：退出码未知
        {
            _exited.TrySetResult(new TerminalExit(null, null, Message: ex.Message));
        }
        finally { await _output.Writer.CompleteAsync(); }
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken ct = default) => _process.WriteAsync(input, ct);

    public ValueTask ResizeAsync(TerminalSize size, CancellationToken ct = default)
    {
        _process.SetTerminalSize(ToTmds(size));                          // window-change，无应答
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> SignalAsync(TerminalSignal signal, CancellationToken ct = default)
        => ValueTask.FromResult(_process.SendSignal(signal switch      // RFC 4254 §6.9：名字不带 "SIG"
        {
            TerminalSignal.Hup => "HUP", TerminalSignal.Int => "INT", TerminalSignal.Term => "TERM", TerminalSignal.Kill => "KILL",
            _ => throw new ArgumentOutOfRangeException(nameof(signal)),
        }));
        // 注意：交互中的 Ctrl+C 仍然以字节 0x03 写入，由远端 tty 产生信号；signal 请求只用于"强制结束"

    public ValueTask<ForegroundInfo?> GetForegroundAsync(CancellationToken ct = default) => ValueTask.FromResult<ForegroundInfo?>(null);
    public ValueTask<string?> GetWorkingDirectoryAsync(CancellationToken ct = default) => ValueTask.FromResult<string?>(null);

    public async ValueTask DisposeAsync()
    {
        _process.Dispose();                                              // 关闭通道
        await _lease.DisposeAsync();                                     // 引用计数归零后，连接池按空闲超时关闭 SshClient
    }
}
```

- **`SshConnectionPool`**：
  - 键 = 映射后设置的规范化指纹（hostname、port、user、proxy 链、凭据来源），而不是别名，因此两个指向同一目标的别名共享连接；
  - 引用计数；无人使用后保持 5 分钟再关闭；
  - `AutoReconnect = false`：断线时由上层决定是否重连。终端的会话不能透明重连（SSH 协议没有会话恢复 [实 research/06 §4]）。
- **关闭确认**（07 §6.1）：没有 `ForegroundProcess` 能力，除非远端有 shell 集成并处于提示符状态，否则视为"在运行"。
- **`TerminalExit`** 区分退出码、信号和断线（`Message`），07 §6.3 和 §2.8 的界面据此显示。

---

## 4. 安全

| 主题 | 规则 |
|---|---|
| 凭据 | 二期的密码、密钥口令只保存在内存中，用完后尽量清零（`char[]`、`CryptographicOperations.ZeroMemory`）；**从不写盘**，也不放进 settings.json；对话框不提供"记住密码"（以后如果要做，使用系统钥匙串，单独评审） |
| 主机密钥 | 只用 OpenSSH 的 known_hosts 文件，不建立 TCode 自己的信任库；`Changed` 和 `Revoked` 一律拒绝（§3.4） |
| 提示的来源 | 所有凭据对话框都显示主机名、端口和"由 TCode 托管连接请求"，与 Agent 的审批对话框在视觉上区分开，防止远端或 Agent 诱导用户在错误的对话框中输入密码 |
| agent forwarding | 托管连接一律关闭 agent 和 X11 转发（§3.2） |
| 远程路径 | SFTP 返回的文件名是不可信输入：显示时过滤控制字符和换行；`..`、绝对路径和符号链接按远端语义处理，不拼接成本地路径；下载到本地时处理 Windows 的保留名（`CON`、`NUL` 等）和非法字符（`:`、`*`），覆盖前确认 |
| Agent 的远程操作 | Agent 读取远程文件、在远程终端中执行命令，都需要审批，并在审批界面中明确显示远程主机（09）；远程写入默认不提供 |
| 子进程环境 | `ssh -G`、`ssh -V`、回退方案中的 master 都使用 07 §4 的环境（剔除 `TCODE_*`，防止 pty host 的令牌泄漏给 ssh 调用的 `ProxyCommand`、`Match exec`） |
| 控制 socket | 回退方案的 ControlPath 放在 `$TCODE_HOME/run/ssh/`（0700），文件名用哈希，避免超过 Unix socket 路径长度上限（104 字节）[推] |
| 日志 | 不记录 `ssh -G` 的完整输出（可能包含内部主机名和用户名）；只在用户主动打开诊断时显示 |
| 项目级配置（以后） | 不允许项目级配置定义 ssh profile、`ssh.path`、`ssh.configFile`（§2.10） |

---

## 5. settings 中的 `terminal.ssh` 节

| 字段 | 类型 | 默认 | 热重载 | 说明 |
|---|---|---|---|---|
| `ssh.enabled` | bool | true | 立即（刷新菜单） | false 时不列出主机，自定义的 `type: ssh` profile 仍然可用 |
| `ssh.path` | string | 自动探测（§2.1） | 新终端 | |
| `ssh.configFile` | string | 不设置（使用默认文件） | 立即（重新扫描） | 对应 `ssh -F` |
| `ssh.sendEnv` | bool | true | 新终端 | §2.5 |
| `ssh.hiddenHosts` | string[] | git 托管站点列表（§2.2） | 立即 | 写出时替换默认列表；支持 `*` 通配 |
| `ssh.shellIntegration` | `off` | `off` | — | 预留给 §2.6 的方案 A |
| `ssh.fileTree` | bool | true | 立即 | 二期：是否为 SSH 主机提供远程文件树 |

设置页的"SSH"子页：

- ssh 可执行文件（探测结果、版本、"重新检测"）；
- 配置文件位置；
- 主机列表：显示来源文件和行号；点击后显示 `ssh -G` 的解析结果（按需运行）；二期还显示"文件树：兼容 / 不兼容（原因）"；
- 隐藏主机；
- SendEnv 开关。

---

## 6. 测试计划

### 6.1 单元测试（三平台）

| 组 | 用例 |
|---|---|
| `SshConfigScanner` | `Host a b c`（多个别名）；`Host *`、`Host *.corp`、`Host !bad` 被排除；`Host=foo` 与引号；`Match` 块内的 HostName 不归属任何别名；`Include` 的相对路径（用户配置相对 `~/.ssh`，系统配置相对 `/etc/ssh`）、glob 的字典序、环境变量与 `~`；块内的 Include；递归深度 17 时停止；Include 循环；BOM、CRLF、制表符；别名大小写去重；`hiddenHosts` 过滤；文件不存在 |
| `ssh -G` 输出解析 | 用不同版本 OpenSSH 的真实输出做 fixture：7.4（RHEL 7）、8.0（RHEL 8）、9.6（Ubuntu 24.04）、10.3（macOS 26）、Win32-OpenSSH 9.5；多值关键字；`none`；包含空格的路径 |
| 命令行构造 | 一期 `BuildSshArgs` 的快照：各字段组合、`--` 的位置、`remoteCommand` 与 `-t`、`-F`、`sendEnv` 开关；非法主机名、用户名被拒绝 |
| 255 的界面判定 | 时长与输出量的组合；常见错误前缀的提示匹配 |
| 二期映射 | 每个关键字的映射；不兼容关键字的判定（包括"设置为默认值时不算不兼容"）；`identitiesonly` + `.pub` 的特例；ProxyJump 递归（含某一跳不兼容）；算法交集为空；输出快照（`SshClientSettings` 的规范化 JSON） |
| known_hosts 策略 | `KnownHostResult` × `StrictHostKeyChecking` × `IsNonInteractive` 的完整矩阵（使用假的对话框服务） |

### 6.2 集成测试：CI 中的 OpenSSH 容器

GitHub Actions 的 Linux runner 用 service container 运行。镜像是自己维护的 Dockerfile（`tests/ssh-server/Dockerfile`，基于 `debian:stable-slim` + `openssh-server`，因为需要 PAM 来测试 keyboard-interactive），通过 docker compose 启动两个实例：

| 实例 | 配置 |
|---|---|
| `target` | 用户：`keyuser`（ed25519 公钥）、`pwuser`（密码）、`otpuser`（PAM keyboard-interactive，用固定应答模拟 OTP）、`certuser`（由测试 CA 签发的用户证书，`TrustedUserCAKeys`）；`AcceptEnv COLORTERM TERM_PROGRAM TERM_PROGRAM_VERSION`；第二个监听端口使用一份**不含** AcceptEnv 的配置；固定的主机密钥（另有一份"更换后"的主机密钥，用于测试 Changed） |
| `bastion` | 只允许转发到 `target`，用于测试 ProxyJump |

测试代码生成一个临时的 `~/.ssh/config`（`HOME` 指向临时目录）和 known_hosts，并启动 `ssh-agent` 加载测试密钥。

**一期 E2E**（通过真实的 `LaunchBuilder` + 本地 PTY 运行系统 ssh，只断言字节流，不依赖 UI）：

| 用例 | 断言 |
|---|---|
| 连接 `target`（agent 认证） | 远端 `echo $TERM` 输出 `xterm-256color`；`echo $COLORTERM` 输出 `truecolor`（AcceptEnv 端口）或空（另一个端口），并且后者**没有**任何报错 |
| 用户 config 中有 `SetEnv FOO=bar` | 远端 `echo $FOO` 为 `bar`（证明我们没有用 `-o SetEnv` 覆盖它） |
| `exit 7` | 本地会话的退出码为 7 |
| 端口关闭 | 退出码 255，状态为"连接失败" |
| 未知主机密钥 | 终端中出现 `Are you sure you want to continue connecting`；写入 `yes` 后连接成功 |
| 主机密钥已更换 | 255，并命中 `REMOTE HOST IDENTIFICATION HAS CHANGED` 的提示 |
| ProxyJump 经过 `bastion` | 连接成功 |
| `pwuser` 的密码提示 | 终端中出现 `password:`；写入密码后成功（证明没有设置 askpass） |
| ControlMaster | 配置 `ControlMaster auto` 后打开两个会话，第二个会话不经认证就连上（`-v` 输出中有 `mux_client_request_session`） |
| `ssh -G` | 带 `Match exec "test -n \"$X\""` 的配置，在两种环境下得到不同的 `hostname` |

**二期 E2E**：

- 映射 + 连接：`keyuser`（文件密钥与 agent 两种）、`certuser`、`pwuser`（假的密码对话框）、ProxyJump；
- `otpuser`：上游 PR 合入前，断言得到"需要交互式认证"的诊断；合入后断言成功；
- SFTP 列目录、读文件、写文件、大文件的流控（读取速度受消费方控制，内存不增长）；
- known_hosts：Unknown → 假对话框选择"信任并保存" → 文件中多一行哈希记录 → 第二次连接为 Trusted；用更换后的主机密钥测试 Changed → 拒绝；
- managed 终端：resize 后远端 `stty size` 正确；`exit 3` → 3；`kill -9 $$` → Signal = `KILL`；在容器内断开网络 → `TerminalExit.Message` 不为空；
- **AOT**：`SshAotSmoke` 的发布物在同一个容器上跑一遍上述连接用例。

**macOS 与 Windows runner**：不能运行 Linux 容器。只跑单元测试，以及"ssh 探测 + 命令行构造 + 连接 `localhost` 上不存在的端口 → 255"这类不需要服务端的用例。Windows 上可以选择在 nightly 中安装 Win32-OpenSSH Server（`Add-WindowsCapability OpenSSH.Server`），对 `localhost` 跑一期的 E2E（验证 ConPTY 下的 ssh.exe 行为），这一项不阻塞 PR。

---

## 7. 风险

| 风险 | 影响 | 应对 |
|---|---|---|
| Windows 用户没有安装 OpenSSH 客户端（Win10 1803+ 默认安装，但可能被移除） | 没有 SSH 功能 | §2.1 的提示；不内置托管实现作为一期的兜底 |
| 用户只有 Git for Windows 的 ssh | agent 和路径行为与 Win32-OpenSSH 不同 | 排在最后、明确标注；二期的文件树按 Win32 的约定（命名管道 agent）工作 |
| 列表中混入无法登录的主机（git 托管站点、只做跳板的主机） | 菜单杂乱 | 默认隐藏列表 + 用户可调；以后可以按"最近使用"折叠 |
| `ssh -G` 执行 `Match exec` 带来的延迟和副作用 | 打开详情变慢 | 只按需运行，带超时，结果缓存 |
| 255 的歧义（远端命令自己返回 255） | 误报"连接失败" | 横幅措辞保守，以终端里的原始输出为准 |
| Tmds.Ssh 是单人维护的库（research/06：244 星） | 上游 PR 合入慢或者不合入；项目停滞 | pin 版本；上游 PR 不合入时在 TCode 内维护一个小的 fork（MIT）；文件树功能本身有回退方案，可以降级 |
| Tmds.Ssh 不兼容的主机比例高（企业环境中 ProxyCommand、OTP 很常见） | 文件树对这些用户不可用 | 一期不受影响；二期提供"不兼容原因"，并优先推进 ProxyCommand 和 keyboard-interactive 的上游 PR |
| 两次认证（Windows、没有 agent 的密码主机） | 体验差 | `managed` 模式（§3.5）；鼓励使用 agent |
| 远程 shell 集成的方案 A 需要在输出路径上识别握手 | 安全面增加（远端可以伪造请求） | 一次性口令 + 只在 TCode 主动发起的引导会话中识别；v2 单独评审 |
| `SendEnv` 导致个别网络设备断开连接 | 连接失败 | `ssh.sendEnv: false`，或按 profile 关闭 |

---

## 8. 与 01 的差异和待决问题

| # | 问题 | 本文的处理 | 需要谁决定 |
|---|---|---|---|
| 1 | 01 D4 写的是"系统 `ssh -t`" | 默认**不加** `-t`，只在有远程命令时加（§2.4），以尊重用户的 `RequestTTY` 配置 | 01 维护者确认措辞 |
| 2 | 01 §6.2 的 `ConnectionCapabilities` 需要能表达"没有前台进程信息"（SSH） | 沿用 01 的能力位，SSH 不声明 `ForegroundProcess`、`WorkingDirectory`；07 §6.1 据此处理 | — |
| 3 | SFTP 连接池放在 pty host 中，需要 04 的协议增加 `remote/*` 消息和 `ssh/hostKeyPrompt`、`ssh/credentialPrompt` 这类反向请求 | §3.1 建议放在 pty host | 04 |
| 4 | 远程 shell 集成方案 A 需要镜像核心或会话 actor 识别私有握手序列 | v2 再定 | 02 / 04 |
| 5 | Tmds.Ssh 的上游 PR：公开的 Stream 代理、`SshAgentCredentials(address)`、keyboard-interactive、sk 算法、基于 Stream 的 SftpClient | 二期开始前提交 | 用户确认是否向上游贡献 |
| 6 | 一期是否需要 askpass | 不需要（§2.7） | — |

## 9. 来源

**源码**：

- OpenSSH（OSSH `6545e6a`）：`readconf.c:1936-1989`（SendEnv 追加 / SetEnv 第一组生效）、`readconf.c:2629-2641`（Include 深度 16）、`clientloop.c:2706`（env 请求 want_reply=0）、`ssh.1:276-283`（`-G`）、`ssh.1:1579-1582`（退出码 255）、`ssh_config.5:288-310`（Include）、`ssh_config.5:1899-1992`（SendEnv、SetEnv）
- Tmds.Ssh（TMDS `e186178`）：`src/Tmds.Ssh/SshClientSettings.cs`（:194-743 公开属性）、`SshConfigParser.cs`（:560-740 关键字处理与异常）、`Proxy.cs:9-40`（internal 抽象成员）、`SshProxy.cs:11-50`、`SshAgentCredentials.cs:9-25`（internal 构造函数）、`HostAuthentication.cs:1-63`、`KnownHostResult.cs`、`RemoteProcess.cs:249-266、365-405、557-609`、`ExecuteOptions.cs:71-100`、`TerminalSize.cs:14-26`（像素为整个区域）、`SftpClient.cs:117-147`
- Ghostty（GH `b3dc3d7`）：`src/cli/ssh.zig:225-335`（SetEnv/SendEnv 与 terminfo 缓存）
- Zed（ZED `6b218b8`）：`crates/askpass/src/askpass.rs:34-60、225-405`
- Windows Terminal（WT `44b440b`）：`src/cascadia/TerminalSettingsModel/SshHostGenerator.cpp:15-165`

**文档**：research/06 的全部来源（RFC 4254、RFC 8160、RFC 9987、sshd_config(5)、Debian `debian-config.patch`、kitty ssh kitten、Tmds.Ssh、SSH.NET、DevTunnels 等）；research/03 §7。
