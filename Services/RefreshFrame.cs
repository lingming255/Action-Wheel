using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Graphics;

namespace Action_Wheel.Services
{
    /// <summary>
    /// A monitor capture and pre-rasterized pixel layers for the refresh effect. Capture happens
    /// before <see cref="ScreenGrade"/> lowers saturation; analysis runs on a worker while the wheel
    /// is held so release only has to assemble and animate already prepared images.
    /// </summary>
    internal sealed class RefreshFrame
    {
        /// <summary>Premultiplied BGRA cover graded exactly like <see cref="ScreenGrade"/>.</summary>
        public required byte[] Grey { get; init; }
        /// <summary>Large, dense blocks assigned to visually salient regions.</summary>
        public required byte[] Subject { get; init; }
        /// <summary>Fine points, plus signs and wave glyphs assigned to quieter regions.</summary>
        public required byte[] Background { get; init; }
        /// <summary>High-saturation accents rasterized from colour-region boundaries.</summary>
        public required byte[] Boundary { get; init; }
        public required RectInt32 Bounds { get; init; }
        /// <summary>Original click position relative to the captured monitor, in physical pixels.</summary>
        public required int OriginX { get; init; }
        public required int OriginY { get; init; }
        /// <summary>Physical source tile size used by the 11.2 DIP analysis grid.</summary>
        public required int TilePx { get; init; }
        public required int ClusterPx { get; init; }
        public required RefreshCluster[] Clusters { get; init; }
        /// <summary>Low-resolution emissive bloom, prepared per cluster and per animated layer.</summary>
        public required RefreshClusterBloom[] Blooms { get; init; }

        /// <summary>
        /// Captures and builds on a worker thread. <paramref name="grabbed"/> runs as soon as the
        /// raw screen has been read, which is the earliest safe moment to lower saturation. It may
        /// run twice on failure paths, so callers must keep it idempotent.
        /// </summary>
        public static Task<RefreshFrame?> CaptureAsync(PointInt32 position, Action grabbed)
            => Task.Run(() =>
            {
                try { return Capture(position, grabbed); }
                finally { grabbed(); }
            });

        private static RefreshFrame? Capture(PointInt32 position, Action grabbed)
        {
            try
            {
                var monitor = MonitorFromPoint(new POINT { x = position.X, y = position.Y }, MONITOR_DEFAULTTONEAREST);
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (!GetMonitorInfo(monitor, ref mi))
                    return null;

                int x = mi.rcMonitor.left, y = mi.rcMonitor.top;
                int w = mi.rcMonitor.right - x, h = mi.rcMonitor.bottom - y;
                double scale = GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1.0;
                int tile = RefreshAnalyzer.TilePixels(scale);

                byte[]? pixels = Grab(x, y, w, h);
                grabbed();
                if (pixels == null)
                    return null;

                // Both calculations read the original colour capture. The grey cover and pixel
                // layers never alias the raw screenshot, which is released after this method returns.
                byte[] grey = BuildGrey(pixels);
                int originX = Math.Clamp(position.X - x, 0, w - 1);
                int originY = Math.Clamp(position.Y - y, 0, h - 1);
                RefreshAnalysis analysis = RefreshAnalyzer.Build(
                    pixels, w, h, tile, position.X ^ position.Y ^ Environment.TickCount,
                    originX, originY);
                var blooms = new RefreshClusterBloom[analysis.Clusters.Length];
                Parallel.For(0, analysis.Clusters.Length, new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount),
                }, i =>
                {
                    RefreshCluster cluster = analysis.Clusters[i];
                    blooms[i] = new RefreshClusterBloom(
                        RefreshBloom.Build(analysis.Subject, w, h, cluster.Left, cluster.Top,
                            cluster.Width, cluster.Height, scale),
                        RefreshBloom.Build(analysis.Background, w, h, cluster.Left, cluster.Top,
                            cluster.Width, cluster.Height, scale),
                        RefreshBloom.Build(analysis.Boundary, w, h, cluster.Left, cluster.Top,
                            cluster.Width, cluster.Height, scale));
                });
                return new RefreshFrame
                {
                    Grey = grey,
                    Subject = analysis.Subject,
                    Background = analysis.Background,
                    Boundary = analysis.Boundary,
                    Bounds = new RectInt32(x, y, w, h),
                    OriginX = originX,
                    OriginY = originY,
                    TilePx = tile,
                    ClusterPx = analysis.ClusterPx,
                    Clusters = analysis.Clusters,
                    Blooms = blooms,
                };
            }
            catch (Exception)
            {
                // A failed capture or analysis must never leave the colour matrix active. The
                // release path treats null as a plain restore and closes the menu safely.
                return null;
            }
        }

        /// <summary>The same blend as ScreenGrade's matrix, so swapping the cover for live colour is invisible.</summary>
        private static byte[] BuildGrey(byte[] pixels)
        {
            var grey = new byte[pixels.Length];
            float s = ScreenGrade.Saturation, k = 1 - s;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                float b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                float luma = k * (ScreenGrade.LumR * r + ScreenGrade.LumG * g + ScreenGrade.LumB * b) + 0.5f;
                grey[i] = (byte)Math.Min(255f, luma + s * b);
                grey[i + 1] = (byte)Math.Min(255f, luma + s * g);
                grey[i + 2] = (byte)Math.Min(255f, luma + s * r);
                // BitBlt leaves alpha undefined; the cover has to be fully opaque.
                grey[i + 3] = 255;
            }
            return grey;
        }

        private static byte[]? Grab(int x, int y, int w, int h)
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            IntPtr mem = CreateCompatibleDC(screen);
            var info = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // top-down
                biPlanes = 1,
                biBitCount = 32,
            };
            IntPtr dib = CreateDIBSection(screen, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
            try
            {
                if (dib == IntPtr.Zero)
                    return null;
                IntPtr old = SelectObject(mem, dib);
                bool ok = BitBlt(mem, 0, 0, w, h, screen, x, y, SRCCOPY);
                SelectObject(mem, old);
                if (!ok)
                    return null;

                var pixels = new byte[checked(w * h * 4)];
                Marshal.Copy(bits, pixels, 0, pixels.Length);
                return pixels;
            }
            finally
            {
                if (dib != IntPtr.Zero) DeleteObject(dib);
                DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        private const int SRCCOPY = 0x00CC0020;
        private const int MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, int flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
        [DllImport("Shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, int usage,
            out IntPtr bits, IntPtr section, int offset);
        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    }

    internal readonly record struct RefreshClusterBloom(
        RefreshBloomImage? Subject, RefreshBloomImage? Background, RefreshBloomImage? Boundary);
}
