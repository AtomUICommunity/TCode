/* libtcodepty 内部公共部分：错误码映射、线程局部 errno、fd 提升。不导出。 */
#ifndef TCPTY_INTERNAL_H
#define TCPTY_INTERNAL_H

#include "tcodepty.h"
#include <errno.h>
#include <fcntl.h>
#include <stdint.h>
#include <unistd.h>

extern _Thread_local int32_t tcpty_tls_errno;

/* 记录原始 errno 并返回可移植错误码。 */
static inline int32_t tcpty_fail(int err) {
  tcpty_tls_errno = err;
  switch (err) {
    case EAGAIN:
#if EWOULDBLOCK != EAGAIN
    case EWOULDBLOCK:
#endif
      return TCPTY_E_AGAIN;
    case EIO: case EPIPE: case ECONNRESET: return TCPTY_E_IO;
    case EPERM: case EACCES: return TCPTY_E_PERM;
    case ENOENT: return TCPTY_E_NOENT;
    case ESRCH: return TCPTY_E_SRCH;
    case ECHILD: return TCPTY_E_CHILD;
    case ENOSYS: case ENOTSUP:
#if defined(EOPNOTSUPP) && EOPNOTSUPP != ENOTSUP
    case EOPNOTSUPP:
#endif
      return TCPTY_E_NOSYS;
    case EINVAL: return TCPTY_E_INVAL;
    case ENOMEM: return TCPTY_E_NOMEM;
    case EBADF: return TCPTY_E_BADF;
    case ENXIO: return TCPTY_E_NXIO;
    case ETIMEDOUT: return TCPTY_E_TIMEDOUT;
    case EMFILE: case ENFILE: return TCPTY_E_MFILE;
    case ENOTTY: return TCPTY_E_NOTTY;
    default: return TCPTY_E_OTHER;
  }
}

/* 把 fd 提升到 ≥ 10 并带 CLOEXEC，关闭原 fd（03 §2.2：避免与 0–3 冲突）。失败返回 -1，errno 已设置。 */
static inline int tcpty_fd_high(int fd) {
  if (fd >= 10) return fd;
  int high = fcntl(fd, F_DUPFD_CLOEXEC, 10);
  int saved = errno;
  close(fd);
  errno = saved;
  return high;
}

static inline void tcpty_close_quiet(int fd) {
  if (fd >= 0) {
    int saved = errno;
    close(fd);
    errno = saved;
  }
}

#endif
