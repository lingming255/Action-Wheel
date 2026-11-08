using System;

namespace Action_Wheel.Services
{
    internal enum RefreshPixelLayer
    {
        Background,
        Subject,
        Boundary,
    }

    internal readonly record struct RefreshEnvelope(double StartMs, double PeakMs, double EndMs);

    /// <summary>Shared radial-front timing and short, local flashes for the refresh effect.</summary>
    internal static class RefreshTiming
    {
        private const double ReferenceTotalMs = 350;
        private const double ReferenceSweepMs = 210;
        internal const double TotalMs = 800;
        internal const double TimeScale = TotalMs / ReferenceTotalMs;
        internal const double SweepMs = ReferenceSweepMs * TimeScale;
        internal const double CoverFadeMs = 95 * TimeScale;

        /// <summary>Slow an original 350 ms animation time without changing its relative rhythm.</summary>
        internal static double Scale(double milliseconds) => milliseconds * TimeScale;

        /// <summary>
        /// Earlier pixels finish sooner and also have a shorter local lifetime. A distant pixel
        /// gets a little more time to read without keeping the already-passed centre illuminated.
        /// </summary>
        internal static RefreshEnvelope Envelope(RefreshPixelLayer layer, double frontMs)
        {
            // Cluster starts are already scaled. Work in the original time domain and scale
            // the complete envelope once, including offsets, rises and local lifetimes.
            double front = Math.Clamp(frontMs / TimeScale, 0, ReferenceSweepMs);
            double progress = front / ReferenceSweepMs;
            double start, rise, lifetime;
            switch (layer)
            {
                case RefreshPixelLayer.Background:
                    start = Math.Max(0, front - 12);
                    rise = 20 + 5 * progress;
                    lifetime = 70 + 35 * progress;
                    break;
                case RefreshPixelLayer.Subject:
                    start = front + 7;
                    rise = 25 + 7 * progress;
                    lifetime = 100 + 25 * progress;
                    break;
                case RefreshPixelLayer.Boundary:
                    start = front + 18;
                    rise = 30 + 5 * progress;
                    lifetime = 110 + 10 * progress;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(layer));
            }
            return new RefreshEnvelope(Scale(start), Scale(start + rise),
                Scale(Math.Min(ReferenceTotalMs, start + lifetime)));
        }
    }
}
