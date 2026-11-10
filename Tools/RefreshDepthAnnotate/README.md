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

Requires the .NET 10 Windows Desktop SDK and the application's pinned model installed at
`%LOCALAPPDATA%\ActionWheel\Models\depth-anything-v2-small-quantized.onnx`.
