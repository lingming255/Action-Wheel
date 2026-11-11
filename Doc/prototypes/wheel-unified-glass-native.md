# Native unified frosted wheel

The native wheel now uses the continuous glass layout from `wheel-unified-glass-preview.html`:
one circular desktop material, a centre divider, eight fine radial dividers, stationary icons,
local selection tint and a short direction arc. The whole surface opens and closes together.

`WheelGlassSurface` uses the OS compositor's host backdrop with a Win2D Gaussian blur and circular
alpha mask. It samples the live desktop without a screenshot. The mask follows the XAML centre,
scale, opacity and monitor DPI. The settled frost covers 60%, with a separate 20% tint; the dividers
remain independent and retain their original stroke/opacity. Windows 11 host backdrop support is required for desktop blur;
unsupported systems retain a readable translucent tint. Closing releases the effect graph; a
centre refresh releases it before the overlay becomes a full-screen click-through animation.

The current ring animation setting still controls duration and preset, interpreted as motion of
the entire sheet. `None` stays immediate. Confirm/cancel tails last 65% of the opening duration,
clamped to 70–160ms, and pass input through immediately. Actions dispatch after the tail using
the target captured for that exact gesture. Existing user refresh/reclaim settings are preserved.

The sheet now settles with a slight overshoot, a short perimeter sweep and a fading outline pulse.
Selection tint/arc fade in and out, centre text rises gently, and confirmation gives a short pulse.
These decorations do not delay input or action timing; `None` disables them, and refresh/close
stop them before releasing the glass.

Optional Kenney CC0 open/switch/confirm/cancel sounds borrowed from Point Duel are embedded. Settings expose a bilingual
enable switch and volume control; the default is off and 45%. Audio device work runs on a
background consumer. Open borrows the card slide, direction entry a 70ms fading chip collision,
and confirm the chip placement. Cancel reuses the short hover cue. Direction changes have no
fixed time throttle; at most three cues overlap, replacing the oldest. Exact sources, gains,
CC0 license and offline checks live in `Assets/Sounds/Casino/`; original preview cues remain in
`wheel-sounds/`. Installing the update does not change the user's enabled/volume settings.

The opt-in `--refresh-gpu-smoke <absolute-report-path>` check includes a hidden native glass
probe: desktop brush/mask connection and centre, eight sectors, stationary selection, group
expansion/collapse, duplicate confirmation, closing input transparency and cleanup. It also
retains the existing full refresh/reclaim/settings/presentation checks. These do not constitute
visual acceptance; the user reviews the deployed wheel on the desktop.

Rollback before migration: `archive/wheel-before-native-unified-glass-20261112` (`d7ac6ae`).
The complete source/runtime/model/configuration snapshot and SHA manifest are under
`bin/Backups/wheel-before-native-unified-glass-20261112/` (local, not tracked).

Rollback before transparency/motion/casino cues:
`archive/wheel-before-translucent-motion-20261112` (`10d4c9b`), with a complete source/runtime/
model/configuration backup under the matching `bin/Backups/` folder.
