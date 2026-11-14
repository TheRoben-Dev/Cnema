# Cnema

A video library and player built on libVLC. Point it at folders, it scans them for videos, and you can
play anything VLC can play and cut clips out of it without leaving the app.

![Library](docs/library.png)
![Player and trimmer](docs/player.png)
![Rename, tag and move](docs/edit.png)
![Background clipping](docs/clipping.png)
![Settings](docs/settings.png)

Stack: C# / .NET 8, [Avalonia 11](https://avaloniaui.net) for UI, [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp)
for playback, ffmpeg/ffprobe for thumbnails, metadata and clip export. One codebase for macOS, Windows and Linux.

## Download

Grab a build from [Releases](https://github.com/TheRoben-Dev/Cnema/releases). On macOS and Windows everything is
inside the app: the .NET runtime, libVLC with its plugins, and ffmpeg/ffprobe. Nothing else to install.

On Linux, ffmpeg and .NET are bundled but libVLC comes from your distro (VideoLAN publishes no Linux package):
`sudo apt install libvlc5 vlc-plugin-base`.

The macOS app is not notarized, so the first launch needs either right-click → Open, or:

```sh
xattr -dr com.apple.quarantine /Applications/Cnema.app
```

## Run from source (macOS)

```sh
brew install --cask dotnet-sdk         # .NET 8+ SDK
brew install ffmpeg                    # only needed when running from source
dotnet run --project src/Cnema
```

libVLC is restored from NuGet, so VLC does not need to be installed.

Build a real `Cnema.app` with the icon: `scripts/bundle-macos.sh` (output in `dist/`).

Releases are built by GitHub Actions: run the **Release** workflow with a version number (or push a tag like
`v0.2.0`) to publish macOS (Apple Silicon and Intel), Windows and Linux builds.

| OS | libVLC comes from | Notes |
| --- | --- | --- |
| macOS | `VideoLAN.LibVLC.Mac` NuGet, copied into the app (`libvlc/osx-<arch>/`) | Falls back to an installed VLC.app if the bundled copy is missing. |
| Windows | `VideoLAN.LibVLC.Windows` NuGet, copied into the app (`libvlc/win-x64/`) | |
| Linux | system packages: `sudo apt install libvlc5 vlc-plugin-base` | |

Set `CNEMA_LIBVLC_PATH` to a folder containing libvlc + libvlccore to override.

## Features

- **Library**: add any number of folders; scanned recursively, watched for changes, cached so launch is instant.
  Thumbnails, duration, resolution, codec, size. Search, sort, per-folder filtering, resume position.
- **Organise**: tag videos and star favourites; both appear in the sidebar and in search (`#tag` searches tags only).
  Rename files and move them between library folders from the app. Tags are stored in Cnema's library, not in
  the files, and follow a file that is renamed or moved outside the app.
- **Player**: libVLC playback, frame stepping, speed, volume, fullscreen.
- **Trim**: set in/out at the playhead or drag the brackets on the timeline, preview/loop the selection, export.
  - *Lossless* (default): stream copy. Instant, zero quality loss, keeps the container. The cut starts on the
    keyframe at or before the in point, so the clip can start up to a GOP earlier than marked.
  - *Precise*: re-encodes to H.264/AAC MP4 (CRF 18). Frame-accurate, slower, slight generation loss.

### Background clipping

Turn on **Clipping** in the sidebar (or in Settings) and Cnema keeps recording the screen into a small
rolling buffer. Press the hotkey (default `Ctrl+Alt+S`, `⌃⌥S` on macOS) and the last 30 seconds are
written to your clips folder, which is a library folder, so the clip shows up in the app straight away.

- **Runs in the background.** Closing the window leaves Cnema in the menu bar / system tray. Settings has
  "Launch Cnema when I log in"; it then starts without a window.
- **Light on resources.** One ffmpeg process encodes with the GPU where it can (VideoToolbox on macOS;
  NVENC, AMF or Quick Sync on Windows) and falls back to x264. The buffer is 2-second segments in the temp
  folder, capped at the clip length; saving is a stream copy, not a re-encode.
- **Settings:** clip length (15 s to 5 min), resolution, frame rate, video bitrate, hardware encoding,
  two audio devices (mixed), audio bitrate, hotkey, clips folder.

Platform notes:

| | Screen | Audio |
|---|---|---|
| macOS | Needs the Screen Recording permission (asked on first use). | Microphones work. System sound needs a loopback device such as BlackHole. |
| Windows | Desktop Duplication (`ddagrab`), `gdigrab` as fallback. | DirectShow devices. System sound needs "Stereo Mix" or a loopback device. |
| Linux | X11 only, and needs the distro's ffmpeg (the bundled static build has no `x11grab`). | PulseAudio / PipeWire sources; `.monitor` sources record system sound. |

### Shortcuts (player)

| Key | Action | Key | Action |
| --- | --- | --- | --- |
| Space / K | Play / pause | I / O | Set in / out |
| ← / → | Seek 5 s (Shift: 1 s) | Shift+I / Shift+O | Jump to in / out |
| J | Back 10 s | P | Play selection |
| , / . | Previous / next frame | L | Loop selection |
| ↑ / ↓ | Volume | X | Clear selection |
| M | Mute | E | Export clip |
| F | Fullscreen | Esc | Back to library |

## Layout

```
src/Cnema
  Services/    VlcBootstrap (native lib loading per OS), LibraryService (scan/cache/watch),
               MediaProbe, ThumbnailService, TrimService, ToolLocator, PlatformShell, AppPaths
  Controls/    VideoFrameBuffer + VideoSurface (libVLC -> bitmap), TrimTimeline (scrubber with in/out)
  ViewModels/  MainViewModel (library), PlayerViewModel (transport + trimming)
  Views/       MainWindow
```

Data lives in `~/Library/Application Support/Cnema` (macOS), `%LOCALAPPDATA%\Cnema` (Windows),
`~/.local/share/Cnema` (Linux).

## Design notes

- **Not a fork of VLC's source.** VLC itself is ~1M lines of C/C++/Obj-C with a separate UI per platform;
  libVLC is the same engine exposed as a library, so playback is identical while the UI is ours.
- **Video is rendered through libVLC's frame callbacks** into an Avalonia bitmap rather than handing VLC a
  native window. It behaves the same on every OS and lets UI sit on top of the video. The cost is one CPU copy
  per frame (and no zero-copy GPU path); fine through 4K, worth revisiting for 8K/high-fps HDR.
- **All OS-specific code is in four places**: `VlcBootstrap`, `AppPaths`, `PlatformShell`, `ToolLocator`,
  plus title-bar insets in `MainWindow.axaml.cs`.

## Licensing

Cnema is MIT licensed. Release builds bundle libVLC (LGPL, dynamically linked) and ffmpeg (GPL, run as a
separate program); see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
