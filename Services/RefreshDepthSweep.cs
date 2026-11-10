using System;

namespace Action_Wheel.Services
{
    /// <summary>Two equal-speed depth fronts moving away from the pointer's sampled depth.</summary>
    internal static class RefreshDepthSweep
    {
        internal const double TimeScale = .85;
        internal const double DurationMs = 3000 * TimeScale;
        internal const double StartDelayMs = 140 * TimeScale;
        internal const double TravelMs = 2200 * TimeScale;
        internal const double LifetimeMs = 600 * TimeScale;
        internal const double RiseMs = 100 * TimeScale;
        internal const double FadeStartMs = 220 * TimeScale;
        internal const double BackgroundRestoreMs = 400 * TimeScale;

        internal static double StartAt(float depth, float originDepth)
        {
            if (!float.IsFinite(depth) || depth < 0 || depth > 1
                || !float.IsFinite(originDepth) || originDepth < 0 || originDepth > 1)
                throw new ArgumentException("Depth sweep values must be finite and normalized.");
            // Both sides use one depth speed. The shorter side finishes naturally sooner;
            // independently stretching each half would slow down a nearly empty depth range.
            double range = Math.Max(originDepth, 1 - (double)originDepth);
            return StartDelayMs + TravelMs * Math.Abs((double)depth - originDepth) / range;
        }

        internal static float SampleOrigin(RefreshDepthMap map, int width, int height, int x, int y)
        {
            x = Math.Clamp(x, 0, width - 1);
            y = Math.Clamp(y, 0, height - 1);
            float px = Math.Clamp((float)((x + .5) / width * map.Width - .5), 0, map.Width - 1);
            float py = Math.Clamp((float)((y + .5) / height * map.Height - .5), 0, map.Height - 1);
            int x0 = (int)px, y0 = (int)py;
            int x1 = Math.Min(x0 + 1, map.Width - 1), y1 = Math.Min(y0 + 1, map.Height - 1);
            return float.Lerp(float.Lerp(map.Values[y0 * map.Width + x0], map.Values[y0 * map.Width + x1], px - x0),
                float.Lerp(map.Values[y1 * map.Width + x0], map.Values[y1 * map.Width + x1], px - x0), py - y0);
        }
    }
}
