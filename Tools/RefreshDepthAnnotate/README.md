# Annotate the actual refresh depth values

This local Windows helper calls the application's real `RefreshDepthEstimator` and
`UniformBlockRefreshRenderer`. It uses the pinned local model, the same preprocessing,
the same block geometry and the same centre-depth samples. It does not capture the screen
or change the running application.

From the repository root:

```powershell
.\Tools\RefreshDepthAnnotate\Annotate-Depth.ps1 -Image 'D:\path\image.png' -Output 'F:\path\results'
```

The output contains original-image overlays at native and 2x sizes, a coloured 2x number
grid, complete per-block `blocks.csv` (including depth and appearance time), the unrounded
model `depth.json` and dimensions/meaning in `metadata.json`. Image numbers are rounded to
two decimals: 0 is far, 1 is near. The last clipped row/column use full display slots so
their labels fit; the actual sample centres and clipped geometry are retained in the CSV.

Appearance times use two depth fronts moving away from the actual pointer depth towards
far and near. Start is `119 + 1870*abs(depth-originDepth)/max(originDepth,1-originDepth)` ms;
both directions share one depth speed, with all timing 15% shorter than the preceding version.
The model is sampled at the actual pointer pixel for `originDepth`; exported block depths
remain raw cell-centre values. The default simulated mouse
release is the image centre. Pass both `-OriginX 300 -OriginY 200` to
the PowerShell command to choose a physical pixel position; out-of-image coordinates clamp
to the image boundary. `metadata.json` records that origin and the travel-cost rule. Depth
numbers and heatmap colours remain depth alone, independent of the simulated click. Existing
exports retain the timing version recorded when they were generated.

For a heatmap with a fixed 0..1 colour bar and no per-block numbers, reuse that output:

```powershell
python .\Tools\RefreshDepthAnnotate\Plot-Heatmap.py 'F:\path\results'
```

This writes `depth-heatmap.png`, using blue for far and red for near. It preserves native
clipped cell boundaries and uses the existing depth CSV, without running the model again.
The plotting script requires Pillow and NumPy.

Requires the .NET 10 Windows Desktop SDK and the application's pinned model installed at
`%LOCALAPPDATA%\ActionWheel\Models\depth-anything-v2-small-quantized.onnx`.
