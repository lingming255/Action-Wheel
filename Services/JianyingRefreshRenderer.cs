using System;
using System.Numerics;
using System.Threading.Tasks;

namespace Action_Wheel.Services
{
    /// <summary>
    /// Reference effect stack: spectrum/ASCII scan, then square-pixel scan. Depth is supplied by
    /// a separate learned model. No legacy Wheel particle, outline, exposure or radial-front pass.
    /// One reference update is 1/30 second; missed display frames still advance the whole stack.
    /// </summary>
    internal sealed class JianyingRefreshRenderer : IRefreshRenderer
    {
        private const double ReferenceHz = 30;
        public double DurationMs => 3000;
        public double FrameIntervalMs => 1000.0 / ReferenceHz;
        public int BloomWidth => 1;
        public int BloomHeight => 1;
        private readonly int _width, _height;
        private readonly byte[] _capture;
        private readonly ReferenceTexture _source, _spectrumFrame, _final;
        private readonly float[] _depth;
        private readonly RefreshSpectrumEffect _spectrum;
        private readonly RefreshPixelScanEffect _pixels;
        private readonly RefreshOutputEncoder _encoder;
        private int _lastStep = -1;

        public JianyingRefreshRenderer(int width, int height, byte[] capture, RefreshDepthMap depth)
        {
            if (capture.Length != checked(width * height * 4)) throw new ArgumentException("Capture size mismatch.");
            _width = width; _height = height; _capture = capture;
            // Bound the CPU shader work; keep the reference equations and reconstruct their
            // correction above the original full capture. This resolution adaptation is explicit
            // and differs from the source engine's nominal 720p render targets.
            double scale = Math.Min(1, 1280.0 / Math.Max(width, height));
            int workWidth = Math.Max(2, (int)Math.Round(width * scale));
            int workHeight = Math.Max(2, (int)Math.Round(height * scale));
            _source = new ReferenceTexture(workWidth, workHeight);
            _spectrumFrame = new ReferenceTexture(workWidth, workHeight);
            _final = new ReferenceTexture(workWidth, workHeight);
            _depth = new float[checked(workWidth * workHeight)];
            Parallel.For(0, workHeight, new ParallelOptions { MaxDegreeOfParallelism = 4 }, y =>
            {
                float v = (y + .5f) / workHeight;
                for (int x = 0; x < workWidth; x++)
                {
                    float u = (x + .5f) / workWidth;
                    _source.Pixels[y * workWidth + x] = SampleCapture(u, v);
                    _depth[y * workWidth + x] = SampleDepth(depth, u, v);
                }
            });
            _spectrum = new RefreshSpectrumEffect(workWidth, workHeight,
                ReferenceEffectMath.LoadTexture("atlas.rgb.gz"),
                ReferenceEffectMath.LoadTexture("colorbar.rgb.gz"),
                ReferenceEffectMath.LoadTexture("spectrum-lut.rgb.gz"));
            _spectrum.Prepare(_source, _depth);
            _pixels = new RefreshPixelScanEffect(workWidth, workHeight,
                ReferenceEffectMath.LoadTexture("pixel-lut.rgb.gz"));
            _encoder = new RefreshOutputEncoder(width, height, capture, _source);
        }

        public void Render(double elapsedMs, byte[] fullBuffer, byte[] bloomBuffer,
            System.Threading.CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            if (fullBuffer.Length != _capture.Length || bloomBuffer.Length != 4)
                throw new ArgumentException("Playback buffers do not match reference renderer.");
            bloomBuffer.AsSpan().Clear();
            if (!double.IsFinite(elapsedMs) || elapsedMs < 0 || elapsedMs >= DurationMs)
            {
                fullBuffer.AsSpan().Clear();
                return;
            }
            int step = (int)Math.Floor(elapsedMs * ReferenceHz / 1000);
            if (step < _lastStep) { _pixels.Reset(); _lastStep = -1; }
            for (int next = _lastStep + 1; next <= step; next++)
            {
                cancellation.ThrowIfCancellationRequested();
                double seconds = next / ReferenceHz;
                float backgroundFade = seconds < 2.4 ? 1 : (float)((3 - seconds) / .6);
                _spectrum.Render(_source, _depth, seconds, backgroundFade, _spectrumFrame);
                cancellation.ThrowIfCancellationRequested();
                _pixels.AdvanceHistory(_spectrumFrame, _depth, seconds);
                _lastStep = next;
            }
            cancellation.ThrowIfCancellationRequested();
            double time = step / ReferenceHz;
            // B's Lua keeps filter_alpha constant; its depth mask restores the background.
            _pixels.Render(_spectrumFrame, _depth, time, 1, _final);
            // Static maps and SIMD encode the native-resolution correction without repeating
            // two full-screen floating-point texture samples and three divisions per pixel.
            _encoder.Encode(_final, fullBuffer, cancellation);
        }

        internal static void EncodeCorrection(Vector3 source, Vector3 target, byte[] output, int offset)
        {
            float alpha = Math.Max(Coverage(source.X, target.X),
                Math.Max(Coverage(source.Y, target.Y), Coverage(source.Z, target.Z)));
            int a = Math.Clamp((int)MathF.Ceiling(alpha * 255), 0, 255);
            float remaining = 1 - a / 255f;
            output[offset] = Channel(target.Z - remaining * source.Z, a);
            output[offset + 1] = Channel(target.Y - remaining * source.Y, a);
            output[offset + 2] = Channel(target.X - remaining * source.X, a);
            output[offset + 3] = (byte)a;
        }
        private static float Coverage(float source, float target)
            => target >= source ? (source < 1 ? (target - source) / (1 - source) : 0)
                : (source > 0 ? (source - target) / source : 0);
        private static byte Channel(float value, int alpha)
            => (byte)Math.Clamp((int)MathF.Round(value * 255), 0, alpha);
        private Vector3 SampleCapture(float u, float v)
        {
            float x = Math.Clamp(u * _width - .5f, 0, _width - 1), y = Math.Clamp(v * _height - .5f, 0, _height - 1);
            int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, _width - 1), y1 = Math.Min(y0 + 1, _height - 1);
            Vector3 Read(int px, int py)
            {
                int p = (py * _width + px) * 4;
                return new Vector3(_capture[p + 2], _capture[p + 1], _capture[p]) / 255;
            }
            return Vector3.Lerp(Vector3.Lerp(Read(x0, y0), Read(x1, y0), x - x0),
                Vector3.Lerp(Read(x0, y1), Read(x1, y1), x - x0), y - y0);
        }
        private static float SampleDepth(RefreshDepthMap map, float u, float v)
        {
            float x = Math.Clamp(u * map.Width - .5f, 0, map.Width - 1), y = Math.Clamp(v * map.Height - .5f, 0, map.Height - 1);
            int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, map.Width - 1), y1 = Math.Min(y0 + 1, map.Height - 1);
            float upper = float.Lerp(map.Values[y0 * map.Width + x0], map.Values[y0 * map.Width + x1], x - x0);
            float lower = float.Lerp(map.Values[y1 * map.Width + x0], map.Values[y1 * map.Width + x1], x - x0);
            return float.Lerp(upper, lower, y - y0);
        }
    }
}
