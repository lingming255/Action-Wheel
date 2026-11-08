using System;
using System.Collections.Generic;

namespace Action_Wheel.Services
{
    /// <summary>One macro tile with independent timing for its four raster layers.</summary>
    internal readonly record struct RefreshCluster(
        int Left, int Top, int Width, int Height, double StartMs);

    internal sealed class RefreshAnalysis
    {
        public required byte[] Subject { get; init; }
        public required byte[] Background { get; init; }
        public required byte[] Boundary { get; init; }
        public required int ClusterPx { get; init; }
        public required RefreshCluster[] Clusters { get; init; }
    }

    /// <summary>
    /// Makes a lightweight, deterministic scene map from a monitor capture. It is a visual-depth
    /// proxy, not semantic object recognition: coherent colour regions, local contrast, detail and
    /// centre saliency estimate which areas should read as nearer and more visually important.
    /// </summary>
    internal static class RefreshAnalyzer
    {
        private const int ClusterTiles = 12;
        private const double RegionJoinDistance = 52;
        private const double LegacyTileDip = 14;
        internal const double TileDip = LegacyTileDip * 0.8;

        // Physical pixel rounding keeps the raster crisp at every monitor DPI.
        internal static int TilePixels(double scale) => Math.Max(4, (int)Math.Round(TileDip * scale));

        private struct Region
        {
            public long Red, Green, Blue;
            public int Count;
            public double EdgeSum;
            public int EdgeCount;
        }

        public static RefreshAnalysis Build(byte[] source, int width, int height, int tile, int seed,
            int originX, int originY)
        {
            int columns = (width + tile - 1) / tile;
            int rows = (height + tile - 1) / tile;
            int count = checked(columns * rows);
            var red = new byte[count];
            var green = new byte[count];
            var blue = new byte[count];
            SampleCells(source, width, height, tile, columns, rows, red, green, blue);

            int[] labels = FindColourRegions(red, green, blue, columns, rows, out int regionCount);
            var regions = BuildRegionStats(red, green, blue, labels, columns, rows, regionCount);
            var boundaryStrength = new float[count];
            var boundaryNeighbour = new int[count];
            var depth = BuildDepth(red, green, blue, labels, regions, columns, rows,
                boundaryStrength, boundaryNeighbour);
            SmoothDepth(depth, columns, rows);

            int byteCount = checked(width * height * 4);
            var subject = new byte[byteCount];
            var background = new byte[byteCount];
            var boundary = new byte[byteCount];
            Rasterize(source, width, height, tile, columns, rows, red, green, blue,
                labels, depth, boundaryStrength, boundaryNeighbour,
                subject, background, boundary, seed);

            int clusterPx = tile * ClusterTiles;
            var clusters = BuildClusters(width, height, tile, columns, rows, clusterPx,
                depth, boundaryStrength, originX, originY);
            return new RefreshAnalysis
            {
                Subject = subject,
                Background = background,
                Boundary = boundary,
                ClusterPx = clusterPx,
                Clusters = clusters,
            };
        }

        private static void SampleCells(byte[] source, int width, int height, int tile,
            int columns, int rows, byte[] red, byte[] green, byte[] blue)
        {
            const int sampleStep = 3;
            for (int cy = 0; cy < rows; cy++)
            {
                int y0 = cy * tile, y1 = Math.Min(height, y0 + tile);
                for (int cx = 0; cx < columns; cx++)
                {
                    int x0 = cx * tile, x1 = Math.Min(width, x0 + tile);
                    long sr = 0, sg = 0, sb = 0, n = 0;
                    for (int y = y0; y < y1; y += sampleStep)
                    {
                        int row = y * width * 4;
                        for (int x = x0; x < x1; x += sampleStep)
                        {
                            int i = row + x * 4;
                            sb += source[i];
                            sg += source[i + 1];
                            sr += source[i + 2];
                            n++;
                        }
                    }

                    int cell = cy * columns + cx;
                    red[cell] = (byte)(sr / n);
                    green[cell] = (byte)(sg / n);
                    blue[cell] = (byte)(sb / n);
                }
            }
        }

        private static int[] FindColourRegions(byte[] red, byte[] green, byte[] blue,
            int columns, int rows, out int regionCount)
        {
            int count = red.Length;
            var labels = new int[count];
            Array.Fill(labels, -1);
            var queue = new int[count];
            regionCount = 0;

            for (int seed = 0; seed < count; seed++)
            {
                if (labels[seed] >= 0)
                    continue;

                int currentLabel = regionCount;
                int head = 0, tail = 0;
                labels[seed] = currentLabel;
                queue[tail++] = seed;
                while (head < tail)
                {
                    int current = queue[head++];
                    int x = current % columns, y = current / columns;
                    TryAdd(x > 0 ? current - 1 : -1, current);
                    TryAdd(x + 1 < columns ? current + 1 : -1, current);
                    TryAdd(y > 0 ? current - columns : -1, current);
                    TryAdd(y + 1 < rows ? current + columns : -1, current);
                }
                regionCount++;

                void TryAdd(int candidate, int from)
                {
                    if (candidate < 0 || labels[candidate] >= 0)
                        return;
                    if (ColourDistance(red[from], green[from], blue[from],
                        red[candidate], green[candidate], blue[candidate]) > RegionJoinDistance)
                        return;
                    labels[candidate] = currentLabel;
                    queue[tail++] = candidate;
                }
            }
            return labels;
        }

        private static Region[] BuildRegionStats(byte[] red, byte[] green, byte[] blue,
            int[] labels, int columns, int rows, int regionCount)
        {
            var regions = new Region[regionCount];
            for (int i = 0; i < labels.Length; i++)
            {
                ref Region region = ref regions[labels[i]];
                region.Red += red[i];
                region.Green += green[i];
                region.Blue += blue[i];
                region.Count++;
            }

            for (int y = 0; y < rows; y++)
            for (int x = 0; x < columns; x++)
            {
                int i = y * columns + x;
                if (x + 1 < columns) AddEdge(i, i + 1);
                if (y + 1 < rows) AddEdge(i, i + columns);
            }

            for (int i = 0; i < regions.Length; i++)
            {
                ref Region region = ref regions[i];
                if (region.EdgeCount > 0)
                    region.EdgeSum /= region.EdgeCount;
            }
            return regions;

            void AddEdge(int a, int b)
            {
                if (labels[a] == labels[b]) return;
                double distance = ColourDistance(red[a], green[a], blue[a], red[b], green[b], blue[b]);
                ref Region first = ref regions[labels[a]];
                ref Region second = ref regions[labels[b]];
                first.EdgeSum += distance; first.EdgeCount++;
                second.EdgeSum += distance; second.EdgeCount++;
            }
        }

        private static float[] BuildDepth(byte[] red, byte[] green, byte[] blue, int[] labels,
            Region[] regions, int columns, int rows, float[] boundaryStrength, int[] boundaryNeighbour)
        {
            int total = red.Length;
            var depth = new float[total];
            Array.Fill(boundaryNeighbour, -1);
            for (int y = 0; y < rows; y++)
            for (int x = 0; x < columns; x++)
            {
                int i = y * columns + x;
                double strongestEdge = 0, localTexture = 0;
                int strongestNeighbour = -1;
                Compare(x > 0 ? i - 1 : -1);
                Compare(x + 1 < columns ? i + 1 : -1);
                Compare(y > 0 ? i - columns : -1);
                Compare(y + 1 < rows ? i + columns : -1);

                double regionContrast = Math.Clamp(regions[labels[i]].EdgeSum / 120.0, 0, 1);
                double area = regions[labels[i]].Count / (double)total;
                // Tiny texture islands and huge background fields are less likely to be the
                // foreground subject; mid-sized, coherent regions receive a stronger base score.
                double regionScale = Math.Clamp(area / 0.025, 0, 1)
                    * Math.Clamp((1 - area) / 0.55, 0, 1);
                double max = Math.Max(red[i], Math.Max(green[i], blue[i]));
                double min = Math.Min(red[i], Math.Min(green[i], blue[i]));
                double saturation = max > 0 ? (max - min) / max : 0;
                double dx = (x + 0.5) / columns * 2 - 1;
                double dy = (y + 0.5) / rows * 2 - 1;
                double centre = Math.Clamp(1 - Math.Sqrt(dx * dx + dy * dy) / Math.Sqrt(2), 0, 1);

                boundaryStrength[i] = (float)Math.Clamp(strongestEdge / 105.0, 0, 1);
                boundaryNeighbour[i] = strongestNeighbour;
                double detail = Math.Clamp(localTexture / 105.0, 0, 1);
                // This smooth visual-depth proxy favours coherent regions that stand apart from
                // their neighbours, with local detail and centre saliency breaking ties.
                depth[i] = (float)Math.Clamp(
                    0.28 * centre
                    + 0.16 * detail
                    + 0.10 * saturation
                    + 0.24 * regionContrast
                    + 0.13 * boundaryStrength[i]
                    + 0.09 * regionScale, 0, 1);

                void Compare(int other)
                {
                    if (other < 0) return;
                    double distance = ColourDistance(red[i], green[i], blue[i],
                        red[other], green[other], blue[other]);
                    localTexture += distance * 0.25;
                    if (labels[i] != labels[other] && distance > strongestEdge)
                    {
                        strongestEdge = distance;
                        strongestNeighbour = other;
                    }
                }
            }
            return depth;
        }

        private static void SmoothDepth(float[] depth, int columns, int rows)
        {
            var blurred = new float[depth.Length];
            for (int y = 0; y < rows; y++)
            for (int x = 0; x < columns; x++)
            {
                float sum = 0, weight = 0;
                for (int oy = -1; oy <= 1; oy++)
                for (int ox = -1; ox <= 1; ox++)
                {
                    int nx = x + ox, ny = y + oy;
                    if (nx < 0 || ny < 0 || nx >= columns || ny >= rows) continue;
                    int w = ox == 0 && oy == 0 ? 4 : ox == 0 || oy == 0 ? 2 : 1;
                    sum += depth[ny * columns + nx] * w;
                    weight += w;
                }
                blurred[y * columns + x] = sum / weight;
            }
            Array.Copy(blurred, depth, depth.Length);
        }

        /// <summary>
        /// Large blocks reserve analysis cells, keeping subject and seam blocks mutually exclusive.
        /// A separate fine grid may overlay them with much smaller, quieter background marks.
        /// </summary>
        private static void Rasterize(byte[] source, int width, int height, int tile,
            int columns, int rows, byte[] red, byte[] green, byte[] blue, int[] labels,
            float[] depth, float[] boundaryStrength, int[] boundaryNeighbour,
            byte[] subject, byte[] background, byte[] boundary, int seed)
        {
            var random = new Random(seed);
            var occupied = new bool[labels.Length];
            const int gap = 1; // The legacy mosaic's one-physical-pixel gap never scales with block size.

            // Reserve seam blocks first, so a later subject stamp cannot draw over them.
            for (int cy = 0; cy < rows; cy++)
            for (int cx = 0; cx < columns; cx++)
            {
                int i = cy * columns + cx;
                double edge = boundaryStrength[i];
                if (occupied[i] || boundaryNeighbour[i] < 0 || edge <= 0.22
                    || random.NextDouble() >= 0.15 + edge * 0.30)
                    continue;

                int cellsWide = edge > 0.65 && random.NextDouble() < 0.28 ? 2 : 1;
                int cellsHigh = cellsWide == 1 && edge > 0.65 && random.NextDouble() < 0.28 ? 2 : 1;
                if (!CanReserve(cx, cy, cellsWide, cellsHigh, true))
                    cellsWide = cellsHigh = 1;
                Reserve(cx, cy, cellsWide, cellsHigh);
                int x = cx * tile, y = cy * tile;
                int blockWidth = Math.Min(width - x, tile * cellsWide) - gap;
                int blockHeight = Math.Min(height - y, tile * cellsHigh) - gap;
                if (blockWidth <= 0 || blockHeight <= 0) continue;
                SampleRectColour(source, width, x, y, blockWidth, blockHeight,
                    out byte r, out byte g, out byte b);
                LegacyPigment(r, g, b, out _, out _, out _, out double alpha);
                // Most of the scene stays source-coloured. Only sparse seam blocks receive
                // a contrast accent derived from their own sampled colour, never a palette.
                if (random.NextDouble() < 0.62)
                    MakeContrastColour(r, g, b, out r, out g, out b);
                else
                    LegacyPigment(r, g, b, out r, out g, out b, out _);
                FillRect(boundary, width, height, x, y, blockWidth, blockHeight,
                    r, g, b, Math.Clamp(0.42 + alpha * 0.28, 0.42, 0.72));
            }

            for (int cy = 0; cy < rows; cy++)
            for (int cx = 0; cx < columns; cx++)
            {
                int i = cy * columns + cx;
                double z = depth[i];
                // Keep quiet/neutral regions populated too, while salient regions remain denser.
                double chance = 0.45 + 0.48 * z;
                if (occupied[i] || random.NextDouble() >= chance)
                    continue;

                // Merge only similar source-colour cells in one region and one animation cluster.
                // Large blocks are reserved grid rectangles, never displaced, overlapping stamps.
                int cellsWide = z > 0.40 && random.NextDouble() < z * 0.42 ? 2 : 1;
                int cellsHigh = z > 0.50 && random.NextDouble() < z * 0.30 ? 2 : 1;
                if (!CanReserve(cx, cy, cellsWide, cellsHigh, false))
                    cellsWide = cellsHigh = 1;
                Reserve(cx, cy, cellsWide, cellsHigh);
                int x = cx * tile, y = cy * tile;
                int blockWidth = Math.Min(width - x, tile * cellsWide) - gap;
                int blockHeight = Math.Min(height - y, tile * cellsHigh) - gap;
                if (blockWidth <= 0 || blockHeight <= 0) continue;
                SampleRectColour(source, width, x, y, blockWidth, blockHeight,
                    out byte r, out byte g, out byte b);
                LegacyPigment(r, g, b, out r, out g, out b, out double alpha);
                FillRect(subject, width, height, x, y, blockWidth, blockHeight, r, g, b, alpha);
            }

            // About 0.2 of the old 14 DIP tile. Unlike the former random stamps, every mark
            // has its own fine-grid footprint with a one-pixel right/bottom gap.
            int fineTile = Math.Max(2, (int)Math.Round(tile * 0.25));
            for (int y = 0; y < height; y += fineTile)
            for (int x = 0; x < width; x += fineTile)
            {
                int i = (y / tile) * columns + x / tile;
                double z = depth[i];
                int markWidth = Math.Min(fineTile, width - x) - gap;
                int markHeight = Math.Min(fineTile, height - y) - gap;
                if (markWidth <= 0 || markHeight <= 0) continue;
                SampleRectColour(source, width, x, y, markWidth, markHeight,
                    out byte r, out byte g, out byte b);
                // Neutral screenshots still need fine texture. Saturation adds detail without
                // making colourful backgrounds compete with the larger, more opaque subject.
                double chance = (0.28 + 0.26 * Saturation(r, g, b)) * (0.64 + (1 - z) * 0.22);
                if (random.NextDouble() >= chance) continue;
                LegacyPigment(r, g, b, out r, out g, out b, out double alpha);
                alpha *= 0.52; // Fine marks may overlap large blocks, but never compete in opacity.
                DrawFineMark(background, width, height, x, y, markWidth, markHeight,
                    r, g, b, alpha, random.Next(8));
            }

            bool CanReserve(int cx, int cy, int cellsWide, int cellsHigh, bool seam)
            {
                if (cx + cellsWide > columns || cy + cellsHigh > rows
                    || cx / ClusterTiles != (cx + cellsWide - 1) / ClusterTiles
                    || cy / ClusterTiles != (cy + cellsHigh - 1) / ClusterTiles)
                    return false;
                int first = cy * columns + cx;
                for (int oy = 0; oy < cellsHigh; oy++)
                for (int ox = 0; ox < cellsWide; ox++)
                {
                    int cell = (cy + oy) * columns + cx + ox;
                    if (occupied[cell] || labels[cell] != labels[first]
                        || ColourDistance(red[first], green[first], blue[first],
                            red[cell], green[cell], blue[cell]) > 24
                        || (seam && boundaryStrength[cell] <= 0.22))
                        return false;
                }
                return true;
            }

            void Reserve(int cx, int cy, int cellsWide, int cellsHigh)
            {
                for (int oy = 0; oy < cellsHigh; oy++)
                for (int ox = 0; ox < cellsWide; ox++)
                    occupied[(cy + oy) * columns + cx + ox] = true;
            }
        }

        private static void SampleRectColour(byte[] source, int width, int x, int y,
            int rectWidth, int rectHeight, out byte r, out byte g, out byte b)
        {
            long sr = 0, sg = 0, sb = 0, count = 0;
            for (int py = y; py < y + rectHeight; py += 2)
            for (int px = x; px < x + rectWidth; px += 2)
            {
                int offset = (py * width + px) * 4;
                sb += source[offset]; sg += source[offset + 1]; sr += source[offset + 2];
                count++;
            }
            r = (byte)(sr / count); g = (byte)(sg / count); b = (byte)(sb / count);
        }

        private static double Saturation(byte r, byte g, byte b)
        {
            double max = Math.Max(r, Math.Max(g, b));
            return max > 0 ? (max - Math.Min(r, Math.Min(g, b))) / max : 0;
        }

        /// <summary>The legacy mosaic's hue-preserving lift and saturation/value-dependent opacity.</summary>
        private static void LegacyPigment(byte sr, byte sg, byte sb,
            out byte r, out byte g, out byte b, out double alpha)
        {
            double value = Math.Max(sr, Math.Max(sg, sb)) / 255.0;
            double saturation = Saturation(sr, sg, sb);
            double lifted = value + (1 - value) * saturation * 0.85;
            double scale = value > 0 ? lifted / value : 0;
            r = ToByte(sr / 255.0 * scale);
            g = ToByte(sg / 255.0 * scale);
            b = ToByte(sb / 255.0 * scale);
            alpha = Math.Clamp((0.15 + 0.85 * saturation) * (0.3 + 0.7 * value), 0, 1);
        }

        /// <summary>
        /// Rotate the sampled hue by 180 degrees while keeping its saturation, rather than
        /// inverting RGB into muddy pastels. Neutral cells get only a restrained light/dark contrast.
        /// </summary>
        internal static void MakeContrastColour(byte sr, byte sg, byte sb,
            out byte r, out byte g, out byte b)
        {
            double max = Math.Max(sr, Math.Max(sg, sb));
            double min = Math.Min(sr, Math.Min(sg, sb));
            if (Saturation(sr, sg, sb) < 0.10)
            {
                byte neutral = ToByte((0.3 * sr + 0.59 * sg + 0.11 * sb) / 255.0 > 0.5 ? 0.24 : 0.78);
                r = g = b = neutral;
                return;
            }
            // max + min - channel is a 180-degree HSV hue rotation, preserving S and V.
            double scale = Math.Clamp(max / 255.0 + 0.12, 0.62, 0.90) * 255 / max;
            r = ToByte((max + min - sr) * scale / 255);
            g = ToByte((max + min - sg) * scale / 255);
            b = ToByte((max + min - sb) * scale / 255);
        }

        private static byte ToByte(double value)
            => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);

        private static void DrawFineMark(byte[] target, int width, int height, int x, int y,
            int markWidth, int markHeight, byte r, byte g, byte b, double alpha, int shape)
        {
            // Mostly tiny squares/points. Plus and wave marks are clipped to the same small
            // footprint at higher DPI; they cannot grow into a competing foreground block.
            if (shape == 6 && markWidth >= 3 && markHeight >= 3)
            {
                FillRect(target, width, height, x + markWidth / 2, y, 1, markHeight, r, g, b, alpha);
                FillRect(target, width, height, x, y + markHeight / 2, markWidth, 1, r, g, b, alpha);
            }
            else if (shape == 7 && markWidth >= 3 && markHeight >= 2)
            {
                for (int dx = 0; dx < markWidth; dx++)
                    FillRect(target, width, height, x + dx, y + dx % markHeight, 1, 1, r, g, b, alpha);
            }
            else
                FillRect(target, width, height, x, y, shape == 0 ? 1 : markWidth,
                    shape == 0 ? 1 : markHeight, r, g, b, alpha);
        }

        private static RefreshCluster[] BuildClusters(int width, int height, int tile,
            int columns, int rows, int clusterPx, float[] depth, float[] boundary,
            int originX, int originY)
        {
            int clusterColumns = (width + clusterPx - 1) / clusterPx;
            int clusterRows = (height + clusterPx - 1) / clusterPx;
            var clusters = new List<RefreshCluster>(clusterColumns * clusterRows);
            for (int cy = 0; cy < clusterRows; cy++)
            for (int cx = 0; cx < clusterColumns; cx++)
            {
                int left = cx * clusterPx, top = cy * clusterPx;
                int right = Math.Min(width, left + clusterPx), bottom = Math.Min(height, top + clusterPx);
                int gx0 = left / tile, gy0 = top / tile;
                int gx1 = Math.Min(columns, (right + tile - 1) / tile);
                int gy1 = Math.Min(rows, (bottom + tile - 1) / tile);
                double depthSum = 0, edgeSum = 0;
                int n = 0;
                for (int y = gy0; y < gy1; y++)
                for (int x = gx0; x < gx1; x++)
                {
                    int i = y * columns + x;
                    depthSum += depth[i];
                    edgeSum += boundary[i];
                    n++;
                }

                double z = n > 0 ? depthSum / n : 0;
                double edge = n > 0 ? edgeSum / n : 0;
                double clusterX = (left + right) * 0.5;
                double clusterY = (top + bottom) * 0.5;
                double distance = Math.Sqrt((clusterX - originX) * (clusterX - originX)
                    + (clusterY - originY) * (clusterY - originY));
                double farthest = Math.Max(
                    Math.Max(Math.Sqrt(originX * originX + originY * originY),
                        Math.Sqrt((width - originX) * (width - originX) + originY * originY)),
                    Math.Max(Math.Sqrt(originX * originX + (height - originY) * (height - originY)),
                        Math.Sqrt((width - originX) * (width - originX)
                            + (height - originY) * (height - originY))));
                double progress = farthest > 0 ? distance / farthest : 0;
                // A radial wave radiates from the opening click. Two low-frequency noise scales
                // and small deterministic jitter soften the ring edge without randomizing the flow.
                double noise = Math.Sin(cx * 0.34 + cy * 0.23) * 9
                    + Math.Sin(cx * 0.13 - cy * 0.31) * 7
                    + (((cx * 73856093) ^ (cy * 19349663)) & 15) - 7.5;
                // Normalize the original shape/noise into the shared, shorter sweep window.
                double phase = Math.Clamp((progress * 285 + noise + z * 13 + edge * 22) / 300, 0, 1);
                double start = phase * RefreshTiming.SweepMs;
                clusters.Add(new RefreshCluster(left, top, right - left, bottom - top, start));
            }
            return clusters.ToArray();
        }

        private static double ColourDistance(byte r1, byte g1, byte b1, byte r2, byte g2, byte b2)
        {
            double dr = r1 - r2, dg = g1 - g2, db = b1 - b2;
            return Math.Sqrt(dr * dr * 0.30 + dg * dg * 0.59 + db * db * 0.11) * 1.55;
        }

        private static void FillRect(byte[] target, int width, int height, int x, int y,
            int rectWidth, int rectHeight, byte r, byte g, byte b, double alpha)
        {
            int x0 = Math.Clamp(x, 0, width), y0 = Math.Clamp(y, 0, height);
            int x1 = Math.Clamp(x + rectWidth, 0, width), y1 = Math.Clamp(y + rectHeight, 0, height);
            if (x1 <= x0 || y1 <= y0) return;
            byte a = (byte)Math.Clamp(Math.Round(alpha * 255), 0, 255);
            byte pb = (byte)(b * a / 255), pg = (byte)(g * a / 255), pr = (byte)(r * a / 255);
            for (int py = y0; py < y1; py++)
            {
                int offset = (py * width + x0) * 4;
                for (int px = x0; px < x1; px++, offset += 4)
                {
                    target[offset] = pb;
                    target[offset + 1] = pg;
                    target[offset + 2] = pr;
                    target[offset + 3] = a;
                }
            }
        }
    }
}
