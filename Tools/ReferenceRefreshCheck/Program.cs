using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using Action_Wheel.Services;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static byte[] Scene(int width, int height)
{
    var pixels = new byte[width * height * 4];
    for (int y = 0; y < height; y++)
    for (int x = 0; x < width; x++)
    {
        int i = (y * width + x) * 4;
        pixels[i] = (byte)(30 + y * 170 / Math.Max(1, height - 1));
        pixels[i + 1] = (byte)(40 + x * 150 / Math.Max(1, width - 1));
        pixels[i + 2] = (byte)(((x / 16 + y / 16) % 2 == 0) ? 70 : 180);
        pixels[i + 3] = 255;
    }
    return pixels;
}

static RefreshDepthMap SyntheticDepth(int width, int height)
{
    float[] values = new float[width * height];
    for (int y = 0; y < height; y++)
    for (int x = 0; x < width; x++) values[y * width + x] = (x + y) / (float)(width + height - 2);
    return new RefreshDepthMap(width, height, values);
}

if (!args.Contains("--correctness-only"))
{
    foreach ((int width, int height) in new[] { (1920, 1080), (3840, 2160) })
    {
        // Full-resolution correction encoding, one warmed step, then three missed updates.
        byte[] largeScene = Scene(width, height);
        var clock = Stopwatch.StartNew();
        var renderer = new JianyingRefreshRenderer(width, height, largeScene, SyntheticDepth(128, 72));
        byte[] frame = new byte[largeScene.Length], bloom = new byte[4];
        double prepare = clock.Elapsed.TotalMilliseconds;
        renderer.Render(0, frame, bloom);
        clock.Restart();
        renderer.Render(34, frame, bloom);
        double stepMs = clock.Elapsed.TotalMilliseconds;
        clock.Restart();
        renderer.Render(134, frame, bloom);
        Console.WriteLine($"Reference native {width}x{height}: prepare={prepare:F2}ms, one step+encode={stepMs:F2}ms, three-step catch-up+encode={clock.Elapsed.TotalMilliseconds:F2}ms (before UI upload).");
    }
}
if (args.Contains("--performance-only")) return;

CheckTexturesAndLuts();
CheckHsl();
CheckCorrection();
CheckPlayback();
CheckLearnedDepth();
Console.WriteLine("Reference refresh checks passed: texture coordinates, shader HSL/LUT, learned depth, fixed-step history, correction and transparent ending.");

static void Near(Vector3 actual, Vector3 expected, float tolerance, string name)
{
    Require(float.IsFinite(actual.X) && float.IsFinite(actual.Y) && float.IsFinite(actual.Z)
        && Vector3.Abs(actual - expected).MaxComponent() <= tolerance,
        $"{name}: expected {expected}, got {actual}.");
}

static void CheckTexturesAndLuts()
{
    var texture = new ReferenceTexture(2, 2, [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, Vector3.One]);
    Near(texture.Sample(.5f, .5f), new Vector3(.5f), 1e-6f, "Bilinear centre");
    Near(texture.Sample(-1, -1), Vector3.UnitX, 1e-6f, "Clamped texture edge");
    Near(texture.SampleGl(.25f, .25f), Vector3.UnitZ, 1e-6f, "GL bottom-left");
    Near(texture.SampleGl(.25f, .75f), Vector3.UnitX, 1e-6f, "GL top-left");
    var spectrumLut = ReferenceEffectMath.LoadTexture("spectrum-lut.rgb.gz");
    var pixelLut = ReferenceEffectMath.LoadTexture("pixel-lut.rgb.gz");
    Require(spectrumLut.Width == 512 && spectrumLut.Height == 512
        && pixelLut.Width == 512 && pixelLut.Height == 512, "LUT dimensions changed.");
    // Independently sampled PNG pixels, recorded directly from the two local source assets.
    Near(spectrumLut.Pixels[511 * 512 + 511], new Vector3(99 / 255f), 1e-6f, "Spectrum source white");
    Near(spectrumLut.Pixels[256 * 512 + 256], new Vector3(0, 0, 57 / 255f), 1e-6f, "Spectrum source sample");
    Near(pixelLut.Pixels[511 * 512 + 511], new Vector3(158 / 255f), 1e-6f, "Pixel source white");
    Near(pixelLut.Pixels[256 * 512 + 256], new Vector3(15 / 255f), 1e-6f, "Pixel source sample");
    Vector3 spectrumGrey = ReferenceEffectMath.Lut(spectrumLut, new Vector3(128 / 255f));
    Vector3 pixelGrey = ReferenceEffectMath.Lut(pixelLut, new Vector3(128 / 255f));
    Near(spectrumGrey, new Vector3(50 / 255f), 4 / 255f, "Spectrum grey lookup");
    Near(pixelGrey, new Vector3(72 / 255f), 4 / 255f, "Pixel grey lookup");
    Console.WriteLine($"Reference LUT grey128: spectrum={spectrumGrey * 255}, pixel={pixelGrey * 255}.");
}

static void CheckHsl()
{
    var random = new Random(23112);
    float maximum = 0;
    for (int sample = 0; sample < 1500; sample++)
    {
        Vector3 rgb = new(random.NextSingle() * 4, random.NextSingle() * 4, random.NextSingle() * 4);
        float total = rgb.MaxComponent() + rgb.MinComponent();
        if (MathF.Abs(total - 2) < .02f) continue; // Avoid the reference's division-by-zero singularity.
        Vector3 expected = ShaderRgbToHsl(rgb), actual = ReferenceEffectMath.RgbToHsl(rgb);
        Near(actual, expected, 1e-4f, "Shader RGB-to-HSL including HDR");
        Vector3 expectedRgb = ShaderHslToRgb(expected);
        Vector3 actualRgb = ReferenceEffectMath.HslToRgb(expected);
        maximum = MathF.Max(maximum, Vector3.Abs(expectedRgb - actualRgb).MaxComponent());
        Near(actualRgb, expectedRgb, 1e-4f, "Shader HSL-to-RGB including HDR");
    }
    Vector3 hdr = ReferenceEffectMath.RgbToHsl(new Vector3(3f, .8f, .2f));
    Require(hdr.Z > 1 && hdr.Y < 0, "HDR HSL was clamped before the shader's actual colour arithmetic.");
    Console.WriteLine($"Shader HSL maximum sampled RGB error={maximum:E2}.");
}

static Vector3 ShaderRgbToHsl(Vector3 rgb)
{
    double r = rgb.X, g = rgb.Y, b = rgb.Z;
    double min = Math.Min(r, Math.Min(g, b)), max = Math.Max(r, Math.Max(g, b));
    double delta = max - min, luminance = (max + min) / 2;
    if (delta == 0) return new Vector3(0, 0, (float)luminance);
    double saturation = luminance < .5 ? delta / (max + min) : delta / (2 - max - min);
    double dr = (((max - r) / 6) + delta / 2) / delta;
    double dg = (((max - g) / 6) + delta / 2) / delta;
    double db = (((max - b) / 6) + delta / 2) / delta;
    double hue = r == max ? db - dg : g == max ? 1.0 / 3 + dr - db : 2.0 / 3 + dg - dr;
    if (hue < 0) hue += 1;
    else if (hue > 1) hue -= 1;
    return new Vector3((float)hue, (float)saturation, (float)luminance);
}

static Vector3 ShaderHslToRgb(Vector3 hsl)
{
    double h = hsl.X, s = hsl.Y, l = hsl.Z;
    if (s == 0) return new Vector3((float)l);
    double f2 = l < .5 ? l * (1 + s) : l + s - s * l;
    double f1 = 2 * l - f2;
    double Hue(double t)
    {
        if (t < 0) t += 1;
        else if (t > 1) t -= 1;
        if (6 * t < 1) return f1 + (f2 - f1) * 6 * t;
        if (2 * t < 1) return f2;
        if (3 * t < 2) return f1 + (f2 - f1) * (2.0 / 3 - t) * 6;
        return f1;
    }
    return new Vector3((float)Hue(h + 1.0 / 3), (float)Hue(h), (float)Hue(h - 1.0 / 3));
}

static void CheckCorrection()
{
    var random = new Random(63263);
    byte[] encoded = new byte[4];
    float maximum = 0;
    for (int sample = 0; sample < 10000; sample++)
    {
        Vector3 source = new(random.Next(256) / 255f, random.Next(256) / 255f, random.Next(256) / 255f);
        Vector3 target = sample % 20 == 0 ? source : new(random.NextSingle(), random.NextSingle(), random.NextSingle());
        JianyingRefreshRenderer.EncodeCorrection(source, target, encoded, 0);
        int alpha = encoded[3];
        Require(encoded[0] <= alpha && encoded[1] <= alpha && encoded[2] <= alpha, "Correction is not legal PBGRA.");
        Vector3 visible = new Vector3(encoded[2], encoded[1], encoded[0]) + source * (255 - alpha);
        maximum = MathF.Max(maximum, Vector3.Abs(visible - target * 255).MaxComponent());
        Require(maximum < 1.5f, "Visible correction exceeds 1.5 byte error.");
        if (target == source) Require(encoded.All(value => value == 0), "Unchanged pixels gained an overlay.");
    }
    Console.WriteLine($"Reference correction maximum sampled visible error={maximum:F3} byte.");
}

static void CheckPlayback()
{
    const int width = 160, height = 90;
    byte[] input = Scene(width, height), immutable = (byte[])input.Clone();
    var depth = SyntheticDepth(40, 24);
    float[] originalDepth = (float[])depth.Values.Clone();
    var sequential = new JianyingRefreshRenderer(width, height, input, depth);
    var jumped = new JianyingRefreshRenderer(width, height, input, depth);
    string[] forbidden = ["RefreshAnalyzer", "RefreshWavefront", "RefreshOutline", "RefreshIllumination", "RefreshRenderer"];
    foreach (FieldInfo field in typeof(JianyingRefreshRenderer).GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
        Require(!forbidden.Contains(field.FieldType.Name), "Reference renderer retained a legacy Wheel visual stage.");
    Require(sequential.DurationMs == 3000, "Reference cycle duration changed.");
    byte[] a = new byte[input.Length], b = new byte[input.Length], light = new byte[4];
    for (int step = 0; step <= 15; step++) sequential.Render(step * 1000.0 / 30 + .001, a, light);
    jumped.Render(500.001, b, light);
    Require(a.AsSpan().SequenceEqual(b), "Skipped display frames changed fixed-step history.");
    Require(light.All(value => value == 0), "Reference output still requires a second bloom image.");
    Require(input.AsSpan().SequenceEqual(immutable) && depth.Values.AsSpan().SequenceEqual(originalDepth), "Reference rendering mutated its source.");
    Require(a.Where((_, i) => i % 4 == 3).Any(alpha => alpha > 0), "Reference rendering produced no visible correction.");
    for (int i = 0; i < a.Length; i += 4)
        Require(a[i] <= a[i + 3] && a[i + 1] <= a[i + 3] && a[i + 2] <= a[i + 3], "Reference frame is not legal PBGRA.");
    Array.Fill(a, (byte)253);
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    try { sequential.Render(600, a, light, cancelled.Token); throw new InvalidOperationException("Cancelled playback continued."); }
    catch (OperationCanceledException) { }
    Require(a.All(value => value == 253), "Cancelled playback modified an output buffer.");
    sequential.Render(3000, a, light);
    Require(a.All(value => value == 0) && light.All(value => value == 0), "Reference cycle did not end transparently.");
    Console.WriteLine("Reference fixed-step/immutable/cancel/end checks passed.");
}

static void CheckLearnedDepth()
{
    byte[] scene = Scene(320, 180), immutable = (byte[])scene.Clone();
    var clock = Stopwatch.StartNew();
    RefreshDepthMap map = RefreshDepthEstimator.Estimate(scene, 320, 180);
    Require(map.Values.Length == map.Width * map.Height, "Learned depth dimensions are incomplete.");
    Require(map.Values.All(value => float.IsFinite(value) && value >= 0 && value <= 1), "Learned depth is not finite normalized data.");
    Require(map.Values.Max() - map.Values.Min() > .5f, "Learned model returned constant depth.");
    Require(scene.AsSpan().SequenceEqual(immutable), "Learned model mutated capture bytes.");
    Console.WriteLine($"Independent learned depth: {map.Width}x{map.Height}, range={map.Values.Min():F3}..{map.Values.Max():F3}, {clock.Elapsed.TotalMilliseconds:F2}ms including session setup.");
}

internal static class VectorComponents
{
    public static float MaxComponent(this Vector3 v) => MathF.Max(v.X, MathF.Max(v.Y, v.Z));
    public static float MinComponent(this Vector3 v) => MathF.Min(v.X, MathF.Min(v.Y, v.Z));
}
