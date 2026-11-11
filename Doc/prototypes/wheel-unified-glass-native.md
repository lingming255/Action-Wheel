# Native unified frosted wheel

The native wheel now uses the continuous glass layout from `wheel-unified-glass-preview.html`:
one circular desktop material, a centre divider, eight fine radial dividers, stationary icons,
local selection tint and a short direction arc. The whole surface opens and closes together.

`WheelGlassSurface` uses the OS compositor's host backdrop with a Win2D Gaussian blur and circular
alpha mask. It samples the live desktop without a screenshot. The mask follows the XAML centre,
scale, opacity and monitor DPI. Windows 11 host backdrop support is required for desktop blur;
unsupported systems retain a readable translucent tint. Closing releases the effect graph; a
centre refresh releases it before the overlay becomes a full-screen click-through animation.

The current ring animation setting still controls duration and preset, interpreted as motion of
the entire sheet. `None` stays immediate. Confirm/cancel tails last 65% of the opening duration,
clamped to 70–160ms, and pass input through immediately. Actions dispatch after the tail using
the target captured for that exact gesture. Existing user refresh/reclaim settings are preserved.

Optional Kenney CC0 open/switch/confirm/cancel sounds are embedded. Settings expose a bilingual
enable switch and volume control; the default is off and 45%. Audio device work runs on a
background consumer. A direction cue is throttled to 60ms. Licensing remains in `wheel-sounds/`.

The opt-in `--refresh-gpu-smoke <absolute-report-path>` check includes a hidden native glass
probe: desktop brush/mask connection and centre, eight sectors, stationary selection, group
expansion/collapse, duplicate confirmation, closing input transparency and cleanup. It also
retains the existing full refresh/reclaim/settings/presentation checks. These do not constitute
visual acceptance; the user reviews the deployed wheel on the desktop.

Rollback before migration: `archive/wheel-before-native-unified-glass-20261112` (`d7ac6ae`).
The complete source/runtime/model/configuration snapshot and SHA manifest are under
`bin/Backups/wheel-before-native-unified-glass-20261112/` (local, not tracked).
