#!/usr/bin/env bash
# Builds dist/Cinema.app with everything inside it: the .NET runtime, libVLC and its plugins,
# ffmpeg/ffprobe and the app icon. Nothing else needs to be installed.
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
cp LICENSE THIRD-PARTY-NOTICES.md "$APP/Contents/Resources/"

# ffmpeg + ffprobe (skip with CINEMA_SKIP_FFMPEG=1 to build offline)
if [ "${CINEMA_SKIP_FFMPEG:-0}" != "1" ]; then
  scripts/fetch-ffmpeg.sh "$RID" "$APP/Contents/MacOS/tools"
fi

# libVLC must be inside the bundle.
test -f "$APP/Contents/MacOS/libvlc/$RID/lib/libvlc.dylib" || { echo "libVLC missing from bundle" >&2; exit 1; }

# Ad-hoc signature: required for arm64 binaries to launch at all.
if command -v codesign >/dev/null 2>&1; then
  # macOS treats everything under Contents/MacOS as code, so every file there needs a signature
  # (managed .dll files and VLC data files included) before the bundle itself can be sealed.
  find "$APP/Contents/MacOS" -type f -print0 | xargs -0 -n 50 codesign --force --sign - 2>/dev/null
  codesign --force --sign - "$APP"
  codesign --verify --deep --strict "$APP" && echo "Signature OK"
fi
echo "Built $APP ($RID, v$VERSION)"
