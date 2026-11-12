using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Action_Wheel.Services;
using Microsoft.UI.Dispatching;
using Windows.Graphics;

namespace Action_Wheel.Overlay
{
    /// <summary>
    /// Live 1:1 desktop grading below the wheel. Native magnifier controls retain pixel detail;
    /// unlike a host backdrop they do not pre-blur the desktop. All calls use the menu UI thread.
    /// </summary>
    internal sealed class ScreenGradeSurface : IDisposable
    {
        private const string HostClass = "ActionWheel.ScreenGrade.NativeHost";
        private static readonly NativeWindowProc HostProc = WindowProc;
        private static readonly List<ScreenGradeSurface> ActiveGroups = new();
        private static ushort _hostClass;
        private static int _sessionUsers;
        private readonly List<MonitorSurface> _surfaces = new();
        private readonly nint _menuHwnd;
        private nint _decorationHwnd;
        private readonly bool _canShow;
        private DispatcherQueueTimer? _timer;
        private bool _disposed, _sessionOwned, _registered, _filtersDirty;

        internal static string? LastFailure { get; private set; }
        internal IReadOnlyList<nint> WindowHandles { get; private set; } = Array.Empty<nint>();
        internal IReadOnlyList<RectInt32> Bounds { get; private set; } = Array.Empty<RectInt32>();

        // Capture-excluded inward particles cannot be sampled by a magnifier. Keep their
        // independently graded layer above this surface while the interactive wheel stays on top.
        internal void SetDecorationAnchor(nint hwnd)
        {
            if (_decorationHwnd == hwnd) return;
            _decorationHwnd = hwnd;
            _filtersDirty = true;
        }

        private nint LayerAnchor => _decorationHwnd != 0 && IsWindow(_decorationHwnd)
            ? _decorationHwnd : _menuHwnd;

        internal static ScreenGradeSurface? TryCreate(nint menuHwnd)
        {
            LastFailure = null;
            try
            {
                if (menuHwnd == 0 || !IsWindow(menuHwnd))
                    throw new InvalidOperationException("Live grading requires a valid menu window.");
                var bounds = new List<RectInt32>();
                MonitorCallback callback = (nint monitor, nint dc, ref NativeRect rect, nint data) =>
                {
                    if (rect.Right > rect.Left && rect.Bottom > rect.Top)
                        bounds.Add(new RectInt32(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
                    return true;
                };
                Check(EnumDisplayMonitors(0, 0, callback, 0) && bounds.Count > 0, "Read monitor bounds");
                return new ScreenGradeSurface(bounds, menuHwnd, show: true, captureExcluded: true);
            }
            catch (Exception exception) { LastFailure = Describe(exception); return null; }
        }

        /// <summary>Only this synthetic ROI is used by diagnostics. Hidden until a successful Apply.</summary>
        internal static ScreenGradeSurface CreateForProbe(RectInt32 bounds, nint menuHwnd, bool show, bool captureExcluded)
            => new(new[] { bounds }, menuHwnd, show, captureExcluded);

        private ScreenGradeSurface(IReadOnlyList<RectInt32> bounds, nint menuHwnd, bool show, bool captureExcluded)
        {
            _menuHwnd = menuHwnd;
            _canShow = show;
            try
            {
                if (_sessionUsers == 0) Check(MagInitialize(), "Initialize magnifier controls");
                _sessionUsers++;
                _sessionOwned = true;
                EnsureHostClass();
                var handles = new nint[bounds.Count];
                var physicalBounds = new RectInt32[bounds.Count];
                for (int index = 0; index < bounds.Count; index++)
                {
                    var surface = new MonitorSurface(bounds[index], captureExcluded);
                    _surfaces.Add(surface);
                    handles[index] = surface.Host;
                    physicalBounds[index] = bounds[index];
                }
                WindowHandles = Array.AsReadOnly(handles);
                Bounds = Array.AsReadOnly(physicalBounds);
                ActiveGroups.Add(this);
                _registered = true;
                RefreshFilters();
                var queue = DispatcherQueue.GetForCurrentThread()
                    ?? throw new InvalidOperationException("Live grading requires the menu dispatcher.");
                _timer = queue.CreateTimer();
                _timer.Interval = TimeSpan.FromMilliseconds(16);
                _timer.IsRepeating = true;
                _timer.Tick += OnTick;
            }
            catch { Dispose(); throw; }
        }

        internal bool Apply(float saturation)
        {
            if (_disposed) return false;
            try
            {
                if (!float.IsFinite(saturation)) throw new ArgumentOutOfRangeException(nameof(saturation));
                if (_filtersDirty) { RefreshFilters(); _filtersDirty = false; }
                var matrix = new ColorEffect { Values = ScreenGrade.BuildMatrix(Math.Clamp(saturation, 0, 1)) };
                foreach (var surface in _surfaces) surface.SetColor(ref matrix);
                RefreshSources();
                if (_canShow)
                {
                    foreach (var surface in _surfaces) surface.ShowBelow(LayerAnchor);
                    // The endpoint remains live even after ScreenGrade's transition timer stops.
                    _timer!.Start();
                }
                return true;
            }
            catch (Exception exception) { LastFailure = Describe(exception); Dispose(); return false; }
        }

        private void OnTick(DispatcherQueueTimer sender, object args)
        {
            if (_disposed || !ReferenceEquals(sender, _timer)) return;
            try { RefreshSources(); }
            catch (Exception exception) { LastFailure = Describe(exception); Dispose(); }
        }

        private void RefreshSources()
        {
            if (_menuHwnd != 0 && !IsWindow(_menuHwnd))
                throw new InvalidOperationException("The grading layer's menu no longer exists.");
            if (_decorationHwnd != 0 && !IsWindow(_decorationHwnd))
            {
                _decorationHwnd = 0;
                RefreshFilters();
            }
            foreach (var surface in _surfaces)
            {
                surface.RefreshSource();
                if (_canShow) surface.OrderBelow(LayerAnchor);
            }
        }

        private static void RefreshFilters()
        {
            var excluded = new HashSet<nint>();
            foreach (var group in ActiveGroups)
            {
                if (group._menuHwnd != 0) excluded.Add(group._menuHwnd);
                if (group._decorationHwnd != 0 && IsWindow(group._decorationHwnd)) excluded.Add(group._decorationHwnd);
                foreach (var surface in group._surfaces) { excluded.Add(surface.Host); excluded.Add(surface.Child); }
            }
            var handles = new nint[excluded.Count];
            excluded.CopyTo(handles);
            foreach (var group in ActiveGroups)
                foreach (var surface in group._surfaces)
                    Check(MagSetWindowFilterList(surface.Child, 0, handles.Length, handles), "Exclude grading layers and wheel from magnifier source");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var timer = _timer; _timer = null;
            if (timer != null) { timer.Stop(); timer.Tick -= OnTick; }
            foreach (var surface in _surfaces) surface.Hide();
            if (_registered) { ActiveGroups.Remove(this); _registered = false; }
            for (int index = _surfaces.Count - 1; index >= 0; index--) _surfaces[index].Dispose();
            _surfaces.Clear();
            // Probes may temporarily coexist. Keep the process session until the last owner,
            // and remove retired HWNDs from every remaining control's exclusion list.
            try { if (ActiveGroups.Count > 0) RefreshFilters(); }
            catch (Exception exception) { LastFailure = Describe(exception); }
            if (_sessionOwned) { _sessionOwned = false; if (--_sessionUsers == 0) MagUninitialize(); }
        }

        private sealed class MonitorSurface : IDisposable
        {
            private readonly NativeRect _source;
            internal nint Host { get; private set; }
            internal nint Child { get; private set; }

            internal MonitorSurface(RectInt32 bounds, bool captureExcluded)
            {
                if (bounds.Width <= 0 || bounds.Height <= 0) throw new ArgumentOutOfRangeException(nameof(bounds));
                _source = new NativeRect { Left = bounds.X, Top = bounds.Y, Right = bounds.X + bounds.Width, Bottom = bounds.Y + bounds.Height };
                try
                {
                    // The app is PerMonitorV2: these are physical pixels, with no XAML/DIP scale.
                    const uint exStyle = 0x080800A8; // NOACTIVATE | LAYERED | TOOLWINDOW | TRANSPARENT | TOPMOST
                    Host = CreateWindowEx(exStyle, HostClass, "Action Wheel Desktop Grade", 0x82000000,
                        bounds.X, bounds.Y, bounds.Width, bounds.Height, 0, 0, GetModuleHandle(null), 0);
                    Check(Host != 0, "Create native grading host");
                    Check(SetLayeredWindowAttributes(Host, 0, 255, 2), "Set grading host opacity");
                    Child = CreateWindowEx(0x20, "Magnifier", null, 0x50000000,
                        0, 0, bounds.Width, bounds.Height, Host, 0, GetModuleHandle(null), 0);
                    Check(Child != 0, "Create 1:1 magnifier control");
                    var transform = new MagnifierTransform { M11 = 1, M22 = 1, M33 = 1 };
                    Check(MagSetWindowTransform(Child, ref transform), "Set identity magnification");
                    var color = new ColorEffect { Values = ScreenGrade.BuildMatrix(1) };
                    SetColor(ref color);
                    uint requestedAffinity = captureExcluded ? 0x11u : 0;
                    Check(SetWindowDisplayAffinity(Host, requestedAffinity)
                        && GetWindowDisplayAffinity(Host, out uint affinity) && affinity == requestedAffinity,
                        "Set grading host capture policy");
                    RefreshSource();
                }
                catch { Dispose(); throw; }
            }

            internal void SetColor(ref ColorEffect color)
                => Check(MagSetColorEffect(Child, ref color), "Set Rec601 grading matrix");

            internal void RefreshSource()
            {
                // MagSetWindowSource takes RECT by value. Its unchanged 1:1 rectangle must still
                // be refreshed independently, so underlying content keeps moving at saturation 0.
                Check(MagSetWindowSource(Child, _source), "Refresh live magnifier source");
                Check(InvalidateRect(Child, 0, false), "Invalidate live grading pixels");
            }

            internal void ShowBelow(nint menuHwnd)
            {
                OrderBelow(menuHwnd);
                ShowWindow(Host, 4); // SW_SHOWNOACTIVATE; all sources/matrices/filters are ready.
                OrderBelow(menuHwnd);
            }

            internal void OrderBelow(nint menuHwnd)
                => Check(SetWindowPos(Host, menuHwnd != 0 ? menuHwnd : new nint(-1), 0, 0, 0, 0, 0x13), "Keep live grade below the wheel");
            internal void Hide() { if (Host != 0) ShowWindow(Host, 0); }
            public void Dispose()
            {
                Hide();
                // Destroying the parent also destroys its magnifier child before MagUninitialize.
                if (Host != 0) DestroyWindow(Host);
                Host = 0; Child = 0;
            }
        }

        private static void EnsureHostClass()
        {
            if (_hostClass != 0) return;
            var definition = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(), Instance = GetModuleHandle(null),
                WindowProc = Marshal.GetFunctionPointerForDelegate(HostProc), ClassName = HostClass,
            };
            _hostClass = RegisterClassEx(ref definition);
            Check(_hostClass != 0, "Register native grading host");
        }

        private static nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam)
            => message switch { 0x84 => new nint(-1), 0x21 => 3, 0x14 => 1, _ => DefWindowProc(hwnd, message, wParam, lParam) };

        /// <summary>Only hidden native controls: no visible effect and no screen pixel readback.</summary>
        internal static async Task<object> RunHiddenProbeAsync()
        {
            int initialUsers = _sessionUsers;
            nint foreground = GetForegroundWindow();
            var bounds = new RectInt32(-20000, -20000, 640, 360);
            using var grade = CreateForProbe(bounds, 0, show: false, captureExcluded: true);
            var surface = grade._surfaces[0];
            nint hwnd = surface.Host, child = surface.Child;
            Require(!IsWindowVisible(hwnd) && _sessionUsers == initialUsers + 1, "Hidden grading session was not initialized.");
            foreach (float saturation in new[] { 1f, .5f, 0f })
            {
                Require(grade.Apply(saturation), "The hidden grading matrix was rejected.");
                Require(MagGetColorEffect(child, out var actual) && actual.Values.AsSpan().SequenceEqual(ScreenGrade.BuildMatrix(saturation)),
                    "The native grading matrix differs from Rec601.");
            }
            Require(MagGetWindowTransform(child, out var transform) && transform.M11 == 1 && transform.M22 == 1 && transform.M33 == 1
                && transform.M12 == 0 && transform.M13 == 0 && transform.M21 == 0 && transform.M23 == 0 && transform.M31 == 0 && transform.M32 == 0,
                "Magnification is not exactly 1:1.");
            Require(MagGetWindowSource(child, out var source) && source.Left == bounds.X && source.Top == bounds.Y
                && source.Right == bounds.X + bounds.Width && source.Bottom == bounds.Y + bounds.Height,
                "The native source is not the physical ROI.");
            Require(GetWindowRect(hwnd, out var rect) && rect.Equals(source)
                && GetClientRect(child, out var client) && client.Right == bounds.Width && client.Bottom == bounds.Height,
                "Native grading bounds are not 1:1 physical pixels.");
            Require((GetWindowLong(hwnd, -20) & 0x080800A0) == 0x080800A0, "The native grading host is not click-through.");
            Require(GetWindowDisplayAffinity(hwnd, out uint affinity) && affinity == 0x11, "The grading host could contaminate capture.");
            var excluded = new nint[2];
            Require(MagGetWindowFilterList(child, out uint mode, excluded.Length, excluded) == 2 && mode == 0
                && Array.IndexOf(excluded, hwnd) >= 0 && Array.IndexOf(excluded, child) >= 0,
                "The magnifier source includes its own grading windows.");
            using (var peer = CreateForProbe(bounds, 0, show: false, captureExcluded: true))
                Require(_sessionUsers == initialUsers + 2, "A second native grading group did not retain the process session.");
            Require(_sessionUsers == initialUsers + 1, "A retired group released another group's session.");
            Require(!IsWindowVisible(hwnd) && GetForegroundWindow() == foreground, "The hidden grading probe disturbed the desktop.");
            await Task.Yield();
            // A user or another app may legitimately change focus across a dispatcher yield.
            // Check our own hidden HWND after the yield; focus is checked synchronously above.
            Require(!IsWindowVisible(hwnd), "The hidden grading probe became visible.");
            grade.Dispose();
            Require(!IsWindow(hwnd) && !IsWindow(child) && _sessionUsers == initialUsers && !grade.Apply(0), "Native grading resources survived disposal.");
            grade.Dispose();
            return new { Passed = true, NativeMagnifier = true, IdentityMagnification = true, Rec601WeightsVerified = true,
                PhysicalBoundsVerified = true, ClickThroughVerified = true, CaptureExclusionVerified = true,
                SessionLifetimeVerified = true, UsedDesktopCapture = false, DisplayedEffect = false };
        }

        private static string Describe(Exception exception) => $"{exception.GetType().Name}: {exception.Message}";
        private static void Check(bool condition, string operation)
        {
            if (!condition) throw new Win32Exception(Marshal.GetLastWin32Error(), operation);
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct MagnifierTransform { public float M11, M12, M13, M21, M22, M23, M31, M32, M33; }
        [StructLayout(LayoutKind.Sequential)] private struct ColorEffect { [MarshalAs(UnmanagedType.ByValArray, SizeConst = 25)] public float[] Values; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
        {
            public uint Size, Style; public nint WindowProc; public int ClassExtra, WindowExtra;
            public nint Instance, Icon, Cursor, Background; public string? MenuName, ClassName; public nint SmallIcon;
        }
        private delegate bool MonitorCallback(nint monitor, nint dc, ref NativeRect rect, nint data);
        private delegate nint NativeWindowProc(nint hwnd, uint message, nint wParam, nint lParam);
        [DllImport("Magnification.dll", SetLastError = true)] private static extern bool MagInitialize();
        [DllImport("Magnification.dll")] private static extern bool MagUninitialize();
        [DllImport("Magnification.dll", SetLastError = true)] private static extern bool MagSetWindowSource(nint hwnd, NativeRect source);
        [DllImport("Magnification.dll")] private static extern bool MagGetWindowSource(nint hwnd, out NativeRect source);
        [DllImport("Magnification.dll", SetLastError = true)] private static extern bool MagSetWindowTransform(nint hwnd, ref MagnifierTransform transform);
        [DllImport("Magnification.dll")] private static extern bool MagGetWindowTransform(nint hwnd, out MagnifierTransform transform);
        [DllImport("Magnification.dll", SetLastError = true)] private static extern bool MagSetColorEffect(nint hwnd, ref ColorEffect effect);
        [DllImport("Magnification.dll")] private static extern bool MagGetColorEffect(nint hwnd, out ColorEffect effect);
        [DllImport("Magnification.dll", SetLastError = true)] private static extern bool MagSetWindowFilterList(nint hwnd, uint mode, int count, [In] nint[] windows);
        [DllImport("Magnification.dll")] private static extern int MagGetWindowFilterList(nint hwnd, out uint mode, int count, [Out] nint[] windows);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass definition);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint exStyle, string className, string? name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
        [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] private static extern int GetWindowLong(nint hwnd, int index);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
        [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out NativeRect rect);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(nint hwnd, uint colorKey, byte alpha, uint flags);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
        [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool InvalidateRect(nint hwnd, nint rect, [MarshalAs(UnmanagedType.Bool)] bool erase);
    }
}
