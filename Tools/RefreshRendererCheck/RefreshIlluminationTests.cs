using Action_Wheel.Services;

internal static class RefreshIlluminationTests
{
    internal static void Run()
    {
        CheckTimeEnvelope();
        CheckExposureAndCaptureOwnership();
        CheckBlackWhiteAndColour();
        CheckSpatialFalloff();
        CheckSceneEmission();
        CheckRendererIntegration();
        Console.WriteLine("Refresh illumination checks passed: visible exposure, colour and highlight detail, "
            + "click-centred falloff, scene bloom, radial cover, transparency and immutable captures.");
    }

    private static void CheckTimeEnvelope()
    {
        Require(RefreshIllumination.StrengthAt(0) == 0, "exposure starts transparent");
        Require(RefreshIllumination.StrengthAt(800) == 0
            && RefreshIllumination.StrengthAt(900) == 0, "exposure cannot outlive the refresh");
        Require(RefreshIllumination.StrengthAt(25) > 0, "exposure begins rising promptly");
        float earlyPeak = 0;
        for (int time = 0; time <= 180; time += 5)
            earlyPeak = Math.Max(earlyPeak, RefreshIllumination.StrengthAt(time));
        Require(earlyPeak > 0.9f, "exposure reaches its main strength early");
        Require(RefreshIllumination.StrengthAt(300) > RefreshIllumination.StrengthAt(650),
            "the early light fades towards the end");
        for (int time = 0; time <= 800; time += 5)
        {
            float strength = RefreshIllumination.StrengthAt(time);
            Require(float.IsFinite(strength) && strength is >= 0 and <= 1,
                "time weighting remains bounded and finite");
        }
    }

    private static void CheckExposureAndCaptureOwnership()
    {
        const int width = 129, height = 97, originX = 64, originY = 48;
        byte[] source = Solid(width, height, 32, 64, 96);
        for (int i = 3; i < source.Length; i += 4) source[i] = (byte)(i % 256);
        byte[] before = (byte[])source.Clone();
        var illumination = new RefreshIllumination(width, height, 4, originX, originY, source);
        byte[] opaque = (byte[])source.Clone();
        for (int i = 3; i < opaque.Length; i += 4) opaque[i] = 255;
        var opaqueIllumination = new RefreshIllumination(width, height, 4, originX, originY, opaque);
        Require(source.AsSpan().SequenceEqual(before), "preparation never changes the captured BGRA bytes");
        Require(illumination.Overlay.AsSpan().SequenceEqual(opaqueIllumination.Overlay)
            && illumination.Emission.AsSpan().SequenceEqual(opaqueIllumination.Emission),
            "undefined GDI source alpha does not change exposure or scene emission");
        ValidatePremultiplied(illumination.Overlay);
        int centre = Pixel(width, originX, originY);
        for (int channel = 0; channel < 3; channel++)
            Require(Visible(illumination.Overlay, source, centre, channel) > source[centre + channel],
                "the image itself brightens under ordinary source-over composition");
        double ratio = VisibleLuminance(illumination.Overlay, source, centre) / SourceLuminance(source, centre);
        Require(ratio > 1.35 && ratio < 1.85,
            "mid-tone exposure is a restrained linear-light lift rather than a 1.6x sRGB-byte multiply");
        double partial = Visible(illumination.Overlay, source, centre, 1, 0.5);
        double full = Visible(illumination.Overlay, source, centre, 1);
        Require(Math.Abs(partial - (source[centre + 1] + full) * 0.5) < 0.01,
            "fading premultiplied coverage fades the visible exposure without another opaque screenshot");
    }

    private static void CheckBlackWhiteAndColour()
    {
        const int width = 65, height = 49, originX = 32, originY = 24;
        int centre = Pixel(width, originX, originY);
        byte[] black = Solid(width, height, 0, 0, 0);
        var dark = new RefreshIllumination(width, height, 4, originX, originY, black);
        Require(dark.Overlay.All(value => value == 0) && dark.Emission.All(value => value == 0),
            "pure black creates neither a white veil nor scene bloom");

        double previous = -1;
        var highlights = new Dictionary<int, double>();
        foreach (int level in new[] { 0, 3, 12, 32, 64, 96, 160, 192, 224, 240, 248, 255 })
        {
            byte[] source = Solid(width, height, (byte)level, (byte)level, (byte)level);
            var light = new RefreshIllumination(width, height, 4, originX, originY, source);
            ValidatePremultiplied(light.Overlay);
            double value = Visible(light.Overlay, source, centre, 0);
            Require(value >= level - 0.6 && value <= 255, "neutral image values never darken or overflow");
            Require(value > previous, "the exposure preserves ordered shadow and highlight detail");
            Require(Math.Abs(value - Visible(light.Overlay, source, centre, 1)) < 0.01
                && Math.Abs(value - Visible(light.Overlay, source, centre, 2)) < 0.01,
                "neutral colours remain neutral");
            previous = value;
            highlights[level] = value;
        }
        Require(highlights[224] < highlights[248] && highlights[248] < highlights[255],
            "bright gradients retain detail instead of clipping into a flat white patch");
        Require(Math.Abs(highlights[255] - 255) < 0.01, "existing white remains white");

        byte[] red = Solid(width, height, 0, 0, 180);
        var redLight = new RefreshIllumination(width, height, 4, originX, originY, red);
        Require(Visible(redLight.Overlay, red, centre, 2) > 180
            && Visible(redLight.Overlay, red, centre, 0) == 0
            && Visible(redLight.Overlay, red, centre, 1) == 0,
            "a coloured source brightens without mixing white into its absent channels");
        byte[] saturated = Solid(width, height, 255, 96, 0);
        var saturatedLight = new RefreshIllumination(width, height, 4, originX, originY, saturated);
        Require(Math.Abs(Visible(saturatedLight.Overlay, saturated, centre, 0) - 255) < 0.6
            && Visible(saturatedLight.Overlay, saturated, centre, 1) > 96
            && Visible(saturatedLight.Overlay, saturated, centre, 2) == 0,
            "alpha quantization preserves an already saturated channel while lifting the others");
    }

    private static void CheckSpatialFalloff()
    {
        const int width = 129, height = 97;
        byte[] source = Solid(width, height, 80, 80, 80);
        foreach ((int originX, int originY) in new[] { (64, 48), (13, 21), (111, 75) })
        {
            var light = new RefreshIllumination(width, height, 4, originX, originY, source);
            int centre = Pixel(width, originX, originY);
            double peak = Visible(light.Overlay, source, centre, 0) - 80;
            Require(peak > 8, "an off-centre click keeps a substantial local exposure peak");
            int halfX = originX + (width - 1 - originX) / 2;
            double halfway = Visible(light.Overlay, source, Pixel(width, halfX, originY), 0) - 80;
            Require(halfway > 0 && halfway < peak * 0.85,
                "exposure rolls off smoothly away from the actual click");
            for (int x = 0; x < width; x++)
            {
                Require(Math.Abs(Visible(light.Overlay, source, Pixel(width, x, 0), 0) - 80) <= 1
                    && Math.Abs(Visible(light.Overlay, source, Pixel(width, x, height - 1), 0) - 80) <= 1,
                    "top and bottom screen edges have no meaningful exposure lift");
            }
            for (int y = 0; y < height; y++)
                Require(Math.Abs(Visible(light.Overlay, source, Pixel(width, 0, y), 0) - 80) <= 1
                    && Math.Abs(Visible(light.Overlay, source, Pixel(width, width - 1, y), 0) - 80) <= 1,
                    "left and right screen edges have no meaningful exposure lift");
        }
        var edgeClick = new RefreshIllumination(width, height, 4, 0, 48, source);
        Require(Visible(edgeClick.Overlay, source, Pixel(width, 0, 48), 0) > 88,
            "a click on the monitor edge still owns the exposure peak");
    }

    private static void CheckSceneEmission()
    {
        const int width = 65, height = 49;
        byte[] scene = Solid(width, height, 255, 0, 0);
        var light = new RefreshIllumination(width, height, 4, 32, 24, scene);
        Require(light.Emission.Length == ((width + 3) / 4) * ((height + 3) / 4) * 3,
            "scene bloom covers partial edge cells with BGR triplets");
        Require(light.Emission.Any(value => value > 0),
            "saturated scene highlights can bloom even when exposure has no channel headroom");
        for (int i = 0; i < light.Emission.Length; i += 3)
            Require(float.IsFinite(light.Emission[i]) && light.Emission[i] >= 0
                && light.Emission[i + 1] == 0 && light.Emission[i + 2] == 0,
                "scene bloom keeps its original pigment instead of emitting a white radial image");
        var tiny = new RefreshIllumination(1, 1, 4, 0, 0, [255, 255, 255, 0]);
        Require(tiny.Emission.Length == 3 && tiny.Emission.All(float.IsFinite),
            "a capture smaller than one bloom sample still has a valid light cell");
    }

    private static void CheckRendererIntegration()
    {
        const int width = 129, height = 97, originX = 64, originY = 48;
        byte[] scene = Solid(width, height, 96, 96, 96);
        byte[] sceneBefore = (byte[])scene.Clone();
        byte[] grey = Solid(width, height, 96, 96, 96, 255);
        byte[] greyBefore = (byte[])grey.Clone();
        RefreshRenderer legacy = Renderer(width, height, originX, originY, grey);
        RefreshRenderer current = Renderer(width, height, originX, originY, grey, scene);
        var oldFrame = new byte[scene.Length];
        var frame = new byte[scene.Length];
        var oldBloom = new byte[legacy.BloomWidth * legacy.BloomHeight * 4];
        var bloom = new byte[oldBloom.Length];
        current.Render(0, frame, bloom);
        Require(frame.AsSpan().SequenceEqual(grey) && bloom.All(value => value == 0),
            "the first refresh frame retains the opaque grey handoff without a flash ahead of it");
        int centre = Pixel(width, originX, originY);
        current.Render(150, frame, bloom);
        Require(frame[centre + 3] < frame[Pixel(width, 0, 0) + 3],
            "scene illumination retains the radial near-before-far grey reveal");
        foreach (double time in new[] { 80d, 150d, 260d, 450d, 650d })
        {
            legacy.Render(time, oldFrame, oldBloom);
            current.Render(time, frame, bloom);
            ValidatePremultiplied(frame); ValidatePremultiplied(bloom);
            for (int pixel = 0; pixel < frame.Length; pixel += 4)
                Require(Visible(frame, scene, pixel, 0) >= Visible(oldFrame, scene, pixel, 0) - 2.1,
                    "the exposure correction does not darken a neutral desktop during cover reveal");
            if (time == 260)
                Require(Visible(frame, scene, centre, 0) > 102 && frame[centre + 3] < 128,
                    "revealed desktop pixels brighten through translucent correction rather than an opaque frozen image");
        }
        current.Render(760, frame, bloom);
        Require(frame.All(value => value == 0) && bloom.All(value => value == 0),
            "scene-only illumination leaves no frozen screenshot after its tail");
        current.Render(800, frame, bloom);
        Require(frame.All(value => value == 0) && bloom.All(value => value == 0),
            "the complete scene effect is transparent at the refresh deadline");
        Require(scene.AsSpan().SequenceEqual(sceneBefore) && grey.AsSpan().SequenceEqual(greyBefore),
            "renderer playback leaves scene and grey preparation buffers immutable");

        // With no particle layers at all, only actual bright scene content can supply this light.
        byte[] bluePatch = Solid(width, height, 0, 0, 0);
        for (int y = 43; y < 53; y++)
        for (int x = 78; x < 88; x++) bluePatch[Pixel(width, x, y)] = 255;
        var sceneBloomRenderer = Renderer(width, height, 16, 48, grey, bluePatch);
        sceneBloomRenderer.Render(20, frame, bloom);
        Require(bloom.All(value => value == 0), "scene bloom does not precede its wavefront arrival");
        sceneBloomRenderer.Render(400, frame, bloom);
        Require(bloom.Any(value => value != 0), "a revealed scene highlight supplies bloom without particles");
        for (int pixel = 0; pixel < bloom.Length; pixel += 4)
            Require(bloom[pixel + 1] == 0 && bloom[pixel + 2] == 0,
                "scene-only blue bloom preserves its pigment through filtering");

        byte[] opaque = Solid(width, height, 0, 0, 0);
        int particlePixel = Pixel(width, 60, 44);
        opaque[particlePixel] = 60; opaque[particlePixel + 1] = 110;
        opaque[particlePixel + 2] = 180; opaque[particlePixel + 3] = 255;
        byte[][] layers = [opaque, new byte[scene.Length], new byte[scene.Length]];
        var particleRenderer = new RefreshRenderer(width, height, 8, 1, originX, originY,
            grey, layers, layers, [new(60, 44, 1, 1, RefreshPixelLayer.Subject, 0)], scene: scene);
        particleRenderer.Render(RefreshTiming.Envelope(RefreshPixelLayer.Subject, 0).PeakMs, frame, bloom);
        Require(frame.AsSpan(particlePixel, 4).SequenceEqual(opaque.AsSpan(particlePixel, 4)),
            "an opaque original-colour particle stays above the scene correction and grey cover");
    }

    private static RefreshRenderer Renderer(int width, int height, int originX, int originY,
        byte[] grey, byte[]? scene = null)
    {
        byte[][] empty = [new byte[grey.Length], new byte[grey.Length], new byte[grey.Length]];
        var wave = new RefreshWavefront(width, height, originX, originY, 1729);
        return new RefreshRenderer(width, height, 8, 1, originX, originY, grey, empty, empty, [], wave, scene);
    }

    private static byte[] Solid(int width, int height, byte blue, byte green, byte red, byte alpha = 0)
    {
        var result = new byte[width * height * 4];
        for (int i = 0; i < result.Length; i += 4)
        {
            result[i] = blue; result[i + 1] = green; result[i + 2] = red; result[i + 3] = alpha;
        }
        return result;
    }

    // Observe what an ordinary source-over Image actually displays over the original desktop.
    // This oracle deliberately does not reimplement the exposure or spatial-transfer functions.
    private static double Visible(byte[] overlay, byte[] scene, int pixel, int channel, double weight = 1)
        => overlay[pixel + channel] * weight
            + scene[pixel + channel] * (1 - overlay[pixel + 3] * weight / 255.0);

    private static double VisibleLuminance(byte[] overlay, byte[] scene, int pixel)
        => Linear(Visible(overlay, scene, pixel, 2)) * 0.2126
            + Linear(Visible(overlay, scene, pixel, 1)) * 0.7152
            + Linear(Visible(overlay, scene, pixel, 0)) * 0.0722;

    private static double SourceLuminance(byte[] scene, int pixel)
        => Linear(scene[pixel + 2]) * 0.2126 + Linear(scene[pixel + 1]) * 0.7152
            + Linear(scene[pixel]) * 0.0722;

    private static double Linear(double channel)
    {
        double value = channel / 255;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static int Pixel(int width, int x, int y) => (y * width + x) * 4;

    private static void ValidatePremultiplied(byte[] pixels)
    {
        for (int i = 0; i < pixels.Length; i += 4)
            Require(pixels[i] <= pixels[i + 3] && pixels[i + 1] <= pixels[i + 3]
                && pixels[i + 2] <= pixels[i + 3], "illumination output is valid premultiplied BGRA");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Refresh illumination check failed: {message}.");
    }
}
