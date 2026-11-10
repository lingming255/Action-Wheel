using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Action_Wheel.Services
{
    /// <summary>
    /// Bounded worker renderer for whole-particle radial pulses. Source layers remain immutable;
    /// the caller supplies reusable output buffers and must not render one instance concurrently.
    /// </summary>
    internal sealed class RefreshRenderer : IRefreshRenderer
    {
        private readonly int _width, _bloomStep;
        private readonly byte[] _grey;
        private readonly bool _opaqueGrey;
        private readonly byte[][] _outlined;
        private readonly ParticleState[][] _particles;
        private readonly CoverTile[] _cover;
        private readonly RefreshIllumination? _illumination;
        private readonly RefreshLightComposite? _lightComposite;
        private readonly uint[]? _coreLight;
        private readonly double[]? _sceneStarts;
        private readonly float[] _emission, _horizontal, _near, _far, _verticalSums;
        private static readonly int[] LayerOrder = { 0, 2, 1 };
        public int BloomWidth { get; }
        public int BloomHeight { get; }
        public double DurationMs => RefreshTiming.TotalMs;

        private readonly record struct CoverTile(int Left, int Top, int Width, int Height, double StartMs);
        private readonly record struct LightSample(int Index, float Blue, float Green, float Red);
        private sealed record ParticleState(RefreshParticle Particle, int LayerIndex,
            RefreshEnvelope Envelope, LightSample[] Light);

        public RefreshRenderer(int width, int height, int tile, double scale, int originX, int originY,
            byte[] grey, byte[][] originalLayers, byte[][] outlinedLayers, RefreshParticle[] particles,
            RefreshWavefront? wavefront = null, byte[]? scene = null)
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
            _opaqueGrey = true;
            for (int i = 3; i < grey.Length; i += 4)
                if (grey[i] != 255) { _opaqueGrey = false; break; }
            _bloomStep = Math.Max(2, (int)Math.Round(4 * scale));
            BloomWidth = (width + _bloomStep - 1) / _bloomStep;
            BloomHeight = (height + _bloomStep - 1) / _bloomStep;
            int lightLength = checked(BloomWidth * BloomHeight * 3);
            _emission = new float[lightLength]; _horizontal = new float[lightLength];
            _near = new float[lightLength]; _far = new float[lightLength];
            _verticalSums = new float[BloomWidth * 3];
            if (scene != null)
            {
                _lightComposite = new RefreshLightComposite(width, height, scene);
                _coreLight = new uint[checked(width * height)];
                _illumination = new RefreshIllumination(width, height, _bloomStep, originX, originY, scene);
                _sceneStarts = new double[BloomWidth * BloomHeight];
                for (int cy = 0; cy < BloomHeight; cy++)
                for (int cx = 0; cx < BloomWidth; cx++)
                {
                    // Use the containing cover tile's arrival so emission starts with the
                    // desktop reveal. The subsequent blur can spill across its boundary.
                    int x = Math.Min(width - 1, cx * _bloomStep + _bloomStep / 2);
                    int y = Math.Min(height - 1, cy * _bloomStep + _bloomStep / 2);
                    int left = x / tile * tile, top = y / tile * tile;
                    double centreX = left + Math.Min(tile, width - left) * 0.5;
                    double centreY = top + Math.Min(tile, height - top) * 0.5;
                    _sceneStarts[cy * BloomWidth + cx] = wavefront?.FrontAt(centreX, centreY)
                        ?? RefreshTiming.FrontAt(centreX, centreY, width, height, originX, originY);
                }
            }
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

        public void Render(double elapsedMs, byte[] fullBuffer, byte[] bloomBuffer,
            System.Threading.CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            if (fullBuffer.Length != _grey.Length || bloomBuffer.Length != BloomWidth * BloomHeight * 4)
                throw new ArgumentException("Output buffer dimensions do not match the renderer.");
            fullBuffer.AsSpan().Clear(); bloomBuffer.AsSpan().Clear();
            if (elapsedMs >= RefreshTiming.TotalMs || !double.IsFinite(elapsedMs)) return;
            Span<uint> output = MemoryMarshal.Cast<byte, uint>(fullBuffer.AsSpan());
            ReadOnlySpan<uint> grey = MemoryMarshal.Cast<byte, uint>(_grey);
            float sceneStrength = _illumination == null ? 0 : RefreshIllumination.StrengthAt(elapsedMs);
            int lightOpacity = (int)MathF.Round(sceneStrength * 256);
            ReadOnlySpan<uint> illumination = _illumination == null
                ? ReadOnlySpan<uint>.Empty : MemoryMarshal.Cast<byte, uint>(_illumination.Overlay);
            foreach (CoverTile tile in _cover)
            {
                double fade = Math.Clamp((elapsedMs - tile.StartMs) / RefreshTiming.CoverFadeMs, 0, 1);
                int opacity = (int)Math.Round((1 - fade) * 256);
                if (opacity == 0 && lightOpacity == 0) continue;
                for (int y = tile.Top; y < tile.Top + tile.Height; y++)
                {
                    int first = y * _width + tile.Left, last = first + tile.Width;
                    if (opacity == 256 && _opaqueGrey)
                        grey.Slice(first, tile.Width).CopyTo(output.Slice(first, tile.Width));
                    else if (opacity == 0)
                    {
                        // Revealed tiles only need the correction; avoid reading and scaling
                        // the absent grey cover or performing a redundant source-over blend.
                        for (int pixel = first; pixel < last; pixel++)
                            output[pixel] = Scale(illumination[pixel], lightOpacity);
                    }
                    else if (lightOpacity != 0)
                    {
                        for (int pixel = first; pixel < last; pixel++)
                        {
                            // This is a scene-colour correction with a derived alpha, not a
                            // white radial sprite. Keep it below the cover and the particles.
                            uint light = Scale(illumination[pixel], lightOpacity);
                            uint cover = Scale(grey[pixel], opacity);
                            int a = (int)(cover >> 24);
                            output[pixel] = cover + Scale(light, 256 - a - (a >> 7));
                        }
                    }
                    else if (opacity == 256) grey.Slice(first, tile.Width).CopyTo(output.Slice(first, tile.Width));
                    else for (int pixel = first; pixel < last; pixel++) output[pixel] = Scale(grey[pixel], opacity);
                }
            }
            // Dim the complete background (including unrevealed grey), then put light on top.
            // A black source-over layer changes any live desktop without freezing its pixels.
            int dimOpacity = (int)MathF.Round(sceneStrength * 0.20f * 256);
            if (dimOpacity > 0 && _lightComposite != null)
                RefreshLightComposite.DimFrame(fullBuffer, dimOpacity);
            _emission.AsSpan().Clear();
            if (_coreLight != null) _coreLight.AsSpan().Clear();
            bool anyLight = false;
            if (sceneStrength > 0 && _illumination != null && _sceneStarts != null)
            {
                float[] sceneLight = _illumination.Emission;
                for (int cell = 0, channel = 0; cell < _sceneStarts.Length; cell++, channel += 3)
                {
                    float reveal = (float)Math.Clamp((elapsedMs - _sceneStarts[cell])
                        / RefreshTiming.CoverFadeMs, 0, 1);
                    float strength = sceneStrength * reveal;
                    _emission[channel] = sceneLight[channel] * strength;
                    _emission[channel + 1] = sceneLight[channel + 1] * strength;
                    _emission[channel + 2] = sceneLight[channel + 2] * strength;
                    anyLight |= _emission[channel] > 0 || _emission[channel + 1] > 0
                        || _emission[channel + 2] > 0;
                }
            }
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
                    if (_lightComposite != null) anyLight = true;
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
                            if (_lightComposite != null)
                            {
                                // The reference effect adds a bright core to the background;
                                // translucent pigment alone looks painted on rather than lit.
                                // Accumulate core energy first. One final pass combines it
                                // with the glow, avoiding repeated scene/alpha reconstruction.
                                uint previous = _coreLight![pixel];
                                int b = Math.Min(255, (int)(previous & 255) + (int)(front & 255) * 4);
                                int g = Math.Min(255, (int)((previous >> 8) & 255) + (int)((front >> 8) & 255) * 4);
                                int r = Math.Min(255, (int)((previous >> 16) & 255) + (int)((front >> 16) & 255) * 4);
                                _coreLight[pixel] = (uint)(b | (g << 8) | (r << 16));
                            }
                            else
                            {
                                int a = (int)(front >> 24);
                                output[pixel] = front + Scale(output[pixel], 256 - a - (a >> 7));
                            }
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
            Blur(_emission, _far, _lightComposite == null ? 2 : 4, 3);
            for (int cell = 0, pixel = 0; cell < _near.Length; cell += 3, pixel += 4)
            {
                _near[cell] = _near[cell] * 0.8f + _far[cell] * 0.55f;
                _near[cell + 1] = _near[cell + 1] * 0.8f + _far[cell + 1] * 0.55f;
                _near[cell + 2] = _near[cell + 2] * 0.8f + _far[cell + 2] * 0.55f;
                byte b = Channel(_near[cell]);
                byte g = Channel(_near[cell + 1]);
                byte r = Channel(_near[cell + 2]);
                bloomBuffer[pixel] = b; bloomBuffer[pixel + 1] = g; bloomBuffer[pixel + 2] = r;
                bloomBuffer[pixel + 3] = Math.Max(r, Math.Max(g, b));
            }
            // The small buffer remains useful for checks; the UI displays only the completed
            // full frame, with its light added here instead of alpha-blending a coloured fog.
            _lightComposite?.AddBloom(fullBuffer, _near, BloomWidth, BloomHeight, _bloomStep, _coreLight);
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
                if (_lightComposite != null)
                {
                    // Every active coloured particle emits, including darker source colours.
                    // The reference has independent core x4 and blur x2 controls, no bright-pass.
                    float lightGain = 2f * normalization;
                    result.Add(new LightSample((cy * BloomWidth + cx) * 3,
                        sb * lightGain, sg * lightGain, sr * lightGain));
                    continue;
                }
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
