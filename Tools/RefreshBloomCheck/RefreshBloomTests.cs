using Action_Wheel.Services;
using System.Diagnostics;

const int width = 128, height = 96;
var source = new byte[width * height * 4];
Require(RefreshBloom.Build(source, width, height, 0, 0, width, height, 1) == null,
    "empty pixel layers allocate no light image");
Fill(source, width, 0, 0, width, height, 30, 30, 30, 255);
Require(RefreshBloom.Build(source, width, height, 0, 0, width, height, 1) == null,
    "dark pigments stay below the bloom soft knee");
Array.Clear(source);
Fill(source, width, 56, 40, 16, 16, 255, 72, 24, 128);
RefreshBloomImage bloom = RefreshBloom.Build(source, width, height, 0, 0, width, height, 1)
    ?? throw new InvalidOperationException("Bright translucent source produced no light.");
Require(bloom.Left < 0 && bloom.Top < 0 && bloom.Width > width && bloom.Height > height,
    "glow has room to extend past animation-cluster edges");
Require(bloom.Pixels.Length == bloom.PixelWidth * bloom.PixelHeight * 4,
    "low resolution bitmap matches its buffer");
long blue = 0, green = 0, red = 0;
bool outsidePigment = false;
for (int y = 0; y < bloom.PixelHeight; y++)
for (int x = 0; x < bloom.PixelWidth; x++)
{
    int i = (y * bloom.PixelWidth + x) * 4;
    Require(bloom.Pixels[i] <= bloom.Pixels[i + 3]
        && bloom.Pixels[i + 1] <= bloom.Pixels[i + 3]
        && bloom.Pixels[i + 2] <= bloom.Pixels[i + 3], "output is valid premultiplied BGRA");
    blue += bloom.Pixels[i]; green += bloom.Pixels[i + 1]; red += bloom.Pixels[i + 2];
    int px = bloom.Left + x * bloom.Width / bloom.PixelWidth;
    int py = bloom.Top + y * bloom.Height / bloom.PixelHeight;
    outsidePigment |= bloom.Pixels[i + 3] > 0 && (px < 56 || px >= 72 || py < 40 || py >= 56);
}
Require(red > green * 2 && green > blue * 2, "bloom retains its emitter's warm hue");
Require(outsidePigment, "bright particles illuminate neighbouring transparent pixels");

// A particle at a macro-cluster boundary must spread into the neighbouring cluster without
// copying that neighbour's independent (and possibly not-yet-visible) emitters into its halo.
Array.Clear(source);
Fill(source, width, 60, 24, 4, 40, 60, 180, 255, 255);
RefreshBloomImage edge = RefreshBloom.Build(source, width, height, 0, 0, 64, height, 1)
    ?? throw new InvalidOperationException("Cluster-edge emitter produced no light.");
bool acrossBoundary = false;
for (int y = 0; y < edge.PixelHeight; y++)
for (int x = 0; x < edge.PixelWidth; x++)
{
    int px = edge.Left + x * edge.Width / edge.PixelWidth;
    acrossBoundary |= px >= 64 && edge.Pixels[(y * edge.PixelWidth + x) * 4 + 3] > 0;
}
Require(acrossBoundary, "halo extends across macro-cluster boundaries");
Require(RefreshBloom.Build(source, width, height, 64, 0, 64, height, 1) == null,
    "unlit neighbouring cluster does not borrow other clusters' light");

// Monitor-edge/DPI placement keeps the visual radius in DIP and the source bounded.
RefreshBloomImage highDpi = RefreshBloom.Build(source, width, height, 0, 0, 64, height, 2)
    ?? throw new InvalidOperationException("High-DPI emitter produced no light.");
Require(highDpi.Left == edge.Left * 2 && highDpi.Top == edge.Top * 2,
    "bloom padding follows monitor DPI");

foreach (var size in new[] { (Width: 1920, Height: 1080), (Width: 3840, Height: 2160) })
{
    var layer = new byte[size.Width * size.Height * 4];
    for (int y = 0; y < size.Height; y += 11)
    for (int x = 0; x < size.Width; x += 11)
        Fill(layer, size.Width, x, y, Math.Min(10, size.Width - x), Math.Min(10, size.Height - y),
            44, 180, 250, 180);
    // Warm up before reporting preparation cost; this is not a per-frame cost.
    _ = RefreshBloom.Build(layer, size.Width, size.Height, 0, 0, 132, 132, 1);
    foreach (int parallelism in new[] { 1, Math.Min(4, Environment.ProcessorCount) }.Distinct())
    {
        var timer = Stopwatch.StartNew();
        RefreshBloomImage?[] frame = PrepareFrame(layer, size.Width, size.Height, parallelism);
        timer.Stop();
        long bytes = frame.Sum(image => (long)(image?.Pixels.Length ?? 0));
        Require(bytes < layer.LongLength, "three quarter-resolution light layers fit under one full image");
        Console.WriteLine($"Bloom {size.Width}x{size.Height}: {frame.Count(image => image != null)} light images, "
            + $"{bytes / 1024.0 / 1024.0:F2} MiB, {timer.ElapsedMilliseconds} ms preparation "
            + $"(synthetic, three dense layers, parallelism {parallelism}).");
    }
}
Console.WriteLine("Refresh bloom checks passed: threshold, colour, alpha, cluster padding, empty layers and DPI.");

static void Fill(byte[] pixels, int width, int left, int top, int rectangleWidth, int rectangleHeight,
    byte r, byte g, byte b, byte a)
{
    for (int y = top; y < top + rectangleHeight; y++)
    for (int x = left; x < left + rectangleWidth; x++)
    {
        int i = (y * width + x) * 4;
        pixels[i] = (byte)(b * a / 255);
        pixels[i + 1] = (byte)(g * a / 255);
        pixels[i + 2] = (byte)(r * a / 255);
        pixels[i + 3] = a;
    }
}

static RefreshBloomImage?[] PrepareFrame(byte[] layer, int width, int height, int parallelism)
{
    int columns = (width + 131) / 132, rows = (height + 131) / 132;
    var images = new RefreshBloomImage?[columns * rows * 3];
    Parallel.For(0, columns * rows, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, cluster =>
    {
        int left = cluster % columns * 132, top = cluster / columns * 132;
        for (int channel = 0; channel < 3; channel++)
            images[cluster * 3 + channel] = RefreshBloom.Build(layer, width, height, left, top,
                Math.Min(132, width - left), Math.Min(132, height - top), 1);
    });
    return images;
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException($"Refresh bloom check failed: {message}.");
}
