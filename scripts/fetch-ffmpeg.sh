#!/usr/bin/env bash
# Downloads a static ffmpeg build into <dest>/ so Cnema can ship them in its "tools" folder.
# Usage: scripts/fetch-ffmpeg.sh <osx-arm64|osx-x64|win-x64|linux-x64> <dest-dir>
#
# Source: https://github.com/eugeneware/ffmpeg-static (pinned release). These are GPL builds; they are
# shipped as separate executables that Cnema launches as child processes. See THIRD-PARTY-NOTICES.md.
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
# ffprobe is deliberately not shipped: Cnema reads metadata through ffmpeg, which saves ~70 MB.
for tool in ffmpeg; do
  out="$DEST/$tool$EXT"
  echo "Fetching $tool ($PLAT, $TAG)"
  curl -fsSL --retry 3 "$BASE/$tool-$PLAT.gz" | gunzip > "$out"
  chmod +x "$out"
done
cat > "$DEST/README.txt" <<NOTE
ffmpeg is a separate program from the FFmpeg project (https://ffmpeg.org),
licensed under the GNU GPL version 3 or later. This is an unmodified static build from
https://github.com/eugeneware/ffmpeg-static (release $TAG). You may replace it with any
other build. See THIRD-PARTY-NOTICES.md shipped with Cnema.
NOTE
ls -la "$DEST"
