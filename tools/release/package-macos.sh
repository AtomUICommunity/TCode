#!/usr/bin/env bash
# 把（已签名的）TKCode.app 公证并 staple，再打成 zip（应用内更新）和 dmg（首次安装）（docs/design/auto-update.md §4、§11）。
# 用法: tools/release/package-macos.sh <TKCode.app> <版本> <rid> <输出目录>
# 环境变量（都可省略；省略时生成未签名 / 未公证的包，只用于本地测试）：
#   MACOS_SIGN_IDENTITY  Developer ID Application（签 dmg）
#   APPLE_ID / APPLE_APP_SPECIFIC_PASSWORD / APPLE_TEAM_ID  notarytool 的 Apple ID 凭据
set -euo pipefail
APP="$1"
VERSION="$2"
RID="$3"
OUT="$4"
mkdir -p "$OUT"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

ZIP="$OUT/TKCode-$VERSION-$RID.zip"
DMG="$OUT/TKCode-$VERSION-$RID.dmg"
NOTARIZE=false
if [ -n "${APPLE_ID:-}" ] && [ -n "${APPLE_APP_SPECIFIC_PASSWORD:-}" ] && [ -n "${APPLE_TEAM_ID:-}" ]; then
    NOTARIZE=true
fi

# 公证失败时 notarytool 也可能返回 0，以随后的 stapler staple 是否成功为准
notarize() {
    xcrun notarytool submit "$1" --apple-id "$APPLE_ID" --password "$APPLE_APP_SPECIFIC_PASSWORD" --team-id "$APPLE_TEAM_ID" --wait
}

# 公证通过后 stapler 要从 CloudKit 取票据，runner 上偶尔断网（Error 68）或票据还没同步完；重试几次，仍失败才算失败
staple() {
    local attempt
    for attempt in 1 2 3 4 5; do
        if xcrun stapler staple "$1"; then
            return 0
        fi
        echo "stapler staple 失败（第 $attempt 次），稍后重试" >&2
        sleep $((attempt * 10))
    done
    return 1
}

# ---- .app：先公证再 staple，zip 和 dmg 里都是 staple 过的 .app（离线也能通过 Gatekeeper） ----
if $NOTARIZE; then
    ditto -c -k --keepParent "$APP" "$WORK/notarize.zip"
    notarize "$WORK/notarize.zip"
    staple "$APP"
    spctl -a -t exec -vv "$APP"
fi

# ---- zip：顶层是 TKCode.app；ditto 保留符号链接与扩展属性，客户端也用 ditto 解压 ----
ditto -c -k --keepParent "$APP" "$ZIP"

# ---- dmg：TKCode.app + 指向 /Applications 的快捷方式 ----
mkdir -p "$WORK/dmg"
ditto "$APP" "$WORK/dmg/TKCode.app"
ln -s /Applications "$WORK/dmg/Applications"
hdiutil create -volname TKCode -srcfolder "$WORK/dmg" -ov -format UDZO "$DMG" >/dev/null
if [ -n "${MACOS_SIGN_IDENTITY:-}" ]; then
    codesign --force --timestamp --sign "$MACOS_SIGN_IDENTITY" "$DMG"
fi
if $NOTARIZE; then
    notarize "$DMG"
    staple "$DMG"
    spctl -a -t open --context context:primary-signature -vv "$DMG"
fi

echo "已生成 $ZIP"
echo "已生成 $DMG"
