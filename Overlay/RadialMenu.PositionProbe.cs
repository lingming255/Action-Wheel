using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Action_Wheel.Services;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics;

namespace Action_Wheel.Overlay
{
    public sealed partial class RadialMenu
    {
        private const int PositionProbeCloakAttribute = 13;
        private const int PositionProbeCloakedAttribute = 14;

        private delegate bool PositionProbeMonitorCallback(IntPtr monitor, IntPtr dc,
            ref RECT rectangle, IntPtr data);

        [DllImport("user32.dll", EntryPoint = "EnumDisplayMonitors")]
        private static extern bool PositionProbeEnumDisplayMonitors(IntPtr dc, IntPtr clip,
            PositionProbeMonitorCallback callback, IntPtr data);

        [DllImport("user32.dll", EntryPoint = "ClientToScreen")]
        private static extern bool PositionProbeClientToScreen(IntPtr hwnd, ref POINT point);

        [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
        private static extern bool PositionProbeGetWindowRect(IntPtr hwnd, out RECT rectangle);

        [DllImport("user32.dll", EntryPoint = "IsWindow")]
        private static extern bool PositionProbeIsWindow(IntPtr hwnd);

        [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
        private static extern int PositionProbeGetDwmAttribute(IntPtr hwnd, int attribute,
            out int value, int size);

        private sealed record PositionProbeMonitor(int Index, RECT Bounds, uint Dpi,
            bool Primary);

        /// <summary>
        /// Explicit native regression probe. DWM cloaking is installed before the first show,
        /// so the production Activate/DPI path runs without presenting a wheel to the user.
        /// No hooks, user configuration, screen capture or audible cues are involved.
        /// </summary>
        internal static async Task<object> RunPositionProbeAsync()
        {
            WheelSounds.Configure(WheelSoundSettings.Default with { Enabled = false });
            var monitors = PositionProbeReadMonitors();
            var rounds = new List<object>();
            int failures = 0;

            foreach (var monitor in monitors)
            {
                int centerX = monitor.Bounds.left + (monitor.Bounds.right - monitor.Bounds.left) / 2;
                int centerY = monitor.Bounds.top + (monitor.Bounds.bottom - monitor.Bounds.top) / 2;
                var points = new[]
                {
                    ("center", new PointInt32(centerX, centerY)),
                    ("left-edge", new PointInt32(monitor.Bounds.left + 2, centerY)),
                    ("right-edge", new PointInt32(monitor.Bounds.right - 3, centerY)),
                    ("top-edge", new PointInt32(centerX, monitor.Bounds.top + 2)),
                    ("bottom-edge", new PointInt32(centerX, monitor.Bounds.bottom - 3)),
                };
                foreach (var (name, point) in points)
                {
                    var result = await PositionProbeFreshRoundAsync(monitor, point, name, false);
                    rounds.Add(result.Report);
                    if (!result.Passed) failures++;
                }

                var group = await PositionProbeFreshRoundAsync(monitor,
                    new PointInt32(centerX, centerY), "group-center", true);
                rounds.Add(group.Report);
                if (!group.Passed) failures++;
            }

            // Reuse a real HWND across monitors as well as testing fresh windows. This exposes
            // deferred DPI/layout callbacks which accidentally use the previous opening's scale.
            RadialMenu? moving = null;
            try
            {
                moving = PositionProbeCreateMenu(false);
                foreach (var monitor in monitors.Concat(monitors.AsEnumerable().Reverse()).Concat(monitors))
                {
                    var point = new PointInt32(
                        monitor.Bounds.left + (monitor.Bounds.right - monitor.Bounds.left) / 2,
                        monitor.Bounds.top + (monitor.Bounds.bottom - monitor.Bounds.top) / 2);
                    var result = await PositionProbeRoundAsync(moving, monitor, point,
                        "repeated-cross-screen", false);
                    rounds.Add(result.Report);
                    if (!result.Passed) failures++;
                }
            }
            catch (Exception exception)
            {
                failures++;
                rounds.Add(new { Case = "repeated-cross-screen", Passed = false,
                    Error = exception.ToString() });
            }
            finally
            {
                if (moving is { _closed: false }) moving.CloseMenu();
            }

            foreach (var monitor in monitors.Concat(monitors))
            {
                RadialMenu? menu = null;
                try
                {
                    menu = PositionProbeCreateMenu(false);
                    var hwnd = menu._windowHandle;
                    var point = new PointInt32(
                        monitor.Bounds.left + (monitor.Bounds.right - monitor.Bounds.left) / 2,
                        monitor.Bounds.top + (monitor.Bounds.bottom - monitor.Bounds.top) / 2);
                    menu.ShowAtPosition(point);
                    PositionProbeRequireCloaked(hwnd);
                    // Close before the asynchronous XAML/DPI work has necessarily settled.
                    menu.DismissMenu();
                    await Task.Delay(40);
                    bool passed = menu._closed && !PositionProbeIsWindow(hwnd);
                    if (!passed) failures++;
                    rounds.Add(new { Case = "immediate-cancel", Monitor = monitor.Index,
                        Passed = passed, Closed = menu._closed, NativeWindowDestroyed = !PositionProbeIsWindow(hwnd) });
                }
                catch (Exception exception)
                {
                    failures++;
                    rounds.Add(new { Case = "immediate-cancel", Monitor = monitor.Index,
                        Passed = false, Error = exception.ToString() });
                }
                finally
                {
                    if (menu is { _closed: false }) menu.CloseMenu();
                }
            }

            int positioningRoundCount = rounds.Count;
            foreach (var monitor in monitors)
            {
                var result = await PositionProbeRefreshLifecycleAsync(monitor);
                rounds.Add(result.Report);
                if (!result.Passed) failures++;
            }

            return new
            {
                Passed = failures == 0,
                FailedRounds = failures,
                RoundCount = rounds.Count,
                PositioningRoundCount = positioningRoundCount,
                RefreshLifecycleRoundCount = monitors.Count,
                CloakedNativeShow = true,
                ProductionShowAtPosition = true,
                SoundsDisabledInMemory = true,
                ConfigurationUntouched = true,
                DesktopCapture = false,
                Monitors = monitors.Select(m => new
                {
                    m.Index, m.Primary, m.Dpi,
                    Scale = m.Dpi / 96.0,
                    Bounds = new { m.Bounds.left, m.Bounds.top, m.Bounds.right, m.Bounds.bottom },
                }).ToArray(),
                Rounds = rounds,
            };
        }

        private static async Task<(bool Passed, object Report)> PositionProbeRefreshLifecycleAsync(
            PositionProbeMonitor monitor)
        {
            RadialMenu? menu = null;
            RefreshFrame? frame = null;
            try
            {
                // Build the synthetic scene before showing: no await may drain the placement
                // callback between ShowAtPosition and the immediate release below.
                frame = CreateReleaseProbeFrame(800);
                RefreshFrame readyFrame = frame;
                var gpu = frame.BorrowGpuRenderer();
                menu = PositionProbeCreateMenu(false);
                var point = new PointInt32(
                    monitor.Bounds.left + (monitor.Bounds.right - monitor.Bounds.left) / 2,
                    monitor.Bounds.top + (monitor.Bounds.bottom - monitor.Bounds.top) / 2);
                menu.ShowAtPosition(point);
                PositionProbeRequireCloaked(menu._windowHandle);
                bool hadPendingPlacement = menu._menuPlacementQueued;
                menu.BeginRefreshFeedback(point);
                bool feedbackStoppedPlacement = menu._menuAnchor == null && menu._menuDpiRoot == null;
                readyFrame.PrepareRelease(point);
                int restoreCalls = 0;
                menu.PlayRefresh(readyFrame, () =>
                {
                    restoreCalls++;
                    readyFrame.Dispose();
                });
                var immediate = PositionProbeRefreshBounds(menu, readyFrame.Bounds);
                await Task.Delay(120);
                var afterPendingCallbacks = PositionProbeRefreshBounds(menu, readyFrame.Bounds);
                bool pendingPlacementDrained = !menu._menuPlacementQueued;

                // A cloaked window need not receive DWM rendering ticks. Exercise the existing
                // production handoff callback explicitly if those ticks have not arrived.
                bool manuallyCommittedCover = restoreCalls == 0;
                if (manuallyCommittedCover)
                    for (int tick = 0; tick < 3; tick++)
                        menu._refreshOnRendering?.Invoke(null, new object());
                bool rendererTransferred = readyFrame.BorrowGpuRenderer() == null
                    && (menu._gpuRefreshPlayback is { OwnsRenderer: true } || menu._refreshPlayback != null);
                await Task.Delay(400);
                var beforePlaybackEnds = PositionProbeRefreshBounds(menu, readyFrame.Bounds);
                bool playbackSurvived = !menu._closed && menu.IsRefreshPlaying && menu._fullRefreshStarted;
                bool noPlacementWatch = menu._menuAnchor == null && menu._menuDpiRoot == null;
                menu.CloseMenu();
                await Task.Delay(40);
                bool resourcesReleased = menu._closed && menu._gpuRefreshPlayback == null
                    && menu._refreshPlayback == null && menu._refreshTimer == null
                    && menu._refreshOnRendering == null && menu._refreshFeedbackResources.Count == 0
                    && menu._menuAnchor == null && menu._menuDpiRoot == null;
                bool gpuReleased = gpu == null;
                if (gpu != null)
                {
                    try { gpu.Render(0); }
                    catch (ObjectDisposedException) { gpuReleased = true; }
                }
                bool passed = hadPendingPlacement && feedbackStoppedPlacement
                    && immediate.Passed && afterPendingCallbacks.Passed && beforePlaybackEnds.Passed
                    && pendingPlacementDrained && rendererTransferred && restoreCalls == 1
                    && playbackSurvived && noPlacementWatch && resourcesReleased && gpuReleased;
                return (passed, new
                {
                    Case = "refresh-after-pending-placement", Monitor = monitor.Index, Passed = passed,
                    HadPendingPlacementAtRelease = hadPendingPlacement,
                    FeedbackStoppedPlacement = feedbackStoppedPlacement,
                    PendingPlacementDrained = pendingPlacementDrained,
                    CoverHandoffCommittedManually = manuallyCommittedCover,
                    RestoreCalls = restoreCalls, RendererTransferred = rendererTransferred,
                    PlaybackSurvivedUntilLateSample = playbackSurvived,
                    NoPlacementWatchDuringRefresh = noPlacementWatch,
                    ClosedResourcesReleased = resourcesReleased, AdoptedGpuReleased = gpuReleased,
                    SyntheticFrameBounds = new { readyFrame.Bounds.X, readyFrame.Bounds.Y,
                        readyFrame.Bounds.Width, readyFrame.Bounds.Height },
                    Immediate = immediate, After120ms = afterPendingCallbacks,
                    BeforePlaybackEnds = beforePlaybackEnds,
                });
            }
            catch (Exception exception)
            {
                return (false, new { Case = "refresh-after-pending-placement", Monitor = monitor.Index,
                    Passed = false, Error = exception.ToString() });
            }
            finally
            {
                if (menu is { _closed: false }) menu.CloseMenu();
                frame?.Dispose();
            }
        }

        private sealed record PositionProbeRefreshMeasurement(bool Passed, bool BoundsMatch,
            int X, int Y, int Width, int Height, bool PlacementAnchorCleared,
            bool DpiWatchCleared, int CloakedFlags);

        private static PositionProbeRefreshMeasurement PositionProbeRefreshBounds(RadialMenu menu,
            RectInt32 expected)
        {
            if (!menu.TryGetScreenClientBounds(out var actual))
                throw new InvalidOperationException("Could not read refresh client bounds after the wheel handoff.");
            int width = actual.right - actual.left, height = actual.bottom - actual.top;
            bool matches = actual.left == expected.X && actual.top == expected.Y
                && width == expected.Width && height == expected.Height;
            PositionProbeRequireCloaked(menu._windowHandle);
            PositionProbeGetDwmAttribute(menu._windowHandle, PositionProbeCloakedAttribute,
                out int cloak, sizeof(int));
            bool anchorCleared = menu._menuAnchor == null, watchCleared = menu._menuDpiRoot == null;
            return new PositionProbeRefreshMeasurement(matches && anchorCleared && watchCleared,
                matches, actual.left, actual.top, width, height, anchorCleared, watchCleared, cloak);
        }

        private static List<PositionProbeMonitor> PositionProbeReadMonitors()
        {
            var monitors = new List<PositionProbeMonitor>();
            PositionProbeMonitorCallback callback = (IntPtr monitor, IntPtr dc, ref RECT rectangle, IntPtr data) =>
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (!GetMonitorInfo(monitor, ref info)) return true;
                if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpi, out _) != 0 || dpi == 0)
                    dpi = 96;
                monitors.Add(new PositionProbeMonitor(monitors.Count + 1, info.rcMonitor, dpi,
                    (info.dwFlags & 1) != 0));
                return true;
            };
            if (!PositionProbeEnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero)
                || monitors.Count == 0)
                throw new InvalidOperationException("No physical monitor bounds were available for the position probe.");
            return monitors;
        }

        private static RadialMenu PositionProbeCreateMenu(bool groups)
        {
            var actions = ActionConfig.Defaults().Select(action => action with { Kind = ActionKind.None }).ToList();
            if (groups)
                actions[1] = actions[1] with { Kind = ActionKind.Group,
                    GroupChildren = new[] { actions[2] with { Label = "position probe child" } } };
            var menu = new RadialMenu(actions,
                RingAppearance.Default with { Animation = RingAnimation.None });
            try
            {
                int cloak = 1;
                int hr = DwmSetWindowAttribute(menu._windowHandle, PositionProbeCloakAttribute,
                    ref cloak, sizeof(int));
                if (hr != 0)
                    throw new InvalidOperationException($"DWM cloak failed before show: 0x{hr:X8}.");
                PositionProbeRequireCloaked(menu._windowHandle);
                return menu;
            }
            catch
            {
                menu.CloseMenu();
                throw;
            }
        }

        private static void PositionProbeRequireCloaked(IntPtr hwnd)
        {
            int hr = PositionProbeGetDwmAttribute(hwnd, PositionProbeCloakedAttribute,
                out int cloak, sizeof(int));
            if (hr != 0 || (cloak & 1) == 0)
            {
                ShowWindow(hwnd, SW_HIDE);
                throw new InvalidOperationException($"The probe window was not app-cloaked: hr=0x{hr:X8}, flags={cloak}.");
            }
        }

        private static async Task<(bool Passed, object Report)> PositionProbeFreshRoundAsync(
            PositionProbeMonitor monitor, PointInt32 point, string name, bool groups)
        {
            RadialMenu? menu = null;
            try
            {
                menu = PositionProbeCreateMenu(groups);
                return await PositionProbeRoundAsync(menu, monitor, point, name, groups);
            }
            catch (Exception exception)
            {
                return (false, new { Case = name, Monitor = monitor.Index, Groups = groups,
                    Passed = false, Error = exception.ToString() });
            }
            finally
            {
                if (menu is { _closed: false }) menu.CloseMenu();
            }
        }

        private static async Task<(bool Passed, object Report)> PositionProbeRoundAsync(RadialMenu menu,
            PositionProbeMonitor monitor, PointInt32 point, string name, bool groups)
        {
            uint initialDpi = GetDpiForWindow(menu._windowHandle);
            var initialSize = menu.AppWindow.Size;
            menu.ShowAtPosition(point);
            PositionProbeRequireCloaked(menu._windowHandle);
            var immediate = PositionProbeSample(menu, point, false);
            await Task.Delay(120);
            PositionProbeRequireCloaked(menu._windowHandle);
            menu.RootGrid.UpdateLayout();
            var settled = PositionProbeSample(menu, point, true);
            bool centerCase = name is "center" or "group-center" or "repeated-cross-screen";
            bool monitorDpiMatches = !centerCase || settled.NativeDpi == monitor.Dpi;
            bool passed = immediate.NativeGeometryPassed && settled.Passed && monitorDpiMatches;
            return (passed, new
            {
                Case = name, Monitor = monitor.Index, Groups = groups, Passed = passed,
                Target = new { point.X, point.Y }, TargetMonitorDpi = monitor.Dpi,
                InitialNativeDpi = initialDpi,
                InitialSize = new { initialSize.Width, initialSize.Height },
                CenterCaseMonitorDpiMatches = monitorDpiMatches,
                Immediate = immediate, Settled = settled,
            });
        }

        private sealed record PositionProbeMeasurement(bool Passed, bool NativeGeometryPassed,
            uint NativeDpi, double XamlScale, double CachedScale,
            int ExpectedPhysicalSize, int ClientWidth, int ClientHeight,
            double ClientOriginX, double ClientOriginY, double CachedOriginX, double CachedOriginY,
            double NativeCenterErrorX, double NativeCenterErrorY,
            double? VisualCenterErrorX, double? VisualCenterErrorY,
            double GestureCenterErrorX, double GestureCenterErrorY,
            int WindowWidth, int WindowHeight, int CloakedFlags, string? LayoutError);

        private static PositionProbeMeasurement PositionProbeSample(RadialMenu menu,
            PointInt32 point, bool requireLayout)
        {
            if (!GetClientRect(menu._windowHandle, out var client)
                || !PositionProbeGetWindowRect(menu._windowHandle, out var window))
                throw new InvalidOperationException("Could not read native wheel geometry.");
            var origin = new POINT(0, 0);
            if (!PositionProbeClientToScreen(menu._windowHandle, ref origin))
                throw new InvalidOperationException("Could not map the native client origin to screen pixels.");
            uint dpi = GetDpiForWindow(menu._windowHandle);
            double nativeScale = RingGeometry.ScaleFromDpi(dpi);
            double scale = menu.RootGrid.XamlRoot?.RasterizationScale ?? 0;
            int expectedSize = RingGeometry.PhysicalMenuSize(nativeScale, menu.MenuSize);
            int width = client.right - client.left, height = client.bottom - client.top;
            double nativeErrorX = origin.x + width / 2.0 - point.X;
            double nativeErrorY = origin.y + height / 2.0 - point.Y;
            bool nativePassed = width == expectedSize && height == expectedSize
                && Math.Abs(nativeErrorX) <= 1 && Math.Abs(nativeErrorY) <= 1;
            double? visualErrorX = null, visualErrorY = null;
            string? layoutError = null;
            try
            {
                if (menu.CenterBtn.ActualWidth <= 0 || menu.CenterBtn.ActualHeight <= 0)
                    throw new InvalidOperationException("Center button layout has not been realised.");
                var center = menu.CenterBtn.TransformToVisual(menu.RootGrid).TransformPoint(
                    new Point(menu.CenterBtn.ActualWidth / 2, menu.CenterBtn.ActualHeight / 2));
                visualErrorX = origin.x + center.X * scale - point.X;
                visualErrorY = origin.y + center.Y * scale - point.Y;
            }
            catch (Exception exception) { layoutError = exception.Message; }
            var gesture = menu.GestureOffset(point);
            bool passed = nativePassed && Math.Abs(scale - nativeScale) < .001
                && Math.Abs(menu._rasterizationScale - nativeScale) < .001
                && Math.Abs(gesture.X) <= 1 && Math.Abs(gesture.Y) <= 1
                && (!requireLayout || visualErrorX is double x && visualErrorY is double y
                    && Math.Abs(x) <= 1 && Math.Abs(y) <= 1);
            int hr = PositionProbeGetDwmAttribute(menu._windowHandle, PositionProbeCloakedAttribute,
                out int cloak, sizeof(int));
            passed &= hr == 0 && (cloak & 1) != 0;
            return new PositionProbeMeasurement(passed, nativePassed, dpi, scale,
                menu._rasterizationScale, expectedSize, width, height, origin.x, origin.y,
                menu._screenOrigin.X, menu._screenOrigin.Y, nativeErrorX, nativeErrorY,
                visualErrorX, visualErrorY, gesture.X, gesture.Y,
                window.right - window.left, window.bottom - window.top, cloak, layoutError);
        }
    }
}
