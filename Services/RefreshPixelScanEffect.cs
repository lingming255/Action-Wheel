using System;
using System.Numerics;
using System.Threading.Tasks;

namespace Action_Wheel.Services
{
    /// <summary>
    /// The square-pixel depth-scan pass chain reconstructed from the local reference effect.
    /// The owner advances the complete effect stack at a fixed 30 Hz in reference time; rendering
    /// a visible frame does not advance history and therefore cannot change the trail on slow CPUs.
    /// Textures use top-down RGB storage. Shader UVs are converted explicitly at their boundaries.
    /// </summary>
    internal sealed class RefreshPixelScanEffect
    {
        private const float ScanWidth = 0.09f;
        private const float ScanSpeed = (0.5f + 0.33329999446868896f * 1.5f) * 3.333f;
        private const float EdgeThreshold = 0.69999998807907104f * 0.95f;
        private const float MosaicFraction = 0.27000001072883606f * 0.03f + 0.01f;
        private const float BlurSpacing = 0.15000000596046448f * 5f;
        private const float FilterStrength = 0.3f;
        private const float CoreGain = 0.40000000596046448f * 10f;
        private const float GlowGain = 0.40000000596046448f * 5f;
        private const float ColourSaturation = 0.69999998807907104f;
        private static readonly float[] NearWeights = [1f, 0.980198f, 0.923116f, 0.835270f, 0.726149f];
        private static readonly float[] FarWeights = [1f, 0.980198f, 0.923116f, 0.835270f, 0.726149f, 0.606530f];
        private static readonly Vector3 Green = new(0.3f, 1f, 0f);
        private static readonly Vector3 Cyan = new(0f, 1f, 1f);

        private readonly int _width, _height, _mosaicSize;
        private readonly ReferenceTexture _lut;
        private readonly ReferenceTexture _history;
        private readonly ReferenceTexture _sharp;
        private readonly ReferenceTexture _blur;
        private readonly ReferenceTexture _blurAux;
        private readonly float[] _magnitude;
        private readonly Vector3[] _squareSamples;
        private readonly float[] _squareGain;
        private readonly int _squareColumns, _squareRows;
        private readonly int[] _squareCellX, _squareGainX, _squareCellY, _squareGainY;
        private readonly int[] _blurX0, _blurX1, _blurY0, _blurY1;
        private readonly float[] _blurXWeight, _blurYWeight;
        private readonly SourceCache[] _sourceCache;
        private readonly int _bandCount;
        private readonly ParallelOptions _parallelOptions;
        private readonly Action<int> _magnitudeBand, _historyBand, _compositeBand;
        private ReferenceTexture? _activeInput, _activeOutput;
        private float[]? _activeDepth;
        private float _activeProgress, _activeFilter;
        private double _historyTime = double.NegativeInfinity;

        private struct SourceCache
        {
            public Vector3 Source, Lut;
            public float Hue;
            public byte Valid;
        }

        public RefreshPixelScanEffect(int width, int height, ReferenceTexture lut)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width));
            ArgumentNullException.ThrowIfNull(lut);
            _width = width;
            _height = height;
            _lut = lut;
            _mosaicSize = Math.Max(2, 2 * (int)MathF.Floor(MosaicFraction * Math.Min(width, height) / 2f));
            _history = Texture(width, height);
            _sharp = Texture(width, height);
            _blur = Texture(Math.Max(1, (int)MathF.Round(width * 0.25f)),
                Math.Max(1, (int)MathF.Round(height * 0.25f)));
            _blurAux = Texture(Math.Max(1, (int)MathF.Round(width * 0.2f)),
                Math.Max(1, (int)MathF.Round(height * 0.2f)));
            _magnitude = new float[checked(width * height)];
            _sourceCache = new SourceCache[checked(width * height)];
            _squareColumns = (width + _mosaicSize - 1) / _mosaicSize;
            _squareRows = (height + _mosaicSize - 1) / _mosaicSize;
            _squareSamples = new Vector3[checked(_squareColumns * _squareRows)];
            _squareGain = new float[checked(_mosaicSize * _mosaicSize)];
            _squareCellX = new int[width]; _squareGainX = new int[width];
            _squareCellY = new int[height]; _squareGainY = new int[height];
            for (int x = 0; x < width; x++)
            {
                _squareCellX[x] = x / _mosaicSize;
                _squareGainX[x] = x % _mosaicSize;
            }
            for (int y = 0; y < height; y++)
            {
                int glY = height - y - 1;
                _squareCellY[y] = glY / _mosaicSize * _squareColumns;
                _squareGainY[y] = glY % _mosaicSize * _mosaicSize;
            }
            (_blurX0, _blurX1, _blurXWeight) = SampleMap(width, _blur.Width);
            (_blurY0, _blurY1, _blurYWeight) = SampleMap(height, _blur.Height);
            _bandCount = (long)width * height >= 128 * 1024 ? Math.Min(4, Environment.ProcessorCount) : 1;
            _parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = _bandCount };
            _magnitudeBand = PrepareMagnitudeBand;
            _historyBand = AdvanceHistoryBand;
            _compositeBand = RenderCompositeBand;
            float half = _mosaicSize * 0.5f;
            for (int y = 0; y < _mosaicSize; y++)
            for (int x = 0; x < _mosaicSize; x++)
            {
                float dx = MathF.Abs(half - (x + 0.5f) * 1.2f) - half;
                float dy = MathF.Abs(half - (y + 0.5f) * 1.2f) - half;
                float ox = MathF.Max(dx, 0f), oy = MathF.Max(dy, 0f);
                float distance = MathF.Sqrt(ox * ox + oy * oy) + MathF.Min(MathF.Max(dx, dy), 0f);
                _squareGain[y * _mosaicSize + x] = MathF.Max(0f, 1f - distance);
            }
        }

        /// <summary>
        /// Updates one reference frame of history. The caller supplies every intervening frame
        /// in increasing shader time, after rendering the preceding effect for that same time.
        /// Repeated timestamps are ignored. Reset clears a previous gesture or a backward seek.
        /// </summary>
        public void AdvanceHistory(ReferenceTexture input, float[] depth, double seconds)
        {
            Validate(input, depth);
            if (seconds < _historyTime) Reset();
            if (seconds == _historyTime) return;
            _historyTime = seconds;
            _activeInput = input;
            _activeDepth = depth;
            _activeProgress = Progress(seconds);
            try
            {
                // Finish all magnitude rows before any band reads its Sobel neighbours.
                RunBands(_magnitudeBand);
                RunBands(_historyBand);
            }
            finally { _activeInput = null; _activeDepth = null; }
        }

        private void PrepareMagnitudeBand(int band)
        {
            Vector3[] input = _activeInput!.Pixels;
            int begin = _height * band / _bandCount * _width;
            int end = _height * (band + 1) / _bandCount * _width;
            for (int i = begin; i < end; i++) _magnitude[i] = input[i].Length();
        }

        private void AdvanceHistoryBand(int band)
        {
            float[] depth = _activeDepth!;
            float t = _activeProgress;
            int firstY = _height * band / _bandCount, lastY = _height * (band + 1) / _bandCount;
            for (int y = firstY; y < lastY; y++)
            {
                int up = Math.Max(0, y - 1) * _width;
                int row = y * _width;
                int down = Math.Min(_height - 1, y + 1) * _width;
                for (int x = 0; x < _width; x++)
                {
                    int left = Math.Max(0, x - 1), right = Math.Min(_width - 1, x + 1);
                    // GL +Y is the physical top of our top-down input. The signs are material:
                    // the reference accepts a signed gradient near +1, not abs(gradient).
                    float tl = _magnitude[up + left], tc = _magnitude[up + x], tr = _magnitude[up + right];
                    float ml = _magnitude[row + left], mr = _magnitude[row + right];
                    float bl = _magnitude[down + left], bc = _magnitude[down + x], br = _magnitude[down + right];
                    float gx = tl + 2f * ml + bl - tr - 2f * mr - br;
                    float gy = bl + 2f * bc + br - tl - 2f * tc - tr;
                    float edge = (MathF.Abs(1f - gx) <= EdgeThreshold ? 0.5f : 0f)
                        + (MathF.Abs(1f - gy) <= EdgeThreshold ? 0.5f : 0f);
                    int i = row + x;
                    float h = depth[i] * 0.5f + ScanWidth;
                    float m0 = Step(t + ScanWidth * 0.5f, h);
                    float m1 = Step(t + ScanWidth / 3f, h);
                    float m2 = Step(t, h);
                    float m3 = Step(t - ScanWidth * 0.5f, h);
                    Vector3 colourBand = Green * (m3 - m2) + Cyan * (m2 - m1) + Vector3.One * (m1 - m0);
                    Vector3 accumulated = (_history.Pixels[i] * 0.8f + colourBand * (edge * 0.2f)) * 1.01f
                        - new Vector3(0.01f);
                    _history.Pixels[i] = Vector3.Clamp(accumulated, Vector3.Zero, Vector3.One);
                }
            }
        }

        /// <summary>
        /// Renders the current history without changing it. backgroundFade scales only the LUT
        /// transition; use one for the reference default. No additional exposure or outlines apply.
        /// </summary>
        public void Render(ReferenceTexture input, float[] depth, double seconds,
            float backgroundFade, ReferenceTexture output)
        {
            Validate(input, depth);
            ArgumentNullException.ThrowIfNull(output);
            if (output.Width != _width || output.Height != _height)
                throw new ArgumentException("The pixel-scan output must match its prepared dimensions.", nameof(output));

            RasterizeSquares();
            DownsampleSharp();
            // The two RT sizes alternate. The reference's vertical offsets are expressed against
            // blurRT, even while sampling blurAuxRT, so convert those offsets to source pixels.
            ReferenceEffectMath.Gaussian(_blur, _blurAux, 1f, true, 4, NearWeights);
            float verticalScale = _blurAux.Height / (float)_blur.Height;
            ReferenceEffectMath.Gaussian(_blurAux, _blur, verticalScale, false, 4, NearWeights);
            ReferenceEffectMath.Gaussian(_blur, _blurAux, BlurSpacing, true, 5, FarWeights);
            ReferenceEffectMath.Gaussian(_blurAux, _blur, BlurSpacing * verticalScale, false, 5, FarWeights);

            _activeInput = input;
            _activeOutput = output;
            _activeDepth = depth;
            _activeProgress = Progress(seconds);
            _activeFilter = FilterStrength * Math.Clamp(backgroundFade, 0f, 1f);
            try { RunBands(_compositeBand); }
            finally { _activeInput = null; _activeOutput = null; _activeDepth = null; }
        }

        private void RenderCompositeBand(int band)
        {
            Vector3[] input = _activeInput!.Pixels, output = _activeOutput!.Pixels;
            float[] depth = _activeDepth!;
            float t = _activeProgress, filter = _activeFilter;
            int firstY = _height * band / _bandCount, lastY = _height * (band + 1) / _bandCount;
            for (int y = firstY; y < lastY; y++)
            {
                int top = _blurY0[y] * _blur.Width, bottom = _blurY1[y] * _blur.Width;
                float vertical = _blurYWeight[y];
                for (int x = 0; x < _width; x++)
                {
                    int i = y * _width + x;
                    Vector3 original = input[i];
                    ref SourceCache cached = ref _sourceCache[i];
                    if (original != cached.Source) { cached.Source = original; cached.Valid = 0; }
                    int left = _blurX0[x], right = _blurX1[x];
                    float horizontal = _blurXWeight[x];
                    Vector3 blurred = Vector3.Lerp(
                        Vector3.Lerp(_blur.Pixels[top + left], _blur.Pixels[top + right], horizontal),
                        Vector3.Lerp(_blur.Pixels[bottom + left], _blur.Pixels[bottom + right], horizontal), vertical);
                    Vector3 light = _sharp.Pixels[i] * CoreGain
                        + blurred * GlowGain;
                    // Preserve the original shader's HSL operations, including values above one
                    // before the final UNorm clamp. Mode 26 transfers original hue to the light.
                    if (light != Vector3.Zero)
                    {
                        Vector3 lightHsl = ReferenceEffectMath.RgbToHsl(light);
                        lightHsl.Y *= ColourSaturation;
                        // The intervening HSL->RGB->HSL is an inverse pair. Remove that pair
                        // for finite saturation, including the shader's negative HDR saturation.
                        // Near lightness one, float cancellation in that inverse pair is
                        // material. Retain the original arithmetic there and at its singularity.
                        if (!float.IsFinite(lightHsl.Y) || MathF.Abs(lightHsl.Z - 1f) < 0.01f)
                            lightHsl = ReferenceEffectMath.RgbToHsl(ReferenceEffectMath.HslToRgb(lightHsl));
                        if ((cached.Valid & 1) == 0)
                        {
                            cached.Hue = ReferenceEffectMath.RgbToHsl(original).X;
                            cached.Valid |= 1;
                        }
                        lightHsl.X = cached.Hue;
                        light = ReferenceEffectMath.HslToRgb(lightHsl);
                    }

                    Vector3 background = original;
                    if (filter > 0f)
                    {
                        float mask = ReferenceEffectMath.SmoothStep(t - ScanWidth, t + ScanWidth,
                            depth[i] * 0.5f + ScanWidth);
                        if (mask > 0f)
                        {
                            if ((cached.Valid & 2) == 0)
                            {
                                cached.Lut = ReferenceEffectMath.Lut(_lut, original);
                                cached.Valid |= 2;
                            }
                            Vector3 graded = Vector3.Lerp(original, cached.Lut, filter);
                            background = Vector3.Lerp(graded, original, 1f - mask);
                        }
                    }
                    output[i] = Vector3.Clamp(background + light, Vector3.Zero, Vector3.One);
                }
            }
        }

        public void Reset()
        {
            Array.Clear(_history.Pixels);
            _historyTime = double.NegativeInfinity;
        }

        private void RunBands(Action<int> action)
        {
            // Gaussian's own bounded bands execute before this phase, never inside these bands.
            if (_bandCount == 1) action(0);
            else Parallel.For(0, _bandCount, _parallelOptions, action);
        }

        private void RasterizeSquares()
        {
            // Every cell reads one identical bilinear corner sample. Cache that value once and
            // reuse the fixed SDF footprint instead of repeating sampling/square roots per pixel.
            for (int y = 0; y < _squareRows; y++)
            for (int x = 0; x < _squareColumns; x++)
                _squareSamples[y * _squareColumns + x] = _history.SampleGl(
                    x * _mosaicSize / (float)_width, y * _mosaicSize / (float)_height);
            for (int y = 0; y < _height; y++)
            {
                // Shader UV is bottom-up. Its floor-anchored cells begin at the physical bottom,
                // including when the capture height is not an exact multiple of the cell size.
                int cellRow = _squareCellY[y];
                int gainRow = _squareGainY[y];
                for (int x = 0; x < _width; x++)
                {
                    Vector3 sample = _squareSamples[cellRow + _squareCellX[x]];
                    float gain = _squareGain[gainRow + _squareGainX[x]];
                    _sharp.Pixels[y * _width + x] = Vector3.Clamp(sample * gain,
                        Vector3.Zero, Vector3.One);
                }
            }
        }

        private void DownsampleSharp()
        {
            float dx = 1f / _width, dy = 1f / _height;
            for (int y = 0; y < _blur.Height; y++)
            {
                float v = 1f - (y + 0.5f) / _blur.Height;
                for (int x = 0; x < _blur.Width; x++)
                {
                    float u = (x + 0.5f) / _blur.Width;
                    _blur.Pixels[y * _blur.Width + x] = (_sharp.SampleGl(u, v)
                        + _sharp.SampleGl(u, v + dy) + _sharp.SampleGl(u + dx, v)
                        + _sharp.SampleGl(u + dx, v + dy)) * 0.25f;
                }
            }
        }

        private void Validate(ReferenceTexture input, float[] depth)
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(depth);
            if (input.Width != _width || input.Height != _height || depth.Length != _width * _height)
                throw new ArgumentException("Pixel-scan input and top-down depth must match its prepared dimensions.");
        }

        private static float Progress(double seconds)
        {
            double phase = seconds * ScanSpeed;
            return (float)((phase - Math.Floor(phase / 10.0) * 10.0) / 10.0);
        }

        private static float Step(float edge, float value) => value >= edge ? 1f : 0f;

        private static (int[] First, int[] Second, float[] Weight) SampleMap(int pixels, int samples)
        {
            var first = new int[pixels]; var second = new int[pixels]; var weight = new float[pixels];
            for (int pixel = 0; pixel < pixels; pixel++)
            {
                // Preserve Sample's operation order, so the cached coordinates are identical.
                float coordinate = Math.Clamp((pixel + 0.5f) / pixels * samples - 0.5f, 0, samples - 1);
                int lower = (int)coordinate;
                first[pixel] = lower; second[pixel] = Math.Min(lower + 1, samples - 1);
                weight[pixel] = coordinate - lower;
            }
            return (first, second, weight);
        }

        private static ReferenceTexture Texture(int width, int height)
            => new(width, height, new Vector3[checked(width * height)]);
    }
}
