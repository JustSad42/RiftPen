# RiftPen

An Epic Pen-style screen drawing tool for League of Legends. In **replays and spectator mode**, drawings stick to the map and to the replay timeline. Pan, zoom or scrub, and a circle around Baron stays around Baron and shows up again whenever you rewatch that moment.

**Install:** download `RiftPen.exe` from the [latest release](https://github.com/JustSad42/RiftPen/releases/latest), put it in a folder of its own and run it. It needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0). On first run RiftPen adds itself to the Start menu (you can turn this off in Settings → General). It also sits in the notification area: left-click the tray icon to show or hide the toolbar, right-click for Settings, Start with Windows and Quit.

**Updates:** RiftPen checks GitHub for a new version 30 seconds after it starts and then every 6 hours. A new version downloads in the background, is checked against its SHA-256, and installs when you quit RiftPen. To install it straight away, right-click the tray icon → **Restart to update**, or use Settings → General → Updates (where you can also check now or turn automatic updates off).

## Quickest way to use it

- **Tap `` ~ ` ``** to turn drawing on or off while League is focused.
- **Hold `` ~ ` ``** to open the **tool wheel** at your cursor. Slide over a color (inner ring), then over a tool (outer ring), and let go. You never need to reach for the toolbar.
- **Hold `F1`** while drawing to see all your shortcuts on screen.
- The toolbar collapses to just the Draw button when you're not drawing and expands when you start.
- Each tool remembers its own color and size. The laser stays red and the highlighter yellow, for example.

## The toolbar

| | |
|---|---|
| **✎ Draw** | Draw mode on/off. When off, clicks go through to the game. |
| Select | Click a drawing, then drag to move it, scroll to resize it, or pick a color to recolor it. **Range circles:** drag the circle's edge (or the white handle) to resize it; the size shows in game units as you drag, and Shift snaps to 25. This also works with the range circle tool. |
| Pen, highlighter, **laser**, **ping**, eraser | The laser fades after a moment. Ping makes a League-style ripple where you click. A "Ping at cursor" shortcut does the same without turning on drawing. |
| **Fading ink** | Press `I` while drawing (or Settings → General) and pen, highlighter, line, arrow, rectangle and circle strokes disappear on their own after a few seconds (4 by default, adjustable). Good for live commentary. Fading strokes aren't saved. |
| Spotlight | Dims everything except a soft circle around your cursor (`Ctrl+Alt+S` in League). Size and darkness are in Settings → General. |
| Shapes ▾ | Line, arrow, rectangle and circle share one button that remembers the last one used. Shift snaps lines to 45° and makes squares. On the map, circles grow from the center. |
| Text | Click, type, press Enter. |
| Stamps | **Vision:** ward, control ward, farsight ward, sweep. **Spells:** Flash, Teleport, recall. **Objectives:** dragon, Baron, Rift Herald, Void Grubs. **Calls:** danger, missing, on my way, target, objective, team dots. |
| Range circles | Vision, XP, tower, summoner spells, plus **champion abilities** from Riot's Data Dragon. |
| Color dot ▾ | Dropdown with colors and size. |
| AUTO / MAP / SCREEN | Where new drawings go. The dot turns green when a replay is connected. |
| Clock | Timeline notes on/off. |
| Undo, redo, clear, hide | Clear only removes what's on screen now; replay notes at other moments stay. |
| Folder | **Boards**: save the current drawings as a reusable layout, load one, recent boards. |
| Camera | Screenshot of the game plus your drawings, saved to `Pictures\RiftPen` and copied to the clipboard. |
| ‹ | Collapse the toolbar to just the Draw button. |

## Replay features

- **Timeline notes:** while a replay is open, each drawing remembers the moment you made it. It appears when the replay reaches that moment and fades out after 20 s (adjustable, or "until the end"). This works when rewinding and rewatching. Notes are saved per replay automatically.
- **Auto-pause:** turning on draw mode pauses the replay, and turning it off resumes it.
- **Replay control while drawing:** Space plays/pauses, ← → skip 5 s, ↑ ↓ change speed, and Shift+← → jump to the previous or next note.
- **Notes timeline strip:** a thin bar over the replay shows a dot for every note. Click a dot to jump to it, or click anywhere on the bar to seek. Drag the `⋮` grip to move the bar. Hide it with its `×` button or `Ctrl+Shift+N` (in League); bring it back with the same shortcut or by right-clicking the toolbar's timeline button.
- **Notes list** (`N` while drawing, or the list icon on the strip): every note in this replay with its time. Jump to a note, give it a title, delete it, or **copy the whole list as text** for Discord or a doc.
- **Circles and arrows that follow a champion:** turn on Follow in the range menu (or press `F` while drawing). Then click a champion with the range circle to attach it to them, or start an arrow on a champion so it keeps starting from them. Positions come from the Rift Board's minimap tracking, so this works for any champion, not just the one the camera is on (keep League's minimap visible). Clicking away from champions while the camera is locked onto one attaches the circle to that champion as before. Works on the Rift Board too.
- **Ward vision:** ward and control ward stamps placed on the map show the area they'd reveal (900 units; farsight wards 500). Turn it off in Settings → General.
- **Hide RiftPen from stream capture** (`Ctrl+Alt+C`, the tray menu, or Settings → General): OBS, Discord and other screen capture show your drawings but not RiftPen's toolbar, notes bar, menus or settings. You still see them. The Rift Board stays capturable, so you can stream it.
- **Minimap (automatic):** when a replay opens, RiftPen finds the minimap by itself. It pauses for a split second, hides the minimap through the Replay API and sees what changed. It does this again whenever you change minimap size, minimap side, HUD scale or resolution in League. Map drawings then also appear on the minimap, and drawing on the minimap places drawings on the map. *Find minimap now* and *Set it by hand…* are in Settings → Replay.

## Rift Board

A big Summoner's Rift with every champion on it, synced to the replay that's open in League. Open it with the globe button on the toolbar or `B` while drawing. Put it on a second monitor and coach from it while the replay runs.

- **Finds the game by itself:** it matches the open replay to its `.rofl` file (Documents\League of Legends\Replays), then loads Riot's timeline for that game from your League client: kills, towers, objectives, and per-minute gold, XP, CS and positions.
- **Champion positions:** while the replay plays, RiftPen reads the minimap a few times a second (the screen only, nothing in the game's memory). It finds each icon by its blue or red team ring and learns what every champion's icon actually looks like, so custom skins work too. Learned icons are remembered across replays (up to four looks per champion, one per skin), so later games are recognised right away. Settings (⚙) → **Forget learned icons** starts over if champions keep getting mixed up. Positions are saved per game, so any part you've watched once can be scrubbed later. Readings that would mean a champion moved faster than they can (about 700 units/s plus one Flash or dash) are thrown out unless the next readings agree, which still lets Teleport and recalls through. When an icon is briefly hidden, the champion stays where it was last seen (slightly faded) instead of jumping. Parts you haven't watched are estimated from the timeline and drawn with a dashed ring. The green stripe on the timeline shows what has been recorded. Keep League's minimap visible: when another window covers it, recording pauses.
- **Timeline:** play/pause, ±10 s, speed, and a scrubber with the team gold difference, kills, objectives and towers. While **Linked**, scrubbing moves the replay and the replay moves the board. Unlink for a sandbox: the board moves through the game on its own and the replay stays put.
- **Explain plays:** drag champions anywhere (the replay pauses; **Reset** or moving on in time puts them back). Turn on **Draw** (`D`) for pen, arrow, circle, range circle, ward, text, laser, eraser and select. Board drawings are map drawings: they also appear in the game at that moment of the replay, and in-game map drawings show on the board.
- **At a glance:** champions show their level, dead champions turn grey with a respawn countdown, recent kills and objectives flash where they happened, towers and inhibitors show as destroyed, trails show the last 6 seconds of movement, and the replay camera's view is outlined. Double-click the map (or a champion) to move the replay camera there.
- **Stats:** score, gold lead, and per champion KDA, CS, level, gold and items (items come from the game, so they show while the board is at the replay's moment), plus objectives per team. Click a champion for more detail.
- **Objective timers (Timers):** drake/Elder, Void Grubs, Rift Herald and Baron countdowns on their pits (or "up" when alive), plus inhibitor respawns, at the board's moment. Worked out from the kills in Riot's timeline with today's spawn rules (drakes 5:00 then every 5:00, Elder every 6:00, grubs 8:00, Herald 16:00, Baron 20:00 then every 6:00), so very old replays may differ.
- **Lane matchup:** pick Top, Jungle, Mid, Bot or Support to compare the two players: gold, CS, level and XP with the difference, and a graph of the gap (gold, CS or XP) over the whole game. Click the graph to jump there.
- **Paths and heatmap (Paths ▾):** a fading line of where champions walked in the last 1, 2 or 5 minutes, or a heatmap of where they spent their time (last 5 or 10 minutes, or the whole game so far). Select a champion to see only them; otherwise everyone shows in team colours. Uses the positions recorded from the minimap.
- **Map that looks like League's:** the board uses the game's own minimap terrain for the current patch (from CommunityDragon), redrawn at 2048 pixels with smooth wall edges so it stays sharp when zoomed in, and with the dark walls League's minimap uses. After the second drake, the map switches to the elemental rift the game turned into (Infernal, Ocean, Mountain, Cloud or Hextech). Jungle camps and scuttle crabs are marked, with a countdown before they first spawn.
- **Wards (Wards):** every ward that's out at the board's moment, with its vision area and time left (stealth trinkets 90–120 s by level, support wards 150 s, control and farsight wards until cleared, and the per-player limits). Cleared wards show a cross for a few seconds. Riot's timeline says who placed a ward and when, but not where, so each ward sits where its owner was standing at that moment (wards land within about 600 units). When that spot is only estimated because that part of the replay wasn't recorded, the ward has a dashed ring. The side panel counts wards placed, out now and cleared per team, and per champion when one is selected. Ward data needs a Riot API key or a game loaded from the client.
- **Lanes and turret plates:** small pips under each outer turret show how many of its 5 plates are left (until plates fall off at 14:00). The LANES section shows each lane's turrets (outer, inner, inhibitor) for both teams and how many plates each team has taken. Riot's data has no turret health, so plates are the closest measure before 14:00.
- **Jungle start (Jungle):** each jungler's first clear, worked out from the recorded minimap positions: numbered camps with the time they got there, and their path, for the first 4 minutes. A camp counts once the jungler has spent a few seconds at it after it spawned. The JUNGLE START section lists the route as text; click a jungler there to show only theirs. It needs that part of the replay (1:30–4:00) to have been played with the minimap visible.
- **Second monitor:** with "Open the Rift Board on a screen League isn't on" (⚙, on by default), the board opens maximized on another screen so it never covers the minimap.
- **Riot API key (optional, ⚙):** adds item purchase history and skill order. Development keys from developer.riotgames.com expire after 24 hours.

Map controls: mouse wheel zooms, drag with the left button (on empty map), right or middle button to pan, **Fit** resets the view. Space plays/pauses, ← → skip 5 s.
RiftPen remembers your choices between launches: the last tool, colour and size, AUTO/MAP/SCREEN, timeline notes on or off, the notes bar, and on the Rift Board Clean map, Trails, Linked/Sandbox and where you were zoomed in. Settings are saved about a second after each change, so nothing is lost if RiftPen is closed from Task Manager or the PC restarts.

## Shortcuts

Every action can be rebound in Settings → **Shortcuts**: click it, then press a key, a combo, or a mouse button (middle / Mouse 4 / Mouse 5). Each shortcut also has a **scope**:

- **While drawing:** only while draw mode is on, so it doesn't interfere with typing.
- **In League:** also while League (or RiftPen) is the focused window. Other apps still receive that key normally.
- **Everywhere:** works in any app, and the key is taken over everywhere.

Defaults: `` ` `` toggles drawing (in League). While drawing: `S` select, `P` pen, `H` highlighter, `Q` laser, `I` fading ink on/off, `E` eraser, `L` line, `A` arrow, `R` rectangle, `O` circle, `T` text, `K` stamp, `V` range circle, `1`–`0` colors, `[` `]` size, `Ctrl+Z`/`Ctrl+Y` undo/redo, `Del` clear, `Backspace` delete selection, `M` AUTO/MAP/SCREEN, `G` grid, `Esc` stop. `F8` takes a screenshot (in League). `Ctrl+Alt+H` hides drawings and `Ctrl+Alt+T` shows or hides the toolbar (everywhere).

Shortcuts work in-game through a low-level keyboard/mouse hook, the same method Discord push-to-talk uses, because League blocks ordinary Windows hotkeys. If they don't respond inside League, League is probably running as administrator; run RiftPen as administrator too.

## One-time setup for map drawings

1. With the game closed, open Settings → Replay → **Turn on the Replay API in game.cfg**.
2. Open a replay (Match History → Watch).
3. Calibrate once: use the default top-down camera, then press **Auto-calibrate**. The test grid should stay fixed to the ground.

RiftPen uses only Riot's official Replay API (`https://127.0.0.1:2999`) for the camera, clock and playback control. It never reads or writes game memory. That API only runs in replays and spectator mode, so in your own live games RiftPen works as a normal screen pen.

## Performance

When nothing is happening, RiftPen checks in only 4 times a second (about 0.5% of one CPU core). It switches to redrawing at your monitor's refresh rate the moment the camera moves or you draw, and drops back about 1.5 s after things go still. Camera polling also slows from about 250 to about 30 checks a second when the camera is still. The mouse hook is only installed while a shortcut actually uses a mouse button. Drawings that stay fixed on screen are drawn once and reused, and pen strokes are simplified and smoothed when you finish them. On a drawing tablet, line width follows pen pressure (can be turned off in Settings → General).

## Files

- Settings: `%APPDATA%\RiftPen\settings.json`
- Drawings and replay notes (autosaved): `%APPDATA%\RiftPen\drawings.json`
- Boards: `%APPDATA%\RiftPen\boards\*.rpboard`
- Champion data cache: `%APPDATA%\RiftPen\ddragon\`
- Rift Board game data and recorded positions: `%APPDATA%\RiftPen\games\`
- Learned champion icons: `%APPDATA%\RiftPen\learned-icons.bin`
- Errors: `%APPDATA%\RiftPen\error.log`

## Tips

- League must run Borderless or Windowed (yours is Borderless).
- Streaming: OBS Game Capture won't see the overlay. Use Display Capture.
- If the overlay ever shows a solid background, turn off Fast rendering in Settings → General.

## Building

The code is on GitHub at [JustSad42/RiftPen](https://github.com/JustSad42/RiftPen) (branch `main`).


```
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

**Releasing:** `.\release.ps1 1.4.0 "What changed"` sets the version, commits, tags and pushes. GitHub Actions then builds `RiftPen.exe` with its `.sha256` and publishes the release, and installed copies update themselves.

`RiftPen.exe --selftest <folder>` runs the built-in checks (map math, undo, saving and loading, timeline, shortcuts) and renders sample images.
