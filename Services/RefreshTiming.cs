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
        internal const double TotalMs = 350;
        internal const double SweepMs = 210;
        internal const double CoverFadeMs = 95;

        /// <summary>
        /// Earlier pixels finish sooner and also have a shorter local lifetime. A distant pixel
        /// gets a little more time to read without keeping the already-passed centre illuminated.
        /// </summary>
        internal static RefreshEnvelope Envelope(RefreshPixelLayer layer, double frontMs)
        {
            double front = Math.Clamp(frontMs, 0, SweepMs);
            double progress = front / SweepMs;
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
            return new RefreshEnvelope(start, start + rise, Math.Min(TotalMs, start + lifetime));
        }
    }
}
