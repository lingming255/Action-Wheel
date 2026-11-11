using System;
using System.Diagnostics;
using System.Numerics;
using Action_Wheel.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace Action_Wheel.Overlay
{
    /// <summary>
    /// Presents a D3D11 premultiplied swapchain directly in the transparent menu. Composition
    /// owns display synchronization; each callback submits shaders without pixel readback.
    /// Construction, callbacks and disposal occur on the window's UI thread.
    /// </summary>
    internal sealed unsafe class GpuRefreshPlayback : IDisposable
    {
        private readonly GpuRefreshRenderer _renderer;
        private readonly FrameworkElement _host;
        private SpriteVisual? _visual;
        private CompositionSurfaceBrush? _brush;
        private ICompositionSurface? _surface;
        private readonly Stopwatch _clock = new();
        private Action? _failed;
        private long _lastFrameSlot = -1;
        private bool _disposed, _started, _subscribed, _attached;
        internal int PresentedFrames { get; private set; }
        internal string AdapterName => _renderer.Device.AdapterName;

        public GpuRefreshPlayback(GpuRefreshRenderer renderer, FrameworkElement host)
        {
            _renderer = renderer; _host = host;
            try
            {
                renderer.Device.CreateSwapChain(renderer.Width, renderer.Height);
                var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
                // Microsoft.UI has a different compositor interop ABI from Windows.UI.
                // ICompositorSwapChainInterop inherits CreateGraphicsDevice before its two
                // surface constructors (Microsoft.UI.Composition.Interop.h, Windows App SDK).
                nint native = ((WinRT.IWinRTObject)compositor).NativeObject.ThisPtr;
                nint interop = GpuRefreshDevice.Query(native, new Guid("FC084699-67D8-40E1-ADE7-08901D84FFDA"));
                nint surface = 0;
                try
                {
                    GpuRefreshDevice.Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)GpuRefreshDevice.V(interop, 5))(
                        interop, renderer.Device.SwapChain, &surface));
                    _surface = WinRT.MarshalInterface<ICompositionSurface>.FromAbi(surface);
                }
                finally { GpuRefreshDevice.Release(ref surface); GpuRefreshDevice.Release(ref interop); }
                _brush = compositor.CreateSurfaceBrush(_surface);
                _brush.Stretch = CompositionStretch.Fill;
                _brush.BitmapInterpolationMode = CompositionBitmapInterpolationMode.NearestNeighbor;
                _visual = compositor.CreateSpriteVisual();
                _visual.RelativeSizeAdjustment = Vector2.One;
                _visual.Brush = _brush;
                // Establish the grey starting cover before the existing three-frame handoff.
                RenderFrame(0);
                if (PresentedFrames == 0)
                    throw new InvalidOperationException("The GPU starting cover could not be queued.");
                ElementCompositionPreview.SetElementChildVisual(host, _visual);
                _attached = true;
            }
            catch
            {
                try { DetachSurface(); } catch { }
                throw;
            }
        }

        public void Start(Action failed)
        {
            if (_disposed || _started) return;
            _started = true; _failed = failed;
            _clock.Start();
            CompositionTarget.Rendering += OnRendering;
            _subscribed = true;
        }

        private void OnRendering(object? sender, object args)
        {
            if (_disposed) return;
            try
            {
                double elapsed = _clock.Elapsed.TotalMilliseconds;
                if (elapsed >= _renderer.DurationMs)
                {
                    Unsubscribe();
                    return;
                }
                // High-refresh monitors still submit at most 60 frames per second. A slow
                // callback evaluates the latest time; it never queues simulation catch-up.
                long frameSlot = (long)Math.Floor((elapsed + .75) * 60 / 1000);
                if (frameSlot <= _lastFrameSlot) return;
                _lastFrameSlot = frameSlot;
                RenderFrame(elapsed);
            }
            catch
            {
                Unsubscribe();
                if (!_disposed) _failed?.Invoke();
            }
        }

        /// <summary>The smoke path exercises the exact shader/presentation submission.</summary>
        internal void RenderFrame(double elapsedMs)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _renderer.Render(elapsedMs);
            if (_renderer.Device.Present(_renderer.OutputTexture)) PresentedFrames++;
        }

        private void Unsubscribe()
        {
            if (!_subscribed) return;
            CompositionTarget.Rendering -= OnRendering;
            _subscribed = false;
        }

        private void DetachSurface()
        {
            // The XAML host may already be closing. A decorative teardown must not interrupt
            // the menu's remaining Closed handlers, which restore the desktop colour matrix.
            if (_attached) TryClose(() => ElementCompositionPreview.SetElementChildVisual(_host, null));
            _attached = false;
            SpriteVisual? visual = _visual; _visual = null;
            if (visual != null) { TryClose(() => visual.Brush = null); TryClose(visual.Dispose); }
            CompositionSurfaceBrush? brush = _brush; _brush = null;
            if (brush != null) TryClose(brush.Dispose);
            // ICompositionSurface has no IClosable. Explicitly release this owned CsWinRT
            // reference after the brush, rather than retaining the swapchain until a GC.
            ICompositionSurface? surface = _surface; _surface = null;
            if (surface is WinRT.IWinRTObject native) TryClose(native.NativeObject.Dispose);
            else if (surface is IDisposable disposable) TryClose(disposable.Dispose);
        }

        private static void TryClose(Action close) { try { close(); } catch (Exception) { } }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Unsubscribe();
            _clock.Stop();
            try { DetachSurface(); }
            finally { _renderer.Dispose(); }
        }
    }
}
