using System;

namespace Action_Wheel.Services
{
    /// <summary>Inset neon rims on particle silhouettes; all transparent gaps stay transparent.</summary>
    internal static class RefreshOutline
    {
        public static byte[] Apply(byte[] pixels, int width, int height, double scale)
        {
            ArgumentNullException.ThrowIfNull(pixels);
            if (width <= 0 || height <= 0 || pixels.LongLength != (long)width * height * 4)
                throw new ArgumentException("Outline dimensions must match the PBGRA buffer.", nameof(pixels));
            if (!double.IsFinite(scale) || scale <= 0 || scale > 8)
                throw new ArgumentOutOfRangeException(nameof(scale));

            var result = (byte[])pixels.Clone();
            var distance = new byte[checked(width * height)];
            int reach = (int)Math.Ceiling(3 * scale) + 1;

            // A bounded, two-pass distance to transparency. Using the real alpha silhouette
            // outlines a whole mosaic block/plus/wave, not every physical pixel inside it.
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                if (pixels[i * 4 + 3] == 0) continue;
                int d = x == 0 || y == 0 ? 1 : reach;
                if (x > 0) d = Math.Min(d, distance[i - 1] + 1);
                if (y > 0) d = Math.Min(d, distance[i - width] + 1);
                distance[i] = (byte)d;
            }
            for (int y = height - 1; y >= 0; y--)
            for (int x = width - 1; x >= 0; x--)
            {
                int i = y * width + x;
                if (distance[i] == 0) continue;
                int d = distance[i];
                if (x == width - 1 || y == height - 1) d = 1;
                if (x + 1 < width) d = Math.Min(d, distance[i + 1] + 1);
                if (y + 1 < height) d = Math.Min(d, distance[i + width] + 1);
                distance[i] = (byte)d;
            }

            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x, d = distance[i];
                if (d == 0 || d >= reach) continue;
                double inset = (d - 1) / scale;
                double colourRim = Math.Clamp(1 - inset / 2.5, 0, 1);
                if (colourRim <= 0) continue;
                double core = Math.Clamp(1.25 - inset, 0, 1);

                int offset = i * 4;
                byte alpha = pixels[offset + 3];
                double b = pixels[offset] / (double)alpha;
                double g = pixels[offset + 1] / (double)alpha;
                double r = pixels[offset + 2] / (double)alpha;
                double peak = Math.Max(r, Math.Max(g, b));
                // Lift all three channels by the same factor. The bright line core now has
                // exactly the particle's hue, including fine dots; no white is mixed into it.
                double strength = Math.Max(core, 0.45 * colourRim);
                double lift = peak > 0 ? 1 + (1 / peak - 1) * strength : 1;
                result[offset] = Channel(b);
                result[offset + 1] = Channel(g);
                result[offset + 2] = Channel(r);
                // Coverage stays identical, including every gap and translucent fine mark.
                byte Channel(double value) => (byte)Math.Clamp(
                    Math.Round(Math.Clamp(value * lift, 0, 1) * alpha), 0, alpha);
            }
            return result;
        }
    }
}
