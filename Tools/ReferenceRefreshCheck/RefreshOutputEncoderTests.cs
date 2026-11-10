using System;
using System.Numerics;
using System.Threading;
using Action_Wheel.Services;

internal static class RefreshOutputEncoderTests
{
    public static void Run()
    {
        double maximum = 0;
        Check(1, 1, 1, 1, ref maximum);
        Check(33, 17, 7, 5, ref maximum);
        // Above the encoder's band threshold, with an uneven band height and SIMD tail.
        // Every output pixel is checked, including the last row of each band.
        Check(641, 411, 137, 89, ref maximum);
        CheckEndpoints();
        Console.WriteLine($"Refresh output encoder checks passed: native colour correction, SIMD/tail, band boundaries, alpha, cancellation, clear and immutable inputs; maximum visible error={maximum:F6} byte.");
    }

    private static void Check(int width, int height, int workWidth, int workHeight, ref double maximum)
    {
        var random = new Random(781 + width);
        var capture = new byte[checked(width * height * 4)];
        random.NextBytes(capture); // Includes undefined screenshot alpha.
        Vector3[] extremes = [Vector3.Zero, Vector3.One, Vector3.UnitX, Vector3.UnitY,
            Vector3.UnitZ, new Vector3(1, 1, 0), new Vector3(0.5f)];
        for (int p = 0; p < width * height; p++)
        {
            int kind = p % 11;
            if (kind >= extremes.Length) continue;
            Vector3 colour = extremes[kind] * 255;
            capture[p * 4] = (byte)colour.Z;
            capture[p * 4 + 1] = (byte)colour.Y;
            capture[p * 4 + 2] = (byte)colour.X;
        }

        var source = new ReferenceTexture(workWidth, workHeight);
        var final = new ReferenceTexture(workWidth, workHeight);
        for (int p = 0; p < source.Pixels.Length; p++)
        {
            source.Pixels[p] = new Vector3((float)random.NextDouble(),
                (float)random.NextDouble(), (float)random.NextDouble());
            final.Pixels[p] = (p % 9) switch
            {
                0 => Vector3.Zero,
                1 => Vector3.One,
                2 => Vector3.UnitX,
                3 => Vector3.UnitY,
                4 => Vector3.UnitZ,
                5 => source.Pixels[p],
                _ => new Vector3((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble()),
            };
        }
        byte[] captureBefore = (byte[])capture.Clone();
        Vector3[] sourceBefore = (Vector3[])source.Pixels.Clone();
        Vector3[] finalBefore = (Vector3[])final.Pixels.Clone();
        var encoder = new RefreshOutputEncoder(width, height, capture, source);
        var actual = new byte[capture.Length];
        var expected = new byte[capture.Length];
        Array.Fill(actual, (byte)0xBD);
        Oracle(capture, width, height, source, final, expected);
        encoder.Encode(final, actual);
        AssertVisible(capture, expected, actual, ref maximum);

        byte[] changedAlpha = (byte[])capture.Clone();
        for (int i = 3; i < changedAlpha.Length; i += 4) changedAlpha[i] ^= 255;
        var alphaIndependent = new byte[actual.Length];
        new RefreshOutputEncoder(width, height, changedAlpha, source).Encode(final, alphaIndependent);
        Require(actual.AsSpan().SequenceEqual(alphaIndependent), "Undefined capture alpha affected the correction.");

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            byte[] beforeCancel = (byte[])actual.Clone();
            bool observed = false;
            try { encoder.Encode(final, actual, cancelled.Token); }
            catch (OperationCanceledException) { observed = true; }
            Require(observed && actual.AsSpan().SequenceEqual(beforeCancel), "Pre-cancelled output was modified or cancellation was ignored.");
        }
        encoder.Encode(source, actual);
        foreach (byte value in actual) Require(value == 0, "Unchanged frame left a stale native output pixel.");
        encoder.Encode(final, actual);
        AssertVisible(capture, expected, actual, ref maximum);
        Require(capture.AsSpan().SequenceEqual(captureBefore), "Encoding changed the capture.");
        Require(source.Pixels.AsSpan().SequenceEqual(sourceBefore), "Encoding changed the source work texture.");
        Require(final.Pixels.AsSpan().SequenceEqual(finalBefore), "Encoding changed the final work texture.");
    }

    private static void CheckEndpoints()
    {
        byte[] capture = [0, 0, 0, 11, 255, 255, 255, 19, 255, 0, 0, 37, 0, 255, 0, 91];
        var source = new ReferenceTexture(4, 1, [Vector3.Zero, Vector3.One, Vector3.UnitZ, Vector3.UnitY]);
        var final = new ReferenceTexture(4, 1, [Vector3.One, Vector3.Zero, Vector3.UnitX, Vector3.UnitZ]);
        var expected = new byte[capture.Length];
        var actual = new byte[capture.Length];
        Oracle(capture, 4, 1, source, final, expected);
        new RefreshOutputEncoder(4, 1, capture, source).Encode(final, actual);
        double maximum = 0;
        AssertVisible(capture, expected, actual, ref maximum);
        Require(actual[3] == 255 && actual[0] == 255 && actual[1] == 255 && actual[2] == 255,
            "Black-to-white correction failed.");
        Require(actual[7] == 255 && actual[4] == 0 && actual[5] == 0 && actual[6] == 0,
            "White-to-black correction failed.");
    }

    private static void AssertVisible(byte[] capture, byte[] expected, byte[] actual, ref double maximum)
    {
        for (int i = 0; i < actual.Length; i += 4)
        {
            int alpha = actual[i + 3];
            for (int channel = 0; channel < 3; channel++)
            {
                Require(actual[i + channel] <= alpha, "Output is not legal premultiplied BGRA.");
                // Exact rational comparison avoids float noise around the one-byte boundary.
                long numerator = (expected[i + channel] - actual[i + channel]) * 255L
                    + capture[i + channel] * (actual[i + 3] - expected[i + 3]);
                double error = Math.Abs(numerator) / 255.0;
                maximum = Math.Max(maximum, error);
                Require(error <= 1.01, "Visible correction differs from the original encoder by more than one byte.");
            }
        }
    }

    private static void Oracle(byte[] capture, int width, int height, ReferenceTexture sourceWork,
        ReferenceTexture final, byte[] output)
    {
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = (y * width + x) * 4;
            float u = (x + 0.5f) / width, v = (y + 0.5f) / height;
            Vector3 source = new(capture[offset + 2] / 255f, capture[offset + 1] / 255f, capture[offset] / 255f);
            Vector3 target = Vector3.Clamp(source + (final.Sample(u, v) - sourceWork.Sample(u, v)), Vector3.Zero, Vector3.One);
            float alpha = Math.Max(Coverage(source.X, target.X), Math.Max(Coverage(source.Y, target.Y), Coverage(source.Z, target.Z)));
            int a = Math.Clamp((int)MathF.Ceiling(alpha * 255), 0, 255);
            float remaining = 1 - a / 255f;
            output[offset] = Channel(target.Z - remaining * source.Z, a);
            output[offset + 1] = Channel(target.Y - remaining * source.Y, a);
            output[offset + 2] = Channel(target.X - remaining * source.X, a);
            output[offset + 3] = (byte)a;
        }
    }

    private static float Coverage(float source, float target)
        => target >= source ? (source < 1 ? (target - source) / (1 - source) : 0)
            : (source > 0 ? (source - target) / source : 0);
    private static byte Channel(float value, int alpha)
        => (byte)Math.Clamp((int)MathF.Round(value * 255), 0, alpha);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
