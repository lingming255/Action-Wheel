using System.Diagnostics;
using System.Numerics;
using Action_Wheel.Services;

if (!args.Contains("--performance-only")) UniformBlockRefreshTests.Run();
if (!args.Contains("--correctness-only")) UniformBlockRefreshTests.Measure();

internal static class UniformBlockRefreshTests
{
    public static void Run()
    {
        CheckWholeBlocks();
        CheckDepthAndEnvelope();
        CheckClickWave();
        CheckRenderedBackgroundArrival();
        CheckTimeAndBuffers();
        CheckBloomColour();
        CheckArgumentsAndCaptureAlpha();
        CheckVisibleCompositing();
        Console.WriteLine("Uniform-block refresh checks passed: native solid cores, click-centred radial order, bounded depth stagger, synchronized background, whole-block fades, coloured exterior bloom, direct-time determinism, cancellation, immutable inputs and transparent ending.");
    }

    private static void CheckVisibleCompositing()
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var add = typeof(UniformBlockRefreshRenderer).GetMethod("AddLight", flags)
            ?? throw new InvalidOperationException("Native light compositor is unavailable.");
        var over = typeof(UniformBlockRefreshRenderer).GetMethod("Over", flags)
            ?? throw new InvalidOperationException("Native core compositor is unavailable.");
        var random = new Random(571309);
        double maximumAdd = 0, maximumOver = 0;
        for (int sample = 0; sample < 10000; sample++)
        {
            uint source = (uint)random.Next(1 << 24);
            uint background = RandomPremultiplied(random), foreground = RandomPremultiplied(random);
            int blue = random.Next(256), green = random.Next(256), red = random.Next(256);
            uint lit = (uint)add.Invoke(null, [background, source, blue, green, red])!;
            CheckPackedPbgra(lit);
            Vector3 expected = Vector3.Clamp(Visible(background, source) + new Vector3(red, green, blue), Vector3.Zero, new Vector3(255));
            maximumAdd = Math.Max(maximumAdd, Largest(Vector3.Abs(Visible(lit, source) - expected)));
            uint combined = (uint)over.Invoke(null, [foreground, background])!;
            CheckPackedPbgra(combined);
            Vector3 expectedOver = Rgb(foreground) + Visible(background, source) * (1 - (foreground >> 24) / 255f);
            maximumOver = Math.Max(maximumOver, Largest(Vector3.Abs(Visible(combined, source) - expectedOver)));
        }
        Require(maximumAdd <= 1.01 && maximumOver <= 1.01,
            "Packed native composition differs from visible RGB arithmetic by more than one byte.");
        Console.WriteLine($"Native visible-composition oracle: additive max={maximumAdd:F6} byte, core source-over max={maximumOver:F6} byte.");
    }

    private static uint RandomPremultiplied(Random random)
    {
        int alpha = random.Next(256);
        return (uint)(random.Next(alpha + 1) | (random.Next(alpha + 1) << 8)
            | (random.Next(alpha + 1) << 16) | (alpha << 24));
    }

    private static Vector3 Rgb(uint colour) => new((colour >> 16) & 255, (colour >> 8) & 255, colour & 255);
    private static Vector3 Visible(uint overlay, uint source) => Rgb(overlay) + Rgb(source) * (1 - (overlay >> 24) / 255f);
    private static float Largest(Vector3 value) => Math.Max(value.X, Math.Max(value.Y, value.Z));
    private static void CheckPackedPbgra(uint value)
    {
        uint alpha = value >> 24;
        Require((value & 255) <= alpha && ((value >> 8) & 255) <= alpha && ((value >> 16) & 255) <= alpha,
            "A native packed compositor produced illegal PBGRA.");
    }

    private static void CheckWholeBlocks()
    {
        const int width = 813, height = 469;
        byte[] capture = Solid(width, height, new Vector3(170, 65, 30));
        var renderer = new UniformBlockRefreshRenderer(width, height, capture, Depth(79, 43), width / 2, height / 2);
        var blocks = renderer.Blocks.ToArray();
        Require(blocks.Length > 100, "The native scene did not produce independent blocks.");
        byte[] frame = new byte[capture.Length], bloom = new byte[4];
        int checkedBlocks = 0, sharpBoundaries = 0;
        for (int index = blocks.Length / 8; index < blocks.Length && checkedBlocks < 12; index += Math.Max(1, blocks.Length / 15))
        {
            var block = blocks[index];
            Require(block.Left >= 0 && block.Top >= 0 && block.Width > 0 && block.Height > 0
                && block.Left + block.Width <= width && block.Top + block.Height <= height,
                "A core extends outside the native capture.");
            if (block.Width < 3 || block.Height < 3) continue;
            renderer.Render(block.StartMs + 180, frame, bloom);
            int centre = ((block.Top + block.Height / 2) * width + block.Left + block.Width / 2) * 4;
            // Uniform source colour and depth for the whole cell make its underlay constant.
            // A native core must therefore have exactly the same PBGRA across every pixel.
            for (int y = block.Top; y < block.Top + block.Height; y++)
            for (int x = block.Left; x < block.Left + block.Width; x++)
            {
                int pixel = (y * width + x) * 4;
                Require(frame.AsSpan(pixel, 4).SequenceEqual(frame.AsSpan(centre, 4)),
                    "A block core contains a glyph, soft mask, spatial brightness variation or bloom.");
            }
            if (block.Left > block.CellLeft)
            {
                int edge = ((block.Top + block.Height / 2) * width + block.Left - 1) * 4;
                if (!frame.AsSpan(edge, 4).SequenceEqual(frame.AsSpan(centre, 4))) sharpBoundaries++;
            }
            checkedBlocks++;
        }
        Require(checkedBlocks >= 8 && sharpBoundaries >= 4, "Core boundaries did not remain native hard rectangles.");
        Console.WriteLine($"Native solid-core check: {checkedBlocks} complete rectangles, {sharpBoundaries} hard boundary samples, {blocks.Length} independent cells.");
    }

    private static void CheckDepthAndEnvelope()
    {
        var renderer = new UniformBlockRefreshRenderer(417, 235,
            Solid(417, 235, new Vector3(95, 150, 45)), Depth(41, 25), 208, 117);
        var blocks = renderer.Blocks.ToArray();
        Require(blocks.Select(block => block.StartMs).Distinct().Count() > 30,
            "Many independent blocks collapsed into one shared animation time.");
        var opacity = Enumerable.Range(0, 81)
            .Select(index => UniformBlockRefreshRenderer.OpacityAt(index * 10)).ToArray();
        Require(UniformBlockRefreshRenderer.OpacityAt(-10) == 0 && opacity[0] == 0 && opacity[^1] == 0,
            "Whole-block opacity has a stale start or tail.");
        Require(opacity.All(value => float.IsFinite(value) && value >= 0 && value <= 1)
            && opacity.Max() > .2f, "Whole-block fade is invalid or invisible.");
        int peak = Array.IndexOf(opacity, opacity.Max());
        Require(peak > 0 && peak < opacity.Length - 1
            && opacity.Take(peak).Any(value => value > 0 && value < opacity[peak])
            && opacity.Skip(peak + 1).Any(value => value > 0 && value < opacity[peak]),
            "Blocks do not fade as a whole on both sides of the light pulse.");
    }

    private static void CheckClickWave()
    {
        const int width = 137, height = 79;
        byte[] capture = Solid(width, height, new Vector3(95, 150, 45));
        foreach ((int originX, int originY) in new[] { (68, 39), (0, 0), (136, 78), (21, 57) })
        {
            var farDepth = new UniformBlockRefreshRenderer(width, height, capture,
                new RefreshDepthMap(1, 1, [0]), originX, originY);
            var nearDepth = new UniformBlockRefreshRenderer(width, height, capture,
                new RefreshDepthMap(1, 1, [1]), originX, originY);
            var farBlocks = farDepth.Blocks.ToArray();
            var nearBlocks = nearDepth.Blocks.ToArray();
            var ordered = farBlocks.OrderBy(block => Radius(block, originX, originY)).ToArray();
            for (int index = 1; index < ordered.Length; index++)
                Require(ordered[index].StartMs >= ordered[index - 1].StartMs - .001,
                    "Equal-depth blocks do not appear in radial order from the click.");
            Require(ordered[^1].StartMs - ordered[0].StartMs > 1400,
                "The click wave no longer drives most of the appearance timing.");
            Require(Radius(ordered[0], originX, originY) <= 3,
                "The wave did not begin at a cell next to the click.");

            double smallestStagger = double.MaxValue, largestStagger = 0;
            for (int index = 0; index < farBlocks.Length; index++)
            {
                double stagger = nearBlocks[index].StartMs - farBlocks[index].StartMs;
                Require(stagger >= -.001 && stagger <= 240.001,
                    "Depth reversed the scan or overwhelmed the radial wave.");
                smallestStagger = Math.Min(smallestStagger, stagger);
                largestStagger = Math.Max(largestStagger, stagger);
                Require(nearBlocks[index].StartMs >= 0 && nearBlocks[index].StartMs + 600 <= nearDepth.DurationMs,
                    "A late block is cut off before its whole fade finishes.");
                CheckBackgroundEnvelope(nearBlocks[index]);
            }
            Require(smallestStagger < largestStagger * .15 && largestStagger > 200,
                "Depth layers are absent or create an initial hole around the click.");
            var nearby = nearBlocks.OrderBy(block => Radius(block, originX, originY)).ElementAt(nearBlocks.Length / 10);
            var distant = farBlocks.OrderBy(block => Radius(block, originX, originY)).ElementAt(farBlocks.Length * 9 / 10);
            Require(nearby.StartMs < distant.StartMs,
                "Depth delayed a nearby block behind a much more distant one.");
        }

        var left = new UniformBlockRefreshRenderer(width, height, capture,
            new RefreshDepthMap(1, 1, [.5f]), 0, 0).Blocks.ToArray();
        var right = new UniformBlockRefreshRenderer(width, height, capture,
            new RefreshDepthMap(1, 1, [.5f]), width - 1, height - 1).Blocks.ToArray();
        Require(left[0].StartMs < right[0].StartMs && right[^1].StartMs < left[^1].StartMs,
            "Moving the click did not move the source of the wave.");
        var clampedLeft = new UniformBlockRefreshRenderer(width, height, capture,
            new RefreshDepthMap(1, 1, [.5f]), int.MinValue, int.MinValue).Blocks.ToArray();
        var clampedRight = new UniformBlockRefreshRenderer(width, height, capture,
            new RefreshDepthMap(1, 1, [.5f]), int.MaxValue, int.MaxValue).Blocks.ToArray();
        Require(left.SequenceEqual(clampedLeft) && right.SequenceEqual(clampedRight),
            "Out-of-monitor clicks did not clamp to the captured monitor.");
        Require(left[^1].CellWidth < left[0].CellWidth && left[^1].CellHeight < left[0].CellHeight,
            "The radial checks did not exercise clipped edge cells.");

        foreach ((int w, int h) in new[] { (1, 1), (1, 17), (19, 1) })
        {
            var renderer = new UniformBlockRefreshRenderer(w, h, Solid(w, h, new Vector3(100)),
                new RefreshDepthMap(1, 1, [1]), 0, 0);
            foreach (var block in renderer.Blocks)
                Require(double.IsFinite(block.StartMs) && block.StartMs >= 0
                    && block.StartMs + 600 <= renderer.DurationMs,
                    "A degenerate monitor produced invalid or unfinished click-wave timing.");
        }
        Console.WriteLine("Click-wave checks: radial ordering at four origins, bounded/de-emphasized central depth, moved and clamped sources, clipped/degenerate edges, complete tails and shared background timing.");
    }

    private static double Radius(UniformBlockRefreshRenderer.Block block, int originX, int originY)
    {
        double dx = block.CellLeft + (block.CellWidth - 1) * .5 - originX;
        double dy = block.CellTop + (block.CellHeight - 1) * .5 - originY;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static void CheckBackgroundEnvelope(UniformBlockRefreshRenderer.Block block)
    {
        Require(UniformBlockRefreshRenderer.BackgroundAmountAt(block, block.StartMs - 1) == 256
            && UniformBlockRefreshRenderer.BackgroundAmountAt(block, block.StartMs) == 256,
            "Background recovery preceded its block's arrival.");
        int previous = 256;
        for (int age = 50; age <= 400; age += 50)
        {
            int current = UniformBlockRefreshRenderer.BackgroundAmountAt(block, block.StartMs + age);
            Require(current >= 0 && current < previous,
                "Background recovery did not move smoothly with the arriving block.");
            previous = current;
        }
        Require(previous == 0 && UniformBlockRefreshRenderer.BackgroundAmountAt(block, block.StartMs + 600) == 0,
            "Background did not fully restore before the block faded out.");
    }

    private static void CheckRenderedBackgroundArrival()
    {
        const int width = 601, height = 367;
        byte[] capture = Solid(width, height, new Vector3(150));
        var depth = new RefreshDepthMap(1, 1, [.5f]);
        var geometry = new UniformBlockRefreshRenderer(width, height, capture, depth, width / 2, height / 2)
            .Blocks.ToArray();
        // Set only the pigment sampler's 16 points to black. Unsampled gap pixels remain grey,
        // giving visible background correction with no emitted light to contaminate the check.
        foreach (var block in geometry)
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            int px = block.CellLeft + Math.Min(block.CellWidth - 1, (int)((x + .5f) * block.CellWidth / 4));
            int py = block.CellTop + Math.Min(block.CellHeight - 1, (int)((y + .5f) * block.CellHeight / 4));
            Array.Clear(capture, (py * width + px) * 4, 3);
        }
        var renderer = new UniformBlockRefreshRenderer(width, height, capture, depth, width / 2, height / 2);
        Require(renderer.Blocks.ToArray().All(block => block.Colour == Vector3.Zero),
            "The isolated background check unexpectedly emitted bloom.");
        var target = renderer.Blocks[renderer.Blocks.Length / 2];
        int gapX = target.CellLeft + 1, gapY = target.CellTop;
        Require(gapY < target.Top && capture[(gapY * width + gapX) * 4] == 150,
            "The background integration sample is not an unsampled grey gap.");
        byte[] frame = new byte[capture.Length], bloom = new byte[4];
        int pixel = (gapY * width + gapX) * 4;
        renderer.Render(target.StartMs - 1, frame, bloom);
        byte[] beforeArrival = frame.AsSpan(pixel, 4).ToArray();
        Require(beforeArrival[3] > 0, "The isolated gap has no background correction to restore.");
        renderer.Render(target.StartMs, frame, bloom);
        Require(frame.AsSpan(pixel, 4).SequenceEqual(beforeArrival),
            "Rendered gap recovery began before the block arrived.");
        renderer.Render(target.StartMs + 200, frame, bloom);
        Require(frame[pixel + 3] > 0 && frame[pixel + 3] < beforeArrival[3],
            "Rendered gap did not partially recover with its arriving block.");
        renderer.Render(target.StartMs + 400, frame, bloom);
        Require(frame.AsSpan(pixel, 4).ToArray().All(value => value == 0),
            "Rendered gap retained background correction after its block's recovery finished.");
    }

    private static void CheckTimeAndBuffers()
    {
        foreach ((int width, int height) in new[] { (1, 1), (7, 5), (37, 19), (641, 411) })
        {
            byte[] capture = Scene(width, height), sourceBefore = (byte[])capture.Clone();
            var depth = Depth(17, 11);
            float[] depthBefore = (float[])depth.Values.Clone();
            var renderer = new UniformBlockRefreshRenderer(width, height, capture, depth, width / 2, height / 2);
            Require(renderer.DurationMs == 3000 && ((IRefreshRenderer)renderer).FrameIntervalMs > 0,
                "The intended cycle or playback interval changed.");
            byte[] a = new byte[capture.Length], b = new byte[capture.Length], clean = new byte[capture.Length], bloom = new byte[4];
            renderer.Render(913.25, a, bloom);
            renderer.Render(2500, b, bloom);
            renderer.Render(12, b, bloom);
            renderer.Render(913.25, b, bloom);
            Require(a.AsSpan().SequenceEqual(b), "Jumping/backward elapsed time changed a stateless frame.");
            renderer.Render(913.25, b, bloom);
            Require(a.AsSpan().SequenceEqual(b), "Repeated elapsed time changed a frame.");
            foreach (double elapsed in new[] { 0, 110, 700, 1500, 2350, 2999.99 })
            {
                Array.Fill(b, (byte)239);
                Array.Fill(bloom, (byte)239);
                renderer.Render(elapsed, b, bloom);
                Array.Clear(clean);
                renderer.Render(elapsed, clean, bloom);
                Require(b.AsSpan().SequenceEqual(clean), "Completed output depends on stale buffer pixels.");
                Require(bloom.All(value => value == 0), "The single-composite renderer left a second bloom image.");
                CheckPbgra(b);
            }
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Array.Fill(b, (byte)253);
            Array.Fill(bloom, (byte)253);
            bool observed = false;
            try { renderer.Render(1200, b, bloom, cancelled.Token); }
            catch (OperationCanceledException) { observed = true; }
            Require(observed && b.All(value => value == 253) && bloom.All(value => value == 253),
                "Pre-cancelled playback modified buffers or continued.");
            renderer.Render(913.25, b, bloom);
            Require(a.AsSpan().SequenceEqual(b), "Cancelled playback could not resume deterministically.");
            foreach (double elapsed in new[] { -1, 3000, 4000, double.NaN, double.PositiveInfinity })
            {
                Array.Fill(b, (byte)239);
                Array.Fill(bloom, (byte)239);
                renderer.Render(elapsed, b, bloom);
                Require(b.All(value => value == 0) && bloom.All(value => value == 0),
                    "Out-of-cycle playback did not clear every output pixel.");
            }
            Require(capture.AsSpan().SequenceEqual(sourceBefore) && depth.Values.AsSpan().SequenceEqual(depthBefore),
                "Uniform-block rendering changed its capture or depth input.");
        }
    }

    private static void CheckArgumentsAndCaptureAlpha()
    {
        const int width = 137, height = 79;
        byte[] capture = Scene(width, height), changedAlpha = (byte[])capture.Clone();
        for (int alpha = 3; alpha < changedAlpha.Length; alpha += 4) changedAlpha[alpha] ^= 255;
        var depth = Depth(17, 11);
        var original = new UniformBlockRefreshRenderer(width, height, capture, depth, width / 2, height / 2);
        var alphaChanged = new UniformBlockRefreshRenderer(width, height, changedAlpha, depth, width / 2, height / 2);
        byte[] a = new byte[capture.Length], b = new byte[capture.Length], bloom = new byte[4];
        foreach (double elapsed in new[] { 0, 450, 1100, 2100, 2999 })
        {
            original.Render(elapsed, a, bloom);
            alphaChanged.Render(elapsed, b, bloom);
            Require(a.AsSpan().SequenceEqual(b), "Undefined screenshot alpha affected block colour or output.");
        }
        byte[] before = (byte[])capture.Clone();
        ThrowsArgument(() => original.Render(1100, capture, bloom), "Capture used as the output buffer was accepted.");
        ThrowsArgument(() => original.Render(1100, new byte[a.Length - 1], bloom), "Incomplete native output was accepted.");
        ThrowsArgument(() => original.Render(1100, a, new byte[3]), "Incomplete bloom output was accepted.");
        ThrowsArgument(() => original.Render(1100, a, new byte[8]), "Oversized bloom output was accepted.");
        Require(capture.AsSpan().SequenceEqual(before), "An invalid buffer call changed the capture.");
        ThrowsArgument(() => new UniformBlockRefreshRenderer(width, height, capture, new RefreshDepthMap(0, 11, []), 0, 0),
            "Zero-sized depth was accepted.");
        ThrowsArgument(() => new UniformBlockRefreshRenderer(width, height, capture, new RefreshDepthMap(17, -1, []), 0, 0),
            "Negative-sized depth was accepted.");
        ThrowsArgument(() => new UniformBlockRefreshRenderer(width, height, capture, new RefreshDepthMap(17, 11, new float[186]), 0, 0),
            "Incomplete depth input was accepted.");
    }

    private static void CheckBloomColour()
    {
        const int width = 429, height = 247;
        ReferenceTexture lut = ReferenceEffectMath.LoadTexture("pixel-lut.rgb.gz");
        foreach (Vector3 channelColour in new[] { new Vector3(200, 0, 0), new Vector3(0, 0, 200) })
        {
            byte[] capture = Solid(width, height, channelColour);
            var renderer = new UniformBlockRefreshRenderer(width, height, capture, Depth(43, 25), width / 2, height / 2);
            var blocks = renderer.Blocks.ToArray();
            bool[] core = new bool[width * height];
            foreach (var block in blocks)
            for (int y = block.Top; y < block.Top + block.Height; y++)
            for (int x = block.Left; x < block.Left + block.Width; x++) core[y * width + x] = true;
            byte[] frame = new byte[capture.Length], bloom = new byte[4];
            var starts = blocks.Select(block => block.StartMs).Order().ToArray();
            renderer.Render(starts[starts.Length / 2] + 180, frame, bloom);
            Vector3 source = channelColour / 255;
            Vector3 strongestBackground = Vector3.Max(source,
                Vector3.Lerp(source, ReferenceEffectMath.Lut(lut, source), .3f)) * 255;
            int active = channelColour.X > 0 ? 0 : 2;
            int litExterior = 0;
            for (int pixel = 0; pixel < core.Length; pixel++)
            {
                if (core[pixel]) continue;
                int offset = pixel * 4;
                float remaining = 1 - frame[offset + 3] / 255f;
                Vector3 visible = new(frame[offset + 2] + channelColour.X * remaining,
                    frame[offset + 1] + channelColour.Y * remaining,
                    frame[offset] + channelColour.Z * remaining);
                if (Component(visible, active) > Component(strongestBackground, active) + 1.5f) litExterior++;
                for (int channel = 0; channel < 3; channel++)
                    if (channel != active)
                        Require(Component(visible, channel) <= Component(strongestBackground, channel) + 1.5f,
                            "Exterior bloom introduced white or an unrelated colour channel.");
            }
            Require(litExterior > 10, "Current blocks did not emit visible light outside their solid cores.");
        }
    }

    public static void Measure()
    {
        foreach ((int width, int height) in new[] { (1920, 1080), (3840, 2160) })
        {
            byte[] capture = Scene(width, height);
            var watch = Stopwatch.StartNew();
            var renderer = new UniformBlockRefreshRenderer(width, height, capture, Depth(128, 72), width / 2, height / 2);
            double prepare = watch.Elapsed.TotalMilliseconds;
            byte[] frame = new byte[capture.Length], bloom = new byte[4];
            renderer.Render(0, frame, bloom);
            renderer.Render(100, frame, bloom);
            var times = new List<double>();
            for (int sample = 0; sample < 60; sample++)
            {
                watch.Restart();
                renderer.Render(400 + sample * (2000.0 / 60), frame, bloom);
                times.Add(watch.Elapsed.TotalMilliseconds);
            }
            long before = GC.GetTotalAllocatedBytes(true);
            for (int sample = 0; sample < 5; sample++) renderer.Render(800 + sample * 17, frame, bloom);
            long allocated = GC.GetTotalAllocatedBytes(true) - before;
            Require(allocated < 1024 * 1024, "Playback allocated a large per-frame buffer.");
            times.Sort();
            Console.WriteLine($"Uniform blocks native {width}x{height}: prepare={prepare:F2}ms, active p50={times[times.Count / 2]:F2}ms, p95={times[(int)(times.Count * .95)]:F2}ms, max={times[^1]:F2}ms; all-thread allocations={allocated} bytes / 5 frames (before UI upload/depth/capture).");
        }
    }

    private static byte[] Solid(int width, int height, Vector3 rgb)
    {
        var result = new byte[checked(width * height * 4)];
        for (int pixel = 0; pixel < width * height; pixel++)
        {
            result[pixel * 4] = (byte)rgb.Z;
            result[pixel * 4 + 1] = (byte)rgb.Y;
            result[pixel * 4 + 2] = (byte)rgb.X;
            result[pixel * 4 + 3] = (byte)(pixel % 256); // BitBlt alpha is undefined.
        }
        return result;
    }

    private static byte[] Scene(int width, int height)
    {
        byte[] result = Solid(width, height, Vector3.Zero);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = (y * width + x) * 4;
            result[offset] = (byte)(30 + y * 170 / Math.Max(1, height - 1));
            result[offset + 1] = (byte)(40 + x * 150 / Math.Max(1, width - 1));
            result[offset + 2] = (byte)(((x / 16 + y / 16) % 2 == 0) ? 70 : 180);
        }
        return result;
    }

    private static RefreshDepthMap Depth(int width, int height)
    {
        float[] values = new float[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            values[y * width + x] = (x + y) / (float)Math.Max(1, width + height - 2);
        return new RefreshDepthMap(width, height, values);
    }

    private static void CheckPbgra(byte[] frame)
    {
        for (int pixel = 0; pixel < frame.Length; pixel += 4)
            Require(frame[pixel] <= frame[pixel + 3] && frame[pixel + 1] <= frame[pixel + 3]
                && frame[pixel + 2] <= frame[pixel + 3], "A frame is not legal premultiplied BGRA.");
    }

    private static float Component(Vector3 value, int channel) => channel == 0 ? value.X : channel == 1 ? value.Y : value.Z;
    private static void ThrowsArgument(Action action, string message)
    {
        bool rejected = false;
        try { action(); }
        catch (ArgumentException) { rejected = true; }
        Require(rejected, message);
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
