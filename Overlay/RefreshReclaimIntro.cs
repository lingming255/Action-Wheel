using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
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
    /// A short, independent decoration behind the interactive wheel. Stationary blocks pulse
    /// from the monitor's outside towards the pointer; no desktop pixels move or get rescaled.
    /// All methods belong to the menu's UI thread. Dispose also completes a waiting release.
    /// </summary>
    internal sealed class RefreshReclaimIntro : IDisposable
    {
        internal const double DurationMs = 240;
        private const int MaximumBlocks = 600;
        private const int TimingBands = 40;
        private const double PulseMs = DurationMs / 4;
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
        private XamlRoot? _xamlRoot;
        private nint _backgroundBrush;
        private bool _disposed, _shown;

        private sealed record BlockVisual(int X, int Y, CompositionColorBrush Brush, DropShadow Shadow);

        internal Task Completion => _completion.Task;
        internal double ElapsedMs => _clock.Elapsed.TotalMilliseconds;
        internal double RemainingMs => _disposed ? 0 : Math.Max(0, DurationMs - ElapsedMs);

        internal static RefreshReclaimIntro? TryStart(PointInt32 origin, nint menuHwnd)
        {
            RefreshReclaimIntro? intro = null;
            try
            {
                nint monitor = MonitorFromPoint(new NativePoint(origin.X, origin.Y), 2);
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfo(monitor, ref info)) return null;
                intro = new RefreshReclaimIntro(new RectInt32(info.Monitor.Left, info.Monitor.Top,
                    info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top), origin, menuHwnd, false);
                return intro;
            }
            catch
            {
                intro?.Dispose();
                return null;
            }
        }

        private RefreshReclaimIntro(RectInt32 bounds, PointInt32 origin, nint menuHwnd, bool hidden)
        {
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
                _finishTimer.Interval = TimeSpan.FromMilliseconds(DurationMs);
                _finishTimer.Tick += Finish;
                _window.Closed += OnClosed;
                _host.Loaded += OnLoaded;
                _window.AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                ConfigureNativeWindow();
                _captureExcluded = SetWindowDisplayAffinity(_hwnd, 0x11)
                    && GetWindowDisplayAffinity(_hwnd, out uint affinity) && affinity == 0x11;
                BuildVisuals();
                _clock.Start();
                _finishTimer.Start();
                // A failed capture-exclusion request must not paint into the raw desktop grab.
                // UpdateCapture may reveal this window only after that grab is safely complete.
                if (_captureExcluded) Show();
            }
            catch
            {
                Dispose();
                throw;
            }
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
            // A separate click-through HWND keeps the menu's original size and hit targets.
            // Insert directly behind that topmost menu, rather than covering its buttons.
            if (!SetWindowPos(_hwnd, _menuHwnd != 0 ? _menuHwnd : new nint(-1),
                _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, 0x30))
                throw new InvalidOperationException("The reclaim decoration could not stay behind its menu.");
            PaintNativeBackground();
            SyncScale();
        }

        private void BuildVisuals()
        {
            var compositor = ElementCompositionPreview.GetElementVisual(_host).Compositor;
            var visual = compositor.CreateContainerVisual();
            _visual = visual;
            _resources.Add(visual);
            visual.Size = new Vector2(_bounds.Width, _bounds.Height);
            var bands = new ContainerVisual[TimingBands];
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
            double step = Math.Max(cell * 3.0, Math.Sqrt((double)_bounds.Width * _bounds.Height / MaximumBlocks));
            int columns = Math.Max(1, (int)Math.Floor(_bounds.Width / step));
            int rows = Math.Max(1, (int)Math.Floor(_bounds.Height / step));
            double dx = Math.Max(_origin.X - _bounds.X, _bounds.X + _bounds.Width - 1 - _origin.X);
            double dy = Math.Max(_origin.Y - _bounds.Y, _bounds.Y + _bounds.Height - 1 - _origin.Y);
            double farthest = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
            double clearRadius = cell * 1.7;
            for (int row = 0; row < rows; row++)
            for (int column = 0; column < columns; column++)
            {
                int x = (int)Math.Round((column + .5) * _bounds.Width / columns);
                int y = (int)Math.Round((row + .5) * _bounds.Height / rows);
                double ox = x - (_origin.X - _bounds.X), oy = y - (_origin.Y - _bounds.Y);
                double distance = Math.Sqrt(ox * ox + oy * oy);
                if (distance < clearRadius || _blocks.Count >= MaximumBlocks) continue;
                int band = Math.Clamp((int)Math.Floor((1 - Math.Min(1, distance / farthest)) * (TimingBands - 1)), 0, TimingBands - 1);
                // Tint is an immediate placeholder. The raw capture can replace pigment later
                // without touching either this fixed position or the already-running scan.
                Color colour = Color.FromArgb(255, 75, 132, 157);
                var brush = compositor.CreateColorBrush(colour);
                var shadow = compositor.CreateDropShadow();
                shadow.Color = colour;
                shadow.BlurRadius = cell * .65f;
                shadow.Opacity = .48f;
                shadow.Offset = Vector3.Zero;
                var block = compositor.CreateSpriteVisual();
                block.Size = new Vector2(cell * .72f);
                block.Offset = new Vector3(x - block.Size.X / 2, y - block.Size.Y / 2, 0);
                block.Brush = brush;
                block.Shadow = shadow;
                bands[band].Children.InsertAtTop(block);
                _blocks.Add(new BlockVisual(x, y, brush, shadow));
                _resources.Add(brush); _resources.Add(shadow); _resources.Add(block);
            }

            // A few blocks share each opacity clock. Composition handles the scan off-thread;
            // there is no per-frame UI loop, position interpolation or screenshot texture.
            for (int band = 0; band < bands.Length; band++)
            {
                var pulse = compositor.CreateScalarKeyFrameAnimation();
                pulse.InsertKeyFrame(0, 0);
                pulse.InsertKeyFrame(.22f, .68f);
                pulse.InsertKeyFrame(.5f, .4f);
                pulse.InsertKeyFrame(1, 0);
                pulse.Duration = TimeSpan.FromMilliseconds(PulseMs);
                pulse.DelayTime = TimeSpan.FromMilliseconds(band / (double)(TimingBands - 1) * (DurationMs - PulseMs));
                pulse.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
                bands[band].StartAnimation("Opacity", pulse);
                _resources.Add(pulse);
            }
            ElementCompositionPreview.SetElementChildVisual(_host, visual);
            SyncScale();
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
        private void OnClosed(object sender, WindowEventArgs args) => Dispose();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
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
            try { ElementCompositionPreview.SetElementChildVisual(_host, null); } catch { }
            for (int index = _resources.Count - 1; index >= 0; index--)
            {
                try { _resources[index].Dispose(); } catch { }
            }
            _resources.Clear(); _blocks.Clear(); _visual = null;
            if (_hwnd != 0) RemoveWindowSubclass(_hwnd, _backgroundSubclass, new UIntPtr(91));
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
            bool rejected = false;
            try { intro.UpdateCapture(new byte[3], bounds); } catch (ArgumentException) { rejected = true; }
            Require(rejected && !IsWindowVisible(intro._hwnd), "Invalid pigment was accepted or a hidden update showed the window.");
            Task completion = intro.Completion;
            Require(await Task.WhenAny(completion, Task.Delay(1500)) == completion,
                "The reclaim intro never completed.");
            await completion;
            Require(intro._disposed && intro._resources.Count == 0 && intro.RemainingMs == 0,
                "Completed reclaim resources remained alive.");
            using var early = new RefreshReclaimIntro(bounds, origin, 0, true);
            early.Dispose(); early.Dispose(); early.UpdateCapture(pixels, bounds);
            Require(early.Completion.IsCompletedSuccessfully && early._resources.Count == 0,
                "Early cancellation did not finish or release the reclaim decoration.");
            bool offscreenShown = false;
            if (verifyOffscreenShow)
            {
                // The optional first-show check uses a position beyond the entire virtual
                // desktop, including monitors with negative coordinates. It never activates,
                // captures or places a visible pixel on any connected display.
                int left = GetSystemMetrics(76), top = GetSystemMetrics(77);
                var outside = new RectInt32(left - bounds.Width - 4096, top - bounds.Height - 4096, bounds.Width, bounds.Height);
                using var shown = new RefreshReclaimIntro(outside,
                    new PointInt32(outside.X + 640, outside.Y + 360), 0, true);
                shown.UpdateCapture(pixels, outside);
                // Clip the test HWND to an empty region before its first show as well. Even
                // a Windows first-show reposition cannot produce a pixel during this probe.
                nint empty = CreateRectRgn(0, 0, 0, 0);
                if (empty == 0 || SetWindowRgn(shown._hwnd, empty, false) == 0)
                {
                    if (empty != 0) DeleteObject(empty);
                    throw new InvalidOperationException("The offscreen first-show check could not suppress its pixels.");
                }
                // Windows owns the empty region after SetWindowRgn succeeds.
                shown.Show(allowHiddenOffscreen: true);
                await Task.Delay(35);
                Require(shown._shown && IsWindowVisible(shown._hwnd), "The offscreen first-show check did not realize its window.");
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
                TimingBoundary = "Synthetic hidden window construction only; excludes real input, on-screen presentation and desktop capture. This does not measure input-to-visible latency." };
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
