/* libtcodepty：子进程退出与信号（03-pty.md §6、§10）。
 * 与 .NET 运行时共存的规则：只 waitpid(pid)，绝不 waitpid(-1)（research/02 §2.5）。 */
#include "tcpty_internal.h"

#include <pthread.h>
#include <signal.h>
#include <stdio.h>
#include <string.h>
#include <sys/wait.h>
#if defined(__linux__)
#include <sys/epoll.h>
#include <sys/syscall.h>
#elif defined(__APPLE__)
#include <sys/event.h>
#endif

TCPTY_API int32_t tcpty_decode_wait_status(int32_t raw, tcpty_exit_status *out) {
  memset(out, 0, sizeof *out);
  out->size = sizeof *out;
  out->raw = raw;
  if (WIFEXITED(raw)) {
    out->kind = TCPTY_EXIT_CODE;
    out->value = WEXITSTATUS(raw);
  } else if (WIFSIGNALED(raw)) {
    out->kind = TCPTY_EXIT_SIGNAL;
    out->value = WTERMSIG(raw);
#ifdef WCOREDUMP
    out->core_dumped = WCOREDUMP(raw) ? 1 : 0;
#endif
  } else {
    out->kind = TCPTY_EXIT_UNKNOWN;
  }
  return TCPTY_OK;
}

TCPTY_API int32_t tcpty_reap(int32_t pid, int32_t block, tcpty_exit_status *out) {
  int st = 0;
  for (;;) {
    pid_t r = waitpid((pid_t)pid, &st, block ? 0 : WNOHANG);
    if (r == pid) {
      if (out != NULL) tcpty_decode_wait_status(st, out);
      return 1;
    }
    if (r == 0) return 0;
    if (errno == EINTR) continue;
    if (errno == ECHILD && out != NULL) {
      /* 已被别人回收（pid 1 容器或 SIGCHLD=SIG_IGN 时 .NET 会 waitpid(-1)）：退出码未知 */
      memset(out, 0, sizeof *out);
      out->size = sizeof *out;
      out->kind = TCPTY_EXIT_UNKNOWN;
    }
    return tcpty_fail(errno);
  }
}

/* ───────── 进程级退出监视 ───────── */

static pthread_mutex_t g_watch_mutex = PTHREAD_MUTEX_INITIALIZER;
static int g_watch_fd = -1;
static int g_wake[2] = { -1, -1 };

TCPTY_API int32_t tcpty_exitwatch_init(void) {
  pthread_mutex_lock(&g_watch_mutex);
  int32_t rc = TCPTY_OK;
  if (g_watch_fd < 0) {
    if (tcpty_wake_create(&g_wake[0], &g_wake[1]) != TCPTY_OK) {
      rc = tcpty_fail(tcpty_tls_errno);
      goto out;
    }
#if defined(__linux__)
    int fd = epoll_create1(EPOLL_CLOEXEC);
    if (fd < 0) { rc = tcpty_fail(errno); goto out; }
    struct epoll_event ev = { .events = EPOLLIN, .data = { .u64 = (uint64_t)-1 } };
    epoll_ctl(fd, EPOLL_CTL_ADD, g_wake[0], &ev);
#else
    int fd = kqueue();
    if (fd < 0) { rc = tcpty_fail(errno); goto out; }
    fcntl(fd, F_SETFD, FD_CLOEXEC);
    struct kevent kev;
    EV_SET(&kev, g_wake[0], EVFILT_READ, EV_ADD, 0, 0, NULL);
    kevent(fd, &kev, 1, NULL, 0, NULL);
#endif
    g_watch_fd = fd;
  }
out:
  pthread_mutex_unlock(&g_watch_mutex);
  return rc;
}

TCPTY_API int32_t tcpty_exitwatch_add(int32_t pid, uint32_t flags) {
  (void)flags;
  if (g_watch_fd < 0) return tcpty_fail(EBADF);
#if defined(__linux__)
#if defined(SYS_pidfd_open)
  int pfd = (int)syscall(SYS_pidfd_open, (pid_t)pid, 0);
  if (pfd < 0) return tcpty_fail(errno);  /* ENOSYS（内核 < 5.3）/ EPERM（seccomp）→ 调用方改用 waitpid 线程；ESRCH → 已退出 */
  pfd = tcpty_fd_high(pfd);
  if (pfd < 0) return tcpty_fail(errno);
  /* 事件数据：高 32 位放 pidfd，低 32 位放 pid；一次性事件，触发后由 wait 关闭 pidfd */
  struct epoll_event ev = { .events = EPOLLIN | EPOLLONESHOT, .data = { .u64 = ((uint64_t)(uint32_t)pfd << 32) | (uint32_t)pid } };
  if (epoll_ctl(g_watch_fd, EPOLL_CTL_ADD, pfd, &ev) != 0) {
    int err = errno;
    close(pfd);
    return tcpty_fail(err);
  }
  return TCPTY_OK;
#else
  return tcpty_fail(ENOSYS);
#endif
#else
  struct kevent kev;
  EV_SET(&kev, (uintptr_t)pid, EVFILT_PROC, EV_ADD | EV_ONESHOT, NOTE_EXIT | NOTE_EXITSTATUS, 0, NULL);
  if (kevent(g_watch_fd, &kev, 1, NULL, 0, NULL) != 0) return tcpty_fail(errno);  /* ESRCH：已退出（僵尸竞态） */
  return TCPTY_OK;
#endif
}

TCPTY_API int32_t tcpty_exitwatch_wait(tcpty_exit_event *events, int32_t cap, int32_t timeout_ms) {
  if (g_watch_fd < 0) return tcpty_fail(EBADF);
  if (cap > 64) cap = 64;
  int count = 0;
#if defined(__linux__)
  struct epoll_event evs[64];
  int n;
  do {
    n = epoll_wait(g_watch_fd, evs, cap, timeout_ms);
  } while (n < 0 && errno == EINTR);
  if (n < 0) return tcpty_fail(errno);
  for (int i = 0; i < n; i++) {
    if (evs[i].data.u64 == (uint64_t)-1) {
      uint8_t drain[64];
      while (read(g_wake[0], drain, sizeof drain) > 0) {}
      continue;
    }
    int pfd = (int)(evs[i].data.u64 >> 32);
    epoll_ctl(g_watch_fd, EPOLL_CTL_DEL, pfd, NULL);
    close(pfd);
    events[count].pid = (int32_t)(uint32_t)(evs[i].data.u64 & 0xffffffffu);
    events[count].has_status = 0;  /* 调用方随后 waitpid(pid) 取状态 */
    events[count].status_raw = 0;
    count++;
  }
#else
  struct kevent evs[64];
  struct timespec ts, *tp = NULL;
  if (timeout_ms >= 0) {
    ts.tv_sec = timeout_ms / 1000;
    ts.tv_nsec = (long)(timeout_ms % 1000) * 1000000L;
    tp = &ts;
  }
  int n;
  do {
    n = kevent(g_watch_fd, NULL, 0, evs, cap, tp);
  } while (n < 0 && errno == EINTR);
  if (n < 0) return tcpty_fail(errno);
  for (int i = 0; i < n; i++) {
    if (evs[i].filter == EVFILT_READ) {
      uint8_t drain[64];
      while (read(g_wake[0], drain, sizeof drain) > 0) {}
      continue;
    }
    if (evs[i].filter == EVFILT_PROC && (evs[i].fflags & NOTE_EXIT)) {
      events[count].pid = (int32_t)evs[i].ident;
      events[count].has_status = (evs[i].fflags & NOTE_EXITSTATUS) ? 1 : 0;
      events[count].status_raw = (int32_t)evs[i].data;
      count++;
    }
  }
#endif
  return count;
}

TCPTY_API int32_t tcpty_exitwatch_wake(void) {
  if (g_wake[1] < 0) return tcpty_fail(EBADF);
  return tcpty_wake_signal(g_wake[1]);
}

/* ───────── 信号 ───────── */

TCPTY_API int32_t tcpty_signal_number(int32_t portable) {
  switch (portable) {
    case TCPTY_SIG_HUP: return SIGHUP;
    case TCPTY_SIG_INT: return SIGINT;
    case TCPTY_SIG_QUIT: return SIGQUIT;
    case TCPTY_SIG_KILL: return SIGKILL;
    case TCPTY_SIG_TERM: return SIGTERM;
    case TCPTY_SIG_CONT: return SIGCONT;
    case TCPTY_SIG_STOP: return SIGSTOP;
    case TCPTY_SIG_TSTP: return SIGTSTP;
    case TCPTY_SIG_WINCH: return SIGWINCH;
    case TCPTY_SIG_USR1: return SIGUSR1;
    case TCPTY_SIG_USR2: return SIGUSR2;
    default: return tcpty_fail(EINVAL);
  }
}

TCPTY_API int32_t tcpty_signal_name(int32_t signo, char *buf, int32_t cap) {
  static const struct { int no; const char *name; } names[] = {
    { SIGHUP, "SIGHUP" }, { SIGINT, "SIGINT" }, { SIGQUIT, "SIGQUIT" }, { SIGILL, "SIGILL" },
    { SIGTRAP, "SIGTRAP" }, { SIGABRT, "SIGABRT" }, { SIGBUS, "SIGBUS" }, { SIGFPE, "SIGFPE" },
    { SIGKILL, "SIGKILL" }, { SIGUSR1, "SIGUSR1" }, { SIGSEGV, "SIGSEGV" }, { SIGUSR2, "SIGUSR2" },
    { SIGPIPE, "SIGPIPE" }, { SIGALRM, "SIGALRM" }, { SIGTERM, "SIGTERM" }, { SIGCHLD, "SIGCHLD" },
    { SIGCONT, "SIGCONT" }, { SIGSTOP, "SIGSTOP" }, { SIGTSTP, "SIGTSTP" }, { SIGTTIN, "SIGTTIN" },
    { SIGTTOU, "SIGTTOU" }, { SIGXCPU, "SIGXCPU" }, { SIGXFSZ, "SIGXFSZ" }, { SIGWINCH, "SIGWINCH" },
  };
  char tmp[16];
  const char *name = NULL;
  for (size_t i = 0; i < sizeof names / sizeof names[0]; i++)
    if (names[i].no == signo) { name = names[i].name; break; }
  if (name == NULL) {
    snprintf(tmp, sizeof tmp, "SIG%d", signo);
    name = tmp;
  }
  return (int32_t)snprintf(buf, buf != NULL ? (size_t)cap : 0, "%s", name);
}

TCPTY_API int32_t tcpty_kill(int32_t pid, int32_t signo) {
  if (pid <= 0) return tcpty_fail(EINVAL);
  if (kill((pid_t)pid, signo) != 0) return tcpty_fail(errno);
  return TCPTY_OK;
}

TCPTY_API int32_t tcpty_killpg(int32_t pgid, int32_t signo) {
  if (pgid <= 1) return tcpty_fail(EINVAL);  /* 防止误发给 init 或整个会话 */
  if (kill(-(pid_t)pgid, signo) != 0) return tcpty_fail(errno);
  return TCPTY_OK;
}
