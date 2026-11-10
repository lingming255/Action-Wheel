using System;

namespace Action_Wheel.Services
{
    /// <summary>
    /// A seeded, continuously changing circular wave with a gently uneven edge. Arrival times
    /// are inverted once during preparation; playback uses only the already assigned particle
    /// times, so the edge evolves without per-frame trigonometry or particles flashing back on.
    /// </summary>
    internal sealed class RefreshWavefront
    {
        private const int AngleSamples = 512;
        private const int DistanceSamples = 256;
        private const int TimeSamples = 1024;
        private const double TwoPi = Math.PI * 2;
        private const double Amplitude = 0.04;
        private readonly int _originX, _originY;
        private readonly double _farthest;
        private readonly double _phase0, _phase1, _phase2;
        private readonly double[] _arrival = new double[AngleSamples * (DistanceSamples + 1)];

        public RefreshWavefront(int width, int height, int originX, int originY, int seed)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "Wave bounds must have positive dimensions.");
            if (originX < 0 || originX > width || originY < 0 || originY > height)
                throw new ArgumentOutOfRangeException(nameof(originX), "Wave origin must lie inside its bounds.");
            _originX = originX; _originY = originY;
            double farX = Math.Max(originX, width - (double)originX);
            double farY = Math.Max(originY, height - (double)originY);
            _farthest = Math.Sqrt(farX * farX + farY * farY);
            var random = new Random(seed);
            _phase0 = random.NextDouble() * TwoPi;
            _phase1 = random.NextDouble() * TwoPi;
            _phase2 = random.NextDouble() * TwoPi;
            PrepareArrival();
        }

        /// <summary>Arrival at a physical source position, in milliseconds of the radial sweep.</summary>
        public double FrontAt(double x, double y)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y))
                throw new ArgumentOutOfRangeException(nameof(x), "Wave position must be finite.");
            double dx = x - _originX, dy = y - _originY;
            double distance = Math.Sqrt(dx * dx + dy * dy) / _farthest;
            if (distance <= 0) return 0;
            if (distance >= 1) return RefreshTiming.SweepMs;
            double angle = Math.Atan2(dy, dx);
            if (angle < 0) angle += TwoPi;
            double angleIndex = angle * AngleSamples / TwoPi;
            int firstAngle = (int)angleIndex;
            // Atan2 may round an infinitesimal negative angle to 2*pi after wrapping.
            if (firstAngle >= AngleSamples) firstAngle = 0;
            int secondAngle = (firstAngle + 1) % AngleSamples;
            double angleFraction = angleIndex >= AngleSamples ? 0 : angleIndex - firstAngle;
            double distanceIndex = distance * DistanceSamples;
            int firstDistance = (int)distanceIndex;
            double distanceFraction = distanceIndex - firstDistance;
            int first = firstAngle * (DistanceSamples + 1) + firstDistance;
            int second = secondAngle * (DistanceSamples + 1) + firstDistance;
            double a = Lerp(_arrival[first], _arrival[first + 1], distanceFraction);
            double b = Lerp(_arrival[second], _arrival[second + 1], distanceFraction);
            double shapeProgress = Lerp(a, b, angleFraction) / RefreshTiming.SweepMs;
            return RefreshTiming.SweepTimeProgress(shapeProgress) * RefreshTiming.SweepMs;
        }

        /// <summary>Normalized radius at an angle and elapsed sweep progress; useful for checks.</summary>
        internal double RadiusAt(double angle, double progress)
        {
            if (!double.IsFinite(angle) || !double.IsFinite(progress))
                throw new ArgumentOutOfRangeException(nameof(progress));
            if (progress <= 0) return 0;
            if (progress >= 1) return 1;
            double q = RefreshTiming.EaseSweepProgress(progress);
            double noise = 0.50 * Math.Sin(3 * angle + _phase0 + TwoPi * 0.55 * q)
                + 0.32 * Math.Sin(7 * angle + _phase1 - TwoPi * 0.90 * q)
                + 0.18 * Math.Sin(13 * angle + _phase2 + TwoPi * 1.30 * q);
            return q + Amplitude * Math.Sin(Math.PI * q) * noise;
        }

        private void PrepareArrival()
        {
            // Keep the lookup in uneased geometric progress. FrontAt applies inverse easing
            // only after interpolation, avoiding a poorly sampled slow tail in the inverse table.
            // Separate angle/time sinusoids so a new gesture needs only a few thousand trig
            // calls. The 512*1024 inner loop is just multiply/add plus a monotone scan.
            var sin0 = new double[TimeSamples + 1]; var cos0 = new double[TimeSamples + 1];
            var sin1 = new double[TimeSamples + 1]; var cos1 = new double[TimeSamples + 1];
            var sin2 = new double[TimeSamples + 1]; var cos2 = new double[TimeSamples + 1];
            var envelope = new double[TimeSamples + 1];
            for (int time = 0; time <= TimeSamples; time++)
            {
                double p = time / (double)TimeSamples;
                (sin0[time], cos0[time]) = Math.SinCos(TwoPi * 0.55 * p);
                (sin1[time], cos1[time]) = Math.SinCos(-TwoPi * 0.90 * p);
                (sin2[time], cos2[time]) = Math.SinCos(TwoPi * 1.30 * p);
                envelope[time] = Amplitude * Math.Sin(Math.PI * p);
            }
            for (int angleIndex = 0; angleIndex < AngleSamples; angleIndex++)
            {
                double angle = angleIndex * TwoPi / AngleSamples;
                (double s0, double c0) = Math.SinCos(3 * angle + _phase0);
                (double s1, double c1) = Math.SinCos(7 * angle + _phase1);
                (double s2, double c2) = Math.SinCos(13 * angle + _phase2);
                int row = angleIndex * (DistanceSamples + 1), distanceIndex = 1;
                double previousRadius = 0;
                for (int time = 1; time <= TimeSamples && distanceIndex <= DistanceSamples; time++)
                {
                    double progress = time / (double)TimeSamples;
                    double noise = 0.50 * (s0 * cos0[time] + c0 * sin0[time])
                        + 0.32 * (s1 * cos1[time] + c1 * sin1[time])
                        + 0.18 * (s2 * cos2[time] + c2 * sin2[time]);
                    double radius = time == TimeSamples ? 1 : progress + envelope[time] * noise;
                    // |N|<=1 and |dN/dp|<=2*pi*(.50*.55+.32*.90+.18*1.30).
                    // Therefore dRadius/dShapeProgress >= 1-.04*(pi+5.008) > .674 for every seed/angle.
                    // Every distance is crossed exactly once, making this inverse well-defined.
                    while (distanceIndex <= DistanceSamples
                        && distanceIndex / (double)DistanceSamples <= radius)
                    {
                        double target = distanceIndex / (double)DistanceSamples;
                        double fraction = (target - previousRadius) / (radius - previousRadius);
                        _arrival[row + distanceIndex] = (time - 1 + fraction)
                            / TimeSamples * RefreshTiming.SweepMs;
                        distanceIndex++;
                    }
                    previousRadius = radius;
                }
                _arrival[row + DistanceSamples] = RefreshTiming.SweepMs;
            }
        }

        private static double Lerp(double from, double to, double amount) => from + (to - from) * amount;
    }
}
