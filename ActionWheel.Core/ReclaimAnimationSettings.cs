using System;

namespace Action_Wheel.Core
{
    /// <summary>Preferences for the inward decoration, independent from outward refresh timing.</summary>
    public sealed record ReclaimAnimationSettings
    {
        public static readonly ReclaimAnimationSettings Default = new();
        public const double DefaultDurationMs = 240;
        public const double DefaultPixelDurationMs = 60;
        public const double MinDurationMs = 100, MaxDurationMs = 1500;
        public const double MinPixelDurationMs = 20, MaxPixelDurationMs = 500;
        public const double MinDensityPercent = 10, MaxDensityPercent = 200;
        public const double MinGlowPercent = 0, MaxGlowPercent = 200;

        public double DurationMs { get; init; } = DefaultDurationMs;
        public double PixelDurationMs { get; init; } = DefaultPixelDurationMs;
        public double DensityPercent { get; init; } = 100;
        public double GlowPercent { get; init; } = 100;

        public ReclaimAnimationSettings Normalised()
        {
            double duration = Clamp(DurationMs, DefaultDurationMs, MinDurationMs, MaxDurationMs);
            return this with
            {
                DurationMs = duration,
                PixelDurationMs = Clamp(PixelDurationMs, DefaultPixelDurationMs, MinPixelDurationMs,
                    Math.Min(MaxPixelDurationMs, duration)),
                DensityPercent = Clamp(DensityPercent, 100, MinDensityPercent, MaxDensityPercent),
                GlowPercent = Clamp(GlowPercent, 100, MinGlowPercent, MaxGlowPercent),
            };
        }

        private static double Clamp(double value, double fallback, double minimum, double maximum)
            => Math.Clamp(double.IsFinite(value) ? Math.Round(value) : fallback, minimum, maximum);
    }
}
