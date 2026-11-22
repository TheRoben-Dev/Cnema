# Changelog

What changed in each Savefile (formerly Cnema) release. The app shows the entries you have not seen yet after it updates.

## 0.52.0
- Wrapped and Auto montage have a new look and move with your music. With a song, the picture punches in on the beat (harder on the first beat of a bar, hardest when the song drops), flashes on the drop, and slow moves ease in and out instead of crawling. Shots flow into each other through liquid blobs, rolling waves and ink blooms instead of plain wipes, with a burst of light on the drop. A cut lands right on each drop, the bright parts of the picture glow softly, and a flowing glow in violet, magenta, blue and peach runs along the bottom as the video starts and ends. Turn it off in Settings › Editing › Beat effects.
- Wrapped's cards are redrawn: soft, grainy blends of violet, magenta, blue and orange instead of one flat colour, glossy pearls bouncing on the beat, a glossy Mochi, and big white titles on a gentle wave with a teal shadow.
- Auto montage with a song now cuts on its beats: each piece is made a little longer or shorter so the next one starts on a beat (the first beat of a bar when one is close), never in the middle of a sentence.
- Auto montage and Wrapped now use everything the smart features know about a clip to cut it, new in Settings › Smart features › Smart cuts (on by default). A cut no longer lands in the middle of a sentence: the transcript's lines are joined into sentences, and each piece starts and ends in a pause. A laugh comes with the joke that set it off, a cheer or a banner on screen (VICTORY, ACE, a penta kill) gets to play out, and moments you marked with the hotkey count the most. Clips with friends recognised by voice (only when Savefile is sure) are picked a bit more often, and with the visual model on, two look-alike clips aren't put side by side.
- In Wrapped, cuts still land on the beat: the director picks the beat that keeps the cut out of a sentence, and ends each longer moment on the bar line where what's being said is over. When a quick shot still catches half a sentence, the music stays up over it so chopped words aren't heard.
- Montage pieces can be up to half longer than the length you picked, so a sentence or a joke isn't cut short. Whatever smart feature is off, or hasn't looked at a clip yet, is simply left out; with nothing known, montages cut as before.

## 0.51.2
- Fixed the sound breaking up while watching clips. The loud-moment markers on the timeline read the whole video at the same time as it played, which could starve the player on the Mac; that now waits while a video plays, like the rest of the indexing, and the markers fill in when you pause. While a video plays, Savefile also keeps macOS from putting it to sleep in the background (App Nap) and keeps memory clean-ups short.
- If the sound still breaks up, Savefile now writes down how many bits of sound were lost and what else was busy at the time, so Send diagnostics shows the cause.

## 0.51.1
- No more crackle when you close the Quick player or skip to the next clip. The sound now fades out quickly instead of being cut off mid-wave, and background indexing stays paused until the video has fully stopped, so it can't steal time from the last bit of audio.

## 0.51.0
- Who's that? goes on for as long as you like. Mochi fetches more voices while you play, instead of stopping after 8, and only says it's done when it has asked about every voice it has.
- Who's that? has points, levels and badges. Naming a voice earns points, with extra for a streak, for picking Mochi's first guess and for a friend's very first voice. Skipping never costs anything; it only starts a new streak. There are 14 badges to collect, and Mochi cheers when you earn one or reach a new level. Undo takes the points back too. You can switch it off in Settings › Smart features › Who's talking.
- A voice you've answered in Who's that? is never asked about again, whatever the answer (Not sure, skip included).
- Who's talking is much stricter. A friend is only tagged in a clip when the voice is a near-perfect match: very close to that friend's voice, clearly closer than anyone else's, and only after a few of your answers for that friend, from more than a line or two of talking. Tags Savefile added by voice that don't pass this any more are taken off by themselves. Friends you tagged yourself are never touched.
- Every smart feature has a Reset in Settings › Smart features (Best moments, Smart titles, Voice search, Visual search, Laughter and hype, Text on screen and Who's talking). It forgets what that feature worked out for every video and starts over in the background. It asks first, and what you did yourself stays: titles you typed, friends you tagged, thumbnails you picked and your answers in Who's that?.

## 0.50.1
- Who's that? now opens on top of Settings instead of behind it, and closing it (or pressing Esc) takes you back to Settings.
- Fixed Savefile freezing around the voice game: when a snippet (or any video) started playing, the background indexing was stopped right on the window's own thread, which could lock the window up until it finished. It now stops in the background, and the snippet player talks to VLC off the window's thread too.

## 0.50.0
- Who's that? only plays bits where one person talks. Mochi used to listen to whole transcript lines, and a line often holds a reply or two people talking at once, so the snippet could have several voices in it. Now each line is listened to in short overlapping slices, the voice is followed slice by slice, bits that sound like two people (or like game noise) are left out, and each snippet starts and ends at a pause, where people take turns. In tests with several friends talking over game noise, snippets with only one voice went from 35 of 78 to 54 of 59, and the rest have under a twentieth of another voice in them.
- Who's talking is steadier: a voice's voiceprint is now made only from the bits where that person talks alone, the same person is far less often split into two voices (the count of voices per clip was off by 21 in the tests, now by 6), and friends are no longer recognised in clips they're not in (2 wrong guesses before, none now).
- Videos are listened to again after this update, in the background. Your answers in Who's that? are kept and still work.

## 0.49.0
- Settings has a new layout: a sidebar with eight pages (General, Library, Clipping, Editing, Memories, Smart features, Backup and Accessibility) instead of one long Features page, and a search box at the top. Type a word and every page shows just the settings that match, in Swedish or English.
- Savefile now asks before anything that can't be undone: removing a folder or another recorder from your library, deleting a video from the bin for good, deleting a collection, taking a tag or a friend off every video, removing a smart model, starting playtime over, forgetting your server's key, Start over in the editor, resetting all shortcuts and importing a settings file.
- Captions and voice search are much more accurate, above all when Swedish and English are mixed. Savefile now works out the language of each stretch of speech on its own, out of English, the language Savefile is in and your computer's, instead of guessing once for minutes at a time (it often took Swedish for Norwegian). With Better Swedish, the English parts go to the standard model, which writes them as said instead of translating them into Swedish. In tests with Swedish and English speech over game noise, more than half the words used to come out wrong; now it's about 4 in 10 with Standard and about 1 in 7 with Better Swedish (1 in 10 for Swedish on its own), so if you speak Swedish, Better Swedish is well worth the download.
- New speech model choice, Most accurate (Whisper large-v3 turbo, 574 MB): the best in every language, and the slowest.
- After switching speech models, Settings offers to write down again what the old model wrote, with how many videos and about how long it takes. Nothing is redone unless you say so, and the old words stay searchable until each video is done.
- Smart titles use more of what Savefile knows: who said the line that made everyone laugh ("Sam cracks everyone up: …"), a friend's name on a kill banner ("Quadra kill by Alex"), the game from what's on screen when a clip has no game tag, and what the picture shows when the visual model is sure (underwater, up in the air, a car crash).

## 0.48.0
- Savefile now checks for updates first thing when it starts, before the video player, your library or the window open. With Install updates automatically on, a new version installs right there on the loading screen (with its progress shown, and a click skips it), so even a version that can't open its window can't keep you stuck on it. Offline, or when GitHub is slow, the start isn't held up: the check gives up after a couple of seconds at most.
- If Savefile ever fails to open its window, the next start looks for a fixed version before anything else runs and installs it.
- Savefile opens faster: Settings, the editor's tool pages and the bigger dialogs (the Bin, backups, Year in clips, stats and the rest) are put together the first time you open them instead of at every start. Everything looks exactly as before.
- "Who's that?", Mochi's voice game, is easy to find again. It hid until Mochi had found a voice, and often didn't notice when it had, so it looked gone. It's now in Settings › Smart features whenever Who's talking is on, and on every friend's page (next to Make a montage).
- Choose where exports go: Settings › Editing › Exported clips go to. Exports, shares, saved frames, montages and Wrapped all go to that folder; Reset puts them back next to the original video, as before.
- Videos copied into your clips folder, or recorded into it by another app, show up properly once they're finished. Before, a long copy could leave a video without its length or thumbnail until Savefile was restarted.
- Your library, settings and bin no longer reset if another program (a virus scanner or a backup tool) happens to be reading the file at the very moment Savefile starts. Savefile waits a moment and reads it again.

## 0.47.1
- Mac: fixed Savefile vanishing right after the loading screen in 0.47.0, staying stuck in the background until it was force quit. The ready-compiled build that made 0.47.0 start faster broke the main window on Apple Silicon Macs, so Macs go back to the way 0.46 started (still quick). Windows keeps the faster start.
- If the main window ever can't open again, Savefile now quits and writes why in its log instead of lingering invisibly.

## 0.47.0
- Faster everywhere, with nothing changed in how it looks. Scrolling a big library is smoother: cards that scroll into view take about half the work they did, thumbnails you just scrolled past stay ready when you scroll back, and hover previews wait until you stop scrolling instead of starting for every card that slides under the pointer.
- Savefile starts about twice as fast: the app now comes ready-compiled for your computer instead of preparing itself at every start.
- The video player starts much faster, above all on Windows, where it could take 20 seconds: it no longer opens all of its plugins at every start. If your copy has to make that list itself, the first start after an update is as before and every one after it is quick.
- Saving your library (tags, favourites, what the smart features found) happens in the background now, so it never makes the window stutter, even with a big library.
- Opening a video in the Quick player is snappier: the scan for loud moments on its timeline waits until the video is playing.
- Mochi takes a break while scrolled out of sight.
- Closing Settings is instant: it no longer restyles the whole window when nothing about the theme or language changed.
- Playing a video now also pauses the smart features (voice search, visual search, laughter, text on screen, who's talking) instead of only the background ffmpeg work, so playback stays smooth while your library is being looked through. The video they were on is done again afterwards. Mochi rests too while the window is minimised or hidden.
- Dragging a long clip into Discord: a clip too long to fit in 10 MB (or 50 MB with Nitro) at watchable quality now sends the original and tells you how short to trim it, instead of a blurry copy that could still end up over the limit. A copy that comes out a little too big is redone once so it fits.
- The library notices a whole folder of clips moved in or out of your clips folder, and catches up when a big copy floods the folder with changes faster than it can follow.
- A broken or half-copied video no longer stops the library scan, and macOS "._" files on external drives and Make room's work files no longer show up as videos.
- Smart features switched off and on again quickly now really start again, a rescan picks up text on screen too, and Make room leaves your archive folder alone when it sits inside the clips folder.
- A Wrapped that was being made when Savefile quit always picks up where it stopped, even if it quit at the worst moment.
- Smaller fixes: the shelf arrows add up when clicked quickly instead of tugging against each other, Mochi never swaps a line for an older one, thumbnails made at the same moment no longer clash, and "Who's that?" snippets play without holding up the window.

## 0.46.3
- Windows: clipping now recovers by itself when Windows takes the screen away from it, which happens when a game switches between fullscreen and windowed, the display or HDR changes, or the PC locks. It used to stop with an error after a second or two ("Generic error in an external library"); now it starts again on its own, and if Windows' fast screen capture keeps failing, Savefile switches to its compatible capture so clipping keeps going (games in exclusive fullscreen may record black there, so borderless windowed works best). Switching clipping off and on tries the fast capture again.
- When clipping really can't run, the card explains why in plain words (the screen, the graphics card's encoder, a sound device or a full drive) instead of showing ffmpeg's error lines. The full details are still in Copy diagnostics.
- Windows: opening Savefile while another copy is already running (say, an installed one and an older unzipped one) brings the running copy forward instead of starting a second one that fights it over clipping.

## 0.46.2
- Fixed crackling and little static pops when playing videos while Savefile was looking through your clips in the background. That background work now freezes while a video plays and carries on where it left off when you pause or close it, and time spent frozen never counts against it, so no clip gets skipped for taking too long.

## 0.46.1
- Savefile has its own save sounds now: seven short retro chiptune jingles made for Savefile (Save crystal, Power-up, Checkpoint, Victory fanfare, Quest complete, Loot drop and Coin chain). Pick one in Settings › Clipping; choosing one plays it, and Hear it plays it again.
- The save sound plays from Savefile itself, so it works even when the computer's sound scheme is silent (on Windows it used to follow the system's sounds, so a PC set to "No Sounds" stayed quiet). Only the volume matters now.
- On a Mac recording System audio, the save sound no longer ends up in your next clip when you save two in a row.

## 0.46.0
- Windows: Savefile now has a proper installer. Choose to install it for just you (no administrator needed) or for everyone on the PC (in Program Files). It adds Savefile to the Start menu and the desktop, and you can uninstall it from Settings › Apps like any other app (your videos, settings and library stay). An installed Savefile updates itself with the next installer and keeps your choice; installed for everyone, it asks for an administrator once per update, so it waits for you to click the update instead of updating at login.
- The zip download stays as the portable version, for running Savefile without installing it (from a USB stick, for example). If you unzipped Savefile before, it keeps updating itself as it did; to switch, run the installer and delete the old folder (your settings and library carry over).

## 0.45.2
- On a friend's page in a smaller window, the page title no longer slides under the Play all, montage and Wrapped buttons. The montage and Wrapped buttons show only their icons until there's room (hover them for what they do), and a title that still doesn't fit ends in "…".

## 0.45.1
- External drives on a Mac or Linux are recognised as their own drive. Make room checked free space on the startup disk instead of the drive the video is on, and merging folders didn't notice that videos from another drive need room to be copied.
- A video in the Bin on an external drive that isn't plugged in now waits for the drive instead of being forgotten, so it's still deleted for good when the drive comes back.
- The Bin count updates as soon as videos expire, instead of up to 30 seconds later.
- Mochi no longer keeps watching the pointer after it leaves the screen, which could slowly add up while using the app.

## 0.45.0
- Text on screen: Savefile can read kill feeds, scoreboards and banners. Search finds what was on screen (a name from the kill feed, "ace"), smart titles name the banner ("Victory Royale · Fortnite", "Ace · Valorant"), and a clip with no game yet gets one when the game's own words give it away (SPIKE PLANTED means Valorant, VICTORY ROYALE means Fortnite). Turn it on in Settings › Smart features; it needs a one-time 10 MB download, reads Swedish letters too, and everything happens on this computer.

## 0.44.0
- Smart crop: a vertical (9:16) export follows the action instead of always cutting out the middle. Savefile looks at a small copy of the part you export, ignores a HUD that never moves and camera turns that move everything, and glides the slice to where things happen. When nothing stands out (looking around in a shooter, say) it stays in the middle like before. It works with effects and cuts too, and you can turn it off in Settings › Smart features.
- Fixes: "Who's that?" no longer trips over the library changing while it picks snippets, the snippet levels read only the snippet instead of the whole clip, and undoing a friend the game added works whatever the name's capitals.

## 0.43.0
- Smart titles say what happened instead of just the game and the date. A title can quote the line someone shouted at the big moment ("“Behind you!” · Valorant with Alex"), the line that set off a laugh ("Laughing at “did he just jump off the roof” · Minecraft"), name a friend Mochi recognises by voice ("Alex: “Go go go!”"), or call out a big laugh or a loud moment. When nothing stands out, you get the old style. It works best with Voice search and Laughter and hype on, and Smart rename uses it for the whole library.
- Clips that named themselves (Name new clips by themselves) get a better name once they've been listened to. A name you typed or picked is never changed.

## 0.42.0
- Friend picks: the names of the friends you have the most clips with sit above Worth a rewatch. Pick one and the shelf fills with the best of your clips together, and Mochi says something about the two of you: how many clips you share, your game together, the biggest laugh, your first clip, or the clips it recognised them in by voice. For you takes it back to the usual mix. You can turn it off in Settings › Memories.

## 0.41.1
- Who's talking: Savefile can now tell your friends' voices apart and fill in Who was there by itself. Turn it on in Settings › Smart features (it needs Voice search, and a one-time 26 MB download). It all happens on this computer.
- "Who's that?", Mochi's little voice game: listen to a short snippet, then say who it is. Mochi offers its best guesses, or you can pick Someone else (a new name becomes a new friend), That's me, Not sure, or Not a person for game voices and music. Undo takes an answer back. After a few answers per friend, Mochi recognises them in your other clips too, and only adds a name it's sure about. If you take a name off a clip, it stays off.
- Renaming or merging a friend carries their voice along.
- The favourite star in the Quick player glimmers when you turn it on: a glint across it and a small sparkle (no movement with Reduced motion).

## 0.40.1
- The Quick player now takes the shape of the video: an ultrawide clip gets a wide, short player and a phone clip a narrow one, so there are no black bars beside the picture. It resizes smoothly when the next clip has another shape (instantly with Reduced motion on).
- A favourite star in the Quick player, next to tags and delete. It glows when you turn it on and stays filled while the clip is a favourite. Press F to do the same.

## 0.40.0
- Wrapped: the funny bits now come with their setup. Each laugh starts at what led to it (the line someone said, never mid-sentence) and the laugh plays out before the cut. Quick cuts never chop a laugh in half any more, and Best laughs is a run of full moments instead of two-second flashes.
- Wrapped plays smoothly: clips recorded at 60 fps played at half speed during the slow zooms, and the music could drop out for most of a second after a loud clip. Both are fixed.
- The finished Wrapped is a clean, seekable video: a keyframe every 2 seconds and a steady 30 fps, so skipping around works in every player. It's also about a quarter smaller (around 6 Mbit/s at 1080p).
- Best moments is much faster on a Mac. Some recordings made Savefile decode every single frame on one core, which took minutes per clip; it now uses the Mac's own video decoder, and a clip that still takes too long falls back to its sound. The other smart features no longer wait behind it.
- Mochi has a lot more to say: over 130 lines, with nods to the games in your library (Fortnite, Warframe, Honkai: Star Rail, Destiny 2, No Man's Sky, ARK, Valorant, Minecraft and more), your friends and stats, the time of day and the season. Nothing repeats until the rest have been said.
- If you made a Wrapped with an earlier version, make it again to get all of this.

## 0.39.0
- Make room (under Tools): Savefile finds big videos you haven't watched in three months or more, skipping favourites and anything newer than a month, and can encode them again as HEVC. They look the same in about half the space. You see the list first, tick what you want, and can try one first. Every new copy is checked to be the same length, keep every sound track and be clearly smaller before it replaces the original. Name, date, favourite, tags, friends and notes all stay, and the original waits in the bin for 24 hours.
- When there's 2 GB or more to win, Mochi mentions it.

## 0.38.0
- Badges: 12 to earn from what your clips show, like Night owl, Full squad, On a roll (clips 7 days in a row), Explorer and Throwback. Mochi holds up each new one, and Stats shows them all with the date you earned them. Switch them off under Settings, Features, Badges.
- Old favourites: every few days at most, Mochi brings up a favourite you haven't watched in a couple of months, with a Watch it button right in its speech bubble. It's under Settings, Features, Mochi.
- Playtime (off unless you turn it on): Savefile counts hours per game while a game it knows is in front, like Steam does. It only counts from the moment you switch it on, says so, and stays on this computer. You'll find it in Stats.
- The indexing card says when it's waiting, for example while your Wrapped is being made, instead of looking stuck.

## 0.37.0
- Mochi comes alive: smoother, springier animation with a crouch before every hop, a squash on landing, a jelly wobble and little idle habits (glances, stretches, double blinks, the odd small hop), never the same one twice in a row.
- Mochi reacts when you save a clip: a squish and sparkles in the "Saved" message, and a big hop when a clip you just saved turns out to be a great moment. For a milestone it holds up a gold badge.
- Mochi fills empty pages: it peeks up when the bin is empty, when a search finds nothing ("Mochi looked everywhere") and on a friend's page with no clips yet.
- Seasonal looks: a Santa hat in December, a party hat on Savefile's birthday (the day you installed it) and tinsel on the Wrapped present.
- After midnight Mochi gets sleepy and asks if it's a late session. Its eyes follow your pointer when it's idle, and clicking it makes it wobble (click it ten times quickly…).
- While a backup runs, Mochi carries a box in the sidebar. Next to a problem (ffmpeg missing, the video player failing, a model download that went wrong) it looks puzzled and says how to fix it in plain words.
- Each of these has its own switch under Settings, Features, Mochi, and Reduced motion keeps Mochi still.

## 0.36.1
- Smart features get through your library again: on a Mac they managed about one video every few minutes. macOS was putting Savefile to sleep in the background (App Nap) and holding its helper back on the slowest cores, so a big library would have taken days. Now Savefile stays awake while it works (your Mac can still sleep), and a video that takes far too long is skipped instead of holding up the rest.
- Meet Mochi: the blob has a name. While Savefile looks through your clips, Mochi sits at the bottom of the sidebar flipping through them ("Looking through your clips… 15 of 645") and hops when it finds a really good moment. Click it to see what each smart feature is doing, how long is left, and to pause for now.

## 0.36.0
- Savefile Wrapped: your year as a 5–10 minute video, cut to the beat of your own songs. Chapters on your games, your people, your biggest night and your best laughs, a card for each of your crew, a month-by-month flash, your first and last clip of the year, a hidden gem and the funniest moment to close. Every number counts clips, never guessed hours.
- From 1 November to 31 December the blob has a present for you. Open it, add a few of your own MP3s (Savefile says how many minutes of music it needs) and it makes your Wrapped on this computer while you do other things. It picks up where it left off if you quit. The first time, it plays full screen with only pause and scrubbing; after that the open box next to the blob replays it, with Export.
- Wrapped with a friend: on a friend's page, make a shorter cut of only your clips together.
- Make it any time from Year in clips, or turn it off under Settings, Features, Wrapped.

## 0.35.0
- Big libraries scroll smoothly: the library grid no longer bends out of shape far down a long list, and scrolling to the bottom of hundreds of clips doesn't freeze any more. Every card is now the same size, only the rows on screen are built, and thumbnails you've already scrolled past aren't loaded. Tested with 1,140 clips.
- The bin: deleting a video anywhere in Savefile (the card's delete button, right-click, select and delete, Clean up, storage limits and the Quick player) moves it to Savefile's bin instead of the system Trash. Each video waits there for 24 hours from when you deleted it, on its own clock, then it's deleted for good. Bin in the sidebar shows how long each one has left, and Put back brings it back with its favourite, tags, friends and notes. You can turn it off under Settings, Features, Savefile bin.
- Quick player: new buttons to change the name, tags and friends (or press I) and to delete (or press Del). Deleting moves the video to the bin and the next one starts playing.
- Thinner scroll bars that stay out of the way until you point at them. The sidebar and Worth a rewatch have none at all; the shelf has arrows instead.
- In Swedish, dates are Swedish too ("måndag 27 juli", "sedan februari 2024").

## 0.34.0
- Swedish speech: pick Better Swedish under Settings, Features, Voice search and captions to use KB-Whisper from the National Library of Sweden (175 MB download). It's far more accurate in Swedish and still handles English mixed in. Search also ignores accents now, so "rakna" finds "räkna".
- Drag a video into Discord and it fits: a video too big for Discord's 10 MB limit becomes a copy that fits while you hold it (50 MB if you switch on I have Discord Nitro). Smaller videos go as they are, and you can switch it off to always drag the original.
- Savefile på svenska is complete again: every screen, setting and message added since the first translation now has Swedish too, around 520 new lines.
- A calmer look: Dark is now neutral dark grey and Light is white and light grey. Savefile purple is saved for what matters (buttons, switches, selection, progress, the blob), so it pops.
- Smart features now actually get to your library: the passes take turns per video instead of waiting for each other to finish everything, so none of them sits at "0 videos" any more. Settings says why one is waiting ("Paused while you play"), a model that won't start says so instead of hanging, and Help, Diagnostics lists what each pass has done.
- The blob is softer: it moves like a drop of liquid now (it wobbles, squashes when it lands and splashes a little), and its smiling eyes have round tips.
- Laughter and hype: Savefile can listen for laughing, cheering and shouting, on this computer and only when it isn't busy. The videos where everyone lost it come first in Worth a rewatch, the blob tells you about them ("You and Sam couldn't stop laughing on Friday"), and searching "laughing" or "hype" finds them. It needs a small sound model, a one-time download of 15 MB, and costs about a second per minute of video. Settings, Features, Smart, Laughter and hype.
- Smart thumbnails: thumbnails now come from a lively frame near the best moment, never a black screen or a loading screen. A frame you picked yourself stays. Settings, Features, Smart thumbnails.
- Trim suggestions: when a video starts or ends with a blank screen and no sound (a loading screen, a fade, the menu before the round), the editor's Trim page offers to cut it. Nothing is cut until you say so.
- Clean up my voice: a new switch under the editor's Sound removes background hum, fans and keyboard noise from the voice track of an export and evens out its level. It runs inside the export, nothing to download.

## 0.33.0
- Meet the blob: a little purple blob now presents Worth a rewatch, with something to say about your clips. "Your Valorant clip from last night is a keeper", "3 new moments with Alex this week", "A year ago today you were playing ARK with Jo": every line comes from your own library (friends, games, dates, best moments, On this day), never made up. It changes every few seconds; click the blob for the next one. Turn it off under Settings, Features, The blob. With Reduced motion it holds still.
- A loading screen: the logo, the name and the blob while Savefile starts, saying what it's getting ready, including waking up the smart models you switched on so the first search answers at once. It stays at most 8 seconds (whatever is still loading carries on in the background), a click skips it, and it never shows when Savefile starts at login. Settings, Features, Loading screen.
- What smart features cost: every smart setting now says what it uses while it works (CPU, memory, download) and how long your library will take, first as an estimate and then timed on your own computer. While a pass runs, Settings also says how long is left.
- Best moments is about 12 times lighter: it now looks only at the key frames of each video. A 2-minute 1080p60 clip takes about a second instead of most of a minute.
- Linux: the speech library's one system dependency now comes with Savefile, so nothing extra needs installing.

## 0.32.0
- Visual search: Savefile can look at a few frames of each video, on this computer and only when it isn't busy, and then find videos by what's on screen. Search for "snow", "a car", "night" or "a boss fight" and the videos that look like it come after the ones whose names match, marked "Looks like it". Opening one starts near the frame that matched.
- Videos like this one: right-click a video to see the ones that look most alike (the same map, the same game, the same evening). Done takes you back.
- Clean up now also spots look-alikes: the same video saved twice in different files, like an export or a re-encoded copy. They're listed with the biggest file first and never ticked for you.
- It needs a visual model, a one-time download of 169 MB (the model plus the engine that runs it). Switch on Settings, Features, Smart, Visual search and similar videos, then Download the visual model. After that nothing leaves your computer, and Remove the model frees the space again.
- Clean up: the summary no longer lists "0 duplicates" when it only found other things.

## 0.31.0
- Voice search: Savefile can write down what's said in your videos (voice chat included, even on its own audio track), on this computer and only when it isn't busy. Search for something someone said, like "behind you", and the video comes up with the words on its card; open it and it starts right where they're said.
- Captions while you watch: the new CC button in the player (or V) shows what's said as captions.
- Captions for exports: in the editor, Captions, Add captions from what's said turns every line in the part you export into a caption you can still edit.
- It needs a speech model, a one-time download of 57 MB. Switch on Settings, Features, Smart, Voice search and captions, then Download the speech model. After that nothing leaves your computer, and Remove the model frees the space again (the words already found stay searchable).

## 0.30.0
- Best moments: Savefile now listens to and watches your videos, on this computer and only when it isn't busy, to find what stands out: sudden loud moments (shouts, gunfire, a crowd) and a lot going on on screen. It waits while you play. The moments that stand out most get a sparkle on their card, Sort by Best moments puts them first, and Worth a rewatch, Year in clips and Auto montage pick them (montages cut around the best few seconds it found). Nothing is downloaded and nothing leaves your computer.
- Smart titles: names from the game, who was there and when, like "Late-night Valorant with Alex and Sam · 16 Nov". Edit has a new sparkle button that suggests one. Savefile never makes up what happened in a clip.
- Smart rename (Tools): suggestions for your whole library at once. Clips with names a recorder made up ("Clip 2026-11-16 23-01-02", "Replay_20251114") are ticked; change any suggestion before you rename. Only the name shown in Savefile changes unless you tick Rename the files too, and Undo puts the old names back.
- Name new clips by themselves (off at first): new clips with a made-up name get a smart title in Savefile.
- All three are under Settings, Features, Smart.

## 0.29.0
- Backup over SSH: back up to a folder on any computer you can SSH into (your Linux server, a NAS, a Mac with Remote Login on), no extra server software needed. Sign in with your SSH key (the one in your .ssh folder is found by itself; a passphrase is kept in the system keychain) or a password. SFTP (SSH) is the default in Backup now; S3 servers work as before.
- Savefile remembers your server's key the first time it connects, like ssh does, and refuses a server that answers with a different one. Forget server key starts over if you reinstalled the server.
- One library folder: your library is now one folder by default, the same one clips are saved to, and Add folder becomes Change folder. Want videos from several places? Switch on Settings, Features, Several folders. If you already had several folders, nothing changes for you.
- Merge folders into one: with more than three folders, Savefile offers to move your videos into one folder (each old folder becomes a subfolder there). You see first what moves where, unplugged drives and recorder folders are left alone, nothing is overwritten, favourites, tags, friends, notes, collections, edits and backups go with the videos, and Undo merge puts it all back. Also under Clean up.

## 0.28.0
- Backup to your own server: keep a copy of your favourites (or every video) on a server you run at home, such as Garage, SeaweedFS or MinIO. Set it up under Tools, Backup. New clips and favourites are backed up on their own a little while after they're saved, a cloud mark shows on every video that's backed up, and nothing is ever deleted on the server. Switch it on under Settings, Features, Memories.
- Restore on a new computer: Backup finds the videos on your server that aren't on this computer and brings them back into a Restored folder, with their dates, favourites, tags, friends and notes.
- The secret key for your server is kept in the system keychain (Keychain on macOS, encrypted for your account on Windows), not in Savefile's settings.
- Search understands when: type a year ("2025"), a month ("march" or "mar"), "today" or "yesterday", or "fav" for favourites. Mix them with names, tags and friends, like "@alex 2025".
- Clean up: when you bin a copy of a video, its favourite, tags, friends, notes and bookmarks move to the copy you keep.
- Controller mode is gone, along with the gamepad support and the simple editor. Keyboard focus now gets the same clear highlight on videos and in the sidebar that Controller mode had.

## 0.27.0
- Save Year in clips as a picture: one click makes a tidy image of your year (moments, friends, games, best day) to post in the group chat. It goes to your Pictures folder.
- Make a montage with a friend: on a friend's page, Make a montage puts your best moments together in one video.
- The first look through a big library (thousands of videos) now keeps its progress as it goes, so quitting halfway doesn't start it over.
- Fixed: on Windows, updating could fail (and Savefile not come back) when your user name has letters like é or ö. Updates are now copied next to the app first and only swapped in when the copy worked, so a busy file can't leave a half-updated app.
- Fixed: quitting during an export left ffmpeg running in the background until it finished.
- Fixed: after the fourth display sleep or unplugged device in one run, clipping stopped instead of restarting.
- Fixed: Storage limits could move montages and exports you made to the trash. They only tidy clips now.
- Fixed: quitting right after a very long session could leave a large unfinished file in the clips folder. The session is now saved properly at the next start instead.
- Fixed: a cancelled Steam clip import left a temporary file behind.

## 0.26.0
- Play all: watch a whole view one clip after another in the Quick player, like a slideshow. Use Play all in the header (a friend, a collection, a tag, a year of search results, anything), or switch it on for the video you're watching with the button next to the arrows. Switch it off under Settings, Features, Memories.
- A friend's page says how long you've been saving moments together, and what you mostly play ("24 moments together since March 2025 · mostly Valorant").
- Rename or merge friends and tags: right-click one in the sidebar. Renaming "Jo" to "Joanna" fixes every clip at once, and renaming to a name that already exists merges the two. You can also take a tag or a friend off every video.
- The Quick player shows when a clip was recorded and who was there, next to the time.
- From December to mid-January, All videos shows a card when your year in clips is ready.
- Fixed: after leaving a video while it was in a slow-motion part, the next video played at the wrong speed.
- Fixed: closing the Quick player right after picking another video could leave that video playing with nothing on screen, and lose where you were in the last one.
- Fixed: phone videos recorded upright showed captions, blur and zoom boxes in the wrong place (and the blur landed in the wrong spot in the export). Videos added from now on get this right.
- Fixed: exports with effects failed for videos with an odd width or height, such as a screen recording of part of the screen.
- Fixed: dragging on the timeline or a blur box could get stuck if the player closed mid-drag, and the box then followed the mouse.
- Fixed: a thumbnail cut off halfway through stayed broken until the video changed.
- Fixed: one error in a controller action switched the controller off until restart, and a button held while the window was in the background fired when it came back.
- Fixed: Clean up suggested binning videos that were still being recorded.
- Fixed: keyboard shortcuts you changed weren't remembered if you saved Settings afterwards (since 0.25.0).
- Fixed: switching off Sessions under Settings, Features while a session was recording left it running with no way to stop it. It's now finished and saved.
- Fixed: renamed or removed tags and friends came back on videos stored on a drive that was unplugged at the time.
- Fixed: Play all stayed on after going from the Quick player to the editor, and the Play all button could select a video in select mode.
- Fixed: renaming a tag or a friend in the sidebar no longer jumps to it, and clicking through videos quickly no longer loses where you were in one.
- The loud-moments scan stops when the player closes, instead of reading the rest of a long session recording.

## 0.25.0
- Friends in the sidebar: everyone you've tagged under Who was there gets a row, with the people you play with most on top. Click a name to see every clip you have together.
- Year in clips (sidebar, Tools): look back on a year. How many moments you kept, the days you saved something, your busiest month, the friends who were there most, what you played, your first save of the year and your biggest day. Pick an earlier year at the top.
- Make a montage of the year: one click from Year in clips starts Auto montage with that year's favourites (topped up with clips with friends), oldest first.
- Playing with: while clipping is on, tell Savefile who you're playing with (under the clipping box) and every clip and session you save is tagged with them until you clear it.
- Fixed: if a library folder was on a drive that wasn't plugged in (an external SSD, a NAS) when Savefile started, its favourites, tags, friends and notes could be lost. They now wait for the drive to come back.
- Fixed: saving Settings, even just to change the theme, restarted capture. That split a running session in two and emptied the replay buffer. Capture now only restarts when a capture setting changed.
- Fixed: two clips saved in the same second (a double press, or two hotkeys) lost the second one. Both are kept now.
- Fixed: Steam clips you deleted (or that the storage limits removed) came back at the next start.
- Fixed: with OBS or NVIDIA added under Other recorders, Savefile's own clips got a "Savefile Clips" game tag.
- Fixed: a video renamed or moved outside Savefile dropped out of its collections and lost its remembered edits.
- Fixed: the Vertical share preset failed on tall phone recordings.
- Fixed: a cancelled export could leave a large temporary file behind on Windows.
- Fixed: when Clipping or Sessions are switched off under Settings, Features, the tray menu no longer offers them.
- Fixed: a Settings change that was refused (for example an archive folder inside the clips folder, which is now caught) could still be saved later by accident.
- Capture that can't start (a full disk, for example) says so instead of showing "Starting capture…" forever.
- Settings, the library and collections are written more safely, so a power cut right after a change can't leave an empty file. A file that can't be read is kept aside instead of being overwritten.
- Adding a whole drive as a library folder works on Windows.
- Year in clips can be switched off under Settings, Features, Memories. The Friends list and Playing with go with Who was there.

## 0.24.0
- Fixed: jumping to the previous or next video with ← / → in the Quick player (and the editor) could leave a black picture until the player was closed and opened again. The next video now always shows up, and switching is a little smoother too.
- The Quick player has the same previous / next video buttons as the editor, next to Edit.
- The last bits of the old blue-green (cyan) colour are gone: selected icons, In / Out times, the timeline selection, statuses and the stats bars are now Savefile purple, in dark and light.
- Buttons line up: Cancel, Save, Move to trash and the other main buttons now have the same height and text size everywhere.
- Light look: dialogs dim the app with a soft violet shade instead of near-black, their shadows are lighter, and switches that are off use a soft outline instead of a hard black one.
- Text boxes keep the card colour while you type instead of turning black (dark) or stark white (light), and drop-downs, checkboxes and text boxes use the same text size in Settings, dialogs and the welcome setup.
- Library: the counts in the sidebar line up for folders too, the rows of videos sit closer together, and the select button has an outline icon like its neighbours.
- Start over in the editor's Trim page is a proper button now.
- Stats shows seconds for a library under a minute instead of "0 min".
- Other recorders: "Not found on this computer" reads as a normal sentence instead of a file path.
- A failed update check says what went wrong in plain words (no connection, or GitHub is busy), and the message goes away after the next check that works.
- Fixed a rare crash when capture had to be stopped because the replay buffer got stuck.

## 0.23.0
- Cnema is now **Savefile**. Some saves you never overwrite: the app is about the moments you keep with your friends, so it has a name and a look to match. New icon (a memory card with a play button), and the logo sits right in the sidebar. Your library, settings and clips stay exactly where they were.
- Worth a rewatch: a row at the top of All videos with old favourites, clips with friends and things you haven't seen in a while. A new pick every day.
- On this day: a card with what you recorded on this day in earlier years (or a month ago), with Watch again.
- Who was there: tag the friends in a clip under Rename, tag or move. Their names show on the card ("with Alex and Sam"), and search finds them, or only them with @name.
- Milestones: a small celebration for your first save, your 10th, your 100th and so on. Saving a clip also gets a friendlier message.
- One look, in Savefile purple, now in dark and light. Pick Dark, Light or Same as system under Settings, General, Appearance (or in the welcome setup). Same as system switches along with your computer. The other colour schemes are gone; if you used one, you get the purple look.
- Dark is a step lighter, so panels and cards stand apart instead of everything sitting on near-black.
- Friendlier words around the app: the library header counts the moments you've kept, and empty screens and the delete prompt read more like a person wrote them.
- All of the new parts can be switched off under Settings, Features, Memories.
- The app file is still called Cnema.app / Cnema.exe for this release so updates keep working; it shows as Savefile everywhere you see it.

## 0.22.0
- Controller mode, reworked. The cursor is now a rounded ring with a soft glow in your colour scheme, like the hover glow, instead of the sharp white box.
- The D-pad goes to whatever is actually in that direction, in Settings, dialogs and the toolbar, instead of jumping around. Coming back to the grid lands on the card you were on, and Auto montage and Other recorders can be reached in the sidebar.
- Simple editor with a controller: only Trim and Export (In / Out, parts, markers, thumbnail, save frame), so everything can be reached with the pad. Anything you added on the desktop still exports. Switch it off under Settings, General, Controller.
- In the editor, ▲ / ▼ put the cursor on the tool panel or the controls under the video, and B takes it away again. LT / RT now change the volume.
- The controller legend at the bottom now shows the right buttons in Settings, dialogs and the editor's tools.

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
