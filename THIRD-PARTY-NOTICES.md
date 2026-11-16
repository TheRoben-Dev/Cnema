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

- What: the `ffmpeg` executable, in the `tools/` folder.
- From: https://github.com/eugeneware/ffmpeg-static (pinned static builds).
- Licence: GPL-3.0-or-later (these builds include GPL components such as x264).
- Cnema does not link against FFmpeg. It runs these programs as separate processes for thumbnails,
  metadata and clip export. You can replace it with any other ffmpeg build.
- Source: https://ffmpeg.org/download.html and the build scripts in the repository above.

## whisper.cpp (speech, for voice search and captions)

- What: `whisper` and `ggml` native libraries, in the `runtimes/` folder, used through Whisper.net.
- From: the `Whisper.net.Runtime` NuGet package (https://github.com/sandrohanea/whisper.net).
- Licence: MIT (whisper.cpp, ggml and Whisper.net).
- The speech model itself is not shipped: it is downloaded only when you switch on voice search
  (`ggml-base-q5_1.bin`, the OpenAI Whisper base model converted by whisper.cpp, MIT licence).
- Source: https://github.com/ggml-org/whisper.cpp
- Windows builds also carry the Microsoft Visual C++ runtime (`msvcp140`, `vcruntime140`, `vcruntime140_1`,
  `vcomp140`) next to these libraries, redistributed under the Visual Studio redistributable terms.
  Linux builds carry `libgomp.so.1` (GCC's OpenMP runtime, GPL with the GCC Runtime Library Exception) so nothing extra needs installing.

## Clean up my voice

- `rnnoise/voice.rnnn`: the "beguiling-drafter" RNNoise model (recording, voice) from
  https://github.com/GregorR/rnnoise-models, which states that the models are not creative work and not subject
  to copyright. It is read by ffmpeg's `arnndn` filter.

## Visual search (downloaded only when switched on)

- CLIP ViT-B/32 (OpenAI, MIT licence), as 8-bit ONNX files converted by Xenova
  (https://huggingface.co/Xenova/clip-vit-base-patch32).
- ONNX Runtime 1.23.2 (Microsoft, MIT licence): the managed `Microsoft.ML.OnnxRuntime.Managed` package ships
  with the app; its native library for your platform is part of the download.

## .NET runtime and libraries

- .NET runtime (MIT), Avalonia (MIT), LibVLCSharp (LGPL-2.1-or-later), CommunityToolkit.Mvvm (MIT),
  Inter typeface (SIL Open Font License 1.1).
