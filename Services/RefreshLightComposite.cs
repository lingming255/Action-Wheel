using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Action_Wheel.Services
{
    /// <summary>
    /// Adds scene-coloured light to the expected visible desktop, then encodes that result as
    /// a transparent PBGRA correction. A normal source-over colour fog would darken the other
    /// channels; this instead preserves them while adding light in the source's sRGB space.
    /// The captured desktop is read-only and its undefined GDI alpha is ignored.
    /// </summary>
    internal sealed class RefreshLightComposite
    {
        private static readonly byte[] RequiredAlpha = BuildRequiredAlpha();
        private readonly int _width, _height;
        private readonly byte[] _scene;
        private readonly int[] _x0, _x1, _y0, _y1;
        private readonly float[] _xWeight, _yWeight;
        private readonly float[][] _rowGlow;
        private readonly int _bandCount;
        private readonly ParallelOptions _parallelOptions;
        private readonly Action<int> _renderBand;
        private byte[]? _activeOverlay;
        private float[]? _activeGlow;
        private uint[]? _activeCores;
        private int _activeBloomWidth;
        private int _mappedStep;

        public RefreshLightComposite(int width, int height, byte[] scene)
        {
            ArgumentNullException.ThrowIfNull(scene);
            if (width <= 0 || height <= 0 || scene.LongLength != (long)width * height * 4)
                throw new ArgumentException("Light composition dimensions must match the BGRA capture.", nameof(scene));
            _width = width; _height = height; _scene = scene;
            _x0 = new int[width]; _x1 = new int[width]; _xWeight = new float[width];
            _y0 = new int[height]; _y1 = new int[height]; _yWeight = new float[height];
            // At step=1 the low-resolution row can be as wide as the capture. Allocating its
            // maximum size here keeps changing DPI/sample scales allocation-free at render time.
            _bandCount = (long)width * height >= 512 * 1024 ? Math.Min(4, Environment.ProcessorCount) : 1;
            _rowGlow = new float[_bandCount][];
            for (int band = 0; band < _bandCount; band++) _rowGlow[band] = new float[checked(width * 3)];
            _parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = _bandCount };
            _renderBand = RenderBand;
        }

        /// <summary>
        /// Adds nonnegative B/G/R light, in byte-channel units, at a uint pixel index.
        /// Returns valid PBGRA for normal source-over display above the captured scene.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Add(uint overlay, int pixelIndex, int blue, int green, int red)
        {
            if (blue <= 0 && green <= 0 && red <= 0) return overlay;
            return AddBounded(overlay, pixelIndex, Math.Clamp(blue, 0, 255),
                Math.Clamp(green, 0, 255), Math.Clamp(red, 0, 255));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint AddBounded(uint overlay, int pixelIndex, int blue, int green, int red)
        {
            int alpha = (int)(overlay >> 24);
            if (alpha == 255)
            {
                // Opaque cover/cores already contain the whole visible background. Their
                // light is plain saturating RGB addition, without capture reads or lookups.
                int opaqueB = Math.Min(255, (int)(overlay & 255) + blue);
                int opaqueG = Math.Min(255, (int)((overlay >> 8) & 255) + green);
                int opaqueR = Math.Min(255, (int)((overlay >> 16) & 255) + red);
                return 0xFF000000u | (uint)(opaqueB | (opaqueG << 8) | (opaqueR << 16));
            }
            uint source = Unsafe.ReadUnaligned<uint>(ref _scene[checked(pixelIndex * 4)]);
            uint visible = (overlay & 0x00FFFFFFu) + ProductRgb255(source, 255 - alpha);
            int b = Math.Min(255, (int)(visible & 255) + blue);
            int g = Math.Min(255, (int)((visible >> 8) & 255) + green);
            int r = Math.Min(255, (int)((visible >> 16) & 255) + red);

            // A target T over the original S needs at least A=(T-S)/(1-S). Retaining the
            // existing coverage also preserves pixels where the current cover darkened S.
            // The lookup is rounded upwards; P is calculated from that actual byte alpha,
            // not an unquantized approximation that could make saturated channels darken.
            alpha = Math.Max(alpha, RequiredAlpha[((int)(source & 255) << 8) | b]);
            alpha = Math.Max(alpha, RequiredAlpha[((int)((source >> 8) & 255) << 8) | g]);
            alpha = Math.Max(alpha, RequiredAlpha[((int)((source >> 16) & 255) << 8) | r]);
            // Coverage only increases, so each target is at least its uncovered source value.
            // No channel borrows when subtracting the three packed values together.
            uint target = (uint)(b | (g << 8) | (r << 16));
            return (target - ProductRgb255(source, 255 - alpha)) | ((uint)alpha << 24);
        }

        /// <summary>
        /// Places black above the current overlay, before light/particles. For any live desktop
        /// underneath, both its visible colour and the cover are dimmed by opacity256/256.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Dim(uint overlay, int opacity256)
        {
            if (opacity256 <= 0) return overlay;
            if (opacity256 >= 256) return 0xFF000000u;
            int remaining = 256 - opacity256;
            int b = ((int)(overlay & 255) * remaining + 128) >> 8;
            int g = ((int)((overlay >> 8) & 255) * remaining + 128) >> 8;
            int r = ((int)((overlay >> 16) & 255) * remaining + 128) >> 8;
            int a = 255 - (((255 - (int)(overlay >> 24)) * remaining + 128) >> 8);
            return (uint)(b | (g << 8) | (r << 16) | (a << 24));
        }

        public static void DimFrame(byte[] overlay, int opacity256)
        {
            if (opacity256 <= 0) return;
            Span<uint> pixels = MemoryMarshal.Cast<byte, uint>(overlay.AsSpan());
            int index = 0;
            if (Vector.IsHardwareAccelerated && opacity256 < 256)
            {
                var remaining = new Vector<uint>((uint)(256 - opacity256));
                var channel = new Vector<uint>(255);
                var rbMask = new Vector<uint>(0x00FF00FFu);
                var rbRound = new Vector<uint>(0x00800080u);
                var half = new Vector<uint>(128);
                int end = pixels.Length - Vector<uint>.Count;
                for (; index <= end; index += Vector<uint>.Count)
                {
                    var source = new Vector<uint>(pixels.Slice(index, Vector<uint>.Count));
                    var rb = (((source & rbMask) * remaining + rbRound) >> 8) & rbMask;
                    var green = (((((source >> 8) & channel) * remaining + half) >> 8) << 8);
                    var alpha = channel - (((channel - (source >> 24)) * remaining + half) >> 8);
                    (rb | green | (alpha << 24)).CopyTo(pixels.Slice(index, Vector<uint>.Count));
                }
            }
            for (; index < pixels.Length; index++) pixels[index] = Dim(pixels[index], opacity256);
        }

        /// <summary>
        /// Bilinearly upsamples low-resolution B/G/R emission and adds it to the full frame.
        /// Emission 1.0 equals 255 channel units. Working maps and row storage are reused;
        /// one instance must only be used by the renderer's single worker.
        /// </summary>
        public void AddBloom(byte[] overlay, float[] glow, int bloomWidth, int bloomHeight, int step,
            uint[]? cores = null)
        {
            ArgumentNullException.ThrowIfNull(overlay);
            ArgumentNullException.ThrowIfNull(glow);
            if (step <= 0 || bloomWidth != (_width + (long)step - 1) / step
                || bloomHeight != (_height + (long)step - 1) / step
                || overlay.Length != _scene.Length
                || glow.LongLength != (long)bloomWidth * bloomHeight * 3
                || (cores != null && cores.LongLength != (long)_width * _height))
                throw new ArgumentException("Bloom dimensions must match the capture and sampling step.");
            if (_mappedStep != step)
            {
                Map(_x0, _x1, _xWeight, bloomWidth, step);
                Map(_y0, _y1, _yWeight, bloomHeight, step);
                _mappedStep = step;
            }
            if (_bandCount == 1)
            {
                RenderRows(overlay, glow, cores, _rowGlow[0], 0, _height, bloomWidth);
                return;
            }
            // Only the caller's one worker enters AddBloom; the bounded bands write disjoint
            // rows and own independent row storage. Mapping is complete before dispatch.
            _activeOverlay = overlay; _activeGlow = glow; _activeCores = cores;
            _activeBloomWidth = bloomWidth;
            try { Parallel.For(0, _bandCount, _parallelOptions, _renderBand); }
            finally { _activeOverlay = null; _activeGlow = null; _activeCores = null; }
        }

        private void RenderBand(int band)
            => RenderRows(_activeOverlay!, _activeGlow!, _activeCores, _rowGlow[band],
                _height * band / _bandCount, _height * (band + 1) / _bandCount, _activeBloomWidth);

        private void RenderRows(byte[] overlay, float[] glow, uint[]? cores, float[] rowGlow,
            int firstY, int lastY, int bloomWidth)
        {
            Span<uint> pixels = MemoryMarshal.Cast<byte, uint>(overlay.AsSpan());
            int rowChannels = bloomWidth * 3;
            for (int y = firstY; y < lastY; y++)
            {
                int top = _y0[y] * rowChannels, bottom = _y1[y] * rowChannels;
                float vertical = _yWeight[y];
                bool anyLight = false;
                for (int channel = 0; channel < rowChannels; channel++)
                {
                    float upper = glow[top + channel];
                    float value = upper + (glow[bottom + channel] - upper) * vertical;
                    rowGlow[channel] = value * 255;
                    anyLight |= value > 0;
                }
                int row = y * _width;
                if (!anyLight)
                {
                    if (cores != null)
                        for (int x = 0; x < _width; x++)
                        {
                            int pixel = row + x;
                            uint core = cores[pixel];
                            if (core == 0) continue;
                            pixels[pixel] = AddBounded(pixels[pixel], pixel, (int)(core & 255),
                                (int)((core >> 8) & 255), (int)((core >> 16) & 255));
                        }
                    continue;
                }
                for (int x = 0; x < _width; x++)
                {
                    int left = _x0[x] * 3, right = _x1[x] * 3;
                    float horizontal = _xWeight[x];
                    int b = LightChannel(rowGlow[left]
                        + (rowGlow[right] - rowGlow[left]) * horizontal);
                    int g = LightChannel(rowGlow[left + 1]
                        + (rowGlow[right + 1] - rowGlow[left + 1]) * horizontal);
                    int r = LightChannel(rowGlow[left + 2]
                        + (rowGlow[right + 2] - rowGlow[left + 2]) * horizontal);
                    int pixel = row + x;
                    if (cores != null)
                    {
                        uint core = cores[pixel];
                        b = Math.Min(255, b + (int)(core & 255));
                        g = Math.Min(255, g + (int)((core >> 8) & 255));
                        r = Math.Min(255, r + (int)((core >> 16) & 255));
                    }
                    if ((b | g | r) == 0) continue;
                    pixels[pixel] = AddBounded(pixels[pixel], pixel, b, g, r);
                }
            }
        }

        private static void Map(int[] first, int[] second, float[] weight, int cells, int step)
        {
            float inverse = 1f / step;
            for (int pixel = 0; pixel < first.Length; pixel++)
            {
                float coordinate = (pixel + 0.5f) * inverse - 0.5f;
                int cell = (int)MathF.Floor(coordinate);
                first[pixel] = Math.Clamp(cell, 0, cells - 1);
                second[pixel] = Math.Clamp(cell + 1, 0, cells - 1);
                weight[pixel] = coordinate - cell;
            }
        }

        private static byte[] BuildRequiredAlpha()
        {
            var lookup = new byte[256 * 256];
            for (int source = 0; source < 255; source++)
            for (int target = source + 1; target < 256; target++)
                lookup[(source << 8) | target] = (byte)(((target - source) * 255
                    + 254 - source) / (255 - source));
            return lookup;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint ProductRgb255(uint rgb, int opacity)
        {
            uint rb = (rgb & 0x00FF00FFu) * (uint)opacity + 0x00800080u;
            rb = ((rb + ((rb >> 8) & 0x00FF00FFu)) >> 8) & 0x00FF00FFu;
            uint g = ((rgb >> 8) & 255) * (uint)opacity + 128;
            g = (g + (g >> 8)) >> 8;
            return rb | (g << 8);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int LightChannel(float value)
            => !(value > 0) ? 0 : (int)MathF.Min(255, MathF.Round(value));
    }
}
