#!/usr/bin/env bash
# 下载并校验 esctest2（native/ghostty/esctest.json），解压到 artifacts/esctest2/，输出 esctest 目录路径。
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
pin="$root/native/ghostty/esctest.json"
read -r url sha commit < <(python3 -c 'import json,sys; p=json.load(open(sys.argv[1])); print(p["url"], p["sha256"], p["commit"])' "$pin")
dest="$root/artifacts/esctest2"
dir="$dest/esctest2-$commit/esctest"
if [[ ! -f "$dir/esctest.py" ]]; then
  mkdir -p "$dest"
  tmp="$(mktemp -d)"
  curl -fsSL -o "$tmp/esctest2.tar.gz" "$url"
  actual="$(shasum -a 256 "$tmp/esctest2.tar.gz" 2>/dev/null || sha256sum "$tmp/esctest2.tar.gz")"
  [[ "${actual%% *}" == "$sha" ]] || { echo "esctest2 sha256 mismatch: ${actual%% *}" >&2; exit 1; }
  tar -xzf "$tmp/esctest2.tar.gz" -C "$dest"
  rm -rf "$tmp"
fi
echo "$dir"
