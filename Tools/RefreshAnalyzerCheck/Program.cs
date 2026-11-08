using Action_Wheel.Services;
using System.Diagnostics;

const int width = 320, height = 192, tile = 8, seed = 1729;
Require(Math.Abs(RefreshAnalyzer.TileDip - 11.2) < 0.001, "analysis tiles are 20% smaller than the legacy grid");
Require(RefreshAnalyzer.TilePixels(1) == 11 && RefreshAnalyzer.TilePixels(1.5) == 17,
    "analysis tile rounding follows monitor DPI");
var source = new byte[width * height * 4];

// Synthetic landscape: two broad colour regions and a central foreground subject.
for (int y = 0; y < height; y++)
for (int x = 0; x < width; x++)
{
    byte r, g, b;
    if (x < width / 2) (r, g, b) = (48, 126, 178);       // water
    else (r, g, b) = (197, 163, 74);                     // dry grass
    if (x is >= 116 and < 210 && y is >= 42 and < 160)   // foreground subject
        (r, g, b) = ((byte)(50 + x % 13), (byte)(132 + y % 11), (byte)(65 + (x + y) % 13));
    int i = (y * width + x) * 4;
    source[i] = b; source[i + 1] = g; source[i + 2] = r; source[i + 3] = 255;
}

int originX = width / 4, originY = height * 3 / 4;
RefreshAnalysis first = RefreshAnalyzer.Build(source, width, height, tile, seed, originX, originY);
RefreshAnalysis second = RefreshAnalyzer.Build(source, width, height, tile, seed, originX, originY);
Require(first.Subject.Length == source.Length, "subject layer dimensions");
Require(first.Background.Length == source.Length, "background layer dimensions");
Require(first.Boundary.Length == source.Length, "boundary layer dimensions");
Require(first.Subject.AsSpan().SequenceEqual(second.Subject), "stable subject output for a fixed seed");
Require(first.Background.AsSpan().SequenceEqual(second.Background), "stable background output for a fixed seed");
Require(first.Boundary.AsSpan().SequenceEqual(second.Boundary), "stable boundary output for a fixed seed");

long subjectPixels = CountAlpha(first.Subject, width, 116, 42, 210, 160);
long backgroundPixels = CountAlpha(first.Background, width, 116, 42, 210, 160);
long boundaryPixels = CountAlpha(first.Boundary, width, width / 2 - 12, 0, width / 2 + 12, height);
byte[][] layers = [first.Subject, first.Background, first.Boundary];
Require(subjectPixels > 0, "subject layer has visible blocks");
Require(backgroundPixels > 0, "background layer has fine symbols");
Require(subjectPixels > backgroundPixels * 4, "subject blocks are denser than background symbols");
Require(boundaryPixels > 0, "colour-region seam has boundary accents");
for (int i = 0; i < source.Length; i += 4)
{
    Require(first.Subject[i + 3] == 0 || first.Boundary[i + 3] == 0,
        "large subject and boundary blocks reserve mutually exclusive cells");
    foreach (byte[] layer in layers)
        Require(layer[i] <= layer[i + 3] && layer[i + 1] <= layer[i + 3] && layer[i + 2] <= layer[i + 3],
            "raster layers contain valid premultiplied BGRA");
}
CheckColourSampling();
CheckRefreshTiming();
CheckQuietSceneDensity();

int expectedColumns = (width + first.ClusterPx - 1) / first.ClusterPx;
int expectedRows = (height + first.ClusterPx - 1) / first.ClusterPx;
Require(first.Clusters.Length == expectedColumns * expectedRows, "cluster grid covers the monitor");
Require(first.Clusters.All(cluster => cluster.StartMs is >= 0 and <= RefreshTiming.SweepMs),
    "cluster starts stay in the shared sweep window");
Require(first.Clusters.All(cluster => cluster.Width > 0 && cluster.Height > 0), "clusters have valid bounds");
var nearClick = first.Clusters.OrderBy(cluster =>
    Math.Pow(cluster.Left + cluster.Width / 2 - originX, 2)
    + Math.Pow(cluster.Top + cluster.Height / 2 - originY, 2)).First();
var farFromClick = first.Clusters.OrderByDescending(cluster =>
    Math.Pow(cluster.Left + cluster.Width / 2 - originX, 2)
    + Math.Pow(cluster.Top + cluster.Height / 2 - originY, 2)).First();
Require(nearClick.StartMs < farFromClick.StartMs, "macro front radiates from the click origin");

const int fullHdWidth = 1920, fullHdHeight = 1080;
var fullHd = new byte[fullHdWidth * fullHdHeight * 4];
for (int y = 0; y < fullHdHeight; y++)
for (int x = 0; x < fullHdWidth; x++)
{
    byte r, g, b;
    if (x < fullHdWidth / 2) (r, g, b) = ((byte)43, (byte)126, (byte)183);
    else (r, g, b) = ((byte)192, (byte)157, (byte)70);
    if (x is >= 720 and < 1190 && y is >= 230 and < 850)
        (r, g, b) = ((byte)(52 + x % 11), (byte)(138 + y % 9), (byte)(68 + (x + y) % 11));
    int i = (y * fullHdWidth + x) * 4;
    fullHd[i] = b; fullHd[i + 1] = g; fullHd[i + 2] = r; fullHd[i + 3] = 255;
}
var stopwatch = Stopwatch.StartNew();
int fullHdTile = RefreshAnalyzer.TilePixels(1);
RefreshAnalysis fullHdResult = RefreshAnalyzer.Build(fullHd, fullHdWidth, fullHdHeight, fullHdTile, seed,
    fullHdWidth / 2, fullHdHeight / 2);
stopwatch.Stop();
Require(fullHdResult.Subject.Length == fullHd.Length, "1080p subject layer dimensions");
Require(fullHdResult.Clusters.Length == 135, "1080p monitor cluster budget for the finer grid");

// Optional raw fixture export for visual review; normal verification does not create artifacts.
if (args.Length == 2 && args[0] == "--export")
{
    Directory.CreateDirectory(args[1]);
    File.WriteAllBytes(Path.Combine(args[1], "source.bgra"), fullHd);
    File.WriteAllBytes(Path.Combine(args[1], "subject.bgra"), fullHdResult.Subject);
    File.WriteAllBytes(Path.Combine(args[1], "background.bgra"), fullHdResult.Background);
    File.WriteAllBytes(Path.Combine(args[1], "boundary.bgra"), fullHdResult.Boundary);
}

Console.WriteLine($"Refresh analysis check passed: {first.Clusters.Length} small-scene clusters; " +
    $"subject={subjectPixels:N0}px, background={backgroundPixels:N0}px, boundary={boundaryPixels:N0}px; " +
    $"1080p clusters={fullHdResult.Clusters.Length}, analysis={stopwatch.ElapsedMilliseconds}ms (synthetic).");

static long CountAlpha(byte[] pixels, int width, int left, int top, int right, int bottom)
{
    long count = 0;
    for (int y = top; y < bottom; y++)
    for (int x = left; x < right; x++)
        if (pixels[(y * width + x) * 4 + 3] > 0) count++;
    return count;
}

static void Require(bool condition, string check)
{
    if (!condition) throw new InvalidOperationException($"Refresh analysis check failed: {check}.");
}

static void CheckRefreshTiming()
{
    Require(RefreshTiming.TotalMs == 800 && Math.Abs(RefreshTiming.Scale(350) - 800) < 0.001,
        "the original refresh plays in the requested 800 ms budget");
    Require(Math.Abs(RefreshTiming.SweepMs / RefreshTiming.TotalMs - 210.0 / 350) < 0.001
        && Math.Abs(RefreshTiming.CoverFadeMs / RefreshTiming.TotalMs - 95.0 / 350) < 0.001,
        "the sweep and cover keep their original proportions during slow playback");
    foreach (RefreshPixelLayer layer in Enum.GetValues<RefreshPixelLayer>())
    {
        double previousEnd = -1;
        // Include out-of-range front values to check that callers cannot exceed the budget.
        foreach (double front in new[] { -20.0, 0, 10, 70, 105, 180, 210, 500 }.Select(RefreshTiming.Scale))
        {
            RefreshEnvelope envelope = RefreshTiming.Envelope(layer, front);
            Require(envelope.StartMs >= 0 && envelope.StartMs <= envelope.PeakMs
                && envelope.PeakMs < envelope.EndMs && envelope.EndMs <= RefreshTiming.TotalMs,
                "every pixel layer has a valid rise and fade inside the total budget");
            Require(envelope.EndMs >= previousEnd, "radial batches finish in front order");
            previousEnd = envelope.EndMs;
        }
        RefreshEnvelope early = RefreshTiming.Envelope(layer, 0);
        RefreshEnvelope late = RefreshTiming.Envelope(layer, RefreshTiming.SweepMs);
        Require(early.EndMs - early.StartMs < late.EndMs - late.StartMs,
            "early pixels have shorter local lifetimes than late pixels");
        Require(early.EndMs < late.StartMs,
            "the centre is already clear while the outermost pixels arrive");
    }
    Require(RefreshTiming.SweepMs + RefreshTiming.CoverFadeMs <= RefreshTiming.TotalMs,
        "the last grey cover clears before the interaction ends");
}

static void CheckQuietSceneDensity()
{
    const int w = 512, h = 320, t = 11;
    // Both flat grey UI and a nearly neutral pastel used to leave broad unpopulated areas.
    foreach ((byte r, byte g, byte b) in new[] { ((byte)128, (byte)128, (byte)128),
        ((byte)148, (byte)155, (byte)162) })
    {
        var capture = new byte[w * h * 4];
        for (int i = 0; i < capture.Length; i += 4)
        {
            capture[i] = b; capture[i + 1] = g; capture[i + 2] = r; capture[i + 3] = 255;
        }
        RefreshAnalysis result = RefreshAnalyzer.Build(capture, w, h, t, 2731, w / 2, h / 2);
        double subjectCoverage = CountAlpha(result.Subject, w, 0, 0, w, h) / (double)(w * h);
        double fineCoverage = CountAlpha(result.Background, w, 0, 0, w, h) / (double)(w * h);
        Require(subjectCoverage >= 0.40, "quiet scenes keep substantial large-pixel coverage");
        Require(fineCoverage >= 0.055, "neutral scenes retain a visible fine-texture layer");
        Require(subjectCoverage > fineCoverage * 3,
            "fine texture stays subordinate to the large scene-coloured pixels");
        Console.WriteLine($"Quiet-scene coverage ({r},{g},{b}): subject={subjectCoverage:P1}, fine={fineCoverage:P1}.");
    }
}

static void CheckColourSampling()
{
    // A gradual hue-preserving colour field gets joined into one coherent region. Each
    // block must still sample its own footprint, rather than inherit that region's average.
    const int w = 96, h = 64, t = 11;
    var capture = new byte[w * h * 4];
    for (int y = 0; y < h; y++)
    for (int x = 0; x < w; x++)
    {
        int i = (y * w + x) * 4;
        capture[i] = (byte)(24 + x / 2);
        capture[i + 1] = 180;
        capture[i + 2] = (byte)(20 + x / 4);
        capture[i + 3] = 255;
    }
    RefreshAnalysis result = RefreshAnalyzer.Build(capture, w, h, t, 4823, 20, 20);
    long sampled = 0, overlappingFinePixels = 0;
    int fineTile = (int)Math.Round(t * 0.25);
    for (int y = 0; y < h; y++)
    for (int x = 0; x < w; x++)
    {
        int i = (y * w + x) * 4;
        if (result.Background[i + 3] > 0)
        {
            Require(x % fineTile != fineTile - 1 && y % fineTile != fineTile - 1,
                "fine background grid retains its one-physical-pixel gap");
            // The sampled green channel is deliberately much stronger than red/blue.
            Require(result.Background[i + 1] > result.Background[i] * 2
                && result.Background[i + 1] > result.Background[i + 2] * 3,
                "fine marks retain the sampled hue");
            int sx = x / fineTile * fineTile;
            int sy = y / fineTile * fineTile;
            int sourcePixel = (sy * w + sx) * 4;
            Require(Math.Abs(result.Background[i] * capture[sourcePixel + 1]
                - result.Background[i + 1] * capture[sourcePixel]) < 450,
                "fine marks use their local sample rather than a joined-region average");
            if (result.Subject[i + 3] > 0)
            {
                overlappingFinePixels++;
                Require(result.Background[i + 3] < result.Subject[i + 3],
                    "overlapping fine marks stay quieter than large blocks");
            }
            sampled++;
        }
    }
    Require(sampled > 0 && overlappingFinePixels > 0, "fixture exercises local fine marks and controlled overlap");
    CheckLargeRectangles(result.Subject, capture, w, h, t);
    RefreshAnalyzer.MakeContrastColour(48, 126, 178, out byte r, out byte g, out byte b);
    Require(r > g && g > b, "blue water generates a warm complementary accent");
    RefreshAnalyzer.MakeContrastColour(197, 163, 74, out r, out g, out b);
    Require(b > g && g > r, "yellow grass generates a cool complementary accent");
    RefreshAnalyzer.MakeContrastColour(180, 180, 180, out r, out g, out b);
    Require(r == g && g == b && r < 128, "neutral sources get neutral light-dark contrast");
}

static void CheckLargeRectangles(byte[] layer, byte[] capture, int width, int height, int tile)
{
    var visited = new bool[width * height];
    var queue = new Queue<int>();
    int blocks = 0;
    for (int start = 0; start < visited.Length; start++)
    {
        if (visited[start] || layer[start * 4 + 3] == 0) continue;
        visited[start] = true;
        queue.Enqueue(start);
        int left = width, top = height, right = 0, bottom = 0, count = 0;
        while (queue.TryDequeue(out int pixel))
        {
            int x = pixel % width, y = pixel / width;
            left = Math.Min(left, x); top = Math.Min(top, y);
            right = Math.Max(right, x + 1); bottom = Math.Max(bottom, y + 1);
            count++;
            foreach (int other in new[] { x > 0 ? pixel - 1 : -1, x + 1 < width ? pixel + 1 : -1,
                y > 0 ? pixel - width : -1, y + 1 < height ? pixel + width : -1 })
                if (other >= 0 && !visited[other] && layer[other * 4 + 3] > 0)
                {
                    visited[other] = true;
                    queue.Enqueue(other);
                }
        }
        Require(count == (right - left) * (bottom - top), "large pixels are whole rectangles, not fragment stamps");
        Require(left % tile == 0 && top % tile == 0
            && ((right + 1) % tile == 0 || right == width - 1)
            && ((bottom + 1) % tile == 0 || bottom == height - 1),
            "large pixel footprints align to cells and keep the outer one-pixel gap");
        Require(right - left <= tile * 2 && bottom - top <= tile * 2,
            "large blocks reserve at most two cells per axis");
        long blue = 0, green = 0, samples = 0;
        for (int y = top; y < bottom; y += 2)
        for (int x = left; x < right; x += 2)
        {
            int i = (y * width + x) * 4;
            blue += capture[i]; green += capture[i + 1]; samples++;
        }
        int first = (top * width + left) * 4;
        Require(Math.Abs(layer[first] * (green / samples) - layer[first + 1] * (blue / samples)) < 450,
            "large blocks retain their footprint's sampled hue instead of the whole region's hue");
        blocks++;
    }
    Require(blocks > 0, "fixture exercises whole source-coloured blocks");
}
