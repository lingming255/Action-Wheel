using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Action_Wheel.Services
{
    /// <summary>
    /// Native-resolution, solid colour blocks. Geometry and pigment stay fixed for a gesture;
    /// elapsed time changes only each whole block's opacity and brightness. Bloom is drawn
    /// outside active cores, so a blurred light field never textures their interiors.
    /// One playback worker owns the instance and its reusable buffers.
    /// </summary>
    internal sealed class UniformBlockRefreshRenderer : IRefreshRenderer
    {
        private const float MosaicFraction = 0.0181f;
        private const double BlockLifetimeMs = 600;
        private const float MaximumOpacity = 0.88f;
        private const float BloomGain = 0.8f;
        private static readonly byte[] RequiredAlpha = BuildRequiredAlpha();

        internal readonly record struct Block(int CellLeft, int CellTop, int CellWidth, int CellHeight,
            int Left, int Top, int Width, int Height, Vector3 Colour, float Depth, double StartMs);

        private struct State
        {
            public uint Core;
            public int BackgroundAmount;
            public Vector3 Emission;
        }

        private readonly int _width, _height, _cellSize, _columns;
        private readonly byte[] _capture, _background;
        private readonly Block[] _blocks;
        private readonly RefreshPropagation _propagation;
        private int _originX = -1, _originY = -1;
        private readonly State[] _states;
        private readonly int _bloomStep, _bloomWidth, _bloomHeight, _bandCount;
        private readonly Vector3[] _emission, _horizontal, _near, _far;
        private readonly Vector3[][] _glowRows;
        private readonly int[] _x0, _x1, _y0, _y1;
        private readonly float[] _xWeight, _yWeight;
        private readonly ParallelOptions _parallel;
        private readonly Action<int> _blurBand, _renderBand;
        private Vector3[]? _blurSource, _blurTarget;
        private byte[]? _activeOutput;
        private int _blurRadius;
        private bool _horizontalPass;
        private CancellationToken _cancellation;

        public double DurationMs => 3000;
        public double FrameIntervalMs => 1000.0 / 60;
        // The final bitmap already contains core, background correction and bloom.
        public int BloomWidth => 1;
        public int BloomHeight => 1;
        internal ReadOnlySpan<Block> Blocks => _blocks;

        public UniformBlockRefreshRenderer(int width, int height, byte[] capture, RefreshDepthMap depth,
            int originX, int originY)
        {
            ArgumentNullException.ThrowIfNull(capture);
            ArgumentNullException.ThrowIfNull(depth);
            ArgumentNullException.ThrowIfNull(depth.Values);
            if (width <= 0 || height <= 0 || capture.LongLength != (long)width * height * 4)
                throw new ArgumentException("Block renderer dimensions must match the BGRA capture.");
            if (depth.Width <= 0 || depth.Height <= 0
                || depth.Values.LongLength != (long)depth.Width * depth.Height)
                throw new ArgumentException("Depth dimensions must match its normalized values.", nameof(depth));
            foreach (float value in depth.Values)
                if (!float.IsFinite(value) || value < 0 || value > 1)
                    throw new ArgumentException("Depth values must be finite and normalized.", nameof(depth));

            _width = width; _height = height; _capture = capture;
            _cellSize = Math.Max(4, 2 * (int)MathF.Floor(MosaicFraction * Math.Min(width, height) / 2));
            _columns = (width + _cellSize - 1) / _cellSize;
            int rows = (height + _cellSize - 1) / _cellSize;
            _blocks = new Block[checked(_columns * rows)];
            _states = new State[_blocks.Length];
            var blockDepth = new float[_blocks.Length];
            int margin = Math.Max(1, (int)Math.Round(_cellSize * 0.10));
            for (int row = 0; row < rows; row++)
            for (int column = 0; column < _columns; column++)
            {
                int left = column * _cellSize, top = row * _cellSize;
                int cellWidth = Math.Min(_cellSize, width - left), cellHeight = Math.Min(_cellSize, height - top);
                int insetX = Math.Min(margin, (cellWidth - 1) / 2);
                int insetY = Math.Min(margin, (cellHeight - 1) / 2);
                float d = SampleDepth(depth, (left + cellWidth * .5f) / width,
                    (top + cellHeight * .5f) / height);
                Vector3 colour = SampleColour(left, top, cellWidth, cellHeight);
                // Lift the sampled pigment while retaining its RGB proportions. There is no
                // white centre, thresholded white glare, hue transfer or animated colour field.
                float maximum = Math.Max(colour.X, Math.Max(colour.Y, colour.Z));
                if (maximum > 0) colour *= 1 + .35f * (.92f / maximum - 1);
                blockDepth[row * _columns + column] = d;
                _blocks[row * _columns + column] = new Block(left, top, cellWidth, cellHeight,
                    left + insetX, top + insetY, cellWidth - insetX * 2, cellHeight - insetY * 2,
                    colour, d, 0);
            }
            _propagation = new RefreshPropagation(width, height, _cellSize, blockDepth);
            SetOrigin(originX, originY);

            // Bloom work stays proportional to block count, rather than native monitor pixels.
            _bloomStep = Math.Max(1, _cellSize / 4);
            _bloomWidth = (width + _bloomStep - 1) / _bloomStep;
            _bloomHeight = (height + _bloomStep - 1) / _bloomStep;
            int count = checked(_bloomWidth * _bloomHeight);
            _emission = new Vector3[count]; _horizontal = new Vector3[count];
            _near = new Vector3[count]; _far = new Vector3[count];
            _bandCount = (long)width * height >= 256 * 1024 ? Math.Min(4, Environment.ProcessorCount) : 1;
            _parallel = new ParallelOptions { MaxDegreeOfParallelism = _bandCount };
            _blurBand = BlurBand; _renderBand = RenderBand;
            _glowRows = new Vector3[_bandCount][];
            for (int band = 0; band < _bandCount; band++) _glowRows[band] = new Vector3[_bloomWidth];
            _x0 = new int[width]; _x1 = new int[width]; _xWeight = new float[width];
            _y0 = new int[height]; _y1 = new int[height]; _yWeight = new float[height];
            Map(_x0, _x1, _xWeight, _bloomWidth, _bloomStep);
            Map(_y0, _y1, _yWeight, _bloomHeight, _bloomStep);
            _background = BuildBackground(width, height, capture);
        }

        /// <summary>Retimes the prepared grid at release, before any playback worker starts.</summary>
        internal void SetOrigin(int originX, int originY)
        {
            originX = Math.Clamp(originX, 0, _width - 1);
            originY = Math.Clamp(originY, 0, _height - 1);
            if (originX == _originX && originY == _originY) return;
            ReadOnlySpan<double> arrivals = _propagation.Arrivals(originX, originY);
            for (int index = 0; index < _blocks.Length; index++)
                _blocks[index] = _blocks[index] with { StartMs = arrivals[index] };
            _originX = originX; _originY = originY;
        }

        /// <summary>The same scalar applies to every pixel of a block; age is in milliseconds.</summary>
        internal static float OpacityAt(double ageMs)
            => !double.IsFinite(ageMs) || ageMs <= 0 || ageMs >= BlockLifetimeMs ? 0
                : MaximumOpacity * Smooth(0, 100, ageMs) * (1 - Smooth(220, BlockLifetimeMs, ageMs));

        internal static uint CoreAt(Block block, double elapsedMs)
        {
            float opacity = OpacityAt(elapsedMs - block.StartMs);
            float brightness = .75f + .25f * (opacity / MaximumOpacity);
            int alpha = Math.Clamp((int)MathF.Round(opacity * 255), 0, 255);
            Vector3 colour = block.Colour * (brightness * alpha);
            int r = Math.Clamp((int)MathF.Round(colour.X), 0, alpha);
            int g = Math.Clamp((int)MathF.Round(colour.Y), 0, alpha);
            int b = Math.Clamp((int)MathF.Round(colour.Z), 0, alpha);
            return (uint)(b | (g << 8) | (r << 16) | (alpha << 24));
        }

        // Reveal the ungraded desktop behind the same arriving light pulse. Bloom already uses
        // this block's age, so all three layers propagate from the gesture's physical origin.
        internal static int BackgroundAmountAt(Block block, double elapsedMs)
            => (int)MathF.Round(256 * (1 - Smooth(0, 400, elapsedMs - block.StartMs)));

        public void Render(double elapsedMs, byte[] fullBuffer, byte[] bloomBuffer,
            CancellationToken cancellation = default)
        {
            ArgumentNullException.ThrowIfNull(fullBuffer);
            ArgumentNullException.ThrowIfNull(bloomBuffer);
            if (fullBuffer.Length != _capture.Length || bloomBuffer.Length != 4
                || ReferenceEquals(fullBuffer, _capture) || ReferenceEquals(bloomBuffer, _capture)
                || ReferenceEquals(fullBuffer, bloomBuffer))
                throw new ArgumentException("Block playback requires separate, correctly sized output buffers.");
            cancellation.ThrowIfCancellationRequested();
            bloomBuffer.AsSpan().Clear();
            if (!double.IsFinite(elapsedMs) || elapsedMs < 0 || elapsedMs >= DurationMs)
            {
                fullBuffer.AsSpan().Clear();
                return;
            }

            _cancellation = cancellation;
            _parallel.CancellationToken = cancellation;
            _activeOutput = fullBuffer;
            try
            {
                _emission.AsSpan().Clear();
                bool anyEmission = false;
                for (int index = 0; index < _blocks.Length; index++)
                {
                    if ((index & 63) == 0) cancellation.ThrowIfCancellationRequested();
                    Block block = _blocks[index];
                    float opacity = OpacityAt(elapsedMs - block.StartMs);
                    float brightness = .75f + .25f * (opacity / MaximumOpacity);
                    ref State state = ref _states[index];
                    state.Core = CoreAt(block, elapsedMs);
                    state.BackgroundAmount = BackgroundAmountAt(block, elapsedMs);
                    state.Emission = block.Colour * (opacity * brightness * BloomGain);
                    if (state.Emission == Vector3.Zero) continue;
                    anyEmission = true;
                    StampEmission(block, state.Emission);
                }
                if (anyEmission)
                {
                    Blur(_emission, _horizontal, true, 1);
                    Blur(_horizontal, _near, false, 1);
                    Blur(_near, _horizontal, true, 2);
                    Blur(_horizontal, _far, false, 2);
                }
                else { _near.AsSpan().Clear(); _far.AsSpan().Clear(); }
                RunBands(_renderBand);
            }
            finally
            {
                _activeOutput = null; _blurSource = null; _blurTarget = null;
                _parallel.CancellationToken = default; _cancellation = default;
            }
        }

        private void StampEmission(Block block, Vector3 colour)
        {
            int right = block.Left + block.Width, bottom = block.Top + block.Height;
            int firstX = block.Left / _bloomStep, firstY = block.Top / _bloomStep;
            int lastX = Math.Min(_bloomWidth, (right + _bloomStep - 1) / _bloomStep);
            int lastY = Math.Min(_bloomHeight, (bottom + _bloomStep - 1) / _bloomStep);
            float inverse = 1f / (_bloomStep * _bloomStep);
            for (int y = firstY; y < lastY; y++)
            {
                int overlapY = Math.Min(bottom, (y + 1) * _bloomStep) - Math.Max(block.Top, y * _bloomStep);
                for (int x = firstX; x < lastX; x++)
                {
                    int overlapX = Math.Min(right, (x + 1) * _bloomStep) - Math.Max(block.Left, x * _bloomStep);
                    _emission[y * _bloomWidth + x] += colour * (overlapX * overlapY * inverse);
                }
            }
        }

        private void Blur(Vector3[] source, Vector3[] target, bool horizontal, int radius)
        {
            _blurSource = source; _blurTarget = target; _horizontalPass = horizontal; _blurRadius = radius;
            RunBands(_blurBand);
        }

        private void BlurBand(int band)
        {
            Vector3[] source = _blurSource!, target = _blurTarget!;
            int radius = _blurRadius;
            float inverse = 1f / (radius * 2 + 1);
            if (_horizontalPass)
            {
                int firstY = _bloomHeight * band / _bandCount, lastY = _bloomHeight * (band + 1) / _bandCount;
                for (int y = firstY; y < lastY; y++)
                {
                    _cancellation.ThrowIfCancellationRequested();
                    int row = y * _bloomWidth;
                    Vector3 sum = Vector3.Zero;
                    for (int x = 0; x <= radius && x < _bloomWidth; x++) sum += source[row + x];
                    for (int x = 0; x < _bloomWidth; x++)
                    {
                        target[row + x] = Vector3.Max(Vector3.Zero, sum * inverse);
                        if (x - radius >= 0) sum -= source[row + x - radius];
                        if (x + radius + 1 < _bloomWidth) sum += source[row + x + radius + 1];
                    }
                }
            }
            else
            {
                int firstX = _bloomWidth * band / _bandCount, lastX = _bloomWidth * (band + 1) / _bandCount;
                for (int x = firstX; x < lastX; x++)
                {
                    _cancellation.ThrowIfCancellationRequested();
                    Vector3 sum = Vector3.Zero;
                    for (int y = 0; y <= radius && y < _bloomHeight; y++) sum += source[y * _bloomWidth + x];
                    for (int y = 0; y < _bloomHeight; y++)
                    {
                        target[y * _bloomWidth + x] = Vector3.Max(Vector3.Zero, sum * inverse);
                        if (y - radius >= 0) sum -= source[(y - radius) * _bloomWidth + x];
                        if (y + radius + 1 < _bloomHeight) sum += source[(y + radius + 1) * _bloomWidth + x];
                    }
                }
            }
        }

        private void RunBands(Action<int> action)
        {
            if (_bandCount == 1) action(0);
            else Parallel.For(0, _bandCount, _parallel, action);
        }

        private void RenderBand(int band)
        {
            Span<uint> output = MemoryMarshal.Cast<byte, uint>(_activeOutput!.AsSpan());
            ReadOnlySpan<uint> background = MemoryMarshal.Cast<byte, uint>(_background.AsSpan());
            ReadOnlySpan<uint> capture = MemoryMarshal.Cast<byte, uint>(_capture.AsSpan());
            Vector3[] glowRow = _glowRows[band];
            int firstY = _height * band / _bandCount, lastY = _height * (band + 1) / _bandCount;
            for (int y = firstY; y < lastY; y++)
            {
                _cancellation.ThrowIfCancellationRequested();
                int top = _y0[y] * _bloomWidth, bottom = _y1[y] * _bloomWidth;
                float vertical = _yWeight[y];
                bool anyGlow = false;
                for (int x = 0; x < _bloomWidth; x++)
                {
                    Vector3 near = Vector3.Lerp(_near[top + x], _near[bottom + x], vertical);
                    Vector3 far = Vector3.Lerp(_far[top + x], _far[bottom + x], vertical);
                    Vector3 glow = (near * .55f + far * .4f) * 255;
                    glowRow[x] = glow;
                    anyGlow |= Math.Max(glow.X, Math.Max(glow.Y, glow.Z)) >= .5f;
                }
                int row = y * _width, blockRow = y / _cellSize * _columns;
                for (int column = 0; column < _columns; column++)
                {
                    int index = blockRow + column;
                    Block block = _blocks[index];
                    State state = _states[index];
                    int start = row + block.CellLeft, end = start + block.CellWidth;
                    if (state.BackgroundAmount == 0) output.Slice(start, block.CellWidth).Clear();
                    else if (state.BackgroundAmount == 256) background.Slice(start, block.CellWidth).CopyTo(output.Slice(start, block.CellWidth));
                    else for (int pixel = start; pixel < end; pixel++) output[pixel] = Scale(background[pixel], state.BackgroundAmount);

                    bool coreRow = state.Core != 0 && y >= block.Top && y < block.Top + block.Height;
                    int coreLeft = block.Left, coreRight = coreLeft + block.Width;
                    for (int x = block.CellLeft; x < block.CellLeft + block.CellWidth; x++)
                    {
                        int pixel = row + x;
                        if (coreRow && x >= coreLeft && x < coreRight)
                        {
                            output[pixel] = Over(state.Core, output[pixel]);
                            continue;
                        }
                        if (!anyGlow) continue;
                        Vector3 light = Vector3.Lerp(glowRow[_x0[x]], glowRow[_x1[x]], _xWeight[x]);
                        int red = Math.Clamp((int)(light.X + .5f), 0, 255);
                        int green = Math.Clamp((int)(light.Y + .5f), 0, 255);
                        int blue = Math.Clamp((int)(light.Z + .5f), 0, 255);
                        if ((red | green | blue) != 0) output[pixel] = AddLight(output[pixel], capture[pixel], blue, green, red);
                    }
                }
            }
        }

        private Vector3 SampleColour(int left, int top, int width, int height)
        {
            Vector3 sum = Vector3.Zero;
            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                int px = left + Math.Min(width - 1, (int)((x + .5f) * width / 4));
                int py = top + Math.Min(height - 1, (int)((y + .5f) * height / 4));
                int offset = (py * _width + px) * 4;
                sum += new Vector3(_capture[offset + 2], _capture[offset + 1], _capture[offset]);
            }
            return sum / (16 * 255f);
        }

        private static byte[] BuildBackground(int width, int height, byte[] capture)
        {
            double scale = Math.Min(1, 1280.0 / Math.Max(width, height));
            int workWidth = Math.Max(1, (int)Math.Round(width * scale));
            int workHeight = Math.Max(1, (int)Math.Round(height * scale));
            var source = new ReferenceTexture(workWidth, workHeight);
            var graded = new ReferenceTexture(workWidth, workHeight);
            ReferenceTexture lut = ReferenceEffectMath.LoadTexture("pixel-lut.rgb.gz");
            Parallel.For(0, workHeight, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) }, y =>
            {
                for (int x = 0; x < workWidth; x++)
                {
                    Vector3 colour = SampleCapture(capture, width, height, (x + .5f) / workWidth, (y + .5f) / workHeight);
                    int pixel = y * workWidth + x;
                    source.Pixels[pixel] = colour;
                    graded.Pixels[pixel] = Vector3.Lerp(colour, ReferenceEffectMath.Lut(lut, colour), .3f);
                }
            });
            var result = new byte[capture.Length];
            new RefreshOutputEncoder(width, height, capture, source).Encode(graded, result);
            return result;
        }

        private static Vector3 SampleCapture(byte[] capture, int width, int height, float u, float v)
        {
            float x = Math.Clamp(u * width - .5f, 0, width - 1), y = Math.Clamp(v * height - .5f, 0, height - 1);
            int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, width - 1), y1 = Math.Min(y0 + 1, height - 1);
            Vector3 Read(int px, int py)
            {
                int offset = (py * width + px) * 4;
                return new Vector3(capture[offset + 2], capture[offset + 1], capture[offset]) / 255;
            }
            return Vector3.Lerp(Vector3.Lerp(Read(x0, y0), Read(x1, y0), x - x0),
                Vector3.Lerp(Read(x0, y1), Read(x1, y1), x - x0), y - y0);
        }

        private static float SampleDepth(RefreshDepthMap depth, float u, float v)
        {
            float x = Math.Clamp(u * depth.Width - .5f, 0, depth.Width - 1), y = Math.Clamp(v * depth.Height - .5f, 0, depth.Height - 1);
            int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, depth.Width - 1), y1 = Math.Min(y0 + 1, depth.Height - 1);
            return float.Lerp(float.Lerp(depth.Values[y0 * depth.Width + x0], depth.Values[y0 * depth.Width + x1], x - x0),
                float.Lerp(depth.Values[y1 * depth.Width + x0], depth.Values[y1 * depth.Width + x1], x - x0), y - y0);
        }

        private static float Smooth(double low, double high, double value)
        {
            float t = (float)Math.Clamp((value - low) / (high - low), 0, 1);
            return t * t * (3 - 2 * t);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Scale(uint value, int amount)
        {
            uint rb = (((value & 0x00FF00FFu) * (uint)amount + 0x00800080u) >> 8) & 0x00FF00FFu;
            uint green = (((((value >> 8) & 255) * (uint)amount + 128) >> 8) << 8);
            uint alpha = ((((value >> 24) * (uint)amount + 128) >> 8) << 24);
            return rb | green | alpha;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Over(uint foreground, uint background)
        {
            int remaining = 255 - (int)(foreground >> 24);
            uint rgb = (foreground & 0x00FFFFFFu) + ProductRgb255(background, remaining);
            int alphaProduct = (int)(background >> 24) * remaining + 128;
            int alpha = (int)(foreground >> 24) + ((alphaProduct + (alphaProduct >> 8)) >> 8);
            return rgb | ((uint)alpha << 24);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint AddLight(uint overlay, uint source, int blue, int green, int red)
        {
            int alpha = (int)(overlay >> 24);
            uint visible = (overlay & 0x00FFFFFFu) + ProductRgb255(source, 255 - alpha);
            int b = Math.Min(255, (int)(visible & 255) + blue);
            int g = Math.Min(255, (int)((visible >> 8) & 255) + green);
            int r = Math.Min(255, (int)((visible >> 16) & 255) + red);
            alpha = Math.Max(alpha, RequiredAlpha[((int)(source & 255) << 8) | b]);
            alpha = Math.Max(alpha, RequiredAlpha[((int)((source >> 8) & 255) << 8) | g]);
            alpha = Math.Max(alpha, RequiredAlpha[((int)((source >> 16) & 255) << 8) | r]);
            uint target = (uint)(b | (g << 8) | (r << 16));
            return (target - ProductRgb255(source, 255 - alpha)) | ((uint)alpha << 24);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint ProductRgb255(uint rgb, int opacity)
        {
            uint rb = (rgb & 0x00FF00FFu) * (uint)opacity + 0x00800080u;
            rb = ((rb + ((rb >> 8) & 0x00FF00FFu)) >> 8) & 0x00FF00FFu;
            uint green = ((rgb >> 8) & 255) * (uint)opacity + 128;
            green = (green + (green >> 8)) >> 8;
            return rb | (green << 8);
        }

        private static byte[] BuildRequiredAlpha()
        {
            var result = new byte[256 * 256];
            for (int source = 0; source < 255; source++)
            for (int target = source + 1; target < 256; target++)
                result[(source << 8) | target] = (byte)(((target - source) * 255 + 254 - source) / (255 - source));
            return result;
        }

        private static void Map(int[] first, int[] second, float[] weight, int samples, int step)
        {
            for (int pixel = 0; pixel < first.Length; pixel++)
            {
                float coordinate = (pixel + .5f) / step - .5f;
                int lower = (int)MathF.Floor(coordinate);
                first[pixel] = Math.Clamp(lower, 0, samples - 1);
                second[pixel] = Math.Clamp(lower + 1, 0, samples - 1);
                weight[pixel] = coordinate - lower;
            }
        }
    }
}
