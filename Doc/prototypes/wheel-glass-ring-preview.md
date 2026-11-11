# Segmented floating glass wheel

Interactive follow-up to `wheel-glass-preview.html`. The earlier proposal remains
unchanged. This version uses eight separated annular glass sectors, light icons,
a circular centre label and a small direction arc. It is a browser design preview;
the installed Windows application is unchanged.

## Interaction and appearance

- Hold the blank stage, drag and release, or click a sector. Tab and Enter work;
  arrow keys select cardinal directions and Escape cancels. Actions are simulated.
- A thin frosted surface passes background colour through. A local highlight
  blends into the neighbouring sector as pointer direction changes; it never
  illuminates the entire ring. The small outer arc follows the pointer angle.
- Opening moves the sectors outward by 13 px with a small clockwise stagger.
  Selection has a separate 1.8 px offset. Confirmation briefly compresses the
  selected sector, while the other sectors recede. No looping animation.
- The opening control starts at 280 ms with Slow x4 enabled, matching the user's
  last preview setting. This is a comparison setting, not a native timing decision.
  Opening duration can be adjusted from 80 to 320 ms. Reduced motion removes motion;
  changing that preference finishes current animations without reopening a closed wheel.
- The visible wheel is kept inside the stage, but held gestures use the actual
  press position. Pressing near an edge without dragging still chooses the centre.
- The design follows the host's light/dark appearance. Optional host design controls
  adjust blur, glass tint and the selected edge. The actual system Acrylic or
  Liquid Glass APIs are not used and native performance has not been measured.

## Sound

Four CC0 samples from [Kenney Interface Sounds](https://kenney.nl/assets/interface-sounds)
are embedded as PCM WAV data. Their originals, derivatives, source manifest and
licence are preserved in `wheel-sounds/`. Embedding keeps the preview independent
of network calls and media paths in the conversation sandbox.

Sound is off by default. Users can enable it or open Audition to hear each event.
The next deliberate gesture activates Web Audio; restoring state never autoplays.
An independent volume control starts at 45%. Event gains are 0.25 for open, 0.10
for switch, 0.22 for confirm and 0.20 for cancel, multiplied by the volume control.
Switches only sound when the selected sector changes, at most once every 55 ms.
Mute cancels pending sound starts and stops current voices. Old decode completions
cannot play a superseded event. Sounds are neither stretched nor repeated in slow mode.

Sound mapping is provisional. Source names and offline signal analysis informed
selection; final timbre and loudness require the user's audition.

## References

- [SWAT 4 concept by Pedro Konishi](https://dribbble.com/shots/13526754-UI-Concept-SWAT-4-Weapon-Wheel): thin divided circular structure.
- [Kando](https://kando.menu/): lightweight action icons and centre information.
- [visionOS ornaments](https://developer.apple.com/design/human-interface-guidelines/ornaments): floating control layer.
- [Microsoft Acrylic](https://learn.microsoft.com/en-us/windows/apps/design/style/acrylic): translucent blurred menu material.
- [Apple Liquid Glass](https://www.apple.com.cn/newsroom/2025/06/apple-introduces-a-delightful-and-elegant-new-software-design/): restrained edge light and continuous interaction.

These are visual references. No interface artwork or implementation code from
these references was copied into the preview.

## Preservation and verification — 2026-11-12

Before implementation, commit `a4de092` was archived as
`archive/wheel-before-segmented-glass-20261112`. The corresponding ignored backup
under `bin/Backups/` contains `source.zip`, verified against all 201 Git blobs.
Both copies of the earlier preview matched. The installed executable still
matches the previous complete runtime/model/configuration backup.

Checks performed:

- Node syntax check and embedded audio identity/manifest validation.
- Real Chromium rendering in the conversation sandbox at 736 px and 320 px.
  The 320 px browser leaves a 288 px product surface; the ring fits without overflow.
- Eight keyboard selections, Enter confirmation, Escape cancellation, held
  drag/release, edge press without dragging, and rapid repeated replay.
- Four audition paths with volume set to zero, followed by mute; no speaker
  playback was used for validation. No browser console errors were reported.
- `dotnet build "Action Wheel.csproj" --no-restore`: zero warnings and errors.

No native UI, hooks, settings, executable or model was changed. This preview does
not execute system actions, open the screenshot submenu or play the desktop
depth-refresh effect. User visual and sound acceptance remains outstanding.
