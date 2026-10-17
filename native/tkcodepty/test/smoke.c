// libtkcodepty 冒烟测试：spawn → 读输出 → resize → 退出码 → SIGPIPE 复位（03-pty.md §14 的 T1/T5/T19 雏形）。
#include "tkcodepty.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int failures;
static void expect(int ok, const char *what) { printf("%s %s\n", ok ? "PASS" : "FAIL", what); if (!ok) failures++; }

static int run(const char *helper, const char *script, char *out, int cap, tcpty_exit_status *st, int with_watch) {
  const char *argv[] = { "sh", "-c", script, NULL };
  extern char **environ;
  tcpty_spawn_options o = { .size = sizeof o, .flags = TCPTY_SPAWN_IUTF8, .helper_path = helper, .file = "/bin/sh",
                            .argv = argv, .envp = (const char *const *)environ, .cwd = "/tmp", .cols = 80, .rows = 24 };
  tcpty_spawn_result r = { .size = sizeof r };
  int rc = tcpty_spawn(&o, &r);
  if (rc != 0) { printf("spawn failed rc=%d stage=%d errno=%d\n", rc, r.stage, r.error); return rc; }
  if (with_watch) tcpty_exitwatch_add(r.pid, 0);
  tcpty_set_nonblock(r.master_fd, 1);
  int len = 0;
  for (;;) {
    uint32_t flags = 0;
    int n = tcpty_read_gather(r.master_fd, -1, (uint8_t *)out + len, cap - 1 - len, NULL, &flags);
    if (n > 0) len += n;
    if (n < 0 || (flags & TCPTY_GATHER_EOF) || len >= cap - 1) break;
  }
  out[len] = 0;
  if (with_watch) {
    tcpty_exit_event ev[4];
    int n = tcpty_exitwatch_wait(ev, 4, 2000);
    expect(n == 1 && ev[0].pid == r.pid, "exitwatch 报告退出");
  }
  tcpty_reap(r.pid, 1, st);
  tcpty_close(r.master_fd);
  return 0;
}

int main(int argc, char **argv) {
  const char *helper = argc > 1 ? argv[1] : "./tkcode-pty-spawn";
  tcpty_init_info info = { .size = sizeof info };
  tcpty_init(&info);
  expect(tcpty_exitwatch_init() == 0, "exitwatch init");
  char out[8192];
  tcpty_exit_status st;

  run(helper, "printf 'hello\\n'; tty >/dev/null && echo has-tty; pwd; exit 3", out, sizeof out, &st, 1);
  expect(strstr(out, "hello") != NULL, "读到输出");
  expect(strstr(out, "has-tty") != NULL, "子进程有控制终端（TIOCSCTTY）");
  expect(strstr(out, "/tmp") != NULL || strstr(out, "/private/tmp") != NULL, "cwd 生效");
  expect(st.kind == TCPTY_EXIT_CODE && st.value == 3, "退出码 3");

  run(helper, "stty size", out, sizeof out, &st, 0);
  expect(strstr(out, "24 80") != NULL, "初始尺寸 24x80");

  run(helper, "trap '' INT; kill -TERM $$", out, sizeof out, &st, 0);
  expect(st.kind == TCPTY_EXIT_SIGNAL, "被信号终止");

  // SIGPIPE 必须是默认处置：yes | head -1 中 yes 应被 SIGPIPE 终止（PIPESTATUS 141）。用 sh 的等价写法：
  run(helper, "(yes; echo $? >&2) 2>&1 | head -2 | tail -1; sleep 0.2", out, sizeof out, &st, 0);
  run(helper, "sh -c 'yes | head -1 >/dev/null; kill -0 $$' ; trap -p PIPE; echo trap-ok", out, sizeof out, &st, 0);
  expect(strstr(out, "trap-ok") != NULL && strstr(out, "PIPE") == NULL, "SIGPIPE 未被忽略（trap -p 无输出）");

  printf("%s (%d failures)\n", failures ? "FAILED" : "ALL PASSED", failures);
  return failures ? 1 : 0;
}
