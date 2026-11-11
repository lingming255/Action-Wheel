using System;
using Action_Wheel.Core;

namespace Action_Wheel.Services
{
    /// <summary>One immutable timing snapshot shared by capture, both renderers and window lifetime.</summary>
    internal sealed class RefreshTimingProfile
    {
        internal static readonly RefreshTimingProfile Default = new(RefreshAnimationSettings.Default);
        internal RefreshAnimationSettings Settings { get; }
        internal double PlaybackDurationMs => Settings.DurationMs;
        internal double DistanceDelayMs { get; }
        internal double LifetimeMs { get; }
        internal double RiseMs { get; }
        internal double FadeStartMs { get; }
        internal double BackgroundRestoreMs { get; }
        internal double ReferenceDurationMs { get; }
        internal double ReferenceTimeRatio => ReferenceDurationMs / PlaybackDurationMs;

        internal RefreshTimingProfile(RefreshAnimationSettings settings)
        {
            Settings = settings.Normalised();
            DistanceDelayMs = RefreshDepthSweep.DistanceDelayMs * Settings.DistanceDelayPercent / 100;
            double pulse = Settings.PixelDurationPercent / 100;
            LifetimeMs = RefreshDepthSweep.LifetimeMs * pulse;
            RiseMs = RefreshDepthSweep.RiseMs * pulse;
            FadeStartMs = RefreshDepthSweep.FadeStartMs * pulse;
            BackgroundRestoreMs = RefreshDepthSweep.BackgroundRestoreMs * pulse;
            // Preserve the accepted 81 ms tail. Fitting the entire reference span into the
            // requested duration keeps every allowed combination from clipping late pixels.
            const double tail = RefreshDepthSweep.DurationMs - RefreshDepthSweep.StartDelayMs
                - RefreshDepthSweep.DepthTravelMs - RefreshDepthSweep.DistanceDelayMs - RefreshDepthSweep.LifetimeMs;
            ReferenceDurationMs = RefreshDepthSweep.StartDelayMs + RefreshDepthSweep.DepthTravelMs
                + DistanceDelayMs + LifetimeMs + tail;
        }

        internal double StartAt(float depth, float originDepth, double normalizedDistance)
            => RefreshDepthSweep.StartAt(depth, originDepth, normalizedDistance)
                + (DistanceDelayMs - RefreshDepthSweep.DistanceDelayMs) * normalizedDistance;
    }
}
