/* tcode-pty-spawn：posix_spawn / Process.Start 之后、目标程序之前的一跳，exec 之后不运行任何托管代码。
 * PTY 模式：argv = [self, cwd, file, argv0, args...]；fd 0-2 = slave；fd 3 = 状态管道写端。
 * exec 模式：argv = [self, "--exec", file, argv0, args...]；复位全部信号后 execvp。
 *           [self, "--exec-cwd", cwd, file, argv0, args...] 先 chdir（tcpty_spawn_pipe 使用）。
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
    if (argc >= 5 && strcmp(argv[1], "--exec-cwd") == 0) {
        reset_signals();
        if (chdir(argv[2]) == -1) die(127, "chdir");
        execvp(argv[3], &argv[4]);
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
    if (argc < 4) report(101, EINVAL);                              /* [self, cwd, file, argv0]：argv0 必有，参数可以为空 */
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
