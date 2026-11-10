using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Action_Wheel.Services
{
    /// <summary>
    /// CPU reimplementation of the reference package A's depth-gated ASCII rain, Gaussian
    /// spill and source-image HSL composition. It has no Wheel particle, outline, radial
    /// wavefront or exposure stage. A single playback worker owns each instance.
    /// </summary>
    internal sealed class RefreshSpectrumEffect
    {
        private const float ScanSpeed = (0.5f + 0.3333300054073334f * 1.5f) * 3.333f;
        private const float ScanWidth = 0.3330000042915344f * 0.55f;
        private const float Brightness = 0.614f * 5.0f * 1.4f;
        private const float GlowGain = 0.642f * 3.0f * 1.4f;
        private const float FilterAlpha = 0.0f;
        private const float MeshRows = 82.0f;
        private const float CurveRate = 0.45f;
        private const float LineCutRate = 0.4f;
        private static readonly float[] GaussianWeights = [0.208046f, 0.203926f, 0.192051f];

        private readonly int _width;
        private readonly int _height;
        private readonly int _bandCount;
        private readonly ReferenceTexture _atlas;
        private readonly ReferenceTexture _lut;
        private readonly ReferenceTexture _sharp;
        private readonly ReferenceTexture _blur;
        private readonly ReferenceTexture _aux;
        private readonly Vector3 _rainColour;
        private readonly float _atlasCellWidth;
        private readonly float _atlasCellHeight;
        private readonly ParallelOptions _parallelOptions;
        private readonly Action<int> _sharpBand;
        private readonly Action<int> _preparedSharpBand;
        private readonly Action<int> _prepareBand;
        private readonly Action<int> _compositeBand;
        private readonly int[] _blurLeft, _blurRight, _blurTop, _blurBottom;
        private readonly float[] _blurHorizontal, _blurVertical;
        private readonly bool[] _bandHasLight;
        private Vector3[]? _preparedPigment, _preparedSourceHsl;
        private Vector2[]? _preparedRain;
        private ReferenceTexture? _preparedInput;
        private float[]? _preparedDepth;
        private ReferenceTexture? _activeInput;
        private ReferenceTexture? _activeOutput;
        private float[]? _activeDepth;
        private float[]? _activeEnvelope;
        private CancellationToken _cancellation;
        private float _seconds;
        private float _filterAmount;

        public RefreshSpectrumEffect(int width, int height, ReferenceTexture atlas,
            ReferenceTexture colourBar, ReferenceTexture lut)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width));
            ArgumentNullException.ThrowIfNull(atlas);
            ArgumentNullException.ThrowIfNull(colourBar);
            ArgumentNullException.ThrowIfNull(lut);
            _width = width;
            _height = height;
            _atlas = atlas;
            _lut = lut;
            _atlasCellWidth = atlas.Width / 8.0f;
            _atlasCellHeight = atlas.Height / 3.0f;
            _rainColour = colourBar.SampleGl(0.58f, 0.5f);
            _sharp = new ReferenceTexture(width, height, new Vector3[checked(width * height)]);
            int blurWidth = Math.Max(1, (width + 3) / 4);
            int blurHeight = Math.Max(1, (height + 3) / 4);
            _blur = new ReferenceTexture(blurWidth, blurHeight,
                new Vector3[checked(blurWidth * blurHeight)]);
            _aux = new ReferenceTexture(blurWidth, blurHeight,
                new Vector3[checked(blurWidth * blurHeight)]);
            _bandCount = (long)width * height >= 128 * 1024
                ? Math.Min(4, Environment.ProcessorCount) : 1;
            _parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = _bandCount };
            _sharpBand = RenderSharpBand;
            _preparedSharpBand = RenderPreparedSharpBand;
            _prepareBand = PrepareBand;
            _compositeBand = RenderCompositeBand;
            _bandHasLight = new bool[_bandCount];
            _blurLeft = new int[width]; _blurRight = new int[width];
            _blurHorizontal = new float[width];
            _blurTop = new int[height]; _blurBottom = new int[height];
            _blurVertical = new float[height];
            for (int x = 0; x < width; x++)
            {
                float coordinate = Math.Clamp((x + 0.5f) / width * blurWidth - 0.5f, 0, blurWidth - 1);
                int first = (int)coordinate;
                _blurLeft[x] = first; _blurRight[x] = Math.Min(first + 1, blurWidth - 1);
                _blurHorizontal[x] = coordinate - first;
            }
            for (int y = 0; y < height; y++)
            {
                // Keep the same two flips as SampleGl(u, 1-(y+.5)/height).
                float v = 1.0f - (y + 0.5f) / height;
                float coordinate = Math.Clamp((1 - v) * blurHeight - 0.5f, 0, blurHeight - 1);
                int first = (int)coordinate;
                _blurTop[y] = first * blurWidth;
                _blurBottom[y] = Math.Min(first + 1, blurHeight - 1) * blurWidth;
                _blurVertical[y] = coordinate - first;
            }
        }

        /// <summary>
        /// Prepares geometry and colour that stay constant for this gesture. The caller must
        /// leave input/depth unchanged until preparing again. Render without this explicit
        /// opt-in retains the original mutable-input behaviour. Retained data uses 32 bytes
        /// per work pixel; scratch sharp/blur targets and small sampling maps are additional.
        /// </summary>
        public void Prepare(ReferenceTexture input, float[] depth)
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(depth);
            if (input.Width != _width || input.Height != _height || depth.Length != _width * _height)
                throw new ArgumentException("Spectrum preparation dimensions must match.");
            int count = checked(_width * _height);
            _preparedPigment ??= new Vector3[count];
            _preparedSourceHsl ??= new Vector3[count];
            _preparedRain ??= new Vector2[count];
            _preparedInput = null; _preparedDepth = null;
            _activeInput = input; _activeDepth = depth;
            try
            {
                RunBands(_prepareBand);
                // The last two blur passes repeat the 5.25-texel plans. Prepare each unique
                // mapping here so the first visible light frame only performs the actual blur.
                ReferenceEffectMath.PrepareGaussian(_sharp, _aux,
                    (float)_sharp.Width / _blur.Width, true, 2);
                ReferenceEffectMath.PrepareGaussian(_aux, _blur, 1.0f, false, 2);
                ReferenceEffectMath.PrepareGaussian(_blur, _aux, 5.25f, true, 2);
                ReferenceEffectMath.PrepareGaussian(_aux, _blur, 5.25f, false, 2);
                _preparedInput = input; _preparedDepth = depth;
            }
            finally { _activeInput = null; _activeDepth = null; }
        }

        private void PrepareBand(int band)
        {
            ReferenceTexture input = _activeInput!;
            float[] depth = _activeDepth!;
            Vector3[] pigments = _preparedPigment!, sourceHsl = _preparedSourceHsl!;
            Vector2[] rain = _preparedRain!;
            int firstY = _height * band / _bandCount, lastY = _height * (band + 1) / _bandCount;
            for (int y = firstY; y < lastY; y++)
            {
                float v = 1.0f - (y + 0.5f) / _height;
                for (int x = 0; x < _width; x++)
                {
                    int pixel = y * _width + x;
                    SampleGeometry(input, x, v * _height, depth[pixel],
                        out pigments[pixel], out float phase, out float columnSpeed);
                    rain[pixel] = new Vector2(phase, columnSpeed);
                    sourceHsl[pixel] = ReferenceEffectMath.RgbToHsl(input.Pixels[pixel]);
                }
            }
        }

        /// <summary>
        /// Renders shader time in seconds, independently of the host's playback envelope.
        /// Depth is normalized and already aligned with top-down input pixels. The original
        /// depth sampler's vertical flip is accounted for by that alignment, not applied twice.
        /// </summary>
        public void Render(ReferenceTexture input, float[] depth, double seconds,
            float backgroundFade, ReferenceTexture output)
            => RenderCore(input, depth, seconds, backgroundFade, output, null, default);

        /// <summary>
        /// Reuses the reference atlas, rain, Gaussian and HSL stages with a host-supplied
        /// light envelope. Raw depth still controls glyph geometry, not the scan timing.
        /// The caller owns a finite, normalized envelope matching the work texture.
        /// </summary>
        internal void RenderWithEnvelope(ReferenceTexture input, float[] depth, double seconds,
            float[] envelope, ReferenceTexture output, CancellationToken cancellation = default)
        {
            ArgumentNullException.ThrowIfNull(envelope);
            if (envelope.Length != checked(_width * _height))
                throw new ArgumentException("Spectrum envelope dimensions must match.", nameof(envelope));
            RenderCore(input, depth, seconds, 1, output, envelope, cancellation);
        }

        private void RenderCore(ReferenceTexture input, float[] depth, double seconds,
            float backgroundFade, ReferenceTexture output, float[]? envelope, CancellationToken cancellation)
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(depth);
            ArgumentNullException.ThrowIfNull(output);
            if (input.Width != _width || input.Height != _height
                || output.Width != _width || output.Height != _height
                || depth.Length != checked(_width * _height))
                throw new ArgumentException("Spectrum input, depth and output dimensions must match.");
            cancellation.ThrowIfCancellationRequested();

            _activeInput = input;
            _activeOutput = output;
            _activeDepth = depth;
            _activeEnvelope = envelope;
            _cancellation = cancellation;
            _parallelOptions.CancellationToken = cancellation;
            _seconds = (float)seconds;
            // Package A's extra.json defaults background animation to zero. Keep the host's
            // end fade in the formula without introducing an extra background dimming effect.
            _filterAmount = FilterAlpha * Math.Clamp(backgroundFade, 0.0f, 1.0f);
            try
            {
                bool prepared = ReferenceEquals(input, _preparedInput) && ReferenceEquals(depth, _preparedDepth);
                RunBands(prepared ? _preparedSharpBand : _sharpBand);

                bool anyLight = false;
                for (int band = 0; band < _bandHasLight.Length; band++) anyLight |= _bandHasLight[band];

                // The first horizontal pass reads full-size sharp texels with an offset of
                // one QUARTER-SIZE blur texel. Remaining passes read quarter-size buffers.
                if (anyLight)
                {
                    cancellation.ThrowIfCancellationRequested();
                    ReferenceEffectMath.Gaussian(_sharp, _aux,
                        (float)_sharp.Width / _blur.Width, true, 2, GaussianWeights);
                    cancellation.ThrowIfCancellationRequested();
                    ReferenceEffectMath.Gaussian(_aux, _blur, 1.0f, false, 2, GaussianWeights);
                    cancellation.ThrowIfCancellationRequested();
                    ReferenceEffectMath.Gaussian(_blur, _aux, 5.25f, true, 2, GaussianWeights);
                    cancellation.ThrowIfCancellationRequested();
                    ReferenceEffectMath.Gaussian(_aux, _blur, 5.25f, false, 2, GaussianWeights);
                    cancellation.ThrowIfCancellationRequested();
                    ReferenceEffectMath.Gaussian(_blur, _aux, 5.25f, true, 2, GaussianWeights);
                    cancellation.ThrowIfCancellationRequested();
                    ReferenceEffectMath.Gaussian(_aux, _blur, 5.25f, false, 2, GaussianWeights);
                }
                else _blur.Pixels.AsSpan().Clear();

                cancellation.ThrowIfCancellationRequested();
                RunBands(_compositeBand);
            }
            finally
            {
                _activeInput = null;
                _activeOutput = null;
                _activeDepth = null;
                _activeEnvelope = null;
                _cancellation = default;
                _parallelOptions.CancellationToken = default;
            }
        }

        private void RunBands(Action<int> render)
        {
            if (_bandCount == 1)
                render(0);
            else
                Parallel.For(0, _bandCount, _parallelOptions, render);
        }

        private void RenderSharpBand(int band)
        {
            ReferenceTexture input = _activeInput!;
            float[] depth = _activeDepth!;
            Vector3[] sharp = _sharp.Pixels;
            int firstY = _height * band / _bandCount;
            int lastY = _height * (band + 1) / _bandCount;
            float time = _seconds;
            float scan = Fract(time * ScanSpeed / 10.0f);
            bool anyLight = false;
            for (int y = firstY; y < lastY; y++)
            {
                _cancellation.ThrowIfCancellationRequested();
                float v = 1.0f - (y + 0.5f) / _height;
                float imageY = v * _height;
                int row = y * _width;
                for (int x = 0; x < _width; x++)
                {
                    int pixel = row + x;
                    float height = depth[pixel];
                    float depthBand = DepthBandAt(height, scan, pixel);
                    if (depthBand <= 0.0f)
                    {
                        sharp[pixel] = Vector3.Zero;
                        continue;
                    }

                    SampleGeometry(input, x, imageY, height,
                        out Vector3 pigment, out float phase, out float columnSpeed);
                    float rainPosition = Fract(v + time * columnSpeed * ScanSpeed / 3.0f + phase);
                    float rain = 1.0f / rainPosition - LineCutRate / (rainPosition + 0.001f);
                    Vector3 light = pigment * (Brightness * (_rainColour * rain)) * depthBand;
                    // Intermediate reference render targets are SDR. Preserve their saturation
                    // before blur; a zero glyph at the rain's exact singularity stays unlit.
                    sharp[pixel] = ClampSdr(light);
                    anyLight |= sharp[pixel] != Vector3.Zero;
                }
            }
            _bandHasLight[band] = anyLight;
        }

        private void RenderPreparedSharpBand(int band)
        {
            float[] depth = _activeDepth!;
            Vector3[] sharp = _sharp.Pixels, pigments = _preparedPigment!;
            Vector2[] rainParameters = _preparedRain!;
            float time = _seconds, scan = Fract(time * ScanSpeed / 10.0f);
            int firstY = _height * band / _bandCount, lastY = _height * (band + 1) / _bandCount;
            bool anyLight = false;
            for (int y = firstY; y < lastY; y++)
            {
                _cancellation.ThrowIfCancellationRequested();
                float v = 1.0f - (y + 0.5f) / _height;
                int row = y * _width;
                for (int x = 0; x < _width; x++)
                {
                    int pixel = row + x;
                    float depthBand = DepthBandAt(depth[pixel], scan, pixel);
                    if (depthBand <= 0.0f)
                    {
                        sharp[pixel] = Vector3.Zero;
                        continue;
                    }
                    Vector2 parameters = rainParameters[pixel];
                    float position = Fract(v + time * parameters.Y * ScanSpeed / 3.0f + parameters.X);
                    float rain = 1.0f / position - LineCutRate / (position + 0.001f);
                    Vector3 light = pigments[pixel] * (Brightness * (_rainColour * rain)) * depthBand;
                    sharp[pixel] = ClampSdr(light);
                    anyLight |= sharp[pixel] != Vector3.Zero;
                }
            }
            _bandHasLight[band] = anyLight;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float DepthBandAt(float depth, float scan, int pixel)
        {
            if (_activeEnvelope != null) return _activeEnvelope[pixel];
            float mask = ReferenceEffectMath.SmoothStep(scan, scan + ScanWidth,
                depth * 0.5f + ScanWidth);
            return Math.Clamp(ReferenceEffectMath.SmoothStep(0.0f, 0.5f, mask)
                - ReferenceEffectMath.SmoothStep(0.5f, 1.0f, mask), 0.0f, 1.0f);
        }

        private void SampleGeometry(ReferenceTexture input, int x, float imageY, float depth,
            out Vector3 pigment, out float phase, out float columnSpeed)
        {
            float cellAspect = _atlasCellWidth / _atlasCellHeight;
            float meshCount = MeshRows - depth * MeshRows * CurveRate;
            float cellWidth, cellHeight;
            if (_width > _height)
            {
                cellHeight = _height / meshCount;
                cellWidth = cellHeight * cellAspect;
            }
            else
            {
                cellWidth = _width / meshCount;
                cellHeight = cellWidth / cellAspect;
            }
            float u = (x + 0.5f) / _width;
            float imageX = u * _width;
            float cellX = MathF.Floor(imageX / cellWidth) * cellWidth;
            float cellY = MathF.Floor(imageY / cellHeight) * cellHeight;
            Vector3 source = input.SampleGl((cellX + cellWidth * 0.5f) / _width,
                (cellY + cellHeight * 0.5f) / _height);
            float luminance = Vector3.Dot(source, new Vector3(0.299f, 0.587f, 0.114f));
            float glyph = MathF.Floor(luminance * 24.0f);
            float glyphX = glyph - MathF.Floor(glyph / 8.0f) * 8.0f;
            float glyphY = MathF.Floor(glyph / 8.0f);
            float offsetX = (imageX - cellX) * _atlasCellWidth / cellWidth;
            float offsetY = (imageY - cellY) * _atlasCellHeight / cellHeight;
            Vector3 glyphMask = Vector3.One - _atlas.SampleGl(
                (glyphX * _atlasCellWidth + offsetX) / _atlas.Width,
                (glyphY * _atlasCellHeight + offsetY) / _atlas.Height);
            pigment = glyphMask * source;
            phase = MathF.Sin(cellX * 15.0f);
            columnSpeed = MathF.Cos(cellX * 3.0f) * 0.3f + 0.7f;
        }

        private void RenderCompositeBand(int band)
        {
            ReferenceTexture input = _activeInput!;
            Vector3[] output = _activeOutput!.Pixels;
            Vector3[] sharp = _sharp.Pixels;
            int firstY = _height * band / _bandCount;
            int lastY = _height * (band + 1) / _bandCount;
            float filterAmount = _filterAmount;
            bool prepared = ReferenceEquals(input, _preparedInput) && ReferenceEquals(_activeDepth, _preparedDepth);
            for (int y = firstY; y < lastY; y++)
            {
                _cancellation.ThrowIfCancellationRequested();
                int row = y * _width;
                for (int x = 0; x < _width; x++)
                {
                    int pixel = row + x;
                    Vector3 source = input.Pixels[pixel];
                    Vector3 background = filterAmount > 0.0f
                        ? Vector3.Lerp(source, ReferenceEffectMath.Lut(_lut, source), filterAmount)
                        : source;
                    Vector3 light = sharp[pixel]
                        + GlowGain * SampleBlur(x, y);
                    if (light == Vector3.Zero)
                    {
                        output[pixel] = ClampSdr(background);
                        continue;
                    }

                    // Match the two active blendFunc calls in reference blending.frag:
                    // first original hue, then 70% original saturation. Do not clamp the
                    // sharp+glow sum before HSL: the source shader also converts values > 1.
                    Vector3 sourceHsl = prepared ? _preparedSourceHsl![pixel]
                        : ReferenceEffectMath.RgbToHsl(source);
                    Vector3 lightHsl = ReferenceEffectMath.RgbToHsl(light);
                    if (float.IsFinite(lightHsl.Y) && MathF.Abs(lightHsl.Z - 1.0f) > 0.001f)
                    {
                        // For fixed hue/lightness, HSLToRGB is affine in saturation. The
                        // original hue replacement and 70/30 saturation blend therefore
                        // reduce to this single inverse transform. Keep the literal source
                        // operations near L=1, where its HDR HSL division is ill-conditioned.
                        light = ReferenceEffectMath.HslToRgb(new Vector3(sourceHsl.X,
                            sourceHsl.Y * 0.7f + lightHsl.Y * 0.3f, lightHsl.Z));
                        output[pixel] = ClampSdr(background + light);
                        continue;
                    }
                    light = ReferenceEffectMath.HslToRgb(new Vector3(
                        sourceHsl.X, lightHsl.Y, lightHsl.Z));
                    lightHsl = ReferenceEffectMath.RgbToHsl(light);
                    Vector3 sourceSaturation = ReferenceEffectMath.HslToRgb(new Vector3(
                        lightHsl.X, sourceHsl.Y, lightHsl.Z));
                    light = sourceSaturation * 0.7f + light * 0.3f;
                    output[pixel] = ClampSdr(background + light);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Vector3 SampleBlur(int x, int y)
        {
            Vector3[] pixels = _blur.Pixels;
            int left = _blurLeft[x], right = _blurRight[x];
            int top = _blurTop[y], bottom = _blurBottom[y];
            float horizontal = _blurHorizontal[x];
            return Vector3.Lerp(
                Vector3.Lerp(pixels[top + left], pixels[top + right], horizontal),
                Vector3.Lerp(pixels[bottom + left], pixels[bottom + right], horizontal),
                _blurVertical[y]);
        }

        private static float Fract(float value) => value - MathF.Floor(value);

        private static Vector3 ClampSdr(Vector3 colour)
            => new(ClampChannel(colour.X), ClampChannel(colour.Y), ClampChannel(colour.Z));

        private static float ClampChannel(float channel)
            => !(channel > 0.0f) ? 0.0f : MathF.Min(1.0f, channel);
    }
}
