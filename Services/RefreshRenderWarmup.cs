using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Action_Wheel.Services
{
    /// <summary>
    /// Warms the complete refresh render path once, without capturing or displaying anything.
    /// A shared completion task lets early gestures wait without competing with this work.
    /// </summary>
    internal static class RefreshRenderWarmup
    {
        private const int Width = 1280;
        private const int Height = 720;
        private const int MinimumFrames = 60;
        private const int MinimumDurationMs = 2000;
        private static readonly object Gate = new();
        private static readonly CancellationTokenSource Lifetime = new();
        private static Task? _completion;

        /// <summary>Starts or joins the same best-effort background warmup.</summary>
        public static Task EnsureAsync()
        {
            lock (Gate)
                return _completion ??= Task.Run(RunSafelyAsync);
        }

        /// <summary>Stops background work when the application exits.</summary>
        public static void Cancel() => Lifetime.Cancel();

        private static async Task RunSafelyAsync()
        {
            try
            {
                await RenderAsync(Lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Exiting must not turn this shared, decorative task into an unobserved fault.
            }
            catch (Exception)
            {
                // A warmup failure must not prevent the real capture/render path from trying.
            }
        }

        private static async Task RenderAsync(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            byte[] capture = CreateCapture(cancellation);
            RefreshDepthMap depth = CreateDepth();
            var renderer = new SpectrumOverlayRefreshRenderer(
                Width, Height, capture, depth, Width / 2, Height / 2);
            byte[] pixels = new byte[capture.Length];
            byte[] light = new byte[renderer.BloomWidth * renderer.BloomHeight * 4];
            var clock = Stopwatch.StartNew();
            int frames = 0;
            while (frames < MinimumFrames || clock.ElapsedMilliseconds < MinimumDurationMs)
            {
                cancellation.ThrowIfCancellationRequested();
                double begin = clock.Elapsed.TotalMilliseconds;
                // Use the lit part of the scan: zero-time frames alone miss character/Bloom work.
                double sampleMs = 600 + (frames % 40) * 35;
                renderer.Render(sampleMs, pixels, light, cancellation);
                frames++;
                // Tiered code optimization needs time to run; avoid a tight startup CPU burst.
                int delay = (int)Math.Max(1, Math.Ceiling(renderer.FrameIntervalMs
                    - (clock.Elapsed.TotalMilliseconds - begin)));
                await Task.Delay(delay, cancellation).ConfigureAwait(false);
            }
            // These synthetic buffers belong only to this invocation. The shared task has no
            // result or renderer fields, so completion does not retain a permanent warmup scene.
        }

        private static byte[] CreateCapture(CancellationToken cancellation)
        {
            var capture = new byte[Width * Height * 4];
            for (int y = 0; y < Height; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int x = 0; x < Width; x++)
                {
                    int pixel = (y * Width + x) * 4;
                    capture[pixel] = (byte)(30 + y * 140 / (Height - 1));
                    capture[pixel + 1] = (byte)(45 + x * 140 / (Width - 1));
                    capture[pixel + 2] = (byte)((x / 13 + y / 11) % 2 == 0 ? 80 : 175);
                    capture[pixel + 3] = 255;
                }
            }
            return capture;
        }

        private static RefreshDepthMap CreateDepth()
        {
            const int width = 128, height = 72;
            var values = new float[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    values[y * width + x] = (x + y) / (float)(width + height - 2);
            // Used only for discarded offscreen frames; real gestures still use learned depth.
            return new RefreshDepthMap(width, height, values);
        }
    }
}
