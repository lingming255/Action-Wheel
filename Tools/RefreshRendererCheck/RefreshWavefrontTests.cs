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
        double biggestArrivalError = 0, seedDifference = 0, evolvingDifference = 0;
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
            // Remove the amplitude envelope. A static scalloped circle would have identical
            // angular deviation at both times after removing this envelope.
            double early = (wave.RadiusAt(angle, 0.25) - 0.25) / Math.Sin(Math.PI * 0.25);
            double late = (wave.RadiusAt(angle, 0.65) - 0.65) / Math.Sin(Math.PI * 0.65);
            evolvingDifference += Math.Abs(early - late);
        }
        Require(seedDifference > 1, "different gesture seeds produce different edges");
        Require(evolvingDifference > 0.1, "edge shape changes during expansion rather than merely scaling");
        Require(biggestArrivalError < 1, "inverse lookup reaches the evolving front within one millisecond");
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
            + $"max inverse error={biggestArrivalError:F4}ms; periodic, seeded, evolving and monotone.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Refresh wavefront check failed: {message}.");
    }
}
