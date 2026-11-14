#!/usr/bin/env bash
# Removes the libVLC plugins Cnema never uses (network streaming, stream output, disc and capture
# access, visualisations, encoders) from a bundled libVLC directory. Cnema only plays local files.
# Usage: scripts/strip-vlc.sh <dir containing libVLC and its plugins>
#
# Anything that decodes, demuxes or renders local video stays. CI then runs "Cnema --selftest", which
# decodes a real H.264/AAC file through what is left.
set -euo pipefail
DIR="${1:?libVLC directory required}"
before=$(du -sk "$DIR" | cut -f1)

# Plugin names, as in lib<name>_plugin.{dll,dylib}. Shell patterns are allowed.
REMOVE=(
  'access_output_*' 'stream_out_*' 'mux_*'
  access_srt access_mms access_wasapi rist adaptive hds live555 ftp sftp nfs smb satip upnp bonjour
  rtp rtpvideo sdp udp nsc rss dtv vnc rdp vdr
  demux_chromecast libbluray dcp vcd cdda dvdnav dvdread
  avcapture avaudiocapture dshow screen shm
  goom visual glspectrum projectm vsxu
  lua addonsfsstorage addonsvorepository fingerprinter remoteosd nsspeechsynthesizer
  audiotoolboxmidi fluidsynth gme mod
  'x264*' 'x265*' twolame t140 crystalhd
  gnutls securetransport
)
for name in "${REMOVE[@]}"; do
  find "$DIR" -type f \( -name "lib${name}_plugin.dll" -o -name "lib${name}_plugin.dylib" \) -delete
done
# Blu-ray Java menus, Lua scripts, import libraries, and the plugin cache (stale once files are gone).
find "$DIR" -type f \( -name 'libbluray*.jar' -o -name '*.lib' -o -name 'plugins.dat' \) -delete
find "$DIR" -type d -name lua -prune -exec rm -rf {} +
find "$DIR" -type d -empty -delete

after=$(du -sk "$DIR" | cut -f1)
echo "Stripped libVLC: $((before / 1024)) MB -> $((after / 1024)) MB ($DIR)"
