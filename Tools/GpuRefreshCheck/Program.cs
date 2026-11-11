using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Action_Wheel.Services;

internal static class Program
{
    private static readonly List<object> Comparisons = [];

    private static int Main(string[] args)
    {
        try
        {
            bool correctness = !args.Contains("--performance-only");
            bool performance = !args.Contains("--correctness-only");
            if (correctness) RunCorrectness();
            object[] measurements = performance ? [Measure(1920, 1080), Measure(3840, 2160)] : [];
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
        gpu.Render(referenceMs / GpuRefreshRenderer.ReferenceTimeRatio);
        byte[] actual = Readback(gpu);
        Require(actual.Length == source.Length, "GPU readback has the wrong native dimensions.");
        if (referenceMs == 0)
            for (int pixel = 0; pixel < actual.Length; pixel += 4)
                Require(actual[pixel] == actual[pixel + 1] && actual[pixel] == actual[pixel + 2]
                    && actual[pixel + 3] == 255, "The initial GPU frame is not opaque grey.");
        if (referenceMs == 3000)
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
            if (UniformBlockRefreshRenderer.OpacityAt(referenceMs - block.StartMs) <= 0) continue;
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
            PlaybackMs = referenceMs / GpuRefreshRenderer.ReferenceTimeRatio, Phase = phase,
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
