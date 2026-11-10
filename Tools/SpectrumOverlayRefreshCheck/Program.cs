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
        CheckDistanceArrival();
        CheckSaturationEnvelope();
        CheckCompositing();
        Console.WriteLine("Spectrum overlay checks passed: actual reference atlas/rain/Gaussian/HSL with independent host timing, native base preservation at zero gain, positive light addition, shared depth/distance arrival and saturation envelope, origin retiming, PBGRA, stateless elapsed time, cancellation/reuse and transparent ending.");
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
            Require(overlay.DurationMs == 3000 && overlay.BloomWidth == 1 && overlay.BloomHeight == 1,
                "The experimental layer changed the shared lifetime or output shape.");
            byte[] a = new byte[capture.Length], b = new byte[capture.Length], clean = new byte[capture.Length], light = new byte[4];
            foreach (double time in new[] { 0, 180, 700, 1300, 2100, 2790, 2999.9 })
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
            foreach (double time in new[] { 0, 180, 700, 1300, 2100, 2790, 2999.9 })
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
            foreach (double time in new[] { -1, double.NaN, double.PositiveInfinity, 3000, 3250 })
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

    private static void CheckDistanceArrival()
    {
        // Inspect the prepared arrival map against the product formula, independently of
        // the production distance helpers. This also exercises work-to-native coordinates.
        foreach ((int width, int height) in new[] { (1, 1), (1, 73), (7, 5), (417, 235), (1281, 9) })
        {
            var renderer = new SpectrumOverlayRefreshRenderer(width, height, Scene(width, height), Depth(9, 7), 0, 0);
            byte[] initial = new byte[width * height * 4], initialLight = new byte[4];
            Array.Fill(initial, (byte)239);
            renderer.Render(0, initial, initialLight);
            for (int offset = 0; offset < initial.Length; offset += 4)
                Require(initial[offset] == initial[offset + 1] && initial[offset + 1] == initial[offset + 2]
                    && initial[offset + 3] == 255,
                    "The zero-band ASCII host leaked source colour or transparency into the initial full-grey desktop.");
            double scale = Math.Min(1, 1280.0 / Math.Max(width, height));
            int workWidth = Math.Max(1, (int)Math.Round(width * scale));
            int workHeight = Math.Max(1, (int)Math.Round(height * scale));
            double[] starts = (double[])typeof(SpectrumOverlayRefreshRenderer)
                .GetField("_starts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(renderer)!;
            Require(starts.Length == workWidth * workHeight, "The ASCII arrival map has unexpected dimensions.");
            foreach ((int requestedX, int requestedY) in new[] { (-20, -30), (width / 2, height / 2), (width + 3, height + 5), (0, height - 1) })
            {
                renderer.SetOrigin(requestedX, requestedY);
                int originX = Math.Clamp(requestedX, 0, width - 1), originY = Math.Clamp(requestedY, 0, height - 1);
                double originDepth = PlaneDepth((originX + .5) / width, (originY + .5) / height);
                double range = Math.Max(originDepth, 1 - originDepth);
                double dx = Math.Max(originX, width - 1 - originX), dy = Math.Max(originY, height - 1 - originY);
                double farthest = Math.Sqrt(dx * dx + dy * dy);
                Require(Math.Abs(renderer.OriginDepth - originDepth) < 1e-6, "The ASCII origin is not the actual clamped release pixel.");
                for (int y = 0; y < workHeight; y++)
                for (int x = 0; x < workWidth; x++)
                {
                    double centerX = (x + .5) * width / workWidth, centerY = (y + .5) * height / workHeight;
                    double depth = PlaneDepth((x + .5) / workWidth, (y + .5) / workHeight);
                    double offsetX = centerX - (originX + .5), offsetY = centerY - (originY + .5);
                    double distance = farthest == 0 ? 0 : Math.Clamp(Math.Sqrt(offsetX * offsetX + offsetY * offsetY) / farthest, 0, 1);
                    double expected = 119 + 1590 * Math.Abs(depth - originDepth) / range + 700 * distance;
                    double actual = starts[y * workWidth + x];
                    Require(Math.Abs(actual - expected) < .002, "The ASCII layer does not share the independent depth/distance arrival formula.");
                    Require(actual >= 119 && actual + 510 <= 3000, "Distance delay exceeds the existing animation lifetime.");
                }
            }
        }

        const int flatWidth = 101, flatHeight = 71, flatOriginX = 50, flatOriginY = 35;
        var flatDepth = new RefreshDepthMap(1, 1, new[] { .4f });
        var flat = new SpectrumOverlayRefreshRenderer(flatWidth, flatHeight, Scene(flatWidth, flatHeight), flatDepth, flatOriginX, flatOriginY);
        double[] arrivals = (double[])typeof(SpectrumOverlayRefreshRenderer)
            .GetField("_starts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(flat)!;
        double At(int x, int y) => arrivals[y * flatWidth + x];
        Require(At(50, 35) == 119 && At(60, 35) < At(80, 35) && At(80, 35) < At(100, 35),
            "Flat-depth ASCII pixels do not activate progressively away from the release pixel.");
        Require(At(40, 35) == At(60, 35) && At(50, 25) == At(50, 45),
            "Equal spatial distances at equal depth have different ASCII arrival times.");
        Console.WriteLine("Spectrum distance arrival: independent 119+1590*depth+700*distance oracle, actual clamped release pixel, flat-depth radial order, equal-distance symmetry, opaque full-grey initial native pixels (including 1281-to-1280 work scaling), 1x1/narrow/corner cases and 3000ms duration passed.");
    }

    private static double PlaneDepth(double u, double v)
        => (Math.Clamp(u * 9 - .5, 0, 8) + Math.Clamp(v * 7 - .5, 0, 6)) / 14;

    private static void CheckSaturationEnvelope()
    {
        const int width = 97, height = 73;
        var renderer = new SpectrumOverlayRefreshRenderer(width, height, Scene(width, height), Depth(17, 11), width / 2, height / 2);
        T Field<T>(string name) => (T)typeof(SpectrumOverlayRefreshRenderer)
            .GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(renderer)!;
        var source = Field<ReferenceTexture>("_source");
        var actualDelta = Field<ReferenceTexture>("_spectrumFrame");
        float[] depth = Field<float[]>("_depth"), envelope = Field<float[]>("_envelope");
        double[] starts = Field<double[]>("_starts");
        var reference = Spectrum(width, height);
        reference.Prepare(source, depth);
        var ungraded = new ReferenceTexture(width, height);
        byte[] output = new byte[width * height * 4], light = new byte[4];
        int fadedSamples = 0, peakSamples = 0, greySamples = 0;
        float maximumError = 0;
        foreach (double time in new[] { 130, 250, 500, 900, 1500, 2100, 2700 })
        {
            renderer.Render(time, output, light);
            reference.RenderWithEnvelope(source, depth, time / (1000 * .85), envelope, ungraded);
            for (int pixel = 0; pixel < starts.Length; pixel++)
            {
                Vector3 positive = Vector3.Max(Vector3.Zero, ungraded.Pixels[pixel] - source.Pixels[pixel]);
                double age = time - starts[pixel];
                double rise = Math.Clamp(age / 85, 0, 1), fall = Math.Clamp((age - 187) / 323, 0, 1);
                double band = age <= 0 || age >= 510 ? 0 : rise * rise * (3 - 2 * rise) * (1 - fall * fall * (3 - 2 * fall));
                float saturation = (float)band;
                float luma = .2126f * positive.X + .7152f * positive.Y + .0722f * positive.Z;
                Vector3 expected = (new Vector3(luma) * (1 - saturation) + positive * saturation) * (.35f * 255);
                Vector3 actual = actualDelta.Pixels[pixel];
                maximumError = Math.Max(maximumError, Maximum(Vector3.Abs(actual - expected)));
                Require(Minimum(actual) >= 0 && Maximum(actual) <= 89.251f,
                    "Host saturation made the ASCII light negative or exceeded its unchanged gain.");
                float originalChroma = Maximum(positive) - Minimum(positive);
                if (originalChroma > .01f)
                {
                    Require(Math.Abs((Maximum(actual) - Minimum(actual)) - originalChroma * saturation * (.35f * 255)) < .001,
                        "ASCII chroma does not follow the whole-pixel age envelope.");
                    if (saturation < .98f) fadedSamples++;
                    if (saturation > .995f) peakSamples++;
                    if (saturation == 0)
                    {
                        Require(Maximum(actual) - Minimum(actual) < .001f,
                            "The ASCII light retained chroma outside its 0-to-1-to-0 saturation envelope.");
                        greySamples++;
                    }
                }
            }
            CheckPbgra(output);
        }
        Require(maximumError < .001f && fadedSamples > 100 && peakSamples > 20 && greySamples > 20,
            "The independent Rec709 ASCII saturation oracle failed or did not cover zero chroma, fades and full colour.");
        Console.WriteLine($"Spectrum host saturation: positive reference-A delta, independent Rec709/age oracle, 0-to-1-to-0 chroma, nonnegative unchanged gain and PBGRA passed; maximum error={maximumError:F6} byte.");
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
