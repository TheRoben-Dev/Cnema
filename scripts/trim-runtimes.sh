#!/usr/bin/env bash
# The speech library (Whisper.net.Runtime) puts every platform's native files under runtimes/;
# keep only the one this archive is for.
# Usage: scripts/trim-runtimes.sh <osx-arm64|osx-x64|win-x64|linux-x64> <publish dir>
set -euo pipefail
RID="${1:?rid required}"
DIR="${2:?publish dir required}"
case "$RID" in
  osx-arm64) keep=macos-arm64 ;;
  osx-x64)   keep=macos-x64 ;;
  *)         keep="$RID" ;;
esac
[ -d "$DIR/runtimes" ] || exit 0
test -d "$DIR/runtimes/$keep" || { echo "Speech library for $keep missing from $DIR/runtimes" >&2; exit 1; }
for d in "$DIR"/runtimes/*/; do
  [ "$(basename "$d")" = "$keep" ] || rm -rf "$d"
done
echo "Kept runtimes/$keep"
