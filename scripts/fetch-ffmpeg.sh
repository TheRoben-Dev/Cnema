#!/usr/bin/env bash
# Downloads static ffmpeg + ffprobe builds into <dest>/ so Cinema can ship them in its "tools" folder.
# Usage: scripts/fetch-ffmpeg.sh <osx-arm64|osx-x64|win-x64|linux-x64> <dest-dir>
#
# Source: https://github.com/eugeneware/ffmpeg-static (pinned release). These are GPL builds; they are
# shipped as separate executables that Cinema launches as child processes. See THIRD-PARTY-NOTICES.md.
set -euo pipefail

RID="${1:?rid required}"
DEST="${2:?dest dir required}"
TAG="${FFMPEG_STATIC_TAG:-b6.1.1}"
BASE="https://github.com/eugeneware/ffmpeg-static/releases/download/$TAG"

case "$RID" in
  osx-arm64) PLAT="darwin-arm64"; EXT="" ;;
  osx-x64)   PLAT="darwin-x64";   EXT="" ;;
  linux-x64) PLAT="linux-x64";    EXT="" ;;
  win-x64)   PLAT="win32-x64";    EXT=".exe" ;;
  *) echo "Unsupported RID: $RID" >&2; exit 1 ;;
esac

mkdir -p "$DEST"
for tool in ffmpeg ffprobe; do
  out="$DEST/$tool$EXT"
  echo "Fetching $tool ($PLAT, $TAG)"
  curl -fsSL --retry 3 "$BASE/$tool-$PLAT.gz" | gunzip > "$out"
  chmod +x "$out"
  # Keep the upstream licence text next to the binary.
  curl -fsSL --retry 3 "$BASE/$tool-$PLAT.LICENSE" -o "$DEST/$tool.LICENSE.txt" || true
done
ls -la "$DEST"
