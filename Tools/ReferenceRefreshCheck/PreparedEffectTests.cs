using System.Numerics;
using Action_Wheel.Services;

internal static class PreparedEffectTests
{
    public static void Run()
    {
        CheckPreparedSpectrum();
        CheckGaussianPlans();
        Console.WriteLine("Prepared reference checks passed: static spectrum data, mutable fallback, replaced inputs and Gaussian sampling plans.");
    }

    private static void CheckPreparedSpectrum()
    {
        ReferenceTexture atlas = ReferenceEffectMath.LoadTexture("atlas.rgb.gz");
        ReferenceTexture colourBar = ReferenceEffectMath.LoadTexture("colorbar.rgb.gz");
        ReferenceTexture lut = ReferenceEffectMath.LoadTexture("spectrum-lut.rgb.gz");
        foreach ((int width, int height) in new[] { (127, 73), (73, 127) })
        {
            var source = Scene(width, height);
            var depth = new float[width * height];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                depth[y * width + x] = (x + y) / (float)(width + height - 2);
            Vector3[] original = (Vector3[])source.Pixels.Clone();
            float[] originalDepth = (float[])depth.Clone();
            var prepared = new RefreshSpectrumEffect(width, height, atlas, colourBar, lut);
            var dynamic = new RefreshSpectrumEffect(width, height, atlas, colourBar, lut);
            var actual = new ReferenceTexture(width, height);
            var expected = new ReferenceTexture(width, height);
            prepared.Prepare(source, depth);
            foreach (double seconds in new[] { .15, .7, 1.2, 2.4, .4 })
            {
                prepared.Render(source, depth, seconds, 1, actual);
                dynamic.Render(source, depth, seconds, 1, expected);
                Near(actual.Pixels, expected.Pixels, "Prepared spectrum");
            }

            // Replacing either reference must use the mutable path, rather than the prepared
            // geometry/colours for a previous source. The explicitly prepared inputs stay fixed.
            ReferenceTexture replacement = Scene(width, height, 7);
            prepared.Render(replacement, depth, .8, 1, actual);
            dynamic.Render(replacement, depth, .8, 1, expected);
            Near(actual.Pixels, expected.Pixels, "Changed source fallback");
            float[] replacementDepth = depth.Select(value => 1 - value).ToArray();
            prepared.Render(source, replacementDepth, .8, 1, actual);
            dynamic.Render(source, replacementDepth, .8, 1, expected);
            Near(actual.Pixels, expected.Pixels, "Changed depth fallback");

            prepared.Prepare(replacement, replacementDepth);
            prepared.Render(replacement, replacementDepth, .8, 1, actual);
            dynamic.Render(replacement, replacementDepth, .8, 1, expected);
            Near(actual.Pixels, expected.Pixels, "Replaced preparation");
            // Dynamic callers are still allowed to alter the same texture/depth arrays.
            replacement.Pixels[width + 2] = Vector3.One;
            replacementDepth[width + 2] = .91f;
            var fresh = new RefreshSpectrumEffect(width, height, atlas, colourBar, lut);
            dynamic.Render(replacement, replacementDepth, .8, 1, actual);
            fresh.Render(replacement, replacementDepth, .8, 1, expected);
            Near(actual.Pixels, expected.Pixels, "Mutable input after render");
            if (!source.Pixels.SequenceEqual(original) || !depth.SequenceEqual(originalDepth))
                throw new InvalidOperationException("Spectrum preparation changed its inputs.");
        }
    }

    private static void CheckGaussianPlans()
    {
        var target = new ReferenceTexture(13, 7);
        var expected = new ReferenceTexture(13, 7);
        foreach ((int width, int height) in new[] { (7, 5), (17, 11), (1, 1), (7, 5) })
        {
            ReferenceTexture source = Scene(width, height);
            Vector3[] original = (Vector3[])source.Pixels.Clone();
            foreach (float[] weights in new[]
            {
                new[] { .208046f, .203926f, .192051f },
                new[] { 1f, .980198f, .923116f, .835270f, .726149f },
                new[] { 1f, .980198f, .923116f, .835270f, .726149f, .606530f },
            })
            foreach (float spacing in new[] { 1f, .75f, 5.25f, 1f })
            foreach (bool horizontal in new[] { true, false })
            {
                ReferenceEffectMath.Gaussian(source, target, spacing, horizontal, weights.Length - 1, weights);
                LiteralGaussian(source, expected, spacing, horizontal, weights);
                Near(target.Pixels, expected.Pixels, "Gaussian cached map");
                // Maps may be reused with new weights, but normalization/weights must be live.
                float saved = weights[0];
                weights[0] *= 1.25f;
                ReferenceEffectMath.Gaussian(source, target, spacing, horizontal, weights.Length - 1, weights);
                LiteralGaussian(source, expected, spacing, horizontal, weights);
                Near(target.Pixels, expected.Pixels, "Gaussian changed weights");
                weights[0] = saved;
            }
            if (!source.Pixels.SequenceEqual(original))
                throw new InvalidOperationException("Gaussian changed its source.");
        }
    }

    // Direct transcription of the original sampling loop serves as an independent map oracle.
    private static void LiteralGaussian(ReferenceTexture source, ReferenceTexture target,
        float spacing, bool horizontal, float[] weights)
    {
        int radius = weights.Length - 1;
        float total = weights[0];
        for (int tap = 1; tap <= radius; tap++) total += 2 * weights[tap];
        float inverse = 1 / total;
        float du = horizontal ? spacing / source.Width : 0;
        float dv = horizontal ? 0 : spacing / source.Height;
        for (int y = 0; y < target.Height; y++)
        for (int x = 0; x < target.Width; x++)
        {
            float u = (x + .5f) / target.Width, v = (y + .5f) / target.Height;
            Vector3 value = source.Sample(u, v) * weights[0];
            for (int tap = 1; tap <= radius; tap++)
                value += (source.Sample(u - tap * du, v - tap * dv)
                    + source.Sample(u + tap * du, v + tap * dv)) * weights[tap];
            target.Pixels[y * target.Width + x] = ReferenceEffectMath.Clamp(value * inverse);
        }
    }

    private static ReferenceTexture Scene(int width, int height, int seed = 0)
    {
        var texture = new ReferenceTexture(width, height);
        var random = new Random(713 + seed);
        for (int p = 0; p < texture.Pixels.Length; p++)
            texture.Pixels[p] = new Vector3((float)random.NextDouble(),
                (float)random.NextDouble(), (float)random.NextDouble());
        return texture;
    }

    private static void Near(Vector3[] actual, Vector3[] expected, string label)
    {
        for (int pixel = 0; pixel < actual.Length; pixel++)
        {
            Vector3 delta = Vector3.Abs(actual[pixel] - expected[pixel]);
            float maximum = Math.Max(delta.X, Math.Max(delta.Y, delta.Z));
            if (!float.IsFinite(maximum) || maximum > 1e-6f)
                throw new InvalidOperationException($"{label} mismatch at pixel {pixel}: {maximum}.");
        }
    }
}
