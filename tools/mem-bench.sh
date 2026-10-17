#!/usr/bin/env bash
# 内存 / 空闲 CPU 基准：NativeAOT 发布 bench/TCode.MemoryHarness，在真实窗口中依次跑场景，结果追加到 artifacts/mem-bench.txt。
# 用法：tools/mem-bench.sh [rid]（默认按当前平台）。运行期间会打开窗口；每个场景约 25 秒。
# 指标：footprint（macOS 活动监视器的“内存”列）、相对启动后的增量、历史峰值、GC 堆，以及场景结束后安静窗口内的 CPU。
set -euo pipefail
cd "$(dirname "$0")/.."
rid="${1:-$(dotnet --info | awk '/RID:/ {print $2; exit}')}"
out_dir="artifacts/mem-bench/$rid"
# 桌面端发布会同时 AOT 发布 Agent Host（agenthost/），基准程序复用它
dotnet publish src/TCode.Desktop -c Release -r "$rid" -o "artifacts/mem-bench/$rid-desktop" 2>&1 | grep -E "warning IL|error" || true
dotnet publish bench/TCode.MemoryHarness -c Release -r "$rid" -o "$out_dir" 2>&1 | grep -E "warning IL|error" || true
exe="$out_dir/TCode.MemoryHarness"
[[ -x "$exe" ]] || exe="$out_dir/TCode.MemoryHarness.exe"
export TCODE_AGENTHOST_DIR="$PWD/artifacts/mem-bench/$rid-desktop/agenthost"
result="artifacts/mem-bench.txt"
mkdir -p artifacts
echo "# $(date -u +%Y-%m-%dT%H:%M:%SZ) $rid $(git rev-parse --short HEAD)" >> "$result"
warm="$(mktemp -d)"
run() { MH_OUT="$PWD/$result" MH_LABEL="$1" "${@:2}" "$exe" > /dev/null 2>&1; }
run idle           env MH_SCENARIO=idle
run markdown-cold  env MH_SCENARIO=markdown MH_HOME="$warm"
run markdown-warm  env MH_SCENARIO=markdown MH_HOME="$warm"
run chat           env MH_SCENARIO=chat
rm -rf "$warm"
tail -n 5 "$result"
