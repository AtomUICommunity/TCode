#!/usr/bin/env bash
# 把 AOT 发布产物打成 TKCode.app（Dock / Finder / Cmd+Tab 显示 app.icns）。
# 用法: tools/package_macos_app.sh [rid=osx-arm64] [输出目录=artifacts] [版本=0.0.0-dev]
# 设置 MACOS_SIGN_IDENTITY（例如 "Developer ID Application: Name (TEAMID)"）时由内向外签名（hardened runtime），否则不签名。
# 发布流程见 docs/design/auto-update.md §11；zip / dmg / 公证由 tools/release/package-macos.sh 完成。
set -euo pipefail
cd "$(dirname "$0")/.."
RID="${1:-osx-arm64}"
OUT="${2:-artifacts}"
VERSION="${3:-0.0.0-dev}"
PUB="$OUT/publish/$RID"
APP="$OUT/TKCode.app"

dotnet publish src/TKCode.Desktop/TKCode.Desktop.csproj -c Release -r "$RID" -p:Version="$VERSION" -o "$PUB"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUB/." "$APP/Contents/MacOS/"
# 调试符号与 AOT 用不到的 runtimeconfig 不进安装包（Contents/MacOS 里只留代码，签名才不会报未签名的子组件）
find "$APP/Contents/MacOS" \( -name '*.pdb' -o -name '*.runtimeconfig.json' \) -delete
find "$APP/Contents/MacOS" -name '*.dSYM' -type d -prune -exec rm -rf {} +
cp src/TKCode.Desktop/Packaging/app.icns "$APP/Contents/Resources/app.icns"

# CFBundleVersion 只允许数字段：预发布版本（1.2.0-beta.1）取 1.2.0
SHORT="${VERSION%%-*}"
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>TKCode</string>
    <key>CFBundleDisplayName</key><string>TKCode</string>
    <key>CFBundleIdentifier</key><string>com.tkcode.desktop</string>
    <key>CFBundleExecutable</key><string>TKCode</string>
    <key>CFBundleIconFile</key><string>app</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>$SHORT</string>
    <key>CFBundleVersion</key><string>$SHORT</string>
    <key>LSMinimumSystemVersion</key><string>11.0</string>
    <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

if [ -n "${MACOS_SIGN_IDENTITY:-}" ]; then
    # 由内向外：先签每个嵌套的 Mach-O（dylib、agenthost、ptyhost、spawn helper），最后签整个包；不用 --deep
    while IFS= read -r f; do
        if file -b "$f" | grep -q 'Mach-O'; then
            codesign --force --timestamp --options runtime --sign "$MACOS_SIGN_IDENTITY" "$f"
        fi
    done < <(find "$APP/Contents/MacOS" -type f ! -path "$APP/Contents/MacOS/TKCode")
    codesign --force --timestamp --options runtime --sign "$MACOS_SIGN_IDENTITY" "$APP"
    codesign --verify --strict --verbose=2 "$APP"
fi

echo "已生成 $APP"
