using Action_Wheel.Services;
using System.Diagnostics;

const int width = 72, height = 48;
byte[] source = new byte[width * height * 4];
Fill(source, width, 8, 8, 24, 24, 24, 100, 220, 255);
Fill(source, width, 33, 8, 24, 24, 220, 80, 24, 160);
Fill(source, width, 8, 36, 2, 2, 24, 90, 220, 100);
Fill(source, width, 16, 34, 1, 5, 24, 90, 220, 100);
Fill(source, width, 14, 36, 5, 1, 24, 90, 220, 100);
byte[] original = (byte[])source.Clone();
byte[] outlined = RefreshOutline.Apply(source, width, height, 1);
Require(source.AsSpan().SequenceEqual(original), "source pigments remain available for coloured bloom");
Require(Pixel(20, 20).SequenceEqual(Source(20, 20)), "large particle interiors retain their source colour");
var core = Pixel(8, 18);
Require(core[2] == 255 && core[2] > core[1] && core[1] > core[0],
    "large-particle outline has a bright source-coloured line core");
Require(Math.Abs(core[0] * Source(8, 18)[2] - core[2] * Source(8, 18)[0]) < 255
    && Math.Abs(core[1] * Source(8, 18)[2] - core[2] * Source(8, 18)[1]) < 255,
    "outline core preserves source channel ratios instead of mixing white");
var nearRim = Pixel(10, 18);
Require(nearRim[2] > nearRim[1] && nearRim[1] > nearRim[0], "inner edge beside the core retains the particle hue");
Require(nearRim[2] > Source(10, 18)[2], "coloured near-edge is brighter than the unchanged interior");
var fine = Pixel(8, 36);
Require(fine[2] > fine[1] && fine[1] > fine[0] && fine[0] < fine[3] * 0.6,
    "tiny particles retain a source-coloured highlight without becoming white");
for (int i = 0; i < source.Length; i += 4)
{
    Require(outlined[i + 3] == source[i + 3], "outline preserves exact coverage and transparent gaps");
    Require(outlined[i] <= outlined[i + 3] && outlined[i + 1] <= outlined[i + 3]
        && outlined[i + 2] <= outlined[i + 3], "outlined image remains valid premultiplied BGRA");
}
Require(RefreshOutline.Apply(new byte[16], 2, 2, 1).All(value => value == 0), "empty layers remain empty");
byte[] scaled = new byte[width * height * 4];
Fill(scaled, width, 8, 8, 48, 32, 24, 100, 220, 255);
byte[] highDpi = RefreshOutline.Apply(scaled, width, height, 2);
int dpiRim = (20 * width + 10) * 4;
Require(highDpi[dpiRim] > outlined[(18 * width + 10) * 4], "bright-core thickness follows monitor DPI");

foreach (var size in new[] { (W: 1920, H: 1080), (W: 3840, H: 2160) })
{
    var frame = new byte[size.W * size.H * 4];
    for (int y = 0; y + 10 < size.H; y += 11)
    for (int x = 0; x + 10 < size.W; x += 11)
        Fill(frame, size.W, x, y, 10, 10, 32, 120, 230, 180);
    var result = new byte[3][];
    var watch = Stopwatch.StartNew();
    Parallel.For(0, result.Length, new ParallelOptions { MaxDegreeOfParallelism = 3 },
        i => result[i] = RefreshOutline.Apply(frame, size.W, size.H, 1));
    watch.Stop();
    Require(result.All(layer => layer.Length == frame.Length), "full monitor outline dimensions");
    Console.WriteLine($"Outline {size.W}x{size.H}: {watch.ElapsedMilliseconds} ms (synthetic, three dense layers, parallelism 3).");
}
if (args.Length == 2 && args[0] == "--export")
{
    Directory.CreateDirectory(args[1]);
    File.WriteAllBytes(Path.Combine(args[1], "source.bgra"), source);
    File.WriteAllBytes(Path.Combine(args[1], "outlined.bgra"), outlined);
    Console.WriteLine($"Exported {width}x{height} PBGRA outline fixture.");
}
Console.WriteLine("Refresh outline checks passed: source-coloured core and edge, silhouette, gaps, fine marks, alpha and DPI.");

ReadOnlySpan<byte> Pixel(int x, int y) => outlined.AsSpan((y * width + x) * 4, 4);
ReadOnlySpan<byte> Source(int x, int y) => source.AsSpan((y * width + x) * 4, 4);
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException($"Outline check failed: {message}.");
}
static void Fill(byte[] target, int w, int x, int y, int width, int height, byte b, byte g, byte r, byte a)
{
    for (int py = y; py < y + height; py++)
    for (int px = x; px < x + width; px++)
    {
        int i = (py * w + px) * 4;
        target[i] = (byte)(b * a / 255); target[i + 1] = (byte)(g * a / 255);
        target[i + 2] = (byte)(r * a / 255); target[i + 3] = a;
    }
}
