#!/usr/bin/env bash
# Builds dist/Cnema.app with everything inside it: the .NET runtime, libVLC and its plugins,
# ffmpeg and the app icon. Nothing else needs to be installed.
# Usage: scripts/bundle-macos.sh [osx-arm64|osx-x64] [version]
set -euo pipefail
cd "$(dirname "$0")/.."

RID="${1:-osx-$( [ "$(uname -m)" = "arm64" ] && echo arm64 || echo x64 )}"
VERSION="${2:-0.1.0}"
APP="dist/Cnema.app"

rm -rf "$APP" "dist/publish-$RID"
dotnet publish src/Cnema/Cnema.csproj -c Release -r "$RID" --self-contained true \
  -p:Version="$VERSION" -p:DebugType=none -o "dist/publish-$RID"

mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
scripts/trim-runtimes.sh "$RID" "dist/publish-$RID"
cp -R "dist/publish-$RID/." "$APP/Contents/MacOS/"
cp packaging/cnema.icns "$APP/Contents/Resources/cnema.icns"
sed "s/__VERSION__/$VERSION/g" packaging/Info.plist > "$APP/Contents/Info.plist"
chmod +x "$APP/Contents/MacOS/Cnema"
cp LICENSE THIRD-PARTY-NOTICES.md "$APP/Contents/Resources/"

# ffmpeg (skip with CNEMA_SKIP_FFMPEG=1 to build offline)
if [ "${CNEMA_SKIP_FFMPEG:-0}" != "1" ]; then
  scripts/fetch-ffmpeg.sh "$RID" "$APP/Contents/MacOS/tools"
fi

# System audio helper (ScreenCaptureKit, macOS 13+). Optional: without it, clipping still records
# microphones and loopback devices.
if command -v swiftc >/dev/null 2>&1; then
  arch=$([ "$RID" = "osx-arm64" ] && echo arm64 || echo x86_64)
  mkdir -p "$APP/Contents/MacOS/tools"
  swiftc -O -target "$arch-apple-macos13.0" packaging/macos/cnema-audio.swift \
    -o "$APP/Contents/MacOS/tools/cnema-audio" && echo "Built cnema-audio ($arch)"
fi

# libVLC must be inside the bundle.
test -f "$APP/Contents/MacOS/libvlc/$RID/lib/libvlc.dylib" || { echo "libVLC missing from bundle" >&2; exit 1; }
scripts/strip-vlc.sh "$APP/Contents/MacOS/libvlc"

# Ad-hoc signature: required for arm64 binaries to launch at all.
if command -v codesign >/dev/null 2>&1; then
  # macOS treats everything under Contents/MacOS as code, so every file there needs a signature
  # (managed .dll files and VLC data files included) before the bundle itself can be sealed.
  failed=0
  while IFS= read -r -d '' f; do
    if ! err=$(codesign --force --sign - "$f" 2>&1); then
      echo "codesign failed: $f: $err"; failed=$((failed + 1))
    fi
  done < <(find "$APP/Contents/MacOS" -type f -print0)
  echo "Signed bundle contents ($failed file(s) could not be signed)"
  codesign --force --sign - "$APP"
  codesign --verify --deep --strict "$APP" && echo "Signature OK"
fi
echo "Built $APP ($RID, v$VERSION)"
