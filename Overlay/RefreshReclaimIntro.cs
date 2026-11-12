using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;
using Color = Windows.UI.Color;

namespace Action_Wheel.Overlay
{
    /// <summary>
    /// A short, independent decoration behind the interactive wheel, promoted above outward
    /// playback when released early. Stationary blocks pulse from the monitor's outside towards
    /// the pointer; no desktop pixels move or get rescaled.
    /// All methods belong to the menu's UI thread. Dispose also completes a waiting release.
    /// </summary>
    internal sealed class RefreshReclaimIntro : IDisposable
    {
        internal const double DurationMs = ReclaimAnimationSettings.DefaultDurationMs;
        private const int MaximumBlocks = 600;
        private const int TimingBands = 40;
        // Exactly one idle bundle is retained. Each playback gets a fresh lease object, so an
        // old outward/Closed callback can never dispose a later gesture using the same HWND.
        private static RefreshReclaimIntro? _preparedCache;
        private static int _cacheGeneration, _activeCacheLeases;
        private readonly int _generation;
        private readonly ReclaimAnimationSettings _profile;
        private readonly nint _monitor;
        private readonly uint _preparedDpi;
        private readonly ContainerVisual[] _bands = new ContainerVisual[TimingBands];
        private readonly ScalarKeyFrameAnimation[] _pulses = new ScalarKeyFrameAnimation[TimingBands];
        private bool _cacheEligible, _ownsResources = true, _countedLease;
        private double _clearRadius;
        private readonly double _durationMs, _pulseMs;
        private readonly double _densityScale;
        private readonly int _maximumBlocks;
        private readonly float _glowOpacity;
        private readonly Window _window;
        private readonly Grid _host;
        private readonly RectInt32 _bounds;
        private readonly PointInt32 _origin;
        private readonly nint _menuHwnd;
        private readonly nint _hwnd;
        private readonly bool _hidden;
        private readonly bool _captureExcluded;
        private readonly NativeSubclass _backgroundSubclass;
        private readonly List<IDisposable> _resources = new();
        private readonly List<BlockVisual> _blocks = new();
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Stopwatch _clock = new();
        private readonly DispatcherQueueTimer? _finishTimer;
        private ContainerVisual? _visual;
        private ContainerVisual? _sourceVisual;
        private CompositionVisualSurface? _sourceSurface;
        private CompositionSurfaceBrush? _sourceBrush;
        private CompositionEffectBrush? _gradeBrush;
        private XamlRoot? _xamlRoot;
        private nint _backgroundBrush;
        private bool _disposed, _shown, _promotedForPlayback;

        private sealed record BlockVisual(int X, int Y, CompositionColorBrush Brush, DropShadow Shadow, SpriteVisual Visual);

        internal Task Completion => _completion.Task;
        internal bool AlreadyCompleted => _disposed || _completion.Task.IsCompleted;
        internal double ElapsedMs => _clock.Elapsed.TotalMilliseconds;
        internal double RemainingMs => _disposed ? 0 : Math.Max(0, _durationMs - ElapsedMs);
        internal nint WindowHandle => _hwnd;

        /// <summary>Grades the whole animated pulse with two GPU coefficients, never per-block writes.</summary>
        internal void ApplySaturation(float saturation)
        {
            if (_disposed || _gradeBrush == null) return;
            if (!float.IsFinite(saturation)) throw new ArgumentOutOfRangeException(nameof(saturation));
            saturation = Math.Clamp(saturation, 0, 1);
            if (_promotedForPlayback && saturation < 1) return;
            _gradeBrush.Properties.InsertScalar("ReclaimGrade.Source1Amount", saturation);
            _gradeBrush.Properties.InsertScalar("ReclaimGrade.Source2Amount", 1 - saturation);
        }

        /// <summary>
        /// Keeps an unfinished inward pulse visible above the outward playback's opaque cover.
        /// Ownership may pass from the held gesture to that playback window; its existing clock,
        /// fixed geometry and natural completion remain unchanged. A hidden probe stays hidden.
        /// </summary>
        internal bool PromoteForPlayback()
        {
            if (AlreadyCompleted) return false;
            try
            {
                ApplySaturation(1);
                RefreshInputTransparency.Enable(_hwnd);
                if (!SetWindowPos(_hwnd, new nint(-1), 0, 0, 0, 0, 0x13)) // NOACTIVATE | NOMOVE | NOSIZE
                    throw new InvalidOperationException("The inward pulse could not remain above outward playback.");
                _promotedForPlayback = true;
                return true;
            }
            catch
            {
                // A decorative z-order failure must not cancel outward playback or leave an
                // orphaned overlay. Disposing also releases any observer awaiting completion.
                Dispose();
                return false;
            }
        }

        internal static RefreshReclaimIntro? TryStart(PointInt32 origin, nint menuHwnd,
            ReclaimAnimationSettings? settings = null)
        {
            RefreshReclaimIntro? intro = null;
            try
            {
                if (!TryResolve(origin, out var bounds, out nint monitor)) return null;
                var profile = (settings ?? ReclaimAnimationSettings.Default).Normalised();
                var prepared = _preparedCache;
                _preparedCache = null;
                if (prepared != null && prepared.Matches(bounds, monitor, profile))
                    intro = new RefreshReclaimIntro(prepared, origin, menuHwnd);
                else
                {
                    prepared?.ReleaseResources();
                    intro = new RefreshReclaimIntro(bounds, origin, menuHwnd, false, profile);
                }
                return intro;
            }
            catch
            {
                intro?.Dispose();
                return null;
            }
        }

        /// <summary>UI-thread idle work only: prepares one hidden bundle without starting any scan.</summary>
        internal static bool WarmAt(PointInt32 origin, ReclaimAnimationSettings? settings = null)
        {
            // Never build a second speculative bundle while a gesture/outward pulse owns one.
            if (_activeCacheLeases != 0) return false;
            try
            {
                if (!TryResolve(origin, out var bounds, out nint monitor)) return false;
                var profile = (settings ?? ReclaimAnimationSettings.Default).Normalised();
                if (_preparedCache?.Matches(bounds, monitor, profile) == true) return true;
                ClearCache();
                _preparedCache = new RefreshReclaimIntro(bounds, origin, 0, false, profile, prepareOnly: true);
                return true;
            }
            catch { ClearCache(); return false; }
        }

        /// <summary>Releases idle resources and prevents already-active older leases from recaching.</summary>
        internal static void ClearCache()
        {
            _cacheGeneration++;
            var prepared = _preparedCache;
            _preparedCache = null;
            prepared?.ReleaseResources();
        }

        private static bool TryResolve(PointInt32 origin, out RectInt32 bounds, out nint monitor)
        {
            monitor = MonitorFromPoint(new NativePoint(origin.X, origin.Y), 2);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            bool found = GetMonitorInfo(monitor, ref info);
            bounds = found ? new RectInt32(info.Monitor.Left, info.Monitor.Top,
                info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top) : default;
            return found;
        }

        private bool Matches(RectInt32 bounds, nint monitor, ReclaimAnimationSettings profile)
            => _ownsResources && _disposed && _generation == _cacheGeneration && _bounds.Equals(bounds)
            && _monitor == monitor && _profile == profile && IsWindow(_hwnd)
            && _preparedDpi != 0 && GetDpiForWindow(_hwnd) == _preparedDpi
            && GetClientRect(_hwnd, out var client) && client.Right == bounds.Width && client.Bottom == bounds.Height
            && GetWindowDisplayAffinity(_hwnd, out uint affinity) && affinity == 0x11;

        /// <summary>Creates only a never-shown synthetic decoration for integration diagnostics.</summary>
        internal static RefreshReclaimIntro CreateHiddenForProbe(RectInt32 bounds, PointInt32 origin, nint menuHwnd,
            ReclaimAnimationSettings? settings = null)
            => new(bounds, origin, menuHwnd, hidden: true, settings: settings);

        private RefreshReclaimIntro(RectInt32 bounds, PointInt32 origin, nint menuHwnd, bool hidden,
            ReclaimAnimationSettings? settings = null, bool prepareOnly = false)
        {
            var profile = (settings ?? ReclaimAnimationSettings.Default).Normalised();
            _profile = profile;
            _generation = _cacheGeneration;
            _cacheEligible = !hidden;
            _monitor = MonitorFromPoint(new NativePoint(origin.X, origin.Y), 2);
            _durationMs = profile.DurationMs;
            _pulseMs = profile.PixelDurationMs;
            _maximumBlocks = (int)Math.Round(MaximumBlocks * profile.DensityPercent / 100);
            _densityScale = Math.Sqrt(profile.DensityPercent / 100);
            _glowOpacity = (float)(.48 * profile.GlowPercent / 100);
            _bounds = bounds; _origin = origin; _menuHwnd = menuHwnd; _hidden = hidden;
            _host = new Grid { Background = new SolidColorBrush(Colors.Transparent), IsHitTestVisible = false };
            _backgroundSubclass = ClearNativeBackground;
            _window = new Window();
            try
            {
                // Once a Window exists every following step is covered by the same cleanup,
                // including partial timer/HWND initialization before a constructor can return.
                _window.Content = _host;
                _window.SystemBackdrop = new RadialMenu.TransparentSystemBackdrop();
                _hwnd = WindowNative.GetWindowHandle(_window);
                _finishTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
                _finishTimer.IsRepeating = false;
                _finishTimer.Interval = TimeSpan.FromMilliseconds(_durationMs);
                _finishTimer.Tick += Finish;
                _window.Closed += OnClosed;
                _host.Loaded += OnLoaded;
                _window.AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                ConfigureNativeWindow();
                _captureExcluded = SetWindowDisplayAffinity(_hwnd, 0x11)
                    && GetWindowDisplayAffinity(_hwnd, out uint affinity) && affinity == 0x11;
                _preparedDpi = GetDpiForWindow(_hwnd);
                BuildVisuals();
                if (prepareOnly)
                {
                    if (!_captureExcluded) throw new InvalidOperationException("Prepared reclaim resources must retain capture exclusion.");
                    _disposed = true;
                    StopAndHide();
                    _completion.TrySetResult();
                }
                else StartLease(resetPigment: false);
            }
            catch
            {
                _cacheEligible = false;
                Dispose();
                ReleaseResources();
                throw;
            }
        }

        private RefreshReclaimIntro(RefreshReclaimIntro prepared, PointInt32 origin, nint menuHwnd)
        {
            _profile = prepared._profile; _generation = _cacheGeneration; _cacheEligible = true;
            _durationMs = prepared._durationMs; _pulseMs = prepared._pulseMs;
            _densityScale = prepared._densityScale; _maximumBlocks = prepared._maximumBlocks;
            _glowOpacity = prepared._glowOpacity; _clearRadius = prepared._clearRadius;
            _bounds = prepared._bounds; _origin = origin; _menuHwnd = menuHwnd; _hidden = prepared._hidden;
            _monitor = prepared._monitor; _preparedDpi = prepared._preparedDpi;
            _window = prepared._window; _host = prepared._host; _hwnd = prepared._hwnd;
            _captureExcluded = prepared._captureExcluded; _backgroundBrush = prepared._backgroundBrush;
            _backgroundSubclass = ClearNativeBackground;
            _resources = prepared._resources; _blocks = prepared._blocks;
            _bands = prepared._bands; _pulses = prepared._pulses;
            _visual = prepared._visual; _sourceVisual = prepared._sourceVisual;
            _sourceSurface = prepared._sourceSurface; _sourceBrush = prepared._sourceBrush; _gradeBrush = prepared._gradeBrush;
            // Transfer sole ownership before any step that could throw; old callbacks stay inert.
            prepared._ownsResources = false;
            prepared._backgroundBrush = 0;
            try
            {
                _finishTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
                _finishTimer.IsRepeating = false;
                _finishTimer.Interval = TimeSpan.FromMilliseconds(_durationMs);
                _finishTimer.Tick += Finish;
                _window.Closed += OnClosed;
                _host.Loaded += OnLoaded;
                if (!SetWindowSubclass(_hwnd, _backgroundSubclass, new UIntPtr(91), UIntPtr.Zero))
                    throw new InvalidOperationException("The cached reclaim window could not bind its new owner.");
                SyncScale();
                StartLease(resetPigment: false);
            }
            catch { _cacheEligible = false; Dispose(); throw; }
        }

        private void StartLease(bool resetPigment)
        {
            if (_visual != null) _visual.Offset = Vector3.Zero;
            RetargetBlocks(resetPigment);
            ApplySaturation(1);
            for (int band = 0; band < TimingBands; band++)
                _bands[band].StartAnimation("Opacity", _pulses[band]);
            _clock.Restart();
            _finishTimer!.Start();
            if (_cacheEligible) { _activeCacheLeases++; _countedLease = true; }
            // Capture-exclusion failure retains the original delayed-show fallback.
            if (_captureExcluded) Show();
        }

        private void ConfigureNativeWindow()
        {
            StripFrame();
            RefreshInputTransparency.Enable(_hwnd);
            int corners = 1, border = unchecked((int)0xFFFFFFFE);
            DwmSetWindowAttribute(_hwnd, 33, ref corners, sizeof(int));
            DwmSetWindowAttribute(_hwnd, 34, ref border, sizeof(int));
            var blur = new BlurBehind { Flags = 1, Enable = true };
            DwmEnableBlurBehindWindow(_hwnd, ref blur);
            _backgroundBrush = CreateSolidBrush(0);
            if (!SetWindowSubclass(_hwnd, _backgroundSubclass, new UIntPtr(91), UIntPtr.Zero))
                throw new InvalidOperationException("The reclaim decoration could not prepare its transparent background.");
            // AppWindow dimensions and bounds are physical. The visual's inverse-DPI scale is
            // applied separately, so monitor placement never changes the wheel's own geometry.
            if (!SetWindowPos(_hwnd, new nint(-1), _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, 0x30))
                throw new InvalidOperationException("The reclaim decoration could not match its monitor bounds.");
            PaintNativeBackground();
        }

        private void StripFrame()
        {
            int style = GetWindowLong(_hwnd, -16);
            SetWindowLong(_hwnd, -16, style & ~0x00C40000); // CAPTION | THICKFRAME
            int exStyle = GetWindowLong(_hwnd, -20);
            SetWindowLong(_hwnd, -20, (exStyle & ~0x00020301) | 0x08000080); // remove frame edges; TOOLWINDOW | NOACTIVATE
        }

        private void Show(bool allowHiddenOffscreen = false)
        {
            if ((_hidden && !allowHiddenOffscreen) || _shown || _disposed) return;
            _window.AppWindow.Show(false);
            _shown = true;
            // WinUI's first show can restore nonclient frame bits. Strip them again before
            // applying physical bounds, just as the interactive wheel's ShowAtPosition does.
            StripFrame();
            RefreshInputTransparency.Enable(_hwnd);
            // Stay behind the interactive menu while held. Release may promote this same
            // unfinished decoration above the fullscreen playback's opaque starting cover.
            nint insertAfter = !_promotedForPlayback && _menuHwnd != 0 ? _menuHwnd : new nint(-1);
            if (!SetWindowPos(_hwnd, insertAfter,
                _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, 0x30))
                throw new InvalidOperationException("The reclaim decoration could not keep its presentation order.");
            PaintNativeBackground();
            SyncScale();
        }

        private void BuildVisuals()
        {
            var compositor = ElementCompositionPreview.GetElementVisual(_host).Compositor;
            var visual = compositor.CreateContainerVisual();
            _sourceVisual = visual;
            _resources.Add(visual);
            visual.Size = new Vector2(_bounds.Width, _bounds.Height);
            var bands = _bands;
            for (int band = 0; band < bands.Length; band++)
            {
                var container = compositor.CreateContainerVisual();
                container.Opacity = 0;
                bands[band] = container;
                visual.Children.InsertAtTop(container);
                _resources.Add(container);
            }

            int shorter = Math.Min(_bounds.Width, _bounds.Height);
            int cell = Math.Max(6, 2 * (int)Math.Floor(.0181 * shorter / 2));
            double step = Math.Max(cell * 3.0 / _densityScale,
                Math.Sqrt((double)_bounds.Width * _bounds.Height / _maximumBlocks));
            int columns = Math.Max(1, (int)Math.Floor(_bounds.Width / step));
            int rows = Math.Max(1, (int)Math.Floor(_bounds.Height / step));
            _clearRadius = cell * 1.7;
            for (int row = 0; row < rows; row++)
            for (int column = 0; column < columns; column++)
            {
                int x = (int)Math.Round((column + .5) * _bounds.Width / columns);
                int y = (int)Math.Round((row + .5) * _bounds.Height / rows);
                if (_blocks.Count >= _maximumBlocks) continue;
                // Tint is an immediate placeholder. The raw capture can replace pigment later
                // without touching either this fixed position or the already-running scan.
                Color colour = Color.FromArgb(255, 75, 132, 157);
                var brush = compositor.CreateColorBrush(colour);
                var shadow = compositor.CreateDropShadow();
                shadow.Color = colour;
                shadow.BlurRadius = cell * .65f;
                shadow.Opacity = _glowOpacity;
                shadow.Offset = Vector3.Zero;
                var block = compositor.CreateSpriteVisual();
                block.Size = new Vector2(cell * .72f);
                block.Offset = new Vector3(x - block.Size.X / 2, y - block.Size.Y / 2, 0);
                block.Brush = brush;
                block.Shadow = shadow;
                bands[0].Children.InsertAtTop(block);
                _blocks.Add(new BlockVisual(x, y, brush, shadow, block));
                _resources.Add(brush); _resources.Add(shadow); _resources.Add(block);
            }

            // A few blocks share each opacity clock. Composition handles the scan off-thread;
            // there is no per-frame UI loop, position interpolation or desktop screenshot.
            for (int band = 0; band < bands.Length; band++)
            {
                var pulse = compositor.CreateScalarKeyFrameAnimation();
                pulse.InsertKeyFrame(0, 0);
                pulse.InsertKeyFrame(.22f, .68f);
                pulse.InsertKeyFrame(.5f, .4f);
                pulse.InsertKeyFrame(1, 0);
                pulse.Duration = TimeSpan.FromMilliseconds(_pulseMs);
                pulse.DelayTime = TimeSpan.FromMilliseconds(band / (double)(TimingBands - 1) * (_durationMs - _pulseMs));
                pulse.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
                _pulses[band] = pulse;
                _resources.Add(pulse);
            }
            // Capture only our animated child tree, including its native opacity clocks and
            // shadows. The source stays in physical pixels with unit scale; inverse DPI belongs
            // solely to the displayed outer container, otherwise the surface is scaled twice.
            _sourceSurface = compositor.CreateVisualSurface();
            _resources.Add(_sourceSurface);
            _sourceSurface.SourceVisual = visual;
            _sourceSurface.SourceSize = visual.Size;
            _sourceBrush = compositor.CreateSurfaceBrush(_sourceSurface);
            _resources.Add(_sourceBrush);
            _sourceBrush.Stretch = CompositionStretch.None;
            _sourceBrush.HorizontalAlignmentRatio = 0;
            _sourceBrush.VerticalAlignmentRatio = 0;
            var gray = new ColorMatrixEffect
            {
                ColorMatrix = new Matrix5x4
                {
                    M11 = .30f, M12 = .30f, M13 = .30f,
                    M21 = .59f, M22 = .59f, M23 = .59f,
                    M31 = .11f, M32 = .11f, M33 = .11f, M44 = 1,
                },
                ClampOutput = true,
                Source = new CompositionEffectSourceParameter("pulse"),
            };
            _resources.Add(gray);
            var blend = new ArithmeticCompositeEffect
            {
                Name = "ReclaimGrade", Source1 = new CompositionEffectSourceParameter("pulse"), Source2 = gray,
                MultiplyAmount = 0, Source1Amount = 1, Source2Amount = 0, Offset = 0, ClampOutput = true,
            };
            _resources.Add(blend);
            var factory = compositor.CreateEffectFactory(blend,
                new[] { "ReclaimGrade.Source1Amount", "ReclaimGrade.Source2Amount" });
            _resources.Add(factory);
            _gradeBrush = factory.CreateBrush();
            _resources.Add(_gradeBrush);
            _gradeBrush.SetSourceParameter("pulse", _sourceBrush);
            ApplySaturation(1);
            var output = compositor.CreateSpriteVisual();
            _resources.Add(output);
            output.Size = visual.Size;
            output.Brush = _gradeBrush;
            _visual = compositor.CreateContainerVisual();
            _resources.Add(_visual);
            _visual.Size = visual.Size;
            _visual.Children.InsertAtTop(output);
            ElementCompositionPreview.SetElementChildVisual(_host, _visual);
            SyncScale();
        }

        private void RetargetBlocks(bool resetPigment)
        {
            foreach (var band in _bands)
            {
                band.StopAnimation("Opacity");
                band.Opacity = 0;
                band.Children.RemoveAll();
            }
            double originX = _origin.X - _bounds.X, originY = _origin.Y - _bounds.Y;
            double dx = Math.Max(originX, _bounds.Width - 1 - originX);
            double dy = Math.Max(originY, _bounds.Height - 1 - originY);
            double farthest = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
            Color placeholder = Color.FromArgb(255, 75, 132, 157);
            foreach (var block in _blocks)
            {
                double ox = block.X - originX, oy = block.Y - originY;
                double distance = Math.Sqrt(ox * ox + oy * oy);
                int band = Math.Clamp((int)Math.Floor((1 - Math.Min(1, distance / farthest)) * (TimingBands - 1)), 0, TimingBands - 1);
                block.Visual.Opacity = distance < _clearRadius ? 0 : 1;
                _bands[band].Children.InsertAtTop(block.Visual);
                if (resetPigment) { block.Brush.Color = placeholder; block.Shadow.Color = placeholder; }
            }
        }

        /// <summary>Applies original desktop pigment after the raw grab, without restarting animation.</summary>
        internal void UpdateCapture(byte[] bgra, RectInt32 bounds)
        {
            if (_disposed) return;
            ArgumentNullException.ThrowIfNull(bgra);
            if (bounds.Width <= 0 || bounds.Height <= 0 || bgra.LongLength != (long)bounds.Width * bounds.Height * 4)
                throw new ArgumentException("Reclaim pigment needs a complete BGRA capture.", nameof(bgra));
            if (bounds.X != _bounds.X || bounds.Y != _bounds.Y
                || bounds.Width != _bounds.Width || bounds.Height != _bounds.Height) return;
            foreach (BlockVisual block in _blocks)
            {
                int x = Math.Clamp(block.X, 0, bounds.Width - 1), y = Math.Clamp(block.Y, 0, bounds.Height - 1);
                int p = (y * bounds.Width + x) * 4;
                float red = bgra[p + 2], green = bgra[p + 1], blue = bgra[p];
                float maximum = Math.Max(red, Math.Max(green, blue));
                float gain = maximum > 0 && maximum < 150 ? 150 / maximum : 1;
                Color colour = Color.FromArgb(255, (byte)Math.Min(255, red * gain),
                    (byte)Math.Min(255, green * gain), (byte)Math.Min(255, blue * gain));
                block.Brush.Color = colour;
                block.Shadow.Color = colour;
            }
            if (!_captureExcluded) Show();
        }

        private void OnLoaded(object sender, RoutedEventArgs args) => SyncScale();
        private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SyncScale();

        private void SyncScale()
        {
            if (_disposed || _visual == null) return;
            XamlRoot? root = _host.XamlRoot;
            if (!ReferenceEquals(root, _xamlRoot))
            {
                if (_xamlRoot != null) _xamlRoot.Changed -= OnRootChanged;
                _xamlRoot = root;
                if (root != null) root.Changed += OnRootChanged;
            }
            double scale = root?.RasterizationScale ?? Math.Max(1, GetDpiForWindow(_hwnd) / 96.0);
            _visual.Scale = new Vector3((float)(1 / scale), (float)(1 / scale), 1);
        }

        private void Finish(DispatcherQueueTimer sender, object args) => Dispose();
        private void OnClosed(object sender, WindowEventArgs args)
        {
            _cacheEligible = false; // A Window already entering Closed is never a reusable bundle.
            Dispose();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopAndHide();
            _completion.TrySetResult();
            try
            {
                if (_cacheEligible && _ownsResources && _generation == _cacheGeneration
                    && TryResolve(_origin, out var bounds, out nint monitor) && Matches(bounds, monitor, _profile))
                {
                    // A promoted pulse reaches this path only on its own finish/cancellation.
                    if (_preparedCache == null)
                    {
                        // Reset while hidden, outside the next Show critical path.
                        Color placeholder = Color.FromArgb(255, 75, 132, 157);
                        foreach (var block in _blocks) { block.Brush.Color = placeholder; block.Shadow.Color = placeholder; }
                        _preparedCache = this;
                        return;
                    }
                }
            }
            catch { /* A lost device invalidates the prepared bundle, never the gesture cleanup. */ }
            ReleaseResources();
        }

        private void StopAndHide()
        {
            _clock.Stop();
            _finishTimer?.Stop();
            if (_finishTimer != null) _finishTimer.Tick -= Finish;
            // Hide before detaching the transparent backdrop; its final default clear must never
            // become a visible monitor-sized flash. Cancellation is also a successful completion.
            if (_hwnd != 0) ShowWindow(_hwnd, 0);
            _window.Closed -= OnClosed;
            _host.Loaded -= OnLoaded;
            if (_xamlRoot != null) _xamlRoot.Changed -= OnRootChanged;
            _xamlRoot = null;
            if (_hwnd != 0) RemoveWindowSubclass(_hwnd, _backgroundSubclass, new UIntPtr(91));
            foreach (var band in _bands)
            {
                if (band == null) continue;
                try { band.StopAnimation("Opacity"); band.Opacity = 0; } catch { }
            }
            _shown = false;
            if (_countedLease) { _countedLease = false; _activeCacheLeases--; }
        }

        private void ReleaseResources()
        {
            if (!_ownsResources) return;
            _ownsResources = false;
            _disposed = true;
            StopAndHide();
            try { ElementCompositionPreview.SetElementChildVisual(_host, null); } catch { }
            for (int index = _resources.Count - 1; index >= 0; index--)
            {
                try { _resources[index].Dispose(); } catch { }
            }
            _resources.Clear(); _blocks.Clear(); _visual = null;
            _sourceVisual = null; _sourceSurface = null; _sourceBrush = null; _gradeBrush = null;
            if (_backgroundBrush != 0) DeleteObject(_backgroundBrush);
            _backgroundBrush = 0;
            try { _window.Close(); } catch { }
            _completion.TrySetResult();
        }

        private nint ClearNativeBackground(nint hwnd, uint message, nint wParam, nint lParam, UIntPtr id, UIntPtr data)
        {
            if (message == 0x14 && wParam != 0 && _backgroundBrush != 0 && GetClientRect(hwnd, out NativeRect rect))
            {
                FillRect(wParam, ref rect, _backgroundBrush);
                return 1;
            }
            return DefSubclassProc(hwnd, message, wParam, lParam);
        }

        private void PaintNativeBackground()
        {
            nint dc = GetDC(_hwnd);
            if (dc == 0) return;
            try { if (GetClientRect(_hwnd, out NativeRect rect)) FillRect(dc, ref rect, _backgroundBrush); }
            finally { ReleaseDC(_hwnd, dc); }
        }

        /// <summary>Synthetic, opt-in lifecycle check. Neither test window is ever shown.</summary>
        internal static async Task<object> RunHiddenProbeAsync(bool verifyOffscreenShow = false)
        {
            var bounds = new RectInt32(-20000, -20000, 1280, 720);
            var origin = new PointInt32(bounds.X + 640, bounds.Y + 360);
            var creation = Stopwatch.StartNew();
            using var intro = new RefreshReclaimIntro(bounds, origin, 0, true);
            creation.Stop();
            Require(!IsWindowVisible(intro._hwnd), "The hidden reclaim probe became visible.");
            int exStyle = GetWindowLong(intro._hwnd, -20);
            Require((exStyle & 0x08080020) == 0x08080020, "The reclaim decoration intercepts input or activates.");
            Require(intro._captureExcluded, "The reclaim decoration is not excluded from capture.");
            Require(GetClientRect(intro._hwnd, out NativeRect rect)
                && rect.Right - rect.Left == bounds.Width && rect.Bottom - rect.Top == bounds.Height,
                "The reclaim window changed physical monitor dimensions.");
            Require(intro._blocks.Count is > 0 and <= MaximumBlocks && intro._visual != null,
                "The reclaim decoration exceeded its fixed block budget.");
            float expectedScale = (float)(1 / (intro._host.XamlRoot?.RasterizationScale
                ?? Math.Max(1, GetDpiForWindow(intro._hwnd) / 96.0)));
            Require(Math.Abs(intro._visual!.Scale.X - expectedScale) < .00001,
                "The reclaim decoration did not map physical pixels through inverse DPI.");
            int count = intro._blocks.Count;
            var pixels = new byte[bounds.Width * bounds.Height * 4];
            for (int p = 0; p < pixels.Length; p += 4) { pixels[p] = 20; pixels[p + 1] = 100; pixels[p + 2] = 200; }
            intro.UpdateCapture(pixels, bounds);
            Require(intro._blocks.Count == count && intro._blocks.TrueForAll(block =>
                block.Brush.Color.R == 200 && block.Brush.Color.G == 100 && block.Brush.Color.B == 20
                && block.Brush.Color.Equals(block.Shadow.Color)), "The reclaim pigment differs from its original capture or glow.");
            Require(intro._sourceVisual != null && intro._sourceVisual.Scale == Vector3.One
                && intro._sourceSurface?.SourceVisual == intro._sourceVisual
                && intro._sourceSurface.SourceSize == new Vector2(bounds.Width, bounds.Height)
                && intro._sourceBrush?.Surface is CompositionVisualSurface sourceSurface
                && sourceSurface == intro._sourceSurface
                && intro._gradeBrush?.GetSourceParameter("pulse") == intro._sourceBrush
                && intro._visual!.Children.Count == 1,
                "The reclaim grade lost its animated source, effect chain or unit physical source scale.");
            foreach (float saturation in new[] { 0f, .5f, 1f })
            {
                intro.ApplySaturation(saturation);
                Require(intro._gradeBrush!.Properties.TryGetScalar("ReclaimGrade.Source1Amount", out float colorWeight)
                    == CompositionGetValueStatus.Succeeded && colorWeight == saturation
                    && intro._gradeBrush.Properties.TryGetScalar("ReclaimGrade.Source2Amount", out float grayWeight)
                    == CompositionGetValueStatus.Succeeded && grayWeight == 1 - saturation
                    && intro._blocks.TrueForAll(block => block.Brush.Color.R == 200
                        && block.Brush.Color.G == 100 && block.Brush.Color.B == 20),
                    "The reclaim grade changed native pigment instead of its two GPU coefficients.");
            }
            intro.ApplySaturation(0);
            Require(intro.PromoteForPlayback() && !intro.AlreadyCompleted && !IsWindowVisible(intro._hwnd)
                && intro._blocks.Count == count && !intro.Completion.IsCompleted,
                "Promoting a hidden inward pulse showed, completed or rebuilt it.");
            intro.ApplySaturation(0);
            Require(intro._gradeBrush!.Properties.TryGetScalar("ReclaimGrade.Source1Amount", out float promotedWeight)
                == CompositionGetValueStatus.Succeeded && promotedWeight == 1,
                "A late held-grade tick recolored the promoted inward pulse.");
            bool rejected = false;
            try { intro.UpdateCapture(new byte[3], bounds); } catch (ArgumentException) { rejected = true; }
            Require(rejected && !IsWindowVisible(intro._hwnd), "Invalid pigment was accepted or a hidden update showed the window.");
            Task completion = intro.Completion;
            Require(await Task.WhenAny(completion, Task.Delay(1500)) == completion,
                "The reclaim intro never completed.");
            await completion;
            Require(intro._disposed && intro._resources.Count == 0 && intro.RemainingMs == 0,
                "Completed reclaim resources remained alive.");
            Require(intro.AlreadyCompleted && !intro.PromoteForPlayback(),
                "Promoting a completed inward pulse revived it.");
            using var early = new RefreshReclaimIntro(bounds, origin, 0, true);
            early.Dispose(); early.Dispose(); early.UpdateCapture(pixels, bounds);
            Require(early.Completion.IsCompletedSuccessfully && early._resources.Count == 0,
                "Early cancellation did not finish or release the reclaim decoration.");
            bool offscreenShown = false;
            bool playbackPromotionVerified = false;
            if (verifyOffscreenShow)
            {
                // The optional first-show check uses a position beyond the entire virtual
                // desktop, including monitors with negative coordinates. It never activates,
                // captures or places a visible pixel on any connected display.
                int left = GetSystemMetrics(76), top = GetSystemMetrics(77);
                var outside = new RectInt32(left - bounds.Width - 4096, top - bounds.Height - 4096, bounds.Width, bounds.Height);
                // A second empty offscreen window stands in for the interactive menu. No
                // actual wheel, model, screen capture or desktop animation participates.
                using var fakeMenu = new RefreshReclaimIntro(outside,
                    new PointInt32(outside.X + 640, outside.Y + 360), 0, true);
                SuppressProbePixels(fakeMenu._hwnd);
                fakeMenu.Show(allowHiddenOffscreen: true);
                using var shown = new RefreshReclaimIntro(outside,
                    new PointInt32(outside.X + 640, outside.Y + 360), fakeMenu._hwnd, true);
                shown.UpdateCapture(pixels, outside);
                // Clip the test HWND to an empty region before its first show as well. Even
                // a Windows first-show reposition cannot produce a pixel during this probe.
                SuppressProbePixels(shown._hwnd);
                shown.Show(allowHiddenOffscreen: true);
                Require(shown._shown && IsWindowVisible(shown._hwnd), "The offscreen first-show check did not realize its window.");
                Require(IsAboveInZOrder(fakeMenu._hwnd, shown._hwnd),
                    "The inward opening did not stay behind the interactive menu.");
                int beforeCount = shown._blocks.Count;
                Vector3 beforeOffset = shown._visual!.Offset;
                double beforeTime = shown.ElapsedMs;
                Task beforeCompletion = shown.Completion;
                nint foreground = GetForegroundWindow();
                Require(shown.PromoteForPlayback() && !shown.AlreadyCompleted
                    && IsAboveInZOrder(shown._hwnd, fakeMenu._hwnd),
                    "The inward pulse did not rise above the outward playback window.");
                Require(shown._blocks.Count == beforeCount && shown._visual.Offset == beforeOffset
                    && shown.ElapsedMs >= beforeTime && ReferenceEquals(beforeCompletion, shown.Completion)
                    && !shown.Completion.IsCompleted && GetForegroundWindow() == foreground,
                    "Promotion restarted, moved, completed or activated the inward pulse.");
                Require(GetWindowDisplayAffinity(shown._hwnd, out uint promotedAffinity) && promotedAffinity == 0x11,
                    "Promotion removed the inward pulse's capture exclusion.");
                playbackPromotionVerified = true;
                await Task.Delay(35);
                Require(GetWindowRect(shown._hwnd, out NativeRect windowRect)
                    && windowRect.Right < left && windowRect.Bottom < top,
                    "The first-show check approached a connected display.");
                Require(GetClientRect(shown._hwnd, out NativeRect client)
                    && client.Right - client.Left == outside.Width && client.Bottom - client.Top == outside.Height,
                    "First show changed the reclaim decoration's physical client dimensions.");
                Require((GetWindowLong(shown._hwnd, -16) & 0x00C40000) == 0
                    && (GetWindowLong(shown._hwnd, -20) & 0x00020301) == 0
                    && (GetWindowLong(shown._hwnd, -20) & 0x08080020) == 0x08080020,
                    "First show restored a frame or changed reclaim input transparency.");
                shown.SyncScale();
                double scale = shown._host.XamlRoot?.RasterizationScale ?? Math.Max(1, GetDpiForWindow(shown._hwnd) / 96.0);
                Require(shown._visual != null && Math.Abs(shown._visual.Scale.X - 1 / scale) < .00001,
                    "First show changed the reclaim decoration's inverse-DPI mapping.");
                offscreenShown = true;
            }
            return new { Passed = true, UsedScreenCaptureOrDisplay = false, DurationMs, Blocks = count,
                CaptureExcluded = true, ClickThrough = true, NoActivate = true, FixedPhysicalSize = true,
                CompletionElapsedMs = intro.ElapsedMs, EarlyDisposeCompleted = true, PigmentAndShadowMatched = true,
                OffscreenShown = offscreenShown, CreationMs = creation.Elapsed.TotalMilliseconds,
                PlaybackPromotionVerified = playbackPromotionVerified,
                ReclaimGradeChainAndWeightsVerified = true, PromotedPulseRetainsColor = true,
                VisualSurfaceAnimationVerifiedOnScreen = false,
                TimingBoundary = "Synthetic hidden window construction only; excludes real input, on-screen presentation and desktop capture. This does not measure input-to-visible latency." };
        }

        private static void SuppressProbePixels(nint hwnd)
        {
            nint empty = CreateRectRgn(0, 0, 0, 0);
            if (empty == 0 || SetWindowRgn(hwnd, empty, false) == 0)
            {
                if (empty != 0) DeleteObject(empty);
                throw new InvalidOperationException("The offscreen first-show check could not suppress its pixels.");
            }
            // Windows owns the empty region after SetWindowRgn succeeds.
        }

        /// <summary>Verifies configurable native animations with synthetic, never-shown windows.</summary>
        internal static async Task<object> RunSettingsProbeAsync()
        {
            var bounds = new RectInt32(-20000, -20000, 1280, 720);
            var origin = new PointInt32(-19360, -19640);
            int sparseCount;
            using (var sparse = CreateHiddenForProbe(bounds, origin, 0, new ReclaimAnimationSettings
            {
                DurationMs = 100, PixelDurationMs = 500, DensityPercent = 10, GlowPercent = 0,
            }))
            {
                sparseCount = sparse._blocks.Count;
                Require(sparse._durationMs == 100 && sparse._pulseMs == 100
                    && sparse._maximumBlocks == 60 && sparseCount is > 0 and <= 60,
                    "Sparse opening did not apply its bounded timing/density snapshot.");
                foreach (var block in sparse._blocks)
                    Require(block.Shadow.Opacity == 0, "Zero glow did not disable the soft halo.");
                int animations = 0;
                foreach (IDisposable resource in sparse._resources)
                    if (resource is ScalarKeyFrameAnimation animation)
                    {
                        animations++;
                        Require(animation.Duration.TotalMilliseconds == 100 && animation.DelayTime == TimeSpan.Zero,
                            "A pulse longer than total produced an invalid native scan delay.");
                    }
                Require(animations == TimingBands && !IsWindowVisible(sparse._hwnd),
                    "Configurable opening lost its native clocks or became visible.");
                Require(await Task.WhenAny(sparse.Completion, Task.Delay(1200)) == sparse.Completion,
                    "The custom total duration did not complete naturally.");
            }
            using var dense = CreateHiddenForProbe(bounds, origin, 0, new ReclaimAnimationSettings
            {
                DurationMs = 1500, PixelDurationMs = 400, DensityPercent = 200, GlowPercent = 200,
            });
            Require(dense._blocks.Count > sparseCount && dense._blocks.Count <= 1200
                && dense._maximumBlocks == 1200 && dense._durationMs == 1500 && dense._pulseMs == 400,
                "The dense opening ignored its bounded block budget or custom timings.");
            foreach (var block in dense._blocks)
                Require(Math.Abs(block.Shadow.Opacity - .96f) < .00001,
                    "Custom glow strength did not reach the native shadows.");
            foreach (IDisposable resource in dense._resources)
                if (resource is ScalarKeyFrameAnimation animation)
                    Require(animation.Duration.TotalMilliseconds == 400
                        && animation.DelayTime.TotalMilliseconds >= 0
                        && animation.DelayTime.TotalMilliseconds + 400 <= 1500.01,
                        "The configurable scan outlasts its native window lifetime.");
            int denseCount = dense._blocks.Count;
            dense.Dispose();
            Require(dense.Completion.IsCompletedSuccessfully && dense._resources.Count == 0,
                "Custom opening resources survived cancellation.");
            return new { Passed = true, SparseBlocks = sparseCount, DenseBlocks = denseCount,
                CustomNativeTimingsVerified = true, PulseClampedToTotal = true,
                ZeroAndDoubleGlowVerified = true, NaturalEndAndCancellation = true,
                UsedDesktopCapture = false, DisplayedEffect = false };
        }

        /// <summary>Hidden resource reuse and stale-owner checks; no desktop capture or visible scan.</summary>
        internal static Task<object> RunPreparedCacheProbeAsync()
        {
            ClearCache();
            try
            {
                Require(TryResolve(new PointInt32(0, 0), out var bounds, out _), "No monitor for the hidden cache probe.");
                var origin = new PointInt32(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
                var profile = ReclaimAnimationSettings.Default with { DurationMs = 1500 };
                using var ready = new RefreshReclaimIntro(bounds, origin, 0, true, profile, prepareOnly: true);
                Require(ready._disposed && !ready._clock.IsRunning && !ready._finishTimer!.IsRunning
                    && !IsWindowVisible(ready._hwnd), "Preparation started a clock or showed a window.");
                int resourceCount = ready._resources.Count;
                var resourceList = ready._resources;
                var leaseClock = Stopwatch.StartNew();
                using var first = new RefreshReclaimIntro(ready, origin, 0);
                leaseClock.Stop();
                Require(!ready._ownsResources && ReferenceEquals(resourceList, first._resources)
                    && first._resources.Count == resourceCount && first._clock.IsRunning,
                    "The first lease rebuilt resources or retained two owners.");
                Task oldCompletion = first.Completion;
                first.Dispose();
                Require(oldCompletion.IsCompletedSuccessfully && ReferenceEquals(_preparedCache, first)
                    && first._resources.Count == resourceCount && !IsWindowVisible(first._hwnd),
                    "A completed lease was not returned hidden to the single slot.");
                _preparedCache = null;
                var nextOrigin = new PointInt32(bounds.X + bounds.Width / 3, bounds.Y + bounds.Height / 3);
                using var second = new RefreshReclaimIntro(first, nextOrigin, 0);
                first.Dispose(); first.ApplySaturation(0); first.UpdateCapture(Array.Empty<byte>(), bounds);
                Require(!second.AlreadyCompleted && !second.Completion.IsCompleted && oldCompletion.IsCompleted
                    && ReferenceEquals(resourceList, second._resources) && !first._ownsResources
                    && second._gradeBrush!.Properties.TryGetScalar("ReclaimGrade.Source1Amount", out float weight)
                        == CompositionGetValueStatus.Succeeded && weight == 1,
                    "An old lease changed the later lease's completion or color.");
                foreach (var block in second._blocks)
                {
                    double dx = block.X - (nextOrigin.X - bounds.X), dy = block.Y - (nextOrigin.Y - bounds.Y);
                    Require(block.Visual.Opacity == (Math.Sqrt(dx * dx + dy * dy) < second._clearRadius ? 0 : 1),
                        "A cached block retained the previous origin's clear hole.");
                }
                Require(second.PromoteForPlayback() && !WarmAt(nextOrigin, profile)
                    && !IsWindowVisible(second._hwnd), "Warmup stole or displayed a promoted lease.");
                ClearCache();
                second.Dispose();
                Require(_preparedCache == null && second._resources.Count == 0 && _activeCacheLeases == 0,
                    "A cleared generation recached an in-flight lease or retained native resources.");
                return Task.FromResult<object>(new { Passed = true, ResourceCount = resourceCount,
                    FirstRentMs = leaseClock.Elapsed.TotalMilliseconds, DistinctLeaseOwners = true,
                    OriginRetargeted = true, PromotedLeaseNotStolen = true, ClearInvalidatesInFlight = true,
                    UsedDesktopCapture = false, DisplayedEffect = false });
            }
            finally { ClearCache(); }
        }

        private static bool IsAboveInZOrder(nint upper, nint lower)
        {
            int remaining = 2048;
            for (nint previous = GetWindow(lower, 3); previous != 0 && remaining-- > 0; previous = GetWindow(previous, 3))
                if (previous == upper) return true;
            return false;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        [StructLayout(LayoutKind.Sequential)] private struct NativePoint(int x, int y) { public int X = x, Y = y; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
        [StructLayout(LayoutKind.Sequential)] private struct BlurBehind { public uint Flags; [MarshalAs(UnmanagedType.Bool)] public bool Enable; public nint Region; [MarshalAs(UnmanagedType.Bool)] public bool Transition; }
        private delegate nint NativeSubclass(nint hwnd, uint message, nint wParam, nint lParam, UIntPtr id, UIntPtr data);
        [DllImport("user32.dll")] private static extern nint MonitorFromPoint(NativePoint point, uint flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
        [DllImport("user32.dll")] private static extern int GetWindowLong(nint hwnd, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(nint hwnd, int index, int style);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
        [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
        [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
        [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
        [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out NativeRect rect);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
        [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
        [DllImport("user32.dll")] private static extern int FillRect(nint dc, ref NativeRect rect, nint brush);
        [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint colour);
        [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmEnableBlurBehindWindow(nint hwnd, ref BlurBehind blur);
        [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, NativeSubclass callback, UIntPtr id, UIntPtr data);
        [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, NativeSubclass callback, UIntPtr id);
        [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
    }
}
