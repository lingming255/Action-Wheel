using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Action_Wheel.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Action_Wheel.Overlay
{
    /// <summary>
    /// One worker and one outstanding UI upload. Buffers are reused only after the upload
    /// finishes; rendering follows elapsed time, so a slow frame never extends the animation.
    /// </summary>
    internal sealed class RefreshPlayback : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly IRefreshRenderer _renderer;
        private readonly DispatcherQueue _dispatcher;
        private readonly WriteableBitmap _bitmap;
        private readonly Action _failed;
        private readonly byte[] _pixels;
        private readonly byte[] _light;
        private readonly Stream _pixelStream;
        private bool _disposed;
        private bool _started;

        public RefreshPlayback(RefreshFrame frame, DispatcherQueue dispatcher,
            WriteableBitmap bitmap, Action failed)
        {
            _renderer = frame.Renderer;
            _dispatcher = dispatcher;
            _bitmap = bitmap;
            _failed = failed;
            _pixels = new byte[frame.Grey.Length];
            _light = new byte[_renderer.BloomWidth * _renderer.BloomHeight * 4];
            _pixelStream = bitmap.PixelBuffer.AsStream();
        }

        public void Start()
        {
            if (_disposed || _started) return;
            _started = true;
            CancellationToken token = _cancellation.Token;
            var clock = Stopwatch.StartNew();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        double elapsed = clock.Elapsed.TotalMilliseconds;
                        if (elapsed >= _renderer.DurationMs)
                            return;
                        double begin = clock.Elapsed.TotalMilliseconds;
                        _renderer.Render(elapsed, _pixels, _light, token);
                        token.ThrowIfCancellationRequested();
                        var uploaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        if (!_dispatcher.TryEnqueue(() =>
                        {
                            try
                            {
                                if (!_disposed && !token.IsCancellationRequested
                                    && clock.Elapsed.TotalMilliseconds < _renderer.DurationMs)
                                {
                                    _pixelStream.Position = 0;
                                    _pixelStream.Write(_pixels, 0, _pixels.Length);
                                    _bitmap.Invalidate();
                                }
                                uploaded.TrySetResult();
                            }
                            catch (Exception error) { uploaded.TrySetException(error); }
                        }))
                            return;
                        await uploaded.Task.WaitAsync(token).ConfigureAwait(false);
                        // Respect the renderer's display budget. Elapsed-time renderers can
                        // skip directly to the latest state without extending playback.
                        int delay = (int)Math.Max(1, Math.Ceiling(_renderer.FrameIntervalMs
                            - (clock.Elapsed.TotalMilliseconds - begin)));
                        await Task.Delay(delay, token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception)
                {
                    // Decorative failure must close the overlay and restore desktop colour.
                    _dispatcher.TryEnqueue(() => { if (!_disposed) _failed(); });
                }
            }, token);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cancellation.Cancel();
            _pixelStream.Dispose();
        }
    }
}
