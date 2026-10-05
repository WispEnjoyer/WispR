# WispR

A small (~250 KB) replacement for the Windows Start menu **and taskbar** (Windows 10/11).

- Tap the **Windows key** → the launcher rises out of the bottom edge of the screen; type part
  of an app's name, press **Enter**. (Settings → General → Position: attached to the
  bottom edge, middle or top.)
- **Double-tap** the Windows key → hide/show the taskbar and system box.
- A floating, centered **taskbar** with your pinned and running apps, and a separate
  **system box** with tray icons, keyboard layout, network, volume, battery, clock and
  notifications — all in the same style as the launcher.

No install, no runtime download — it uses .NET Framework 4.8, which is built into Windows.

## Run it

1. Put `WispR.exe` somewhere permanent, e.g. `C:\Tools\WispR\`.
2. Double-click it. A small blue-squares icon appears in the tray.
3. Right-click the tray icon → **Start with Windows** to launch it at login.

Windows SmartScreen may warn the first time because the exe isn't code-signed:
click **More info → Run anyway**.

## Taskbar

The Windows taskbar is hidden while WispR's is on, and put back exactly as it was when
you exit WispR or turn its taskbar off.

**Start & power** — their own small box in the bottom-left corner. The power button (right of
Start) offers lock, sleep, sign out, restart and shut down. Start opens the **Start menu**
(search stays on the Windows key):
- *Pinned* — a grid of your pinned apps (suggestions from your most-used apps until you pin some).
- *Recommended* — apps you use often, with when you last used them.
- *All apps* — an A–Z list with a **search box** (Enter opens the top match). Scroll down on
  the Start menu to jump to it. Drag the scrollbar, click above/below it to jump a page, or
  **middle-click** to auto-scroll (move the mouse up/down; click again or press Esc to stop).
- Bottom row: your account (account settings, lock, sign out), File Explorer and Settings.
- Right-click any app: open, run as administrator, pin/unpin to Start or taskbar, open file
  location, uninstall.

**Apps** — pinned apps first (**drag them to reorder**), then other running apps. Click to switch to an app (click
again to minimize), middle-click for a new window. A coloured bar marks running apps; the
active app is highlighted, and apps asking for attention glow in the accent colour. Apps
with **several windows** get one dot per window (up to three) and a small count badge.

**Hover** a running app for half a second for **live previews** of its windows (they update in real time).
Click a preview to switch to that window, hover it and click ✕ (or middle-click) to close
it. Apps with several windows show one preview per window.

**Right-click** an app for: its open windows, New window / Open, Run as administrator,
Pin / Unpin from taskbar, Open file location, Restore / Minimize / Maximize, End task, and
Close window(s). Pin apps from here or from the launcher's right-click menu.

**CPU / RAM box** (bottom-left corner) — live CPU and memory use with a 30-second graph and
a load bar (accent colour normally, amber above 60 %, red above 85 %). Hover for details
(e.g. "11.4 of 16.0 GB in use"); click it or the icon for Task Manager; right-click for
Resource Monitor or to hide the box (Settings → Taskbar brings it back).

**Media player** (next to the CPU / RAM box, only while something plays) — cover art, title,
artist and progress for Spotify, YouTube in your browser, media players and anything else
that shows up in Windows' own media controls. Previous / play-pause / next buttons; click the
title to bring the app to the front. **Click or drag the progress line to jump** within the
song. (Some apps, e.g. Firefox-based browsers, don't report a song length — then there's no
progress line.)

**System box**
- *Tray icons* — the usual app icons, left/right/double-click work as normal. Right-click
  menus that are standard Windows menus (and WispR's own) are shown in WispR's style,
  growing out of the bar (the app still gets your choice exactly as from its own menu); apps
  that draw their own menus keep theirs. The arrow
  collapses them.
- *Keyboard layout* — click to switch (same as Win+Space). Only shown if you have more than one.
- *Upload / download speed* — live ↑/↓ speed in KB/s, MB/s or GB/s, highlighted above
  1 MB/s. Click for Task Manager.
- *Volume* — click for a **volume slider** (with your output device, a mute button and a
  shortcut to sound settings). Scroll on the icon or the slider to change it, middle-click
  to mute.
- *Network, battery* — click for Windows' quick settings.
- *Clock* — click for the **calendar**: time with seconds, the full date, the month with
  week numbers (KW). Arrows or scrolling change the month; click the month name for today.
- *Bell* — Windows' notifications.
- *Show-desktop corner* — the thin line at the end.
- Right-click any of them for the matching Windows settings page.

Right-click an empty part of the bars for WispR settings, Task Manager, Show desktop,
hiding the bars, or turning the taskbar off.

**Several monitors** — every monitor gets its own taskbar and system box (tray icons, CPU /
RAM and the media player stay on the main monitor, like Windows). By default each taskbar
shows the windows that are on its own monitor; pinned apps appear on all of them. Both are
options in Settings → Taskbar. Plugging monitors in/out is picked up automatically.

The bars hide automatically while a game or video is fullscreen (only on that monitor). While a window is
**maximized**, the whole bar area is filled edge to edge (no desktop peeking through
around the floating bars); this can be turned off in Settings → Taskbar.

**Log file** — WispR keeps a small diagnostic log (errors, and anything the watchdog
had to repair) at `%APPDATA%\WispR\log.txt`. Right-click the WispR tray icon →
**Open log file**. If something misbehaves, that file says what happened.

**If the Windows taskbar ever stays hidden** (for example if WispR was force-closed in
Task Manager): run **Restore Windows taskbar.bat** from the zip, or
`WispR.exe --restore-taskbar`.

## Settings

Right-click the WispR icon in the tray (or an empty spot on the bars) → **Settings…**.
Everything applies instantly and is saved to `%APPDATA%\WispR\settings.ini`.

The window has a sidebar with six sections, and it takes on your current theme (it recolours
live when you pick another one):

- **Appearance** — pick one of 8 themes from preview tiles (Midnight, Light, Nord, Dracula,
  Catppuccin, Gruvbox, Rosé Pine, Solarized Light), or click any colour to make your own
  palette; opacity and launcher width.
- **Background** — use your desktop wallpaper (it follows changes, including Wallpaper Engine)
  or a picture of your own; show it behind the launcher and bars or only take its colours;
  tint and blur; **Match theme colours** builds the palette from the picture (Auto, Dark or
  Light). Picking a theme or a colour by hand switches matching off.
- **Wallpapers** — what the `wallpaper` carousel shows, and your pictures folder.
- **Taskbar** — on/off, screen edge, apps centred or left, where the system box goes, icon
  size, gap from the edge, keeping maximized windows clear, the band behind the bars, and
  multi-monitor options.
- **Screen frame** (in Taskbar) — a border all the way around the screen, thin on three sides
  and as tall as the bars at the bottom, with rounded inner corners. The bars sit in it, the
  launcher grows out of its top edge. It steps aside while a window is maximized on that screen
  (the window fills the screen as usual, above the bars). Clicks pass through it; it hides for
  fullscreen apps and with the double-tap, like the bars.
- **Top drop-down** (in On the bars, on by default) — touch the top edge of the screen with the
  mouse (around the middle) and a panel slides down out of the frame; move away and it slides
  back up. **Media**: cover, title, artist, a progress bar you can click or drag to seek, and
  previous / play-pause / next. A vinyl record slides out from behind the cover and spins while
  the song plays — coloured from the cover, in a pressing picked per song (solid, splatter,
  swirl, smoke, galaxy, pinwheel, bullseye, marble, glitter or haze), with the
  cover as its label. The progress is a waveform in the cover's colour (click or drag to seek;
  hovering previews the time), and the play button takes the cover's colour too. **Performance**: CPU, memory and GPU temperature as rings
  (orange/red when busy or hot). The GPU temperature and load come from AMD's driver library
  (Radeon cards); on other GPUs that ring shows "unavailable". It doesn't open while you drag a window to the top, or over
  fullscreen games and videos. With it on, the CPU/RAM and media boxes leave the bar.
- **On the bars** — which boxes and items show (Start, Task view, CPU/RAM, media, tray,
  keyboard layout, network, speed, volume, battery, notifications, clock, date, seconds,
  show-desktop corner).
- **General** — Windows-key takeover and double-tap speed, launcher position, web search
  engine, the Start menu name and account picture, start with Windows.
- **Preview launcher** (bottom left) opens the launcher so you can see your changes.

## Right-click menu

Right-click any result (or press the Menu key / Shift+F10):

- **Open** / **Run as administrator**
- **Pin to Start** — shows it in the Start menu's pinned grid (and first in the launcher
  when the search box is empty)
- **Pin to taskbar** — pins it to the WispR taskbar
- **Open install folder** — selects the app's .exe in Explorer (Store apps open their
  package folder, which Windows may not let you browse)
- **Open shortcut location** — the Start-menu shortcut
- **More Windows options…** — the full Explorer menu (Uninstall, Properties, Send to…).
  Hold Shift for extended options.

(With the WispR taskbar turned off, "Pin to taskbar…" pins to the Windows taskbar
instead. Windows only allows Explorer itself to do that, so WispR opens Explorer with
the shortcut selected: right-click it → **Pin to taskbar**.)

You can also **drag** an app out of WispR: onto the desktop or into a folder to
create a shortcut, or (on Windows 10) onto the taskbar to pin it.

## Keys

| Key | Action |
|---|---|
| Windows (tap) | Open / close the launcher |
| Windows (double-tap) | Hide / show the taskbar and system box |
| Type | Search apps |
| ↑ ↓ / Tab | Move selection |
| Enter | Launch |
| Ctrl+Shift+Enter | Launch as administrator |
| Esc | Clear search, then close |
| Menu key / Shift+F10 | Right-click menu for the selected app |
| Ctrl+Esc | Still opens the real Windows Start menu |

All Windows-key shortcuts (Win+E, Win+L, Win+Shift+S, Win+V…) keep working —
only a lone tap of the key is taken over. To get the normal Start menu back
temporarily, untick **Replace Windows key Start menu** in the tray menu.

## Calculator, units and Windows settings

The launcher works out what you mean:

- **Maths** — `15*9`, `15x9`, `(3+4)*2`, `2^10`, `100/3`, `15,5*2`, `19% of 250`,
  `=sqrt(2)`, `2pi`. The answer is the top row; **Enter copies it**.
- **Units** — `5 km in miles`, `100 f to c`, `2 gb in mb`, `60 mph in km/h`, `10 kg to lb`,
  `500 ml in cups`, `1 day in hours`… (length, weight, volume, speed, time, data, area,
  temperature; "in", "to" or "nach").
- **Windows settings** — `bluetooth`, `display`, `sound`, `wifi`, `dark mode`, `uninstall`,
  `taskleiste`, `lautstärke`, `drucker`… jump straight to the page (English and German
  words). Classic tools too: Device Manager, Services, Registry Editor, Environment
  variables, Control Panel…

App names that contain numbers ("7-Zip", "Office 365") are still treated as app searches.

## Wallpaper picker

Type **`wallpaper`** (or `>wallpaper`, `hintergrund`) in the launcher for a carousel of your
wallpapers — an endless loop in both directions. The selected one is shown large in the middle; under each picture are five
circles with the colours WispR would use for it (background, surface, selection,
accent, text). Scroll, use ← / → (Page Up/Down jumps 5), or click a side picture to browse.
**Enter** or clicking the middle picture sets it as your desktop wallpaper; **Ctrl+Enter**
also switches WispR to that wallpaper's colours. `wallpaper city` filters by name.

The pictures come from the folder set in Settings → Wallpapers → Pictures folder
(automatic: `Pictures\Wallpapers` if it exists, otherwise Windows' own wallpapers), plus your
current wallpaper.

Settings → Background → *Show the picture*: turn it off to keep
a solid background while still taking the theme colours from your wallpaper (with *Match theme
colours to the image* on).

### Wallpaper Engine

If Wallpaper Engine is installed (found through Steam), the carousel shows **only the
wallpapers you favourited (♥)**, marked **▶ Scene / Video / Web**. Favourites are read from
Wallpaper Engine's settings, and you can also ♥ wallpapers right in the carousel: type
`wallpaper all` to see everything, then **right-click** a card or press **Ctrl+F**. Change this in
Settings → Wallpapers → *Show*: favourites, all Wallpaper Engine wallpapers, or
Wallpaper Engine plus your pictures folder. Without Wallpaper Engine, the pictures folder is used. Picking one makes **Wallpaper Engine play it**
(through its official `-control openWallpaper` command), so animation, effects and audio
response all keep working. The selected card plays the wallpaper's animated preview.

Picking a normal picture while Wallpaper Engine is running takes Wallpaper Engine's wallpaper
down (`-control closeWallpaper`) so the picture actually shows.

WispR's frosted background and wallpaper colours follow what's on screen: while
Wallpaper Engine plays a wallpaper, they use that wallpaper's preview picture. The bars and
launcher sit above the desktop, so Wallpaper Engine keeps animating underneath them; it also
pauses as usual when an app is fullscreen.

## How the search works

Results are ranked in tiers, best first:

- exact name → start of name → start of any word (`code` → Visual Studio Code)
- initials (`vsc` → Visual Studio Code, `ps` → Windows PowerShell)
- several words in any order (`task man`)
- anywhere in the name, then fuzzy in-order letters (`chrm`)
- typo-tolerant (`spotfy`, `fierfox`)

On top of that it **learns**: apps you launch often or recently rise, and if you type
`c` and pick Chrome a few times, `c` will start offering Chrome first.
Uninstallers, readmes and help links are pushed down.

When nothing fits, the last rows let you **Run** what you typed (a command like
`cmd`, `regedit`, `ping 1.1.1.1`, a path or a URL) or **search the web** for it.

With an empty box you see your most-used apps.

Apps are found from the same list the Start menu uses (desktop programs and
Microsoft Store apps, with their real icons), refreshed in the background.

Usage history lives in `%APPDATA%\WispR\usage.txt` — delete it to reset learning.

## Known limitations

- With monitors at different scaling (e.g. 150 % and 100 %), bars on the second monitor may
  look slightly soft.
- Windows' own flyouts (quick settings, notifications, calendar) open in their usual place.
- Minimized windows show their app icon in the preview (Windows has no live picture of them).

When a window running **as administrator** is focused (e.g. an elevated terminal or
Task Manager), Windows doesn't let a normal app see the keypress, so the real
Start menu opens instead. To fix that, run WispR itself as administrator
(e.g. via Task Scheduler "Run with highest privileges" at logon).

## Build from source

Requires the .NET SDK (8 or newer) on Windows:

```
dotnet build -c Release
```

The exe lands in `bin\Release\net48\`.

| File | What it does |
|---|---|
| `KeyboardHook.cs` | Detects a lone Windows-key tap and stops the real Start menu opening |
| `AppIndex.cs` | Finds installed apps and their icons |
| `Matcher.cs` | The ranking / fuzzy matching |
| `Usage.cs` | Learns from what you launch |
| `LauncherForm.cs` | The popup window, theming and right-click menu |
| `Launcher.cs` | Launching apps, finding install folders |
| `ShellMenu.cs` | Windows' own context menu (taskbar pinning, "More options") |
| `Settings.cs` / `SettingsForm.cs` | Themes, settings file and the settings window |
| `Palette.cs` | Colours from an image; reading the desktop wallpaper |
| `Backdrop.cs` | Background image / frosted-wallpaper rendering shared by all windows |
| `BarsController.cs` | Runs the bars: placement, hiding, fullscreen detection, screen space |
| `BarForm.cs`, `TaskbarForm.cs`, `SystemBoxForm.cs` | The taskbar and system-box windows |
| `BarMenu.cs` | The themed right-click menus on the bars |
| `PreviewForm.cs` | Live window previews when hovering an app |
| `PerfBoxForm.cs` | The CPU / RAM box with the Task Manager button |
| `MediaBoxForm.cs`, `MediaService.cs` | The media player box and reading "now playing" from Windows |
| `VolumePopup.cs` | The volume slider popup |
| `Calculator.cs` | Maths and unit conversion in the launcher |
| `SettingsCatalog.cs` | Windows settings pages the launcher can open |
| `CalendarPopup.cs` | The clock / calendar popup |
| `WallpaperPicker.cs` | The wallpaper carousel in the launcher |
| `WallpaperEngine.cs` | Finding, reading and switching Wallpaper Engine wallpapers |
| `SettingsUi.cs` | Themed controls for the settings window (switches, sliders, cards) |
| `Anim.cs` | The small animation engine shared by all windows |
| `GpuSensor.cs` | AMD GPU temperature and load (via the Radeon driver's library) |
| `StartBoxForm.cs` | The Start + power box in the bottom-left corner |
| `TrayMenus.cs` | Shows tray icons' right-click menus in WispR's style |
| `GrowOut.cs` | The "grows out of the bar" look for popups |
| `Vinyl.cs` | The spinning record in the Media tab |
| `TopPanelForm.cs` | The drop-down from the top edge (media + performance) |
| `FrameForm.cs` | The border around the whole screen |
| `WinEvents.cs` | Window events, so the bars react instantly to maximize/fullscreen |
| `LauncherShell.cs` | The smooth outline of the launcher when it's attached to the screen's bottom edge |
| `StripForm.cs` | The full-width band behind the bars while a window is maximized |
| `StartMenuForm.cs` | The Start menu (pinned, recommended, all apps, power) |
| `WindowTracker.cs` | Which windows are open (same rules as Alt+Tab) |
| `TrayService.cs` | Receives app tray icons (and passes them on to Windows) |
| `SystemStatus.cs` | Volume, network, battery, keyboard layout |
| `ExplorerTaskbar.cs` | Hiding / restoring the Windows taskbar, reserving screen space |
| `Native.cs` | Shared Windows API declarations |
| `Log.cs` | The diagnostic log |
| `Program.cs` | Tray icon, menu, autostart |
