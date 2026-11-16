using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Action_Wheel.Services;
using Microsoft.UI.Dispatching;
using Windows.Graphics;

namespace Action_Wheel.Overlay
{
    /// <summary>
    /// Live 1:1 desktop grading below the wheel. Native magnifier controls retain pixel detail;
    /// unlike a host backdrop they do not pre-blur the desktop. Each group stays on its creating
    /// dispatcher; production uses a dedicated native dispatcher, while probes may use the UI.
    /// </summary>
    internal sealed class ScreenGradeSurface : IDisposable
    {
        private const string HostClass = "ActionWheel.ScreenGrade.NativeHost";
        private static readonly NativeWindowProc HostProc = WindowProc;
        [ThreadStatic] private static List<ScreenGradeSurface>? _activeGroups;
        private static List<ScreenGradeSurface> ActiveGroups => _activeGroups ??= new();
        private static readonly object NativeSessionGate = new();
        private static int _processSessionUsers, _processSessionThread;
        private static ushort _hostClass;
        [ThreadStatic] private static int _sessionUsers;
        [ThreadStatic] private static ScreenGradeSurface? _idleGroup;
        [ThreadStatic] private static long _initializeCalls, _uninitializeCalls, _createHostCalls, _createMagnifierCalls;
        [ThreadStatic] private static long _setColorCalls, _setSourceCalls, _invalidateCalls, _showCalls, _hideCalls;
        [ThreadStatic] private static long _setWindowPosCalls, _setFilterCalls, _cacheRents, _cacheReturns, _cacheWarms;
        [ThreadStatic] private static string? _lastFailure;
        private readonly List<MonitorSurface> _surfaces = new();
        private nint _menuHwnd;
        private nint _decorationHwnd;
        private readonly bool _canShow, _cacheEligible;
        private DispatcherQueueTimer? _timer;
        private bool _disposed, _sessionOwned, _registered, _filtersDirty, _presented;
        private Func<bool>? _mayPresent;
        private DurationSamples _applyTimings, _tickTimings, _tickIntervals;
        private long _previousMeasuredTick;
        private int _tickIntervalsOver50Ms, _tickIntervalsOver66Ms;

        // Opt-in and instance-local: normal desktop operation collects no timing samples.
        internal bool MeasureTimings { get; set; }
        internal object GetTimingDiagnostics()
        {
            var sources = new List<object>();
            foreach (var surface in _surfaces) sources.Add(surface.GetTimingDiagnostics());
            return new
            {
                Enabled = MeasureTimings,
                TargetTickIntervalMs = 33,
                Apply = _applyTimings.Snapshot(), Tick = _tickTimings.Snapshot(),
                TickIntervals = _tickIntervals.Snapshot(),
                TickIntervalsOver50Ms = _tickIntervalsOver50Ms,
                TickIntervalsOver66Ms = _tickIntervalsOver66Ms,
                MonitorSources = sources,
                Boundary = "Owning-thread native call wall times; no pixel readback or presentation latency measurement.",
            };
        }

        internal void ResetTimingDiagnostics()
        {
            _applyTimings = _tickTimings = _tickIntervals = default;
            _previousMeasuredTick = 0;
            _tickIntervalsOver50Ms = _tickIntervalsOver66Ms = 0;
            foreach (var surface in _surfaces) surface.ResetTimingDiagnostics();
        }

        internal static string? LastFailure { get => _lastFailure; private set => _lastFailure = value; }
        internal IReadOnlyList<nint> WindowHandles { get; private set; } = Array.Empty<nint>();
        internal IReadOnlyList<RectInt32> Bounds { get; private set; } = Array.Empty<RectInt32>();
        internal void SetPresentationGuard(Func<bool>? mayPresent) => _mayPresent = mayPresent;
        private bool MayPresent => _mayPresent?.Invoke() != false;

        private void StopAndHide()
        {
            _timer?.Stop();
            foreach (var surface in _surfaces) surface.Hide();
            _presented = false;
        }

        // Owning-thread snapshots only. Counters stay in memory; diagnostics compare deltas.
        internal readonly record struct NativeCallDiagnostics(long Initialize, long Uninitialize,
            long CreateHost, long CreateMagnifier, long SetColorEffect, long SetWindowSource,
            long Invalidate, long ShowWindow, long HideWindow, long SetWindowPos, long SetFilterList,
            long CacheRents, long CacheReturns, long CacheWarms, int SessionUsers, bool HasIdleGroup,
            int ProcessSessionUsers, int ProcessSessionThread);

        internal static NativeCallDiagnostics GetNativeCallDiagnostics()
            => new(_initializeCalls, _uninitializeCalls, _createHostCalls, _createMagnifierCalls,
                _setColorCalls, _setSourceCalls, _invalidateCalls, _showCalls, _hideCalls,
                _setWindowPosCalls, _setFilterCalls, _cacheRents, _cacheReturns, _cacheWarms,
                _sessionUsers, _idleGroup is { _disposed: false }, Volatile.Read(ref _processSessionUsers),
                Volatile.Read(ref _processSessionThread));

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
                var bounds = ReadMonitorBounds();
                var idle = _idleGroup;
                _idleGroup = null;
                if (idle != null)
                {
                    if (!idle._disposed && idle.MatchesBounds(bounds))
                    {
                        idle._menuHwnd = menuHwnd;
                        idle._decorationHwnd = 0;
                        idle._filtersDirty = true;
                        _cacheRents++;
                        return idle;
                    }
                    idle.Dispose();
                }
                return new ScreenGradeSurface(bounds, menuHwnd, show: true, captureExcluded: true, cacheEligible: true);
            }
            catch (Exception exception) { LastFailure = Describe(exception); return null; }
        }

        /// <summary>
        /// Builds at most one hidden group on its owning dispatcher during idle time.
        /// No source timer or grading window is shown until a later successful Apply.
        /// </summary>
        internal static bool Warm()
        {
            try
            {
                var bounds = ReadMonitorBounds();
                if (_idleGroup is { _disposed: false } idle && idle.MatchesBounds(bounds)) return true;
                ShutdownCache();
                // An in-use group can be returned when the gesture ends; avoid a duplicate warm.
                foreach (var group in ActiveGroups)
                    if (group._cacheEligible && !group._disposed && group._menuHwnd != 0
                        && group.MatchesBounds(bounds)) return true;
                _idleGroup = new ScreenGradeSurface(bounds, 0, show: true, captureExcluded: true, cacheEligible: true);
                _cacheWarms++;
                return true;
            }
            catch (Exception exception) { LastFailure = Describe(exception); return false; }
        }

        /// <summary>Normal gesture cleanup. Failures and diagnostic groups must use real disposal.</summary>
        internal void ReturnToCache()
        {
            if (_disposed || ReferenceEquals(_idleGroup, this)) return;
            if (!_cacheEligible) { Dispose(); return; }
            try
            {
                _timer?.Stop();
                foreach (var surface in _surfaces) surface.Hide();
                _mayPresent = null;
                _presented = false;
                _menuHwnd = 0;
                _decorationHwnd = 0;
                var retired = _idleGroup;
                _idleGroup = null;
                retired?.Dispose();
                // Remove the retired menu/decoration HWNDs before either can be reused by Windows.
                RefreshFilters();
                _filtersDirty = false;
                _idleGroup = this;
                _cacheReturns++;
            }
            catch (Exception exception) { LastFailure = Describe(exception); Dispose(); }
        }

        /// <summary>Destroys the single idle group on the same dispatcher that created it.</summary>
        internal static void ShutdownCache()
        {
            var idle = _idleGroup;
            _idleGroup = null;
            idle?.Dispose();
        }

        private static List<RectInt32> ReadMonitorBounds()
        {
            var bounds = new List<RectInt32>();
            MonitorCallback callback = (nint monitor, nint dc, ref NativeRect rect, nint data) =>
            {
                if (rect.Right > rect.Left && rect.Bottom > rect.Top)
                    bounds.Add(new RectInt32(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
                return true;
            };
            Check(EnumDisplayMonitors(0, 0, callback, 0) && bounds.Count > 0, "Read monitor bounds");
            return bounds;
        }

        private bool MatchesBounds(IReadOnlyList<RectInt32> bounds)
        {
            if (Bounds.Count != bounds.Count || _surfaces.Count != bounds.Count) return false;
            for (int index = 0; index < bounds.Count; index++)
            {
                var existing = Bounds[index];
                var candidate = bounds[index];
                if (existing.X != candidate.X || existing.Y != candidate.Y
                    || existing.Width != candidate.Width || existing.Height != candidate.Height
                    || !IsWindow(_surfaces[index].Host) || !IsWindow(_surfaces[index].Child)) return false;
            }
            return true;
        }

        /// <summary>Only this synthetic ROI is used by diagnostics. Hidden until a successful Apply.</summary>
        internal static ScreenGradeSurface CreateForProbe(RectInt32 bounds, nint menuHwnd, bool show, bool captureExcluded)
            => new(new[] { bounds }, menuHwnd, show, captureExcluded);

        private ScreenGradeSurface(IReadOnlyList<RectInt32> bounds, nint menuHwnd, bool show, bool captureExcluded,
            bool cacheEligible = false)
        {
            _menuHwnd = menuHwnd;
            _canShow = show;
            _cacheEligible = cacheEligible;
            try
            {
                lock (NativeSessionGate)
                {
                    // This native control supports one owning thread at a time per process.
                    // An independent native isolation probe returns ERROR_NOT_SUPPORTED from
                    // MagSetColorEffect on a second thread even after its own MagInitialize.
                    // The owner can transfer only after every previous group is destroyed.
                    int thread = Environment.CurrentManagedThreadId;
                    if (_processSessionUsers != 0 && _processSessionThread != thread)
                        throw new InvalidOperationException("Magnifier groups must share one owning thread until its cache is released.");
                    if (_sessionUsers == 0)
                    {
                        _initializeCalls++;
                        Check(MagInitialize(), "Initialize magnifier controls");
                    }
                    _processSessionUsers++;
                    _processSessionThread = thread;
                }
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
                // Keep live desktop sampling separate from the 60 Hz saturation animation.
                _timer.Interval = TimeSpan.FromMilliseconds(33);
                _timer.IsRepeating = true;
                _timer.Tick += OnTick;
            }
            catch { Dispose(); throw; }
        }

        internal bool Apply(float saturation)
        {
            if (_disposed) return false;
            if (!MayPresent) { StopAndHide(); return false; }
            long started = MeasureTimings ? Stopwatch.GetTimestamp() : 0;
            try
            {
                if (!float.IsFinite(saturation)) throw new ArgumentOutOfRangeException(nameof(saturation));
                SynchronizeLayer();
                var matrix = new ColorEffect { Values = ScreenGrade.BuildMatrix(Math.Clamp(saturation, 0, 1)) };
                foreach (var surface in _surfaces) surface.SetColor(ref matrix);
                if (!_presented)
                {
                    // Initial sampling must finish before showing any host. Subsequent saturation
                    // updates touch only the colour matrix; the independent timer owns resampling.
                    if (!RefreshSources()) return false;
                    if (_canShow)
                        foreach (var surface in _surfaces)
                        {
                            if (!MayPresent) { StopAndHide(); return false; }
                            surface.ShowBelow(LayerAnchor);
                            if (!MayPresent) { StopAndHide(); return false; }
                        }
                    _presented = true;
                    // The endpoint remains live even after ScreenGrade's transition timer stops.
                    if (_canShow) _timer!.Start();
                }
                return true;
            }
            catch (Exception exception) { LastFailure = Describe(exception); Dispose(); return false; }
            finally
            {
                if (started != 0) _applyTimings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }

        private void OnTick(DispatcherQueueTimer sender, object args)
        {
            if (_disposed || !_presented || !_canShow || !ReferenceEquals(sender, _timer)) return;
            if (!MayPresent) { StopAndHide(); return; }
            long started = MeasureTimings ? Stopwatch.GetTimestamp() : 0;
            if (started != 0)
            {
                if (_previousMeasuredTick != 0)
                {
                    double interval = Stopwatch.GetElapsedTime(_previousMeasuredTick, started).TotalMilliseconds;
                    _tickIntervals.Add(interval);
                    if (interval > 50) _tickIntervalsOver50Ms++;
                    if (interval > 66) _tickIntervalsOver66Ms++;
                }
                _previousMeasuredTick = started;
            }
            try { SynchronizeLayer(); RefreshSources(); }
            catch (Exception exception) { LastFailure = Describe(exception); Dispose(); }
            finally
            {
                if (started != 0) _tickTimings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }

        private void SynchronizeLayer()
        {
            if (_menuHwnd != 0 && !IsWindow(_menuHwnd))
                throw new InvalidOperationException("The grading layer's menu no longer exists.");
            if (_decorationHwnd != 0 && !IsWindow(_decorationHwnd))
            {
                _decorationHwnd = 0;
                _filtersDirty = true;
            }
            if (_filtersDirty) { RefreshFilters(); _filtersDirty = false; }
            if (_canShow && _presented)
                foreach (var surface in _surfaces) surface.OrderBelow(LayerAnchor);
        }

        private bool RefreshSources()
        {
            foreach (var surface in _surfaces)
            {
                if (!MayPresent) { StopAndHide(); return false; }
                surface.RefreshSource(MeasureTimings);
            }
            if (!MayPresent) { StopAndHide(); return false; }
            return true;
        }

        private static void RefreshFilters()
        {
            var handles = BuildExcludedWindowHandles();
            foreach (var group in ActiveGroups)
                foreach (var surface in group._surfaces)
                {
                    _setFilterCalls++;
                    Check(MagSetWindowFilterList(surface.Child, 0, handles.Length, handles), "Exclude grading layers and wheel from magnifier source");
                }
        }

        private static nint[] BuildExcludedWindowHandles()
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
            return handles;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (ReferenceEquals(_idleGroup, this)) _idleGroup = null;
            var timer = _timer; _timer = null;
            if (timer != null) { timer.Stop(); timer.Tick -= OnTick; }
            foreach (var surface in _surfaces) surface.Hide();
            if (_registered) { ActiveGroups.Remove(this); _registered = false; }
            for (int index = _surfaces.Count - 1; index >= 0; index--) _surfaces[index].Dispose();
            _surfaces.Clear();
            // Probes may temporarily coexist. Keep this thread's session until its last owner,
            // and remove retired HWNDs from the remaining local controls' exclusion lists.
            try { if (ActiveGroups.Count > 0) RefreshFilters(); }
            catch (Exception exception) { LastFailure = Describe(exception); }
            if (_sessionOwned)
            {
                _sessionOwned = false;
                _sessionUsers--;
                lock (NativeSessionGate)
                {
                    _processSessionUsers--;
                    if (_sessionUsers == 0) { _uninitializeCalls++; MagUninitialize(); }
                    if (_processSessionUsers == 0) _processSessionThread = 0;
                }
            }
        }

        private sealed class MonitorSurface : IDisposable
        {
            private readonly NativeRect _source;
            private DurationSamples _sourceTimings, _invalidateTimings;
            private bool _shown, _hasOrder;
            private nint _orderedAnchor;
            internal nint Host { get; private set; }
            internal nint Child { get; private set; }

            internal object GetTimingDiagnostics() => new
            {
                X = _source.Left, Y = _source.Top,
                Width = _source.Right - _source.Left, Height = _source.Bottom - _source.Top,
                SetWindowSource = _sourceTimings.Snapshot(), Invalidate = _invalidateTimings.Snapshot(),
            };

            internal void ResetTimingDiagnostics() => _sourceTimings = _invalidateTimings = default;

            internal MonitorSurface(RectInt32 bounds, bool captureExcluded)
            {
                if (bounds.Width <= 0 || bounds.Height <= 0) throw new ArgumentOutOfRangeException(nameof(bounds));
                _source = new NativeRect { Left = bounds.X, Top = bounds.Y, Right = bounds.X + bounds.Width, Bottom = bounds.Y + bounds.Height };
                try
                {
                    // The app is PerMonitorV2: these are physical pixels, with no XAML/DIP scale.
                    const uint exStyle = 0x080800A8; // NOACTIVATE | LAYERED | TOOLWINDOW | TRANSPARENT | TOPMOST
                    _createHostCalls++;
                    Host = CreateWindowEx(exStyle, HostClass, "Action Wheel Desktop Grade", 0x82000000,
                        bounds.X, bounds.Y, bounds.Width, bounds.Height, 0, 0, GetModuleHandle(null), 0);
                    Check(Host != 0, "Create native grading host");
                    Check(SetLayeredWindowAttributes(Host, 0, 255, 2), "Set grading host opacity");
                    _createMagnifierCalls++;
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
            {
                _setColorCalls++;
                Check(MagSetColorEffect(Child, ref color), "Set Rec601 grading matrix");
            }

            internal void RefreshSource(bool measureTimings = false)
            {
                // MagSetWindowSource takes RECT by value. Its unchanged 1:1 rectangle must still
                // be refreshed independently, so underlying content keeps moving at saturation 0.
                _setSourceCalls++;
                long started = measureTimings ? Stopwatch.GetTimestamp() : 0;
                Check(MagSetWindowSource(Child, _source), "Refresh live magnifier source");
                if (started != 0)
                {
                    _sourceTimings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    started = Stopwatch.GetTimestamp();
                }
                _invalidateCalls++;
                Check(InvalidateRect(Child, 0, false), "Invalidate live grading pixels");
                if (started != 0) _invalidateTimings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            internal void ShowBelow(nint menuHwnd)
            {
                if (_shown) { OrderBelow(menuHwnd); return; }
                OrderBelow(menuHwnd);
                _showCalls++;
                ShowWindow(Host, 4); // SW_SHOWNOACTIVATE; all sources/matrices/filters are ready.
                _shown = true;
                // Showing can change z-order. Reassert once, then retain it until the anchor changes.
                _hasOrder = false;
                OrderBelow(menuHwnd);
            }

            internal void OrderBelow(nint menuHwnd)
            {
                nint anchor = menuHwnd != 0 ? menuHwnd : new nint(-1);
                if (_hasOrder && _orderedAnchor == anchor) return;
                _setWindowPosCalls++;
                Check(SetWindowPos(Host, anchor, 0, 0, 0, 0, 0x13), "Keep live grade below the wheel");
                _orderedAnchor = anchor;
                _hasOrder = true;
            }

            internal void Hide()
            {
                if (Host != 0 && _shown) { _hideCalls++; ShowWindow(Host, 0); }
                _shown = false;
                _hasOrder = false;
            }
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
            lock (NativeSessionGate)
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
            // Production may already own a hidden idle group. Probes retain their own resources
            // and still verify their host/child are excluded without assuming process ownership.
            var expectedExcluded = BuildExcludedWindowHandles();
            var excluded = new nint[expectedExcluded.Length];
            Require(MagGetWindowFilterList(child, out uint mode, excluded.Length, excluded) == expectedExcluded.Length && mode == 0
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

        private static string Describe(Exception exception) => exception is Win32Exception native
            ? $"{exception.GetType().Name} ({native.NativeErrorCode}): {exception.Message}"
            : $"{exception.GetType().Name}: {exception.Message}";
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
