# RiftPen

An Epic Pen-style screen drawing tool for League of Legends. In **replays and spectator mode**, drawings stick to the map and to the replay timeline. Pan, zoom or scrub, and a circle around Baron stays around Baron and shows up again whenever you rewatch that moment.

Run `publish\RiftPen.exe`. It needs the .NET 10 Desktop Runtime, which is already installed on this PC. RiftPen also sits in the notification area: left-click the tray icon to show or hide the toolbar, right-click for Settings, Start with Windows and Quit.

## The toolbar

| | |
|---|---|
| **✎ Draw** | Draw mode on/off. When off, clicks go through to the game. |
| Select | Click a drawing, then drag to move it, scroll to resize it, or pick a color to recolor it. |
| Pen, highlighter, **laser**, eraser | The laser fades after a moment, which suits streams and quick "look here" moments. |
| Line, arrow, rectangle, circle, text | Shift snaps lines to 45° and makes squares. On the map, circles are drawn from the center outward. |
| Markers | Ward, control ward, danger, missing, on my way, target, objective, team dots. |
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
- **Minimap:** in Settings → Replay → *Set minimap area…*, drag a box around your minimap once. After that, map drawings also appear on the minimap, and drawing on the minimap places drawings on the map.

## Shortcuts

Every action can be rebound in Settings → **Shortcuts**: click it, then press a key, a combo, or a mouse button (middle / Mouse 4 / Mouse 5). Each shortcut also has a **scope**:

- **While drawing:** only while draw mode is on, so it doesn't interfere with typing.
- **In League:** also while League (or RiftPen) is the focused window. Other apps still receive that key normally.
- **Everywhere:** works in any app, and the key is taken over everywhere.

Defaults: `` ` `` toggles drawing (in League). While drawing: `S` select, `P` pen, `H` highlighter, `Q` laser, `E` eraser, `L` line, `A` arrow, `R` rectangle, `O` circle, `T` text, `K` marker, `V` range circle, `1`–`0` colors, `[` `]` size, `Ctrl+Z`/`Ctrl+Y` undo/redo, `Del` clear, `Backspace` delete selection, `M` AUTO/MAP/SCREEN, `G` grid, `Esc` stop. `F8` takes a screenshot (in League). `Ctrl+Alt+H` hides drawings and `Ctrl+Alt+T` shows or hides the toolbar (everywhere).

Shortcuts work in-game through a low-level keyboard/mouse hook, the same method Discord push-to-talk uses, because League blocks ordinary Windows hotkeys. If they don't respond inside League, League is probably running as administrator; run RiftPen as administrator too.

## One-time setup for map drawings

1. With the game closed, open Settings → Replay → **Turn on the Replay API in game.cfg**.
2. Open a replay (Match History → Watch).
3. Calibrate once: use the default top-down camera, then press **Auto-calibrate**. The test grid should stay fixed to the ground.

RiftPen uses only Riot's official Replay API (`https://127.0.0.1:2999`) for the camera, clock and playback control. It never reads or writes game memory. That API only runs in replays and spectator mode, so in your own live games RiftPen works as a normal screen pen.

## Files

- Settings: `%APPDATA%\RiftPen\settings.json`
- Drawings and replay notes (autosaved): `%APPDATA%\RiftPen\drawings.json`
- Boards: `%APPDATA%\RiftPen\boards\*.rpboard`
- Champion data cache: `%APPDATA%\RiftPen\ddragon\`
- Errors: `%APPDATA%\RiftPen\error.log`

## Tips

- League must run Borderless or Windowed (yours is Borderless).
- Streaming: OBS Game Capture won't see the overlay. Use Display Capture.
- If the overlay ever shows a solid background, turn off Fast rendering in Settings → General.

## Building

```
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

`RiftPen.exe --selftest <folder>` runs the built-in checks (map math, undo, saving and loading, timeline, shortcuts) and renders sample images.
