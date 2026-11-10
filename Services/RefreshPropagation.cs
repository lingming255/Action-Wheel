using System;
using System.Collections.Generic;

namespace Action_Wheel.Services
{
    /// <summary>
    /// Prepared travel times on the block grid. Positive neighbour costs keep every arrived
    /// region connected to the release cell. This instance is retimed only before playback.
    /// </summary>
    internal sealed class RefreshPropagation
    {
        internal const double StartDelayMs = 140;
        internal const double MaximumTravelMs = 2200;
        private const double BaseTravelMs = 1700;
        private const double DepthBoundaryMs = 50;
        private const double MaximumNeighbourGapMs = 220;
        private readonly int _width, _height, _cellSize, _columns, _rows;
        private readonly double[] _centreX, _centreY, _arrival;
        private readonly float[] _depth;
        private readonly PriorityQueue<int, double> _frontier = new();

        public RefreshPropagation(int width, int height, int cellSize, ReadOnlySpan<float> rawDepth)
        {
            if (width <= 0 || height <= 0 || cellSize <= 0)
                throw new ArgumentException("Propagation requires positive grid dimensions.");
            _width = width; _height = height; _cellSize = cellSize;
            _columns = 1 + (width - 1) / cellSize;
            _rows = 1 + (height - 1) / cellSize;
            int count = checked(_columns * _rows);
            if (rawDepth.Length != count)
                throw new ArgumentException("Propagation requires one depth per cell.", nameof(rawDepth));
            foreach (float depth in rawDepth)
                if (!float.IsFinite(depth) || depth < 0 || depth > 1)
                    throw new ArgumentException("Propagation depths must be finite and normalized.", nameof(rawDepth));

            _centreX = Centres(width, cellSize, _columns);
            _centreY = Centres(height, cellSize, _rows);
            _arrival = new double[count];
            _depth = new float[count];
            // A single compact binomial filter removes speed jitter without changing the raw
            // depth used by annotations. Replicated edges also handle one-row/column grids.
            for (int row = 0; row < _rows; row++)
            for (int column = 0; column < _columns; column++)
            {
                float sum = 0;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int y = Math.Clamp(row + dy, 0, _rows - 1);
                    int x = Math.Clamp(column + dx, 0, _columns - 1);
                    int weight = (dy == 0 ? 2 : 1) * (dx == 0 ? 2 : 1);
                    sum += rawDepth[y * _columns + x] * weight;
                }
                _depth[row * _columns + column] = sum / 16;
            }
        }

        /// <summary>Returns owned, reusable start times; the next solve replaces their contents.</summary>
        internal ReadOnlySpan<double> Arrivals(int originX, int originY)
        {
            originX = Math.Clamp(originX, 0, _width - 1);
            originY = Math.Clamp(originY, 0, _height - 1);
            double farX = Math.Max(originX, _width - 1 - originX);
            double farY = Math.Max(originY, _height - 1 - originY);
            double radius = Math.Sqrt(farX * farX + farY * farY);
            double msPerPixel = radius > 0 ? BaseTravelMs / radius : 0;
            int source = originY / _cellSize * _columns + originX / _cellSize;
            Array.Fill(_arrival, double.PositiveInfinity);
            _frontier.Clear();
            _arrival[source] = 0;
            _frontier.Enqueue(source, 0);
            while (_frontier.TryDequeue(out int current, out double time))
            {
                if (time != _arrival[current]) continue;
                int row = current / _columns, column = current % _columns;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int y = row + dy, x = column + dx;
                    if (y < 0 || y >= _rows || x < 0 || x >= _columns) continue;
                    int next = y * _columns + x;
                    double distanceX = _centreX[x] - _centreX[column];
                    double distanceY = _centreY[y] - _centreY[row];
                    double distance = Math.Sqrt(distanceX * distanceX + distanceY * distanceY);
                    double meanDepth = (_depth[current] + _depth[next]) * .5;
                    double slowness = .85 + .55 * (1 - meanDepth);
                    double boundary = DepthBoundaryMs * Math.Abs(_depth[current] - _depth[next]);
                    double candidate = time + distance * msPerPixel * slowness + boundary;
                    if (candidate >= _arrival[next]) continue;
                    _arrival[next] = candidate;
                    _frontier.Enqueue(next, candidate);
                }
            }

            double latest = 0, largestGap = 0;
            for (int row = 0; row < _rows; row++)
            for (int column = 0; column < _columns; column++)
            {
                double time = _arrival[row * _columns + column];
                latest = Math.Max(latest, time);
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int y = row + dy, x = column + dx;
                    if (y < 0 || y >= _rows || x < 0 || x >= _columns) continue;
                    largestGap = Math.Max(largestGap, Math.Abs(time - _arrival[y * _columns + x]));
                }
            }
            // Preserve naturally faster scenes; compress only when needed to fit complete
            // fades or to overlap adjacent pulses on tiny/degenerate grids. A single scale
            // retains every shortest-path ordering and never creates a simultaneous end wall.
            double scale = latest > MaximumTravelMs ? MaximumTravelMs / latest : 1;
            if (largestGap > MaximumNeighbourGapMs)
                scale = Math.Min(scale, MaximumNeighbourGapMs / largestGap);
            for (int index = 0; index < _arrival.Length; index++)
                _arrival[index] = StartDelayMs + _arrival[index] * scale;
            return _arrival;
        }

        private static double[] Centres(int size, int cellSize, int count)
        {
            var centres = new double[count];
            for (int index = 0; index < count; index++)
            {
                int left = index * cellSize;
                centres[index] = left + (Math.Min(cellSize, size - left) - 1) * .5;
            }
            return centres;
        }
    }
}
