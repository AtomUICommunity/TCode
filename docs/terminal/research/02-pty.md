# 终端调研 02：跨平台 PTY 层

状态：调研完成（2026-10-16），待决策
范围：在 macOS / Linux / Windows 上创建伪终端、启动系统 shell，以及读写、resize、退出回收、关闭、前台进程和 cwd 探测。VT 解析、渲染、shell 选择与环境变量策略见 01 / 04 / 05，本文只在它们影响 PTY 设计时提及。
关联文档：[05-platform-behavior-config.md](05-platform-behavior-config.md)（默认 shell、login(1)、环境变量、关闭确认）、[architecture.md](../../architecture.md)、StreamMark RaTeX（"小型原生库 + C FFI + 6 RID CI"的先例）

**标注约定**

- **[实]**：已在下文所列提交的源码、官方文档、NuGet / npm / crates.io 元数据或本机实测中核实。
- **[推]**：基于已核实事实的推理，实现前需要实测。
- 源码引用格式为 `前缀 路径:行号`，前缀与提交号的对应见 §0。

---

## 0. 调研对象与版本

| 前缀 | 对象 | 提交 / 版本 | 日期 |
|---|---|---|---|
| RT | dotnet/runtime | `bc110ba`（main，12.0 alpha）；另核对 `release/10.0` `591ede8`、`release/11.0` | 2026-10-16 |
| NP | microsoft/node-pty | `d182c60`（npm latest 1.1.0；beta 1.2.0-beta.15） | 2026-10-02 |
| WT | microsoft/terminal | `44b440b`（另核对 `release-1.24`、`release-1.25`、`v1.18.10301.0`） | 2026-10-13 |
| GH | ghostty-org/ghostty | `b3dc3d7` | 2026-10-15 |
| XEV | mitchellh/libxev | `9ce8e8e` | 2026-05-06 |
| AL | alacritty/alacritty | `5421470`（alacritty_terminal 0.26.1-dev） | 2026-10-16 |
| WZ | wezterm/wezterm（`pty/` 即 portable-pty 0.9.0） | `e2a98e1` | 2026-10-10 |
| XNU | apple-oss-distributions/xnu | 标签 `xnu-12377.121.6`（最新）；本地快照 `xnu-12377.1.9` | — |
| LX | torvalds/linux | master（7.3-rc7） | 2026-10-16 |
| GLIBC | glibc | HEAD（NEWS 已到 2.45） | 2026-10-16 |
| MUSL | musl | HEAD | 2026-10-16 |
| PP | tomlm/Porta.Pty | `0186b60`（NuGet 2.2.3） | 2026-10-01 |
| VSP | microsoft/vs-pty.net | `8351527` | 2024-05-14 |
| QP | aaasoft/Quick.PtyNet | `304c9bb`（NuGet 1.0.4） | 2026-03-02 |
| MP | guitarrapc/MiniPty | `7460059`（NuGet 1.3.0） | 2026-10-15 |
| RTM | royalapplications/RoyalTerminal | `01833b8`（NuGet 0.7.0-preview.2） | 2026-10-09 |
| GF | ghostflyby/Pty.Net | `9dc4a8f`（NuGet Ghostflyby.Pty 1.0.0） | 2026-10-12 |
| CONPTY | NuGet Microsoft.Windows.Console.ConPTY | 1.25.260930003（stable） | 2026-10-02 |
| local | 本机 macOS 26.6.2（25G83），.NET SDK 10.0.301，Xcode SDK 头文件 | — | 2026-10-16 |

TCode 现状（已读）：`ProcessRunner.cs`、`BashTool.cs`、`ShellResolver.cs` 只用 `System.Diagnostics.Process` 以 `-c` 方式运行非交互命令，没有任何 PTY 代码。Desktop 与 AgentHost 两个进程都会用 `Process` 启动子进程。

---

## 1. 结论摘要

1. **不要在 .NET 进程里从托管代码 fork。** vs-pty.net、Quick.PtyNet 在 `forkpty` 返回后的子进程里继续执行托管代码（`Environment.CurrentDirectory = ...`）[实 VSP src/Pty.Net/Mac/PtyProvider.cs:57-72]。fork 后的子进程只剩一个线程，GC、JIT、锁的状态都是 fork 那一刻的复制品，只允许调用 async-signal-safe 函数。Porta.Pty 把 fork 移到了 C 里，但子进程里仍调用 `setenv`、`execvp`（可能 malloc）[实 PP porta_pty.c:245,265,272]，严格来说也不安全。Ghostflyby.Pty 甚至在 C# 里直接 `fork()`，靠 `GC.TryStartNoGCRegion` 规避 [实 GF PtyProcess.Start.Unix.cs:227-238]。**推荐全部改用 `posix_spawn`。**
2. **posix_spawn 在 Linux 上可以独立完成，在 macOS 上必须加一个小 helper。**
   - Linux：glibc 和 musl 都是**先 `setsid` 再执行 file actions** [实 GLIBC spawni.c:159-174；MUSL posix_spawn.c:69,121]。内核规定"会话首进程第一次不带 `O_NOCTTY` 打开 tty 时，该 tty 自动成为控制终端" [实 LX tty_io.c:2138-2144、tty_jobctrl.c:132-154]。所以 `POSIX_SPAWN_SETSID` + `addopen(0, slave)` 就能得到控制终端（RoyalTerminal 正是这么做的 [实 RTM UnixPtyProcess.cs:56-63]）。
   - macOS：XNU **先执行 file actions，后执行 `POSIX_SPAWN_SETSID`** [实 XNU kern_exec.c:4894 vs 5140 @ xnu-12377.121.6]。打开 slave 时进程还不是会话首进程，拿不到控制终端，而 posix_spawn 也没有 TIOCSCTTY 动作。因此 node-pty、RoyalTerminal、MiniPty 都在 macOS 上用一个十几行的 helper：它先 `ioctl(0, TIOCSCTTY)` 再 `execvp` [实 NP src/unix/spawn-helper.cc:1-23；RTM native/unix-pty/spawn-helper.c]。
3. **.NET 的 SIGCHLD 处理器默认不会抢走我们的子进程**，但有两个例外和一个副作用。
   - 默认只回收 `Process` 类启动的子进程 [实 RT pal_signal.c:374-413；ProcessWaitState.Unix.cs:609-700]。
   - 例外：启动时 SIGCHLD 的原始处置为 `SIG_IGN`，或者进程是 pid 1（容器里），这时会 `waitpid(-1)` 回收所有子进程 [实 同上]。
   - 副作用：它用 `waitid(P_ALL, WNOWAIT)` 窥探。我们的子进程退出后如果迟迟不回收，每次 SIGCHLD 都会触发 .NET 扫描全部托管子进程 [实 ProcessWaitState.Unix.cs:622-640]。所以要**立即**用 `waitpid(pid)` 回收，且**永远不要** `waitpid(-1)`。
   - 退出通知：Linux 用 `pidfd_open`（5.3+）+ poll，macOS 用 kqueue `EVFILT_PROC` + `NOTE_EXIT|NOTE_EXITSTATUS`。RHEL 8（内核 4.18，.NET 10 仍支持）要回退为专用线程阻塞 `waitpid(pid)`。
4. **.NET 会把 SIGPIPE 设为 `SIG_IGN`，`Process.Start` 启动的子进程会继承这个设置**（本机实测：`Process.Start` 启动的 perl 报告 `SIGPIPE=IGNORE`）[实 local；RT PalUnix.cpp:204-210、pal_process.c macOS posix_spawn 路径只重置"自定义处理器"]。在 shell 里会表现为 `yes | head` 之类的管道行为异常。PTY 启动时必须用 `POSIX_SPAWN_SETSIGDEF` 把**全部**信号重置为默认（node-pty 在 macOS 的 pty_posix_spawn 中用 `sigfillset` [实 NP pty.cc:740-866]）。TCode 现有的 BashTool 也有这个问题，见 §6 陷阱 U3。
5. **Apple arm64 上不能直接 P/Invoke 可变参数函数**（`ioctl`、`fcntl`、`open`）：可变参数走栈，固定参数走寄存器，结果参数错位 [实 dotnet/runtime#48752 已关闭，通用方案 #48796 仍为 Open/Future]。Porta.Pty 实测 `fcntl(F_SETFL)` 写入了垃圾标志 [实 PP porta_pty.c:363-375]，RoyalTerminal 的 resize 只好退化为调用 `stty` [实 RTM UnixPty.cs:197-214]。**所以 Unix 侧本来就需要一个 C shim**，"纯 C#"路线在 macOS 上并不纯。
6. **Windows：随应用分发 `conpty.dll` + `OpenConsole.exe`，不依赖系统 conhost。**
   - 2024-08 ConPTY 重写（#17510）后，应用输出的 VT 序列**原样透传**，不再被"重新渲染"；`PASSTHROUGH_MODE`、`RESIZE_QUIRK`、`WIN32_INPUT_MODE` 三个 flag 已从源码删除 [实 WT PR #16954/#17510/#16916]。
   - Windows 10 的 inbox conhost 不再回移修复（DHowett，2024-06）；Windows 11 的 inbox 版本只在"大版本换代"时同步 OpenConsole，下一次可能是 26H2（lhecker，2025-10）[实 WT discussion #17608]。
   - NuGet 包 `Microsoft.Windows.Console.ConPTY` 1.25.260930003（2026-10-02，MIT）包含 x86/x64/arm64 的 `conpty.dll` 和 `OpenConsole.exe` [实 CONPTY]。VS Code（node-pty）、WezTerm、Alacritty、Zed、Porta.Pty 都在分发或支持加载它。
7. **现有 .NET 库没有一个可以直接用**：AOT 安全性、fork 安全性、ConPTY 版本、维护状况至少有一项不达标（§8）。**推荐路线 B**：C# `LibraryImport` 加一个约 300 行的 C shim（`libtcodepty`）和约 40 行的 helper 可执行文件（`tcode-pty-spawn`），只在 4 个 Unix RID 上构建；Windows 不需要我们自己的原生代码。

---

## 2. macOS / Linux 推荐实现

### 2.1 三种创建方式对比

| 方式 | 过程 | 在 .NET 进程中的风险 | 谁在用 |
|---|---|---|---|
| `forkpty`（= `openpty` + `fork` + `login_tty`） | 子进程里 setsid、TIOCSCTTY、dup2，再 exec | fork 复制整个多线程进程；fork 到 exec 之间只能调用 async-signal-safe 函数；CoreCLR 的信号处理器在子进程里会把 SIGTERM 转发给**父进程**（`kill(gPID, sig)`，gPID 缓存的是父进程 pid）[实 PP porta_pty.c:180-200 的事故记录]；完整 fork 大堆进程有页表复制开销 | node-pty（Linux）、Porta.Pty、MiniPty（Linux）、vs-pty.net |
| `openpty` + `fork` + 子进程自己 setup | 同上 | 同上；Rust 的 `Command::pre_exec`（Alacritty、portable-pty）也属于此类，且 portable-pty 在 pre_exec 里 `read_dir("/dev/fd")`，会分配内存 [实 WZ pty/src/unix.rs:152-180,238-290] | Ghostty、Alacritty、WezTerm |
| `openpty` + `posix_spawn` | 内核或 libc 在受控路径里完成 setsid、dup2、exec；glibc 用 `clone(CLONE_VM\|CLONE_VFORK)`，在子进程里屏蔽全部信号 [实 GLIBC spawni.c:31-47,99-135] | 不在子进程里执行任何我们的代码；不复制页表 | node-pty（macOS）、RoyalTerminal、MiniPty（macOS） |

### 2.2 控制终端的语义差异

| 事实 | Linux | macOS（XNU） | 来源 |
|---|---|---|---|
| 会话首进程打开 tty 时自动成为控制终端 | 是（未设 `O_NOCTTY`、尚无控制终端、tty 不属于其他会话；还要求读权限） | 是（`!(flags & O_NOCTTY)` 且没有 `P_CONTROLT`） | LX tty_io.c:2138-2144、tty_jobctrl.c:132-154；XNU vfs_syscalls.c:5168-5176 [实] |
| `TIOCSCTTY` | 要求调用者是会话首进程且尚无控制终端，否则 `EPERM` | 同左 | LX tty_jobctrl.c:365-395 [实] |
| posix_spawn 中 SETSID 与 file actions 的顺序 | glibc / musl：**先 setsid，后 file actions** | **先 file actions，后 setsid** | GLIBC spawni.c:159-161 vs 174；MUSL posix_spawn.c:69 vs 121；XNU kern_exec.c:4894 vs 5140 [实] |
| 结论 | `SETSID` + `addopen(0, slave_path, O_RDWR)` 即可获得控制终端，不需要 helper | 必须在 exec 之后的新进程里做 `TIOCSCTTY`，即需要 helper | 推导自上面三行 [实] |

`POSIX_SPAWN_SETSID` 的可用性 [实]：

| libc | 版本 | 备注 |
|---|---|---|
| glibc | 2.26 起（`0x80`） | GLIBC NEWS 2.26、spawn.h:62。.NET 10 的 glibc 基线是 2.27，因此总能使用 |
| musl | 1.1.17 起（`128`） | MUSL WHATSNEW 1.1.17。.NET 基线 musl 1.2.3 |
| macOS | `0x0400` | SDK sys/spawn.h:61；node-pty 为 10.14 以前的系统自行定义 [NP pty.cc:84-86] |

相关的 file action 扩展 [实 GLIBC NEWS、MUSL WHATSNEW、macOS SDK spawn.h]：

| 函数 | glibc | musl | macOS |
|---|---|---|---|
| `addchdir_np` | 2.29 | 1.1.24 | 10.15；26.0 起标记为废弃，改用 `addchdir` |
| `addclosefrom_np` | 2.34 | 无 | 无，改用 `POSIX_SPAWN_CLOEXEC_DEFAULT`（`0x4000`） |
| `addtcsetpgrp_np` | 2.35 | 无 | 无 |
| `pidfd_spawn` | 2.39 | 无 | — |

**注意：RHEL 8 是 glibc 2.28，而 .NET 10 仍支持 RHEL 8** [实 dotnet/core supported-os.md]。它没有 `addchdir_np` 和 `addclosefrom_np`。这是 Linux 上也统一使用 helper 的主要理由：cwd 和关闭多余 fd 都交给 helper 处理，不依赖 libc 版本。

### 2.3 推荐的启动序列（macOS 与 Linux 统一）

```
父进程（C shim: tcpty_spawn，从 C# 一次 LibraryImport 调用进入）
 1. 全局互斥锁（macOS 的 openpty / posix_openpt 并发时会以负 errno -6(-ENXIO) 失败，
    Porta / MiniPty / Ghostflyby 都实测到了 [实 PP porta_pty.c:110-130；MP minipty_unix.c:179-200；GF PtyProcess.Start.Unix.cs:34-45]）
 2. master = posix_openpt(O_RDWR | O_NOCTTY | O_CLOEXEC)  ; macOS 失败且 errno<=0 时重试 ≤8 次，间隔 1 ms
    grantpt(master); unlockpt(master)
    取 slave 路径：macOS 用 ioctl(TIOCPTYGNAME)（线程安全，NP pty.cc:788），Linux 用 ptsname_r
    slave = open(path, O_RDWR | O_NOCTTY | O_CLOEXEC)
    （也可以直接 openpty()，再对两个 fd 设 FD_CLOEXEC，Ghostty 的做法 [GH pty.zig:156-170]）
 3. termios：tcgetattr(slave) 取得系统默认值，只额外加上 IUTF8 → tcsetattr（§2.6）
    ioctl(slave, TIOCSWINSZ, {rows, cols, xpixel, ypixel})
 4. pipe2(statusPipe, O_CLOEXEC)    ; helper 通过它报告 exec 之前的错误
 5. posix_spawnattr:
      flags = POSIX_SPAWN_SETSID | POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETSIGMASK
              (macOS 另加 POSIX_SPAWN_CLOEXEC_DEFAULT)
      sigdefault = sigfillset()        ; 必须是全集，才能清除继承来的 SIGPIPE=SIG_IGN
      sigmask    = sigemptyset()
    file actions:
      adddup2(slave, 0); adddup2(slave, 1); adddup2(slave, 2)
      adddup2(statusPipe[1], 3)
      （master、slave 原 fd 都带 CLOEXEC，不用显式 close）
 6. posix_spawn(&pid, HELPER_PATH, &fa, &attr,
                argv = [helper, cwd, file, argv0, args...],   ; argv0 允许 "-zsh"（login shell）
                envp = 由 C# 构造的完整环境块)
    EINTR 时重试；失败 → 回滚并返回 errno
 7. close(statusPipe[1]); close(slave)    ; 父进程必须关闭 slave，否则永远读不到 EOF
    read(statusPipe[0], &err, 4)：
       返回 0      → exec 成功（fd 3 因 CLOEXEC 被关闭）
       返回 4 字节 → helper 失败：waitpid(pid, 0) 回收，把 err 交给调用方
 8. 解锁；fcntl(master, F_SETFL, O_NONBLOCK)（在 C 里调用，见 §5 的可变参数问题）
 9. 注册退出监视（§2.5），返回 {master, pid}

helper（tcode-pty-spawn，exec 后是一个全新的单线程进程，没有 async-signal-safe 的限制）
 a. ioctl(0, TIOCSCTTY, 0)          ; Linux 上自动获得的控制终端此调用直接成功，macOS 上则必需
 b. close_range(4, ~0U, 0)          ; Linux ≥5.9；否则遍历 /proc/self/fd；macOS 已由 CLOEXEC_DEFAULT 处理
 c. if cwd 非空: chdir(cwd)，失败则回退到 $HOME（与 Ghostty 一致，忽略错误继续 [GH Command.zig:212]）
 d. execve(resolved_file, argv0..., environ)   ; 用 PATH 查找后的绝对路径，查找规则与 execvp 相同
 e. 失败: write(3, &errno, 4); _exit(127)
```

为什么不用 helper 的"exec 后再 setsid"写法：子进程必须在 exec 之前就是会话首进程，否则 shell 的作业控制（`tcsetpgrp`）会失败。`POSIX_SPAWN_SETSID` 已经在 exec helper 前完成了这一步。

macOS 的 login shell：05 报告建议用 `/usr/bin/login -flp <user> /bin/bash --noprofile --norc -c 'exec -l <shell>'`。这只改变 helper 要 exec 的 `file` 和 argv，PTY 层不受影响。注意 login(1) 会 fork 一次，并以 root 身份等待子进程（system_cmds login/login.c:851-879 @ `408bba7`）[实]，所以我们拿到的 pid 是 login 的 pid，退出码由 login 转交 [推，需实测]。

### 2.4 进程组、会话与关闭语义

- shell 是新会话的首进程，它自己管理作业的进程组和前台进程组（`tcsetpgrp`），PTY 层不要干预。
- **关闭标签时应模拟系统终端**：
  1. `kill(-pgid_of_shell, SIGHUP)`（即 killpg），再 `close(master)`。master 关闭后内核也会对会话挂断（hangup），向前台进程组发 SIGHUP。
  2. 等待最多 N ms（建议 2000），然后 `kill(-pgid, SIGKILL)`。
  3. `nohup`、`disown` 或自行 `setsid` 的后台程序**保留**，与 Terminal.app、Ghostty 一致。
- Ghostty 循环执行 `killpg(pgid, SIGHUP)`，直到 `waitpid(WNOHANG)` 返回 [实 GH termio/Exec.zig:1167-1211]：
  - 用来覆盖"setsid 尚未执行时 pgid 还等于父进程"的窗口（`getpgid` 循环，Exec.zig:1213-1245）。采用 posix_spawn 后，`posix_spawn` 返回时 setsid 已经完成，这个窗口不存在 [推]。
  - Darwin 上 killpg 偶尔返回 `EPERM`，Ghostty 选择忽略 [实 Exec.zig:1186-1191]。
- Alacritty 在 Drop 时只对子进程发 SIGHUP，再阻塞 `wait` [实 AL tty/unix.rs:309-319]。
- **"杀进程树"不是系统终端的行为**，不作为默认做法。只有 TCode 自身退出时，才对所有仍存活的会话执行上面的 SIGHUP → SIGKILL 序列。
- Linux 可选 `prctl(PR_SET_PDEATHSIG)`：需要在 helper 里设置，而且只作用于直接子进程（login 或 shell），作为 TCode 崩溃时的兜底 [推]。

### 2.5 退出码获取：与 .NET 运行时共存

**.NET 的行为** [实，main 与 release/10.0 逻辑相同]：

1. 任何 `Process`、`Console` 或 `PosixSignalRegistration` 的使用都会安装 SIGCHLD 处理器（`SA_RESTART|SA_NOCLDSTOP`）。处理器只往管道写一个字节，由 ".NET SigHandler" 线程处理 [RT pal_signal.c:226-266,345-413]。
2. `reapAll = (g_pid == 1) || 原始处置是 SIG_IGN` [pal_signal.c:384]。为真时调用 `waitpid(-1, WNOHANG)`，**连我们的子进程一起回收** [pal_signal.c:388-402；ProcessWaitState.Unix.cs:688-708]。
3. 否则走 `CheckChildren`：
   - `waitid(P_ALL, WEXITED|WNOHANG|WNOWAIT)`（只窥探不回收）[pal_process.c:1152-1173]；
   - 窥探到的 pid 若属于自己则回收；不属于自己（例如我们的 shell）就设 `checkAll`，逐个 `waitpid(pid, WNOHANG)` 自己的子进程，**不碰我们的子进程** [ProcessWaitState.Unix.cs:609-686]。

**对我们的要求**：

| 规则 | 原因 |
|---|---|
| 只 `waitpid(pid, …)`，绝不 `waitpid(-1)` | 否则会抢走 `Process` 的退出状态，导致它的 `WaitForExit` 永远挂起（Porta 的 PtyReaper 注释里有同样的结论 [实 PP PtyReaper.cs:14-36]） |
| 收到退出通知后立刻回收 | 未回收的僵尸会让 .NET 每次 SIGCHLD 都走 `checkAll` 全量扫描 |
| `waitpid` 返回 `ECHILD` 时，退出码记为"未知" | 出现在 reapAll 场景（pid 1 容器、父进程把 SIGCHLD 设为 SIG_IGN）；UI 显示"进程已退出"，不显示退出码 |
| 不要把 SIGCHLD 设为 SIG_IGN 或 SA_NOCLDWAIT | 会让内核自动回收，所有人都拿不到退出码 |

**通知机制**：

| 平台 | 机制 | 要点 | 来源 |
|---|---|---|---|
| Linux ≥5.3 | `pidfd_open(pid, 0)` → poll/epoll 可读 → `waitpid(pid, &st, 0)`（或 `waitid(P_PIDFD)`，需 5.4） | pidfd 对未回收的已退出子进程立即可读；不能用 `/proc/pid` 的 fd 代替，它不可 poll | man pidfd_open(2)、waitid(2) [实]；XEV src/watcher/process.zig:45,110 [实] |
| Linux <5.3（RHEL 8） | 每个会话一个专用线程阻塞 `waitpid(pid, &st, 0)`，处理 EINTR | node-pty 在 Linux 上就是这么做的 | NP pty.cc:200-218 [实] |
| macOS | kqueue `EVFILT_PROC`，fflags = `NOTE_EXIT \| NOTE_EXITSTATUS` → `waitpid(pid)` | 注册时返回 `ESRCH` 表示已经退出，要立即 `waitpid` 兜底；node-pty 参照 Chromium 处理了这个"僵尸竞态" | NP pty.cc:170-200；XEV backend/kqueue.zig:45-48,1270-1282；SDK sys/event.h:261-267 [实] |

建议做法：全进程**一个**退出监视线程（epoll/kqueue 上挂所有 pidfd 或 proc filter，再加一个唤醒管道），Porta.Pty 与 MiniPty 都是这样 [实 PP PtyReaper.cs；MP minipty_unix_exit.c]。读线程见 §2.7。

**.NET 11 的新选项** [实 RT PR #128862，2026-06-30 合并，release/11.0 已含]：`UnixProcessStartArguments.Start(ProcessStartInfo, Func<UnixProcessStartArguments,int>)` 允许我们用自己的系统调用创建进程，再把 pid 交给 `Process` 跟踪。之后 `WaitForExitAsync`、`ExitCode`、`Kill` 都由运行时维护，SIGCHLD 的冲突从根本上消失。缺点：回调在内部的启动锁里执行；TCode 目前用 .NET 10，.NET 11 GA 预计在 2026-11 [推]。**建议 v1 用自研退出监视，抽象成 `IPtyExitWatcher`，升级到 .NET 11 后再评估切换。**

### 2.6 termios 初始值

| 项 | Linux pts 默认 | macOS 默认 | 结论 |
|---|---|---|---|
| iflag | `ICRNL\|IXON`（无 IUTF8） | `BRKINT\|ICRNL\|IMAXBEL\|IXON\|IXANY`（无 IUTF8） | 保留系统默认，**只加 IUTF8**，否则 canonical 模式下退格删不掉完整的 UTF-8 字符 |
| oflag | `OPOST\|ONLCR` | 同 | — |
| cflag | `B38400\|CS8\|CREAD` | `CREAD\|CS8\|HUPCL` | — |
| lflag | `ISIG\|ICANON\|ECHO\|ECHOE\|ECHOK\|ECHOCTL\|ECHOKE\|IEXTEN` | `ECHO\|ICANON\|ISIG\|IEXTEN\|ECHOE\|ECHOKE\|ECHOCTL` | — |
| VERASE | `0x7f` | `0177` | 退格键发送 `0x7f` |
| macOS 特有 | — | `VDSUSP=^Y`、`VSTATUS=^T` | 显式写死 termios 的库（node-pty、vs-pty.net）会丢掉其他平台的默认值，所以不要整体覆盖 |

来源 [实]：LX pty.c:884-893、tty_io.c:124-134、termios_internal.h:21-35；XNU bsd/sys/ttydefaults.h:78-104；NP pty.cc:353-385；GH pty.zig:172-180；AL tty/unix.rs:205-211。

结构体布局：macOS 的 `tcflag_t` 和 `speed_t` 是 `unsigned long`（8 字节），`NCCS=20`；Linux 是 4 字节，`NCCS=32` [实 SDK sys/termios.h:108,263-265]。**termios 只在 C shim 里处理**，不在 C# 里镜像这个结构体。

### 2.7 读写模型

| 事实 | 来源 |
|---|---|
| Linux：最后一个 slave fd 关闭后，`read(master)` 返回 **`EIO`**，poll 报 `POLLHUP` | LX n_tty.c:2140-2141,2449-2450；pty.c:57-69 [实]；WZ 把 EIO 当作 EOF [WZ unix.rs:95-107] |
| macOS：子进程退出后 `read(master)` 返回 **0**（而不是 EAGAIN），随后 poll 报 HUP | XNU tty_dev.c `ptcread` 中 `TS_CONNECTED==0 → EOF` [实]；GH Exec.zig:1680-1684 [实] |
| macOS：内核 tty 输出队列让**每次 read 最多返回约 1 KiB**，与缓冲区大小无关 | GH Exec.zig:1264-1299 [实，Ghostty 实测] |
| macOS：`poll(2)` 的 man 页 BUGS 写着 "does not support devices"，但 Ghostty 实际对 pty master 使用 poll；XNU 实现了 `ptmx_kqfilter` | local man 2 poll；GH Exec.zig:1710-1726；XNU tty_ptmx.c:773 [实]；libuv 对 macOS 上 /dev 的 fd 改用 select 线程 [实 libuv src/unix/stream.c:273-298] |
| 后台进程仍持有 slave（例如 shell 退出前 `cmd &` 了一个守护进程）时，Linux 不会出现 EIO | 推导自上 [推] |

**推荐**：

- 每个会话一个**专用读线程**（`new Thread`，不占用线程池；macOS 上设 QoS user-initiated，Ghostty 实测吞吐提升约 15%）。
- master 设为非阻塞。用 `poll([master, wakePipe])` 等待；可读时连续 `read` 直到 EAGAIN，攒成一批（最多 64 KiB，或最长 3 ms）后交给解析线程。**这就是 Ghostty 的 gather 设计**，用来弥补 macOS 每次只能读 1 KiB 的限制。
- `read` 返回 0 或 `EIO` 一律视为 EOF；`EINTR` 重试。
- 没有使用 epoll/kqueue 管理多个会话：几十个标签对应几十个线程完全可以接受，代码也简单 [推]。
- **写入**：用户输入、粘贴、resize 引发的应答（DA、CPR）放进一个 channel，由**专用写线程**写入。写满（`EAGAIN`）时 poll `POLLOUT`。不能在 UI 线程上阻塞写：子进程不读时，输入队列很快就满（macOS 的 tty 输入队列较小 [推]）。大段粘贴按 4 KiB 分块。
- 关闭时：写入 wakePipe → 读线程退出 → 关闭 master。

### 2.8 resize

- 对 **master** 调用 `ioctl(TIOCSWINSZ, {ws_row, ws_col, ws_xpixel, ws_ypixel})`。内核只在尺寸确实变化时向前台进程组发 SIGWINCH [实 LX tty_io.c:2299-2317；XNU tty.c:1594-1600]，所以 RoyalTerminal 手动补发 SIGWINCH [RTM UnixPty.cs:216-225] 是多余的。
- 务必填写像素尺寸（`cols×cellW`、`rows×cellH`）：sixel、kitty 图形协议和一些 TUI 需要它们。Ghostty 与 Alacritty 都会填写 [实 GH Exec.zig:1140-1147；AL unix.rs:427-436]；Porta.Pty 写的是 0 [实 PP porta_pty.c resize]。
- 调用必须走 C shim（ioctl 是可变参数函数）。
- UI 侧 resize 做 16–33 ms 去抖 [推]。

### 2.9 前台进程、标题与 cwd

| 需求 | Linux | macOS | 来源 |
|---|---|---|---|
| 前台进程组 | `tcgetpgrp(master)` | `tcgetpgrp(master)` | NP pty.cc:659-720；GH pty.zig:266-290 [实] |
| 进程名 | `/proc/<pgrp>/comm`（15 字符）或 `/proc/<pgrp>/cmdline` 第一段 | `proc_name(pid)` / `proc_pidinfo(PROC_PIDTBSDINFO).pbi_name`，或 `sysctl{CTL_KERN,KERN_PROC,KERN_PROC_PID}` 的 `p_comm`（node-pty） | SDK libproc.h:96-102、sys/proc_info.h:72-73,723；NP pty.cc:700-720 [实] |
| 子进程 cwd | `readlink /proc/<pid>/cwd` | `proc_pidinfo(pid, PROC_PIDVNODEPATHINFO)` → `pvi_cdir.vip_path` | SDK sys/proc_info.h:342,741 [实] |
| "是否有程序在运行"（关闭确认） | `tcgetpgrp(master) != shell 的 pgid`，再按忽略列表比对进程名 | 同左 | 05 报告 §8.1 |

- cwd 的首选来源是 shell 集成的 OSC 7；`/proc` 和 libproc 只作兜底。按需查询（标题刷新时节流到 ≤1 Hz），不要轮询 [推]。
- macOS 用 login 包装时，`tcgetpgrp` 得到的是 shell 的 pgid。shell 是 login 的子进程，对比时要用"shell 的 pgid"，不能用 spawn 返回的 pid（那是 login）[推]。
- 这些查询都放进 C shim（`sysctl`、`proc_pidinfo` 的结构体太大，不适合在 C# 中镜像），只返回字符串或整数。

### 2.10 环境变量

- 由 C# 构造完整的 `envp`（`KEY=VALUE\0` 数组）传给 `posix_spawn`。**不要在子进程里 setenv**（Porta.Pty 的做法）。
- PTY 层只负责传递，策略由 05 报告 §2 决定，例如 `TERM=xterm-256color`、`COLORTERM=truecolor`、`TERM_PROGRAM=TCode`，剔除 `TCODE_AGENT`、`VTE_VERSION`、`TMUX` 等。
- `LANG` / `LC_*`：macOS 从 Dock 启动的程序常常没有 `LANG`。是否补 UTF-8 locale 由 05 报告决定。PTY 侧只保证 IUTF8 已经打开。

---

## 3. Windows（ConPTY）推荐实现

### 3.1 API 全集（以 WT `44b440b` 与 Microsoft Learn 为准）

| API | kernel32（inbox） | conpty.dll 导出 | 说明 |
|---|---|---|---|
| `CreatePseudoConsole(COORD, hIn, hOut, flags, HPCON*)` | 1809（17763）+ | `ConptyCreatePseudoConsole`，另有同名别名 | 文档要求 hIn 和 hOut 是同步句柄；新版源码已支持 overlapped（#17510）[实] |
| `ResizePseudoConsole(HPCON, COORD)` | 1809+ | 有，带别名 | 实现上是往 signal 管道写 `{RESIZE, X, Y}` [实 WT winconpty.cpp:288-301] |
| `ClosePseudoConsole(HPCON)` | 1809+；**24H2（26100）起立即返回**，此前会一直等到 conhost 退出 | 有，带别名；OSS 版本已对齐 24H2，不再等待 [实 WT PR #17704] | 向所有客户端发 `CTRL_CLOSE_EVENT` |
| `ReleasePseudoConsole(HPCON)` | **24H2（26100）+** | `ConptyReleasePseudoConsole`，带别名 | 释放 `\Reference` 句柄。之后最后一个客户端断开时 conhost 自动退出，output 管道 `ReadFile` 返回 `ERROR_BROKEN_PIPE` [实 Learn；WT winconpty.cpp:548-575] |
| `ClearPseudoConsole(HPCON, BOOL keepCursorRow)` | 不在公开文档里 | 有，带别名 | 用户执行"清除缓冲区"时同步清空 conhost 缓冲区（GH#1193） |
| `ConptyShowHidePseudoConsole(HPCON, BOOL)` | — | 只有 Conpty 前缀的名字 | 同步伪窗口的最小化状态（GH#12515） |
| `ConptyReparentPseudoConsole(HPCON, HWND)` | — | 只有 Conpty 前缀的名字 | 让 `GetConsoleWindow()` 返回宿主拥有的 HWND（GH#2988） |
| `ConptyCreatePseudoConsoleAsUser`、`ConptyPackPseudoConsole` | — | 只有 Conpty 前缀的名字 | 以其他用户身份运行 / 接收 handoff 会话，TCode 用不到 |

来源 [实]：WT src/inc/conpty-static.h:26-49、src/winconpty/dll/winconpty.def、winconpty.cpp:430-622；Learn CreatePseudoConsole、ClosePseudoConsole、ReleasePseudoConsole 页面。

### 3.2 dwFlags 的真实定义（以现行源码为准）

| flag | 值 | 现状 | 说明 |
|---|---|---|---|
| `PSEUDOCONSOLE_INHERIT_CURSOR` | `0x1` | 有效 | ConPTY 启动时先发 `ESC[6n`，**必须应答**，否则在旧版本中会卡住输入；文档也警告关闭时可能死锁。TCode **不使用** |
| `PSEUDOCONSOLE_RESIZE_QUIRK` | `0x2` | **已删除**（#17510，2024-08） | 旧 inbox 版本中用于改善 resize 重绘；新版本忽略 |
| `PSEUDOCONSOLE_WIN32_INPUT_MODE` | `0x4` | **已删除**（#16916，2024-06）；现在 ConPTY 总是请求 9001 | — |
| `PSEUDOCONSOLE_PASSTHROUGH_MODE` | `0x8` | **已删除**（#16954，2024-03） | **注意：0x8 和 0x10 已被重新用于字宽模式**。新版 conpty.dll 会把旧的 0x8 解释为 GLYPH_WIDTH_GRAPHEMES |
| `PSEUDOCONSOLE_GLYPH_WIDTH_GRAPHEMES` / `_WCSWIDTH` / `_CONSOLE` | `0x08` / `0x10` / `0x18`（掩码 `0x18`） | 有效（#16916 起） | 对应 `--textMeasurement graphemes\|wcswidth\|console`。**必须与我们模拟器的字宽算法一致**，否则光标会错位 |
| `PSEUDOCONSOLE_AMBIGUOUS_IS_WIDE` | `0x20` | 有效，**只在 1.25 中** | 东亚歧义宽度字符按宽字符处理，需与模拟器设置一致 |

来源 [实]：WT conpty-static.h:26-38、winconpty.cpp:169-185；`v1.18.10301.0` 的 winconpty.h:53-63（旧 flag 定义）；`release-1.24` 和 `release-1.25` 的 conpty-static.h 对比。WezTerm 至今仍传 `0x1|0x2|0x4`（WZ pseudocon.rs:85-87），其中 0x4 不落在字宽掩码内，所以无害。

### 3.3 调用序列

```
初始化（进程级）：
  conpty = LoadLibraryExW(<appdir>\conpty.dll 绝对路径, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|…)
           失败 → 回退 kernel32（inbox，功能受限；按"降级模式"记录日志）
  通过 GetProcAddress 取函数指针（delegate* unmanaged[Stdcall]，AOT 安全）
  job = CreateJobObject(); SetInformationJobObject(JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE)  ; TCode 崩溃时兜底

每个会话：
 1. CreatePipe(&inRead, &inWrite) ; CreatePipe(&outRead, &outWrite)   ; 匿名、同步、不可继承
 2. hr = CreatePseudoConsole({cols, rows}, inRead, outWrite, flags, &hpc)
      flags = 0，或 GLYPH_WIDTH_GRAPHEMES（取决于模拟器字宽，见 §3.2）
      ConPTY 内部会 DuplicateHandle 这两个句柄 [WT winconpty.cpp:496]，
      所以可以立即关闭 inRead / outWrite（文档建议在 CreateProcess 之后关闭；两种都可以）
 3. 启动读线程（ReadFile(outRead) 循环）和写线程，**必须在 CreateProcess 之前启动**，
    以便及时应答 ConPTY 的 DA1 握手
 4. InitializeProcThreadAttributeList(NULL, 2, …) 取大小 → 分配 → 初始化
    UpdateProcThreadAttribute(PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, lpValue = hpc（HPCON 值本身，不是 &hpc）, sizeof(HPCON))
    UpdateProcThreadAttribute(PROC_THREAD_ATTRIBUTE_JOB_LIST, &job, sizeof(HANDLE))   ; Win10+
 5. STARTUPINFOEXW si = {cb = sizeof}; si.dwFlags = STARTF_USESTDHANDLES; hStd* = NULL
      （显式置空，防止 TCode 自身被重定向的 stdio 被子进程继承；WZ pseudocon.rs:114-123、NP conpty.cc:396-401）
    CreateProcessW(NULL, cmdline（可写缓冲区）, NULL, NULL, bInheritHandles = FALSE,
                   EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                   envBlock, cwd, &si, &pi)
    DeleteProcThreadAttributeList；CloseHandle(pi.hThread)
 6. 如果 ReleasePseudoConsole 可用（conpty.dll，或 inbox 26100+）：立即调用
      → 此后 outRead 上的 ERROR_BROKEN_PIPE 就是"所有客户端都已断开"的可靠 EOF（node-pty 同样做法，NP conpty.cc:450-460）
 7. 退出通知：RegisterWaitForSingleObject(pi.hProcess, WT_EXECUTEONLYONCE) 或专用等待线程
      → GetExitCodeProcess（AL tty/windows/child.rs:40-80）

resize：ResizePseudoConsole（UI 侧去抖）
清屏：用户执行"清除缓冲区"时调用 ClearPseudoConsole(hpc, keepCursorRow = TRUE)（conpty.dll 才有）

关闭标签：
  a. 读线程**继续读取**，直到 ERROR_BROKEN_PIPE
  b. 在**读线程以外**的线程上调用 ClosePseudoConsole(hpc)（会发 CTRL_CLOSE_EVENT）
     旧版 inbox 中此调用会一直阻塞到 conhost 退出，期间必须有人在读 output 管道，否则死锁
  c. 关闭 inWrite；等进程句柄最多 N 秒；超时则 TerminateJobObject 或 TerminateProcess 兜底
  d. 关闭 outRead、hProcess
```

### 3.4 系统 conhost 与随包 conpty.dll

| 维度 | inbox conhost | 随包 `conpty.dll` + `OpenConsole.exe` |
|---|---|---|
| 版本 | 跟随 OS。Windows 10 不再回移修复；Windows 11 只在大版本换代时同步（下一次可能是 26H2）[实 WT discussion #17608] | 由 TCode 锁定版本，跨 Windows 10 1809 到 11 26H1 行为一致 |
| VT 原样透传、OSC 透传、同步输出等 | 取决于 OS 构建 [推：24H2/25H2 是否已含 #17510 未核实] | 1.22 起具备（WT 1.22 发行说明 [实 devblogs 2024-08-27]） |
| `ReleasePseudoConsole`、不阻塞的 Close | 26100+ | 全部 Windows 版本 |
| Kitty 键盘协议、不等待 DA1 | 否 | 1.25（#19817、#20536）[实] |
| 代价 | 0 | 约 1.2 MB / 架构（x64 的 conpty.dll 120 KB，OpenConsole.exe 1.09 MB）[实 CONPTY] |

**`Microsoft.Windows.Console.ConPTY` NuGet 包** [实，下载 1.25.260930003 解包核实]：

- 版本：stable 线有 `1.24.261001001` 和 **`1.25.260930003`**（两者都发布于 2026-10-02）；预览线是 `1.26.260930004-preview`。许可证 MIT；描述写明 "should work on all versions of Windows 10.0.17763.0 and above"。
- 文件：
  - `runtimes/win-{x86,x64,arm64}/native/conpty.dll`；
  - `build/native/runtimes/{x86,x64,arm64}/OpenConsole.exe`；
  - `runtimes/*/lib/uap10.0/conpty.lib`；
  - `inc/conpty.h`。
- `build/*.props` 和 `*.targets` 按 `PlatformTarget` 把 OpenConsole 复制到 `x64\`、`arm64\` 子目录：x64 应用同时带上 x64 和 arm64 两份，arm64 应用只带 arm64。
- conpty.dll 的查找顺序：同目录 `OpenConsole.exe` → `<本机架构>\OpenConsole.exe`（按 `IsWow64Process2` 判断本机架构）→ **找不到时静默回退 System32\conhost.exe** [实 WT winconpty.cpp:49-95]。**打包时必须验证 OpenConsole.exe 确实在位**，否则功能会悄悄降级。

**同类项目的做法**：

| 项目 | 做法 | 来源 |
|---|---|---|
| node-pty | 随包带 conpty 1.25.260303002（x64、arm64）；`useConptyDll` 仍标为 EXPERIMENTAL，默认 false。VS Code 1.109（2026-02）移除 winpty 后，建议用 `terminal.integrated.windowsUseConptyDll: true` 兜底，仍称实验性 | NP third_party/conpty、typings/node-pty.d.ts:99-111；code.visualstudio.com/updates/v1_109 [实] |
| WezTerm | 随包带 `assets/windows/conhost/{conpty.dll, OpenConsole.exe}`，优先加载同目录的 conpty.dll，否则用 kernel32。2026-05 的 issue #7774：随包的 1.22 版本导致 pwsh 在 TUI 退出时崩溃，换成 1.24 版本后修复，并提醒**两个文件必须成对升级** | WZ pseudocon.rs:44-58 [实]；wezterm#7774 [实] |
| Alacritty | 在 PATH 或可执行文件目录中找到 conpty.dll 就使用，否则用系统 API | AL tty/windows/conpty.rs:30-90 [实] |
| Zed | 2025 年起随包分发 conpty.dll + OpenConsole.exe（PR #39090） | [实 提交说明] |
| Porta.Pty | 依赖 ConPTY 1.24.260710001，带绝对路径的 `DllImportResolver`，防止误绑定 System32 下的 conpty.dll | PP Porta.Pty.csproj:44、ConPtyImportResolver.cs [实] |

### 3.5 Windows 专属陷阱

| # | 陷阱 | 对策 |
|---|---|---|
| W1 | **ClosePseudoConsole 死锁**：24H2 以前的 inbox Close 会等 conhost 退出，而 conhost 可能卡在向已满的 output 管道写"最后一帧" [实 Learn ClosePseudoConsole、Creating a Pseudoconsole session] | 读线程一直读到 BROKEN_PIPE；Close 在其他线程调用；优先使用随包 conpty.dll。Alacritty 的 Drop 里明确写着"会死锁"[实 AL conpty.rs:96-103] |
| W2 | 客户端还在启动时就 Close，会弹出 `0xc0000142` 错误框 [实 Learn] | 启动后约 500 ms 内的关闭请求排队，等进程完成初始化（或先 `WaitForInputIdle`）再执行 [推] |
| W3 | **DA1 握手**：ConPTY 启动时输出 `ESC[c`、`ESC[?1004h`、`ESC[?9001h` [实 WT VtIo.cpp:197-204]。1.24 及更早版本会**阻塞最多 3 秒**等待 DA1 回复（`WaitUntilDA1(3000)`）；1.25 只在 INHERIT_CURSOR 时等待，且只等 1 秒（#20536，2026-08-12）[实 release 分支对比] | 我们是真正的模拟器：读线程在 CreateProcess 之前启动，解析到 DA1 立即回复。**不需要** Porta.Pty 那种 DA1 过滤流（它是为不应答的消费者准备的 hack）[实 PP StartupDa1FilterStream.cs:11-40] |
| W4 | **win32-input-mode（9001）**：ConPTY 请求终端改用 `ESC[Vk;Sc;Uc;Kd;Cs;Rc_` 发送完整的 KEY_EVENT_RECORD | 键盘层要支持 9001，否则 Ctrl+Space、AltGr 组合、按键抬起等无法传给 Win32 控制台程序（WT spec #4999）[实]。1004 焦点事件也要支持 |
| W5 | 字宽不一致：ConPTY 自己维护一个缓冲区，按自己的字宽模式布局；模拟器的字宽不同时，emoji 和 CJK 会让光标错位 | flags 设为与模拟器一致的 GLYPH_WIDTH_*（01/04 报告决定字宽模型后再定）[推] |
| W6 | **旧版 inbox 的"重新渲染"**：#17510 之前，ConPTY 把屏幕当作渲染目标重新生成 VT，会改写或丢弃未知序列和 OSC，resize 时整屏重绘，清屏会出现奇怪的回滚 [实 PR #17510 说明] | 随包 conpty.dll；用户执行"清除缓冲区"时同时调用 ClearPseudoConsole，否则下一次重绘会把旧内容画回来（GH#1193）|
| W7 | resize 后 ConPTY 会重新获取光标位置：1.24 之后的版本会在 resize 后发 `ESC[6n`（#19535）[实 WT VtIo.cpp:747] | 模拟器任何时候都要应答 CPR |
| W8 | 编码：ConPTY 的输入和输出都是 UTF-8 + VT [实 Learn]。客户端程序的控制台代码页（例如 936）只影响程序自己的 `WriteFile` 字节如何解码，ConPTY 交给我们的总是 UTF-8 | 宿主端不需要 chcp；`ReadFile` 的结果直接按 UTF-8 增量解码（处理跨块截断）[推：中文 Windows 上用 `cmd /c type gbk.txt` 实测] |
| W9 | 句柄泄漏：`bInheritHandles=TRUE` 会把 TCode 的可继承句柄泄漏给 shell | 固定传 FALSE（node-pty 注释："VERY IMPORTANT"，NP conpty.cc:434）|
| W10 | 进程树：Close 后，挂在这个控制台上的客户端收到 CTRL_CLOSE_EVENT；脱离控制台的进程（GUI、`start` 出去的进程）不受影响 | 与 WT 一致，接受。Job 只用于 TCode 退出或崩溃时兜底。注意：KILL_ON_JOB_CLOSE 会连"用户在终端里 `start` 出去的 GUI 程序"一起杀掉，需要决定是否设置 `JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK` [推] |
| W11 | 前台进程和 cwd：Windows 没有 tcgetpgrp。`GetConsoleProcessList` 需要先 AttachConsole，而一个进程只能附加一个控制台，所以 node-pty 专门 fork 一个进程来查 [实 NP conpty_console_list.cc、conpty_console_list_agent.ts] | 用 Toolhelp32 遍历以 shell 为根的进程树判断"是否在运行"（VS Code、WezTerm 的做法，见 05 §8.1）；cwd 只取 shell 集成的 OSC 7 / OSC 9;9 |
| W12 | DLL 劫持 / 误绑定：裸名 `conpty.dll` 可能解析到其他位置 | 只用绝对路径 `LoadLibraryExW`；无法加载时明确降级为 kernel32 并记录日志 |

---

## 4. 陷阱清单（Unix）

| # | 陷阱 | 对策 | 来源 |
|---|---|---|---|
| U1 | 托管代码中 fork、子进程运行托管代码、CoreCLR 信号处理器把信号转发给父进程 | 只用 posix_spawn；子进程逻辑全部在 helper 里 | §1.1 |
| U2 | `waitpid(-1)` 抢走 `Process` 的退出状态；不及时回收导致 .NET 反复全量扫描 | 只 `waitpid(pid)`，收到通知立即回收 | §2.5 |
| U3 | **SIGPIPE 继承为 SIG_IGN** | `SETSIGDEF` 用全集；**另外建议修复 TCode 现有的 BashTool**（`Process.Start` 启动的 bash 继承了 SIG_IGN，`… \| head` 的上游不会被 SIGPIPE 终止，只会收到 EPIPE）| 本机实测 [实] |
| U4 | 可变参数函数（ioctl、fcntl、open）在 Apple arm64 上 P/Invoke 后参数错位，而且**静默成功** | 全部放进 C shim | dotnet/runtime#48752/#48796；PP porta_pty.c:363-375 [实] |
| U5 | 父进程没有关闭 slave → 永远读不到 EOF | spawn 后立即关闭 | — |
| U6 | master 或 slave 泄漏给其他子进程（包括 AgentHost、`Process` 启动的工具进程） | 全部用 `O_CLOEXEC` 打开；macOS 用 `CLOEXEC_DEFAULT`，Linux 由 helper 执行 `close_range` | GH pty.zig:156-170；WZ unix.rs:139-180（GNOME/mutter 会给子进程泄漏 fd）[实] |
| U7 | macOS 上 openpty / posix_openpt 并发失败，errno 为 -6 | 加锁 + 重试 | §2.3 |
| U8 | macOS 每次 read 只得到约 1 KiB；单线程"读一次解析一次"会让 `cat` 大文件变慢 | gather 读线程 + 批量交付 | GH Exec.zig:1264-1299 [实] |
| U9 | Linux 上 EIO 和 macOS 上 read 返回 0 是同一种 EOF | 统一处理 | §2.7 |
| U10 | termios 结构体布局因平台而异 | 只在 C 里处理 | §2.6 |
| U11 | resize 不传像素尺寸 | 传 xpixel / ypixel | §2.8 |
| U12 | 写入阻塞 UI | 专用写线程 + channel | §2.7 |
| U13 | helper 被 NuGet 解压后丢失可执行位；macOS 需要签名和公证 | 构建和打包时 `chmod +x`；放在 `Contents/MacOS/` 或 `Contents/Helpers/` 并用 hardened runtime 签名 [推]；RoyalTerminal 在运行时补设 `UserExecute` [实 RTM UnixPtyProcess.cs:112-115] | — |
| U14 | 在 PTY 会话里 `Process` 又启动了带终端的子进程？不会：TCode 自己的 stdin 不是这个 pty，.NET 的 `ConfigureTerminalForChildProcesses` 只作用于 TCode 自身的控制终端 | 无需处理 [推] | RT SafeProcessHandle.Unix.cs:297-310 |

---

## 5. NativeAOT 注意事项

1. **全部用 `[LibraryImport]`，参数只用 blittable 类型**：`int`、`nint`、指针、`[StructLayout(Sequential)]` 的纯值类型。字符串用 `StringMarshalling.Utf8` / `Utf16` 或手动 `byte*`；程序集上加 `[assembly: DisableRuntimeMarshalling]`。不使用 `StringBuilder`、`[MarshalAs]` 等运行时封送。
2. **动态加载的 conpty.dll 用函数指针**：`NativeLibrary.Load(absolutePath)` + `NativeLibrary.GetExport` → `delegate* unmanaged[Stdcall]<COORD, nint, nint, uint, nint*, int>`。不用 `Marshal.GetDelegateForFunctionPointer`（它依赖运行时生成代码）。
3. **C shim 的加载方式**：
   - 默认按名称 `libtcodepty` 懒绑定，文件放在 `runtimes/<rid>/native/`，发布时平铺到输出目录；
   - 可选 `<DirectPInvoke Include="libtcodepty" />` + `<NativeLibrary Include="libtcodepty.a" />` **静态链接进 AOT 可执行文件**，少一个文件，调用也更快 [实 Learn native-aot/interop]。代价是需要为每个 RID 提供 `.a`，而且 JIT（Debug）构建仍然需要 `.so` / `.dylib`；
   - helper 可执行文件无论如何都是单独的文件。
4. **Windows 侧不需要我们自己的原生代码**：所有 Win32 API 都是固定参数（非可变参数）的 stdcall，可以全部 `LibraryImport`（或使用 CsWin32 源生成，Porta.Pty 的做法）。`STARTUPINFOEXW`、`PROCESS_INFORMATION`、`COORD` 都是 blittable 类型。
5. **线程**：读、写和退出监视都用 `new Thread { IsBackground = true }`，不在线程池里阻塞。回调回 UI 时通过 `Dispatcher` 投递，不在原生线程上调用托管回调，因此不需要 `UnmanagedCallersOnly`。
6. **fd 和句柄所有权**：用 `SafeHandle` 子类包装 master fd、HPCON、进程句柄，`ReleaseHandle` 里调用 `close` / `ClosePseudoConsole`。注意 W1：`ClosePseudoConsole` 不能在终结器线程上同步执行。
7. 验证：按 TCode 的规则跑 `dotnet publish -r <rid> -p:PublishAot=true`，要求 0 个 IL2xxx/IL3xxx 警告，再运行 AOT 二进制做一次 PTY 往返冒烟测试（StreamMark 的 aot-smoke 模式）。

---

## 6. 进程归属（PTY 放在哪个进程）

| 方案 | 说明 | 取舍 |
|---|---|---|
| a. Desktop 进程内 | 读线程直接把数据交给模拟器 | 延迟最低，实现最简单；PTY 原生代码崩溃会带走 UI |
| b. AgentHost 进程内 | 经 JSON-RPC 转发字节流 | 多一跳，而且 JSON 不适合传二进制（要 base64，约 33% 开销）；好处是 Agent 的 bash 工具以后可以复用同一个 PTY |
| c. 独立的 pty-host 进程（VS Code 的 ptyHost 模式） | UI 重启或崩溃时终端会话仍然存活 | 复杂度最高；需要二进制帧协议 |

PTY 库本身与进程无关（独立库 `TCode.Terminal.Pty`，未来 SSH 也实现同一个 `ITerminalTransport` 接口）。**v1 建议选 a** [推]，这个选择不影响库的设计。

---

## 7. 现有库与参考实现评估

| 库 | 最新版本 / 最近提交 | 许可证 | TFM | AOT | Unix 子进程创建 | Windows | 结论 |
|---|---|---|---|---|---|---|---|
| **Pty.Net**（microsoft/vs-pty.net） | 无新版；最后提交 2024-05-14 | MIT | netstandard2.0 | DllImport + `StringBuilder` 等非 blittable 封送 | **C# 里 forkpty 后，子进程执行托管代码** [VSP Mac/PtyProvider.cs:57-72] | 随包带旧的 `os64\conpty.dll` + winpty | 不可用 |
| **Quick.PtyNet** | 1.0.4（2026-03-02） | **仓库和包都没有声明许可证**（GitHub license 为 null） | netstandard2.0 | 同上 | 同 vs-pty.net（是它的 fork）[QP Mac/PtyProvider.cs:57-72] | winpty x86/x64（无 arm64）+ 旧 conpty | 不可用（法律风险，且不安全） |
| **Porta.Pty** | 2.2.3（2026-10-01），2.x 从 2026-08 开始密集发版；22.7 万次下载 | MIT | net10.0 | `IsAotCompatible=true`，Windows 用 CsWin32 源生成；但 Mac/NativeMethods.cs 和 OOB ConPTY 仍有 `[DllImport]` [PP] | 原生 shim 内 `forkpty`，fork 前屏蔽信号并加锁；**子进程里调用 `setenv`、`execvp`**（不是 async-signal-safe）；退出用 pidfd/kqueue；resize 不传像素 | 依赖 ConPTY NuGet 1.24.260710001（不是最新），Job Object，DA1 过滤 hack | 最成熟，但子进程实现的安全性和 ConPTY 版本都需要改；可以作为参考或 fork 的基础 |
| **MiniPty** | 1.3.0（2026-07-14），仓库 2026-10-16 仍活跃，6 星 | MIT | net10.0 | 声明 AOT ready，主要用 LibraryImport | Linux：shim 内 forkpty；macOS：posix_spawn + helper；FreeBSD 也支持 | **只用 inbox ConPTY**，不分发 conpty.dll | 设计接近推荐方案，但 Windows 侧不够，社区很小 |
| **RoyalTerminal**（RoyalApps.\*.Pty.\*） | 0.7.0-preview.2（2026-10-11），28 星 | MIT | net10.0（Avalonia 12.1.3 + libghostty） | 以 LibraryImport 为主，resize、waitpid 仍是 DllImport | **C# 直接调 posix_spawn**：Linux 用 SETSID + addopen；macOS 用 helper + 状态管道 [RTM UnixPtyProcess.cs] | ConPTY | Unix 启动方式是最好的参考；resize 在 macOS 上退化为 `stty`；预览版；整体绑定 libghostty |
| Ghostflyby.Pty | 1.0.0（2026-09-05） | Apache-2.0 | net10.0 | 有 LibraryImport | **C# 里直接 `fork()`**，靠 `GC.TryStartNoGCRegion` 规避 | ConPTY | 不可用 |
| Devolutions terminal | 2026-10-09 | 包 MIT，仓库 NOASSERTION | net10.0 | 部分项目 PublishAot | Linux 上用独立的 `dt-pty-host` 原生进程 | — | 可作为"独立 pty 宿主进程"的参考 |
| 其他（Terminal.Pty 0.4.4、PtySharp 1.1.0 只支持 net8 和 Unix、LibGhostty.Net、ConPtySharp 等） | — | — | — | — | — | — | 体量小或不完整，未深入 |

非 .NET 参考实现：

| 实现 | Unix | Windows | 备注 |
|---|---|---|---|
| node-pty（MIT，1.1.0 / 1.2.0-beta.15） | Linux：forkpty（C++）；macOS：posix_spawn + spawn-helper；退出：Linux 专用线程 waitpid，macOS kqueue | 可选 conpty.dll 1.25.260303002，ReleasePseudoConsole | 是"macOS 必须用 helper"的权威出处 |
| portable-pty（MIT，crate 0.9.0，2025-02-11） | `Command::pre_exec`（fork）：setsid、TIOCSCTTY、`close_random_fds`（会分配内存） | 优先同目录 conpty.dll，flags 0x7 | Rust 的 fork 路径在 .NET 进程中同样有 U1 风险 [推] |
| Alacritty | pre_exec 中 setsid + TIOCSCTTY；macOS 用 login；IUTF8；SIGCHLD 通过 signal-hook 管道 | 可选 conpty.dll | — |
| Ghostty | Zig 自己 fork + childPreExec；IUTF8；gather 读线程；killpg 循环；macOS 用 login | inbox CreatePseudoConsole（flags=0），Windows 支持仍初步 | 读路径性能设计的最佳参考 |

---

## 8. 可选路线

| 路线 | 内容 | 优点 | 缺点 | 评估 |
|---|---|---|---|---|
| **A. 纯 C# LibraryImport** | Unix：C# 直接调 openpty、posix_spawn、waitpid、kqueue / pidfd；Windows：C# 调 ConPTY | 没有自研原生代码，也不需要 CI 构建原生产物 | **macOS 做不到**：拿不到控制终端（需要 helper），Apple arm64 上可变参数的 ioctl/fcntl 不安全（resize、非阻塞都受影响）；termios 结构体要按平台镜像；RHEL 8 上没有 addchdir_np | 只有 Linux 和 Windows 能完全满足，不能作为整体方案 |
| **B. C# + 极小的 C shim 和 helper（推荐）** | `libtcodepty`（约 300 行：openpty + termios、spawn、resize、fg pgrp/名称/cwd、exit watch、set_nonblock）+ `tcode-pty-spawn`（约 40 行）。Windows 侧纯 C#，加上 ConPTY NuGet 的二进制 | 所有 fork/exec、可变参数调用、结构体差异都在 C 里一次处理好；子进程逻辑运行在 exec 后的干净进程中；与 StreamMark RaTeX 的 6 RID CI 先例一致（这里只需 4 个 Unix RID）；完全由我们掌控 | 需要维护少量 C 代码和 CI；helper 要随包分发并签名 | **推荐** |
| C. 通过 FFI 复用 Rust portable-pty | cdylib 包一层 C ABI | 有现成的跨平台实现 | Unix 走 fork + pre_exec，`close_random_fds` 会分配内存，回到 U1 风险；退出监视和 SIGPIPE 要自己补；Windows flags 过时；crate 最后发布于 2025-02；需要引入 Rust 工具链和 6 RID 构建 | 不推荐 |
| D. 直接引用现有 .NET 库 | 首选 Porta.Pty 2.2.3 | 马上能用，22.7 万下载，正在维护 | 子进程里调用 setenv/execvp；ConPTY 版本被锁定在 1.24；仍有 DllImport；单人维护；不传像素尺寸；无法插入 login 和 helper 的定制 | 原型阶段可用；产品阶段建议 fork 后改造，实质上就回到了 B |

---

## 9. 推荐

**路线 B**，建议以独立库 `TCode.Terminal.Pty` 的形式实现，未来 SSH 共用 `ITerminalTransport`：

1. **Unix**：
   - posix_spawn 启动 helper，由 helper 执行 TIOCSCTTY、close_range、chdir、execve；Linux 和 macOS 用同一套代码；
   - 退出：pidfd（RHEL 8 回退为 waitpid 线程）或 kqueue，只 `waitpid(pid)`；
   - termios 保留系统默认值并加 IUTF8；resize 带像素尺寸；
   - 关闭：SIGHUP 进程组 → 关闭 master → 超时后 SIGKILL。
2. **Windows**：
   - 随包分发 `Microsoft.Windows.Console.ConPTY` **1.25.260930003** 的 conpty.dll 和 OpenConsole.exe，按绝对路径加载；加载失败时降级为 kernel32；
   - flags 设为 0，或与模拟器字宽一致的 GLYPH_WIDTH_*；
   - 读写各一个线程；Release 可用时立即调用；在读线程以外关闭；Job 只用于 TCode 退出时兜底；
   - 模拟器必须支持 DA1、CPR、9001、1004。
3. **顺带修复**：TCode 现有的 BashTool 启动的子进程继承了 `SIGPIPE=SIG_IGN`（U3）。可以在 `-c` 前面加 `trap - PIPE;`，或者以后统一走新的 spawn 路径。
4. **.NET 11 升级后**：评估用 `UnixProcessStartArguments` / `WindowsProcessStartArguments` 回调接入 `Process` 的退出跟踪，替换自研的 exit watch。

实施里程碑建议：

- M1：Linux 和 macOS 的 spawn、读写、resize、退出、关闭，以及 AOT 冒烟测试；
- M2：Windows ConPTY 与随包 OpenConsole；
- M3：前台进程、cwd、关闭确认；
- M4：压力测试（`cat` 1 GB、并发打开 50 个标签、快速开关、`yes | head`、TUI resize 风暴）。

---

## 10. 需要用户决策的点

| # | 决策 | 选项 | 建议 |
|---|---|---|---|
| 1 | 实现路线 | A 纯 C# / **B C# + C shim + helper** / C portable-pty / D Porta.Pty | **B**；原型期可以用 D 快速验证 UI |
| 2 | Windows 是否随包分发 conpty.dll + OpenConsole.exe（每架构约 1.2 MB，x64 包需同时带 arm64 的 OpenConsole） | 分发（锁定 1.25.x）/ 只用系统 conhost | **分发**；系统 conhost 只作降级 |
| 3 | PTY 放在哪个进程 | a Desktop 进程内 / b AgentHost / c 独立 pty-host | **a**（v1），库保持与进程无关 |
| 4 | Windows Job Object 的 KILL_ON_JOB_CLOSE 是否连带杀掉用户从终端 `start` 出去的程序 | 杀 / 允许 breakaway | 允许 breakaway，只兜底 shell 树 [推] |
| 5 | 退出监视最低内核要求 | 支持 RHEL 8（加 waitpid 线程回退）/ 要求 Linux ≥5.3 | 支持，回退代码约 30 行 |

---

## 11. 来源

### 源码（浅克隆或 GitHub API，只读）

| 来源 | 版本 | 用途 |
|---|---|---|
| github.com/dotnet/runtime：`src/native/libs/System.Native/pal_signal.c`、`pal_process.c`；`System.Diagnostics.Process/src/…/ProcessWaitState.Unix.cs`、`ProcessUtils.Unix.cs`、`SafeProcessHandle.Unix.cs`、`ProcessStartInfo.cs`、`Runtime/InteropServices/UnixProcessStartArguments.cs`；`nativeaot/Runtime/unix/PalUnix.cpp`；`pal/src/exception/signal.cpp` | main `bc110ba`、release/10.0 `591ede8`、release/11.0 | SIGCHLD、reapAll、WNOWAIT、SIGPIPE、posix_spawn 路径、.NET 11 回调 API |
| dotnet/runtime PR #128862、issue #71595、#48752、#48796 | — | 回调 API、可变参数 P/Invoke |
| github.com/microsoft/node-pty `src/unix/pty.cc`、`spawn-helper.cc`、`src/win/conpty.cc`、`conpty_console_list.cc`、`windowsPtyAgent.ts`、`third_party/conpty` | `d182c60` | |
| github.com/microsoft/terminal `src/winconpty/*`、`src/inc/conpty-static.h`、`src/host/VtIo.cpp`、`doc/specs/#4999…`；PR #14041、#16916、#16954、#17510、#17704、#19535、#19817、#19864、#20536、#20774；discussion #17608 | `44b440b`、release-1.24、release-1.25、v1.18.10301.0 | |
| github.com/ghostty-org/ghostty `src/pty.zig`、`src/pty.c`、`src/Command.zig`、`src/termio/Exec.zig` | `b3dc3d7` | |
| github.com/mitchellh/libxev `src/watcher/process.zig`、`src/backend/kqueue.zig` | `9ce8e8e` | |
| github.com/alacritty/alacritty `alacritty_terminal/src/tty/unix.rs`、`tty/windows/{conpty,child}.rs` | `5421470` | |
| github.com/wezterm/wezterm `pty/src/{unix,cmdbuilder}.rs`、`pty/src/win/pseudocon.rs`、`assets/windows/conhost`；issue #7774 | `e2a98e1` | |
| github.com/apple-oss-distributions/xnu `bsd/kern/kern_exec.c`、`bsd/vfs/vfs_syscalls.c`、`bsd/kern/tty.c`、`tty_dev.c`、`tty_ptmx.c`、`bsd/sys/{spawn.h,ttydefaults.h}` | xnu-12377.121.6 / 12377.1.9 | |
| github.com/apple-oss-distributions/system_cmds `login/login.c` | `408bba7` | |
| github.com/torvalds/linux `drivers/tty/{tty_io.c,tty_jobctrl.c,pty.c,n_tty.c}`、`include/linux/termios_internal.h` | master 7.3-rc7 | |
| sourceware.org glibc `sysdeps/unix/sysv/linux/spawni.c`、`posix/spawn.h`、`bits/spawn_ext.h`、`NEWS` | HEAD | |
| git.musl-libc.org musl `src/process/posix_spawn.c`、`include/spawn.h`、`WHATSNEW` | HEAD | |
| github.com/libuv/libuv `src/unix/stream.c` | v1.x | macOS 上 /dev 设备的 kqueue 问题 |
| github.com/tomlm/Porta.Pty | `0186b60` | |
| github.com/microsoft/vs-pty.net | `8351527` | |
| github.com/aaasoft/Quick.PtyNet | `304c9bb` | |
| github.com/guitarrapc/MiniPty | `7460059` | |
| github.com/royalapplications/RoyalTerminal | `01833b8` | |
| github.com/ghostflyby/Pty.Net | `9dc4a8f` | |
| github.com/Devolutions/devolutions-terminal | `be7ad21` | |
| zed-industries/zed 提交 `8af3f58`（"Better conpty #39090"） | — | |

### 文档与元数据

- https://learn.microsoft.com/windows/console/createpseudoconsole
- https://learn.microsoft.com/windows/console/closepseudoconsole （24H2 行为说明，2025-07 更新）
- https://learn.microsoft.com/windows/console/releasepseudoconsole （26100+）
- https://learn.microsoft.com/windows/console/creating-a-pseudoconsole-session （2025-08 更新）
- https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute
- https://learn.microsoft.com/dotnet/core/deploying/native-aot/interop （DirectPInvoke / NativeLibrary）
- https://www.nuget.org/packages/Microsoft.Windows.Console.ConPTY （api.nuget.org registration；1.25.260930003 nupkg 已解包核实）
- https://www.nuget.org/packages/Porta.Pty 、/Quick.PtyNet 、/MiniPty 、/RoyalApps.RoyalTerminal.Terminal.Pty.Unix 、/Ghostflyby.Pty （NuGet 搜索与 registration API）
- https://registry.npmjs.org/node-pty ；https://crates.io/api/v1/crates/portable-pty
- https://code.visualstudio.com/updates/v1_109 （移除 winpty、windowsUseConptyDll）
- https://devblogs.microsoft.com/commandline/windows-terminal-preview-1-22-release （ConPTY 重写）
- https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md （RHEL 8、glibc 2.27 / musl 1.2.3 基线）
- man7.org：pidfd_open(2)（Linux 5.3）、waitid(2)（P_PIDFD 需 5.4）
- 本机 macOS SDK 头文件：`sys/spawn.h`、`spawn.h`、`_stdlib.h`、`sys/termios.h`、`sys/ttycom.h`、`sys/event.h`、`sys/proc_info.h`、`libproc.h`、`sys/sysctl.h`；man 页 poll(2)、kqueue(2)

### 本机实测（macOS 26.6.2，.NET SDK 10.0.301）

- 用 `dotnet run` 运行单文件脚本，以 `Process.Start` 启动 `/usr/bin/perl -e 'print $SIG{PIPE}'`，输出 `IGNORE`，证明 .NET 的子进程继承了 `SIGPIPE=SIG_IGN`（§1.4、U3）。
