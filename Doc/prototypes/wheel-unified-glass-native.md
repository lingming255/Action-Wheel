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

Optional CC0 interaction sounds are embedded. Settings expose a bilingual
enable switch and volume control; the default is off and 45%. Audio device work runs on a
background consumer. Open uses a 205ms relay mechanism cue at 0.45 reference gain; direction entry retains Point Duel's 70ms fading chip collision,
and confirm the chip placement. Cancel reuses the short hover cue. Direction changes have no
fixed time throttle; at most three cues overlap, replacing the oldest. Exact sources, gains,
CC0 license and offline checks live in `Assets/Sounds/Casino/` and `Assets/Sounds/Relay/`; original preview cues remain in
`wheel-sounds/`. Installing the update does not change the user's enabled/volume settings.

The opt-in `--refresh-gpu-smoke <absolute-report-path>` check includes a hidden native glass
probe: desktop brush/mask connection and centre, eight sectors, stationary selection, group
expansion/collapse, duplicate confirmation, closing input transparency and cleanup. It also
retains the existing full refresh/reclaim/settings/presentation checks. These do not constitute
visual acceptance; the user reviews the deployed wheel on the desktop.

Mixed-DPI placement first migrates the hidden HWND to the target monitor, then uses
`GetDpiForWindow` to set its physical position and size together around the gesture origin.
The first show, native DPI changes and XamlRoot changes reconcile that same anchor. Corrections
only write changed bounds, and close/refresh detach them before the window changes purpose.
Gesture offsets and outside-click checks read the actual native client rectangle; the main
direction gesture keeps its original pointer anchor. This prevents the old double scaling
(a 630px destination size became 756px when moving from 125% to 150%, shifting the ring 63px).

The opt-in `--wheel-position-smoke <absolute-report-path>` diagnostic uses real WinUI windows
cloaked by DWM before their first activation. It exercises all attached monitors, edges, group
sizes, repeated cross-monitor moves and cancellation, measuring native, visual and gesture
centres. It starts before hooks and configuration loading, disables audio only in memory, and
does not capture or show the desktop. These numerical checks supplement user acceptance.

Rollback before the mixed-DPI placement fix: `archive/wheel-before-mixed-dpi-20261112`
(`1207df4`), with the complete source/runtime/model/configuration snapshot under the matching
`bin/Backups/` folder.

Rollback before migration: `archive/wheel-before-native-unified-glass-20261112` (`d7ac6ae`).
The complete source/runtime/model/configuration snapshot and SHA manifest are under
`bin/Backups/wheel-before-native-unified-glass-20261112/` (local, not tracked).

Rollback before transparency/motion/casino cues:
`archive/wheel-before-translucent-motion-20261112` (`10d4c9b`), with a complete source/runtime/
model/configuration backup under the matching `bin/Backups/` folder.
