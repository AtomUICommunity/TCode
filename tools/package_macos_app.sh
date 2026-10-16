#!/usr/bin/env bash
# 把 AOT 发布产物打成 TCode.app（Dock / Finder / Cmd+Tab 显示 app.icns）。
# 用法: tools/package_macos_app.sh [rid=osx-arm64] [输出目录=artifacts]
set -euo pipefail
cd "$(dirname "$0")/.."
RID="${1:-osx-arm64}"
OUT="${2:-artifacts}"
PUB="$OUT/publish/$RID"
APP="$OUT/TCode.app"

dotnet publish src/TCode.Desktop/TCode.Desktop.csproj -c Release -r "$RID" -o "$PUB"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUB/." "$APP/Contents/MacOS/"
cp src/TCode.Desktop/Packaging/app.icns "$APP/Contents/Resources/app.icns"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>TCode</string>
    <key>CFBundleDisplayName</key><string>TCode</string>
    <key>CFBundleIdentifier</key><string>com.tcode.desktop</string>
    <key>CFBundleExecutable</key><string>TCode</string>
    <key>CFBundleIconFile</key><string>app</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>0.1.0</string>
    <key>CFBundleVersion</key><string>0.1.0</string>
    <key>LSMinimumSystemVersion</key><string>11.0</string>
    <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

echo "已生成 $APP"
