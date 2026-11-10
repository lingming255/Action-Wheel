using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Action_Wheel.Services
{
    /// <summary>
    /// Experimental reference ASCII/rain layer above the native uniform blocks. Only its
    /// positive colour delta is reconstructed; the native base never passes through a scaler.
    /// One playback worker owns the instance and its reusable work textures.
    /// </summary>
    internal sealed class SpectrumOverlayRefreshRenderer : IRefreshRenderer
    {
        private readonly int _width, _height, _workWidth, _workHeight, _bandCount;
        private readonly byte[] _capture;
        private readonly ReferenceTexture _source, _spectrumFrame;
        private readonly RefreshSpectrumEffect _spectrum;
        private readonly float[] _depth, _envelope;
        private readonly double[] _starts;
        private readonly int[] _x0, _x1, _y0, _y1;
        private readonly float[] _xWeight, _yWeight;
        private readonly Vector3[][] _rows;
        private readonly ParallelOptions _parallel;
        private readonly Action<int> _envelopeBand, _deltaBand, _compositeBand;
        private readonly float _overlayGain;
        private double _elapsedMs;
        private byte[]? _activeOutput;
        private CancellationToken _cancellation;

        internal UniformBlockRefreshRenderer BlocksRenderer { get; }
        internal float OriginDepth => BlocksRenderer.OriginDepth;
        public double DurationMs => RefreshDepthSweep.DurationMs;
        // A runs three Gaussian pairs and native delta composition in addition to the base.
        // A display-only 30 Hz budget skips directly to elapsed time, without history catch-up.
        public double FrameIntervalMs => 1000.0 / 30;
        public int BloomWidth => 1;
        public int BloomHeight => 1;

        public SpectrumOverlayRefreshRenderer(int width, int height, byte[] capture, RefreshDepthMap depth,
            int originX, int originY, float overlayGain = .35f)
        {
            if (!float.IsFinite(overlayGain) || overlayGain < 0 || overlayGain > 1)
                throw new ArgumentOutOfRangeException(nameof(overlayGain));
            BlocksRenderer = new UniformBlockRefreshRenderer(width, height, capture, depth, originX, originY);
            _width = width; _height = height; _capture = capture; _overlayGain = overlayGain;
            double scale = Math.Min(1, 1280.0 / Math.Max(width, height));
            _workWidth = Math.Max(1, (int)Math.Round(width * scale));
            _workHeight = Math.Max(1, (int)Math.Round(height * scale));
            _source = new ReferenceTexture(_workWidth, _workHeight);
            _spectrumFrame = new ReferenceTexture(_workWidth, _workHeight);
            int count = checked(_workWidth * _workHeight);
            _depth = new float[count]; _envelope = new float[count]; _starts = new double[count];
            _bandCount = (long)width * height >= 256 * 1024 ? Math.Min(4, Environment.ProcessorCount) : 1;
            _parallel = new ParallelOptions { MaxDegreeOfParallelism = _bandCount };
            Parallel.For(0, _workHeight, _parallel, y =>
            {
                for (int x = 0; x < _workWidth; x++)
                {
                    int pixel = y * _workWidth + x;
                    float u = (x + .5f) / _workWidth, v = (y + .5f) / _workHeight;
                    _source.Pixels[pixel] = SampleCapture(capture, width, height, u, v);
                    _depth[pixel] = SampleDepth(depth, u, v);
                }
            });
            _spectrum = new RefreshSpectrumEffect(_workWidth, _workHeight,
                ReferenceEffectMath.LoadTexture("atlas.rgb.gz"),
                ReferenceEffectMath.LoadTexture("colorbar.rgb.gz"),
                ReferenceEffectMath.LoadTexture("spectrum-lut.rgb.gz"));
            _spectrum.Prepare(_source, _depth);
            _x0 = new int[width]; _x1 = new int[width]; _xWeight = new float[width];
            _y0 = new int[height]; _y1 = new int[height]; _yWeight = new float[height];
            Map(width, _workWidth, _x0, _x1, _xWeight);
            Map(height, _workHeight, _y0, _y1, _yWeight);
            _rows = new Vector3[_bandCount][];
            for (int band = 0; band < _bandCount; band++) _rows[band] = new Vector3[_workWidth];
            _envelopeBand = FillEnvelopeBand; _deltaBand = MakeDeltaBand; _compositeBand = CompositeBand;
            SetOrigin(originX, originY);
        }

        /// <summary>Retimes both layers before playback; raw glyph geometry stays prepared.</summary>
        internal void SetOrigin(int originX, int originY)
        {
            BlocksRenderer.SetOrigin(originX, originY);
            float originDepth = OriginDepth;
            for (int index = 0; index < _starts.Length; index++)
                _starts[index] = RefreshDepthSweep.StartAt(_depth[index], originDepth);
        }

        public void Render(double elapsedMs, byte[] fullBuffer, byte[] bloomBuffer,
            CancellationToken cancellation = default)
        {
            // The base owns buffer validation, alias checks and complete invalid/end clearing.
            BlocksRenderer.Render(elapsedMs, fullBuffer, bloomBuffer, cancellation);
            if (_overlayGain == 0 || !double.IsFinite(elapsedMs) || elapsedMs < 0 || elapsedMs >= DurationMs)
                return;
            _elapsedMs = elapsedMs; _cancellation = cancellation;
            _parallel.CancellationToken = cancellation;
            _activeOutput = fullBuffer;
            try
            {
                RunBands(_envelopeBand);
                cancellation.ThrowIfCancellationRequested();
                _spectrum.RenderWithEnvelope(_source, _depth,
                    elapsedMs / (1000 * RefreshDepthSweep.TimeScale), _envelope, _spectrumFrame, cancellation);
                RunBands(_deltaBand);
                RunBands(_compositeBand);
            }
            finally
            {
                _activeOutput = null; _cancellation = default; _parallel.CancellationToken = default;
            }
        }

        private void RunBands(Action<int> action)
        {
            if (_bandCount == 1) action(0);
            else Parallel.For(0, _bandCount, _parallel, action);
        }

        private void FillEnvelopeBand(int band)
        {
            int firstY = _workHeight * band / _bandCount, lastY = _workHeight * (band + 1) / _bandCount;
            for (int y = firstY; y < lastY; y++)
            {
                _cancellation.ThrowIfCancellationRequested();
                int last = (y + 1) * _workWidth;
                for (int pixel = y * _workWidth; pixel < last; pixel++)
                    _envelope[pixel] = UniformBlockRefreshRenderer.OpacityAt(_elapsedMs - _starts[pixel]) / .88f;
            }
        }

        private void MakeDeltaBand(int band)
        {
            int firstY = _workHeight * band / _bandCount, lastY = _workHeight * (band + 1) / _bandCount;
            for (int y = firstY; y < lastY; y++)
            {
                _cancellation.ThrowIfCancellationRequested();
                int last = (y + 1) * _workWidth;
                for (int pixel = y * _workWidth; pixel < last; pixel++)
                    _spectrumFrame.Pixels[pixel] = Vector3.Max(Vector3.Zero,
                        _spectrumFrame.Pixels[pixel] - _source.Pixels[pixel]) * (_overlayGain * 255);
            }
        }

        private void CompositeBand(int band)
        {
            ReadOnlySpan<uint> capture = MemoryMarshal.Cast<byte, uint>(_capture.AsSpan());
            Span<uint> output = MemoryMarshal.Cast<byte, uint>(_activeOutput!.AsSpan());
            Vector3[] row = _rows[band], delta = _spectrumFrame.Pixels;
            int firstY = _height * band / _bandCount, lastY = _height * (band + 1) / _bandCount;
            for (int y = firstY; y < lastY; y++)
            {
                _cancellation.ThrowIfCancellationRequested();
                int top = _y0[y] * _workWidth, bottom = _y1[y] * _workWidth;
                for (int x = 0; x < _workWidth; x++)
                    row[x] = Vector3.Lerp(delta[top + x], delta[bottom + x], _yWeight[y]);
                int nativeRow = y * _width;
                for (int x = 0; x < _width; x++)
                {
                    Vector3 light = Vector3.Lerp(row[_x0[x]], row[_x1[x]], _xWeight[x]);
                    int red = Math.Clamp((int)(light.X + .5f), 0, 255);
                    int green = Math.Clamp((int)(light.Y + .5f), 0, 255);
                    int blue = Math.Clamp((int)(light.Z + .5f), 0, 255);
                    if ((red | green | blue) == 0) continue;
                    int pixel = nativeRow + x;
                    output[pixel] = UniformBlockRefreshRenderer.AddLight(output[pixel], capture[pixel], blue, green, red);
                }
            }
        }

        private static Vector3 SampleCapture(byte[] capture, int width, int height, float u, float v)
        {
            float x = Math.Clamp(u * width - .5f, 0, width - 1), y = Math.Clamp(v * height - .5f, 0, height - 1);
            int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, width - 1), y1 = Math.Min(y0 + 1, height - 1);
            Vector3 Read(int px, int py)
            {
                int offset = (py * width + px) * 4;
                return new Vector3(capture[offset + 2], capture[offset + 1], capture[offset]) / 255;
            }
            return Vector3.Lerp(Vector3.Lerp(Read(x0, y0), Read(x1, y0), x - x0),
                Vector3.Lerp(Read(x0, y1), Read(x1, y1), x - x0), y - y0);
        }

        private static float SampleDepth(RefreshDepthMap depth, float u, float v)
        {
            float x = Math.Clamp(u * depth.Width - .5f, 0, depth.Width - 1);
            float y = Math.Clamp(v * depth.Height - .5f, 0, depth.Height - 1);
            int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, depth.Width - 1), y1 = Math.Min(y0 + 1, depth.Height - 1);
            return float.Lerp(float.Lerp(depth.Values[y0 * depth.Width + x0], depth.Values[y0 * depth.Width + x1], x - x0),
                float.Lerp(depth.Values[y1 * depth.Width + x0], depth.Values[y1 * depth.Width + x1], x - x0), y - y0);
        }

        private static void Map(int destination, int samples, int[] first, int[] second, float[] weight)
        {
            for (int pixel = 0; pixel < destination; pixel++)
            {
                float coordinate = Math.Clamp((pixel + .5f) / destination * samples - .5f, 0, samples - 1);
                first[pixel] = (int)coordinate;
                second[pixel] = Math.Min(first[pixel] + 1, samples - 1);
                weight[pixel] = coordinate - first[pixel];
            }
        }
    }
}
