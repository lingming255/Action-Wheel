using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Action_Wheel.Services;
using Action_Wheel.Core;

internal static class Program
{
    private static readonly List<object> Comparisons = [];

    private static int Main(string[] args)
    {
        try
        {
            bool preparationOnly = args.Contains("--preparation-only");
            bool correctness = !preparationOnly && !args.Contains("--performance-only");
            bool performance = !preparationOnly && !args.Contains("--correctness-only");
            if (correctness) RunCorrectness();
            object[] measurements = performance ? [Measure(1920, 1080), Measure(3840, 2160)] : [];
            object[] preparation = preparationOnly ? [MeasurePreparation(1920, 1080), MeasurePreparation(3840, 2160)] : [];
            if (correctness) CheckDeviceReuse();
            var report = new
            {
                Passed = true,
                Timestamp = DateTimeOffset.Now,
                UsedScreenCaptureOrDisplay = false,
                Inputs = "Synthetic gradients, high contrast line/text pattern, checkerboard; known relative depth.",
                PlaybackDurationMs = GpuRefreshRenderer.PlaybackDurationMs,
                ReferenceTimeRatio = GpuRefreshRenderer.ReferenceTimeRatio,
                Comparisons,
                Measurements = measurements,
                Preparation = preparation,
                TimingBoundary = "CPU submission, Render+Finish completed-frame wall latency, and disjoint-validated GPU timestamp duration are separate. Finish waits for actual GPU work without pixel readback. None includes presentation or model/capture preparation, and none establishes desktop fps.",
            };
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            string? output = ValueAfter(args, "--output");
            if (output != null)
            {
                string absolute = Path.GetFullPath(output);
                Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
                File.WriteAllText(absolute, json);
            }
            if (output == null) Console.WriteLine(json);
            else Console.WriteLine($"Saved complete GPU validation report: {Path.GetFullPath(output)}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally { GpuRefreshDevice.ShutdownPool(); }
    }

    private static void CheckDeviceReuse()
    {
        const int width = 173, height = 97;
        byte[] source = Scene(width, height);
        var reference = Reference(width, height, source);
        var scene = new GpuRefreshScene(reference);
        var previous = new GpuRefreshRenderer(scene);
        nint native = previous.Device.NativeDevice;
        previous.Render(700);
        byte[] expected = Readback(previous);
        // Swapchains remain lease-local and must never survive in the idle cache.
        previous.Device.CreateSwapChain(width, height);
        previous.Dispose();
        using var reused = new GpuRefreshRenderer(scene);
        Require(reused.Device.ReusedBackend && reused.Device.NativeDevice == native,
            "A completed gesture did not reuse the idle native device.");
        Require(reused.Device.SwapChain == 0 && reused.Device.SwapChainScale == 0,
            "A reused device retained a previous window's swapchain or scale.");
        previous.Dispose();
        reused.Render(700);
        Require(Readback(reused).AsSpan().SequenceEqual(expected),
            "A stale gesture disposal or recycled device changed the new frame.");
        using var overlap = new GpuRefreshRenderer(scene);
        Require(overlap.Device.NativeDevice != reused.Device.NativeDevice,
            "Two active gestures share the same immediate context.");
        overlap.Render(1100);
        Require(Readback(reused).AsSpan().SequenceEqual(expected),
            "An overlapping gesture overwrote another gesture's output.");
        using var fresh = new GpuRefreshRenderer(scene, allowDeviceReuse: false);
        Require(!fresh.Device.ReusedBackend, "The diagnostic fresh-device path reused a backend.");
        fresh.Render(700);
        Require(Readback(fresh).AsSpan().SequenceEqual(expected),
            "Cached and fresh devices render different pixels.");
        // Shutdown must release an idle owner and reject returns from all live leases.
        overlap.Dispose();
        GpuRefreshDevice.ShutdownPool();
        reused.Dispose();
        using var afterShutdown = new GpuRefreshRenderer(scene);
        Require(!afterShutdown.Device.ReusedBackend, "Device shutdown still accepts or lends idle devices.");
        afterShutdown.Render(700);
        Require(Readback(afterShutdown).AsSpan().SequenceEqual(expected),
            "Shutdown cleanup damaged a later independent diagnostic device.");
        Console.WriteLine("Device reuse: fresh pixels, overlapping isolation, lease-local swapchain, stale disposal and shutdown checked.");
    }

    private static object MeasurePreparation(int width, int height)
    {
        byte[] source = Scene(width, height);
        var depth = Depth(width, height);
        var rounds = new List<object>();
        for (int round = 0; round < 7; round++)
        {
            var watch = Stopwatch.StartNew();
            var reference = new SpectrumOverlayRefreshRenderer(width, height, source, depth, width / 3, height / 2);
            double cpuMs = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            var scene = new GpuRefreshScene(reference);
            double mapsMs = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            using var gpu = new GpuRefreshRenderer(scene);
            double gpuMs = watch.Elapsed.TotalMilliseconds;
            rounds.Add(new { Round = round, CpuReferenceMs = cpuMs, GpuMapsMs = mapsMs,
                GpuCreationMs = gpuMs, TotalMs = cpuMs + mapsMs + gpuMs,
                gpu.Device.ReusedBackend });
        }
        return new { Width = width, Height = height, Rounds = rounds,
            Excludes = "Input generation, model inference, capture, UI presentation and release overlap." };
    }

    private static void RunCorrectness()
    {
        foreach ((int width, int height) in new[] { (1, 1), (17, 9), (173, 97), (1281, 73),
            (640, 360), (1281, 721), (1920, 1080), (3840, 2160) })
        {
            byte[] source = Scene(width, height);
            byte[] sourceHash = SHA256.HashData(source);
            var reference = Reference(width, height, source);
            var scene = new GpuRefreshScene(reference);
            using var gpu = new GpuRefreshRenderer(scene);
            Require(gpu.DurationMs == 2500 && GpuRefreshRenderer.ReferenceTimeRatio == 1.2,
                "The complete reference timing domain must be preserved in 2.5 seconds.");
            byte[] cpuFrame = new byte[source.Length], light = new byte[4];
            var accelerated = new AcceleratedRefreshRenderer(reference);
            Require(accelerated.DurationMs == 2500, "CPU fallback has a different duration from GPU playback.");
            foreach (double elapsed in new double[] { 0, 250, 700, 1500, 2499, 2500 })
            {
                byte[] fallbackFrame = new byte[source.Length];
                accelerated.Render(elapsed, fallbackFrame, light);
                reference.Render(elapsed * 1.2, cpuFrame, light);
                Require(fallbackFrame.AsSpan().SequenceEqual(cpuFrame), "CPU fallback does not preserve 1.2x reference timing.");
            }
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                try
                {
                    accelerated.Render(100, cpuFrame, light, cancelled.Token);
                    throw new InvalidOperationException("Canceled CPU fallback rendered a frame.");
                }
                catch (OperationCanceledException) { }
            }
            double[] times = width * height < 500_000
                ? [0, 300, 700, 1100, 1600, 2200, 2919, 3000]
                : [0, 700, 1100, 1600, 3000];
            foreach (double elapsed in times)
                Compare(reference, gpu, source, cpuFrame, light, elapsed, "initial origin");

            // The GPU must consume the small retiming upload, rather than stale preparation-
            // time origins or a fresh neural estimate. Source and geometry remain unchanged.
            reference.SetOrigin(width - 1, height - 1);
            scene.UpdateTimings(reference);
            gpu.SetTimings(scene.Blocks, scene.SpectrumParameters);
            foreach (double elapsed in width * height < 500_000
                ? new double[] { 0, 700, 1600, 2919, 3000 } : new double[] { 700, 1600 })
                Compare(reference, gpu, source, cpuFrame, light, elapsed, "retargeted origin");

            // Stateless elapsed-time evaluation is essential when a slow frame is skipped.
            gpu.Render(1100 / 1.2);
            byte[] first = Readback(gpu);
            gpu.Render(1800 / 1.2);
            gpu.Render(1100 / 1.2);
            Require(first.AsSpan().SequenceEqual(Readback(gpu)), "GPU output depends on previous frame order.");
            foreach (double invalid in new double[] { -1, double.NaN, double.PositiveInfinity, 2500, 5000 })
            {
                gpu.Render(invalid);
                Require(Readback(gpu).All(value => value == 0), "Invalid/end time retained stale GPU pixels.");
            }
            Require(SHA256.HashData(source).AsSpan().SequenceEqual(sourceHash), "GPU preparation/render mutated capture pixels.");
        }

        foreach (bool instantStart in new[] { false, true })
        foreach (var options in new RefreshAnimationSettings[]
        {
            new() { DurationMs = 600, DistanceDelayPercent = 0, PixelDurationPercent = 25 },
            new() { DurationMs = 6000, DistanceDelayPercent = 200, PixelDurationPercent = 200 },
            new() { DurationMs = 1400, DistanceDelayPercent = 75, PixelDurationPercent = 50 },
        })
        {
            const int width = 173, height = 97;
            byte[] source = Scene(width, height);
            var timing = new RefreshTimingProfile(options, instantStart);
            float[] depths = Enumerable.Range(0, width * height)
                .Select(p => (p % width) / (float)(width - 1)).ToArray();
            var reference = new SpectrumOverlayRefreshRenderer(width, height, source,
                new RefreshDepthMap(width, height, depths), width / 2, height / 2, timing: timing);
            using var gpu = new GpuRefreshRenderer(new GpuRefreshScene(reference));
            var fallback = new AcceleratedRefreshRenderer(reference, timing);
            Require(gpu.DurationMs == options.DurationMs && fallback.DurationMs == options.DurationMs,
                "Custom duration differs between CPU and GPU.");
            Require(gpu.TimeRatio == timing.ReferenceTimeRatio,
                "GPU clock ignored the custom or instant reference span.");
            if (instantStart)
                Require(timing.StartAt(.5f, .5f, 0) == 0,
                    "Instant-start GPU scene retained the archived scan lead-in.");
            byte[] pixels = new byte[source.Length], light = new byte[4], fallbackPixels = new byte[source.Length];
            foreach (double fraction in new[] { 0, .15, .4, .75, .99, 1.0 })
            {
                double referenceMs = fraction * timing.ReferenceDurationMs;
                Compare(reference, gpu, source, pixels, light, referenceMs,
                    $"{(instantStart ? "instant" : "archived")} custom {options.DurationMs}ms stagger{options.DistanceDelayPercent}% pulse{options.PixelDurationPercent}%");
                fallback.Render(fraction * timing.PlaybackDurationMs, fallbackPixels, light);
                reference.Render(fraction * timing.PlaybackDurationMs * timing.ReferenceTimeRatio, pixels, light);
                Require(fallbackPixels.AsSpan().SequenceEqual(pixels), "Custom CPU fallback clock is inconsistent.");
            }
            reference.SetOrigin(0, height - 1);
            var retimed = new GpuRefreshScene(reference);
            gpu.SetTimings(retimed.Blocks, retimed.SpectrumParameters);
            Compare(reference, gpu, source, pixels, light, timing.ReferenceDurationMs * .4,
                $"{(instantStart ? "instant" : "archived")} custom release retiming");
            if (instantStart)
            {
                double firstStart = double.PositiveInfinity;
                foreach (var block in reference.BlocksRenderer.Blocks)
                    firstStart = Math.Min(firstStart, block.StartMs);
                double firstPeak = firstStart + timing.RiseMs;
                Compare(reference, gpu, source, pixels, light, firstPeak,
                    "instant retargeted first visible pixel rise");
                gpu.Render(timing.PlaybackDurationMs);
                Require(Readback(gpu).All(value => value == 0),
                    "Instant-start GPU scene did not clear at the selected wall deadline.");
            }
        }

        // Disposal is intentionally repeated because menu cancellation and close can converge.
        var disposable = new GpuRefreshRenderer(new GpuRefreshScene(Reference(17, 9, Scene(17, 9))));
        disposable.Render(200);
        disposable.Dispose();
        disposable.Dispose();
        try
        {
            disposable.Render(200);
            throw new InvalidOperationException("A disposed GPU renderer accepted another frame.");
        }
        catch (ObjectDisposedException) { }
        Console.WriteLine("GPU correctness: reference pixels, grey initial, transparent end, retiming, source identity, stateless frames and repeat disposal checked.");
    }

    private static void Compare(SpectrumOverlayRefreshRenderer reference, GpuRefreshRenderer gpu,
        byte[] source, byte[] cpuFrame, byte[] light, double referenceMs, string phase)
    {
        reference.Render(referenceMs, cpuFrame, light);
        gpu.Render(referenceMs / gpu.TimeRatio);
        byte[] actual = Readback(gpu);
        Require(actual.Length == source.Length, "GPU readback has the wrong native dimensions.");
        if (referenceMs == 0)
            for (int pixel = 0; pixel < actual.Length; pixel += 4)
                Require(actual[pixel] == actual[pixel + 1] && actual[pixel] == actual[pixel + 2]
                    && actual[pixel + 3] == 255, "The initial GPU frame is not opaque grey.");
        if (referenceMs >= reference.DurationMs)
            Require(actual.All(value => value == 0), "The ending GPU frame is not fully transparent.");

        int[] differences = new int[source.Length / 4 * 3];
        long rawDifference = 0, visibleDifference = 0;
        int maximum = 0, rawMaximum = 0, at = 0, coreMaximum = 0;
        long coreSamples = 0;
        int width = gpu.Width;
        for (int pixel = 0; pixel < actual.Length; pixel += 4)
        {
            int a = actual[pixel + 3], expectedA = cpuFrame[pixel + 3];
            Require(actual[pixel] <= a && actual[pixel + 1] <= a && actual[pixel + 2] <= a,
                "GPU output contains a non-premultiplied channel.");
            for (int channel = 0; channel < 4; channel++)
            {
                int raw = Math.Abs(actual[pixel + channel] - cpuFrame[pixel + channel]);
                rawDifference += raw;
                rawMaximum = Math.Max(rawMaximum, raw);
                if (channel == 3) continue;
                int visible = actual[pixel + channel] + Multiply255(source[pixel + channel], 255 - a);
                int expected = cpuFrame[pixel + channel] + Multiply255(source[pixel + channel], 255 - expectedA);
                int difference = Math.Abs(visible - expected);
                differences[at++] = difference;
                maximum = Math.Max(maximum, difference);
                visibleDifference += difference;
            }
        }
        foreach (var block in reference.BlocksRenderer.Blocks)
        {
            if (UniformBlockRefreshRenderer.OpacityAt(referenceMs - block.StartMs, reference.Timing) <= 0) continue;
            for (int y = block.Top; y < block.Top + block.Height; y++)
            for (int x = block.Left; x < block.Left + block.Width; x++)
            {
                int pixel = (y * width + x) * 4;
                for (int channel = 0; channel < 3; channel++)
                {
                    int visible = actual[pixel + channel] + Multiply255(source[pixel + channel], 255 - actual[pixel + 3]);
                    int expected = cpuFrame[pixel + channel] + Multiply255(source[pixel + channel], 255 - cpuFrame[pixel + 3]);
                    coreMaximum = Math.Max(coreMaximum, Math.Abs(visible - expected));
                    coreSamples++;
                }
            }
        }
        Array.Sort(differences);
        int p99 = differences[(int)Math.Floor((differences.Length - 1) * .99)];
        var entry = new
        {
            Width = gpu.Width, Height = gpu.Height, ReferenceMs = referenceMs,
            PlaybackMs = referenceMs / gpu.TimeRatio, Phase = phase,
            MaximumVisibleByteError = maximum,
            P99VisibleByteError = p99,
            MeanVisibleByteError = (double)visibleDifference / differences.Length,
            MaximumRawByteError = rawMaximum,
            MeanRawByteError = (double)rawDifference / source.Length,
            ActiveCoreSamples = coreSamples,
            ActiveCoreMaximumVisibleByteError = coreMaximum,
        };
        Comparisons.Add(entry);
        Console.WriteLine(JsonSerializer.Serialize(entry));
        // Float shader arithmetic may round a channel on either side of its CPU boundary;
        // allow two byte levels at isolated pixels, rather than a broad visual tolerance.
        Require(p99 <= 1 && maximum <= 2,
            $"GPU/reference mismatch at {gpu.Width}x{gpu.Height} {referenceMs}ms ({phase}): p99={p99}, max={maximum}, core={coreMaximum}.");
    }

    private static object Measure(int width, int height)
    {
        byte[] source = Scene(width, height);
        var prepare = Stopwatch.StartNew();
        var reference = Reference(width, height, source);
        var scene = new GpuRefreshScene(reference);
        double prepareMs = prepare.Elapsed.TotalMilliseconds;
        var creation = Stopwatch.StartNew();
        using var gpu = new GpuRefreshRenderer(scene);
        double creationMs = creation.Elapsed.TotalMilliseconds;
        for (int index = 0; index < 16; index++)
        {
            gpu.Render((600 + index * 70) / 1.2);
            gpu.Device.Finish();
        }
        const int samples = 60;
        double[] submit = new double[samples], completed = new double[samples], gpuTimestamp = new double[samples];
        for (int index = 0; index < samples; index++)
        {
            double elapsed = (500 + index * 25) / 1.2;
            var timer = Stopwatch.StartNew();
            gpu.Render(elapsed);
            submit[index] = timer.Elapsed.TotalMilliseconds;
            gpu.Device.Finish();
            completed[index] = timer.Elapsed.TotalMilliseconds;
            gpuTimestamp[index] = gpu.Device.MeasureGpuMilliseconds(() => gpu.Render(elapsed));
            Require(double.IsFinite(gpuTimestamp[index]) && gpuTimestamp[index] >= 0,
                "A disjoint/invalid GPU timestamp cannot establish a frame duration.");
        }
        var result = new
        {
            Width = width, Height = height, Samples = samples,
            Adapter = gpu.Device.AdapterName,
            gpu.Device.VendorId,
            gpu.Device.FeatureLevel,
            gpu.Device.IsHardware,
            PreparedCpuSceneMs = prepareMs,
            GpuCreationUploadCompileAndInitialFinishMs = creationMs,
            CpuSubmission = Statistics(submit),
            CompletedFrameWall = Statistics(completed),
            GpuTimestamp = Statistics(gpuTimestamp),
            MeasuredPresentation = false,
            ReadbackDuringTiming = false,
            ModelIncluded = false,
        };
        Console.WriteLine(JsonSerializer.Serialize(result));
        return result;
    }

    private static object Statistics(double[] values)
    {
        double[] sorted = (double[])values.Clone();
        Array.Sort(sorted);
        return new { MinimumMs = sorted[0], MedianMs = sorted[sorted.Length / 2],
            P95Ms = sorted[(int)Math.Floor((sorted.Length - 1) * .95)], MaximumMs = sorted[^1], MeanMs = sorted.Average() };
    }

    private static byte[] Readback(GpuRefreshRenderer gpu) => gpu.Device.ReadBgra(gpu.OutputTexture);

    private static SpectrumOverlayRefreshRenderer Reference(int width, int height, byte[] source)
        => new(width, height, source, Depth(width, height), width / 3, height / 2);

    private static RefreshDepthMap Depth(int width, int height)
    {
        int w = Math.Min(width, 196), h = Math.Min(height, 112);
        var values = new float[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            float u = (x + .5f) / w, v = (y + .5f) / h;
            values[y * w + x] = Math.Clamp(.1f + .65f * u + .2f * MathF.Sin(v * 9 + u * 3), 0, 1);
        }
        return new RefreshDepthMap(w, h, values);
    }

    private static byte[] Scene(int width, int height)
    {
        var data = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = (y * width + x) * 4;
            bool checker = ((x / 17 + y / 13) & 1) == 0;
            bool lines = (y % 19 == 3 && x % 31 < 24) || (x % 23 == 4 && y % 19 < 13);
            data[offset] = lines ? (byte)240 : (byte)(20 + x * 210 / Math.Max(1, width - 1));
            data[offset + 1] = lines ? (byte)15 : (byte)(25 + y * 205 / Math.Max(1, height - 1));
            data[offset + 2] = lines ? (byte)250 : checker ? (byte)220 : (byte)50;
            data[offset + 3] = 255;
        }
        return data;
    }

    private static int Multiply255(int colour, int coverage)
    {
        int product = colour * coverage + 128;
        return (product + (product >> 8)) >> 8;
    }

    private static string? ValueAfter(string[] args, string key)
    {
        int index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

namespace Action_Wheel.Services
{
    // This check uses a known synthetic depth field. The product's learned estimator is
    // separately validated; no model, download, camera, screenshot or UI is needed here.
    internal sealed record RefreshDepthMap(int Width, int Height, float[] Values);
}
