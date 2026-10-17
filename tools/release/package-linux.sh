#!/usr/bin/env bash
# 把 Linux 的 AOT 发布目录打成 tar.gz（顶层目录为 TKCode/，docs/design/auto-update.md §4、§7.3）。
# 用法: tools/release/package-linux.sh <发布目录> <版本> <rid> <输出目录>
set -euo pipefail
PUB="$1"
VERSION="$2"
RID="$3"
OUT="$4"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
mkdir -p "$OUT"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
cp -R "$PUB" "$WORK/TKCode"
find "$WORK/TKCode" \( -name '*.pdb' -o -name '*.dbg' -o -name '*.runtimeconfig.json' \) -delete
mkdir -p "$WORK/TKCode/share/icons"
cp -R "$ROOT/src/TKCode.Desktop/Packaging/linux/hicolor" "$WORK/TKCode/share/icons/"
chmod +x "$WORK/TKCode/TKCode" "$WORK/TKCode/agenthost/TKCode.AgentHost" "$WORK/TKCode/ptyhost/tkcode-ptyhost"
find "$WORK/TKCode" -name tkcode-pty-spawn -exec chmod +x {} +
# COPYFILE_DISABLE：在 macOS 上本地打包时不写 ._* 附加文件（安装时要求顶层只有 TKCode/）
COPYFILE_DISABLE=1 tar -C "$WORK" -czf "$OUT/TKCode-$VERSION-$RID.tar.gz" TKCode
echo "已生成 $OUT/TKCode-$VERSION-$RID.tar.gz"
