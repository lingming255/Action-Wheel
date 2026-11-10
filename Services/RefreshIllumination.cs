using System;

namespace Action_Wheel.Services
{
    /// <summary>
    /// Content-dependent exposure correction and scene-coloured light prepared from the original
    /// capture. The capture is not retained, and its undefined GDI alpha is deliberately ignored.
    /// </summary>
    internal sealed class RefreshIllumination
    {
        private const double Exposure = 1.6;
        private const double RiseMs = 50;
        private const double HoldUntilMs = 220;
        private const double EndMs = 700;
        private const float BloomThreshold = 0.62f;
        private const float BloomKnee = 0.12f;
        private const float BloomGain = 0.45f;
        private static readonly double[] Source = new double[256];
        private static readonly double[] Delta = new double[256];
        private static readonly double[] Alpha = new double[256];
        private static readonly float[] LightGain = new float[256];

        /// <summary>Full-resolution premultiplied BGRA exposure correction, before time weighting.</summary>
        public byte[] Overlay { get; }
        /// <summary>Low-resolution, linear-filterable scene emission in B/G/R triplets.</summary>
        public float[] Emission { get; }
        public int BloomWidth { get; }
        public int BloomHeight { get; }

        static RefreshIllumination()
        {
            for (int value = 0; value < 256; value++)
            {
                double srgb = value / 255.0;
                double linear = srgb <= 0.04045 ? srgb / 12.92
                    : Math.Pow((srgb + 0.055) / 1.055, 2.4);
                // The rational shoulder has a 1.6x slope at black, reaches exactly one at
                // white, and retains a positive slope there instead of clipping light detail.
                double exposed = Exposure * linear / (1 + (Exposure - 1) * linear);
                double target = exposed <= 0.0031308 ? exposed * 12.92
                    : 1.055 * Math.Pow(exposed, 1 / 2.4) - 0.055;
                Source[value] = srgb;
                Delta[value] = Math.Max(0, Math.Min(1, target) - srgb);
                Alpha[value] = value == 255 ? 0 : Delta[value] / (1 - srgb);

                float brightness = value / 255f;
                float knee = Math.Clamp(brightness - BloomThreshold + BloomKnee, 0, 2 * BloomKnee);
                float contribution = Math.Max(brightness - BloomThreshold,
                    knee * knee / (4 * BloomKnee));
                LightGain[value] = brightness > 0 ? contribution / brightness * BloomGain : 0;
            }
        }

        public RefreshIllumination(int width, int height, int bloomStep,
            int originX, int originY, byte[] source)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (width <= 0 || height <= 0 || source.LongLength != (long)width * height * 4)
                throw new ArgumentException("Illumination dimensions must match the BGRA capture.", nameof(source));
            if (bloomStep <= 0)
                throw new ArgumentOutOfRangeException(nameof(bloomStep));
            if (originX < 0 || originX >= width || originY < 0 || originY >= height)
                throw new ArgumentOutOfRangeException(nameof(originX));

            Overlay = new byte[checked(width * height * 4)];
            BloomWidth = (int)((width + (long)bloomStep - 1) / bloomStep);
            BloomHeight = (int)((height + (long)bloomStep - 1) / bloomStep);
            Emission = new float[checked(BloomWidth * BloomHeight * 3)];
            var horizontalDistance = new double[width];
            for (int x = 0; x < width; x++)
            {
                double dx = x - (double)originX;
                double extent = Math.Max(1, x < originX ? originX : width - 1 - originX);
                double normalized = dx / extent;
                horizontalDistance[x] = normalized * normalized;
            }
            float normalization = 1f / ((float)bloomStep * bloomStep * 255);
            for (int y = 0; y < height; y++)
            {
                double dy = y - (double)originY;
                double verticalExtent = Math.Max(1, y < originY ? originY : height - 1 - originY);
                double normalizedY = dy / verticalExtent;
                double verticalDistance = normalizedY * normalizedY;
                int row = y * width * 4;
                int lightRow = y / bloomStep * BloomWidth * 3;
                for (int x = 0; x < width; x++)
                {
                    // Each side reaches zero at its own screen edge. A biased click therefore
                    // creates a soft asymmetric ellipse rather than keeping a wide monitor's
                    // nearer top/bottom edges illuminated by a far-corner-based radius.
                    double falloff = Math.Max(0, 1 - horizontalDistance[x] - verticalDistance);
                    double spatial = falloff * falloff;
                    if (spatial <= 0) continue;
                    int pixel = row + x * 4;
                    byte b = source[pixel], g = source[pixel + 1], r = source[pixel + 2];
                    double alpha = Math.Max(Alpha[b], Math.Max(Alpha[g], Alpha[r]));
                    if (alpha > 0)
                    {
                        // Source-over correction: P = delta + A*S, so P + (1-A)*S = S+delta.
                        // Weight P and A together; this scales the exposure change, rather than
                        // placing a faded duplicate screenshot or a white radial sprite on top.
                        byte a = (byte)Math.Clamp(Math.Ceiling(alpha * spatial * 255), 0, 255);
                        Overlay[pixel] = Correction(b);
                        Overlay[pixel + 1] = Correction(g);
                        Overlay[pixel + 2] = Correction(r);
                        Overlay[pixel + 3] = a;

                        // After ceiling A, use that quantized coverage in P as well. This
                        // preserves even a 255-valued colour channel under source-over instead
                        // of losing one level merely because alpha rounded upwards.
                        byte Correction(byte channel) => (byte)Math.Min(a,
                            Byte(Delta[channel] * spatial + a / 255.0 * Source[channel]));
                    }

                    // Only genuine scene highlights emit. Colour ratios come directly from the
                    // captured RGB, independently of correction alpha and any particle outlines.
                    float peak = Math.Max(r, Math.Max(g, b));
                    float luminance = r * 0.2126f + g * 0.7152f + b * 0.0722f;
                    int brightness = Math.Clamp((int)MathF.Round(peak * 0.65f + luminance * 0.35f), 0, 255);
                    float light = LightGain[brightness] * (float)spatial * normalization;
                    if (light <= 0) continue;
                    int cell = lightRow + x / bloomStep * 3;
                    Emission[cell] += b * light;
                    Emission[cell + 1] += g * light;
                    Emission[cell + 2] += r * light;
                }
            }
        }

        /// <summary>Rise in 50 ms, hold through the cover reveal, then fade smoothly to zero at 700 ms.</summary>
        public static float StrengthAt(double elapsedMs)
        {
            if (!double.IsFinite(elapsedMs) || elapsedMs <= 0 || elapsedMs >= EndMs)
                return 0;
            if (elapsedMs < RiseMs)
            {
                double t = elapsedMs / RiseMs;
                return (float)(t * (2 - t));
            }
            if (elapsedMs <= HoldUntilMs) return 1;
            double remaining = 1 - (elapsedMs - HoldUntilMs) / (EndMs - HoldUntilMs);
            return (float)(remaining * remaining * (3 - 2 * remaining));
        }

        private static byte Byte(double normalized)
            => (byte)Math.Clamp(Math.Round(normalized * 255), 0, 255);
    }
}
