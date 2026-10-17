#!/usr/bin/env bash
# 构建 libtcodepty 与 tcode-pty-spawn（03-pty.md §11.1）。Windows 不使用。
# 产物：artifacts/native/<rid>/libtcodepty.{dylib,so}、libtcodepty.a、tcode-pty-spawn
# 用法：native/tcodepty/build.sh [rid]   （默认本机 RID；Linux 目标用 zig cc 交叉编译）
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
here="$root/native/tcodepty"
rid="${1:-}"
if [[ -z "$rid" ]]; then
  case "$(uname -s)" in Darwin) os=osx;; Linux) os=linux;; *) echo "unsupported host" >&2; exit 1;; esac
  case "$(uname -m)" in arm64|aarch64) arch=arm64;; *) arch=x64;; esac
  rid="$os-$arch"
fi
out="$root/artifacts/native/$rid"
obj="$root/artifacts/native-obj/tcodepty/$rid"
mkdir -p "$out" "$obj"
src=("$here/src/tcpty_spawn.c" "$here/src/tcpty_io.c" "$here/src/tcpty_exit.c" "$here/src/tcpty_proc.c")
cflags=(-std=c11 -O2 -fPIC -fvisibility=hidden -D_GNU_SOURCE -D_DARWIN_C_SOURCE -Wall -Wextra -Werror -I"$here/include")

case "$rid" in
  osx-*)
    arch="$([[ "$rid" == osx-arm64 ]] && echo arm64 || echo x86_64)"
    target=(-arch "$arch" -mmacosx-version-min=15.0)
    clang "${target[@]}" "${cflags[@]}" -dynamiclib -install_name @rpath/libtcodepty.dylib -o "$out/libtcodepty.dylib" "${src[@]}"
    objs=()
    for f in "${src[@]}"; do
      o="$obj/$(basename "$f" .c).o"; clang "${target[@]}" "${cflags[@]}" -c "$f" -o "$o"; objs+=("$o")
    done
    rm -f "$out/libtcodepty.a"; libtool -static -o "$out/libtcodepty.a" "${objs[@]}"
    clang "${target[@]}" -std=c11 -O2 -Wall -Wextra -Werror -o "$out/tcode-pty-spawn" "$here/helper/tcode-pty-spawn.c"
    ;;
  linux-*)
    zig="${TCODE_ZIG:-$("$root/tools/terminal/install-zig.sh" | tail -1)}"
    triple="$([[ "$rid" == linux-arm64 ]] && echo aarch64 || echo x86_64)"
    "$zig" cc -target "$triple-linux-gnu.2.28" "${cflags[@]}" -shared -Wl,-soname,libtcodepty.so -o "$out/libtcodepty.so" "${src[@]}"
    objs=()
    for f in "${src[@]}"; do
      o="$obj/$(basename "$f" .c).o"; "$zig" cc -target "$triple-linux-gnu.2.28" "${cflags[@]}" -c "$f" -o "$o"; objs+=("$o")
    done
    rm -f "$out/libtcodepty.a"; "$zig" ar rcs "$out/libtcodepty.a" "${objs[@]}"
    # helper 用 musl 完全静态链接，与目标机的 glibc 无关
    "$zig" cc -target "$triple-linux-musl" -std=c11 -O2 -static -s -D_GNU_SOURCE -Wall -Wextra -Werror -o "$out/tcode-pty-spawn" "$here/helper/tcode-pty-spawn.c"
    ;;
  *) echo "libtcodepty is Unix-only (rid $rid)" >&2; exit 1;;
esac
chmod 755 "$out/tcode-pty-spawn"
ls -la "$out"/libtcodepty.* "$out/tcode-pty-spawn"
