# Changelog

What changed in each Cnema release. The app shows the entries you have not seen yet after it updates.

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
