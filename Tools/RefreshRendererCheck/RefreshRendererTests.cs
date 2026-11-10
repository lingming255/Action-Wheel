using Action_Wheel.Services;
using System.Diagnostics;

CheckBehaviour();
RefreshWavefrontTests.Run();
RefreshIlluminationTests.Run();
RefreshLightCompositeTests.Run();
if (args.Length == 2 && args[0] == "--export") Export(args[1]);
foreach (var size in new[] { (W: 1920, H: 1080), (W: 3840, H: 2160) })
{
    const int tile = 11;
    var wavefront = new RefreshWavefront(size.W, size.H, size.W / 2, size.H / 2, 1729);
    var grey = new byte[size.W * size.H * 4];
    var source = new byte[grey.Length];
    var fine = new byte[grey.Length];
    var edge = new byte[grey.Length];
    var particles = new List<RefreshParticle>();
    for (int y = 0; y < size.H; y++)
    for (int x = 0; x < size.W; x++)
    {
        int i = (y * size.W + x) * 4;
        grey[i] = grey[i + 1] = grey[i + 2] = 100; grey[i + 3] = 255;
    }
    for (int y = 0; y < size.H; y += tile)
    for (int x = 0; x < size.W; x += tile)
    {
        int w = Math.Min(tile - 1, size.W - x), h = Math.Min(tile - 1, size.H - y);
        Fill(source, size.W, x, y, w, h, 40, 180, 255, 170);
        particles.Add(new RefreshParticle(x, y, w, h, RefreshPixelLayer.Subject,
            wavefront.FrontAt(x + w * 0.5, y + h * 0.5)));
    }
    for (int y = 0; y < size.H; y += 3)
    for (int x = 0; x < size.W; x += 3)
    {
        if ((x + y) % 4 != 0) continue;
        int w = Math.Min(2, size.W - x), h = Math.Min(2, size.H - y);
        Fill(fine, size.W, x, y, w, h, 40, 180, 255, 70);
        particles.Add(new RefreshParticle(x, y, w, h, RefreshPixelLayer.Background,
            wavefront.FrontAt(x + w * 0.5, y + h * 0.5)));
    }
    byte[][] layers = [source, fine, edge];
    var scene = new byte[grey.Length];
    Fill(scene, size.W, 0, 0, size.W, size.H, 40, 180, 255, 255);
    var prepare = Stopwatch.StartNew();
    var renderer = new RefreshRenderer(size.W, size.H, tile, 1, size.W / 2, size.H / 2,
        grey, layers, layers, particles.ToArray(), wavefront, scene);
    prepare.Stop();
    var output = new byte[grey.Length];
    var bloom = new byte[renderer.BloomWidth * renderer.BloomHeight * 4];
    foreach (double t in new[] { 0d, 100d, 250d, 400d, 600d, 800d }) renderer.Render(t, output, bloom);
    long allocationBefore = GC.GetTotalAllocatedBytes(precise: true);
    var timer = Stopwatch.StartNew();
    for (int repeat = 0; repeat < 5; repeat++) renderer.Render(250, output, bloom);
    timer.Stop();
    long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocationBefore;
    Console.WriteLine($"Renderer with scene light {size.W}x{size.H}: {particles.Count:N0} particles, prepare={prepare.ElapsedMilliseconds}ms, "
        + $"render250={timer.Elapsed.TotalMilliseconds / 5:F2}ms, measured allocations={allocated} bytes / 5 frames.");
    // Bounded band scheduling may allocate small tasks; count all managed threads so
    // frame-sized allocations cannot hide on a compositor worker.
    Require(allocated < 65536, "render uses bounded scheduling without frame-sized allocations");
    foreach (double t in new[] { 0d, 100d, 250d, 400d, 600d, 800d })
    {
        timer.Restart(); renderer.Render(t, output, bloom); timer.Stop();
        ValidatePremultiplied(output); ValidatePremultiplied(bloom);
        Console.WriteLine($"  t={t:F0}ms: {timer.Elapsed.TotalMilliseconds:F2}ms");
    }
}
Console.WriteLine("Refresh renderer checks passed: independent particle timing, whole merged rectangles, radial cover, scene illumination, active bloom, transparency and immutable sources.");

static void CheckBehaviour()
{
    const int w = 160, h = 96;
    var grey = new byte[w * h * 4];
    var subject = new byte[grey.Length];
    Fill(subject, w, 12, 12, 20, 10, 48, 160, 255, 200);
    Fill(subject, w, 100, 12, 20, 10, 48, 160, 255, 200);
    byte[][] layers = [subject, new byte[grey.Length], new byte[grey.Length]];
    byte[] before = (byte[])subject.Clone();
    RefreshParticle[] particles = [
        new(12, 12, 20, 10, RefreshPixelLayer.Subject, 0),
        new(100, 12, 20, 10, RefreshPixelLayer.Subject, RefreshTiming.SweepMs * 0.7),
    ];
    var renderer = new RefreshRenderer(w, h, 11, 1, 10, 10, grey, layers, layers, particles);
    var output = new byte[grey.Length];
    var bloom = new byte[renderer.BloomWidth * renderer.BloomHeight * 4];
    RefreshEnvelope near = RefreshTiming.Envelope(RefreshPixelLayer.Subject, 0);
    renderer.Render(near.PeakMs, output, bloom);
    int expectedAlpha = output[(12 * w + 12) * 4 + 3];
    Require(expectedAlpha > 0 && output[(12 * w + 100) * 4 + 3] == 0,
        "particles in a shared old macro footprint have independent radial times");
    for (int y = 12; y < 22; y++)
    for (int x = 12; x < 32; x++)
        Require(output[(y * w + x) * 4 + 3] == expectedAlpha,
            "one merged rectangle rises as a whole rather than being sliced by the front");
    Require(bloom.Any(value => value != 0), "active original pigment produces coloured bloom");
    Require(output[(12 * w + 32) * 4 + 3] == 0, "particle geometry and transparent gap remain unchanged");
    renderer.Render(RefreshTiming.TotalMs, output, bloom);
    Require(output.All(value => value == 0) && bloom.All(value => value == 0),
        "all overlay pixels and bloom are transparent at 800ms");
    renderer.Render(near.EndMs + 1, output, bloom);
    Require(output[(12 * w + 12) * 4 + 3] == 0, "earlier particle has already disappeared");
    Require(bloom.All(value => value == 0), "bloom clears when its emitting particle has faded");
    Require(subject.AsSpan().SequenceEqual(before), "render never changes the original or outlined input buffers");
    ValidatePremultiplied(output); ValidatePremultiplied(bloom);

    // An opaque foreground at its peak must fully replace the grey pixel underneath, without
    // a residual destination contribution from the packed source-over approximation.
    var opaque = new byte[grey.Length];
    Fill(opaque, w, 12, 12, 20, 10, 48, 160, 255, 255);
    Fill(grey, w, 0, 0, w, h, 100, 100, 100, 255);
    byte[][] opaqueLayers = [opaque, new byte[grey.Length], new byte[grey.Length]];
    var opaqueRenderer = new RefreshRenderer(w, h, 11, 1, 10, 10, grey, opaqueLayers,
        opaqueLayers, [particles[0]]);
    opaqueRenderer.Render(near.PeakMs, output,
        new byte[opaqueRenderer.BloomWidth * opaqueRenderer.BloomHeight * 4]);
    int firstPixel = (12 * w + 12) * 4;
    Require(output.AsSpan(firstPixel, 4).SequenceEqual(opaque.AsSpan(firstPixel, 4)),
        "opaque source-over completely replaces the destination");

    Fill(grey, w, 0, 0, w, h, 100, 100, 100, 255);
    var coverRenderer = new RefreshRenderer(w, h, 11, 1, 0, 0, grey,
        [new byte[grey.Length], new byte[grey.Length], new byte[grey.Length]],
        [new byte[grey.Length], new byte[grey.Length], new byte[grey.Length]], []);
    coverRenderer.Render(RefreshTiming.CoverFadeMs * 0.8, output,
        new byte[coverRenderer.BloomWidth * coverRenderer.BloomHeight * 4]);
    Require(output[3] < output[((h - 1) * w + w - 1) * 4 + 3],
        "near cover tiles fade before far cover tiles");
}

static void Export(string directory)
{
    const int width = 640, height = 360;
    var source = new byte[width * height * 4];
    for (int y = 0; y < height; y++)
    for (int x = 0; x < width; x++)
    {
        byte r = (byte)(24 + x * 48 / width), g = (byte)(38 + y * 45 / height), b = (byte)(58 + x * 65 / width);
        if (x > 70 && x < 270 && y > 50 && y < 270) (r, g, b) = (38, 145, 225);
        if (x > 325 && x < 570 && y > 105 && y < 305) (r, g, b) = (235, 116, 52);
        if (x > 235 && x < 410 && y > 155 && y < 325) (r, g, b) = (70, 192, 118);
        int i = (y * width + x) * 4;
        source[i] = b; source[i + 1] = g; source[i + 2] = r; source[i + 3] = 255;
    }
    var grey = new byte[source.Length];
    for (int i = 0; i < source.Length; i += 4)
    {
        byte luma = (byte)(source[i] * 0.114 + source[i + 1] * 0.587 + source[i + 2] * 0.299);
        grey[i] = (byte)(luma * 0.9 + source[i] * 0.1);
        grey[i + 1] = (byte)(luma * 0.9 + source[i + 1] * 0.1);
        grey[i + 2] = (byte)(luma * 0.9 + source[i + 2] * 0.1); grey[i + 3] = 255;
    }
    RefreshAnalysis analysis = RefreshAnalyzer.Build(source, width, height, 11, 1729, 160, 180);
    byte[][] original = [analysis.Subject, analysis.Background, analysis.Boundary];
    byte[][] outlined = original.Select(layer => RefreshOutline.Apply(layer, width, height, 1)).ToArray();
    var renderer = new RefreshRenderer(width, height, 11, 1, 160, 180,
        grey, original, outlined, analysis.Particles, analysis.Wavefront, source);
    var frame = new byte[source.Length];
    var bloom = new byte[renderer.BloomWidth * renderer.BloomHeight * 4];
    Directory.CreateDirectory(directory);
    File.WriteAllBytes(Path.Combine(directory, "source.bgra"), source);
    File.WriteAllText(Path.Combine(directory, "dimensions.json"),
        $"{{\"width\":{width},\"height\":{height},\"bloomWidth\":{renderer.BloomWidth},\"bloomHeight\":{renderer.BloomHeight},\"format\":\"premultiplied BGRA8\"}}");
    foreach (double time in new[] { 120d, 240d, 360d, 480d, 640d, 800d })
    {
        renderer.Render(time, frame, bloom);
        File.WriteAllBytes(Path.Combine(directory, $"main-{time:F0}.bgra"), frame);
        File.WriteAllBytes(Path.Combine(directory, $"bloom-{time:F0}.bgra"), bloom);
    }
    Console.WriteLine($"Exported exact-particle synthetic frames: {directory}");
}

static void ValidatePremultiplied(byte[] pixels)
{
    for (int i = 0; i < pixels.Length; i += 4)
        Require(pixels[i] <= pixels[i + 3] && pixels[i + 1] <= pixels[i + 3]
            && pixels[i + 2] <= pixels[i + 3], "output is valid premultiplied BGRA");
}

static void Fill(byte[] pixels, int width, int left, int top, int rectangleWidth, int rectangleHeight,
    byte r, byte g, byte b, byte a)
{
    for (int y = top; y < top + rectangleHeight; y++)
    for (int x = left; x < left + rectangleWidth; x++)
    {
        int i = (y * width + x) * 4;
        pixels[i] = (byte)(b * a / 255); pixels[i + 1] = (byte)(g * a / 255);
        pixels[i + 2] = (byte)(r * a / 255); pixels[i + 3] = a;
    }
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException($"Refresh renderer check failed: {message}.");
}
