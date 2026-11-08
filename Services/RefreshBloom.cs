using System;

namespace Action_Wheel.Services
{
    /// <summary>A low-resolution PBGRA light layer; its placement is in physical monitor pixels.</summary>
    internal sealed record RefreshBloomImage(byte[] Pixels, int PixelWidth, int PixelHeight,
        int Left, int Top, int Width, int Height);

    /// <summary>
    /// Prefilters the emissive pixels of a single animation cluster into a coloured, two-scale bloom.
    /// The particle geometry is static during a refresh: only each layer's intensity is animated.
    /// Blur is linear, so animating this image with that same opacity is equivalent to blurring the
    /// current emissive layer every frame, without CPU readback or a full-screen blur per frame.
    /// </summary>
    internal static class RefreshBloom
    {
        private const float Threshold = 0.50f;
        private const float SoftKnee = 0.18f;
        private const float EmissionGain = 1.6f;
        private const float NearWeight = 0.80f;
        private const float FarWeight = 0.55f;

        public static RefreshBloomImage? Build(byte[] layer, int sourceWidth, int sourceHeight,
            int left, int top, int width, int height, double scale)
        {
            ArgumentNullException.ThrowIfNull(layer);
            if (sourceWidth <= 0 || sourceHeight <= 0
                || layer.LongLength != (long)sourceWidth * sourceHeight * 4)
                throw new ArgumentException("Bloom source dimensions must match its BGRA buffer.", nameof(layer));
            if (left < 0 || top < 0 || width <= 0 || height <= 0
                || (long)left + width > sourceWidth || (long)top + height > sourceHeight)
                throw new ArgumentOutOfRangeException(nameof(width), "Bloom cluster must lie inside the source.");
            if (!double.IsFinite(scale) || scale <= 0 || scale > 8)
                throw new ArgumentOutOfRangeException(nameof(scale));

            // One light sample per four DIP: roughly 1/16th as many pixels as the source at 100%.
            // At higher DPI the work and the visible blur radius stay approximately constant.
            int step = Math.Max(2, (int)Math.Round(4 * scale));
            const int paddingCells = 6; // 24 DIP; covers the widest Gaussian's complete kernel.
            int padding = paddingCells * step;
            int pixelWidth = checked((width + step - 1) / step + paddingCells * 2);
            int pixelHeight = checked((height + step - 1) / step + paddingCells * 2);
            int outputLeft = left - padding, outputTop = top - padding;
            var emission = new float[checked(pixelWidth * pixelHeight * 3)];
            float normalization = 1f / (step * step);
            bool hasEmission = false;

            for (int sampleY = 0; sampleY < height; sampleY += step)
            for (int sampleX = 0; sampleX < width; sampleX += step)
            {
                int sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                int bottom = Math.Min(top + sampleY + step, top + height);
                int right = Math.Min(left + sampleX + step, left + width);
                for (int y = top + sampleY; y < bottom; y++)
                {
                    int offset = (y * sourceWidth + left + sampleX) * 4;
                    for (int x = left + sampleX; x < right; x++, offset += 4)
                    {
                        sumB += layer[offset]; sumG += layer[offset + 1];
                        sumR += layer[offset + 2]; sumA += layer[offset + 3];
                    }
                }
                if (sumA == 0) continue;
                // Derive brightness from straight colour after downsampling, then weight by the
                // original alpha. Testing premultiplied RGB against the threshold would discard
                // translucent saturated pigments even though they are visually bright particles.
                // Integer downsampling first avoids doing the soft-knee divisions at full resolution.
                float b = sumB / (float)sumA, g = sumG / (float)sumA, r = sumR / (float)sumA;
                float peak = Math.Max(r, Math.Max(g, b));
                float luma = r * 0.2126f + g * 0.7152f + b * 0.0722f;
                float brightness = peak * 0.65f + luma * 0.35f;
                float knee = Math.Clamp(brightness - Threshold + SoftKnee, 0, 2 * SoftKnee);
                knee = knee * knee / (4 * SoftKnee);
                float contribution = Math.Max(brightness - Threshold, knee);
                if (contribution <= 0) continue;
                float gain = contribution / Math.Max(brightness, 0.0001f)
                    * EmissionGain * normalization / 255f;
                int cell = ((sampleY / step + paddingCells) * pixelWidth
                    + sampleX / step + paddingCells) * 3;
                emission[cell] = sumB * gain;
                emission[cell + 1] = sumG * gain;
                emission[cell + 2] = sumR * gain;
                hasEmission = true;
            }
            if (!hasEmission) return null;

            // Repeated separable box filters approximate a Gaussian, but each pass has constant
            // work per sample. A 4K capture has hundreds of independent cluster images; doing a
            // complete Gaussian convolution for each would make opening the wheel noticeably late.
            float[] near = Blur(emission, pixelWidth, pixelHeight, 1, 2);
            float[] far = Blur(emission, pixelWidth, pixelHeight, 2, 3);
            var pixels = new byte[checked(pixelWidth * pixelHeight * 4)];
            bool hasVisibleLight = false;
            for (int cell = 0, pixel = 0; cell < near.Length; cell += 3, pixel += 4)
            {
                byte b = ToByte(near[cell] * NearWeight + far[cell] * FarWeight);
                byte g = ToByte(near[cell + 1] * NearWeight + far[cell + 1] * FarWeight);
                byte r = ToByte(near[cell + 2] * NearWeight + far[cell + 2] * FarWeight);
                byte alpha = Math.Max(r, Math.Max(g, b));
                pixels[pixel] = b;
                pixels[pixel + 1] = g;
                pixels[pixel + 2] = r;
                // Smallest valid alpha keeps light saturated and luminous under source-over,
                // instead of surrounding each particle with a dark translucent rectangle.
                pixels[pixel + 3] = alpha;
                hasVisibleLight |= alpha != 0;
            }
            return hasVisibleLight
                ? new RefreshBloomImage(pixels, pixelWidth, pixelHeight, outputLeft, outputTop,
                    pixelWidth * step, pixelHeight * step)
                : null;
        }

        private static float[] Blur(float[] source, int width, int height, int radius, int passes)
        {
            var horizontal = new float[source.Length];
            var result = new float[source.Length];
            float inverseWidth = 1f / (radius * 2 + 1);
            for (int pass = 0; pass < passes; pass++)
            {
                float[] input = pass == 0 ? source : result;
                for (int y = 0; y < height; y++)
                {
                    float b = 0, g = 0, r = 0;
                    for (int x = 0; x <= radius && x < width; x++)
                    {
                        int i = (y * width + x) * 3;
                        b += input[i]; g += input[i + 1]; r += input[i + 2];
                    }
                    for (int x = 0; x < width; x++)
                    {
                        int target = (y * width + x) * 3;
                        horizontal[target] = b * inverseWidth;
                        horizontal[target + 1] = g * inverseWidth;
                        horizontal[target + 2] = r * inverseWidth;
                        int removeX = x - radius, addX = x + radius + 1;
                        if (removeX >= 0)
                        {
                            int remove = (y * width + removeX) * 3;
                            b -= input[remove]; g -= input[remove + 1]; r -= input[remove + 2];
                        }
                        if (addX < width)
                        {
                            int add = (y * width + addX) * 3;
                            b += input[add]; g += input[add + 1]; r += input[add + 2];
                        }
                    }
                }
                for (int x = 0; x < width; x++)
                {
                    float b = 0, g = 0, r = 0;
                    for (int y = 0; y <= radius && y < height; y++)
                    {
                        int i = (y * width + x) * 3;
                        b += horizontal[i]; g += horizontal[i + 1]; r += horizontal[i + 2];
                    }
                    for (int y = 0; y < height; y++)
                    {
                        int target = (y * width + x) * 3;
                        result[target] = b * inverseWidth;
                        result[target + 1] = g * inverseWidth;
                        result[target + 2] = r * inverseWidth;
                        int removeY = y - radius, addY = y + radius + 1;
                        if (removeY >= 0)
                        {
                            int remove = (removeY * width + x) * 3;
                            b -= horizontal[remove]; g -= horizontal[remove + 1]; r -= horizontal[remove + 2];
                        }
                        if (addY < height)
                        {
                            int add = (addY * width + x) * 3;
                            b += horizontal[add]; g += horizontal[add + 1]; r += horizontal[add + 2];
                        }
                    }
                }
            }
            return result;
        }

        private static byte ToByte(float value)
            => (byte)Math.Clamp(MathF.Round(value * 255), 0, 255);
    }
}
