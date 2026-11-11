using System;

namespace Action_Wheel.Core
{
    /// <summary>Timing preferences for the desktop refresh animation.</summary>
    public sealed record RefreshAnimationSettings
    {
        public static readonly RefreshAnimationSettings Default = new();

        public const double DefaultDurationMs = 2500.0;
        public const double MinDurationMs = 600.0;
        public const double MaxDurationMs = 6000.0;

        public const double DefaultDistanceDelayPercent = 100.0;
        public const double MinDistanceDelayPercent = 0.0;
        public const double MaxDistanceDelayPercent = 200.0;

        public const double DefaultPixelDurationPercent = 100.0;
        public const double MinPixelDurationPercent = 25.0;
        public const double MaxPixelDurationPercent = 200.0;

        /// <summary>Total animation duration, in milliseconds.</summary>
        public double DurationMs { get; init; } = DefaultDurationMs;

        /// <summary>How strongly distance from the click delays each pixel block.</summary>
        public double DistanceDelayPercent { get; init; } = DefaultDistanceDelayPercent;

        /// <summary>How long each pixel block remains visible, relative to the default.</summary>
        public double PixelDurationPercent { get; init; } = DefaultPixelDurationPercent;

        /// <summary>Round preferences to whole numbers and bring them inside their legal ranges.</summary>
        public RefreshAnimationSettings Normalised()
        {
            return this with
            {
                DurationMs = Math.Clamp(
                    double.IsFinite(DurationMs) ? Math.Round(DurationMs) : DefaultDurationMs,
                    MinDurationMs, MaxDurationMs),
                DistanceDelayPercent = Math.Clamp(
                    double.IsFinite(DistanceDelayPercent) ? Math.Round(DistanceDelayPercent) : DefaultDistanceDelayPercent,
                    MinDistanceDelayPercent, MaxDistanceDelayPercent),
                PixelDurationPercent = Math.Clamp(
                    double.IsFinite(PixelDurationPercent) ? Math.Round(PixelDurationPercent) : DefaultPixelDurationPercent,
                    MinPixelDurationPercent, MaxPixelDurationPercent),
            };
        }
    }
}
