# Refresh effect

A centre release of the wheel plays a desktop visual effect. Hold left and press right to open,
then release right inside `RingGeometry.GestureDeadZone` (58 DIP). Releasing in a sector runs
that action instead; releasing left first or pressing Esc cancels.

## Current implementation: click-centred uniform blocks (2026-11-11)

The user chose a simpler visual after trying the reference reconstruction: each pixel block
is one solid colour with a fixed square shape. The whole block fades in/out and changes
brightness/opacity together. There are no characters, digital-rain patterns, interior SDF
gain or other per-pixel animation within a block. The preceding implementation is archived at
`archive/refresh-before-uniform-blocks-20261111` (`d613596`), with source, executable and model
backup under `bin/Backups/refresh-before-uniform-blocks-20261111`.

The capture path now selects `UniformBlockRefreshRenderer`. It keeps the independent learned
depth map and approximately three-second duration. Distance from the original gesture click
now leads the outward wave; depth supplies a small local timing stagger. Each cell samples one
original-image colour and one depth value during preparation. Fixed integer rectangles are
drawn at native output resolution; their sharp edges never pass through a low-resolution
upsampler. The per-cell envelope is evaluated directly from elapsed time, so skipped display
frames do not require history catch-up.

Current coloured emission drives a reusable low-resolution bloom buffer. Only the halo is
upsampled; the native core excludes that spatially varying halo. A core's source-over layer
has one RGB and alpha throughout its footprint, while the live background can still show
through according to that alpha. The existing 0.3 reference LUT background grade is retained
and restored behind the same arriving blocks; no additional exposure, white glare texture or global dimming
is added. All effects are composed into one native PBGRA image with a bounded worker and one
acknowledged upload, using the existing cover handoff and shared close timer.

This is an intentionally simplified design rather than a complete reproduction of both
reference shaders. `JianyingRefreshRenderer`, its original textures and the reference checks
remain available for comparison and rollback. Model inference still uses the captured still;
the static-input and independent-depth limitations below also apply to this version.

The native grid uses an even cell size near `0.0181 * min(width,height)` (minimum four pixels),
with a small fixed inset. Each cell averages 16 capture samples for its pigment and samples
depth at its centre. The same RGB proportions receive a fixed brightness lift. The physical
click, relative to the captured monitor, is passed explicitly from `RefreshFrame`. Radius `r`
is the distance to the cell's physical pixel centre divided by the distance to the farthest
screen pixel corner. It is zero for a one-pixel image; origins clamp to monitor bounds.
Start time is `140 + 1960*r + 240*depth*smoothstep(0,.15,r)` ms. Distance supplies the main
ordering, while the existing far-before-near depth direction staggers a local band by at most
240 ms and tapers away at the origin. Depth may reorder neighbours, but cannot reverse a radial
separation exceeding 240 ms. Each block lasts 600 ms, fades in over 100 ms and fades out from
220 to 600 ms. Maximum opacity is 0.88, and whole-block brightness ranges from 0.75 to 1.
The renderer requests 60 Hz display updates and evaluates continuous elapsed time, without
the archived pipeline's 30 Hz history simulation.

The 0.3 LUT correction is prepared once using a 1280-side work buffer and encoded at native
resolution. Its mask is one scalar per cell, fading from full correction to none over the
first 400 ms after that same block's arrival. Core, bloom and background therefore share
the click-centred wave. The latest possible block ends by 2940 ms, before the 3000 ms close.
All distance calculations happen once at preparation, with no added per-frame distance work.
Bloom uses four reusable
RGB float grids sized according to the native cell spacing: about 5.9 MiB at 1080p or 4.7 MiB
at 4K. The additional retained native background correction is about 7.9/31.6 MiB respectively;
capture/playback/UI buffers and the depth model are additional. The construction-only work
textures and encoder can be collected afterward. No full-screen buffer is allocated per frame.

Before this timing change, the clean depth-only uniform-block version was preserved at
`archive/refresh-before-click-depth-wave-20261111` (`650e8da`; runtime from `d88a9cf`).
`bin/Backups/refresh-before-click-depth-wave-20261111` contains source ZIP, complete runtime
and local models, with copied files hash-verified. The annotation helper accepts an optional
physical click and records its new timing formula; previously exported depth plots are unchanged.

### Uniform-block validation before click timing (2026-11-11)

All six `Build.ps1 -Task VerifyRefresh` suites passed. New checks cover 12 whole native core
rectangles pixel-for-pixel, hard boundary samples, independent depth timing, whole-block fades,
pure-red/blue exterior bloom without white contamination, direct-time jump/backward/repeated
determinism, PBGRA, full overwrite of dirty buffers, cancellation/reuse, immutable inputs,
undefined capture alpha and invalid buffer/depth/alias rejection. A 10,000-case independent
visible-composition oracle measured maximum errors of 0.977 byte for additive bloom and
0.957 byte for core source-over composition. Ending and invalid timestamps clear both buffers.

In the same Release verification run, native 1080p active-frame p50/p95 were 4.88/5.59 ms,
and native 4K 11.94/13.89 ms. The archived reference pipeline measured 32.73/38.13 ms and
48.76/56.37 ms respectively over the same 0.4–2.4 s interval. This compares two different
visual designs, not mathematically equivalent implementations. Preparation measured 202 ms
for the first 1080p instance and 105 ms for the subsequent 4K instance; JIT/warmup and scheduling
make these unsuitable for a claim that 4K prepares faster. Five frames allocated about 51 KiB
across all managed threads. None of these measurements include capture, depth inference or UI
upload, and they do not establish actual desktop fps. Complex appearance remains user acceptance.

Exterior bloom is excluded once a core has nonzero byte alpha. Strong overlapping neighbouring
halos can therefore change at that first opacity step; user review will determine whether that
boundary needs adjustment while preserving uniform interiors.

Debug built with zero warnings/errors and Release publishing completed. The published
`refresh-uniform-blocks-20261111-win-x64` executable was copied to `quick-info-win-x64`, started
and remained alive for five seconds. Published/deployed SHA-256 matched; the running path and
enabled startup entry both point to the usual `quick-info-win-x64` executable. No desktop
animation was automatically triggered; visual acceptance remains with the user.

### Click-wave validation (2026-11-11)

All six refresh suites passed again, and Debug built with zero warnings/errors. The active
renderer checks now cover equal-depth radial order at four centre/corner/off-centre clicks,
moving/clamping the source, depth stagger bounded to 240 ms and attenuated near the click,
clipped edge cells, 1x1 and single-row/column inputs, and complete fades before close. A real
rendered-gap check isolates background correction from emitted bloom and verifies no recovery
before arrival, partial recovery at age 200 ms and full recovery at age 400 ms. Existing uniform
core, hard-edge, compositing, PBGRA, stateless-time and cancellation checks remain passing.
The annotation helper ran the actual model with default-centre and explicit clamped-corner
origins, exported different arrival times, and rejected an unpaired coordinate.

Release active-frame p50/p95 measured 4.67/5.71 ms at native 1080p and 11.56/14.20 ms at 4K,
with maxima of 6.13/20.08 ms and about 51 KiB of managed allocations per five frames. These
synthetic renderer measurements exclude capture, depth inference and UI upload. Read-only
review found no actionable issue; visual propagation/layering remains user acceptance.

Release publish completed at `refresh-click-depth-wave-20261111-win-x64`. Its executable
was copied to the usual `quick-info-win-x64` directory, restarted and remained alive for
five seconds. Published/deployed SHA-256 matched
`4AF315D951EC2C2F6BA370141DE834329926F3809D2931703FB178E11450205B`;
the running path and enabled startup entry both point to that usual executable. No desktop
effect was automatically triggered.

## Archived implementation: the two reference pipelines (2026-11-10)

Before the uniform-block change, the capture path called `RefreshDepthEstimator` and `JianyingRefreshRenderer`.
The preceding radial particles, irregular wavefront, outlines, scene exposure, 20% background
dim and extra core enhancement are not called. They remain in source and Git for comparison;
the overbright revision is archived at `archive/refresh-additive-bright-before-jianying-20261110`.

1. Capture the cursor monitor before the hold-time colour matrix lowers saturation to 10%.
2. Compute normalized relative inverse depth with a local Depth Anything V2 Small int8 ONNX
   model. A single ONNX Runtime CPU session is reused, with at most four inference threads and
   a lock against simultaneous inference. The pinned weight hash is checked at first load.
3. Run A: depth-gated ASCII atlas rasterization and column rain, three exact Gaussian pairs,
   original-image HSL hue/saturation transfer, and RGB addition. Use the resource's UI defaults,
   including zero background LUT strength.
4. Feed that result to B: Sobel-gated depth bands, 0.8 history / 0.2 current accumulation and
   decay, fixed square SDF rasterization, four-point downsampling, the nine- and eleven-tap
   Gaussian pairs, HSL source-hue transfer and addition over the depth-masked 0.3 LUT background.
5. Advance both chains together at fixed 30 Hz and display the latest finished correction.
   The reference default scan speed gives approximately three seconds. The legacy 800 ms and
   radial easing are inactive in this trial. A retains its source background fade (zero at its default); B keeps LUT strength constant
   and restores the background through its depth mask. No extra light envelope is added.

The hold's grey cover is presented for three frames before the matrix is restored and the
new playback starts. Playback and its one-shot close timer share `IRefreshRenderer.DurationMs`
(3000 ms here). One worker owns the reusable textures; only one acknowledged UI upload is
outstanding. Cancellation is checked between reference steps and during full-resolution output.
Model/capture failure produces no effect and safely restores desktop colour.

## Model and reference display boundaries

The original `nh_depth_for_light_scanning` file exists locally but requires Jianying's internal
ByteNN/cv runtime; no independently usable SDK/API was found. At the user's explicit choice,
the replacement is Depth Anything V2 Small from the pinned ONNX community export, Apache-2.0.
`Assets/RefreshReference/depth-model.json` records URL, revision, SHA-256 and preprocessing;
its licence is beside it. The 27.26 MB weights remain outside Git at
`%LOCALAPPDATA%/ActionWheel/Models/depth-anything-v2-small-quantized.onnx`.
No downloads or heuristic depth fallback occur inside the application.

The independent estimator uses ImageNet RGB normalization, a longest input side of 392 with
multiples of 14, bilinear resizing and min-max inverse-depth normalization. This differs from
both Jianying's model and the export's default 518-pixel/bicubic processor. On the local CPU,
a C# synthetic 640x360 estimate returned finite 392x224 depth in about 543 ms at first load and
114 ms on reuse. These are inference measurements, not gesture latency.

The source is the still captured at gesture start. There is no live-video DIS flow/depth
accumulation. A-then-B stacking and 30 Hz are explicit host choices; the cached packages do not
establish the user's timeline frame rate or evaluation order. The work canvas preserves the
monitor aspect ratio with a longest side capped at 1280 (previously 640); nominal reference RTs are 720x1280,
and their engine's auto-adaptation rules remain unknown. Every shader pass uses the audited
math and sampling weights, with SDR clamping, but the floating-point working buffers do not
reproduce every intermediate UNorm quantization step of the original GPU engine.

The work-resolution effect-minus-source correction is bilinearly reconstructed over the full
capture. For source S and target T, coverage A must be at least `(T-S)/(1-S)` for a brightening
channel or `(S-T)/S` for a darkening channel. Quantize A upward and encode premultiplied
`P=T-(1-A)*S`. A normal source-over image then displays the expected target without blurring
all desktop text or painting a second transparent glow image. Unchanged pixels are transparent.
Moving desktop content can misalign with this capture-relative correction; strong changes can
require opaque coverage. This is SDR, with no HDR output or continuous desktop recapture.
Only the cursor's monitor receives the effect; other monitors regain colour when the matrix lifts.

## Reference reconstruction validation (2026-11-10)

All five `Build.ps1 -Task VerifyRefresh` suites passed, including the new reference check.
The latter verifies independent shader HSL arithmetic including out-of-range inputs, source
LUT samples, scalar correction error (maximum sampled 0.500 byte), fixed-step skip equivalence,
immutable inputs, cancellation and transparent ending. Added regression checks cover prepared
versus mutable spectrum inputs, replaced inputs, Gaussian-plan invalidation and native output
SIMD tails/band boundaries/black-white endpoints. The optimized encoder differs from the old
visible composite by at most 1.000 byte in these cases. Actual learned depth inference passed.
Debug built with zero warnings and errors. Release publishing completed; the deployed
executable at `quick-info-win-x64` matched the release SHA-256 and survived the five-second
startup check. The enabled startup entry points to that same executable. A separately published self-contained single-file
check EXE successfully loaded ONNX Runtime's native library, embedded textures and the real
model, covering native extraction rather than just an unbundled console run.
The main WinUI bundle resolves Windows App SDK's ONNX native DLL version
`1.24.20260504.1.7444c45`; that exact extracted DLL was explicitly loaded in a separate
check with the 1.23.2 .NET bindings and successfully ran the real model. Different native
file hashes therefore do not by themselves indicate a failed inference dependency.

## Clarity and CPU optimization (2026-11-10)

The user reported stuttering and blurry character/edge detail. Raising the work cap to 1280
doubles each working dimension and quadruples working pixels on larger monitors. The source
size/density parameters stay unchanged; their exact raster rounding still depends on resolution.
A trial at 1920 gave active-stage medians of 62 ms (native 1080p) and 85 ms (native 4K), too slow
for this CPU stack's 30 Hz catch-up. 1280 is the selected balance for user review.

Static glyph geometry, cell-centre pigment, rain parameters and source HSL are prepared once
per gesture. B reuses HSL/LUT only for exactly unchanged input RGB. Gaussian passes reuse
bilinear coordinate plans and retain the original kernels and accumulation order. Parallel
passes use at most four bands; zero-light paths bypass unnecessary work. Native correction
encoding reuses maps/row buffers and SIMD, preserving each full-resolution capture pixel rather
than stretching an already-encoded alpha layer. Playback schedules at the renderer's 30 Hz
interval rather than rendering duplicate simulation states at 60 Hz. Every missed history step
is still computed. Closing checks cancellation between steps and during output encoding.

At a 1280x720 work canvas, the added A/B preparation caches use about 56.3 MiB and the encoder's
delta buffer about 10.5 MiB, plus small maps/row buffers and the existing textures. These caches
belong to one gesture; Gaussian plans are weakly associated with their target textures.

Release synthetic measurements across two runs, excluding UI upload, capture and depth:

| Native output | Preparation | One early step + encode | Three-step catch-up + encode | Active 0.4–2.4 s p50 | Active p95 |
| --- | --- | --- | --- | --- | --- |
| 1920x1080 | 152–166 ms | 25–27 ms | 45–47 ms | 30–34 ms | 42–59 ms |
| 3840x2160 | 263–296 ms | 35–39 ms | 49–54 ms | 49–52 ms | 54–56 ms |

The old 640-side implementation recorded roughly 96–115 ms per early frame/catch-up, sometimes
160 ms. Those old samples did not measure the same active interval and are not a precise
speedup ratio. 30 Hz describes history simulation, not guaranteed display fps; 4K remains CPU
limited. Capture, inference, upload, preparation overlap and actual gesture latency are additional.
Complex desktop appearance, motion and multi-monitor acceptance remain with the user.

## Local Jianying reference examined on 2026-11-10

The user identified the reference tracks as `彩色像素` and `光谱扫描`. The local Jianying cache is a junction from `%LOCALAPPDATA%\JianyingPro\User Data\Cache` to `D:\Caches\R1my\jianying-media-cache`. Two resource packages updated at 21:10 contain readable Lua/GLSL. Their Chinese titles are absent from the package manifests; associating A with `光谱扫描` and B with `彩色像素` remains an inference from their parameters and rendering code, not a verified title-to-ID mapping.

**Reference reconstruction status:** the user rejected the overbright additive-only revision (`e280851`) and requested both complete pixel/scanning/light pipelines. The archived `JianyingRefreshRenderer` reimplements those audited postprocessing chains with an independently runnable Depth Anything V2 Small model. The user subsequently chose the simplified uniform-block design above. The reconstruction remains available as a source comparison, not the original Jianying engine or a claim of identical output.

A is `effect\7399492904915389696\5373a80f847b81bdfa9eba25aa6aab49`:

- Its `algorithmConfig.json` first resizes input to 360 x 640 and requests `nh_depth_for_light_scanning` with `flow_weight=0.8`. `AmazingFeature\lua\SeekModeScript.lua` lines 161–169 obtains `outputMap["depth"]` from `Amaz.Algorithm.getAEAlgorithmResult().getScriptInfo(...)`. Depth is an external model result, not calculated by its fragment shader. Both effects sample depth with a vertical UV flip and optionally invert it when `dir>0.5`.
- `AmazingFeature\xshader\depthLayer.frag` lines 73–99 scans in depth order, with `t=fract(time*speed/10)` and a smooth triangular band derived from `smoothstep(t,t+width,0.5*depth+width)`. Lines 102–143 form a depth-dependent ASCII grid: density is `82*(1-0.45*depth)`, the cell centre samples the original image, and its luminance selects one of 24 glyphs in an 8-column x 3-row atlas. The actual `image\text.png` is 396 x 194; a generated square-block mask would not reproduce this texture.
- Lines 162–197 add column-dependent digital-rain phase and speed. With the quantized column coordinate `x`, phase is `sin(15*x)`, column speed is `0.3*cos(3*x)+0.7`, and `y=fract(v+time*columnSpeed*speed/3+phase)`. Rain is the colour-bar sample at 0.58 multiplied by `1/y-0.4/(y+0.001)`. The inverted glyph texture, cell-centre source colour, brightness, rain and depth band are multiplied for the sharp layer. The colour bar is 180 x 1; a nearest sample around 0.58 is approximately RGB (235,122,0), before final hue processing.
- Lua lines 111–127 render the sharp layer, then three horizontal/vertical Gaussian pairs. `gaussian5.frag` uses the five weights `0.192051, 0.203926, 0.208046, 0.203926, 0.192051`. Lua lines 229–245 use a one-texel step for the first pair and 5.25 blur texels for each later pair. Serialized render targets have nominal sizes 720 x 1280 for sharp/output and 180 x 320 for blur/aux; engine adaptation to other resolutions/aspects has not been verified. These are not the Wheel's repeated box blurs.
- `blending.frag` lines 473–483 first computes `light=sharp+glowGain*blur`, replaces its HSL hue with the original image's hue, then blends 70% of a version using the original image's saturation while retaining the light's lightness. Only after this colour processing is light added directly to the LUT-graded background. The hue and saturation stages are part of the active path, not unused helper functions.
- The `extra.json` event defaults differ from the Lua constructor: intensity 0.614 maps to brightness 4.298, luminance 0.642 to glow gain 2.6964, speed approximately 1/3 to scan speed approximately 3.333, range 0.333 to width 0.18315, and background animation 0 to `filter_alpha=0`. A's default therefore leaves the background ungraded, despite its dark LUT. Its background LUT strength fades during the final 20% for clips shorter than three seconds, or the last 0.6 seconds for longer clips; this does not establish the user's current UI values.

B is `effect\7399495216064892200\8073d46c5c3b46cee867e6a3c0f86154`:

- Its active Lua chain is depth/Sobel/history -> square-pixel rasterization -> downsample -> two horizontal/vertical Gaussian pairs -> history copy -> final composition. `colorGlow.frag` exists but is not in that chain. `depthLayer.frag` derives three depth bands in green, cyan and white, gates them using a Sobel test on the RGB vector length, and computes `L=clamp(1.01*(0.8*previous+0.2*bands*edgeGate)-0.01,0,1)`. With cleared history, the first frame's maximum is 0.192; directly using full-strength bands would bypass this important brightness and persistence behaviour.
- `depthLayer0.frag` rounds the mosaic spacing down to an even number. At the package's default size parameter 0.27 this is `m=2*floor(0.0181*min(width,height)/2)`. It samples the history-derived light at the grid cell's top-left corner, evaluates a signed box distance at `centre-1.2*pixelOffset`, and outputs `clamp(sample*(1-distance),0,1)`. This is a square signed-distance raster, not Wheel's saliency blocks, outlined chips or ASCII layer.
- The active first Gaussian kernel has nine taps: centre weight 1 and symmetric outward weights 0.980198, 0.923116, 0.835270 and 0.726149, divided by 7.929466. The second has eleven taps, additionally using 0.606530 at distance five, divided by 9.142526. The first horizontal/vertical pair has a one-texel step and the second uses the blur parameter, default 0.75. Serialized nominal render targets are 720 x 1280 for the main/history layers, 180 x 320 for blur and 144 x 256 for aux; their engine resize rules remain unverified.
- Lua lines 302–305 map luminance `x` to sharp gain `10*x` and glow gain `5*x`, giving 4 and 2 at default 0.4. `blending.frag` lines 491–510 computes `light=4*sharp+2*blur`, scales HSL saturation by the default colour value 0.7, then replaces hue with the original image hue while preserving light saturation/lightness. It mixes original and LUT-graded background through the depth scan mask, using default `filter_alpha=0.3`, before adding the processed light in RGB. There is no ordinary brightness threshold on particle emission.

Both final passes use RGB addition rather than Screen or a second transparent colour layer, but their depth input, temporal history, rasterization, Gaussian sampling and HSL stages are essential to the full result. The LUT sampler is a 64-cubed colour cube packed into 512 x 512, with bilinear R/G sampling and interpolation between the two adjacent B slices. Nearest-neighbour grey-ramp samples from A's `image\filter_1.png` map input 128 to approximately 50 and 255 to 99; B maps them to approximately 72 and 158. These samples alone do not establish visible dimming: A defaults to LUT strength 0 and B to 0.3, and the user's current UI parameters are unverified.

A CPU port of the readable shader mathematics alone would still need matching depth results, image textures, texture sampling and render-target quantization to reproduce the two effects. Substituting the Wheel's current visual-saliency heuristic for the neural depth model would be an approximation, not complete reproduction. Full third-party shader source has not been copied into this repository.

## Code locations

- `Services/RefreshFrame.cs`: original capture, initial grey handoff and depth preparation.
- `Services/UniformBlockRefreshRenderer.cs`: active native solid-block scan, whole-block envelopes and same-colour exterior bloom.
- `Services/RefreshDepthEstimator.cs`: pinned local ONNX model, normalization and CPU inference.
- `Services/JianyingRefreshRenderer.cs`: fixed-step A-then-B stack and full-resolution correction.
- `Services/RefreshOutputEncoder.cs`: cached/SIMD native-resolution colour correction.
- `Services/RefreshSpectrumEffect.cs`: ASCII/depth scan, rain, Gaussian and HSL chain A.
- `Services/RefreshPixelScanEffect.cs`: Sobel/history, square raster, Gaussian and HSL chain B.
- `Services/ReferenceEffectMath.cs`: bilinear textures, HSL, LUT and Gaussian kernels.
- `Assets/RefreshReference/`: compressed atlas/colour-bar/LUT textures and model provenance.
- `Services/IRefreshRenderer.cs`, `Overlay/RefreshPlayback.cs`: bounded worker and cancellation.
- `Overlay/RadialMenu.xaml.cs`: final-composite image, cover handoff and shared close budget.
- `Services/LauncherService.cs`: trigger, hold-time colour grading and safe recovery.
- `Tools/ReferenceRefreshCheck/`: reconstruction checks. `Build.ps1 -Task VerifyRefresh` also
  retains the legacy Analyzer/Bloom/Outline/Renderer suites as regressions for archived code.
- `Tools/UniformBlockRefreshCheck/`: checks for the active uniform-block renderer.
- `Tools/RefreshDepthAnnotate/`: a local script that annotates an input image with the actual
  renderer's per-block depth, exporting readable images and precise depth/start-time CSV.
