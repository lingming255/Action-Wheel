# Refresh effect

## Purpose

The refresh gesture is a tactile reset rather than a data reload. It replaces the familiar habit of returning to the desktop and choosing Refresh, giving the left-plus-right mouse chord a satisfying neutral outcome when no wheel sector is selected.

## Interaction

1. Hold the left mouse button and press the right mouse button to open the wheel.
2. Release the right button inside the centre dead zone to play the refresh effect.
3. Move into a sector and release the right button to execute that sector instead.
4. Release the left button first, or press Esc, to cancel without an effect.

The centre dead zone is `RingGeometry.GestureDeadZone`, currently 58 DIP.

## Visual design

The release side has three image-driven pixel layers, with the whole animation finishing within 800 ms:

- Hold: as the wheel opens, `RefreshFrame` captures the cursor's monitor on a worker thread, then `ScreenGrade` drops saturation on every monitor to 10% via the Magnification API's full-screen colour matrix. The capture runs first because GDI captures see that matrix. From the original colour capture it builds the grey cover and the three pixel layers before release.
- Scene analysis: `RefreshAnalyzer` samples an 11.2 DIP grid (20% finer than the legacy 14 DIP grid), joins neighbouring cells with similar colours into coherent regions, and estimates visual depth/saliency from region contrast, local detail, saturation, centre bias and region scale. It remains a lightweight visual heuristic, not semantic object recognition or a learned depth model.
- Subject pixels: higher-saliency cells reserve exclusive one- or two-cell blocks, with a one-physical-pixel gap. Increased minimum cell coverage reduces missing patches while retaining scene depth. Large blocks merge only locally similar samples in the same colour region; the previous macro-cluster boundary is retained solely as a generation/merge constraint. Each resulting complete block has its own timing. Pigment is sampled from the exact screenshot footprint and uses the legacy hue-preserving lift and opacity curve.
- Background glyphs: a finer 2.8 DIP grid (about one fifth of a legacy tile) produces many small points, plus signs and wave marks. They keep the same one-pixel gap, sample their own screenshot pixels, and may sit over larger blocks at lower opacity to create a distinct fine-grain layer.
- Boundary accents: sparse seam blocks reserve cells before subject blocks, so the two large-pixel layers never overwrite each other. Accent chips derive their contrasting hue from the corresponding local screenshot colour by rotating it 180 degrees; neutral areas use a restrained light/dark contrast instead of an arbitrary hue. The boundary layer starts later and fades last.
- Release in the centre: the grey still covers the monitor first, and only once it has been presented is the colour matrix lifted, so nothing changes visibly. Every actual mosaic block and fine glyph receives an independent arrival from a shared near-circular wavefront with evolving dents and bulges. Its phases are randomized once per gesture using the Analyzer seed and an independent `Random`, leaving the generated particle geometry unchanged. Individual analysis tiles of the cover use that same wavefront, so the live desktop reveal follows the particles; neither rises and falls in 12 x 12 macro rectangles.
- Wavefront shape: let `theta` be the direction from the opening click, `p` the normalized sweep progress from 0 to 1, and `r` the distance normalized by the farthest monitor corner. The front radius is `F(theta,p) = p + 0.04*sin(pi*p)*N(theta,p)`, with `N = 0.50*sin(3*theta + phi1 + 2*pi*0.55*p) + 0.32*sin(7*theta + phi2 - 2*pi*0.9*p) + 0.18*sin(13*theta + phi3 + 2*pi*1.3*p)`. The seeded phases are fixed within a gesture; the phase travel with `p` makes its outline change continuously during expansion. Integer angular frequencies make the seam periodic, while the bounded amplitude and progress rates preserve monotonic outward travel without flashbacks. The perturbation vanishes at both ends, preserving radius 0 at `p=0` and radius 1 at `p=1`.
- Arrival preparation: `RefreshWavefront` builds a small angle-by-radius inverse-arrival lookup table during preparation. For a particle footprint centre or one analysis tile of the cover, it looks up the progress satisfying `F(theta,p) = r` and converts it into an arrival within the 480 ms sweep. Particle and cover preparation share the same seeded wavefront. Playback evaluates the stored arrival/envelope at the current elapsed time; it does not evaluate sine functions per particle per frame or cache a full-screen frame sequence.
- Timing: `RefreshTiming.TimeScale` retains the 800 ms budget: the sweep is 480 ms and each analysis tile of the grey cover fades in about 217 ms after its own arrival. Early particles retain shorter local lifetimes and disappear first; the last possible boundary pulse finishes around 795 ms. A merged one- or two-cell block always has one envelope for its whole footprint. Fine points, plus signs and wave marks use their own footprint centres. Legacy macro clusters remain only for the unchanged merge constraint and older checks, not playback. The previous pure distance-ratio arrival describes the archived circular version; current arrival follows the evolving wavefront.
- Highlight outlines: each actual particle silhouette receives a narrow highlight in that particle's own colour, with a softer coloured rim beside it. RGB channels brighten by the same factor to preserve their ratios and hue; no white is mixed into the pigment. Fine points, plus signs and wave marks also retain their original colour. The source-coloured interior, original alpha coverage and transparent one-pixel gaps remain. Bloom emission samples are prepared from the original pigments independently of the outlined layer.
- Frame rendering and pixel bloom: one bounded worker evaluates the current cover and every particle envelope on demand into reusable full-resolution PBGRA and low-resolution bloom buffers. Each frame accumulates the active particles' original-colour emission, weighted by their current opacity, then applies two scales of repeated box blur. The UI uploads the two completed buffers into two `Image`/`WriteableBitmap` surfaces. Only one upload can be queued, and the worker waits for its acknowledgement before reusing the buffers. Playback follows elapsed time rather than stretching the 800 ms duration for late frames. The live desktop itself is not captured again or blurred, and no per-particle UI nodes or stored full-screen frame sequence are created.
- Glare: the expanding white circular highlight and `AddSweepGlare` have been removed. The central click flare and horizontal lens streak remain, following the same timing scale (about 571 ms and 503 ms) with their reduced intensity.
- Validation and performance (2026-11-10, evolving irregular edge): Debug, Release Publish and all four `VerifyRefresh` suites passed, including wavefront continuity, changing shape, fixed-seed repeatability, monotonic expansion, inverse-arrival accuracy and the 800 ms budget. The usual local executable was replaced, its SHA-256 matched the publish output, and it survived the five-second startup check. Wavefront preparation measured 3.44 ms and sampled inverse error was at most 0.0174 ms; these are synthetic checks. This revision adds no per-frame wavefront calculation. Dense synthetic 4K renderer measurements were about 28–45 ms per frame, before UI upload; this does not establish 60 fps. Actual desktop visual acceptance is left to the user; no automated visual acceptance was attempted for this revision. End-to-end frame rate and immediate-release latency remain unmeasured. Capture, analysis, outline/emission preparation, UI bitmap setup and the three-frame cover handoff precede the 800 ms playback and add latency. Capture preparation is not cancellable yet, so rapid repeated opening can briefly overlap that work.

Only the cursor's monitor gets the radial restore; other monitors regain colour all at once when the matrix lifts. The pixel layers still represent the screen at gesture start, but the revealed desktop is live, so changes made during the hold are not replaced by a stale full-screen still.

The overlay is excluded from capture (`WDA_EXCLUDEFROMCAPTURE`), stays topmost and click-through, and closes when the 800 ms storyboard completes. Closing cancels the worker, detaches pending rendering callbacks and stops the storyboard; a late upload cannot touch the closed overlay. Colour is also restored whenever the menu closes for any other reason, and Windows drops the matrix if the process exits.

## Code locations

- `Services/ScreenGrade.cs`: saturation level and the colour matrix.
- `Services/RefreshFrame.cs`: monitor capture, grey cover and worker-thread analysis orchestration.
- `Services/RefreshAnalyzer.cs`: colour regions, visual-depth proxy, local screenshot-colour pigment, size-separated pixel rasterization, complementary seam accents and individual particle footprints/arrival times from the shared gesture wavefront.
- `Services/RefreshWavefront.cs`: per-gesture seeded multi-frequency wavefront, progress-dependent shape and small inverse-arrival lookup table shared by particles and cover tiles.
- `Services/RefreshTiming.cs`: shared 800 ms budget, common timing scale, cover fade and individual pixel-layer envelopes; `FrontAt` retains the circular reference arrival for legacy callers/checks.
- `Services/RefreshOutline.cs`: hue-preserving highlight outlines on actual particle silhouettes, using proportional RGB brightening without a white mix.
- `Services/RefreshRenderer.cs`: preparation of per-particle emission and envelopes, cover-tile arrival from the shared wavefront, current-frame particle compositing and two-scale box-blurred bloom.
- `Services/RefreshBloom.cs`: retained reference bloom builder for synthetic checks; live frame bloom is computed by `RefreshRenderer`.
- `Overlay/RefreshPlayback.cs`: one worker, reusable buffers, acknowledged dispatcher uploads, elapsed-time scheduling and cancellation.
- `Overlay/RadialMenu.xaml.cs`, `PlayRefresh(RefreshFrame?, Action)`: two image surfaces, initial cover presentation, playback lifecycle and `AddCursorFlare`. The expanding sweep highlight is removed.
- `Overlay/RadialMenu.xaml`: the `RefreshLayer` canvas hosting the two images and remaining central flare/streak.
- `Services/LauncherService.cs`: `BeginGrade`, `EndGrade`, `PlayRefresh`.
- `Tools/RefreshAnalyzerCheck/`, `Tools/RefreshBloomCheck/`, `Tools/RefreshOutlineCheck/` and `Tools/RefreshRendererCheck/`: synthetic-scene checks for coverage, independent radial particle timing, local colour, emission, outline geometry, rendered frames and performance; run with `Build.ps1 -Task VerifyRefresh`.

### Trigger

`Services/LauncherService.cs`, in `OnMiddleUp(...)`:

`OnMiddleUp(...)` and `OnEmergencyTriggerReleased(...)` both call `PlayRefresh()` when the release is inside the dead zone.

`RingGeometry.TagForDirection(...)` returns `null` while the pointer remains inside the dead zone, which selects refresh rather than a sector.

## Deliberate first-version limits

- No sound.
- No settings UI.
- No learned depth or semantic segmentation model; the first version uses a CPU-only visual heuristic.
- Bloom filters the current frame's particle emission on a CPU worker; it is not HDR post-processing of a continuously recaptured desktop. Frame rendering and UI upload costs limit the achieved frame rate, especially at 4K.
- No effect across all monitors at once.

These should be added only after repeated use shows that the current visual feedback is worth keeping and identifies what feels missing.
