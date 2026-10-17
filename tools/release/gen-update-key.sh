#!/usr/bin/env bash
# 生成更新清单的签名密钥（ECDSA P-256，docs/design/auto-update.md §4）。
# 私钥：写入 CI secret UPDATE_SIGNING_KEY，并离线备份；丢失后旧客户端无法再验证新版本。
# 公钥：粘贴到 src/TCode.Desktop/Updates/UpdateManifest.cs 的 ManifestVerifier.ReleaseKeys。
# 用法: tools/release/gen-update-key.sh [输出目录=~/.tcode-release]
set -euo pipefail
OUT="${1:-$HOME/.tcode-release}"
mkdir -p "$OUT"
chmod 700 "$OUT"
KEY="$OUT/update-signing-key.pem"
if [ -e "$KEY" ]; then
    echo "已存在 $KEY，不覆盖" >&2
    exit 1
fi
openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt -out "$KEY"
chmod 600 "$KEY"
openssl ec -in "$KEY" -pubout -out "$OUT/update-signing-key.pub.pem" 2>/dev/null
echo "私钥: $KEY"
echo "公钥:"
cat "$OUT/update-signing-key.pub.pem"
