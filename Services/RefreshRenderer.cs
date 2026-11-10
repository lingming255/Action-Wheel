using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Action_Wheel.Services
{
    /// <summary>
    /// Bounded worker renderer for whole-particle radial pulses. Source layers remain immutable;
    /// the caller supplies reusable output buffers and must not render one instance concurrently.
    /// </summary>
    internal sealed class RefreshRenderer
    {
        private readonly int _width, _bloomStep;
        private readonly byte[] _grey;
        private readonly byte[][] _outlined;
        private readonly ParticleState[][] _particles;
        private readonly CoverTile[] _cover;
        private readonly float[] _emission, _horizontal, _near, _far, _verticalSums;
        private static readonly int[] LayerOrder = { 0, 2, 1 };
        public int BloomWidth { get; }
        public int BloomHeight { get; }

        private readonly record struct CoverTile(int Left, int Top, int Width, int Height, double StartMs);
        private readonly record struct LightSample(int Index, float Blue, float Green, float Red);
        private sealed record ParticleState(RefreshParticle Particle, int LayerIndex,
            RefreshEnvelope Envelope, LightSample[] Light);

        public RefreshRenderer(int width, int height, int tile, double scale, int originX, int originY,
            byte[] grey, byte[][] originalLayers, byte[][] outlinedLayers, RefreshParticle[] particles,
            RefreshWavefront? wavefront = null)
        {
            if (width <= 0 || height <= 0 || tile <= 0 || !double.IsFinite(scale) || scale <= 0)
                throw new ArgumentOutOfRangeException(nameof(width));
            int bytes = checked(width * height * 4);
            if (grey.Length != bytes || originalLayers.Length != 3 || outlinedLayers.Length != 3)
                throw new ArgumentException("Renderer requires grey plus subject/background/boundary layers.");
            for (int i = 0; i < 3; i++)
                if (originalLayers[i].Length != bytes || outlinedLayers[i].Length != bytes)
                    throw new ArgumentException("Layer dimensions must match the renderer.");
            _width = width;
            _grey = grey; _outlined = outlinedLayers;
            _bloomStep = Math.Max(2, (int)Math.Round(4 * scale));
            BloomWidth = (width + _bloomStep - 1) / _bloomStep;
            BloomHeight = (height + _bloomStep - 1) / _bloomStep;
            int lightLength = checked(BloomWidth * BloomHeight * 3);
            _emission = new float[lightLength]; _horizontal = new float[lightLength];
            _near = new float[lightLength]; _far = new float[lightLength];
            _verticalSums = new float[BloomWidth * 3];
            var cover = new List<CoverTile>();
            for (int y = 0; y < height; y += tile)
            for (int x = 0; x < width; x += tile)
            {
                int w = Math.Min(tile, width - x), h = Math.Min(tile, height - y);
                double front = wavefront?.FrontAt(x + w * 0.5, y + h * 0.5)
                    ?? RefreshTiming.FrontAt(x + w * 0.5, y + h * 0.5, width, height, originX, originY);
                cover.Add(new CoverTile(x, y, w, h, front));
            }
            _cover = cover.ToArray();
            var grouped = new List<ParticleState>[] { new(), new(), new() };
            for (int i = 0; i < particles.Length; i++)
            {
                RefreshParticle p = particles[i];
                if (p.Left < 0 || p.Top < 0 || p.Width <= 0 || p.Height <= 0
                    || (long)p.Left + p.Width > width || (long)p.Top + p.Height > height)
                    throw new ArgumentException("Particle rectangle must be inside the source.");
                int index = p.Layer switch
                {
                    RefreshPixelLayer.Subject => 0,
                    RefreshPixelLayer.Background => 1,
                    RefreshPixelLayer.Boundary => 2,
                    _ => throw new ArgumentException("Unknown particle layer."),
                };
                grouped[index].Add(new ParticleState(p, index, RefreshTiming.Envelope(p.Layer, p.StartMs),
                    BuildLight(originalLayers[index], p)));
            }
            _particles = new ParticleState[3][];
            for (int layer = 0; layer < 3; layer++)
            {
                _particles[layer] = grouped[layer].ToArray();
            }
        }

        public void Render(double elapsedMs, byte[] fullBuffer, byte[] bloomBuffer)
        {
            if (fullBuffer.Length != _grey.Length || bloomBuffer.Length != BloomWidth * BloomHeight * 4)
                throw new ArgumentException("Output buffer dimensions do not match the renderer.");
            fullBuffer.AsSpan().Clear(); bloomBuffer.AsSpan().Clear();
            if (elapsedMs >= RefreshTiming.TotalMs || !double.IsFinite(elapsedMs)) return;
            Span<uint> output = MemoryMarshal.Cast<byte, uint>(fullBuffer.AsSpan());
            ReadOnlySpan<uint> grey = MemoryMarshal.Cast<byte, uint>(_grey);
            foreach (CoverTile tile in _cover)
            {
                double fade = Math.Clamp((elapsedMs - tile.StartMs) / RefreshTiming.CoverFadeMs, 0, 1);
                int opacity = (int)Math.Round((1 - fade) * 256);
                if (opacity == 0) continue;
                for (int y = tile.Top; y < tile.Top + tile.Height; y++)
                {
                    int first = y * _width + tile.Left, last = first + tile.Width;
                    if (opacity == 256) grey.Slice(first, tile.Width).CopyTo(output.Slice(first, tile.Width));
                    else for (int pixel = first; pixel < last; pixel++) output[pixel] = Scale(grey[pixel], opacity);
                }
            }
            _emission.AsSpan().Clear();
            bool anyLight = false;
            // Match the existing visual stacking: subject, boundary, then quieter fine background.
            foreach (int layerIndex in LayerOrder)
            {
                ParticleState[] group = _particles[layerIndex];
                // Raster order is deliberate: radial sorting makes buffer writes random and
                // is substantially slower on 4K frames despite visiting fewer metadata entries.
                for (int particleIndex = 0; particleIndex < group.Length; particleIndex++)
                {
                    ParticleState state = group[particleIndex];
                    float alpha = Opacity(state.Envelope, elapsedMs);
                    if (layerIndex == 1) alpha *= 0.9f;
                    int opacity = (int)MathF.Round(alpha * 256);
                    if (opacity == 0) continue;
                    RefreshParticle p = state.Particle;
                    ReadOnlySpan<uint> source = MemoryMarshal.Cast<byte, uint>(_outlined[state.LayerIndex]);
                    for (int y = p.Top; y < p.Top + p.Height; y++)
                    {
                        int first = y * _width + p.Left, last = first + p.Width;
                        for (int pixel = first; pixel < last; pixel++)
                        {
                            uint colour = source[pixel];
                            if ((colour >> 24) == 0) continue;
                            uint front = Scale(colour, opacity);
                            int a = (int)(front >> 24);
                            output[pixel] = front + Scale(output[pixel], 256 - a - (a >> 7));
                        }
                    }
                    foreach (LightSample sample in state.Light)
                    {
                        _emission[sample.Index] += sample.Blue * alpha;
                        _emission[sample.Index + 1] += sample.Green * alpha;
                        _emission[sample.Index + 2] += sample.Red * alpha;
                        anyLight = true;
                    }
                }
            }
            if (!anyLight) return;
            Blur(_emission, _near, 1, 2);
            Blur(_emission, _far, 2, 3);
            for (int cell = 0, pixel = 0; cell < _near.Length; cell += 3, pixel += 4)
            {
                byte b = Channel(_near[cell] * 0.8f + _far[cell] * 0.55f);
                byte g = Channel(_near[cell + 1] * 0.8f + _far[cell + 1] * 0.55f);
                byte r = Channel(_near[cell + 2] * 0.8f + _far[cell + 2] * 0.55f);
                bloomBuffer[pixel] = b; bloomBuffer[pixel + 1] = g; bloomBuffer[pixel + 2] = r;
                bloomBuffer[pixel + 3] = Math.Max(r, Math.Max(g, b));
            }
        }

        private LightSample[] BuildLight(byte[] source, RefreshParticle p)
        {
            var result = new List<LightSample>();
            float normalization = 1f / (_bloomStep * _bloomStep * 255);
            int x0 = p.Left / _bloomStep, y0 = p.Top / _bloomStep;
            int x1 = (p.Left + p.Width + _bloomStep - 1) / _bloomStep;
            int y1 = (p.Top + p.Height + _bloomStep - 1) / _bloomStep;
            for (int cy = y0; cy < y1; cy++)
            for (int cx = x0; cx < x1; cx++)
            {
                int sb = 0, sg = 0, sr = 0, sa = 0;
                int left = Math.Max(p.Left, cx * _bloomStep), top = Math.Max(p.Top, cy * _bloomStep);
                int right = Math.Min(p.Left + p.Width, (cx + 1) * _bloomStep);
                int bottom = Math.Min(p.Top + p.Height, (cy + 1) * _bloomStep);
                for (int y = top; y < bottom; y++)
                for (int x = left; x < right; x++)
                {
                    int i = (y * _width + x) * 4;
                    sb += source[i]; sg += source[i + 1]; sr += source[i + 2]; sa += source[i + 3];
                }
                if (sa == 0) continue;
                float b = sb / (float)sa, g = sg / (float)sa, r = sr / (float)sa;
                float brightness = 0.65f * Math.Max(r, Math.Max(g, b))
                    + 0.35f * (r * 0.2126f + g * 0.7152f + b * 0.0722f);
                float knee = Math.Clamp(brightness - 0.5f + 0.18f, 0, 0.36f);
                float contribution = Math.Max(brightness - 0.5f, knee * knee / 0.72f);
                if (contribution <= 0) continue;
                float gain = contribution / Math.Max(brightness, 0.0001f) * 1.6f * normalization;
                result.Add(new LightSample((cy * BloomWidth + cx) * 3, sb * gain, sg * gain, sr * gain));
            }
            return result.ToArray();
        }

        private void Blur(float[] source, float[] result, int radius, int passes)
        {
            float inverse = 1f / (radius * 2 + 1);
            for (int pass = 0; pass < passes; pass++)
            {
                float[] input = pass == 0 ? source : result;
                for (int y = 0; y < BloomHeight; y++)
                {
                    float b = 0, g = 0, r = 0;
                    for (int x = 0; x <= radius && x < BloomWidth; x++)
                    {
                        int i = (y * BloomWidth + x) * 3;
                        b += input[i]; g += input[i + 1]; r += input[i + 2];
                    }
                    for (int x = 0; x < BloomWidth; x++)
                    {
                        int i = (y * BloomWidth + x) * 3;
                        _horizontal[i] = b * inverse; _horizontal[i + 1] = g * inverse;
                        _horizontal[i + 2] = r * inverse;
                        int remove = x - radius, add = x + radius + 1;
                        if (remove >= 0)
                        {
                            int k = (y * BloomWidth + remove) * 3;
                            b -= input[k]; g -= input[k + 1]; r -= input[k + 2];
                        }
                        if (add < BloomWidth)
                        {
                            int k = (y * BloomWidth + add) * 3;
                            b += input[k]; g += input[k + 1]; r += input[k + 2];
                        }
                    }
                }
                // Traverse rows contiguously. A column-at-a-time vertical pass jumps through
                // several megabytes at 4K and defeats the CPU cache for every blur iteration.
                _verticalSums.AsSpan().Clear();
                int rowLength = BloomWidth * 3;
                for (int y = 0; y <= radius && y < BloomHeight; y++)
                {
                    int row = y * rowLength;
                    for (int channel = 0; channel < rowLength; channel++)
                        _verticalSums[channel] += _horizontal[row + channel];
                }
                for (int y = 0; y < BloomHeight; y++)
                {
                    int row = y * rowLength;
                    for (int channel = 0; channel < rowLength; channel++)
                        result[row + channel] = _verticalSums[channel] * inverse;
                    int remove = y - radius, add = y + radius + 1;
                    if (remove >= 0 && add < BloomHeight)
                    {
                        int removeRow = remove * rowLength, addRow = add * rowLength;
                        for (int channel = 0; channel < rowLength; channel++)
                            _verticalSums[channel] += _horizontal[addRow + channel] - _horizontal[removeRow + channel];
                    }
                    else if (remove >= 0)
                    {
                        int removeRow = remove * rowLength;
                        for (int channel = 0; channel < rowLength; channel++)
                            _verticalSums[channel] -= _horizontal[removeRow + channel];
                    }
                    else if (add < BloomHeight)
                    {
                        int addRow = add * rowLength;
                        for (int channel = 0; channel < rowLength; channel++)
                            _verticalSums[channel] += _horizontal[addRow + channel];
                    }
                }
            }
        }

        private static float Opacity(RefreshEnvelope envelope, double time)
        {
            if (time <= envelope.StartMs || time >= envelope.EndMs) return 0;
            if (time <= envelope.PeakMs)
            {
                double t = (time - envelope.StartMs) / (envelope.PeakMs - envelope.StartMs);
                return (float)(1 - (1 - t) * (1 - t));
            }
            double fade = (time - envelope.PeakMs) / (envelope.EndMs - envelope.PeakMs);
            return (float)(1 - fade * fade);
        }

        private static uint Scale(uint pixel, int opacity)
            => (((pixel & 0x00FF00FFu) * (uint)opacity >> 8) & 0x00FF00FFu)
                | ((((pixel >> 8) & 0x00FF00FFu) * (uint)opacity) & 0xFF00FF00u);

        private static byte Channel(float value) => (byte)Math.Clamp(MathF.Round(value * 255), 0, 255);
    }
}
