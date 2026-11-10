using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Action_Wheel.Services
{
    /// <summary>
    /// Upsamples the reference effects' colour change, then encodes it over every original
    /// capture pixel. Only colour deltas are interpolated: a low-resolution alpha image would
    /// darken or blur details in the full-resolution desktop. One playback worker owns an instance.
    /// </summary>
    internal sealed class RefreshOutputEncoder
    {
        private static readonly float[] BrightCoverage = BuildCoverage(true);
        private static readonly float[] DarkCoverage = BuildCoverage(false);
        private readonly int _width, _height, _workWidth, _workHeight;
        private readonly byte[] _capture;
        private readonly ReferenceTexture _source;
        private readonly Vector3[] _delta;
        private readonly int[] _x0, _x1, _y0, _y1;
        private readonly float[] _xWeight, _yWeight;
        private readonly Vector3[][] _rows;
        private readonly float[][] _redRows, _greenRows, _blueRows;
        private readonly int _bandCount;
        private readonly ParallelOptions _options;
        private readonly Action<int> _encodeBand;
        private byte[]? _activeOutput;
        private CancellationToken _cancellation;

        public RefreshOutputEncoder(int width, int height, byte[] capture, ReferenceTexture sourceWork)
        {
            ArgumentNullException.ThrowIfNull(capture);
            ArgumentNullException.ThrowIfNull(sourceWork);
            if (width <= 0 || height <= 0 || capture.LongLength != (long)width * height * 4)
                throw new ArgumentException("Output encoding dimensions must match the full BGRA capture.");
            _width = width; _height = height; _capture = capture; _source = sourceWork;
            _workWidth = sourceWork.Width; _workHeight = sourceWork.Height;
            _delta = new Vector3[sourceWork.Pixels.Length];
            _x0 = new int[width]; _x1 = new int[width]; _xWeight = new float[width];
            _y0 = new int[height]; _y1 = new int[height]; _yWeight = new float[height];
            Map(width, _workWidth, _x0, _x1, _xWeight);
            Map(height, _workHeight, _y0, _y1, _yWeight);
            _bandCount = (long)width * height >= 256 * 1024
                ? Math.Min(4, Environment.ProcessorCount) : 1;
            _rows = new Vector3[_bandCount][];
            _redRows = new float[_bandCount][];
            _greenRows = new float[_bandCount][];
            _blueRows = new float[_bandCount][];
            for (int band = 0; band < _bandCount; band++)
            {
                _rows[band] = new Vector3[_workWidth];
                _redRows[band] = new float[_width];
                _greenRows[band] = new float[_width];
                _blueRows[band] = new float[_width];
            }
            _options = new ParallelOptions { MaxDegreeOfParallelism = _bandCount };
            _encodeBand = EncodeBand;
        }

        public void Encode(ReferenceTexture final, byte[] output, CancellationToken token = default)
        {
            ArgumentNullException.ThrowIfNull(final);
            ArgumentNullException.ThrowIfNull(output);
            if (final.Width != _workWidth || final.Height != _workHeight
                || output.Length != _capture.Length || ReferenceEquals(output, _capture))
                throw new ArgumentException("Final texture and independent output buffer must match their prepared dimensions.");
            token.ThrowIfCancellationRequested();

            bool anyChange = false;
            // Bilinear interpolation is linear, so Sample(final)-Sample(source) equals
            // Sample(final-source). Prepare only a work-sized delta, in byte-channel units.
            for (int y = 0; y < _workHeight; y++)
            {
                token.ThrowIfCancellationRequested();
                int first = y * _workWidth, last = first + _workWidth;
                for (int i = first; i < last; i++)
                {
                    Vector3 difference = (final.Pixels[i] - _source.Pixels[i]) * 255f;
                    _delta[i] = difference;
                    anyChange |= difference != Vector3.Zero;
                }
            }
            if (!anyChange)
            {
                output.AsSpan().Clear();
                return;
            }

            _activeOutput = output;
            _cancellation = token;
            _options.CancellationToken = token;
            try
            {
                if (_bandCount == 1) EncodeBand(0);
                else Parallel.For(0, _bandCount, _options, _encodeBand);
            }
            finally
            {
                _activeOutput = null;
                _cancellation = default;
                _options.CancellationToken = default;
            }
        }

        private void EncodeBand(int band)
        {
            ReadOnlySpan<uint> capture = MemoryMarshal.Cast<byte, uint>(_capture.AsSpan());
            Span<uint> output = MemoryMarshal.Cast<byte, uint>(_activeOutput!.AsSpan());
            Vector3[] row = _rows[band];
            float[] red = _redRows[band], green = _greenRows[band], blue = _blueRows[band];
            int firstY = _height * band / _bandCount, lastY = _height * (band + 1) / _bandCount;
            for (int y = firstY; y < lastY; y++)
            {
                _cancellation.ThrowIfCancellationRequested();
                int top = _y0[y] * _workWidth, bottom = _y1[y] * _workWidth;
                float vertical = _yWeight[y];
                for (int x = 0; x < _workWidth; x++)
                    row[x] = Vector3.Lerp(_delta[top + x], _delta[bottom + x], vertical);

                int start = y * _width;
                for (int x = 0; x < _width; x++)
                {
                    Vector3 delta = Vector3.Lerp(row[_x0[x]], row[_x1[x]], _xWeight[x]);
                    red[x] = delta.X; green[x] = delta.Y; blue[x] = delta.Z;
                }
                int position = 0;
                if (Vector.IsHardwareAccelerated)
                {
                    int end = _width - Vector<uint>.Count;
                    for (; position <= end; position += Vector<uint>.Count)
                    {
                        Vector<uint> encoded = EncodeVector(
                            new Vector<uint>(capture.Slice(start + position, Vector<uint>.Count)),
                            new Vector<float>(red, position), new Vector<float>(green, position),
                            new Vector<float>(blue, position));
                        encoded.CopyTo(output.Slice(start + position, Vector<uint>.Count));
                    }
                }
                for (; position < _width; position++)
                    output[start + position] = EncodePixel(capture[start + position],
                        new Vector3(red[position], green[position], blue[position]));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector<uint> EncodeVector(Vector<uint> source,
            Vector<float> redDelta, Vector<float> greenDelta, Vector<float> blueDelta)
        {
            var mask = new Vector<uint>(255);
            var maximum = new Vector<float>(255);
            Vector<float> red = Vector.ConvertToSingle((source >> 16) & mask);
            Vector<float> green = Vector.ConvertToSingle((source >> 8) & mask);
            Vector<float> blue = Vector.ConvertToSingle(source & mask);
            Vector<float> targetRed = Vector.Min(maximum, Vector.Max(Vector<float>.Zero, red + redDelta));
            Vector<float> targetGreen = Vector.Min(maximum, Vector.Max(Vector<float>.Zero, green + greenDelta));
            Vector<float> targetBlue = Vector.Min(maximum, Vector.Max(Vector<float>.Zero, blue + blueDelta));
            Vector<float> alpha = Vector.Min(maximum, Vector.Ceiling(Vector.Max(
                CoverageVector(red, targetRed), Vector.Max(CoverageVector(green, targetGreen),
                    CoverageVector(blue, targetBlue)))));
            Vector<float> remaining = (maximum - alpha) * (1f / 255f);
            Vector<uint> alphaBytes = Vector.ConvertToUInt32(alpha);
            var half = new Vector<float>(.5f);
            Vector<uint> redBytes = Vector.Min(alphaBytes, Vector.ConvertToUInt32(Vector.Max(
                Vector<float>.Zero, targetRed - remaining * red) + half));
            Vector<uint> greenBytes = Vector.Min(alphaBytes, Vector.ConvertToUInt32(Vector.Max(
                Vector<float>.Zero, targetGreen - remaining * green) + half));
            Vector<uint> blueBytes = Vector.Min(alphaBytes, Vector.ConvertToUInt32(Vector.Max(
                Vector<float>.Zero, targetBlue - remaining * blue) + half));
            return blueBytes | (greenBytes << 8) | (redBytes << 16) | (alphaBytes << 24);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector<float> CoverageVector(Vector<float> source, Vector<float> target)
        {
            Vector<float> difference = target - source;
            Vector<float> denominator = Vector.ConditionalSelect(
                Vector.GreaterThanOrEqual(difference, Vector<float>.Zero),
                new Vector<float>(255) - source, source);
            // A zero denominator can occur only for an unchanged black/white channel; its
            // numerator is also zero. Using one avoids NaNs without inventing any coverage.
            return Vector.Abs(difference) / Vector.Max(denominator, Vector<float>.One) * 255;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint EncodePixel(uint source, Vector3 delta)
        {
            if (delta == Vector3.Zero) return 0;
            int b = (int)(source & 255), g = (int)((source >> 8) & 255), r = (int)((source >> 16) & 255);
            float targetB = Math.Clamp(b + delta.Z, 0, 255);
            float targetG = Math.Clamp(g + delta.Y, 0, 255);
            float targetR = Math.Clamp(r + delta.X, 0, 255);
            // These 256-entry reciprocal tables preserve the floating target rather than
            // quantizing it before coverage selection, and remove all per-pixel divisions.
            float coverageB = Coverage(b, targetB), coverageG = Coverage(g, targetG), coverageR = Coverage(r, targetR);
            int alpha = Math.Clamp((int)MathF.Ceiling(Math.Max(coverageB, Math.Max(coverageG, coverageR))), 0, 255);
            float remaining = (255 - alpha) * (1f / 255f);
            int blue = Channel(targetB - remaining * b, alpha);
            int green = Channel(targetG - remaining * g, alpha);
            int red = Channel(targetR - remaining * r, alpha);
            return (uint)(blue | (green << 8) | (red << 16) | (alpha << 24));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Coverage(int source, float target)
            => target >= source ? (target - source) * BrightCoverage[source]
                : (source - target) * DarkCoverage[source];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Channel(float value, int alpha)
            => Math.Clamp((int)MathF.Round(value), 0, alpha);

        private static float[] BuildCoverage(bool brighter)
        {
            var factors = new float[256];
            for (int source = 0; source < 256; source++)
            {
                int denominator = brighter ? 255 - source : source;
                factors[source] = denominator > 0 ? 255f / denominator : 0;
            }
            return factors;
        }

        private static void Map(int destinationSize, int sourceSize, int[] first, int[] second, float[] weights)
        {
            for (int pixel = 0; pixel < destinationSize; pixel++)
            {
                // Keep the original normalized-coordinate calculation to preserve its edge
                // clamping and half-texel positions at both 640 and 1280 work resolutions.
                float coordinate = Math.Clamp(((pixel + .5f) / destinationSize) * sourceSize - .5f, 0, sourceSize - 1);
                int lower = (int)coordinate;
                first[pixel] = lower; second[pixel] = Math.Min(lower + 1, sourceSize - 1);
                weights[pixel] = coordinate - lower;
            }
        }
    }
}
