#!/usr/bin/env bash
# Builds a release archive for one platform into dist/.
# Usage: scripts/package.sh <osx-arm64|osx-x64|win-x64|linux-x64> <version>
set -euo pipefail
cd "$(dirname "$0")/.."

RID="${1:?rid required}"
VERSION="${2:?version required}"
mkdir -p dist

case "$RID" in
  osx-*)
    scripts/bundle-macos.sh "$RID" "$VERSION"
    label=$([ "$RID" = "osx-arm64" ] && echo apple-silicon || echo intel)
    # ditto keeps signatures and permissions intact
    (cd dist && ditto -c -k --keepParent Cinema.app "Cinema-v$VERSION-macos-$label.zip")
    ;;
  win-x64|linux-x64)
    out="dist/Cinema"
    rm -rf "$out"
    dotnet publish src/Cinema/Cinema.csproj -c Release -r "$RID" --self-contained true \
      -p:Version="$VERSION" -p:DebugType=none -o "$out"
    scripts/fetch-ffmpeg.sh "$RID" "$out/tools"
    cp LICENSE THIRD-PARTY-NOTICES.md "$out/"
    if [ "$RID" = "win-x64" ]; then
      test -f "$out/libvlc/win-x64/libvlc.dll" || { echo "libVLC missing from package" >&2; exit 1; }
      (cd dist && 7z a -bso0 "Cinema-v$VERSION-windows-x64.zip" Cinema)
    else
      cp src/Cinema/Assets/cinema.png "$out/cinema.png"
      tar -C dist -czf "dist/Cinema-v$VERSION-linux-x64.tar.gz" Cinema
    fi
    ;;
  *) echo "Unsupported RID: $RID" >&2; exit 1 ;;
esac
ls -la dist
