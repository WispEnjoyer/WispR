<div align="center">

<img src="docs/logo.png" width="128" alt="WispR logo">

# WispR

**A light, smooth Start menu and taskbar for Windows.**

<a href="../../releases/latest"><img src="https://img.shields.io/badge/download-latest-8b7cf6?style=flat-square" alt="Download"></a>
<img src="https://img.shields.io/badge/Windows-10%20%7C%2011-5b8def?style=flat-square" alt="Windows 10 | 11">
<img src="https://img.shields.io/badge/.NET%20Framework-4.8-7c6fd6?style=flat-square" alt=".NET Framework 4.8">
<img src="https://img.shields.io/badge/size-~500%20KB-3fb6c8?style=flat-square" alt="~500 KB">

</div>

<br>

Tap the **Windows key** and a launcher rises out of the bar. Type a few letters, press **Enter**.
Everything around it — taskbar, tray, Start menu, popups — is drawn in one consistent style,
animated at your monitor's refresh rate.

## ✨ Features

- **Launcher** — fuzzy app search that learns what you use, plus a calculator, unit conversion and Windows settings search
- **Taskbar & Start menu** — pinned and running apps with live previews; popups that grow out of the bar
- **Screen frame** — an optional rounded border around the whole desktop that the bars sit in
- **Top drop-down** — touch the notch at the top edge for
  - 🎵 **Media**: cover, a spinning vinyl coloured from the cover, a seekable waveform
  - 📊 **Performance**: CPU, memory and GPU temperature
- **Timers** — `set timer 10m` in the launcher; a small widget on the right edge shows them and slides out when you touch it
- **Wallpapers** — a carousel of your pictures or Wallpaper Engine favourites
- **Themes** — 8 built-in themes, your own colours, or colours taken from your wallpaper

## 🚀 Getting started

1. Download **WispR.zip** from the [latest release](../../releases/latest) and unzip it somewhere permanent.
2. Run **`WispR.exe`**. *(SmartScreen may ask once: **More info → Run anyway**.)*
3. Right-click the tray icon → **Start with Windows**.

Settings: right-click the tray icon or an empty spot on the bar → **Settings…**

> [!TIP]
> If Windows' own taskbar ever stays hidden (e.g. after force-closing WispR), run
> **Restore Windows taskbar.bat** from the zip, or `WispR.exe --restore-taskbar`.

## ⌨️ Keys

| Key | Action |
|---|---|
| <kbd>Win</kbd> | Open / close the launcher |
| <kbd>Win</kbd> <kbd>Win</kbd> (double-tap) | Hide / show the bars |
| <kbd>Enter</kbd> | Launch · <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>Enter</kbd> as administrator |
| <kbd>↑</kbd> <kbd>↓</kbd> / <kbd>Tab</kbd> | Move the selection |
| <kbd>Esc</kbd> | Clear the search, then close |
| <kbd>Shift</kbd>+<kbd>F10</kbd> | Right-click menu for the selected app |
| <kbd>Ctrl</kbd>+<kbd>Esc</kbd> | Still opens the real Windows Start menu |

All other Windows shortcuts (<kbd>Win</kbd>+<kbd>E</kbd>, <kbd>Win</kbd>+<kbd>L</kbd>, <kbd>Win</kbd>+<kbd>Shift</kbd>+<kbd>S</kbd>…) keep working.

**Try typing:** `15*9` · `19% of 250` · `5 km in miles` · `bluetooth` · `dark mode` · `wallpaper` · `set timer "Brot backen" 10m`

## 📖 Everything in detail

<details>
<summary><b>Taskbar, system box & Start menu</b></summary>

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

</details>

<details>
<summary><b>Settings</b></summary>

Right-click the WispR icon in the tray (or an empty spot on the bars) → **Settings…**.
Everything applies instantly and is saved to `%APPDATA%\WispR\settings.ini`.

The window has a sidebar with six sections, and it takes on your current theme (it recolours
live when you pick another one):

- **Appearance** — pick one of 8 themes from preview tiles (Midnight, Light, Nord, Dracula,
  Catppuccin, Gruvbox, Rosé Pine, Solarized Light), or click any colour to make your own
  palette; opacity and launcher width; **corner roundness** (one value for the launcher, popups,
  drop-down, timers and screen frame) and **animations** (off, fast, normal or relaxed).
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
- **Drop-down** — the panel that slides down when the mouse rests at the top edge of the screen
  (marked by a notch). **Media**: cover, title, artist, a waveform you can click or drag to seek,
  and previous / play-pause / next; a vinyl record coloured from the cover spins while the song
  plays. **Performance**: CPU, memory and GPU temperature as gauges with a 40-second graph each
  (GPU readings come from AMD's driver library; other GPUs show "unavailable"). Settings: on/off,
  the notch, how long the mouse rests before it opens, which tab it opens on, the record's speed,
  and its look (a different pressing per song, or always the same one). It doesn't open while you
  drag a window to the top, or over fullscreen games and videos.
- **On the bars** — which boxes and items show (Start, Task view, tray,
  keyboard layout, network, speed, volume, battery, notifications, clock, date, seconds,
  show-desktop corner).
- **General** — Windows-key takeover and double-tap speed, launcher position, web search
  engine, how many results the launcher shows, the timer sound, the Start menu name and account
  picture, start with Windows.
- **Preview launcher** (bottom left) opens the launcher so you can see your changes.

</details>

<details>
<summary><b>Right-click menu</b></summary>

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

</details>

<details>
<summary><b>Calculator, units & Windows settings</b></summary>

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

</details>

<details>
<summary><b>Wallpaper picker & Wallpaper Engine</b></summary>

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

</details>

<details>
<summary><b>How the search works</b></summary>

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

</details>

<details>
<summary><b>Known limitations</b></summary>

- With monitors at different scaling (e.g. 150 % and 100 %), bars on the second monitor may
  look slightly soft.
- Windows' own flyouts (quick settings, notifications, calendar) open in their usual place.
- Minimized windows show their app icon in the preview (Windows has no live picture of them).

When a window running **as administrator** is focused (e.g. an elevated terminal or
Task Manager), Windows doesn't let a normal app see the keypress, so the real
Start menu opens instead. To fix that, run WispR itself as administrator
(e.g. via Task Scheduler "Run with highest privileges" at logon).

</details>

## 🛠️ Building

Requires the .NET SDK (8 or newer) on Windows:

```
dotnet build -c Release
```

The exe lands in `bin\Release\net48\`.

<details>
<summary><b>What each file does</b></summary>

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
| `MediaService.cs` | Reading "now playing" from Windows |
| `ImageLoad.cs` | Safe, size-checked loading of pictures from outside (scaled to what's needed) |
| `WinRtEventSink.cs` | Listens to Windows' media change events (keeps the song length and position up to date) |
| `Timers.cs`, `TimerWidget.cs` | Timers from the launcher and their widget on the right edge |
| `Playhead.cs` | WispR's own clock for the song position, so junk reports from apps can't reset it |
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
| `Notch.cs` | The little tab at the top edge that marks the drop-down |
| `MediaApps.cs` | Readable names for media apps (e.g. Floorp instead of a code) |
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

</details>

