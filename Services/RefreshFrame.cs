using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics;

namespace Action_Wheel.Services
{
    /// <summary>
    /// Original monitor capture and learned depth for the uniform-block scan. Capture happens
    /// before <see cref="ScreenGrade"/> lowers saturation; inference prepares on the hold worker.
    /// </summary>
    internal sealed class RefreshFrame
    {
        /// <summary>Premultiplied BGRA cover graded exactly like <see cref="ScreenGrade"/>.</summary>
        public required byte[] Grey { get; init; }
        /// <summary>Renders native solid blocks, their bloom and the experimental ASCII light layer.</summary>
        public required SpectrumOverlayRefreshRenderer Renderer { get; init; }
        public required RectInt32 Bounds { get; init; }
        /// <summary>Wave origin relative to the captured monitor, in physical pixels.</summary>
        public int OriginX { get; private set; }
        public int OriginY { get; private set; }
        private bool _releasePrepared;

        /// <summary>
        /// UI thread, after capture completes and before playback. The first release freezes
        /// timing, so a duplicate completion cannot retime a renderer already owned by a worker.
        /// Keep the original capture monitor if a release crosses its edge.
        /// </summary>
        internal void PrepareRelease(PointInt32 release)
        {
            if (_releasePrepared) return;
            int originX = (int)Math.Clamp((long)release.X - Bounds.X, 0, Bounds.Width - 1);
            int originY = (int)Math.Clamp((long)release.Y - Bounds.Y, 0, Bounds.Height - 1);
            Renderer.SetOrigin(originX, originY);
            OriginX = originX; OriginY = originY;
            _releasePrepared = true;
        }

        /// <summary>
        /// Waits for the shared startup renderer warmup, then captures and builds on a worker.
        /// This keeps startup training frames from competing with a real gesture's playback.
        /// <paramref name="grabbed"/> runs as soon as the
        /// raw screen has been read, which is the earliest safe moment to lower saturation. It may
        /// run twice on failure paths, so callers must keep it idempotent.
        /// </summary>
        public static Task<RefreshFrame?> CaptureAsync(PointInt32 position, Action grabbed,
            CancellationToken cancellation = default)
            => Task.Run(async () =>
            {
                try
                {
                    // Cancel only this gesture's wait, never the process-wide training task.
                    await RefreshRenderWarmup.EnsureAsync().WaitAsync(cancellation).ConfigureAwait(false);
                    return Capture(position, grabbed, cancellation);
                }
                catch (OperationCanceledException) { return null; }
                finally { grabbed(); }
            });

        private static RefreshFrame? Capture(PointInt32 position, Action grabbed, CancellationToken cancellation)
        {
            try
            {
                cancellation.ThrowIfCancellationRequested();
                var monitor = MonitorFromPoint(new POINT { x = position.X, y = position.Y }, MONITOR_DEFAULTTONEAREST);
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (!GetMonitorInfo(monitor, ref mi))
                    return null;

                int x = mi.rcMonitor.left, y = mi.rcMonitor.top;
                int w = mi.rcMonitor.right - x, h = mi.rcMonitor.bottom - y;

                byte[]? pixels = Grab(x, y, w, h);
                grabbed();
                if (pixels == null)
                    return null;
                cancellation.ThrowIfCancellationRequested();

                // Preserve the raw capture for depth and capture-relative light/LUT correction.
                // The hold's grey cover is only the initial handoff, not a radial reveal layer.
                byte[] grey = BuildGrey(pixels);
                int originX = Math.Clamp(position.X - x, 0, w - 1);
                int originY = Math.Clamp(position.Y - y, 0, h - 1);
                cancellation.ThrowIfCancellationRequested();
                RefreshDepthMap depth = RefreshDepthEstimator.Estimate(pixels, w, h);
                cancellation.ThrowIfCancellationRequested();
                return new RefreshFrame
                {
                    Grey = grey,
                    Renderer = new SpectrumOverlayRefreshRenderer(w, h, pixels, depth, originX, originY),
                    Bounds = new RectInt32(x, y, w, h),
                    OriginX = originX,
                    OriginY = originY,
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

}
