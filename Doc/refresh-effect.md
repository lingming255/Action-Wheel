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

The release side has three image-driven pixel layers, with the whole animation finishing within 500 ms:

- Hold: as the wheel opens, `RefreshFrame` captures the cursor's monitor on a worker thread, then `ScreenGrade` drops saturation on every monitor to 10% via the Magnification API's full-screen colour matrix. The capture runs first because GDI captures see that matrix. From the original colour capture it builds the grey cover and the three pixel layers before release.
- Scene analysis: `RefreshAnalyzer` samples an 11.2 DIP grid (20% finer than the legacy 14 DIP grid), joins neighbouring cells with similar colours into coherent regions, and estimates visual depth/saliency from region contrast, local detail, saturation, centre bias and region scale. It remains a lightweight visual heuristic, not semantic object recognition or a learned depth model.
- Subject pixels: higher-saliency cells reserve exclusive one- or two-cell blocks, with a one-physical-pixel gap. Large blocks merge only locally similar samples in the same colour region and animation cluster. Pigment is sampled from the exact screenshot footprint and uses the legacy hue-preserving lift and opacity curve.
- Background glyphs: a finer 2.8 DIP grid (about one fifth of a legacy tile) produces many small points, plus signs and wave marks. They keep the same one-pixel gap, sample their own screenshot pixels, and may sit over larger blocks at lower opacity to create a distinct fine-grain layer.
- Boundary accents: sparse seam blocks reserve cells before subject blocks, so the two large-pixel layers never overwrite each other. Accent chips derive their contrasting hue from the corresponding local screenshot colour by rotating it 180 degrees; neutral areas use a restrained light/dark contrast instead of an arbitrary hue. The boundary layer starts later and fades last.
- Release in the centre: the grey still covers the monitor first, and only once it has been presented is the colour matrix lifted, so nothing changes visibly. The cover and pixel front spread out in an irregular circular wave from the click that opened the wheel. The live desktop is revealed underneath; the press-time pixel layers play above it.
- Timing and performance: up to 12 x 12 analysis tiles share each macro cluster. Cluster starts span the 300 ms sweep with deterministic multi-scale variation based on radial distance from the click; the background marks rise and fade first, subject pixels last longer, and boundary accents last longest. All pixel layers and the grey cover are complete by 500 ms.
- Glare: a soft white radial light front expands from the opening click (180 DIP wide). The same point gets a radial bloom (scales 0.25 to 1.3, 360 ms) and a thin horizontal lens streak (300 ms).

Only the cursor's monitor gets the radial restore; other monitors regain colour all at once when the matrix lifts. The pixel layers still represent the screen at gesture start, but the revealed desktop is live, so changes made during the hold are not replaced by a stale full-screen still.

The overlay is excluded from capture (`WDA_EXCLUDEFROMCAPTURE`), stays topmost and click-through, and closes when the storyboard completes. Colour is also restored whenever the menu closes for any other reason, and Windows drops the matrix if the process exits.

## Code locations

- `Services/ScreenGrade.cs`: saturation level and the colour matrix.
- `Services/RefreshFrame.cs`: monitor capture, grey cover and worker-thread analysis orchestration.
- `Services/RefreshAnalyzer.cs`: colour regions, visual-depth proxy, local screenshot-colour pigment, size-separated pixel rasterization, complementary seam accents and radial cluster timing.
- `Overlay/RadialMenu.xaml.cs`, `PlayRefresh(RefreshFrame?, Action)`: `RefreshBudgetMs`, `RefreshSweepMs`, `RefreshRiseMs`, `RefreshUncoverMs`, `RefreshGlareDip`; `AddSweepGlare` and `AddCursorFlare` for the glare.
- `Overlay/RadialMenu.xaml`: the empty `RefreshLayer` canvas the bands are added to.
- `Services/LauncherService.cs`: `BeginGrade`, `EndGrade`, `PlayRefresh`.
- `Tools/RefreshAnalyzerCheck/`: synthetic-scene regression check; run with `Build.ps1 -Task VerifyRefresh`.

### Trigger

`Services/LauncherService.cs`, in `OnMiddleUp(...)`:

`OnMiddleUp(...)` and `OnEmergencyTriggerReleased(...)` both call `PlayRefresh()` when the release is inside the dead zone.

`RingGeometry.TagForDirection(...)` returns `null` while the pointer remains inside the dead zone, which selects refresh rather than a sector.

## Deliberate first-version limits

- No sound.
- No settings UI.
- No learned depth or semantic segmentation model; the first version uses a CPU-only visual heuristic.
- No blur or GPU shader; particle-like marks are rasterized into cluster images on the worker thread.
- No effect across all monitors at once.

These should be added only after repeated use shows that the current visual feedback is worth keeping and identifies what feels missing.
