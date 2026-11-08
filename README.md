<div align="center">

<img src="Assets/AppIcon.png" alt="Action Wheel icon" width="120" height="120">

# Action Wheel

**A radial "pie" menu for Windows — a left+right chord puts any shortcut or app right under your cursor.**

A WinUI 3 (.NET 10) desktop app that pops up a radial menu at the cursor when you hold the left mouse button and press the right mouse button, anywhere in Windows.

[![License: MIT](https://img.shields.io/github/license/cmm-cmm/Action-Wheel?color=blue)](LICENSE.txt)
[![Latest tag](https://img.shields.io/github/v/tag/cmm-cmm/Action-Wheel?label=version&color=success)](https://github.com/cmm-cmm/Action-Wheel/tags)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%2F%2011-0078D6?logo=windows&logoColor=white)](#requirements)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](#requirements)
[![Windows App SDK](https://img.shields.io/badge/Windows%20App%20SDK-2.3.1-0078D6?logo=windowsxp&logoColor=white)](#requirements)
[![PRs Welcome](https://img.shields.io/badge/PRs-welcome-brightgreen.svg)](CONTRIBUTING.md)

</div>

---

## Table of contents

- [Features](#features)
- [Requirements](#requirements)
- [Build & run](#build--run)
- [Using it](#using-it)
- [Configuring the buttons](#configuring-the-buttons)
- [How it works](#how-it-works)
- [Troubleshooting](#troubleshooting)
- [Contributing](#contributing)
- [License](#license)

## Features

### 🖱️ Interaction

- **Global trigger** — a low-level `WH_MOUSE_LL` hook watches for the trigger system-wide. The click is *swallowed*, so the app underneath doesn't also get it (no stray browser tabs or autoscroll).
- **Configurable trigger** — hold left and press right by default. Settings can switch to the reverse chord, middle button, or side buttons 4/5. Hold `Ctrl+Alt+Space` for the separate Quick Info wheel; hover a Markdown topic, move onto a child, and release Space to copy it. Images and other resources still open normally.
- **Opens at the cursor** — the menu centres itself on the pointer and is clamped to the current monitor's work area.
- **Pick by gesture** — keep the first mouse button held, move towards a sector, and release the second button to execute. Releasing inside the centre plays a brief screen ripple refresh; releasing the first button first cancels the gesture.
- **Hold for a second action** — press and hold a button for about half a second instead of clicking it, and it runs a separate, optional action. Configured per button, right below its main one.
- **Dismiss any way you like** — click a button, click anywhere off the buttons, click elsewhere on screen, or press <kbd>Esc</kbd>.

### 🎨 Customization

- **Configurable buttons** — 8 outer buttons plus a centre button. Each can send a keyboard shortcut, launch a program/file/URL, run a built-in Windows function (lock, sleep, snap, show desktop, switch profile, ...), or reveal a group. Edit them in the built-in settings window, or by hand in `actions.json`.
- **Group buttons** — turn any outer button into a small satellite ring of up to 5 more buttons. They fan out on hover from that button's own position, sized and coloured to match it, so a group reads as one control rather than a separate menu.
- **Ring size, distance and animation are all adjustable** — button size and orbit radius have a live preview, and there are 7 opening animations to choose from (fade and scale, pop, radiate from centre, converge from outside, clockwise/counter-clockwise sweep, or none), each with its own effect duration.
- **Truly transparent** — per-pixel alpha, so only the buttons are drawn; the desktop shows through everywhere else.
- **Buttons carry a drop shadow** — they stay readable over a white window underneath, which matters because the overlay is genuinely transparent.
- **Picks up edits to `actions.json` on its own** — the file is watched in both locations it can live in.

### 📁 Profiles & backup

- **Named profiles** — save the current 9 buttons under a name, and load any saved profile back onto the ring later. A button can itself switch or open a profile via the built-in "switch profile" Windows function, and the tray icon's menu lists recent profiles for one-click switching.
- **Import and export** — a profile is a self-contained XML file, so it can be shared or version-controlled independently of `actions.json`.
- **One-file backup and restore** — every profile, the trigger, the ring appearance and extracted icons, saved to or restored from a single file.

### 🔒 Reliability & privacy

- **One instance only** — starting the app again asks the copy already running to open its settings instead of installing a second global hook.
- **Runs in the notification area** — closing the main window hides it; the app keeps listening in the background.
- **Says when it isn't working** — the main window reports whether the hooks installed and whether `actions.json` was usable.
- **Private by design** — no usage events, clicks, gestures, launched actions, profile changes or errors are written to a log file.
- **Starts with Windows** — optional, from the main window's toggle or the tray menu. Uses the per-user Run key, so it needs no elevation and shows up in Windows' own Startup Apps settings.
- **Stays out of the way** — `WS_EX_TOOLWINDOW` + `WS_EX_NOACTIVATE` keep the overlay out of Alt+Tab and the taskbar, and it never steals focus, so shortcuts land in the app you were actually using.

## Requirements

- Windows 10 version 1809 (build 17763) or later
- .NET 10.0
- Windows App SDK 2.3.1
- Visual Studio 2022 with the "Windows application development" workload (for building)

## Build & run

`Build.ps1` is the one entry point — the same commands developers and CI run.

```powershell
.\Build.ps1                       # Debug build (the default)
.\Build.ps1 -Task Release
.\Build.ps1 -Task Publish         # self-contained single-file exe in bin\Publish\win-x64
.\Build.ps1 -Task Package         # Publish, ZIP it, then launch the ZIP's exe as a smoke test
.\Build.ps1 -Task Installer       # Publish, then the Inno Setup installer
.\Build.ps1 -Task Clean
.\Build.ps1 -Task Publish -Platform ARM64
```

Nothing in it prompts, so it works unattended. It refuses to build while a copy of Action Wheel is still running, because otherwise the exe is locked and MSBuild fails several hundred lines later with a copy error that never mentions the app being open.

Plain `dotnet build` also works: the project defaults `Platform` to `x64` and derives the matching runtime identifier, so no `-r win-x64` is needed. Pass `-p:Platform=ARM64` or `-r win-x86` to override.

In Visual Studio, press F5 — there is one launch profile, **"Action Wheel (Unpackaged)"**.

Self-contained publishes are large (roughly 250–300 MB). That is normal for a self-contained WinUI 3 / .NET 10 app, not a bug. `-Task Package` exists because a single-file publish that *builds* is not proof it *runs* - Windows App SDK below 2.3.1 produced an exe that crashed on launch (`0xc000027b` in `Microsoft.UI.Xaml.dll`) despite building cleanly, so this task extracts the exact ZIP a user would download and launches it for real before calling the build a success.

## Using it

1. Run the app. The main window reports whether the global hooks are actually installed, and whether `actions.json` was usable — the menu itself works whether or not the window is open.
2. Hold the **left mouse button**, then press the **right mouse button** anywhere.
3. Move to a sector and release the right button to run its action. Release in the centre for the refresh effect; release left first, or press <kbd>Esc</kbd>, to cancel.
4. For Quick Info, hold <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>Space</kbd>, hover a topic, move onto an information button, and release <kbd>Space</kbd> to copy. See [Quick Info](Doc/quick-info.md) for the Markdown catalog.
4. Right-click the tray icon to open the main window, open the settings window, open `actions.json` in an editor, reload the config, turn **Start with Windows** on or off, or quit.

## Configuring the buttons

### Settings window

**Open button settings** — from the main window, or **Settings…** in the tray icon's right-click menu. It is organised the way Windows' own Settings app is: a left-hand rail with three sections.

**Buttons** (the default section) is master-detail: a compact, one-line-per-button list on the left — drag a row to move that button around the ring, the first row is the centre button and the rest go clockwise from the top — and a detail panel on the right for whichever button is selected (click a row, or click a button in the live preview above the panel), with:

- **Label**, **action type** and **value**. Action type is one of **Do nothing**, **Send shortcut**, **Open app / file**, **Windows function** (a built-in: lock, sleep, restart, shut down, show desktop, snap, open Task Manager, reload the config, switch or open a profile, and more), or **Group**.
- a **record button** (keyboard icon) that captures a key press instead of making you type the shortcut — Ctrl, Alt and Shift combinations are captured; the Windows key never reaches the app because the shell takes it first, so `Win+…` has to be typed by hand. <kbd>Esc</kbd> cancels recording.
- a **file browser** for launch targets, and an **arguments** box for the parameters to pass them
- an **icon**. The preview button opens a picker offering all three sources: any glyph in the bundled icon font (searchable by name or code, and read from the font itself so the list matches what the app can draw), your own **`.svg`, `.png` or `.ico` file**, and **the icon of the program the button launches**. Browsing for a launch target takes that program's icon automatically.
- **icon colour**, **button colour**, **shadow colour** and shadow opacity/blur/offset, and an icon **scale** from 0.25× to 3× with its own horizontal/vertical offset
- a **tint** switch for file icons: on, an `.svg` is recoloured to the icon colour; off, it keeps the colours it was drawn with. A `.png` or `.ico` always keeps its own colours.
- a **hold action** — a second, optional action for the same button, with its own type, value and arguments
- an **Edit group…** button when the type is Group, opening a dialog to pick up to 5 child buttons, each with its own icon, name and type (everything except Group itself)
- **reset** and **test** buttons — test runs the action immediately, without saving or opening the ring
- a **live status marker** next to the value: green when a launch target was found (on disk, on PATH, or a well-formed URL), amber when it was not or when another button uses the same shortcut, red when the shortcut contains a key that cannot be sent

**Appearance & trigger** holds the mouse trigger (which gesture opens the ring, and the chord timing/movement limits) and the ring's own size, orbit distance and opening animation, each with a live preview.

**Profiles & backup** holds saving/loading/renaming/deleting named profiles, the active profile indicator, the "Recognised key names" reference, and the one-file backup/restore.

A **live preview** in the Buttons section shows the ring as your unsaved edits will leave it, and **undo/redo** in the bottom bar step through those edits (typing is coalesced, so undo does not go one keystroke at a time). The bottom bar and its Save button are reachable from every section.

**Save** writes `actions.json` and tells the running service to reload, so the next menu that opens uses the new configuration; a menu that is already on screen keeps the one it was opened with.

Saving is refused — with the reason shown in the bar at the bottom — if a shortcut contains an unrecognised key name, an action has no value, or an icon code is not a usable code point. Those are exactly the mistakes that would otherwise produce a button that silently does nothing.

Duplicate shortcuts and launch targets that were not found are reported *after* saving rather than instead of it: both are legal. A program may be installed only on another machine, and two buttons may deliberately send the same keys.

### The file

Buttons are read from `actions.json`, searched in this order:

1. next to the executable (portable installs)
2. `%LOCALAPPDATA%\ActionWheel\actions.json`

If neither exists, the defaults are written to the `%LOCALAPPDATA%` copy on first run — the install directory is often read-only. The settings window saves to whichever of the two is actually in effect, so a portable copy is never silently shadowed.

Both locations are watched, so an edit made in a text editor takes effect on its own — the tray menu's **Reload** is still there but no longer necessary.

Saving never writes the file in place: the JSON goes to a temp file, that file is flushed to disk, the previous contents are copied to `actions.json.bak`, and only then does the temp file replace the original. An interrupted save therefore leaves either the old file or the new one, never half of one.

A file that cannot be read is reported directly in the main window. The built-in defaults take over so the menu keeps working, **your file is left exactly as it is**, and the main window says which file failed and why — with a **Restore the backup** button when a `.bak` is available. (Restoring keeps the broken file as `actions.json.invalid`; it is still the only copy of whatever you were in the middle of writing.)

```json
[
  { "tag": 0, "label": "Close menu", "type": "none", "value": "", "glyph": "F00D" },
  { "tag": 1, "label": "Copy", "type": "keys", "value": "Ctrl+C", "glyph": "F0C5" },
  {
    "tag": 2, "label": "Open notes", "type": "launch",
    "value": "notepad.exe", "arguments": "\"C:\\Notes\\today.txt\"",
    "iconPath": "C:\\icons\\notes.svg", "foreground": "#FFFFFFFF", "background": "#FF1B6FD4",
    "holdType": "function", "holdValue": "action-wheel.profile:Work"
  },
  { "tag": 3, "label": "Lock", "type": "function", "value": "windows.lock", "glyph": "F023" },
  {
    "tag": 4, "label": "More", "type": "group", "glyph": "F0EA",
    "group": [
      { "tag": 0, "label": "Task View", "type": "function", "value": "windows.task-view", "glyph": "F009" },
      { "tag": 1, "label": "File Explorer", "type": "function", "value": "windows.file-explorer", "glyph": "F114" }
    ]
  }
]
```

| Field | Meaning |
|---|---|
| `tag` | `0` = centre button, `1`–`8` = outer buttons clockwise from the top |
| `type` | `keys` (send a shortcut), `launch` (run a program / open a path or URL), `function` (a built-in Windows function), `group` (reveal a satellite ring instead of dispatching anything), `none` (just close) |
| `value` | the shortcut, the target to launch, or the built-in function's id; unused (and may be omitted) for `none` and `group` |
| `arguments` | command-line arguments for a `launch` target; quote paths that contain spaces |
| `label` | tooltip text |
| `glyph` | Icon code point in hex, from the bundled [CaskaydiaCove Nerd Font](https://www.nerdfonts.com/). Five digits are allowed — two thirds of the font's icons sit above U+FFFF |
| `iconPath` | path to an `.svg`, `.png` or `.ico` file to draw instead of the glyph |
| `iconTint` | `true` (default) recolours an `.svg` to `foreground`; `false` keeps the colours in the file |
| `iconScale`, `iconOffsetX`, `iconOffsetY` | icon size (0.25–3×, default 1) and its offset in DIPs from the button's centre (default 0) |
| `foreground` | icon colour, `#RRGGBB` or `#AARRGGBB` |
| `background` | button colour, same format |
| `shadow`, `shadowEnabled`, `shadowOpacity`, `shadowBlur`, `shadowOffsetX`, `shadowOffsetY` | the button's own drop shadow — colour, on/off, and the same shadow parameters any design tool exposes |
| `holdType`, `holdValue`, `holdArguments` | a second action for the same button, run on press-and-hold instead of click; same vocabulary as `type`/`value`/`arguments`, and a button needs none of them to skip having a hold action |
| `group` | for `type: "group"` only — an array of up to 5 child actions (same shape as a top-level action, minus `tag`'s ring meaning: a child's `tag` is just its position among its siblings). A child cannot itself be a group. |

Every field after `value` is optional; leaving one out uses the built-in default — a config written before `iconTint` existed therefore gets tinting, which is what a single-colour pictogram wants. Only a vector icon can be recoloured: a `.png` or `.ico` is drawn as-is whatever `iconTint` says, and `background` applies either way.

The centre button (`tag: 0`) cannot be `"type": "group"` — there is nowhere for a satellite ring to fan out from a button that has no direction of its own.

Icons copied from an application are written to `%LOCALAPPDATA%\ActionWheel\Icons` as PNGs, so a button keeps its picture after that application is updated, moved or uninstalled. On a later startup, generated PNGs no longer referenced by `actions.json` or any readable saved profile are removed automatically; user-selected files elsewhere are never touched.

**The icon font ships with the app** (`Assets/Fonts/`), it is not the system's. CaskaydiaCove Nerd Font Propo carries about ten thousand icons against Segoe Fluent Icons' two thousand, including the application and language logos a launcher wants. The two fonts overlap in `E700`–`E8EF` and disagree about every one of the 887 shared code points — `E711` is a close button in Segoe and the Apple logo here — so a `glyph` value only means anything against the font it was chosen from, and that font therefore travels with the app rather than being looked up on the machine. Licences and attribution are in `Assets/Fonts/Licenses/`. Every `FontIcon` in the application explicitly uses this bundled family.

Use the **Propo** cut, not the plain one. In the plain cut an icon keeps the monospace advance of 0.586 em while its ink runs to a full em, overflowing the cell through a negative right side bearing: measured, the ink's centre lands at 0.756 of the advance rather than 0.5, and every icon sits right of where it belongs. Propo widens the advance to match the ink. The Mono cut also centres, but by shrinking every icon into the 0.586 cell.

**There is no compatibility path from the old icon font.** A `glyph` the shipped font has no icon for draws nothing — no substitute, and no notdef box, which would read as a rendering fault rather than a setting to change. The settings window reports those rows as a warning after a save instead of blocking it, since a button with no picture still works.

Recognised key names: `Ctrl`, `Alt`, `Shift`, `Win`, `A`–`Z`, `0`–`9`, `F1`–`F24`, `Esc`, `Tab`, `Enter`, `Space`, `Backspace`, `Delete`, `Insert`, `Home`, `End`, `PageUp`, `PageDown`, arrow keys, `PrintScreen`, and the media keys (`PlayPause`, `MediaNext`, `MediaPrev`, `MediaStop`, `VolumeUp`, `VolumeDown`, `Mute`).

### Profiles

A **profile** is a named, saved copy of all nine buttons, stored as its own file at `%LOCALAPPDATA%\ActionWheel\Profiles\<name>.xml` — `actions.json` remains the one file the running ring actually reads; **Load** copies a profile's contents into it. Manage profiles from the **Profiles & backup** section: save the current buttons under a name, load, rename or delete a saved one, or **Import XML** / **Export XML** a profile file directly from the bottom command bar (independent of the active configuration, so a profile can be shared or checked into version control on its own).

A button's **Windows function** type can switch or open a profile by name (`action-wheel.profile:<name>`), and the tray icon's right-click menu lists recently-used profiles for switching without opening Settings at all.

### Backup and restore

**Backup all…**, in the Profiles & backup section, saves everything into one `.zip`: `actions.json`, `preferences.json` (the trigger and ring appearance), every saved profile, and the icons extracted from applications. **Restore backup…** shows what a backup file contains — when it was made, which app version, how many profiles and icons — before touching anything, then replaces the matching files on this machine. The running ring and an open Settings window both pick up a restore immediately.

## How it works

The code is in two assemblies. **`ActionWheel.Core`** is the business logic and references nothing but the base class library — no WinUI, no `user32`. **`Action Wheel`** is the app: windows, view models, hooks, and the Win32 work the overlay needs. The split is a compiler-enforced version of the layering, so logic cannot quietly grow a dependency on a XAML type.

**`ActionWheel.Core`** — plain `net10.0`:

| File | Role |
|---|---|
| `ActionItem.cs` | The immutable action model — including a button's hold action and, for a Group, its child actions |
| `ActionConfig.cs` | Reads, validates and writes `actions.json`; the atomic save, the backup, and `ConfigLoadResult` |
| `ActionValueCodec.cs` | `ActionKind` ↔ the settings UI's index and ↔ the `type`/`holdType` string; glyph hex ↔ character |
| `ActionsValidator.cs` | Validates a set of actions, recursively into a Group's children |
| `ConfigWatcher.cs` | `FileSystemWatcher` over both config locations, debounced |
| `ConfigBackup.cs` | The one-file backup: zips `actions.json`, `preferences.json`, every profile and extracted icon; previews and restores one |
| `ProfileXml.cs` | Serialises the nine actions to/from a named profile's `<ActionWheelProfile>` XML file |
| `WindowsFunctions.cs` | The catalog of built-in Windows functions (lock, sleep, snap, switch profile, ...), each with an id, name, description and glyph |
| `RingAppearance.cs` | Button size, orbit radius, opening animation and its duration — the user's own ring preference, normalised to a legal range |
| `ShortcutKeys.cs` | Turns "Ctrl+Shift+S" into virtual-key codes; reports the key it did not recognise |
| `RingGeometry.cs` | Menu size for a DPI, position clamped to a monitor's work area, and the gesture direction → tag |
| `ColorValue.cs` / `IconFile.cs` | Colour parsing and lighten/darken; which icon files are usable |
| `SvgTint.cs` | Rewrites an SVG's fills and strokes to one colour — the only way to recolour one, since the renderer draws it exactly as authored |
| `FontGlyphs.cs` | Reads a font file's character map, because GDI only sees installed fonts and cannot report anything above U+FFFF |
| `StartupCommand.cs` | The command line stored under the Run key |
| `AtomicFile.cs` | Generic atomic text-file writer (temp file → flush → `.bak` → rename) behind `preferences.json` and the active-profile marker |
| `UndoHistory.cs` | The settings window's undo/redo stack |
| `AppDataPaths.cs` | The shared `%LOCALAPPDATA%\ActionWheel` configuration location; it stores settings, not activity history |
| `DiagnosticsReport.cs` | The text behind "Copy diagnostics" |

**`Action Wheel`** — the WinUI 3 app:

| File | Role |
|---|---|
| `Services/MouseHook.cs` | `WH_MOUSE_LL` hook: raises the configured trigger's events (including chord detection for the two-button triggers), swallows it, and reports other clicks so the menu can be dismissed |
| `Services/KeyboardHook.cs` | `WH_KEYBOARD_LL` hook, only for <kbd>Esc</kbd> — the overlay never has keyboard focus |
| `Services/LauncherService.cs` | Owns the hooks, the watcher and the menu's lifetime; guarantees one menu at a time; reports hook status; runs the drag gesture and the hold countdown |
| `Services/ActionDispatcher.cs` | Synthesises shortcuts via `SendInput`, or starts a process; raises `ActionFailed` when either does not work |
| `Services/TrayIcon.cs` | `Shell_NotifyIcon` tray icon with a right-click menu (including recent profiles), on a message-only window |
| `Services/AppIcon.cs` | Reads `icon.ico` back out of the exe's own resources for the window title bars and the tray |
| `Services/AppIconExtractor.cs` | Copies the icon Windows shows for a program into a 256px PNG, via the system image list |
| `Services/IconFactory.cs` | Builds the actual icon element (glyph or SVG/raster image) for an action, shared by the ring and the settings preview |
| `Services/IconFont.cs` | The bundled icon font: the family string for a `FontIcon`, and which glyphs it has |
| `Services/StartupManager.cs` | The per-user Run key entry behind "Start with Windows" |
| `Services/ShellCommands.cs` | Opening a folder in Explorer and putting text on the clipboard |
| `Services/ProfileLibrary.cs` | Save/load/rename/delete a named profile; lists profiles by recency for the tray menu |
| `Services/ActiveProfileSettings.cs` | Which profile's name is currently shown as active |
| `Services/TriggerSettings.cs` | Loads/saves the trigger kind, chord timing and `RingAppearance` together in `preferences.json` |
| `Services/WindowSizing.cs` | Sizes and centres a window on the display it opened on, in physical pixels |
| `ViewModels/MainViewModel.cs` | Everything the main window shows and does |
| `ViewModels/SettingsViewModel*.cs` | The settings window's rows, undo/redo, validation and saving (`.Appearance.cs` and `.Profiles.cs` are partials of the same class) |
| `Overlay/ButtonShadow.cs` | The composition drop shadow behind each ring button, including a group's satellites |
| `Overlay/RingOpenAnimation.cs` | The seven opening-animation presets, driving only `Opacity` and a `RenderTransform` |
| `Overlay/RadialMenu.xaml(.cs)` | The overlay window: transparency, positioning, layout, hit-testing, and a Group button's satellite ring |
| `Settings/SettingsWindow.xaml(.cs)` | The settings window: the `NavigationView` sections, drag-reorder retagging, dialogs, the file picker |
| `Settings/ActionEditModel.cs` | Mutable, observable counterpart of the immutable `ActionItem`, used only while that window (or a group's child editor) is open |
| `Settings/RingPreview.cs` | Draws the live ring preview and replays its opening animation |
| `Settings/GlyphChoice.cs` | The icon picker's entries: a named, curated set plus every glyph the font reports |
| `Settings/ShortcutRecorder.cs` | Turns a live keypress + modifier state into a `"Ctrl+Shift+S"`-style string |
| `MainWindow.xaml(.cs)` | Status window; closing it hides to the tray |

A few implementation details are easy to break and worth knowing about:

- **Transparency** needs *both* a custom `SystemBackdrop` holding a transparent composition brush *and* `DwmEnableBlurBehindWindow`. Drop either and the menu renders as a solid black square. The classic `WS_EX_LAYERED` colour-key trick does not work with WinUI 3 content.
- **Removing the window outline** is done entirely by `RemoveWindowFrame`, which strips `WS_DLGFRAME` and friends directly. It only works if the presenter was attached to the `AppWindow` first, otherwise the style changes are silently reverted.
- **`InitializeWindow` deliberately configures nothing on the presenter, and does not touch `AppWindow.TitleBar`.** Both blocks used to be there and between them cost 46 ms of the 52 ms it took to build the overlay — on every single click — while achieving nothing measurable: the TitleBar properties style a title bar this window does not have, and every presenter flag is superseded a few lines later by `RemoveWindowFrame` and `SetWindowPos(HWND_TOPMOST)`. Removing them left `GWL_EXSTYLE` byte-identical and `GWL_STYLE` different only in two bits that cannot draw without `WS_CAPTION`. Don't put them back.
- **The opening animation's timings are tuned.** The 0.012 s stagger and 0.09 s per button put the last one on screen 186 ms after the click; they were 0.03 s / 0.15 s, which meant 400 ms and was most of how slow the menu felt. Click-to-first-pixel is only ~15 ms, so the animation, not the code, is what the user is waiting for.
- **Code reachable from a hook callback must never perform disk I/O.** The mouse hook reads the cursor position from `lParam` rather than calling `GetCursorPos`, which is both cheaper and more accurate during fast movement.
- **Button styles use full `ControlTemplate`s** with every visual state written out. Setting `Background`/`Foreground` on a `Button` only styles the initial rest state, and the default template has nothing to restore it with, so hover colours would stick permanently once the pointer left.
- **The settings window's row and detail-panel templates use classic `{Binding}`**, not `{x:Bind}`, which means they resolve properties by reflection. That is fine while `PublishTrimmed` is `False`; turning trimming on later means converting those bindings to `x:Bind` first. The Buttons section's detail panel is itself one such template, applied via a `ContentControl` bound to the row list's `SelectedItem` — `Verify-Bindings.ps1` (the script behind `Build.ps1 -Task VerifyBindings`) knows to skip `ElementName` bindings like that one, since they read a named element's own property rather than the region's `DataContext`.
- **A group's satellites are positioned by angle, not by reusing the main ring's own 8 slots.** They still fan out starting from the parent's own direction, but the angular step between neighbours is computed from the current orbit radius and button size so their edges sit a fixed distance apart regardless of how far the group itself is from the ring's centre - reusing the main ring's 45° slot spacing put satellites much further apart from each other than from the ring, since arc length between two points a fixed angle apart grows with the radius they're on.
- **Every satellite in a group is painted in its parent's own background, foreground and shadow**, not its own - only the icon is per-child. A group reads as one control fanning out from its parent, not several independently-styled buttons.

## Troubleshooting

The main window carries **Copy diagnostics**. It copies environment, configuration and hook status only when the user explicitly asks; it contains no activity history.

**Menu doesn't appear** — the main window says so directly, in red. It is often security software blocking `SetWindowsHookEx`.

Both hooks are installed together or not at all. If only one goes in, the other is rolled back deliberately: a mouse hook without a keyboard hook gives a menu that <kbd>Esc</kbd> silently will not close, and a keyboard hook without a mouse hook swallows <kbd>Esc</kbd> system-wide while nothing ever opens.

**An action does nothing** — it now says so in the main window. Unknown key names are reported with the offending key, a program that cannot be started is reported with the error, and a `SendInput` that Windows refused is reported as such (that one usually means the foreground window is running elevated and this app is not).

**The buttons went back to the defaults** — `actions.json` was rejected. The main window shows which file and why, and offers to restore the backup. Your file is never overwritten in that state.

**No tray icon** — the app falls back to exiting when the main window closes, so it can't become unquittable.

## Contributing

Bug reports, feature requests and pull requests are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md) for the build/test workflow and coding conventions, and [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) for the ground rules. Found a security issue? Please report it privately per [SECURITY.md](SECURITY.md) rather than opening a public issue.

## License

[MIT](LICENSE.txt) © 2026 Minh Pham
