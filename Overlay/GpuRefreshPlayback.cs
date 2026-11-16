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
        private readonly nint _window;
        private int _targetRefreshRate = 60;
        private SpriteVisual? _visual;
        private CompositionSurfaceBrush? _brush;
        private ICompositionSurface? _surface;
        private XamlRoot? _xamlRoot;
        private readonly Stopwatch _clock = new();
        private Action? _failed;
        private long _lastFrameSlot = -1;
        private bool _disposed, _started, _subscribed, _attached, _hostEventsSubscribed;
        private bool _geometryPresentPending;
        private bool _ownsRenderer;
        private DurationSamples _callbackIntervals, _renderTimings, _presentTimings;
        private long _previousMeasuredCallback, _measuredCallbacks, _busyPresents;
        private int _callbackIntervalsOver25Ms, _callbackIntervalsOver33Ms;
        internal bool OwnsRenderer => _ownsRenderer;
        internal GpuRefreshRenderer Renderer => _renderer;
        internal int PresentedFrames { get; private set; }
        internal string AdapterName => _renderer.Device.AdapterName;
        // Diagnostic probes explicitly opt in; normal playback collects no timing samples.
        internal bool MeasureTimings { get; set; }
        internal object GetTimingDiagnostics() => new
        {
            Enabled = MeasureTimings, RenderingCallbacks = _measuredCallbacks,
            TargetRefreshRateHz = _targetRefreshRate,
            CallbackIntervals = _callbackIntervals.Snapshot(),
            CallbackIntervalsOver25Ms = _callbackIntervalsOver25Ms,
            CallbackIntervalsOver33Ms = _callbackIntervalsOver33Ms,
            RenderSubmission = _renderTimings.Snapshot(), PresentCall = _presentTimings.Snapshot(),
            BusyPresents = _busyPresents,
            Boundary = "UI callback intervals and submission wall times; no GPU completion wait, pixel readback or displayed-frame measurement.",
        };

        internal void ResetTimingDiagnostics()
        {
            _callbackIntervals = _renderTimings = _presentTimings = default;
            _previousMeasuredCallback = _measuredCallbacks = _busyPresents = 0;
            _callbackIntervalsOver25Ms = _callbackIntervalsOver33Ms = 0;
        }
        internal object GetMappingDiagnostic()
        {
            var parent = ElementCompositionPreview.GetElementVisual(_host);
            return new
            {
                HostWidth = _host.Width, HostHeight = _host.Height,
                _host.ActualWidth, _host.ActualHeight,
                RasterizationScale = _host.XamlRoot?.RasterizationScale,
                SwapChainRasterizationScale = _renderer.Device.SwapChainScale,
                PhysicalWidth = _host.ActualWidth * (_host.XamlRoot?.RasterizationScale ?? 1),
                PhysicalHeight = _host.ActualHeight * (_host.XamlRoot?.RasterizationScale ?? 1),
                ParentSize = parent.Size.ToString(), ParentScale = parent.Scale.ToString(),
                ParentTransform = parent.TransformMatrix.ToString(),
                VisualSize = _visual?.Size.ToString(),
                RelativeSize = _visual?.RelativeSizeAdjustment.ToString(),
                VisualScale = _visual?.Scale.ToString(), BrushStretch = _brush?.Stretch.ToString(),
                BrushScale = _brush?.Scale.ToString(),
            };
        }

        public GpuRefreshPlayback(GpuRefreshRenderer renderer, FrameworkElement host, bool ownsRenderer = true,
            nint window = 0)
        {
            _renderer = renderer; _host = host;
            _window = window;
            _ownsRenderer = ownsRenderer;
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
                _visual.Brush = _brush;
                _host.Loaded += OnHostLoaded;
                _host.Unloaded += OnHostUnloaded;
                _hostEventsSubscribed = true;
                SyncGeometry();
                // Establish the grey starting cover before the existing three-frame handoff.
                RenderFrame(0);
                if (PresentedFrames == 0)
                    throw new InvalidOperationException("The GPU starting cover could not be queued.");
                ElementCompositionPreview.SetElementChildVisual(host, _visual);
                _attached = true;
            }
            catch
            {
                StopWatchingGeometry();
                try { DetachSurface(); } catch { }
                throw;
            }
        }

        /// <summary>The release path transfers the already prepared frame before disposing it.</summary>
        internal void AdoptRendererOwnership()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _ownsRenderer = true;
        }

        public void Start(Action failed)
        {
            if (_disposed || _started) return;
            _started = true; _failed = failed;
            try { SyncGeometry(); }
            catch { failed(); return; }
            _clock.Start();
            CompositionTarget.Rendering += OnRendering;
            _subscribed = true;
        }

        private void SyncGeometry()
        {
            if (_disposed) return;
            int refreshRate = DisplayRefreshRate.ForWindow(_window);
            if (refreshRate != _targetRefreshRate)
            {
                _targetRefreshRate = refreshRate;
                _lastFrameSlot = -1;
            }
            XamlRoot? root = _host.XamlRoot;
            if (root != _xamlRoot)
            {
                StopWatchingRoot();
                _xamlRoot = root;
                if (root != null) root.Changed += OnRootChanged;
            }
            // The capture and swapchain use physical pixels. XAML/Composition use DIPs;
            // moving/resizing the full-screen cover may update this scale asynchronously.
            // The native swapchain also needs its own inverse-DPI matrix; brush stretching
            // alone does not resize pixels from a composition swapchain.
            double scale = root?.RasterizationScale ?? 1;
            bool matrixChanged = _renderer.Device.SetSwapChainScale(scale);
            double width = _renderer.Width / scale, height = _renderer.Height / scale;
            if (_host.Width != width) _host.Width = width;
            if (_host.Height != height) _host.Height = height;
            var size = new Vector2((float)width, (float)height);
            if (_visual != null && _visual.Size != size) _visual.Size = size;
            // DXGI applies a new matrix at the next Present. Reuse the current opaque
            // cover during the handoff so correcting DPI never waits for animation start.
            _geometryPresentPending |= matrixChanged;
            if (_geometryPresentPending && PresentedFrames > 0 && _renderer.Device.Present(_renderer.OutputTexture))
            {
                PresentedFrames++;
                _geometryPresentPending = false;
            }
        }

        private void OnHostLoaded(object sender, RoutedEventArgs args) => RefreshGeometry();
        private void OnHostUnloaded(object sender, RoutedEventArgs args) => StopWatchingRoot();
        private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => RefreshGeometry();

        private void RefreshGeometry()
        {
            if (_disposed) return;
            try { SyncGeometry(); }
            catch
            {
                Unsubscribe();
                _failed?.Invoke();
            }
        }

        private void StopWatchingRoot()
        {
            XamlRoot? root = _xamlRoot; _xamlRoot = null;
            if (root != null) TryClose(() => root.Changed -= OnRootChanged);
        }

        private void StopWatchingGeometry()
        {
            StopWatchingRoot();
            if (!_hostEventsSubscribed) return;
            TryClose(() => _host.Loaded -= OnHostLoaded);
            TryClose(() => _host.Unloaded -= OnHostUnloaded);
            _hostEventsSubscribed = false;
        }

        private void OnRendering(object? sender, object args)
        {
            if (_disposed) return;
            if (MeasureTimings)
            {
                long now = Stopwatch.GetTimestamp();
                _measuredCallbacks++;
                if (_previousMeasuredCallback != 0)
                {
                    double interval = Stopwatch.GetElapsedTime(_previousMeasuredCallback, now).TotalMilliseconds;
                    _callbackIntervals.Add(interval);
                    if (interval > 25) _callbackIntervalsOver25Ms++;
                    if (interval > 33) _callbackIntervalsOver33Ms++;
                }
                _previousMeasuredCallback = now;
            }
            try
            {
                double elapsed = _clock.Elapsed.TotalMilliseconds;
                if (elapsed >= _renderer.DurationMs)
                {
                    Unsubscribe();
                    return;
                }
                // Match the target monitor's nominal mode, bounded by actual Composition
                // callbacks. A late callback never queues simulation catch-up frames.
                long frameSlot = (long)Math.Floor((elapsed + .75) * _targetRefreshRate / 1000);
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
            long started = MeasureTimings ? Stopwatch.GetTimestamp() : 0;
            _renderer.Render(elapsedMs);
            if (started != 0)
            {
                _renderTimings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                started = Stopwatch.GetTimestamp();
            }
            bool presented = _renderer.Device.Present(_renderer.OutputTexture);
            if (started != 0)
            {
                _presentTimings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                if (!presented) _busyPresents++;
            }
            if (presented)
            {
                PresentedFrames++;
                _geometryPresentPending = false;
            }
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
            StopWatchingGeometry();
            _clock.Stop();
            try { DetachSurface(); }
            finally { if (_ownsRenderer) _renderer.Dispose(); }
        }
    }
}
