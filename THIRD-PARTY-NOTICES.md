# Third-party software

Cnema itself is MIT licensed (see `LICENSE`). Release builds also ship the components below, each under
its own licence. None of them are modified.

## libVLC (VideoLAN)

- What: `libvlc`, `libvlccore` and the VLC plugin modules, in the `libvlc/` folder.
- From: the official `VideoLAN.LibVLC.Mac` and `VideoLAN.LibVLC.Windows` NuGet packages.
- Licence: LGPL-2.1-or-later for libVLC and libVLCcore. A few plugin modules are GPL-2.0-or-later.
- Cnema loads these as separate dynamic libraries. You can replace them with your own build: swap the
  files in `libvlc/`, or point `CNEMA_LIBVLC_PATH` at another copy.
- Source: https://code.videolan.org/videolan/vlc (3.0 branch)

## FFmpeg

- What: the `ffmpeg` and `ffprobe` executables, in the `tools/` folder.
- From: https://github.com/eugeneware/ffmpeg-static (pinned static builds).
- Licence: GPL-3.0-or-later (these builds include GPL components such as x264).
- Cnema does not link against FFmpeg. It runs these programs as separate processes for thumbnails,
  metadata and clip export. You can replace them with any other ffmpeg/ffprobe build.
- Source: https://ffmpeg.org/download.html and the build scripts in the repository above.

## .NET runtime and libraries

- .NET runtime (MIT), Avalonia (MIT), LibVLCSharp (LGPL-2.1-or-later), CommunityToolkit.Mvvm (MIT),
  Inter typeface (SIL Open Font License 1.1).
