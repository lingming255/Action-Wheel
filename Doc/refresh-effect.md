# Refresh effect

A centre release of the wheel plays a desktop visual effect. Hold left and press right to open,
then release right inside `RingGeometry.GestureDeadZone` (58 DIP). Releasing in a sector runs
that action instead; releasing left first or pressing Esc cancels.

## Current implementation: the two reference pipelines

The active capture path calls only `RefreshDepthEstimator` and `JianyingRefreshRenderer`.
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

## Model and display boundaries

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
monitor aspect ratio with a longest side capped at 640; nominal reference RTs are 720x1280,
and their engine's auto-adaptation rules remain unknown. Every shader pass uses the audited
math and sampling weights, with SDR clamping, but the floating-point working buffers do not
reproduce every intermediate UNorm quantization step of the original GPU engine.

The low-resolution effect-minus-source correction is bilinearly reconstructed over the full
capture. For source S and target T, coverage A must be at least `(T-S)/(1-S)` for a brightening
channel or `(S-T)/S` for a darkening channel. Quantize A upward and encode premultiplied
`P=T-(1-A)*S`. A normal source-over image then displays the expected target without blurring
all desktop text or painting a second transparent glow image. Unchanged pixels are transparent.
Moving desktop content can misalign with this capture-relative correction; strong changes can
require opaque coverage. This is SDR, with no HDR output or continuous desktop recapture.
Only the cursor's monitor receives the effect; other monitors regain colour when the matrix lifts.

## Validation

All five `Build.ps1 -Task VerifyRefresh` suites passed, including the new reference check.
The latter verifies independent shader HSL arithmetic including out-of-range inputs, source
LUT samples, visible correction error (maximum sampled 0.500 byte), fixed-step skip equivalence,
immutable inputs, cancellation and transparent ending. Actual learned depth inference passed.
Debug built with zero warnings and errors. Release publishing completed; the deployed
executable at `quick-info-win-x64` matched the release SHA-256 and survived the five-second
startup check. The enabled startup entry points to that same executable. A separately published self-contained single-file
check EXE successfully loaded ONNX Runtime's native library, embedded textures and the real
model, covering native extraction rather than just an unbundled console run.
The main WinUI bundle resolves Windows App SDK's ONNX native DLL version
`1.24.20260504.1.7444c45`; that exact extracted DLL was explicitly loaded in a separate
check with the 1.23.2 .NET bindings and successfully ran the real model. Different native
file hashes therefore do not by themselves indicate a failed inference dependency.

Single synthetic samples with a 640-side work canvas took about 96–115 ms per completed frame
including full-resolution correction at native 1080p/4K; these noisy samples do not establish
that either resolution is faster. Earlier samples reached about 160 ms during catch-up.
This CPU trial is well below 30 display fps: 30 Hz describes simulation history, not achieved
output frame rate. Capture, inference, UI upload, preparation overlap and gesture latency are
additional. Closing cancels between simulation steps so a catch-up cannot continue indefinitely.
Complex desktop appearance, motion and multi-monitor acceptance remain with the user.

## Local Jianying reference examined on 2026-11-10

The user identified the reference tracks as `彩色像素` and `光谱扫描`. The local Jianying cache is a junction from `%LOCALAPPDATA%\JianyingPro\User Data\Cache` to `D:\Caches\R1my\jianying-media-cache`. Two resource packages updated at 21:10 contain readable Lua/GLSL. Their Chinese titles are absent from the package manifests; associating A with `光谱扫描` and B with `彩色像素` remains an inference from their parameters and rendering code, not a verified title-to-ID mapping.

**Implementation status:** the user rejected the overbright additive-only revision (`e280851`) and requested both complete pixel/scanning/light pipelines. The active path now reimplements the two audited postprocessing chains, with an independently runnable Depth Anything V2 Small model chosen by the user. This is a source reconstruction, not the original Jianying engine or a claim of identical output. Model, frame-rate, render-target adaptation and static-input differences are stated below.

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
- `Services/RefreshDepthEstimator.cs`: pinned local ONNX model, normalization and CPU inference.
- `Services/JianyingRefreshRenderer.cs`: fixed-step A-then-B stack and full-resolution correction.
- `Services/RefreshSpectrumEffect.cs`: ASCII/depth scan, rain, Gaussian and HSL chain A.
- `Services/RefreshPixelScanEffect.cs`: Sobel/history, square raster, Gaussian and HSL chain B.
- `Services/ReferenceEffectMath.cs`: bilinear textures, HSL, LUT and Gaussian kernels.
- `Assets/RefreshReference/`: compressed atlas/colour-bar/LUT textures and model provenance.
- `Services/IRefreshRenderer.cs`, `Overlay/RefreshPlayback.cs`: bounded worker and cancellation.
- `Overlay/RadialMenu.xaml.cs`: final-composite image, cover handoff and shared close budget.
- `Services/LauncherService.cs`: trigger, hold-time colour grading and safe recovery.
- `Tools/ReferenceRefreshCheck/`: reconstruction checks. `Build.ps1 -Task VerifyRefresh` also
  retains the legacy Analyzer/Bloom/Outline/Renderer suites as regressions for archived code.