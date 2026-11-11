# Unified frosted glass wheel

The user requested one continuous circular menu with fine dividing lines, using
the supplied weapon-wheel image as a structural reference. This preview replaces
separated glass pieces with one frosted circular backing shared by the outer ring
and centre. The earlier independent-button and separated-sector previews remain
unchanged for comparison.

## Changes from the separated ring

- Exactly one background-blur surface covers the complete circular menu.
  The outer circle, inner circle and eight radial dividers are drawn once.
- Transparent sector hit areas meet without gaps. Selection changes only local
  illumination and the small direction arc; sectors do not move apart or gain
  separate outlined frames.
- Centre information sits on the same glass backing, with only the inner circle
  separating it from the action ring. The centre target is 130 px across; the
  held-gesture dead zone remains 58 px.
- The whole menu scales in and recedes as a unit. There is no sector stagger or
  separate centre opening animation. The backing's ancestors have no filter, so
  the blur can sample the scene beneath the menu.
- Existing bilingual labels, opening-duration and slow controls, keyboard and
  held-gesture interaction, local action simulation and four Kenney CC0 sounds
  are retained. Sound is off by default and never autoplays on initial render.
- Optional host controls adjust blur, tint opacity and local illumination.
  The background window overlaps the menu to make the frosted treatment visible.

This is a browser preview, not a native Acrylic implementation. It does not execute
system actions or replace the installed application. Visual acceptance remains
with the user.

## Preservation and verification — 2026-11-12

Before implementation, commit `49c3a5f` was archived as
`archive/wheel-before-unified-glass-20261112`. Its ignored backup in `bin/Backups/`
contains a source ZIP verified against all 214 Git blobs. Source and conversation
copies of the separated-ring preview matched, and the installed executable still
matched the complete earlier runtime/model/configuration backup.

Validation: Node syntax check; four embedded sound buffers identical to the saved
WAV assets; real Chromium rendering in the conversation sandbox at 736 and 320 px;
single blur surface, eight dividers and zero per-sector transforms; right-arrow
selection, Enter confirmation, repeated replay and Escape cancellation. The narrow
preview had equal client and scroll widths of 288 px. Browser console was empty.
The inherited drag and audio state machines were reviewed without changing them;
their detailed previous validation remains documented in `wheel-glass-ring-preview.md`.
Repository `dotnet build "Action Wheel.csproj" --no-restore` passed with zero
warnings and errors. No installed files, settings, hooks or models were changed.
