# 06 · SSH 接入、传输抽象与进程架构

> 调研日期：2026-10-16。范围：终端组件的「连接」一侧——SSH 接入方式、.NET 可用的 SSH 实现、「与系统一致」的需求清单、SSH 会话细节、远端 terminfo、传输抽象接口，以及 PTY/SSH 会话放在哪个进程。
> 标注约定：**[已核实]** = 读过源码/官方文档或本机实测；**[推断]** = 根据已核实事实推出、未直接验证。来源编号见文末。

## 0. 结论摘要

1. **主流做法是调用系统 `ssh`**：VS Code Remote-SSH、Zed、Warp、Ghostty、Windows Terminal、JetBrains（"SSH connections ... run via OpenSSH"）都直接运行本机 OpenSSH；只有 WezTerm（libssh/libssh2）、Tabby（russh）、Termius、WindTerm 自带 SSH 实现。原因只有一个：`~/.ssh/config`、ssh-agent、FIDO2、ControlMaster、GSSAPI、`Match exec` 这些「与系统一致」的行为，只有 OpenSSH 自己能做到 100%。[已核实，§1]
2. **终端里的 SSH = 本地 PTY 里跑 `ssh -t`**，零额外代码就「与系统一致」。托管库的价值在**非交互**能力：SFTP 文件树、端口转发、远端执行（给 Agent 工具）、精确的 exit-status/exit-signal。
3. **.NET 托管库现状**（2026-10，均实测 AOT 发布）：
   - **SSH.NET 2026.0.0**：`IsAotCompatible`，AOT 发布 **0 警告**，能跑；但**没有 ssh-agent、没有 ProxyJump、不读 ssh_config/known_hosts**，`ShellStream` 是同步锁 + 事件模型，拿不到 shell 会话的退出码。
   - **Tmds.Ssh 0.26.0**（MIT，活跃，2026-10-10 发版）：`IsAotCompatible`，AOT 发布 **0 警告**；原生 async、读 ssh_config（`Include`/`Match host/user`）、known_hosts、ssh-agent（Unix socket + Windows 命名管道）、ProxyJump、OpenSSH 证书、GSSAPI、agent/X11 转发、SFTP。缺：keyboard-interactive、FIDO 私钥文件、`ProxyCommand`、`Match exec`、`ControlMaster`、`SetEnv`——且**遇到不支持的关键字直接抛 `NotSupportedException`**（实测）。
   - **Microsoft.DevTunnels.Ssh 3.12.42**：AOT 发布 **11 条 IL 警告**；macOS 上 ECDH 与 AES-GCM 不可用（JIT 下也不可用）；无 ed25519/curve25519/证书；README 明确「不支持登录用户账号、连接 shell、SFTP」——**不适合**。
   - libssh（LGPL-2.1，0.12.x 有 FIDO2、ProxyJump、完整 `Match exec`）、libssh2（BSD，1.11.1 停在 2024-10）、russh（Apache-2.0，Rust，需自建 C ABI）都要 FFI + 6 个 RID 的原生构建，成本高。
4. **传输抽象**：终端核心只需要「字节进、字节出、resize(cols,rows,px)、退出(code|signal)、可选的 cwd/前台进程」。本地 PTY、`ssh -t`、Tmds.Ssh channel、WSL、`docker exec -it`、串口、录制回放都实现同一个 `ITerminalConnection`（§6 给出草案）。
5. **现有 stdio JSON-RPC 不适合承载终端字节流**（实测 §7.3）：16 KB 块时 JSON+base64 约 940 MB/s、线上膨胀 1.33×、**每 1 MB 负载分配约 9 MB**；若当字符串传 ESC 密集输出，膨胀 1.5×、吞吐跌到约 150–220 MB/s。而裸二进制帧可达 2.4–2.9 GB/s。终端数据需要**独立的二进制通道**（或同一管道上的二进制帧），JSON-RPC 只用于控制面。
6. **进程架构推荐**：第一阶段 **PTY 放在 UI 进程**（Zed 模式，最简单、最低延迟），但接口按「可跨进程」设计；当出现「UI 重载后保持会话 / Agent 工具在用户可见终端里执行 / 跨窗口共享」三个需求中的任意一个时，迁移到**独立 pty host 进程**（VS Code ptyHost 模式：持久会话、headless 终端做 serialize 回放、流控 ack）。**不放进 AgentHost**：AgentHost 崩溃重启是常态设计，会连带杀掉用户的 shell。

---

## 1. 其他客户端如何接入 SSH

| 客户端 | 方式 | 关键细节 | 得 | 失 | 来源 |
|---|---|---|---|---|---|
| **VS Code Remote-SSH** | 系统 `ssh`（PATH，或 `remote.SSH.path`；Windows 优先 Win32-OpenSSH，PuTTY 不支持） | 推荐用户自己配 `ControlMaster auto`；`remote.SSH.showLoginTerminal` 在终端里显示密码/2FA 提示；远端装 VS Code Server，终端由远端 ptyHost 提供（`remoteterminal` IPC 通道） | 100% 兼容 ssh_config/agent/2FA | 依赖本机 OpenSSH；交互认证要靠终端或 askpass | [VSC-TS] [已核实]；`vscode/src/vs/workbench/contrib/terminal/common/remote/remoteTerminalChannel.ts:30` @4765bd3 |
| **Zed remote** | 系统 `ssh` + **ControlMaster** | 先 `ssh -G` 解析用户 `ControlPath`，`ssh -O check` 探活复用；否则自起 `ssh -N -o ControlMaster=yes -o ControlPersist=no -o ControlPath=…` 作为 master，`SSH_ASKPASS` + `SSH_ASKPASS_REQUIRE=force` 把密码提示接进 UI；终端 = `ssh -t host "cd … && exec env K=V $SHELL -l"`；Windows 不支持 ControlMaster，改用「打印魔术串」探测连接成功 | 一次认证、多通道；兼容全部 ssh_config | Windows 上无复用；依赖远端 POSIX shell 引号规则 | `zed/crates/remote/src/transport/ssh.rs:298-400,720-800,2028-2160` @6b218b8 [已核实] |
| **Warp** | 包装系统 `/usr/bin/ssh` | 新方案：征得同意后在远端装 `~/.warp/remote-server`，通过现有 SSH 连接通信；旧方案（legacy wrapper）`warp_ssh_helper` 函数 bootstrap 远端 bash/zsh；tmux 方案已移除 | 用户 ssh 行为不变 | 只认 bash/zsh；alias 或脚本里的 ssh 检测不到；`RemoteCommand` 会让 wrapper 失败 | [WARP-SSH] [WARP-LEGACY] [已核实] |
| **Ghostty** | 系统 `ssh` 的 shell 函数包装 `ghostty +ssh` | `ssh-env`：`-o SetEnv=TERM=xterm-256color` + `SendEnv COLORTERM TERM_PROGRAM TERM_PROGRAM_VERSION`；`ssh-terminfo`：首次连接经临时 ControlMaster 运行 `tic -x -` 把内嵌 terminfo 装到远端 `~/.terminfo`，成功后本地缓存，之后用 `TERM=xterm-ghostty` | 零协议代码，兼容一切 | 依赖远端有 `tic`；SendEnv 要远端 AcceptEnv | `ghostty/src/cli/ssh.zig:102-160,225-330,480-560`；`src/config/Config.zig:2960-2985`；`src/shell-integration/bash/ghostty.bash:119-128` @b3dc3d7 [已核实] |
| **WezTerm** | 内置库：**libssh（默认）/ libssh2**（`ssh_backend`）；`wezterm ssh`；SSH domain（远端需装 wezterm mux server，会话可持久） | 自己解析 ssh_config：支持 Host 通配、`Include`、`Match host/localuser`，**`Match exec` 不支持**，**ProxyJump 不支持**（只有 ProxyCommand）；libssh2 后端不支持 agent 转发 | 跨平台一致、多 tab 复用一个会话 | 与 OpenSSH 行为差异需逐条补；断网即断（ad-hoc 模式） | `wezterm/docs/ssh.md`；`wezterm-ssh/src/config.rs:94-140,700-710`；`wezterm-ssh/src/channelwrap.rs:149-160`；`config/src/ssh.rs:9-16` @e2a98e1 [已核实] |
| **Tabby** | 内置库 **russh**（npm `russh@0.1.38`，Rust 绑定） | 自己的 profile；可从 `~/.ssh/config` 导入（递归 Include）；agent：自动探测 `SSH_AUTH_SOCK` / Windows `\\.\pipe\openssh-ssh-agent` / Pageant；跳板机、ProxyCommand 是 profile 字段 | 统一 UI、SFTP、X11 | 与 OpenSSH 配置语义不完全一致（导入而非实时解析） | `tabby/app/package.json:35`；`tabby-ssh/src/session/ssh.ts:21-24,166-205,301-317` @467ce8e；[TABBY-IMP] [已核实] |
| **Termius** | 自研/内置（闭源） | Host Chaining（跳板链）、FIDO2 密钥（6.1.1 起）、agent 转发 | 跨端同步 | 不读 ssh_config 实时语义；具体库不公开 | [TERMIUS] [已核实部分；库未知] |
| **WindTerm** | 内置 SSH v2 实现 | README 声称支持 ControlMaster、ProxyCommand/ProxyJump、agent、agent 转发、keyboard-interactive、gssapi-with-mic、X11、SFTP | 功能全 | 闭源核心，无法核对实现质量 | [WINDTERM] [已核实声明] |
| **Windows Terminal** | 直接运行 `ssh.exe` 的 profile | `SshHostGenerator` 扫描 `%ProgramData%\ssh\ssh_config` 与 `%UserProfile%\.ssh\config` 的 `Host`，为每个主机生成 `ssh.exe <host>` profile | 零协议代码 | 仅 Host 名列表（不解析 Include/Match） | `winterminal/src/cascadia/TerminalSettingsModel/SshHostGenerator.cpp:18-35,86-161` @44b440b [已核实] |
| **JetBrains Gateway / IDE** | OpenSSH（"SSH connections in IntelliJ IDEA run via OpenSSH"） | 认证方式含「OpenSSH config and authentication agent」；列出 21 个支持的 OpenSSH 指令（无 ProxyJump、Include、Match） | — | 只支持指令子集 | [JB-SSH] [已核实] |

**规律**：凡是声称「行为与系统一致」的（VS Code、Zed、Ghostty、Warp、WT），都把 SSH 交给 OpenSSH；内置库的产品都在文档里列「支持的 ssh_config 指令清单」，即都有缺口。

---

## 2. .NET 可用的 SSH 实现

### 2.1 总表

| 实现 | 版本 / 日期 | 许可 | AOT | 关键能力 | 关键缺口 | 来源 |
|---|---|---|---|---|---|---|
| **SSH.NET** | 2026.0.0（2026-08-09）；develop @7d54d88（2026-10-14） | MIT | `IsAotCompatible`（net8+）；CI 跑 AOT 测试 app `/warnaserror`；**本机 AOT 发布 0 警告，10.7 MB，运行正常** | KEX 含 mlkem768x25519、sntrup761、curve25519；ed25519/ECDSA/rsa-sha2；OpenSSH 证书（`PrivateKeyFile(…, certificateFileName)`）；keyboard-interactive（`AuthenticationPrompt` 事件）；Local/Remote/Dynamic 转发；SFTP、SCP；SOCKS4/5/HTTP 代理；`KeepAliveInterval` | **无 ssh-agent**（2017 年 issue #184 仍 open，需第三方 SshNet.Agent）；**无 ProxyJump**（#481/#530 open）；**不读 ssh_config / known_hosts**（仅 `HostKeyReceived` 事件）；无 sk-* FIDO；无 GSSAPI；每个 `SshClient`/`SftpClient` 各自一条 TCP 连接 | `Renci.SshNet.csproj:39-41`；`.github/workflows/build.yml:80-81`；README；[SSHNET-REL] |
| **Tmds.Ssh** | 0.26.0（2026-10-10）；@e186178（2026-10-14） | MIT | `IsAotCompatible`（`Tmds.Ssh.csproj:9`）；**本机 AOT 发布 0 警告，7.2 MB，运行正常** | 全 async（`ValueTask`、`Sequence` 池）；读 ssh_config（Include、Host、`Match all/host/originalhost/user/localuser/canonical/final`）；known_hosts（含 `UpdateKnownHostsFileAfterAuthentication`）；`StrictHostKeyChecking=ask` 回调；ssh-agent（`SSH_AUTH_SOCK` 或 Windows `\\.\pipe\openssh-ssh-agent`）；`SshProxy` 跳板链（ProxyJump）；OpenSSH 证书；GSSAPI/Kerberos；agent 转发、X11 转发；本地/远端/SOCKS 转发；SFTP；`AllocateTerminal` + `SetTerminalSize` + `SendSignal` + `GetExitStatusAsync()`（exit code + signal）；`AutoReconnect`、KeepAlive | 只支持安全算法（无 ssh-rsa SHA-1；加密仅 aes-gcm 与 chacha20-poly1305，老旧设备可能连不上）；**无 keyboard-interactive**（`kbdinteractivedevices` 注释 "not supported"）；**私钥仅 openssh-key-v1**（无 PuTTY/PEM）；**无 sk-* FIDO**；**不支持 `ProxyCommand`、`SetEnv`、`Tag`、`AddressFamily`、`ControlPath`、`Match exec` —— 遇到即抛异常**（本机实测）；`IdentityAgent` 被忽略 | `src/Tmds.Ssh/SshConfigParser.cs:188-262,604-657,897-960`；`SshAgent.cs:35-44`；`src/docfx/index.md` |
| **Microsoft.DevTunnels.Ssh** | 3.12.42；@e65278d（2026-08-24） | MIT | 仅 netstandard2.1/net48；用反射加载 Cng/OpenSsl 程序集；**本机 AOT 发布 11 条 IL 警告**（IL2026/2059/2067/2070/2072/2075）；运行时 `ecdh-sha2-*` 与 `aes256-gcm` **不可用**（macOS，JIT 下同样不可用） | 任意 Stream 上的 SSH、通道复用、端口转发、会话重连扩展 | README Limitations：不支持「登录用户账号、连接 shell、执行命令、SCP/SFTP、渲染终端」；无 ed25519/curve25519/chacha20/证书 | `src/cs/Ssh/Algorithms/ECDiffieHellman.cs:33-110`；`SshAlgorithm.cs:60-110`；README [已核实] |
| **libssh**（C） | 0.12.2（2026-07-28 安全版）；0.12.0（2026-02-10）加入 FIDO/U2F（libfido2）、sshsig、GSSAPI KEX、PQ KEX、ProxyJump 改进 | **LGPL-2.1** | FFI（`LibraryImport` 源生成，AOT 友好）；需为 6 RID 编译并随包分发 .dylib/.so/.dll，LGPL 要求动态链接、允许替换 | ssh_config 覆盖最全的第三方实现：`Match exec` 真执行、ProxyJump、ProxyCommand、IdentityAgent | `ControlMaster/ControlPath` 标 `SOC_NA`；仍需自写 C# 绑定（无成熟 .NET 包） | [LIBSSH]；`libssh src/config.c:74-147,1271-1288`（gitlab master）[已核实] |
| **libssh2**（C） | 1.11.1（2024-10-16，之后两年无发版） | BSD-3 | 同上 FFI | 1.11.0 起 sk-* FIDO、RSA 证书、agent 转发 | 无 ssh_config 解析；WezTerm 注释「libssh2 doesn't properly support agent forwarding」 | [LIBSSH2]；`wezterm-ssh/src/channelwrap.rs:149-157` |
| **russh**（Rust） | 0.64.1；@e5d80a0（2026-10-05） | Apache-2.0 | 需自建 `cdylib` + C ABI + 6 RID 交叉编译（Rust 工具链进 CI） | async、PQ KEX、证书、agent（含 Windows 命名管道、Pageant）、agent 转发、PPK、russh-config（含 ProxyJump 字段）、sk-ed25519 公钥类型 | 无 FIDO 签名；ssh_config 支持弱；FFI 层工作量大 | `russh/README.md`；`russh/src/keys/agent/client.rs:73-110` |
| **libsasshost**（本机缓存 1.3.3 / 1.4.0） | — | — | — | **与 SSH 无关**：是 Andrey Taritsyn 的「LibSass Host for .NET」，LibSass（Sass/SCSS 编译器）的 .NET 包装，原生库在 `LibSassHost.Native.*` 包里（本机还缓存了 linux-x64、win-x64）。名字里的 "sass host" 易被误读为 "ssh"。来自 nuget.org，大概率是本机其他项目的依赖 | — | `~/.nuget/packages/libsasshost/1.4.0/libsasshost.nuspec`（只读）[已核实] |

### 2.2 SSH.NET 细节（读源码 @7d54d88）

| 项目 | 情况 | 位置 |
|---|---|---|
| pty-req | `ChannelSession.SendPseudoTerminalRequest(term, cols, rows, w, h, modes)`；`TerminalModes` 枚举可传 `IUTF8` 等 | `Channels/ChannelSession.cs:134`；`Common/TerminalModes.cs:7` |
| window-change | `ShellStream.ChangeWindowSize(cols, rows, w, h)` | `ShellStream.cs:296-300` |
| env / signal / break / keepalive@openssh.com / x11-req / xon-xoff | 请求类型都有实现 | `Messages/Connection/ChannelRequest/*.cs` |
| exit-status / exit-signal | 只有 `SshCommand`（exec）暴露 `ExitStatus` / `ExitSignal`；**`ShellStream` 只有 `Closed` 事件，拿不到 shell 退出码** | `SshCommand.cs:84-101,564-575`；`ShellStream.cs:41-51` |
| 读路径 | 消息线程回调 `Channel_DataReceived` → `lock` + `Monitor.PulseAll` 写入 `ArrayBuffer`，同时 `DataReceived` 事件再 `ToArray()` 复制一次；**没有重写 `ReadAsync`**（走 Stream 默认的同步包装） | `ShellStream.cs:1015-1029` |
| 背压 | 本地窗口在**收到数据时**立即扣减并在低于包大小时补满（`AdjustDataWindow` 在 `OnData` 内），**与消费者是否读走无关** → 慢消费者时 `_readBuffer` 无界增长 | `Channels/Channel.cs:380-394,770-780` |
| 线程模型 | 每个 Session 一个长期线程 `MessageListener` | `Session.cs:610,1916` |
| 主机密钥 | `HostKeyReceived` 事件（`CanTrust`、SHA256 指纹、`Certificate`）；known_hosts 解析需自写 | `Common/HostKeyEventArgs.cs:12-98` |
| 连接方式 | `IConnector` 为 internal，返回 `Socket` —— **无法插入自定义 Stream（ProxyCommand/跳板）** | `Connection/IConnector.cs:9-18` |
| agent | 无；SshNet.Agent（第三方，darinkes，2026.0.0）提供 OpenSSH agent + Pageant、sk-* 经 agent 签名 | [SSHNET-AGENT] |

### 2.3 Tmds.Ssh 细节

- 背压：`AdjustChannelWindow` 在**消费者读走数据时**才扣减并在低于半窗时补窗（`SshChannel.cs:418-463`）——正确的流控，慢 UI 会自然把远端压住。默认窗口 2 MB。
- pty-req 的 encoded modes 由 `TerminalSettings` 生成（VERASE=0x7F、VINTR=^C…，`IsUtf8` 跟随编码设置 IUTF8），`ExecuteOptions.TerminalType` 默认 `xterm-256color`（实测输出）。`TerminalSettings.cs:9-217`；`ExecuteOptions.cs:162-166`。
- ssh_config 实测（AOT 二进制，本机 2026-10-16）：

| 测试配置 | 结果 |
|---|---|
| 系统默认（macOS `/etc/ssh/ssh_config` → `Include ssh_config.d/*` → `crypto.conf` 的 `KexAlgorithms ^ecdh-sha2-nistp256`） | 成功；`^` 前置语法生效；`SendEnv LANG LC_*` 生效 |
| `ProxyJump jump1,jump2` | 成功 |
| `IdentityAgent ~/.1password/agent.sock` | 解析成功但**被忽略**（源码注释 "auth agent is not supported" 指的是该关键字） |
| `SetEnv` / `ProxyCommand` / `ControlPath` / `AddressFamily` / `Tag` | **`NotSupportedException`，整个连接失败** |
| `Match exec "true"` | **`NotImplementedException`** |
| 不匹配当前主机的 `Host other` 块里的 `ProxyCommand` | 成功（只对匹配块报错） |

> 结论：Tmds.Ssh 是 .NET 里最接近「与系统一致」的托管实现，但它对未知关键字是**失败关闭**的。只要用户 config 里有一行 `SetEnv` 或 `ProxyCommand`（非常常见），直接用 `SshConfigSettings.DefaultConfig` 就会连不上；TKCode 若采用，必须用 `ssh -G` 结果或自写预处理来兜底。

---

## 3. 「与系统一致」的需求清单

本机 OpenSSH 10.3p1（macOS），上游最新 10.6p1（2026-10-06）。`ssh_config` 有 112 个有效关键字（`readconf.c` 关键字表，另有 20 个弃用/不支持）。

| 需求 | OpenSSH 行为 | 托管库（以 Tmds.Ssh 为上限）会丢失什么 | 自己补的成本 |
|---|---|---|---|
| `Include`（相对路径基于 `~/.ssh`/`/etc/ssh`、glob） | 支持 | Tmds.Ssh 支持；SSH.NET 无 | 低 |
| `Host` 通配 / 否定 `!` | 支持 | 支持 | 低 |
| `Match`：`host/originalhost/user/localuser/all/canonical/final/tagged/exec/localnetwork/version/sessiontype/command` | 全部（`readconf.c:738-871`） | Tmds.Ssh 无 `exec`/`localnetwork`/`tagged`/`version`/`sessiontype`/`command`；无 `CanonicalizeHostname` 两遍解析 | 中：`exec` 要执行 shell（安全与 token 展开 `%h %p %r %C`） |
| `ProxyJump`（`-J`，多级） | 支持 | Tmds.Ssh 支持；SSH.NET 无 | — |
| `ProxyCommand`（含 `%h %p`、`ProxyUseFdpass`） | 支持 | Tmds.Ssh 抛异常；SSH.NET 无法注入 Stream | 中：起进程、stdio 作为传输 Stream（Tmds.Ssh 内部有 `ConnectCallback`，但为 internal） |
| `IdentityAgent` / `SSH_AUTH_SOCK` | 支持，含 `none`、`SSH_AUTH_SOCK` 字面值 | Tmds.Ssh 忽略 `IdentityAgent`，只读环境变量 → **1Password（`~/Library/Group Containers/2BUA8C4S2C.com.1password/t/agent.sock`）、Secretive（`~/Library/Containers/com.maxgoedjen.Secretive.SecretAgent/Data/socket.ssh`）这类按主机配置的 agent 会失效** | 低-中：自己解析后传 agent 地址 |
| Windows agent | Win32-OpenSSH 服务监听 `\\.\pipe\openssh-ssh-agent`；1Password Windows 也接管该管道；Pageant 0.75 起有命名管道，0.77 起 `--openssh-config` 写 `IdentityAgent` 片段；Git for Windows 自带 ssh 不用该管道 | Tmds.Ssh 默认就连该管道；Pageant 的私有管道需要 `IdentityAgent` | 低 |
| `IdentitiesOnly`、`IdentityFile`、`CertificateFile` | 支持；agent 里 key 过多会撞 `MaxAuthTries`（1Password 文档称 "six-key limit"） | Tmds.Ssh 支持 `IdentitiesOnly`/`IdentityFile`/`CertificateFile` | — |
| `ControlMaster`/`ControlPath`/`ControlPersist` | 复用已有 master，用户期望「第二个 tab 不再输密码」 | 托管库**完全无法复用 OpenSSH 的 master**（PROTOCOL.mux 是私有 Unix socket 协议）；只能在自己进程内复用会话 | 高（实现 mux 客户端）；或接受「TKCode 内部复用」 |
| known_hosts：哈希主机名 `|1|salt|hash`、`@cert-authority`、`@revoked`、`[host]:port`、`UpdateHostKeys`、`KnownHostsCommand`、`HostKeyAlias`、`CheckHostIP` | 全部（`hostfile.h:59-63`；`sshd.8:717-844`） | Tmds.Ssh 支持读写（含哈希、CA、revoked [推断：源码有 `TrustedHostKeys`/`HostCertificateInfo`]）；无 `KnownHostsCommand`/`HostKeyAlias`/`UpdateHostKeys` | 主机密钥确认 UI：中（必须做，指纹 SHA256 + 随机艺术可选） |
| keyboard-interactive / MFA（Duo、Google Authenticator、PAM） | 支持 | **Tmds.Ssh 不支持**；SSH.NET 支持 | Tmds.Ssh 内补：中（RFC 4256，需 fork 或上游 PR） |
| FIDO2（`sk-ssh-ed25519@openssh.com`、`sk-ecdsa…`） | 通过 `SecurityKeyProvider`/libfido2，触摸与 PIN 提示 | 托管库均**不支持私钥文件签名**；**经 ssh-agent 签名可行**（agent 驱动硬件），前提是库认识 sk 公钥算法——Tmds.Ssh 源码无 sk 字样 [推断：经 agent 也会失败] | 高（CTAP2/HID 6 平台） |
| OpenSSH 证书（用户/主机） | 支持 | SSH.NET、Tmds.Ssh 都支持 | — |
| GSSAPI/Kerberos | `gssapi-with-mic`、GSSAPI KEX（Debian/RHEL 补丁） | Tmds.Ssh 有 `gssapi-with-mic`，无 GSSAPI KEX | — |
| `SendEnv`/`SetEnv`、`RequestTTY`、`RemoteCommand`、`LocalCommand`、`EscapeChar`（`~.`）、`ObscureKeystrokeTiming`、`ServerAliveInterval/CountMax`、`ConnectTimeout`、算法 `+ - ^` 修饰、`/etc/ssh/crypto.conf`（macOS） | 支持 | 多数缺，或抛异常 | 逐项中低，但长尾无穷 |

**判断**：用托管库永远是「OpenSSH 子集 + 自己的 bug」。差距最大的四项是 **ControlMaster 复用、`Match exec`/`ProxyCommand`、keyboard-interactive（Tmds）、FIDO2**——恰恰是企业用户最常见的配置。

---

## 4. SSH 会话细节（RFC 4254 + OpenSSH）

| 消息 | 格式 / 语义 | 实现要点 | 来源 |
|---|---|---|---|
| `pty-req`（want_reply） | `string TERM, uint32 cols, rows, px_w, px_h, string modes`；「零尺寸 MUST 忽略，字符尺寸优先于像素」 | 像素尺寸要填（sixel/kitty 图形协议、`TIOCGWINSZ` 的 xpixel 依赖它）；OpenSSH 客户端直接取本地 `ioctl(TIOCGWINSZ)` | RFC 4254 §6.2；`openssh clientloop.c:2716-2747` |
| encoded terminal modes | opcode(byte)+uint32 对，`0`=TTY_OP_END；1–18 为控制字符（VINTR=1 … VDISCARD=18；255 表示禁用），30–42 iflag（`ICRNL`=36、`IXON`=38、**`IUTF8`=42，RFC 8160**），50–62 lflag（`ISIG`=50、`ICANON`=51、`ECHO`=53…），70–75 oflag（`OPOST`=70、`ONLCR`=72），90–93 cflag（`CS8`=91），128/129 = ISPEED/OSPEED | OpenSSH 把本地 termios 全量编码；GUI 客户端没有本地 tty，应发一套合理默认（VERASE=0x7F、VINTR=^C、ICRNL/IXON/ISIG/ICANON/ECHO*/OPOST/ONLCR/CS8/IUTF8=1，速率 38400）——Tmds.Ssh `TerminalSettings` 即此做法 | RFC 4254 §8；RFC 8160；`openssh ttymodes.h`、`ttymodes.c:278-335` |
| `window-change` | 无回复；cols/rows/px | 前端 resize 节流后发送 | RFC 4254 §6.7；`channels.c:5064` |
| `env` | 服务端按 `AcceptEnv` 白名单；**上游默认不接受任何变量**（`sshd_config.5`："The default is not to accept any environment variables"）；Debian 默认 `AcceptEnv LANG LC_* COLORTERM NO_COLOR`；macOS 客户端默认 `SendEnv LANG LC_*` | 被拒绝时不能当错误（WezTerm 只提示一次）；`TERM` 不走 env，走 pty-req（OpenSSH 用 `SetEnv TERM=…` 覆盖 pty-req 里的 TERM，`ssh.c:2202`） | `sshd_config.5:63-89`；Debian `debian-config.patch`；`/etc/ssh/ssh_config.d/100-macos.conf` |
| `signal` | 名字不带 SIG；sshd 用 `killpg` 发给会话进程组；forced-command / subsystem 会话拒绝；扩展 `INFO@openssh.com` | 在 pty 会话中，Ctrl-C 应作为字节 0x03 发送（由远端 tty 的 ISIG 产生信号），`signal` 请求用于「强制终止」 | RFC 4254 §6.9；`session.c:2102-2141`；PROTOCOL §2.6 |
| `exit-status` / `exit-signal` | `uint32`；或 `signal name, bool core, string msg, lang` | 终端退出 UI 要区分 code 与 signal | RFC 4254 §6.10 |
| keepalive | 客户端 `ServerAliveInterval` 发全局请求 `keepalive@openssh.com`（want_reply）；新的 transport 层 `SSH2_MSG_PING/PONG`（ext-info `ping@openssh.com`） | `ServerAliveCountMax` 次无回复即断 | `clientloop.c:502`；PROTOCOL §1.8 |
| 断线重连 | SSH 协议**没有会话恢复**；断线 = shell 死亡（除非远端有 tmux/mosh/持久 host） | 重连只能新开 shell；要「不丢会话」得在远端放持久进程（VS Code Server ptyHost、WezTerm mux、Zed `remote_server proxy --reconnect`） | `zed transport/ssh.rs:628-700` |
| 端口转发 | `tcpip-forward`/`direct-tcpip`/`forwarded-tcpip`；OpenSSH 扩展 `streamlocal` | 托管库都有 | RFC 4254 §7；PROTOCOL §2.4 |
| SFTP | subsystem `sftp`，v3 + OpenSSH 扩展（`posix-rename`、`statvfs`、`limits`、`copy-data`、`home-directory`…） | 以后给 TKCode 文件树用；SSH.NET 2026.0.0 大幅降低分配；Tmds.Ssh 有完整 async SFTP | PROTOCOL §4 |
| agent 转发 | 旧：`auth-agent-req@openssh.com` + 通道 `auth-agent@openssh.com`；**新：RFC 9987（2026-05）`agent-req` + `agent-connect`，服务端 ext-info 宣告 `agent-forward`**（OpenSSH 10.3 起） | 客户端需兼容两套名字；会话绑定 `session-bind@openssh.com` | RFC 9987 §7；`clientloop.c:2829-2840`；`kex.c:330-345,480-495`；PROTOCOL.agent |

---

## 5. 远端 terminfo 与 shell 集成

| 方案 | 做法 | 谁在用 | 来源 |
|---|---|---|---|
| 统一报 `xterm-256color` | 最稳；新功能用 `COLORTERM=truecolor`、`TERM_PROGRAM` 让程序探测 | VS Code（`terminalProcess.ts:148-154`）、WezTerm 默认 `term = xterm-256color`（`config.rs:1751`）、Zed | [已核实] |
| `SetEnv TERM=xterm-256color` + `SendEnv COLORTERM TERM_PROGRAM…` | 本地 TERM 可以是自定义值，进 ssh 时降级 | Ghostty `ssh-env` | `ghostty/src/cli/ssh.zig:290-303` |
| 自动安装 terminfo | `ssh host 'mkdir -p ~/.terminfo && tic -x -'` 喂入编译源，本地缓存「已安装」 | Ghostty `ssh-terminfo` | `ssh.zig:480-560` |
| bootstrap 脚本经 tty 传输 | `kitten ssh` 在远端执行 POSIX sh（或 Python）引导脚本，从 tty 读取 base64 的 tar（含 terminfo + shell 集成 + env），用一次性口令防伪；env 由脚本设置，**绕开 AcceptEnv** | kitty | [KITTY-SSH] |
| 远端常驻 server | 远端装二进制，经 SSH stdio 通信，提供 pty、文件、索引 | VS Code Server、Zed remote_server、Warp SSH extension、WezTerm mux | §1 |

**建议**：TKCode 终端核心默认 `TERM=xterm-256color`，自定义能力（OSC 133 提示符标记、OSC 7 cwd、OSC 633 命令行）全部走**不依赖 terminfo 的 OSC 序列**。远端 shell 集成注入分两级：①默认不注入（零侵入）；②用户开启时采用 kitty 式「经 tty 传 bootstrap」或 Ghostty 式「ControlMaster 上 scp 片段到 `~/.tkcode/shell/` 并 `exec $SHELL` 时 source」。[推断]

---

## 6. 传输抽象（C# 接口草案）

设计约束：NativeAOT（无反射、无动态代理）；字节流不解码（编码交给 VT 解析器，SSH/PTY 都只是字节）；背压从 UI 消费端一路传到 PTY/SSH 窗口；零拷贝优先（`PipeReader` / `ReadOnlySequence<byte>`）；所有异步可取消。

```csharp
namespace TKCode.Terminal.Transport;

/// 终端尺寸：字符与像素（像素可为 0）。
public readonly record struct TerminalSize(int Columns, int Rows, int PixelWidth = 0, int PixelHeight = 0);

/// 退出信息：Code 与 Signal 至多一个非空；两者都空表示「连接丢失、原因未知」。
public sealed record TerminalExit(int? ExitCode, string? Signal, bool CoreDumped = false, string? Message = null);

[Flags]
public enum TransportCapabilities
{
    None = 0,
    Resize = 1,            // 能把尺寸传给远端（串口、回放没有）
    PixelSize = 2,
    Signals = 4,           // 能发 INT/TERM/KILL…（本地：killpg；SSH：signal 请求）
    ForegroundProcess = 8, // 能查询前台进程名/pid（本地 tcgetpgrp；SSH 不行）
    WorkingDirectory = 16, // 能直接读 cwd（本地 /proc 或 proc_pidinfo；其余靠 OSC 7）
    Persistent = 32,       // 能 Detach 后重新 Attach（pty host、tmux、远端 server）
}

public interface ITerminalConnection : IAsyncDisposable
{
    string Kind { get; }                       // "local-pty" | "ssh" | "wsl" | "docker" | "serial" | "replay"
    TransportCapabilities Capabilities { get; }

    /// 远端输出。消费者 AdvanceTo 之前，传输层不得继续从 PTY/通道读取超过缓冲上限（背压）。
    PipeReader Output { get; }

    /// 用户输入。键盘、粘贴、鼠标报告都已编码为字节。
    ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken ct = default);

    /// 节流由调用方负责；无 Resize 能力时忽略。
    ValueTask ResizeAsync(TerminalSize size, CancellationToken ct = default);

    ValueTask<bool> SendSignalAsync(string signal, CancellationToken ct = default);

    /// 进程/通道结束时完成；Output 在其之前读到 EOF。
    Task<TerminalExit> Exited { get; }

    /// 可选信息（无能力时返回 null）。
    ValueTask<ProcessInfo?> GetForegroundProcessAsync(CancellationToken ct = default);
    ValueTask<string?> GetWorkingDirectoryAsync(CancellationToken ct = default);
}

public sealed record ProcessInfo(int? Pid, string Name, string? CommandLine);

/// 启动参数与工厂：每种传输一个实现，AOT 下用显式注册而非反射扫描。
public sealed record TerminalLaunch(
    TerminalSize InitialSize,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment,  // null 值 = 删除变量
    string? Term = "xterm-256color");

public interface ITerminalConnector
{
    string Kind { get; }
    ValueTask<ITerminalConnection> ConnectAsync(TerminalLaunch launch, CancellationToken ct);
}
```

各实现映射：

| 实现 | Output 来源 | Resize | Exited | 备注 |
|---|---|---|---|---|
| 本地 PTY（Unix `forkpty`、Windows ConPTY） | master fd / ConPTY 输出管道 → `PipeWriter` | `TIOCSWINSZ` / `ResizePseudoConsole` | `waitpid` / 进程句柄 | 能力最全 |
| 系统 ssh（路线 A） | 就是本地 PTY 里跑 `ssh -t …` | 本地 `TIOCSWINSZ` → ssh 转 `window-change` | ssh 退出码（远端退出码透传；255 = ssh 自身错误） | 无需任何 SSH 代码 |
| Tmds.Ssh 通道（路线 B） | `RemoteProcess.ReadAsync` → `PipeWriter`（通道窗口自然背压） | `SetTerminalSize` | `GetExitStatusAsync()` | |
| WSL | 本地 PTY 跑 `wsl.exe -d <distro> --cd …` | 同本地 | 同本地 | |
| `docker exec -it` / `kubectl exec -it` | 本地 PTY 跑 CLI | 同本地 | CLI 退出码 | Docker Engine API 的 `/exec/{id}/resize` 可做托管版，非必需 |
| 串口 | `SerialPort`（需 System.IO.Ports 包，非共享框架）或原生 fd | 无 | 关闭即退出 | |
| 回放 | 录制文件（asciicast v2）按时间戳写入 | 记录中的 resize 事件 | 文件结束 | 测试与截图快照用 |
| 远程 pty host 代理 | 二进制帧通道上的数据帧 | 控制帧 | 控制帧 | 见 §7 |

背压语义：参考 VS Code 的 ack 流控——ptyHost 记录未确认字符数，超过 `HighWatermarkChars=100000` 暂停 pty，前端每处理 `CharCountAckSize=5000` 字符回 ack，低于 `LowWatermarkChars=5000` 恢复（`vscode/src/vs/platform/terminal/common/terminal.ts:881-902`；`node/terminalProcess.ts:329-331,583-585`）。在进程内用 `PipeReader` 时由 `Pipe` 的 `pauseWriterThreshold` 自然完成；跨进程时用同样的 ack 计数。

---

## 7. 进程架构

### 7.1 参照

| 产品 | PTY 位置 | 特点 | 来源 |
|---|---|---|---|
| **VS Code** | 独立 **ptyHost**（Electron UtilityProcess，经 MessagePort 直连各窗口） | `PtyService` 持有 `PersistentTerminalProcess`；每个进程挂一个 **@xterm/headless 终端（`XtermSerializer`）**，窗口重载后 `generateReplayEvent()` 用 serialize addon 重放屏幕+滚动区与命令标记；断连宽限期 60 s / 6 s；心跳 5 s，最多重启 5 次；输出按 5 ms 合批；ack 流控 | `vscode/src/vs/platform/terminal/node/ptyService.ts:97,230-256,691-811,1036-1110`；`ptyHostService.ts:26,155-167,364-399`；`common/terminal.ts:466-480,864-902`；`common/terminalDataBuffering.ts:28`；`electron-main/electronPtyHostStarter.ts:44-124` |
| **VS Code Agent 工具** | 在用户可见终端里执行：`runInTerminal`、`getTerminalOutput`、`sendToTerminal`、`killTerminal`；按 shell 集成等级选 rich / basic / none 三种执行策略（OSC 633 A/B/C/D/E 判断命令边界，否则靠空闲检测） | `vscode/src/vs/workbench/contrib/terminalContrib/chatAgentTools/browser/executeStrategy/*.ts`；`tools/runInTerminalTool.ts:454-498` |
| **Zed** | **进程内**：`alacritty_terminal` 的 Term + 自己的 event loop（`spawn_event_loop`），UI 直接锁网格渲染；远程时终端 = 本地 PTY 里的 `ssh -t` | Agent 的 terminal 工具与 ACP `terminal/create` 都创建同一种 `Terminal` 实体，用户在面板里可见 | `zed/crates/terminal/src/terminal.rs:1306,1509-1530`；`crates/acp_thread/src/terminal.rs:936-990`；`crates/agent/src/tools/terminal_tool.rs` |
| **WezTerm** | 进程内或 mux server（本地/远端） | mux domain 让会话独立于 GUI | `docs/multiplexing.md` |

### 7.2 TKCode 现状

- UI ↔ AgentHost：stdio 上的 `JsonRpcConnection`，Content-Length 分帧；发送端 `Enqueue` 每条消息 `ArrayBufferWriter` → `ToArray()`，**无界 `Channel<byte[]>`**；接收端每帧 `new byte[length]` + `JsonDocument.Parse` + `RootElement.Clone()`；通知在读循环里同步分发（`src/TKCode.Rpc.Contracts/JsonRpcConnection.cs:78,280-339,413-459`）。
- AgentHost 按需拉起、崩溃后下次调用重新拉起（`src/TKCode.Desktop/Services/AgentHostConnection.cs:27-95`）。
- bash 工具：`ProcessStartInfo` 重定向 stdout/stderr、关闭 stdin，非 PTY；250 ms 一次把**截至目前的输出快照**（最多 16 K 字符）作为 `ToolExecutionUpdated.Text` 推给 UI（`src/TKCode.Agent.Coding/Tools/Shell/BashTool.cs:186-209,232-253`；`OutputCapture.cs:66-72`）。
- ACP：声明 `terminal:false`、`auth.terminal:false`；v2 草案删除 Client 侧 terminal，改为 Agent 自有、只读的终端输出（输出块 base64）（`docs/protocols/acp-v1.md:152-153,918`）。ACP v1 `terminal/create{command,args,env,cwd,outputByteLimit}` → `terminalId`；`terminal/output` 返回 `{output,truncated,exitStatus}`，超限**从头部截断且在字符边界**；`wait_for_exit`/`kill`/`release`；可把 `{type:"terminal",terminalId}` 嵌入工具调用内容，Client 实时显示 [ACP-TERM]。
- Codex：`item/commandExecution/outputDelta`（每片段 UTF-8 lossy 解码）与 `item/commandExecution/terminalInteraction{processId, stdin}`（向交互式终端写入的内容）（`docs/protocols/codex-app-server.md:304-305`）——终端由 Codex 自己持有，TKCode 只做展示。

### 7.3 实测：现有 JSON-RPC 承载终端字节的代价

方法：把 `JsonRpcConnection.cs` 原样拷入基准程序，两个连接经匿名管道对接，256 MB 负载，Release/JIT，Apple M5，2026-10-16。「raw」= 8 字节头 + 原始字节的二进制帧。ESC 密集样本 = 带 SGR 颜色与中文的日志行。

| 块大小 | raw 二进制帧 | JSON + base64（`byte[]` 字段） | JSON string（ESC 密集） | JSON string（纯 ASCII） |
|---|---|---|---|---|
| 1 KB | 294 MB/s | 188 MB/s，线上 ×1.37 | 121 MB/s，×1.53 | 240 MB/s，×1.06 |
| 4 KB | 1127 MB/s | 658 MB/s，×1.34 | 221 MB/s，×1.50 | 352 MB/s，×1.04 |
| 16 KB | 2391 MB/s | 943 MB/s，×1.34 | 193 MB/s，×1.49 | 436 MB/s，×1.03 |
| 64 KB | 2871 MB/s | 937 MB/s，×1.33 | 146 MB/s，×1.49 | 249 MB/s，×1.03 |

另：JSON+base64 路径**每传 1 MB 负载分配 9–11.5 MB**（GC 压力）。

解读：
- 绝对吞吐上，base64 的 ~900 MB/s 远超任何终端解析/渲染能力（VT 解析通常 100–500 MB/s 级），**单看吞吐够用**。
- 真正的问题是：①分配量 ×9；②与 Agent 事件共用单写者无界队列，`cat` 大文件时会把 `session/event`（流式文本、审批请求）排在几百 MB 终端数据后面——**队头阻塞**；③无界队列无背压；④`string` 承载会因 ESC/控制字符转义膨胀 1.5× 且跨块 UTF-8 截断损坏（Codex 的 outputDelta 就有这个问题）。
- 结论：**终端数据必须走独立通道或独立二进制帧，并带 ack 流控**；JSON-RPC 只承载控制面（create/resize/kill/exit/标题/cwd）。

### 7.4 Agent 工具与「用户可见终端」对架构的影响

| 需求 | 对架构的要求 |
|---|---|
| bash 工具在用户可见终端执行（VS Code 式），或用户能「接管」Agent 卡住的交互命令 | Agent（AgentHost 进程）要能 create/write/read/wait 终端 → 终端会话必须在 **AgentHost 可达**的地方：AgentHost 内，或独立 pty host（AgentHost 与 UI 都连它） |
| ACP `terminal:true` | Client 必须实现 5 个方法 + 输出缓冲/截断 + 进程树清理；终端由 TKCode 持有，位置同上 |
| ACP v2 / Codex | 终端由外部 Agent 持有，TKCode 只读展示 → 只需「回放型」连接（`ITerminalConnection` 的只读实现，输出来自事件） |
| 判断命令结束 / 取输出 | 需要 headless 终端模型 + shell 集成标记（OSC 133/633）在**持有 PTY 的进程**里解析——这正是 VS Code 在 ptyHost 里放 headless xterm 的原因 |
| UI 重载 / 新窗口接管会话 | 会话必须活在 UI 之外，且有 serialize 回放 |

---

## 8. 可选路线

### 8.1 SSH 路线

| 路线 | 优点 | 缺点 | 风险 | 工作量 |
|---|---|---|---|---|
| **A. 系统 ssh 跑在本地 PTY 里** | 100% 与系统一致（config、agent、FIDO2、ControlMaster、2FA 都在 tty 里由 ssh 自己提示）；零 SSH 代码、零新依赖、天然 AOT；与 VS Code/Zed/Ghostty/WT 同路 | Windows 需 Win32-OpenSSH（Win10 1803+ 自带）；拿不到结构化的主机密钥确认 UI（提示在终端里）；SFTP/转发需另行调用 `sftp`/`ssh -L` 或 ControlMaster；Windows 无 ControlMaster | 低 | 小：主机列表（解析 `Host` 或 `ssh -G`）+ profile + `SetEnv TERM`；可选 askpass 接 UI（Zed 模式） |
| **B. 托管库（推荐 Tmds.Ssh；SSH.NET 次选；DevTunnels 排除）** | 结构化 UI（主机密钥对话框、密码框）；进程内多通道复用；SFTP/转发/远端执行同一会话；精确 exit-signal；6 RID 纯托管 | 「与系统一致」只能做到子集：Tmds.Ssh 遇 `SetEnv/ProxyCommand/Match exec/ControlPath` 直接失败、无 keyboard-interactive、无 FIDO、忽略 `IdentityAgent`；SSH.NET 无 agent/ProxyJump/config | 中：用户 config 一行不兼容就连不上；单维护者库（Tmds.Ssh 244 星） | 中-大：config 兜底层、known_hosts UI、kbd-interactive（需 fork/PR）、agent 地址解析 |
| **C. 混合：交互终端用系统 ssh；SFTP/端口转发/Agent 远端执行用托管库（Tmds.Ssh）** | 终端行为与系统一致；结构化能力有托管 API；托管库失败时可回退「在 PTY 里跑 `sftp`/`ssh cmd`」 | 两套认证（用户可能输两次密码，除非 agent/key 免交互）；Unix 上可让托管侧借 `ssh -G` 结果构造设置以减小差异 | 低-中 | 中：第一阶段只做 A，SFTP 文件树上线时再引入 Tmds.Ssh |

### 8.2 进程架构路线

| 路线 | 优点 | 缺点 | 风险 | 工作量 |
|---|---|---|---|---|
| **UI 进程内**（Zed 模式） | 最低延迟；无 IPC；VT 解析与渲染共享内存；实现最快 | UI 崩溃/重启即杀掉所有 shell；Agent 工具（在 AgentHost）无法直接操作终端，需经 UI 转发；渲染线程卡顿会拖慢读取（需独立读线程 + 背压） | 低 | 小 |
| **AgentHost 内** | Agent 工具直接持有终端（bash 工具升级为 PTY 很顺）；ACP terminal 易实现 | AgentHost 设计为可崩溃、按需重启 → 用户 shell 随之消失；终端字节流与 Agent 事件抢同一条 stdio（§7.3 的队头阻塞）；Host 生命周期与窗口/会话绑定 | 高 | 中 |
| **独立 pty host 进程**（VS Code 模式） | 会话独立于 UI 和 AgentHost 存活；UI 重载/多窗口可重连（headless 终端 serialize 回放）；Agent 与 UI 两个客户端共享同一终端；专用二进制通道 + ack 流控 | 多一个 AOT 可执行文件（~5–10 MB [推断]）；需要 headless VT 模型（终端核心必须可在无 UI 进程运行——本来就应如此）；心跳/重启/孤儿回收 | 中 | 大 |

---

## 9. 推荐

1. **SSH：路线 C，分两步。** 第一阶段只做 A（本地 PTY 里 `ssh -t`，TERM 默认 `xterm-256color`，主机列表来自 `ssh -G`/`Host` 解析，Windows 用 `%SystemRoot%\System32\OpenSSH\ssh.exe`）；文件树/SFTP 上线时引入 **Tmds.Ssh**（MIT、AOT 0 警告、async、背压正确），并加一层「config 兜底」：先用 `ssh -G <host>` 得到 OpenSSH 的最终解析结果再映射到 `SshClientSettings`，绕开其对未知关键字抛异常的问题；不支持的认证（kbd-interactive、FIDO 私钥）回退到 PTY 里跑 `sftp`。不选 SSH.NET（无 agent/ProxyJump/config），不选 DevTunnels.Ssh（AOT 警告且算法缺失），不选 FFI 路线（6 RID 原生构建 + LGPL/Rust 工具链，收益不抵成本）。
2. **传输抽象**按 §6 接口落地在终端库中，库本身不依赖 TKCode RPC；本地 PTY 与回放两个实现随第一阶段交付。
3. **进程架构**：第一阶段 **UI 进程内**，但满足三条约束以便将来零改动迁出：①终端核心（VT 解析 + 网格）不依赖 Avalonia，可 headless 运行；②连接层只通过 `ITerminalConnection` 交互；③为跨进程预留「二进制数据帧 + JSON 控制面 + ack 流控」协议（不复用现有 `JsonRpcConnection` 承载数据）。当要做「Agent 在用户可见终端执行命令 / ACP `terminal:true` / UI 重载保活」时，迁到**独立 pty host**（不放 AgentHost）。

---

## 10. 需要用户决策的点

1. **SSH 接入路线**：A（系统 ssh，零代码、完全一致，但无结构化 UI）/ B（托管库，结构化 UI，但必然是 OpenSSH 子集）/ C（推荐：终端走系统 ssh，SFTP 等走 Tmds.Ssh）。另：Windows 是否要求必须有 Win32-OpenSSH（不满足时提示安装，还是内置托管实现兜底）。
2. **终端会话所在进程与生命周期**：第一阶段放 UI 进程（UI 退出即结束 shell），还是一开始就上独立 pty host（支持 UI 重载/多窗口重连、Agent 共享终端，但工作量大一档）。
3. **Agent 与终端的关系**：bash 工具是否改为在用户可见的 PTY 终端中执行（VS Code 式，影响架构选型与审批 UX），以及 ACP 是否声明 `terminal:true`（v2 草案已删除该能力，Codex 也自持终端）。若都「否」，UI 进程内方案可长期够用。

---

## 来源

**仓库（git clone --depth 1，2026-10-16 读取）**
- SSH.NET @7d54d88b（2026-10-14）https://github.com/sshnet/SSH.NET
- Tmds.Ssh @e1861789（2026-10-14）https://github.com/tmds/Tmds.Ssh
- dev-tunnels-ssh @e65278de（2026-08-24）https://github.com/microsoft/dev-tunnels-ssh
- openssh-portable @6545e6a5（2026-10-16）https://github.com/openssh/openssh-portable （PROTOCOL、PROTOCOL.agent、clientloop.c、session.c、ttymodes.[ch]、readconf.c、kex.c、hostfile.h、sshd_config.5、ssh_config.5）
- russh @e5d80a07（2026-10-05）https://github.com/Eugeny/russh
- vscode @4765bd35（2026-10-16，sparse）https://github.com/microsoft/vscode
- zed @6b218b84（2026-10-16，sparse）https://github.com/zed-industries/zed
- wezterm @e2a98e1b（2026-10-10，sparse）https://github.com/wezterm/wezterm
- ghostty @b3dc3d79（2026-10-15，sparse）https://github.com/ghostty-org/ghostty
- tabby @467ce8eb（2026-10-06，sparse）https://github.com/Eugeny/tabby
- microsoft/terminal @44b440bf（2026-10-13，sparse）https://github.com/microsoft/terminal
- libssh `src/config.c`（gitlab master）https://gitlab.com/libssh/libssh-mirror

**NuGet / 发布信息**
- [SSHNET-REL] https://github.com/sshnet/SSH.NET/releases/tag/2026.0.0 ；nuspec https://api.nuget.org/v3-flatcontainer/ssh.net/2026.0.0/ssh.net.nuspec
- Microsoft.DevTunnels.Ssh 3.12.42 nuspec https://api.nuget.org/v3-flatcontainer/microsoft.devtunnels.ssh/3.12.42/microsoft.devtunnels.ssh.nuspec
- Tmds.Ssh releases https://github.com/tmds/Tmds.Ssh/releases （rel/0.26.0，2026-10-10）
- [SSHNET-AGENT] https://github.com/darinkes/SshNet.Agent
- SSH.NET issues #184（agent 转发）、#481/#530/#954（ProxyJump）https://github.com/sshnet/SSH.NET/issues
- [LIBSSH] https://www.libssh.org/ ；0.12.0 CHANGELOG https://sources.debian.org/src/libssh/0.12.0-2/CHANGELOG
- [LIBSSH2] https://libssh2.org/ ；1.11.0 RELEASE-NOTES https://raw.githubusercontent.com/libssh2/libssh2/libssh2-1.11.0/RELEASE-NOTES
- libsasshost：本机 `~/.nuget/packages/libsasshost/{1.3.3,1.4.0}/libsasshost.nuspec`、`readme.txt`；https://github.com/Taritsyn/LibSassHost

**文档 / 标准**
- RFC 4254 https://www.rfc-editor.org/rfc/rfc4254 ；RFC 8160 https://www.rfc-editor.org/rfc/rfc8160 ；RFC 9987 https://datatracker.ietf.org/doc/html/rfc9987
- OpenSSH release notes（10.0 禁用 sshd modp DH；10.3 agent 转发 IANA 码点；最新 10.6）https://www.openssh.org/releasenotes.html
- Debian openssh `debian-config.patch`（AcceptEnv LANG LC_* COLORTERM NO_COLOR）https://sources.debian.org/data/main/o/openssh/1:10.6p1-1/debian/patches/debian-config.patch
- [VSC-TS] https://code.visualstudio.com/docs/remote/troubleshooting
- [WARP-SSH] https://docs.warp.dev/terminal/warpify/ssh ；[WARP-LEGACY] https://docs.warp.dev/terminal/warpify/ssh-legacy
- [KITTY-SSH] https://sw.kovidgoyal.net/kitty/kittens/ssh/
- [JB-SSH] https://www.jetbrains.com/help/idea/create-ssh-configurations.html
- [TERMIUS] https://termius.com/documentation/jump-hosts ；https://docs.termius.com/
- [WINDTERM] https://github.com/kingToolbox/WindTerm
- [TABBY-IMP] https://github.com/Eugeny/tabby/blob/master/tabby-electron/src/sshImporters.ts
- [ACP-TERM] https://agentclientprotocol.com/protocol/terminals
- 1Password SSH agent https://www.1password.dev/ssh/agent/config/ ；Secretive https://github.com/maxgoedjen/secretive-config-instructions
- PuTTY Pageant 与 Windows OpenSSH 互通 https://putty.cs.utah.edu/wishlist/win-pageant-openssh-interop.html
- Win32-OpenSSH releases https://github.com/PowerShell/Win32-OpenSSH/releases （10.0.0.0p2-Preview，2025-10-27）

**本机实测（2026-10-16，Apple M5，macOS，.NET SDK 10.0.301；探针在会话临时目录中构建，未保留）**
- SSH.NET 2026.0.0 / Tmds.Ssh 0.26.0 / DevTunnels.Ssh 3.12.42 的 `PublishAot` osx-arm64 发布与运行；Tmds.Ssh ssh_config 兼容性探针（§2.3 表）。
- `JsonRpcConnection` 吞吐基准（§7.3 表）。
- TKCode 仓库：`docs/architecture.md`、`docs/protocols/acp-v1.md`、`docs/protocols/codex-app-server.md`、`src/TKCode.Rpc.Contracts/*`、`src/TKCode.Agent.Coding/Tools/Shell/*`、`src/TKCode.Desktop/Services/AgentHostConnection.cs`（工作区当前版本，基于 commit dfd3657）。
