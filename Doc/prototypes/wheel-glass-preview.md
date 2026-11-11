# Floating glass wheel preview

Interactive design prototype for the wheel buttons and their opening, selection and
release animations. The installed WinUI application is not changed by this prototype.

Open `wheel-glass-preview.html` in the conversation visualization renderer. The file is
an HTML fragment; its host supplies Lucide icons. It has no network calls or system actions.

## Interaction

- Initially open. Hover or focus a button to preview its selected label and shortcut.
- Hold the primary pointer in the empty stage, move in a direction, then release to
  simulate a choice. The origin stays inside the stage so every button remains visible.
- Direct button clicks and native Tab / Enter work. Arrow keys select the four cardinal
  directions. Escape cancels a held gesture or the focused preview.
- Replay reopens at the centre. Opening duration ranges from 80 to 280 ms (default 120).
  Slow motion multiplies opening, hover and closing timings by four. Reduced motion
  disables motion, including in slow mode.
- Screenshot identifies the existing group action, but does not open its submenu.
  Centre release shows a local pulse; it is not a desktop depth-refresh simulation.

## Design and timing

The native wheel currently uses eight filled wedge sectors. This proposal replaces
them with independent dark glass rounded buttons, retaining the eight directions,
52 DIP outer-button size, 104 DIP centre and 116 DIP orbit. A 58 DIP centre dead zone
is used for the pointer selection. The orbit contracts only for narrow browser previews.

Opening uses a small outward movement and fade. The centre appears first; outer
buttons finish within the selected duration with a subtle clockwise stagger. Selection
scales the closest button up to 1.08, moves it outward by up to 5 px and blends light
toward its neighbour as pointer angle changes. Other buttons dim. Selection transforms
live on a separate element from the opening and closing transforms.

The chosen button compresses and fades over 150 ms while the others fade inward over
110 ms. Simulated action status updates immediately on release. Replay and cancellation
invalidate older completions and cancel outstanding animation objects. No animation loops.

The browser blur is a visual proposal, not a native performance benchmark. Actual
desktop appearance, compositing and interaction require user acceptance and a separate
WinUI implementation.

## Preservation and verification — 2026-11-12

Before creating the prototype, native source commit `c10c157` was preserved as
`archive/wheel-before-glass-demo-20261112`. The ignored backup of that name under
`bin/Backups/` contains a verified source ZIP (199 Git blobs), runtime, model and actual
configuration files (18 SHA-verified files).

Validation: fragment syntax/static DOM checks; local Chromium inspection at 736 px
and 320 px, including the sandboxed conversation wrapper; eight direction sectors;
direct click and close; hold/move/release; rapid replay; keyboard selection and focus;
Escape during opening and while holding; pointer cancellation; reduced motion.
Browser assets were supplied locally during checks, with external requests blocked.
Repository `dotnet build --no-restore` passed with zero warnings and errors.

No native source, installed executable, global hooks, configuration or model was
changed, and no GitHub push or desktop refresh playback was performed for this demo.
