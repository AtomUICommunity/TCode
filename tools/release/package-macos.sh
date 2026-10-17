#!/usr/bin/env bash
# 把（已签名的）TCode.app 打成 pkg（应用内更新与首次安装）和 dmg（首次安装），可选公证（docs/design/auto-update.md §4、§11）。
# 用法: tools/release/package-macos.sh <TCode.app> <版本> <rid> <输出目录>
# 环境变量（都可省略；省略时生成未签名 / 未公证的包，只用于本地测试）：
#   MACOS_SIGN_IDENTITY       Developer ID Application（签 dmg）
#   MACOS_INSTALLER_IDENTITY  Developer ID Installer（签 pkg）
#   APPLE_API_KEY_PATH / APPLE_API_KEY_ID / APPLE_API_ISSUER_ID  notarytool 的 App Store Connect API key
set -euo pipefail
APP="$1"
VERSION="$2"
RID="$3"
OUT="$4"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PKG_DIR="$ROOT/src/TCode.Desktop/Packaging/macos"
mkdir -p "$OUT"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

PKG="$OUT/TCode-$VERSION-$RID.pkg"
DMG="$OUT/TCode-$VERSION-$RID.dmg"

# ---- pkg ----
mkdir -p "$WORK/root"
ditto "$APP" "$WORK/root/TCode.app"
pkgbuild --root "$WORK/root" --component-plist "$PKG_DIR/component.plist" --scripts "$PKG_DIR/scripts" \
    --identifier com.tcode.desktop.pkg --version "${VERSION%%-*}" --install-location /Applications "$WORK/component.pkg"
if [ -n "${MACOS_INSTALLER_IDENTITY:-}" ]; then
    productbuild --package "$WORK/component.pkg" --sign "$MACOS_INSTALLER_IDENTITY" --timestamp "$PKG"
else
    productbuild --package "$WORK/component.pkg" "$PKG"
fi

# ---- dmg：TCode.app + 指向 /Applications 的快捷方式 ----
mkdir -p "$WORK/dmg"
ditto "$APP" "$WORK/dmg/TCode.app"
ln -s /Applications "$WORK/dmg/Applications"
hdiutil create -volname TCode -srcfolder "$WORK/dmg" -ov -format UDZO "$DMG" >/dev/null
if [ -n "${MACOS_SIGN_IDENTITY:-}" ]; then
    codesign --force --timestamp --sign "$MACOS_SIGN_IDENTITY" "$DMG"
fi

# ---- 公证 + staple ----
if [ -n "${APPLE_API_KEY_PATH:-}" ] && [ -n "${APPLE_API_KEY_ID:-}" ] && [ -n "${APPLE_API_ISSUER_ID:-}" ]; then
    for f in "$PKG" "$DMG"; do
        xcrun notarytool submit "$f" --key "$APPLE_API_KEY_PATH" --key-id "$APPLE_API_KEY_ID" --issuer "$APPLE_API_ISSUER_ID" --wait
        xcrun stapler staple "$f"
    done
    pkgutil --check-signature "$PKG"
    spctl -a -t install -vv "$PKG"
fi

echo "已生成 $PKG"
echo "已生成 $DMG"
