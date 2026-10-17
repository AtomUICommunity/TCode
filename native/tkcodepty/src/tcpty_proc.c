/* libtkcodepty：进程信息（03-pty.md §8）。结构体布局因平台而异，只在 C 里处理。 */
#include "tcpty_internal.h"

#include <dirent.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <termios.h>
#if defined(__APPLE__)
#include <libproc.h>
#include <sys/proc_info.h>
#endif

/* 按 snprintf 语义把 s 写入 buf，返回完整长度。 */
static int32_t copy_out(const char *s, size_t len, char *buf, int32_t cap) {
  if (buf != NULL && cap > 0) {
    size_t n = len < (size_t)cap - 1 ? len : (size_t)cap - 1;
    memcpy(buf, s, n);
    buf[n] = '\0';
  }
  return (int32_t)len;
}

TCPTY_API int32_t tcpty_get_fg_pgrp(int32_t master_fd) {
  pid_t p = tcgetpgrp(master_fd);
  if (p < 0) return tcpty_fail(errno);
  return (int32_t)p;
}

TCPTY_API int32_t tcpty_get_pgid(int32_t pid) {
  pid_t p = getpgid((pid_t)pid);
  if (p < 0) return tcpty_fail(errno);
  return (int32_t)p;
}

TCPTY_API int32_t tcpty_get_sid(int32_t pid) {
  pid_t p = getsid((pid_t)pid);
  if (p < 0) return tcpty_fail(errno);
  return (int32_t)p;
}

TCPTY_API int32_t tcpty_get_process_path(int32_t pid, char *buf, int32_t cap) {
#if defined(__APPLE__)
  char path[PROC_PIDPATHINFO_MAXSIZE];
  int n = proc_pidpath(pid, path, sizeof path);
  if (n <= 0) return tcpty_fail(errno);
  return copy_out(path, (size_t)n, buf, cap);
#else
  char link[64], path[4096];
  snprintf(link, sizeof link, "/proc/%d/exe", pid);
  ssize_t n = readlink(link, path, sizeof path);
  if (n < 0) return tcpty_fail(errno);
  return copy_out(path, (size_t)n, buf, cap);
#endif
}

TCPTY_API int32_t tcpty_get_process_name(int32_t pid, char *buf, int32_t cap) {
#if defined(__APPLE__)
  char path[PROC_PIDPATHINFO_MAXSIZE];
  if (proc_pidpath(pid, path, sizeof path) > 0) {
    const char *base = strrchr(path, '/');
    base = base != NULL ? base + 1 : path;
    return copy_out(base, strlen(base), buf, cap);
  }
  char name[2 * MAXCOMLEN + 1];
  int n = proc_name(pid, name, sizeof name);
  if (n <= 0) return tcpty_fail(errno);
  return copy_out(name, (size_t)n, buf, cap);
#else
  char file[64], name[64];
  snprintf(file, sizeof file, "/proc/%d/comm", pid);
  int fd = open(file, O_RDONLY | O_CLOEXEC);
  if (fd < 0) return tcpty_fail(errno);
  ssize_t n = read(fd, name, sizeof name - 1);
  int err = errno;
  close(fd);
  if (n < 0) return tcpty_fail(err);
  while (n > 0 && (name[n - 1] == '\n' || name[n - 1] == '\0')) n--;
  return copy_out(name, (size_t)n, buf, cap);
#endif
}

TCPTY_API int32_t tcpty_get_cwd(int32_t pid, char *buf, int32_t cap) {
#if defined(__APPLE__)
  struct proc_vnodepathinfo info;
  int n = proc_pidinfo(pid, PROC_PIDVNODEPATHINFO, 0, &info, sizeof info);
  if (n != (int)sizeof info) return tcpty_fail(n < 0 ? errno : ESRCH);
  return copy_out(info.pvi_cdir.vip_path, strlen(info.pvi_cdir.vip_path), buf, cap);
#else
  char link[64], path[4096];
  snprintf(link, sizeof link, "/proc/%d/cwd", pid);
  ssize_t n = readlink(link, path, sizeof path);
  if (n < 0) return tcpty_fail(errno);
  return copy_out(path, (size_t)n, buf, cap);
#endif
}

TCPTY_API int32_t tcpty_list_children(int32_t pid, int32_t *pids, int32_t cap) {
#if defined(__APPLE__)
  int n = proc_listchildpids(pid, pids, cap * (int)sizeof(int32_t));
  if (n < 0) return tcpty_fail(errno);
  return n;  /* 返回的是个数（不是字节数，macOS 的实现如此） */
#else
  char file[96];
  snprintf(file, sizeof file, "/proc/%d/task/%d/children", pid, pid);
  FILE *f = fopen(file, "re");
  int count = 0;
  if (f != NULL) {
    long child;
    while (fscanf(f, "%ld", &child) == 1) {
      if (count < cap) pids[count] = (int32_t)child;
      count++;
    }
    fclose(f);
    return count;
  }
  /* 没有 children 文件（CONFIG_PROC_CHILDREN 未开启）：扫描 /proc/<n>/stat 的 ppid */
  DIR *d = opendir("/proc");
  if (d == NULL) return tcpty_fail(errno);
  for (struct dirent *e; (e = readdir(d)) != NULL;) {
    char *end;
    long p = strtol(e->d_name, &end, 10);
    if (*end != '\0' || p <= 0) continue;
    char stat[64], line[512];
    snprintf(stat, sizeof stat, "/proc/%ld/stat", p);
    FILE *s = fopen(stat, "re");
    if (s == NULL) continue;
    if (fgets(line, sizeof line, s) != NULL) {
      char *rp = strrchr(line, ')');  /* comm 可能包含空格和括号，取最后一个 ')' 之后的字段 */
      int ppid;
      char state;
      if (rp != NULL && sscanf(rp + 1, " %c %d", &state, &ppid) == 2 && ppid == pid) {
        if (count < cap) pids[count] = (int32_t)p;
        count++;
      }
    }
    fclose(s);
  }
  closedir(d);
  return count;
#endif
}
