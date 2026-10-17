/* tkcodepty.h — libtkcodepty C API v1.0
 * 只在 linux-{x64,arm64}、osx-{x64,arm64} 上构建；Windows 不使用。 */
#ifndef TKCODEPTY_H
#define TKCODEPTY_H
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
  const char *helper_path;      /* 必填，tkcode-pty-spawn 的绝对路径 */
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

/* ───────── 管道模式启动（不分配 PTY；用于 ssh -G 等短命令，§2.3） ───────── */
typedef struct tcpty_pipe_options {
  uint32_t size;
  const char *helper_path;      /* tkcode-pty-spawn，以 --exec 模式启动：复位全部信号后 execvp */
  const char *file;
  const char *const *argv;      /* argv[0] 是 argv0 */
  const char *const *envp;
  const char *cwd;              /* 可为 NULL */
} tcpty_pipe_options;
typedef struct tcpty_pipe_result {
  uint32_t size;
  int32_t pid;
  int32_t stdin_fd;             /* 均为 O_CLOEXEC、≥ 10；调用方负责关闭 */
  int32_t stdout_fd;
  int32_t stderr_fd;
} tcpty_pipe_result;
TCPTY_API int32_t tcpty_spawn_pipe(const tcpty_pipe_options *opt, tcpty_pipe_result *res);

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
