using System.Diagnostics;
using System.Numerics;
using Action_Wheel.Services;

if (!args.Contains("--performance-only")) SpectrumOverlayTests.Run();
if (!args.Contains("--correctness-only")) SpectrumOverlayTests.Measure();

internal static class SpectrumOverlayTests
{
    public static void Run()
    {
        CheckExternalEnvelope();
        CheckOverlay();
        CheckCompositing();
        Console.WriteLine("Spectrum overlay checks passed: actual reference atlas/rain/Gaussian/HSL with independent host timing, native base preservation at zero gain, positive light addition, origin retiming, PBGRA, stateless elapsed time, cancellation/reuse and transparent ending.");
    }

    private static RefreshSpectrumEffect Spectrum(int width, int height)
        => new(width, height, ReferenceEffectMath.LoadTexture("atlas.rgb.gz"),
            ReferenceEffectMath.LoadTexture("colorbar.rgb.gz"), ReferenceEffectMath.LoadTexture("spectrum-lut.rgb.gz"));

    private static void CheckExternalEnvelope()
    {
        const int width = 173, height = 97;
        var source = ReferenceTexture.FromBgra(Scene(width, height), width, height);
        float[] depth = Depth(width, height).Values, beforeDepth = (float[])depth.Clone();
        Vector3[] beforeSource = (Vector3[])source.Pixels.Clone();
        var prepared = Spectrum(width, height);
        var mutable = Spectrum(width, height);
        prepared.Prepare(source, depth);
        var a = new ReferenceTexture(width, height);
        var b = new ReferenceTexture(width, height);
        float[] envelope = new float[depth.Length];
        prepared.RenderWithEnvelope(source, depth, .4, envelope, a);
        Require(a.Pixels.AsSpan().SequenceEqual(source.Pixels), "A zero host envelope changed the source image.");

        // Independent reference shader band. Passing exactly the reference mask must preserve
        // the old path, including its prepared raw-depth glyph geometry and HSL operations.
        const double seconds = .913;
        float scanSpeed = (0.5f + 0.3333300054073334f * 1.5f) * 3.333f;
        float scanWidth = 0.3330000042915344f * .55f;
        float scan = (float)seconds * scanSpeed / 10;
        scan -= MathF.Floor(scan);
        for (int index = 0; index < depth.Length; index++)
        {
            float mask = Smooth(scan, scan + scanWidth, depth[index] * .5f + scanWidth);
            envelope[index] = Math.Clamp(Smooth(0, .5f, mask) - Smooth(.5f, 1, mask), 0, 1);
        }
        prepared.Render(source, depth, seconds, 1, a);
        prepared.RenderWithEnvelope(source, depth, seconds, envelope, b);
        Require(a.Pixels.AsSpan().SequenceEqual(b.Pixels), "External timing altered reference maths for the identical scan band.");
        mutable.RenderWithEnvelope(source, depth, seconds, envelope, a);
        Require(a.Pixels.AsSpan().SequenceEqual(b.Pixels), "Prepared and mutable raw-depth glyph geometry differ under host timing.");

        // At shader time zero the original scanner is unlit. A full host band must override
        // only that scanner and reveal reference glyph/rain light using the same raw depth.
        prepared.Render(source, depth, 0, 1, a);
        Array.Fill(envelope, 1);
        prepared.RenderWithEnvelope(source, depth, 0, envelope, b);
        Require(a.Pixels.AsSpan().SequenceEqual(source.Pixels)
            && b.Pixels.Where((pixel, index) => pixel != source.Pixels[index]).Any(),
            "Host timing did not independently activate the reference ASCII layer.");
        Array.Clear(envelope);
        prepared.RenderWithEnvelope(source, depth, 0, envelope, b);
        Require(b.Pixels.AsSpan().SequenceEqual(source.Pixels), "A cleared host band retained stale glyphs or blur.");
        Require(depth.AsSpan().SequenceEqual(beforeDepth) && source.Pixels.AsSpan().SequenceEqual(beforeSource),
            "Reference overlay timing modified raw depth or input colour.");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        bool cancelled = false;
        try { prepared.RenderWithEnvelope(source, depth, 0, envelope, b, cancel.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Require(cancelled, "A canceled reference overlay continued rendering.");
        prepared.Render(source, depth, seconds, 1, a);
        mutable.Render(source, depth, seconds, 1, b);
        Require(a.Pixels.AsSpan().SequenceEqual(b.Pixels), "Host timing/cancellation leaked into subsequent archived rendering.");
        Console.WriteLine("External spectrum envelope: zero-band identity, bit-identical reference-band oracle, prepared/mutable raw geometry, scan override, cleanup and cancellation passed.");
    }

    private static void CheckOverlay()
    {
        foreach ((int width, int height) in new[] { (1, 1), (7, 5), (417, 235) })
        {
            byte[] capture = Scene(width, height), before = (byte[])capture.Clone();
            var depth = Depth(39, 23);
            float[] beforeDepth = (float[])depth.Values.Clone();
            var baseRenderer = new UniformBlockRefreshRenderer(width, height, capture, depth, width / 2, height / 2);
            var disabled = new SpectrumOverlayRefreshRenderer(width, height, capture, depth, width / 2, height / 2, 0);
            var overlay = new SpectrumOverlayRefreshRenderer(width, height, capture, depth, width / 2, height / 2);
            Require(overlay.DurationMs == 2550 && overlay.BloomWidth == 1 && overlay.BloomHeight == 1,
                "The experimental layer changed the shared lifetime or output shape.");
            byte[] a = new byte[capture.Length], b = new byte[capture.Length], clean = new byte[capture.Length], light = new byte[4];
            foreach (double time in new[] { 0, 180, 700, 1300, 2100, 2549.9 })
            {
                baseRenderer.Render(time, a, light);
                disabled.Render(time, b, light);
                Require(a.AsSpan().SequenceEqual(b), "Zero spectrum gain changed the native base.");
            }
            double chosen = RefreshDepthSweep.StartDelayMs + 150;
            overlay.Render(chosen, a, light);
            overlay.Render(2100, b, light);
            overlay.Render(12, b, light);
            overlay.Render(chosen, b, light);
            Require(a.AsSpan().SequenceEqual(b), "A time jump or backward render altered a stateless ASCII frame.");
            overlay.Render(chosen, b, light);
            Require(a.AsSpan().SequenceEqual(b), "Repeated elapsed time changed the ASCII frame.");
            foreach (double time in new[] { 0, 180, 700, 1300, 2100, 2549.9 })
            {
                Array.Fill(b, (byte)239); Array.Fill(light, (byte)239);
                overlay.Render(time, b, light);
                overlay.Render(time, clean, light);
                Require(b.AsSpan().SequenceEqual(clean) && light.All(value => value == 0),
                    "Spectrum output depends on stale pixels or left a second light layer.");
                CheckPbgra(b);
            }
            int brighter = 0;
            for (int sample = 0; sample < 4; sample++)
            {
                double time = 200 + sample * 400;
                baseRenderer.Render(time, a, light); overlay.Render(time, b, light);
                for (int pixel = 0; pixel < width * height; pixel++)
                {
                    Vector3 addition = Visible(b, capture, pixel) - Visible(a, capture, pixel);
                    Require(Minimum(addition) >= -1.01f && Maximum(addition) <= 90.01f,
                        "Spectrum composition darkened the base or exceeded the explicit host gain.");
                    if (Maximum(addition) > 1) brighter++;
                }
            }
            if (width > 20) Require(brighter > width * height / 20, "The experiment added no visible reference ASCII light.");
            var geometry = overlay.BlocksRenderer.Blocks.ToArray();
            overlay.SetOrigin(0, 0); disabled.SetOrigin(0, 0);
            Require(overlay.OriginDepth == disabled.OriginDepth,
                "The ASCII layer and native blocks use different pointer depth.");
            Require(geometry.Zip(overlay.BlocksRenderer.Blocks.ToArray()).All(pair =>
                (pair.First with { StartMs = 0 }) == (pair.Second with { StartMs = 0 })),
                "Retiming the ASCII experiment changed native geometry or pigment.");
            foreach (double time in new[] { -1, double.NaN, double.PositiveInfinity, 2550, 3000 })
            {
                Array.Fill(b, (byte)239); Array.Fill(light, (byte)239);
                overlay.Render(time, b, light);
                Require(b.All(value => value == 0) && light.All(value => value == 0), "Ending/invalid spectrum time did not clear output.");
            }
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            bool cancelled = false;
            try { overlay.Render(900, b, light, cancel.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Require(cancelled, "Canceled spectrum playback continued.");
            overlay.Render(900, b, light); overlay.Render(900, clean, light);
            Require(b.AsSpan().SequenceEqual(clean), "Cancellation leaked renderer state.");
            Require(capture.AsSpan().SequenceEqual(before) && depth.Values.AsSpan().SequenceEqual(beforeDepth),
                "Spectrum playback changed capture or model depth.");
        }
    }

    private static void CheckCompositing()
    {
        var random = new Random(71267);
        float maximumError = 0;
        for (int sample = 0; sample < 4000; sample++)
        {
            uint source = (uint)random.Next(1 << 24);
            int alpha = random.Next(256);
            uint overlay = (uint)(random.Next(alpha + 1) | random.Next(alpha + 1) << 8
                | random.Next(alpha + 1) << 16 | alpha << 24);
            int blue = random.Next(90), green = random.Next(90), red = random.Next(90);
            uint output = UniformBlockRefreshRenderer.AddLight(overlay, source, blue, green, red);
            Vector3 expected = Vector3.Min(new Vector3(255), Visible(overlay, source) + new Vector3(red, green, blue));
            maximumError = Math.Max(maximumError, Maximum(Vector3.Abs(Visible(output, source) - expected)));
            Require((output & 255) <= output >> 24 && ((output >> 8) & 255) <= output >> 24
                && ((output >> 16) & 255) <= output >> 24, "In-place light composition produced invalid PBGRA.");
        }
        Require(maximumError <= 1.01f, "Capture-relative spectrum composition differs from RGB addition.");
        Console.WriteLine($"Spectrum visible RGB-addition oracle maximum error={maximumError:F6} byte.");
    }

    public static void Measure()
    {
        foreach ((int width, int height) in new[] { (1920, 1080), (3840, 2160) })
        {
            byte[] capture = Scene(width, height);
            var watch = Stopwatch.StartNew();
            var renderer = new SpectrumOverlayRefreshRenderer(width, height, capture, Depth(128, 72), width / 2, height / 2);
            double prepare = watch.Elapsed.TotalMilliseconds;
            byte[] frame = new byte[capture.Length], light = new byte[4];
            renderer.Render(700, frame, light);
            renderer.Render(1000, frame, light);
            renderer.SetOrigin(width / 3, height / 3);
            var originSamples = new List<double>();
            for (int index = 0; index < 20; index++)
            {
                watch.Restart(); renderer.SetOrigin(width * (index % 3 + 1) / 4, height * (index % 3 + 1) / 4);
                originSamples.Add(watch.Elapsed.TotalMilliseconds);
            }
            renderer.SetOrigin(width / 2, height / 2);
            originSamples.Sort();
            var samples = new List<double>();
            for (int index = 0; index < 30; index++)
            {
                watch.Restart(); renderer.Render(300 + index * 1700.0 / 30, frame, light);
                samples.Add(watch.Elapsed.TotalMilliseconds);
            }
            long before = GC.GetTotalAllocatedBytes(true);
            for (int index = 0; index < 5; index++) renderer.Render(800 + index * 17, frame, light);
            long allocated = GC.GetTotalAllocatedBytes(true) - before;
            Require(allocated < 1024 * 1024, "Spectrum playback allocated a full-screen frame buffer.");
            samples.Sort();
            Console.WriteLine($"Spectrum overlay native {width}x{height}: prepare={prepare:F2}ms, SetOrigin warm p50={originSamples[originSamples.Count / 2]:F2}ms/p95={originSamples[(int)(originSamples.Count * .95)]:F2}ms, active p50={samples[samples.Count / 2]:F2}ms, p95={samples[(int)(samples.Count * .95)]:F2}ms, max={samples[^1]:F2}ms; process-wide managed allocations={allocated} bytes/5 frames (excluding capture, model and UI).");
        }
    }

    private static float Smooth(float low, float high, float value)
    {
        float t = Math.Clamp((value - low) / (high - low), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static byte[] Scene(int width, int height)
    {
        var result = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int pixel = (y * width + x) * 4;
            result[pixel] = (byte)(30 + y * 140 / Math.Max(1, height - 1));
            result[pixel + 1] = (byte)(45 + x * 140 / Math.Max(1, width - 1));
            result[pixel + 2] = (byte)((x / 13 + y / 11) % 2 == 0 ? 80 : 175);
            result[pixel + 3] = (byte)(x + y);
        }
        return result;
    }

    private static RefreshDepthMap Depth(int width, int height)
    {
        float[] values = new float[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++) values[y * width + x] = (x + y) / (float)Math.Max(1, width + height - 2);
        return new RefreshDepthMap(width, height, values);
    }

    private static Vector3 Visible(byte[] layer, byte[] source, int pixel)
    {
        int offset = pixel * 4;
        return new Vector3(layer[offset + 2], layer[offset + 1], layer[offset])
            + new Vector3(source[offset + 2], source[offset + 1], source[offset]) * (1 - layer[offset + 3] / 255f);
    }
    private static Vector3 Visible(uint layer, uint source) => Rgb(layer) + Rgb(source) * (1 - (layer >> 24) / 255f);
    private static Vector3 Rgb(uint colour) => new((colour >> 16) & 255, (colour >> 8) & 255, colour & 255);
    private static float Minimum(Vector3 value) => Math.Min(value.X, Math.Min(value.Y, value.Z));
    private static float Maximum(Vector3 value) => Math.Max(value.X, Math.Max(value.Y, value.Z));
    private static void CheckPbgra(byte[] frame)
    {
        for (int offset = 0; offset < frame.Length; offset += 4)
            Require(frame[offset] <= frame[offset + 3] && frame[offset + 1] <= frame[offset + 3]
                && frame[offset + 2] <= frame[offset + 3], "Spectrum output is not premultiplied BGRA.");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
