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
        CheckPreparedPropagation();
        CheckBidirectionalTiming();
        CheckClickWave();
        CheckRenderedBackgroundArrival();
        CheckTimeAndBuffers();
        CheckBloomColour();
        CheckArgumentsAndCaptureAlpha();
        CheckVisibleCompositing();
        Console.WriteLine("Uniform-block refresh checks passed: native solid cores, bidirectional depth order, actual pointer-depth sampling, equal-depth simultaneous arrival, faster envelopes, synchronized background, coloured exterior bloom, direct-time determinism, cancellation, immutable inputs and transparent ending. Archived neighbour propagation checks also remain passing.");
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
        Require(UniformBlockRefreshRenderer.OpacityAt(RefreshDepthSweep.RiseMs) > .8f
            && UniformBlockRefreshRenderer.OpacityAt(RefreshDepthSweep.LifetimeMs) == 0
            && renderer.DurationMs == 2550 && RefreshDepthSweep.LifetimeMs == 510,
            "The requested modest 15% speedup did not preserve a complete whole-block pulse.");
    }

    private static void CheckPreparedPropagation()
    {
        const int side = 264, cell = 8, columns = 33, seedRow = 16;
        int seed = seedRow * columns + seedRow;
        double[] near = new RefreshPropagation(side, side, cell, Enumerable.Repeat(1f, columns * columns).ToArray())
            .Arrivals(131, 131).ToArray();
        double[] far = new RefreshPropagation(side, side, cell, new float[columns * columns])
            .Arrivals(131, 131).ToArray();
        Require(far.Max() - near.Max() > 350,
            "Uniform near/far scenes were normalized to the same duration instead of retaining speed differences.");
        for (int index = 0; index < near.Length; index++)
            if (index != seed) Require(near[index] < far[index], "Near surfaces did not propagate faster than far surfaces.");

        // Infer the flat-field speed from one axial step, then compare all directions against
        // the Euclidean lower bound. Eight-neighbour paths should stay within 8.3% of a circle.
        double unit = (near[seed + 1] - near[seed]) / cell;
        for (int row = 0; row < columns; row++)
        for (int column = 0; column < columns; column++)
        {
            double distance = Math.Sqrt(Math.Pow(row - seedRow, 2) + Math.Pow(column - seedRow, 2)) * cell;
            double travel = near[row * columns + column] - near[seed];
            Require(travel >= distance * unit - .001 && travel <= distance * unit * 1.083 + .001,
                "Flat-field propagation acquired excessive directional grid bias.");
            Require(Math.Abs(near[row * columns + column] - near[(columns - 1 - row) * columns + column]) < .001,
                "A symmetric flat depth field has asymmetric propagation.");
        }

        float[] split = new float[columns * columns];
        for (int row = 0; row < seedRow; row++) Array.Fill(split, 1, row * columns, columns);
        double[] layers = new RefreshPropagation(side, side, cell, split).Arrivals(131, 131).ToArray();
        Require(layers[4 * columns + seedRow] + 150 < layers[28 * columns + seedRow],
            "Equal-distance near/far regions in one scene did not show different propagation speeds.");

        const int stripeSide = 512, stripeColumns = 64;
        float[] stripe = Enumerable.Repeat(.9f, stripeColumns * stripeColumns).ToArray();
        double[] clear = new RefreshPropagation(stripeSide, stripeSide, cell, stripe).Arrivals(0, 260).ToArray();
        for (int row = 0; row < stripeColumns; row++) stripe[row * stripeColumns + 8] = 0;
        double[] obstructed = new RefreshPropagation(stripeSide, stripeSide, cell, stripe).Arrivals(0, 260).ToArray();
        int beyondBoundary = 32 * stripeColumns + 16;
        Require(obstructed[beyondBoundary] > clear[beyondBoundary] + 35,
            "Crossing a narrow depth boundary added no perceptible latency beyond its small spatial width.");

        const int width = 137, height = 79, smallCell = 4;
        int smallColumns = (width + smallCell - 1) / smallCell, rows = (height + smallCell - 1) / smallCell;
        float[] raw = Enumerable.Range(0, smallColumns * rows).Select(index => (index * 37 % 101) / 100f).ToArray();
        float[] before = (float[])raw.Clone();
        var solver = new RefreshPropagation(width, height, smallCell, raw);
        foreach ((int x, int y) in new[] { (68, 39), (0, 0), (136, 78), (21, 57) })
        {
            double[] times = solver.Arrivals(x, y).ToArray();
            CheckArrivalField(times, smallColumns, rows, y / smallCell * smallColumns + x / smallCell);
        }
        double[] first = solver.Arrivals(0, 0).ToArray();
        Require(first.AsSpan().SequenceEqual(solver.Arrivals(int.MinValue, int.MinValue)),
            "An offscreen negative origin did not clamp to the captured grid.");
        double[] last = solver.Arrivals(width - 1, height - 1).ToArray();
        Require(last.AsSpan().SequenceEqual(solver.Arrivals(int.MaxValue, int.MaxValue)),
            "An offscreen positive origin did not clamp to the captured grid.");
        Require(first.AsSpan().SequenceEqual(solver.Arrivals(0, 0)), "Reusing the solver retained stale frontier state.");
        Require(raw.AsSpan().SequenceEqual(before), "Propagation smoothing modified the original depth values.");

        foreach ((int w, int h) in new[] { (1, 1), (1, 17), (19, 1), (7, 5) })
        {
            int c = (w + 3) / 4, r = (h + 3) / 4;
            double[] times = new RefreshPropagation(w, h, 4, new float[c * r]).Arrivals(0, 0).ToArray();
            CheckArrivalField(times, c, r, 0);
        }
        ThrowsArgument(() => new RefreshPropagation(0, 1, 4, []), "An invalid propagation grid was accepted.");
        ThrowsArgument(() => new RefreshPropagation(7, 5, 4, [0]), "Missing per-cell propagation depths were accepted.");
        ThrowsArgument(() => new RefreshPropagation(1, 1, 4, [float.NaN]), "A nonfinite propagation depth was accepted.");
        Console.WriteLine("Archived prepared propagation checks: near/far speed, same-scene layers, depth-edge latency, flat-field isotropy, all-sublevel connectivity at four origins, bounded adjacent gaps, clamped/degenerate/residual cells and immutable depth.");
    }

    private static void CheckArrivalField(double[] times, int columns, int rows, int seed)
    {
        Require(times.Length == columns * rows && times.All(time => double.IsFinite(time)
            && time >= 140 && time + 600 <= 3000.001), "A propagation arrival is invalid or loses its complete fade.");
        Require(times[seed] == 140 && times.Where((_, index) => index != seed).All(time => time > times[seed]),
            "Propagation did not begin uniquely at the release-containing cell.");
        foreach (int current in Enumerable.Range(0, times.Length))
        foreach (int next in Neighbours(current, columns, rows))
            Require(Math.Abs(times[current] - times[next]) <= 220.001,
                "Adjacent light pulses no longer overlap before their whole-block fade finishes.");

        // Independently flood every sublevel set, including equal-time batches. Every reached
        // cell must be connected through already reached neighbours to the one release cell.
        var visited = new bool[times.Length];
        var flood = new Queue<int>();
        foreach (double threshold in times.Distinct().Order())
        {
            Array.Clear(visited);
            flood.Clear();
            flood.Enqueue(seed); visited[seed] = true;
            int count = 0;
            while (flood.TryDequeue(out int current))
            {
                count++;
                foreach (int next in Neighbours(current, columns, rows))
                    if (!visited[next] && times[next] <= threshold)
                    { visited[next] = true; flood.Enqueue(next); }
            }
            Require(count == times.Count(time => time <= threshold),
                "A disconnected remote island lit before propagation could reach it through adjacent cells.");
        }
    }

    private static IEnumerable<int> Neighbours(int index, int columns, int rows)
    {
        int row = index / columns, column = index % columns;
        for (int y = Math.Max(0, row - 1); y <= Math.Min(rows - 1, row + 1); y++)
        for (int x = Math.Max(0, column - 1); x <= Math.Min(columns - 1, column + 1); x++)
            if (y * columns + x != index) yield return y * columns + x;
    }

    private static void CheckBidirectionalTiming()
    {
        foreach (float origin in new[] { 0f, .1f, .25f, .5f, .6f, .9f, 1f })
        {
            Require(RefreshDepthSweep.StartAt(origin, origin) == RefreshDepthSweep.StartDelayMs,
                "The pointer's own depth did not begin the scan.");
            double previousFar = RefreshDepthSweep.StartAt(origin, origin);
            double previousNear = previousFar;
            for (int step = 1; step <= 100; step++)
            {
                float farther = origin * (1 - step / 100f);
                float nearer = origin + (1 - origin) * step / 100f;
                double far = RefreshDepthSweep.StartAt(farther, origin);
                double near = RefreshDepthSweep.StartAt(nearer, origin);
                Require(far >= previousFar - .001 && near >= previousNear - .001,
                    "A bidirectional depth branch scanned backwards or skipped its starting layer.");
                Require(double.IsFinite(far) && double.IsFinite(near)
                    && Math.Max(far, near) + RefreshDepthSweep.LifetimeMs <= RefreshDepthSweep.DurationMs,
                    "An endpoint or interior depth loses the complete fade after the faster scan.");
                previousFar = far; previousNear = near;
            }
            float span = Math.Min(origin, 1 - origin) * .75f;
            Require(Math.Abs(RefreshDepthSweep.StartAt(origin - span, origin)
                - RefreshDepthSweep.StartAt(origin + span, origin)) < .001,
                "Equally separated far/near layers were given different scan speeds.");
        }
        Require(RefreshDepthSweep.StartAt(1, .75f) < RefreshDepthSweep.StartAt(0, .75f)
            && RefreshDepthSweep.StartAt(0, .25f) < RefreshDepthSweep.StartAt(1, .25f),
            "The shorter depth branch was stretched to finish with the longer branch.");
        double closeFar = RefreshDepthSweep.StartAt(.59f, .6f);
        double closeNear = RefreshDepthSweep.StartAt(.61f, .6f);
        Require(Math.Abs(closeFar - closeNear) < .001 && closeFar > RefreshDepthSweep.StartDelayMs
            && closeFar - RefreshDepthSweep.StartDelayMs < 40,
            "Slightly smaller/larger depth layers did not follow the initial depth continuously.");
        foreach (float invalid in new[] { -1f, 2f, float.NaN, float.PositiveInfinity })
        {
            ThrowsArgument(() => RefreshDepthSweep.StartAt(invalid, .5f), "Invalid scan depth was accepted.");
            ThrowsArgument(() => RefreshDepthSweep.StartAt(.5f, invalid), "Invalid pointer depth was accepted.");
        }
        Console.WriteLine("Bidirectional depth timing checks: both branches progress away from pointer depth, equal-depth offsets share speed, shorter side finishes earlier, endpoints are finite and all fades finish within the faster cycle.");
    }

    private static void CheckClickWave()
    {
        const int width = 137, height = 79;
        byte[] capture = Solid(width, height, new Vector3(95, 150, 45));
        var depth = new RefreshDepthMap(width, height,
            Enumerable.Range(0, width * height).Select(index => index % width / (float)(width - 1)).ToArray());
        float[] rawDepth = (float[])depth.Values.Clone();
        var renderer = new UniformBlockRefreshRenderer(width, height, capture, depth, 0, 0);
        var original = renderer.Blocks.ToArray();
        int cell = original[0].CellWidth;
        foreach ((int x, int y) in new[] { (68, 39), (0, 0), (136, 78), (21, 57), (1, 1), (2, 3) })
        {
            renderer.SetOrigin(x, y);
            var blocks = renderer.Blocks.ToArray();
            Require(Math.Abs(renderer.OriginDepth - x / (float)(width - 1)) < .000001,
                "Pointer depth was taken from a cell centre instead of the actual pointer pixel.");
            var byDepthDistance = blocks.OrderBy(block => Math.Abs(block.Depth - renderer.OriginDepth)).ToArray();
            for (int index = 1; index < byDepthDistance.Length; index++)
                Require(byDepthDistance[index].StartMs >= byDepthDistance[index - 1].StartMs - .001,
                    "The scan was ordered by screen position instead of distance from pointer depth.");
            for (int index = 0; index < blocks.Length; index++)
            {
                Require((blocks[index] with { StartMs = 0 }) == (original[index] with { StartMs = 0 }),
                    "Retiming at release changed prepared geometry, pigment or raw depth.");
                Require(double.IsFinite(blocks[index].StartMs)
                    && blocks[index].StartMs >= RefreshDepthSweep.StartDelayMs
                    && blocks[index].StartMs + RefreshDepthSweep.LifetimeMs <= renderer.DurationMs,
                    "A bidirectional block has invalid timing or an incomplete fade.");
                CheckBackgroundEnvelope(blocks[index]);
            }
            foreach (var sameDepth in blocks.GroupBy(block => block.Depth))
                Require(sameDepth.Select(block => block.StartMs).Distinct().Count() == 1,
                    "Equal-depth remote blocks did not appear together.");
        }
        renderer.SetOrigin(0, 0);
        Require(original.SequenceEqual(renderer.Blocks.ToArray()), "Returning to the initial release cell changed the prepared scene.");
        renderer.SetOrigin(int.MinValue, int.MinValue);
        Require(original.SequenceEqual(renderer.Blocks.ToArray()), "Renderer origin retiming did not clamp negative coordinates.");
        renderer.SetOrigin(width - 1, height - 1);
        var last = renderer.Blocks.ToArray();
        renderer.SetOrigin(int.MaxValue, int.MaxValue);
        Require(last.SequenceEqual(renderer.Blocks.ToArray()), "Renderer origin retiming did not clamp positive coordinates.");
        Require(original[0].StartMs < last[0].StartMs && last[^1].StartMs < original[^1].StartMs,
            "Moving from the far endpoint to the near endpoint did not reverse scan order.");
        Require(last[^1].CellWidth < last[0].CellWidth && last[^1].CellHeight < last[0].CellHeight,
            "The release checks did not exercise clipped edge cells.");
        Require(depth.Values.AsSpan().SequenceEqual(rawDepth), "Release retiming modified the model's raw depth map.");
        renderer.SetOrigin(1, 1);
        double atFirstPixel = renderer.Blocks[0].StartMs;
        renderer.SetOrigin(2, 1);
        Require(renderer.Blocks[0].StartMs != atFirstPixel,
            "Moving within one native cell failed to update the actual pointer depth.");
        float originBefore = renderer.OriginDepth;
        double[] timesBefore = renderer.Blocks.ToArray().Select(block => block.StartMs).ToArray();
        renderer.SetOrigin(2, height - 1);
        Require(renderer.OriginDepth == originBefore
            && timesBefore.AsSpan().SequenceEqual(renderer.Blocks.ToArray().Select(block => block.StartMs).ToArray()),
            "Moving the pointer spatially at the same depth changed scan order.");

        foreach ((int w, int h) in new[] { (1, 1), (1, 17), (19, 1), (7, 5) })
        {
            var flat = new UniformBlockRefreshRenderer(w, h, Solid(w, h, new Vector3(100)),
                new RefreshDepthMap(1, 1, [.3f]), w - 1, h - 1);
            Require(flat.Blocks.ToArray().All(block => block.StartMs == RefreshDepthSweep.StartDelayMs),
                "Flat depth or a degenerate image incorrectly retained spatial propagation delays.");
        }
        var lowResolutionDepth = new RefreshDepthMap(2, 1, [0, 1]);
        Require(Math.Abs(RefreshDepthSweep.SampleOrigin(lowResolutionDepth, 9, 1, 4, 0) - .5f) < .000001
            && RefreshDepthSweep.SampleOrigin(lowResolutionDepth, 9, 1, int.MinValue, 0) == 0
            && RefreshDepthSweep.SampleOrigin(lowResolutionDepth, 9, 1, int.MaxValue, 0) == 1,
            "Pointer depth did not use bilinear model sampling and edge clamping.");
        Console.WriteLine("Pointer-depth retiming checks: actual pixel/bilinear sampling, both depth directions, equal-depth remote simultaneity, spatially independent timing, immutable geometry/pigment/depth, clipped/degenerate edges and synchronized background.");
    }

    private static void CheckBackgroundEnvelope(UniformBlockRefreshRenderer.Block block)
    {
        Require(UniformBlockRefreshRenderer.BackgroundAmountAt(block, block.StartMs - 1) == 256
            && UniformBlockRefreshRenderer.BackgroundAmountAt(block, block.StartMs) == 256,
            "Background recovery preceded its block's arrival.");
        int previous = 256;
        for (int step = 1; step <= 8; step++)
        {
            double age = step * RefreshDepthSweep.BackgroundRestoreMs / 8;
            int current = UniformBlockRefreshRenderer.BackgroundAmountAt(block, block.StartMs + age);
            Require(current >= 0 && current < previous,
                "Background recovery did not move smoothly with the arriving block.");
            previous = current;
        }
        Require(previous == 0 && UniformBlockRefreshRenderer.BackgroundAmountAt(block, block.StartMs + RefreshDepthSweep.LifetimeMs) == 0,
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
        renderer.Render(target.StartMs + RefreshDepthSweep.BackgroundRestoreMs * .5, frame, bloom);
        Require(frame[pixel + 3] > 0 && frame[pixel + 3] < beforeArrival[3],
            "Rendered gap did not partially recover with its arriving block.");
        renderer.Render(target.StartMs + RefreshDepthSweep.BackgroundRestoreMs, frame, bloom);
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
            Require(renderer.DurationMs == RefreshDepthSweep.DurationMs && ((IRefreshRenderer)renderer).FrameIntervalMs > 0,
                "The intended cycle or playback interval changed.");
            byte[] a = new byte[capture.Length], b = new byte[capture.Length], clean = new byte[capture.Length], bloom = new byte[4];
            renderer.Render(913.25, a, bloom);
            renderer.Render(2500, b, bloom);
            renderer.Render(12, b, bloom);
            renderer.Render(913.25, b, bloom);
            Require(a.AsSpan().SequenceEqual(b), "Jumping/backward elapsed time changed a stateless frame.");
            renderer.Render(913.25, b, bloom);
            Require(a.AsSpan().SequenceEqual(b), "Repeated elapsed time changed a frame.");
            foreach (double elapsed in new[] { 0, 110, 700, 1500, 2350, RefreshDepthSweep.DurationMs - .01 })
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
            foreach (double elapsed in new[] { -1, RefreshDepthSweep.DurationMs, 4000, double.NaN, double.PositiveInfinity })
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
        foreach (double elapsed in new[] { 0, 450, 1100, 2100, RefreshDepthSweep.DurationMs - 1 })
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
            (int X, int Y)[] origins = [(0, 0), (width - 1, height - 1),
                (width / 4, height * 3 / 4), (width * 3 / 4, height / 4)];
            foreach (var origin in origins) renderer.SetOrigin(origin.X, origin.Y);
            var retimes = new List<double>();
            for (int sample = 0; sample < 12; sample++)
            {
                var origin = origins[sample % origins.Length];
                watch.Restart();
                renderer.SetOrigin(origin.X, origin.Y);
                retimes.Add(watch.Elapsed.TotalMilliseconds);
            }
            retimes.Sort();
            Console.WriteLine($"Prepared pointer-depth retime native {width}x{height}: {renderer.Blocks.Length} blocks, warmed p50={retimes[retimes.Count / 2]:F2}ms, p95/max={retimes[^1]:F2}ms (timing only, before UI upload/depth/capture).");
            renderer.SetOrigin(width / 2, height / 2);
            byte[] frame = new byte[capture.Length], bloom = new byte[4];
            renderer.Render(0, frame, bloom);
            renderer.Render(100, frame, bloom);
            var times = new List<double>();
            for (int sample = 0; sample < 60; sample++)
            {
                watch.Restart();
                renderer.Render(RefreshDepthSweep.DurationMs * (.15 + sample * (.7 / 60)), frame, bloom);
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
