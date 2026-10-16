/* libtcodepty：读写、等待、resize（03-pty.md §2、§5）。 */
#include "tcpty_internal.h"

#include <poll.h>
#include <string.h>
#include <sys/ioctl.h>
#include <termios.h>
#include <time.h>
#if defined(__APPLE__)
#include <pthread.h>
#include <sys/qos.h>
#endif

TCPTY_API int32_t tcpty_set_nonblock(int32_t fd, int32_t enable) {
  /* fcntl 是可变参数函数，Apple arm64 上不能直接 P/Invoke（research/02 §1.5），所以放在这里 */
  int flags = fcntl(fd, F_GETFL);
  if (flags == -1) return tcpty_fail(errno);
  flags = enable ? (flags | O_NONBLOCK) : (flags & ~O_NONBLOCK);
  if (fcntl(fd, F_SETFL, flags) == -1) return tcpty_fail(errno);
  return TCPTY_OK;
}

TCPTY_API int32_t tcpty_close(int32_t fd) {
  /* close 被信号打断时 fd 已经释放（Linux、macOS 都是），不能重试 */
  if (close(fd) != 0 && errno != EINTR) return tcpty_fail(errno);
  return TCPTY_OK;
}

TCPTY_API int32_t tcpty_wake_create(int32_t *read_fd, int32_t *write_fd) {
  int fds[2];
#if defined(__linux__)
  if (pipe2(fds, O_CLOEXEC | O_NONBLOCK) != 0) return tcpty_fail(errno);
#else
  if (pipe(fds) != 0) return tcpty_fail(errno);
  for (int i = 0; i < 2; i++) {
    fcntl(fds[i], F_SETFD, FD_CLOEXEC);
    fcntl(fds[i], F_SETFL, fcntl(fds[i], F_GETFL) | O_NONBLOCK);
  }
#endif
  int r = tcpty_fd_high(fds[0]);
  int w = tcpty_fd_high(fds[1]);
  if (r < 0 || w < 0) {
    int err = errno;
    tcpty_close_quiet(r);
    tcpty_close_quiet(w);
    return tcpty_fail(err);
  }
  *read_fd = r;
  *write_fd = w;
  return TCPTY_OK;
}

TCPTY_API int32_t tcpty_wake_signal(int32_t write_fd) {
  uint8_t b = 1;
  for (;;) {
    ssize_t n = write(write_fd, &b, 1);
    if (n == 1 || (n < 0 && errno == EAGAIN)) return TCPTY_OK;  /* 管道已满也算已唤醒（粘性） */
    if (n < 0 && errno == EINTR) continue;
    return tcpty_fail(errno);
  }
}

TCPTY_API int32_t tcpty_read(int32_t fd, uint8_t *buf, int32_t cap) {
  for (;;) {
    ssize_t n = read(fd, buf, (size_t)cap);
    if (n >= 0) return (int32_t)n;
    if (errno == EINTR) continue;
    if (errno == EIO) return 0;  /* Linux：最后一个 slave 关闭后 read(master) 返回 EIO，等同 EOF */
    return tcpty_fail(errno);
  }
}

TCPTY_API int32_t tcpty_write(int32_t fd, const uint8_t *buf, int32_t len) {
  for (;;) {
    ssize_t n = write(fd, buf, (size_t)len);
    if (n >= 0) return (int32_t)n;
    if (errno == EINTR) continue;
    return tcpty_fail(errno);
  }
}

static int poll_retry(struct pollfd *fds, nfds_t n, int timeout_ms) {
  for (;;) {
    int r = poll(fds, n, timeout_ms);
    if (r >= 0 || errno != EINTR) return r;
  }
}

TCPTY_API int32_t tcpty_wait(int32_t fd, int32_t wake_fd, int32_t events, int32_t timeout_ms, uint32_t *revents) {
  struct pollfd p[2];
  nfds_t n = 1;
  p[0].fd = fd;
  p[0].events = (short)(((events & TCPTY_POLL_IN) ? POLLIN : 0) | ((events & TCPTY_POLL_OUT) ? POLLOUT : 0));
  p[0].revents = 0;
  if (wake_fd >= 0) {
    p[1].fd = wake_fd;
    p[1].events = POLLIN;
    p[1].revents = 0;
    n = 2;
  }
  int r = poll_retry(p, n, timeout_ms);
  if (r < 0) return tcpty_fail(errno);
  uint32_t out = 0;
  if (p[0].revents & POLLIN) out |= TCPTY_POLL_IN;
  if (p[0].revents & POLLOUT) out |= TCPTY_POLL_OUT;
  if (p[0].revents & (POLLHUP | POLLERR | POLLNVAL)) out |= TCPTY_POLL_HUP;
  if (n == 2 && (p[1].revents & (POLLIN | POLLHUP))) out |= TCPTY_POLL_WOKEN;
  if (revents != NULL) *revents = out;
  return r > 0 ? 1 : 0;
}

static long elapsed_us(const struct timespec *since) {
  struct timespec now;
  clock_gettime(CLOCK_MONOTONIC, &now);
  return (now.tv_sec - since->tv_sec) * 1000000L + (now.tv_nsec - since->tv_nsec) / 1000L;
}

/* Ghostty 的 gather 读（research/02 §2.7）：macOS 每次 read 只给约 1 KiB，
 * 数据流饱和时继续读到 EAGAIN / 缓冲区满 / 时间预算用完，攒成一批再交给解析。 */
TCPTY_API int32_t tcpty_read_gather(int32_t fd, int32_t wake_fd, uint8_t *buf, int32_t cap,
                                    const tcpty_gather_config *cfg, uint32_t *flags_out) {
  int32_t bridge = cfg ? cfg->bridge_threshold : 1024;
  int32_t spin_max = cfg ? cfg->spin_max : 16;
  int32_t bridge_poll_ms = cfg ? cfg->bridge_poll_ms : 1;
  long budget_us = cfg ? cfg->budget_us : 3000;
  uint32_t flags = 0;
  int32_t total = 0;

  /* 1. 阻塞等待第一批数据（或唤醒） */
  for (;;) {
    ssize_t n = read(fd, buf, (size_t)cap);
    if (n > 0) { total = (int32_t)n; break; }
    if (n == 0 || (n < 0 && errno == EIO)) { flags |= TCPTY_GATHER_EOF; goto done; }
    if (errno == EINTR) continue;
    if (errno != EAGAIN) return tcpty_fail(errno);
    uint32_t rev = 0;
    int32_t w = tcpty_wait(fd, wake_fd, TCPTY_POLL_IN, -1, &rev);
    if (w < 0) return w;
    if (rev & TCPTY_POLL_WOKEN) { flags |= TCPTY_GATHER_WOKEN; goto done; }
  }

  /* 2. 饱和时继续攒批 */
  struct timespec start;
  clock_gettime(CLOCK_MONOTONIC, &start);
  int spins = 0;
  while (total < cap && total >= bridge && elapsed_us(&start) < budget_us) {
    ssize_t n = read(fd, buf + total, (size_t)(cap - total));
    if (n > 0) { total += (int32_t)n; spins = 0; continue; }
    if (n == 0 || (n < 0 && errno == EIO)) { flags |= TCPTY_GATHER_EOF; break; }
    if (errno == EINTR) continue;
    if (errno != EAGAIN) break;
    if (spins++ < spin_max) continue;
    uint32_t rev = 0;
    if (tcpty_wait(fd, wake_fd, TCPTY_POLL_IN, bridge_poll_ms, &rev) <= 0) break;
    if (rev & TCPTY_POLL_WOKEN) { flags |= TCPTY_GATHER_WOKEN; break; }
    spins = 0;
  }

done:
  if (flags_out != NULL) *flags_out = flags;
  return total;
}

TCPTY_API int32_t tcpty_resize(int32_t fd, uint16_t cols, uint16_t rows, uint16_t xpixel, uint16_t ypixel) {
  struct winsize ws = { rows, cols, xpixel, ypixel };
  if (ioctl(fd, TIOCSWINSZ, &ws) != 0) return tcpty_fail(errno);
  return TCPTY_OK;
}

TCPTY_API int32_t tcpty_get_winsize(int32_t fd, uint16_t out[4]) {
  struct winsize ws;
  if (ioctl(fd, TIOCGWINSZ, &ws) != 0) return tcpty_fail(errno);
  out[0] = ws.ws_col;
  out[1] = ws.ws_row;
  out[2] = ws.ws_xpixel;
  out[3] = ws.ws_ypixel;
  return TCPTY_OK;
}

TCPTY_API void tcpty_set_thread_qos_interactive(void) {
#if defined(__APPLE__)
  pthread_set_qos_class_self_np(QOS_CLASS_USER_INITIATED, 0);
#endif
}

TCPTY_API int32_t tcpty_get_termios_state(int32_t fd, uint32_t *flags) {
  struct termios t;
  if (tcgetattr(fd, &t) != 0) return tcpty_fail(errno);
  uint32_t f = 0;
  if (t.c_lflag & ECHO) f |= TCPTY_TERMIOS_ECHO;
  if (t.c_lflag & ICANON) f |= TCPTY_TERMIOS_ICANON;
  if (t.c_lflag & ISIG) f |= TCPTY_TERMIOS_ISIG;
  *flags = f;
  return TCPTY_OK;
}
