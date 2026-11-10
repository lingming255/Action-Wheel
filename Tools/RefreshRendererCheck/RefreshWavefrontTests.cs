using Action_Wheel.Services;
using System.Diagnostics;

internal static class RefreshWavefrontTests
{
    internal static void Run()
    {
        const int width = 1920, height = 1080, originX = 610, originY = 460;
        var timer = Stopwatch.StartNew();
        var wave = new RefreshWavefront(width, height, originX, originY, 1729);
        timer.Stop();
        var repeated = new RefreshWavefront(width, height, originX, originY, 1729);
        var changed = new RefreshWavefront(width, height, originX, originY, 1730);
        double farthest = Math.Sqrt(Math.Pow(Math.Max(originX, width - originX), 2)
            + Math.Pow(Math.Max(originY, height - originY), 2));
        double biggestArrivalError = 0, biggestTailError = 0, seedDifference = 0, evolvingDifference = 0;
        for (int direction = 0; direction < 79; direction++)
        {
            double angle = direction * Math.PI * 2 / 79 + 0.0047;
            double previous = -1;
            for (int step = 0; step <= 1000; step++)
            {
                double p = step / 1000.0;
                double radius = wave.RadiusAt(angle, p);
                Require(radius >= 0 && radius <= 1, "radius stays inside normalized bounds");
                Require(radius > previous, "every angle moves strictly outwards without a recrossing");
                previous = radius;
                Require(Math.Abs(radius - wave.RadiusAt(angle + Math.PI * 2, p)) < 1e-12,
                    "the angular seam is periodic");
                Require(radius == repeated.RadiusAt(angle, p), "the same seed gives identical evolution");
                seedDifference += Math.Abs(radius - changed.RadiusAt(angle, p));
                if (step % 17 != 0) continue;
                double x = originX + Math.Cos(angle) * radius * farthest;
                double y = originY + Math.Sin(angle) * radius * farthest;
                double time = wave.FrontAt(x, y);
                biggestArrivalError = Math.Max(biggestArrivalError,
                    Math.Abs(time - p * RefreshTiming.SweepMs));
            }
            Require(wave.RadiusAt(angle, 0) == 0 && wave.RadiusAt(angle, 1) == 1,
                "the exact start/end radii preserve the sweep budget");
            // Shape modulation lives in geometric progress q. Query wall times that reach two
            // chosen q values, then remove q's amplitude envelope. A static scalloped circle
            // would have identical deviations after removing this envelope.
            const double earlyShape = 0.25, lateShape = 0.65;
            double earlyTime = 1 - Math.Sqrt(1 - earlyShape);
            double lateTime = 1 - Math.Sqrt(1 - lateShape);
            double early = (wave.RadiusAt(angle, earlyTime) - earlyShape) / Math.Sin(Math.PI * earlyShape);
            double late = (wave.RadiusAt(angle, lateTime) - lateShape) / Math.Sin(Math.PI * lateShape);
            evolvingDifference += Math.Abs(early - late);

            // These observations are independent of the easing helpers: halfway through the
            // wall-clock sweep the nominal geometric radius is .75, with only the soft wobble.
            double halfwayRadius = wave.RadiusAt(angle, 0.5);
            Require(halfwayRadius is >= 0.72 and <= 0.78,
                "the wave covers about three quarters of its radius during the first half of time");
            double halfRadiusTime = wave.FrontAt(originX + Math.Cos(angle) * farthest * 0.5,
                originY + Math.Sin(angle) * farthest * 0.5);
            Require(halfRadiusTime > 0 && halfRadiusTime < RefreshTiming.SweepMs * 0.38,
                "every direction reaches half radius clearly before half of sweep time");

            // The inverse easing becomes sensitive as q approaches one. Exercise the very last
            // portion directly, where cancellation or a poorly interpolated inverse causes drift.
            foreach (double timeProgress in new[] { 0.9, 0.95, 0.98, 0.99, 0.995, 0.999 })
            {
                double radius = wave.RadiusAt(angle, timeProgress);
                double arrival = wave.FrontAt(originX + Math.Cos(angle) * radius * farthest,
                    originY + Math.Sin(angle) * radius * farthest);
                biggestTailError = Math.Max(biggestTailError,
                    Math.Abs(arrival - timeProgress * RefreshTiming.SweepMs));
            }
        }
        Require(seedDifference > 1, "different gesture seeds produce different edges");
        Require(evolvingDifference > 0.1, "edge shape changes during expansion rather than merely scaling");
        Require(biggestArrivalError < 1, "inverse lookup reaches the evolving front within one millisecond");
        Require(biggestTailError < 1, "late ease-out arrival remains accurate within one millisecond");
        Require(Math.Abs(RefreshTiming.EaseSweepProgress(0.5) - 0.75) < 1e-12,
            "the sweep uses quadratic ease-out rather than changing the overall duration");
        Require(Math.Abs(RefreshTiming.SweepTimeProgress(0.5) - (1 - Math.Sqrt(0.5))) < 1e-12,
            "the easing inverse preserves the expected half-radius wall time");
        foreach (double timeProgress in new[] { 0d, 0.5, 0.9, 0.95, 0.98, 0.99, 0.995, 0.999, 1d })
            Require(Math.Abs(RefreshTiming.SweepTimeProgress(RefreshTiming.EaseSweepProgress(timeProgress))
                - timeProgress) * RefreshTiming.SweepMs < 1e-6,
                "the easing and its stable inverse agree through both endpoints and the late tail");
        Require(wave.FrontAt(originX, originY) == 0, "the click has zero arrival delay");
        foreach ((int x, int y) in new[] { (0, 0), (width, 0), (0, height), (width, height) })
            Require(wave.FrontAt(x, y) <= RefreshTiming.SweepMs, "every monitor corner arrives inside the sweep");
        Require(RefreshTiming.Envelope(RefreshPixelLayer.Boundary, RefreshTiming.SweepMs).EndMs
            <= RefreshTiming.TotalMs, "the last irregular-front particles finish by 800 milliseconds");
        // Compare both sides of the atan2 wrap, where an unwrapped LUT would make an obvious seam.
        double seamA = wave.FrontAt(originX + farthest * 0.6, originY + 1e-7);
        double seamB = wave.FrontAt(originX + farthest * 0.6, originY - 1e-7);
        Require(Math.Abs(seamA - seamB) < 1e-5, "inverse lookup is continuous through angle zero");
        Console.WriteLine($"Refresh wavefront check passed: prepare={timer.Elapsed.TotalMilliseconds:F2}ms, "
            + $"max inverse error={biggestArrivalError:F4}ms, tail error={biggestTailError:F4}ms; "
            + "periodic, seeded, evolving, monotone and initially fast.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Refresh wavefront check failed: {message}.");
    }
}
