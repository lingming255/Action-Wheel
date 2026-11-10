using Action_Wheel.Services;
using System.Runtime.InteropServices;

internal static class RefreshLightCompositeTests
{
    internal static void Run()
    {
        CheckVisibleAdditiveLight();
        CheckBackgroundDimming();
        CheckBatchBackgroundDimming();
        CheckCaptureAlphaAndOwnership();
        CheckInterpolatedBloom();
        CheckCombinedCoreAndBloom();
        CheckCoreBandsAtLargeDimensions();
        Console.WriteLine("Refresh light composite checks passed: visible RGB addition, background contrast, "
            + "unchanged unlit channels, saturation, premultiplied output and smooth bloom.");
    }

    private static void CheckVisibleAdditiveLight()
    {
        byte[] scene = [50, 100, 180, 0];
        var composite = new RefreshLightComposite(1, 1, scene);
        uint blueLight = composite.Add(0, 0, 90, 0, 0);
        Require(Math.Abs(Visible(blueLight, scene[0], 0) - 140) <= 2,
            "blue light adds brightness to the original blue channel");
        Require(Math.Abs(Visible(blueLight, scene[1], 1) - 100) <= 2
            && Math.Abs(Visible(blueLight, scene[2], 2) - 180) <= 2,
            "a blue glow does not dim the original green or red channels like a tinted alpha image");
        Require((blueLight >> 24) < 255, "a moderate glow keeps the desktop visible underneath");

        var random = new Random(4421);
        double largestError = 0;
        for (int sample = 0; sample < 5000; sample++)
        {
            int alpha = random.Next(256);
            uint overlay = Pack(random.Next(alpha + 1), random.Next(alpha + 1),
                random.Next(alpha + 1), alpha);
            for (int channel = 0; channel < 3; channel++) scene[channel] = (byte)random.Next(256);
            var prepared = new RefreshLightComposite(1, 1, scene);
            int b = random.Next(600), g = random.Next(600), r = random.Next(600);
            uint result = prepared.Add(overlay, 0, b, g, r);
            ValidatePremultiplied(result);
            int[] energy = [b, g, r];
            for (int channel = 0; channel < 3; channel++)
            {
                double before = Visible(overlay, scene[channel], channel);
                double expected = Math.Min(255, before + energy[channel]);
                double actual = Visible(result, scene[channel], channel);
                largestError = Math.Max(largestError, Math.Abs(expected - actual));
                Require(Math.Abs(expected - actual) <= 2,
                    "addition over transparent, translucent and opaque content matches visible RGB energy");
                Require(actual >= before - 2 && actual <= 255.01,
                    "added light cannot meaningfully darken or overflow any channel");
            }
            Require(prepared.Add(overlay, 0, 0, 0, 0) == overlay,
                "zero energy leaves the existing PBGRA pixel exactly unchanged");
        }
        Console.WriteLine($"  Additive source-over maximum sampled error: {largestError:F3} byte levels.");
    }

    private static void CheckBackgroundDimming()
    {
        var random = new Random(317);
        foreach (int dim in new[] { 0, 1, 24, 51, 128, 255, 256 })
        for (int sample = 0; sample < 160; sample++)
        {
            int alpha = random.Next(256);
            uint overlay = Pack(random.Next(alpha + 1), random.Next(alpha + 1),
                random.Next(alpha + 1), alpha);
            uint result = RefreshLightComposite.Dim(overlay, dim);
            ValidatePremultiplied(result);
            for (int channel = 0; channel < 3; channel++)
            {
                int liveDesktop = random.Next(256);
                double expected = Visible(overlay, liveDesktop, channel) * (1 - dim / 256.0);
                Require(Math.Abs(Visible(result, liveDesktop, channel) - expected) <= 2,
                    "background dimming multiplies both covered and live desktop pixels");
            }
            if (dim == 0) Require(result == overlay, "zero dimming is an exact identity");
            if (dim == 256) Require(result == 0xFF000000u, "full dimming is opaque black");
        }

        byte[] source = [100, 100, 100, 0];
        var light = new RefreshLightComposite(1, 1, source);
        uint background = RefreshLightComposite.Dim(0, 51);
        uint luminousPixel = light.Add(background, 0, 0, 120, 0);
        Require(Visible(background, 100, 1) < 83 && Visible(luminousPixel, 100, 1) > 195,
            "a luminous core stays bright when its background is darkened for contrast");
        Require(Math.Abs(Visible(luminousPixel, 100, 0) - Visible(background, 100, 0)) <= 2
            && Math.Abs(Visible(luminousPixel, 100, 2) - Visible(background, 100, 2)) <= 2,
            "dimming is applied before light and does not multiply the emitted energy");
    }

    private static void CheckCaptureAlphaAndOwnership()
    {
        byte[] source = [34, 80, 121, 0, 88, 147, 201, 25];
        byte[] opaque = (byte[])source.Clone();
        opaque[3] = opaque[7] = 255;
        byte[] before = (byte[])source.Clone();
        var undefinedAlpha = new RefreshLightComposite(2, 1, source);
        var opaqueAlpha = new RefreshLightComposite(2, 1, opaque);
        for (int pixel = 0; pixel < 2; pixel++)
            Require(undefinedAlpha.Add(0x32140A1Eu, pixel, 26, 50, 90)
                == opaqueAlpha.Add(0x32140A1Eu, pixel, 26, 50, 90),
                "GDI capture alpha does not affect the derived light correction");
        var output = new byte[source.Length];
        undefinedAlpha.AddBloom(output, [0.2f, 0.1f, 0.05f], 1, 1, 4);
        Require(source.AsSpan().SequenceEqual(before), "light preparation and rendering never modify the capture");
    }

    private static void CheckBatchBackgroundDimming()
    {
        // Several complete vectors plus an uneven tail exercise both accelerated rows and the
        // scalar remainder, independently observing their displayed colour over a desktop.
        const int count = 103;
        var original = new uint[count];
        var desktop = new byte[count * 3];
        var random = new Random(5181);
        for (int pixel = 0; pixel < count; pixel++)
        {
            int alpha = pixel == 0 ? 0 : pixel == 1 ? 255 : random.Next(256);
            original[pixel] = Pack(random.Next(alpha + 1), random.Next(alpha + 1),
                random.Next(alpha + 1), alpha);
        }
        random.NextBytes(desktop);
        foreach (int dim in new[] { 0, 1, 51, 255, 256 })
        {
            byte[] frame = MemoryMarshal.AsBytes(original.AsSpan()).ToArray();
            RefreshLightComposite.DimFrame(frame, dim);
            ReadOnlySpan<uint> result = MemoryMarshal.Cast<byte, uint>(frame.AsSpan());
            for (int pixel = 0; pixel < count; pixel++)
            {
                ValidatePremultiplied(result[pixel]);
                Require(result[pixel] == RefreshLightComposite.Dim(original[pixel], dim),
                    "bulk dimming and its scalar remainder produce identical pixel coverage");
                for (int channel = 0; channel < 3; channel++)
                {
                    int live = desktop[pixel * 3 + channel];
                    double expected = Visible(original[pixel], live, channel) * (1 - dim / 256.0);
                    Require(Math.Abs(Visible(result[pixel], live, channel) - expected) <= 1.5,
                        "vector and tail dimming preserve the expected contrast over arbitrary live colours");
                }
            }
        }
    }

    private static void CheckInterpolatedBloom()
    {
        const int width = 33, height = 9, step = 8;
        const int bloomWidth = 5, bloomHeight = 2;
        var source = new byte[width * height * 4];
        for (int pixel = 0; pixel < width * height; pixel++)
        {
            source[pixel * 4] = 40;
            source[pixel * 4 + 1] = 70;
            source[pixel * 4 + 2] = 90;
        }
        var composite = new RefreshLightComposite(width, height, source);
        var output = new byte[source.Length];
        var glow = new float[bloomWidth * bloomHeight * 3];
        for (int y = 0; y < bloomHeight; y++)
        for (int x = 0; x < bloomWidth; x++)
            glow[(y * bloomWidth + x) * 3] = x switch { 1 => 0.1f, 2 => 0.2f, 3 => 0.1f, _ => 0 };
        composite.AddBloom(output, glow, bloomWidth, bloomHeight, step);
        Span<uint> pixels = MemoryMarshal.Cast<byte, uint>(output.AsSpan());
        var distinctLevels = new HashSet<int>();
        double previous = -1;
        for (int x = 4; x <= 16; x++)
        {
            uint pixel = pixels[4 * width + x];
            ValidatePremultiplied(pixel);
            double visible = Visible(pixel, 40, 0);
            distinctLevels.Add((int)Math.Round(visible));
            if (previous >= 0)
                Require(Math.Abs(visible - previous) <= 5,
                    "upsampled bloom crosses low-resolution cell boundaries without square jumps");
            previous = visible;
            Require(Math.Abs(Visible(pixel, 70, 1) - 70) <= 2
                && Math.Abs(Visible(pixel, 90, 2) - 90) <= 2,
                "filtered blue bloom preserves the other desktop channels");
        }
        Require(distinctLevels.Count >= 10, "bloom interpolation creates smooth levels rather than nearest-cell blocks");
        foreach (uint pixel in pixels) ValidatePremultiplied(pixel);
        byte[] before = (byte[])output.Clone();
        composite.AddBloom(output, new float[glow.Length], bloomWidth, bloomHeight, step);
        Require(output.AsSpan().SequenceEqual(before), "an empty bloom field leaves the frame exactly unchanged");
    }

    private static void CheckCombinedCoreAndBloom()
    {
        const int width = 8, height = 4, step = 4, litPixel = 12;
        var scene = new byte[width * height * 4];
        for (int pixel = 0; pixel < width * height; pixel++)
        {
            scene[pixel * 4] = 20; scene[pixel * 4 + 1] = 40; scene[pixel * 4 + 2] = 60;
        }
        var composite = new RefreshLightComposite(width, height, scene);
        var frame = new byte[scene.Length];
        var cores = new uint[width * height];
        cores[litPixel] = Pack(90, 0, 0, 0);
        uint[] originalCores = (uint[])cores.Clone();
        composite.AddBloom(frame, new float[6], 2, 1, step, cores);
        Span<uint> output = MemoryMarshal.Cast<byte, uint>(frame.AsSpan());
        Require(Math.Abs(Visible(output[litPixel], 20, 0) - 110) <= 2,
            "a core is composited even when its entire bloom row has zero blurred energy");
        Require(Math.Abs(Visible(output[litPixel], 40, 1) - 40) <= 2
            && Math.Abs(Visible(output[litPixel], 60, 2) - 60) <= 2,
            "a core-only blue light preserves the other desktop channels");
        for (int pixel = 0; pixel < output.Length; pixel++)
            if (pixel != litPixel) Require(output[pixel] == 0, "core-only light retains its exact pixel footprint");
        Require(cores.AsSpan().SequenceEqual(originalCores), "the composite reads core energy without consuming or changing it");

        frame.AsSpan().Clear();
        cores[litPixel] = Pack(230, 30, 0, 0);
        // Both low-resolution cells are identical, so interpolation must preserve this
        // constant light field. The centre receives its core and this glow in one pass.
        composite.AddBloom(frame, [0.2f, 0.2f, 0f, 0.2f, 0.2f, 0f], 2, 1, step, cores);
        Require(Math.Abs(Visible(output[litPixel], 20, 0) - 255) <= 2
            && Math.Abs(Visible(output[litPixel], 40, 1) - 121) <= 2
            && Math.Abs(Visible(output[litPixel], 60, 2) - 60) <= 2,
            "core and blurred glow add before final channel saturation without dimming unrelated colour");
        foreach (uint pixel in output) ValidatePremultiplied(pixel);
        Require(Math.Abs(Visible(output[0], 20, 0) - 71) <= 2
            && Math.Abs(Visible(output[0], 40, 1) - 91) <= 2,
            "a constant bloom field stays constant away from the separate sharp core");
    }

    private static void CheckCoreBandsAtLargeDimensions()
    {
        // This non-divisible height crosses the helper's bounded parallel threshold. Put
        // isolated cores around row partition boundaries as well as the final scanline.
        const int width = 1025, height = 513, step = 4;
        var scene = new byte[width * height * 4];
        for (int pixel = 0; pixel < width * height; pixel++)
        {
            scene[pixel * 4] = 17; scene[pixel * 4 + 1] = 41; scene[pixel * 4 + 2] = 73;
        }
        var frame = new byte[scene.Length];
        var cores = new uint[width * height];
        foreach (int y in new[] { 0, 127, 128, 255, 256, 383, 384, 512 })
            cores[y * width + y * 17 % width] = Pack(45, 90, 0, 0);
        var composite = new RefreshLightComposite(width, height, scene);
        int bloomWidth = (width + step - 1) / step, bloomHeight = (height + step - 1) / step;
        composite.AddBloom(frame, new float[bloomWidth * bloomHeight * 3], bloomWidth, bloomHeight, step, cores);
        ReadOnlySpan<uint> output = MemoryMarshal.Cast<byte, uint>(frame.AsSpan());
        for (int pixel = 0; pixel < output.Length; pixel++)
        {
            if (cores[pixel] == 0)
            {
                Require(output[pixel] == 0, "separate row bands leave every unlit pixel untouched");
                continue;
            }
            ValidatePremultiplied(output[pixel]);
            Require(Math.Abs(Visible(output[pixel], 17, 0) - 62) <= 2
                && Math.Abs(Visible(output[pixel], 41, 1) - 131) <= 2
                && Math.Abs(Visible(output[pixel], 73, 2) - 73) <= 2,
                "row partition boundaries and the last row receive their isolated core exactly once");
        }
    }

    // Observe ordinary PBGRA source-over over the original scene, independently of how a
    // residual alpha is derived. These checks test the displayed result, not its implementation.
    private static double Visible(uint overlay, int desktop, int channel)
        => ((overlay >> (channel * 8)) & 255) + desktop * (1 - (overlay >> 24) / 255.0);

    private static uint Pack(int b, int g, int r, int a)
        => (uint)(b | g << 8 | r << 16 | a << 24);

    private static void ValidatePremultiplied(uint pixel)
    {
        uint alpha = pixel >> 24;
        Require((pixel & 255) <= alpha && ((pixel >> 8) & 255) <= alpha
            && ((pixel >> 16) & 255) <= alpha, "composite output remains valid PBGRA");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Refresh light composite check failed: {message}.");
    }
}
