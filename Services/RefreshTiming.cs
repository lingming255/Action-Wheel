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

        /// <summary>Fast launch and a gradual slowdown within the unchanged radial sweep budget.</summary>
        internal static double EaseSweepProgress(double timeProgress)
        {
            double t = Math.Clamp(timeProgress, 0, 1);
            return t * (2 - t);
        }

        /// <summary>Inverse of the sweep easing, applied after the geometric arrival lookup.</summary>
        internal static double SweepTimeProgress(double shapeProgress)
        {
            double q = Math.Clamp(shapeProgress, 0, 1);
            // Equivalent to 1-sqrt(1-q), without cancellation close to the click.
            return q / (1 + Math.Sqrt(1 - q));
        }

        /// <summary>Circular reference timing; live gestures use a shared <see cref="RefreshWavefront"/>.</summary>
        internal static double FrontAt(double x, double y, int width, int height, int originX, int originY)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "The monitor must have positive dimensions.");

            double farX = Math.Max(originX, width - (double)originX);
            double farY = Math.Max(originY, height - (double)originY);
            double farthest = Math.Sqrt(farX * farX + farY * farY);
            double dx = x - originX, dy = y - originY;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            return Math.Clamp(distance / farthest, 0, 1) * SweepMs;
        }

        /// <summary>
        /// Earlier pixels finish sooner and also have a shorter local lifetime. A distant pixel
        /// gets a little more time to read without keeping the already-passed centre illuminated.
        /// </summary>
        internal static RefreshEnvelope Envelope(RefreshPixelLayer layer, double frontMs)
        {
            // Arrival times are already scaled. Work in the original time domain and scale
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
