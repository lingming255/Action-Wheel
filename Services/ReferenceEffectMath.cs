using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Action_Wheel.Services
{
    /// <summary>RGB float render target, with explicit image and GLSL texture coordinates.</summary>
    internal sealed class ReferenceTexture
    {
        public int Width { get; }
        public int Height { get; }
        public Vector3[] Pixels { get; }
        public ReferenceTexture(int width, int height, Vector3[] pixels)
        {
            if (width <= 0 || height <= 0 || pixels.Length != checked(width * height))
                throw new ArgumentException("Texture dimensions do not match pixels.");
            Width = width; Height = height; Pixels = pixels;
        }
        public ReferenceTexture(int width, int height) : this(width, height, new Vector3[checked(width * height)]) { }
        public Vector3 Sample(float u, float v)
        {
            float x = Math.Clamp(u * Width - .5f, 0, Width - 1);
            float y = Math.Clamp(v * Height - .5f, 0, Height - 1);
            int x0 = (int)x, y0 = (int)y;
            int x1 = Math.Min(x0 + 1, Width - 1), y1 = Math.Min(y0 + 1, Height - 1);
            return Vector3.Lerp(Vector3.Lerp(Pixels[y0 * Width + x0], Pixels[y0 * Width + x1], x - x0),
                Vector3.Lerp(Pixels[y1 * Width + x0], Pixels[y1 * Width + x1], x - x0), y - y0);
        }
        public Vector3 SampleGl(float u, float v) => Sample(u, 1 - v);
        public static ReferenceTexture FromBgra(byte[] bgra, int width, int height)
        {
            if (bgra.Length != checked(width * height * 4)) throw new ArgumentException("Capture size mismatch.");
            var result = new ReferenceTexture(width, height);
            for (int p = 0; p < result.Pixels.Length; p++)
                result.Pixels[p] = new Vector3(bgra[p * 4 + 2], bgra[p * 4 + 1], bgra[p * 4]) / 255;
            return result;
        }
    }

    internal static class ReferenceEffectMath
    {
        private static readonly ConditionalWeakTable<ReferenceTexture, GaussianCache> GaussianPlans = new();

        public static float SmoothStep(float low, float high, float value)
        {
            float t = Math.Clamp((value - low) / (high - low), 0, 1);
            return t * t * (3 - 2 * t);
        }
        public static Vector3 RgbToHsl(Vector3 rgb)
        {
            // Deliberately retain the reference shader's arithmetic outside [0,1].
            float max = Math.Max(rgb.X, Math.Max(rgb.Y, rgb.Z));
            float min = Math.Min(rgb.X, Math.Min(rgb.Y, rgb.Z));
            float lightness = (max + min) * .5f, delta = max - min;
            if (delta == 0) return new Vector3(0, 0, lightness);
            float saturation = lightness < .5f ? delta / (max + min) : delta / (2 - max - min);
            float hue = max == rgb.X ? (rgb.Y - rgb.Z) / delta + (rgb.Y < rgb.Z ? 6 : 0)
                : max == rgb.Y ? (rgb.Z - rgb.X) / delta + 2 : (rgb.X - rgb.Y) / delta + 4;
            return new Vector3(hue / 6, saturation, lightness);
        }
        public static Vector3 HslToRgb(Vector3 hsl)
        {
            if (hsl.Y == 0) return new Vector3(hsl.Z);
            float q = hsl.Z < .5f ? hsl.Z * (1 + hsl.Y) : hsl.Z + hsl.Y - hsl.Z * hsl.Y;
            float p = 2 * hsl.Z - q;
            return new Vector3(Hue(p, q, hsl.X + 1f / 3), Hue(p, q, hsl.X), Hue(p, q, hsl.X - 1f / 3));
        }
        private static float Hue(float p, float q, float t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1f / 6) return p + (q - p) * 6 * t;
            if (t < .5f) return q;
            if (t < 2f / 3) return p + (q - p) * (2f / 3 - t) * 6;
            return p;
        }
        public static Vector3 Clamp(Vector3 rgb) => Vector3.Clamp(rgb, Vector3.Zero, Vector3.One);
        /// <summary>Reference Gaussian samples in source texels, including RT size changes.</summary>
        public static void Gaussian(ReferenceTexture source, ReferenceTexture target, float spacing,
            bool horizontal, int radius, float[] weights)
        {
            float sum = weights[0];
            for (int i = 1; i <= radius; i++) sum += 2 * weights[i];
            GaussianCache cache = GaussianPlans.GetOrCreateValue(target);
            var key = new GaussianKey(source.Width, source.Height, spacing, horizontal, radius);
            GaussianPlan plan;
            lock (cache.Plans)
            {
                if (!cache.Plans.TryGetValue(key, out plan!))
                {
                    plan = new GaussianPlan(source, target, spacing, horizontal, radius);
                    cache.Plans.Add(key, plan);
                }
            }
            plan.Apply(source.Pixels, target.Pixels, weights, 1 / sum);
        }

        private readonly record struct GaussianKey(int SourceWidth, int SourceHeight,
            float Spacing, bool Horizontal, int Radius);

        private sealed class GaussianCache
        {
            public readonly Dictionary<GaussianKey, GaussianPlan> Plans = new();
        }

        /// <summary>
        /// Sampling coordinates depend on sizes and spacing, not the current frame. Store the
        /// exact Sample() indices and fractions once, including the original UV arithmetic.
        /// The weak target cache releases these maps with the gesture's render targets.
        /// </summary>
        private sealed class GaussianPlan
        {
            private readonly int _width, _height, _sourceWidth, _radius, _tapCount, _bands;
            private readonly int[] _left, _right, _top, _bottom;
            private readonly float[] _horizontal, _vertical;
            private readonly ParallelOptions _options = new() { MaxDegreeOfParallelism = 4 };
            private readonly Action<int> _renderBand;
            private Vector3[]? _source, _target;
            private float[]? _weights;
            private float _inverse;

            public GaussianPlan(ReferenceTexture source, ReferenceTexture target,
                float spacing, bool horizontal, int radius)
            {
                _width = target.Width; _height = target.Height;
                _sourceWidth = source.Width; _radius = radius; _tapCount = 1 + radius * 2;
                _left = new int[_width * _tapCount]; _right = new int[_left.Length];
                _horizontal = new float[_left.Length];
                _top = new int[_height * _tapCount]; _bottom = new int[_top.Length];
                _vertical = new float[_top.Length];
                float du = horizontal ? spacing / source.Width : 0;
                float dv = horizontal ? 0 : spacing / source.Height;
                for (int x = 0; x < _width; x++)
                {
                    float u = (x + .5f) / _width;
                    MapX(x, 0, u, source.Width);
                    for (int tap = 1; tap <= radius; tap++)
                    {
                        MapX(x, tap * 2 - 1, u - tap * du, source.Width);
                        MapX(x, tap * 2, u + tap * du, source.Width);
                    }
                }
                for (int y = 0; y < _height; y++)
                {
                    float v = (y + .5f) / _height;
                    MapY(y, 0, v, source.Height);
                    for (int tap = 1; tap <= radius; tap++)
                    {
                        MapY(y, tap * 2 - 1, v - tap * dv, source.Height);
                        MapY(y, tap * 2, v + tap * dv, source.Height);
                    }
                }
                _bands = (long)_width * _height >= 8192 ? Math.Min(4, Environment.ProcessorCount) : 1;
                _renderBand = RenderBand;
            }

            private void MapX(int x, int tap, float u, int sourceWidth)
            {
                float coordinate = Math.Clamp(u * sourceWidth - .5f, 0, sourceWidth - 1);
                int index = x * _tapCount + tap;
                int first = (int)coordinate;
                _left[index] = first; _right[index] = Math.Min(first + 1, sourceWidth - 1);
                _horizontal[index] = coordinate - first;
            }

            private void MapY(int y, int tap, float v, int sourceHeight)
            {
                float coordinate = Math.Clamp(v * sourceHeight - .5f, 0, sourceHeight - 1);
                int index = y * _tapCount + tap;
                int first = (int)coordinate;
                _top[index] = first * _sourceWidth;
                _bottom[index] = Math.Min(first + 1, sourceHeight - 1) * _sourceWidth;
                _vertical[index] = coordinate - first;
            }

            public void Apply(Vector3[] source, Vector3[] target, float[] weights, float inverse)
            {
                _source = source; _target = target; _weights = weights; _inverse = inverse;
                try
                {
                    if (_bands == 1) RenderBand(0);
                    else Parallel.For(0, _bands, _options, _renderBand);
                }
                finally { _source = null; _target = null; _weights = null; }
            }

            private void RenderBand(int band)
            {
                Vector3[] target = _target!;
                float[] weights = _weights!;
                float inverse = _inverse;
                int firstY = _height * band / _bands, lastY = _height * (band + 1) / _bands;
                for (int y = firstY; y < lastY; y++)
                {
                    int row = y * _width, yMap = y * _tapCount;
                    for (int x = 0; x < _width; x++)
                    {
                        int xMap = x * _tapCount;
                        Vector3 result = Sample(xMap, yMap) * weights[0];
                        // Preserve the shader/oracle's centre, then symmetric-pair addition order.
                        for (int tap = 1; tap <= _radius; tap++)
                        {
                            int negative = tap * 2 - 1, positive = tap * 2;
                            result += (Sample(xMap + negative, yMap + negative)
                                + Sample(xMap + positive, yMap + positive)) * weights[tap];
                        }
                        target[row + x] = Clamp(result * inverse);
                    }
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private Vector3 Sample(int xMap, int yMap)
            {
                Vector3[] pixels = _source!;
                int left = _left[xMap], right = _right[xMap];
                int top = _top[yMap], bottom = _bottom[yMap];
                float horizontal = _horizontal[xMap];
                float vertical = _vertical[yMap];
                if (vertical == 0)
                    return Vector3.Lerp(pixels[top + left], pixels[top + right], horizontal);
                if (horizontal == 0)
                    return Vector3.Lerp(pixels[top + left], pixels[bottom + left], vertical);
                return Vector3.Lerp(
                    Vector3.Lerp(pixels[top + left], pixels[top + right], horizontal),
                    Vector3.Lerp(pixels[bottom + left], pixels[bottom + right], horizontal),
                    vertical);
            }
        }
        public static Vector3 Lut(ReferenceTexture lut, Vector3 rgb)
        {
            rgb = Clamp(rgb);
            float blue = rgb.Z * 63, lower = MathF.Floor(blue), upper = MathF.Ceiling(blue);
            Vector3 Slice(float slice)
            {
                float row = MathF.Floor(slice / 8), col = slice - row * 8;
                float u = col / 8 + .5f / 512 + (1f / 8 - 1f / 512) * rgb.X;
                float v = row / 8 + .5f / 512 + (1f / 8 - 1f / 512) * rgb.Y;
                // LUT assets store slice row zero at the top, as in the original PNG.
                return lut.Sample(u, v);
            }
            return Vector3.Lerp(Slice(lower), Slice(upper), blue - lower);
        }
        public static ReferenceTexture LoadTexture(string suffix)
        {
            using Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("ActionWheel.Refresh." + suffix)
                ?? throw new FileNotFoundException("Missing refresh reference texture: " + suffix);
            using var zip = new GZipStream(resource, CompressionMode.Decompress);
            using var reader = new BinaryReader(zip);
            int width = reader.ReadInt32(), height = reader.ReadInt32();
            var texture = new ReferenceTexture(width, height);
            for (int p = 0; p < texture.Pixels.Length; p++)
                texture.Pixels[p] = new Vector3(reader.ReadByte(), reader.ReadByte(), reader.ReadByte()) / 255;
            return texture;
        }
    }
}
