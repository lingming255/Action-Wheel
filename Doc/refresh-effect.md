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
- Release in the centre: the grey still covers the monitor first, and only once it has been presented is the colour matrix lifted, so nothing changes visibly. Every actual mosaic block and fine glyph receives an independent arrival from the distance between its own footprint centre and the opening click, normalized by the farthest monitor corner. The live desktop is revealed underneath as individual analysis tiles of the cover fade; neither particles nor the cover rise and fall in 12 x 12 macro rectangles.
- Timing: `RefreshTiming.TimeScale` retains the 800 ms budget: the radial sweep is 480 ms and each analysis tile of the grey cover fades in about 217 ms after its own arrival. Early particles retain shorter local lifetimes and disappear first; the last possible boundary pulse finishes around 795 ms. A merged one- or two-cell block always has one envelope for its whole footprint. Fine points, plus signs and wave marks use their own footprint centres. Legacy macro clusters remain only for the unchanged merge constraint and older checks, not playback.
- Highlight outlines: each actual particle silhouette receives a narrow highlight in that particle's own colour, with a softer coloured rim beside it. RGB channels brighten by the same factor to preserve their ratios and hue; no white is mixed into the pigment. Fine points, plus signs and wave marks also retain their original colour. The source-coloured interior, original alpha coverage and transparent one-pixel gaps remain. Bloom emission samples are prepared from the original pigments independently of the outlined layer.
- Frame rendering and pixel bloom: one bounded worker evaluates the current cover and every particle envelope on demand into reusable full-resolution PBGRA and low-resolution bloom buffers. Each frame accumulates the active particles' original-colour emission, weighted by their current opacity, then applies two scales of repeated box blur. The UI uploads the two completed buffers into two `Image`/`WriteableBitmap` surfaces. Only one upload can be queued, and the worker waits for its acknowledgement before reusing the buffers. Playback follows elapsed time rather than stretching the 800 ms duration for late frames. The live desktop itself is not captured again or blurred, and no per-particle UI nodes or stored full-screen frame sequence are created.
- Glare: the expanding white circular highlight and `AddSweepGlare` have been removed. The central click flare and horizontal lens streak remain, following the same timing scale (about 571 ms and 503 ms) with their reduced intensity.
- Validation and performance (2026-11-10): Debug, Release Publish and all four `VerifyRefresh` checks passed. The usual local executable was replaced, its SHA-256 matched the publish output, and it survived the five-second startup check with the service reported running. Synthetic Analyzer/Outline/Renderer frames confirm independent radial particles; actual desktop visual acceptance is left to the user. Dense synthetic 4K renderer measurements are roughly 28–48 ms per frame, before UI upload; this does not establish 60 fps. End-to-end frame rate and immediate-release latency remain unmeasured. Capture, analysis, outline/emission preparation, UI bitmap setup and the three-frame cover handoff precede the 800 ms playback and add latency. Capture preparation is not cancellable yet, so rapid repeated opening can briefly overlap that work.

Only the cursor's monitor gets the radial restore; other monitors regain colour all at once when the matrix lifts. The pixel layers still represent the screen at gesture start, but the revealed desktop is live, so changes made during the hold are not replaced by a stale full-screen still.

The overlay is excluded from capture (`WDA_EXCLUDEFROMCAPTURE`), stays topmost and click-through, and closes when the 800 ms storyboard completes. Closing cancels the worker, detaches pending rendering callbacks and stops the storyboard; a late upload cannot touch the closed overlay. Colour is also restored whenever the menu closes for any other reason, and Windows drops the matrix if the process exits.

## Code locations

- `Services/ScreenGrade.cs`: saturation level and the colour matrix.
- `Services/RefreshFrame.cs`: monitor capture, grey cover and worker-thread analysis orchestration.
- `Services/RefreshAnalyzer.cs`: colour regions, visual-depth proxy, local screenshot-colour pigment, size-separated pixel rasterization, complementary seam accents and individual particle footprints/arrival times.
- `Services/RefreshTiming.cs`: shared 800 ms budget, common timing scale, exact radial arrival (`FrontAt`), cover fade and individual pixel-layer envelopes.
- `Services/RefreshOutline.cs`: hue-preserving highlight outlines on actual particle silhouettes, using proportional RGB brightening without a white mix.
- `Services/RefreshRenderer.cs`: preparation of per-particle emission and envelopes, tile-level cover reveal, current-frame particle compositing and two-scale box-blurred bloom.
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
