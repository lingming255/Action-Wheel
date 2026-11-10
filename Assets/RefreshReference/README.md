# Local refresh reference inputs

The compressed RGB textures were converted losslessly from the user-selected local Jianying
cache packages for this local reconstruction. Each gzip expands to little-endian int32 width,
int32 height, then top-down RGB bytes. Lua/GLSL source is not copied into the application.

- atlas.rgb.gz, colorbar.rgb.gz, spectrum-lut.rgb.gz: package A,
  7399492904915389696/5373a80f847b81bdfa9eba25aa6aab49/AmazingFeature/image.
- pixel-lut.rgb.gz: package B,
  7399495216064892200/8073d46c5c3b46cee867e6a3c0f86154/AmazingFeature/image/filter_1.png.
- depth-model.json: independent Depth Anything V2 Small ONNX model provenance, pinned revision,
  SHA-256, preprocessing and actual local inference validation. This model replaces Jianying's
  ByteNN model at the user's explicit request; the depth results are not identical.

The 27.26 MB model is intentionally outside Git, at
%LOCALAPPDATA%/ActionWheel/Models/depth-anything-v2-small-quantized.onnx.
Install that exact model from the manifest before running. The application verifies SHA-256;
it does not download models or substitute a visual heuristic. The model's Apache 2.0 licence
is included here. ONNX Runtime 1.23.2 is the CPU inference dependency.

The host processes a captured still with one depth inference. It does not reproduce Jianying's
live-video DIS flow/depth accumulation. Both postprocessing chains advance together at 30 Hz;
that update rate and A-then-B stack order are explicit host choices, since the cached resources
do not determine the user's project frame rate or stack evaluation order. The source defaults
and approximately three-second scan cycle are retained. The work canvas is capped at a longest
side of 1280 pixels, preserving aspect ratio (raised from 640 for character/edge clarity);
the original packages use nominal 720x1280 RTs,
and their engine's automatic adaptation rules are not documented. The final correction is
upsampled over the original full-resolution capture, leaving unchanged pixels transparent.

Per-gesture preparation caches immutable character geometry/pigment/rain/source HSL. B caches
colour work only for exactly unchanged RGB; Gaussian plans reuse sampling coordinates while
retaining the audited weights. Native correction encoding uses reusable maps/buffers and SIMD.
These CPU optimizations retain the depth bands, defaults and 30 Hz history; display output is
still limited by render and upload time. Memory tradeoffs and measured timings are in
`Doc/refresh-effect.md`.

Legacy Wheel particles, radial easing, irregular wavefront, outlines and exposure correction
are retained in source/history for comparison but are not called by this playback path.
