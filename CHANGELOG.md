# Changelog

What changed in each Cnema release. The app shows the entries you have not seen yet after it updates.

## 0.21.0
- Crash-safe sessions: if Cnema crashes, is force quit or your computer loses power in the middle of a session recording, the recording is no longer lost. The next time Cnema starts it saves what was recorded, bookmarks included, as "… session (recovered)" in your clips folder.
- The replay buffer now lives in Cnema's cache folder instead of the system's temp folder, so it survives a restart. It's still cleared every time capture starts.
- Can be switched off under Settings, Features (Crash-safe sessions).

## 0.20.0
- Music: pick a song on the editor's Sound page and it plays under the export. It repeats if it's shorter than the clip, fades in and out, and can drop for a moment whenever the game gets loud (shots, voices), so the action still comes through. Quiet, Medium or Loud.
- Auto montage (sidebar, Tools, or Montage… while selecting): Cnema takes the loudest moment of each favourite (or the selected videos, or everything in view), puts them together with a crossfade, fade to black, slide or plain cut, adds a song if you like, and saves the result in a Montages folder. It runs in the background under Exports.
- Both can be switched off under Settings, Features.

## 0.19.0
- Zoom and pan (new Zoom page in the editor): punch in on the action with Z, or set keyframes yourself. On the Zoom page you see the whole picture with the zoom frame on it; drag the frame to aim and its corners to zoom. Cnema glides smoothly from one keyframe to the next, in the preview and in the export.
- Blur (new Blur page, or H): hide names, chat or bits of the HUD before you share. Drag the box into place and pull its corners to fit, then pick Blur or Pixelate. It covers the whole video, or just the part you choose. You see it blurred in the player.
- Other recorders now also finds Medal, AMD ReLive, Outplayed, Insights Capture, SteelSeries Moments and Snipping Tool recordings on Windows, and screen recordings on a Mac. "Another app" takes any other folder.
- Recorders that don't exist on your system are no longer listed.
- Zoom and pan and Blur can be switched off under Settings, Features. Remember edits keeps them too.

## 0.18.0
- Other recorders (sidebar, Tools): bring in videos from OBS, NVIDIA (ShadowPlay / NVIDIA app), Xbox Game Bar and Steam. Cnema finds their recording folders, adds them to your library and picks up new videos by themselves. Nothing is moved or changed. Use Change… if you record somewhere else.
- Videos from other recorders are tagged with their game where the recorder tells: NVIDIA's game folders, Game Bar file names, Steam's game, and subfolders for OBS. Switch it off with Tag by game.
- Steam keeps its clips in a streaming format other apps can't open, so Cnema copies new ones into MP4 files in a Steam folder next to your clips, at launch and with one click.
- Notes: write a line about a video under Rename, tag or move. It shows on the card in place of the file details, and search finds it.
- Drag out: drag a video from the library straight into Discord, a browser or a folder. With several videos selected, drag them all at once.
- All three can be switched off under Settings, Features.

## 0.17.0
- Cnema på svenska! Pick the language under Settings, General, or in the welcome setup. By default Cnema follows your system language, and anything not translated shows in English. The list of changes here stays in English.
- Remember edits (Settings, Features, off by default): trim marks, parts, captions and speed changes are still there when you open a video again. The video file only changes when you export, and Start over on the Trim page clears it all.
- Run the welcome setup again from Settings, General. It starts from your current choices and leaves your folders, hotkeys and other settings alone.
- "Export 1 parts" now says "Export 1 part".

## 0.16.0
- More save-clip hotkeys, each with its own length: say Ctrl+Alt+S for the last 15 seconds and Ctrl+Alt+D for the last 3 minutes. Add them under Settings, Clipping. The sidebar lists them all.
- Clip length now goes up to 10 minutes.
- Webcam in the corner: pick a camera under Settings, Clipping, and it goes into every clip and session, in the corner and size you choose. If the camera can't be opened, clipping carries on without it and tells you why.

## 0.15.0
- The editor is laid out around its tools now. A rail beside the video holds Trim, Captions, Speed, Sound, Brand and Export, and each opens a page with everything for that job: in and out points, parts, markers and chapters on Trim, the share presets on Export, and so on.
- A row of labelled buttons under the timeline covers what used to need a key: In, Out, Play and Loop the selection, Clear, Add part, Next marker, Caption, Slow-mo, Thumbnail and Save frame. Each one shows its key.
- Click the open page in the rail again (or press Shift+E) to fold the panel away for a bigger picture. Cnema remembers which page you had open and whether the panel was folded.
- Save frame (G) saves the frame on screen as a full-size PNG next to the video.
- ‹ and › buttons next to the title go to the previous and next video, like ← and →.

## 0.14.0
- Effects panel in the editor (Effects button, or Shift+E). Everything in it changes exports only; your original file stays as it is.
- Captions: press C to add text at the playhead, then pick top, middle or bottom, a size, and white text with an outline or on a dark box. You see them over the video while you edit, at the size the export will have.
- Slow motion and fast forward: pick part of a clip and a speed from ¼× to 4×, with an optional ramp that eases in and out. The player plays it at that speed so you can check it, and the sound keeps its pitch.
- Sound tools: even out loudness so quiet clips aren't too quiet (−14 LUFS, what YouTube and Twitch use). With your mic on its own track, boost your voice and lower the game while you talk.
- Watermark, intro and outro: set them up once under Settings, Editing (image, corner, size, opacity, and clips to put before and after), then switch them on or off per export.
- Export queue: exports and Share presets run in the background one after another while you keep editing. The Exports button shows progress, and you can cancel, open the folder, or clear finished ones.
- The video in the player is no longer slightly stretched for some files.
- All of this can be switched off under Settings, Features.

## 0.13.2
- ← and → now go to the previous or next video, in the order the library shows them (with your search, filters and sort), in both the quick player and the editor. Seeking moved to Shift+← and Shift+→ (5 seconds), and Alt+← / Alt+→ (⌥ on a Mac) for 1 second. If you have trim marks or parts in the editor, press the arrow twice so they aren't lost by accident. You can change these keys under Shortcuts (press ?).

## 0.13.1
- The mini player is now the quick player: it fills almost the whole window instead of a corner, and works the same way. Click the dimmed edge around it (or press Esc) to close it, and Edit still opens the full editor.

## 0.13.0
- Mini player: clicking a video plays it in a small player over the library, so you can keep browsing. Edit opens it in the full editor at the same spot, and the editor's Mini player button sends it back. Space, ← → and M control it; Esc closes it. Switch it off under Settings, Features to open videos straight in the editor.
- Speed menu: pick 0.25× to 4×, or press Shift+, and Shift+. to go slower or faster.
- A-B loop: press B at the start and again at the end; the part between repeats until you press B once more. The loop is also the trim selection, so you can export it as it is.
- Chapters from bookmarks: the bookmarks you drop while recording a session become chapters. The player lists them (jump to one, or select it to export just that chapter), Ctrl+← and Ctrl+→ (⌘ on a Mac) step between them, and session recordings and exports carry them so other players show them too.
- Click the player clock to show the frame number instead of tenths of a second, handy with frame stepping (, and .).

## 0.12.0
- Filters next to the search box: resolution, length, date added and "only videos I haven't watched". The button shows how many are on.
- Collections: group videos from any folders, like playlists. Right-click a video, Add to collection, or use + in the sidebar. A video can be in several collections, and renaming or moving it keeps it there.
- Select several videos (the tick button in the toolbar, or S) to favourite, tag, add to a collection, mark as watched or delete them together. Ctrl/⌘+A selects all, Esc leaves.
- A dot marks videos you haven't opened yet. Right-click to mark one as watched or unwatched.
- Pick the thumbnail: in the player, Set thumbnail (or T) uses the frame on screen. Right-click, Reset thumbnail, goes back to the automatic one.
- Clean up (sidebar, Tools) finds duplicate files and broken, empty or very short ones. It suggests what to remove; you tick what actually goes to the trash.
- Stats (sidebar, Tools): how many videos and hours you have, clips per week and clips per game.
- Collections, unwatched dots, Clean up and Stats can each be switched off under Settings, Features.
- Menus and pop-ups follow your color scheme.

## 0.11.0
- Settings has pages now: General, Features, Clipping and Accessibility. Ctrl+Tab, or LB and RB on a controller, switches between them.
- Features: switch off anything you don't use (background clipping, session recording, controller support, hover preview, loud moment markers, hover glow). Its buttons and settings disappear and it stops running.
- Accessibility: make the whole interface bigger or smaller, turn on high contrast, or reduce motion (no animations, zoom or glow).
- Press ? to see every keyboard shortcut. Click one to give it another key; a key that was taken moves over and you're told which action lost it.
- Export your settings to a file and import them on another computer (Settings, General, Backup).
- Portable mode keeps settings, library and thumbnails in a "Cnema Data" folder next to the app, for running Cnema from a USB stick.
- A short welcome screen on the very first start lets you pick a look, add folders and choose features.
- Fixed: after saving settings, the clip hotkey could be reported as taken by another app on Linux.

## 0.10.1
- The hover glow fades in and out smoothly and sits around the thumbnail instead of the whole card.

## 0.10.0
- Color schemes: pick Violet, Graphite, Ember, Mint or Rose under Settings, Appearance. They are flat, without gradients. Aurora keeps the original gradient look.
- Hovering a video in the library gives it a soft glow in the color of your scheme.
- A delete button appears in the lower left corner of a video when you hover it. It asks before moving the file to the trash.

## 0.9.0
- Cnema now updates itself: at launch it downloads a newer version, installs it and restarts. Turn this off under Settings, Startup.
- A "What's new" screen like this one appears after an update, and Settings has a button to open it again.
- The update card in the sidebar installs the update with one click and shows its progress.

## 0.8.1
- The update check works for everyone: releases are published to a public page.
- Cnema checks for updates every 3 hours while it runs, not only at launch, and notifies you once per new version.
- Settings shows the result of the last check and has a Check now button.

## 0.8.0
- Multi-part export: mark a selection, press A to add it as a part, mark the next one, then export them joined into one file.
- Audio tracks: record your two audio devices as separate tracks, and switch each on or off in the player before exporting.
- Markers: loud moments are marked on the timeline. N and Shift+N jump between markers.
- Session recording: record everything until you stop, and use the clip hotkey to drop bookmarks.
- Game profiles: a different clip length, bitrate or format per game.

## 0.7.0
- Much smaller downloads.
- Storage limits: remove old clips by age or folder size, to the trash or an archive folder. Favourites are always kept.
- Hover over a video to scrub through it.
- Share menu in the player: fit in 10 MB or 50 MB for Discord, vertical 9:16, or GIF.
- Fixed a crash that a half-written export could cause.

## 0.6.0
- Update check, with a card in the sidebar when a new version is out.
- Crash and error log, and Copy diagnostics in Settings.
- Delete videos to the trash from inside the app.
- Clips are named and tagged after the game in front, and a sound plays when a clip is saved.

## 0.5.0
- Choose the clip format: H.264, HEVC or AV1, on hardware encoders where the computer has them.
- macOS: record system audio without extra software.
- Controller mode for handhelds and TVs.

## 0.4.0
- Background clipping: keep the last seconds in a buffer and save them with a hotkey.
- Start at login, run from the tray.
