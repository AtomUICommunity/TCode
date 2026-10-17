/* libtcodepty：初始化与进程启动（03-pty.md §2、§2.2、§2.3）。 */
#include "tcpty_internal.h"

#include <poll.h>
#include <pthread.h>
#include <signal.h>
#include <spawn.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/wait.h>
#include <termios.h>
#include <time.h>
#if defined(__linux__)
#include <sys/syscall.h>
#endif

_Thread_local int32_t tcpty_tls_errno;

static pthread_mutex_t g_spawn_mutex = PTHREAD_MUTEX_INITIALIZER;

TCPTY_API int32_t tcpty_version(void) { return (TCPTY_ABI_MAJOR << 16) | TCPTY_ABI_MINOR; }

TCPTY_API int32_t tcpty_last_errno(void) { return tcpty_tls_errno; }

TCPTY_API int32_t tcpty_strerror(int32_t raw_errno, char *buf, int32_t cap) {
  const char *s = strerror(raw_errno);
  int32_t len = (int32_t)strlen(s);
  if (buf != NULL && cap > 0) {
    int32_t n = len < cap - 1 ? len : cap - 1;
    memcpy(buf, s, (size_t)n);
    buf[n] = '\0';
  }
  return len;
}

TCPTY_API int32_t tcpty_init(tcpty_init_info *info) {
  tcpty_init_info local;
  memset(&local, 0, sizeof local);
  local.size = sizeof local;

  /* SIGCHLD 为 SIG_IGN 或带 SA_NOCLDWAIT 时内核会自动回收子进程，谁都拿不到退出码（03 §10）。 */
  struct sigaction sa;
  if (sigaction(SIGCHLD, NULL, &sa) == 0 && (sa.sa_handler == SIG_IGN || (sa.sa_flags & SA_NOCLDWAIT))) {
    local.sigchld_was_ignored = 1;
    struct sigaction dfl;
    memset(&dfl, 0, sizeof dfl);
    dfl.sa_handler = SIG_DFL;
    sigaction(SIGCHLD, &dfl, NULL);
  }

  /* fd 0–2 有空位时补上 /dev/null，防止之后打开的 PTY master 恰好占用 0–2（04 §15）。 */
  for (int fd = 0; fd <= 2; fd++) {
    if (fcntl(fd, F_GETFD) == -1 && errno == EBADF) {
      int n = open("/dev/null", O_RDWR);
      if (n >= 0 && n != fd) {
        dup2(n, fd);
        close(n);
      }
      local.stdio_repaired = 1;
    }
  }

#if defined(__linux__) && defined(SYS_pidfd_open)
  int pfd = (int)syscall(SYS_pidfd_open, getpid(), 0);
  if (pfd >= 0) {
    local.pidfd_supported = 1;
    close(pfd);
  }
#endif

  if (info != NULL) {
    uint32_t n = info->size < sizeof local ? info->size : (uint32_t)sizeof local;
    memcpy(info, &local, n);
    info->size = n;
  }
  return TCPTY_OK;
}

/* 打开 PTY 对：master 与 slave 都带 CLOEXEC 并提升到 ≥ 10。 */
static int32_t open_pty(int *master_out, int *slave_out, int32_t *stage) {
  int m = -1;
  for (int attempt = 0; attempt < 8; attempt++) {
    m = open("/dev/ptmx", O_RDWR | O_NOCTTY | O_CLOEXEC);
    if (m >= 0) break;
    /* macOS 并发 openpt 偶尔以 ENXIO（或非正 errno）失败，短暂重试（research/02 §2.3） */
    if (errno != ENXIO && errno > 0) break;
    struct timespec ts = { 0, 1000000 };
    nanosleep(&ts, NULL);
  }
  if (m < 0) { *stage = TCPTY_STAGE_OPENPT; return -1; }
  if (grantpt(m) != 0 || unlockpt(m) != 0) { *stage = TCPTY_STAGE_GRANT; tcpty_close_quiet(m); return -1; }

  char path[128];
#if defined(__APPLE__)
  if (ioctl(m, TIOCPTYGNAME, path) != 0) { *stage = TCPTY_STAGE_SLAVE; tcpty_close_quiet(m); return -1; }
#else
  if (ptsname_r(m, path, sizeof path) != 0) { *stage = TCPTY_STAGE_SLAVE; tcpty_close_quiet(m); return -1; }
#endif
  int s = open(path, O_RDWR | O_NOCTTY | O_CLOEXEC);
  if (s < 0) { *stage = TCPTY_STAGE_SLAVE; tcpty_close_quiet(m); return -1; }

  m = tcpty_fd_high(m);
  if (m < 0) { *stage = TCPTY_STAGE_OPENPT; tcpty_close_quiet(s); return -1; }
  s = tcpty_fd_high(s);
  if (s < 0) { *stage = TCPTY_STAGE_SLAVE; tcpty_close_quiet(m); return -1; }
  *master_out = m;
  *slave_out = s;
  return 0;
}

static int make_pipe(int fds[2]) {
#if defined(__linux__)
  if (pipe2(fds, O_CLOEXEC) != 0) return -1;
#else
  if (pipe(fds) != 0) return -1;
  fcntl(fds[0], F_SETFD, FD_CLOEXEC);
  fcntl(fds[1], F_SETFD, FD_CLOEXEC);
#endif
  int r = tcpty_fd_high(fds[0]);
  int w = tcpty_fd_high(fds[1]);
  if (r < 0 || w < 0) { tcpty_close_quiet(r); tcpty_close_quiet(w); return -1; }
  fds[0] = r;
  fds[1] = w;
  return 0;
}

static int init_attr(posix_spawnattr_t *attr, short extra_flags) {
  if (posix_spawnattr_init(attr) != 0) return -1;
  sigset_t all, none;
  sigfillset(&all);   /* 全集：清除从 .NET 继承的 SIGPIPE=SIG_IGN 等（research/02 §1.4） */
  sigemptyset(&none);
  short flags = POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETSIGMASK | extra_flags;
#if defined(__APPLE__)
  flags |= POSIX_SPAWN_CLOEXEC_DEFAULT;
#endif
  if (posix_spawnattr_setsigdefault(attr, &all) != 0 || posix_spawnattr_setsigmask(attr, &none) != 0 ||
      posix_spawnattr_setflags(attr, flags) != 0) {
    posix_spawnattr_destroy(attr);
    return -1;
  }
  return 0;
}

static int count_argv(const char *const *argv) {
  int n = 0;
  while (argv != NULL && argv[n] != NULL) n++;
  return n;
}

/* 读 helper 的状态记录直到 EOF（exec 成功）或致命错误。返回 0 成功，-1 失败（stage/error 已填）。 */
static int read_status(int fd, uint32_t timeout_ms, tcpty_spawn_result *r) {
  struct timespec start;
  clock_gettime(CLOCK_MONOTONIC, &start);
  for (;;) {
    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    long elapsed = (now.tv_sec - start.tv_sec) * 1000 + (now.tv_nsec - start.tv_nsec) / 1000000;
    long left = (long)timeout_ms - elapsed;
    if (left <= 0) { r->stage = TCPTY_STAGE_HELPER_PROTO; r->error = ETIMEDOUT; return -1; }
    struct pollfd p = { fd, POLLIN, 0 };
    int n = poll(&p, 1, (int)left);
    if (n < 0) { if (errno == EINTR) continue; r->stage = TCPTY_STAGE_HELPER_PROTO; r->error = errno; return -1; }
    if (n == 0) continue;
    int32_t rec[2];
    ssize_t got = read(fd, rec, sizeof rec);
    if (got < 0) { if (errno == EINTR) continue; r->stage = TCPTY_STAGE_HELPER_PROTO; r->error = errno; return -1; }
    if (got == 0) return 0;  /* EOF：fd 3 随 exec 关闭，目标程序已在运行 */
    if (got != (ssize_t)sizeof rec) { r->stage = TCPTY_STAGE_HELPER_PROTO; r->error = EPROTO; return -1; }
    if (rec[0] < 0) {
      r->warnings |= TCPTY_WARN_CWD_FALLBACK;
      r->warn_error = rec[1];
      continue;
    }
    r->stage = rec[0];
    r->error = rec[1];
    return -1;
  }
}

TCPTY_API int32_t tcpty_spawn(const tcpty_spawn_options *o, tcpty_spawn_result *r) {
  if (o == NULL || r == NULL || o->helper_path == NULL || o->file == NULL || o->argv == NULL || o->argv[0] == NULL ||
      o->envp == NULL || o->cols == 0 || o->rows == 0)
    return tcpty_fail(EINVAL);
  memset(r, 0, sizeof *r);
  r->size = sizeof *r;
  r->master_fd = -1;
  r->pid = -1;

  int m = -1, s = -1, status[2] = { -1, -1 };
  int32_t stage = 0;
  int err = 0;
  pid_t pid = -1;
  const char **argv_h = NULL;

  pthread_mutex_lock(&g_spawn_mutex);
  if (open_pty(&m, &s, &stage) != 0) { err = errno; goto fail_locked; }

  struct termios t;
  if (tcgetattr(s, &t) != 0) { err = errno; stage = TCPTY_STAGE_TERMIOS; goto fail_locked; }
  if (o->flags & TCPTY_SPAWN_IUTF8) {
#ifdef IUTF8
    t.c_iflag |= IUTF8;
#endif
    if (tcsetattr(s, TCSANOW, &t) != 0) { err = errno; stage = TCPTY_STAGE_TERMIOS; goto fail_locked; }
  }
  struct winsize ws = { o->rows, o->cols, o->xpixel, o->ypixel };
  if (ioctl(s, TIOCSWINSZ, &ws) != 0) { err = errno; stage = TCPTY_STAGE_WINSIZE; goto fail_locked; }

  if (make_pipe(status) != 0) { err = errno; stage = TCPTY_STAGE_PIPE; goto fail_locked; }

  posix_spawnattr_t attr;
  posix_spawn_file_actions_t fa;
  if (init_attr(&attr, POSIX_SPAWN_SETSID) != 0) { err = errno ? errno : ENOMEM; stage = TCPTY_STAGE_ATTR; goto fail_locked; }
  if (posix_spawn_file_actions_init(&fa) != 0) {
    posix_spawnattr_destroy(&attr);
    err = ENOMEM; stage = TCPTY_STAGE_ATTR; goto fail_locked;
  }
  posix_spawn_file_actions_adddup2(&fa, s, 0);
  posix_spawn_file_actions_adddup2(&fa, s, 1);
  posix_spawn_file_actions_adddup2(&fa, s, 2);
  posix_spawn_file_actions_adddup2(&fa, status[1], 3);

  /* helper 的 argv：[helper, cwd, file, argv0, args...] */
  int n = count_argv(o->argv);
  argv_h = calloc((size_t)n + 4, sizeof *argv_h);
  if (argv_h == NULL) {
    posix_spawn_file_actions_destroy(&fa);
    posix_spawnattr_destroy(&attr);
    err = ENOMEM; stage = TCPTY_STAGE_ATTR; goto fail_locked;
  }
  argv_h[0] = o->helper_path;
  argv_h[1] = o->cwd != NULL ? o->cwd : "";
  argv_h[2] = o->file;
  for (int i = 0; i < n; i++) argv_h[3 + i] = o->argv[i];

  int rc;
  do {
    rc = posix_spawn(&pid, o->helper_path, &fa, &attr, (char *const *)argv_h, (char *const *)o->envp);
  } while (rc == EINTR);
  posix_spawn_file_actions_destroy(&fa);
  posix_spawnattr_destroy(&attr);
  if (rc != 0) { err = rc; stage = TCPTY_STAGE_SPAWN; pid = -1; goto fail_locked; }
  pthread_mutex_unlock(&g_spawn_mutex);

  free(argv_h);
  argv_h = NULL;
  close(status[1]);
  status[1] = -1;
  close(s);  /* 父进程不关 slave 就永远读不到 EOF（research/02 U5） */
  s = -1;

  if (read_status(status[0], o->status_timeout_ms ? o->status_timeout_ms : 5000, r) != 0) {
    if (r->stage == TCPTY_STAGE_HELPER_PROTO) kill(pid, SIGKILL);
    int st;
    while (waitpid(pid, &st, 0) < 0 && errno == EINTR) {}
    close(status[0]);
    close(m);
    tcpty_tls_errno = r->error;
    r->pid = -1;
    return r->stage == TCPTY_STAGE_HELPER_PROTO && r->error == ETIMEDOUT ? TCPTY_E_TIMEDOUT : tcpty_fail(r->error);
  }
  close(status[0]);
  r->master_fd = m;
  r->pid = pid;
  return TCPTY_OK;

fail_locked:
  pthread_mutex_unlock(&g_spawn_mutex);
  free(argv_h);
  tcpty_close_quiet(status[0]);
  tcpty_close_quiet(status[1]);
  tcpty_close_quiet(s);
  tcpty_close_quiet(m);
  r->stage = stage;
  r->error = err;
  return tcpty_fail(err);
}

TCPTY_API int32_t tcpty_spawn_pipe(const tcpty_pipe_options *o, tcpty_pipe_result *r) {
  if (o == NULL || r == NULL || o->helper_path == NULL || o->file == NULL || o->argv == NULL || o->argv[0] == NULL ||
      o->envp == NULL)
    return tcpty_fail(EINVAL);
  memset(r, 0, sizeof *r);
  r->size = sizeof *r;
  r->pid = r->stdin_fd = r->stdout_fd = r->stderr_fd = -1;

  int in[2] = { -1, -1 }, out[2] = { -1, -1 }, errp[2] = { -1, -1 };
  int err = 0;
  pid_t pid = -1;
  const char **argv_h = NULL;

  pthread_mutex_lock(&g_spawn_mutex);
  if (make_pipe(in) != 0 || make_pipe(out) != 0 || make_pipe(errp) != 0) { err = errno; goto fail_locked; }

  posix_spawnattr_t attr;
  posix_spawn_file_actions_t fa;
  if (init_attr(&attr, 0) != 0) { err = errno ? errno : ENOMEM; goto fail_locked; }
  if (posix_spawn_file_actions_init(&fa) != 0) { posix_spawnattr_destroy(&attr); err = ENOMEM; goto fail_locked; }
  posix_spawn_file_actions_adddup2(&fa, in[0], 0);
  posix_spawn_file_actions_adddup2(&fa, out[1], 1);
  posix_spawn_file_actions_adddup2(&fa, errp[1], 2);

  /* helper --exec：[helper, "--exec", file, argv0, args...]；cwd 由 helper 之前的 posix_spawn 无法设置，
   * 因此用 "--exec-cwd" 变体传入（helper 支持） */
  int n = count_argv(o->argv);
  argv_h = calloc((size_t)n + 6, sizeof *argv_h);
  if (argv_h == NULL) { posix_spawn_file_actions_destroy(&fa); posix_spawnattr_destroy(&attr); err = ENOMEM; goto fail_locked; }
  int k = 0;
  argv_h[k++] = o->helper_path;
  if (o->cwd != NULL && o->cwd[0] != '\0') {
    argv_h[k++] = "--exec-cwd";
    argv_h[k++] = o->cwd;
  } else {
    argv_h[k++] = "--exec";
  }
  argv_h[k++] = o->file;
  for (int i = 0; i < n; i++) argv_h[k++] = o->argv[i];

  int rc;
  do {
    rc = posix_spawn(&pid, o->helper_path, &fa, &attr, (char *const *)argv_h, (char *const *)o->envp);
  } while (rc == EINTR);
  posix_spawn_file_actions_destroy(&fa);
  posix_spawnattr_destroy(&attr);
  if (rc != 0) { err = rc; goto fail_locked; }
  pthread_mutex_unlock(&g_spawn_mutex);
  free(argv_h);
  close(in[0]);
  close(out[1]);
  close(errp[1]);
  r->pid = pid;
  r->stdin_fd = in[1];
  r->stdout_fd = out[0];
  r->stderr_fd = errp[0];
  return TCPTY_OK;

fail_locked:
  pthread_mutex_unlock(&g_spawn_mutex);
  free(argv_h);
  for (int i = 0; i < 2; i++) { tcpty_close_quiet(in[i]); tcpty_close_quiet(out[i]); tcpty_close_quiet(errp[i]); }
  return tcpty_fail(err);
}
