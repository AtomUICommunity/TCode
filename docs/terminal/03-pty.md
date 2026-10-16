# TCode 终端 03：PTY 层（TCode.Terminal.Pty + native/tcodepty）

> 状态：详细设计 v1（2026-10-16）。上游：[01-architecture.md](01-architecture.md)（D1–D9、§5 线程模型、§6.2 接口）、[research/02-pty.md](research/02-pty.md)、[research/05](research/05-platform-behavior-config.md) §1/§2/§8、[research/06](research/06-ssh-transport.md) §6。
> 本文写到"照着就能实现"的粒度：C 头文件、helper 源码、C# 互操作声明、线程与关闭状态机、构建命令、测试与 spike 清单。

**标注约定**（沿用 research/02）

- **[实]**：已在源码、官方文档或本机实测中核实；源码引用写成 `前缀 路径:行号`，前缀与提交号见 research/02 §0，本文新增的见下表。
- **[推]**：推断，实现前需要实测，并已列入 §15 spike 清单。

| 前缀 | 对象 | 版本 |
|---|---|---|
| LOGIN | apple-oss-distributions/system_cmds `login/login.c` | `408bba7` |
| XNU | apple-oss-distributions/xnu `bsd/kern/kern_event.c` | 本地快照 xnu-12377.1.9 |
| GH | ghostty `src/termio/Exec.zig`、`src/terminal/modes.zig` | `b3dc3d7` |
| WT | microsoft/terminal `src/winconpty/winconpty.cpp`、`src/inc/conpty-static.h`、`src/host/*`；`src/terminal/adapter/adaptDispatch.cpp`（main，2026-10-16 通过 GitHub 读取） | `44b440b` |
| MP | MiniPty `src/MiniPty/Native/minipty_unix_exit.c`、`scripts/build-native.sh` | `7460059` |
| PP | Porta.Pty `src/Porta.Pty/build/Porta.Pty.targets`、`ConPtyImportResolver.cs` | `0186b60` |
| RT | dotnet/runtime `src/native/libs/System.Native/pal_process.c`、`pal_signal.c` | `bc110ba` |
| local | 本机 macOS 26.6.2，bash 3.2.57，zsh | 2026-10-16 |

---

## 0. 结论摘要

1. **Unix**：C# 一次 `LibraryImport` 调用进入 `tcpty_spawn`。它在 C 里完成 `/dev/ptmx` → termios(IUTF8) → winsize → `posix_spawn(helper)`，参数为 `SETSID | SETSIGDEF(全集) | SETSIGMASK(空)`，macOS 另加 `CLOEXEC_DEFAULT`。helper 依次执行 `TIOCSCTTY`、close_range、chdir、`execvp`，exec 之前出错就通过 fd 3 的状态管道报告。**Linux 也走 helper**：我们的 file action 是 `adddup2`（dup 不会自动取得控制终端），而且 RHEL 8 的 glibc 2.28 没有 `addchdir_np`。
2. **读**：每个会话一个专用读线程，调用 `tcpty_read_gather`（Ghostty 的算法：第一次 EAGAIN 就交付；只有凑满 ≥1 KiB 时才自旋 16 次、poll 1 ms，单批总预算 3 ms，上限 64 KiB）→ `PipeWriter`，`pauseWriterThreshold = 256 KiB` 就是 01 §5.1 说的"4 块 × 64 KiB"。**写**：每个会话一个专用写线程，按 4 KiB 分块；遇到 EAGAIN 就 poll `POLLOUT` 和 wake fd。
3. **退出**：整个进程一个退出监视线程（Linux 用 pidfd + epoll，macOS 用 kqueue），只调用 `waitpid(pid)`；pidfd 不可用时，每个会话一个阻塞 `waitpid` 的线程。Windows 用 `ThreadPool.RegisterWaitForSingleObject`。**`Exited` 要等输出排空（EOF，或静默 100 ms，最长 1 s）之后才完成。**
4. **关闭**：Unix 依次 SIGHUP 前台进程组和 shell 进程组 → 关闭 master（内核挂断）→ 2 s 后 SIGKILL。Windows 在专用线程上调用 `ClosePseudoConsole`，读线程同时切到"丢弃模式"继续读，读到 `ERROR_BROKEN_PIPE` 为止，超时后 `TerminateProcess`。
5. **Windows**：按绝对路径加载随包的 `conpty.dll`，`OpenConsole.exe` 放在 `x64\`、`arm64\` 子目录；加载失败时回退 kernel32 并记录"降级"。函数指针用 `delegate* unmanaged`。读线程在 `CreatePseudoConsole` 之后、`CreateProcessW` 之前启动，DA1 由镜像核心应答。每个会话一个 Job，标志为 `KILL_ON_JOB_CLOSE | BREAKAWAY_OK`；正常关闭时先清除 `KILL_ON_JOB_CLOSE` 再关闭 Job 句柄，所以 Job 只在崩溃时兜底。
6. **新发现**（详见 §16）：
   - macOS 的 `login` 父进程等到 shell 结束后**总是 `exit(0)`** [实 LOGIN login.c:854-883]，shell 的退出码丢失；因此 2026-10-17 决定不经 login 包装，改用 argv0 `-zsh`（§8.3）；
   - ConPTY 把 mode 2027 当作"永久"模式，设置无效 [实 WT adaptDispatch.cpp:1891-1892、2032-2033]，宽度模式在创建时就固定了；
   - libghostty-vt 的模式表里没有 9001 [实 GH modes.zig]；
   - research/02 建议的 `trap - PIPE` **对 bash 无效** [实 local]；
   - `PR_SET_PDEATHSIG` 跟随的是"父线程"，所以不采用。

---

## 1. 项目结构与文件清单

```
native/tcodepty/
  include/tcodepty.h            C API（§2），C# 侧逐字镜像
  src/tcpty_spawn.c             init、spawn、termios、winsize、状态管道协议          ~220 行
  src/tcpty_io.c                read_gather、read、write、wait、wake、set_nonblock、close、QoS   ~150 行
  src/tcpty_exit.c              exitwatch（epoll+pidfd / kqueue）、reap、信号映射   ~170 行
  src/tcpty_proc.c              前台进程组、进程名/路径、cwd、子进程、termios 状态   ~150 行
  src/tcpty_internal.h          错误码映射、fd 提升（F_DUPFD_CLOEXEC ≥ 10）
  helper/tcode-pty-spawn.c      helper 可执行文件（§3）：PTY / --exec / --detach 三种模式   ~85 行
  build.sh                      ./build.sh <rid> [--static]（§11）
  check-glibc.sh                检查 .so 依赖的最高 GLIBC_ 符号版本 ≤ 2.28
src/TCode.Terminal.Pty/
  TCode.Terminal.Pty.csproj     IsAotCompatible、AllowUnsafeBlocks、DisableRuntimeMarshalling
  ITerminalConnection.cs        接口与值类型（§4.1）
  PtyLaunch.cs                  PtyLaunch、PtyWidthMode、PtyLaunchHints
  LocalPty.cs                   工厂：LocalPty.Start(PtyLaunch, LocalPtyOptions)
  LocalPtyConnection.cs         与平台无关的部分：Pipe、读写线程、Exited、关闭状态机（§5）
  LocalPtyOptions.cs、PtyException.cs、PtyDiagnostics.cs
  Unix/TcPtyNative.cs           LibraryImport 声明（§4.2）
  Unix/TcPtyStructs.cs          blittable 结构体
  Unix/PtyFdHandle.cs           SafeHandle（master fd、wake fd）
  Unix/UnixPtyBackend.cs        spawn 编组、gather 读、写、resize、信号、关闭
  Unix/UnixExitWatcher.cs       进程级退出监视 + waitpid 回退线程
  Unix/UnixProcessInfo.cs       前台进程、cwd（§8）
  Unix/NativeStringBlock.cs     argv / envp 编码为 UTF-8 的 char**
  Unix/HelperLocator.cs
  Windows/Win32.cs              kernel32 LibraryImport（§7.6）
  Windows/ConPtyApi.cs          conpty.dll 加载与函数指针（§7.1）
  Windows/PseudoConsoleHandle.cs
  Windows/ConPtyBackend.cs      调用序列（§7.3）
  Windows/WindowsJob.cs、Windows/EnvironmentBlock.cs、Windows/CommandLine.cs
  Windows/ProcessTree.cs        Toolhelp32（§8.2）
tests/TCode.Terminal.Pty.Tests/ §14
```

- libtcodepty 实际约 690 行，比 01 估计的约 300 行多。多出来的是 exitwatch、proc 查询和错误映射，所有平台差异都集中在这里。
- Windows 没有我们自己的原生代码 [实 research/02 §5.4]。

---

## 2. libtcodepty C API（头文件草案）

约定（对所有函数都成立）：

| 项 | 约定 |
|---|---|
| 返回值 | `int32_t`：`>= 0` 表示成功（含义见各函数说明）；`< 0` 是 **可移植错误码** `TCPTY_E_*`。errno 在 Linux 和 macOS 上取值不同（例如 EAGAIN 分别是 11 和 35），所以 C# 只根据可移植码分支。原始 errno 用 `tcpty_last_errno()`（线程局部）取，仅用于日志 |
| EINTR | 全部在 C 里重试，绝不返回给调用方 |
| fd 所有权 | 返回给调用方的 fd 都带 `FD_CLOEXEC`，并已提升到 ≥ 10（防止与 0–3 冲突，见 §2.2），由调用方负责关闭（`tcpty_close`） |
| 输入字符串 / 数组 | 借用，只在调用期间有效；编码为 UTF-8，以 NUL 结尾 |
| 输出字符串 | 写入调用方提供的缓冲区，**返回完整长度**（不含 NUL），语义同 `snprintf`：返回值 ≥ cap 表示被截断 |
| 结构体 | 首字段 `uint32_t size` = `sizeof`，用于以后扩展；C# 填 `sizeof(T)` |
| 线程安全 | 全部可重入；`tcpty_spawn` 内部在 fd 创建到 posix_spawn 这一段持有全局互斥锁（macOS 并发 openpt 会以 -ENXIO 失败 [实 research/02 §2.3、U7]） |
| ABI 版本 | `tcpty_version()` 返回 `major << 16 \| minor`。动态加载时 C# 检查 major 是否相等；静态链接（DirectPInvoke）时不会不一致 |

```c
/* tcodepty.h — libtcodepty C API v1.0
 * 只在 linux-{x64,arm64}、osx-{x64,arm64} 上构建；Windows 不使用。 */
#ifndef TCODEPTY_H
#define TCODEPTY_H
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
#define TCPTY_API __attribute__((visibility("default")))
#define TCPTY_ABI_MAJOR 1
#define TCPTY_ABI_MINOR 0

/* ───────── 错误码（可移植，负值） ───────── */
enum {
  TCPTY_OK         = 0,
  TCPTY_E_AGAIN    = -1,   /* EAGAIN / EWOULDBLOCK */
  TCPTY_E_IO       = -2,   /* EIO / EPIPE / ECONNRESET：对端已消失 */
  TCPTY_E_PERM     = -3,   /* EPERM / EACCES */
  TCPTY_E_NOENT    = -4,
  TCPTY_E_SRCH     = -5,   /* 进程不存在 */
  TCPTY_E_CHILD    = -6,   /* 没有这个子进程（已被别人回收）→ 退出码未知 */
  TCPTY_E_NOSYS    = -7,   /* ENOSYS / ENOTSUP：内核或 seccomp 不支持 */
  TCPTY_E_INVAL    = -8,
  TCPTY_E_NOMEM    = -9,
  TCPTY_E_BADF     = -10,
  TCPTY_E_NXIO     = -11,  /* pty 用尽或 macOS 并发竞争（已重试过） */
  TCPTY_E_TIMEDOUT = -12,
  TCPTY_E_MFILE    = -13,  /* EMFILE / ENFILE */
  TCPTY_E_NOTTY    = -14,
  TCPTY_E_OTHER    = -99
};
TCPTY_API int32_t tcpty_version(void);
TCPTY_API int32_t tcpty_last_errno(void);                       /* 线程局部，最近一次失败的原始 errno */
TCPTY_API int32_t tcpty_strerror(int32_t raw_errno, char *buf, int32_t cap);

/* ───────── 进程级初始化（幂等；pty host 启动时调用一次） ───────── */
typedef struct tcpty_init_info {
  uint32_t size;
  int32_t  pidfd_supported;      /* Linux：pidfd_open(getpid()) 探测成功；macOS 恒为 0 */
  int32_t  sigchld_was_ignored;  /* 进入时 SIGCHLD 为 SIG_IGN 或带 SA_NOCLDWAIT；已复位为 SIG_DFL（见 §10） */
  int32_t  stdio_repaired;       /* fd 0–2 中有空位，已用 /dev/null 补上 */
} tcpty_init_info;
TCPTY_API int32_t tcpty_init(tcpty_init_info *info);

/* ───────── 启动 ───────── */
enum {                       /* tcpty_spawn_options.flags */
  TCPTY_SPAWN_IUTF8 = 1u << 0  /* 在系统默认 termios 上加 IUTF8（默认应当置位） */
};
enum {                       /* 失败阶段：result.stage */
  TCPTY_STAGE_NONE = 0,
  TCPTY_STAGE_OPENPT = 1, TCPTY_STAGE_GRANT = 2, TCPTY_STAGE_SLAVE = 3,
  TCPTY_STAGE_TERMIOS = 4, TCPTY_STAGE_WINSIZE = 5, TCPTY_STAGE_PIPE = 6,
  TCPTY_STAGE_ATTR = 7, TCPTY_STAGE_SPAWN = 8,          /* posix_spawn(helper) 本身失败，例如 helper 缺失 */
  TCPTY_STAGE_HELPER_ARGS = 101, TCPTY_STAGE_HELPER_CTTY = 102,
  TCPTY_STAGE_HELPER_EXEC = 103,                        /* 目标程序 exec 失败（ENOENT / EACCES …） */
  TCPTY_STAGE_HELPER_PROTO = 104                        /* 状态管道协议错误或超时 */
};
enum { TCPTY_WARN_CWD_FALLBACK = 1u << 0 };            /* cwd 不可用，已回退到 $HOME 或 / */

typedef struct tcpty_spawn_options {
  uint32_t size;
  uint32_t flags;
  const char *helper_path;      /* 必填，tcode-pty-spawn 的绝对路径 */
  const char *file;             /* 目标程序；含 '/' 时直接 exec，否则按 envp 中的 PATH 查找（execvp 规则） */
  const char *const *argv;      /* 以 NULL 结尾；argv[0] 就是 argv0（例如 "-zsh"） */
  const char *const *envp;      /* 以 NULL 结尾的 "K=V" 数组，作为子进程的完整环境 */
  const char *cwd;              /* NULL 或 "" 表示继承 host 的 cwd（不推荐） */
  uint16_t cols, rows;          /* 字符尺寸，必须 > 0 */
  uint16_t xpixel, ypixel;      /* 整个窗口的像素尺寸，未知时为 0 */
  uint32_t status_timeout_ms;   /* 等待 helper 报告的上限，0 表示默认 5000 */
} tcpty_spawn_options;

typedef struct tcpty_spawn_result {
  uint32_t size;
  int32_t  master_fd;           /* 成功时 ≥ 10，带 CLOEXEC，处于阻塞模式（由调用方 set_nonblock） */
  int32_t  pid;                 /* 直接子进程，即 shell */
  int32_t  stage;               /* TCPTY_STAGE_*，成功时为 0 */
  int32_t  error;               /* 失败时的原始 errno（helper 报告的值也放在这里） */
  uint32_t warnings;            /* TCPTY_WARN_* */
  int32_t  warn_error;          /* 产生警告时的原始 errno */
} tcpty_spawn_result;

/* 返回 0 或 TCPTY_E_*。失败时 master 和 pid 都已经回收，调用方不用清理。 */
TCPTY_API int32_t tcpty_spawn(const tcpty_spawn_options *opt, tcpty_spawn_result *res);

/* ───────── I/O ───────── */
TCPTY_API int32_t tcpty_set_nonblock(int32_t fd, int32_t enable);
TCPTY_API int32_t tcpty_close(int32_t fd);
/* wake：每个会话一对 pipe。read 端交给 gather/wait；被唤醒后不排空（"粘性"），之后的等待都会立即返回 */
TCPTY_API int32_t tcpty_wake_create(int32_t *read_fd, int32_t *write_fd);
TCPTY_API int32_t tcpty_wake_signal(int32_t write_fd);

typedef struct tcpty_gather_config {
  uint32_t size;
  int32_t bridge_threshold;     /* 1024：攒够这么多才认为流已"饱和" */
  int32_t spin_max;             /* 16：饱和后遇到 EAGAIN 立即重读的次数 */
  int32_t bridge_poll_ms;       /* 1 */
  int32_t budget_us;            /* 3000：单批总时间预算 */
} tcpty_gather_config;
enum { TCPTY_GATHER_EOF = 1u << 0, TCPTY_GATHER_WOKEN = 1u << 1 };
/* 阻塞直到有数据、EOF 或被唤醒。返回读到的字节数（≥ 0）；*flags_out 是 EOF 和 WOKEN 的组合。
 * EIO（Linux：最后一个 slave 已关闭）与 read()==0（macOS）都报告为 EOF。fd 必须是非阻塞的。 */
TCPTY_API int32_t tcpty_read_gather(int32_t fd, int32_t wake_fd, uint8_t *buf, int32_t cap,
                                    const tcpty_gather_config *cfg, uint32_t *flags_out);
TCPTY_API int32_t tcpty_read(int32_t fd, uint8_t *buf, int32_t cap);         /* 单次读：>0、0=EOF、E_AGAIN */
TCPTY_API int32_t tcpty_write(int32_t fd, const uint8_t *buf, int32_t len);  /* >=0 已写字节数、E_AGAIN、E_IO */
enum { TCPTY_POLL_IN = 1, TCPTY_POLL_OUT = 2, TCPTY_POLL_HUP = 4, TCPTY_POLL_WOKEN = 8 };
/* 返回 1 表示就绪，0 表示超时；*revents 是上面各位的组合 */
TCPTY_API int32_t tcpty_wait(int32_t fd, int32_t wake_fd, int32_t events, int32_t timeout_ms, uint32_t *revents);
TCPTY_API int32_t tcpty_resize(int32_t fd, uint16_t cols, uint16_t rows, uint16_t xpixel, uint16_t ypixel);
TCPTY_API int32_t tcpty_get_winsize(int32_t fd, uint16_t out[4]);           /* 测试与诊断用 */
TCPTY_API void    tcpty_set_thread_qos_interactive(void);  /* macOS：QOS_CLASS_USER_INITIATED；其他平台为空操作 */

/* ───────── 退出 ───────── */
enum { TCPTY_EXIT_UNKNOWN = 0, TCPTY_EXIT_CODE = 1, TCPTY_EXIT_SIGNAL = 2 };
typedef struct tcpty_exit_status {
  uint32_t size;
  int32_t kind;                 /* TCPTY_EXIT_* */
  int32_t value;                /* 退出码，或信号编号（平台原值） */
  int32_t core_dumped;
  int32_t raw;                  /* 原始 wait status */
} tcpty_exit_status;
/* 只调用 waitpid(pid)。block=0 时加 WNOHANG。返回 1 表示已回收，0 表示尚未退出；
 * 返回 TCPTY_E_CHILD 时 out->kind = UNKNOWN */
TCPTY_API int32_t tcpty_reap(int32_t pid, int32_t block, tcpty_exit_status *out);

/* 进程级退出监视：Linux 用 epoll + pidfd（EPOLLONESHOT），macOS 用 kqueue EVFILT_PROC */
TCPTY_API int32_t tcpty_exitwatch_init(void);
/* 已删除：TCPTY_WATCH_EXITSTATUS（原用于 login 包装下的孙进程；2026-10-17 决定不经 login，见 §8.3） */
/* 返回 0；TCPTY_E_SRCH 表示进程已经退出（调用方立即 reap）；TCPTY_E_NOSYS 或 TCPTY_E_PERM 表示
 * 不支持（调用方对这个 pid 改用阻塞 waitpid 线程） */
TCPTY_API int32_t tcpty_exitwatch_add(int32_t pid, uint32_t flags);
typedef struct tcpty_exit_event { int32_t pid; int32_t has_status; int32_t status_raw; } tcpty_exit_event;
/* 返回事件个数（≥ 0）。被 wake 唤醒时返回 0；timeout_ms 为 -1 表示无限等待 */
TCPTY_API int32_t tcpty_exitwatch_wait(tcpty_exit_event *events, int32_t cap, int32_t timeout_ms);
TCPTY_API int32_t tcpty_exitwatch_wake(void);
TCPTY_API int32_t tcpty_decode_wait_status(int32_t raw, tcpty_exit_status *out);

/* ───────── 信号 ───────── */
enum { TCPTY_SIG_HUP = 1, TCPTY_SIG_INT = 2, TCPTY_SIG_QUIT = 3, TCPTY_SIG_KILL = 4, TCPTY_SIG_TERM = 5,
       TCPTY_SIG_CONT = 6, TCPTY_SIG_STOP = 7, TCPTY_SIG_TSTP = 8, TCPTY_SIG_WINCH = 9,
       TCPTY_SIG_USR1 = 10, TCPTY_SIG_USR2 = 11 };
TCPTY_API int32_t tcpty_signal_number(int32_t portable);                        /* → 平台信号编号 */
TCPTY_API int32_t tcpty_signal_name(int32_t signo, char *buf, int32_t cap);     /* "SIGHUP"…；未知时为 "SIG<n>" */
TCPTY_API int32_t tcpty_kill(int32_t pid, int32_t signo);                       /* kill(pid, s) */
TCPTY_API int32_t tcpty_killpg(int32_t pgid, int32_t signo);                    /* kill(-pgid, s)；pgid ≤ 1 时返回 E_INVAL */

/* ───────── 进程信息 ───────── */
TCPTY_API int32_t tcpty_get_fg_pgrp(int32_t master_fd);      /* tcgetpgrp(master)，返回 > 0 或错误码 */
TCPTY_API int32_t tcpty_get_pgid(int32_t pid);
TCPTY_API int32_t tcpty_get_sid(int32_t pid);
/* Linux：/proc/<pid>/comm（15 字符）；macOS：proc_pidpath 的 basename，失败时用 proc_name */
TCPTY_API int32_t tcpty_get_process_name(int32_t pid, char *buf, int32_t cap);
TCPTY_API int32_t tcpty_get_process_path(int32_t pid, char *buf, int32_t cap);
/* Linux：readlink /proc/<pid>/cwd；macOS：proc_pidinfo(PROC_PIDVNODEPATHINFO).pvi_cdir */
TCPTY_API int32_t tcpty_get_cwd(int32_t pid, char *buf, int32_t cap);
/* macOS：proc_listchildpids；Linux：/proc/<pid>/task/<pid>/children，没有时扫描 /proc 下的 stat。返回个数 */
TCPTY_API int32_t tcpty_list_children(int32_t pid, int32_t *pids, int32_t cap);
enum { TCPTY_TERMIOS_ECHO = 1, TCPTY_TERMIOS_ICANON = 2, TCPTY_TERMIOS_ISIG = 4 };
TCPTY_API int32_t tcpty_get_termios_state(int32_t fd, uint32_t *flags);   /* 密码提示判断（09 Agent 用） */

#ifdef __cplusplus
}
#endif
#endif
```

### 2.1 为什么 Unix C API 没有"宽度模式"参数

内核 PTY 不测量字宽，Unicode 宽度只影响模拟器和 ConPTY 自己的缓冲区。Unix 上唯一相关的设置是 IUTF8：canonical 模式下按 UTF-8 字符退格。所以 `PtyLaunch.WidthMode` 只在 Windows 上映射为 ConPTY flags（§7.4），在 Unix 上忽略。

### 2.2 `tcpty_spawn` 实现要点（父进程侧）

```c
int32_t tcpty_spawn(const tcpty_spawn_options *o, tcpty_spawn_result *r) {
  lock(&g_spawn_mutex);
  /* 1. master：直接 open /dev/ptmx（等价于 posix_openpt），一开始就带 O_CLOEXEC，避免与其他线程的 fork 竞争
   *    macOS 上 errno<=0 或 ENXIO 时最多重试 8 次，每次间隔 1 ms [实 research/02 §2.3] */
  m = open("/dev/ptmx", O_RDWR | O_NOCTTY | O_CLOEXEC); grantpt(m); unlockpt(m);
  /* slave 路径：macOS 用 ioctl(m, TIOCPTYGNAME, buf)（线程安全，NP pty.cc:788），Linux 用 ptsname_r */
  s = open(path, O_RDWR | O_NOCTTY | O_CLOEXEC);
  m = fd_high(m); s = fd_high(s);   /* fcntl(F_DUPFD_CLOEXEC, 10) 后关闭原 fd，见下文 */
  /* 2. termios：tcgetattr(s) 取得系统默认值，只加 IUTF8 [实 research/02 §2.6]；TIOCSWINSZ(s, …) */
  /* 3. 状态管道：Linux 用 pipe2(O_CLOEXEC)，macOS 用 pipe + FD_CLOEXEC（仍在锁内），再提升到 ≥ 10 */
  /* 4. attr：SETSID | SETSIGDEF | SETSIGMASK (+ macOS 的 CLOEXEC_DEFAULT)；sigdefault = sigfillset，sigmask = 空 */
  /* 5. file actions：adddup2(s,0) adddup2(s,1) adddup2(s,2) adddup2(pw,3) */
  /* 6. argv_h = [helper, cwd ?: "", file, argv[0..]] ；posix_spawn(&pid, helper, &fa, &attr, argv_h, envp)，EINTR 时重试 */
  unlock(&g_spawn_mutex);
  close(pw); close(s);                       /* 父进程不关 slave 就永远读不到 EOF [实 U5] */
  /* 7. 读状态管道，直到 EOF（helper exec 成功后 fd 3 因 CLOEXEC 被关闭），用 poll 限时：
   *    每条记录 {int32 stage, int32 err}：stage < 0 是警告，记下后继续读；stage > 0 是致命错误 → waitpid(pid,0)，返回错误
   *    超时 → kill(pid, SIGKILL) + waitpid → 返回 STAGE_HELPER_PROTO / E_TIMEDOUT */
  r->master_fd = m; r->pid = pid; return TCPTY_OK;
}
```

- **fd 提升到 ≥ 10**：如果 status 管道的写端恰好就是 fd 3，`adddup2(3, 3)` 在某些 libc 上不会清除 CLOEXEC，helper 一 exec 就失去 fd 3 [推，POSIX 2017 才要求清除；未逐一核对 glibc 2.28 和 XNU]。pty host 以分离方式启动、stdio 可能被关闭时，`/dev/ptmx` 也可能拿到 0–2。统一提升到 ≥ 10，就不必关心这些差异。
- **CLOEXEC 竞争**：macOS 没有 `pipe2`。从 `pipe` 到 `fcntl` 之间，其他线程如果 fork，会继承这对 fd。pty host 内基本不用 `Process`，所以只记作风险 [推]。
- `ptsname_r` 在 glibc 2.28 和 musl 中都有。posix_spawn 的 `SETSID` 需要 glibc ≥ 2.26、musl ≥ 1.1.17、macOS 的 0x0400 [实 research/02 §2.2]。

---

### 2.3 管道模式启动：`tcpty_spawn_pipe`（Unix）

pty host 在 Unix 上不能用 `Process`（§10），但 profile 探测和 SSH 需要运行一些短命令（`ssh -G <host>`、读取 shell 版本等），并收集它们的输出。为此 libtcodepty 提供一个不分配 PTY 的启动接口，子进程的退出同样由 §6 的退出监视处理（只 `waitpid(pid)`）：

```c
typedef struct tcpty_pipe_options {
    uint32_t size;                 /* sized-struct */
    const char *helper_path;       /* tcode-pty-spawn，以 --exec 模式启动：复位全部信号后 execvp */
    const char *file;              /* 按 PATH 查找规则解析 */
    const char *const *argv;       /* argv[0] 是 argv0 */
    const char *const *envp;
    const char *cwd;               /* 可为 NULL */
} tcpty_pipe_options;

typedef struct tcpty_pipe_result {
    int32_t pid;
    int32_t stdin_fd;              /* 均为 O_CLOEXEC；调用方负责关闭 */
    int32_t stdout_fd;
    int32_t stderr_fd;
} tcpty_pipe_result;

TCPTY_API int32_t tcpty_spawn_pipe(const tcpty_pipe_options *opt, tcpty_pipe_result *res);
```

C# 侧包装为 `PipeProcess.RunAsync(file, args, env, cwd, timeout, maxOutput)`，超时后 `killpg`。Windows 没有这个限制，直接使用 `System.Diagnostics.Process`。

## 3. tcode-pty-spawn helper（完整草案）

三种模式：

| 模式 | 调用方 | 动作 |
|---|---|---|
| **PTY 模式** | `tcpty_spawn`（pty host 内） | TIOCSCTTY → 关闭 fd ≥ 4 → chdir → execvp，通过 fd 3 报告错误 |
| **`--exec`** | BashTool（AgentHost，经 `Process.Start`） | 把全部信号恢复为默认、清空掩码 → execvp（§12） |
| **`--detach`** | Desktop 启动 pty host（[04-pty-host.md](04-pty-host.md) §1） | `setsid` → 恢复信号、清空掩码 → 可选地把就绪管道 dup 到 fd 1 → 关闭 fd ≥ 3 → `execv(host)` |

`--detach` 的参数格式按 04 §1：`tcode-pty-spawn --detach [--stdout-fd N] -- <host 绝对路径> [args...]`。

- `--stdout-fd N`：就绪管道所在的 fd。N ≠ 1 时先 `dup2(N, 1)`，这样它才能在"关闭 fd ≥ 3"之后保留下来。Desktop 用 `Process.Start` 重定向 stdout 时传 1 即可。
- 用 `execv`，不做 PATH 查找，host 路径必须是绝对路径。
- 失败时没有状态管道，helper 向 stderr 写一行 `tcode-pty-spawn: <stage>: <strerror>`，并以 **126**（setsid / 参数错误）或 **127**（exec 失败）退出。这两个码与 04 §1 中 host 自身的退出码 0/2/3/4 不冲突，Desktop 据此区分"helper 失败"与"host 失败"。
- 由 `Process.Start` 启动的 helper 不是进程组组长（.NET 不会 setpgid），所以 `setsid()` 必定成功。失败（EPERM）也按 126 退出，不重试 fork [推]。

```c
/* tcode-pty-spawn：posix_spawn / Process.Start 之后、目标程序之前的一跳，exec 之后不运行任何托管代码。
 * PTY 模式：argv = [self, cwd, file, argv0, args...]；fd 0-2 = slave；fd 3 = 状态管道写端。
 * exec 模式：argv = [self, "--exec", file, argv0, args...]；复位全部信号后 execvp。
 * detach 模式：argv = [self, "--detach", ["--stdout-fd", N], "--", host, args...]；setsid、复位信号、关闭 fd≥3 后 execv。
 * 状态记录 {int32 stage, int32 err}：stage > 0 致命（随后 _exit(127)）；stage < 0 是警告，继续执行。
 * stage 编号与 tcodepty.h 的 TCPTY_STAGE_HELPER_* 一致：101 参数，102 控制终端，103 exec；-1 表示 cwd 回退。 */
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <unistd.h>
#if defined(__linux__)
#include <sys/syscall.h>
#endif

static void report(int32_t stage, int32_t err) {
    int32_t rec[2] = { stage, err };
    while (write(3, rec, sizeof rec) < 0 && errno == EINTR) {}
    if (stage > 0) _exit(127);
}

static void close_from(int low) {            /* 兜底：防止宿主中有不带 CLOEXEC 的 fd 泄漏给子进程 */
#if defined(__linux__) && defined(SYS_close_range)
    if (syscall(SYS_close_range, low, ~0U, 0) == 0) return;          /* Linux ≥ 5.9 */
#endif
#if defined(__linux__)
    DIR *d = opendir("/proc/self/fd");
#else
    DIR *d = opendir("/dev/fd");             /* macOS：PTY 模式已有 CLOEXEC_DEFAULT，detach 模式需要遍历 */
#endif
    if (d == NULL) return;
    for (struct dirent *e; (e = readdir(d)) != NULL;) {
        int fd = atoi(e->d_name);
        if (fd >= low && fd != dirfd(d)) close(fd);
    }
    closedir(d);
}

static void reset_signals(void) {
    struct sigaction dfl; memset(&dfl, 0, sizeof dfl); dfl.sa_handler = SIG_DFL;
    for (int s = 1; s < NSIG; s++) sigaction(s, &dfl, NULL);           /* SIGKILL/SIGSTOP 失败可忽略 */
    sigset_t none; sigemptyset(&none); sigprocmask(SIG_SETMASK, &none, NULL);
}

static void die(int code, const char *stage) {                          /* exec / detach 模式：没有状态管道 */
    dprintf(2, "tcode-pty-spawn: %s: %s\n", stage, strerror(errno));
    _exit(code);
}

int main(int argc, char **argv) {
    if (argc >= 4 && strcmp(argv[1], "--exec") == 0) {
        reset_signals();
        execvp(argv[2], &argv[3]);
        die(127, "exec");
    }
    if (argc >= 2 && strcmp(argv[1], "--detach") == 0) {
        int i = 2;
        if (i + 1 < argc && strcmp(argv[i], "--stdout-fd") == 0) {
            int n = atoi(argv[i + 1]);
            if (n != 1 && dup2(n, 1) == -1) die(126, "dup2");
            i += 2;
        }
        if (i >= argc || strcmp(argv[i], "--") != 0 || i + 1 >= argc) { errno = EINVAL; die(126, "args"); }
        if (setsid() == -1) die(126, "setsid");                         /* 新会话、无控制终端 */
        reset_signals();
        close_from(3);
        execv(argv[i + 1], &argv[i + 1]);                               /* host 绝对路径；argv0 = 路径本身 */
        die(127, "exec");
    }
    if (fcntl(3, F_SETFD, FD_CLOEXEC) == -1) _exit(127);              /* 没有状态管道就无法报告 */
    if (argc < 5) report(101, EINVAL);
    if (ioctl(STDIN_FILENO, TIOCSCTTY, 0) == -1) report(102, errno);  /* Linux 与 macOS 都需要（adddup2 不会取得控制终端） */
    close_from(4);
    const char *cwd = argv[1];
    if (cwd[0] != '\0' && chdir(cwd) == -1) {
        report(-1, errno);                                            /* 与 Ghostty 一致：回退而不是失败 */
        const char *home = getenv("HOME");
        if (home == NULL || chdir(home) == -1) (void)chdir("/");
    }
    execvp(argv[2], &argv[3]);                                        /* 按子进程环境中的 PATH 查找；ENOEXEC 时回退 /bin/sh */
    report(103, errno);
    return 127;
}
```

| 设计点 | 说明 | 来源 |
|---|---|---|
| 为什么 Linux 也要 `TIOCSCTTY` | research/02 §2.2 的结论"Linux 不需要 helper"前提是 `addopen(0, slave_path)`；我们用的是 `adddup2(已打开的 O_NOCTTY slave)`，dup 不会让会话首进程取得控制终端，所以必须显式 TIOCSCTTY。posix_spawn 已经执行了 setsid，helper 是会话首进程，调用会成功 | [实 LX tty_jobctrl.c:365-395、research/02 §2.2]；[推] Linux 上会成功，见 S2 |
| fd 3 在 exec 前设为 CLOEXEC | exec 成功即关闭 fd 3，父进程读到 EOF 就表示成功 | [实 RTM spawn-helper.c:14] |
| `execvp` 而不是 `execve` + 自己找路径 | helper 的 environ 就是子进程的完整环境；PATH 查找和 ENOEXEC 回退由 libc 完成 | [实 RTM spawn-helper.c:19-21] |
| **不用 `PR_SET_PDEATHSIG`** | 它在**父线程**退出时就触发，而 spawn 可能在任意线程上执行；pty host 退出时 master 被关闭，内核会挂断会话，已经能兜底 | man 2 PR_SET_PDEATHSIG [实]；与 research/02 §2.4 的可选建议相反 |
| 信号 | PTY 模式由父进程的 `SETSIGDEF(全集)` 和空 mask 处理；`--exec`、`--detach` 自己复位（它们是被 .NET `Process.Start` 启动的，SIGPIPE 继承为 SIG_IGN） | §10、§12 |
| `--detach` 的 fd 清理 | Linux 用 `close_range`，没有时遍历 `/proc/self/fd`；macOS 遍历 `/dev/fd`（没有 CLOEXEC_DEFAULT 可用）。fd 0–2 保留，由 host 在就绪后自己重定向到 /dev/null（04 §1） | 04 §1 |
| `--detach` 不 fork | `Process.Start` 的子进程不是组长，`setsid` 一定成功；不 fork 的话，Desktop 拿到的 pid 就是 host 的 pid，并且 Desktop 必须回收它（`Process` 会自动回收），否则会留下僵尸 | [推]；host 退出后 Desktop 的 `Process` 对象负责回收 |
| 静态链接 | Linux 版 helper 用 musl 完全静态链接（§11），不依赖目标机的 glibc 版本 | §11 |

---

## 4. C# 公共接口与互操作

### 4.1 接口（对齐 01 §6.2，加粗的是本文提议的补充）

```csharp
namespace TCode.Terminal.Pty;

public readonly record struct TerminalSize(int Columns, int Rows, int CellWidth, int CellHeight);
// 与 01 §6.1 一致：CellWidth/CellHeight 是单个 cell 的像素；PTY 层计算 ws_xpixel = Columns × CellWidth、
// ws_ypixel = Rows × CellHeight（截断到 65535）。cell 尺寸未知时为 0，像素也写 0

public sealed record TerminalExit(int? ExitCode, string? Signal, bool CoreDumped = false, string? Message = null);
public enum TerminalSignal { Hup, Int, Quit, Term, Kill }
public sealed record ForegroundInfo(int? Pid, string? Name, bool IsShell);

[Flags] public enum ConnectionCapabilities
{ None = 0, Resize = 1, PixelSize = 2, Signals = 4, ForegroundProcess = 8, WorkingDirectory = 16, ClearBuffer = 32 /* 新增 */ }

public enum PtyWidthMode { Compat, Grapheme }
// 原 PtyLaunchHints.MacLoginWrapper 已删除：2026-10-17 决定不经 login（§8.3）

public sealed record PtyLaunch(
    string File, IReadOnlyList<string> Arguments, string? Argv0, string? WorkingDirectory,
    IReadOnlyList<KeyValuePair<string, string>> Environment, TerminalSize InitialSize, PtyWidthMode WidthMode)
{
    public PtyLaunchHints Hints { get; init; }                  // 新增
    public string? WindowsCommandLine { get; init; }            // 新增：原样传给 CreateProcessW（cmd.exe 和用户写的命令行字符串）
    public bool AmbiguousWide { get; init; }                    // 新增：ConPTY 0x20，只在 1.25 中有效
}

public sealed class LocalPtyConnection : ITerminalConnection
{
    public int ProcessId { get; }                               // 直接子进程
    public PtyStartWarnings Warnings { get; }                   // 例如 CwdFallback
    public ConPtyImplementation? ConPty { get; }                // Bundled / Inbox / InboxNoRelease（仅 Windows）
    public bool TryWriteReply(ReadOnlySpan<byte> reply);        // 新增：镜像核心的应答通道，不阻塞，见 §5.3
    public ValueTask ClearBufferAsync(bool keepCursorRow);      // 新增：Windows 调用 ClearPseudoConsole；其他平台为空操作
    public ValueTask CloseAsync(TimeSpan grace);                // DisposeAsync 等于 CloseAsync(options.CloseGrace)
    // 以及 ITerminalConnection 的全部成员
}
```

能力：

| 平台 | Capabilities |
|---|---|
| Unix | Resize \| PixelSize \| Signals \| ForegroundProcess \| WorkingDirectory |
| Windows | Resize \| Signals（受限）\| ForegroundProcess（启发式）\| ClearBuffer（仅随包 conpty.dll） |

ConPTY 的 resize 只接受 COORD，**没有像素尺寸**；`CSI 14 t` 这类查询由镜像核心应答（S4 要核实 ConPTY 是否透传）。

### 4.2 LibraryImport 声明（摘录，其余函数按相同模式声明）

```csharp
[assembly: System.Runtime.CompilerServices.DisableRuntimeMarshalling]
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]

namespace TCode.Terminal.Pty.Unix;

internal static unsafe partial class TcPtyNative
{
    private const string Lib = "libtcodepty";   // 与 <DirectPInvoke Include="libtcodepty" /> 同名

    [LibraryImport(Lib, EntryPoint = "tcpty_version")]      internal static partial int Version();
    [LibraryImport(Lib, EntryPoint = "tcpty_last_errno")]   internal static partial int LastErrno();
    [LibraryImport(Lib, EntryPoint = "tcpty_init")]         internal static partial int Init(TcPtyInitInfo* info);
    [LibraryImport(Lib, EntryPoint = "tcpty_spawn")]        internal static partial int Spawn(TcPtySpawnOptions* o, TcPtySpawnResult* r);
    [LibraryImport(Lib, EntryPoint = "tcpty_set_nonblock")] internal static partial int SetNonblock(PtyFdHandle fd, int enable);
    [LibraryImport(Lib, EntryPoint = "tcpty_close")]        internal static partial int Close(int fd);
    [LibraryImport(Lib, EntryPoint = "tcpty_wake_create")]  internal static partial int WakeCreate(int* r, int* w);
    [LibraryImport(Lib, EntryPoint = "tcpty_wake_signal")]  internal static partial int WakeSignal(PtyFdHandle w);
    [LibraryImport(Lib, EntryPoint = "tcpty_read_gather")]
    internal static partial int ReadGather(PtyFdHandle fd, PtyFdHandle wake, byte* buf, int cap, TcPtyGatherConfig* cfg, uint* flags);
    [LibraryImport(Lib, EntryPoint = "tcpty_write")]        internal static partial int Write(PtyFdHandle fd, byte* buf, int len);
    [LibraryImport(Lib, EntryPoint = "tcpty_wait")]
    internal static partial int Wait(PtyFdHandle fd, PtyFdHandle wake, int events, int timeoutMs, uint* revents);
    [LibraryImport(Lib, EntryPoint = "tcpty_resize")]
    internal static partial int Resize(PtyFdHandle fd, ushort cols, ushort rows, ushort xpx, ushort ypx);
    [LibraryImport(Lib, EntryPoint = "tcpty_reap")]         internal static partial int Reap(int pid, int block, TcPtyExitStatus* s);
    [LibraryImport(Lib, EntryPoint = "tcpty_exitwatch_init")] internal static partial int ExitWatchInit();
    [LibraryImport(Lib, EntryPoint = "tcpty_exitwatch_add")]  internal static partial int ExitWatchAdd(int pid, uint flags);
    [LibraryImport(Lib, EntryPoint = "tcpty_exitwatch_wait")] internal static partial int ExitWatchWait(TcPtyExitEvent* ev, int cap, int timeoutMs);
    [LibraryImport(Lib, EntryPoint = "tcpty_exitwatch_wake")] internal static partial int ExitWatchWake();
    [LibraryImport(Lib, EntryPoint = "tcpty_killpg")]       internal static partial int KillPg(int pgid, int signo);
    [LibraryImport(Lib, EntryPoint = "tcpty_kill")]         internal static partial int Kill(int pid, int signo);
    [LibraryImport(Lib, EntryPoint = "tcpty_signal_number")] internal static partial int SignalNumber(int portable);
    [LibraryImport(Lib, EntryPoint = "tcpty_get_fg_pgrp")]  internal static partial int GetFgPgrp(PtyFdHandle fd);
    [LibraryImport(Lib, EntryPoint = "tcpty_get_cwd")]      internal static partial int GetCwd(int pid, byte* buf, int cap);
    [LibraryImport(Lib, EntryPoint = "tcpty_list_children")] internal static partial int ListChildren(int pid, int* pids, int cap);
    [LibraryImport(Lib, EntryPoint = "tcpty_set_thread_qos_interactive")] internal static partial void SetThreadQosInteractive();
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct TcPtySpawnOptions
{
    public uint Size, Flags;
    public byte* HelperPath, File;
    public byte** Argv, Envp;
    public byte* Cwd;
    public ushort Cols, Rows, XPixel, YPixel;
    public uint StatusTimeoutMs;
}
// TcPtySpawnResult / TcPtyInitInfo / TcPtyGatherConfig / TcPtyExitStatus / TcPtyExitEvent 与头文件逐字段对应，全部是 blittable 类型

internal sealed class PtyFdHandle : SafeHandle
{
    public PtyFdHandle() : base(-1, ownsHandle: true) { }
    public PtyFdHandle(int fd) : base(-1, true) => SetHandle(fd);
    public override bool IsInvalid => handle == -1;
    protected override bool ReleaseHandle() => TcPtyNative.Close((int)handle) == 0;
}
```

- **SafeHandle 的引用计数**保证 `tcpty_read_gather` 在 poll 中阻塞时，fd 不会被关闭后又被复用。关闭顺序见 §6，先唤醒读线程，再 Dispose fd。
- `NativeStringBlock`：把 argv 和 envp 用 UTF-8 编码进一块 `NativeMemory.Alloc` 分配的内存（字符串区 + `byte*[]` 指针数组，以 NULL 结尾），调用结束后 Free。不使用任何运行时封送。
- 静态链接（DirectPInvoke）与动态加载使用同一组声明，见 §11.5。

---

## 5. LocalPtyConnection：线程、背压、写入

### 5.1 线程（与 01 §5.1 对应）

| 线程 | 数量 | 实现 |
|---|---|---|
| 读线程 | 每个会话 1 个，`new Thread { IsBackground = true, Name = "pty-read-<id>" }` | Unix：`tcpty_set_thread_qos_interactive()`，然后循环调用 `tcpty_read_gather`。Windows：阻塞 `ReadFile` 64 KiB，再用 `PeekNamedPipe` 把已有数据续读满（便宜的 gather） |
| 写线程 | 每个会话 1 个 | 从 channel 取数据；Unix 用 `tcpty_write` + `tcpty_wait(POLLOUT)`，Windows 用阻塞 `WriteFile` |
| 退出监视 | Unix：进程级 1 个（pidfd 不可用时，每个会话再加 1 个 256 KiB 栈的 waitpid 线程）；Windows：线程池等待 | `UnixExitWatcher` / `ThreadPool.RegisterWaitForSingleObject` |
| 关闭线程 | Windows：每次关闭临时创建 1 个 | 专门调用 `ClosePseudoConsole`（W1） |

### 5.2 读循环与背压

```csharp
private static readonly PipeOptions OutputPipe = new(
    pool: MemoryPool<byte>.Shared, readerScheduler: PipeScheduler.Inline, writerScheduler: PipeScheduler.Inline,
    pauseWriterThreshold: 256 * 1024, resumeWriterThreshold: 128 * 1024,   // 相当于 01 §5.1 的"4 块 × 64 KiB"
    minimumSegmentSize: 64 * 1024, useSynchronizationContext: false);

private void ReadLoop()
{
    _backend.OnReadThreadStart();
    var writer = _output.Writer;
    byte[]? scratch = null;
    try
    {
        while (true)
        {
            if (Volatile.Read(ref _discarding))                // 正在关闭或消费方已离开：只读不交付
            {
                scratch ??= new byte[64 * 1024];
                if (_backend.Read(scratch, out var st) == 0 && (st & ReadStatus.Eof) != 0) break;
                if ((st & ReadStatus.Woken) != 0 && _backend.StopOnWake) break;
                continue;
            }
            Memory<byte> mem = writer.GetMemory(64 * 1024);
            int n; ReadStatus status;
            using (MemoryHandle pin = mem.Pin()) n = _backend.Read(pin, mem.Length, out status);
            if (n > 0)
            {
                writer.Advance(n);
                Volatile.Write(ref _lastOutputTick, Environment.TickCount64);
                var vt = writer.FlushAsync();
                var r = vt.IsCompleted ? vt.Result : vt.AsTask().GetAwaiter().GetResult(); // 阻塞在这里就是背压
                if (r.IsCompleted) Volatile.Write(ref _discarding, true);     // 消费方已 Complete
                // r.IsCanceled：关闭路径调用了 CancelPendingFlush，下一轮进入丢弃模式
            }
            if ((status & ReadStatus.Eof) != 0) break;
            if ((status & ReadStatus.Woken) != 0 && _backend.StopOnWake) break;
        }
        _eofSeen = true;
        writer.Complete();
    }
    catch (Exception ex) { writer.Complete(ex); }
    finally { _readerDone.Set(); }
}
```

- **背压链**：消费方（会话 actor）不 `AdvanceTo` → Pipe 超过 256 KiB → `FlushAsync` 不完成 → 读线程停止读取 → 内核 tty 队列或 ConPTY 输出管道写满 → 子进程的 `write` 阻塞。这就是 01 §5.1 要求的行为。
- **丢弃模式**是防死锁的关键：关闭时读线程可能正阻塞在 `FlushAsync` 上（消费方已不再读）。Windows 上这会让 conhost 卡在写输出 → 旧版 `ClosePseudoConsole` 永远等待（W1）。所以关闭路径要先 `_discarding = true`，再 `writer.CancelPendingFlush()`。Unix 上也采用同一机制，代码统一。
- `StopOnWake`：Unix 为 true（wake 就是关闭信号）；Windows 为 false（ReadFile 没有 wake，用 BROKEN_PIPE 结束）。
- gather 参数沿用 Ghostty 的实测值：1 KiB / 16 次自旋 / 1 ms / 3 ms / 64 KiB [实 GH Exec.zig:1290-1352、1561-1690]。未饱和的流在第一次 EAGAIN 时立即交付，所以**回显路径上没有额外延迟**。

### 5.3 写入

| 来源 | 接口 | 阻塞语义 |
|---|---|---|
| 用户按键、粘贴（经 IPC） | `WriteAsync(input)` | 先复制到 ArrayPool 缓冲区再入队，**立即完成**。连接层不设上限，只公开 `PendingInputBytes`。积压上限和溢出策略由 host 决定（04 §7.5：8 MiB，超出部分丢弃并发 `session/inputOverflow`），这样 IPC 读线程永远不会被阻塞 |
| 镜像核心的应答（DA、CPR、DECRQM…），在会话 actor 上同步产生 | `TryWriteReply(span)` | **永不阻塞**，与用户输入进同一个 FIFO，不受 host 的上限约束。actor 如果阻塞在写入上 → 不再解析输出 → 子进程写输出阻塞 → 子进程读不到输入，形成死锁。（04 §5.3 说"不保证相对顺序"；这里按 FIFO 实现，属于更强的保证） |

写线程：每次 `WriteFile` / `tcpty_write` 最多 4 KiB。遇到 EAGAIN 就 `tcpty_wait(POLLOUT | wake)`；遇到 `E_IO` 表示进程已不在，丢弃剩余数据并停止。

**Unix canonical 模式的输入队列有上限**：Linux 是 4095 字节；macOS 是 MAX_INPUT 1024 [推]。往 `cat` 这类读整行的程序粘贴超长单行时会被截断。系统终端也有这个限制，不处理，只写进文档（S12）。

### 5.4 Resize

`ResizeAsync` 在调用线程（会话 actor）上同步执行：

- Unix：`tcpty_resize(master, cols, rows, cols×cw, rows×ch)`；
- Windows：`ResizePseudoConsole(hpc, {cols, rows})`。

调用很便宜。去抖由 UI 负责（16–33 ms）。内核只在尺寸确实变化时发送 SIGWINCH，所以不需要手动补发 [实 research/02 §2.8]。

---

## 6. 退出与关闭

### 6.1 自然退出流程

```
Unix：  退出监视线程收到 pid 事件 → tcpty_reap(pid, block=1)
        → TerminalExit（kind=CODE → ExitCode；SIGNAL → Signal="SIGxxx", CoreDumped；E_CHILD → 两者都为空）
        → connection.OnChildExited(exit)
Windows：wait 回调 → GetExitCodeProcess → TerminalExit(ExitCode = unchecked((int)code))
        （0xC000013A 这类 NTSTATUS 由 UI 显示成十六进制，与 WT 一致）

OnChildExited：
  1. _reaped = true（与信号路径持同一把锁：回收之后不再按 pid/pgid 发信号，防止 pid 被复用后误伤）
  2. 收尾排空：等到读线程读到 EOF，或输出静默 ≥ 100 ms，最长 1 s
  3. Windows 且 ConPty == InboxNoRelease（没有 Release，EOF 不会自己出现）：在关闭线程上调用 ClosePseudoConsole，以产生 EOF
  4. _exited.TrySetResult(exit)
  5. Output：读到 EOF 时 Complete；如果后台进程仍持有 slave / 控制台，就继续读，直到 CloseAsync
```

- **为什么 `Exited` 要晚于排空**：pty host 在 `Exited` 完成时往输出日志追加一条"退出"记录。排空保证 shell 最后的输出（例如 `logout`）出现在这条记录之前。research/06 §6 要求"Output 在 Exited 之前读到 EOF"，但后台进程持有 slave 时（`sleep 100 &` 之后 exit）做不到 [推，research/02 §2.7]，所以改为"EOF 或静默超时"，见 §16-6。
- Linux 上最后一个 slave 关闭时，`read` 返回 EIO；macOS 上返回 0。`tcpty_read_gather` 把两者都报告为 EOF [实 research/02 §2.7]。

### 6.2 关闭（`CloseAsync(grace)`，默认 grace = 2 s）

**Unix**

| 步 | 动作 | 说明 |
|---|---|---|
| 1 | `_writes.Complete()`，丢弃未写出的输入 | — |
| 2 | 如果尚未回收：`fg = tcpty_get_fg_pgrp(master)`；`killpg(fg, SIGHUP)`（fg > 0 且 ≠ shellPgid 时）；`killpg(shellPgid, SIGHUP)`；忽略 EPERM 和 ESRCH | Darwin 上 killpg 偶尔返回 EPERM，Ghostty 也忽略 [实 GH Exec.zig:1186-1191]；login 包装时 pgid 的来源见 §8.3 |
| 3 | `_discarding = true`、`CancelPendingFlush()`、`tcpty_wake_signal(wake_w)`；Join 读写线程（各最多 500 ms） | 唤醒是"粘性"的，gather 和 wait 都会立即返回 |
| 4 | Dispose master fd | 内核挂断会话：向前台进程组和会话首进程发 SIGHUP，之后 tty 读写都返回 EIO [实 research/02 §2.4] |
| 5 | 在 grace 时间内等 `Exited`；超时后对 shellPgid 和最后一次看到的 fg 执行 `killpg(SIGKILL)`，再 `kill(pid, SIGKILL)`；之后再等 1 s | `nohup`、`disown`、自行 setsid 的程序会保留，与 Terminal.app 和 Ghostty 一致 [实 research/02 §2.4] |
| 6 | 仍然没有退出：记录日志，让退出监视继续挂着，等它最终回收 | 不阻塞关闭流程 |

**Windows**

| 步 | 动作 | 说明 |
|---|---|---|
| 1 | `_writes.Complete()` | — |
| 2 | **W2 保护**：距 `CreateProcessW` 不足 500 ms 时，先等到 500 ms 或进程退出 | 防止 `0xc0000142` 错误框 [实 Learn；时长为推，见 S6] |
| 3 | `_discarding = true`、`CancelPendingFlush()`。读线程**继续**读，直到 `ERROR_BROKEN_PIPE` | W1 |
| 4 | 新建关闭线程，调用 `ClosePseudoConsole(hpc)`：conhost 向所有客户端发送 `CTRL_CLOSE_EVENT` | 绝不在读线程、终结器或 UI 线程上调用 |
| 5 | 在 grace 时间内等 `Exited`；超时后 `TerminateProcess(hProcess, 1)`（只杀 shell，与 Unix 的 SIGKILL 对等） | — |
| 6 | 等读线程最多 1 s；仍阻塞时用 `CancelSynchronousIo(读线程句柄)` | 兜底 |
| 7 | 关闭 inWrite、outRead、hProcess；Job：先清除 `KILL_ON_JOB_CLOSE`，再关闭 Job 句柄（§7.5） | 正常关闭后，`start` 出去的 GUI 程序保留 |

`PseudoConsoleHandle.ReleaseHandle()`（终结器路径）：随包 conpty.dll 或 26100 以上的 inbox 版本，Close 不会阻塞，直接调用；其他情况新建一个线程去调用，绝不阻塞终结器 [实 research/02 §5.6]。

### 6.3 SignalAsync

| 信号 | Unix | Windows |
|---|---|---|
| Int | `killpg(fg 或 shellPgid, SIGINT)` | 写入 `0x03`（ConPTY 把它转换为 CTRL_C_EVENT [推，S4]） |
| Hup / Term / Quit | 先 fg，再 shellPgid | Hup/Term → `ClosePseudoConsole` 路径；Quit 返回 false |
| Kill | fg、shellPgid 和 pid 都发 SIGKILL | `TerminateProcess` |

回收之后的 SignalAsync 一律返回 false。

---

## 7. Windows：ConPTY

### 7.1 conpty.dll 加载策略

```csharp
internal static unsafe class ConPtyApi
{
    public static ConPtyImplementation Implementation { get; private set; }   // Bundled | Inbox | InboxNoRelease
    static delegate* unmanaged<Coord, nint, nint, uint, nint*, int> s_create;  // 返回 HRESULT
    static delegate* unmanaged<nint, Coord, int> s_resize;
    static delegate* unmanaged<nint, void> s_close;
    static delegate* unmanaged<nint, int> s_release;     // 可能为 null
    static delegate* unmanaged<nint, int, int> s_clear;  // 只有 conpty.dll 才有（BOOL keepCursorRow）

    internal static void EnsureLoaded(string baseDir, Action<PtyDiagnostic> log)
    {
        string dll = Path.Combine(baseDir, "conpty.dll");
        string host = Path.Combine(baseDir, NativeArchFolder(), "OpenConsole.exe");   // IsWow64Process2 → x64 / arm64
        nint lib = 0;
        if (File.Exists(dll) && File.Exists(host))
            lib = Win32.LoadLibraryExW(dll, 0, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (lib != 0
            && NativeLibrary.TryGetExport(lib, "ConptyCreatePseudoConsole", out var c)
            && NativeLibrary.TryGetExport(lib, "ConptyResizePseudoConsole", out var r)
            && NativeLibrary.TryGetExport(lib, "ConptyClosePseudoConsole", out var cl))
        {
            s_create = (delegate* unmanaged<Coord, nint, nint, uint, nint*, int>)c; /* … */
            s_release = TryGet(lib, "ConptyReleasePseudoConsole");
            s_clear = TryGet(lib, "ConptyClearPseudoConsole");
            Implementation = ConPtyImplementation.Bundled;
            return;
        }
        log(PtyDiagnostic.ConPtyDegraded(dll, host, Marshal.GetLastPInvokeError()));   // 明确记录降级
        nint k32 = NativeLibrary.Load("kernel32.dll");                                 // KnownDLL，裸名安全
        s_create = /* CreatePseudoConsole */; s_resize = /* … */; s_close = /* … */;
        s_release = TryGet(k32, "ReleasePseudoConsole");                               // 26100+ 才有
        s_clear = null;
        Implementation = s_release != null ? ConPtyImplementation.Inbox : ConPtyImplementation.InboxNoRelease;
    }
}
```

| 规则 | 原因 | 来源 |
|---|---|---|
| 只按绝对路径加载；不使用 `[LibraryImport("conpty.dll")]` | **Windows 11 的 System32 下自带 `conpty.dll`**，裸名会悄悄绑定到 inbox 实现 | [实 PP ConPtyImportResolver.cs 注释]；research/02 W12 |
| 加载前确认 `<arch>\OpenConsole.exe` 存在 | conpty.dll 先找同目录，再找 `<本机架构>\`，**都找不到时静默回退 System32\conhost.exe** | [实 WT winconpty.cpp:57-95] |
| 架构子目录按**本机**架构选择（IsWow64Process2），同目录**不放** OpenConsole | x64 进程在 arm64 Windows 上模拟运行时，conpty.dll 会选 arm64 的 OpenConsole（本机运行）；同目录放一个会抢先被选中 | [实 WT winconpty.cpp:57-95；PP Porta.Pty.targets:64-66] |
| 用 `delegate* unmanaged`，不用 `Marshal.GetDelegateForFunctionPointer` | AOT 安全；x64 和 arm64 只有一种调用约定，不必写 `[Stdcall]` | [实 research/02 §5.2] |

### 7.2 Win32 句柄与 SafeHandle

- `PseudoConsoleHandle : SafeHandle`（HPCON）
- `SafeFileHandle`（管道两端）
- `SafeProcessHandle`（BCL 类型）
- `JobHandle : SafeHandle`

### 7.3 完整调用序列

```
进程级（第一次 Start 时）：ConPtyApi.EnsureLoaded

每个会话：
 1. CreatePipe(out inRead, out inWrite, null, 0); CreatePipe(out outRead, out outWrite, null, 0)   // 不可继承、同步
 2. flags = MapFlags(launch)（§7.4）
    hr = s_create({cols, rows}, inRead, outWrite, flags, &hpc)；失败时抛 PtyException(hr)
    立即关闭 inRead 和 outWrite（ConPTY 内部已经 DuplicateHandle）[实 research/02 §3.3]
 3. **启动读线程和写线程**（读 outRead，写 inWrite）
    OpenConsole 一启动就输出 ESC[c、ESC[?1004h、ESC[?9001h [实 WT VtIo.cpp:197-204]；
    会话 actor 必须在 Start 返回后立即开始消费 Output，由镜像核心的 write_pty 应答 DA1（§7.7）
 4. job = WindowsJob.Create()  // 每个会话一个：KILL_ON_JOB_CLOSE | BREAKAWAY_OK（§7.5）
 5. InitializeProcThreadAttributeList(null, 2, 0, &size) → NativeMemory.Alloc(size) → Initialize(…)
    UpdateProcThreadAttribute(list, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x20016, (void*)hpc, sizeof(nint))  // 传值，不是 &hpc
    UpdateProcThreadAttribute(list, 0, PROC_THREAD_ATTRIBUTE_JOB_LIST = 0x2000D, &job, sizeof(nint))
 6. STARTUPINFOEXW si = { cb = sizeof, dwFlags = STARTF_USESTDHANDLES, hStd* = 0, lpAttributeList = list }
    cmd = launch.WindowsCommandLine ?? CommandLine.Build(File, Arguments)   // 可写的 char 缓冲区
    env = EnvironmentBlock.Build(launch.Environment)                         // §9
    cwd = Directory.Exists(wd) ? wd : %USERPROFILE%（发生回退时设置 Warnings.CwdFallback）
    CreateProcessW(null, cmd, null, null, bInheritHandles: FALSE,
                   EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, env, cwd, &si, &pi)
    失败：读线程收尾（关闭 hpc → BROKEN_PIPE），抛出异常
    DeleteProcThreadAttributeList; Free; CloseHandle(pi.hThread)
 7. if (s_release != null) s_release(hpc)
    // 之后 outRead 上的 BROKEN_PIPE 就是"所有客户端都已断开"的可靠 EOF [实 research/02 §3.3；NP conpty.cc:450-460]
 8. ThreadPool.RegisterWaitForSingleObject(new ProcessWaitHandle(pi.hProcess), OnExit, null, -1, executeOnlyOnce: true)
```

`ClearBufferAsync(keepCursorRow)`：只在 Bundled 实现下调用 `s_clear(hpc, keepCursorRow ? 1 : 0)`，在用户执行"清除缓冲区"（⌘K / Ctrl+Shift+K）时与核心的 clear 一起调用。否则 conhost 下一次重绘会把旧内容画回来 [实 research/02 W6]。

### 7.4 flags 与宽度模式

| 输入 | Bundled（1.25.x） | Inbox（kernel32） |
|---|---|---|
| `PtyWidthMode.Compat` | `GLYPH_WIDTH_WCSWIDTH` 0x10 | **0** |
| `PtyWidthMode.Grapheme` | `GLYPH_WIDTH_GRAPHEMES` 0x08 | **0** |
| `AmbiguousWide` | 再加 `0x20`（要求 conpty.dll 文件版本 ≥ 1.25） | 忽略 |
| `INHERIT_CURSOR` 0x1 | 永不使用 | 永不使用 |

- Inbox 必须传 0：旧版 inbox 中 0x8 是已删除的 `PASSTHROUGH_MODE` [实 research/02 §3.2]。不传时，conhost 的默认测量方式是 Graphemes [实 WT settings.hpp:236]。
- flags 位定义见 [实 WT conpty-static.h:26-38、winconpty.cpp:169-185]。
- **宽度在创建时就固定了。** ConPTY 对 DECSET 2027 不做任何事，DECRQM 报告"永久"状态 [实 WT adaptDispatch.cpp:1891-1892、2032-2033]。因此 Windows 上的两份核心必须**锁定宽度模式**，忽略程序发来的 2027 切换。建议 Windows 默认 Grapheme（与 WT 和 conhost 默认一致），这与 D9 的"默认兼容模式"不同，见 §16-4。

### 7.5 Job Object 策略（允许 breakaway，只兜底 shell 树）

| 项 | 取值 | 理由 |
|---|---|---|
| 粒度 | 每个会话一个 Job | 便于按会话收尾 |
| LimitFlags | `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE \| JOB_OBJECT_LIMIT_BREAKAWAY_OK`，**不设** `SILENT_BREAKAWAY_OK` | shell 树默认都在 Job 里（崩溃时兜底）；显式使用 `CREATE_BREAKAWAY_FROM_JOB` 的程序可以脱离 |
| 加入方式 | `PROC_THREAD_ATTRIBUTE_JOB_LIST` | 不存在"创建之后、Assign 之前"的竞争窗口 [推：1809 是否支持需要实测，S7] |
| 正常关闭 | `SetInformationJobObject` 清除 `KILL_ON_JOB_CLOSE` → `CloseHandle(job)` | 关闭标签时，用户 `start` 出去的程序保留，与 WT 一致 [推，S7] |
| pty host 崩溃 | 系统关闭 Job 句柄 → 整棵树被杀 | 兜底 |
| OpenConsole.exe | 不在会话 Job 中（它由 conpty.dll 在 CreatePseudoConsole 时启动） | host 消失 → signal 管道断开 → OpenConsole 自行退出，并向客户端发送 CTRL_CLOSE |

### 7.6 kernel32 互操作清单

全部使用 `[LibraryImport("kernel32.dll", SetLastError = true)]`，参数只有 blittable 类型、指针和 SafeHandle：

- 管道：`CreatePipe`、`ReadFile`、`WriteFile`、`PeekNamedPipe`、`CancelSynchronousIo`
- 进程：`InitializeProcThreadAttributeList`、`UpdateProcThreadAttribute`、`DeleteProcThreadAttributeList`、`CreateProcessW`、`GetExitCodeProcess`、`TerminateProcess`、`GetProcessTimes`、`IsWow64Process2`
- Job：`CreateJobObjectW`、`SetInformationJobObject`
- 进程快照：`CreateToolhelp32Snapshot`、`Process32FirstW`、`Process32NextW`
- 加载：`LoadLibraryExW`

结构体：

- `STARTUPINFOEXW`、`PROCESS_INFORMATION`、`COORD`、`JOBOBJECT_EXTENDED_LIMIT_INFORMATION`（含 `IO_COUNTERS`）；
- `PROCESSENTRY32W`，其中 `fixed char szExeFile[260]`。

也可以改用 CsWin32 源生成（Porta.Pty 的做法）；手写声明约 25 个，差别不大。

### 7.7 DA1 / 9001 握手

| 序列 | 谁处理 | 要求 |
|---|---|---|
| `ESC[c`（DA1） | 镜像核心（libghostty-vt）的 write_pty → `TryWriteReply` | 1.24 及更早版本、inbox：应答之前 conhost 最多阻塞 3 s；1.25：只在使用 INHERIT_CURSOR 时等待 [实 research/02 W3]。读线程在 CreateProcess 之前启动，并且 actor 立即开始消费 |
| `ESC[?1004h` | 两份核心都记录焦点模式 | UI 发送焦点事件 |
| `ESC[?9001h` | **libghostty-vt 的模式表里没有 9001** [实 GH modes.zig：有 1004 和 2027，没有 9001] | 需要在 TCode 一侧额外跟踪：两份核心前面各加一个轻量的 9001 序列识别器，写进 `FrameSnapshot.Modes`，并负责 DECRQM 9001 的应答。属于 02-core / 06-input 的工作，见 §16-5 |
| resize 之后的 `ESC[6n` | 镜像核心应答 CPR | [实 research/02 W7] |

### 7.8 W1–W12 对策

| # | 陷阱 | 本设计的对策 | 位置 |
|---|---|---|---|
| W1 | Close 死锁 | 在关闭线程上调用 Close；读线程进入丢弃模式继续读到 BROKEN_PIPE；`CancelPendingFlush`；优先使用随包 dll（其 Close 不等待） | §5.2、§6.2 |
| W2 | 启动中 Close 弹出 0xc0000142 | 距启动不足 500 ms 的关闭请求先等待 | §6.2 步骤 2 |
| W3 | DA1 阻塞 | 读线程在 CreateProcess 之前启动；镜像核心应答；**不做** DA1 过滤 hack | §7.3、§7.7 |
| W4 | 9001 | 在 TCode 一侧跟踪 9001，06-input 负责编码 | §7.7 |
| W5 | 字宽不一致 | flags 与核心一致；Windows 锁定宽度模式 | §7.4 |
| W6 | 旧版"重新渲染"与清屏 | 随包 dll；`ClearBufferAsync` | §7.3 |
| W7 | resize 后的 CPR | 镜像核心任何时候都应答 | §7.7 |
| W8 | 编码 | ConPTY 的输入输出都是 UTF-8；字节原样交给核心，PTY 层不解码 | — |
| W9 | 句柄泄漏 | `bInheritHandles = FALSE`；hStd* 显式为 0 | §7.3 步骤 6 |
| W10 | 进程树 | 每个会话一个 Job，正常关闭时清除 KILL 标志 | §7.5 |
| W11 | 前台进程和 cwd | Toolhelp32；cwd 只来自 OSC 7 / 9;9 | §8.2 |
| W12 | DLL 误绑定 | 绝对路径 + 确认 OpenConsole 在位；回退时明确记录日志 | §7.1 |

---

## 8. 进程信息

### 8.1 Unix：前台进程

```
GetForegroundAsync：
  fg = tcpty_get_fg_pgrp(master)                       // tcgetpgrp
  shellPgid = ResolveShellPgid()                        // §8.3
  if fg <= 0 → null
  name = tcpty_get_process_name(fg)                     // 进程组组长；不存在时（管道中的组长已退出）name = null
  return new ForegroundInfo(fg, name, IsShell: fg == shellPgid)
```

- "是否有程序在运行"由 host 结合忽略列表判断（research/05 §8.1：shell 和 login 不算，tmux 算）。PTY 层只提供上面三项。
- 查询按需进行，标题刷新节流到 ≤ 1 Hz，不轮询。

### 8.2 Windows：Toolhelp32 进程树

1. `CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS)`，建立 pid → ppid 映射；
2. 从 shell pid 出发，求后代集合。用 `GetProcessTimes` 校验"子进程的创建时间 ≥ 父进程的创建时间"，防止 pid 复用造成误判；
3. 排除 `conhost.exe` 和 `OpenConsole.exe`；
4. 剩余后代为空 → `IsShell = true`；否则取创建时间最晚的后代作为 Name 和 Pid。

单次快照约 1–5 ms [推]，按需调用。WorkingDirectory 能力为 false。

### 8.3 macOS：不经 login 包装（2026-10-17 决定）

用户决定放弃 `/usr/bin/login` 包装（原因：login 父进程 `waitpid` 到 shell 后调用 `bail(NO_SLEEP_EXIT, 0)` → `exit(0)`，完全不看 shell 的退出状态 [实 LOGIN login.c:854-883、1480-1493]）。macOS 改为由 helper 直接 exec shell，argv0 为 `-zsh` 形式（[07](07-shell-profiles-config.md) §3.2）。因此在所有 Unix 平台上：

- spawn 返回的 pid 就是 shell，`shellPgid = pid`（POSIX_SPAWN_SETSID）；
- 退出码直接来自 `waitpid(pid)`，不需要对孙进程注册 `NOTE_EXITSTATUS`；`TCPTY_WATCH_EXITSTATUS` 与 `PtyLaunchHints.MacLoginWrapper` 不再需要，实现时删除；
- cwd 兜底直接查询 shell 的 pid。

### 8.4 cwd 回退链

| 顺序 | 来源 | 平台 |
|---|---|---|
| 1 | 镜像核心记录的 OSC 7（Windows 还有 OSC 9;9），只作为不可信输入，需要校验目录存在 | 全部（host 层） |
| 2 | `connection.GetWorkingDirectoryAsync()` → `tcpty_get_cwd(shellPid)`：Linux 读 `/proc/<pid>/cwd`，macOS 用 `PROC_PIDVNODEPATHINFO` [实 SDK proc_info.h:741] | Unix |
| 3 | 这个会话启动时的 cwd | 全部 |
| 4 | 项目根目录 → 用户主目录 | 全部 |

- 取的是 **shell** 的 cwd，不是前台程序的 cwd（与 Konsole 一致 [实 research/05 §1.2]）。

---

## 9. 环境变量的传递格式

| 平台 | 格式 | 规则 |
|---|---|---|
| Unix | `char *const envp[]`，元素为 UTF-8 的 `"KEY=VALUE"`，以 NULL 结尾，通过 `posix_spawn` 原样传给 helper，helper 用它 `execvp`（PATH 查找也使用这份环境） | 由 C# 构造：key 去重（后者覆盖前者）；key 为空或含 `=`、值含 NUL 时直接拒绝（抛 `ArgumentException`）。PTY 层**不增删任何变量**，策略在 TCode.Terminal.Shell（research/05 §2.3）。不在子进程里 setenv |
| Windows | UTF-16 环境块：`K=V\0K=V\0\0`（为空时是 `\0\0`），配合 `CREATE_UNICODE_ENVIRONMENT` | 按名称做大小写不敏感的排序（Win32 文档要求按 Unicode 序，与区域无关；用 `OrdinalIgnoreCase` 近似）；以 `=` 开头的驱动器 cwd 项（`=C:=C:\…`）保留在最前面；key 去重时不区分大小写 |

`.NET` 读取环境时按 UTF-8 解码，原来不是 UTF-8 的字节会丢失 [推]。v1 接受这个限制，S11 实测其影响。

argv 与命令行：

- Unix 用 `argv0` 加参数数组，不经过 shell；
- Windows 优先使用 `WindowsCommandLine` 原样传入，否则按 MSVCRT 规则拼接（`"File" args`，处理反斜杠和引号）；
- cmd.exe 和用户手写的命令行必须走原样传入，这是在 `PtyLaunch` 中新增该字段的原因（§16-1）。

---

## 10. 与 .NET 运行时共存的规则

| # | 规则 | 原因 | 来源 |
|---|---|---|---|
| R1 | 只 `waitpid(pid)`，**绝不** `waitpid(-1)` 或 `wait()` | 否则会抢走 `Process` 的退出状态 | [实 research/02 §2.5；RT ProcessWaitState.Unix.cs:609-700] |
| R2 | 收到退出通知后**立即**回收 | 僵尸进程会让 .NET 的每次 SIGCHLD 都走 checkAll 全量扫描 | [实 同上:622-640] |
| R3 | 回收时遇到 `ECHILD` → 退出码"未知"（ExitCode 和 Signal 都为空） | .NET reapAll 的场景（pid 1，或启动时 SIGCHLD = SIG_IGN）| [实 RT pal_signal.c:380-388] |
| R4 | 不把 SIGCHLD 设为 SIG_IGN 或 SA_NOCLDWAIT；`tcpty_init` 发现进入时已经是 SIG_IGN 时，复位为 SIG_DFL 并上报 `sigchld_was_ignored` | .NET 在初始化时就记录了"原始处置"并据此决定 reapAll，我们事后复位改变不了它的决定，只能保证内核不再自动回收 | [实 RT pal_signal.c:384]；影响为 [推] |
| R5 | pty host 尽量不用 `System.Diagnostics.Process`、`Console`、`PosixSignalRegistration`；必须用时，确保启动时 SIGCHLD 不是 SIG_IGN（Desktop 用 `Process.Start` 启动 host 时，自定义处理器会被复位为 SIG_DFL） | 不用就不会安装 .NET 的 SIGCHLD 处理器，彼此完全不干扰 | [实 RT pal_process.c:584-604（只复位自定义处理器）] |
| R6 | posix_spawn 的 `SETSIGDEF` 用**全集**，`SETSIGMASK` 用空集 | .NET 的 posix_spawn 路径只复位"自定义处理器"，SIG_IGN 被保留，所以 SIGPIPE 会以忽略状态传下去 | [实 RT pal_process.c:584-604；node-pty pty.cc:830-845] |
| R7 | SIGPIPE 在 host 内保持 .NET 的 SIG_IGN | 写入已关闭的 pty 或管道时得到 EPIPE（映射为 `E_IO`），而不是让 host 崩溃 | [实 research/02 §1.4] |
| R8 | fd 0–2 在 host 启动时补齐（`tcpty_init`） | host 以分离方式启动，stdio 可能被关闭，之后 `/dev/ptmx` 会拿到 0–2 | [推]；node-pty 的 low_fds 也是在防这个 [实 NP pty.cc:752-758] |
| R9 | 读、写、退出监视都用专用 `Thread`，不在线程池中阻塞 | — | [实 research/02 §5.5] |
| R10 | .NET 11 GA 之后评估 `UnixProcessStartArguments` 回调 | 把 pid 交给 `Process` 跟踪，从根本上消除 SIGCHLD 冲突。`IPtyExitWatcher` 抽象保留替换点 | [实 research/02 §2.5] |

---

## 11. 原生构建与打包

### 11.1 编译命令（4 个 Unix RID）

推荐方案：**Linux 用 zig 交叉编译**（libghostty-vt 已经需要 pin 一个 zig 版本，复用即可），macOS 用 Apple clang。

```bash
# 公共选项
CFLAGS="-O2 -fPIC -fvisibility=hidden -D_GNU_SOURCE -Wall -Wextra -Werror -Inative/tcodepty/include"
SRC="native/tcodepty/src/tcpty_spawn.c native/tcodepty/src/tcpty_io.c native/tcodepty/src/tcpty_exit.c native/tcodepty/src/tcpty_proc.c"

# linux-x64 / linux-arm64：glibc 基线 2.28（RHEL 8），任何 Linux 主机都能交叉编译
for T in x86_64 aarch64; do
  zig cc -target $T-linux-gnu.2.28 $CFLAGS -shared -Wl,-soname,libtcodepty.so -o out/$T/libtcodepty.so $SRC
  for f in $SRC; do zig cc -target $T-linux-gnu.2.28 $CFLAGS -c $f -o out/$T/$(basename $f .c).o; done
  zig ar rcs out/$T/libtcodepty.a out/$T/*.o                                             # 供 DirectPInvoke 使用
  zig cc -target $T-linux-musl -O2 -static -o out/$T/tcode-pty-spawn native/tcodepty/helper/tcode-pty-spawn.c   # 完全静态
done
native/tcodepty/check-glibc.sh out/*/libtcodepty.so    # objdump -T | grep -o 'GLIBC_[0-9.]*' | sort -V | tail -1 ≤ 2.28

# osx-arm64 / osx-x64：Apple clang，在 arm64 runner 上可以同时产出两个架构
for A in arm64 x86_64; do
  clang -arch $A -mmacosx-version-min=11.0 $CFLAGS -dynamiclib -install_name @rpath/libtcodepty.dylib -o out/$A/libtcodepty.dylib $SRC
  clang -arch $A -mmacosx-version-min=11.0 $CFLAGS -c $SRC   # 生成 .o，然后：
  libtool -static -o out/$A/libtcodepty.a *.o
  clang -arch $A -mmacosx-version-min=11.0 -O2 -o out/$A/tcode-pty-spawn native/tcodepty/helper/tcode-pty-spawn.c
done
```

| 项 | 决定 | 说明 |
|---|---|---|
| 备选（不用 zig） | Linux 用 `quay.io/pypa/manylinux_2_28_{x86_64,aarch64}` 容器中的 gcc-toolset，分别在 `ubuntu-24.04` 和 `ubuntu-24.04-arm` runner 上原生编译 | 与 MiniPty 的 runner 布局相同 [实 MP build.yaml:20-40]；helper 也可以用 `gcc -static`（glibc 静态链接时 `opendir` 没有问题） |
| glibc 基线 | 2.28；用到的符号：posix_spawn\*、ptsname_r、epoll_\*、pipe2、syscall。`close_range` 和 `pidfd_open` **只通过 `syscall()` 调用**，不依赖 2.34 / 2.36 的封装 | .NET 10 的 glibc 基线是 2.27 [实 dotnet/core supported-os.md] |
| musl | **v1 不发布 musl RID**（01 定的是 6 个 RID）。代码与 musl 兼容（SETSID 需要 1.1.17+，ptsname_r 存在），以后加 `zig cc -target *-linux-musl` 即可。helper 本来就是 musl 静态链接，与发行版无关 | libghostty-vt 也要同步加 musl 构建，所以放到以后 |
| macOS 最低版本 | `-mmacosx-version-min=11.0`，与 `tools/package_macos_app.sh` 的 `LSMinimumSystemVersion` 一致；使用的 API 最低要求 10.7（proc_listchildpids），不使用 26.0 才有的 `addchdir` | .NET 10 官方只支持 macOS 15、26、27 [实 supported-os.md:23]，建议 12-build 文档把 LSMinimumSystemVersion 提高到 15.0 |
| 编译警告 | `-Werror`；另跑一遍 `-fsanitize=address,undefined` 的测试构建（只在 Linux CI 上） | — |

### 11.2 签名与公证（macOS）

```bash
# 由内向外签名：先签 helper 和 dylib，再签 tcode-ptyhost，最后签 .app
codesign --force --options runtime --timestamp --sign "$DEVELOPER_ID" TCode.app/Contents/MacOS/ptyhost/tcode-pty-spawn
codesign --force --options runtime --timestamp --sign "$DEVELOPER_ID" TCode.app/Contents/MacOS/ptyhost/libtcodepty.dylib   # 仅动态模式
codesign --force --options runtime --timestamp --sign "$DEVELOPER_ID" TCode.app/Contents/MacOS/ptyhost/tcode-ptyhost
# …最后签 TCode.app，然后 notarytool submit --wait，stapler staple
```

- helper **不需要任何 entitlement**。hardened runtime 只约束 helper 自己的映像，exec 之后由新映像（shell）自己的签名决定 [推，S13]。
- 动态模式下 dylib 必须使用相同的 Team ID（library validation）。
- 可执行位：构建脚本中执行 `chmod 755`。打包时用 `<None … CopyToPublishDirectory>`，MSBuild 的 Copy 会保留权限 [推]。运行时 `HelperLocator` 检查 `File.GetUnixFileMode` 是否有 UserExecute，没有就报出明确错误（不在已签名的 bundle 里改权限）[实 research/02 U13]。

### 11.3 发布目录布局

沿用 AgentHost 发布到 `agenthost/` 子目录的做法 [实 src/TCode.Desktop/TCode.Desktop.csproj:50-53]。pty host 发布到 `ptyhost/`。

```
<publish>/                        （macOS：TCode.app/Contents/MacOS/）
  TCode[.exe]
  libghostty-vt.{dylib,so,dll}     UI 侧核心使用
  agenthost/…
  ptyhost/
    tcode-ptyhost[.exe]            AOT
    libghostty-vt.*                镜像核心使用（与 UI 侧是同一个构建，01 §3.2 规则 3）
    tcode-pty-spawn                Unix
    libtcodepty.{so,dylib}         Unix，只在动态模式下存在
    conpty.dll                     Windows（来自 runtimes/win-<arch>/native）
    x64/OpenConsole.exe            Windows x64 包同时带 x64 和 arm64 两份；arm64 包只带 arm64
    arm64/OpenConsole.exe
```

`HelperLocator` 默认使用 `Path.Combine(AppContext.BaseDirectory, "tcode-pty-spawn")`；可以用 `LocalPtyOptions.HelperPath` 覆盖（测试用）。

### 11.4 ConPTY NuGet 引用

```xml
<!-- src/TCode.Terminal.Host/TCode.Terminal.Host.csproj：必须是可执行项目自己的直接引用 -->
<PropertyGroup>
  <ConptyRequiresx64Host>true</ConptyRequiresx64Host>
  <ConptyRequiresARM64Host>true</ConptyRequiresARM64Host>
</PropertyGroup>
<ItemGroup Condition="$(RuntimeIdentifier.StartsWith('win'))">
  <PackageReference Include="Microsoft.Windows.Console.ConPTY" Version="1.25.260930003" />
</ItemGroup>
```

- 包里的 `build/` targets **只对直接引用生效**，通过 ProjectReference 传递时只会带上 conpty.dll，OpenConsole 会丢失，而 conpty.dll 找不到它时会静默回退到 conhost [实 PP Porta.Pty.targets:3-23]。所以要在 Host 项目里直接引用，`TCode.Terminal.Pty` 本身不引用这个包。
- 属性名 `ConptyRequires*Host` 取自 Porta.Pty 的用法 [实 PP Porta.Pty.csproj:35-36]。它在 AOT 发布中是否把文件放进 `x64\` 和 `arm64\`，需要 S8 实测；如果不行，就用 `GeneratePathProperty` 加显式的 `<None Link="x64\OpenConsole.exe">` 兜底。
- **CI 断言**：Windows 发布目录必须包含 `conpty.dll`、`x64\OpenConsole.exe` 和 `arm64\OpenConsole.exe`（x64 包）。启动日志必须报告 `ConPty=Bundled`。
- 升级规则：conpty.dll 与 OpenConsole.exe **必须成对升级**（wezterm#7774 [实 research/02 §3.4]）。

### 11.5 AOT 下静态链接 libtcodepty（DirectPInvoke）

| 方案 | 优点 | 缺点 | 决定 |
|---|---|---|---|
| 动态 `.so` / `.dylib` | Debug/JIT、单元测试直接可用；替换方便 | 多一个需要签名的文件；需要处理加载路径（已用 `AssemblyDirectory` 限定） | **测试和 Debug 构建使用** |
| 静态 `.a` + `<DirectPInvoke Include="libtcodepty" />` + `<NativeLibrary Include="…/libtcodepty.a" />` | 少一个文件；符号缺失时在链接期报错；调用没有懒绑定开销 [实 research/02 §5.3] | 每个 RID 都要有 `.a`；它与宿主的 libc 一起链接（这正是我们想要的） | **tcode-ptyhost 的 Release AOT 发布使用** |

```xml
<!-- TCode.Terminal.Host.csproj（Unix RID 且 PublishAot 时） -->
<ItemGroup Condition="'$(PublishAot)' == 'true' And !$(RuntimeIdentifier.StartsWith('win'))">
  <DirectPInvoke Include="libtcodepty" />
  <NativeLibrary Include="$(RepoRoot)artifacts/native/tcodepty/$(RuntimeIdentifier)/libtcodepty.a" />
</ItemGroup>
```

注意：AOT 二进制本身链接的是**构建机**的 glibc。要支持 RHEL 8，tcode-ptyhost 必须在 glibc ≤ 2.28 的 sysroot 上发布，由 12-build 文档负责。

---

## 12. 独立修复：BashTool 的 SIGPIPE 问题（只给出改法，本次不改代码）

**问题**：`BashTool.Start` 通过 `Process.Start` 启动 `$SHELL -c`（src/TCode.Agent.Coding/Tools/Shell/BashTool.cs:184-211）。.NET 把 SIGPIPE 设为 SIG_IGN，它的 spawn 路径又只复位自定义处理器（RT pal_process.c:584-604），所以 bash 进程继承了 SIGPIPE 忽略状态。结果 `yes | head -1` 中的 `yes` 不会被 SIGPIPE 结束，而是报 `yes: stdout: Broken pipe` 并以 1 退出；Python 会打印 BrokenPipeError 栈。

**本机实测**（父进程 SIGPIPE=IGNORE）[实 local]：

| 命令 | 结果 |
|---|---|
| `bash -c 'trap - PIPE; yes \| head -1'` | `yes: stdout: Broken pipe`，PIPESTATUS = `1 0` → **`trap - PIPE` 对 bash 无效**（bash 规定：进入时就被忽略的信号不能被 trap 或复位） |
| `zsh -c 'trap - PIPE; yes \| head -1'` | pipestatus = `141 0` → zsh 有效 |
| `sh -c 'trap - PIPE; perl -e "print \$SIG{PIPE}"'` | `IGNORE` → /bin/sh 也无效 |

所以 research/02 §9.3 的建议"在 `-c` 前加 `trap - PIPE;`"不成立。

**改法**：借用 helper 的 `--exec` 模式，在 exec shell 之前把全部信号复位。

```csharp
// BashTool.Start（只在 Unix 上，并且找到 helper 时）
var shell = ShellResolver.Current;
var helper = PtyHelperPath.TryResolve();   // 1) 环境变量 TCODE_PTY_HELPER（Desktop 启动 AgentHost 时注入）
                                          // 2) Path.Combine(AppContext.BaseDirectory, "..", "ptyhost", "tcode-pty-spawn")
var info = helper is null || OperatingSystem.IsWindows()
    ? new ProcessStartInfo(shell.Executable)
    : new ProcessStartInfo(helper) { ArgumentList = { "--exec", shell.Executable, shell.Executable } };  // file, argv0
foreach (var arg in shell.PrefixArguments) info.ArgumentList.Add(arg);
info.ArgumentList.Add(command);
// 其余（重定向、环境变量）不变
```

| 项 | 说明 |
|---|---|
| 进程树与 Kill | helper 是 exec 进 shell 的，pid 不变，`Process.Kill(entireProcessTree)` 的行为不变 |
| 找不到 helper 时 | 保持现状并记录一次警告；zsh 可以在命令前加 `trap - PIPE;` 作为局部缓解（已实测有效），bash 没有纯 shell 层面的修复 |
| 依赖方向 | AgentHost 不需要引用 TCode.Terminal.\*，只需要知道 helper 的路径 |
| 测试 | 新增 `BashToolTests.SigpipeIsDefault`：`yes \| head -1; echo ${PIPESTATUS[0]}` 期望输出 141 |
| `ProcessRunner`（rg、git） | 这些程序自己处理 EPIPE，可以不改；需要时用同样的方式包装 |

---

## 13. 退出码语义汇总

| 情形 | TerminalExit | UI 文案（research/05 §8.3） |
|---|---|---|
| Unix WIFEXITED | `ExitCode = n` | 0 → 按 closeOnCleanExit 处理 |
| Unix WIFSIGNALED | `Signal = "SIGTERM"…`，`CoreDumped` | "被信号终止" |
| shell 自己报告 128+N（子进程被信号杀死后 shell 正常退出） | `ExitCode = 128+N` | 原样显示 |
| `ECHILD` | 两者都为空 | "进程已退出"，不显示退出码 |
| Windows | `GetExitCodeProcess`；`STILL_ACTIVE`（259）不会出现，因为只在进程句柄被触发后才读取 | ≥ 0x80000000 时显示十六进制 |
| 启动失败（stage ≠ 0） | 不产生连接，`Start` 抛出 `PtyException(Stage, Errno, Message)` | 05 §8.3 "启动失败"界面：显示命令和 errno 文本 |

---

## 14. 测试计划（tests/TCode.Terminal.Pty.Tests）

所有测试都通过真实的 PTY 进行，不使用模拟。断言针对输出字节（带超时的 `ReadUntil(pattern, 5s)`）、`Exited` 和 fd/线程计数。

| # | 用例 | 平台 | 断言 |
|---|---|---|---|
| T1 | 往返：`sh -c 'printf ready; read x; printf "got:%s" "$x"'`，写入 `abc\r` | 全部（Windows 用 `cmd /c set /p`） | 输出包含 `got:abc`；echo 可见 |
| T2 | tty 正确性：`tty; test -t 0; ps -o sid=,pgid=,tpgid= -p $$; stty -a` | Unix | 会话首进程 = pid；tpgid = pgid；`iutf8` 已打开；`bash -i` 不报 "no job control" |
| T3 | SIGPIPE：`bash -c 'yes \| head -1; echo ${PIPESTATUS[0]}'` | Unix | `141` |
| T4 | resize：`trap 'stty size' WINCH` 循环；ResizeAsync(100×30, cell 9×18) | Unix；Windows 用 `mode con` 查询 | 输出 `30 100`；`tcpty_get_winsize` 等于 `{30,100,900,540}` |
| T5 | 退出码：`exit 0` / `exit 3` / `kill -TERM $$` / `exec false` | 全部 | 0 / 3 / Signal=SIGTERM / 1 |
| T6 | 启动错误：文件不存在、不可执行、cwd 不存在 | Unix | `PtyException` 的 Stage=HELPER_EXEC，ENOENT / EACCES；cwd 不存在时 `Warnings.CwdFallback` 且实际在 $HOME |
| T7 | 关闭语义：`trap '' HUP; sleep 100` | Unix | 关闭耗时 ≈ grace + ε，最后被 SIGKILL；普通 shell 在 200 ms 内退出；`nohup sleep 30 &` 在关闭后仍存活（`kill -0`） |
| T8 | 退出排空：`printf 'last-line'; exit 0` | 全部 | Output 中 `last-line` 出现在 `Exited` 完成之前 |
| T9 | 后台进程持有 slave：`sleep 5 & exit` | Unix | `Exited` 在 ≤ 1.2 s 内完成（不等 5 s） |
| T10 | `yes \| head -c 100M` 与慢消费方 | 全部 | 消费方暂停 2 s 期间，Pipe 积压 ≤ 256 KiB + 64 KiB；子进程被阻塞（随后吞吐恢复） |
| T11 | `cat` 1 GB（`head -c 1G /dev/zero \| tr '\0' a`） | 全部 | 记录 MB/s（基准：macOS ≥ 300 MB/s [推]）；host 的 RSS 增量 < 50 MB |
| T12 | 50 个并发会话：同时启动，各自往返，再全部关闭 | 全部 | 全部成功；之后 fd 数量回到基线（`/proc/self/fd` 或 `lsof -p`），线程数回到基线，没有僵尸进程（`ps -o stat`） |
| T13 | 快速开关：500 次 Start → 立即 Close | 全部 | 没有异常、没有泄漏；macOS 不出现 ENXIO（锁 + 重试）；Windows 不出现 0xC0000142 |
| T14 | TUI resize 风暴：测试程序在 SIGWINCH 时打印尺寸；1 s 内 resize 1000 次（随机尺寸），最后一次固定 | 全部 | 最后一次打印的尺寸等于最终尺寸；不崩溃、不挂起；`vim -u NONE` 能正常退出 |
| T15 | 与 SIGCHLD 共存：后台循环 `Process.Start("true")` 1000 次，同时 20 个 PTY 会话各自退出 | Unix | 每个 `Process.WaitForExit` 都返回；每个 PTY 的退出码正确 |
| T16 | pidfd 回退：设置 `TCODE_PTY_FORCE_WAITPID_THREAD=1` 后重跑 T5、T12 | Linux | 结果相同 |
| T17 | macOS argv0 login shell：argv0 `-zsh` 启动 `/bin/zsh`，执行 `exit 3` | macOS | `ps -o args= -p $$` 为 `-zsh`；PATH 含 `/etc/paths.d` 中的路径；`TerminalExit.ExitCode == 3` |
| T18 | helper `--detach`：从 .NET `Process.Start` 启动 `--detach --stdout-fd 1 -- /bin/sh -c 'ps -o pgid= -p $$; tty; ls /dev/fd; echo READY'`（Linux 另查 `ps -o sid=`，macOS 用 `ps -o sess=`），并额外打开一个不带 CLOEXEC 的 fd | Unix | sid = pgid = pid（新会话），没有控制终端（`tty` 输出 "not a tty"）；`/dev/fd` 只有 0–2（以及 ls 自己打开的 fd）；SIGPIPE 为默认；参数错误时退出码为 126，exec 失败时为 127 |
| T19 | helper `--exec`：`--exec bash bash -c 'yes \| head -1; echo ${PIPESTATUS[0]}'` | Unix | `141` |
| W-T1 | DA1：Inbox 模式（强制）下，从启动到第一个提示符的时间 < 500 ms；把应答路径关掉时约 3 s（证明应答确实生效） | Windows | — |
| W-T2 | 9001/1004：输出中包含 `ESC[?9001h`、`ESC[?1004h`；以 9001 编码发送 `a` 的按下和抬起，`cmd` 收到 `a` | Windows | — |
| W-T3 | 关闭不死锁：`cmd /c type 大文件` 持续输出，消费方停止读取后调用 Close | Windows（Bundled 和强制 InboxNoRelease 各跑一遍） | Close 在 grace + 1 s 内完成 |
| W-T4 | Release 模式的 EOF：`cmd /c exit 5` | Windows | ExitCode = 5；Output 完成 |
| W-T5 | Unicode 环境变量：`TCODE_T=中文🙂`，执行 `cmd /c echo %TCODE_T%` | Windows | UTF-8 字节一致 |
| W-T6 | 发布布局：conpty.dll + `x64\` + `arm64\` 下的 OpenConsole.exe，`Implementation == Bundled` | Windows | — |
| AOT | `tests/aot-smoke`：AOT 发布一个控制台程序，跑 T1 + T5 + 关闭 | 6 个 RID | 0 个 IL 警告；退出码 0 |

**CI**（`.github/workflows/terminal-pty.yml`；runner 名称参考 MiniPty [实 MP build.yaml:26-40]）：

| Job | runner | 内容 |
|---|---|---|
| native-linux | ubuntu-24.04 | zig 交叉编译两个 Linux RID；check-glibc；产物上传为 artifact |
| native-macos | macos-26（arm64） | 产出两个 macOS RID 的 dylib、.a 和 helper |
| test-linux-x64 / arm64 | ubuntu-24.04 / ubuntu-24.04-arm | 下载 native 产物 → `dotnet test`（全部 Unix 用例，含 T16）+ ASan 构建 + AOT smoke |
| test-rhel8 | ubuntu-24.04 + `almalinux:8` 容器 | AOT smoke（在 glibc 2.28 sysroot 上发布的二进制）。容器内的内核仍是宿主内核，pidfd 回退路径靠 T16 的强制开关覆盖 |
| test-macos-arm64 / x64 | macos-26 / macos-26-intel（或 macos-15-intel） | 全部 Unix 用例 + T17 + AOT smoke |
| test-windows-x64 / arm64 | windows-2025 / windows-11-arm | W-T1..6；用 `LocalPtyOptions.ForceConPty = Inbox / InboxNoRelease` 覆盖降级路径（windows-2025 是 26100 构建，inbox 自带 Release） |
| 手动矩阵（发版前） | Windows 10 1809 / 21H2 虚拟机、RHEL 8 实机、macOS 15 | W-T3 的真实 inbox 死锁场景；内核 4.18 上的 pidfd 回退 |

GitHub runner 提供 `/dev/ptmx`；Docker 默认挂载 `/dev/pts`。

---

## 15. 风险与 spike 清单

| # | 待实测项 | 怎么测 | 失败时的后备方案 |
|---|---|---|---|
| S1 | ~~macOS login 包装~~（已取消：2026-10-17 决定不经 login） | — | — |
| S2 | Linux：helper 在 `adddup2` 了 O_NOCTTY slave 之后执行 TIOCSCTTY 成功；glibc 2.28 和 musl 在 sigdefault 含 SIGKILL 时不报错 | T2，在 RHEL 8 和 Alpine 容器中运行 | sigdefault 去掉 SIGKILL 和 SIGSTOP |
| S3 | macOS gather 参数的收益（1 KiB / 16 / 1 ms / 3 ms）；用户交互型 QoS | T11 对比实验 | 调整参数 |
| S4 | ConPTY 1.25：DECSET 2027、DECRQM 2027/9001、`CSI 14/16/18 t` 是由 conhost 自己应答还是透传；`0x03` 是否产生 CTRL_C_EVENT | 用 Windows 测试程序写入查询序列，比较输出 | 修改 10-protocol-compat 的应答表 |
| S5 | inbox 1809 / 21H2 上丢弃模式能否避免 Close 死锁 | W-T3 手动矩阵 | 在 Close 之前先 `CancelSynchronousIo` 并关闭 outRead |
| S6 | W2 的 0xc0000142 窗口实际有多长 | T13 在 Windows 上统计 | 调整 500 ms |
| S7 | 1809 是否支持 `PROC_THREAD_ATTRIBUTE_JOB_LIST`；运行时能否清除 `KILL_ON_JOB_CLOSE` | 单元测试 + 1809 虚拟机 | CREATE_SUSPENDED → AssignProcessToJobObject → ResumeThread |
| S8 | `ConptyRequires*Host` 在 net10.0 AOT 发布中是否把 OpenConsole 放进两个架构子目录 | W-T6 | `GeneratePathProperty` + 显式 `<None Link>` |
| S9 | 用 DirectPInvoke 把 libtcodepty.a 静态链接进 4 个 RID 的 AOT 二进制，与 libghostty-vt 没有符号冲突 | AOT smoke | 改用动态库 |
| S10 | pidfd 在 Docker 默认 seccomp 和 Flatpak 中是否可用 | T16 + `tcpty_init_info.pidfd_supported` | waitpid 线程回退（已实现） |
| S11 | 环境变量中有非 UTF-8 字节时的影响 | 构造 `LANG=` 和 GBK 值进行测试 | C shim 直接读取 host 原始的 `environ` |
| S12 | canonical 模式下长行粘贴被截断（macOS 1024、Linux 4095） | 往 `cat` 粘贴 8 KiB 单行 | 写进文档；多行粘贴警告已经覆盖大部分场景 |
| S13 | hardened runtime 和公证对 helper exec 用户 shell 的影响；TCC 权限提示归属（Desktop → host → shell 的"责任进程"） | 公证构建 + 访问 ~/Documents | 由 04-pty-host 决定 host 的启动方式（spawnattr 的 disclaim 或不 disclaim） |
| S14 | Release 模式下后台控制台客户端（`start /b`）对 EOF 的影响 | Windows 实测 | 与 Unix T9 采用同样的排空超时 |

其他风险：

| 风险 | 影响 | 缓解 |
|---|---|---|
| 两份核心的 9001 跟踪逻辑不在 libghostty 中 | Windows 上按键编码错误 | §7.7，在 02-core / 06-input 中落实 |
| macOS 上 openpt 到 fcntl 之间的 CLOEXEC 竞争 | 极少数情况下 fd 泄漏给并发 fork 的子进程 | 直接用 `open(O_CLOEXEC)`；host 内不用 Process |
| OpenConsole 缺失时静默回退 | 功能悄悄降级 | 加载前检查 + CI 断言 + 诊断日志 |
| libtcodepty 体积超过 01 的估计 | 维护成本 | 按功能拆成四个源文件，每个都有单元测试 |

---

## 16. 与 01 及其他文档的不一致、待决问题

| # | 问题 | 建议 |
|---|---|---|
| 1 | 01 §6.2 的 `PtyLaunch` 缺少：Windows 原样命令行、macOS login 包装提示、AmbiguousWide；`ITerminalConnection` 缺少应答专用的非阻塞写入、`ClearBuffer`、带 grace 参数的关闭、启动警告 | 采用 §4.1 的补充，并同步修改 01 §6.2 |
| 2 | ~~`TerminalSize` 的像素语义~~ | **已解决**：01 已统一为 cell 像素；本文 §4.1 已对齐。research/06 §6 的"总像素"写法已经过时 |
| 2a | 04 §1 的 `--detach` 模式由本文 §3 的 helper 实现。04 §4.4 `session/signal` 在 Windows 上把 KILL 写成"终止进程树"，本文 §6.3 只 `TerminateProcess(shell)`（与 Unix 只杀进程组对等） | 二选一后统一。建议用 `TerminateJobObject(会话 Job)`：只限于这个会话的树，符合"终止进程树"的说法 |
| 3 | ~~macOS login 包装吞掉退出码~~ | 已解决：不经 login（§8.3） |
| 4 | D9 "默认兼容宽度，收到 2027 再切换"与 ConPTY 不兼容：宽度在创建时固定，2027 是永久模式 [实] | Windows 默认 Grapheme 并锁定；Unix 保持 D9 不变 |
| 5 | 01 §6.1 的 `FrameSnapshot.Modes` 包含 9001，但 libghostty-vt 没有这个模式 [实] | 由 02-core 设计 TCode 侧的模式跟踪器 |
| 6 | research/06 §6 "Output 在 Exited 之前读到 EOF"在后台进程持有 slave 时做不到 | 采用 §6.1 的"EOF 或静默超时"语义 |
| 7 | research/02 §9.3 / U3 的 `trap - PIPE` 修复对 bash 无效 [实 local] | 采用 §12 的 helper `--exec` 方案 |
| 8 | research/02 §2.4 建议的 `PR_SET_PDEATHSIG` 跟随的是父**线程** | 不采用；由 master 关闭后的内核挂断兜底 |
| 9 | 01 估计 libtcodepty 约 300 行，本设计约 690 行 | 修改 01 §4 中的估计 |
| 10 | `tools/package_macos_app.sh` 的最低系统版本是 11.0，而 .NET 10 只支持 macOS 15 及以上 | 由 12-build 决定是否提高 |
| 11 | pty host 不应使用 `Process` / `Console`（R5），stdio 必须补齐（R8） | 写进 04-pty-host 的启动规范 |
