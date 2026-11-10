using System;
using System.Numerics;

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
        private double _historyTime = double.NegativeInfinity;

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
            _squareColumns = (width + _mosaicSize - 1) / _mosaicSize;
            _squareRows = (height + _mosaicSize - 1) / _mosaicSize;
            _squareSamples = new Vector3[checked(_squareColumns * _squareRows)];
            _squareGain = new float[checked(_mosaicSize * _mosaicSize)];
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
            float t = Progress(seconds);

            for (int i = 0; i < _magnitude.Length; i++)
                _magnitude[i] = input.Pixels[i].Length();

            for (int y = 0; y < _height; y++)
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
                    Vector3 band = Green * (m3 - m2) + Cyan * (m2 - m1) + Vector3.One * (m1 - m0);
                    Vector3 accumulated = (_history.Pixels[i] * 0.8f + band * (edge * 0.2f)) * 1.01f
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

            float t = Progress(seconds);
            float filter = FilterStrength * Math.Clamp(backgroundFade, 0f, 1f);
            for (int y = 0; y < _height; y++)
            {
                float v = (y + 0.5f) / _height;
                for (int x = 0; x < _width; x++)
                {
                    int i = y * _width + x;
                    Vector3 original = input.Pixels[i];
                    Vector3 light = _sharp.Pixels[i] * CoreGain
                        + _blur.Sample((x + 0.5f) / _width, v) * GlowGain;
                    // Preserve the original shader's HSL operations, including values above one
                    // before the final UNorm clamp. Mode 26 transfers original hue to the light.
                    Vector3 lightHsl = ReferenceEffectMath.RgbToHsl(light);
                    lightHsl.Y *= ColourSaturation;
                    light = ReferenceEffectMath.HslToRgb(lightHsl);
                    lightHsl = ReferenceEffectMath.RgbToHsl(light);
                    lightHsl.X = ReferenceEffectMath.RgbToHsl(original).X;
                    light = ReferenceEffectMath.HslToRgb(lightHsl);

                    Vector3 graded = Vector3.Lerp(original, ReferenceEffectMath.Lut(_lut, original), filter);
                    float mask = ReferenceEffectMath.SmoothStep(t - ScanWidth, t + ScanWidth,
                        depth[i] * 0.5f + ScanWidth);
                    Vector3 background = Vector3.Lerp(graded, original, 1f - mask);
                    output.Pixels[i] = Vector3.Clamp(background + light, Vector3.Zero, Vector3.One);
                }
            }
        }

        public void Reset()
        {
            Array.Clear(_history.Pixels);
            _historyTime = double.NegativeInfinity;
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
                int glY = _height - y - 1;
                int cellRow = glY / _mosaicSize * _squareColumns;
                int gainRow = glY % _mosaicSize * _mosaicSize;
                for (int x = 0; x < _width; x++)
                {
                    Vector3 sample = _squareSamples[cellRow + x / _mosaicSize];
                    float gain = _squareGain[gainRow + x % _mosaicSize];
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

        private static ReferenceTexture Texture(int width, int height)
            => new(width, height, new Vector3[checked(width * height)]);
    }
}
