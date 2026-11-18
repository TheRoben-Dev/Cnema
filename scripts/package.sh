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
    (cd dist && ditto -c -k --sequesterRsrc --keepParent Cnema.app "Savefile-v$VERSION-macos-$label.zip")
    ;;
  win-x64|linux-x64)
    out="dist/Cnema"
    rm -rf "$out"
    dotnet publish src/Cnema/Cnema.csproj -c Release -r "$RID" --self-contained true \
      -p:Version="$VERSION" -p:DebugType=none -o "$out"
    scripts/fetch-ffmpeg.sh "$RID" "$out/tools"
    scripts/trim-runtimes.sh "$RID" "$out"
    cp LICENSE THIRD-PARTY-NOTICES.md "$out/"
    if [ "$RID" = "win-x64" ]; then
      test -f "$out/libvlc/win-x64/libvlc.dll" || { echo "libVLC missing from package" >&2; exit 1; }
      # The NuGet package ships three architectures; this archive is x64 only.
      rm -rf "$out/libvlc/win-x86" "$out/libvlc/win-arm64"
      # The speech library (and the downloadable visual-search runtime) are built with MSVC and OpenMP:
      # ship the Visual C++ runtime app-local (as Microsoft allows), so they work without the redistributable.
      for dll in msvcp140.dll msvcp140_1.dll vcruntime140.dll vcruntime140_1.dll vcomp140.dll; do
        cp "/c/Windows/System32/$dll" "$out/" || { echo "$dll not found on the build machine" >&2; exit 1; }
      done
      scripts/strip-vlc.sh "$out/libvlc"
      # libVLC's plugin cache, made after stripping (it lists every plugin by date and size). Without it libVLC opens
      # all of its plugins on every start, which took 20 s on a Windows PC. The app writes one itself if this fails.
      "$out/Cnema.exe" --vlc-cache || echo "libVLC plugin cache not written here; the app will write it on first start"
      (cd dist && 7z a -bso0 "Savefile-v$VERSION-windows-x64.zip" Cnema)
    else
      cp src/Cnema/Assets/cnema.png "$out/cnema.png"
      # The speech library needs GCC's OpenMP runtime; bring it along (SpeechService loads it first),
      # so nothing has to be installed for voice search.
      cp "$(gcc -print-file-name=libgomp.so.1 2>/dev/null || echo /usr/lib/x86_64-linux-gnu/libgomp.so.1)" "$out/runtimes/linux-x64/libgomp.so.1" \
        || cp /usr/lib/x86_64-linux-gnu/libgomp.so.1 "$out/runtimes/linux-x64/libgomp.so.1"
      tar -C dist -czf "dist/Savefile-v$VERSION-linux-x64.tar.gz" Cnema
    fi
    ;;
  *) echo "Unsupported RID: $RID" >&2; exit 1 ;;
esac
ls -la dist
