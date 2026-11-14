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
    (cd dist && ditto -c -k --sequesterRsrc --keepParent Cnema.app "Cnema-v$VERSION-macos-$label.zip")
    ;;
  win-x64|linux-x64)
    out="dist/Cnema"
    rm -rf "$out"
    dotnet publish src/Cnema/Cnema.csproj -c Release -r "$RID" --self-contained true \
      -p:Version="$VERSION" -p:DebugType=none -o "$out"
    scripts/fetch-ffmpeg.sh "$RID" "$out/tools"
    cp LICENSE THIRD-PARTY-NOTICES.md "$out/"
    if [ "$RID" = "win-x64" ]; then
      test -f "$out/libvlc/win-x64/libvlc.dll" || { echo "libVLC missing from package" >&2; exit 1; }
      (cd dist && 7z a -bso0 "Cnema-v$VERSION-windows-x64.zip" Cnema)
    else
      cp src/Cnema/Assets/cnema.png "$out/cnema.png"
      tar -C dist -czf "dist/Cnema-v$VERSION-linux-x64.tar.gz" Cnema
    fi
    ;;
  *) echo "Unsupported RID: $RID" >&2; exit 1 ;;
esac
ls -la dist
