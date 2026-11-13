#!/usr/bin/env bash
# Builds dist/Cinema.app (self-contained, no .NET install needed) with the app icon.
# Usage: scripts/bundle-macos.sh [osx-arm64|osx-x64] [version]
set -euo pipefail
cd "$(dirname "$0")/.."

RID="${1:-osx-$( [ "$(uname -m)" = "arm64" ] && echo arm64 || echo x64 )}"
VERSION="${2:-0.1.0}"
APP="dist/Cinema.app"

rm -rf "$APP" "dist/publish-$RID"
dotnet publish src/Cinema/Cinema.csproj -c Release -r "$RID" --self-contained true \
  -p:Version="$VERSION" -p:DebugType=none -o "dist/publish-$RID"

mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "dist/publish-$RID/." "$APP/Contents/MacOS/"
cp packaging/cinema.icns "$APP/Contents/Resources/cinema.icns"
sed "s/__VERSION__/$VERSION/g" packaging/Info.plist > "$APP/Contents/Info.plist"
chmod +x "$APP/Contents/MacOS/Cinema"

# Ad-hoc signature: required for arm64 binaries to launch at all.
if command -v codesign >/dev/null 2>&1; then
  codesign --force --deep --sign - "$APP"
fi
echo "Built $APP ($RID, v$VERSION)"
